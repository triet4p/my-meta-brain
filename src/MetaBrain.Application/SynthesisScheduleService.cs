using System.Security.Cryptography;
using MetaBrain.Core.Domain;
using MetaBrain.Core.Security;

namespace MetaBrain.Application;

/// <summary>
/// One owner-authored schedule draft, exactly as the owner supplied it: the
/// interval, the explicit bounded synthesis selection, the destination inbox,
/// and the provider/model the scheduled runs may use.
/// </summary>
public sealed record SynthesisScheduleDraft(
    int IntervalMinutes,
    JobSelectionDraft Selection,
    string DestinationZoneId,
    string DestinationResourceId,
    long DestinationRevision,
    string Provider,
    string Model);

/// <summary>
/// The outcome of one evaluation of the owner's schedule. The outcome names only
/// what actually happened: nothing configured, disabled, not yet due, refused
/// before any provider send, or one bounded job that ran (and the state that job
/// reached). It is never evidence that the schedule does more than the owner
/// configured.
/// </summary>
public sealed record SynthesisScheduleRunResult(
    string Outcome,
    string? JobId,
    string? JobState,
    string? FailureCode,
    DateTimeOffset? NextDueAtUtc,
    string? Message);

/// <summary>
/// The owner-enabled synthesis schedule (S6-T6). The schedule is one encrypted
/// vault record, and every scheduled run is the same bounded, owner-scoped
/// synthesis job the owner can start by hand: the same explicit input set pinned
/// to revisions, the same pre-send pricing/token/cost caps, the same fail-closed
/// revalidation, and the same pending-proposal output. Nothing here mints
/// authority, unlocks the vault, publishes, or resolves anything.
/// <para>
/// Two properties are policy, not convention. A run only happens while the vault
/// is unlocked, because both the due check and the job itself read the encrypted
/// record and the selected inputs; a locked vault pauses the schedule instead of
/// triggering an unlock. And the next due instant is always computed from the
/// moment an attempt finished, so slots missed while locked or while the service
/// was down are never replayed as a burst of catch-up runs.
/// </para>
/// </summary>
public sealed class SynthesisScheduleService
{
    public const string ScheduleResourceId = "sec-synthesis-schedule";
    public const string SystemZoneId = ModelJobService.SystemZoneId;

    private readonly ModelJobService _jobs;

    public SynthesisScheduleService(ModelJobService jobs)
    {
        _jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
    }

    /// <summary>
    /// Writes the owner's schedule as one enabled, encrypted record. The owner
    /// enables a schedule by supplying it; disabling is a separate, explicit act.
    /// </summary>
    public StoredSynthesisSchedule SaveEnabled(AuthenticatedContext context, SynthesisScheduleDraft draft, IManagedResourceStore store)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(store);
        EnsureOwner(context);

        if (!SynthesisSchedulePolicy.IsValidInterval(draft.IntervalMinutes))
        {
            throw new ServiceRefusalException("synthesis_schedule_invalid", $"The interval must be between {SynthesisSchedulePolicy.MinimumIntervalMinutes} and {SynthesisSchedulePolicy.MaximumIntervalMinutes} minutes.");
        }

        var selectedInputs = draft.Selection.Inputs ?? Array.Empty<JobInputReference>();
        if (selectedInputs.Length < 2)
        {
            throw new ServiceRefusalException("synthesis_requires_multiple_inputs", "A scheduled synthesis requires at least two explicitly selected inputs.");
        }

        if (selectedInputs.Length > MemoryEnvelopeCodec.MaximumSources)
        {
            throw new ServiceRefusalException("invalid_job_inputs", $"A synthesis schedule selects between 2 and {MemoryEnvelopeCodec.MaximumSources} inputs.");
        }

        if (string.IsNullOrWhiteSpace(draft.Selection.Objective))
        {
            throw new ServiceRefusalException("objective_required", "A scheduled synthesis requires the objective the owner wants addressed.");
        }

        if (draft.Selection.Objective.Length > JobSelectionCodec.MaximumObjectiveLength)
        {
            throw new ServiceRefusalException("objective_too_long", $"The objective must be at most {JobSelectionCodec.MaximumObjectiveLength} characters.");
        }

        if (!SynthesisLevels.TryParse(draft.Selection.TargetLevel, out _))
        {
            throw new ServiceRefusalException("synthesis_level_required", $"The requested level must be one of: {SynthesisLevels.L2}, {SynthesisLevels.L3Plus}.");
        }

        if (string.IsNullOrWhiteSpace(draft.DestinationZoneId) || string.IsNullOrWhiteSpace(draft.DestinationResourceId) || draft.DestinationRevision < 1)
        {
            throw new ServiceRefusalException("invalid_request", "The destination zone, destination resource and its revision are required.");
        }

        if (string.IsNullOrWhiteSpace(draft.Provider) || string.IsNullOrWhiteSpace(draft.Model))
        {
            throw new ServiceRefusalException("invalid_request", "The provider and model of a scheduled run are required.");
        }

        var previous = TryRead(store);
        var now = DateTimeOffset.UtcNow;
        var record = new StoredSynthesisSchedule(
            SchemaVersion: SynthesisScheduleCodec.CurrentSchemaVersion,
            Enabled: true,
            IntervalMinutes: draft.IntervalMinutes,
            Inputs: selectedInputs,
            Objective: draft.Selection.Objective.Trim(),
            TargetLevel: draft.Selection.TargetLevel ?? string.Empty,
            DestinationZoneId: draft.DestinationZoneId ?? string.Empty,
            DestinationResourceId: draft.DestinationResourceId ?? string.Empty,
            DestinationRevision: draft.DestinationRevision,
            Provider: draft.Provider ?? string.Empty,
            Model: draft.Model ?? string.Empty,
            CreatedAtUtc: previous?.CreatedAtUtc ?? now,
            UpdatedAtUtc: now,
            NextDueAtUtc: now + TimeSpan.FromMinutes(draft.IntervalMinutes),
            LastRunAtUtc: null,
            LastRunJobId: null,
            LastRunOutcome: null);

        // The record shape is validated by the same codec the vault read uses, so
        // an incoherent schedule is refused here instead of being stored and
        // discovered later by the background pass.
        _ = SynthesisScheduleCodec.Decode(SynthesisScheduleCodec.Encode(record));
        WriteSchedule(record, store);
        return record;
    }

    /// <summary>
    /// Disables the owner's schedule while keeping its configuration, so a
    /// disabled schedule stops being due immediately and can be enabled again
    /// without retyping it. Nothing else is changed.
    /// </summary>
    public StoredSynthesisSchedule Disable(AuthenticatedContext context, IManagedResourceStore store)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(store);
        EnsureOwner(context);

        var schedule = TryRead(store)
            ?? throw new ServiceRefusalException("schedule_absent", "No synthesis schedule is configured, so there is nothing to disable.");
        var disabled = schedule with
        {
            Enabled = false,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            NextDueAtUtc = null
        };
        WriteSchedule(disabled, store);
        return disabled;
    }

    /// <summary>
    /// Reads the stored schedule, or null when none is configured. An absent or
    /// unreadable record is reported as "no schedule"; a locked vault surfaces as
    /// its own failure so callers never read a locked schedule as a disabled one.
    /// </summary>
    public StoredSynthesisSchedule? TryRead(IManagedResourceStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        byte[]? bytes = null;
        try
        {
            bytes = store.ReadContent(ScheduleResourceId, null);
        }
        catch (ManagedResourceUnavailableException)
        {
            return null;
        }

        try
        {
            return bytes.Length == 0 ? null : SynthesisScheduleCodec.Decode(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    /// <summary>
    /// Evaluates the owner's schedule once. When it is enabled and due, exactly
    /// one bounded synthesis job is created and executed through the shared model
    /// job lifecycle; every other outcome creates and sends nothing. The due
    /// window is then advanced from the finish of this attempt, so a slot missed
    /// while the vault was locked, the service was down, or the previous attempt
    /// was still running is never queued as an extra run.
    /// </summary>
    public async Task<SynthesisScheduleRunResult> RunDueAsync(
        AuthenticatedContext context,
        IManagedResourceStore store,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(store);
        EnsureOwner(context);

        var schedule = TryRead(store);
        if (schedule is null)
        {
            return new SynthesisScheduleRunResult("not_configured", null, null, null, null,
                "No synthesis schedule is configured; no scheduled job exists and nothing was sent.");
        }

        if (!schedule.Enabled)
        {
            return new SynthesisScheduleRunResult("disabled", null, null, null, null,
                "The owner's synthesis schedule is disabled; no scheduled job was created and nothing was sent.");
        }

        if (schedule.NextDueAtUtc is { } due && now < due)
        {
            return new SynthesisScheduleRunResult("not_due", null, null, null, due,
                "The next scheduled run is not due yet; nothing was created and nothing was sent.");
        }

        string? jobId = null;
        string? jobState = null;
        string? failureCode = null;
        string? refusalMessage = null;
        string outcome;
        try
        {
            var job = _jobs.CreateJob(
                context,
                schedule.Inputs,
                schedule.DestinationZoneId,
                schedule.DestinationResourceId,
                schedule.DestinationRevision,
                schedule.Provider,
                schedule.Model,
                maxCostUsd: null,
                store,
                StructuringProfiles.Synthesis,
                schedule.Objective,
                schedule.TargetLevel);
            jobId = job.Id;
            var record = await _jobs.StartJobAsync(context, job.Id, store, cancellationToken).ConfigureAwait(false);
            jobState = record.State.ToString();
            failureCode = record.Failure?.Code;
            outcome = record.State switch
            {
                JobState.Succeeded => "ran",
                JobState.Cancelled => "cancelled",
                _ => "failed"
            };
        }
        catch (ServiceRefusalException refusal)
        {
            // The shared lifecycle refused the scheduled job before it could send
            // anything (a moved or revoked input, unknown pricing, a budget
            // refusal). The refusal is recorded on the schedule and the window
            // still advances, so a refused schedule does not retry every poll.
            outcome = "refused";
            failureCode = refusal.Code;
            refusalMessage = refusal.Message;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or InvalidDataException)
        {
            // The shared lifecycle refused the scheduled job before it could send
            // anything (a moved or revoked input, unknown pricing, a budget
            // refusal). The refusal is recorded on the schedule and the window
            // still advances, so a refused schedule does not retry every poll.
            // The code position keeps a code and the sentence stays the owner's
            // explanation; a sentence is never stored as a code.
            outcome = "refused";
            failureCode = ex is InvalidDataException ? "resource_unavailable" : "invalid_request";
            refusalMessage = ex.Message;
        }

        var finished = DateTimeOffset.UtcNow;
        var advanced = schedule with
        {
            LastRunAtUtc = finished,
            LastRunJobId = jobId,
            LastRunOutcome = failureCode is null ? outcome : outcome + ":" + failureCode,
            NextDueAtUtc = finished + TimeSpan.FromMinutes(schedule.IntervalMinutes),
            UpdatedAtUtc = finished
        };
        WriteSchedule(advanced, store);

        return new SynthesisScheduleRunResult(
            outcome,
            jobId,
            jobState,
            failureCode,
            advanced.NextDueAtUtc,
            outcome switch
            {
                "ran" => "The scheduled synthesis job " + jobId + " completed and committed pending proposal(s) for the owner to review.",
                "cancelled" => "The scheduled synthesis job " + jobId + " was cancelled; the schedule window advanced and nothing was published.",
                "failed" => "The scheduled synthesis job " + jobId + " failed (" + (failureCode ?? "unknown") + "); nothing was published.",
                _ => "The scheduled run was refused by the shared job contract (" + (failureCode ?? "unknown") + ") before any provider send." +
                     (refusalMessage is null ? string.Empty : " The service said: " + refusalMessage)
            });
    }

    private static void WriteSchedule(StoredSynthesisSchedule record, IManagedResourceStore store)
    {
        var payload = SynthesisScheduleCodec.Encode(record);
        try
        {
            store.WriteContent(ScheduleResourceId, SystemZoneId, payload, expectedRevision: ResolveRevisionOrZero(store));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    private static long ResolveRevisionOrZero(IManagedResourceStore store)
    {
        var found = store.ListResources().FirstOrDefault(resource => string.Equals(resource.ResourceId, ScheduleResourceId, StringComparison.Ordinal));
        return found?.Revision ?? 0;
    }

    private static void EnsureOwner(AuthenticatedContext context)
    {
        if (context.Kind != PrincipalKind.Owner)
        {
            throw new ServiceRefusalException("forbidden", "Only trusted owner context can manage the synthesis schedule.");
        }
    }
}
