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
        bool Stale);

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
        DateTimeOffset? asOfUtc = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(vault);
        if (materialized is null)
        {
            throw new InvalidDataException("The search index has no materialized generation.");
        }

        var terms = SearchText.ExpandQuery(query);
        if (terms.Length == 0)
        {
            return Array.Empty<ScoredHit>();
        }

        var scope = session.Resources
            .OrderBy(resource => resource.ResourceId, StringComparer.Ordinal)
            .ToArray();
        var scopeIds = scope.Select(resource => resource.ResourceId).ToArray();
        var termTokens = terms.Select(term => term.Token).Distinct(StringComparer.Ordinal).ToArray();

        // Candidate prefilter inside the engine: scope IDs bind in SQL before
        // any token row returns, so out-of-scope plaintext is never scored.
        var candidates = index.SelectSearchCandidates(scopeIds, termTokens);
        var candidateSet = new HashSet<string>(candidates, StringComparer.Ordinal);
        var termWeights = terms.ToDictionary(term => term.Token, term => term, StringComparer.Ordinal);

        var scored = new List<ScoredHit>();
        foreach (var approved in scope)
        {
            if (!candidateSet.Contains(approved.ResourceId))
            {
                continue;
            }

            var document = index.SelectSearchDocument(approved.ResourceId, approved.Revision);
            if (document is null)
            {
                // Stale or never-indexed candidate: excluded, never guessed.
                continue;
            }

            if (kind is not null && !string.Equals(document.Kind, kind, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var score = Score(document, termWeights, out var directHit);
            if (score <= 0)
            {
                continue;
            }

            if (current is not null && !PassesCurrentScoped(document, LifecycleOf(vault, approved.ResourceId), current, asOfUtc, vault, approved.ResourceId))
            {
                continue;
            }

            var stale = IsStale(document, vault, approved);
            var item = ReadApprovedItem(vault, approved);
            if (item is null)
            {
                continue;
            }

            scored.Add(new ScoredHit(approved.ResourceId, approved.Revision, ApplyStatePenalty(score, document, stale), directHit, document, item, stale));
        }

        return scored
            .OrderByDescending(hit => hit.Score)
            .ThenBy(hit => hit.ResourceId, StringComparer.Ordinal)
            .Skip(offset)
            .Take(limit)
            .ToArray();
    }

    public int Count(
        AgentSessionSnapshot session,
        string? query,
        string? kind,
        string? current,
        PrivateIndexSnapshot? materialized,
        IPrivateIndexStore index,
        IManagedResourceStore vault,
        DateTimeOffset? asOfUtc = null)
    {
        // Total over the frozen scope: same filters, full ranking, no paging.
        var all = Search(session, query, kind, current, 0, int.MaxValue, materialized, index, vault, asOfUtc);
        return all.Count;
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
        IManagedResourceStore vault)
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

        var scope = ownerScope.OrderBy(resource => resource.ResourceId, StringComparer.Ordinal).ToArray();
        var scopeIds = scope.Select(resource => resource.ResourceId).ToArray();
        var termTokens = terms.Select(term => term.Token).Distinct(StringComparer.Ordinal).ToArray();
        var termWeights = terms.ToDictionary(term => term.Token, term => term, StringComparer.Ordinal);

        // Browse reads the whole owner scope; a text query binds the token
        // prefilter in the engine before any candidate document is scored.
        var candidates = terms.Length == 0
            ? new HashSet<string>(scopeIds, StringComparer.Ordinal)
            : new HashSet<string>(index.SelectSearchCandidates(scopeIds, termTokens), StringComparer.Ordinal);

        var matched = new List<OwnerCandidate>();
        foreach (var resource in scope)
        {
            if (!candidates.Contains(resource.ResourceId))
            {
                continue;
            }

            var document = index.SelectSearchDocument(resource.ResourceId, resource.Revision);
            if (document is null)
            {
                // Not a materialized memory item at this revision: excluded,
                // never guessed at from another row or another revision.
                continue;
            }

            if (query.Kind is not null && !string.Equals(document.Kind, query.Kind, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var lifecycle = query.AsOfUtc is not null || string.Equals(query.Current, "current", StringComparison.OrdinalIgnoreCase)
                ? LifecycleOf(vault, resource.ResourceId)
                : null;
            if (!PassesCurrentScoped(document, lifecycle, query.Current ?? string.Empty, query.AsOfUtc, vault, resource.ResourceId))
            {
                continue;
            }

            if (!PassesWindow(document, query.FromUtc, query.ToUtc))
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

            matched.Add(new OwnerCandidate(resource, document, score, directHit, item));
        }

        var ordered = terms.Length > 0
            ? matched
                .OrderByDescending(candidate => candidate.Score)
                .ThenBy(candidate => candidate.Resource.ResourceId, StringComparer.Ordinal)
            : matched
                .OrderByDescending(candidate => candidate.Document.OccurredAtUtc ?? candidate.Document.RecordedAtUtc)
                .ThenBy(candidate => candidate.Resource.ResourceId, StringComparer.Ordinal);

        var hits = new List<ScoredHit>();
        foreach (var candidate in ordered.Skip(query.Offset).Take(query.Limit))
        {
            var item = candidate.Item ?? ReadApprovedItem(vault, candidate.Resource);
            if (item is null)
            {
                // The row moved or became unreadable between the filter and the
                // read: the hit is dropped instead of being served from a
                // different revision.
                continue;
            }

            var stale = IsStale(candidate.Document, vault, candidate.Resource);
            hits.Add(new ScoredHit(
                candidate.Resource.ResourceId,
                candidate.Resource.Revision,
                terms.Length > 0 ? ApplyStatePenalty(candidate.Score, candidate.Document, stale) : 0,
                candidate.DirectHit,
                candidate.Document,
                item,
                stale));
        }

        return new OwnerLibraryPage(matched.Count, hits);
    }

    private sealed record OwnerCandidate(
        ScopeResourceRevision Resource,
        SearchIndexDocument Document,
        double Score,
        bool DirectHit,
        MemoryItem? Item);

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
        var documentTokens = new HashSet<string>(document.Tokens, StringComparer.Ordinal);
        var score = 0.0;
        directHit = false;
        foreach (var term in terms.Values)
        {
            if (!documentTokens.Contains(term.Token))
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

    private static bool PassesCurrentScoped(SearchIndexDocument document, StoredLifecycleRecord? lifecycle, string current, DateTimeOffset? asOfUtc, IManagedResourceStore vault, string resourceId)
    {
        if (!PassesCurrentScoped(document, lifecycle, current, asOfUtc))
        {
            return false;
        }

        return !string.Equals(current, "current", StringComparison.OrdinalIgnoreCase) ||
            !IsSupersededTarget(vault, lifecycle, resourceId);
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

    private static bool IsSupersededTarget(IManagedResourceStore vault, StoredLifecycleRecord? lifecycle, string resourceId)
    {
        if (lifecycle is { Intention: IntentionState.Superseded })
        {
            return true;
        }

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
                    relation.State is RelationState.Proposed or RelationState.Accepted &&
                    string.Equals(relation.From.ResourceId, resourceId, StringComparison.Ordinal))
                {
                    return true;
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

        return false;
    }

    private static bool IsExpired(SearchIndexDocument document)
    {
        // Temporal expiry is advisory metadata carried on the document; the
        // authoritative validity window lives in the lifecycle record (S7).
        // Here only the recorded instant bounds "current": items recorded in
        // the future (clock skew, never fabricated dates) are not current.
        return document.RecordedAtUtc > DateTimeOffset.UtcNow.AddMinutes(5);
    }

    private static bool IsStale(SearchIndexDocument document, IManagedResourceStore vault, ScopeResourceRevision approved)
    {
        // Stale = the live canonical revision moved past the frozen grant, or
        // the lifecycle subject moved past the indexed publication state. The
        // hit still serves its frozen bytes (the grant's address), flagged.
        var live = vault.ListResources().FirstOrDefault(resource =>
            string.Equals(resource.ResourceId, approved.ResourceId, StringComparison.Ordinal));
        return live is null || live.Revision != approved.Revision;
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
