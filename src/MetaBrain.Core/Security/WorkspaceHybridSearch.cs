using System.Globalization;
using System.Text;

namespace MetaBrain.Core.Security;

/// <summary>
/// The retrieval layers one hybrid owner result can come from. A literal hit
/// keeps the layer that produced it, and a semantic neighbour that overlaps a
/// literal hit keeps both.
/// </summary>
public static class WorkspaceHybridChannels
{
    /// <summary>An exact file-name or logical-path literal match.</summary>
    public const string Exact = "exact";

    /// <summary>A literal content match with identifier boundaries.</summary>
    public const string Lexical = "lexical";

    /// <summary>A literal Markdown heading match.</summary>
    public const string Structural = "structural";

    /// <summary>A local passage-embedding neighbour, never a literal match.</summary>
    public const string Semantic = "semantic";
}

/// <summary>
/// How one hybrid hit relates to the query. A literal match is an exact,
/// bounded occurrence of the query text in the source. A semantic candidate is
/// only a local-model neighbour: its relevance is not confirmed, it is not
/// supporting evidence and it is not an answer. Consumers must keep the two
/// classes distinguishable instead of counting every row as a match.
/// </summary>
public static class WorkspaceHybridEvidenceClass
{
    public const string LiteralMatch = "literal_match";
    public const string SemanticCandidate = "semantic_candidate";
}

/// <summary>The literal fields a hybrid request may narrow to.</summary>
public static class WorkspaceHybridSearchFields
{
    public const string Name = "name";
    public const string Path = "path";
    public const string Heading = "heading";
    public const string Content = "content";

    public static bool IsKnown(string? field) =>
        field is null or Name or Path or Heading or Content;
}

/// <summary>The currentness selection of a hybrid request.</summary>
public static class WorkspaceHybridSearchCurrent
{
    public const string CurrentOnly = "current";
    public const string HistoryOnly = "history";
    public const string All = "all";

    public static bool IsKnown(string? current) =>
        current is null or CurrentOnly or HistoryOnly or All;
}

/// <summary>
/// Why the semantic channel did or did not participate in a hybrid result.
/// The status is always reported, so a caller never has to assume that a
/// lexical-only answer was a semantic one.
/// </summary>
public static class WorkspaceHybridSemanticStatus
{
    public const string Applied = "applied";
    public const string AppliedCurrentOnly = "applied_current_only";
    public const string AbstainedExactQuery = "abstained_exact_query";
    public const string NotApplicableHistory = "not_applicable_history";
    public const string NotApplicableField = "not_applicable_field";
    public const string NotApplicableEmptyScope = "not_applicable_empty_scope";
}

/// <summary>
/// One hybrid candidate, already resolved to an exact file revision. Literal
/// candidates carry their <see cref="Score"/>; semantic candidates carry their
/// <see cref="Similarity"/>. A merged candidate carries both channel names.
/// The evidence sentence is built only for the returned page, so an in-scope
/// candidate is never dropped to save output space.
/// </summary>
public sealed record WorkspaceHybridCandidate(
    string FileId,
    string Path,
    long ContentRevision,
    string FileSha256,
    string Field,
    string[] Channels,
    bool Stale,
    int? Line,
    long? ByteOffset,
    long? ByteLength,
    string? AnchorSha256,
    int? TextOffset,
    int? TextLength,
    string? AnchorKind,
    string? Attribution,
    int? ChunkOrdinal,
    int Score,
    double? Similarity,
    string? Evidence = null)
{
    /// <summary>True for every hit that came from a literal source match.</summary>
    public bool IsLiteral => Channels.Any(channel => !string.Equals(channel, WorkspaceHybridChannels.Semantic, StringComparison.Ordinal));
}

/// <summary>
/// The one shared owner hybrid ranking policy: one deterministic order, one
/// cross-channel de-duplication rule, and the exact-literal abstention rule.
///
/// Ordering contract:
/// 1. literal evidence (name, path, heading, content) before every
///    semantic-only neighbour, so an exact symbol, date or phrase is never
///    displaced by a semantically similar passage — not even by a neighbour
///    of current content;
/// 2. inside the literal band and inside the semantic band, current evidence
///    before retained (stale) evidence, because a superseded revision must
///    never outrank current content of the same strength; a retained literal
///    hit is still exact evidence and stays above semantic neighbours;
/// 3. the literal score, then the semantic similarity, descending;
/// 4. path, file identity, byte offset, byte length, field and chunk ordinal,
///    all ordinal, so equal evidence always resolves the same way.
/// </summary>
public static class WorkspaceHybridSearchPolicy
{
    /// <summary>
    /// True for a query that is one identifier-shaped token: a symbol, an
    /// identifier, a path fragment, a date or a number. Such a query has a
    /// determinate literal answer, so the semantic channel abstains instead of
    /// returning near-miss neighbours for it. This is the conservative
    /// abstention rule for the frozen symbol and boundary negatives; it is not
    /// a similarity threshold.
    /// </summary>
    public static bool IsExactLiteralQuery(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return false;
        }

        var value = query.AsSpan().Trim();
        var length = 0;
        var letters = 0;
        var lowercaseLetters = 0;
        var digits = 0;
        var delimiters = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            length++;
            if (Rune.IsWhiteSpace(rune) || Rune.IsControl(rune))
            {
                return false;
            }

            if (Rune.IsLetter(rune))
            {
                letters++;
                if (Rune.ToLowerInvariant(rune) == rune)
                {
                    lowercaseLetters++;
                }
            }
            else if (Rune.IsDigit(rune))
            {
                digits++;
            }
            else if (rune.Value is '_' or '-' or '.' or '/' or '#' or ':' or '@' or '+' or '&' or '=' or '%' or '~' or '\\' or '|')
            {
                delimiters++;
            }
            else
            {
                return false;
            }
        }

        if (length == 0)
        {
            return false;
        }

        return digits > 0 || delimiters > 0 || (letters >= 2 && lowercaseLetters == 0);
    }

    /// <summary>
    /// Collapses cross-channel duplicates and returns the deterministic order.
    /// A semantic neighbour whose anchored passage contains a literal hit is
    /// merged into that hit, so the same evidence is never returned twice. A
    /// semantic candidate keeps at most one hit per file revision.
    /// </summary>
    public static WorkspaceHybridCandidate[] RankAndDeduplicate(IReadOnlyList<WorkspaceHybridCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count == 0)
        {
            return [];
        }

        var results = new List<WorkspaceHybridCandidate>(candidates.Count);
        var literalKeys = new Dictionary<string, int>(StringComparer.Ordinal);
        var semanticKeys = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            if (!candidate.IsLiteral)
            {
                var semanticKey = SemanticKey(candidate);
                if (semanticKeys.TryGetValue(semanticKey, out var existingSemantic))
                {
                    var current = results[existingSemantic];
                    if ((candidate.Similarity ?? double.NegativeInfinity) >
                        (current.Similarity ?? double.NegativeInfinity))
                    {
                        results[existingSemantic] = candidate;
                    }

                    continue;
                }

                semanticKeys.Add(semanticKey, results.Count);
                results.Add(candidate);
                continue;
            }

            var key = DuplicateKey(candidate);
            if (literalKeys.TryGetValue(key, out var existing))
            {
                results[existing] = MergeChannels(results[existing], candidate);
                continue;
            }

            literalKeys.Add(key, results.Count);
            results.Add(candidate);
        }

        for (var index = 0; index < results.Count; index++)
        {
            var neighbour = results[index];
            if (neighbour.IsLiteral || neighbour.ByteOffset is not { } neighbourOffset ||
                neighbour.ByteLength is not { } neighbourLength)
            {
                continue;
            }

            var merged = false;
            for (var host = 0; host < results.Count; host++)
            {
                var candidate = results[host];
                if (candidate is null || !candidate.IsLiteral ||
                    !string.Equals(candidate.FileId, neighbour.FileId, StringComparison.Ordinal) ||
                    candidate.ContentRevision != neighbour.ContentRevision ||
                    candidate.ByteOffset is not { } offset ||
                    candidate.ByteLength is not { } length ||
                    !Contains(neighbourOffset, neighbourLength, offset, length))
                {
                    continue;
                }

                results[host] = MergeChannels(candidate, neighbour);
                merged = true;
            }

            if (merged)
            {
                results[index] = null!;
            }
        }

        results.RemoveAll(candidate => candidate is null);
        results.Sort(Compare);
        return results.ToArray();
    }

    /// <summary>
    /// The evidence class of one resolved candidate: a hit that carries any
    /// literal channel is a literal match; a semantic-only hit is a candidate.
    /// </summary>
    public static string EvidenceClassOf(WorkspaceHybridCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return candidate.IsLiteral
            ? WorkspaceHybridEvidenceClass.LiteralMatch
            : WorkspaceHybridEvidenceClass.SemanticCandidate;
    }

    /// <summary>Compares two resolved candidates by the shared deterministic order.</summary>
    public static int Compare(WorkspaceHybridCandidate? left, WorkspaceHybridCandidate? right)
    {
        if (ReferenceEquals(left, right))
        {
            return 0;
        }

        if (left is null)
        {
            return 1;
        }

        if (right is null)
        {
            return -1;
        }

        var tier = Tier(left).CompareTo(Tier(right));
        if (tier != 0)
        {
            return tier;
        }

        double score;
        if (left.IsLiteral)
        {
            score = right.Score.CompareTo(left.Score);
        }
        else
        {
            score = (right.Similarity ?? double.NegativeInfinity).CompareTo(left.Similarity ?? double.NegativeInfinity);
        }

        if (score != 0)
        {
            return score < 0 ? -1 : 1;
        }

        var comparison = string.CompareOrdinal(left.Path, right.Path);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = string.CompareOrdinal(left.FileId, right.FileId);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = Nullable.Compare(left.ByteOffset, right.ByteOffset);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = Nullable.Compare(left.ByteLength, right.ByteLength);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = string.CompareOrdinal(left.Field, right.Field);
        if (comparison != 0)
        {
            return comparison;
        }

        return Nullable.Compare(left.ChunkOrdinal, right.ChunkOrdinal);
    }

    private static int Tier(WorkspaceHybridCandidate candidate) =>
        (candidate.IsLiteral ? 0 : 2) + (candidate.Stale ? 1 : 0);

    private static bool Contains(long outerOffset, long outerLength, long innerOffset, long innerLength) =>
        innerOffset >= outerOffset &&
        innerLength <= outerLength &&
        innerOffset - outerOffset <= outerLength - innerLength;

    private static string SemanticKey(WorkspaceHybridCandidate candidate) =>
        string.Concat(
            candidate.FileId, "\n",
            candidate.ContentRevision.ToString(CultureInfo.InvariantCulture));

    private static string DuplicateKey(WorkspaceHybridCandidate candidate) =>
        string.Concat(
            candidate.FileId, "\n",
            candidate.ContentRevision.ToString(CultureInfo.InvariantCulture), "\n",
            candidate.Field, "\n",
            candidate.ByteOffset?.ToString(CultureInfo.InvariantCulture) ?? "-", "\n",
            candidate.ByteLength?.ToString(CultureInfo.InvariantCulture) ?? "-");

    private static WorkspaceHybridCandidate MergeChannels(
        WorkspaceHybridCandidate host,
        WorkspaceHybridCandidate other)
    {
        var channels = host.Channels;
        foreach (var channel in other.Channels)
        {
            if (!channels.Contains(channel, StringComparer.Ordinal))
            {
                channels = [.. channels, channel];
            }
        }

        var similarity = host.Similarity;
        if (other.Similarity is { } otherSimilarity &&
            (similarity is null || otherSimilarity > similarity.Value))
        {
            similarity = otherSimilarity;
        }

        return host with
        {
            Channels = channels,
            Similarity = similarity,
            AnchorKind = host.AnchorKind ?? other.AnchorKind,
            Attribution = host.Attribution ?? other.Attribution,
            ChunkOrdinal = host.ChunkOrdinal ?? other.ChunkOrdinal,
        };
    }
}
