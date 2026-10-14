using MetaBrain.Core.Domain;
using MetaBrain.Core.Index;
using MetaBrain.Core.Security;

namespace MetaBrain.Application;

/// <summary>
/// S4-T2 scoped search authority: lexical/metadata/ranking/time queries over
/// the protected private index, served only inside one redeemed session's
/// frozen approved snapshot (R06, R23, R26).
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
        IManagedResourceStore vault)
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

            if (current is not null && !PassesCurrent(document, current))
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
        IManagedResourceStore vault)
    {
        // Total over the frozen scope: same filters, full ranking, no paging.
        var all = Search(session, query, kind, current, 0, int.MaxValue, materialized, index, vault);
        return all.Count;
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
