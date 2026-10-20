using System.Security.Cryptography;

using MetaBrain.Core.Domain;
using MetaBrain.Core.Security;

namespace MetaBrain.Application;

/// <summary>
/// The owner's review queue: every item whose review-after deadline has
/// passed, whose cited source moved past the revision it cited, whose cited
/// source is gone, or whose cited source states that it is unavailable.
/// </summary>
/// <param name="Entries">The items that ask for the owner's attention, most
/// urgent first, then by ID.</param>
/// <param name="ScannedItems">How many live memory items the evaluation read.</param>
/// <param name="Truncated">True when the scan stopped at its bound, so the
/// owner knows the queue does not cover the whole vault.</param>
public sealed record ReviewQueueResult(
    ReviewEntry[] Entries,
    int ScannedItems,
    bool Truncated);

/// <summary>
/// S7-T6 review-after and lifecycle reconciliation service.
/// <para>
/// The service is a read-time evaluation over live canonical state. It never
/// writes an item, never rewrites a belief, never changes an evidence level,
/// never completes an intention and never deletes anything: a source that
/// moved, vanished or became unavailable is reported as a fact about the
/// evidence, and the owner's own content stays exactly as it was written.
/// </para>
/// <para>
/// Only the vault's own canonical state is read. The service never opens an
/// enrolled source file, never polls anything outside the owner-enrolled
/// allowlist, and never talks to a provider: what it knows about a source's
/// availability is what the registered source record already states.
/// </para>
/// </summary>
public sealed class ReviewQueueService
{
    /// <summary>
    /// The most live memory items one queue evaluation reads. The bound keeps
    /// one owner request inside the pipe timeout on a large vault; a truncated
    /// queue says so instead of pretending it covered everything.
    /// </summary>
    public const int MaximumScannedItems = 20000;

    private readonly TimeProvider _timeProvider;
    private readonly OwnerReviewAuthority _reviews;

    public ReviewQueueService(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _reviews = new OwnerReviewAuthority(_timeProvider);
    }

    /// <summary>
    /// Computes the review queue from live canonical state. The evaluation is
    /// deterministic: it reads the current items, the current registered
    /// sources, the current lifecycle records and the owner's own review
    /// state, and reports what it found without changing anything.
    /// </summary>
    public ReviewQueueResult List(AuthenticatedContext context, IVaultOperation operation)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(operation);
        EnsureOwner(context);

        var now = _timeProvider.GetUtcNow().ToUniversalTime();
        var live = operation.ListResources();
        var sources = new Dictionary<string, LiveSource>(StringComparer.Ordinal);
        var lifecycles = new Dictionary<string, StoredLifecycleRecord>(StringComparer.Ordinal);
        var items = new List<(ScopeResourceRevision Resource, StoredMemoryEnvelope Envelope)>();
        var truncated = false;

        foreach (var resource in live)
        {
            byte[]? raw = null;
            try
            {
                raw = operation.ReadContent(resource.ResourceId, resource.Revision);
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
            {
                continue;
            }

            try
            {
                if (TryDecodeItem(raw, resource, out var envelope))
                {
                    if (items.Count >= MaximumScannedItems)
                    {
                        truncated = true;
                        continue;
                    }

                    items.Add((resource, envelope));
                    continue;
                }

                if (TryDecodeSource(raw, resource, out var source))
                {
                    sources[source.Id] = new LiveSource(
                        resource.Revision, source.Availability, source.RecordedAtUtc);
                    continue;
                }

                if (TryDecodeLifecycle(raw, resource, out var lifecycle))
                {
                    lifecycles[lifecycle.SubjectResourceId] = lifecycle;
                }
            }
            finally
            {
                if (raw is { Length: > 0 })
                {
                    CryptographicOperations.ZeroMemory(raw);
                }
            }
        }

        var reviewState = OwnerReviewAuthority.ValidateAndNormalizeState(operation.LoadReviewState());
        var reviews = new Dictionary<string, OwnerReviewEntry>(StringComparer.Ordinal);
        foreach (var entry in reviewState.Entries)
        {
            reviews[entry.SubjectResourceId] = entry;
        }

        var entries = new List<ReviewEntry>();
        foreach (var (resource, envelope) in items)
        {
            var evaluation = Evaluate(envelope, resource, sources, lifecycles, now);
            if (evaluation is null)
            {
                continue;
            }

            reviews.TryGetValue(envelope.Id, out var review);
            entries.Add(Describe(evaluation, review));
        }

        var ordered = entries
            .OrderByDescending(entry => entry.State == ReviewState.NeedsReview ? 1 : 0)
            .ThenBy(entry => entry.ResourceId, StringComparer.Ordinal)
            .ToArray();

        return new ReviewQueueResult(ordered, items.Count, truncated);
    }

    /// <summary>
    /// Records one explicit owner review of one subject. The subject must be a
    /// live memory item now; the reasons the service computed at that moment
    /// are recorded with the owner's rationale, so the queue can tell a fresh
    /// signal from one the owner already answered. Nothing about the item
    /// itself changes.
    /// </summary>
    public OwnerReviewEntry Record(
        AuthenticatedContext context,
        string subjectResourceId,
        string? rationale,
        IVaultOperation operation)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(operation);
        EnsureOwner(context);

        if (string.IsNullOrEmpty(subjectResourceId) ||
            !DomainIdentifiers.IsDomainIdentifier(subjectResourceId))
        {
            throw new ArgumentException("The review names a valid subject memory item.");
        }

        var evaluation = EvaluateSubject(subjectResourceId, operation);
        if (evaluation is null)
        {
            throw new ArgumentException("The reviewed subject is not a live memory item.");
        }

        var observed = evaluation.Citations
            .Where(citation => !citation.Missing && citation.LiveRevision is { } revision)
            .Select(citation => new OwnerReviewObservation(citation.SourceId, citation.LiveRevision!.Value))
            .ToArray();

        return _reviews.Record(
            context,
            subjectResourceId,
            rationale,
            evaluation.Reasons | evaluation.UndeterminedReasons,
            observed,
            evaluation.ReviewAfter,
            operation);
    }

    /// <summary>
    /// The reasons one subject has right now, with the citations they came
    /// from. Used by the review act and by the queue.
    /// </summary>
    private Evaluation? EvaluateSubject(string subjectResourceId, IVaultOperation operation)
    {
        var live = operation.ListResources();
        foreach (var resource in live)
        {
            if (!string.Equals(resource.ResourceId, subjectResourceId, StringComparison.Ordinal))
            {
                continue;
            }

            byte[]? raw = null;
            try
            {
                raw = operation.ReadContent(resource.ResourceId, resource.Revision);
                if (!TryDecodeItem(raw, resource, out var envelope))
                {
                    return null;
                }

                var sources = new Dictionary<string, LiveSource>(StringComparer.Ordinal);
                var lifecycles = new Dictionary<string, StoredLifecycleRecord>(StringComparer.Ordinal);
                foreach (var candidate in live)
                {
                    if (string.Equals(candidate.ResourceId, subjectResourceId, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    byte[]? content = null;
                    try
                    {
                        content = operation.ReadContent(candidate.ResourceId, candidate.Revision);
                        if (TryDecodeSource(content, candidate, out var source))
                        {
                            sources[source.Id] = new LiveSource(
                                candidate.Revision, source.Availability, source.RecordedAtUtc);
                        }
                        else if (TryDecodeLifecycle(content, candidate, out var lifecycle))
                        {
                            lifecycles[lifecycle.SubjectResourceId] = lifecycle;
                        }
                    }
                    catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
                    {
                    }
                    finally
                    {
                        if (content is { Length: > 0 })
                        {
                            CryptographicOperations.ZeroMemory(content);
                        }
                    }
                }

                return Evaluate(envelope, resource, sources, lifecycles, _timeProvider.GetUtcNow().ToUniversalTime());
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
            {
                return null;
            }
            finally
            {
                if (raw is { Length: > 0 })
                {
                    CryptographicOperations.ZeroMemory(raw);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Evaluates one item against the live sources and its own lifecycle
    /// record. The evaluation separates what is certainly wrong (a passed
    /// deadline, a moved source, a missing source, an unavailable source) from
    /// what cannot be decided (a coarsely known deadline), and it never treats
    /// an unknown availability as anything but unknown.
    /// </summary>
    private static Evaluation? Evaluate(
        StoredMemoryEnvelope envelope,
        ScopeResourceRevision resource,
        IReadOnlyDictionary<string, LiveSource> sources,
        IReadOnlyDictionary<string, StoredLifecycleRecord> lifecycles,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var reasons = ReviewReason.None;
        var undetermined = ReviewReason.None;
        var citations = new List<ReviewCitationStatus>();

        if (lifecycles.TryGetValue(envelope.Id, out var lifecycle) && lifecycle.ReviewAfter is { } reviewAfter)
        {
            switch (reviewAfter.Precision)
            {
                case TimestampPrecision.Exact when reviewAfter.InstantUtc is { } instant && instant <= now:
                    reasons |= ReviewReason.ReviewAfterDue;
                    break;
                case TimestampPrecision.Exact:
                    break;
                default:
                    // A coarse or unknown deadline cannot be compared with now.
                    // It is reported as undetermined instead of being narrowed
                    // into a date that was never recorded.
                    undetermined |= ReviewReason.ReviewAfterUndetermined;
                    break;
            }
        }

        foreach (var citation in envelope.Sources ?? Array.Empty<MemorySourceReference>())
        {
            if (string.IsNullOrEmpty(citation.SourceId))
            {
                continue;
            }

            if (!sources.TryGetValue(citation.SourceId!, out var live))
            {
                reasons |= ReviewReason.CitedSourceMissing;
                citations.Add(new ReviewCitationStatus(
                    citation.SourceId!, citation.SourceRevision, null, SourceAvailability.Unknown, true, null));
                continue;
            }

            citations.Add(new ReviewCitationStatus(
                citation.SourceId!,
                citation.SourceRevision,
                live.Revision,
                live.Availability,
                false,
                live.RecordedAtUtc));

            if (citation.SourceRevision is { } cited && cited != live.Revision)
            {
                reasons |= ReviewReason.CitedSourceRevisionChanged;
            }

            switch (live.Availability)
            {
                case SourceAvailability.Unavailable:
                    reasons |= ReviewReason.CitedSourceUnavailable;
                    break;
                case SourceAvailability.Available:
                    break;
                default:
                    // Unknown availability is reported as unknown in the
                    // citation status. It is not a reason to ask for a review
                    // on its own: the owner never asserted anything about it.
                    break;
            }
        }

        if (reasons == ReviewReason.None && undetermined == ReviewReason.None)
        {
            return null;
        }

        var state = reasons != ReviewReason.None ? ReviewState.NeedsReview : ReviewState.Undetermined;
        return new Evaluation(
            envelope.Id,
            envelope.ZoneId,
            resource.Revision,
            envelope.Kind,
            envelope.Title,
            state,
            reasons,
            undetermined,
            lifecycles.TryGetValue(envelope.Id, out var record) ? record.ReviewAfter : null,
            citations.ToArray());
    }

    /// <summary>
    /// Combines the evaluation with the owner's own last review. A reason the
    /// owner already answered stays answered until the state it was about
    /// changes: a source that is revised again, or a deadline that is set
    /// again, asks the owner once more, because the owner's answer was about
    /// the revision and the deadline they were looking at.
    /// </summary>
    private static ReviewEntry Describe(Evaluation evaluation, OwnerReviewEntry? review)
    {
        var acknowledged = review?.AcknowledgedReasons ?? ReviewReason.None;
        var pending = evaluation.Reasons | evaluation.UndeterminedReasons;
        var reviewedAt = review?.ReviewedAtUtc;

        if (review is not null)
        {
            // The reasons the owner answered are settled, and only the state
            // they were about can open them again.
            pending &= ~acknowledged;
            if (!Equals(review.ObservedReviewAfter, evaluation.ReviewAfter))
            {
                // The review-after deadline is not the one the owner answered.
                pending |= ReviewReason.ReviewAfterDue | ReviewReason.ReviewAfterUndetermined;
            }

            var (moved, becameMissing) = SourcesChangedSince(review.ObservedSources, evaluation.Citations);
            if (moved)
            {
                // A cited source is no longer the one the owner answered.
                pending |= ReviewReason.CitedSourceRevisionChanged | ReviewReason.CitedSourceUnavailable;
            }

            if (becameMissing)
            {
                // A source the owner saw is gone now.
                pending |= ReviewReason.CitedSourceMissing;
            }
        }

        return new ReviewEntry(
            evaluation.ResourceId,
            evaluation.ZoneId,
            evaluation.ResourceRevision,
            LifecycleTokens.Subject(evaluation.Kind),
            evaluation.Title,
            evaluation.State,
            evaluation.Reasons,
            evaluation.UndeterminedReasons,
            evaluation.ReviewAfter,
            reviewedAt,
            review?.Rationale,
            acknowledged,
            pending,
            evaluation.Citations);
    }

    /// <summary>
    /// Whether a cited source is no longer the one the owner answered: a
    /// source that moved to another live revision, a source that is now gone,
    /// or a citation that was not there when the owner looked. The owner's
    /// answer is bound to the revisions they saw, not to a wall-clock moment
    /// the owner does not control.
    /// </summary>
    private static (bool Moved, bool BecameMissing) SourcesChangedSince(
        OwnerReviewObservation[] observed,
        ReviewCitationStatus[] citations)
    {
        var live = citations
            .Where(citation => !citation.Missing && citation.LiveRevision is { } revision)
            .ToDictionary(citation => citation.SourceId, citation => citation.LiveRevision!.Value, StringComparer.Ordinal);
        var moved = false;
        var becameMissing = false;
        foreach (var observation in observed)
        {
            if (!live.TryGetValue(observation.SourceId, out var revision))
            {
                becameMissing = true;
                continue;
            }

            if (revision != observation.LiveRevision)
            {
                moved = true;
            }
        }

        foreach (var citation in citations)
        {
            if (!observed.Any(observation =>
                    string.Equals(observation.SourceId, citation.SourceId, StringComparison.Ordinal)))
            {
                // A citation the owner never saw.
                moved = true;
            }
        }

        return (moved, becameMissing);
    }

    private static bool TryDecodeItem(
        byte[] raw,
        ScopeResourceRevision resource,
        out StoredMemoryEnvelope envelope)
    {
        try
        {
            var decoded = MemoryEnvelopeCodec.DecodeStored(raw);
            if (string.Equals(decoded.Id, resource.ResourceId, StringComparison.Ordinal))
            {
                envelope = decoded;
                return true;
            }
        }
        catch (ArgumentException)
        {
        }

        envelope = null!;
        return false;
    }

    private static bool TryDecodeSource(
        byte[] raw,
        ScopeResourceRevision resource,
        out StoredRegisteredSource source)
    {
        try
        {
            var decoded = RegisteredSourceCodec.DecodeStored(raw);
            if (string.Equals(decoded.Id, resource.ResourceId, StringComparison.Ordinal))
            {
                source = decoded;
                return true;
            }
        }
        catch (ArgumentException)
        {
        }

        source = null!;
        return false;
    }

    private static bool TryDecodeLifecycle(
        byte[] raw,
        ScopeResourceRevision resource,
        out StoredLifecycleRecord lifecycle)
    {
        try
        {
            var decoded = LifecycleCodec.DecodeStored(raw);
            if (string.Equals(decoded.Id, resource.ResourceId, StringComparison.Ordinal))
            {
                lifecycle = decoded;
                return true;
            }
        }
        catch (ArgumentException)
        {
        }

        lifecycle = null!;
        return false;
    }

    private static void EnsureOwner(AuthenticatedContext context)
    {
        if (context.Kind != PrincipalKind.Owner)
        {
            throw new UnauthorizedAccessException("Only the owner may read or record the review queue.");
        }
    }

    private sealed record LiveSource(long Revision, SourceAvailability Availability, DateTimeOffset RecordedAtUtc);

    private sealed record Evaluation(
        string ResourceId,
        string ZoneId,
        long ResourceRevision,
        MemoryKind Kind,
        string? Title,
        ReviewState State,
        ReviewReason Reasons,
        ReviewReason UndeterminedReasons,
        TemporalStamp? ReviewAfter,
        ReviewCitationStatus[] Citations);
}
