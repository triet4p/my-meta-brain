using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MetaBrain.Core.Index;
using MetaBrain.Core.Security;

namespace MetaBrain.Application;

/// <summary>One bounded owner hybrid search request.</summary>
public sealed record WorkspaceHybridSearchRequest(
    string Query,
    string? FolderId,
    string? Field,
    string Current,
    DateOnly? ContentDateFrom,
    DateOnly? ContentDateTo,
    int Offset,
    int Limit,
    string? ModelDirectory);

/// <summary>One returned hybrid hit with its exact source anchor and evidence.</summary>
public sealed record WorkspaceHybridSearchHit(
    int Rank,
    string FileId,
    string Path,
    long ContentRevision,
    string FileSha256,
    string Field,
    string[] Channels,
    string EvidenceClass,
    bool Stale,
    int? Line,
    long? ByteOffset,
    long? ByteLength,
    string? AnchorSha256,
    int? TextOffset,
    int? TextLength,
    string Evidence,
    string? AnchorKind,
    string? Attribution,
    int? ChunkOrdinal,
    double? Similarity);

/// <summary>One scoped, ranked and bounded hybrid result page.</summary>
public sealed record WorkspaceHybridSearchOutcome(
    long WorkspaceRevision,
    string? FolderId,
    string? FolderPath,
    string Query,
    string? Field,
    string Current,
    string? ContentDateFrom,
    string? ContentDateTo,
    int Total,
    int LiteralTotal,
    int SemanticCandidateTotal,
    int Offset,
    int Limit,
    bool HasMore,
    string SemanticStatus,
    string? ModelRevision,
    int? VectorDimensions,
    WorkspaceHybridSearchHit[] Hits);

/// <summary>
/// S15-T3 owner hybrid retrieval. One server-side pass combines the retained
/// exact/lexical/heading path with the authenticated local passage generation:
/// the owner's folder scope and currentness are resolved first, every eligible
/// candidate of the retained revisions is read, literal and semantic evidence
/// are merged and de-duplicated by one shared deterministic order, and only
/// then are the totals and one bounded page disclosed. No caller-side ranking,
/// no candidate truncation before the totals, and no similarity threshold.
/// </summary>
public sealed class WorkspaceHybridSearchService
{
    public const int MaximumQueryChars = 256;
    public const int MaximumLimit = 100;
    public const int DefaultLimit = 20;
    public const int EvidenceSnippetChars = 240;

    // A bounded content-date scan: the filter looks for ISO-8601 dates in the
    // anchored evidence, never at an unbounded line or record.
    private const int MaximumDateScanChars = 4096;
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly IWorkspacePassageIndexEngine _index;

    public WorkspaceHybridSearchService(IWorkspacePassageIndexEngine index)
    {
        _index = index ?? throw new ArgumentNullException(nameof(index));
    }

    public WorkspaceHybridSearchOutcome Search(
        IWorkspacePersistence workspace,
        WorkspaceHybridSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(request);
        Validate(request);
        cancellationToken.ThrowIfCancellationRequested();

        var snapshot = workspace.ReadWorkspaceTree();
        if (snapshot is null)
        {
            if (request.FolderId is not null)
            {
                throw new ServiceRefusalException(
                    "workspace_folder_not_found", "The selected folder is not in the live workspace tree.");
            }

            return new WorkspaceHybridSearchOutcome(
                0, null, null, request.Query, request.Field, request.Current,
                request.ContentDateFrom?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                request.ContentDateTo?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                0, 0, 0, request.Offset, request.Limit, false,
                WorkspaceHybridSemanticStatus.NotApplicableEmptyScope, null, null, []);
        }

        Dictionary<string, string> paths;
        try
        {
            paths = WorkspaceSearch.BuildPaths(snapshot.RootId, snapshot.Nodes);
        }
        catch (InvalidDataException)
        {
            throw new WorkspacePassageIndexException("workspace_tree_invalid");
        }

        var folderPath = ResolveFolder(snapshot, paths, request.FolderId);
        var currentMode = request.Current ?? WorkspaceHybridSearchCurrent.CurrentOnly;
        var scopeRevisions = BuildScope(snapshot, paths, folderPath, currentMode);
        var currentScope = scopeRevisions
            .Where(item => item.IsCurrent)
            .Select(item => new WorkspacePassageScope(item.Node.Id, item.Revision.Revision))
            .ToArray();

        string semanticStatus;
        string? modelRevision = null;
        int? vectorDimensions = null;
        var semanticByRevision = new Dictionary<(string FileId, long Revision), List<WorkspacePassageSemanticMatch>>();
        if (WorkspaceHybridSearchPolicy.IsExactLiteralQuery(request.Query))
        {
            // An identifier-shaped query has a determinate literal answer. The
            // semantic channel abstains instead of offering near-miss
            // neighbours; no similarity threshold is involved.
            semanticStatus = WorkspaceHybridSemanticStatus.AbstainedExactQuery;
        }
        else if (request.Field is not null &&
                 !string.Equals(request.Field, WorkspaceHybridSearchFields.Content, StringComparison.Ordinal))
        {
            semanticStatus = WorkspaceHybridSemanticStatus.NotApplicableField;
        }
        else if (string.Equals(currentMode, WorkspaceHybridSearchCurrent.HistoryOnly, StringComparison.Ordinal))
        {
            semanticStatus = WorkspaceHybridSemanticStatus.NotApplicableHistory;
        }
        else if (currentScope.Length == 0)
        {
            semanticStatus = WorkspaceHybridSemanticStatus.NotApplicableEmptyScope;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(request.ModelDirectory))
            {
                throw new WorkspacePassageIndexException("workspace_index_model_missing");
            }

            var semantic = _index.SearchSemantic(
                workspace, request.ModelDirectory, request.Query, currentScope, cancellationToken);
            modelRevision = semantic.ModelRevision;
            vectorDimensions = semantic.VectorDimensions;
            foreach (var match in semantic.Matches)
            {
                if (!semanticByRevision.TryGetValue((match.FileId, match.ContentRevision), out var matches))
                {
                    matches = [];
                    semanticByRevision.Add((match.FileId, match.ContentRevision), matches);
                }

                matches.Add(match);
            }

            semanticStatus = string.Equals(currentMode, WorkspaceHybridSearchCurrent.All, StringComparison.Ordinal)
                ? WorkspaceHybridSemanticStatus.AppliedCurrentOnly
                : WorkspaceHybridSemanticStatus.Applied;
        }

        var candidates = new List<WorkspaceHybridCandidate>();
        foreach (var item in scopeRevisions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            candidates.AddRange(CollectRevisionCandidates(workspace, item, request, semanticByRevision, cancellationToken));
        }

        var after = workspace.ReadWorkspaceTree();
        if (after is null ||
            after.Revision != snapshot.Revision ||
            !string.Equals(after.WorkspaceId, snapshot.WorkspaceId, StringComparison.Ordinal))
        {
            throw new WorkspacePassageIndexException("workspace_search_snapshot_changed");
        }

        var ranked = WorkspaceHybridSearchPolicy.RankAndDeduplicate(candidates);
        var total = ranked.Length;
        var literalTotal = 0;
        foreach (var candidate in ranked)
        {
            if (candidate.IsLiteral)
            {
                literalTotal++;
            }
        }

        var semanticCandidateTotal = total - literalTotal;
        var offset = request.Offset;
        var limit = request.Limit;
        var pageStart = Math.Min(offset, total);
        var pageLength = Math.Min(limit, total - pageStart);
        var page = new WorkspaceHybridSearchHit[pageLength];
        if (pageLength > 0)
        {
            var pageCandidates = new WorkspaceHybridCandidate[pageLength];
            Array.Copy(ranked, pageStart, pageCandidates, 0, pageLength);
            var nodes = snapshot.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
            var evidence = BuildPageEvidence(workspace, nodes, pageCandidates, cancellationToken);
            for (var index = 0; index < pageLength; index++)
            {
                var candidate = pageCandidates[index];
                page[index] = new WorkspaceHybridSearchHit(
                    pageStart + index + 1,
                    candidate.FileId,
                    candidate.Path,
                    candidate.ContentRevision,
                    candidate.FileSha256,
                    candidate.Field,
                    candidate.Channels,
                    WorkspaceHybridSearchPolicy.EvidenceClassOf(candidate),
                    candidate.Stale,
                    candidate.Line,
                    candidate.ByteOffset,
                    candidate.ByteLength,
                    candidate.AnchorSha256,
                    candidate.TextOffset,
                    candidate.TextLength,
                    evidence[index],
                    candidate.AnchorKind,
                    candidate.Attribution,
                    candidate.ChunkOrdinal,
                    candidate.Similarity);
            }
        }

        return new WorkspaceHybridSearchOutcome(
            snapshot.Revision,
            request.FolderId,
            folderPath,
            request.Query,
            request.Field,
            currentMode,
            request.ContentDateFrom?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            request.ContentDateTo?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            total,
            literalTotal,
            semanticCandidateTotal,
            offset,
            limit,
            pageStart + pageLength < total,
            semanticStatus,
            modelRevision,
            vectorDimensions,
            page);
    }

    private static void Validate(WorkspaceHybridSearchRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Query) ||
            request.Query.Length > MaximumQueryChars ||
            !WorkspaceHybridSearchFields.IsKnown(request.Field) ||
            !WorkspaceHybridSearchCurrent.IsKnown(request.Current) ||
            request.Offset < 0 ||
            request.Limit is < 1 or > MaximumLimit ||
            (request.ContentDateFrom is { } from && request.ContentDateTo is { } to && from > to))
        {
            throw new ServiceRefusalException(
                "workspace_search_request_invalid",
                "The workspace search request is outside its supported bounds.");
        }

        try
        {
            _ = StrictUtf8.GetByteCount(request.Query);
        }
        catch (EncoderFallbackException)
        {
            throw new ServiceRefusalException(
                "workspace_search_request_invalid", "The workspace search query must contain valid Unicode.");
        }
    }

    private static string? ResolveFolder(
        WorkspaceSnapshot snapshot,
        IReadOnlyDictionary<string, string> paths,
        string? folderId)
    {
        if (folderId is null)
        {
            return null;
        }

        var folder = snapshot.Nodes.FirstOrDefault(node => string.Equals(node.Id, folderId, StringComparison.Ordinal));
        if (folder is null)
        {
            throw new ServiceRefusalException(
                "workspace_folder_not_found", "The selected folder is not in the live workspace tree.");
        }

        if (folder.Kind != WorkspaceNodeKind.Folder)
        {
            throw new ServiceRefusalException(
                "workspace_filter_not_folder", "The workspace search scope must be a live folder.");
        }

        return paths[folder.Id];
    }

    private static List<ScopeRevision> BuildScope(
        WorkspaceSnapshot snapshot,
        IReadOnlyDictionary<string, string> paths,
        string? folderPath,
        string currentMode)
    {
        var scope = new List<ScopeRevision>();
        foreach (var node in snapshot.Nodes)
        {
            if (node.Kind != WorkspaceNodeKind.File || !paths.TryGetValue(node.Id, out var path))
            {
                continue;
            }

            if (folderPath is not null && folderPath.Length > 0 &&
                !(path.StartsWith(folderPath, StringComparison.Ordinal) &&
                  path.Length > folderPath.Length &&
                  path[folderPath.Length] == '/'))
            {
                continue;
            }

            var currentFound = false;
            foreach (var revision in node.Revisions)
            {
                var isCurrent = revision.Revision == node.ContentRevision;
                currentFound |= isCurrent;
                if (isCurrent && string.Equals(currentMode, WorkspaceHybridSearchCurrent.HistoryOnly, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!isCurrent && string.Equals(currentMode, WorkspaceHybridSearchCurrent.CurrentOnly, StringComparison.Ordinal))
                {
                    continue;
                }

                scope.Add(new ScopeRevision(node, path, revision, isCurrent));
            }

            if (!currentFound)
            {
                throw new WorkspacePassageIndexException("workspace_index_source_revision_invalid");
            }
        }

        return scope;
    }

    private static List<WorkspaceHybridCandidate> CollectRevisionCandidates(
        IWorkspacePersistence workspace,
        ScopeRevision item,
        WorkspaceHybridSearchRequest request,
        IReadOnlyDictionary<(string FileId, long Revision), List<WorkspacePassageSemanticMatch>> semanticByRevision,
        CancellationToken cancellationToken)
    {
        var candidates = new List<WorkspaceHybridCandidate>();
        WorkspaceFileContent file;
        try
        {
            file = workspace.ReadWorkspaceFile(item.Node.Id, item.Revision.Revision);
        }
        catch (WorkspaceRefusalException)
        {
            throw new WorkspacePassageIndexException("workspace_index_source_unavailable");
        }

        try
        {
            if (file.Revision != item.Revision.Revision ||
                file.Length != file.Content.LongLength ||
                file.Length != item.Revision.Length ||
                !string.Equals(file.Sha256, item.Revision.Sha256, StringComparison.Ordinal) ||
                !string.Equals(Digest(file.Content), item.Revision.Sha256, StringComparison.Ordinal))
            {
                throw new WorkspacePassageIndexException("workspace_index_source_changed");
            }

            // A retained revision must report its own revision in every hit,
            // so the search sees that revision as the current one for this read.
            var searchNode = item.IsCurrent ? item.Node : item.Node with { ContentRevision = item.Revision.Revision };
            var hasSemantic = semanticByRevision.ContainsKey((item.Node.Id, item.Revision.Revision));
            var hits = WorkspaceSearch.SearchFile(
                request.Query, searchNode, item.Path, item.Revision.Sha256, file.Content);
            string? text = null;
            if ((hits.Count > 0 || hasSemantic) && !WorkspaceSearch.TryDecodeSearchText(file.Content, out text))
            {
                text = null;
            }

            var dateFilter = request.ContentDateFrom is not null || request.ContentDateTo is not null;
            foreach (var hit in hits)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (request.Field is not null &&
                    !string.Equals(hit.Field, request.Field, StringComparison.Ordinal))
                {
                    continue;
                }

                var metadata = hit.ByteOffset is null;
                if (metadata && !item.IsCurrent)
                {
                    // Name and path are the live node's identity, not a
                    // retained revision's evidence.
                    continue;
                }

                if (metadata && dateFilter)
                {
                    // A name or path is not the content date the filter names.
                    continue;
                }

                if (dateFilter)
                {
                    if (text is null)
                    {
                        continue;
                    }

                    var line = LineText(text, hit.TextOffset, hit.TextLength);
                    if (!ContainsContentDate(line, request.ContentDateFrom, request.ContentDateTo))
                    {
                        continue;
                    }
                }

                candidates.Add(new WorkspaceHybridCandidate(
                    hit.FileId,
                    hit.Path,
                    hit.ContentRevision,
                    hit.FileSha256,
                    hit.Field,
                    [ChannelOf(hit.Field)],
                    !item.IsCurrent,
                    hit.Line,
                    hit.ByteOffset,
                    hit.ByteLength,
                    hit.AnchorSha256,
                    hit.TextOffset,
                    hit.TextLength,
                    null,
                    null,
                    null,
                    hit.RankingScore,
                    null,
                    hit.Evidence));
            }

            if (text is not null && hasSemantic)
            {
                foreach (var match in semanticByRevision[(item.Node.Id, item.Revision.Revision)])
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (match.ByteOffset < 0 || match.ByteLength < 1 ||
                        match.ByteOffset > file.Content.LongLength ||
                        match.ByteLength > file.Content.LongLength - match.ByteOffset)
                    {
                        throw new WorkspacePassageIndexException("workspace_index_anchor_invalid");
                    }

                    var span = file.Content.AsSpan(checked((int)match.ByteOffset), checked((int)match.ByteLength));
                    if (!string.Equals(Digest(span), match.Sha256, StringComparison.Ordinal))
                    {
                        throw new WorkspacePassageIndexException("workspace_index_anchor_invalid");
                    }

                    if (!WorkspaceSearch.TryMapByteRange(
                            file.Content, text, match.ByteOffset, match.ByteLength, out var range) ||
                        range is null)
                    {
                        throw new WorkspacePassageIndexException("workspace_index_anchor_invalid");
                    }

                    if (dateFilter &&
                        !ContainsContentDate(
                            text.Substring(range.Offset, range.Length),
                            request.ContentDateFrom,
                            request.ContentDateTo))
                    {
                        continue;
                    }

                    candidates.Add(new WorkspaceHybridCandidate(
                        match.FileId,
                        item.Path,
                        match.ContentRevision,
                        item.Revision.Sha256,
                        WorkspaceHybridSearchFields.Content,
                        [WorkspaceHybridChannels.Semantic],
                        false,
                        null,
                        match.ByteOffset,
                        match.ByteLength,
                        match.Sha256,
                        range.Offset,
                        range.Length,
                        match.Kind,
                        match.Attribution,
                        match.ChunkOrdinal,
                        0,
                        match.Similarity));
                }
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(file.Content);
        }

        return candidates;
    }

    private static string[] BuildPageEvidence(
        IWorkspacePersistence workspace,
        IReadOnlyDictionary<string, WorkspaceNodeInfo> nodes,
        IReadOnlyList<WorkspaceHybridCandidate> page,
        CancellationToken cancellationToken)
    {
        var evidence = new string[page.Count];
        var cache = new Dictionary<(string FileId, long Revision), string?>();
        for (var index = 0; index < page.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = page[index];
            if (candidate.ByteOffset is null)
            {
                evidence[index] = candidate.Evidence ?? string.Empty;
                continue;
            }

            if (!cache.TryGetValue((candidate.FileId, candidate.ContentRevision), out var text) || text is null)
            {
                if (!nodes.ContainsKey(candidate.FileId))
                {
                    throw new WorkspacePassageIndexException("workspace_search_snapshot_changed");
                }

                WorkspaceFileContent file;
                try
                {
                    file = workspace.ReadWorkspaceFile(candidate.FileId, candidate.ContentRevision);
                }
                catch (WorkspaceRefusalException)
                {
                    throw new WorkspacePassageIndexException("workspace_index_source_unavailable");
                }

                try
                {
                    if (file.Revision != candidate.ContentRevision ||
                        !string.Equals(Digest(file.Content), candidate.FileSha256, StringComparison.Ordinal))
                    {
                        throw new WorkspacePassageIndexException("workspace_index_source_changed");
                    }

                    text = WorkspaceSearch.TryDecodeSearchText(file.Content, out var decoded) ? decoded : null;
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(file.Content);
                }

                cache[(candidate.FileId, candidate.ContentRevision)] = text;
            }

            if (text is null)
            {
                evidence[index] = string.Empty;
                continue;
            }

            // A merged hit keeps the literal anchor, so its evidence is the
            // surrounding line; only a semantic-only hit is anchored to the
            // whole passage.
            evidence[index] = candidate.IsLiteral
                ? TextSnippet(text, candidate.TextOffset, candidate.TextLength)
                : PassageSnippet(text, candidate.TextOffset, candidate.TextLength);
        }

        return evidence;
    }

    private static string TextSnippet(string text, int? textOffset, int? textLength)
    {
        if (textOffset is not { } offset || offset < 0 || offset > text.Length)
        {
            return string.Empty;
        }

        if (text.Length == 0)
        {
            return string.Empty;
        }

        var matchLength = Math.Min(textLength ?? 1, EvidenceSnippetChars);
        var start = Math.Min(
            Math.Max(0, offset - ((EvidenceSnippetChars - matchLength) / 4)),
            text.Length - 1);
        while (start > 0 && char.IsLowSurrogate(text[start]))
        {
            start--;
        }

        var end = Math.Min(text.Length, start + EvidenceSnippetChars);
        while (end < text.Length && end > start && char.IsLowSurrogate(text[end]))
        {
            end--;
        }

        return text.Substring(start, end - start);
    }

    private static string PassageSnippet(string text, int? textOffset, int? textLength)
    {
        if (textOffset is not { } offset || offset < 0 || offset > text.Length)
        {
            return string.Empty;
        }

        var limit = Math.Min(textLength ?? EvidenceSnippetChars, EvidenceSnippetChars);
        var end = Math.Min(text.Length, offset + limit);
        while (end < text.Length && end > offset && char.IsLowSurrogate(text[end]))
        {
            end--;
        }

        return text.Substring(offset, end - offset);
    }

    private static string LineText(string text, int? textOffset, int? textLength)
    {
        if (textOffset is not { } offset || offset < 0 || offset > text.Length)
        {
            return string.Empty;
        }

        var start = WorkspaceSearch.LineStart(text, offset);
        var end = Math.Min(text.Length, offset + Math.Max(textLength ?? 1, 1));
        while (end < text.Length && text[end] is not ('\r' or '\n'))
        {
            end++;
        }

        return text.Substring(start, end - start);
    }

    /// <summary>
    /// True when the anchored evidence contains at least one ISO-8601
    /// calendar date inside the inclusive window. This is a content-date
    /// filter over text the owner wrote; the workspace records no per-node
    /// wall-clock event or recorded time, and no such value is invented here.
    /// </summary>
    private static bool ContainsContentDate(string text, DateOnly? from, DateOnly? to)
    {
        if (from is null && to is null)
        {
            return true;
        }

        var span = text.AsSpan();
        if (span.Length > MaximumDateScanChars)
        {
            span = span[..MaximumDateScanChars];
        }

        for (var index = 0; index + 10 <= span.Length; index++)
        {
            if (index > 0 && char.IsAsciiDigit(span[index - 1]))
            {
                continue;
            }

            var candidate = span.Slice(index, 10);
            if (!IsIsoDateShape(candidate))
            {
                continue;
            }

            if (index + 10 < span.Length && char.IsAsciiDigit(span[index + 10]))
            {
                continue;
            }

            if (!DateOnly.TryParseExact(
                    candidate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                continue;
            }

            if ((from is null || date >= from.Value) && (to is null || date <= to.Value))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsIsoDateShape(ReadOnlySpan<char> value) =>
        char.IsAsciiDigit(value[0]) &&
        char.IsAsciiDigit(value[1]) &&
        char.IsAsciiDigit(value[2]) &&
        char.IsAsciiDigit(value[3]) &&
        value[4] == '-' &&
        char.IsAsciiDigit(value[5]) &&
        char.IsAsciiDigit(value[6]) &&
        value[7] == '-' &&
        char.IsAsciiDigit(value[8]) &&
        char.IsAsciiDigit(value[9]);

    private static string ChannelOf(string field) => field switch
    {
        WorkspaceHybridSearchFields.Name or WorkspaceHybridSearchFields.Path => WorkspaceHybridChannels.Exact,
        WorkspaceHybridSearchFields.Heading => WorkspaceHybridChannels.Structural,
        _ => WorkspaceHybridChannels.Lexical,
    };

    private static string Digest(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed record ScopeRevision(
        WorkspaceNodeInfo Node,
        string Path,
        WorkspaceContentRevisionInfo Revision,
        bool IsCurrent);
}
