using System.Text.Json;

using System.Security.Cryptography;
using MetaBrain.Core.Domain;
using MetaBrain.Core.Security;

namespace MetaBrain.Application;

/// <summary>
/// Result of a relation read. A null relation means the relation could not be
/// served to this caller (unknown, unavailable, tampered, or an endpoint
/// outside the caller's authority); every such outcome is the single generic
/// denial, so a read never becomes an existence oracle.
/// </summary>
public sealed record RelationReadResult(Relation? Relation, RelationReview? Review);

/// <summary>
/// Result of a relation write. The review state is computed immediately after
/// the commit so a freshly written link is served with its truthful current
/// state (for example a link to an already-unavailable source) instead of
/// looking current by construction.
/// </summary>
public sealed record RelationWriteResult(Relation Relation, RelationReview Review);

/// <summary>An owner tried to confirm a workspace edge whose pinned evidence needs review.</summary>
public sealed class RelationNeedsReviewException : Exception
{
    public RelationNeedsReviewException()
        : base("The workspace relation pins changed or unavailable evidence and needs review.")
    {
    }
}
public sealed record RelationEndpointCandidate(
    string ResourceId,
    RelationEndpointKind EndpointKind,
    long Revision,
    string? Title);

public sealed record RelationResolveResult(RelationEndpointCandidate[] Candidates, bool HasMore);


/// <summary>
/// S2-T4/S16-T2 typed relation authority over the shared encrypted canonical store.
/// Relations remain canonical resources with the same revision gate and crypto;
/// there is no second link or adjacency truth and no change to key or grant state.
/// Relation reads/writes never expand an approved agent snapshot (R11).
/// Managed endpoints pin live managed revisions. Owner-only workspace endpoints
/// use stable file/folder/passage identities and authenticated retained-content
/// pins. Executors come from trusted context; agent relation identities are minted
/// server-side and agent writes always remain proposals.
/// </summary>
public sealed class RelationService
{
    /// <summary>
    /// Guard rail for the transitive review propagation worklist. The chain is
    /// bounded and each resource is expanded at most once, so this only stops
    /// pathological growth; the rebuildable private index (S2-T7) reads the
    /// relation state this service canonicalizes instead of duplicating it.
    /// </summary>
    private const int MaximumPropagationSteps = 4096;
    private static readonly object RelationMutationGate = new();

    public RelationWriteResult Create(
        AuthenticatedContext context,
        string relationId,
        string zoneId,
        byte[] draftJson,
        IManagedResourceStore store,
        IWorkspacePersistence? workspace = null)
    {
        return Write(context, relationId, zoneId, draftJson, store, workspace, expectedRevision: 0, forceProposed: false);
    }

    /// <summary>
    /// Agent link creation inside an owner-approved grant. The relation ID is
    /// minted server-side (an agent never names the identity it writes), the
    /// owning zone is taken from the subject endpoint's registered zone, and
    /// the link is always recorded as proposed: acceptance is an owner act and
    /// is never inferred from an agent session.
    /// </summary>
    public RelationWriteResult CreateAgentLink(
        AuthenticatedContext context,
        byte[] draftJson,
        IManagedResourceStore store)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(draftJson);

        RelationDraft parsed;
        try
        {
            parsed = RelationCodec.DecodeDraft(draftJson);
        }
        catch (ArgumentException)
        {
            throw;
        }

        if (parsed.From is null)
        {
            throw new ArgumentException("An agent relation draft requires a subject endpoint.");
        }

        if (context.Kind != PrincipalKind.Agent)
        {
            throw new UnauthorizedAccessException("Only the authenticated agent path can create an agent link.");
        }

        var zoneId = store.TryGetZone(parsed.From.ResourceId, out var zone) && !string.IsNullOrEmpty(zone)
            ? zone
            : throw new ArgumentException("The relation subject endpoint has no registered zone.");
        return Write(context, MintRelationId(), zoneId, draftJson, store, workspace: null, expectedRevision: 0, forceProposed: true);
    }

    public RelationWriteResult Accept(
        AuthenticatedContext context,
        string relationId,
        long expectedRevision,
        byte[]? transitionJson,
        IManagedResourceStore store,
        IWorkspacePersistence? workspace = null) =>
        Transition(context, relationId, expectedRevision, RelationState.Accepted, transitionJson, store, workspace);

    public RelationWriteResult Reject(
        AuthenticatedContext context,
        string relationId,
        long expectedRevision,
        byte[]? transitionJson,
        IManagedResourceStore store,
        IWorkspacePersistence? workspace = null) =>
        Transition(context, relationId, expectedRevision, RelationState.Rejected, transitionJson, store, workspace);

    public RelationWriteResult Unlink(
        AuthenticatedContext context,
        string relationId,
        long expectedRevision,
        byte[]? transitionJson,
        IManagedResourceStore store,
        IWorkspacePersistence? workspace = null) =>
        Transition(context, relationId, expectedRevision, RelationState.Retracted, transitionJson, store, workspace);

    /// <summary>
    /// Agent unlink is an explicit, reversible relation action. The caller's
    /// quote is retained as agent-reported evidence; it is never interpreted
    /// as owner confirmation.
    /// </summary>
    public RelationWriteResult UnlinkAgent(
        AuthenticatedContext context,
        string relationId,
        long expectedRevision,
        byte[]? transitionJson,
        IManagedResourceStore store) =>
        Transition(context, relationId, expectedRevision, RelationState.Retracted, transitionJson, store,
            workspace: null, requestAction: RelationRequestAction.Remove);

    /// <summary>
    /// Undo for an agent unlink returns the relation to proposed, never accepted.
    /// </summary>
    public RelationWriteResult RelinkAgent(
        AuthenticatedContext context,
        string relationId,
        long expectedRevision,
        byte[]? transitionJson,
        IManagedResourceStore store) =>
        Transition(context, relationId, expectedRevision, RelationState.Proposed, transitionJson, store,
            workspace: null, requestAction: RelationRequestAction.Restore);


    /// <summary>
    /// Relink: the only way back from an unlink, and deliberately through
    /// proposed so an unlinked link is restored for review instead of being
    /// asserted as accepted again.
    /// </summary>
    public RelationWriteResult Relink(
        AuthenticatedContext context,
        string relationId,
        long expectedRevision,
        byte[]? transitionJson,
        IManagedResourceStore store,
        IWorkspacePersistence? workspace = null) =>
        Transition(context, relationId, expectedRevision, RelationState.Proposed, transitionJson, store, workspace);

    public RelationReadResult Read(
        AuthenticatedContext context,
        string relationId,
        long? expectedRevision,
        IManagedResourceStore store,
        Func<RelationEndpoint, bool>? endpointAuthorizer = null,
        IWorkspacePersistence? workspace = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(store);
        EnsureTrusted(context);
        if (string.IsNullOrEmpty(relationId) || !DomainIdentifiers.IsRelationId(relationId))
        {
            return new RelationReadResult(null, null);
        }

        byte[]? raw = null;
        StoredRelation stored;
        try
        {
            raw = store.ReadContent(relationId, expectedRevision);
            stored = RelationCodec.DecodeStored(raw);
        }
        catch (ArgumentException)
        {
            return new RelationReadResult(null, null);
        }
        catch (IOException)
        {
            return new RelationReadResult(null, null);
        }
        finally
        {
            if (raw is { Length: > 0 })
            {
                CryptographicOperations.ZeroMemory(raw);
            }
        }

        var hasWorkspaceEndpoint =
            RelationEndpointSemantics.IsWorkspace(stored.From.EndpointKind) ||
            RelationEndpointSemantics.IsWorkspace(stored.To.EndpointKind);
        if ((hasWorkspaceEndpoint && (workspace is null || context.Kind == PrincipalKind.Agent)) ||
            (endpointAuthorizer is not null &&
                (!endpointAuthorizer(stored.From) || !endpointAuthorizer(stored.To))))
        {
            return new RelationReadResult(null, null);
        }

        long revision;
        try
        {
            revision = expectedRevision ?? ResolveLiveRevision(store, relationId);
        }
        catch (ManagedResourceUnavailableException)
        {
            return new RelationReadResult(null, null);
        }

        var relations = LoadRelations(store);
        if (workspace is null || context.Kind == PrincipalKind.Agent)
        {
            relations = relations
                .Where(relation => !RelationEndpointSemantics.HasWorkspaceEndpoint(relation))
                .ToArray();
        }
        var workspaceReview = PrepareWorkspaceReviewSnapshot(workspace, relations, stored);

        var review = EvaluateReview(stored, relations, store, endpointAuthorizer, workspaceReview);
        return new RelationReadResult(ToRelation(stored, revision), review);
    }

    /// <summary>
    /// Enumerates relation metadata only when both pinned endpoints pass the
    /// caller's frozen-scope authorizer. The caller must separately authorize
    /// the relation-list operation before invoking this projection.
    /// </summary>
    public RelationReadResult[] List(
        AuthenticatedContext context,
        IManagedResourceStore store,
        Func<RelationEndpoint, bool> endpointAuthorizer,
        IWorkspacePersistence? workspace = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(endpointAuthorizer);
        EnsureTrusted(context);

        var relations = LoadRelations(store);
        if (workspace is null || context.Kind == PrincipalKind.Agent)
        {
            relations = relations
                .Where(relation => !RelationEndpointSemantics.HasWorkspaceEndpoint(relation))
                .ToArray();
        }
        var workspaceReview = PrepareWorkspaceReviewSnapshot(workspace, relations, target: null);

        var liveRevisions = store.ListResources()
            .Where(resource => DomainIdentifiers.IsRelationId(resource.ResourceId))
            .ToDictionary(resource => resource.ResourceId, resource => resource.Revision, StringComparer.Ordinal);
        var results = new List<RelationReadResult>();
        foreach (var stored in relations)
        {
            if (!endpointAuthorizer(stored.From) || !endpointAuthorizer(stored.To) ||
                !liveRevisions.TryGetValue(stored.Id, out var revision))
            {
                continue;
            }

            var review = EvaluateReview(stored, relations, store, endpointAuthorizer, workspaceReview);
            results.Add(new RelationReadResult(ToRelation(stored, revision), review));
        }

        results.Sort((left, right) => StringComparer.Ordinal.Compare(left.Relation!.Id, right.Relation!.Id));
        return results.ToArray();
    }

    /// <summary>
    /// Resolves a caller's short reference over only its frozen memory/source
    /// snapshot. It reads the exact approved revisions, projects titles only,
    /// and never returns source text, body, zone, locator, or unapproved IDs.
    /// </summary>
    public RelationResolveResult ResolveEndpoints(
        AuthenticatedContext context,
        string query,
        IReadOnlyList<ScopeResourceRevision> resources,
        IReadOnlyList<ScopeResourceRevision> destinationResources,
        bool canReadMemory,
        bool canReadSource,
        IManagedResourceStore store)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(destinationResources);
        ArgumentNullException.ThrowIfNull(store);
        EnsureTrusted(context);
        if (string.IsNullOrWhiteSpace(query) || query.Length > 256 || query.Any(char.IsControl))
        {
            throw new ArgumentException("A bounded relation endpoint query is required.");
        }

        var needle = query.Trim();
        var candidates = new List<RelationEndpointCandidate>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var hasMore = false;
        foreach (var resource in resources.Concat(destinationResources))
        {
            if (!DomainIdentifiers.IsDomainIdentifier(resource.ResourceId) ||
                !seen.Add(resource.ResourceId) || (!canReadMemory && !canReadSource))
            {
                continue;
            }

            byte[]? raw = null;
            try
            {
                raw = store.ReadContent(resource.ResourceId, resource.Revision);
                using var document = JsonDocument.Parse(raw);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("id", out var idValue) ||
                    idValue.ValueKind != JsonValueKind.String ||
                    !string.Equals(idValue.GetString(), resource.ResourceId, StringComparison.Ordinal))
                {
                    continue;
                }

                RelationEndpointKind endpointKind;
                if (canReadMemory && root.TryGetProperty("kind", out _))
                {
                    endpointKind = RelationEndpointKind.MemoryItem;
                }
                else if (canReadSource && root.TryGetProperty("availability", out _))
                {
                    endpointKind = RelationEndpointKind.RegisteredSource;
                }
                else
                {
                    continue;
                }

                string? title = root.TryGetProperty("title", out var titleValue) &&
                    titleValue.ValueKind == JsonValueKind.String
                    ? titleValue.GetString()
                    : null;
                if (!resource.ResourceId.Contains(needle, StringComparison.OrdinalIgnoreCase) &&
                    (title is null || !title.Contains(needle, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                if (candidates.Count == 32)
                {
                    hasMore = true;
                    continue;
                }

                candidates.Add(new RelationEndpointCandidate(
                    resource.ResourceId, endpointKind, resource.Revision, title));
            }
            catch (JsonException)
            {
                // An approved opaque resource that is not a readable memory or
                // registered-source envelope is not a resolution candidate.
            }
            catch (ManagedResourceUnavailableException)
            {
                // A moved or unavailable frozen revision is not a live target.
            }
            finally
            {
                if (raw is { Length: > 0 })
                {
                    CryptographicOperations.ZeroMemory(raw);
                }
            }
        }

        return new RelationResolveResult(candidates.ToArray(), hasMore);
    }

    private static RelationWriteResult Write(
        AuthenticatedContext context,
        string relationId,
        string zoneId,
        byte[] draftJson,
        IManagedResourceStore store,
        IWorkspacePersistence? workspace,
        long expectedRevision,
        bool forceProposed)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(store);
        EnsureTrusted(context);
        ArgumentNullException.ThrowIfNull(draftJson);
        if (context.Kind == PrincipalKind.Agent && !forceProposed)
        {
            throw new UnauthorizedAccessException("Agent relation writes must use the scoped proposal path.");
        }

        var draft = RelationCodec.DecodeDraft(draftJson);
        var origin = forceProposed ? RelationOrigin.AgentSuggestion : draft.Origin ?? RelationOrigin.Owner;
        if (!Enum.IsDefined(origin) ||
            origin is RelationOrigin.Legacy or RelationOrigin.Unknown ||
            (!forceProposed && origin == RelationOrigin.AgentSuggestion))
        {
            throw new ArgumentException("A relation origin is invalid for this actor.");
        }

        DateTimeOffset? serverRecordedAtUtc = null;
        if (forceProposed)
        {
            if (context.Kind != PrincipalKind.Agent || string.IsNullOrWhiteSpace(draft.RequestEvidence) ||
                draft.RequestEvidence.Length > RelationCodec.MaximumRequestEvidenceLength)
            {
                throw new ArgumentException("An agent link requires bounded caller-reported request evidence.");
            }

            serverRecordedAtUtc = DateTimeOffset.UtcNow;
            draft = draft with
            {
                State = RelationState.Proposed,
                CreationMethod = CreationMethod.Paraphrase,
                StateReason = null,
                OccurredAtUtc = null,
                RecordedAtUtc = serverRecordedAtUtc,
                Origin = origin
            };
        }
        else if (origin is RelationOrigin.ImportedFileLink or RelationOrigin.AiSuggestion)
        {
            serverRecordedAtUtc = DateTimeOffset.UtcNow;
            draft = draft with
            {
                State = RelationState.Proposed,
                StateReason = null,
                RecordedAtUtc = serverRecordedAtUtc,
                Origin = origin
            };
        }
        else
        {
            draft = draft with { Origin = origin };
        }

        RelationValidator.ValidateDraft(draft, relationId, zoneId);
        var fromDraft = draft.From!;
        var toDraft = draft.To!;
        var hasWorkspaceEndpoint =
            RelationEndpointSemantics.IsWorkspace(fromDraft.EndpointKind) ||
            RelationEndpointSemantics.IsWorkspace(toDraft.EndpointKind);
        if (hasWorkspaceEndpoint && (workspace is null || context.Kind == PrincipalKind.Agent))
        {
            throw new ArgumentException("Workspace relations are owner-only and require the shared workspace store.");
        }

        if (origin == RelationOrigin.ImportedFileLink &&
            fromDraft.EndpointKind is not (RelationEndpointKind.WorkspaceFile or RelationEndpointKind.WorkspacePassage) &&
            toDraft.EndpointKind is not (RelationEndpointKind.WorkspaceFile or RelationEndpointKind.WorkspacePassage))
        {
            throw new ArgumentException("An imported-file relation must name a workspace file or passage.");
        }

        var workspaceTree = hasWorkspaceEndpoint ? workspace!.ReadWorkspaceTree() : null;
        var from = VerifyEndpoint(store, fromDraft, workspace, workspaceTree);
        var to = VerifyEndpoint(store, toDraft, workspace, workspaceTree);
        var kind = draft.Kind!.Value;
        var now = serverRecordedAtUtc ?? DateTimeOffset.UtcNow;
        var recordedAtUtc = hasWorkspaceEndpoint || forceProposed
            ? now
            : draft.RecordedAtUtc ?? now;
        var executor = ExecutorOf(context);
        var requestEvidence = forceProposed
            ? new[]
            {
                new RelationRequestEvidence(
                    kind == RelationKind.Supersedes ? RelationRequestAction.Supersede : RelationRequestAction.Create,
                    draft.RequestEvidence!,
                    executor,
                    now)
            }
            : Array.Empty<RelationRequestEvidence>();
        var history = hasWorkspaceEndpoint
            ? new[]
            {
                new RelationHistoryAct(
                    RelationHistoryAction.Created,
                    draft.State!.Value,
                    executor,
                    draft.CreationMethod!.Value,
                    draft.Reason!,
                    recordedAtUtc,
                    ExpectedRelationRevision: 0)
            }
            : null;
        var stored = new StoredRelation(
            RelationCodec.CurrentSchemaVersion,
            relationId,
            zoneId,
            kind,
            draft.State!.Value,
            from,
            to,
            executor,
            draft.CreationMethod!.Value,
            draft.Reason!,
            draft.StateReason,
            draft.OccurredAtUtc,
            recordedAtUtc,
            executor,
            now,
            requestEvidence,
            origin,
            history);

        lock (RelationMutationGate)
        {
            var relations = LoadRelations(store);
            EnsureNoCycle(relations, kind, from, to, relationId);
            if (hasWorkspaceEndpoint && stored.State == RelationState.Accepted)
            {
                var candidateRelations = relations.Append(stored).ToArray();
                var workspaceReview = PrepareWorkspaceReviewSnapshot(workspace, candidateRelations, stored);
                var review = EvaluateReview(
                    stored, candidateRelations, store, endpointAuthorizer: null, workspaceReview: workspaceReview);
                if (review.State == RelationReviewState.NeedsReview)
                {
                    throw new RelationNeedsReviewException();
                }
            }

            var payload = RelationCodec.EncodeStored(stored);
            try
            {
                var revision = store.WriteContent(relationId, zoneId, payload, expectedRevision);
                return new RelationWriteResult(ToRelation(stored, revision), ReviewOf(stored, store, workspace));
            }
            catch (ManagedResourceUnavailableException ex)
            {
                throw new ArgumentException("The relation write was rejected by vault storage.", ex);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(payload);
            }
        }
    }

    private static RelationWriteResult Transition(
        AuthenticatedContext context,
        string relationId,
        long expectedRevision,
        RelationState target,
        byte[]? transitionJson,
        IManagedResourceStore store,
        IWorkspacePersistence? workspace,
        RelationRequestAction? requestAction = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(store);
        EnsureTrusted(context);
        if (string.IsNullOrEmpty(relationId) || !DomainIdentifiers.IsRelationId(relationId) || expectedRevision < 1)
        {
            throw new ArgumentException("The relation ID and live revision are required.");
        }

        var transition = RelationCodec.DecodeTransitionDraft(transitionJson);
        byte[]? raw = null;
        StoredRelation stored;
        try
        {
            raw = store.ReadContent(relationId, expectedRevision);
            stored = RelationCodec.DecodeStored(raw);
        }
        catch (ArgumentException)
        {
            throw;
        }
        catch (IOException ex)
        {
            throw new ArgumentException("The relation is unavailable.", ex);
        }
        finally
        {
            if (raw is { Length: > 0 })
            {
                CryptographicOperations.ZeroMemory(raw);
            }
        }

        var hasWorkspaceEndpoint = RelationEndpointSemantics.HasWorkspaceEndpoint(stored);
        if (hasWorkspaceEndpoint && (workspace is null || context.Kind == PrincipalKind.Agent))
        {
            throw new UnauthorizedAccessException("Workspace relation actions are owner-only.");
        }

        RelationValidator.ValidateTransition(transition, stored.State, target, requireActReason: hasWorkspaceEndpoint);
        if (requestAction is not null &&
            (context.Kind != PrincipalKind.Agent || string.IsNullOrWhiteSpace(transition.RequestEvidence) ||
                transition.RequestEvidence.Length > RelationCodec.MaximumRequestEvidenceLength))
        {
            throw new ArgumentException("An agent relation transition requires bounded caller-reported request evidence.");
        }

        var now = DateTimeOffset.UtcNow;
        var executor = ExecutorOf(context);
        lock (RelationMutationGate)
        {
            var relations = LoadRelations(store);
            if (target is RelationState.Proposed or RelationState.Accepted &&
                RelationSemantics.ConstrainsCycles(stored.Kind))
            {
                EnsureNoCycle(relations, stored.Kind, stored.From, stored.To, relationId);
            }

            if (hasWorkspaceEndpoint && target == RelationState.Accepted)
            {
                var workspaceReview = PrepareWorkspaceReviewSnapshot(workspace, relations, stored);
                var review = EvaluateReview(stored, relations, store, endpointAuthorizer: null, workspaceReview: workspaceReview);
                if (review.State == RelationReviewState.NeedsReview)
                {
                    throw new RelationNeedsReviewException();
                }
            }

            RelationHistoryAct[]? history = stored.History;
            if (hasWorkspaceEndpoint)
            {
                if (history is null)
                {
                    throw new ArgumentException("The workspace relation history is unavailable.");
                }
                if (history.Length >= RelationCodec.MaximumHistoryEntries)
                {
                    throw new ArgumentException("The workspace relation history reached its safe event limit.");
                }

                history = history.Append(new RelationHistoryAct(
                    HistoryActionFor(target),
                    target,
                    executor,
                    CreationMethod.DirectWrite,
                    transition.ActReason!,
                    now,
                    expectedRevision)).ToArray();
            }

            var next = stored with
            {
                State = target,
                StateReason = transition.StateReason,
                StateExecutor = executor,
                StateRecordedAtUtc = now,
                RequestEvidence = requestAction is null
                    ? stored.RequestEvidence
                    : (stored.RequestEvidence ?? Array.Empty<RelationRequestEvidence>())
                        .Append(new RelationRequestEvidence(requestAction.Value, transition.RequestEvidence!, executor, now))
                        .ToArray(),
                History = history
            };
            var payload = RelationCodec.EncodeStored(next);
            try
            {
                var revision = store.WriteContent(relationId, stored.ZoneId, payload, expectedRevision);
                return new RelationWriteResult(ToRelation(next, revision), ReviewOf(next, store, workspace));
            }
            catch (ManagedResourceUnavailableException ex)
            {
                throw new ArgumentException("The relation write was rejected by vault storage.", ex);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(payload);
            }
        }
    }

    /// <summary>
    /// Review state of a just-written relation, evaluated with full owner
    /// authority: write responses never withhold a flag the caller may act on.
    /// </summary>
    private static RelationReview ReviewOf(
        StoredRelation stored,
        IManagedResourceStore store,
        IWorkspacePersistence? workspace)
    {
        var relations = LoadRelations(store);
        var workspaceReview = PrepareWorkspaceReviewSnapshot(workspace, relations, stored);
        return EvaluateReview(
            stored, relations, store, endpointAuthorizer: null, workspaceReview: workspaceReview);
    }

    private sealed record WorkspaceRelationReviewSnapshot(
        IReadOnlyDictionary<string, WorkspaceNodeInfo> Nodes);

    private static WorkspaceRelationReviewSnapshot? PrepareWorkspaceReviewSnapshot(
        IWorkspacePersistence? workspace,
        IReadOnlyList<StoredRelation> relations,
        StoredRelation? target)
    {
        if (workspace is null ||
            (target is null
                ? !relations.Any(RelationEndpointSemantics.HasWorkspaceEndpoint)
                : !RelationEndpointSemantics.HasWorkspaceEndpoint(target) &&
                    !relations.Any(RelationEndpointSemantics.HasWorkspaceEndpoint)))
        {
            return null;
        }

        var tree = workspace.ReadWorkspaceTree();
        return new WorkspaceRelationReviewSnapshot(
            tree is null
                ? new Dictionary<string, WorkspaceNodeInfo>(StringComparer.Ordinal)
                : tree.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal));
    }

    private static bool TryGetWorkspaceNode(
        WorkspaceRelationReviewSnapshot? snapshot,
        RelationEndpoint endpoint,
        out WorkspaceNodeInfo node)
    {
        if (snapshot is not null &&
            snapshot.Nodes.TryGetValue(endpoint.ResourceId, out node!) &&
            (endpoint.EndpointKind == RelationEndpointKind.WorkspaceFolder
                ? node.Kind == WorkspaceNodeKind.Folder
                : node.Kind == WorkspaceNodeKind.File))
        {
            return true;
        }

        node = null!;
        return false;
    }

    /// <summary>
    /// Verifies a managed endpoint against the encrypted manifest and declared
    /// record kind, or verifies a workspace endpoint against one authenticated
    /// tree snapshot and its retained bytes/pins.
    /// </summary>
    private static RelationEndpoint VerifyEndpoint(
        IManagedResourceStore store,
        RelationEndpoint endpoint,
        IWorkspacePersistence? workspace,
        WorkspaceSnapshot? workspaceTree)
    {
        if (RelationEndpointSemantics.IsWorkspace(endpoint.EndpointKind))
        {
            return VerifyWorkspaceEndpoint(workspace, workspaceTree, endpoint);
        }

        var live = SourceRegistryService.TryGetLiveRevision(store, endpoint.ResourceId);
        if (live is null)
        {
            throw new ArgumentException("A relation endpoint must name a registered resource.");
        }

        if (live.Value != endpoint.Revision)
        {
            throw new ArgumentException("A managed relation endpoint must pin its live revision.");
        }

        var kind = DetectEndpointKind(store, endpoint.ResourceId);
        if (kind != endpoint.EndpointKind)
        {
            throw new ArgumentException("A relation endpoint kind does not match the registered resource.");
        }

        return endpoint;
    }

    private static RelationEndpoint VerifyWorkspaceEndpoint(
        IWorkspacePersistence? workspace,
        WorkspaceSnapshot? tree,
        RelationEndpoint endpoint)
    {
        if (workspace is null || tree is null)
        {
            throw new ArgumentException("The workspace endpoint is unavailable.");
        }

        var node = tree.Nodes.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, endpoint.ResourceId, StringComparison.Ordinal));
        if (node is null)
        {
            throw new ArgumentException("A workspace relation endpoint must name a live node.");
        }

        if (endpoint.EndpointKind == RelationEndpointKind.WorkspaceFolder)
        {
            if (node.Kind != WorkspaceNodeKind.Folder || node.Revision != endpoint.Revision ||
                tree.Revision != endpoint.WorkspacePin!.TreeRevision)
            {
                throw new ArgumentException("A workspace folder endpoint must pin its live node and observed tree revisions.");
            }

            return endpoint;
        }

        if (node.Kind != WorkspaceNodeKind.File ||
            !node.Revisions.Any(revision => revision.Revision == endpoint.Revision))
        {
            throw new ArgumentException("A workspace file or passage endpoint must name a retained file revision.");
        }

        var content = workspace.ReadWorkspaceFile(endpoint.ResourceId, endpoint.Revision);
        try
        {
            var retained = node.Revisions.First(revision => revision.Revision == endpoint.Revision);
            var contentSha256 = Convert.ToHexStringLower(SHA256.HashData(content.Content));
            if (content.Revision != endpoint.Revision ||
                !string.Equals(content.Sha256, retained.Sha256, StringComparison.Ordinal) ||
                !string.Equals(content.Sha256, contentSha256, StringComparison.Ordinal))
            {
                throw new ArgumentException("The workspace endpoint content does not match its authenticated retained revision.");
            }

            if (endpoint.EndpointKind == RelationEndpointKind.WorkspaceFile)
            {
                if (!string.Equals(endpoint.WorkspacePin!.Sha256, contentSha256, StringComparison.Ordinal))
                {
                    throw new ArgumentException("The workspace file endpoint digest does not match the named revision.");
                }
            }
            else
            {
                var span = endpoint.Span!;
                var length = span.End - span.Start;
                if (span.End > content.Content.LongLength || span.Start > int.MaxValue || length > int.MaxValue)
                {
                    throw new ArgumentException("The workspace passage range is outside the named retained content.");
                }

                var passageSha256 = Convert.ToHexStringLower(
                    SHA256.HashData(content.Content.AsSpan((int)span.Start, (int)length)));
                if (!string.Equals(endpoint.WorkspacePin!.Sha256, passageSha256, StringComparison.Ordinal))
                {
                    throw new ArgumentException("The workspace passage digest does not match the exact named bytes.");
                }
            }
        }
        finally
        {
            if (content.Content.Length > 0)
            {
                CryptographicOperations.ZeroMemory(content.Content);
            }
        }

        return endpoint;
    }

    private static RelationEndpointKind DetectEndpointKind(IManagedResourceStore store, string resourceId)
    {
        if (DomainIdentifiers.IsRelationId(resourceId))
        {
            throw new ArgumentException("A relation endpoint cannot be another relation.");
        }

        byte[]? raw = null;
        try
        {
            raw = store.ReadContent(resourceId, null);
            try
            {
                _ = MemoryEnvelopeCodec.DecodeStored(raw);
                return RelationEndpointKind.MemoryItem;
            }
            catch (ArgumentException)
            {
            }

            try
            {
                _ = RegisteredSourceCodec.DecodeStored(raw);
                return RelationEndpointKind.RegisteredSource;
            }
            catch (ArgumentException)
            {
            }

            throw new ArgumentException("A relation endpoint must be a memory item or a registered source.");
        }
        finally
        {
            if (raw is { Length: > 0 })
            {
                CryptographicOperations.ZeroMemory(raw);
            }
        }
    }

    /// <summary>
    /// Rejects derived_from/supersedes cycles over stable logical resource IDs
    /// across every stored relation state. Workspace revisions and passage spans
    /// pin evidence but do not create new graph nodes; a passage maps to its file.
    /// </summary>
    private static void EnsureNoCycle(
        IReadOnlyList<StoredRelation> relations,
        RelationKind kind,
        RelationEndpoint from,
        RelationEndpoint to,
        string relationId)
    {
        if (!RelationSemantics.ConstrainsCycles(kind))
        {
            return;
        }

        var edges = new Dictionary<RelationEndpointIdentity, List<RelationEndpointIdentity>>();
        foreach (var relation in relations)
        {
            if (!RelationSemantics.ConstrainsCycles(relation.Kind) ||
                string.Equals(relation.Id, relationId, StringComparison.Ordinal))
            {
                continue;
            }

            var source = RelationEndpointSemantics.CycleIdentityOf(relation.From);
            if (!edges.TryGetValue(source, out var targets))
            {
                targets = new List<RelationEndpointIdentity>();
                edges.Add(source, targets);
            }

            targets.Add(RelationEndpointSemantics.CycleIdentityOf(relation.To));
        }

        var fromIdentity = RelationEndpointSemantics.CycleIdentityOf(from);
        var pending = new Stack<RelationEndpointIdentity>();
        var seen = new HashSet<RelationEndpointIdentity>();
        pending.Push(RelationEndpointSemantics.CycleIdentityOf(to));
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            if (!seen.Add(node))
            {
                continue;
            }

            if (node == fromIdentity)
            {
                throw new RelationCycleException();
            }

            if (edges.TryGetValue(node, out var targets))
            {
                foreach (var next in targets)
                {
                    pending.Push(next);
                }
            }
        }
    }

    /// <summary>
    /// Read-time review uses content selectors rather than cycle-node identity.
    /// Managed edges preserve their legacy one-sided evidence semantics;
    /// workspace semantic edges except related_to pin and review both file or
    /// passage endpoints, including both sides of contradicts. Nothing is
    /// rewritten: stale pins and dependent evidence are reported as review-needed.
    /// </summary>
    private static RelationReview EvaluateReview(
        StoredRelation target,
        IReadOnlyList<StoredRelation> relations,
        IManagedResourceStore store,
        Func<RelationEndpoint, bool>? endpointAuthorizer,
        WorkspaceRelationReviewSnapshot? workspaceReview)
    {
        var liveRevisions = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var resource in store.ListResources())
        {
            liveRevisions[resource.ResourceId] = resource.Revision;
        }
        var availabilityCache = new Dictionary<string, SourceAvailability?>(StringComparer.Ordinal);
        SourceAvailability? AvailabilityOf(string resourceId, long revision)
        {
            if (availabilityCache.TryGetValue(resourceId, out var cached))
            {
                return cached;
            }

            SourceAvailability? availability = null;
            byte[]? raw = null;
            try
            {
                raw = store.ReadContent(resourceId, revision);
                availability = RegisteredSourceCodec.DecodeStored(raw).Availability;
            }
            catch (ArgumentException)
            {
                // Tampered or non-source endpoint bytes fail closed rather than
                // being read as an availability assertion.
                throw;
            }
            catch (IOException)
            {
                availability = null;
            }
            finally
            {
                if (raw is { Length: > 0 })
                {
                    CryptographicOperations.ZeroMemory(raw);
                }
            }

            availabilityCache[resourceId] = availability;
            return availability;
        }

        var reasons = new Dictionary<string, RelationReviewReason>(StringComparer.Ordinal);
        var affected = new HashSet<RelationEndpointIdentity>();
        var pending = new Queue<StoredRelation>();
        foreach (var relation in relations)
        {
            if (workspaceReview is null && RelationEndpointSemantics.HasWorkspaceEndpoint(relation))
            {
                continue;
            }
            var direct = RelationReviewReason.None;
            for (var endpointIndex = 0; endpointIndex < 2; endpointIndex++)
            {
                var endpoint = endpointIndex == 0 ? relation.From : relation.To;
                var identity = RelationEndpointSemantics.IdentityOf(endpoint);
                if (RelationEndpointSemantics.IsWorkspace(endpoint.EndpointKind))
                {
                    if (!TryGetWorkspaceNode(workspaceReview, endpoint, out var node))
                    {
                        direct |= RelationReviewReason.EndpointMissing;
                        affected.Add(identity);
                    }
                    else if (RelationSemantics.PinsWorkspaceRevision(relation.Kind) &&
                        (endpoint.EndpointKind is RelationEndpointKind.WorkspaceFile or RelationEndpointKind.WorkspacePassage) &&
                        node.ContentRevision != endpoint.Revision)
                    {
                        direct |= RelationReviewReason.EndpointRevisionChanged;
                        affected.Add(identity);
                    }

                    continue;
                }

                if (!liveRevisions.TryGetValue(endpoint.ResourceId, out var live))
                {
                    direct |= RelationReviewReason.EndpointMissing;
                    affected.Add(identity);
                    continue;
                }

                var side = RelationSemantics.EvidenceSide(relation.Kind);
                if (side == RelationEvidenceSide.None ||
                    !RelationEndpointSemantics.IdentityOf(SideEndpoint(relation, side)).Equals(identity))
                {
                    continue;
                }

                if (endpoint.EndpointKind == RelationEndpointKind.RegisteredSource &&
                    AvailabilityOf(endpoint.ResourceId, live) != SourceAvailability.Available)
                {
                    direct |= RelationReviewReason.SourceUnavailable;
                    affected.Add(identity);
                }

                if (RelationSemantics.PinsRevision(relation.Kind) && live != endpoint.Revision)
                {
                    direct |= RelationReviewReason.EndpointRevisionChanged;
                    affected.Add(identity);
                }
            }

            if (direct != RelationReviewReason.None)
            {
                reasons[relation.Id] = direct;
                pending.Enqueue(relation);
            }
        }

        var needsReview = new HashSet<string>(reasons.Keys, StringComparer.Ordinal);
        var steps = 0;
        while (pending.Count > 0 && steps < MaximumPropagationSteps)
        {
            steps++;
            var relation = pending.Dequeue();
            var dependentSide = RelationSemantics.DependentSide(relation.Kind);
            if (dependentSide == RelationEvidenceSide.None)
            {
                continue;
            }

            var dependent = SideEndpoint(relation, dependentSide);
            var dependentIdentity = RelationEndpointSemantics.IdentityOf(dependent);
            if (!affected.Add(dependentIdentity))
            {
                continue;
            }

            foreach (var other in relations)
            {
                if (needsReview.Contains(other.Id) ||
                    (workspaceReview is null && RelationEndpointSemantics.HasWorkspaceEndpoint(other)))
                {
                    continue;
                }

                var otherSide = RelationSemantics.EvidenceSide(other.Kind);
                if (otherSide == RelationEvidenceSide.None ||
                    RelationEndpointSemantics.IdentityOf(SideEndpoint(other, otherSide)) != dependentIdentity)
                {
                    continue;
                }

                needsReview.Add(other.Id);
                reasons.TryGetValue(other.Id, out var existing);
                reasons[other.Id] = existing | RelationReviewReason.DependencyNeedsReview;
                pending.Enqueue(other);
            }
        }

        reasons.TryGetValue(target.Id, out var targetReasons);
        var stale = new List<string>();
        for (var endpointIndex = 0; endpointIndex < 2; endpointIndex++)
        {
            var endpoint = endpointIndex == 0 ? target.From : target.To;

            var identity = RelationEndpointSemantics.IdentityOf(endpoint);
            var unavailable = RelationEndpointSemantics.IsWorkspace(endpoint.EndpointKind)
                ? !TryGetWorkspaceNode(workspaceReview, endpoint, out _)
                : !liveRevisions.ContainsKey(endpoint.ResourceId);
            if (unavailable)
            {
                stale.Add(endpoint.ResourceId);
                continue;
            }

            if (RelationEndpointSemantics.IsWorkspace(endpoint.EndpointKind))
            {
                var contentRevisionChanged = RelationSemantics.PinsWorkspaceRevision(target.Kind) &&
                    (endpoint.EndpointKind is RelationEndpointKind.WorkspaceFile or RelationEndpointKind.WorkspacePassage) &&
                    TryGetWorkspaceNode(workspaceReview, endpoint, out var workspaceNode) &&
                    workspaceNode.ContentRevision != endpoint.Revision;
                var evidenceSide = RelationSemantics.EvidenceSide(target.Kind);
                var dependencyNeedsReview = RelationSemantics.PinsWorkspaceRevision(target.Kind) &&
                    (targetReasons & RelationReviewReason.DependencyNeedsReview) != 0 &&
                    evidenceSide != RelationEvidenceSide.None &&
                    RelationEndpointSemantics.IdentityOf(SideEndpoint(target, evidenceSide)) == identity &&
                    affected.Contains(identity);
                if (contentRevisionChanged || dependencyNeedsReview)
                {
                    stale.Add(endpoint.ResourceId);
                }
            }
            else
            {
                var evidenceSide = RelationSemantics.EvidenceSide(target.Kind);
                if (evidenceSide != RelationEvidenceSide.None &&
                    RelationEndpointSemantics.IdentityOf(SideEndpoint(target, evidenceSide)) == identity &&
                    affected.Contains(identity))
                {
                    stale.Add(endpoint.ResourceId);
                }
            }
        }

        var state = targetReasons == RelationReviewReason.None && stale.Count == 0
            ? RelationReviewState.Current
            : RelationReviewState.NeedsReview;
        var disclosed = endpointAuthorizer is null
            ? stale.Distinct(StringComparer.Ordinal).ToArray()
            : stale
                .Where(resourceId => Disclosable(target, resourceId, endpointAuthorizer))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        return new RelationReview(state, targetReasons, disclosed);
    }

    private static bool Disclosable(
        StoredRelation target,
        string resourceId,
        Func<RelationEndpoint, bool> endpointAuthorizer)
    {
        foreach (var endpoint in new[] { target.From, target.To })
        {
            if (string.Equals(endpoint.ResourceId, resourceId, StringComparison.Ordinal) &&
                endpointAuthorizer(endpoint))
            {
                return true;
            }
        }

        return false;
    }

    private static RelationEndpoint SideEndpoint(StoredRelation relation, RelationEvidenceSide side) => side switch
    {
        RelationEvidenceSide.From => relation.From,
        RelationEvidenceSide.To => relation.To,
        _ => throw new ArgumentException("The relation kind has no evidence side.")
    };

    /// <summary>
    /// Every relation record currently in the vault, resolved from the
    /// encrypted manifest through the reserved relation ID prefix and decoded
    /// under the caller's own operation. Items and sources are never touched.
    /// </summary>
    private static IReadOnlyList<StoredRelation> LoadRelations(IManagedResourceStore store)
    {
        var relations = new List<StoredRelation>();
        foreach (var resource in store.ListResources())
        {
            if (!DomainIdentifiers.IsRelationId(resource.ResourceId))
            {
                continue;
            }

            byte[]? raw = null;
            try
            {
                raw = store.ReadContent(resource.ResourceId, resource.Revision);
                relations.Add(RelationCodec.DecodeStored(raw));
            }
            catch (ArgumentException)
            {
                // A relation-shaped resource that does not decode is tampered
                // state: fail closed instead of skipping it.
                throw;
            }
            catch (IOException)
            {
                continue;
            }
            finally
            {
                if (raw is { Length: > 0 })
                {
                    CryptographicOperations.ZeroMemory(raw);
                }
            }
        }

        return relations;
    }

    internal static string MintRelationId() =>
        DomainIdentifiers.RelationIdPrefix + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    private static RelationHistoryAction HistoryActionFor(RelationState target) => target switch
    {
        RelationState.Accepted => RelationHistoryAction.Accepted,
        RelationState.Rejected => RelationHistoryAction.Rejected,
        RelationState.Retracted => RelationHistoryAction.Retracted,
        RelationState.Proposed => RelationHistoryAction.Restored,
        _ => throw new ArgumentException("The target relation state has no history action.")
    };
    internal static Relation ToRelation(StoredRelation stored, long revision) => new(
        stored.Id,
        stored.ZoneId,
        revision,
        stored.SchemaVersion,
        stored.Kind,
        stored.State,
        stored.From,
        stored.To,
        stored.Executor,
        stored.CreationMethod,
        stored.Reason,
        stored.StateReason,
        stored.OccurredAtUtc,
        stored.RecordedAtUtc,
        stored.StateExecutor,
        stored.StateRecordedAtUtc,
        stored.RequestEvidence,
        stored.Origin,
        stored.History);

    private static long ResolveLiveRevision(IManagedResourceStore store, string relationId)
    {
        var live = store.ListResources().FirstOrDefault(resource =>
            string.Equals(resource.ResourceId, relationId, StringComparison.Ordinal));
        if (live is null)
        {
            throw new ManagedResourceUnavailableException();
        }

        return live.Revision;
    }

    private static ExecutorKind ExecutorOf(AuthenticatedContext context) =>
        context.Kind == PrincipalKind.Agent ? ExecutorKind.Agent : ExecutorKind.Owner;

    private static void EnsureTrusted(AuthenticatedContext actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
    }
}
