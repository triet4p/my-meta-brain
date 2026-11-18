using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using MetaBrain.Core.Domain;
using MetaBrain.Core.Index;
using MetaBrain.Core.Security;

namespace MetaBrain.Application;

public sealed record WorkspaceAgentRecallAnnotationEntry(
    string AnnotationId,
    long Revision,
    string[] Facets,
    string Kind,
    string Author,
    string Executor,
    string CreationMethod,
    string Evidence,
    string AbstractionLevel,
    string Origin,
    string ReviewStatus,
    DateTimeOffset RecordedAtUtc,
    string AnchorCitation,
    string AnchorState,
    string SourceAvailability);

public sealed record WorkspaceAgentRecallHybridHit(
    int Rank,
    string FileId,
    long ContentRevision,
    string FileSha256,
    string Citation,
    string Field,
    string[] Channels,
    string EvidenceClass,
    bool Stale,
    int? Line,
    long? ByteOffset,
    long? ByteLength,
    string? AnchorSha256,
    string Evidence,
    string? AnchorKind,
    string? Attribution,
    int? ChunkOrdinal,
    double? Similarity);

public sealed record WorkspaceAgentRecallRelationEntry(Relation Relation, RelationReview Review);

public sealed record WorkspaceAgentRecallReadResult(
    string Operation,
    string Status,
    int BudgetBytes,
    int UsedBytes,
    int CandidateCount,
    int IncludedCount,
    int OmittedCount,
    string? Query,
    int? Total,
    int? LiteralTotal,
    int? SemanticCandidateTotal,
    int? Offset,
    int? Limit,
    bool? HasMore,
    string? SemanticStatus,
    string? ModelRevision,
    int? VectorDimensions,
    WorkspaceAgentRecallAnnotationEntry[] Annotations,
    WorkspaceAgentRecallHybridHit[] HybridHits,
    WorkspaceAgentRecallRelationEntry[] Relations);

/// <summary>Resolves and serves the separately pinned workspace recall authority.</summary>
public static class WorkspaceAgentRecallService
{
    public const int MaximumOutputBytes = 60 * 1024;
    public const int MaximumHybridQueryChars = 256;
    public const int MaximumHybridLimit = 100;
    private static readonly JsonSerializerOptions BudgetJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private sealed record AnnotationResolution(
        CognitiveAnnotation[] Annotations,
        WorkspaceAgentRecallAnnotationPin[] Pins);

    public static (WorkspaceAgentRecallScope Scope, CognitiveAnnotation[] Annotations,
        WorkspaceAgentRecallRelationPreview[] Relations, WorkspacePassageIndexStatus? PassageIndex) ResolvePreview(
        AuthenticatedContext actor,
        WorkspaceAgentRecallSelection selection,
        IManagedResourceStore store,
        IWorkspacePersistence workspace,
        IWorkspacePassageIndexEngine? passageIndex)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(workspace);
        if (actor.Kind != PrincipalKind.Owner || selection.Operations is null ||
            selection.AnnotationIds is null || selection.Files is null || selection.RelationIds is null ||
            selection.Operations.Length is < 1 or > 3 ||
            selection.Operations.Any(operation => operation is null) ||
            selection.Operations.Distinct(StringComparer.Ordinal).Count() != selection.Operations.Length ||
            selection.AnnotationIds.Length > WorkspaceAgentRecallGrantAuthority.MaximumAnnotations ||
            selection.Files.Length > WorkspaceAgentRecallGrantAuthority.MaximumFiles ||
            selection.RelationIds.Length > WorkspaceAgentRecallGrantAuthority.MaximumRelations ||
            selection.RelationIds.Any(id => id is null) ||
            selection.RelationIds.Distinct(StringComparer.Ordinal).Count() != selection.RelationIds.Length)
        {
            throw Refuse("scope_selection_unavailable");
        }

        var snapshot = workspace.ReadWorkspaceTree();
        if (snapshot is null || snapshot.Nodes is null || snapshot.Revision < 1)
        {
            throw Refuse("scope_selection_unavailable");
        }

        Dictionary<string, WorkspaceNodeInfo> nodes;
        try
        {
            nodes = WorkspaceFolderGrantEvaluator.BuildNodeIndex(snapshot);
        }
        catch (ArgumentException)
        {
            throw Refuse("scope_selection_unavailable");
        }
        catch (InvalidDataException)
        {
            throw Refuse("scope_selection_unavailable");
        }

        var operations = selection.Operations;
        var hasAnnotations = operations.Contains(
            WorkspaceAgentRecallGrantAuthority.CognitiveAnnotationReadOperation, StringComparer.Ordinal);
        var hasHybrid = operations.Contains(
            WorkspaceAgentRecallGrantAuthority.HybridSearchOperation, StringComparer.Ordinal);
        var hasRelations = operations.Contains(
            WorkspaceAgentRecallGrantAuthority.RelationRecallOperation, StringComparer.Ordinal);
        if (hasAnnotations != (selection.AnnotationIds.Length > 0) ||
            hasHybrid != (selection.Files.Length > 0) ||
            hasRelations != (selection.RelationIds.Length > 0))
        {
            throw Refuse("scope_selection_unavailable");
        }

        var files = ResolveFiles(selection.Files, nodes, workspace);
        var annotations = hasAnnotations
            ? ResolveAnnotations(actor, selection.AnnotationIds, store, workspace)
            : new AnnotationResolution([], []);
        var relationDetails = hasRelations
            ? ResolveRelations(selection.RelationIds, snapshot, store, workspace)
            : [];
        var relationPins = relationDetails.Select(detail => PinRelation(detail.Relation, snapshot, workspace))
            .ToArray();
        if (relationPins.Any(pin => pin is null))
        {
            throw Refuse("scope_selection_unavailable");
        }

        WorkspacePassageIndexStatus? indexStatus = null;
        if (hasHybrid)
        {
            if (passageIndex is null)
            {
                throw Refuse("workspace_index_unavailable");
            }

            indexStatus = passageIndex.ReadStatus(workspace);
            if (indexStatus.Stale || indexStatus.State is not ("ready" or "partial") ||
                indexStatus.WorkspaceRevision != snapshot.Revision)
            {
                throw Refuse("workspace_agent_recall_scope_stale");
            }
        }

        var scope = WorkspaceAgentRecallGrantAuthority.NormalizeScope(new WorkspaceAgentRecallScope(
            snapshot.WorkspaceId,
            snapshot.Revision,
            operations,
            annotations.Pins,
            files,
            relationPins.Cast<WorkspaceContextPackRelationPin>().ToArray(),
            indexStatus?.SchemaVersion,
            indexStatus?.WorkspaceDigest));
        return (scope, annotations.Annotations, relationDetails, indexStatus);
    }

    public static bool IsCurrent(
        AuthenticatedContext actor,
        WorkspaceAgentRecallScope scope,
        IManagedResourceStore store,
        IWorkspacePersistence workspace,
        IWorkspacePassageIndexEngine? passageIndex)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(workspace);
        var snapshot = workspace.ReadWorkspaceTree();
        if (!WorkspaceAgentRecallGrantAuthority.MatchesWorkspaceMetadata(scope, snapshot))
        {
            return false;
        }

        Dictionary<string, WorkspaceNodeInfo> nodes;
        try
        {
            nodes = WorkspaceFolderGrantEvaluator.BuildNodeIndex(snapshot!);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidDataException)
        {
            return false;
        }

        try
        {
            var annotations = ResolveAnnotations(actor,
                scope.Annotations.Select(pin => pin.AnnotationId).ToArray(), store, workspace);
            if (!annotations.Pins.SequenceEqual(scope.Annotations))
            {
                return false;
            }

            var files = ResolveFiles(scope.Files.Select(pin =>
                    new WorkspaceAgentRecallFileSelection(pin.FileId, pin.ContentRevision, pin.Sha256)).ToArray(),
                nodes,
                workspace);
            if (!files.SequenceEqual(scope.Files))
            {
                return false;
            }

            var relations = scope.Relations.Length == 0
                ? []
                : ResolveRelations(scope.Relations.Select(pin => pin.RelationId).ToArray(), snapshot!, store, workspace);
            if (relations.Length != scope.Relations.Length)
            {
                return false;
            }

            var relationPins = relations.Select(detail => PinRelation(detail.Relation, snapshot!, workspace)).ToArray();
            if (relationPins.Any(pin => pin is null) ||
                !relationPins.Cast<WorkspaceContextPackRelationPin>().SequenceEqual(scope.Relations))
            {
                return false;
            }

            if (scope.Operations.Contains(WorkspaceAgentRecallGrantAuthority.HybridSearchOperation, StringComparer.Ordinal))
            {
                if (passageIndex is null)
                {
                    return false;
                }

                var currentIndex = passageIndex.ReadStatus(workspace);
                if (currentIndex.Stale || currentIndex.State is not ("ready" or "partial") ||
                    currentIndex.SchemaVersion != scope.IndexSchemaVersion ||
                    currentIndex.WorkspaceRevision != scope.TreeRevision ||
                    !string.Equals(currentIndex.WorkspaceDigest, scope.IndexGeneration, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            var current = scope with
            {
                Annotations = annotations.Pins,
                Files = files,
                Relations = relationPins.Cast<WorkspaceContextPackRelationPin>().ToArray()
            };
            return WorkspaceAgentRecallGrantAuthority.ScopeMatches(scope,
                WorkspaceAgentRecallGrantAuthority.NormalizeScope(current));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or IOException or
                                   UnauthorizedAccessException or WorkspaceRefusalException or
                                   CognitiveAnnotationConflictException or WorkspaceAgentRecallGrantRefusalException)
        {
            return false;
        }
    }

    public static WorkspaceAgentRecallReadResult Read(
        IVaultOperation persistence,
        AuthenticatedContext actor,
        string grantId,
        int semanticsVersion,
        long semanticsGeneration,
        string requestedOperation,
        string? query,
        int? offset,
        int? limit,
        IWorkspacePassageIndexEngine? passageIndex,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(persistence);
        ArgumentNullException.ThrowIfNull(actor);
        if (actor.Kind != PrincipalKind.Agent || string.IsNullOrWhiteSpace(requestedOperation))
        {
            throw Refuse("workspace_agent_recall_unavailable");
        }

        return persistence.ReadWorkspaceContextPackGrantSnapshot((state, snapshot, store, workspace) =>
        {
            state = OwnerScopeGrantAuthority.ValidateAndMigrateState(state);
            var grant = state.Grants.FirstOrDefault(candidate =>
                string.Equals(candidate.GrantId, grantId, StringComparison.Ordinal));
            if (grant is null || !WorkspaceAgentRecallGrantAuthority.IsActiveGrant(
                    state, grant, semanticsVersion, semanticsGeneration) ||
                grant.WorkspaceAgentRecallScope is not { } scope ||
                !grant.Operations.Contains(requestedOperation, StringComparer.Ordinal) ||
                !WorkspaceAgentRecallGrantAuthority.MatchesWorkspaceMetadata(scope, snapshot) ||
                !IsCurrent(actor, scope, store, workspace, passageIndex))
            {
                throw Refuse("workspace_agent_recall_scope_stale");
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (string.Equals(requestedOperation,
                    WorkspaceAgentRecallGrantAuthority.CognitiveAnnotationReadOperation, StringComparison.Ordinal))
            {
                if (query is not null || offset is not null || limit is not null)
                {
                    throw Refuse("invalid_request");
                }

                var annotations = ResolveAnnotations(actor,
                    scope.Annotations.Select(pin => pin.AnnotationId).ToArray(), store, workspace);
                if (!annotations.Pins.SequenceEqual(scope.Annotations))
                {
                    throw Refuse("workspace_agent_recall_scope_stale");
                }

                var candidates = annotations.Annotations.Select(ToAnnotationEntry).ToArray();
                return Fit(candidates, (entries, count, included, omitted, status, used) =>
                    new WorkspaceAgentRecallReadResult(requestedOperation, status, MaximumOutputBytes, used,
                        count, included, omitted, null, null, null, null, null, null, null, null, null, null,
                        entries, [], []));
            }

            if (string.Equals(requestedOperation,
                    WorkspaceAgentRecallGrantAuthority.RelationRecallOperation, StringComparison.Ordinal))
            {
                if (query is not null || offset is not null || limit is not null)
                {
                    throw Refuse("invalid_request");
                }

                var relations = ResolveRelations(scope.Relations.Select(pin => pin.RelationId).ToArray(),
                    snapshot!, store, workspace);
                var pins = relations.Select(detail => PinRelation(detail.Relation, snapshot!, workspace)).ToArray();
                if (pins.Any(pin => pin is null) ||
                    !pins.Cast<WorkspaceContextPackRelationPin>().SequenceEqual(scope.Relations))
                {
                    throw Refuse("workspace_agent_recall_scope_stale");
                }

                var candidates = relations.Select(detail =>
                    new WorkspaceAgentRecallRelationEntry(detail.Relation, detail.Review)).ToArray();
                return Fit(candidates, (entries, count, included, omitted, status, used) =>
                    new WorkspaceAgentRecallReadResult(requestedOperation, status, MaximumOutputBytes, used,
                        count, included, omitted, null, null, null, null, null, null, null, null, null, null,
                        [], [], entries));
            }

            if (string.Equals(requestedOperation,
                    WorkspaceAgentRecallGrantAuthority.HybridSearchOperation, StringComparison.Ordinal))
            {
                if (query is null || string.IsNullOrWhiteSpace(query) || query.Length > MaximumHybridQueryChars ||
                    query.Any(char.IsControl))
                {
                    throw Refuse("invalid_request");
                }

                var pageOffset = offset ?? 0;
                var pageLimit = limit ?? WorkspaceHybridSearchService.DefaultLimit;
                if (pageOffset < 0 || pageOffset > 10_000 || pageLimit is < 1 or > MaximumHybridLimit ||
                    passageIndex is null)
                {
                    throw Refuse("invalid_request");
                }

                var outcome = new WorkspaceHybridSearchService(passageIndex).SearchForAgent(
                    actor,
                    persistence,
                    new WorkspaceHybridSearchRequest(
                        query,
                        null,
                        null,
                        WorkspaceHybridSearchCurrent.All,
                        null,
                        null,
                        pageOffset,
                        pageLimit,
                        null),
                    scope,
                    cancellationToken);
                var candidates = outcome.Hits.Select(ToHybridHit).ToArray();
                return Fit(candidates, (entries, count, included, omitted, status, used) =>
                    new WorkspaceAgentRecallReadResult(requestedOperation, status, MaximumOutputBytes, used,
                        count, included, omitted, query, outcome.Total, outcome.LiteralTotal,
                        outcome.SemanticCandidateTotal, outcome.Offset, outcome.Limit,
                        outcome.HasMore, outcome.SemanticStatus, outcome.ModelRevision, outcome.VectorDimensions,
                        [], entries, []));
            }

            throw Refuse("invalid_request");
        });
    }

    private static AnnotationResolution ResolveAnnotations(
        AuthenticatedContext actor,
        IReadOnlyList<string> annotationIds,
        IManagedResourceStore store,
        IWorkspacePersistence workspace)
    {
        if (annotationIds.Count > WorkspaceAgentRecallGrantAuthority.MaximumAnnotations ||
            annotationIds.Any(id => !DomainIdentifiers.IsCognitiveAnnotationId(id)) ||
            annotationIds.Distinct(StringComparer.Ordinal).Count() != annotationIds.Count)
        {
            throw Refuse("scope_selection_unavailable");
        }

        var identities = store.ListResources()
            .Where(resource => annotationIds.Contains(resource.ResourceId, StringComparer.Ordinal))
            .ToDictionary(resource => resource.ResourceId, StringComparer.Ordinal);
        if (identities.Count != annotationIds.Count)
        {
            throw Refuse("scope_selection_unavailable");
        }

        var service = new CognitiveAnnotationService();
        var annotations = new CognitiveAnnotation[annotationIds.Count];
        var pins = new WorkspaceAgentRecallAnnotationPin[annotationIds.Count];
        for (var index = 0; index < annotationIds.Count; index++)
        {
            var id = annotationIds[index];
            var identity = identities[id];
            var annotation = service.ReadForWorkspaceRecall(actor, id, identity.Revision, store, workspace);
            annotations[index] = annotation;
            pins[index] = PinAnnotation(annotation);
        }

        Array.Sort(annotations, static (left, right) => StringComparer.Ordinal.Compare(left.Id, right.Id));
        Array.Sort(pins, static (left, right) => StringComparer.Ordinal.Compare(left.AnnotationId, right.AnnotationId));
        return new AnnotationResolution(annotations, pins);
    }

    private static WorkspaceAgentRecallFilePin[] ResolveFiles(
        IReadOnlyList<WorkspaceAgentRecallFileSelection> selections,
        IReadOnlyDictionary<string, WorkspaceNodeInfo> nodes,
        IWorkspacePersistence workspace)
    {
        if (selections.Count > WorkspaceAgentRecallGrantAuthority.MaximumFiles ||
            selections.Any(selection => selection is null) ||
            selections.Select(selection => selection.FileId).Distinct(StringComparer.Ordinal).Count() != selections.Count)
        {
            throw Refuse("scope_selection_unavailable");
        }

        var pins = new WorkspaceAgentRecallFilePin[selections.Count];
        for (var index = 0; index < selections.Count; index++)
        {
            var selection = selections[index];
            if (!WorkspaceFolderGrantEvaluator.IsNodeId(selection.FileId) || selection.ContentRevision < 1 ||
                !IsSha256(selection.FullSha256) || !nodes.TryGetValue(selection.FileId, out var node) ||
                node.Kind != WorkspaceNodeKind.File || node.Revisions is null)
            {
                throw Refuse("scope_selection_unavailable");
            }

            var revision = node.Revisions.FirstOrDefault(item => item.Revision == selection.ContentRevision);
            if (revision is null || revision.Length < 0 || revision.Length > WorkspaceContentRevisionInfo.MaximumContentBytes ||
                !string.Equals(revision.Sha256, selection.FullSha256, StringComparison.Ordinal) ||
                !VerifyFile(workspace, selection.FileId, selection.ContentRevision, revision.Length, revision.Sha256))
            {
                throw Refuse("scope_selection_unavailable");
            }

            pins[index] = new WorkspaceAgentRecallFilePin(
                selection.FileId, selection.ContentRevision, revision.Length, revision.Sha256,
                node.AccessLifecycleGeneration);
        }

        Array.Sort(pins, static (left, right) => StringComparer.Ordinal.Compare(left.FileId, right.FileId));
        return pins;
    }

    private static WorkspaceAgentRecallRelationPreview[] ResolveRelations(
        IReadOnlyList<string> relationIds,
        WorkspaceSnapshot snapshot,
        IManagedResourceStore store,
        IWorkspacePersistence workspace)
    {
        if (relationIds.Count > WorkspaceAgentRecallGrantAuthority.MaximumRelations ||
            relationIds.Any(id => !DomainIdentifiers.IsRelationId(id)) ||
            relationIds.Distinct(StringComparer.Ordinal).Count() != relationIds.Count)
        {
            throw Refuse("scope_selection_unavailable");
        }

        if (relationIds.Count == 0)
        {
            return [];
        }

        var reads = new RelationService().ReadWorkspaceContextPackRelations(
            store, workspace, relationIds, static _ => true);
        if (reads.Length != relationIds.Count || reads.Any(read =>
                read.Relation is not { State: RelationState.Accepted } ||
                read.Review is not { State: RelationReviewState.Current }))
        {
            throw Refuse("scope_selection_unavailable");
        }

        var pins = ResolveRelationPins(reads, snapshot, workspace);
        if (pins is null)
        {
            throw Refuse("scope_selection_unavailable");
        }

        return reads.Select(read => new WorkspaceAgentRecallRelationPreview(read.Relation!, read.Review!))
            .OrderBy(detail => detail.Relation.Id, StringComparer.Ordinal)
            .ToArray();
    }

    private static WorkspaceContextPackRelationPin[]? ResolveRelationPins(
        IReadOnlyList<RelationReadResult> reads,
        WorkspaceSnapshot snapshot,
        IWorkspacePersistence workspace)
    {
        var endpoints = new List<(string RelationId, bool IsFrom, RelationEndpoint Endpoint)>();
        foreach (var read in reads)
        {
            if (read.Relation is not { State: RelationState.Accepted } relation ||
                read.Review is not { State: RelationReviewState.Current })
            {
                return null;
            }

            endpoints.Add((relation.Id, true, relation.From));
            endpoints.Add((relation.Id, false, relation.To));
        }

        Dictionary<string, WorkspaceNodeInfo> nodes;
        try
        {
            nodes = WorkspaceFolderGrantEvaluator.BuildNodeIndex(snapshot);
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidDataException)
        {
            return null;
        }

        var endpointPins = new Dictionary<(string RelationId, bool IsFrom), WorkspaceContextPackEndpointPin>();
        foreach (var group in endpoints.GroupBy(item => (item.Endpoint.ResourceId, item.Endpoint.Revision)))
        {
            if (!nodes.TryGetValue(group.Key.ResourceId, out var node) || node.Kind != WorkspaceNodeKind.File ||
                node.Revisions is null)
            {
                return null;
            }

            var revision = node.Revisions.FirstOrDefault(item => item.Revision == group.Key.Revision);
            if (revision is null || !VerifyFile(workspace, node.Id, revision.Revision, revision.Length, revision.Sha256))
            {
                return null;
            }

            WorkspaceFileContent content;
            try
            {
                content = workspace.ReadWorkspaceFile(node.Id, revision.Revision);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WorkspaceRefusalException)
            {
                return null;
            }

            try
            {
                foreach (var item in group)
                {
                    var endpoint = item.Endpoint;
                    if (endpoint.WorkspacePin?.Sha256 is not { } pinnedDigest ||
                        endpoint.Revision != content.Revision || content.Content.LongLength != content.Length ||
                        !string.Equals(content.Sha256, revision.Sha256, StringComparison.Ordinal) ||
                        !string.Equals(ComputeSha256(content.Content), revision.Sha256, StringComparison.Ordinal))
                    {
                        return null;
                    }

                    WorkspaceContextPackEndpointPin endpointPin;
                    if (endpoint.EndpointKind == RelationEndpointKind.WorkspaceFile && endpoint.Span is null &&
                        string.Equals(pinnedDigest, revision.Sha256, StringComparison.Ordinal))
                    {
                        endpointPin = new WorkspaceContextPackEndpointPin(
                            endpoint.ResourceId, endpoint.EndpointKind.ToString(), endpoint.Revision,
                            null, null, revision.Sha256);
                    }
                    else if (endpoint.EndpointKind == RelationEndpointKind.WorkspacePassage &&
                             endpoint.Span is { } span && span.Start >= 0 && span.End > span.Start &&
                             span.End <= content.Content.LongLength)
                    {
                        var digest = ComputeSha256(content.Content.AsSpan(
                            checked((int)span.Start), checked((int)(span.End - span.Start))));
                        if (!string.Equals(pinnedDigest, digest, StringComparison.Ordinal))
                        {
                            return null;
                        }

                        endpointPin = new WorkspaceContextPackEndpointPin(
                            endpoint.ResourceId, endpoint.EndpointKind.ToString(), endpoint.Revision,
                            span.Start, span.End - span.Start, digest);
                    }
                    else
                    {
                        return null;
                    }

                    endpointPins.Add((item.RelationId, item.IsFrom), endpointPin);
                }
            }
            finally
            {
                if (content.Content.Length > 0)
                {
                    CryptographicOperations.ZeroMemory(content.Content);
                }
            }
        }

        var result = new WorkspaceContextPackRelationPin[reads.Count];
        for (var index = 0; index < reads.Count; index++)
        {
            var relation = reads[index].Relation!;
            if (!endpointPins.TryGetValue((relation.Id, true), out var from) ||
                !endpointPins.TryGetValue((relation.Id, false), out var to))
            {
                return null;
            }

            result[index] = new WorkspaceContextPackRelationPin(
                relation.Id, relation.Revision, relation.Kind.ToString(), relation.State.ToString(), from, to);
        }

        Array.Sort(result, static (left, right) => StringComparer.Ordinal.Compare(left.RelationId, right.RelationId));
        return result;
    }

    private static WorkspaceContextPackRelationPin? PinRelation(
        Relation relation,
        WorkspaceSnapshot snapshot,
        IWorkspacePersistence workspace)
    {
        var pins = ResolveRelationPins([new RelationReadResult(relation, new RelationReview(
            RelationReviewState.Current, RelationReviewReason.None, []))], snapshot, workspace);
        return pins is { Length: 1 } ? pins[0] : null;
    }

    private static WorkspaceAgentRecallAnnotationPin PinAnnotation(CognitiveAnnotation annotation) => new(
        annotation.Id,
        annotation.Revision,
        annotation.Anchor.AnchorId,
        annotation.Anchor.FileId,
        annotation.Anchor.ContentRevision,
        annotation.Anchor.Offset,
        annotation.Anchor.Length,
        annotation.Anchor.Sha256);

    private static WorkspaceAgentRecallAnnotationEntry ToAnnotationEntry(CognitiveAnnotation annotation) => new(
        annotation.Id,
        annotation.Revision,
        annotation.Facets.Select(facet => facet.ToString()).ToArray(),
        annotation.Kind.ToString(),
        annotation.Author.ToString(),
        annotation.Executor.ToString(),
        annotation.CreationMethod.ToString(),
        annotation.Evidence.ToString(),
        annotation.Level.ToString(),
        annotation.Origin.ToString(),
        annotation.ReviewStatus.ToString(),
        annotation.RecordedAtUtc,
        AnchorCitation(annotation.Anchor),
        annotation.Anchor.State,
        annotation.SourceAvailability.ToString());

    private static WorkspaceAgentRecallHybridHit ToHybridHit(WorkspaceHybridSearchHit hit)
    {
        var evidence = BoundEvidence(hit.Evidence, 240);
        var citation = hit.ByteOffset is { } offset && hit.ByteLength is { } length && hit.AnchorSha256 is { } digest
            ? $"workspace://{hit.FileId}@{hit.ContentRevision}#{offset}+{length};sha256={digest}"
            : $"workspace://{hit.FileId}@{hit.ContentRevision};sha256={hit.FileSha256}";
        return new WorkspaceAgentRecallHybridHit(
            hit.Rank, hit.FileId, hit.ContentRevision, hit.FileSha256, citation, hit.Field,
            (string[])hit.Channels.Clone(), hit.EvidenceClass, hit.Stale, hit.Line,
            hit.ByteOffset, hit.ByteLength, hit.AnchorSha256, evidence, hit.AnchorKind,
            hit.Attribution, hit.ChunkOrdinal, hit.Similarity);
    }

    private static bool VerifyFile(
        IWorkspacePersistence workspace,
        string fileId,
        long revision,
        long expectedLength,
        string expectedSha256)
    {
        WorkspaceFileContent? content = null;
        try
        {
            content = workspace.ReadWorkspaceFile(fileId, revision);
            return content.Revision == revision && content.Length == expectedLength &&
                content.Content.LongLength == expectedLength &&
                string.Equals(content.Sha256, expectedSha256, StringComparison.Ordinal) &&
                string.Equals(ComputeSha256(content.Content), expectedSha256, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WorkspaceRefusalException)
        {
            return false;
        }
        finally
        {
            if (content?.Content is { Length: > 0 } bytes)
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
    }

    private static string AnchorCitation(WorkspaceAnchorInfo anchor) =>
        $"workspace://{anchor.FileId}@{anchor.ContentRevision}#{anchor.Offset}+{anchor.Length};sha256={anchor.Sha256}";

    private static string BoundEvidence(string evidence, int maximumCharacters)
    {
        if (evidence.Length <= maximumCharacters)
        {
            return evidence;
        }

        var end = maximumCharacters;
        if (end > 0 && char.IsHighSurrogate(evidence[end - 1]) && char.IsLowSurrogate(evidence[end]))
        {
            end--;
        }

        return evidence[..end];
    }


    private static string ComputeSha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static bool IsSha256(string? value) => value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static WorkspaceAgentRecallGrantRefusalException Refuse(string code) => new(code);

    private static WorkspaceAgentRecallReadResult Fit<T>(
        IReadOnlyList<T> candidates,
        Func<T[], int, int, int, string, int, WorkspaceAgentRecallReadResult> create)
    {
        var candidateCount = candidates.Count;
        var empty = create([], candidateCount, candidateCount, 0, "budget_limited", MaximumOutputBytes);
        var emptyBytes = JsonSerializer.SerializeToUtf8Bytes(empty, BudgetJsonOptions);
        var used = emptyBytes.Length;
        CryptographicOperations.ZeroMemory(emptyBytes);
        if (used > MaximumOutputBytes)
        {
            throw Refuse("workspace_agent_recall_output_unavailable");
        }

        var selected = new List<T>(candidateCount);
        foreach (var candidate in candidates)
        {
            var serialized = JsonSerializer.SerializeToUtf8Bytes(candidate, BudgetJsonOptions);
            try
            {
                var addition = serialized.Length + (selected.Count == 0 ? 0 : 1);
                if (addition <= MaximumOutputBytes - used)
                {
                    selected.Add(candidate);
                    used += addition;
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(serialized);
            }
        }

        var omitted = candidateCount - selected.Count;
        var status = omitted == 0 ? "complete" : "budget_limited";
        var result = create(selected.ToArray(), candidateCount, selected.Count, omitted,
            status, MaximumOutputBytes);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(result, BudgetJsonOptions);
            var actual = bytes.Length;
            CryptographicOperations.ZeroMemory(bytes);
            if (actual > MaximumOutputBytes)
            {
                throw Refuse("workspace_agent_recall_output_unavailable");
            }

            if (result.UsedBytes == actual)
            {
                return result;
            }

            result = result with { UsedBytes = actual };
        }

        throw Refuse("workspace_agent_recall_output_unavailable");
    }
}
