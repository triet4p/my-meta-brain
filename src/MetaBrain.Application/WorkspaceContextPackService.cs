using System.Security.Cryptography;
using System.Text.Json;
using MetaBrain.Core.Domain;
using MetaBrain.Core.Security;

namespace MetaBrain.Application;

public sealed record WorkspaceContextPackEntry(
    string SeedFileId,
    string FileId,
    long ContentRevision,
    long Offset,
    long Length,
    string FullSha256,
    string ContentSha256,
    string Citation,
    bool IsSeed,
    string Representation,
    byte[] VerbatimBytes,
    WorkspaceContextPackRelationPreview[] RelationPath);

public sealed record WorkspaceContextPackGap(string Citation, string Reason);

public sealed record WorkspaceContextPackReadResult(
    string Status,
    int BudgetBytes,
    long UsedBytes,
    int CandidateCount,
    int IncludedCount,
    int OmittedCount,
    WorkspaceContextPackEntry[] Entries,
    WorkspaceContextPackGap[] Gaps);

/// <summary>Resolves owner-selected pins and reads only their verbatim, frozen context.</summary>
public static class WorkspaceContextPackService
{
    public const int MaximumBudgetBytes = 64 * 1024;
    private static readonly JsonSerializerOptions BudgetJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private sealed record PathState(
        string SeedFileId,
        string FileId,
        long Revision,
        WorkspaceContextPackRelationPreview[] Relations);

    private sealed record EntryCandidate(
        WorkspaceContextPackFilePin File,
        WorkspaceContextPackEntry Template,
        int OutputIndex);

    public static (WorkspaceContextPackScope Scope, WorkspaceContextPackRelationPreview[] Relations) ResolvePreview(
        AuthenticatedContext actor,
        WorkspaceContextPackSelection selection,
        IManagedResourceStore store,
        IWorkspacePersistence workspace)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(workspace);
        if (actor.Kind != PrincipalKind.Owner)
        {
            throw new UnauthorizedAccessException("Workspace context-pack preview requires the owner workflow.");
        }

        ValidateSelection(selection);
        var snapshot = workspace.ReadWorkspaceTree();
        if (snapshot is null || snapshot.Nodes is null)
        {
            throw Refuse("scope_selection_unavailable");
        }

        var nodes = WorkspaceFolderGrantEvaluator.BuildNodeIndex(snapshot);
        var files = new WorkspaceContextPackFilePin[selection.Files.Length];
        var filePins = new Dictionary<string, WorkspaceContextPackFilePin>(StringComparer.Ordinal);
        for (var index = 0; index < selection.Files.Length; index++)
        {
            var selected = selection.Files[index];
            if (!nodes.TryGetValue(selected.FileId, out var node) || node.Kind != WorkspaceNodeKind.File ||
                node.Revisions is null)
            {
                throw Refuse("scope_selection_unavailable");
            }
            var revision = node.Revisions.FirstOrDefault(item => item.Revision == selected.ContentRevision);
            if (revision is null || revision.Length < 0 || revision.Length > WorkspaceContentRevisionInfo.MaximumContentBytes ||
                !IsSha256(selected.FullSha256) || !string.Equals(revision.Sha256, selected.FullSha256, StringComparison.Ordinal))
            {
                throw Refuse("scope_selection_unavailable");
            }

            WorkspaceFileContent? content = null;
            try
            {
                content = workspace.ReadWorkspaceFile(selected.FileId, selected.ContentRevision);
                if (content.Revision != selected.ContentRevision || content.Length != revision.Length ||
                    content.Content.LongLength != revision.Length ||
                    !string.Equals(content.Sha256, revision.Sha256, StringComparison.Ordinal) ||
                    !string.Equals(ComputeSha256(content.Content), revision.Sha256, StringComparison.Ordinal))
                {
                    throw Refuse("scope_selection_unavailable");
                }

                var passages = new WorkspaceContextPackPassagePin[selected.Passages.Length];
                for (var passageIndex = 0; passageIndex < selected.Passages.Length; passageIndex++)
                {
                    var passage = selected.Passages[passageIndex];
                    if (passage.Offset < 0 || passage.Length <= 0 || passage.Offset > revision.Length ||
                        passage.Length > revision.Length - passage.Offset || !IsSha256(passage.Sha256))
                    {
                        throw Refuse("scope_selection_unavailable");
                    }

                    var digest = SHA256.HashData(content.Content.AsSpan(
                        checked((int)passage.Offset), checked((int)passage.Length)));
                    if (!string.Equals(Convert.ToHexString(digest).ToLowerInvariant(), passage.Sha256, StringComparison.Ordinal))
                    {
                        throw Refuse("scope_selection_unavailable");
                    }

                    passages[passageIndex] = new WorkspaceContextPackPassagePin(
                        selected.FileId,
                        selected.ContentRevision,
                        passage.Offset,
                        passage.Length,
                        passage.Sha256);
                }

                var pin = new WorkspaceContextPackFilePin(
                    selected.FileId,
                    selected.ContentRevision,
                    revision.Length,
                    revision.Sha256,
                    node.AccessLifecycleGeneration,
                    selected.IsSeed,
                    passages);
                files[index] = pin;
                filePins.Add(pin.FileId, pin);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WorkspaceRefusalException)
            {
                throw Refuse("scope_selection_unavailable");
            }
            finally
            {
                if (content?.Content is { Length: > 0 } bytes)
                {
                    CryptographicOperations.ZeroMemory(bytes);
                }
            }
        }

        var preliminary = WorkspaceContextPackGrantAuthority.NormalizeScope(
            new WorkspaceContextPackScope(snapshot.WorkspaceId, snapshot.Revision, files, []));
        if (selection.RelationIds.Length == 0)
        {
            return (preliminary, []);
        }

        bool EndpointIsSelected(RelationEndpoint endpoint) =>
            TryCreateEndpointPin(endpoint, filePins, out _);

        var relationReads = new RelationService().ReadWorkspaceContextPackRelations(
            store, workspace, selection.RelationIds, EndpointIsSelected);
        if (relationReads.Length != selection.RelationIds.Length)
        {
            throw Refuse("scope_selection_unavailable");
        }

        var relationPreviews = new WorkspaceContextPackRelationPreview[relationReads.Length];
        var relationPins = new WorkspaceContextPackRelationPin[relationReads.Length];
        for (var index = 0; index < relationReads.Length; index++)
        {
            var read = relationReads[index];
            if (read.Relation is not { State: RelationState.Accepted } relation ||
                read.Review is not { State: RelationReviewState.Current } review ||
                !TryCreateEndpointPin(relation.From, filePins, out var from) ||
                !TryCreateEndpointPin(relation.To, filePins, out var to))
            {
                throw Refuse("scope_selection_unavailable");
            }

            relationPreviews[index] = new WorkspaceContextPackRelationPreview(relation, review);
            relationPins[index] = new WorkspaceContextPackRelationPin(
                relation.Id,
                relation.Revision,
                relation.Kind.ToString(),
                relation.State.ToString(),
                from,
                to);
        }

        var scope = WorkspaceContextPackGrantAuthority.NormalizeScope(
            preliminary with { Relations = relationPins });
        if (!WorkspaceContextPackGrantAuthority.RelationDetailsMatch(scope, relationPreviews))
        {
            throw Refuse("scope_selection_unavailable");
        }

        return (scope, relationPreviews);
    }

    public static bool RelationsAreCurrent(
        IManagedResourceStore store,
        IWorkspacePersistence workspace,
        WorkspaceContextPackScope scope)
    {
        var current = ReadCurrentRelations(store, workspace, scope);
        return WorkspaceContextPackGrantAuthority.RelationDetailsMatch(scope, current);
    }

    public static WorkspaceContextPackReadResult Read(
        IWorkspaceContextPackGrantPersistence persistence,
        string grantId,
        int semanticsVersion,
        long semanticsGeneration,
        int budgetBytes)
    {
        ArgumentNullException.ThrowIfNull(persistence);
        if (budgetBytes is < 1 or > MaximumBudgetBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(budgetBytes));
        }

        return persistence.ReadWorkspaceContextPackForGrant(
            grantId,
            semanticsVersion,
            semanticsGeneration,
            (state, scope, _, store, workspace) => BuildReadResult(scope, store, workspace, budgetBytes));
    }

    private static WorkspaceContextPackReadResult BuildReadResult(
        WorkspaceContextPackScope scope,
        IManagedResourceStore store,
        IWorkspacePersistence workspace,
        int budgetBytes)
    {
        var relationDetails = ReadCurrentRelations(store, workspace, scope);
        if (!WorkspaceContextPackGrantAuthority.RelationDetailsMatch(scope, relationDetails))
        {
            throw Refuse("request_stale");
        }

        var relationById = relationDetails.ToDictionary(item => item.Relation.Id, StringComparer.Ordinal);
        var paths = BuildPaths(scope, relationById);
        var candidates = new List<EntryCandidate>();
        var gaps = new List<WorkspaceContextPackGap>();
        var candidateCount = 0;
        var usedBytes = 0L;

        foreach (var file in scope.Files)
        {
            var passageCount = file.Passages.Length == 0 ? 1 : file.Passages.Length;
            if (!paths.TryGetValue((file.FileId, file.ContentRevision), out var path))
            {
                for (var passageIndex = 0; passageIndex < passageCount; passageIndex++)
                {
                    var offset = file.Passages.Length == 0 ? 0 : file.Passages[passageIndex].Offset;
                    var length = file.Passages.Length == 0 ? file.Length : file.Passages[passageIndex].Length;
                    var digest = file.Passages.Length == 0 ? file.Sha256 : file.Passages[passageIndex].Sha256;
                    var citation = file.Passages.Length == 0
                        ? FileCitation(file)
                        : PassageCitation(file, offset, length, digest);
                    candidateCount++;
                    gaps.Add(new WorkspaceContextPackGap(citation, "not_reachable_within_two_hops"));
                }

                continue;
            }

            for (var passageIndex = 0; passageIndex < passageCount; passageIndex++)
            {
                var offset = file.Passages.Length == 0 ? 0 : file.Passages[passageIndex].Offset;
                var length = file.Passages.Length == 0 ? file.Length : file.Passages[passageIndex].Length;
                var contentSha256 = file.Passages.Length == 0 ? file.Sha256 : file.Passages[passageIndex].Sha256;
                var citation = file.Passages.Length == 0
                    ? FileCitation(file)
                    : PassageCitation(file, offset, length, contentSha256);
                var template = new WorkspaceContextPackEntry(
                    path.SeedFileId,
                    file.FileId,
                    file.ContentRevision,
                    offset,
                    length,
                    file.Sha256,
                    contentSha256,
                    citation,
                    file.IsSeed,
                    "verbatim",
                    [],
                    path.Relations);
                var metadata = JsonSerializer.SerializeToUtf8Bytes(template, BudgetJsonOptions);
                long costBytes;
                try
                {
                    costBytes = checked(metadata.LongLength + Base64Length(length));
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(metadata);
                }

                candidateCount++;
                if (costBytes > budgetBytes - usedBytes)
                {
                    gaps.Add(new WorkspaceContextPackGap(citation, "entry_omitted_by_budget"));
                    continue;
                }

                usedBytes += costBytes;
                candidates.Add(new EntryCandidate(file, template, candidates.Count));
            }
        }

        var entries = new WorkspaceContextPackEntry[candidates.Count];
        var candidatesByFile = candidates.GroupBy(candidate => candidate.File.FileId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        foreach (var file in scope.Files)
        {
            WorkspaceFileContent? content = null;
            try
            {
                content = workspace.ReadWorkspaceFile(file.FileId, file.ContentRevision);
                if (!WorkspaceContextPackGrantAuthority.ContentMatches(file, content))
                {
                    throw Refuse("request_stale");
                }

                if (!candidatesByFile.TryGetValue(file.FileId, out var fileCandidates))
                {
                    continue;
                }

                foreach (var candidate in fileCandidates)
                {
                    entries[candidate.OutputIndex] = candidate.Template with
                    {
                        VerbatimBytes = content.Content.AsSpan(
                            checked((int)candidate.Template.Offset),
                            checked((int)candidate.Template.Length)).ToArray()
                    };
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WorkspaceRefusalException)
            {
                throw Refuse("request_stale");
            }
            finally
            {
                if (content?.Content is { Length: > 0 } bytes)
                {
                    CryptographicOperations.ZeroMemory(bytes);
                }
            }
        }

        var includedCount = entries.Length;
        var omittedCount = gaps.Count;
        var status = includedCount == 0
            ? "unavailable"
            : omittedCount == 0
                ? "complete"
                : "partial";
        return new WorkspaceContextPackReadResult(
            status,
            budgetBytes,
            usedBytes,
            candidateCount,
            includedCount,
            omittedCount,
            entries,
            gaps.ToArray());
    }
    private static WorkspaceContextPackRelationPreview[] ReadCurrentRelations(
        IManagedResourceStore store,
        IWorkspacePersistence workspace,
        WorkspaceContextPackScope scope)
    {
        if (scope.Relations.Length == 0)
        {
            return [];
        }

        var filePins = scope.Files.ToDictionary(file => file.FileId, StringComparer.Ordinal);
        bool EndpointIsSelected(RelationEndpoint endpoint) => TryCreateEndpointPin(endpoint, filePins, out _);
        return new RelationService().ReadWorkspaceContextPackRelations(
                store,
                workspace,
                scope.Relations.Select(relation => relation.RelationId).ToArray(),
                EndpointIsSelected)
            .Where(result => result.Relation is not null && result.Review is not null)
            .Select(result => new WorkspaceContextPackRelationPreview(result.Relation!, result.Review!))
            .ToArray();
    }

    private static bool TryCreateEndpointPin(
        RelationEndpoint endpoint,
        IReadOnlyDictionary<string, WorkspaceContextPackFilePin> filePins,
        out WorkspaceContextPackEndpointPin pin)
    {
        pin = null!;
        if (!filePins.TryGetValue(endpoint.ResourceId, out var file) ||
            endpoint.Revision != file.ContentRevision || endpoint.WorkspacePin?.Sha256 is not { } digest)
        {
            return false;
        }

        if (endpoint.EndpointKind == RelationEndpointKind.WorkspaceFile && endpoint.Span is null &&
            string.Equals(digest, file.Sha256, StringComparison.Ordinal))
        {
            pin = new WorkspaceContextPackEndpointPin(
                endpoint.ResourceId, endpoint.EndpointKind.ToString(), endpoint.Revision, null, null, digest);
            return true;
        }

        if (endpoint.EndpointKind == RelationEndpointKind.WorkspacePassage && endpoint.Span is { } span &&
            file.Passages.Any(passage => passage.Offset == span.Start &&
                passage.Length == span.End - span.Start && string.Equals(passage.Sha256, digest, StringComparison.Ordinal)))
        {
            pin = new WorkspaceContextPackEndpointPin(
                endpoint.ResourceId,
                endpoint.EndpointKind.ToString(),
                endpoint.Revision,
                span.Start,
                span.End - span.Start,
                digest);
            return true;
        }

        return false;
    }


    private static Dictionary<(string FileId, long Revision), PathState> BuildPaths(
        WorkspaceContextPackScope scope,
        IReadOnlyDictionary<string, WorkspaceContextPackRelationPreview> relations)
    {
        var files = scope.Files.ToDictionary(file => (file.FileId, file.ContentRevision));
        var reached = new Dictionary<(string FileId, long Revision), PathState>();
        var frontier = new List<PathState>();
        foreach (var seed in scope.Files.Where(file => file.IsSeed)
                     .OrderBy(file => file.FileId, StringComparer.Ordinal))
        {
            var state = new PathState(seed.FileId, seed.FileId, seed.ContentRevision, []);
            reached.Add((seed.FileId, seed.ContentRevision), state);
            frontier.Add(state);
        }

        var orderedRelations = relations.Values.OrderBy(item => item.Relation.Id, StringComparer.Ordinal).ToArray();
        for (var hop = 0; hop < WorkspaceHybridSearchService.MaximumRelationHops && frontier.Count > 0; hop++)
        {
            var next = new Dictionary<(string FileId, long Revision), PathState>();
            foreach (var source in frontier)
            {
                foreach (var relationPreview in orderedRelations)
                {
                    var relation = relationPreview.Relation;
                    if (relation.From.EndpointKind is not (RelationEndpointKind.WorkspaceFile or RelationEndpointKind.WorkspacePassage) ||
                        relation.To.EndpointKind is not (RelationEndpointKind.WorkspaceFile or RelationEndpointKind.WorkspacePassage))
                    {
                        continue;
                    }

                    var sourceMatches = relation.Kind == RelationKind.RelatedTo
                        ? string.Equals(source.FileId, relation.From.ResourceId, StringComparison.Ordinal)
                        : source.FileId == relation.From.ResourceId && source.Revision == relation.From.Revision;
                    var targetIdentity = (relation.To.ResourceId, relation.To.Revision);
                    if (!sourceMatches || !files.ContainsKey(targetIdentity) || reached.ContainsKey(targetIdentity))
                    {
                        continue;
                    }

                    var path = new WorkspaceContextPackRelationPreview[source.Relations.Length + 1];
                    Array.Copy(source.Relations, path, source.Relations.Length);
                    path[^1] = relationPreview;
                    var candidate = new PathState(source.SeedFileId, targetIdentity.ResourceId, targetIdentity.Revision, path);
                    if (!next.TryGetValue(targetIdentity, out var existing) || ComparePaths(candidate, existing) < 0)
                    {
                        next[targetIdentity] = candidate;
                    }
                }
            }

            foreach (var pair in next.OrderBy(pair => pair.Key.FileId, StringComparer.Ordinal)
                         .ThenBy(pair => pair.Key.Revision))
            {
                reached.Add(pair.Key, pair.Value);
            }

            frontier = next.Values.OrderBy(state => state.FileId, StringComparer.Ordinal)
                .ThenBy(state => state.Revision)
                .ToList();
        }

        return reached;
    }

    private static int ComparePaths(PathState left, PathState right)
    {
        var seedOrder = StringComparer.Ordinal.Compare(left.SeedFileId, right.SeedFileId);
        if (seedOrder != 0)
        {
            return seedOrder;
        }

        var count = Math.Min(left.Relations.Length, right.Relations.Length);
        for (var index = 0; index < count; index++)
        {
            var relationOrder = StringComparer.Ordinal.Compare(
                left.Relations[index].Relation.Id,
                right.Relations[index].Relation.Id);
            if (relationOrder != 0)
            {
                return relationOrder;
            }
        }

        return left.Relations.Length.CompareTo(right.Relations.Length);
    }

    private static void ValidateSelection(WorkspaceContextPackSelection? selection)
    {
        if (selection is null || selection.Files is null ||
            selection.Files.Length is 0 or > WorkspaceContextPackGrantAuthority.MaximumFiles ||
            selection.RelationIds is null ||
            selection.RelationIds.Length > WorkspaceContextPackGrantAuthority.MaximumRelations)
        {
            throw Refuse("scope_selection_unavailable");
        }

        var fileIds = new HashSet<string>(StringComparer.Ordinal);
        var totalPassages = 0;
        var seedCount = 0;
        foreach (var file in selection.Files)
        {
            if (file is null || !WorkspaceFolderGrantEvaluator.IsNodeId(file.FileId) ||
                file.ContentRevision < 1 || !IsSha256(file.FullSha256) || file.Passages is null ||
                !fileIds.Add(file.FileId))
            {
                throw Refuse("scope_selection_unavailable");
            }

            if (file.IsSeed)
            {
                seedCount++;
            }

            if (file.Passages.Length > WorkspaceContextPackGrantAuthority.MaximumPassages - totalPassages)
            {
                throw Refuse("scope_selection_unavailable");
            }

            totalPassages += file.Passages.Length;
            var passageKeys = new HashSet<(long Offset, long Length)>();
            foreach (var passage in file.Passages)
            {
                if (passage is null || passage.Offset < 0 || passage.Length <= 0 ||
                    passage.Offset > WorkspaceContentRevisionInfo.MaximumContentBytes ||
                    passage.Length > WorkspaceContentRevisionInfo.MaximumContentBytes - passage.Offset ||
                    !IsSha256(passage.Sha256) || !passageKeys.Add((passage.Offset, passage.Length)))
                {
                    throw Refuse("scope_selection_unavailable");
                }
            }
        }

        if (seedCount == 0 || selection.RelationIds.Any(id =>
                id is null || !DomainIdentifiers.IsRelationId(id)) ||
            selection.RelationIds.Distinct(StringComparer.Ordinal).Count() != selection.RelationIds.Length)
        {
            throw Refuse("scope_selection_unavailable");
        }
    }

    private static string FileCitation(WorkspaceContextPackFilePin file) =>
        $"workspace://{file.FileId}@{file.ContentRevision};sha256={file.Sha256}";

    private static string PassageCitation(
        WorkspaceContextPackFilePin file,
        long offset,
        long length,
        string sha256) =>
        $"workspace://{file.FileId}@{file.ContentRevision}#bytes={offset}:{length};sha256={sha256};file-sha256={file.Sha256}";

    private static long Base64Length(long byteLength) => checked(((byteLength + 2) / 3) * 4);

    private static string ComputeSha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static bool IsSha256(string? value) => value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static WorkspaceContextPackGrantRefusalException Refuse(string code) => new(code);
}
