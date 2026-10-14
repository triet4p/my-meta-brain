using System.Security.Cryptography;
using System.Text;
using MetaBrain.Core.Domain;
using MetaBrain.Core.Index;
using MetaBrain.Core.Security;

namespace MetaBrain.Application;

public sealed record ServiceRequest(
    int ProtocolVersion,
    string Operation,
    string? ResourceId = null,
    string? ZoneId = null,
    string? Passphrase = null,
    string? RecoveryCode = null,
    string? ContentBase64 = null,
    string[]? ResourceIds = null,
    string[]? Operations = null,
    string[]? DestinationResourceIds = null,
    DateTimeOffset? ExpiresAtUtc = null,
    string? Provider = null,
    string? Model = null,
    decimal? MaximumCostUsd = null,
    string? PreviewId = null,
    string? CollectionId = null,
    string? Token = null,
    string? SessionToken = null,
    string? SessionId = null,
    string? AccessOperation = null,
    long? ResourceRevision = null,
    string? DestinationResourceId = null,
    long? DestinationRevision = null,
    decimal? EstimatedCostUsd = null,
    long? InputTokens = null,
    long? OutputTokens = null,
    string? CatalogId = null,
    string? Label = null,
    string? Description = null,
    string? Query = null,
    string[]? CatalogIds = null,
    string? Purpose = null,
    string? AgentId = null,
    string? AgentContext = null,
    string? DisclosureContext = null,
    string? RequestId = null,
    string? SourceLocator = null,
    string? SourceFormat = null,
    int? SourceFormatVersion = null,
    string? SourceTitle = null,
    string? SourceAuthor = null,
    DateTimeOffset? SourceCapturedAtUtc = null,
    long? SourceGeneration = null,
    DateTimeOffset? SnapshotRetainUntilUtc = null);
public sealed record OwnerScopeGrantSummary(
    string GrantId,
    ScopeResourceRevision[] Resources,
    string[] Operations,
    ScopeResourceRevision[] DestinationResources,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    ScopeGrantEgress? Egress,
    long PolicyGeneration);

public sealed record OwnerScopeCollectionSummary(string CollectionId, string[] ResourceIds);

public sealed record OwnerAccessRequestSummary(
    string RequestId,
    string[] CatalogIds,
    string Purpose,
    string? AgentId,
    string? AgentContext,
    string? Provider,
    string? Model,
    string? DisclosureContext,
    string[] RequestedOperations,
    ScopeResourceRevision[] ResolvedResources,
    DateTimeOffset CreatedAtUtc,
    string Status,
    string? ApprovedGrantId,
    DateTimeOffset? DecidedAtUtc);

public sealed record MemoryItemWire(
    string Id,
    string ZoneId,
    long Revision,
    int SchemaVersion,
    string Kind,
    string Author,
    string Executor,
    string CreationMethod,
    string Evidence,
    string Level,
    string? Title,
    string Body,
    Core.Domain.MemorySourceReference[] Sources,
    DateTimeOffset? OccurredAtUtc,
    DateTimeOffset RecordedAtUtc,
    Core.Domain.MemoryApplicability? Applicability);

/// <summary>
/// Registered source metadata as served to its granted audience. The wire
/// carries the declared origin role (<see cref="RegisteredSourceWire.Author"/>,
/// "unknown" when the registrant omitted it) but never the captured research
/// text: note content stays owner-only on the research read surface, while
/// this wire is the registry metadata contract shared by owner `source.read`
/// and scoped `agent.source.read`.
/// </summary>
public sealed record RegisteredSourceWire(
    string Id,
    string ZoneId,
    long Revision,
    int SchemaVersion,
    string Executor,
    string CreationMethod,
    string Author,
    string? Title,
    string? Locator,
    string? Version,
    string Availability,
    DateTimeOffset? CapturedAtUtc,
    DateTimeOffset RecordedAtUtc);

public sealed record RelationEndpointWire(
    string ResourceId,
    string EndpointKind,
    long Revision,
    Core.Domain.SourceSpan? Span);

public sealed record RelationReviewWire(
    string State,
    string[] Reasons,
    string[] StaleResourceIds);

public sealed record RelationWire(
    string Id,
    string ZoneId,
    long Revision,
    int SchemaVersion,
    string Kind,
    string State,
    RelationEndpointWire From,
    RelationEndpointWire To,
    string Executor,
    string CreationMethod,
    string Reason,
    string? StateReason,
    DateTimeOffset? OccurredAtUtc,
    DateTimeOffset RecordedAtUtc,
    string StateExecutor,
    DateTimeOffset StateRecordedAtUtc,
    RelationReviewWire Review);

public sealed record LifecycleStampWire(
    string Precision,
    DateTimeOffset? InstantUtc,
    int? LocalYear,
    int? LocalMonth,
    int? LocalDay,
    int? LocalQuarter,
    int? TimeZoneOffsetMinutes);

public sealed record LifecycleChangeWire(
    string Aspect,
    string Act,
    string FromState,
    string ToState,
    string Executor,
    DateTimeOffset RecordedAtUtc,
    string? Rationale,
    string? ReplacementResourceId);

public sealed record LifecycleRetainedRevisionWire(
    long SubjectRevision,
    string ArchiveResourceId,
    string Executor,
    DateTimeOffset RecordedAtUtc);

public sealed record LifecycleWire(
    string Id,
    string ZoneId,
    long Revision,
    int SchemaVersion,
    string SubjectResourceId,
    string SubjectKind,
    string Publication,
    string? Question,
    string? Intention,
    bool Notified,
    DateTimeOffset? NotifiedAtUtc,
    bool Acknowledged,
    DateTimeOffset? AcknowledgedAtUtc,
    string? SupersededBy,
    LifecycleStampWire? Occurred,
    LifecycleStampWire? Recorded,
    LifecycleStampWire? ValidFrom,
    LifecycleStampWire? ValidUntil,
    LifecycleStampWire? ReviewAfter,
    string Executor,
    string CreationMethod,
    string? Reason,
    DateTimeOffset RecordedAtUtc,
    LifecycleChangeWire[] History,
    LifecycleRetainedRevisionWire[] RetainedRevisions);

/// <summary>
/// One retained historical revision read back through the archive resource:
/// the archived envelope, the archive resource that holds it, and both the
/// historical subject revision and the archive's own vault revision.
/// </summary>
public sealed record LifecycleRevisionWire(
    string ArchiveResourceId,
    long ArchiveRevision,
    long SubjectRevision,
    MemoryItemWire Subject);

/// <summary>
/// One materialized private-index row, projected from the canonical encrypted
/// envelope. No body, title, locator, embedding or grant metadata crosses the
/// wire: the row carries identity, revision, zone scope, the record's own
/// state, and the exact canonical IDs it references.
/// </summary>
public sealed record PrivateIndexRowWire(
    string ResourceId,
    string ZoneId,
    long Revision,
    int CanonicalSchemaVersion,
    string RecordKind,
    string State,
    string[] LinkedResourceIds);

/// <summary>
/// Owner-facing view of one enrollment: mapping, declared format/version for
/// dispatch, generation and life state. The private locator is deliberately
/// absent from the wire: the owner supplied it, and it stays inside the
/// encrypted state.
/// </summary>
public sealed record SourceEnrollmentWire(
    string SourceId,
    string ZoneId,
    string Format,
    int FormatVersion,
    long Generation,
    string Life,
    long AnchorLength,
    DateTimeOffset EnrolledAtUtc,
    DateTimeOffset? RevokedAtUtc,
    SourceQuarantineWire? Quarantine,
    string Availability,
    DateTimeOffset? AvailabilityObservedAtUtc,
    DateTimeOffset? RelocatedAtUtc,
    long? SourceReferenceRevision,
    DateTimeOffset? PausedAtUtc = null,
    bool RefreshOptIn = false,
    long JournalCiphertextBytes = 0,
    long SnapshotCiphertextBytes = 0,
    long SnapshotRevision = 0);

public sealed record SourceQuarantineWire(string Reason, string Detail, DateTimeOffset RecordedAtUtc);

/// <summary>
/// Persisted checkpoint summary for the current generation. No event body,
/// attribution or span crosses this wire; the owner sees counts and offsets,
/// and content stays in the encrypted journal.
/// </summary>
public sealed record SourceIngestionWire(
    string SourceId,
    long JournalRevision,
    long ConsumedOffset,
    int EventCount,
    int DiagnosticCount,
    DateTimeOffset? LastIngestAtUtc);

/// <summary>
/// One S3-T6 refresh entry: the source, its refresh status, the checkpoint
/// movement of that pass and the diagnostic code when one stopped the run.
/// No transcript content, locator or secret crosses this wire (R36).
/// </summary>
public sealed record SourceRefreshWire(
    string SourceId,
    string Status,
    long ConsumedOffset,
    int EventsAdded,
    int DiagnosticsAdded,
    string? DiagnosticCode);
public sealed record SourceSnapshotWire(
    string Id,
    string ZoneId,
    long Revision,
    string SourceId,
    long SourceRevision,
    long SourceGeneration,
    long SourceLength,
    string SourceDigest,
    DateTimeOffset CapturedAtUtc,
    DateTimeOffset RetainUntilUtc,
    bool Purged,
    string? ContentBase64);

public sealed record SourceSnapshotDescriptorWire(
    string Id,
    string ZoneId,
    long Revision,
    long Generation);


/// <summary>
/// Owner-only private-index state. <see cref="Rows"/> is a bounded sample of
/// the materialized generation (<see cref="RowCount"/> is the true total) so a
/// large vault cannot grow a pipe reply without limit; <see cref="Stale"/>
/// reports whether the materialized generation still equals the live canonical
/// resource/revision set, and <see cref="CanonicalMoved"/> reports a rebuild
/// that failed closed instead of publishing an unprovable generation.
/// </summary>
public sealed record PrivateIndexWire(
    int SchemaVersion,
    string CanonicalDigest,
    int RowCount,
    bool Stale,
    bool CanonicalMoved,
    PrivateIndexRowWire[] Rows);

public sealed record ServiceReply(
    int ProtocolVersion,
    string? Status,
    string? Principal,
    string? Error,
    AuthorizationDecision? Decision = null,
    long? PolicyGeneration = null,
    string? ContentBase64 = null,
    string? ContentType = null,
    string? VaultState = null,
    string? RecoveryCode = null,
    long? ResourceRevision = null,
    OwnerScopeGrantPreview? ScopePreview = null,
    OwnerScopeGrantSummary[]? ScopeGrants = null,
    OwnerScopeCollectionSummary[]? ScopeCollections = null,
    string? CollectionId = null,
    string? GrantId = null,
    string? Token = null,
    string? SessionToken = null,
    AgentSessionSnapshot? AgentSession = null,
    AgentSessionSnapshot[]? AgentSessions = null,
    string? SessionId = null,
    OwnerCatalogPreview? CatalogPreview = null,
    OwnerCatalogEntry[]? CatalogEntries = null,
    PublishedCatalogEntry[]? PublishedCatalog = null,
    string? CatalogId = null,
    OwnerAccessRequestSummary[]? AccessRequests = null,
    OwnerAccessRequestSummary? AccessRequest = null,
    OwnerAccessRequestPreview? AccessRequestPreview = null,
    AgentCollectionView? CollectionView = null,
    string? RequestId = null,
    MemoryItemWire? MemoryItem = null,
    RegisteredSourceWire? Source = null,
    RelationWire? Relation = null,
    LifecycleWire? Lifecycle = null,
    LifecycleRevisionWire? RetainedRevision = null,
    PrivateIndexWire? PrivateIndex = null,
    SourceEnrollmentWire? SourceEnrollment = null,
    SourceIngestionWire? SourceIngestion = null,
    SourceEnrollmentWire[]? SourceEnrollments = null,
    SourceRefreshWire[]? SourceRefresh = null,
    SourceSnapshotWire? SourceSnapshot = null,
    SourceSnapshotDescriptorWire[]? SourceSnapshots = null);


public sealed class ServiceRequestHandler
{
    public const int CurrentProtocolVersion = 1;
    public const int MaximumIndexRows = 512;
    public const string ResourceReadOperation = "resource.read";
    public const string SourceReadOperation = "source.read";
    public const string ResourceWriteOperation = "resource.write";
    public const string OwnerScopePreviewOperation = "owner.scope.preview";
    public const string OwnerScopeIssueOperation = "owner.scope.issue";
    public const string OwnerScopeListOperation = "owner.scope.list";
    public const string OwnerScopeCollectionSetOperation = "owner.scope.collection.set";
    public const string OwnerScopeCollectionListOperation = "owner.scope.collection.list";
    public const string OwnerCatalogPreviewOperation = "owner.catalog.preview";
    public const string OwnerCatalogPublishOperation = "owner.catalog.publish";
    public const string OwnerCatalogListOperation = "owner.catalog.list";
    public const string OwnerCatalogWithdrawOperation = "owner.catalog.withdraw";
    public const string CatalogListOperation = "catalog.list";
    public const string CatalogQueryOperation = "catalog.query";
    public const string AgentAccessRequestOperation = "agent.access.request";
    public const string AgentAccessRequestStatusOperation = "agent.access.request.status";
    public const string OwnerAccessRequestListOperation = "owner.access.requests";
    public const string OwnerAccessRequestPreviewOperation = "owner.access.request.preview";
    public const string OwnerAccessRequestApproveOperation = "owner.access.request.approve";
    public const string OwnerAccessRequestRejectOperation = "owner.access.request.reject";
    public const string AgentRedeemOperation = "agent.session.redeem";
    public const string AgentSessionInspectOperation = "agent.session.inspect";
    public const string AgentAccessAuthorizeOperation = "agent.access.authorize";
    public const string AgentResourceReadOperation = "agent.resource.read";
    public const string AgentMemoryReadOperation = "agent.memory.read";
    public const string AgentCollectionViewOperation = "agent.collection.view";
    public const string OwnerSessionListOperation = "owner.sessions";
    public const string OwnerSessionRevokeOperation = "owner.session.revoke";
    public const string MemoryCreateOperation = "memory.create";
    public const string MemoryReviseOperation = "memory.revise";
    public const string MemoryImportOperation = "memory.import";
    public const string MemoryReadOperation = "memory.read";
    public const string SourceCreateOperation = "source.create";
    public const string SourceReviseOperation = "source.revise";
    public const string AgentSourceReadOperation = "agent.source.read";
    public const string AgentSourceSnapshotReadOperation = "agent.source.snapshot.read";
    public const string SourceSnapshotReadOperation = "source.snapshot.read";
    public const string RelationCreateOperation = "relation.create";
    public const string RelationAcceptOperation = "relation.accept";
    public const string RelationRejectOperation = "relation.reject";
    public const string RelationUnlinkOperation = "relation.unlink";
    public const string RelationRelinkOperation = "relation.relink";
    public const string RelationReadOperation = "relation.read";
    public const string AgentLinkCreateOperation = "agent.link.create";
    public const string AgentRelationReadOperation = "agent.relation.read";
    public const string LifecycleCreateOperation = "lifecycle.create";
    public const string LifecycleReadOperation = "lifecycle.read";
    public const string LifecycleRetainedRevisionReadOperation = "lifecycle.retained.read";
    public const string LifecyclePublishOperation = "lifecycle.publish";
    public const string LifecycleRejectProposalOperation = "lifecycle.reject";
    public const string LifecycleWithdrawOperation = "lifecycle.withdraw";
    public const string LifecycleReProposeOperation = "lifecycle.repropose";
    public const string LifecycleResolveQuestionOperation = "lifecycle.question.resolve";
    public const string LifecycleReopenQuestionOperation = "lifecycle.question.reopen";
    public const string LifecycleCompleteIntentionOperation = "lifecycle.intention.complete";
    public const string LifecycleCancelIntentionOperation = "lifecycle.intention.cancel";
    public const string LifecycleSupersedeIntentionOperation = "lifecycle.intention.supersede";
    public const string LifecycleNotifyOperation = "lifecycle.intention.notify";
    public const string LifecycleAcknowledgeOperation = "lifecycle.intention.acknowledge";
    public const string LifecycleSetValidityOperation = "lifecycle.validity.set";
    public const string LifecycleRetainRevisionOperation = "lifecycle.retain";
    public const string OwnerIndexStatusOperation = "owner.index.status";
    public const string OwnerIndexRebuildOperation = "owner.index.rebuild";
    public const string SourceEnrollOperation = "source.enroll";
    public const string SourceRevokeOperation = "source.revoke";
    public const string SourceStatusOperation = "source.status";
    public const string SourceIngestOperation = "source.ingest";
    public const string SourceRelocateOperation = "source.relocate";
    public const string SourcePauseOperation = "source.pause";
    public const string SourceResumeOperation = "source.resume";
    public const string SourceRefreshOptInOperation = "source.refresh.optin";
    public const string SourceRefreshOptOutOperation = "source.refresh.optout";
    public const string SourceListOperation = "source.list";
    public const string SourceRefreshOperation = "source.refresh";
    public const string SourceSnapshotPreserveOperation = "source.snapshot.preserve";
    public const string SourceSnapshotListOperation = "source.snapshot.list";
    public const string SourceSnapshotRetainOperation = "source.snapshot.retain";
    public const string SourceSnapshotPurgeOperation = "source.snapshot.purge";
    public const string ResearchCaptureOperation = "research.capture";
    public const string ResearchReadOperation = "research.read";

    /// <summary>
    /// Owner-grantable agent operation that authorizes creating one link
    /// between an exact subject (source scope) and object (destination scope)
    /// at pinned revisions. The wire operation is
    /// <see cref="AgentLinkCreateOperation"/>; this is the grant operation name
    /// already fixed by the scope authority.
    /// </summary>
    public const string LinkCreateOperation = "link.create";
    private readonly GrantAuthority _grantAuthority;
    private readonly IVaultLifecycle _vault;
    private readonly OwnerScopeGrantAuthority _ownerScopeGrants;
    private readonly AgentSessionAuthority _agentSessions;
    private readonly OwnerCatalogAuthority _ownerCatalog;
    private readonly ICatalogProjectionStore? _projections;
    private readonly PrivateIndexAuthority _privateIndex;
    private readonly IPrivateIndexStore? _privateIndexStore;
    private readonly SourceIngestionCoordinator? _sourceIngestion;
    private readonly ISourceSnapshotReader? _sourceReader;
    private readonly SourceSnapshotService _sourceSnapshots = new();
    private PrivateIndexSnapshot? _privateIndexMaterialized;

    public ServiceRequestHandler(
        GrantAuthority grantAuthority,
        IVaultLifecycle vault,
        OwnerScopeGrantAuthority ownerScopeGrants,
        AgentSessionAuthority? agentSessions = null,
        OwnerCatalogAuthority? ownerCatalog = null,
        ICatalogProjectionStore? projections = null,
        IPrivateIndexStore? privateIndex = null,
        SourceNormalizerRegistry? sourceNormalizers = null,
        ISourceSnapshotReader? sourceReader = null)
    {
        _grantAuthority = grantAuthority ?? throw new ArgumentNullException(nameof(grantAuthority));
        _vault = vault ?? throw new ArgumentNullException(nameof(vault));
        _ownerScopeGrants = ownerScopeGrants ?? throw new ArgumentNullException(nameof(ownerScopeGrants));
        _agentSessions = agentSessions ?? new AgentSessionAuthority();
        _ownerCatalog = ownerCatalog ?? new OwnerCatalogAuthority();
        _projections = projections;
        _privateIndex = new PrivateIndexAuthority();
        _privateIndexStore = privateIndex;
        _sourceReader = sourceReader;
        // S3-T1 source enrollment/ingestion. The registry ships empty (real
        // Codex/OMP parsers attach in S3-T2/S3-T3); without both a registry
        // and a reader the owner operations fail closed instead of serving a
        // coordinator that cannot honor its explicit-diagnostics contract.
        if (sourceNormalizers is not null && sourceReader is not null)
        {
            _sourceIngestion = new SourceIngestionCoordinator(
                new SourceEnrollmentAuthority(), sourceNormalizers, sourceReader);
        }
    }

    public async Task<ServiceReply> HandleAsync(
        AuthenticatedContext context,
        ServiceRequest request,
        IVaultOperation? operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);

        if (request.ProtocolVersion != CurrentProtocolVersion)
        {
            return Error("unsupported_protocol");
        }

        if (string.Equals(request.Operation, "owner.status", StringComparison.Ordinal))
        {
            if (!AuthorizationPolicy.Allows(context, ServicePermission.InspectOwnerStatus))
            {
                return Error("forbidden");
            }

            return new ServiceReply(CurrentProtocolVersion, "running", "owner", null, VaultState: _vault.State);
        }

        if (string.Equals(request.Operation, AgentRedeemOperation, StringComparison.Ordinal))
        {
            return HandleAgentRedeem(context, request, operation);
        }

        if (string.Equals(request.Operation, AgentSessionInspectOperation, StringComparison.Ordinal))
        {
            return HandleAgentSessionInspect(context, request, operation);
        }

        if (string.Equals(request.Operation, AgentResourceReadOperation, StringComparison.Ordinal))
        {
            return HandleAgentResourceRead(context, request, operation);
        }
        if (string.Equals(request.Operation, AgentMemoryReadOperation, StringComparison.Ordinal))
        {
            return HandleAgentMemoryRead(context, request, operation);
        }
        if (string.Equals(request.Operation, AgentCollectionViewOperation, StringComparison.Ordinal))
        {
            return HandleAgentCollectionView(context, request, operation);
        }
        if (string.Equals(request.Operation, AgentSourceReadOperation, StringComparison.Ordinal))
        {
            return HandleAgentSourceRead(context, request, operation);
        }
        if (string.Equals(request.Operation, AgentSourceSnapshotReadOperation, StringComparison.Ordinal))
        {
            return HandleAgentSourceSnapshotRead(context, request, operation);
        }
        if (string.Equals(request.Operation, AgentLinkCreateOperation, StringComparison.Ordinal))
        {
            return HandleAgentLinkCreate(context, request, operation);
        }
        if (string.Equals(request.Operation, AgentRelationReadOperation, StringComparison.Ordinal))
        {
            return HandleAgentRelationRead(context, request, operation);
        }
        if (string.Equals(request.Operation, AgentAccessAuthorizeOperation, StringComparison.Ordinal))
        {
            return HandleAgentAccessAuthorize(context, request, operation);
        }

        if (string.Equals(request.Operation, AgentAccessRequestOperation, StringComparison.Ordinal))
        {
            return HandleAgentAccessRequest(context, request, operation);
        }

        if (string.Equals(request.Operation, AgentAccessRequestStatusOperation, StringComparison.Ordinal))
        {
            return HandleAgentAccessRequestStatus(context, request, operation);
        }

        if (string.Equals(request.Operation, CatalogListOperation, StringComparison.Ordinal))
        {
            if (HasCatalogDiscoveryExtras(request))
            {
                return CatalogDenied();
            }

            return ServeCatalogList();
        }

        if (string.Equals(request.Operation, CatalogQueryOperation, StringComparison.Ordinal))
        {
            return ServeCatalogQuery(request.Query, HasCatalogDiscoveryExtras(request, allowQuery: true));
        }


        if (context.Kind != PrincipalKind.Owner)
        {
            return Error("forbidden");
        }

        if (string.Equals(request.Operation, "vault.provision", StringComparison.Ordinal))
        {
            if (request.Passphrase is not { Length: >= 12 and <= 4096 })
            {
                return Error("credential_required");
            }

            if (!_vault.Provision(request.Passphrase, out var recoveryCode) || recoveryCode is null)
            {
                return Error("vault_already_initialized");
            }

            return new ServiceReply(CurrentProtocolVersion, "provisioned", "owner", null,
                VaultState: _vault.State, RecoveryCode: recoveryCode);
        }

        if (string.Equals(request.Operation, "vault.unlock", StringComparison.Ordinal) ||
            string.Equals(request.Operation, "vault.recover", StringComparison.Ordinal))
        {
            var recovery = string.Equals(request.Operation, "vault.recover", StringComparison.Ordinal);
            var credential = recovery ? request.RecoveryCode : request.Passphrase;
            if (credential is null || credential.Length is 0 or > 4096 || !_vault.Unlock(credential, recovery))
            {
                return Error("unlock_denied");
            }

            // A new unlock starts a new index generation: the previous
            // materialization described a previous unlocked epoch and must not
            // be served after the vault is locked and reopened.
            ResetPrivateIndex();
            return new ServiceReply(CurrentProtocolVersion, "unlocked", "owner", null, VaultState: _vault.State);
        }

        if (string.Equals(request.Operation, "vault.lock", StringComparison.Ordinal))
        {
            await _vault.LockAsync().ConfigureAwait(false);
            _ownerScopeGrants.ClearPreviews();
            _ownerScopeGrants.ClearRequestPreviews();
            _agentSessions.ClearSessions();
            ResetPrivateIndex();
            return new ServiceReply(CurrentProtocolVersion, "locked", "owner", null, VaultState: _vault.State);
        }

        if (string.Equals(request.Operation, OwnerScopePreviewOperation, StringComparison.Ordinal))
        {
            return HandleScopePreview(context, request, operation);
        }

        if (string.Equals(request.Operation, OwnerScopeIssueOperation, StringComparison.Ordinal))
        {
            return HandleScopeIssue(context, request.PreviewId, operation);
        }

        if (string.Equals(request.Operation, OwnerScopeListOperation, StringComparison.Ordinal))
        {
            return HandleScopeList(context, operation);
        }
        if (string.Equals(request.Operation, OwnerScopeCollectionSetOperation, StringComparison.Ordinal))
        {
            return HandleScopeCollectionSet(context, request.CollectionId, request.ResourceIds, operation);
        }

        if (string.Equals(request.Operation, OwnerScopeCollectionListOperation, StringComparison.Ordinal))
        {
            return HandleScopeCollectionList(context, operation);
        }

        if (string.Equals(request.Operation, OwnerCatalogPreviewOperation, StringComparison.Ordinal))
        {
            return HandleCatalogPreview(context, request, operation);
        }

        if (string.Equals(request.Operation, OwnerCatalogPublishOperation, StringComparison.Ordinal))
        {
            return HandleCatalogPublish(context, request, operation);
        }

        if (string.Equals(request.Operation, OwnerCatalogListOperation, StringComparison.Ordinal))
        {
            return HandleCatalogList(context, operation);
        }

        if (string.Equals(request.Operation, OwnerCatalogWithdrawOperation, StringComparison.Ordinal))
        {
            return HandleCatalogWithdraw(context, request, operation);
        }

        if (string.Equals(request.Operation, OwnerAccessRequestListOperation, StringComparison.Ordinal))
        {
            return HandleAccessRequestList(context, operation);
        }

        if (string.Equals(request.Operation, OwnerAccessRequestPreviewOperation, StringComparison.Ordinal))
        {
            return HandleAccessRequestPreview(context, request, operation);
        }

        if (string.Equals(request.Operation, OwnerAccessRequestApproveOperation, StringComparison.Ordinal))
        {
            return HandleAccessRequestApprove(context, request, operation);
        }

        if (string.Equals(request.Operation, OwnerAccessRequestRejectOperation, StringComparison.Ordinal))
        {
            return HandleAccessRequestReject(context, request, operation);
        }

        if (string.Equals(request.Operation, OwnerSessionListOperation, StringComparison.Ordinal))
        {
            return HandleOwnerSessionList(request, operation);
        }

        if (string.Equals(request.Operation, OwnerIndexStatusOperation, StringComparison.Ordinal))
        {
            return HandleIndexStatus(context, request, operation);
        }

        if (string.Equals(request.Operation, OwnerIndexRebuildOperation, StringComparison.Ordinal))
        {
            return HandleIndexRebuild(context, request, operation, cancellationToken);
        }

        if (string.Equals(request.Operation, SourceEnrollOperation, StringComparison.Ordinal) ||
            string.Equals(request.Operation, SourceRelocateOperation, StringComparison.Ordinal) ||
            string.Equals(request.Operation, SourceRevokeOperation, StringComparison.Ordinal) ||
            string.Equals(request.Operation, SourceStatusOperation, StringComparison.Ordinal) ||
            string.Equals(request.Operation, SourceIngestOperation, StringComparison.Ordinal) ||
            string.Equals(request.Operation, SourcePauseOperation, StringComparison.Ordinal) ||
            string.Equals(request.Operation, SourceResumeOperation, StringComparison.Ordinal) ||
            string.Equals(request.Operation, SourceRefreshOptInOperation, StringComparison.Ordinal) ||
            string.Equals(request.Operation, SourceRefreshOptOutOperation, StringComparison.Ordinal) ||
            string.Equals(request.Operation, SourceListOperation, StringComparison.Ordinal) ||
            string.Equals(request.Operation, SourceRefreshOperation, StringComparison.Ordinal))
        {
            return HandleSourceIngestionCommand(context, request, operation, cancellationToken);
        }
        if (string.Equals(request.Operation, SourceSnapshotPreserveOperation, StringComparison.Ordinal) ||
            string.Equals(request.Operation, SourceSnapshotListOperation, StringComparison.Ordinal) ||
            string.Equals(request.Operation, SourceSnapshotReadOperation, StringComparison.Ordinal) ||
            string.Equals(request.Operation, SourceSnapshotRetainOperation, StringComparison.Ordinal) ||
            string.Equals(request.Operation, SourceSnapshotPurgeOperation, StringComparison.Ordinal))
        {
            return HandleSourceSnapshotCommand(context, request, operation);
        }

        if (string.Equals(request.Operation, OwnerSessionRevokeOperation, StringComparison.Ordinal))
        {
            return HandleOwnerSessionRevoke(request, operation);
        }


        if (string.Equals(request.Operation, MemoryCreateOperation, StringComparison.Ordinal))
        {
            return HandleMemoryCreate(context, request, operation);
        }

        if (string.Equals(request.Operation, MemoryReviseOperation, StringComparison.Ordinal))
        {
            return HandleMemoryRevise(context, request, operation);
        }

        if (string.Equals(request.Operation, MemoryImportOperation, StringComparison.Ordinal))
        {
            return HandleMemoryImport(context, request, operation);
        }

        if (string.Equals(request.Operation, MemoryReadOperation, StringComparison.Ordinal))
        {
            return HandleMemoryRead(context, request, operation);
        }

        if (string.Equals(request.Operation, SourceCreateOperation, StringComparison.Ordinal))
        {
            return HandleSourceCreate(context, request, operation);
        }

        if (string.Equals(request.Operation, SourceReviseOperation, StringComparison.Ordinal))
        {
            return HandleSourceRevise(context, request, operation);
        }

        if (string.Equals(request.Operation, SourceReadOperation, StringComparison.Ordinal))
        {
            return HandleSourceRead(context, request, operation);
        }

        if (string.Equals(request.Operation, ResearchCaptureOperation, StringComparison.Ordinal))
        {
            return HandleResearchCapture(context, request, operation);
        }

        if (string.Equals(request.Operation, ResearchReadOperation, StringComparison.Ordinal))
        {
            return HandleResearchRead(context, request, operation);
        }

        if (string.Equals(request.Operation, RelationCreateOperation, StringComparison.Ordinal))
        {
            return HandleRelationCreate(context, request, operation);
        }

        if (string.Equals(request.Operation, RelationAcceptOperation, StringComparison.Ordinal))
        {
            return HandleRelationTransition(context, request, operation, RelationAcceptOperation);
        }

        if (string.Equals(request.Operation, RelationRejectOperation, StringComparison.Ordinal))
        {
            return HandleRelationTransition(context, request, operation, RelationRejectOperation);
        }

        if (string.Equals(request.Operation, RelationUnlinkOperation, StringComparison.Ordinal))
        {
            return HandleRelationTransition(context, request, operation, RelationUnlinkOperation);
        }

        if (string.Equals(request.Operation, RelationRelinkOperation, StringComparison.Ordinal))
        {
            return HandleRelationTransition(context, request, operation, RelationRelinkOperation);
        }

        if (string.Equals(request.Operation, RelationReadOperation, StringComparison.Ordinal))
        {
            return HandleRelationRead(context, request, operation);
        }

        if (string.Equals(request.Operation, LifecycleCreateOperation, StringComparison.Ordinal))
        {
            return HandleLifecycleCreate(context, request, operation);
        }

        if (string.Equals(request.Operation, LifecycleReadOperation, StringComparison.Ordinal))
        {
            return HandleLifecycleRead(context, request, operation);
        }

        if (string.Equals(request.Operation, LifecycleRetainedRevisionReadOperation, StringComparison.Ordinal))
        {
            return HandleLifecycleRetainedRevisionRead(context, request, operation);
        }

        if (string.Equals(request.Operation, LifecyclePublishOperation, StringComparison.Ordinal))
        {
            return HandleLifecycleTransition(context, request, operation, Core.Domain.LifecycleAct.Publish, LifecyclePublishOperation);
        }

        if (string.Equals(request.Operation, LifecycleRejectProposalOperation, StringComparison.Ordinal))
        {
            return HandleLifecycleTransition(context, request, operation, Core.Domain.LifecycleAct.RejectProposal, LifecycleRejectProposalOperation);
        }

        if (string.Equals(request.Operation, LifecycleWithdrawOperation, StringComparison.Ordinal))
        {
            return HandleLifecycleTransition(context, request, operation, Core.Domain.LifecycleAct.Withdraw, LifecycleWithdrawOperation);
        }

        if (string.Equals(request.Operation, LifecycleReProposeOperation, StringComparison.Ordinal))
        {
            return HandleLifecycleTransition(context, request, operation, Core.Domain.LifecycleAct.RePropose, LifecycleReProposeOperation);
        }

        if (string.Equals(request.Operation, LifecycleResolveQuestionOperation, StringComparison.Ordinal))
        {
            return HandleLifecycleTransition(context, request, operation, Core.Domain.LifecycleAct.ResolveQuestion, LifecycleResolveQuestionOperation);
        }

        if (string.Equals(request.Operation, LifecycleReopenQuestionOperation, StringComparison.Ordinal))
        {
            return HandleLifecycleTransition(context, request, operation, Core.Domain.LifecycleAct.ReopenQuestion, LifecycleReopenQuestionOperation);
        }

        if (string.Equals(request.Operation, LifecycleCompleteIntentionOperation, StringComparison.Ordinal))
        {
            return HandleLifecycleTransition(context, request, operation, Core.Domain.LifecycleAct.CompleteIntention, LifecycleCompleteIntentionOperation);
        }

        if (string.Equals(request.Operation, LifecycleCancelIntentionOperation, StringComparison.Ordinal))
        {
            return HandleLifecycleTransition(context, request, operation, Core.Domain.LifecycleAct.CancelIntention, LifecycleCancelIntentionOperation);
        }

        if (string.Equals(request.Operation, LifecycleSupersedeIntentionOperation, StringComparison.Ordinal))
        {
            return HandleLifecycleTransition(context, request, operation, Core.Domain.LifecycleAct.SupersedeIntention, LifecycleSupersedeIntentionOperation);
        }

        if (string.Equals(request.Operation, LifecycleNotifyOperation, StringComparison.Ordinal))
        {
            return HandleLifecycleTransition(context, request, operation, Core.Domain.LifecycleAct.Notify, LifecycleNotifyOperation);
        }

        if (string.Equals(request.Operation, LifecycleAcknowledgeOperation, StringComparison.Ordinal))
        {
            return HandleLifecycleTransition(context, request, operation, Core.Domain.LifecycleAct.Acknowledge, LifecycleAcknowledgeOperation);
        }

        if (string.Equals(request.Operation, LifecycleSetValidityOperation, StringComparison.Ordinal))
        {
            return HandleLifecycleTransition(context, request, operation, Core.Domain.LifecycleAct.SetValidity, LifecycleSetValidityOperation);
        }

        if (string.Equals(request.Operation, LifecycleRetainRevisionOperation, StringComparison.Ordinal))
        {
            return HandleLifecycleTransition(context, request, operation, Core.Domain.LifecycleAct.RetainRevision, LifecycleRetainRevisionOperation);
        }

        if (string.Equals(request.Operation, ResourceReadOperation, StringComparison.Ordinal))
        {
            return HandleResourceRead(context, request.ResourceId, operation);
        }

        if (string.Equals(request.Operation, ResourceWriteOperation, StringComparison.Ordinal))
        {
            return HandleResourceWrite(context, request, operation);
        }

        return Error("unknown_operation");
    }

    private ServiceReply HandleMemoryCreate(AuthenticatedContext context, ServiceRequest request, IVaultOperation? operation)
    {
        if (context.Kind != PrincipalKind.Owner || operation is null || HasContentWriteExtras(request) ||
            string.IsNullOrEmpty(request.ResourceId) || string.IsNullOrEmpty(request.ZoneId) ||
            string.IsNullOrEmpty(request.ContentBase64))
        {
            return Error("invalid_request");
        }
        byte[]? draft = null;
        try
        {
            draft = Convert.FromBase64String(request.ContentBase64);
            var created = new MemoryItemService().Create(context, request.ResourceId, request.ZoneId, draft, operation);
            return new ServiceReply(CurrentProtocolVersion, "memory_created", "owner", null,
                ResourceRevision: created.Revision, MemoryItem: ToMemoryWire(created));
        }
        catch (FormatException)
        {
            return Error("invalid_request");
        }
        catch (ArgumentException)
        {
            return Error("invalid_request");
        }
        catch (UnauthorizedAccessException)
        {
            return Error("forbidden");
        }
        finally
        {
            if (draft is { Length: > 0 })
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(draft);
            }
        }
    }

    /// <summary>
    /// S2-T2 explicit import boundary: replaces external plaintext editing of
    /// canonical files. The owner supplies exact envelope bytes plus the live
    /// revision they replace (0 for create-only). The bytes must decode as a
    /// valid S2-T1/S2-T2 stored envelope with matching ID/zone before the
    /// revision-gated commit; stale or mismatched imports fail closed and
    /// publish nothing.
    /// </summary>
    private ServiceReply HandleMemoryImport(AuthenticatedContext context, ServiceRequest request, IVaultOperation? operation)
    {
        if (context.Kind != PrincipalKind.Owner || operation is null || HasContentWriteExtras(request) ||
            string.IsNullOrEmpty(request.ResourceId) || string.IsNullOrEmpty(request.ZoneId) ||
            request.ResourceRevision is not { } expectedRevision || expectedRevision < 0 ||
            string.IsNullOrEmpty(request.ContentBase64))
        {
            return Error("invalid_request");
        }

        byte[]? envelope = null;
        try
        {
            envelope = Convert.FromBase64String(request.ContentBase64);
            var imported = new MemoryItemService().Import(context, request.ResourceId, request.ZoneId, expectedRevision, envelope, operation);
            return new ServiceReply(CurrentProtocolVersion, "memory_imported", "owner", null,
                ResourceRevision: imported.Revision, MemoryItem: ToMemoryWire(imported));
        }
        catch (FormatException)
        {
            return Error("invalid_request");
        }
        catch (ArgumentException)
        {
            return Error("invalid_request");
        }
        catch (UnauthorizedAccessException)
        {
            return Error("forbidden");
        }
        finally
        {
            if (envelope is { Length: > 0 })
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(envelope);
            }
        }
    }

    /// <summary>
    /// S2-T2 revision-gated revise: the caller must name the live revision it
    /// replaces. Stale, missing, or mismatched revisions fail closed with the
    /// same generic error as unknown IDs (no existence oracle) and publish
    /// nothing. Successful revises re-validate the typed envelope and return
    /// the new revision.
    /// </summary>
    private ServiceReply HandleMemoryRevise(AuthenticatedContext context, ServiceRequest request, IVaultOperation? operation)
    {
        if (context.Kind != PrincipalKind.Owner || operation is null || HasContentWriteExtras(request) ||
            string.IsNullOrEmpty(request.ResourceId) || string.IsNullOrEmpty(request.ZoneId) ||
            request.ResourceRevision is not { } expectedRevision || expectedRevision < 1 ||
            string.IsNullOrEmpty(request.ContentBase64))
        {
            return Error("invalid_request");
        }

        byte[]? draft = null;
        try
        {
            draft = Convert.FromBase64String(request.ContentBase64);
            var revised = new MemoryItemService().Revise(context, request.ResourceId, request.ZoneId, expectedRevision, draft, operation);
            return new ServiceReply(CurrentProtocolVersion, "memory_revised", "owner", null,
                ResourceRevision: revised.Revision, MemoryItem: ToMemoryWire(revised));
        }
        catch (FormatException)
        {
            return Error("invalid_request");
        }
        catch (ArgumentException)
        {
            return Error("invalid_request");
        }
        catch (UnauthorizedAccessException)
        {
            return Error("forbidden");
        }
        finally
        {
            if (draft is { Length: > 0 })
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(draft);
            }
        }
    }

    private ServiceReply HandleMemoryRead(AuthenticatedContext context, ServiceRequest request, IVaultOperation? operation)
    {
        if (context.Kind != PrincipalKind.Owner || operation is null || HasContentReadExtras(request) ||
            string.IsNullOrEmpty(request.ResourceId))
        {
            return Error("invalid_request");
        }

        try
        {
            var item = new MemoryItemService().Read(context, request.ResourceId, request.ResourceRevision, operation);
            return new ServiceReply(CurrentProtocolVersion, "memory", "owner", null,
                ResourceRevision: item.Revision, MemoryItem: ToMemoryWire(item));
        }
        catch (ArgumentException)
        {
            return Error("invalid_request");
        }
        catch (UnauthorizedAccessException)
        {
            return Error("forbidden");
        }
    }

    /// <summary>
    /// S2-T3 owner source registry: create-only, revision-gated revise, and
    /// pinned-revision read over the shared encrypted store. A registered
    /// source carries metadata only (title, private locator, version,
    /// explicit availability); no source body is persisted here and item
    /// read grants never authorize this API.
    /// </summary>
    private ServiceReply HandleSourceCreate(AuthenticatedContext context, ServiceRequest request, IVaultOperation? operation)
    {
        if (context.Kind != PrincipalKind.Owner || operation is null || HasContentWriteExtras(request) ||
            string.IsNullOrEmpty(request.ResourceId) || string.IsNullOrEmpty(request.ZoneId) ||
            string.IsNullOrEmpty(request.ContentBase64))
        {
            return Error("invalid_request");
        }

        byte[]? draft = null;
        try
        {
            draft = Convert.FromBase64String(request.ContentBase64);
            var created = new SourceRegistryService().Create(context, request.ResourceId, request.ZoneId, draft, operation);
            return new ServiceReply(CurrentProtocolVersion, "source_created", "owner", null,
                ResourceRevision: created.Revision, Source: ToSourceWire(created));
        }
        catch (FormatException)
        {
            return Error("invalid_request");
        }
        catch (ArgumentException)
        {
            return Error("invalid_request");
        }
        catch (UnauthorizedAccessException)
        {
            return Error("forbidden");
        }
        finally
        {
            if (draft is { Length: > 0 })
            {
                CryptographicOperations.ZeroMemory(draft);
            }
        }
    }

    private ServiceReply HandleSourceRevise(AuthenticatedContext context, ServiceRequest request, IVaultOperation? operation)
    {
        if (context.Kind != PrincipalKind.Owner || operation is null || HasContentWriteExtras(request) ||
            string.IsNullOrEmpty(request.ResourceId) || string.IsNullOrEmpty(request.ZoneId) ||
            request.ResourceRevision is not { } expectedRevision || expectedRevision < 1 ||
            string.IsNullOrEmpty(request.ContentBase64))
        {
            return Error("invalid_request");
        }

        byte[]? draft = null;
        try
        {
            draft = Convert.FromBase64String(request.ContentBase64);
            var revised = new SourceRegistryService().Revise(context, request.ResourceId, request.ZoneId, expectedRevision, draft, operation);
            return new ServiceReply(CurrentProtocolVersion, "source_revised", "owner", null,
                ResourceRevision: revised.Revision, Source: ToSourceWire(revised));
        }
        catch (FormatException)
        {
            return Error("invalid_request");
        }
        catch (ArgumentException)
        {
            return Error("invalid_request");
        }
        catch (UnauthorizedAccessException)
        {
            return Error("forbidden");
        }
        finally
        {
            if (draft is { Length: > 0 })
            {
                CryptographicOperations.ZeroMemory(draft);
            }
        }
    }

    private ServiceReply HandleSourceRead(AuthenticatedContext context, ServiceRequest request, IVaultOperation? operation)
    {
        if (context.Kind != PrincipalKind.Owner || operation is null || HasContentReadExtras(request) ||
            string.IsNullOrEmpty(request.ResourceId))
        {
            return Error("invalid_request");
        }

        try
        {
            var source = new SourceRegistryService().Read(context, request.ResourceId, request.ResourceRevision, operation);
            return new ServiceReply(CurrentProtocolVersion, "source", "owner", null,
                ResourceRevision: source.Revision, Source: ToSourceWire(source));
        }
        catch (ArgumentException)
        {
            return Error("invalid_request");
        }
        catch (UnauthorizedAccessException)
        {
            return Error("forbidden");
        }
    }

    /// <summary>
    /// S3-T4 owner research capture: the verbatim unstructured text plus its
    /// zone (and optional owner-supplied title/URL/author role/source date)
    /// is persisted as one encrypted registered source through the S2-T3
    /// registry path before any structuring job could run. Owner-only and
    /// unlocked-only: while locked the capture is refused with
    /// <c>vault_locked</c> before anything is written, nothing reports saved,
    /// and no plaintext draft lives anywhere. The reply carries the source
    /// metadata wire and the stored revision — never the text back.
    /// </summary>
    private ServiceReply HandleResearchCapture(AuthenticatedContext context, ServiceRequest request, IVaultOperation? operation)
    {
        if (context.Kind != PrincipalKind.Owner)
        {
            return Error("forbidden");
        }

        if (operation is null)
        {
            return Error("vault_locked");
        }

        if (HasResearchCaptureExtras(request) || string.IsNullOrEmpty(request.ResourceId) ||
            string.IsNullOrEmpty(request.ZoneId) || string.IsNullOrEmpty(request.ContentBase64))
        {
            return Error("invalid_request");
        }

        SpeakerRole? author = null;
        if (request.SourceAuthor is { } authorToken)
        {
            if (!TryParseSourceAuthor(authorToken, out var parsed))
            {
                return Error("invalid_request");
            }

            author = parsed;
        }

        if (request.SourceCapturedAtUtc is { } capturedAt && capturedAt.Offset != TimeSpan.Zero)
        {
            return Error("invalid_request");
        }

        byte[]? text = null;
        try
        {
            text = Convert.FromBase64String(request.ContentBase64);
            var captured = new ResearchCaptureService().Capture(
                context, request.ResourceId, request.ZoneId, text,
                request.SourceTitle, request.SourceLocator, author, request.SourceCapturedAtUtc, operation);
            return new ServiceReply(CurrentProtocolVersion, "research_captured", "owner", null,
                ResourceRevision: captured.Revision, Source: ToSourceWire(captured));
        }
        catch (FormatException)
        {
            return Error("invalid_request");
        }
        catch (ArgumentException)
        {
            return Error("invalid_request");
        }
        catch (UnauthorizedAccessException)
        {
            return Error("forbidden");
        }
        finally
        {
            if (text is { Length: > 0 })
            {
                CryptographicOperations.ZeroMemory(text);
            }
        }
    }

    /// <summary>
    /// S3-T4 owner research read: returns the live (or exact requested)
    /// revision of one captured note and the verbatim text bytes. Owner-only
    /// and unlocked-only; a metadata-only registration (no captured text)
    /// answers <c>research_note_unavailable</c> instead of inventing content,
    /// and the text never rides the metadata source wire.
    /// </summary>
    private ServiceReply HandleResearchRead(AuthenticatedContext context, ServiceRequest request, IVaultOperation? operation)
    {
        if (context.Kind != PrincipalKind.Owner)
        {
            return Error("forbidden");
        }

        if (operation is null)
        {
            return Error("vault_locked");
        }

        if (HasResearchReadExtras(request) || string.IsNullOrEmpty(request.ResourceId) ||
            request.ResourceRevision is { } expectedRevision && expectedRevision < 1)
        {
            return Error("invalid_request");
        }

        try
        {
            var source = new ResearchCaptureService().Read(context, request.ResourceId, request.ResourceRevision, operation);
            if (source.Text is null)
            {
                return Error("research_note_unavailable");
            }

            var text = Encoding.UTF8.GetBytes(source.Text);
            try
            {
                return new ServiceReply(CurrentProtocolVersion, "research_note", "owner", null,
                    ResourceRevision: source.Revision,
                    Source: ToSourceWire(source),
                    ContentBase64: Convert.ToBase64String(text),
                    ContentType: "text/plain; charset=utf-8");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(text);
            }
        }
        catch (ArgumentException)
        {
            return Error("invalid_request");
        }
        catch (UnauthorizedAccessException)
        {
            return Error("forbidden");
        }
    }

    /// <summary>
    /// Parses one owner-declared source author role token
    /// (<c>user</c>/<c>assistant</c>/<c>tool</c>/<c>external</c>). Unknown is
    /// never parsed from a token: omitting the field is the only way to leave
    /// the author unknown, so an explicit value is always an assertion.
    /// </summary>
    private static bool TryParseSourceAuthor(string token, out SpeakerRole author)
    {
        switch (token)
        {
            case "user":
                author = SpeakerRole.User;
                return true;
            case "assistant":
                author = SpeakerRole.Assistant;
                return true;
            case "tool":
                author = SpeakerRole.Tool;
                return true;
            case "external":
                author = SpeakerRole.External;
                return true;
            default:
                author = SpeakerRole.Unknown;
                return false;
        }
    }

    /// <summary>
    /// Owner source enrollment and incremental ingestion coordinator (S3-T1
    /// plus S3-T6 operations): enroll, revoke, status, ingest, relocate,
    /// pause, resume, refresh opt-in/opt-out, allowlist listing and safe
    /// incremental refresh. Every path is owner-only and needs an unlocked
    /// operation; the agent channel never reaches these operations (the
    /// transport allowlist refuses them), so no agent can enroll a source,
    /// trigger ingestion or mint any grant. Enrollments change no grant
    /// state: the coordinator writes only the encrypted enrollment state and
    /// per-generation journals, so an approved snapshot stays frozen and no
    /// source API authority widens item grants.
    /// </summary>
    private ServiceReply HandleSourceIngestionCommand(
        AuthenticatedContext context,
        ServiceRequest request,
        IVaultOperation? operation,
        CancellationToken cancellationToken)
    {
        if (_sourceIngestion is null)
        {
            return Error("source_ingest_unavailable");
        }

        if (context.Kind != PrincipalKind.Owner)
        {
            return Error("forbidden");
        }

        if (operation is null)
        {
            return Error("vault_locked");
        }

        if (string.Equals(request.Operation, SourceEnrollOperation, StringComparison.Ordinal))
        {
            if (HasSourceEnrollExtras(request) ||
                string.IsNullOrEmpty(request.ResourceId) || string.IsNullOrEmpty(request.ZoneId) ||
                string.IsNullOrEmpty(request.SourceLocator) || string.IsNullOrEmpty(request.SourceFormat) ||
                request.SourceFormatVersion is not { } formatVersion || formatVersion < 1)
            {
                return Error("invalid_request");
            }

            try
            {
                var enrolled = _sourceIngestion.Enroll(context, request.ResourceId, request.ZoneId,
                    request.SourceLocator, request.SourceFormat, formatVersion, operation);
                return new ServiceReply(CurrentProtocolVersion, "source_enrolled", "owner", null,
                    SourceEnrollment: ToSourceEnrollmentWire(enrolled, SourceEnrollmentLife.Enrolled, null));
            }
            catch (ArgumentException)
            {
                return Error("invalid_request");
            }
            catch (SourceOperationException ex)
            {
                return Error(ex.Code);
            }
            catch (UnauthorizedAccessException)
            {
                return Error("forbidden");
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                return Error("source_state_unavailable");
            }
        }

        if (string.Equals(request.Operation, SourceRelocateOperation, StringComparison.Ordinal))
        {
            if (HasSourceCommandExtras(request, allowRelocationFields: true) ||
                string.IsNullOrEmpty(request.ResourceId) || string.IsNullOrEmpty(request.SourceLocator) ||
                request.SourceGeneration is not { } generation || generation < 1)
            {
                return Error("invalid_request");
            }

            try
            {
                var relocated = _sourceIngestion.Relocate(context, request.ResourceId,
                    request.SourceLocator, generation, operation);
                return new ServiceReply(CurrentProtocolVersion, "source_relocated", "owner", null,
                    SourceEnrollment: ToSourceEnrollmentWire(relocated, SourceEnrollmentLife.Enrolled, null));
            }
            catch (ArgumentException)
            {
                return Error("invalid_request");
            }
            catch (SourceOperationException ex)
            {
                return Error(ex.Code);
            }
            catch (UnauthorizedAccessException)
            {
                return Error("forbidden");
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                return Error("source_state_unavailable");
            }
        }

        if (string.Equals(request.Operation, SourceRevokeOperation, StringComparison.Ordinal) ||
            string.Equals(request.Operation, SourceStatusOperation, StringComparison.Ordinal) ||
            string.Equals(request.Operation, SourceIngestOperation, StringComparison.Ordinal))
        {
            if (HasSourceCommandExtras(request) || string.IsNullOrEmpty(request.ResourceId))
            {
                return Error("invalid_request");
            }

            try
            {
                if (string.Equals(request.Operation, SourceRevokeOperation, StringComparison.Ordinal))
                {
                    var revoked = _sourceIngestion.Revoke(context, request.ResourceId, operation);
                    return new ServiceReply(CurrentProtocolVersion, "source_revoked", "owner", null,
                        SourceEnrollment: ToSourceEnrollmentWire(revoked, SourceEnrollmentLife.Revoked, null));
                }

                if (string.Equals(request.Operation, SourceStatusOperation, StringComparison.Ordinal))
                {
                    var status = _sourceIngestion.Status(context, request.ResourceId, operation);
                    return new ServiceReply(CurrentProtocolVersion, "source_status", "owner", null,
                        SourceEnrollment: ToSourceEnrollmentWire(status),
                        SourceIngestion: new SourceIngestionWire(
                            status.SourceId,
                            status.JournalRevision,
                            status.ConsumedOffset,
                            status.EventCount,
                            status.DiagnosticCount,
                            status.LastIngestAtUtc));
                }

                var outcome = _sourceIngestion.Ingest(context, request.ResourceId, operation, cancellationToken);
                // Completed runs (advanced/idle/partial/unsupported diagnostics)
                // reply with a status; refusals and failure modes reply with an
                // explicit error code, matching the service's error convention.
                var outcomeStatus = outcome.Kind switch
                {
                    SourceIngestionOutcomeKind.Advanced => "ingest_advanced",
                    SourceIngestionOutcomeKind.Idle => "ingest_idle",
                    SourceIngestionOutcomeKind.PartialPending => "ingest_partial_pending",
                    SourceIngestionOutcomeKind.UnsupportedFormat => "ingest_unsupported_format",
                    SourceIngestionOutcomeKind.UnsupportedRecord => "ingest_unsupported_record",
                    SourceIngestionOutcomeKind.NotEnrolled => "source_not_enrolled",
                    SourceIngestionOutcomeKind.Revoked => "source_revoked",
                    SourceIngestionOutcomeKind.Paused => "source_paused",
                    SourceIngestionOutcomeKind.MixedQuarantined => "source_mixed_quarantined",
                    SourceIngestionOutcomeKind.GenerationChanged => "source_generation_changed",
                    SourceIngestionOutcomeKind.SourceMissing => "source_missing",
                    SourceIngestionOutcomeKind.SourceUnreadable => "source_unreadable",
                    SourceIngestionOutcomeKind.SourceTooLarge => "source_too_large",
                    SourceIngestionOutcomeKind.JournalLimit => "source_journal_limit",
                    SourceIngestionOutcomeKind.JournalConflict => "source_journal_conflict",
                    _ => "source_ingest_rejected"
                };

                // The reply names the enrolled format/version beside the
                // outcome, so an unsupported dispatch carries its reason
                // without any source content.
                SourceEnrollmentWire? enrollmentWire = null;
                SourceIngestionWire? journalWire = null;
                try
                {
                    var current = _sourceIngestion.Status(context, request.ResourceId, operation);
                    enrollmentWire = ToSourceEnrollmentWire(current);
                    journalWire = new SourceIngestionWire(
                        current.SourceId,
                        current.JournalRevision,
                        current.ConsumedOffset,
                        current.EventCount,
                        current.DiagnosticCount,
                        current.LastIngestAtUtc);
                }
                catch (SourceOperationException)
                {
                    // The source is no longer enrolled (revoked concurrently):
                    // the ingest outcome already names the refusal.
                }

                if (outcome.Kind is SourceIngestionOutcomeKind.NotEnrolled or
                    SourceIngestionOutcomeKind.Revoked or
                    SourceIngestionOutcomeKind.Paused or
                    SourceIngestionOutcomeKind.MixedQuarantined or
                    SourceIngestionOutcomeKind.GenerationChanged or
                    SourceIngestionOutcomeKind.SourceUnreadable or
                    SourceIngestionOutcomeKind.SourceMissing or
                    SourceIngestionOutcomeKind.SourceTooLarge or
                    SourceIngestionOutcomeKind.JournalLimit or
                    SourceIngestionOutcomeKind.JournalConflict)
                {
                    return Error(outcomeStatus);
                }

                return new ServiceReply(CurrentProtocolVersion, outcomeStatus, "owner", null,
                    SourceEnrollment: enrollmentWire, SourceIngestion: journalWire);
            }
            catch (ArgumentException)
            {
                return Error("invalid_request");
            }
            catch (SourceOperationException ex)
            {
                return Error(ex.Code);
            }
            catch (UnauthorizedAccessException)
            {
                return Error("forbidden");
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                return Error("source_state_unavailable");
            }
        }

        if (string.Equals(request.Operation, SourcePauseOperation, StringComparison.Ordinal) ||
            string.Equals(request.Operation, SourceResumeOperation, StringComparison.Ordinal) ||
            string.Equals(request.Operation, SourceRefreshOptInOperation, StringComparison.Ordinal) ||
            string.Equals(request.Operation, SourceRefreshOptOutOperation, StringComparison.Ordinal))
        {
            if (HasSourceCommandExtras(request) || string.IsNullOrEmpty(request.ResourceId))
            {
                return Error("invalid_request");
            }

            try
            {
                if (string.Equals(request.Operation, SourcePauseOperation, StringComparison.Ordinal))
                {
                    var paused = _sourceIngestion.Pause(context, request.ResourceId, operation);
                    return new ServiceReply(CurrentProtocolVersion, "source_paused", "owner", null,
                        SourceEnrollment: ToSourceEnrollmentWire(paused, SourceEnrollmentLife.Paused, null));
                }

                if (string.Equals(request.Operation, SourceResumeOperation, StringComparison.Ordinal))
                {
                    var resumed = _sourceIngestion.Resume(context, request.ResourceId, operation);
                    var status = _sourceIngestion.Status(context, request.ResourceId, operation);
                    return new ServiceReply(CurrentProtocolVersion, "source_resumed", "owner", null,
                        SourceEnrollment: ToSourceEnrollmentWire(status),
                        SourceIngestion: new SourceIngestionWire(
                            status.SourceId,
                            status.JournalRevision,
                            status.ConsumedOffset,
                            status.EventCount,
                            status.DiagnosticCount,
                            status.LastIngestAtUtc));
                }

                var optIn = string.Equals(request.Operation, SourceRefreshOptInOperation, StringComparison.Ordinal);
                var updated = _sourceIngestion.SetRefreshOptIn(context, request.ResourceId, optIn, operation);
                var current = _sourceIngestion.Status(context, request.ResourceId, operation);
                return new ServiceReply(CurrentProtocolVersion,
                    optIn ? "source_refresh_opted_in" : "source_refresh_opted_out", "owner", null,
                    SourceEnrollment: ToSourceEnrollmentWire(current),
                    SourceIngestion: new SourceIngestionWire(
                        current.SourceId,
                        current.JournalRevision,
                        current.ConsumedOffset,
                        current.EventCount,
                        current.DiagnosticCount,
                        current.LastIngestAtUtc));
            }
            catch (ArgumentException)
            {
                return Error("invalid_request");
            }
            catch (SourceOperationException ex)
            {
                return Error(ex.Code);
            }
            catch (UnauthorizedAccessException)
            {
                return Error("forbidden");
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                return Error("source_state_unavailable");
            }
        }

        if (string.Equals(request.Operation, SourceListOperation, StringComparison.Ordinal) ||
            string.Equals(request.Operation, SourceRefreshOperation, StringComparison.Ordinal))
        {
            if (HasSourceCommandExtras(request) || request.ResourceId is not null)
            {
                return Error("invalid_request");
            }

            try
            {
                if (string.Equals(request.Operation, SourceListOperation, StringComparison.Ordinal))
                {
                    var enrollments = _sourceIngestion.List(context, operation)
                        .Select(ToSourceEnrollmentWire)
                        .ToArray();
                    return new ServiceReply(CurrentProtocolVersion, "sources_listed", "owner", null,
                        SourceEnrollments: enrollments);
                }

                var results = _sourceIngestion.Refresh(context, operation, cancellationToken)
                    .Select(result => new SourceRefreshWire(
                        result.SourceId, result.Status, result.ConsumedOffset,
                        result.EventsAdded, result.DiagnosticsAdded, result.DiagnosticCode))
                    .ToArray();
                return new ServiceReply(CurrentProtocolVersion, "sources_refreshed", "owner", null,
                    SourceRefresh: results);
            }
            catch (ArgumentException)
            {
                return Error("invalid_request");
            }
            catch (SourceOperationException ex)
            {
                return Error(ex.Code);
            }
            catch (UnauthorizedAccessException)
            {
                return Error("forbidden");
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                return Error("source_state_unavailable");
            }
        }

        return Error("invalid_request");
    }

    private static SourceEnrollmentWire ToSourceEnrollmentWire(SourceEnrollmentStatus status) => new(
        status.SourceId,
        status.ZoneId,
        status.Format,
        status.FormatVersion,
        status.Generation,
        status.Life switch
        {
            SourceEnrollmentLife.Revoked => "revoked",
            SourceEnrollmentLife.Quarantined => "quarantined",
            SourceEnrollmentLife.Paused => "paused",
            _ => "enrolled"
        },
        status.AnchorLength,
        status.EnrolledAtUtc,
        status.RevokedAtUtc,
        status.Quarantine is { } quarantine
            ? new SourceQuarantineWire(
                quarantine.Reason switch
                {
                    SourceQuarantineReason.GenerationTruncated => "generation_truncated",
                    SourceQuarantineReason.GenerationReplaced => "generation_replaced",
                    _ => "mixed_locator"
                },
                quarantine.Detail,
                quarantine.RecordedAtUtc)
            : null,
        ToWireToken(status.Availability.ToString()),
        status.AvailabilityObservedAtUtc,
        status.RelocatedAtUtc,
        status.SourceReferenceRevision,
        status.PausedAtUtc,
        status.RefreshOptIn,
        status.JournalCiphertextBytes,
        status.SnapshotCiphertextBytes,
        status.SnapshotRevision);

    private static SourceEnrollmentWire ToSourceEnrollmentWire(
        EnrolledSource enrolled,
        SourceEnrollmentLife life,
        SourceQuarantineRecord? quarantine) => new(
        enrolled.SourceId,
        enrolled.ZoneId,
        enrolled.Format,
        enrolled.FormatVersion,
        enrolled.Generation,
        life switch
        {
            SourceEnrollmentLife.Revoked => "revoked",
            SourceEnrollmentLife.Quarantined => "quarantined",
            SourceEnrollmentLife.Paused => "paused",
            _ => "enrolled"
        },
        enrolled.AnchorLength,
        enrolled.EnrolledAtUtc,
        enrolled.RevokedAtUtc,
        quarantine is null
            ? null
            : new SourceQuarantineWire(
                quarantine.Reason switch
                {
                    SourceQuarantineReason.GenerationTruncated => "generation_truncated",
                    SourceQuarantineReason.GenerationReplaced => "generation_replaced",
                    _ => "mixed_locator"
                },
                quarantine.Detail,
                quarantine.RecordedAtUtc),
        ToWireToken(SourceAvailabilityObservation.NotChecked.ToString()),
        null,
        enrolled.RelocatedAtUtc,
        null,
        enrolled.PausedAtUtc,
        enrolled.RefreshOptIn);

    private ServiceReply HandleSourceSnapshotCommand(
        AuthenticatedContext context,
        ServiceRequest request,
        IVaultOperation? operation)
    {
        if (context.Kind != PrincipalKind.Owner)
        {
            return Error("forbidden");
        }

        if (operation is null)
        {
            return Error("vault_locked");
        }

        try
        {
            if (string.Equals(request.Operation, SourceSnapshotPreserveOperation, StringComparison.Ordinal))
            {
                if (_sourceReader is null)
                {
                    return Error("source_snapshot_unavailable");
                }

                if (HasContentReadExtras(request, allowSnapshotRetention: true) ||
                    string.IsNullOrEmpty(request.ResourceId) ||
                    request.ResourceRevision is not { } sourceRevision || sourceRevision < 1 ||
                    request.SnapshotRetainUntilUtc is not { } retainUntilUtc)
                {
                    return Error("invalid_request");
                }

                var preserved = _sourceSnapshots.Preserve(context, request.ResourceId,
                    sourceRevision, retainUntilUtc, operation, _sourceReader);
                return new ServiceReply(CurrentProtocolVersion, "source_snapshot_preserved", "owner", null,
                    ResourceRevision: preserved.Revision,
                    SourceSnapshot: ToSourceSnapshotWire(preserved.Stored, preserved.Revision));
            }

            if (string.Equals(request.Operation, SourceSnapshotListOperation, StringComparison.Ordinal))
            {
                if (HasContentReadExtras(request) || request.ResourceRevision is not null ||
                    string.IsNullOrEmpty(request.ResourceId))
                {
                    return Error("invalid_request");
                }

                var snapshots = _sourceSnapshots.List(context, request.ResourceId, operation)
                    .Select(snapshot => new SourceSnapshotDescriptorWire(
                        snapshot.Id, snapshot.ZoneId, snapshot.Revision, snapshot.Generation))
                    .ToArray();
                return new ServiceReply(CurrentProtocolVersion, "source_snapshots", "owner", null,
                    SourceSnapshots: snapshots);
            }

            if (string.Equals(request.Operation, SourceSnapshotReadOperation, StringComparison.Ordinal))
            {
                if (HasContentReadExtras(request) || string.IsNullOrEmpty(request.ResourceId) ||
                    request.ResourceRevision is not { } expectedRevision || expectedRevision < 1)
                {
                    return Error("invalid_request");
                }

                var snapshot = _sourceSnapshots.Read(request.ResourceId, expectedRevision, operation);
                try
                {
                    return new ServiceReply(CurrentProtocolVersion, "source_snapshot", "owner", null,
                        ResourceRevision: snapshot.Revision,
                        SourceSnapshot: ToSourceSnapshotWire(
                            snapshot.Stored, snapshot.Revision, Convert.ToBase64String(snapshot.Content)));
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(snapshot.Content);
                }
            }

            if (string.Equals(request.Operation, SourceSnapshotRetainOperation, StringComparison.Ordinal))
            {
                if (HasContentReadExtras(request, allowSnapshotRetention: true) ||
                    string.IsNullOrEmpty(request.ResourceId) ||
                    request.ResourceRevision is not { } expectedRevision || expectedRevision < 1 ||
                    request.SnapshotRetainUntilUtc is not { } retainUntilUtc)
                {
                    return Error("invalid_request");
                }

                var retained = _sourceSnapshots.Retain(context, request.ResourceId,
                    expectedRevision, retainUntilUtc, operation);
                return new ServiceReply(CurrentProtocolVersion, "source_snapshot_retained", "owner", null,
                    ResourceRevision: retained.Revision,
                    SourceSnapshot: ToSourceSnapshotWire(retained.Stored, retained.Revision));
            }

            if (string.Equals(request.Operation, SourceSnapshotPurgeOperation, StringComparison.Ordinal))
            {
                if (HasContentReadExtras(request) || string.IsNullOrEmpty(request.ResourceId) ||
                    request.ResourceRevision is not { } expectedRevision || expectedRevision < 1)
                {
                    return Error("invalid_request");
                }

                var purged = _sourceSnapshots.Purge(context, request.ResourceId, expectedRevision, operation);
                return new ServiceReply(CurrentProtocolVersion, "source_snapshot_purged", "owner", null,
                    ResourceRevision: purged.Revision,
                    SourceSnapshot: ToSourceSnapshotWire(purged.Stored, purged.Revision));
            }
        }
        catch (ArgumentException)
        {
            return Error("invalid_request");
        }
        catch (SourceOperationException ex)
        {
            return Error(ex.Code);
        }
        catch (UnauthorizedAccessException)
        {
            return Error("forbidden");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            return Error("source_snapshot_unavailable");
        }

        return Error("invalid_request");
    }

    private static SourceSnapshotWire ToSourceSnapshotWire(
        StoredSourceSnapshot snapshot,
        long revision,
        string? contentBase64 = null) => new(
        snapshot.Id,
        snapshot.ZoneId,
        revision,
        snapshot.SourceId,
        snapshot.SourceRevision,
        snapshot.SourceGeneration,
        snapshot.SourceLength,
        snapshot.SourceDigest,
        snapshot.CapturedAtUtc,
        snapshot.RetainUntilUtc,
        snapshot.Purged,
        contentBase64);

    /// <summary>
    /// S2-T4 owner relation store: create-only over the shared encrypted
    /// canonical store. Both endpoints must exist at the live revision the link
    /// pins and must decode as the declared kind (memory item or registered
    /// source); derivation cycles are refused. A duplicate create fails closed
    /// through the vault revision gate.
    /// </summary>
    private ServiceReply HandleRelationCreate(AuthenticatedContext context, ServiceRequest request, IVaultOperation? operation)
    {
        if (context.Kind != PrincipalKind.Owner || operation is null || HasContentWriteExtras(request) ||
            string.IsNullOrEmpty(request.ResourceId) || string.IsNullOrEmpty(request.ZoneId) ||
            request.ResourceRevision is not null ||
            string.IsNullOrEmpty(request.ContentBase64))
        {
            return Error("invalid_request");
        }

        byte[]? draft = null;
        try
        {
            draft = Convert.FromBase64String(request.ContentBase64);
            var created = new RelationService().Create(context, request.ResourceId, request.ZoneId, draft, operation);
            return new ServiceReply(CurrentProtocolVersion, "relation_created", "owner", null,
                ResourceRevision: created.Relation.Revision, Relation: ToRelationWire(created.Relation, created.Review));
        }
        catch (FormatException)
        {
            return Error("invalid_request");
        }
        catch (ArgumentException)
        {
            return Error("invalid_request");
        }
        catch (UnauthorizedAccessException)
        {
            return Error("forbidden");
        }
        finally
        {
            if (draft is { Length: > 0 })
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(draft);
            }
        }
    }

    /// <summary>
    /// S2-T4 owner relation lifecycle: accept, reject, unlink, relink. Every
    /// transition is a revision-gated write of the same record; endpoints, kind
    /// and the original reason never change, and reject/unlink require an
    /// explicit recorded rationale instead of a silent edit.
    /// </summary>
    private ServiceReply HandleRelationTransition(
        AuthenticatedContext context,
        ServiceRequest request,
        IVaultOperation? operation,
        string transitionOperation)
    {
        if (context.Kind != PrincipalKind.Owner || operation is null || HasRelationTransitionExtras(request) ||
            string.IsNullOrEmpty(request.ResourceId) ||
            request.ResourceRevision is not { } expectedRevision || expectedRevision < 1)
        {
            return Error("invalid_request");
        }

        byte[]? draft = null;
        try
        {
            draft = request.ContentBase64 is null ? null : Convert.FromBase64String(request.ContentBase64);
            var service = new RelationService();
            var result = string.Equals(transitionOperation, RelationAcceptOperation, StringComparison.Ordinal)
                ? service.Accept(context, request.ResourceId, expectedRevision, draft, operation)
                : string.Equals(transitionOperation, RelationRejectOperation, StringComparison.Ordinal)
                    ? service.Reject(context, request.ResourceId, expectedRevision, draft, operation)
                    : string.Equals(transitionOperation, RelationUnlinkOperation, StringComparison.Ordinal)
                        ? service.Unlink(context, request.ResourceId, expectedRevision, draft, operation)
                        : service.Relink(context, request.ResourceId, expectedRevision, draft, operation);
            var status = string.Equals(transitionOperation, RelationAcceptOperation, StringComparison.Ordinal)
                ? "relation_accepted"
                : string.Equals(transitionOperation, RelationRejectOperation, StringComparison.Ordinal)
                    ? "relation_rejected"
                    : string.Equals(transitionOperation, RelationUnlinkOperation, StringComparison.Ordinal)
                        ? "relation_unlinked"
                        : "relation_relinked";
            return new ServiceReply(CurrentProtocolVersion, status, "owner", null,
                ResourceRevision: result.Relation.Revision, Relation: ToRelationWire(result.Relation, result.Review));
        }
        catch (FormatException)
        {
            return Error("invalid_request");
        }
        catch (ArgumentException)
        {
            return Error("invalid_request");
        }
        catch (UnauthorizedAccessException)
        {
            return Error("forbidden");
        }
        finally
        {
            if (draft is { Length: > 0 })
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(draft);
            }
        }
    }

    /// <summary>
    /// S2-T4 owner relation read: the stored record plus its live review
    /// state. Unknown, unavailable, or tampered relations share one generic
    /// denial, so a read is never an existence oracle.
    /// </summary>
    private ServiceReply HandleRelationRead(AuthenticatedContext context, ServiceRequest request, IVaultOperation? operation)
    {
        if (context.Kind != PrincipalKind.Owner || operation is null || HasContentReadExtras(request) ||
            string.IsNullOrEmpty(request.ResourceId))
        {
            return Error("invalid_request");
        }

        try
        {
            var result = new RelationService().Read(context, request.ResourceId, request.ResourceRevision, operation);
            if (result.Relation is null || result.Review is null)
            {
                return Error("invalid_request");
            }

            return new ServiceReply(CurrentProtocolVersion, "relation", "owner", null,
                ResourceRevision: result.Relation.Revision, Relation: ToRelationWire(result.Relation, result.Review));
        }
        catch (ArgumentException)
        {
            return Error("invalid_request");
        }
        catch (UnauthorizedAccessException)
        {
            return Error("forbidden");
        }
    }

    /// <summary>
    /// S2-T4 scoped agent link creation inside the existing owner-approved
    /// `link.create` grant: the session authority must hold both the subject
    /// (source scope) and the object (destination scope) at the pinned
    /// revisions. The relation ID is minted server-side, the owning zone comes
    /// from the subject endpoint's registered zone, and the link is always
    /// proposed: an agent session never records owner acceptance.
    /// </summary>
    private ServiceReply HandleAgentLinkCreate(
        AuthenticatedContext context,
        ServiceRequest request,
        IVaultOperation? operation)
    {
        if (context.Kind != PrincipalKind.Agent || operation is null ||
            HasAgentLinkCreateExtras(request) ||
            request.AccessOperation is not LinkCreateOperation ||
            string.IsNullOrEmpty(request.ContentBase64))
        {
            return ResourceDenied();
        }

        byte[]? draft = null;
        try
        {
            draft = Convert.FromBase64String(request.ContentBase64);
            Core.Domain.RelationDraft parsed;
            try
            {
                parsed = Core.Domain.RelationCodec.DecodeDraft(draft);
            }
            catch (ArgumentException)
            {
                return ResourceDenied();
            }

            if (parsed.From is null || parsed.To is null)
            {
                return ResourceDenied();
            }

            // Authorize the exact operation and both endpoint revisions against
            // the frozen snapshot before any relation data is written.
            AgentAccessRequest Authorize() => new(
                LinkCreateOperation,
                parsed.From.ResourceId,
                parsed.From.Revision,
                parsed.To.ResourceId,
                parsed.To.Revision);

            var beforeWrite = operation.LoadScopeGrantState();
            var decision = _agentSessions.AuthorizeSession(
                request.SessionToken,
                beforeWrite.UnlockEpoch,
                beforeWrite.PolicyGeneration,
                Authorize());
            if (!decision.Allowed)
            {
                return ResourceDenied();
            }

            RelationWriteResult created;
            try
            {
                created = new RelationService().CreateAgentLink(context, draft, operation);
            }
            catch (ArgumentException)
            {
                return ResourceDenied();
            }

            var beforeServe = operation.LoadScopeGrantState();
            var recheck = _agentSessions.AuthorizeSession(
                request.SessionToken,
                beforeServe.UnlockEpoch,
                beforeServe.PolicyGeneration,
                Authorize());
            if (!recheck.Allowed)
            {
                return ResourceDenied();
            }

            operation.ValidateRegistration(created.Relation.Id, created.Relation.Revision);
            return new ServiceReply(
                CurrentProtocolVersion, "relation_created", null, null,
                Decision: recheck,
                PolicyGeneration: recheck.PolicyGeneration,
                ResourceRevision: created.Relation.Revision,
                Relation: ToRelationWire(created.Relation, created.Review));
        }
        catch (FormatException)
        {
            return ResourceDenied();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ResourceDenied();
        }
        finally
        {
            if (draft is { Length: > 0 })
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(draft);
            }
        }
    }

    /// <summary>
    /// S2-T4 scoped agent relation read: the session must hold `relation.read`
    /// on the exact relation ID/revision, and both endpoints must be inside the
    /// granted snapshot at their live revisions with the read operation that
    /// matches the endpoint kind (`source.read` for registered sources,
    /// `memory.read` for memory items). A cross-zone or ungranted endpoint is
    /// one generic denial with no endpoint metadata, title, count, or backlink.
    /// </summary>
    private ServiceReply HandleAgentRelationRead(
        AuthenticatedContext context,
        ServiceRequest request,
        IVaultOperation? operation)
    {
        if (context.Kind != PrincipalKind.Agent || operation is null ||
            HasAgentResourceReadExtras(request) ||
            request.AccessOperation is not RelationReadOperation ||
            string.IsNullOrEmpty(request.ResourceId))
        {
            return ResourceDenied();
        }

        try
        {
            // The relation-level check goes through the single session
            // authority: exact frozen operation, relation ID, and revision.
            var beforeRead = operation.LoadScopeGrantState();
            var decision = _agentSessions.AuthorizeSession(
                request.SessionToken,
                beforeRead.UnlockEpoch,
                beforeRead.PolicyGeneration,
                new AgentAccessRequest(RelationReadOperation, request.ResourceId, request.ResourceRevision));
            if (!decision.Allowed)
            {
                return ResourceDenied();
            }

            // The snapshot supplies the granted endpoint scope for the
            // per-endpoint check the relation service needs.
            var lookup = _agentSessions.LookupSession(
                request.SessionToken,
                beforeRead.UnlockEpoch,
                beforeRead.PolicyGeneration);
            if (lookup.Status != AgentSessionLookupStatus.Valid || lookup.Session is null)
            {
                return ResourceDenied();
            }

            var snapshot = lookup.Session;
            Func<Core.Domain.RelationEndpoint, bool> endpointAuthorizer = endpoint =>
            {
                var live = SourceRegistryService.TryGetLiveRevision(operation, endpoint.ResourceId);
                if (live is null)
                {
                    return false;
                }

                var required = endpoint.EndpointKind == Core.Domain.RelationEndpointKind.RegisteredSource
                    ? SourceReadOperation
                    : MemoryReadOperation;
                if (!snapshot.Operations.Contains(required, StringComparer.Ordinal))
                {
                    return false;
                }

                return IsInGrantScope(snapshot.Resources, endpoint.ResourceId, live.Value) ||
                    IsInGrantScope(snapshot.DestinationResources, endpoint.ResourceId, live.Value);
            };

            var result = new RelationService().Read(context, request.ResourceId, request.ResourceRevision, operation, endpointAuthorizer);
            if (result.Relation is null || result.Review is null)
            {
                return ResourceDenied();
            }

            var beforeServe = operation.LoadScopeGrantState();
            var recheck = _agentSessions.AuthorizeSession(
                request.SessionToken,
                beforeServe.UnlockEpoch,
                beforeServe.PolicyGeneration,
                new AgentAccessRequest(RelationReadOperation, request.ResourceId, request.ResourceRevision));
            if (!recheck.Allowed)
            {
                return ResourceDenied();
            }

            operation.ValidateRegistration(request.ResourceId, request.ResourceRevision);
            return new ServiceReply(
                CurrentProtocolVersion, "relation", null, null,
                Decision: recheck,
                PolicyGeneration: recheck.PolicyGeneration,
                ResourceRevision: result.Relation.Revision,
                Relation: ToRelationWire(result.Relation, result.Review));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ResourceDenied();
        }
    }

    private static RelationWire ToRelationWire(Core.Domain.Relation relation, Core.Domain.RelationReview review) => new(
        relation.Id,
        relation.ZoneId,
        relation.Revision,
        relation.SchemaVersion,
        ToWireToken(relation.Kind.ToString()),
        ToWireToken(relation.State.ToString()),
        ToRelationEndpointWire(relation.From),
        ToRelationEndpointWire(relation.To),
        ToWireToken(relation.Executor.ToString()),
        ToWireToken(relation.CreationMethod.ToString()),
        relation.Reason,
        relation.StateReason,
        relation.OccurredAtUtc,
        relation.RecordedAtUtc,
        ToWireToken(relation.StateExecutor.ToString()),
        relation.StateRecordedAtUtc,
        ToRelationReviewWire(review));

    private static RelationEndpointWire ToRelationEndpointWire(Core.Domain.RelationEndpoint endpoint) => new(
        endpoint.ResourceId,
        ToWireToken(endpoint.EndpointKind.ToString()),
        endpoint.Revision,
        endpoint.Span);

    private static RelationReviewWire ToRelationReviewWire(Core.Domain.RelationReview review) => new(
        ToWireToken(review.State.ToString()),
        ReviewReasonTokens(review.Reasons),
        review.StaleResourceIds);

    /// <summary>
    /// S2-T5 owner lifecycle create. The subject item ID is the only identity
    /// the request carries: the record ID, the zone, the initial states and the
    /// executor are all derived server-side, so no body field can name a
    /// lifecycle identity, declare a state, or spoof an executor.
    /// </summary>
    private ServiceReply HandleLifecycleCreate(AuthenticatedContext context, ServiceRequest request, IVaultOperation? operation)
    {
        if (context.Kind != PrincipalKind.Owner || operation is null || HasLifecycleWriteExtras(request) ||
            string.IsNullOrEmpty(request.ResourceId) || string.IsNullOrEmpty(request.ContentBase64))
        {
            return Error("invalid_request");
        }

        byte[]? draft = null;
        try
        {
            draft = Convert.FromBase64String(request.ContentBase64);
            var created = new LifecycleService().Create(context, request.ResourceId, draft, operation);
            return new ServiceReply(CurrentProtocolVersion, "lifecycle_created", "owner", null,
                ResourceRevision: created.Revision, Lifecycle: ToLifecycleWire(created));
        }
        catch (FormatException)
        {
            return Error("invalid_request");
        }
        catch (ArgumentException)
        {
            return Error("invalid_request");
        }
        catch (UnauthorizedAccessException)
        {
            return Error("forbidden");
        }
        finally
        {
            if (draft is { Length: > 0 })
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(draft);
            }
        }
    }

    /// <summary>
    /// S2-T5 owner lifecycle read: the current states plus the recorded history
    /// and retained revisions of one subject. Unknown, unavailable, tampered or
    /// wrongly shaped records share one generic denial.
    /// </summary>
    private ServiceReply HandleLifecycleRead(AuthenticatedContext context, ServiceRequest request, IVaultOperation? operation)
    {
        if (context.Kind != PrincipalKind.Owner || operation is null || HasContentReadExtras(request) ||
            string.IsNullOrEmpty(request.ResourceId))
        {
            return Error("invalid_request");
        }

        try
        {
            var record = new LifecycleService().Read(context, request.ResourceId, request.ResourceRevision, operation);
            if (record is null)
            {
                return Error("invalid_request");
            }

            return new ServiceReply(CurrentProtocolVersion, "lifecycle", "owner", null,
                ResourceRevision: record.Revision, Lifecycle: ToLifecycleWire(record));
        }
        catch (ArgumentException)
        {
            return Error("invalid_request");
        }
        catch (UnauthorizedAccessException)
        {
            return Error("forbidden");
        }
    }

    /// <summary>
    /// A retained historical revision read names the subject and the exact
    /// historical revision it asks for, and nothing else: the shared content
    /// read guard already refuses every other field.
    /// </summary>
    private ServiceReply HandleLifecycleRetainedRevisionRead(AuthenticatedContext context, ServiceRequest request, IVaultOperation? operation)
    {
        if (context.Kind != PrincipalKind.Owner || operation is null || HasContentReadExtras(request) ||
            string.IsNullOrEmpty(request.ResourceId) || request.ResourceRevision is not { } revision || revision < 1)
        {
            return Error("invalid_request");
        }

        try
        {
            var retained = new LifecycleService()
                .ReadRetainedRevision(context, request.ResourceId, revision, operation);
            if (retained is null)
            {
                return Error("invalid_request");
            }

            return new ServiceReply(CurrentProtocolVersion, "retained_revision", "owner", null,
                ResourceRevision: retained.ArchiveRevision,
                RetainedRevision: new LifecycleRevisionWire(
                    retained.ArchiveResourceId,
                    retained.ArchiveRevision,
                    retained.Subject.Revision,
                    ToMemoryWire(retained.Subject)));
        }
        catch (ArgumentException)
        {
            return Error("invalid_request");
        }
        catch (UnauthorizedAccessException)
        {
            return Error("forbidden");
        }
    }

    /// <summary>
    /// S2-T5 owner lifecycle acts: publish/reject/withdraw/re-propose, question
    /// resolve/reopen, intention complete/cancel/supersede, the notify and
    /// acknowledge markers, a validity update, and a revision retention. Every
    /// act names the subject and the live revision it replaces; the rationale,
    /// the supersession pointer and the temporal stamps travel only in the
    /// draft body the act itself uses.
    /// </summary>
    private ServiceReply HandleLifecycleTransition(
        AuthenticatedContext context,
        ServiceRequest request,
        IVaultOperation? operation,
        Core.Domain.LifecycleAct act,
        string transitionOperation)
    {
        if (context.Kind != PrincipalKind.Owner || operation is null || HasLifecycleTransitionExtras(request) ||
            string.IsNullOrEmpty(request.ResourceId) ||
            request.ResourceRevision is not { } expectedRevision || expectedRevision < 1)
        {
            return Error("invalid_request");
        }

        byte[]? draft = null;
        try
        {
            draft = request.ContentBase64 is null ? null : Convert.FromBase64String(request.ContentBase64);
            var result = new LifecycleService()
                .Transition(context, request.ResourceId, act, expectedRevision, draft, operation);
            return new ServiceReply(CurrentProtocolVersion, LifecycleStatusOf(transitionOperation), "owner", null,
                ResourceRevision: result.Revision, Lifecycle: ToLifecycleWire(result));
        }
        catch (FormatException)
        {
            return Error("invalid_request");
        }
        catch (ArgumentException)
        {
            return Error("invalid_request");
        }
        catch (UnauthorizedAccessException)
        {
            return Error("forbidden");
        }
        finally
        {
            if (draft is { Length: > 0 })
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(draft);
            }
        }
    }

    private static string LifecycleStatusOf(string transitionOperation) => transitionOperation switch
    {
        LifecyclePublishOperation => "lifecycle_published",
        LifecycleRejectProposalOperation => "lifecycle_rejected",
        LifecycleWithdrawOperation => "lifecycle_withdrawn",
        LifecycleReProposeOperation => "lifecycle_reproposed",
        LifecycleResolveQuestionOperation => "lifecycle_question_resolved",
        LifecycleReopenQuestionOperation => "lifecycle_question_reopened",
        LifecycleCompleteIntentionOperation => "lifecycle_intention_completed",
        LifecycleCancelIntentionOperation => "lifecycle_intention_cancelled",
        LifecycleSupersedeIntentionOperation => "lifecycle_intention_superseded",
        LifecycleNotifyOperation => "lifecycle_intention_notified",
        LifecycleAcknowledgeOperation => "lifecycle_intention_acknowledged",
        LifecycleSetValidityOperation => "lifecycle_validity_set",
        _ => "lifecycle_revision_retained"
    };

    private static LifecycleStampWire? ToLifecycleStampWire(Core.Domain.TemporalStamp? stamp) => stamp is null
        ? null
        : new LifecycleStampWire(
            ToWireToken(stamp.Precision.ToString()),
            stamp.InstantUtc,
            stamp.LocalYear,
            stamp.LocalMonth,
            stamp.LocalDay,
            stamp.LocalQuarter,
            stamp.TimeZoneOffsetMinutes);

    private static LifecycleChangeWire ToLifecycleChangeWire(Core.Domain.LifecycleStateChange change) => new(
        Core.Domain.LifecycleTokens.Aspect(change.Aspect),
        ToWireToken(change.Act.ToString()),
        change.FromState,
        change.ToState,
        ToWireToken(change.Executor.ToString()),
        change.RecordedAtUtc,
        change.Rationale,
        change.ReplacementResourceId);

    private static LifecycleRetainedRevisionWire ToLifecycleRetainedRevisionWire(
        Core.Domain.LifecycleRetainedRevision retained) => new(
        retained.SubjectRevision,
        retained.ArchiveResourceId,
        ToWireToken(retained.Executor.ToString()),
        retained.RecordedAtUtc);

    private static LifecycleWire ToLifecycleWire(Core.Domain.LifecycleRecord record) => new(
        record.Id,
        record.ZoneId,
        record.Revision,
        record.SchemaVersion,
        record.SubjectResourceId,
        Core.Domain.LifecycleTokens.Subject(record.SubjectKind),
        Core.Domain.LifecycleTokens.Publication(record.Publication),
        record.Question is { } question ? Core.Domain.LifecycleTokens.Question(question) : null,
        record.Intention is { } intention ? Core.Domain.LifecycleTokens.Intention(intention) : null,
        record.Notified,
        record.NotifiedAtUtc,
        record.Acknowledged,
        record.AcknowledgedAtUtc,
        record.SupersededBy,
        ToLifecycleStampWire(record.Occurred),
        ToLifecycleStampWire(record.Recorded),
        ToLifecycleStampWire(record.ValidFrom),
        ToLifecycleStampWire(record.ValidUntil),
        ToLifecycleStampWire(record.ReviewAfter),
        ToWireToken(record.Executor.ToString()),
        ToWireToken(record.CreationMethod.ToString()),
        record.Reason,
        record.RecordedAtUtc,
        record.History.Select(ToLifecycleChangeWire).ToArray(),
        record.RetainedRevisions.Select(ToLifecycleRetainedRevisionWire).ToArray());

    private static string[] ReviewReasonTokens(Core.Domain.RelationReviewReason reasons)
    {
        var tokens = new List<string>();
        if (reasons.HasFlag(Core.Domain.RelationReviewReason.EndpointMissing))
        {
            tokens.Add("endpointMissing");
        }

        if (reasons.HasFlag(Core.Domain.RelationReviewReason.EndpointRevisionChanged))
        {
            tokens.Add("endpointRevisionChanged");
        }

        if (reasons.HasFlag(Core.Domain.RelationReviewReason.SourceUnavailable))
        {
            tokens.Add("sourceUnavailable");
        }

        if (reasons.HasFlag(Core.Domain.RelationReviewReason.DependencyNeedsReview))
        {
            tokens.Add("dependencyNeedsReview");
        }

        return tokens.ToArray();
    }

    private static bool IsInGrantScope(
        IReadOnlyList<ScopeResourceRevision> scope,
        string? resourceId,
        long? revision) =>
        !string.IsNullOrWhiteSpace(resourceId) && revision is > 0 &&
        scope.Any(resource =>
            string.Equals(resource.ResourceId, resourceId, StringComparison.Ordinal) &&
            resource.Revision == revision.Value);

    /// <summary>
    /// S2-T3 scoped agent source read: the agent channel separately authorizes
    /// the exact "source.read" operation, source ID, and revision against the
    /// frozen grant before and after opening the encrypted registry bytes. An
    /// item grant (memory.read) never satisfies this path, and a grant on a
    /// different resource or revision denies with the same generic code.
    /// </summary>
    private ServiceReply HandleAgentSourceRead(
        AuthenticatedContext context,
        ServiceRequest request,
        IVaultOperation? operation)
    {
        if (context.Kind != PrincipalKind.Agent || operation is null ||
            HasAgentResourceReadExtras(request) ||
            request.AccessOperation is not SourceReadOperation)
        {
            return ResourceDenied();
        }

        byte[]? snapshot = null;
        try
        {
            var beforeRead = operation.LoadScopeGrantState();
            var decision = _agentSessions.AuthorizeSession(
                request.SessionToken,
                beforeRead.UnlockEpoch,
                beforeRead.PolicyGeneration,
                new AgentAccessRequest(request.AccessOperation, request.ResourceId, request.ResourceRevision));
            if (!decision.Allowed || request.ResourceId is null)
            {
                return ResourceDenied();
            }

            snapshot = operation.ReadContent(request.ResourceId, request.ResourceRevision);
            Core.Domain.StoredRegisteredSource stored;
            try
            {
                stored = Core.Domain.RegisteredSourceCodec.DecodeStored(snapshot);
            }
            catch (ArgumentException)
            {
                return ResourceDenied();
            }

            var beforeServe = operation.LoadScopeGrantState();
            var recheck = _agentSessions.AuthorizeSession(
                request.SessionToken,
                beforeServe.UnlockEpoch,
                beforeServe.PolicyGeneration,
                new AgentAccessRequest(request.AccessOperation, request.ResourceId, request.ResourceRevision));
            if (!recheck.Allowed)
            {
                return ResourceDenied();
            }

            operation.ValidateRegistration(request.ResourceId, request.ResourceRevision);
            var wire = ToSourceWire(SourceRegistryService.ToSource(stored,
                request.ResourceRevision ?? ResolveLiveResourceRevision(operation, request.ResourceId)));
            return new ServiceReply(
                CurrentProtocolVersion, "source", null, null,
                Decision: recheck,
                PolicyGeneration: recheck.PolicyGeneration,
                ResourceRevision: wire.Revision,
                Source: wire);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ResourceDenied();
        }
        finally
        {
            if (snapshot is { Length: > 0 })
            {
                CryptographicOperations.ZeroMemory(snapshot);
            }
        }
    }
    /// <summary>
    /// Scoped snapshot expansion uses the shared session authority with the
    /// distinct source.snapshot.read operation, the snapshot's own resource ID
    /// and its exact canonical revision. A source-reference or item grant can
    /// never satisfy this request, and both policy and manifest are rechecked
    /// before the encrypted snapshot bytes are returned.
    /// </summary>
    private ServiceReply HandleAgentSourceSnapshotRead(
        AuthenticatedContext context,
        ServiceRequest request,
        IVaultOperation? operation)
    {
        if (context.Kind != PrincipalKind.Agent || operation is null ||
            HasAgentResourceReadExtras(request) ||
            request.AccessOperation is not SourceSnapshotReadOperation ||
            request.ResourceId is null ||
            request.ResourceRevision is not { } expectedRevision || expectedRevision < 1)
        {
            return ResourceDenied();
        }

        SourceSnapshot? snapshot = null;
        try
        {
            var beforeRead = operation.LoadScopeGrantState();
            var decision = _agentSessions.AuthorizeSession(
                request.SessionToken,
                beforeRead.UnlockEpoch,
                beforeRead.PolicyGeneration,
                new AgentAccessRequest(SourceSnapshotReadOperation, request.ResourceId, expectedRevision));
            if (!decision.Allowed)
            {
                return ResourceDenied();
            }

            snapshot = _sourceSnapshots.Read(request.ResourceId, expectedRevision, operation);
            if (!operation.TryGetZone(request.ResourceId, out var registeredZone) ||
                !string.Equals(snapshot.Stored.ZoneId, registeredZone, StringComparison.Ordinal))
            {
                return ResourceDenied();
            }

            var beforeServe = operation.LoadScopeGrantState();
            var recheck = _agentSessions.AuthorizeSession(
                request.SessionToken,
                beforeServe.UnlockEpoch,
                beforeServe.PolicyGeneration,
                new AgentAccessRequest(SourceSnapshotReadOperation, request.ResourceId, expectedRevision));
            if (!recheck.Allowed)
            {
                return ResourceDenied();
            }

            operation.ValidateRegistration(request.ResourceId, expectedRevision);
            return new ServiceReply(
                CurrentProtocolVersion, "source_snapshot", null, null,
                Decision: recheck,
                PolicyGeneration: recheck.PolicyGeneration,
                ResourceRevision: snapshot.Revision,
                SourceSnapshot: ToSourceSnapshotWire(
                    snapshot.Stored, snapshot.Revision, Convert.ToBase64String(snapshot.Content)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return ResourceDenied();
        }
        finally
        {
            if (snapshot?.Content is { Length: > 0 } content)
            {
                CryptographicOperations.ZeroMemory(content);
            }
        }
    }


    private static RegisteredSourceWire ToSourceWire(Core.Domain.RegisteredSource source) => new(
        source.Id,
        source.ZoneId,
        source.Revision,
        source.SchemaVersion,
        ToWireToken(source.Executor.ToString()),
        ToWireToken(source.CreationMethod.ToString()),
        source.Author is { } author ? ToWireToken(author.ToString()) : "unknown",
        source.Title,
        source.Locator,
        source.Version,
        ToWireToken(source.Availability.ToString()),
        source.CapturedAtUtc,
        source.RecordedAtUtc);

    private static MemoryItemWire ToMemoryWire(Core.Domain.MemoryItem item) => new(
        item.Id,
        item.ZoneId,
        item.Revision,
        item.SchemaVersion,
        ToWireToken(item.Kind.ToString()),
        ToWireToken(item.Author.ToString()),
        ToWireToken(item.Executor.ToString()),
        ToWireToken(item.CreationMethod.ToString()),
        ToWireToken(item.Evidence.ToString()),
        ToWireToken(item.Level.ToString()),
        item.Title,
        item.Body,
        item.Sources,
        item.OccurredAtUtc,
        item.RecordedAtUtc,
        item.Applicability);

    private static string ToWireToken(string name)
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


    private ServiceReply HandleResourceRead(AuthenticatedContext context, string? resourceId, IVaultOperation? operation)
    {
        if (operation is null || SourceSnapshotIds.IsSnapshotId(resourceId))
        {
            return ResourceDenied();
        }

        if (!operation.TryGetZone(resourceId!, out var zoneId) || zoneId is null)
        {
            return ResourceDenied();
        }

        byte[] snapshot;
        try
        {
            snapshot = operation.ReadContent(resourceId!);
        }
        catch (ManagedResourceUnavailableException)
        {
            return ResourceDenied();
        }

        try
        {
            var decision = _grantAuthority.Authorize(
                context, new AccessRequest(resourceId, zoneId, ResourceReadOperation));
            if (!decision.Allowed)
            {
                CryptographicOperations.ZeroMemory(snapshot);
                return ResourceDenied();
            }

            var recheck = _grantAuthority.Authorize(
                context, new AccessRequest(resourceId, zoneId, ResourceReadOperation));
            if (!recheck.Allowed)
            {
                CryptographicOperations.ZeroMemory(snapshot);
                return ResourceDenied();
            }

            try
            {
                operation.ValidateRegistration(resourceId!);
            }
            catch (ManagedResourceUnavailableException)
            {
                CryptographicOperations.ZeroMemory(snapshot);
                return ResourceDenied();
            }

            return new ServiceReply(
                CurrentProtocolVersion, "content", null, null,
                Decision: recheck,
                PolicyGeneration: recheck.PolicyGeneration,
                ContentBase64: Convert.ToBase64String(snapshot),
                ContentType: "application/octet-stream");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(snapshot);
        }
    }

    private ServiceReply HandleAgentResourceRead(
        AuthenticatedContext context,
        ServiceRequest request,
        IVaultOperation? operation)
    {
        if (context.Kind != PrincipalKind.Agent || operation is null ||
            HasAgentResourceReadExtras(request) ||
            request.AccessOperation is not ResourceReadOperation)
        {
            return ResourceDenied();
        }

        byte[]? snapshot = null;
        try
        {
            var beforeRead = operation.LoadScopeGrantState();
            var decision = _agentSessions.AuthorizeSession(
                request.SessionToken,
                beforeRead.UnlockEpoch,
                beforeRead.PolicyGeneration,
                new AgentAccessRequest(request.AccessOperation, request.ResourceId, request.ResourceRevision));
            if (!decision.Allowed || request.ResourceId is null)
            {
                return ResourceDenied();
            }

            snapshot = operation.ReadContent(request.ResourceId, request.ResourceRevision);
            try
            {
                var registeredSource = RegisteredSourceCodec.DecodeStored(snapshot);
                if (string.Equals(registeredSource.Id, request.ResourceId, StringComparison.Ordinal))
                {
                    return ResourceDenied();
                }
            }
            catch (ArgumentException)
            {
                // Generic resources retain their explicit resource.read path;
                // registered sources must use the metadata-only source API.
            }

            var beforeServe = operation.LoadScopeGrantState();
            var recheck = _agentSessions.AuthorizeSession(
                request.SessionToken,
                beforeServe.UnlockEpoch,
                beforeServe.PolicyGeneration,
                new AgentAccessRequest(request.AccessOperation, request.ResourceId, request.ResourceRevision));
            if (!recheck.Allowed)
            {
                return ResourceDenied();
            }

            operation.ValidateRegistration(request.ResourceId, request.ResourceRevision);
            return new ServiceReply(
                CurrentProtocolVersion, "content", null, null,
                Decision: recheck,
                PolicyGeneration: recheck.PolicyGeneration,
                ContentBase64: Convert.ToBase64String(snapshot),
                ContentType: "application/octet-stream");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ResourceDenied();
        }
        finally
        {
            if (snapshot is { Length: > 0 })
            {
                CryptographicOperations.ZeroMemory(snapshot);
            }
        }
    }
    private ServiceReply HandleAgentMemoryRead(
        AuthenticatedContext context,
        ServiceRequest request,
        IVaultOperation? operation)
    {
        if (context.Kind != PrincipalKind.Agent || operation is null ||
            HasAgentResourceReadExtras(request) ||
            request.AccessOperation is not "memory.read")
        {
            return ResourceDenied();
        }

        byte[]? snapshot = null;
        try
        {
            var beforeRead = operation.LoadScopeGrantState();
            var decision = _agentSessions.AuthorizeSession(
                request.SessionToken,
                beforeRead.UnlockEpoch,
                beforeRead.PolicyGeneration,
                new AgentAccessRequest(request.AccessOperation, request.ResourceId, request.ResourceRevision));
            if (!decision.Allowed || request.ResourceId is null)
            {
                return ResourceDenied();
            }

            snapshot = operation.ReadContent(request.ResourceId, request.ResourceRevision);
            Core.Domain.StoredMemoryEnvelope stored;
            try
            {
                stored = Core.Domain.MemoryEnvelopeCodec.DecodeStored(snapshot);
            }
            catch (ArgumentException)
            {
                return ResourceDenied();
            }

            var beforeServe = operation.LoadScopeGrantState();
            var recheck = _agentSessions.AuthorizeSession(
                request.SessionToken,
                beforeServe.UnlockEpoch,
                beforeServe.PolicyGeneration,
                new AgentAccessRequest(request.AccessOperation, request.ResourceId, request.ResourceRevision));
            if (!recheck.Allowed)
            {
                return ResourceDenied();
            }

            operation.ValidateRegistration(request.ResourceId, request.ResourceRevision);
            var wire = ToMemoryWire(MemoryItemService.ToItem(stored, request.ResourceRevision ?? ResolveLiveResourceRevision(operation, request.ResourceId)));
            return new ServiceReply(
                CurrentProtocolVersion, "memory", null, null,
                Decision: recheck,
                PolicyGeneration: recheck.PolicyGeneration,
                ResourceRevision: wire.Revision,
                MemoryItem: wire);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ResourceDenied();
        }
        finally
        {
            if (snapshot is { Length: > 0 })
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(snapshot);
            }
        }
    }

    /// <summary>
    /// Permission-filtered collection view for one redeemed agent session.
    /// The view is projected from the session's frozen approved snapshot
    /// through the single Core projection contract, never from live
    /// collection membership: a member added to the collection after the
    /// grant, a renamed title, a moved path or a new revision cannot enter a
    /// live view, and a subset grant never discloses the members, titles or
    /// counts it was not approved for. Every denial is the generic session
    /// error, so the reply distinguishes nothing about unavailable scope.
    /// </summary>
    private ServiceReply HandleAgentCollectionView(
        AuthenticatedContext context,
        ServiceRequest request,
        IVaultOperation? operation)
    {
        if (context.Kind != PrincipalKind.Agent)
        {
            return Error("forbidden");
        }

        if (operation is null)
        {
            return Error("vault_locked");
        }

        if (HasAgentCollectionViewExtras(request))
        {
            return Error("forbidden");
        }

        AgentSessionLookupResult lookup;
        try
        {
            var state = operation.LoadScopeGrantState();
            lookup = _agentSessions.LookupSession(request.SessionToken, state.UnlockEpoch, state.PolicyGeneration);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return Error("session_unavailable");
        }

        if (lookup.Status != AgentSessionLookupStatus.Valid || lookup.Session is null)
        {
            return lookup.Status switch
            {
                AgentSessionLookupStatus.Revoked => Error("session_revoked"),
                AgentSessionLookupStatus.Superseded => Error("session_superseded"),
                AgentSessionLookupStatus.Expired => Error("session_expired"),
                _ => Error("session_unknown")
            };
        }

        AgentCollectionView view;
        try
        {
            view = CollectionViewAuthority.ProjectSession(lookup.Session);
        }
        catch (InvalidDataException)
        {
            return Error("session_unavailable");
        }

        return new ServiceReply(
            CurrentProtocolVersion, "collection_view", "agent", null,
            PolicyGeneration: lookup.Session.PolicyGeneration,
            CollectionView: view);
    }

    private static long ResolveLiveResourceRevision(IVaultOperation operation, string resourceId)
    {
        var live = operation.ListResources().FirstOrDefault(resource =>
            string.Equals(resource.ResourceId, resourceId, StringComparison.Ordinal));
        return live?.Revision ?? throw new ManagedResourceUnavailableException();
    }

    private ServiceReply HandleResourceWrite(AuthenticatedContext context, ServiceRequest request, IVaultOperation? operation)
    {
        if (operation is null || string.IsNullOrEmpty(request.ResourceId) ||
            SourceSnapshotIds.IsSnapshotId(request.ResourceId) ||
            string.IsNullOrEmpty(request.ZoneId) || request.ResourceRevision is not { } expectedRevision ||
            string.IsNullOrEmpty(request.ContentBase64))
        {
            return ResourceDenied();
        }
        byte[]? content = null;
        try
        {
            content = Convert.FromBase64String(request.ContentBase64);
            var decision = _grantAuthority.Authorize(
                context, new AccessRequest(request.ResourceId, request.ZoneId, ResourceWriteOperation));
            if (!decision.Allowed)
            {
                return ResourceDenied();
            }

            // S2-T2 durable cutover: raw resource writes now require the live
            // revision they replace (0 for create-only). Stale or unknown
            // revisions fail closed without publishing new bytes.
            var revision = operation.WriteContent(request.ResourceId, request.ZoneId, content, expectedRevision);
            return new ServiceReply(CurrentProtocolVersion, "written", null, null,
                Decision: decision, PolicyGeneration: decision.PolicyGeneration, ResourceRevision: revision);
        }
        catch (FormatException)
        {
            return Error("invalid_request");
        }
        catch (ManagedResourceUnavailableException)
        {
            return ResourceDenied();
        }
        finally
        {
            if (content is { Length: > 0 })
            {
                CryptographicOperations.ZeroMemory(content);
            }
        }
    }

    private ServiceReply HandleScopePreview(
        AuthenticatedContext context,
        ServiceRequest request,
        IVaultOperation? operation)
    {
        if (operation is null)
        {
            return Error("vault_locked");
        }

        if (request.ExpiresAtUtc is not { } expiresAtUtc)
        {
            return Error("invalid_grant_request");
        }

        try
        {
            var available = operation.ListResources();
            if (request.CollectionId is not null && (request.ZoneId is not null || request.ResourceIds is { Length: > 0 }))
            {
                throw new ArgumentException("Select a collection, zone, or exact resources.");
            }

            var resources = request.CollectionId is null
                ? ResolveResourceSelection(available, request.ZoneId, request.ResourceIds)
                : Array.Empty<ScopeResourceRevision>();
            var destinations = request.DestinationResourceIds is { Length: > 0 } destinationIds
                ? ResolveResourceIds(available, destinationIds)
                : Array.Empty<ScopeResourceRevision>();
            var hasEgress = request.Provider is not null || request.Model is not null || request.MaximumCostUsd is not null;
            var egress = hasEgress
                ? request.Provider is not null && request.Model is not null && request.MaximumCostUsd is not null
                    ? new ScopeGrantEgress(request.Provider, request.Model, request.MaximumCostUsd.Value)
                    : throw new ArgumentException("Provider egress fields must be supplied together.")
                : null;
            var preview = request.CollectionId is null
                ? _ownerScopeGrants.Preview(context, resources, request.Operations, destinations, expiresAtUtc, egress, operation)
                : _ownerScopeGrants.PreviewCollection(
                    context, request.CollectionId, available, request.Operations, destinations, expiresAtUtc, egress, operation);
            return new ServiceReply(CurrentProtocolVersion, "scope_preview", "owner", null,
                PolicyGeneration: preview.PolicyGeneration, ScopePreview: preview);
        }
        catch (ArgumentException)
        {
            return Error("invalid_grant_request");
        }
        catch (InvalidDataException)
        {
            return Error("scope_state_unavailable");
        }
        catch (IOException)
        {
            return Error("scope_state_unavailable");
        }
    }

    private ServiceReply HandleScopeIssue(
        AuthenticatedContext context,
        string? previewId,
        IVaultOperation? operation)
    {
        if (operation is null)
        {
            return Error("vault_locked");
        }

        try
        {
            var issued = _ownerScopeGrants.Issue(context, previewId, operation);
            return new ServiceReply(CurrentProtocolVersion, "token_issued", "owner", null,
                PolicyGeneration: issued.Grant.PolicyGeneration, GrantId: issued.Grant.GrantId, Token: issued.Token);
        }
        catch (ArgumentException)
        {
            return Error("invalid_grant_request");
        }
        catch (InvalidOperationException)
        {
            return Error("scope_preview_unavailable");
        }
        catch (InvalidDataException)
        {
            return Error("scope_state_unavailable");
        }
        catch (IOException)
        {
            return Error("scope_state_unavailable");
        }
    }

    private ServiceReply HandleScopeList(AuthenticatedContext context, IVaultOperation? operation)
    {
        if (operation is null)
        {
            return Error("vault_locked");
        }

        try
        {
            var grants = _ownerScopeGrants.List(context, operation)
                .Select(grant => new OwnerScopeGrantSummary(
                    grant.GrantId, grant.Resources, grant.Operations, grant.DestinationResources,
                    grant.CreatedAtUtc, grant.ExpiresAtUtc, grant.Egress, grant.PolicyGeneration))
                .ToArray();
            return new ServiceReply(CurrentProtocolVersion, "scope_grants", "owner", null,
                PolicyGeneration: grants.Length == 0 ? 0 : grants.Max(grant => grant.PolicyGeneration),
                ScopeGrants: grants);
        }
        catch (InvalidDataException)
        {
            return Error("scope_state_unavailable");
        }
        catch (IOException)
        {
            return Error("scope_state_unavailable");
        }
    }

    private ServiceReply HandleScopeCollectionSet(
        AuthenticatedContext context,
        string? collectionId,
        string[]? resourceIds,
        IVaultOperation? operation)
    {
        if (operation is null)
        {
            return Error("vault_locked");
        }

        if (resourceIds is not { Length: > 0 })
        {
            return Error("invalid_collection_request");
        }

        try
        {
            var resources = ResolveResourceIds(operation.ListResources(), resourceIds);
            var collection = _ownerScopeGrants.SetCollection(context, collectionId, resources, operation);
            var summary = new OwnerScopeCollectionSummary(collection.CollectionId, collection.ResourceIds);
            return new ServiceReply(CurrentProtocolVersion, "collection_set", "owner", null,
                ScopeCollections: new[] { summary }, CollectionId: collection.CollectionId);
        }
        catch (ArgumentException)
        {
            return Error("invalid_collection_request");
        }
        catch (InvalidDataException)
        {
            return Error("scope_state_unavailable");
        }
        catch (IOException)
        {
            return Error("scope_state_unavailable");
        }
    }

    private ServiceReply HandleScopeCollectionList(AuthenticatedContext context, IVaultOperation? operation)
    {
        if (operation is null)
        {
            return Error("vault_locked");
        }

        try
        {
            var collections = _ownerScopeGrants.ListCollections(context, operation)
                .Select(collection => new OwnerScopeCollectionSummary(collection.CollectionId, collection.ResourceIds))
                .ToArray();
            return new ServiceReply(CurrentProtocolVersion, "scope_collections", "owner", null,
                ScopeCollections: collections);
        }
        catch (InvalidDataException)
        {
            return Error("scope_state_unavailable");
        }
        catch (IOException)
        {
            return Error("scope_state_unavailable");
        }
    }

    private ServiceReply HandleCatalogPreview(
        AuthenticatedContext context, ServiceRequest request, IVaultOperation? operation)
    {
        if (operation is null)
        {
            return Error("vault_locked");
        }

        try
        {
            var preview = _ownerCatalog.Preview(
                context, request.CatalogId, request.Label, request.Description,
                request.ResourceId, operation.ListResources(), operation);
            return new ServiceReply(CurrentProtocolVersion, "catalog_preview", "owner", null,
                CatalogPreview: preview, CatalogId: preview.CatalogId);
        }
        catch (OwnerCatalogValidationException ex)
        {
            return Error(ex.Code);
        }
        catch (InvalidDataException)
        {
            return Error("catalog_state_unavailable");
        }
        catch (IOException)
        {
            return Error("catalog_state_unavailable");
        }
    }

    private ServiceReply HandleCatalogPublish(
        AuthenticatedContext context, ServiceRequest request, IVaultOperation? operation)
    {
        if (operation is null)
        {
            return Error("vault_locked");
        }

        try
        {
            var liveResources = operation.ListResources();
            var published = _ownerCatalog.Publish(
                context, request.CatalogId, request.Label, request.Description,
                request.ResourceId, liveResources, operation);
            RefreshProjection(operation);
            return new ServiceReply(CurrentProtocolVersion, "catalog_published", "owner", null,
                CatalogEntries: new[] { published }, CatalogId: published.CatalogId);
        }
        catch (OwnerCatalogValidationException ex)
        {
            return Error(ex.Code);
        }
        catch (InvalidDataException)
        {
            return Error("catalog_state_unavailable");
        }
        catch (IOException)
        {
            return Error("catalog_state_unavailable");
        }
    }

    private ServiceReply HandleCatalogList(AuthenticatedContext context, IVaultOperation? operation)
    {
        if (operation is null)
        {
            return Error("vault_locked");
        }

        try
        {
            var entries = _ownerCatalog.List(context, operation).ToArray();
            return new ServiceReply(CurrentProtocolVersion, "catalog_entries", "owner", null,
                CatalogEntries: entries);
        }
        catch (InvalidDataException)
        {
            return Error("catalog_state_unavailable");
        }
        catch (IOException)
        {
            return Error("catalog_state_unavailable");
        }
    }

    private ServiceReply HandleCatalogWithdraw(
        AuthenticatedContext context, ServiceRequest request, IVaultOperation? operation)
    {
        if (operation is null)
        {
            return Error("vault_locked");
        }

        try
        {
            var withdrawn = _ownerCatalog.Withdraw(context, request.CatalogId, operation);
            RefreshProjection(operation);
            return new ServiceReply(CurrentProtocolVersion, "catalog_withdrawn", "owner", null,
                CatalogId: withdrawn);
        }
        catch (OwnerCatalogValidationException ex)
        {
            return Error(ex.Code);
        }
        catch (InvalidDataException)
        {
            return Error("catalog_state_unavailable");
        }
        catch (IOException)
        {
            return Error("catalog_state_unavailable");
        }
    }

    private void RefreshProjection(IVaultOperation operation)
    {
        if (_projections is null)
        {
            return;
        }

        var live = OwnerCatalogAuthority.ValidateAndNormalizeState(operation.LoadCatalogState()).Entries;
        var published = live
            .Select(OwnerCatalogAuthority.ToPublished)
            .ToArray();
        _projections.SaveProjections(published);
    }

    private ServiceReply ServeCatalogList()
    {
        try
        {
            var entries = _projections is null
                ? Array.Empty<PublishedCatalogEntry>()
                : OwnerCatalogAuthority.NormalizeProjections(_projections.LoadProjections());
            return new ServiceReply(CurrentProtocolVersion, "catalog", null, null,
                PublishedCatalog: entries);
        }
        catch (InvalidDataException)
        {
            return CatalogDenied();
        }
        catch (ArgumentException)
        {
            return CatalogDenied();
        }
        catch (IOException)
        {
            return CatalogDenied();
        }
    }

    private ServiceReply ServeCatalogQuery(string? query, bool hasExtras)
    {
        if (hasExtras || string.IsNullOrWhiteSpace(query) || query!.Length > 256)
        {
            return CatalogDenied();
        }

        var needle = query.Trim();
        try
        {
            var entries = _projections is null
                ? Array.Empty<PublishedCatalogEntry>()
                : OwnerCatalogAuthority.NormalizeProjections(_projections.LoadProjections());
            var matches = entries
                .Where(entry =>
                    entry.CatalogId.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                    entry.Label.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                    entry.Description.Contains(needle, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            return matches.Length == 0
                ? CatalogDenied()
                : new ServiceReply(CurrentProtocolVersion, "catalog", null, null,
                    PublishedCatalog: matches);
        }
        catch (InvalidDataException)
        {
            return CatalogDenied();
        }
        catch (ArgumentException)
        {
            return CatalogDenied();
        }
        catch (IOException)
        {
            return CatalogDenied();
        }
    }


    private ServiceReply HandleAgentRedeem(AuthenticatedContext context, ServiceRequest request, IVaultOperation? operation)
    {
        if (context.Kind != PrincipalKind.Agent)
        {
            return Error("forbidden");
        }

        if (operation is null)
        {
            return Error("vault_locked");
        }

        if (HasOwnerOnlyFields(request, allowGrantToken: true))
        {
            return Error("forbidden");
        }

        AgentRedeemResult redeemed;
        try
        {
            redeemed = _agentSessions.Redeem(request.Token, operation, CurrentEpoch(operation));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return Error("token_unavailable");
        }

        return redeemed.Status switch
        {
            AgentRedeemStatus.Issued when redeemed.SessionToken is not null && redeemed.Session is not null =>
                new ServiceReply(CurrentProtocolVersion, "session_issued", "agent", null,
                    PolicyGeneration: redeemed.Session.PolicyGeneration,
                    SessionToken: redeemed.SessionToken, AgentSession: redeemed.Session),
            AgentRedeemStatus.Unknown => Error("token_unknown"),
            AgentRedeemStatus.Used => Error("token_used"),
            AgentRedeemStatus.Revoked => Error("token_revoked"),
            AgentRedeemStatus.Superseded => Error("token_superseded"),
            AgentRedeemStatus.Expired => Error("token_expired"),
            _ => Error("token_unavailable"),
        };
    }

    private ServiceReply HandleAgentSessionInspect(AuthenticatedContext context, ServiceRequest request, IVaultOperation? operation)
    {
        if (context.Kind != PrincipalKind.Agent)
        {
            return Error("forbidden");
        }

        if (operation is null)
        {
            return Error("vault_locked");
        }

        if (HasOwnerOnlyFields(request))
        {
            return Error("forbidden");
        }

        AgentSessionLookupResult lookup;
        try
        {
            var state = operation.LoadScopeGrantState();
            lookup = _agentSessions.LookupSession(request.SessionToken, state.UnlockEpoch, state.PolicyGeneration);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return Error("session_unavailable");
        }

        return lookup.Status switch
        {
            AgentSessionLookupStatus.Valid when lookup.Session is not null =>
                new ServiceReply(CurrentProtocolVersion, "session_valid", "agent", null,
                    PolicyGeneration: lookup.Session.PolicyGeneration, AgentSession: lookup.Session),
            AgentSessionLookupStatus.Revoked => Error("session_revoked"),
            AgentSessionLookupStatus.Superseded => Error("session_superseded"),
            AgentSessionLookupStatus.Expired => Error("session_expired"),
            _ => Error("session_unknown")
        };
    }

    private ServiceReply HandleAgentAccessAuthorize(
        AuthenticatedContext context,
        ServiceRequest request,
        IVaultOperation? operation)
    {
        if (context.Kind != PrincipalKind.Agent)
        {
            return Error("forbidden");
        }

        if (operation is null)
        {
            return Error("vault_locked");
        }

        if (HasOwnerOnlyFields(request, allowAccessFields: true))
        {
            return Error("forbidden");
        }

        try
        {
            var state = operation.LoadScopeGrantState();
            var decision = _agentSessions.AuthorizeSession(
                request.SessionToken,
                state.UnlockEpoch,
                state.PolicyGeneration,
                new AgentAccessRequest(
                    request.AccessOperation,
                    request.ResourceId,
                    request.ResourceRevision,
                    request.DestinationResourceId,
                    request.DestinationRevision,
                    request.Provider,
                    request.Model,
                    request.EstimatedCostUsd,
                    request.InputTokens,
                    request.OutputTokens));
            return new ServiceReply(
                CurrentProtocolVersion,
                "authorization_decision",
                "agent",
                null,
                Decision: decision,
                PolicyGeneration: state.PolicyGeneration);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return Error("authorization_unavailable");
        }
    }

    private ServiceReply HandleAgentAccessRequest(AuthenticatedContext context, ServiceRequest request, IVaultOperation? operation)
    {
        if (context.Kind != PrincipalKind.Agent)
        {
            return Error("forbidden");
        }

        if (operation is null)
        {
            return Error("vault_locked");
        }

        if (HasAgentAccessRequestFields(request, forStatus: false))
        {
            return Error("forbidden");
        }

        try
        {
            var submitted = _ownerScopeGrants.SubmitAccessRequest(
                context,
                request.CatalogIds,
                request.Purpose,
                request.AgentId,
                request.AgentContext,
                request.Provider,
                request.Model,
                request.DisclosureContext,
                request.Operations,
                operation.LoadCatalogState(),
                operation.ListResources(),
                operation);
            return new ServiceReply(
                CurrentProtocolVersion,
                "access_requested",
                "agent",
                null,
                RequestId: submitted.RequestId,
                AccessRequest: ToAccessRequestSummary(submitted));
        }
        catch (OwnerAccessRequestException)
        {
            return Error("request_unavailable");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return Error("request_unavailable");
        }
    }

    private ServiceReply HandleAgentAccessRequestStatus(AuthenticatedContext context, ServiceRequest request, IVaultOperation? operation)
    {
        if (context.Kind != PrincipalKind.Agent)
        {
            return Error("forbidden");
        }

        if (operation is null)
        {
            return Error("vault_locked");
        }

        if (HasAgentAccessRequestFields(request, forStatus: true))
        {
            return Error("forbidden");
        }

        try
        {
            var status = _ownerScopeGrants.GetAccessRequestStatus(context, request.RequestId, operation);
            return status switch
            {
                OwnerScopeGrantAuthority.RequestStatusPending => new ServiceReply(
                    CurrentProtocolVersion, "access_request_pending", "agent", null, RequestId: request.RequestId),
                OwnerScopeGrantAuthority.RequestStatusApproved => new ServiceReply(
                    CurrentProtocolVersion, "access_request_approved", "agent", null, RequestId: request.RequestId),
                OwnerScopeGrantAuthority.RequestStatusRejected => new ServiceReply(
                    CurrentProtocolVersion, "access_request_rejected", "agent", null, RequestId: request.RequestId),
                _ => Error("request_unavailable")
            };
        }
        catch (OwnerAccessRequestException)
        {
            return Error("request_unavailable");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return Error("request_unavailable");
        }
    }

    private ServiceReply HandleAccessRequestList(AuthenticatedContext context, IVaultOperation? operation)
    {
        if (operation is null)
        {
            return Error("vault_locked");
        }

        try
        {
            var requests = _ownerScopeGrants.ListAccessRequests(context, operation)
                .Select(ToAccessRequestSummary)
                .ToArray();
            return new ServiceReply(
                CurrentProtocolVersion, "access_requests", "owner", null,
                AccessRequests: requests);
        }
        catch (UnauthorizedAccessException)
        {
            return Error("forbidden");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            return Error("request_unavailable");
        }
    }

    private ServiceReply HandleAccessRequestPreview(AuthenticatedContext context, ServiceRequest request, IVaultOperation? operation)
    {
        if (operation is null)
        {
            return Error("vault_locked");
        }

        try
        {
            var live = operation.ListResources();
            var preview = _ownerScopeGrants.PreviewAccessRequest(
                context, request.RequestId, operation.LoadCatalogState(), live, operation);
            var scopes = new Dictionary<string, ScopeResourceRevision>(StringComparer.Ordinal);
            foreach (var resource in preview.Resources)
            {
                scopes[resource.ResourceId] = resource;
            }

            var contents = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var resource in live)
            {
                if (scopes.TryGetValue(resource.ResourceId, out var frozen) &&
                    resource.Revision == frozen.Revision)
                {
                    var body = operation.ReadContent(resource.ResourceId, frozen.Revision);
                    try
                    {
                        contents[resource.ResourceId] = Convert.ToBase64String(body);
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(body);
                    }
                }
            }

            operation.ValidateRegistrationForAll(preview.Resources);
            var wire = new OwnerAccessRequestPreview(
                preview.PreviewId,
                preview.RequestId,
                preview.CatalogIds,
                preview.Resources,
                preview.Operations,
                preview.Purpose,
                preview.AgentId,
                preview.AgentContext,
                preview.Provider,
                preview.Model,
                preview.DisclosureContext,
                preview.CreatedAtUtc,
                preview.PolicyGeneration,
                preview.UnlockEpoch);
            return new ServiceReply(
                CurrentProtocolVersion, "access_request_preview", "owner", null,
                PolicyGeneration: wire.PolicyGeneration,
                RequestId: wire.RequestId,
                AccessRequestPreview: wire,
                ContentBase64: Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
                    System.Text.Json.JsonSerializer.Serialize(contents))),
                ResourceRevision: preview.Resources.Length);
        }
        catch (OwnerAccessRequestException ex)
        {
            return Error(ex.Code);
        }
        catch (UnauthorizedAccessException)
        {
            return Error("forbidden");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            return Error("request_unavailable");
        }
    }

    private ServiceReply HandleAccessRequestApprove(AuthenticatedContext context, ServiceRequest request, IVaultOperation? operation)
    {
        if (operation is null)
        {
            return Error("vault_locked");
        }

        if (request.ExpiresAtUtc is not { } expiresAtUtc)
        {
            return Error("request_invalid");
        }

        try
        {
            var live = operation.ListResources();
            var issued = _ownerScopeGrants.ApproveAccessRequest(
                context,
                request.PreviewId,
                request.ResourceIds,
                request.Operations,
                request.DestinationResourceIds,
                expiresAtUtc,
                request.Provider,
                request.Model,
                request.MaximumCostUsd,
                operation.LoadCatalogState(),
                live,
                operation);
            return new ServiceReply(
                CurrentProtocolVersion, "access_request_approved", "owner", null,
                PolicyGeneration: issued.Grant.PolicyGeneration,
                GrantId: issued.Grant.GrantId,
                Token: issued.Token,
                RequestId: request.RequestId);
        }
        catch (OwnerAccessRequestException ex)
        {
            return Error(ex.Code);
        }
        catch (ArgumentException)
        {
            // Past-expiry or otherwise malformed approve input is a fail-closed
            // deny that must stay a JSON reply: ValidateExpiry throws
            // ArgumentException before any grant is stored, so nothing is minted.
            return Error("request_invalid");
        }
        catch (UnauthorizedAccessException)
        {
            return Error("forbidden");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            return Error("request_unavailable");
        }
    }

    private ServiceReply HandleAccessRequestReject(AuthenticatedContext context, ServiceRequest request, IVaultOperation? operation)
    {
        if (operation is null)
        {
            return Error("vault_locked");
        }

        try
        {
            var rejected = _ownerScopeGrants.RejectAccessRequest(context, request.RequestId, operation);
            return new ServiceReply(
                CurrentProtocolVersion, "access_request_rejected", "owner", null,
                RequestId: rejected.RequestId,
                AccessRequest: ToAccessRequestSummary(rejected));
        }
        catch (OwnerAccessRequestException ex)
        {
            return Error(ex.Code);
        }
        catch (UnauthorizedAccessException)
        {
            return Error("forbidden");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            return Error("request_unavailable");
        }
    }

    private static OwnerAccessRequestSummary ToAccessRequestSummary(OwnerAccessRequest request) => new(
        request.RequestId,
        request.CatalogIds,
        request.Purpose,
        request.AgentId,
        request.AgentContext,
        request.Provider,
        request.Model,
        request.DisclosureContext,
        request.RequestedOperations,
        request.ResolvedResources,
        request.CreatedAtUtc,
        request.Status,
        request.ApprovedGrantId,
        request.DecidedAtUtc);

    private ServiceReply HandleOwnerSessionList(ServiceRequest request, IVaultOperation? operation)
    {
        if (operation is null)
        {
            return Error("vault_locked");
        }

        if (HasSessionAdministrationExtras(request, allowSessionId: false))
        {
            return Error("invalid_request");
        }

        try
        {
            var state = operation.LoadScopeGrantState();
            var sessions = _agentSessions.ListSessions(state.UnlockEpoch, state.PolicyGeneration);
            return new ServiceReply(
                CurrentProtocolVersion,
                "sessions_listed",
                "owner",
                null,
                PolicyGeneration: state.PolicyGeneration,
                AgentSessions: sessions);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return Error("session_unavailable");
        }
    }

    private ServiceReply HandleOwnerSessionRevoke(ServiceRequest request, IVaultOperation? operation)
    {
        if (operation is null)
        {
            return Error("vault_locked");
        }

        if (HasSessionAdministrationExtras(request, allowSessionId: true))
        {
            return Error("invalid_request");
        }

        try
        {
            var state = operation.LoadScopeGrantState();
            var result = _agentSessions.RevokeSession(
                request.SessionId, state.UnlockEpoch, state.PolicyGeneration);
            return result.Status switch
            {
                AgentSessionRevocationStatus.Revoked =>
                    new ServiceReply(
                        CurrentProtocolVersion,
                        "session_revoked",
                        "owner",
                        null,
                        PolicyGeneration: state.PolicyGeneration,
                        SessionId: request.SessionId),
                AgentSessionRevocationStatus.Expired => Error("session_expired"),
                AgentSessionRevocationStatus.Superseded => Error("session_superseded"),
                _ => Error("session_unknown")
            };
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return Error("session_unavailable");
        }
    }

    private static long CurrentEpoch(IVaultOperation operation) =>
        operation.LoadScopeGrantState().UnlockEpoch;

    /// <summary>
    /// Owner-only reconciliation read: fingerprints the live canonical
    /// resource/revision set and reports whether the materialized generation
    /// still describes it. No canonical body is decrypted here, and no agent
    /// channel operation names the index, so the index can never widen a
    /// grant or become an existence oracle.
    /// </summary>
    private ServiceReply HandleIndexStatus(AuthenticatedContext context, ServiceRequest request, IVaultOperation? operation)
    {
        if (context.Kind != PrincipalKind.Owner)
        {
            return Error("forbidden");
        }

        if (operation is null)
        {
            return Error("vault_locked");
        }

        if (HasPrivateIndexExtras(request))
        {
            return Error("invalid_request");
        }

        if (_privateIndexStore is null)
        {
            return Error("index_unavailable");
        }

        try
        {
            var liveDigest = _privateIndex.Evaluate(operation);
            var materialized = _privateIndexMaterialized;
            if (materialized is null)
            {
                return new ServiceReply(
                    CurrentProtocolVersion,
                    "index_status",
                    "owner",
                    null,
                    PrivateIndex: new PrivateIndexWire(
                        0,
                        liveDigest,
                        0,
                        Stale: false,
                        CanonicalMoved: false,
                        Array.Empty<PrivateIndexRowWire>()));
            }

            var stale = !string.Equals(materialized.CanonicalDigest, liveDigest, StringComparison.Ordinal);
            return new ServiceReply(
                CurrentProtocolVersion,
                stale ? "index_stale" : "index_status",
                "owner",
                null,
                PrivateIndex: new PrivateIndexWire(
                    materialized.SchemaVersion,
                    materialized.CanonicalDigest,
                    materialized.RowCount,
                    stale,
                    CanonicalMoved: false,
                    ProjectRows(materialized.Rows)));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return Error("index_unavailable");
        }
    }

    /// <summary>
    /// Owner-only full materialization from canonical. The canonical envelope
    /// is never written by this operation, so the file stays the only truth;
    /// a canonical set that moves throughout every rebuild attempt publishes
    /// no generation at all instead of a stale projection.
    /// </summary>
    private ServiceReply HandleIndexRebuild(AuthenticatedContext context, ServiceRequest request, IVaultOperation? operation, CancellationToken cancellationToken)
    {
        if (context.Kind != PrincipalKind.Owner)
        {
            return Error("forbidden");
        }

        if (operation is null)
        {
            return Error("vault_locked");
        }

        if (HasPrivateIndexExtras(request))
        {
            return Error("invalid_request");
        }

        if (_privateIndexStore is null)
        {
            return Error("index_unavailable");
        }

        try
        {
            var outcome = _privateIndex.Rebuild(_privateIndexStore, operation, cancellationToken);
            if (outcome.Snapshot is null)
            {
                return Error(outcome.CanonicalMoved ? "index_canonical_moved" : "index_unavailable");
            }

            _privateIndexMaterialized = outcome.Snapshot;
            return new ServiceReply(
                CurrentProtocolVersion,
                "index_rebuilt",
                "owner",
                null,
                PrivateIndex: new PrivateIndexWire(
                    outcome.Snapshot.SchemaVersion,
                    outcome.Snapshot.CanonicalDigest,
                    outcome.Snapshot.RowCount,
                    Stale: false,
                    CanonicalMoved: false,
                    ProjectRows(outcome.Snapshot.Rows)));
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // A requested lock cancels the in-flight rebuild at its batch
                // boundary and no generation is published. This is not the
                // request's own timeout (that token stays live and propagates
                // to the pipe layer as request_timeout), so the cancelled
                // rebuild fails closed under its own status.
                return Error("index_rebuild_cancelled");
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                return Error("index_unavailable");
            }
    }

    /// <summary>
    /// Projects materialized rows for the owner wire reply. The reply carries at
    /// most <see cref="MaximumIndexRows"/> rows so a large vault cannot grow an
    /// unbounded pipe message; the materialized generation itself is never
    /// truncated. Record kinds use the same camelCase wire token as every other
    /// enumeration on this surface.
    /// </summary>
    private static PrivateIndexRowWire[] ProjectRows(PrivateIndexRow[] rows) =>
        rows.Take(MaximumIndexRows).Select(row => new PrivateIndexRowWire(
                row.ResourceId,
                row.ZoneId,
                row.Revision,
                row.CanonicalSchemaVersion,
                ToWireToken(row.RecordKind.ToString()),
                row.State,
                row.LinkedResourceIds))
            .ToArray();

    private void ResetPrivateIndex()
    {
        if (_privateIndexStore is null)
        {
            return;
        }

        try
        {
            // Drops every materialized row and releases the in-memory engine,
            // so no private derived data outlives the unlocked epoch.
            _privateIndexStore.Reset();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ObjectDisposedException)
        {
            // The engine is disposable; a failed reset leaves nothing addressable.
        }

        _privateIndexMaterialized = null;
    }

    /// <summary>
    /// The index operations take no request fields at all: the canonical set is
    /// the input, so a body- or filter-steered request fails closed instead of
    /// selecting what gets materialized.
    /// </summary>
    private static bool HasPrivateIndexExtras(ServiceRequest request) =>
        request.ResourceId is not null || request.ZoneId is not null ||
        request.ResourceIds is not null || request.ResourceRevision is not null ||
        request.CollectionId is not null || request.Query is not null ||
        request.SessionId is not null || request.CatalogId is not null ||
        request.Label is not null || request.Description is not null ||
        request.Token is not null || request.SessionToken is not null ||
        request.ContentBase64 is not null || request.Operations is not null;

    private static bool HasOwnerOnlyFields(
        ServiceRequest request,
        bool allowAccessFields = false,
        bool allowGrantToken = false) =>
        request.Passphrase is not null || request.RecoveryCode is not null ||
        request.ResourceIds is not null || request.Operations is not null ||
        request.DestinationResourceIds is not null || request.ExpiresAtUtc is not null ||
        request.MaximumCostUsd is not null || request.PreviewId is not null ||
        request.CollectionId is not null || request.ZoneId is not null ||
        request.ContentBase64 is not null || (!allowGrantToken && request.Token is not null) ||
        request.SessionId is not null || request.CatalogId is not null ||
        request.Label is not null || request.Description is not null || request.Query is not null ||
        request.CatalogIds is not null || request.Purpose is not null ||
        request.AgentId is not null || request.AgentContext is not null ||
        request.DisclosureContext is not null || request.RequestId is not null ||
        (!allowAccessFields &&
         (request.AccessOperation is not null || request.ResourceId is not null ||
          request.ResourceRevision is not null || request.DestinationResourceId is not null ||
          request.DestinationRevision is not null || request.Provider is not null ||
          request.Model is not null || request.EstimatedCostUsd is not null ||
          request.InputTokens is not null || request.OutputTokens is not null));

    private static bool HasContentWriteExtras(ServiceRequest request) =>
        request.Passphrase is not null || request.RecoveryCode is not null ||
        request.ResourceIds is not null || request.Operations is not null ||
        request.DestinationResourceIds is not null || request.ExpiresAtUtc is not null ||
        request.Provider is not null || request.Model is not null ||
        request.MaximumCostUsd is not null || request.PreviewId is not null ||
        request.CollectionId is not null || request.Token is not null ||
        request.SessionToken is not null || request.SessionId is not null ||
        request.AccessOperation is not null || request.DestinationResourceId is not null ||
        request.DestinationRevision is not null || request.EstimatedCostUsd is not null ||
        request.InputTokens is not null || request.OutputTokens is not null ||
        request.CatalogId is not null || request.Label is not null ||
        request.Description is not null || request.Query is not null ||
        request.CatalogIds is not null || request.Purpose is not null ||
        request.AgentId is not null || request.AgentContext is not null ||
        request.DisclosureContext is not null || request.RequestId is not null ||
        request.SourceLocator is not null || request.SourceFormat is not null ||
        request.SourceFormatVersion is not null || request.SourceTitle is not null ||
        request.SourceAuthor is not null || request.SourceCapturedAtUtc is not null ||
        request.SourceGeneration is not null || request.SnapshotRetainUntilUtc is not null;

    private static bool HasContentReadExtras(ServiceRequest request, bool allowSnapshotRetention = false) =>
        request.Passphrase is not null || request.RecoveryCode is not null ||
        request.ContentBase64 is not null || request.ResourceIds is not null ||
        request.Operations is not null || request.DestinationResourceIds is not null ||
        request.ExpiresAtUtc is not null || request.Provider is not null ||
        request.Model is not null || request.MaximumCostUsd is not null ||
        request.PreviewId is not null || request.CollectionId is not null ||
        request.Token is not null || request.SessionToken is not null ||
        request.SessionId is not null || request.AccessOperation is not null ||
        request.DestinationResourceId is not null || request.DestinationRevision is not null ||
        request.EstimatedCostUsd is not null || request.InputTokens is not null ||
        request.OutputTokens is not null || request.CatalogId is not null ||
        request.Label is not null || request.Description is not null ||
        request.Query is not null || request.CatalogIds is not null ||
        request.Purpose is not null || request.AgentId is not null ||
        request.AgentContext is not null || request.DisclosureContext is not null ||
        request.RequestId is not null || request.ZoneId is not null ||
        request.SourceLocator is not null || request.SourceFormat is not null ||
        request.SourceFormatVersion is not null || request.SourceTitle is not null ||
        request.SourceAuthor is not null || request.SourceCapturedAtUtc is not null ||
        request.SourceGeneration is not null ||
        (!allowSnapshotRetention && request.SnapshotRetainUntilUtc is not null);

    /// <summary>
    /// S3-T1 enrollment carries only the source ID, zone, locator, format and
    /// format version. Every other field — credentials, content, scope,
    /// session, catalog, cost and request fields — is refused, so a body can
    /// never steer enrollment toward another zone or reuse a token flow.
    /// </summary>
    private static bool HasSourceEnrollExtras(ServiceRequest request) =>
        request.Passphrase is not null || request.RecoveryCode is not null ||
        request.ContentBase64 is not null || request.ResourceIds is not null ||
        request.Operations is not null || request.DestinationResourceIds is not null ||
        request.ExpiresAtUtc is not null || request.Provider is not null ||
        request.Model is not null || request.MaximumCostUsd is not null ||
        request.PreviewId is not null || request.CollectionId is not null ||
        request.Token is not null || request.SessionToken is not null ||
        request.SessionId is not null || request.AccessOperation is not null ||
        request.ResourceRevision is not null || request.DestinationResourceId is not null ||
        request.DestinationRevision is not null || request.EstimatedCostUsd is not null ||
        request.InputTokens is not null || request.OutputTokens is not null ||
        request.CatalogId is not null || request.Label is not null ||
        request.Description is not null || request.Query is not null ||
        request.CatalogIds is not null || request.Purpose is not null ||
        request.RequestId is not null || request.SourceTitle is not null ||
        request.SourceAuthor is not null || request.SourceCapturedAtUtc is not null ||
        request.SourceGeneration is not null || request.SnapshotRetainUntilUtc is not null;

    /// <summary>
    /// S3-T1 revoke/status/ingest carry only the source ID: the zone,
    /// locator, format and version come from the encrypted enrollment state,
    /// so a request body can never re-map or re-point a source.
    /// </summary>
    private static bool HasSourceCommandExtras(ServiceRequest request, bool allowRelocationFields = false) =>
        request.ZoneId is not null || request.Passphrase is not null ||
        request.RecoveryCode is not null || request.ContentBase64 is not null ||
        request.ResourceIds is not null || request.Operations is not null ||
        request.DestinationResourceIds is not null || request.ExpiresAtUtc is not null ||
        request.Provider is not null || request.Model is not null ||
        request.MaximumCostUsd is not null || request.PreviewId is not null ||
        request.CollectionId is not null || request.Token is not null ||
        request.SessionToken is not null || request.SessionId is not null ||
        request.AccessOperation is not null || request.ResourceRevision is not null ||
        request.DestinationResourceId is not null || request.DestinationRevision is not null ||
        request.EstimatedCostUsd is not null || request.InputTokens is not null ||
        request.OutputTokens is not null || request.CatalogId is not null ||
        request.Label is not null || request.Description is not null ||
        request.Query is not null || request.CatalogIds is not null ||
        request.Purpose is not null || request.AgentId is not null ||
        request.AgentContext is not null || request.DisclosureContext is not null ||
        request.RequestId is not null ||
        (!allowRelocationFields && (request.SourceLocator is not null || request.SourceGeneration is not null)) ||
        request.SourceFormat is not null || request.SourceFormatVersion is not null ||
        request.SourceTitle is not null || request.SourceAuthor is not null ||
        request.SourceCapturedAtUtc is not null || request.SnapshotRetainUntilUtc is not null;

    /// <summary>
    /// S3-T4 capture carries only the source ID, zone, verbatim text, and the
    /// optional owner-supplied title/URL/author/date. Credentials, revisions,
    /// scope, session, catalog, cost, enrollment and every other field are
    /// refused, so a capture body can never touch another operation's state.
    /// </summary>
    private static bool HasResearchCaptureExtras(ServiceRequest request) =>
        request.Passphrase is not null || request.RecoveryCode is not null ||
        request.ResourceIds is not null || request.Operations is not null ||
        request.DestinationResourceIds is not null || request.ExpiresAtUtc is not null ||
        request.Provider is not null || request.Model is not null ||
        request.MaximumCostUsd is not null || request.PreviewId is not null ||
        request.CollectionId is not null || request.Token is not null ||
        request.SessionToken is not null || request.SessionId is not null ||
        request.AccessOperation is not null || request.ResourceRevision is not null ||
        request.DestinationResourceId is not null || request.DestinationRevision is not null ||
        request.EstimatedCostUsd is not null || request.InputTokens is not null ||
        request.OutputTokens is not null || request.CatalogId is not null ||
        request.Label is not null || request.Description is not null ||
        request.Query is not null || request.CatalogIds is not null ||
        request.Purpose is not null || request.AgentId is not null ||
        request.AgentContext is not null || request.DisclosureContext is not null ||
        request.RequestId is not null || request.SourceFormat is not null ||
        request.SourceFormatVersion is not null || request.SourceGeneration is not null ||
        request.SnapshotRetainUntilUtc is not null;

    /// <summary>
    /// S3-T4 read carries only the source ID and the optional exact revision.
    /// Content, zone, metadata, credentials, scope and session fields are
    /// refused so a read body can never steer a write or another flow.
    /// </summary>
    private static bool HasResearchReadExtras(ServiceRequest request) =>
        request.ZoneId is not null || request.Passphrase is not null ||
        request.RecoveryCode is not null || request.ContentBase64 is not null ||
        request.ResourceIds is not null || request.Operations is not null ||
        request.DestinationResourceIds is not null || request.ExpiresAtUtc is not null ||
        request.Provider is not null || request.Model is not null ||
        request.MaximumCostUsd is not null || request.PreviewId is not null ||
        request.CollectionId is not null || request.Token is not null ||
        request.SessionToken is not null || request.SessionId is not null ||
        request.AccessOperation is not null || request.DestinationResourceId is not null ||
        request.DestinationRevision is not null || request.EstimatedCostUsd is not null ||
        request.InputTokens is not null || request.OutputTokens is not null ||
        request.CatalogId is not null || request.Label is not null ||
        request.Description is not null || request.Query is not null ||
        request.CatalogIds is not null || request.Purpose is not null ||
        request.AgentId is not null || request.AgentContext is not null ||
        request.DisclosureContext is not null || request.RequestId is not null ||
        request.SourceLocator is not null || request.SourceFormat is not null ||
        request.SourceFormatVersion is not null || request.SourceTitle is not null ||
        request.SourceAuthor is not null || request.SourceCapturedAtUtc is not null ||
        request.SourceGeneration is not null || request.SnapshotRetainUntilUtc is not null;

    private static bool HasAgentAccessRequestFields(ServiceRequest request, bool forStatus) =>
        request.ZoneId is not null ||
        request.Passphrase is not null || request.RecoveryCode is not null ||
        request.ContentBase64 is not null ||
        (forStatus
            ? request.CatalogIds is not null || request.Purpose is not null ||
                request.AgentId is not null || request.AgentContext is not null ||
                request.DisclosureContext is not null || request.Operations is not null
            : request.RequestId is not null) ||
        request.ResourceIds is not null ||
        request.DestinationResourceIds is not null ||
        request.ExpiresAtUtc is not null || request.MaximumCostUsd is not null ||
        request.PreviewId is not null || request.CollectionId is not null ||
        request.Token is not null || request.SessionId is not null ||
        request.SessionToken is not null || request.ResourceId is not null ||
        request.ResourceRevision is not null || request.AccessOperation is not null ||
        request.DestinationResourceId is not null || request.DestinationRevision is not null ||
        request.EstimatedCostUsd is not null || request.InputTokens is not null ||
        request.OutputTokens is not null ||
        request.CatalogId is not null || request.Label is not null ||
        request.Description is not null || request.Query is not null;
    private static bool HasAgentResourceReadExtras(ServiceRequest request) =>
        request.ZoneId is not null ||
        request.Passphrase is not null || request.RecoveryCode is not null ||
        request.ContentBase64 is not null || request.ResourceIds is not null ||
        request.Operations is not null || request.DestinationResourceIds is not null ||
        request.ExpiresAtUtc is not null || request.Provider is not null ||
        request.Model is not null || request.MaximumCostUsd is not null ||
        request.PreviewId is not null || request.CollectionId is not null ||
        request.Token is not null || request.SessionId is not null ||
        request.DestinationResourceId is not null || request.DestinationRevision is not null ||
        request.EstimatedCostUsd is not null || request.InputTokens is not null ||
        request.OutputTokens is not null ||
        request.CatalogId is not null || request.Label is not null ||
        request.Description is not null || request.Query is not null ||
        request.CatalogIds is not null || request.Purpose is not null ||
        request.AgentId is not null || request.AgentContext is not null ||
        request.DisclosureContext is not null || request.RequestId is not null ||
        request.SourceLocator is not null || request.SourceFormat is not null ||
        request.SourceFormatVersion is not null || request.SourceTitle is not null ||
        request.SourceAuthor is not null || request.SourceCapturedAtUtc is not null ||
        request.SourceGeneration is not null || request.SnapshotRetainUntilUtc is not null;

    /// <summary>
    /// The agent collection view carries only the session bearer. Every
    /// identity, zone, scope, credential, catalog, egress and request field
    /// is refused, so a body can never steer the view toward another scope.
    /// </summary>
    private static bool HasAgentCollectionViewExtras(ServiceRequest request) =>
        request.ZoneId is not null ||
        request.Passphrase is not null || request.RecoveryCode is not null ||
        request.ContentBase64 is not null || request.ResourceIds is not null ||
        request.Operations is not null || request.DestinationResourceIds is not null ||
        request.ExpiresAtUtc is not null || request.Provider is not null ||
        request.Model is not null || request.MaximumCostUsd is not null ||
        request.PreviewId is not null || request.CollectionId is not null ||
        request.Token is not null || request.SessionId is not null ||
        request.AccessOperation is not null || request.ResourceId is not null ||
        request.ResourceRevision is not null || request.DestinationResourceId is not null ||
        request.DestinationRevision is not null ||
        request.EstimatedCostUsd is not null || request.InputTokens is not null ||
        request.OutputTokens is not null ||
        request.CatalogId is not null || request.Label is not null ||
        request.Description is not null || request.Query is not null ||
        request.CatalogIds is not null || request.Purpose is not null ||
        request.AgentId is not null || request.AgentContext is not null ||
        request.DisclosureContext is not null || request.RequestId is not null;

    /// <summary>
    /// Owner relation lifecycle transitions carry only the relation ID, the
    /// live revision they replace, and the optional rationale draft; every
    /// other field (zone, session, scope, egress, catalog) is refused.
    /// </summary>
    private static bool HasRelationTransitionExtras(ServiceRequest request) =>
        request.ZoneId is not null ||
        request.Passphrase is not null || request.RecoveryCode is not null ||
        request.ResourceIds is not null || request.Operations is not null ||
        request.DestinationResourceIds is not null || request.ExpiresAtUtc is not null ||
        request.Provider is not null || request.Model is not null ||
        request.MaximumCostUsd is not null || request.PreviewId is not null ||
        request.CollectionId is not null || request.Token is not null ||
        request.SessionToken is not null || request.SessionId is not null ||
        request.AccessOperation is not null || request.DestinationResourceId is not null ||
        request.DestinationRevision is not null || request.EstimatedCostUsd is not null ||
        request.InputTokens is not null || request.OutputTokens is not null ||
        request.CatalogId is not null || request.Label is not null ||
        request.Description is not null || request.Query is not null ||
        request.CatalogIds is not null || request.Purpose is not null ||
        request.AgentId is not null || request.AgentContext is not null ||
        request.DisclosureContext is not null || request.RequestId is not null;

    /// <summary>
    /// Agent link creation carries only the session bearer, the access
    /// operation name, and the relation draft: identity, zone, scope, and
    /// egress fields are never accepted from the agent body.
    /// </summary>
    private static bool HasAgentLinkCreateExtras(ServiceRequest request) =>
        request.ResourceId is not null || request.ZoneId is not null ||
        request.ResourceRevision is not null || request.DestinationResourceId is not null ||
        request.DestinationRevision is not null ||
        request.Passphrase is not null || request.RecoveryCode is not null ||
        request.ResourceIds is not null || request.Operations is not null ||
        request.DestinationResourceIds is not null || request.ExpiresAtUtc is not null ||
        request.Provider is not null || request.Model is not null ||
        request.MaximumCostUsd is not null || request.PreviewId is not null ||
        request.CollectionId is not null || request.Token is not null ||
        request.SessionId is not null ||
        request.EstimatedCostUsd is not null || request.InputTokens is not null ||
        request.OutputTokens is not null ||
        request.CatalogId is not null || request.Label is not null ||
        request.Description is not null || request.Query is not null ||
        request.CatalogIds is not null || request.Purpose is not null ||
        request.AgentId is not null || request.AgentContext is not null ||
        request.DisclosureContext is not null || request.RequestId is not null;

    private static bool HasCatalogDiscoveryExtras(ServiceRequest request, bool allowQuery = false) =>
        request.ZoneId is not null ||
        request.Passphrase is not null || request.RecoveryCode is not null ||
        request.ContentBase64 is not null || request.ResourceIds is not null ||
        request.Operations is not null || request.DestinationResourceIds is not null ||
        request.ExpiresAtUtc is not null || request.Provider is not null ||
        request.Model is not null || request.MaximumCostUsd is not null ||
        request.PreviewId is not null || request.CollectionId is not null ||
        request.Token is not null || request.SessionId is not null ||
        request.SessionToken is not null || request.ResourceId is not null ||
        request.ResourceRevision is not null || request.AccessOperation is not null ||
        request.DestinationResourceId is not null || request.DestinationRevision is not null ||
        request.EstimatedCostUsd is not null || request.InputTokens is not null ||
        request.OutputTokens is not null || request.CatalogId is not null ||
        request.Label is not null || request.Description is not null ||
        request.CatalogIds is not null || request.Purpose is not null ||
        request.AgentId is not null || request.AgentContext is not null ||
        request.DisclosureContext is not null || request.RequestId is not null ||
        (!allowQuery && request.Query is not null);
    private static bool HasSessionAdministrationExtras(ServiceRequest request, bool allowSessionId) =>
        request.ResourceId is not null || request.ZoneId is not null ||
        request.Passphrase is not null || request.RecoveryCode is not null ||
        request.ContentBase64 is not null || request.ResourceIds is not null ||
        request.Operations is not null || request.DestinationResourceIds is not null ||
        request.ExpiresAtUtc is not null || request.Provider is not null ||
        request.Model is not null || request.MaximumCostUsd is not null ||
        request.PreviewId is not null || request.CollectionId is not null ||
        request.Token is not null || request.SessionToken is not null ||
        request.AccessOperation is not null || request.ResourceRevision is not null ||
        request.DestinationResourceId is not null || request.DestinationRevision is not null ||
        request.EstimatedCostUsd is not null || request.InputTokens is not null ||
        request.OutputTokens is not null || request.CatalogId is not null ||
        request.Label is not null || request.Description is not null || request.Query is not null ||
        request.CatalogIds is not null || request.Purpose is not null ||
        request.AgentId is not null || request.AgentContext is not null ||
        request.DisclosureContext is not null || request.RequestId is not null ||
        (!allowSessionId && request.SessionId is not null);
    /// <summary>
    /// Owner lifecycle writes carry only the subject item ID and the draft
    /// body: the record ID, the zone, and every initial state are derived
    /// server-side, so no other field (including an executor, a state, or a
    /// lifecycle identity) is accepted from the request.
    /// </summary>
    private static bool HasLifecycleWriteExtras(ServiceRequest request) =>
        request.ZoneId is not null ||
        request.ResourceRevision is not null ||
        request.Passphrase is not null || request.RecoveryCode is not null ||
        request.ResourceIds is not null || request.Operations is not null ||
        request.DestinationResourceIds is not null || request.ExpiresAtUtc is not null ||
        request.Provider is not null || request.Model is not null ||
        request.MaximumCostUsd is not null || request.PreviewId is not null ||
        request.CollectionId is not null || request.Token is not null ||
        request.SessionToken is not null || request.SessionId is not null ||
        request.AccessOperation is not null || request.DestinationResourceId is not null ||
        request.DestinationRevision is not null || request.EstimatedCostUsd is not null ||
        request.InputTokens is not null || request.OutputTokens is not null ||
        request.CatalogId is not null || request.Label is not null ||
        request.Description is not null || request.Query is not null ||
        request.CatalogIds is not null || request.Purpose is not null ||
        request.AgentId is not null || request.AgentContext is not null ||
        request.DisclosureContext is not null || request.RequestId is not null;

    /// <summary>
    /// Owner lifecycle transitions carry only the subject item ID, the live
    /// revision they replace, and the act's own draft (rationale, explicit
    /// replacement item, temporal stamps). A zone, a state, an executor, or any
    /// session/scope field is refused.
    /// </summary>
    private static bool HasLifecycleTransitionExtras(ServiceRequest request) =>
        request.ZoneId is not null ||
        request.Passphrase is not null || request.RecoveryCode is not null ||
        request.ResourceIds is not null || request.Operations is not null ||
        request.DestinationResourceIds is not null || request.ExpiresAtUtc is not null ||
        request.Provider is not null || request.Model is not null ||
        request.MaximumCostUsd is not null || request.PreviewId is not null ||
        request.CollectionId is not null || request.Token is not null ||
        request.SessionToken is not null || request.SessionId is not null ||
        request.AccessOperation is not null || request.DestinationResourceId is not null ||
        request.DestinationRevision is not null || request.EstimatedCostUsd is not null ||
        request.InputTokens is not null || request.OutputTokens is not null ||
        request.CatalogId is not null || request.Label is not null ||
        request.Description is not null || request.Query is not null ||
        request.CatalogIds is not null || request.Purpose is not null ||
        request.AgentId is not null || request.AgentContext is not null ||
        request.DisclosureContext is not null || request.RequestId is not null;

    /// <summary>
    /// A retained historical revision read names the subject and the exact
    /// historical revision it asks for, and nothing else: the shared content
    /// read guard already refuses every other field.
    /// </summary>
    private static ScopeResourceRevision[] ResolveResourceSelection(
        IReadOnlyList<ScopeResourceRevision> available,
        string? zoneId,
        string[]? resourceIds)
    {
        if (zoneId is not null && resourceIds is { Length: > 0 })
        {
            throw new ArgumentException("Select a zone or exact resources, not both.");
        }

        if (zoneId is not null)
        {
            var selected = available.Where(resource => string.Equals(resource.ZoneId, zoneId, StringComparison.Ordinal)).ToArray();
            if (selected.Length == 0)
            {
                throw new ArgumentException("The selected zone has no managed resources.");
            }

            return selected;
        }

        if (resourceIds is not { Length: > 0 })
        {
            throw new ArgumentException("Select a zone or one or more exact resources.");
        }

        return ResolveResourceIds(available, resourceIds);
    }

    private static ScopeResourceRevision[] ResolveResourceIds(
        IReadOnlyList<ScopeResourceRevision> available,
        IReadOnlyList<string> resourceIds)
    {
        var byId = available.ToDictionary(resource => resource.ResourceId, StringComparer.Ordinal);
        var selected = new ScopeResourceRevision[resourceIds.Count];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < resourceIds.Count; index++)
        {
            var resourceId = resourceIds[index];
            if (resourceId is null || !seen.Add(resourceId) || !byId.TryGetValue(resourceId, out var resource))
            {
                throw new ArgumentException("The scope contains an unknown or duplicate resource.");
            }

            selected[index] = resource;
        }

        return selected;
    }

    private static ServiceReply ResourceDenied() =>
        new(CurrentProtocolVersion, "denied", null, "resource_unavailable",
            Decision: new AuthorizationDecision(false, "resource_unavailable", 0));

    private static ServiceReply CatalogDenied() =>
        new(CurrentProtocolVersion, "no_match", null, "catalog_no_match");

    private static ServiceReply Error(string error) => new(CurrentProtocolVersion, null, null, error);
}
