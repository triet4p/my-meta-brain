using MetaBrain.Core.Index;
using MetaBrain.Core.Security;

namespace MetaBrain.Application;

/// <summary>
/// S7-T4 ranking reasons: one short, truthful sentence per hit explaining why
/// it is ordered where it is.
/// <para>
/// The reason names only the facts that actually moved this hit. It never
/// claims a fact the ranking did not use, and it never speaks about the
/// item's truth: an item the owner demoted is still exactly as true as it
/// was, and the reason says "demoted by the owner", not "less true".
/// </para>
/// </summary>
public static class RecallRankingReason
{
    /// <summary>
    /// The reason a scoped or owner-library hit is ordered where it is.
    /// </summary>
    public static string Of(RecallDisposition disposition, bool stale, SearchIndexDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var reasons = new List<string>(3);
        switch (disposition)
        {
            case RecallDisposition.Pinned:
                reasons.Add("pinned by the owner");
                break;
            case RecallDisposition.Demoted:
                reasons.Add("demoted by the owner");
                break;
            case RecallDisposition.Archived:
                reasons.Add("archived by the owner");
                break;
        }

        if (stale)
        {
            reasons.Add("the live revision moved past this snapshot");
        }

        if (string.Equals(document.Publication, "withdrawn", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(document.Publication, "rejected", StringComparison.OrdinalIgnoreCase))
        {
            reasons.Add(document.Publication + " publication");
        }

        if (reasons.Count == 0)
        {
            return "matched the query text";
        }

        return string.Join("; ", reasons);
    }
}
