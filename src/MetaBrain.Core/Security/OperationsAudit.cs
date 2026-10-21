using System.Globalization;
using System.IO;

namespace MetaBrain.Core.Security;

/// <summary>
/// One recorded operation of this vault: who performed it, what they
/// performed, how it ended, what it cost and which error code it produced
/// (S8-T4, R36/R07/R29).
/// </summary>
/// <remarks>
/// The record is deliberately narrow. It carries no title, no body, no query,
/// no transcript excerpt, no path, no credential and no token: only an actor
/// token, the operation token the service already names, an outcome token, the
/// real error code the service returned, the cost the service already charged
/// and an optional opaque resource ID. A record that cannot be written without
/// one of those fields is not recorded at all.
/// </remarks>
public sealed record OperationsAuditEntry(
    long Sequence,
    DateTimeOffset RecordedAtUtc,
    string Actor,
    string Action,
    string Outcome,
    string? ErrorCode,
    decimal? CostUsd,
    string? ResourceId);

/// <summary>
/// One recorded failure the owner can act on: which part of the product
/// failed, the real error code the service returned, and the owner-facing
/// sentence that says what to do next.
/// </summary>
/// <remarks>
/// The owner message is written by the product's own guidance table and never
/// by a caller, so it cannot carry a secret, a payload or a path. It is
/// bounded so one failure cannot grow the encrypted store without limit.
/// </remarks>
public sealed record OperationsDiagnosticEntry(
    long Sequence,
    DateTimeOffset RecordedAtUtc,
    string Component,
    string Code,
    string OwnerMessage,
    string? ResourceId);

/// <summary>
/// The private operations state of one vault: the audit trail, the error
/// diagnostics, the owner's retention choice and the tombstones that keep a
/// purged entry from coming back.
/// </summary>
/// <remarks>
/// The state is persisted as one dedicated encrypted file of the vault, with
/// the same data key and the same AEAD envelope every other dedicated owner
/// state uses. It is not a canonical resource, it is not indexed by the
/// private index, and it never reaches the approved catalog: the only public
/// disclosure of this product stays the owner's published catalog entries.
/// </remarks>
public sealed record OperationsAuditState(
    int SchemaVersion,
    OperationsAuditEntry[] Audit,
    OperationsDiagnosticEntry[] Diagnostics,
    int RetentionDays,
    int MaximumRecords,
    long PurgedThroughSequence,
    long[] PurgedSequences,
    long NextSequence)
{
    public const int CurrentSchemaVersion = 1;

    /// <summary>The retention the product applies until the owner chooses another one.</summary>
    public const int DefaultRetentionDays = 30;

    /// <summary>The number of records the product keeps until the owner chooses another one.</summary>
    public const int DefaultMaximumRecords = 500;

    public static OperationsAuditState Empty { get; } = new(
        CurrentSchemaVersion,
        Array.Empty<OperationsAuditEntry>(),
        Array.Empty<OperationsDiagnosticEntry>(),
        DefaultRetentionDays,
        DefaultMaximumRecords,
        0,
        Array.Empty<long>(),
        1);
}

/// <summary>
/// The one typed failure of the private operations state: the state could
/// not be read or written (S8-T4 correction).
/// </summary>
/// <remarks>
/// <para>
/// The private operations state is written through the vault's own data key,
/// so a failure there is a cryptographic failure, a truncated envelope, an
/// unreadable file or a full disk. Every one of them reaches the product as
/// this one typed exception, so the recorder can tell "the trail could not be
/// written" apart from "the operation the owner asked for failed".
/// </para>
/// <para>
/// The message is deliberately generic: it never repeats the file path, the
/// key material or the exception text of the underlying failure, because the
/// owner never sees it and the audit trail never records it.
/// </para>
/// </remarks>
public sealed class OperationsAuditPersistenceException : IOException
{
    public OperationsAuditPersistenceException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The single authority over the private operations state: it bounds every
/// field, applies the owner's retention, and keeps the tombstones that stop a
/// purged entry from being served again.
/// </summary>
/// <remarks>
/// Two kinds of removal exist and both are recorded. A retention pass removes
/// the oldest entries, which is a prefix of the sequence, so it advances
/// <see cref="OperationsAuditState.PurgedThroughSequence"/>. A purge of one
/// owner resource removes the entries that name it, which is not a prefix, so
/// it records the removed sequence numbers as tombstones. Both are applied
/// when the state is loaded, so an entry that was purged is not served again
/// by a later read, a restart or a rebuild.
/// </remarks>
public static class OperationsAuditAuthority
{
    public const int MaximumAuditEntries = 1000;
    public const int MaximumDiagnosticEntries = 500;
    public const int MaximumTombstones = 8192;
    public const int MaximumActorLength = 16;
    public const int MaximumActionLength = 96;
    public const int MaximumOutcomeLength = 32;
    public const int MaximumCodeLength = 96;
    public const int MaximumMessageLength = 600;
    public const int MaximumResourceIdLength = 128;
    public const int MinimumRetentionDays = 1;
    public const int MaximumRetentionDays = 3650;
    public const int MinimumMaximumRecords = 10;
    public const int MaximumMaximumRecords = 5000;

    /// <summary>The actor tokens the product records.</summary>
    public const string OwnerActor = "owner";
    public const string AgentActor = "agent";
    public const string ServiceActor = "service";

    /// <summary>The outcome tokens the product records.</summary>
    public const string CompletedOutcome = "completed";
    public const string RefusedOutcome = "refused";
    public const string FailedOutcome = "failed";

    /// <summary>
    /// The outcome token of the operational log line that reports a failed
    /// audit write. The operation itself is not the outcome of this line: the
    /// line says that the trail could not be written.
    /// </summary>
    public const string AuditWriteFailedOutcome = "audit_failed";

    /// <summary>
    /// The real code of a failed audit write. It is the only code the
    /// recorder writes about its own failure, so the owner can tell an
    /// incomplete trail from a refused operation.
    /// </summary>
    public const string AuditPersistenceFailedCode = "audit_persistence_failed";

    /// <summary>
    /// Validates and normalizes a loaded state: every bounded field is checked,
    /// every entry that a tombstone or the prefix watermark covers is dropped,
    /// and the sequence counter is moved past every surviving entry.
    /// </summary>
    public static OperationsAuditState ValidateAndNormalizeState(OperationsAuditState? state)
    {
        if (state is null)
        {
            return OperationsAuditState.Empty;
        }

        var audit = (state.Audit ?? Array.Empty<OperationsAuditEntry>())
            .Where(entry => entry is not null && entry.Sequence > state.PurgedThroughSequence &&
                            !state.PurgedSequences.Contains(entry.Sequence))
            .OrderBy(entry => entry.Sequence)
            .Take(MaximumAuditEntries)
            .Select(NormalizeAuditEntry)
            .ToArray();

        var diagnostics = (state.Diagnostics ?? Array.Empty<OperationsDiagnosticEntry>())
            .Where(entry => entry is not null && entry.Sequence > state.PurgedThroughSequence &&
                            !state.PurgedSequences.Contains(entry.Sequence))
            .OrderBy(entry => entry.Sequence)
            .Take(MaximumDiagnosticEntries)
            .Select(NormalizeDiagnosticEntry)
            .ToArray();

        var tombstones = (state.PurgedSequences ?? Array.Empty<long>())
            .Where(sequence => sequence > 0)
            .Distinct()
            .OrderBy(sequence => sequence)
            .Take(MaximumTombstones)
            .ToArray();

        var highest = audit.Length == 0 && diagnostics.Length == 0
            ? 0
            : Math.Max(
                audit.Length == 0 ? 0 : audit[^1].Sequence,
                diagnostics.Length == 0 ? 0 : diagnostics[^1].Sequence);

        var next = Math.Max(state.NextSequence, highest + 1);
        if (next < 1)
        {
            next = 1;
        }

        var retentionDays = state.RetentionDays < MinimumRetentionDays || state.RetentionDays > MaximumRetentionDays
            ? OperationsAuditState.DefaultRetentionDays
            : state.RetentionDays;
        var maximumRecords = state.MaximumRecords < MinimumMaximumRecords || state.MaximumRecords > MaximumMaximumRecords
            ? OperationsAuditState.DefaultMaximumRecords
            : state.MaximumRecords;

        return new OperationsAuditState(
            OperationsAuditState.CurrentSchemaVersion,
            audit,
            diagnostics,
            retentionDays,
            maximumRecords,
            state.PurgedThroughSequence < 0 ? 0 : state.PurgedThroughSequence,
            tombstones,
            next);
    }

    /// <summary>
    /// Appends one audit entry and applies the owner's retention to the
    /// result. The entry is dropped rather than recorded when it does not fit
    /// the bounds, so an unbounded field can never reach the store.
    /// </summary>
    public static OperationsAuditState AppendAudit(OperationsAuditState state, OperationsAuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(entry);
        var normalized = ValidateAndNormalizeState(state);
        if (!IsAuditEntryValid(entry))
        {
            return normalized;
        }

        var sequence = Math.Max(normalized.NextSequence, entry.Sequence);
        var appended = normalized.Audit
            .Append(entry with { Sequence = sequence })
            .OrderBy(item => item.Sequence)
            .ToArray();
        var next = new OperationsAuditState(
            normalized.SchemaVersion,
            appended,
            normalized.Diagnostics,
            normalized.RetentionDays,
            normalized.MaximumRecords,
            normalized.PurgedThroughSequence,
            normalized.PurgedSequences,
            sequence + 1);
        return ApplyRetention(next, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Appends one diagnostic entry and applies the owner's retention to the
    /// result.
    /// </summary>
    public static OperationsAuditState AppendDiagnostic(OperationsAuditState state, OperationsDiagnosticEntry entry)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(entry);
        var normalized = ValidateAndNormalizeState(state);
        if (!IsDiagnosticEntryValid(entry))
        {
            return normalized;
        }

        var sequence = Math.Max(normalized.NextSequence, entry.Sequence);
        var appended = normalized.Diagnostics
            .Append(entry with { Sequence = sequence })
            .OrderBy(item => item.Sequence)
            .ToArray();
        var next = new OperationsAuditState(
            normalized.SchemaVersion,
            normalized.Audit,
            appended,
            normalized.RetentionDays,
            normalized.MaximumRecords,
            normalized.PurgedThroughSequence,
            normalized.PurgedSequences,
            sequence + 1);
        return ApplyRetention(next, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Applies the owner's retention to the state: entries older than the
    /// retention window and entries beyond the record ceiling are removed, and
    /// the removed prefix is recorded so it cannot come back.
    /// </summary>
    public static OperationsAuditState ApplyRetention(OperationsAuditState state, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);
        var normalized = ValidateAndNormalizeState(state);
        var cutoff = now.ToUniversalTime().AddDays(-normalized.RetentionDays);

        var keptAudit = normalized.Audit
            .Where(entry => entry.RecordedAtUtc >= cutoff)
            .TakeLast(normalized.MaximumRecords)
            .ToArray();
        var keptDiagnostics = normalized.Diagnostics
            .Where(entry => entry.RecordedAtUtc >= cutoff)
            .TakeLast(normalized.MaximumRecords)
            .ToArray();

        var lowestKept = keptAudit.Length == 0 && keptDiagnostics.Length == 0
            ? long.MaxValue
            : Math.Min(
                keptAudit.Length == 0 ? long.MaxValue : keptAudit[0].Sequence,
                keptDiagnostics.Length == 0 ? long.MaxValue : keptDiagnostics[0].Sequence);

        var purgedThrough = normalized.PurgedThroughSequence;
        if (lowestKept != long.MaxValue && lowestKept > purgedThrough)
        {
            purgedThrough = lowestKept - 1;
        }
        else if (lowestKept == long.MaxValue && normalized.Audit.Length + normalized.Diagnostics.Length > 0)
        {
            // Everything was removed: the watermark moves to the highest
            // sequence the state ever used, so a later load cannot serve an
            // entry that the retention pass already removed.
            purgedThrough = Math.Max(normalized.NextSequence - 1, purgedThrough);
        }

        return new OperationsAuditState(
            normalized.SchemaVersion,
            keptAudit,
            keptDiagnostics,
            normalized.RetentionDays,
            normalized.MaximumRecords,
            purgedThrough,
            normalized.PurgedSequences,
            normalized.NextSequence);
    }

    /// <summary>
    /// Removes every audit entry and diagnostic that names one of the given
    /// owner resources. The removed sequence numbers become tombstones, so the
    /// entries stay gone after a restart or a restore.
    /// </summary>
    public static OperationsAuditState RemoveForResources(
        OperationsAuditState state,
        IReadOnlyCollection<string> resourceIds,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(resourceIds);
        var normalized = ValidateAndNormalizeState(state);
        if (resourceIds.Count == 0)
        {
            return normalized;
        }

        var removed = new List<long>();
        var keptAudit = normalized.Audit
            .Where(entry =>
            {
                var drop = entry.ResourceId is not null && resourceIds.Contains(entry.ResourceId);
                if (drop)
                {
                    removed.Add(entry.Sequence);
                }

                return !drop;
            })
            .ToArray();
        var keptDiagnostics = normalized.Diagnostics
            .Where(entry =>
            {
                var drop = entry.ResourceId is not null && resourceIds.Contains(entry.ResourceId);
                if (drop)
                {
                    removed.Add(entry.Sequence);
                }

                return !drop;
            })
            .ToArray();

        var tombstones = normalized.PurgedSequences
            .Concat(removed)
            .Distinct()
            .OrderBy(sequence => sequence)
            .TakeLast(MaximumTombstones)
            .ToArray();

        var next = new OperationsAuditState(
            normalized.SchemaVersion,
            keptAudit,
            keptDiagnostics,
            normalized.RetentionDays,
            normalized.MaximumRecords,
            normalized.PurgedThroughSequence,
            tombstones,
            normalized.NextSequence);
        return ApplyRetention(next, now);
    }

    /// <summary>
    /// Validates one retention choice before anything is removed (S8-T4
    /// correction).
    /// </summary>
    /// <remarks>
    /// The retention act deletes: it drops the entries outside the window,
    /// the entries beyond the ceiling and, when the owner asks for it, the
    /// lines of the safe operational log. A choice that the product refuses
    /// must therefore be refused before the first removal, so an invalid
    /// request can never delete a log line or an audit entry. The caller
    /// validates the whole request first and only then applies it.
    /// </remarks>
    public static void ValidateRetentionChoice(int retentionDays, int maximumRecords)
    {
        if (retentionDays < MinimumRetentionDays || retentionDays > MaximumRetentionDays)
        {
            throw new ArgumentOutOfRangeException(
                nameof(retentionDays),
                "The retention window must be between 1 and 3650 days.");
        }

        if (maximumRecords < MinimumMaximumRecords || maximumRecords > MaximumMaximumRecords)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumRecords),
                "The record ceiling must be between 10 and 5000 records.");
        }
    }

    /// <summary>
    /// Applies a new retention choice to the state and reports how many
    /// entries the new choice removed.
    /// </summary>
    public static (OperationsAuditState State, int RemovedAudit, int RemovedDiagnostics) ApplyRetentionChoice(
        OperationsAuditState state,
        int retentionDays,
        int maximumRecords,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);
        ValidateRetentionChoice(retentionDays, maximumRecords);

        var normalized = ValidateAndNormalizeState(state);
        var chosen = new OperationsAuditState(
            normalized.SchemaVersion,
            normalized.Audit,
            normalized.Diagnostics,
            retentionDays,
            maximumRecords,
            normalized.PurgedThroughSequence,
            normalized.PurgedSequences,
            normalized.NextSequence);
        var applied = ApplyRetention(chosen, now);
        var removedAudit = normalized.Audit.Length - applied.Audit.Length;
        var removedDiagnostics = normalized.Diagnostics.Length - applied.Diagnostics.Length;
        return (applied, Math.Max(0, removedAudit), Math.Max(0, removedDiagnostics));
    }

    /// <summary>True when one audit entry fits every bound of the record.</summary>
    public static bool IsAuditEntryValid(OperationsAuditEntry? entry)
    {
        if (entry is null)
        {
            return false;
        }

        if (entry.Sequence < 1 ||
            entry.RecordedAtUtc.Offset != TimeSpan.Zero ||
            !IsBounded(entry.Actor, MaximumActorLength) ||
            !IsBounded(entry.Action, MaximumActionLength) ||
            !IsBounded(entry.Outcome, MaximumOutcomeLength))
        {
            return false;
        }

        if (entry.ErrorCode is not null && !IsBounded(entry.ErrorCode, MaximumCodeLength))
        {
            return false;
        }

        if (entry.ResourceId is not null && !IsBounded(entry.ResourceId, MaximumResourceIdLength))
        {
            return false;
        }

        return entry.CostUsd is null || (entry.CostUsd >= 0m && entry.CostUsd <= 1_000_000m);
    }

    /// <summary>True when one diagnostic entry fits every bound of the record.</summary>
    public static bool IsDiagnosticEntryValid(OperationsDiagnosticEntry? entry)
    {
        if (entry is null)
        {
            return false;
        }

        if (entry.Sequence < 1 ||
            entry.RecordedAtUtc.Offset != TimeSpan.Zero ||
            !IsBounded(entry.Component, MaximumActorLength) ||
            !IsBounded(entry.Code, MaximumCodeLength) ||
            !IsBounded(entry.OwnerMessage, MaximumMessageLength))
        {
            return false;
        }

        return entry.ResourceId is null || IsBounded(entry.ResourceId, MaximumResourceIdLength);
    }

    private static OperationsAuditEntry NormalizeAuditEntry(OperationsAuditEntry entry) => entry with
    {
        Actor = Truncate(entry.Actor, MaximumActorLength),
        Action = Truncate(entry.Action, MaximumActionLength),
        Outcome = Truncate(entry.Outcome, MaximumOutcomeLength),
        ErrorCode = entry.ErrorCode is null ? null : Truncate(entry.ErrorCode, MaximumCodeLength),
        ResourceId = entry.ResourceId is null ? null : Truncate(entry.ResourceId, MaximumResourceIdLength),
        RecordedAtUtc = entry.RecordedAtUtc.ToUniversalTime(),
    };

    private static OperationsDiagnosticEntry NormalizeDiagnosticEntry(OperationsDiagnosticEntry entry) => entry with
    {
        Component = Truncate(entry.Component, MaximumActorLength),
        Code = Truncate(entry.Code, MaximumCodeLength),
        OwnerMessage = Truncate(entry.OwnerMessage, MaximumMessageLength),
        ResourceId = entry.ResourceId is null ? null : Truncate(entry.ResourceId, MaximumResourceIdLength),
        RecordedAtUtc = entry.RecordedAtUtc.ToUniversalTime(),
    };

    private static bool IsBounded(string? value, int maximum) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximum;

    private static string Truncate(string value, int maximum) =>
        value.Length <= maximum ? value : value[..maximum];
}

/// <summary>
/// The owner-facing guidance for one real error code: what the code means in
/// the product and what the owner can do about it.
/// </summary>
/// <remarks>
/// The table is written for the owner, not for a developer: every sentence
/// names the next action. It never repeats a payload, a path, a credential or
/// a value derived from a secret.
/// </remarks>
public static class OwnerGuidance
{
    private static readonly IReadOnlyDictionary<string, string> Messages =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["vault_locked"] =
                "The vault is locked, so this private read was refused. Unlock the vault in Vault & key, then try again.",
            ["vault_locking"] =
                "The vault is locking, so this private read was refused. Wait for the lock to finish, then unlock it again.",
            ["forbidden"] =
                "This operation was refused because the caller is not the owner. Only the owner channel may run it.",
            ["credential_required"] =
                "The service refused this request because it needed a credential that was not supplied. Enter the owner passphrase or the recovery key and try again.",
            ["invalid_request"] =
                "The service refused this request because it carried a field or a value the operation does not accept. Change the selection and try again.",
            ["token_invalid"] =
                "The handoff token was refused because it is not a token this vault issued. Ask for a new handoff and use it once.",
            ["token_unknown"] =
                "The handoff token was refused because this vault does not know it. Ask for a new handoff and use it once; a token that was never issued here is never accepted.",
            ["token_revoked"] =
                "The handoff token was refused because it was revoked. Approve a new request and use the fresh token.",
            ["token_superseded"] =
                "The handoff token was refused because a newer token replaced it. Use the newest token, once.",
            ["token_unavailable"] =
                "The handoff token could not be read from the vault, so nothing was issued. Unlock the vault and try again.",
            ["token_expired"] =
                "The handoff token was refused because it has expired. Approve a new request and use the fresh token.",
            ["token_used"] =
                "The handoff token was refused because it was already redeemed. A token is single use: request a new one.",
            ["session_revoked"] =
                "The session was refused because it was revoked or has expired. Approve a new request to start a new session.",
            ["provider_not_configured"] =
                "No provider is configured, so the model job was not started. Set the provider, model and cost limit in Jobs, then run it again.",
            ["provider_unconfigured"] =
                "The job was refused before any provider call because no stored provider configuration matches the provider and model of the job. Set the matching provider settings in Jobs, then run the job again.",
            ["pricing_unknown"] =
                "The job was refused before any provider call because this provider and model have no approved pricing card. Pick an approved provider and model, then run the job again.",
            ["unknown_context"] =
                "The job was refused before any provider call because the tokenizer or context window of this model is not known. Pick an approved model, then run the job again.",
            ["input_cap_exceeded"] =
                "The job was refused before any provider call because the framed input would exceed the input cap you chose. Shorten the selection or raise the cap, then run the job again.",
            ["output_cap_exceeded"] =
                "The job was refused before any provider call because the output cap you chose is below the minimum. Raise the cap, then run the job again.",
            ["input_not_readable"] =
                "The job was refused before any provider call because one selected input has no readable text. Choose an input that carries text, then run the job again.",
            ["source_unavailable"] =
                "The job was refused before any provider call because one selected source is no longer readable. Revise the selection, then run the job again.",
            ["destination_unavailable"] =
                "The job was refused before any provider call because the destination record is not in the expected zone. Choose the destination again, then run the job again.",
            ["invalid_job_scope"] =
                "The job was refused before any provider call because its selection is incomplete. Pick the inputs, the objective and the level, then run the job again.",
            ["invalid_job_inputs"] =
                "The job was refused before any provider call because its selected inputs do not fit the chosen profile. Pick the inputs the profile reads, then run the job again.",
            ["unknown_profile"] =
                "The job was refused before any provider call because the chosen structuring profile is not one this product supports. Pick a supported profile, then run the job again.",
            ["synthesis_requires_multiple_inputs"] =
                "The synthesis job was refused before any provider call because it needs at least two selected inputs. Select more inputs, then run the job again.",
            ["objective_required"] =
                "The synthesis job was refused before any provider call because it needs the objective you want addressed. State the objective, then run the job again.",
            ["objective_too_long"] =
                "The synthesis job was refused before any provider call because the objective is longer than the product accepts. Shorten the objective, then run the job again.",
            ["synthesis_level_required"] =
                "The synthesis job was refused before any provider call because it needs the level you want reached. Choose the level, then run the job again.",
            ["synthesis_level_ungrounded"] =
                "The synthesis job was refused before any provider call because a higher level needs an input that is already a synthesis item. Build the levels from the ground up, then run the job again.",
            ["source_revoked"] =
                "The job was refused before any provider call because one selected source was revoked. Choose another source, then run the job again.",
            ["source_quarantined"] =
                "The job was refused before any provider call because one selected source is quarantined. Clear the quarantine in Sources, then run the job again.",
            ["lineage_unbounded"] =
                "The job was refused before any provider call because the selected inputs depend on each other too deeply to validate. Pick shallower inputs, then run the job again.",
            ["budget_exceeded"] =
                "The job was refused before the provider was called because the rolling spend budget would be exceeded. Wait for the budget window to reset or raise the limit, then run the job again.",
            ["rolling_budget_exceeded"] =
                "The job was refused before the provider was called because the rolling 30-day spend budget would be exceeded. Wait for the budget window to reset or raise the limit, then run the job again.",
            ["egress_cost_exceeded"] =
                "The job was refused before the provider was called because it would exceed the cost ceiling of this session. Lower the cost of the run, then run the job again.",
            ["invalid_cost"] =
                "The job was refused before the provider was called because the cost limit it carries is not an amount the product accepts. Set a valid limit, then run the job again.",
            ["job_not_startable"] =
                "The job was not started because it is no longer waiting to run. Read the job state in Jobs, then start a new job if you still need the result.",
            ["schedule_absent"] =
                "No synthesis schedule is configured, so there was nothing to change. Set the schedule first, then try again.",
            ["synthesis_schedule_invalid"] =
                "The synthesis schedule was refused because one of its values is outside what the product accepts. Correct the schedule, then try again.",
            ["audit_persistence_failed"] =
                "The private audit trail could not be written, so this operation is not recorded in it. The operation itself did what the owner asked; read the safe operational log for the detail and try again.",
            ["lineage_cycle"] =
                "The job was refused before any provider call because the selected inputs depend on each other in a cycle. Pick a different selection, then run the job again.",
            ["cancelled"] =
                "The job was cancelled before the provider was called, so nothing was sent and nothing was charged. Run it again when you want it to proceed.",
            ["provider_error"] =
                "The provider call failed, so the job did not produce a result. Check the provider settings and the recorded spend in Jobs, then run the job again.",
            ["cost_limit_exceeded"] =
                "The job was refused before the provider was called because it would exceed the cost limit. Raise the limit you chose or pick a cheaper run.",
            ["job_failed"] =
                "The job ran and failed. Read the failure code in Jobs, fix what it names, and run the job again.",
            ["index_not_materialized"] =
                "The private index is not built for this unlocked epoch, so the library cannot answer yet. Rebuild the library index, then search again.",
            ["index_unavailable"] =
                "The private index could not be read, so no private answer was served. Rebuild the library index, then try again.",
            ["resource_unavailable"] =
                "The record was not served: it may have moved, been purged or failed its integrity check. Nothing else was substituted.",
            ["purge_target_moved"] =
                "The purge target moved after the preview, so nothing was deleted. Preview the purge again against the current revision.",
            ["internal_failure"] =
                "The service hit an unexpected internal failure and served no private answer. Read the Operations diagnostics for the exact code, then retry.",
        };

    /// <summary>
    /// The owner-facing sentence for one error code. An unknown code still
    /// produces an actionable sentence that names the code itself.
    /// </summary>
    public static string ForCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return "The service refused this request without naming a reason. Try the action again; if it fails the same way, read the Operations diagnostics.";
        }

        var trimmed = code.Length > OperationsAuditAuthority.MaximumCodeLength
            ? code[..OperationsAuditAuthority.MaximumCodeLength]
            : code;
        if (Messages.TryGetValue(trimmed, out var message))
        {
            return message;
        }

        return "The service refused this request with the code " + trimmed +
            ". Nothing was changed. Try the action again; if it fails the same way, read the Operations diagnostics.";
    }

    /// <summary>
    /// The owner-facing sentence for one known error code, if the product has
    /// one. A surface that shows the code next to the owner's own explanation
    /// uses this, so a real code never costs the owner the sentence.
    /// </summary>
    public static bool TryGetForCode(string? code, out string message)
    {
        message = string.Empty;
        if (string.IsNullOrWhiteSpace(code))
        {
            return false;
        }

        var trimmed = code.Length > OperationsAuditAuthority.MaximumCodeLength
            ? code[..OperationsAuditAuthority.MaximumCodeLength]
            : code;
        return Messages.TryGetValue(trimmed, out message!);
    }

    /// <summary>
    /// The component token the diagnostics record for one operation, so the
    /// owner can see which part of the product failed.
    /// </summary>
    public static string ComponentFor(string? operation)
    {
        if (string.IsNullOrWhiteSpace(operation))
        {
            return "service";
        }

        if (operation.StartsWith("agent.session", StringComparison.Ordinal) ||
            operation.StartsWith("agent.access", StringComparison.Ordinal))
        {
            return "redemption";
        }

        if (operation.StartsWith("owner.job", StringComparison.Ordinal) ||
            operation.StartsWith("owner.provider", StringComparison.Ordinal) ||
            operation.StartsWith("owner.schedule", StringComparison.Ordinal))
        {
            return "provider";
        }

        if (operation.StartsWith("vault.", StringComparison.Ordinal))
        {
            return "vault";
        }

        if (operation.StartsWith("owner.purge", StringComparison.Ordinal) ||
            operation.StartsWith("owner.operations", StringComparison.Ordinal))
        {
            return "storage";
        }

        if (operation.StartsWith("source.", StringComparison.Ordinal))
        {
            return "source";
        }

        return "service";
    }

    /// <summary>
    /// The cost string the owner view shows. The value comes from the spend
    /// the service already charged; it is never estimated here.
    /// </summary>
    public static string FormatCost(decimal? costUsd) =>
        costUsd is null
            ? "-"
            : "$" + costUsd.Value.ToString("0.######", CultureInfo.InvariantCulture);
}
