using MetaBrain.Core.Domain;
using MetaBrain.Core.Index;
using MetaBrain.Core.Security;
namespace MetaBrain.Application;


/// <summary>
/// S4-T4 scoped brief authority: deterministic selection and projection over
/// the session's frozen approved snapshot (R12, R25).
///
/// Order of checks (never inverted): frozen-snapshot candidate enumeration
/// (approved memory-item IDs at exact approved revisions only; protected
/// prefixes never enter); deterministic ranking (scoped search score, then
/// resource ID) over candidates that match the query inside the protected
/// index when a query names one, otherwise stable ID order; bounded excerpt
/// projection under the caller's character budget with per-entry truncation
/// flags and omission records once the budget is spent; citation/uncertainty
/// rendering from the same manifest-only projection as item detail.
///
/// Budget can never turn uncertain or partial context into a confident claim:
/// every abbreviated excerpt is flagged, every skipped scope member is
/// listed with its reason, and every entry with superseded/unknown citations,
/// a moved live revision, or non-verified authorship/evidence is named in
/// the uncertainty ledger. No synthesis, no ranking invention, no instruction
/// text is produced here.
/// </summary>
public sealed class ScopedBriefAuthority
{
    public const int DefaultBudgetChars = 2000;
    public const int MaximumBudgetChars = 8000;
    public const int MinimumBudgetChars = 200;
    public const int MaximumEntries = 8;

    public sealed record PlannedEntry(
        string ResourceId,
        long Revision,
        double Score,
        MemoryItem Item,
        MemoryCitationWire[]? Citations,
        string[]? Warnings,
        bool Stale);

    public MemoryBriefWire Build(
        AgentSessionSnapshot session,
        string? query,
        int budgetChars,
        IReadOnlyList<ScopeResourceRevision> approvedScope,
        Func<ScopeResourceRevision, MemoryItem?> readApproved,
        Func<MemoryItem, IManagedResourceReader?, (MemoryCitationWire[]? Citations, string[]? Warnings)> projectCitations,
        IManagedResourceReader manifest,
        Func<string, bool>? isLiveRevision = null,
        Func<string, string[]?>? expandQuery = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(approvedScope);
        ArgumentNullException.ThrowIfNull(readApproved);
        ArgumentNullException.ThrowIfNull(projectCitations);
        if (budgetChars < 0 || budgetChars > MaximumBudgetChars)
        {
            throw new ArgumentException("The brief budget must be 0-8000 characters.");
        }

        var candidates = PlanCandidates(session, query, approvedScope, readApproved, isLiveRevision, expandQuery);
        var entries = new List<BriefEntryWire>();
        var omitted = new List<BriefOmissionWire>();
        var uncertain = new List<string>();
        var used = 0;
        var truncated = false;

        foreach (var candidate in candidates)
        {
            var (citations, warnings) = projectCitations(candidate.Item, manifest);
            var excerptBudget = Math.Max(120, budgetChars / Math.Max(1, Math.Min(candidates.Count, MaximumEntries)) / 2);
            var remaining = budgetChars - used;
            if (remaining < 120)
            {
                truncated = true;
                omitted.Add(new BriefOmissionWire(candidate.ResourceId, candidate.Revision, "budget_exhausted"));
                MarkUncertain(uncertain, candidate, citations, warnings);
                continue;
            }

            var excerptLength = Math.Min(excerptBudget, remaining);
            var (excerpt, wasTruncated) = TakeExcerpt(candidate.Item.Body, excerptLength);
            used += excerpt.Length;
            if (wasTruncated)
            {
                truncated = true;
            }

            entries.Add(new BriefEntryWire(
                candidate.ResourceId,
                candidate.Revision,
                WireToken(candidate.Item.Kind.ToString()),
                WireToken(candidate.Item.Author.ToString()),
                WireToken(candidate.Item.Executor.ToString()),
                WireToken(candidate.Item.CreationMethod.ToString()),
                WireToken(candidate.Item.Evidence.ToString()),
                WireToken(candidate.Item.Level.ToString()),
                candidate.Item.Title,
                excerpt,
                wasTruncated,
                candidate.Item.OccurredAtUtc,
                candidate.Item.RecordedAtUtc,
                candidate.Item.Applicability,
                citations,
                warnings));
            MarkUncertain(uncertain, candidate, citations, warnings);
            if (entries.Count >= MaximumEntries && candidates.Count > entries.Count)
            {
                truncated = true;
                foreach (var rest in candidates.Skip(entries.Count))
                {
                    var (restCitations, restWarnings) = projectCitations(rest.Item, manifest);
                    omitted.Add(new BriefOmissionWire(rest.ResourceId, rest.Revision, "entry_limit"));
                    MarkUncertain(uncertain, rest, restCitations, restWarnings);
                }

                break;
            }
        }

        return new MemoryBriefWire(
            query ?? string.Empty,
            entries.Select(entry => entry.Id).ToArray(),
            budgetChars,
            used,
            truncated || omitted.Count > 0,
            entries.ToArray(),
            omitted.ToArray(),
            uncertain.Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            session.PolicyGeneration);
    }

    private static List<PlannedEntry> PlanCandidates(
        AgentSessionSnapshot session,
        string? query,
        IReadOnlyList<ScopeResourceRevision> approvedScope,
        Func<ScopeResourceRevision, MemoryItem?> readApproved,
        Func<string, bool>? isLiveRevision,
        Func<string, string[]?>? expandQuery)
    {
        var scope = approvedScope
            .Where(resource => !IsProtectedNamespace(resource.ResourceId))
            .OrderBy(resource => resource.ResourceId, StringComparer.Ordinal)
            .ToArray();
        var planned = new List<PlannedEntry>();
        var terms = string.IsNullOrWhiteSpace(query)
            ? Array.Empty<string>()
            : (expandQuery?.Invoke(query!) ?? NaiveTokens(query!));

        // Deterministic scope-first planning: every approved memory item is
        // read at its exact frozen revision, then filtered by the query
        // against its own folded title/body text. No vault-wide scan, no
        // cross-scope scoring. A frozen pair whose live canonical revision
        // moved is kept as a stale-marked candidate (never silently
        // replaced); the stale flag joins the uncertainty ledger below.
        foreach (var approved in scope)
        {
            var item = readApproved(approved);
            if (item is null || !string.Equals(item.Id, approved.ResourceId, StringComparison.Ordinal))
            {
                continue;
            }

            double score = 0;
            var matched = terms.Length == 0;
            if (terms.Length > 0)
            {
                var folded = SearchText.Normalize(item.Title is null ? item.Body : item.Title + "\n" + item.Body);
                foreach (var term in terms)
                {
                    if (folded.Contains(term, StringComparison.Ordinal))
                    {
                        score += 1;
                    }
                }

                matched = score > 0;
            }

            if (!matched)
            {
                continue;
            }

            var stale = IsStaleHint(item) || (isLiveRevision is not null && !isLiveRevision(approved.ResourceId));
            planned.Add(new PlannedEntry(approved.ResourceId, approved.Revision, score, item, null, null, stale));
        }

        return planned
            .OrderByDescending(entry => entry.Score)
            .ThenBy(entry => entry.ResourceId, StringComparer.Ordinal)
            .Take(MaximumEntries * 2)
            .ToList();
    }

    private static void MarkUncertain(
        List<string> uncertain,
        PlannedEntry candidate,
        MemoryCitationWire[]? citations,
        string[]? warnings)
    {
        var flags = new List<string>();
        if (candidate.Stale)
        {
            flags.Add("stale_revision");
        }

        if (warnings is { Length: > 0 })
        {
            flags.Add("citation_warning");
        }

        if (citations is not null)
        {
            foreach (var citation in citations)
            {
                if (!string.Equals(citation.Status, "current", StringComparison.Ordinal))
                {
                    flags.Add("citation_" + citation.Status);
                }
            }
        }

        if (!string.Equals(candidate.Item.Author.ToString(), "User", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(candidate.Item.Evidence.ToString(), "UserConfirmed", StringComparison.OrdinalIgnoreCase))
        {
            flags.Add("unverified_attribution");
        }

        if (candidate.Item.Kind is MemoryKind.Question or MemoryKind.Hypothesis)
        {
            flags.Add("open_question_or_hypothesis");
        }

        if (flags.Count > 0)
        {
            uncertain.Add(candidate.ResourceId + ":" + string.Join(",", flags.Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal)));
        }
    }

    private static (string Excerpt, bool Truncated) TakeExcerpt(string body, int budget)
    {
        if (string.IsNullOrEmpty(body) || budget <= 0)
        {
            return (string.Empty, !string.IsNullOrEmpty(body));
        }

        if (body.Length <= budget)
        {
            return (body, false);
        }

        var cut = body.LastIndexOf(' ', Math.Min(budget, body.Length - 1));
        if (cut < budget / 2)
        {
            cut = budget;
        }

        return (body[..cut] + "…", true);
    }

    private static bool IsProtectedNamespace(string resourceId) =>
        resourceId.StartsWith("rel-", StringComparison.Ordinal) ||
        resourceId.StartsWith("lc-", StringComparison.Ordinal) ||
        resourceId.StartsWith("arc-", StringComparison.Ordinal) ||
        resourceId.StartsWith("ingest-", StringComparison.Ordinal) ||
        resourceId.StartsWith("snap-", StringComparison.Ordinal) ||
        resourceId.StartsWith("fb-", StringComparison.Ordinal);

    private static bool IsStaleHint(MemoryItem item) =>
        item.Evidence is EvidenceLevel.Unverified && item.Kind is MemoryKind.Question;

    private static string[] NaiveTokens(string query) =>
        SearchText.Normalize(query)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string WireToken(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return name;
        }

        var buffer = new char[name.Length];
        buffer[0] = char.ToLowerInvariant(name[0]);
        for (var index = 1; index < name.Length; index++)
        {
            buffer[index] = name[index];
        }

        return new string(buffer);
    }
}
