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
public sealed record RelationEndpointCandidate(
    string ResourceId,
    RelationEndpointKind EndpointKind,
    long Revision,
    string? Title);

public sealed record RelationResolveResult(RelationEndpointCandidate[] Candidates, bool HasMore);


/// <summary>
/// S2-T4 typed relation authority over the shared encrypted canonical store.
/// A relation is an ordinary revision-gated canonical resource: no second link
/// store, no second crypto, no key or grant state is touched here, so adding,
/// revising, or evaluating relations cannot expand an approved snapshot (R11).
/// Identity comes from trusted request params (minted server-side for agent
/// links), executor from the trusted context, revision from the vault
/// descriptor. Endpoints pin the exact revision the link was made against and
/// are verified to exist at that live revision when the link is written, so a
/// relation can never be created against a missing or already-changed endpoint.
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

    public RelationWriteResult Create(
        AuthenticatedContext context,
        string relationId,
        string zoneId,
        byte[] draftJson,
        IManagedResourceStore store)
    {
        return Write(context, relationId, zoneId, draftJson, store, expectedRevision: 0, forceProposed: false);
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
        return Write(context, MintRelationId(), zoneId, draftJson, store, expectedRevision: 0, forceProposed: true);
    }

    public RelationWriteResult Accept(
        AuthenticatedContext context,
        string relationId,
        long expectedRevision,
        byte[]? transitionJson,
        IManagedResourceStore store) =>
        Transition(context, relationId, expectedRevision, RelationState.Accepted, transitionJson, store);

    public RelationWriteResult Reject(
        AuthenticatedContext context,
        string relationId,
        long expectedRevision,
        byte[]? transitionJson,
        IManagedResourceStore store) =>
        Transition(context, relationId, expectedRevision, RelationState.Rejected, transitionJson, store);

    public RelationWriteResult Unlink(
        AuthenticatedContext context,
        string relationId,
        long expectedRevision,
        byte[]? transitionJson,
        IManagedResourceStore store) =>
        Transition(context, relationId, expectedRevision, RelationState.Retracted, transitionJson, store);

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
            RelationRequestAction.Remove);

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
            RelationRequestAction.Restore);


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
        IManagedResourceStore store) =>
        Transition(context, relationId, expectedRevision, RelationState.Proposed, transitionJson, store);

    public RelationReadResult Read(
        AuthenticatedContext context,
        string relationId,
        long? expectedRevision,
        IManagedResourceStore store,
        Func<RelationEndpoint, bool>? endpointAuthorizer = null)
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

        // Every endpoint of the relation must be inside the caller's authority
        // before the record is served; a cross-zone or ungranted endpoint is a
        // generic denial, never a partial projection.
        if (endpointAuthorizer is not null)
        {
            foreach (var endpoint in new[] { stored.From, stored.To })
            {
                if (!endpointAuthorizer(endpoint))
                {
                    return new RelationReadResult(null, null);
                }
            }
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
        var review = EvaluateReview(stored, relations, store, endpointAuthorizer);
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
        Func<RelationEndpoint, bool> endpointAuthorizer)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(endpointAuthorizer);
        EnsureTrusted(context);

        var relations = LoadRelations(store);
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

            var review = EvaluateReview(stored, relations, store, endpointAuthorizer);
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
        long expectedRevision,
        bool forceProposed)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(store);
        EnsureTrusted(context);
        ArgumentNullException.ThrowIfNull(draftJson);

        RelationDraft draft;
        try
        {
            draft = RelationCodec.DecodeDraft(draftJson);
        }
        catch (ArgumentException)
        {
            throw;
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
                RecordedAtUtc = serverRecordedAtUtc
            };
        }

        RelationValidator.ValidateDraft(draft, relationId, zoneId);
        var kind = draft.Kind!.Value;

        // Endpoints must exist right now, at the live revision the link pins,
        // and with the declared kind. This is also what keeps spans anchored to
        // registered sources only.
        var from = VerifyEndpoint(store, draft.From!);
        var to = VerifyEndpoint(store, draft.To!);

        var relations = LoadRelations(store);
        EnsureNoCycle(relations, kind, from.ResourceId, to.ResourceId, relationId);

        var now = serverRecordedAtUtc ?? DateTimeOffset.UtcNow;
        var recordedAtUtc = forceProposed ? now : draft.RecordedAtUtc ?? now;
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
            requestEvidence);
        var payload = RelationCodec.EncodeStored(stored);
        try
        {
            // Revision-gated exactly like canonical memory writes: 0 creates
            // only when absent, N revises only the live revision N; a stale
            // concurrent transition fails closed and publishes nothing.
            var revision = store.WriteContent(relationId, zoneId, payload, expectedRevision);
            return new RelationWriteResult(ToRelation(stored, revision), ReviewOf(stored, store));
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

    private static RelationWriteResult Transition(
        AuthenticatedContext context,
        string relationId,
        long expectedRevision,
        RelationState target,
        byte[]? transitionJson,
        IManagedResourceStore store,
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

        RelationValidator.ValidateTransition(transition, stored.State, target);
        if (requestAction is not null &&
            (context.Kind != PrincipalKind.Agent || string.IsNullOrWhiteSpace(transition.RequestEvidence) ||
                transition.RequestEvidence.Length > RelationCodec.MaximumRequestEvidenceLength))
        {
            throw new ArgumentException("An agent relation transition requires bounded caller-reported request evidence.");
        }

        // Endpoints and kind are immutable, but the derivation graph around
        // them can have changed: an edge that becomes active again (accept or
        // relink) is re-checked against the current graph.
        if (target is RelationState.Proposed or RelationState.Accepted &&
            RelationSemantics.ConstrainsCycles(stored.Kind))
        {
            var relations = LoadRelations(store);
            EnsureNoCycle(relations, stored.Kind, stored.From.ResourceId, stored.To.ResourceId, relationId);
        }

        var now = DateTimeOffset.UtcNow;
        var next = stored with
        {
            State = target,
            StateReason = transition.StateReason,
            StateExecutor = ExecutorOf(context),
            StateRecordedAtUtc = now,
            RequestEvidence = requestAction is null
                ? stored.RequestEvidence
                : (stored.RequestEvidence ?? Array.Empty<RelationRequestEvidence>())
                    .Append(new RelationRequestEvidence(requestAction.Value, transition.RequestEvidence!, ExecutorOf(context), now))
                    .ToArray()
        };
        var payload = RelationCodec.EncodeStored(next);
        try
        {
            var revision = store.WriteContent(relationId, stored.ZoneId, payload, expectedRevision);
            return new RelationWriteResult(ToRelation(next, revision), ReviewOf(next, store));
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

    /// <summary>
    /// Review state of a just-written relation, evaluated with full owner
    /// authority: write responses never withhold a flag the caller may act on.
    /// </summary>
    private static RelationReview ReviewOf(StoredRelation stored, IManagedResourceStore store) =>
        EvaluateReview(stored, LoadRelations(store), store, endpointAuthorizer: null);

    /// <summary>
    /// Verifies an endpoint against live state: the resource must exist in the
    /// encrypted manifest, the link must pin its current revision, it must
    /// decode as the declared kind (memory item or registered source, never a
    /// relation or a raw resource), and a span requires a registered source.
    /// </summary>
    private static RelationEndpoint VerifyEndpoint(IManagedResourceStore store, RelationEndpoint endpoint)
    {
        var live = SourceRegistryService.TryGetLiveRevision(store, endpoint.ResourceId);
        if (live is null)
        {
            throw new ArgumentException("A relation endpoint must name a registered resource.");
        }

        if (live.Value != endpoint.Revision)
        {
            throw new ArgumentException("A relation endpoint must pin the live revision.");
        }

        var kind = DetectEndpointKind(store, endpoint.ResourceId);
        if (kind != endpoint.EndpointKind)
        {
            throw new ArgumentException("A relation endpoint kind does not match the registered resource.");
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
    /// Rejects `derived_from`/`supersedes` cycles, including mixed chains:
    /// both kinds are derivation/replacement edges over one DAG, so adding an
    /// edge whose object can already reach the subject is refused. other kinds,
    /// including `related_to`, may carry cycles.
    /// </summary>
    private static void EnsureNoCycle(
        IReadOnlyList<StoredRelation> relations,
        RelationKind kind,
        string fromResourceId,
        string toResourceId,
        string relationId)
    {
        if (!RelationSemantics.ConstrainsCycles(kind))
        {
            return;
        }

        var edges = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var relation in relations)
        {
            if (!RelationSemantics.ConstrainsCycles(relation.Kind) ||
                string.Equals(relation.Id, relationId, StringComparison.Ordinal))
            {
                continue;
            }

            if (!edges.TryGetValue(relation.From.ResourceId, out var targets))
            {
                targets = new List<string>();
                edges[relation.From.ResourceId] = targets;
            }

            targets.Add(relation.To.ResourceId);
        }

        var pending = new Stack<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        pending.Push(toResourceId);
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            if (!seen.Add(node))
            {
                continue;
            }

            if (string.Equals(node, fromResourceId, StringComparison.Ordinal))
            {
                throw new RelationCycleException();
            }

            if (edges.TryGetValue(node, out var targets))
            {
                foreach (var target in targets)
                {
                    pending.Push(target);
                }
            }
        }
    }

    /// <summary>
    /// Read-time review evaluation over live state. A relation needs review
    /// when an endpoint is missing, when the cited evidence endpoint moved to a
    /// new revision, when the cited registered source is not available, or when
    /// the evidence endpoint is itself the dependent side of a relation that
    /// needs review (bounded transitive propagation). Nothing is rewritten and
    /// no link is dropped: the stored record stays the truthful history and the
    /// computed state says how far it can still be trusted.
    /// </summary>
    private static RelationReview EvaluateReview(
        StoredRelation target,
        IReadOnlyList<StoredRelation> relations,
        IManagedResourceStore store,
        Func<RelationEndpoint, bool>? endpointAuthorizer)
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
        var affected = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<StoredRelation>();

        foreach (var relation in relations)
        {
            var direct = RelationReviewReason.None;
            foreach (var endpoint in new[] { relation.From, relation.To })
            {
                if (!liveRevisions.ContainsKey(endpoint.ResourceId))
                {
                    direct |= RelationReviewReason.EndpointMissing;
                    affected.Add(endpoint.ResourceId);
                }
            }

            var side = RelationSemantics.EvidenceSide(relation.Kind);
            if (side != RelationEvidenceSide.None)
            {
                var evidence = SideEndpoint(relation, side);
                if (liveRevisions.TryGetValue(evidence.ResourceId, out var live))
                {
                    if (evidence.EndpointKind == RelationEndpointKind.RegisteredSource &&
                        AvailabilityOf(evidence.ResourceId, live) != SourceAvailability.Available)
                    {
                        direct |= RelationReviewReason.SourceUnavailable;
                        affected.Add(evidence.ResourceId);
                    }

                    if (RelationSemantics.PinsRevision(relation.Kind) && live != evidence.Revision)
                    {
                        direct |= RelationReviewReason.EndpointRevisionChanged;
                        affected.Add(evidence.ResourceId);
                    }
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

            var dependent = SideEndpoint(relation, dependentSide).ResourceId;
            if (!affected.Add(dependent))
            {
                // Already handled as a directly changed or missing endpoint:
                // relations citing it are already flagged directly.
                continue;
            }

            foreach (var other in relations)
            {
                if (needsReview.Contains(other.Id))
                {
                    continue;
                }

                var otherSide = RelationSemantics.EvidenceSide(other.Kind);
                if (otherSide == RelationEvidenceSide.None ||
                    !string.Equals(SideEndpoint(other, otherSide).ResourceId, dependent, StringComparison.Ordinal))
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
        foreach (var endpoint in new[] { target.From, target.To })
        {
            if (!liveRevisions.ContainsKey(endpoint.ResourceId))
            {
                stale.Add(endpoint.ResourceId);
            }
        }

        var evidenceSide = RelationSemantics.EvidenceSide(target.Kind);
        if (evidenceSide != RelationEvidenceSide.None)
        {
            var evidence = SideEndpoint(target, evidenceSide);
            if (affected.Contains(evidence.ResourceId))
            {
                stale.Add(evidence.ResourceId);
            }
        }

        var state = targetReasons == RelationReviewReason.None && stale.Count == 0
            ? RelationReviewState.Current
            : RelationReviewState.NeedsReview;

        // Only endpoints of the served relation are ever named, and only when
        // the caller is authorized for them: chain members outside the
        // caller's authority contribute the flag but never an identity.
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
        stored.RequestEvidence);

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
