namespace MetaBrain.Core.Domain;

/// <summary>
/// Why one item needs the owner's attention, computed from live state at read
/// time (R26/R27/R28/R29).
/// <para>
/// Every reason is an observation about the evidence around an item, never a
/// verdict about the item. A stale citation says the source moved past the
/// revision the item cited; it does not say the item is wrong, and it never
/// rewrites the item's body, evidence level, verification, citations or
/// authored content. An unavailable source says the original is not reachable
/// now; an unknown source says exactly that: nothing is known about it, and
/// unknown is never reported as available or as changed.
/// </para>
/// <para>
/// The reasons are also distinct from each other on purpose: "stale",
/// "unavailable" and "unknown" are three different facts about a source, and
/// collapsing them would tell the owner a story the vault does not support.
/// </para>
/// </para>
/// </summary>
[Flags]
public enum ReviewReason
{
    /// <summary>Nothing about this item needs the owner's attention right now.</summary>
    None = 0,

    /// <summary>
    /// The item's review-after deadline is an exact instant that has already
    /// passed. The deadline is the owner's own recorded time, so it is kept
    /// with its precision and its offset.
    /// </summary>
    ReviewAfterDue = 1,

    /// <summary>
    /// The item's review-after deadline is known only coarsely (a day, a
    /// month, a quarter, a year) or not at all, so it cannot be compared with
    /// the current instant. The deadline is reported as it is; it is never
    /// narrowed into a fabricated date to decide that it passed.
    /// </summary>
    ReviewAfterUndetermined = 2,

    /// <summary>
    /// A source the item cites is not registered in the vault any more. The
    /// item keeps its citation; the missing source is reported, not invented.
    /// </summary>
    CitedSourceMissing = 4,

    /// <summary>
    /// A source the item cites moved past the exact revision the item cited,
    /// so the evidence the item was written against is no longer the live
    /// source. This is the source-change invalidation signal: it says the
    /// synthesis may be out of date, never that it is false.
    /// </summary>
    CitedSourceRevisionChanged = 8,

    /// <summary>
    /// A source the item cites states that its original is unavailable: the
    /// owner-enrolled file is missing, unreadable in a way that is not
    /// conclusive, or its bytes no longer match the enrolled generation.
    /// </summary>
    CitedSourceUnavailable = 16,

    /// <summary>
    /// A source the item cites has no asserted availability at all. Unknown
    /// is reported as unknown and never as available, changed or missing.
    /// </summary>
    CitedSourceUnknown = 32,
}

/// <summary>
/// The review state of one item as the service computed it: nothing to do, a
/// definite reason to look, or a signal that cannot be decided from what the
/// vault knows.
/// </summary>
public enum ReviewState
{
    /// <summary>Nothing about this item asks for the owner's attention.</summary>
    Current = 0,

    /// <summary>A definite reason (a passed deadline, a moved source) asks for the owner's review.</summary>
    NeedsReview = 1,

    /// <summary>
    /// The only signal is one that cannot be decided (a coarsely known
    /// deadline), so the owner is told what is known instead of being given a
    /// verdict.
    /// </summary>
    Undetermined = 2,
}

/// <summary>
/// One cited source as the review evaluation sees it: the exact ID the item
/// cites, the exact revision it cited, the live revision of the registered
/// source now, and what is known about the source's own availability.
/// </summary>
public sealed record ReviewCitationStatus(
    string SourceId,
    long? CitedRevision,
    long? LiveRevision,
    SourceAvailability Availability,
    bool Missing,
    DateTimeOffset? SourceRecordedAtUtc);

/// <summary>
/// One item in the owner's review queue: the identity the vault already holds,
/// the reasons the service computed from live state, the review-after stamp
/// exactly as it was recorded, and the owner's own last review of it.
/// </summary>
public sealed record ReviewEntry(
    string ResourceId,
    string ZoneId,
    long ResourceRevision,
    string Kind,
    string? Title,
    ReviewState State,
    ReviewReason Reasons,
    ReviewReason UndeterminedReasons,
    TemporalStamp? ReviewAfter,
    DateTimeOffset? LastReviewedAtUtc,
    string? LastReviewedRationale,
    ReviewReason AcknowledgedReasons,
    ReviewReason PendingReasons,
    ReviewCitationStatus[] Citations);

/// <summary>
/// Stable wire tokens for the review queue, so every surface names the same
/// reason with the same word and never invents a new one.
/// </summary>
public static class ReviewTokens
{
    /// <summary>The token of one reason; an unknown reason is named "none".</summary>
    public static string Reason(ReviewReason reason) => reason switch
    {
        ReviewReason.ReviewAfterDue => "review_after_due",
        ReviewReason.ReviewAfterUndetermined => "review_after_undetermined",
        ReviewReason.CitedSourceMissing => "cited_source_missing",
        ReviewReason.CitedSourceRevisionChanged => "cited_source_revision_changed",
        ReviewReason.CitedSourceUnavailable => "cited_source_unavailable",
        ReviewReason.CitedSourceUnknown => "cited_source_unknown",
        _ => "none"
    };

    /// <summary>The tokens of every reason in the set, in a stable order.</summary>
    public static string[] Reasons(ReviewReason reasons)
    {
        if (reasons == ReviewReason.None)
        {
            return Array.Empty<string>();
        }

        var ordered = new List<string>(6);
        if (reasons.HasFlag(ReviewReason.ReviewAfterDue))
        {
            ordered.Add(Reason(ReviewReason.ReviewAfterDue));
        }

        if (reasons.HasFlag(ReviewReason.ReviewAfterUndetermined))
        {
            ordered.Add(Reason(ReviewReason.ReviewAfterUndetermined));
        }

        if (reasons.HasFlag(ReviewReason.CitedSourceMissing))
        {
            ordered.Add(Reason(ReviewReason.CitedSourceMissing));
        }

        if (reasons.HasFlag(ReviewReason.CitedSourceRevisionChanged))
        {
            ordered.Add(Reason(ReviewReason.CitedSourceRevisionChanged));
        }

        if (reasons.HasFlag(ReviewReason.CitedSourceUnavailable))
        {
            ordered.Add(Reason(ReviewReason.CitedSourceUnavailable));
        }

        if (reasons.HasFlag(ReviewReason.CitedSourceUnknown))
        {
            ordered.Add(Reason(ReviewReason.CitedSourceUnknown));
        }

        return ordered.ToArray();
    }

    /// <summary>The token of one review state.</summary>
    public static string State(ReviewState state) => state switch
    {
        ReviewState.NeedsReview => "needs_review",
        ReviewState.Undetermined => "undetermined",
        _ => "current"
    };

    /// <summary>The token of one cited source's availability.</summary>
    public static string Availability(SourceAvailability availability, bool missing) => missing
        ? "missing"
        : availability switch
        {
            SourceAvailability.Available => "available",
            SourceAvailability.Unavailable => "unavailable",
            _ => "unknown"
        };
}
