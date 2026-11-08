using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MetaBrain.Core.Domain;
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
    string? ModelDirectory,
    string? Purpose = null,
    bool IncludeArchived = false);

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
    double? Similarity,
    WorkspaceRecallHit? Recall = null);

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
    WorkspaceHybridSearchHit[] Hits,
    WorkspaceRecallSummary? Recall = null);

/// <summary>One exact timestamp value, retaining the domain's stated precision.</summary>
public sealed record WorkspaceRecallTemporalStamp(
    string Precision,
    DateTimeOffset? InstantUtc,
    int? LocalYear,
    int? LocalMonth,
    int? LocalDay,
    int? LocalQuarter,
    int? TimeZoneOffsetMinutes);

/// <summary>The existing lifecycle record joined to a cognitive annotation.</summary>
public sealed record WorkspaceRecallLifecycleState(
    string Status,
    long? Revision,
    string? SubjectKind,
    string? Publication,
    string? Question,
    string? Intention,
    bool? Notified,
    DateTimeOffset? NotifiedAtUtc,
    bool? Acknowledged,
    DateTimeOffset? AcknowledgedAtUtc,
    string? SupersededBy,
    WorkspaceRecallTemporalStamp? Occurred,
    WorkspaceRecallTemporalStamp? Recorded,
    WorkspaceRecallTemporalStamp? ValidFrom,
    WorkspaceRecallTemporalStamp? ValidUntil,
    WorkspaceRecallTemporalStamp? ReviewAfter,
    WorkspaceRecallTemporalStamp? IntentionDueAt,
    string? Executor,
    DateTimeOffset? LifecycleRecordedAtUtc);

/// <summary>Stored annotation and owner recall state pinned to one source anchor.</summary>
public sealed record WorkspaceRecallAnnotation(
    string Id,
    long Revision,
    string[] Facets,
    string Kind,
    string Author,
    string Executor,
    string CreationMethod,
    string Evidence,
    string Level,
    string Origin,
    string ReviewStatus,
    string AnchorState,
    string SourceAvailability,
    DateTimeOffset AnnotationRecordedAtUtc,
    string RecallDisposition,
    WorkspaceRecallLifecycleState Lifecycle);

/// <summary>Cognitive evidence and reasons joined to one hybrid candidate.</summary>
public sealed record WorkspaceRecallHit(
    string PurposeEvidenceStatus,
    string[] SelectionReasons,
    WorkspaceRecallAnnotation[] Annotations);

/// <summary>A single file revision region with its globally ranked representative.</summary>
public sealed record WorkspaceRecallRegion(
    string FileId,
    string Path,
    long ContentRevision,
    bool Stale,
    int Total,
    int LiteralTotal,
    int SemanticCandidateTotal,
    int RepresentativeRank,
    string[] SelectionReasons,
    WorkspaceHybridSearchHit Representative);

/// <summary>One bounded page of exact-revision knowledge regions.</summary>
public sealed record WorkspaceRecallSummary(
    string Purpose,
    string SelectionReason,
    int TotalRegions,
    int Offset,
    int Limit,
    bool HasMore,
    WorkspaceRecallRegion[] Regions,
    bool IncludeArchived = false);
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
        AuthenticatedContext context,
        IVaultOperation operation,
        WorkspaceHybridSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(request);
        if (context.Kind != PrincipalKind.Owner)
        {
            throw new UnauthorizedAccessException("Workspace hybrid search is owner-only.");
        }

        IWorkspacePersistence workspace = operation;
        Validate(request);
        var route = request.Purpose is null
            ? null
            : WorkspaceRecallRouter.Resolve(request.Query, request.Purpose);
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
                WorkspaceHybridSemanticStatus.NotApplicableEmptyScope, null, null, [],
                route is null ? null : EmptyRecall(route, request));
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
        var recallSnapshot = route is null
            ? null
            : ReadRecallSnapshot(context, operation, scopeRevisions, cancellationToken);
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

        var recallByCandidate = new Dictionary<CandidateIdentity, WorkspaceRecallHit>();
        if (route is not null && recallSnapshot is not null)
        {
            var selected = new List<WorkspaceHybridCandidate>(candidates.Count);
            FilterRecallCandidates(candidates, recallSnapshot, route, request.IncludeArchived,
                selected, recallByCandidate, cancellationToken);
            candidates = selected;
        }

        var after = workspace.ReadWorkspaceTree();
        if (after is null ||
            after.Revision != snapshot.Revision ||
            !string.Equals(after.WorkspaceId, snapshot.WorkspaceId, StringComparison.Ordinal))
        {
            throw new WorkspacePassageIndexException("workspace_search_snapshot_changed");
        }

        if (recallSnapshot is not null)
        {
            var currentRecallSnapshot = ReadRecallSnapshot(context, operation, scopeRevisions, cancellationToken);
            if (!SameRecallSnapshot(recallSnapshot, currentRecallSnapshot))
            {
                throw new CognitiveAnnotationConflictException();
            }
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
        var pageCandidates = new WorkspaceHybridCandidate[pageLength];
        if (pageLength > 0)
        {
            Array.Copy(ranked, pageStart, pageCandidates, 0, pageLength);
        }

        var regions = route is null ? [] : GroupRecallRegions(ranked);
        var regionStart = Math.Min(offset, regions.Length);
        var regionPageLength = Math.Min(limit, regions.Length - regionStart);
        var evidenceCandidates = new List<WorkspaceHybridCandidate>(pageLength + regionPageLength);
        var evidenceIndices = new HashSet<CandidateIdentity>();
        AddEvidenceCandidates(pageCandidates, evidenceCandidates, evidenceIndices);
        if (regionPageLength > 0)
        {
            for (var index = 0; index < regionPageLength; index++)
            {
                AddEvidenceCandidate(
                    regions[regionStart + index].Representative,
                    evidenceCandidates,
                    evidenceIndices);
            }
        }

        var nodes = snapshot.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        var evidence = BuildPageEvidence(workspace, nodes, evidenceCandidates, cancellationToken);
        var evidenceByCandidate = new Dictionary<CandidateIdentity, string>(evidenceCandidates.Count);
        for (var index = 0; index < evidenceCandidates.Count; index++)
        {
            evidenceByCandidate.Add(CandidateIdentity.Of(evidenceCandidates[index]), evidence[index]);
        }

        var page = new WorkspaceHybridSearchHit[pageLength];
        for (var index = 0; index < pageLength; index++)
        {
            var candidate = pageCandidates[index];
            page[index] = ToSearchHit(
                candidate,
                pageStart + index + 1,
                evidenceByCandidate[CandidateIdentity.Of(candidate)],
                recallByCandidate);
        }

        WorkspaceRecallSummary? recall = null;
        if (route is not null)
        {
            var regionPage = new WorkspaceRecallRegion[regionPageLength];
            for (var index = 0; index < regionPageLength; index++)
            {
                var region = regions[regionStart + index];
                var representative = ToSearchHit(
                    region.Representative,
                    region.RepresentativeRank,
                    evidenceByCandidate[CandidateIdentity.Of(region.Representative)],
                    recallByCandidate);
                regionPage[index] = new WorkspaceRecallRegion(
                    region.Representative.FileId,
                    region.Representative.Path,
                    region.Representative.ContentRevision,
                    region.Representative.Stale,
                    region.Total,
                    region.LiteralTotal,
                    region.Total - region.LiteralTotal,
                    region.RepresentativeRank,
                    representative.Recall?.SelectionReasons ?? [],
                    representative);
            }

            recall = new WorkspaceRecallSummary(
                route.Purpose,
                route.SelectionReason,
                regions.Length,
                offset,
                limit,
                regionStart + regionPageLength < regions.Length,
                regionPage,
                request.IncludeArchived);
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
            page,
            recall);
    }


    private static WorkspaceRecallSummary EmptyRecall(
        WorkspaceRecallRoute route,
        WorkspaceHybridSearchRequest request) =>
        new(route.Purpose, route.SelectionReason, 0, request.Offset, request.Limit, false, [], request.IncludeArchived);

    private static RecallSnapshot ReadRecallSnapshot(
        AuthenticatedContext context,
        IVaultOperation operation,
        IReadOnlyList<ScopeRevision> scopeRevisions,
        CancellationToken cancellationToken)
    {
        var scope = new HashSet<(string FileId, long Revision)>();
        foreach (var item in scopeRevisions)
        {
            scope.Add((item.Node.Id, item.Revision.Revision));
        }

        var annotations = new CognitiveAnnotationService();
        var lifecycle = new LifecycleService();
        var recall = new RecallPolicyService();
        var identities = annotations.List(context, operation);
        var dispositions = recall.List(context, operation)
            .ToDictionary(entry => entry.SubjectResourceId, entry => entry.Disposition, StringComparer.Ordinal);
        var bySource = new Dictionary<(string FileId, long Revision), List<RecallAnnotationState>>();
        var scopedStates = new List<RecallAnnotationState>();
        foreach (var identity in identities)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var annotation = annotations.Read(context, identity.Id, identity.Revision, operation);
            if (annotation is null)
            {
                throw new CognitiveAnnotationConflictException();
            }

            var sourceKey = (annotation.Anchor.FileId, annotation.Anchor.ContentRevision);
            if (!scope.Contains(sourceKey))
            {
                continue;
            }

            var state = new RecallAnnotationState(
                annotation,
                lifecycle.Read(context, annotation.Id, expectedRevision: null, operation),
                dispositions.TryGetValue(annotation.Id, out var disposition)
                    ? disposition
                    : RecallDisposition.Normal);
            scopedStates.Add(state);
            if (!bySource.TryGetValue(sourceKey, out var sourceAnnotations))
            {
                sourceAnnotations = [];
                bySource.Add(sourceKey, sourceAnnotations);
            }

            sourceAnnotations.Add(state);
        }

        return new RecallSnapshot(bySource, scopedStates.ToArray());
    }

    private static bool SameRecallSnapshot(RecallSnapshot left, RecallSnapshot right)
    {
        if (left.States.Length != right.States.Length)
        {
            return false;
        }

        for (var index = 0; index < left.States.Length; index++)
        {
            var before = left.States[index];
            var after = right.States[index];
            if (!string.Equals(before.Annotation.Id, after.Annotation.Id, StringComparison.Ordinal) ||
                before.Annotation.Revision != after.Annotation.Revision ||
                before.RecallDisposition != after.RecallDisposition ||
                before.Lifecycle?.Revision != after.Lifecycle?.Revision)
            {
                return false;
            }
        }

        return true;
    }

    private static void FilterRecallCandidates(
        IReadOnlyList<WorkspaceHybridCandidate> candidates,
        RecallSnapshot snapshot,
        WorkspaceRecallRoute route,
        bool includeArchived,
        List<WorkspaceHybridCandidate> selected,
        Dictionary<CandidateIdentity, WorkspaceRecallHit> recallByCandidate,
        CancellationToken cancellationToken)
    {
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (candidate.ByteOffset is not { } offset ||
                candidate.ByteLength is not { } length ||
                length < 1)
            {
                // Cognitive recall needs an exact passage range; a file name or
                // path is not a cognitive annotation anchor.
                continue;
            }

            var sourceKey = (candidate.FileId, candidate.ContentRevision);
            var anchored = new List<RecallAnnotationState>();
            if (snapshot.BySource.TryGetValue(sourceKey, out var sourceAnnotations))
            {
                foreach (var state in sourceAnnotations)
                {
                    var anchor = state.Annotation.Anchor;
                    if (ContainsRange(anchor.Offset, anchor.Length, offset, length))
                    {
                        anchored.Add(state);
                    }
                }
            }

            if (anchored.Count == 0)
            {
                selected.Add(candidate);
                recallByCandidate[CandidateIdentity.Of(candidate)] =
                    BuildRecallHit(candidate, route, []);
                continue;
            }

            var purposeMatches = new List<RecallAnnotationState>();
            foreach (var state in anchored)
            {
                if (MatchesPurpose(route, state.Annotation))
                {
                    purposeMatches.Add(state);
                }
            }

            if (purposeMatches.Count == 0)
            {
                continue;
            }

            var offered = new List<RecallAnnotationState>(purposeMatches.Count);
            foreach (var state in purposeMatches)
            {
                if (includeArchived || state.RecallDisposition != RecallDisposition.Archived)
                {
                    offered.Add(state);
                }
            }

            if (offered.Count == 0)
            {
                continue;
            }

            selected.Add(candidate);
            recallByCandidate[CandidateIdentity.Of(candidate)] =
                BuildRecallHit(candidate, route, offered);
        }
    }

    private static bool MatchesPurpose(WorkspaceRecallRoute route, CognitiveAnnotation annotation)
    {
        if (route.Facet is not { } facet)
        {
            return true;
        }

        return annotation.Facets.Contains(facet) ||
               (string.Equals(route.Purpose, WorkspaceRecallPurposes.Intention, StringComparison.Ordinal) &&
                annotation.Kind == MemoryKind.Intention);
    }

    private static bool ContainsRange(long outerOffset, long outerLength, long innerOffset, long innerLength) =>
        outerOffset >= 0 &&
        outerLength > 0 &&
        innerOffset >= outerOffset &&
        innerLength > 0 &&
        innerLength <= outerLength &&
        innerOffset - outerOffset <= outerLength - innerLength;

    private static WorkspaceRecallHit BuildRecallHit(
        WorkspaceHybridCandidate candidate,
        WorkspaceRecallRoute route,
        IReadOnlyList<RecallAnnotationState> annotations)
    {
        var reasons = new List<string>(5) { route.SelectionReason };
        if (candidate.IsLiteral)
        {
            reasons.Add(candidate.Stale
                ? "Literal evidence matched a retained source revision; stale status is preserved."
                : "Literal evidence matched the current source revision.");
        }
        else
        {
            reasons.Add(candidate.Stale
                ? "Semantic-only evidence is an unconfirmed candidate from a retained revision."
                : "Semantic-only evidence is an unconfirmed candidate, not supporting evidence or an answer.");
        }

        if (string.Equals(route.Purpose, WorkspaceRecallPurposes.Intention, StringComparison.Ordinal))
        {
            reasons.Add("Only stored prospective lifecycle state is reported; a mention, plan, due reminder, or acknowledgement is not completion.");
        }
        else if (string.Equals(route.Purpose, WorkspaceRecallPurposes.Temporal, StringComparison.Ordinal))
        {
            reasons.Add("Source content dates, annotation record time, and lifecycle occurred/recorded/validity/due stamps are separate; absent event stamps remain unknown.");
        }
        else if (string.Equals(route.Purpose, WorkspaceRecallPurposes.ActionEffect, StringComparison.Ordinal))
        {
            reasons.Add("The stored action/effect association is reported as evidence; it does not establish causality.");
        }
        if (annotations.Count == 0)
        {
            reasons.Add("No owner annotation is pinned to this exact source range; cognitive classification and lifecycle state are unknown.");
        }

        var purposeEvidence = annotations.Count == 0
            ? "unknown"
            : string.Equals(route.Purpose, WorkspaceRecallPurposes.Temporal, StringComparison.Ordinal)
                ? annotations.Any(HasTemporalEvidence) ? "event_time_known" : "event_time_unknown"
                : string.Equals(route.Purpose, WorkspaceRecallPurposes.ActionEffect, StringComparison.Ordinal)
                    ? "annotated_association"
                    : "annotated";
        return new WorkspaceRecallHit(
            purposeEvidence,
            reasons.ToArray(),
            annotations.Select(ToRecallAnnotation).ToArray());
    }

    private static bool HasTemporalEvidence(RecallAnnotationState state)
    {
        var lifecycle = state.Lifecycle;
        return lifecycle is not null &&
               (lifecycle.Occurred is not null ||
                lifecycle.Recorded is not null ||
                lifecycle.ValidFrom is not null ||
                lifecycle.ValidUntil is not null ||
                lifecycle.ReviewAfter is not null ||
                lifecycle.IntentionDueAt is not null ||
                lifecycle.NotifiedAtUtc is not null ||
                lifecycle.AcknowledgedAtUtc is not null);
    }

    private static WorkspaceRecallAnnotation ToRecallAnnotation(RecallAnnotationState state)
    {
        var annotation = state.Annotation;
        var stored = state.Lifecycle;
        var lifecycle = stored is null
            ? new WorkspaceRecallLifecycleState(
                "unknown", null, null, null, null, null, null, null, null, null, null,
                null, null, null, null, null, null, null, null)
            : new WorkspaceRecallLifecycleState(
                "recorded",
                stored.Revision,
                stored.SubjectKind.ToString(),
                stored.Publication.ToString(),
                stored.Question?.ToString(),
                stored.Intention?.ToString(),
                stored.Notified,
                stored.NotifiedAtUtc,
                stored.Acknowledged,
                stored.AcknowledgedAtUtc,
                stored.SupersededBy,
                ToRecallTemporalStamp(stored.Occurred),
                ToRecallTemporalStamp(stored.Recorded),
                ToRecallTemporalStamp(stored.ValidFrom),
                ToRecallTemporalStamp(stored.ValidUntil),
                ToRecallTemporalStamp(stored.ReviewAfter),
                ToRecallTemporalStamp(stored.IntentionDueAt),
                stored.Executor.ToString(),
                stored.RecordedAtUtc);

        return new WorkspaceRecallAnnotation(
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
            annotation.Anchor.State,
            annotation.SourceAvailability.ToString(),
            annotation.RecordedAtUtc,
            state.RecallDisposition.ToString(),
            lifecycle);
    }

    private static WorkspaceRecallTemporalStamp? ToRecallTemporalStamp(TemporalStamp? stamp) =>
        stamp is null
            ? null
            : new WorkspaceRecallTemporalStamp(
                stamp.Precision.ToString(),
                stamp.InstantUtc,
                stamp.LocalYear,
                stamp.LocalMonth,
                stamp.LocalDay,
                stamp.LocalQuarter,
                stamp.TimeZoneOffsetMinutes);

    private static WorkspaceHybridSearchHit ToSearchHit(
        WorkspaceHybridCandidate candidate,
        int rank,
        string evidence,
        IReadOnlyDictionary<CandidateIdentity, WorkspaceRecallHit> recallByCandidate) =>
        new(
            rank,
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
            evidence,
            candidate.AnchorKind,
            candidate.Attribution,
            candidate.ChunkOrdinal,
            candidate.Similarity,
            recallByCandidate.TryGetValue(CandidateIdentity.Of(candidate), out var recall)
                ? recall
                : null);

    private static void AddEvidenceCandidates(
        IReadOnlyList<WorkspaceHybridCandidate> candidates,
        List<WorkspaceHybridCandidate> evidenceCandidates,
        HashSet<CandidateIdentity> evidenceKeys)
    {
        foreach (var candidate in candidates)
        {
            AddEvidenceCandidate(candidate, evidenceCandidates, evidenceKeys);
        }
    }

    private static void AddEvidenceCandidate(
        WorkspaceHybridCandidate candidate,
        List<WorkspaceHybridCandidate> evidenceCandidates,
        HashSet<CandidateIdentity> evidenceKeys)
    {
        if (evidenceKeys.Add(CandidateIdentity.Of(candidate)))
        {
            evidenceCandidates.Add(candidate);
        }
    }

    private static RegionAccumulator[] GroupRecallRegions(IReadOnlyList<WorkspaceHybridCandidate> ranked)
    {
        var byRevision = new Dictionary<(string FileId, long Revision), RegionAccumulator>();
        var regions = new List<RegionAccumulator>();
        for (var index = 0; index < ranked.Count; index++)
        {
            var candidate = ranked[index];
            var key = (candidate.FileId, candidate.ContentRevision);
            if (!byRevision.TryGetValue(key, out var region))
            {
                region = new RegionAccumulator(candidate, index + 1);
                byRevision.Add(key, region);
                regions.Add(region);
            }

            region.Total++;
            if (candidate.IsLiteral)
            {
                region.LiteralTotal++;
            }
        }

        return regions.ToArray();
    }

    private static void Validate(WorkspaceHybridSearchRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Query) ||
            request.Query.Length > MaximumQueryChars ||
            !WorkspaceHybridSearchFields.IsKnown(request.Field) ||
            !WorkspaceHybridSearchCurrent.IsKnown(request.Current) ||
            !WorkspaceRecallPurposes.IsKnown(request.Purpose) ||
            (request.IncludeArchived && request.Purpose is null) ||
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

    private readonly record struct CandidateIdentity(
        string FileId,
        long ContentRevision,
        string Field,
        long? ByteOffset,
        long? ByteLength)
    {
        public static CandidateIdentity Of(WorkspaceHybridCandidate candidate) =>
            new(candidate.FileId, candidate.ContentRevision, candidate.Field, candidate.ByteOffset, candidate.ByteLength);
    }

    private sealed record RecallAnnotationState(
        CognitiveAnnotation Annotation,
        LifecycleRecord? Lifecycle,
        RecallDisposition RecallDisposition);

    private sealed record RecallSnapshot(
        IReadOnlyDictionary<(string FileId, long Revision), List<RecallAnnotationState>> BySource,
        RecallAnnotationState[] States);

    private sealed class RegionAccumulator(WorkspaceHybridCandidate representative, int representativeRank)
    {
        public WorkspaceHybridCandidate Representative { get; } = representative;
        public int RepresentativeRank { get; } = representativeRank;
        public int Total { get; set; }
        public int LiteralTotal { get; set; }
    }

    private sealed record ScopeRevision(
        WorkspaceNodeInfo Node,
        string Path,
        WorkspaceContentRevisionInfo Revision,
        bool IsCurrent);
}
