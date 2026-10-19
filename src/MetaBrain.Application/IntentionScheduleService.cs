using System.Security.Cryptography;
using MetaBrain.Core.Domain;
using MetaBrain.Core.Security;

namespace MetaBrain.Application;

/// <summary>One scheduled intention exactly as the unlocked owner service serves it.</summary>
public sealed record ScheduledIntention(
    string ResourceId,
    string ZoneId,
    long LifecycleRevision,
    string State,
    string? Title,
    string BodyPreview,
    DateTimeOffset DueAt,
    bool IsDue,
    bool Notified,
    DateTimeOffset? NotifiedAtUtc,
    bool Acknowledged,
    DateTimeOffset? AcknowledgedAtUtc);

/// <summary>The owner-supplied one-time trigger; cancellation is explicit and distinct from a missing time.</summary>
public sealed record IntentionTimeTriggerDraft(string? DueAt, bool Cancel, long? ExpectedLifecycleRevision = null);

/// <summary>One scheduler evaluation, with no private intention metadata in its result.</summary>
public sealed record IntentionSchedulePassResult(int NotificationsRecorded);

/// <summary>
/// Time-triggered intention scheduling on the existing revision-gated encrypted lifecycle
/// record. The service has no model/provider dependency. A due pass sets the existing
/// notification marker once; acknowledgement and completion remain separate owner acts.
/// </summary>
public sealed class IntentionScheduleService
{
    private const int MaximumScheduledIntentions = 256;
    private const int BodyPreviewLength = 280;
    private const string LifecyclePrefix = "lc-";
    private readonly LifecycleService _lifecycle;
    private readonly TimeProvider _timeProvider;

    public IntentionScheduleService(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _lifecycle = new LifecycleService(_timeProvider);
    }

    /// <summary>
    /// Creates the lifecycle record for a new trigger or revision-gates an edit/cancel
    /// against the live lifecycle record. The owner's trigger is an exact instant with
    /// its supplied offset, never a guessed local time or an LLM-derived date.
    /// </summary>
    public LifecycleRecord SetTimeTrigger(
        AuthenticatedContext context,
        string resourceId,
        string? dueAtText,
        long? expectedLifecycleRevision,
        IManagedResourceStore store)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(store);
        EnsureOwner(context);
        if (string.IsNullOrEmpty(resourceId) || expectedLifecycleRevision is < 1)
        {
            throw new ArgumentException("An intention ID and a positive expected lifecycle revision are required when a revision is supplied.");
        }

        TemporalStamp? dueAt = null;
        if (dueAtText is not null && !IntentionTimeTrigger.TryParse(dueAtText, out dueAt))
        {
            throw new ArgumentException("A due time must be an exact ISO-8601 timestamp with Z or an explicit numeric UTC offset.");
        }

        var subject = ReadIntention(resourceId, store);
        var current = _lifecycle.Read(context, resourceId, expectedRevision: null, store);
        if (expectedLifecycleRevision is { } expected && current?.Revision != expected)
        {
            throw new ArgumentException("The intention lifecycle moved since the owner read it.");
        }

        if (current is null)
        {
            if (dueAt is null)
            {
                throw new ArgumentException("There is no time trigger to cancel.");
            }

            var creation = LifecycleCodec.EncodeDraft(new LifecycleDraft(
                CreationMethod.DirectWrite,
                "Owner configured a one-time time trigger.",
                null,
                null,
                null,
                null,
                null));
            try
            {
                current = _lifecycle.Create(context, resourceId, creation, store);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(creation);
            }
        }

        if (current.SubjectKind != MemoryKind.Intention || current.Intention != IntentionState.Pending ||
            !string.Equals(current.ZoneId, subject.ZoneId, StringComparison.Ordinal))
        {
            throw new ArgumentException("Only a pending intention can have a time trigger.");
        }

        if (Equals(current.IntentionDueAt, dueAt))
        {
            if (dueAt is null)
            {
                throw new ArgumentException("There is no time trigger to cancel.");
            }

            return current;
        }

        var transition = new LifecycleTransitionDraft(
            Rationale: null,
            ReplacementResourceId: null,
            Occurred: null,
            Recorded: null,
            ValidFrom: null,
            ValidUntil: null,
            ReviewAfter: null,
            IntentionDueAt: dueAt,
            ClearIntentionDueAt: dueAt is null);
        var payload = LifecycleCodec.EncodeTransitionDraft(transition);
        try
        {
            return _lifecycle.Transition(
                context,
                resourceId,
                dueAt is null ? LifecycleAct.ClearIntentionDue : LifecycleAct.SetIntentionDue,
                current.Revision,
                payload,
                store);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    /// <summary>
    /// Lists the bounded set of pending intentions with explicit exact time triggers.
    /// A malformed or unavailable canonical record fails the whole answer instead of
    /// silently disappearing from the owner's due view.
    /// </summary>
    public ScheduledIntention[] ListScheduled(AuthenticatedContext context, IManagedResourceStore store)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(store);
        EnsureOwner(context);

        var now = _timeProvider.GetUtcNow().ToUniversalTime();
        var resources = store.ListResources();
        var scheduled = new List<ScheduledIntention>();
        foreach (var resource in resources)
        {
            if (!resource.ResourceId.StartsWith(LifecyclePrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var subjectId = resource.ResourceId[LifecyclePrefix.Length..];
            var lifecycle = _lifecycle.Read(context, subjectId, resource.Revision, store)
                ?? throw new InvalidDataException("A lifecycle record is unavailable to the intention schedule view.");
            if (lifecycle.Intention != IntentionState.Pending || lifecycle.IntentionDueAt is not { } dueStamp)
            {
                continue;
            }

            var subject = ReadIntention(subjectId, store);
            if (!string.Equals(lifecycle.Id, resource.ResourceId, StringComparison.Ordinal) ||
                !string.Equals(lifecycle.ZoneId, resource.ZoneId, StringComparison.Ordinal) ||
                !string.Equals(subject.ZoneId, lifecycle.ZoneId, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The intention lifecycle and subject do not match their canonical resource descriptors.");
            }

            if (scheduled.Count == MaximumScheduledIntentions)
            {
                throw new InvalidDataException("The intention schedule list exceeds its bounded owner view.");
            }

            var instant = dueStamp.InstantUtc!.Value;
            var dueAt = instant.ToOffset(TimeSpan.FromMinutes(dueStamp.TimeZoneOffsetMinutes!.Value));
            scheduled.Add(new ScheduledIntention(
                subjectId,
                subject.ZoneId,
                lifecycle.Revision,
                LifecycleTokens.Intention(lifecycle.Intention!.Value),
                subject.Title,
                subject.Body.Length <= BodyPreviewLength ? subject.Body : subject.Body[..BodyPreviewLength],
                dueAt,
                instant <= now,
                lifecycle.Notified,
                lifecycle.NotifiedAtUtc,
                lifecycle.Acknowledged,
                lifecycle.AcknowledgedAtUtc));
        }

        scheduled.Sort(static (left, right) =>
        {
            var dueOrder = left.DueAt.UtcDateTime.Ticks.CompareTo(right.DueAt.UtcDateTime.Ticks);
            return dueOrder != 0 ? dueOrder : StringComparer.Ordinal.Compare(left.ResourceId, right.ResourceId);
        });
        return scheduled.ToArray();
    }

    /// <summary>
    /// Marks each due pending trigger notified exactly once in its encrypted lifecycle
    /// record. Missed instants are simply due on the next unlocked pass; no queue or
    /// replay is formed, and read/acknowledge never changes intention completion.
    /// </summary>
    public IntentionSchedulePassResult MarkDue(
        AuthenticatedContext context,
        IManagedResourceStore store,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(store);
        EnsureOwner(context);

        var now = _timeProvider.GetUtcNow().ToUniversalTime();
        var resources = store.ListResources();
        var notificationsRecorded = 0;
        foreach (var resource in resources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!resource.ResourceId.StartsWith(LifecyclePrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var subjectId = resource.ResourceId[LifecyclePrefix.Length..];
            var lifecycle = _lifecycle.Read(context, subjectId, resource.Revision, store)
                ?? throw new InvalidDataException("A lifecycle record is unavailable to the intention scheduler.");
            if (lifecycle.Intention != IntentionState.Pending || lifecycle.Notified ||
                lifecycle.IntentionDueAt?.InstantUtc is not { } dueAt || dueAt > now)
            {
                continue;
            }

            _ = _lifecycle.Transition(
                context,
                subjectId,
                LifecycleAct.Notify,
                lifecycle.Revision,
                transitionJson: null,
                store);
            notificationsRecorded++;
        }

        return new IntentionSchedulePassResult(notificationsRecorded);
    }

    private static StoredMemoryEnvelope ReadIntention(string resourceId, IManagedResourceStore store)
    {
        byte[]? raw = null;
        try
        {
            raw = store.ReadContent(resourceId);
            var subject = MemoryEnvelopeCodec.DecodeStored(raw);
            if (!string.Equals(subject.Id, resourceId, StringComparison.Ordinal) || subject.Kind != MemoryKind.Intention)
            {
                throw new ArgumentException("The named memory item is not an intention.");
            }

            return subject;
        }
        catch (IOException ex)
        {
            throw new ArgumentException("The named intention is unavailable.", ex);
        }
        finally
        {
            if (raw is { Length: > 0 })
            {
                CryptographicOperations.ZeroMemory(raw);
            }
        }
    }

    private static void EnsureOwner(AuthenticatedContext context)
    {
        if (context.Kind != PrincipalKind.Owner)
        {
            throw new UnauthorizedAccessException("Intention scheduling is an owner-only private operation.");
        }
    }
}
