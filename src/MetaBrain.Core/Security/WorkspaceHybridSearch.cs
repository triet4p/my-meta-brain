using System.Globalization;
using MetaBrain.Core.Domain;
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

/// <summary>The purpose-aware owner recall route, resolved without a provider.</summary>
public sealed record WorkspaceRecallRoute(string Purpose, CognitiveFacet? Facet, string SelectionReason);

/// <summary>
/// Canonical route names accepted by the owner workspace recall command.
/// </summary>
public static class WorkspaceRecallPurposes
{
    public const string Auto = "auto";
    public const string Experience = "experience";
    public const string Temporal = "temporal";
    public const string Intention = "intention";
    public const string ActionEffect = "action-effect";
    public const string General = "general";

    public static bool IsKnown(string? purpose) =>
        purpose is null ||
        string.Equals(purpose, Auto, StringComparison.Ordinal) ||
        string.Equals(purpose, Experience, StringComparison.Ordinal) ||
        string.Equals(purpose, Temporal, StringComparison.Ordinal) ||
        string.Equals(purpose, Intention, StringComparison.Ordinal) ||
        string.Equals(purpose, ActionEffect, StringComparison.Ordinal) ||
        string.Equals(purpose, General, StringComparison.Ordinal);
}

/// <summary>
/// Deterministic rule-first recall routing. Precedence is action/effect,
/// intention, temporal, experience, then general; no route asserts truth or
/// changes the shared hybrid evidence order.
/// </summary>
public static class WorkspaceRecallRouter
{
    private static readonly string[] ActionEffectTerms =
    [
        "action-effect", "action effect", "effect", "impact", "cause", "caused", "because",
        "correlation", "outcome", "result", "tác động", "ảnh hưởng", "kết quả", "do đâu", "vì sao"
    ];

    private static readonly string[] IntentionTerms =
    [
        "intention", "intend", "intended", "plan", "planned", "remind", "reminder", "complete",
        "completed", "finish", "finished", "done", "will", "to-do", "todo", "dự định",
        "kế hoạch", "nhắc", "hoàn thành", "xong"
    ];

    private static readonly string[] TemporalTerms =
    [
        "when", "date", "time", "occurred", "recorded", "valid", "timeline", "history",
        "before", "after", "since", "lúc nào", "khi nào", "ngày nào", "thời điểm"
    ];

    private static readonly string[] ExperienceTerms =
    [
        "experience", "experienced", "attempt", "attempted", "tried", "what happened",
        "what did i", "did i", "trải nghiệm", "kinh nghiệm", "đã thử", "lần thử", "tôi đã", "mình đã"
    ];

    public static WorkspaceRecallRoute Resolve(string query, string requestedPurpose)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(requestedPurpose);
        if (!WorkspaceRecallPurposes.IsKnown(requestedPurpose))
        {
            throw new ArgumentException("The workspace recall purpose is not supported.", nameof(requestedPurpose));
        }

        if (!string.Equals(requestedPurpose, WorkspaceRecallPurposes.Auto, StringComparison.Ordinal))
        {
            return Route(requestedPurpose);
        }

        if (ContainsAnyTerm(query, ActionEffectTerms))
        {
            return Route(WorkspaceRecallPurposes.ActionEffect);
        }

        if (ContainsAnyTerm(query, IntentionTerms))
        {
            return Route(WorkspaceRecallPurposes.Intention);
        }

        if (ContainsAnyTerm(query, TemporalTerms))
        {
            return Route(WorkspaceRecallPurposes.Temporal);
        }

        return ContainsAnyTerm(query, ExperienceTerms)
            ? Route(WorkspaceRecallPurposes.Experience)
            : Route(WorkspaceRecallPurposes.General);
    }

    private static WorkspaceRecallRoute Route(string purpose) => purpose switch
    {
        WorkspaceRecallPurposes.Experience => new(
            purpose, CognitiveFacet.Experience,
            "Selected because the explicit route is attributed experience evidence."),
        WorkspaceRecallPurposes.Temporal => new(
            purpose, null,
            "Selected to report stored event/recorded/validity stamps separately from content dates; absent stamps remain unknown."),
        WorkspaceRecallPurposes.Intention => new(
            purpose, CognitiveFacet.Intention,
            "Selected because the explicit route is prospective intention evidence; only stored lifecycle state is reported."),
        WorkspaceRecallPurposes.ActionEffect => new(
            purpose, CognitiveFacet.ActionEffect,
            "Selected because the explicit route is action/effect evidence; association does not establish causality."),
        _ => new(
            WorkspaceRecallPurposes.General, null,
            "No purpose-specific rule matched; results remain ordinary scoped candidates.")
    };

    private static bool ContainsAnyTerm(string query, IReadOnlyList<string> terms)
    {
        foreach (var term in terms)
        {
            if (ContainsTerm(query, term))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsTerm(string value, string term)
    {
        var start = 0;
        while (start <= value.Length - term.Length)
        {
            var found = value.IndexOf(term, start, StringComparison.OrdinalIgnoreCase);
            if (found < 0)
            {
                return false;
            }

            var end = found + term.Length;
            var leftBoundary = found == 0 || !char.IsLetterOrDigit(value[found - 1]);
            var rightBoundary = end == value.Length || !char.IsLetterOrDigit(value[end]);
            if (leftBoundary && rightBoundary)
            {
                return true;
            }

            start = found + 1;
        }

        return false;
    }
}
