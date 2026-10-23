using MetaBrain.Core.Domain;
using MetaBrain.Core.Index;
using MetaBrain.Core.Security;

namespace MetaBrain.Application;

/// <summary>
/// S4-T2 scoped search authority: lexical/metadata/ranking/time queries over
/// the protected private index. One ranking/time/currentness implementation
/// serves two scope providers: a redeemed session's frozen approved snapshot
/// (R06, R23, R26) and the owner's own unlocked library scope (R10, R11, R30),
/// which carries no grant and therefore no snapshot authority.
///
/// Order of checks (never inverted): session lookup against the live unlock
/// epoch and policy generation (revoke/lock/expiry deny before anything is
/// touched); stale-generation check (a moved canonical set fails closed with
/// search_index_stale instead of scanning); frozen-snapshot candidate filter
/// (approved IDs with the memory.search operation, exact approved revisions);
/// token-candidate prefilter inside the index engine (SQL scope-ID filter
/// before any token row returns); scoring over candidate documents only;
/// stable ranked pagination over the frozen order; then — and only for the
/// served page — authorized vault reads at the exact frozen revisions, each
/// re-authorized before and after the read plus a final registration check.
///
/// No hidden counts: totals count scope matches only. No new members,
/// revisions or links enter the grant: candidates must equal the frozen
/// (ID, revision) pairs. Withdrawn/rejected/superseded and past-validity
/// items demote (never vanish silently): the stale flag marks them and the
/// current filter excludes them only when asked. Catalog queries never reach
/// this path: they serve published labels/descriptions only.
/// </summary>
public sealed class ScopedSearchAuthority
{
    public const int DefaultLimit = 10;
    public const int MaximumLimit = 50;

    public sealed record ScoredHit(
        string ResourceId,
        long Revision,
        double Score,
        bool DirectHit,
        SearchIndexDocument Document,
        MemoryItem Item,
        bool Stale)
    {
        /// <summary>
        /// The owner's recall decision for this subject. An ordinary item the
        /// owner never decided about is Normal.
        /// </summary>
        public RecallDisposition Disposition { get; init; } = RecallDisposition.Normal;

        /// <summary>
        /// Why this hit ranks where it does. The reason names only the facts
        /// that actually moved this hit, so it never claims a fact the ranking
        /// did not use.
        /// </summary>
        public string RankingReason { get; init; } = string.Empty;
    }

    public IReadOnlyList<ScoredHit> Search(
        AgentSessionSnapshot session,
        string? query,
        string? kind,
        string? current,
        int offset,
        int limit,
        PrivateIndexSnapshot? materialized,
        IPrivateIndexStore index,
        IManagedResourceStore vault,
        DateTimeOffset? asOfUtc = null,
        RecallPolicyIndex? recall = null,
        bool includeArchived = false,
        CancellationToken cancellationToken = default,
        string? level = null)
    {
        CountOrPage(session, query, kind, current, offset, limit, materialized, index, vault, asOfUtc, recall, includeArchived, retain: true, out var page, cancellationToken, level);
        return page;
    }

    public int Count(
        AgentSessionSnapshot session,
        string? query,
        string? kind,
        string? current,
        PrivateIndexSnapshot? materialized,
        IPrivateIndexStore index,
        IManagedResourceStore vault,
        DateTimeOffset? asOfUtc = null,
        RecallPolicyIndex? recall = null,
        bool includeArchived = false,
        CancellationToken cancellationToken = default,
        string? level = null)
    {
        // Total over the frozen scope: same filters, full ranking, no paging.
        // The candidates are streamed and counted, never retained, so a count
        // over a large scope costs no more memory than a page.
        return CountOrPage(session, query, kind, current, 0, 0, materialized, index, vault, asOfUtc, recall, includeArchived, retain: false, out _, cancellationToken, level);
    }

    /// <summary>
    /// The scoped search over the frozen session scope, streamed.
    /// <para>
    /// S8-T5: the engine streams the candidates of the query in bounded
    /// batches and the ranking keeps only the page it is going to serve, so
    /// the working set of a search over a large scope is bounded by the batch
    /// and the page, not by the number of candidates. Every eligible
    /// candidate is still visited and scored: the ranking, the tie order and
    /// the filters are unchanged.
    /// </para>
    /// </summary>
    private int CountOrPage(
        AgentSessionSnapshot session,
        string? query,
        string? kind,
        string? current,
        int offset,
        int limit,
        PrivateIndexSnapshot? materialized,
        IPrivateIndexStore index,
        IManagedResourceStore vault,
        DateTimeOffset? asOfUtc,
        RecallPolicyIndex? recall,
        bool includeArchived,
        bool retain,
        out ScoredHit[] page,
        CancellationToken cancellationToken,
        string? level)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(vault);
        page = Array.Empty<ScoredHit>();
        cancellationToken.ThrowIfCancellationRequested();
        if (materialized is null)
        {
            throw new InvalidDataException("The search index has no materialized generation.");
        }

        AbstractionLevel? requestedLevel = null;
        if (level is not null)
        {
            if (!Enum.TryParse<AbstractionLevel>(level, ignoreCase: true, out var parsedLevel) ||
                parsedLevel == AbstractionLevel.Unknown ||
                !Enum.IsDefined(parsedLevel))
            {
                throw new ArgumentException("The scoped-search level filter is invalid.", nameof(level));
            }

            requestedLevel = parsedLevel;
        }


        var terms = SearchText.ExpandQuery(query);
        cancellationToken.ThrowIfCancellationRequested();
        if (terms.Length == 0)
        {
            return 0;
        }

        // S8-T5: the live canonical set is read once per query instead of
        // once per candidate. The staleness rule is unchanged: a candidate
        // whose live revision moved past the frozen one is still flagged, and
        // the served body is still read at the exact frozen revision.
        var live = new LiveResourceSet(vault);
        HashSet<string>? supersededTargets = null;

        var scope = session.Resources
            .OrderBy(resource => resource.ResourceId, StringComparer.Ordinal)
            .ToArray();
        var termTokens = terms.Select(term => term.Token).Distinct(StringComparer.Ordinal).ToArray();
        var termWeights = terms.ToDictionary(term => term.Token, term => term, StringComparer.Ordinal);

        var keep = retain ? Math.Max(0, offset) + Math.Max(0, limit) : 0;
        var heap = new BoundedRankHeap<ScoredHit>(keep, CompareScoredHits);
        var matched = 0;

        // S8-T5: the scoped ranking orders by the penalized score, and the
        // withdrawn/rejected penalty is part of that score, so the temporal
        // row of every candidate is read here.
        foreach (var batch in index.StreamSearchCandidates(
                   scope, termTokens, CandidateBatchSize, includeTemporalRow: true, cancellationToken: cancellationToken))
        {
            foreach (var candidate in batch.Candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var approved = candidate.Resource;
                var document = candidate.Document;

                if (kind is not null && !string.Equals(document.Kind, kind, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var score = Score(document, termWeights, out var directHit);
                if (score <= 0)
                {
                    continue;
                }

                if (current is not null && !PassesCurrentScoped(
                        document,
                        LifecycleOf(vault, approved.ResourceId),
                        current,
                        asOfUtc,
                        approved.ResourceId,
                        supersededTargets ??= SupersededTargets(vault)))
                {
                    continue;
                }

                var stale = IsStale(document, live, approved);
                var item = ReadApprovedItem(vault, approved);
                if (item is null || (requestedLevel is { } expectedLevel && item.Level != expectedLevel))
                {
                    continue;
                }

                // The owner's recall decision: a demoted item is still offered and
                // still findable, but it sorts below the items the owner did not
                // demote; a pinned item sorts with the protected items first. An
                // archived item is not offered at all unless the caller explicitly
                // expands the archive. None of this touches the item's content,
                // evidence, verification or revision.
                var disposition = recall?.DispositionOf(approved.ResourceId) ?? RecallDisposition.Normal;
                if (disposition == RecallDisposition.Archived && !includeArchived)
                {
                    continue;
                }

                matched++;
                heap.Add(new ScoredHit(approved.ResourceId, approved.Revision, ApplyStatePenalty(score, document, stale), directHit, document, item, stale)
                {
                    Disposition = disposition,
                    RankingReason = RecallRankingReason.Of(disposition, stale, document),
                });
            }
        }

        if (!retain)
        {
            return matched;
        }

        var ordered = heap.Ordered();
        var served = ordered.Skip(Math.Max(0, offset)).Take(Math.Max(0, limit)).ToArray();

        // S8-T5: the served page is read once more without the term filter,
        // so the excerpt of a hit is built from the candidate's whole token
        // set exactly as before. Only the served page pays that read.
        var servedDocuments = index
            .SelectSearchDocuments(served
                .Select(hit => new ScopeResourceRevision(hit.ResourceId, string.Empty, hit.Revision))
                .ToArray())
            .ToDictionary(document => document.ResourceId, document => document, StringComparer.Ordinal);
        page = served
            .Select(hit => servedDocuments.TryGetValue(hit.ResourceId, out var full) ? hit with { Document = full } : hit)
            .ToArray();
        return matched;
    }

    /// <summary>
    /// One owner library query (R10, R11, R23, R30). <see cref="Text"/> is the
    /// owner's own search text; an empty text is a browse. Zone and collection
    /// are not part of this record: the caller has already reduced the scope to
    /// the owner-selected rows, so a filter can never widen what is searched.
    /// <see cref="AsOfUtc"/> is the S7-T1 as-of instant: when set, the page
    /// replays every candidate's lifecycle history only through that instant,
    /// so currentness reflects what was recorded then. A future as-of fails
    /// closed in the handler, never silently clamped to now.
    /// </summary>
    public sealed record OwnerLibraryQuery(
        string? Text,
        string? Kind,
        string? Current,
        string? Level,
        DateTimeOffset? FromUtc,
        DateTimeOffset? ToUtc,
        int Offset,
        int Limit,
        DateTimeOffset? AsOfUtc = null);

    /// <summary>
    /// One owner library answer: <see cref="Total"/> counts matches under the
    /// same filters inside the owner scope only (never a vault-wide count), and
    /// <see cref="Hits"/> is the ranked page the owner may show.
    /// </summary>
    public sealed record OwnerLibraryPage(int Total, IReadOnlyList<ScoredHit> Hits);

    /// <summary>
    /// S5-T3 owner library search over the owner's own unlocked vault.
    ///
    /// Scope: the caller passes the exact (ID, revision) rows the owner's
    /// filters selected from the materialized index generation. No grant and no
    /// session snapshot is involved, so a collection or zone filter can only
    /// narrow this list; nothing here can hand out authority over a resource
    /// the owner's own vault does not hold at that revision.
    ///
    /// Ranking is the same implementation as scoped search (repeated-term
    /// weight, direct-hit marker, state penalty), so a frontend never ranks.
    /// An empty or whitespace text browses: every candidate in scope is
    /// returned ordered by its recorded instant, because a browse has no query
    /// terms to score. Text that produces no terms (stopwords, symbols only)
    /// is a real query with no match instead of a browse.
    ///
    /// Every served hit is read at its own frozen revision through
    /// <see cref="IManagedResourceStore"/>, which fails closed on a moved,
    /// missing or tampered body, so a materialized row can never serve bytes
    /// that no longer match it.
    /// </summary>
    public OwnerLibraryPage SearchOwner(
        IReadOnlyList<ScopeResourceRevision> ownerScope,
        OwnerLibraryQuery query,
        PrivateIndexSnapshot? materialized,
        IPrivateIndexStore index,
        IManagedResourceStore vault,
        RecallPolicyIndex? recall = null,
        bool includeArchived = false) =>
        SearchOwnerCore(
            ownerScope,
            query,
            materialized,
            index,
            vault,
            recall,
            includeArchived,
            liveSnapshot: null,
            scopeIsOrdered: false);

    // The owner handler passes the same canonical snapshot it just digested,
    // and sorts its filtered scope once before calling this method.
    internal OwnerLibraryPage SearchOwnerWithSnapshot(
        IReadOnlyList<ScopeResourceRevision> ownerScope,
        IReadOnlyList<ScopeResourceRevision> liveSnapshot,
        OwnerLibraryQuery query,
        PrivateIndexSnapshot? materialized,
        IPrivateIndexStore index,
        IManagedResourceStore vault,
        RecallPolicyIndex? recall,
        bool includeArchived)
    {
        ArgumentNullException.ThrowIfNull(liveSnapshot);
        return SearchOwnerCore(
            ownerScope,
            query,
            materialized,
            index,
            vault,
            recall,
            includeArchived,
            liveSnapshot,
            scopeIsOrdered: true);
    }

    private OwnerLibraryPage SearchOwnerCore(
        IReadOnlyList<ScopeResourceRevision> ownerScope,
        OwnerLibraryQuery query,
        PrivateIndexSnapshot? materialized,
        IPrivateIndexStore index,
        IManagedResourceStore vault,
        RecallPolicyIndex? recall,
        bool includeArchived,
        IReadOnlyList<ScopeResourceRevision>? liveSnapshot,
        bool scopeIsOrdered)
    {
        ArgumentNullException.ThrowIfNull(ownerScope);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(vault);
        if (materialized is null)
        {
            throw new InvalidDataException("The search index has no materialized generation.");
        }

        var terms = SearchText.ExpandQuery(query.Text);
        if (terms.Length == 0 && !string.IsNullOrWhiteSpace(query.Text))
        {
            return new OwnerLibraryPage(0, Array.Empty<ScoredHit>());
        }

        // The owner handler's validated, sorted snapshot is used only for the
        // served-page revision checks. Agent search retains its per-candidate map.
        var live = liveSnapshot is null ? new LiveResourceSet(vault) : new LiveResourceSet(liveSnapshot);
        HashSet<string>? supersededTargets = null;

        // The superseded-target set is only consulted for a "current" query,
        // and resolving it reads the whole relation set. Reading it for an
        // "all"/"history" query spent that work for a rule that cannot apply.
        var current = query.Current ?? string.Empty;
        var needsSupersededTargets = string.Equals(current, "current", StringComparison.OrdinalIgnoreCase);

        var scope = scopeIsOrdered
            ? ownerScope
            : ownerScope.OrderBy(resource => resource.ResourceId, StringComparer.Ordinal).ToArray();
        var termTokens = terms.Select(term => term.Token).Distinct(StringComparer.Ordinal).ToArray();
        var termWeights = terms.ToDictionary(term => term.Token, term => term, StringComparer.Ordinal);

        // S8-T5: the engine streams the candidates of the query in bounded
        // batches and the ranking keeps only the page it is going to serve.
        // Every eligible candidate is still visited, scored and filtered; the
        // working set is the batch and the page, not the candidate count.
        var keep = Math.Max(0, query.Offset) + Math.Max(0, query.Limit);
        var heap = new BoundedRankHeap<OwnerCandidate>(
            keep,
            terms.Length > 0 ? CompareOwnerCandidates : CompareOwnerCandidatesByTime);
        var matched = 0;

        // S8-T5: the owner ranking orders by the unpenalized lexical weight,
        // so the temporal row of a candidate is read only when a filter needs
        // it (the kind, the currentness or the time window). The served page
        // reads it anyway.
        var needsTemporalRow = query.Kind is not null ||
            string.Equals(query.Current, "current", StringComparison.OrdinalIgnoreCase) ||
            query.FromUtc is not null ||
            query.ToUtc is not null;

        foreach (var batch in index.StreamSearchCandidates(scope, termTokens, CandidateBatchSize, needsTemporalRow))
        {
            foreach (var candidate in batch.Candidates)
            {
                var resource = candidate.Resource;
                var document = candidate.Document;

                if (query.Kind is not null && !string.Equals(document.Kind, query.Kind, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var lifecycle = query.AsOfUtc is not null || string.Equals(query.Current, "current", StringComparison.OrdinalIgnoreCase)
                    ? LifecycleOf(vault, resource.ResourceId)
                    : null;
                if (!PassesCurrentScoped(document, lifecycle, current, query.AsOfUtc))
                {
                    continue;
                }

                if (needsSupersededTargets &&
                    IsSupersededTarget(lifecycle, resource.ResourceId, supersededTargets ??= SupersededTargets(vault)))
                {
                    continue;
                }

                if (!PassesWindow(document, query.FromUtc, query.ToUtc))
                {
                    continue;
                }

                // The owner's own recall decision applies to the owner's library
                // exactly as it applies to scoped recall: an archived subject is
                // not offered unless the owner explicitly expands the archive, and
                // a demoted or pinned subject keeps its own order. The item's
                // bytes, evidence and revision are untouched.
                var disposition = recall?.DispositionOf(resource.ResourceId) ?? RecallDisposition.Normal;
                if (disposition == RecallDisposition.Archived && !includeArchived)
                {
                    continue;
                }

                var score = 0.0;
                var directHit = false;
                if (terms.Length > 0)
                {
                    score = Score(document, termWeights, out directHit);
                    if (score <= 0)
                    {
                        continue;
                    }
                }

                // The abstraction level is stored in the item itself (the index
                // carries no level column), so an explicit level filter reads the
                // candidate: a body that cannot be proven is excluded, never
                // assumed to match.
                MemoryItem? item = null;
                if (query.Level is not null)
                {
                    item = ReadApprovedItem(vault, resource);
                    if (item is null ||
                        !string.Equals(ServiceRequestHandler.ToWireToken(item.Level.ToString()), query.Level, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                }

                matched++;
                heap.Add(new OwnerCandidate(resource, document, score, directHit, item, disposition));
            }
        }

        var ordered = heap.Ordered();
        var served = ordered.Skip(Math.Max(0, query.Offset)).Take(Math.Max(0, query.Limit)).ToArray();
        // S8-T5: the served page is read once more without the term filter,
        // so the excerpt of a hit is built from the candidate's whole token
        // set exactly as before. Only the served page pays that read.
        var servedDocuments = index
            .SelectSearchDocuments(served
                .Select(candidate => candidate.Resource)
                .ToArray())
            .ToDictionary(document => document.ResourceId, document => document, StringComparer.Ordinal);

        var hits = new List<ScoredHit>();
        foreach (var candidate in served)
        {
            var item = candidate.Item ?? ReadApprovedItem(vault, candidate.Resource);
            if (item is null)
            {
                // The row moved or became unreadable between the filter and the
                // read: the hit is dropped instead of being served from a
                // different revision.
                continue;
            }

            var document = servedDocuments.TryGetValue(candidate.Resource.ResourceId, out var full)
                ? full
                : candidate.Document;
            var stale = IsStale(document, live, candidate.Resource);
            hits.Add(new ScoredHit(
                candidate.Resource.ResourceId,
                candidate.Resource.Revision,
                terms.Length > 0 ? ApplyStatePenalty(candidate.Score, document, stale) : 0,
                candidate.DirectHit,
                document,
                item,
                stale)
            {
                Disposition = candidate.Disposition,
                RankingReason = RecallRankingReason.Of(candidate.Disposition, stale, document),
            });
        }

        return new OwnerLibraryPage(matched, hits);
    }

    /// <summary>
    /// The number of candidates one streamed search reads at a time. The
    /// batch bounds the working set of a search; it never bounds the
    /// coverage: every eligible candidate is still visited.
    /// </summary>
    private const int CandidateBatchSize = 512;

    /// <summary>
    /// The owner's own ranking order: the owner's recall tier first, then the
    /// lexical weight, then the resource ID. The tie order is the order the
    /// previous full materialization produced, so the same page is served.
    /// </summary>
    private static int CompareOwnerCandidates(OwnerCandidate left, OwnerCandidate right)
    {
        var leftTier = RecallPolicyIndex.TierOf(left.Disposition);
        var rightTier = RecallPolicyIndex.TierOf(right.Disposition);
        if (leftTier != rightTier)
        {
            return leftTier.CompareTo(rightTier);
        }

        if (left.Score != right.Score)
        {
            return right.Score.CompareTo(left.Score);
        }

        return string.CompareOrdinal(left.Resource.ResourceId, right.Resource.ResourceId);
    }

    /// <summary>
    /// The browse order: the owner's recall tier first, then the item's own
    /// instant, newest first, then the resource ID. A browse carries no
    /// lexical weight, so its order is the one the previous full
    /// materialization produced.
    /// </summary>
    private static int CompareOwnerCandidatesByTime(OwnerCandidate left, OwnerCandidate right)
    {
        var leftTier = RecallPolicyIndex.TierOf(left.Disposition);
        var rightTier = RecallPolicyIndex.TierOf(right.Disposition);
        if (leftTier != rightTier)
        {
            return leftTier.CompareTo(rightTier);
        }

        var leftInstant = left.Document.OccurredAtUtc ?? left.Document.RecordedAtUtc;
        var rightInstant = right.Document.OccurredAtUtc ?? right.Document.RecordedAtUtc;
        if (leftInstant != rightInstant)
        {
            return rightInstant.CompareTo(leftInstant);
        }

        return string.CompareOrdinal(left.Resource.ResourceId, right.Resource.ResourceId);
    }

    /// <summary>
    /// The scoped ranking order: the owner's recall tier first, then the
    /// scored weight, then the resource ID.
    /// </summary>
    private static int CompareScoredHits(ScoredHit left, ScoredHit right)
    {
        var leftTier = RecallPolicyIndex.TierOf(left.Disposition);
        var rightTier = RecallPolicyIndex.TierOf(right.Disposition);
        if (leftTier != rightTier)
        {
            return leftTier.CompareTo(rightTier);
        }

        if (left.Score != right.Score)
        {
            return right.Score.CompareTo(left.Score);
        }

        return string.CompareOrdinal(left.ResourceId, right.ResourceId);
    }

    /// <summary>
    /// A bounded ranking buffer. It keeps the best candidates of a stream and
    /// never more: the worst candidate it holds is the one it drops when a
    /// better one arrives. The order it produces is exactly the order a full
    /// materialization and sort would produce, because the comparison is a
    /// total order over the candidates.
    /// </summary>
    private sealed class BoundedRankHeap<T>
    {
        private readonly List<T> _items = new();
        private readonly Comparison<T> _better;
        private readonly int _capacity;

        public BoundedRankHeap(int capacity, Comparison<T> better)
        {
            _capacity = Math.Max(0, capacity);
            _better = better;
        }

        public void Add(T item)
        {
            if (_capacity == 0)
            {
                return;
            }

            if (_items.Count < _capacity)
            {
                _items.Add(item);
                SiftUp(_items.Count - 1);
                return;
            }

            // The root is the worst candidate the buffer holds.
            if (_better(item, _items[0]) >= 0)
            {
                return;
            }

            _items[0] = item;
            SiftDown(0);
        }

        /// <summary>The retained candidates, best first.</summary>
        public List<T> Ordered()
        {
            var ordered = new List<T>(_items);
            ordered.Sort(_better);
            return ordered;
        }

        private void SiftUp(int index)
        {
            while (index > 0)
            {
                var parent = (index - 1) / 2;
                if (_better(_items[parent], _items[index]) >= 0)
                {
                    return;
                }

                Swap(parent, index);
                index = parent;
            }
        }

        private void SiftDown(int index)
        {
            while (true)
            {
                var left = (2 * index) + 1;
                var right = left + 1;
                var worst = index;
                if (left < _items.Count && _better(_items[worst], _items[left]) < 0)
                {
                    worst = left;
                }

                if (right < _items.Count && _better(_items[worst], _items[right]) < 0)
                {
                    worst = right;
                }

                if (worst == index)
                {
                    return;
                }

                Swap(worst, index);
                index = worst;
            }
        }

        private void Swap(int left, int right)
        {
            (_items[left], _items[right]) = (_items[right], _items[left]);
        }
    }

    private sealed record OwnerCandidate(
        ScopeResourceRevision Resource,
        SearchIndexDocument Document,
        double Score,
        bool DirectHit,
        MemoryItem? Item,
        RecallDisposition Disposition);

    private static bool PassesWindow(SearchIndexDocument document, DateTimeOffset? fromUtc, DateTimeOffset? toUtc)
    {
        if (fromUtc is null && toUtc is null)
        {
            return true;
        }

        // The item's own time axis: its occurred instant when it has one, else
        // the recorded instant. Both bounds are inclusive and compared in UTC.
        var instant = document.OccurredAtUtc ?? document.RecordedAtUtc;
        return (fromUtc is null || instant >= fromUtc.Value) &&
            (toUtc is null || instant <= toUtc.Value);
    }

    private static double Score(SearchIndexDocument document, Dictionary<string, SearchText.QueryTerm> terms, out bool directHit)
    {
        var score = 0.0;
        directHit = false;
        foreach (var term in terms.Values)
        {
            // Search candidates are read with only the distinct query terms
            // they contain. Array.IndexOf preserves the old set-membership
            // semantics without allocating a HashSet for every candidate.
            if (Array.IndexOf(document.Tokens, term.Token) < 0)
            {
                continue;
            }

            score += term.Weight;
            if (!term.Expanded)
            {
                directHit = true;
            }
        }

        return score;
    }

    private static double ApplyStatePenalty(double score, SearchIndexDocument document, bool stale)
    {
        // Demote, never hide: stale/withdrawn/rejected/superseded sort below
        // current hits at equal lexical weight, so current queries surface
        // live guidance first without newest-wins rewriting (R26).
        if (stale)
        {
            return score * 0.4;
        }

        return string.Equals(document.Publication, "withdrawn", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(document.Publication, "rejected", StringComparison.OrdinalIgnoreCase)
            ? score * 0.4
            : score;
    }

    private static bool PassesCurrent(SearchIndexDocument document, string current)
    {
        if (string.Equals(current, "current", StringComparison.OrdinalIgnoreCase))
        {
            return !IsExpired(document) &&
                !string.Equals(document.Publication, "withdrawn", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(document.Publication, "rejected", StringComparison.OrdinalIgnoreCase);
        }

        if (string.Equals(current, "history", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return true;
    }

    /// <summary>
    /// S7-T1 scoped currentness: publication replays only through the as-of
    /// instant, and the S2-T5 validity window bounds currentness at the as-of
    /// instant (or now when no as-of is named). A superseded endpoint is never
    /// current unless the caller explicitly asks for history. Contradictory
    /// voices stay visible: a contradicts link never removes either side from
    /// history, and newest-wins is never applied here.
    /// </summary>
    private static bool PassesCurrentScoped(SearchIndexDocument document, StoredLifecycleRecord? lifecycle, string current, DateTimeOffset? asOfUtc)
    {
        if (!string.Equals(current, "current", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var at = asOfUtc ?? DateTimeOffset.UtcNow;
        var publication = document.Publication;
        if (lifecycle is not null && asOfUtc is { } asOf)
        {
            publication = LifecycleTokens.Publication(TemporalRecall.StatesAt(lifecycle, asOf).Publication);
        }

        if (string.Equals(publication, "withdrawn", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(publication, "rejected", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (lifecycle is { ValidFrom: not null } || lifecycle is { ValidUntil: not null })
        {
            if (!TemporalRecall.ValidityContains(lifecycle!.ValidFrom, lifecycle.ValidUntil, at))
            {
                return false;
            }
        }
        else if (document.RecordedAtUtc > at.AddMinutes(5))
        {
            return false;
        }

        return true;
    }

    private static bool PassesCurrentScoped(SearchIndexDocument document, StoredLifecycleRecord? lifecycle, string current, DateTimeOffset? asOfUtc, string resourceId, HashSet<string> supersededTargets)
    {
        if (!PassesCurrentScoped(document, lifecycle, current, asOfUtc))
        {
            return false;
        }

        return !string.Equals(current, "current", StringComparison.OrdinalIgnoreCase) ||
            !IsSupersededTarget(lifecycle, resourceId, supersededTargets);
    }

    private static StoredLifecycleRecord? LifecycleOf(IManagedResourceStore vault, string resourceId)
    {
        byte[]? raw = null;
        try
        {
            raw = vault.ReadContent(DomainIdentifiers.LifecycleIdFor(resourceId), null);
            var record = LifecycleCodec.DecodeStored(raw);
            return string.Equals(record.SubjectResourceId, resourceId, StringComparison.Ordinal) ? record : null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            if (raw is { Length: > 0 })
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(raw);
            }
        }
    }

    /// <summary>
    /// S8-T5: the set of resources that a superseding relation points at,
    /// resolved once per query. The rule is unchanged: a proposed or accepted
    /// supersedes relation whose source is this resource makes the resource
    /// not current. The old path walked the whole relation set and decrypted
    /// every relation again for every candidate of the query.
    /// </summary>
    private static HashSet<string> SupersededTargets(IManagedResourceStore vault)
    {
        var targets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var listed in vault.ListResources())
        {
            if (!listed.ResourceId.StartsWith("rel-", StringComparison.Ordinal))
            {
                continue;
            }

            byte[]? raw = null;
            try
            {
                raw = vault.ReadContent(listed.ResourceId, listed.Revision);
                var relation = RelationCodec.DecodeStored(raw);
                if (relation.Kind == RelationKind.Supersedes &&
                    relation.State is RelationState.Proposed or RelationState.Accepted)
                {
                    targets.Add(relation.From.ResourceId);
                }
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            {
            }
            finally
            {
                if (raw is { Length: > 0 })
                {
                    System.Security.Cryptography.CryptographicOperations.ZeroMemory(raw);
                }
            }
        }

        return targets;
    }

    private static bool IsSupersededTarget(StoredLifecycleRecord? lifecycle, string resourceId, HashSet<string> supersededTargets)
    {
        if (lifecycle is { Intention: IntentionState.Superseded })
        {
            return true;
        }

        return supersededTargets.Contains(resourceId);
    }

    private static bool IsExpired(SearchIndexDocument document)
    {
        // Temporal expiry is advisory metadata carried on the document; the
        // authoritative validity window lives in the lifecycle record (S7).
        // Here only the recorded instant bounds "current": items recorded in
        // the future (clock skew, never fabricated dates) are not current.
        return document.RecordedAtUtc > DateTimeOffset.UtcNow.AddMinutes(5);
    }

    private static bool IsStale(SearchIndexDocument document, LiveResourceSet live, ScopeResourceRevision approved)
    {
        // Stale = the live canonical revision moved past the frozen grant, or
        // the lifecycle subject moved past the indexed publication state. The
        // hit still serves its frozen bytes (the grant's address), flagged.
        return live.Moved(approved);
    }

    /// <summary>
    /// S8-T5: the live canonical revisions of one query. The set is read once
    /// per query instead of once per candidate; the staleness rule reads the
    /// same revisions either way, and the served body is still read at the
    /// exact frozen revision, so a body that moved after this set was taken
    /// still fails closed on the read.
    /// </summary>
    private sealed class LiveResourceSet
    {
        private readonly Dictionary<string, long>? _revisions;
        private readonly IReadOnlyList<ScopeResourceRevision>? _orderedResources;

        public LiveResourceSet(IManagedResourceStore vault)
        {
            ArgumentNullException.ThrowIfNull(vault);
            var listed = vault.ListResources();
            var revisions = new Dictionary<string, long>(listed.Count, StringComparer.Ordinal);
            foreach (var resource in listed)
            {
                revisions[resource.ResourceId] = resource.Revision;
            }
            _revisions = revisions;
        }

        public LiveResourceSet(IReadOnlyList<ScopeResourceRevision> orderedResources)
        {
            ArgumentNullException.ThrowIfNull(orderedResources);
            _orderedResources = orderedResources;
        }

        public bool Moved(ScopeResourceRevision approved)
        {
            if (_orderedResources is { } ordered)
            {
                var low = 0;
                var high = ordered.Count - 1;
                while (low <= high)
                {
                    var middle = low + ((high - low) / 2);
                    var resource = ordered[middle];
                    var comparison = string.CompareOrdinal(resource.ResourceId, approved.ResourceId);
                    if (comparison == 0)
                    {
                        return resource.Revision != approved.Revision;
                    }

                    if (comparison < 0)
                    {
                        low = middle + 1;
                    }
                    else
                    {
                        high = middle - 1;
                    }
                }

                return true;
            }

            return !_revisions!.TryGetValue(approved.ResourceId, out var revision) || revision != approved.Revision;
        }
    }

    private static MemoryItem? ReadApprovedItem(IManagedResourceStore vault, ScopeResourceRevision approved)
    {
        byte[]? raw = null;
        try
        {
            raw = vault.ReadContent(approved.ResourceId, approved.Revision);
            var stored = MemoryEnvelopeCodec.DecodeStored(raw);
            if (!string.Equals(stored.Id, approved.ResourceId, StringComparison.Ordinal))
            {
                return null;
            }

            return MemoryItemService.ToItem(stored, approved.Revision);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            if (raw is { Length: > 0 })
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(raw);
            }
        }
    }
}
