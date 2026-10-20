using MetaBrain.Core.Domain;

namespace MetaBrain.Core.Security;

/// <summary>
/// One cited source as the owner saw it during a review: the source ID and the
/// live revision that was current then. The observation is what the owner's
/// answer was about: when the source moves to another revision afterwards,
/// the answer no longer covers it.
/// </summary>
public sealed record OwnerReviewObservation(string SourceId, long LiveRevision);

/// <summary>
/// One recorded owner review of one subject memory item: when the owner
/// looked, who looked, the owner's own rationale, and the reasons the service
/// had computed at that moment. The acknowledged reasons are what the owner
/// saw; they never claim that the underlying evidence changed again.
/// </summary>
public sealed record OwnerReviewChange(
    DateTimeOffset RecordedAtUtc,
    ExecutorKind Executor,
    string? Rationale,
    ReviewReason AcknowledgedReasons);

/// <summary>
/// The owner's last review of one subject memory item, with the bounded
/// history of earlier reviews. This is owner curation about the owner's own
/// attention, not a statement about the item's truth.
/// <para>
/// The entry also carries what the owner was looking at: the live revision of
/// every cited source and the review-after deadline exactly as it was then.
/// The acknowledgement is bound to that observation, so a source that moves
/// again, or a deadline that is set again, asks the owner once more instead
/// of being answered by an answer about the earlier state.
/// </para>
/// </summary>
public sealed record OwnerReviewEntry(
    string SubjectResourceId,
    DateTimeOffset ReviewedAtUtc,
    ExecutorKind Executor,
    string? Rationale,
    ReviewReason AcknowledgedReasons,
    OwnerReviewObservation[] ObservedSources,
    TemporalStamp? ObservedReviewAfter,
    OwnerReviewChange[] History)
{
    public const int MaximumHistory = 32;
    public const int MaximumObservedSources = 256;
}

/// <summary>
/// The whole owner review state of a vault: one entry per subject the owner
/// has reviewed, plus the schema version of the stored file.
/// <para>
/// This is one encrypted owner state beside the scope-grant, catalog,
/// source-enrollment and recall-policy states. It is not canonical content: it
/// never appears in the manifest, never changes an item's revision, never
/// invalidates the private index, and it never carries an item's body. It
/// only records that the owner looked at an item and what the owner saw then,
/// so the review queue can tell a fresh signal from one the owner already
/// answered.
/// </para>
/// </summary>
public sealed record OwnerReviewState(
    int SchemaVersion,
    OwnerReviewEntry[] Entries)
{
    public const int CurrentSchemaVersion = 1;

    public static OwnerReviewState Empty { get; } = new(
        CurrentSchemaVersion, Array.Empty<OwnerReviewEntry>());
}

/// <summary>
/// Encrypted persistence for the owner review state, implemented by the vault
/// lifecycle so a review act shares the same atomic update and lock/epoch
/// semantics as scope grants, the owner catalog, source enrollment and the
/// recall policy.
/// </summary>
public interface IReviewStatePersistence
{
    OwnerReviewState LoadReviewState();

    T UpdateReviewState<T>(
        Func<OwnerReviewState, (OwnerReviewState State, T Result)> update);
}

/// <summary>
/// S7-T6 owner review authority: the only way a subject's review state
/// changes is an explicit owner act through this authority.
/// <para>
/// The authority never touches canonical content: it cannot change an item's
/// body, evidence, verification, revision, citations, attribution or times,
/// and it cannot delete an item or complete an intention. It records that the
/// owner reviewed the item, which reasons the owner saw, when, and why. A
/// stale, unavailable or unknown source is therefore never turned into a
/// denial of the item's belief: the belief stays exactly as the owner wrote
/// it, and the review only says the owner looked.
/// </para>
/// </summary>
public sealed class OwnerReviewAuthority
{
    public const int MaximumSubjects = 20000;
    public const int MaximumRationaleLength = 1000;

    private readonly TimeProvider _timeProvider;

    public OwnerReviewAuthority(TimeProvider? timeProvider = null) =>
        _timeProvider = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// Records one explicit owner review of one subject. The subject must be
    /// an existing memory item (the caller checks that against canonical
    /// state), the rationale is the owner's own words, and the acknowledged
    /// reasons are exactly what the service computed when the owner looked.
    /// </summary>
    public OwnerReviewEntry Record(
        AuthenticatedContext actor,
        string subjectResourceId,
        string? rationale,
        ReviewReason acknowledgedReasons,
        OwnerReviewObservation[] observedSources,
        TemporalStamp? observedReviewAfter,
        IReviewStatePersistence persistence)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(subjectResourceId);
        ArgumentNullException.ThrowIfNull(persistence);
        EnsureOwner(actor);

        if (!IsValidSubjectId(subjectResourceId))
        {
            throw new ArgumentException("The review names a valid subject memory item.", nameof(subjectResourceId));
        }

        var reason = NormalizeRationale(rationale);
        var observations = ValidateObservations(observedSources);
        var now = _timeProvider.GetUtcNow().ToUniversalTime();
        return persistence.UpdateReviewState<OwnerReviewEntry>(current => RecordLocked(
            current, subjectResourceId, reason, acknowledgedReasons, observations, observedReviewAfter, actor, now));
    }

    /// <summary>
    /// Reads the owner's last review of one subject. A subject the owner never
    /// reviewed has no entry, which means nothing about it has been answered
    /// yet.
    /// </summary>
    public OwnerReviewEntry? Read(string subjectResourceId, IReviewStatePersistence persistence)
    {
        ArgumentNullException.ThrowIfNull(subjectResourceId);
        ArgumentNullException.ThrowIfNull(persistence);

        var state = ValidateAndNormalizeState(persistence.LoadReviewState());
        return state.Entries.FirstOrDefault(entry =>
            string.Equals(entry.SubjectResourceId, subjectResourceId, StringComparison.Ordinal));
    }

    private (OwnerReviewState State, OwnerReviewEntry Recorded) RecordLocked(
        OwnerReviewState state,
        string subjectResourceId,
        string rationale,
        ReviewReason acknowledgedReasons,
        OwnerReviewObservation[] observedSources,
        TemporalStamp? observedReviewAfter,
        AuthenticatedContext actor,
        DateTimeOffset now)
    {
        var validated = ValidateAndNormalizeState(state);
        var index = Array.FindIndex(validated.Entries, entry =>
            string.Equals(entry.SubjectResourceId, subjectResourceId, StringComparison.Ordinal));
        var existing = index >= 0 ? validated.Entries[index] : null;
        var change = new OwnerReviewChange(now, ExecutorOf(actor), rationale, acknowledgedReasons);
        var updated = new OwnerReviewEntry(
            subjectResourceId,
            now,
            ExecutorOf(actor),
            rationale,
            acknowledgedReasons,
            observedSources,
            observedReviewAfter,
            AppendHistory(existing?.History ?? Array.Empty<OwnerReviewChange>(), change));

        var entries = validated.Entries.ToArray();
        if (index >= 0)
        {
            entries[index] = updated;
        }
        else
        {
            if (entries.Length >= MaximumSubjects)
            {
                throw new ArgumentException("The review state holds the maximum number of subjects.", nameof(subjectResourceId));
            }

            entries = entries.Append(updated).ToArray();
        }

        return (new OwnerReviewState(validated.SchemaVersion, entries), updated);
    }

    private static OwnerReviewChange[] AppendHistory(OwnerReviewChange[] history, OwnerReviewChange change)
    {
        var appended = history.Append(change).ToArray();
        return appended.Length <= OwnerReviewEntry.MaximumHistory
            ? appended
            : appended[^OwnerReviewEntry.MaximumHistory..];
    }

    /// <summary>
    /// Validates and normalizes a stored review state: a known schema
    /// version, one entry per subject, known reasons, and a bounded history.
    /// A stored file that cannot be validated is refused rather than repaired
    /// into a different review history than the owner recorded.
    /// </summary>
    public static OwnerReviewState ValidateAndNormalizeState(OwnerReviewState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.SchemaVersion != OwnerReviewState.CurrentSchemaVersion)
        {
            throw new InvalidDataException("The review state schema version is unsupported.");
        }

        if (state.Entries.Length > MaximumSubjects)
        {
            throw new InvalidDataException("The review state holds more subjects than the store allows.");
        }

        var known = KnownReasons();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var entries = new OwnerReviewEntry[state.Entries.Length];
        for (var index = 0; index < state.Entries.Length; index++)
        {
            var entry = state.Entries[index];
            if (entry is null || !IsValidSubjectId(entry.SubjectResourceId) || !seen.Add(entry.SubjectResourceId))
            {
                throw new InvalidDataException("The review state names an invalid or repeated subject.");
            }

            if ((entry.AcknowledgedReasons & ~known) != ReviewReason.None)
            {
                throw new InvalidDataException("The review state holds an unknown review reason.");
            }

            var observations = entry.ObservedSources ?? Array.Empty<OwnerReviewObservation>();
            if (observations.Length > OwnerReviewEntry.MaximumObservedSources)
            {
                throw new InvalidDataException("The review state holds too many observed sources.");
            }

            var observedIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var observation in observations)
            {
                if (observation is null || string.IsNullOrEmpty(observation.SourceId) ||
                    !observedIds.Add(observation.SourceId) || observation.LiveRevision < 1)
                {
                    throw new InvalidDataException("The review state names an invalid observed source.");
                }
            }

            if (entry.ReviewedAtUtc.Offset != TimeSpan.Zero)
            {
                throw new InvalidDataException("A review time must be UTC.");
            }

            var history = entry.History ?? Array.Empty<OwnerReviewChange>();
            if (history.Length > OwnerReviewEntry.MaximumHistory)
            {
                throw new InvalidDataException("The review history exceeds its bound.");
            }

            entries[index] = entry with { History = history, ObservedSources = observations };
        }

        return new OwnerReviewState(state.SchemaVersion, entries);
    }

    private static ReviewReason KnownReasons() =>
        ReviewReason.ReviewAfterDue |
        ReviewReason.ReviewAfterUndetermined |
        ReviewReason.CitedSourceMissing |
        ReviewReason.CitedSourceRevisionChanged |
        ReviewReason.CitedSourceUnavailable |
        ReviewReason.CitedSourceUnknown;

    private static OwnerReviewObservation[] ValidateObservations(OwnerReviewObservation[]? observedSources)
    {
        var observations = observedSources ?? Array.Empty<OwnerReviewObservation>();
        if (observations.Length > OwnerReviewEntry.MaximumObservedSources)
        {
            throw new ArgumentException("The review holds too many observed sources.", nameof(observedSources));
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var normalized = new OwnerReviewObservation[observations.Length];
        for (var index = 0; index < observations.Length; index++)
        {
            var observation = observations[index];
            if (observation is null || string.IsNullOrEmpty(observation.SourceId) ||
                !seen.Add(observation.SourceId) || observation.LiveRevision < 1)
            {
                throw new ArgumentException("The review names an invalid observed source.", nameof(observedSources));
            }

            normalized[index] = observation;
        }

        return normalized;
    }

    private static string NormalizeRationale(string? rationale)
    {
        if (string.IsNullOrWhiteSpace(rationale))
        {
            throw new ArgumentException("Recording a review requires the owner's own reason.", nameof(rationale));
        }

        var trimmed = rationale.Trim();
        if (trimmed.Length > MaximumRationaleLength)
        {
            throw new ArgumentException("The review rationale is too long.", nameof(rationale));
        }

        if (trimmed.Any(char.IsControl))
        {
            throw new ArgumentException("The review rationale contains control characters.", nameof(rationale));
        }

        return trimmed;
    }

    private static bool IsValidSubjectId(string subjectResourceId) =>
        !string.IsNullOrEmpty(subjectResourceId) &&
        subjectResourceId.Length <= 200 &&
        subjectResourceId.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static ExecutorKind ExecutorOf(AuthenticatedContext context) =>
        context.Kind == PrincipalKind.Owner ? ExecutorKind.Owner : ExecutorKind.Agent;

    private static void EnsureOwner(AuthenticatedContext context)
    {
        if (context.Kind != PrincipalKind.Owner)
        {
            throw new UnauthorizedAccessException("Only the owner may record a review.");
        }
    }
}
