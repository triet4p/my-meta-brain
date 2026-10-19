using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

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
    int? SearchOffset = null,
    int? SearchLimit = null,
    string? SearchKind = null,
    string? SearchCurrent = null,
    int? BudgetChars = null,
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
    DateTimeOffset? SnapshotRetainUntilUtc = null,
    string? SearchLevel = null,
    DateTimeOffset? SearchFromUtc = null,
    DateTimeOffset? SearchToUtc = null,
    DateTimeOffset? SearchAsOfUtc = null,
    string? JobId = null,
    string? BaseUrl = null,
    string? ApiKey = null,
    string? Profile = null,
    int? IntervalMinutes = null);
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

public sealed record MemoryCitationWire(
    string? SourceId,
    string? Locator,
    string Availability,
    long? SourceRevision,
    Core.Domain.SourceSpan? Span,
    string Status,
    string? Warning);

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
    Core.Domain.MemoryApplicability? Applicability,
    MemoryCitationWire[]? Citations = null,
    string[]? Warnings = null);
/// <summary>
/// One S4-T2 scoped search hit. Lean by design: the approved ID and revision
/// (the T3 get-by-ID address), the kind, title, score and one short snippet,
/// plus the item's own occurred/recorded times and a stale flag for time
/// filtering — never the full body, which stays on the get-by-ID path.
/// </summary>
public sealed record MemorySearchHitWire(
    string Id,
    long Revision,
    string Kind,
    string Title,
    double Score,
    string Snippet,
    DateTimeOffset? OccurredAtUtc,
    DateTimeOffset RecordedAtUtc,
    bool Stale);

/// <summary>
/// One S4-T2 scoped search page over the session's frozen snapshot. Total is
/// the matched count inside the approved scope only (never the vault size);
/// pagination is a stable offset over the frozen ranked order, so later
/// revisions cannot shift the page under the caller.
/// </summary>
public sealed record MemorySearchPageWire(
    string Query,
    int Total,
    int Offset,
    int Limit,
    bool HasMore,
    MemorySearchHitWire[] Hits);

/// <summary>
/// One S5-T3 owner library hit: the exact canonical ID and revision the owner
/// may open, the item's own zone/kind/level, the service's own score and
/// snippet, its occurred/recorded instants and the publication state the index
/// joined at rebuild time. Never a stored body, never an embedding and never a
/// grant: the console shows what the service answered instead of ranking or
/// reinterpreting it.
/// </summary>
public sealed record LibraryHitWire(
    string Id,
    long Revision,
    string ZoneId,
    string Kind,
    string Level,
    string? Title,
    double Score,
    string Snippet,
    DateTimeOffset? OccurredAtUtc,
    DateTimeOffset RecordedAtUtc,
    string Publication);

/// <summary>
/// One S5-T4 owner review outcome. It names the pending proposal that was
/// reviewed at its exact revision, the canonical memory item the review wrote
/// under the ID derived from that proposal, and the publication state the
/// shared lifecycle record of that item now carries. It never carries the
/// proposed body: that travels on the proposal read the owner reviewed, and the
/// item itself is served as an ordinary memory item.
/// </summary>
public sealed record ProposalReviewWire(
    string ProposalId,
    long ProposalRevision,
    string Kind,
    string ItemId,
    long ItemRevision,
    string Publication,
    long LifecycleRevision,
    bool DecisionApplied);

/// <summary>
/// One S5-T3 owner library page. The applied filters are echoed so the console
/// describes the answer it received instead of the filters it hoped were used.
/// A generation that has fallen behind the vault is never served as this page:
/// the service refuses that search with <c>search_index_stale</c> until the
/// owner rebuilds the index, so a page is always a page of a generation that
/// still equals canonical.
/// </summary>
    public sealed record LibraryPageWire(
        string Query,
        string? ZoneId,
        string? CollectionId,
        string? Kind,
        string? Level,
        string Current,
        DateTimeOffset? FromUtc,
        DateTimeOffset? ToUtc,
        int Total,
        int Offset,
        int Limit,
        bool HasMore,
        LibraryHitWire[] Hits,
        DateTimeOffset? AsOfUtc = null);

/// One S4-T4 brief entry: the approved ID and frozen revision it was
/// projected from, the bounded excerpt carried under the caller's budget, and
/// the stored authorship/evidence/applicability/uncertainty signals. Never a
/// synthesis and never an instruction: every field renders stored memory
/// data as the grant already allowed it.
/// </summary>
public sealed record BriefEntryWire(
    string Id,
    long Revision,
    string Kind,
    string Author,
    string Executor,
    string CreationMethod,
    string Evidence,
    string Level,
    string? Title,
    string Excerpt,
    bool Truncated,
    DateTimeOffset? OccurredAtUtc,
    DateTimeOffset RecordedAtUtc,
    Core.Domain.MemoryApplicability? Applicability,
    MemoryCitationWire[]? Citations,
    string[]? Warnings);

/// <summary>
/// One honestly omitted scope member: an approved (ID, revision) pair left
/// out of the brief with the reason it was not selected. Identity and reason
/// only, never body text.
/// </summary>
public sealed record BriefOmissionWire(
    string Id,
    long Revision,
    string Reason);

/// <summary>
/// One S4-T4 scoped brief: a short deterministic projection of the frozen
/// approved snapshot under an explicit character budget, with typed entries,
/// honest omissions, and an uncertainty ledger. Memory data for the caller
/// to interpret, never system instructions.
/// </summary>
public sealed record MemoryBriefWire(
    string Query,
    string[] Selection,
    int BudgetChars,
    int UsedChars,
    bool Truncated,
    BriefEntryWire[] Entries,
    BriefOmissionWire[] Omitted,
    string[] Uncertain,
    long PolicyGeneration);

/// <summary>
/// One S4-T4 stored feedback record as served to its granted audience.
/// Feedback says how one seen revision behaved in use; it never upgrades
/// evidence, verification, or confirmation and never rewrites beliefs.
/// </summary>
public sealed record FeedbackWire(
    string Id,
    string ZoneId,
    long Revision,
    int SchemaVersion,
    string TargetResourceId,
    long TargetRevision,
    string Kind,
    string? Note,
    string Executor,
    string CreationMethod,
    DateTimeOffset RecordedAtUtc);

/// <summary>
/// The proposed item content inside one S4-T5 proposal, rendered exactly as
/// the agent draft carried it: declared authorship, creation method, and
/// evidence travel beside the executor the server trusted context stamped.
/// A body field is never rewritten by the proposal surface.
/// </summary>
public sealed record ProposalItemWire(
    string Kind,
    string Author,
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
/// One S4-T5 pending proposal as served to its granted audience. A proposal
/// is owner-review pending content: it names the approved inbox destination
/// it landed in, the revise subject and exact revision precondition when it
/// proposes a revision, the caller idempotency key, and the pinned content
/// hash. The claimed user confirmation is a recorded claim (R09/R12): it
/// never upgrades the declared evidence, the executor, or any belief.
/// </summary>
public sealed record ProposalWire(
    string Id,
    string ZoneId,
    long Revision,
    int SchemaVersion,
    string Kind,
    string DestinationResourceId,
    long DestinationRevision,
    string? SubjectResourceId,
    long? SubjectRevision,
    string IdempotencyKey,
    string PayloadHash,
    ProposalItemWire Item,
    string Executor,
    DateTimeOffset RecordedAtUtc,
    bool ClaimedUserConfirmed,
    ProposalCitationReviewWire? CitationReview = null);

/// <summary>
/// Computed review state of the sources one pending proposal cites, beside the
/// live state it was read against: whether a cited source moved, disappeared,
/// became unavailable, or was revoked since the proposal was recorded. It
/// reports staleness of the cited evidence only; it never rewrites the
/// proposal, its declared level or evidence, or an owner's conclusion.
/// </summary>
public sealed record ProposalCitationReviewWire(
    string State,
    string[] Reasons,
    string[] StaleSourceIds);

/// <summary>
/// One lean inbox identity: the proposal ID, its live revision, and the
/// owning zone from the encrypted manifest only, never the proposed body.
/// </summary>
public sealed record ProposalIdentityWire(string Id, long Revision, string ZoneId);

/// <summary>
/// One private evidence pin a declassification stands on, served on the owner
/// channel only: the opaque private resource and the reviewed revision. Never
/// served to discovery or to an agent session.
/// </summary>
public sealed record DeclassifyPinWire(string ResourceId, long Revision);

/// <summary>
/// Owner-only preview of a sanitized cross-zone publication: the exact content
/// the shared zone would receive beside the private pins it would stand on.
/// Writes nothing and mints no access.
/// </summary>
public sealed record DeclassifyPreviewWire(
    string DestinationResourceId,
    string DestinationZoneId,
    ProposalItemWire Item,
    DeclassifyPinWire[] Pins,
    bool PinsCurrent,
    string[] MismatchedResourceIds);

/// <summary>
/// One completed owner declassification: the shared destination, its revision,
/// the publication record revision, the encrypted lineage relation IDs and
/// whether this call is what wrote them. No private body crosses this record.
/// </summary>
public sealed record DeclassifyWire(
    string DestinationResourceId,
    long DestinationRevision,
    long LifecycleRevision,
    string[] LineageRelationIds,
    bool DecisionApplied);

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
public sealed record RelationRequestEvidenceWire(
    string Action,
    string Text,
    string Executor,
    DateTimeOffset RecordedAtUtc);

public sealed record RelationEndpointCandidateWire(
    string ResourceId,
    string EndpointKind,
    long Revision,
    string? Title);


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
    RelationReviewWire Review,
    RelationRequestEvidenceWire[]? RequestEvidence = null);

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
    LifecycleRetainedRevisionWire[] RetainedRevisions,
    LifecycleStampWire? IntentionDueAt = null);

public sealed record ScheduledIntentionWire(
    string ResourceId,
    string ZoneId,
    long LifecycleRevision,
    string State,
    string? Title,
    string BodyPreview,
    DateTimeOffset DueAt,
    bool IsDue,
    bool Notified,
    DateTimeOffset? NotifiedAtUtc,
    bool Acknowledged,
    DateTimeOffset? AcknowledgedAtUtc);

public sealed record IntentionScheduleWire(ScheduledIntentionWire[] Items);

public sealed record IntentionSchedulePassWire(int NotificationsRecorded);

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

public sealed record JobWire(
    string Id,
    string ZoneId,
    long Revision,
    string Kind,
    string State,
    JobScopeSnapshot Scope,
    JobUsage? Usage,
    JobFailureReason? Failure,
    string[]? OutputProposalIds,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc);

public sealed record SpendLedgerWire(
    decimal HistoricalReservationUsd,
    decimal RollingSpendUsd,
    SpendLedgerEntry[] Entries);

/// <summary>
/// The owner's synthesis schedule exactly as it is stored: the explicit
/// selection, the interval, the destination, the provider/model, and the
/// observed timing of the last scheduled attempt. It carries no provider
/// credential and no content.
/// </summary>
public sealed record SynthesisScheduleWire(
    bool Enabled,
    int IntervalMinutes,
    JobInputReference[] Inputs,
    string Objective,
    string TargetLevel,
    string DestinationZoneId,
    string DestinationResourceId,
    long DestinationRevision,
    string Provider,
    string Model,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? NextDueAtUtc,
    DateTimeOffset? LastRunAtUtc,
    string? LastRunJobId,
    string? LastRunOutcome);

/// <summary>
/// The model/egress settings the owner configured, reported without the API key:
/// the provider, model and endpoint the service would use, and the frozen
/// pre-send caps it enforces. A configuration that exists is never presented as
/// absent, and no credential is ever echoed.
/// </summary>
public sealed record ProviderStatusWire(
    bool Configured,
    string? Provider,
    string? Model,
    string? BaseUrl,
    decimal MaximumJobCostUsd,
    long MaximumJobInputTokens,
    long MaximumJobOutputTokens,
    decimal MaximumRolling30DayCostUsd);

/// <summary>What one evaluation of the owner's schedule did, and nothing more.</summary>
public sealed record ScheduleRunWire(
    string Outcome,
    string? JobId,
    string? JobState,
    string? FailureCode,
    DateTimeOffset? NextDueAtUtc,
    string? Message);

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
    MemorySearchPageWire? MemorySearch = null,
    MemoryBriefWire? MemoryBrief = null,
    LibraryPageWire? Library = null,
    FeedbackWire? Feedback = null,
    ProposalWire? Proposal = null,
    ProposalIdentityWire[]? Proposals = null,
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
    SourceSnapshotDescriptorWire[]? SourceSnapshots = null,
    RelationWire[]? Relations = null,
    RelationEndpointCandidateWire[]? RelationCandidates = null,
    bool? RelationCandidatesTruncated = null,
    ProposalReviewWire? ProposalReview = null,
    DeclassifyPreviewWire? DeclassifyPreview = null,
    DeclassifyWire? Declassify = null,
    JobWire? Job = null,
    JobWire[]? Jobs = null,
    SpendLedgerWire? SpendLedger = null,
    SynthesisScheduleWire? SynthesisSchedule = null,
    ProviderStatusWire? ProviderStatus = null,
    ScheduleRunWire? ScheduleRun = null,
    IntentionScheduleWire? IntentionSchedule = null,
    IntentionSchedulePassWire? IntentionSchedulePass = null);

public sealed class ServiceRequestHandler
{
    public const int CurrentProtocolVersion = 1;
    public const int MaximumIndexRows = 512;
    public const int MaximumLibraryOffset = 10000;
    public const int MaximumLibraryQueryLength = 256;
    public const string OwnerStatusOperation = "owner.status";
    public const string OwnerVaultProvisionOperation = "vault.provision";
    public const string OwnerVaultUnlockOperation = "vault.unlock";
    public const string OwnerVaultRecoverOperation = "vault.recover";
    public const string OwnerVaultLockOperation = "vault.lock";
    public const string ResourceReadOperation = "resource.read";
    public const string SourceReadOperation = "source.read";
    public const string ResourceWriteOperation = "resource.write";
    public const string OwnerScopePreviewOperation = "owner.scope.preview";
    public const string OwnerJobCreateOperation = "owner.job.create";
    public const string OwnerJobStartOperation = "owner.job.start";
    public const string OwnerJobCancelOperation = "owner.job.cancel";
    public const string OwnerJobStatusOperation = "owner.job.status";
    public const string OwnerJobListOperation = "owner.job.list";
    public const string OwnerJobLedgerOperation = "owner.job.ledger";
    public const string OwnerProviderConfigOperation = "owner.provider.config";
    public const string OwnerProviderStatusOperation = "owner.provider.status";
    public const string OwnerScheduleSetOperation = "owner.schedule.set";
    public const string OwnerScheduleDisableOperation = "owner.schedule.disable";
    public const string OwnerScheduleStatusOperation = "owner.schedule.status";
    public const string OwnerScheduleRunOperation = "owner.schedule.run";
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
    public const string AgentMemorySearchOperation = "agent.memory.search";
    public const string MemorySearchAccessOperation = "memory.search";
    public const string AgentMemoryBriefOperation = "agent.memory.brief";
    public const string MemoryContextAccessOperation = "memory.context";
    public const string AgentFeedbackOperation = "agent.feedback";
    public const string MemoryFeedbackAccessOperation = "memory.feedback";
    public const string FeedbackCreateOperation = "feedback.create";
    public const string FeedbackReadOperation = "feedback.read";
    public const string AgentFeedbackReadOperation = "agent.feedback.read";
    public const string AgentProposeOperation = "agent.propose";
    public const string ProposalCreateAccessOperation = "proposal.create";
    public const string ProposalReadOperation = "proposal.read";
    public const string ProposalListOperation = "proposal.list";
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
    public const string AgentRelationCreateOperation = "agent.relation.create";
    public const string AgentRelationSupersedeOperation = "agent.relation.supersede";
    public const string AgentRelationUnlinkOperation = "agent.relation.unlink";
    public const string AgentRelationRestoreOperation = "agent.relation.restore";
    public const string AgentRelationResolveOperation = "agent.relation.resolve";
    public const string AgentRelationListOperation = "agent.relation.list";
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
    public const string OwnerIntentionScheduleListOperation = "owner.intentions.list";
    public const string OwnerIntentionTriggerSetOperation = "owner.intention.trigger.set";
    public const string OwnerIntentionScheduleRunOperation = "owner.intention.schedule.run";
    public const string LifecycleSetValidityOperation = "lifecycle.validity.set";
    public const string LifecycleRetainRevisionOperation = "lifecycle.retain";
    public const string OwnerIndexStatusOperation = "owner.index.status";
    public const string OwnerIndexRebuildOperation = "owner.index.rebuild";
    public const string OwnerLibrarySearchOperation = "owner.library.search";
    public const string OwnerRelationListOperation = "owner.relation.list";
    public const string OwnerProposalPublishOperation = "owner.proposal.publish";
    public const string OwnerProposalRejectOperation = "owner.proposal.reject";
    public const string OwnerDeclassifyPreviewOperation = "owner.declassify.preview";
    public const string OwnerDeclassifyPublishOperation = "owner.declassify.publish";
    public const string OwnerDeclassifyProposalPublishOperation = "owner.declassify.proposal.publish";
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
    public const string SourceMappingSubmitOperation = "source.mapping.submit";
    public const string SourceMappingResolveOperation = "source.mapping.resolve";
    public const string SourceSnapshotPreserveOperation = "source.snapshot.preserve";
    public const string SourceSnapshotListOperation = "source.snapshot.list";
    public const string SourceSnapshotRetainOperation = "source.snapshot.retain";
    public const string SourceSnapshotPurgeOperation = "source.snapshot.purge";
    public const string ResearchCaptureOperation = "research.capture";
    public const string ResearchReadOperation = "research.read";

    /// <summary>
    /// Agent link creation authorizes both exact endpoints: a source endpoint
    /// from the approved resource scope and a destination endpoint from the
    /// separately approved destination scope.
    /// </summary>
    public const string RelationListAccessOperation = "relation.list";
    public const string LinkCreateOperation = "link.create";
    public const string LinkSupersedeOperation = "link.supersede";
    public const string LinkRemoveOperation = "link.remove";
    public const string LinkRestoreOperation = "link.restore";
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
    private readonly ModelJobService _modelJobs;
    private readonly SynthesisScheduleService _synthesisSchedule;
    private readonly IntentionScheduleService _intentionSchedule;

    public ServiceRequestHandler(
        GrantAuthority grantAuthority,
        IVaultLifecycle vault,
        OwnerScopeGrantAuthority ownerScopeGrants,
        AgentSessionAuthority? agentSessions = null,
        OwnerCatalogAuthority? ownerCatalog = null,
        ICatalogProjectionStore? projections = null,
        IPrivateIndexStore? privateIndex = null,
        SourceNormalizerRegistry? sourceNormalizers = null,
        ISourceSnapshotReader? sourceReader = null,
        IModelProviderAdapter? modelProvider = null,
        TimeProvider? timeProvider = null)
    {
        _modelJobs = new ModelJobService(vault, modelProvider);
        _synthesisSchedule = new SynthesisScheduleService(_modelJobs);
        _intentionSchedule = new IntentionScheduleService(timeProvider);
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

        // The owner library filters are accepted by exactly one operation, plus
        // the S7-T1 as-of instant which the agent search contract also names.
        // Any other operation carrying them fails closed here instead of
        // silently ignoring a field its own request contract does not name.
        if (HasLibraryFilterFields(request) &&
            !string.Equals(request.Operation, OwnerLibrarySearchOperation, StringComparison.Ordinal))
        {
            return Error("invalid_request");
        }

        if (request.SearchAsOfUtc is not null &&
            !string.Equals(request.Operation, OwnerLibrarySearchOperation, StringComparison.Ordinal) &&
            !string.Equals(request.Operation, AgentMemorySearchOperation, StringComparison.Ordinal))
        {
            return Error("invalid_request");
        }

        if (string.Equals(request.Operation, OwnerStatusOperation, StringComparison.Ordinal))
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
        if (string.Equals(request.Operation, AgentMemorySearchOperation, StringComparison.Ordinal))
        {
            return HandleAgentMemorySearch(context, request, operation, cancellationToken);
        }
        if (string.Equals(request.Operation, AgentMemoryBriefOperation, StringComparison.Ordinal))
        {
            return HandleAgentMemoryBrief(context, request, operation);
        }
        if (string.Equals(request.Operation, AgentCollectionViewOperation, StringComparison.Ordinal))
        {
            return HandleAgentCollectionView(context, request, operation);
        }
        if (string.Equals(request.Operation, AgentFeedbackOperation, StringComparison.Ordinal))
        {
            return HandleAgentFeedback(context, request, operation);
        }
        if (string.Equals(request.Operation, AgentFeedbackReadOperation, StringComparison.Ordinal))
        {
            return HandleAgentFeedbackRead(context, request, operation);
        }
        if (string.Equals(request.Operation, AgentProposeOperation, StringComparison.Ordinal))
        {
            return HandleAgentPropose(context, request, operation);
        }
        if (string.Equals(request.Operation, AgentSourceReadOperation, StringComparison.Ordinal))
        {
            return HandleAgentSourceRead(context, request, operation);
        }
        if (string.Equals(request.Operation, AgentSourceSnapshotReadOperation, StringComparison.Ordinal))
        {
            return HandleAgentSourceSnapshotRead(context, request, operation);
        }
        if (string.Equals(request.Operation, AgentRelationResolveOperation, StringComparison.Ordinal))
        {
            return HandleAgentRelationList(context, request, operation, resolve: true);
        }
        if (string.Equals(request.Operation, AgentRelationListOperation, StringComparison.Ordinal))
        {
            return HandleAgentRelationList(context, request, operation, resolve: false);
        }
        if (string.Equals(request.Operation, AgentRelationCreateOperation, StringComparison.Ordinal))
        {
            return HandleAgentRelationCreate(context, request, operation, supersede: false);
        }
        if (string.Equals(request.Operation, AgentRelationSupersedeOperation, StringComparison.Ordinal))
        {
            return HandleAgentRelationCreate(context, request, operation, supersede: true);
        }
        if (string.Equals(request.Operation, AgentRelationUnlinkOperation, StringComparison.Ordinal))
        {
            return HandleAgentRelationTransition(context, request, operation, restore: false);
        }
        if (string.Equals(request.Operation, AgentRelationRestoreOperation, StringComparison.Ordinal))
        {
            return HandleAgentRelationTransition(context, request, operation, restore: true);
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

        if (string.Equals(request.Operation, OwnerLibrarySearchOperation, StringComparison.Ordinal))
        {
            return HandleOwnerLibrarySearch(context, request, operation);
        }

        if (string.Equals(request.Operation, OwnerRelationListOperation, StringComparison.Ordinal))
        {
            return HandleOwnerRelationList(context, request, operation);
        }

        if (string.Equals(request.Operation, OwnerProposalPublishOperation, StringComparison.Ordinal))
        {
            return HandleOwnerProposalReview(context, request, operation, publish: true);
        }

        if (string.Equals(request.Operation, OwnerProposalRejectOperation, StringComparison.Ordinal))
        {
            return HandleOwnerProposalReview(context, request, operation, publish: false);
        }

        if (string.Equals(request.Operation, OwnerDeclassifyPreviewOperation, StringComparison.Ordinal))
        {
            return HandleOwnerDeclassifyPreview(context, request, operation);
        }

        if (string.Equals(request.Operation, OwnerDeclassifyPublishOperation, StringComparison.Ordinal))
        {
            return HandleOwnerDeclassifyPublish(context, request, operation, fromProposal: false);
        }

        if (string.Equals(request.Operation, OwnerDeclassifyProposalPublishOperation, StringComparison.Ordinal))
        {
            return HandleOwnerDeclassifyPublish(context, request, operation, fromProposal: true);
        }

        if (string.Equals(request.Operation, OwnerVaultProvisionOperation, StringComparison.Ordinal))
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

        if (string.Equals(request.Operation, OwnerVaultUnlockOperation, StringComparison.Ordinal) ||
            string.Equals(request.Operation, OwnerVaultRecoverOperation, StringComparison.Ordinal))
        {
            var recovery = string.Equals(request.Operation, OwnerVaultRecoverOperation, StringComparison.Ordinal);
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

        if (string.Equals(request.Operation, OwnerVaultLockOperation, StringComparison.Ordinal))
        {
            _modelJobs.AbortInFlightOnLockOrRestart();
            await _vault.LockAsync().ConfigureAwait(false);
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
            string.Equals(request.Operation, SourceRefreshOperation, StringComparison.Ordinal) ||
            string.Equals(request.Operation, SourceMappingSubmitOperation, StringComparison.Ordinal) ||
            string.Equals(request.Operation, SourceMappingResolveOperation, StringComparison.Ordinal))
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

        if (string.Equals(request.Operation, FeedbackCreateOperation, StringComparison.Ordinal))
        {
            return HandleFeedbackCreate(context, request, operation);
        }

        if (string.Equals(request.Operation, FeedbackReadOperation, StringComparison.Ordinal))
        {
            return HandleFeedbackRead(context, request, operation);
        }
        if (string.Equals(request.Operation, ProposalReadOperation, StringComparison.Ordinal))
        {
            return HandleProposalRead(context, request, operation);
        }
        if (string.Equals(request.Operation, ProposalListOperation, StringComparison.Ordinal))
        {
            return HandleProposalList(context, request, operation);
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
        if (string.Equals(request.Operation, OwnerIntentionScheduleListOperation, StringComparison.Ordinal))
        {
            return HandleIntentionScheduleList(context, request, operation);
        }

        if (string.Equals(request.Operation, OwnerIntentionTriggerSetOperation, StringComparison.Ordinal))
        {
            return HandleIntentionTriggerSet(context, request, operation);
        }

        if (string.Equals(request.Operation, OwnerIntentionScheduleRunOperation, StringComparison.Ordinal))
        {
            return HandleIntentionScheduleRun(context, request, operation, cancellationToken);
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
        if (string.Equals(request.Operation, OwnerJobCreateOperation, StringComparison.Ordinal))
        {
            return HandleOwnerJobCreate(context, request, operation);
        }

        if (string.Equals(request.Operation, OwnerJobStartOperation, StringComparison.Ordinal))
        {
            return await HandleOwnerJobStartAsync(context, request, operation, cancellationToken).ConfigureAwait(false);
        }

        if (string.Equals(request.Operation, OwnerJobCancelOperation, StringComparison.Ordinal))
        {
            return HandleOwnerJobCancel(context, request, operation);
        }

        if (string.Equals(request.Operation, OwnerJobStatusOperation, StringComparison.Ordinal))
        {
            return HandleOwnerJobStatus(context, request, operation);
        }

        if (string.Equals(request.Operation, OwnerJobListOperation, StringComparison.Ordinal))
        {
            return HandleOwnerJobList(context, request, operation);
        }

        if (string.Equals(request.Operation, OwnerJobLedgerOperation, StringComparison.Ordinal))
        {
            return HandleOwnerJobLedger(context, request, operation);
        }

        if (string.Equals(request.Operation, OwnerProviderConfigOperation, StringComparison.Ordinal))
        {
            return HandleOwnerProviderConfig(context, request, operation);
        }

        if (string.Equals(request.Operation, OwnerProviderStatusOperation, StringComparison.Ordinal))
        {
            return HandleOwnerProviderStatus(context, operation);
        }

        if (string.Equals(request.Operation, OwnerScheduleSetOperation, StringComparison.Ordinal))
        {
            return HandleOwnerScheduleSet(context, request, operation);
        }

        if (string.Equals(request.Operation, OwnerScheduleDisableOperation, StringComparison.Ordinal))
        {
            return HandleOwnerScheduleDisable(context, operation);
        }

        if (string.Equals(request.Operation, OwnerScheduleStatusOperation, StringComparison.Ordinal))
        {
            return HandleOwnerScheduleStatus(context, operation);
        }

        if (string.Equals(request.Operation, OwnerScheduleRunOperation, StringComparison.Ordinal))
        {
            return await HandleOwnerScheduleRunAsync(context, operation, cancellationToken).ConfigureAwait(false);
        }


        return Error("unknown_operation");
    }

    private ServiceReply HandleOwnerJobCreate(
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

        if (string.IsNullOrWhiteSpace(request.ZoneId) ||
            string.IsNullOrWhiteSpace(request.DestinationResourceId) ||
            request.DestinationRevision is not { } destRevision || destRevision < 1 ||
            string.IsNullOrWhiteSpace(request.Provider) ||
            string.IsNullOrWhiteSpace(request.Model))
        {
            return Error("invalid_request");
        }

        // The owner's selected input set arrives either as one explicit
        // resource/revision pair (single-input profiles) or as an encoded
        // selection draft on the content field (multi-input synthesis): the
        // selected inputs, the owner objective, and the requested level. Both
        // forms feed exactly one recorded scope shape.
        JobInputReference[] inputs;
        string? objective = null;
        string? targetLevel = null;
        if (request.ContentBase64 is { Length: > 0 } selectionBase64)
        {
            if (!string.IsNullOrWhiteSpace(request.ResourceId) || request.ResourceRevision is not null)
            {
                return Error("invalid_request");
            }

            byte[]? selection = null;
            try
            {
                selection = Convert.FromBase64String(selectionBase64);
                var draft = JobSelectionCodec.DecodeDraft(selection);
                inputs = draft.Inputs ?? Array.Empty<JobInputReference>();
                objective = draft.Objective;
                targetLevel = draft.TargetLevel;
            }
            catch (FormatException)
            {
                return Error("invalid_request");
            }
            catch (ArgumentException ex)
            {
                return new ServiceReply(CurrentProtocolVersion, "denied", "owner", ex.Message);
            }
            finally
            {
                if (selection is { Length: > 0 })
                {
                    CryptographicOperations.ZeroMemory(selection);
                }
            }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(request.ResourceId) ||
                request.ResourceRevision is not { } sourceRevision || sourceRevision < 1)
            {
                return Error("invalid_request");
            }

            inputs = new[] { new JobInputReference(request.ResourceId, sourceRevision) };
        }

        try
        {
            var job = _modelJobs.CreateJob(
                context,
                inputs,
                request.ZoneId,
                request.DestinationResourceId,
                destRevision,
                request.Provider,
                request.Model,
                request.MaximumCostUsd,
                operation,
                request.Profile,
                objective,
                targetLevel);
            return new ServiceReply(
                CurrentProtocolVersion,
                "job_created",
                "owner",
                null,
                Job: ToJobWire(job));
        }
        catch (ArgumentException ex)
        {
            return new ServiceReply(CurrentProtocolVersion, "denied", "owner", ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return new ServiceReply(CurrentProtocolVersion, "denied", "owner", ex.Message);
        }
        catch (InvalidDataException ex)
        {
            return new ServiceReply(CurrentProtocolVersion, "denied", "owner", ex.Message);
        }
        catch (ManagedResourceUnavailableException)
        {
            return Error("vault_locked");
        }
    }

    private async Task<ServiceReply> HandleOwnerJobStartAsync(
        AuthenticatedContext context,
        ServiceRequest request,
        IVaultOperation? operation,
        CancellationToken cancellationToken)
    {
        if (context.Kind != PrincipalKind.Owner)
        {
            return Error("forbidden");
        }

        if (operation is null)
        {
            return Error("vault_locked");
        }

        if (string.IsNullOrWhiteSpace(request.JobId))
        {
            return Error("invalid_request");
        }

        try
        {
            var job = await _modelJobs.StartJobAsync(context, request.JobId, operation, cancellationToken).ConfigureAwait(false);
            var status = job.State switch
            {
                JobState.Succeeded => "job_completed",
                JobState.Cancelled => "job_cancelled",
                _ => "job_failed"
            };

            return new ServiceReply(
                CurrentProtocolVersion,
                status,
                "owner",
                job.Failure?.Message,
                Job: ToJobWire(job));
        }
        catch (ArgumentException)
        {
            return Error("invalid_request");
        }
        catch (InvalidOperationException ex)
        {
            return new ServiceReply(CurrentProtocolVersion, "denied", "owner", ex.Message);
        }
        catch (InvalidDataException ex)
        {
            return new ServiceReply(CurrentProtocolVersion, "denied", "owner", ex.Message);
        }
        catch (ManagedResourceUnavailableException)
        {
            return Error("vault_locked");
        }
    }

    private ServiceReply HandleOwnerJobCancel(
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

        if (string.IsNullOrWhiteSpace(request.JobId))
        {
            return Error("invalid_request");
        }

        try
        {
            var job = _modelJobs.CancelJob(context, request.JobId, operation);
            return new ServiceReply(
                CurrentProtocolVersion,
                "job_cancelled",
                "owner",
                null,
                Job: ToJobWire(job));
        }
        catch (ArgumentException)
        {
            return Error("invalid_request");
        }
        catch (ManagedResourceUnavailableException)
        {
            return Error("vault_locked");
        }
    }

    private ServiceReply HandleOwnerJobStatus(
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

        if (string.IsNullOrWhiteSpace(request.JobId))
        {
            return Error("invalid_request");
        }

        try
        {
            var job = _modelJobs.GetJob(request.JobId, operation);
            return new ServiceReply(
                CurrentProtocolVersion,
                "job_status",
                "owner",
                null,
                Job: ToJobWire(job));
        }
        catch (ArgumentException)
        {
            return Error("invalid_request");
        }
        catch (ManagedResourceUnavailableException)
        {
            return Error("vault_locked");
        }
    }

    private ServiceReply HandleOwnerJobList(
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
            var jobs = _modelJobs.ListJobs(operation);
            return new ServiceReply(
                CurrentProtocolVersion,
                "job_list",
                "owner",
                null,
                Jobs: jobs.Select(ToJobWire).ToArray());
        }
        catch (ManagedResourceUnavailableException)
        {
            return Error("vault_locked");
        }
    }

    private ServiceReply HandleOwnerJobLedger(
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
            var ledger = _modelJobs.LoadSpendLedger(operation);
            var rollingSpend = ledger.CalculateRollingSpend(DateTimeOffset.UtcNow);
            return new ServiceReply(
                CurrentProtocolVersion,
                "job_ledger",
                "owner",
                null,
                SpendLedger: new SpendLedgerWire(
                    HistoricalReservationUsd: ledger.HistoricalReservationUsd,
                    RollingSpendUsd: rollingSpend,
                    Entries: ledger.Entries.ToArray()));
        }
        catch (ManagedResourceUnavailableException)
        {
            return Error("vault_locked");
        }
    }

    private ServiceReply HandleOwnerProviderConfig(
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

        if (string.IsNullOrWhiteSpace(request.Provider) ||
            string.IsNullOrWhiteSpace(request.Model) ||
            string.IsNullOrWhiteSpace(request.BaseUrl) ||
            string.IsNullOrWhiteSpace(request.ApiKey))
        {
            return Error("invalid_request");
        }

        try
        {
            _modelJobs.SaveProviderConfig(
                context,
                request.Provider.Trim(),
                request.Model.Trim(),
                request.BaseUrl.Trim(),
                request.ApiKey.Trim(),
                operation);

            return new ServiceReply(
                CurrentProtocolVersion,
                "provider_configured",
                "owner",
                null);
        }
        catch (ManagedResourceUnavailableException)
        {
            return Error("vault_locked");
        }
    }

    /// <summary>
    /// Reports the model/egress settings the owner configured — provider, model,
    /// endpoint and the frozen pre-send caps — and never the credential. A
    /// configuration that exists is reported as configured; none is reported as
    /// no configuration instead of a guessed one.
    /// </summary>
    private ServiceReply HandleOwnerProviderStatus(AuthenticatedContext context, IVaultOperation? operation)
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
            var config = _modelJobs.LoadProviderConfig(operation);
            return new ServiceReply(
                CurrentProtocolVersion,
                "provider_status",
                "owner",
                null,
                ProviderStatus: new ProviderStatusWire(
                    Configured: config is not null,
                    Provider: config?.Provider,
                    Model: config?.Model,
                    BaseUrl: config?.BaseUrl,
                    MaximumJobCostUsd: SpendLedger.MaximumJobCostUsd,
                    MaximumJobInputTokens: SpendLedger.MaximumJobInputTokens,
                    MaximumJobOutputTokens: SpendLedger.MaximumJobOutputTokens,
                    MaximumRolling30DayCostUsd: SpendLedger.MaximumRolling30DayCostUsd));
        }
        catch (ManagedResourceUnavailableException)
        {
            return Error("vault_locked");
        }
    }

    /// <summary>
    /// Enables the owner's synthesis schedule: one enabled encrypted record with
    /// the explicit selection, interval, destination and provider/model. The
    /// selection arrives in the same encoded draft shape a synthesis job uses, so
    /// the schedule and a manual job share one selection contract.
    /// </summary>
    private ServiceReply HandleOwnerScheduleSet(AuthenticatedContext context, ServiceRequest request, IVaultOperation? operation)
    {
        if (context.Kind != PrincipalKind.Owner)
        {
            return Error("forbidden");
        }

        if (operation is null)
        {
            return Error("vault_locked");
        }

        if (request.IntervalMinutes is not { } interval ||
            string.IsNullOrWhiteSpace(request.ZoneId) ||
            string.IsNullOrWhiteSpace(request.DestinationResourceId) ||
            request.DestinationRevision is not { } destinationRevision || destinationRevision < 1 ||
            string.IsNullOrWhiteSpace(request.Provider) ||
            string.IsNullOrWhiteSpace(request.Model) ||
            request.ContentBase64 is not { Length: > 0 } selectionBase64)
        {
            return Error("invalid_request");
        }

        byte[]? selection = null;
        try
        {
            selection = Convert.FromBase64String(selectionBase64);
            var draft = JobSelectionCodec.DecodeDraft(selection);
            var schedule = _synthesisSchedule.SaveEnabled(
                context,
                new SynthesisScheduleDraft(
                    interval,
                    draft,
                    request.ZoneId,
                    request.DestinationResourceId,
                    destinationRevision,
                    request.Provider,
                    request.Model),
                operation);
            return new ServiceReply(
                CurrentProtocolVersion,
                "schedule_enabled",
                "owner",
                null,
                SynthesisSchedule: ToScheduleWire(schedule));
        }
        catch (FormatException)
        {
            return Error("invalid_request");
        }
        catch (ArgumentException ex)
        {
            return new ServiceReply(CurrentProtocolVersion, "denied", "owner", ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return new ServiceReply(CurrentProtocolVersion, "denied", "owner", ex.Message);
        }
        catch (InvalidDataException ex)
        {
            return new ServiceReply(CurrentProtocolVersion, "denied", "owner", ex.Message);
        }
        catch (ManagedResourceUnavailableException)
        {
            return Error("vault_locked");
        }
        finally
        {
            if (selection is { Length: > 0 })
            {
                CryptographicOperations.ZeroMemory(selection);
            }
        }
    }

    /// <summary>
    /// Disables the owner's schedule, keeping its configuration. This is what
    /// stops scheduled runs: a due check on a disabled schedule creates nothing.
    /// </summary>
    private ServiceReply HandleOwnerScheduleDisable(AuthenticatedContext context, IVaultOperation? operation)
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
            var schedule = _synthesisSchedule.Disable(context, operation);
            return new ServiceReply(
                CurrentProtocolVersion,
                "schedule_disabled",
                "owner",
                null,
                SynthesisSchedule: ToScheduleWire(schedule));
        }
        catch (InvalidOperationException ex)
        {
            return new ServiceReply(CurrentProtocolVersion, "denied", "owner", ex.Message);
        }
        catch (ArgumentException ex)
        {
            return new ServiceReply(CurrentProtocolVersion, "denied", "owner", ex.Message);
        }
        catch (ManagedResourceUnavailableException)
        {
            return Error("vault_locked");
        }
    }

    /// <summary>
    /// Reports the owner's stored schedule, or states that none is configured.
    /// Nothing is created by reading it.
    /// </summary>
    private ServiceReply HandleOwnerScheduleStatus(AuthenticatedContext context, IVaultOperation? operation)
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
            var schedule = _synthesisSchedule.TryRead(operation);
            return schedule is null
                ? new ServiceReply(CurrentProtocolVersion, "schedule_absent", "owner", null)
                : new ServiceReply(
                    CurrentProtocolVersion,
                    "schedule_status",
                    "owner",
                    null,
                    SynthesisSchedule: ToScheduleWire(schedule));
        }
        catch (ManagedResourceUnavailableException)
        {
            return Error("vault_locked");
        }
    }

    /// <summary>
    /// Evaluates the owner's schedule once through the shared schedule policy and
    /// the shared model job lifecycle: at most one bounded synthesis job when the
    /// schedule is enabled and due, and nothing at all otherwise. The service's
    /// own background loop calls exactly this path, so what the owner can trigger
    /// by hand is what runs unattended — never a second, weaker policy.
    /// </summary>
    private async Task<ServiceReply> HandleOwnerScheduleRunAsync(
        AuthenticatedContext context,
        IVaultOperation? operation,
        CancellationToken cancellationToken)
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
            var run = await _synthesisSchedule.RunDueAsync(context, operation, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
            return new ServiceReply(
                CurrentProtocolVersion,
                "schedule_run",
                "owner",
                null,
                ScheduleRun: new ScheduleRunWire(
                    run.Outcome,
                    run.JobId,
                    run.JobState,
                    run.FailureCode,
                    run.NextDueAtUtc,
                    run.Message));
        }
        catch (ArgumentException ex)
        {
            return new ServiceReply(CurrentProtocolVersion, "denied", "owner", ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return new ServiceReply(CurrentProtocolVersion, "denied", "owner", ex.Message);
        }
        catch (InvalidDataException ex)
        {
            return new ServiceReply(CurrentProtocolVersion, "denied", "owner", ex.Message);
        }
        catch (ManagedResourceUnavailableException)
        {
            return Error("vault_locked");
        }
    }

    private static SynthesisScheduleWire ToScheduleWire(StoredSynthesisSchedule schedule) => new(
        Enabled: schedule.Enabled,
        IntervalMinutes: schedule.IntervalMinutes,
        Inputs: schedule.Inputs,
        Objective: schedule.Objective,
        TargetLevel: schedule.TargetLevel,
        DestinationZoneId: schedule.DestinationZoneId,
        DestinationResourceId: schedule.DestinationResourceId,
        DestinationRevision: schedule.DestinationRevision,
        Provider: schedule.Provider,
        Model: schedule.Model,
        CreatedAtUtc: schedule.CreatedAtUtc,
        UpdatedAtUtc: schedule.UpdatedAtUtc,
        NextDueAtUtc: schedule.NextDueAtUtc,
        LastRunAtUtc: schedule.LastRunAtUtc,
        LastRunJobId: schedule.LastRunJobId,
        LastRunOutcome: schedule.LastRunOutcome);

    private static JobWire ToJobWire(JobRecord job) => new(
        Id: job.Id,
        ZoneId: job.ZoneId,
        Revision: job.Revision,
        Kind: job.Kind.ToString(),
        State: job.State.ToString(),
        Scope: job.Scope,
        Usage: job.Usage,
        Failure: job.Failure,
        OutputProposalIds: job.OutputProposalIds,
        CreatedAtUtc: job.CreatedAtUtc,
        StartedAtUtc: job.StartedAtUtc,
        CompletedAtUtc: job.CompletedAtUtc);

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
                ResourceRevision: created.Revision, MemoryItem: ToMemoryWire(created, operation));
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
                ResourceRevision: imported.Revision, MemoryItem: ToMemoryWire(imported, operation));
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
                ResourceRevision: revised.Revision, MemoryItem: ToMemoryWire(revised, operation));
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
                ResourceRevision: item.Revision, MemoryItem: ToMemoryWire(item, operation));
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
    /// S4-T4 owner feedback create: append-only over the shared encrypted
    /// store. The target must exist at exactly the seen revision; the record
    /// keeps that anchor as truthful history. Nothing here edits the target,
    /// upgrades evidence, or rewrites beliefs.
    /// </summary>
    private ServiceReply HandleFeedbackCreate(AuthenticatedContext context, ServiceRequest request, IVaultOperation? operation)
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
            var created = new FeedbackService().Create(context, request.ResourceId, request.ZoneId, draft, operation);
            return new ServiceReply(CurrentProtocolVersion, "feedback_created", "owner", null,
                ResourceRevision: created.Revision, Feedback: ToFeedbackWire(created));
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
    /// S4-T4 owner feedback read: the stored record at the pinned revision.
    /// </summary>
    private ServiceReply HandleFeedbackRead(AuthenticatedContext context, ServiceRequest request, IVaultOperation? operation)
    {
        if (context.Kind != PrincipalKind.Owner || operation is null || HasContentReadExtras(request) ||
            string.IsNullOrEmpty(request.ResourceId))
        {
            return Error("invalid_request");
        }

        try
        {
            var feedback = new FeedbackService().Read(context, request.ResourceId, request.ResourceRevision, operation);
            return new ServiceReply(CurrentProtocolVersion, "feedback", "owner", null,
                ResourceRevision: feedback.Revision, Feedback: ToFeedbackWire(feedback));
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
    /// S4-T5 owner proposal read: the stored pending proposal at the pinned
    /// revision, exactly as the agent proposed it — destination, revise
    /// subject and precondition, cited sources, declared authorship beside
    /// the agent executor, and the recorded user-confirmation claim (a claim,
    /// never evidence). The owner channel is the review surface; the agent
    /// surface has no proposal read at all.
    /// </summary>
    private ServiceReply HandleProposalRead(AuthenticatedContext context, ServiceRequest request, IVaultOperation? operation)
    {
        if (context.Kind != PrincipalKind.Owner || operation is null || HasContentReadExtras(request) ||
            string.IsNullOrEmpty(request.ResourceId))
        {
            return Error("invalid_request");
        }

        try
        {
            var proposal = new ProposalService().Read(request.ResourceId, request.ResourceRevision, operation);
            return new ServiceReply(CurrentProtocolVersion, "proposal", "owner", null,
                ResourceRevision: proposal.Revision, Proposal: ToProposalWire(proposal, operation));
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
    /// S4-T5 owner inbox listing: lean proposal identities from the
    /// encrypted manifest (ID, live revision, owning zone), never the
    /// proposed body. Pending content becomes readable one proposal at a
    /// time through <see cref="HandleProposalRead"/>.
    /// </summary>
    private ServiceReply HandleProposalList(AuthenticatedContext context, ServiceRequest request, IVaultOperation? operation)
    {
        if (context.Kind != PrincipalKind.Owner || operation is null || HasContentReadExtras(request))
        {
            return Error("invalid_request");
        }

        return new ServiceReply(CurrentProtocolVersion, "proposals", "owner", null,
            Proposals: ProposalService.List(operation)
                .Select(identity => new ProposalIdentityWire(identity.Id, identity.Revision, identity.ZoneId))
                .ToArray());
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
        if (string.Equals(request.Operation, SourceMappingSubmitOperation, StringComparison.Ordinal))
        {
            // S5-T7 mapping review submission carries only the source ID, the
            // submitted locator and the unresolved-field detail. Zone, format
            // and version travel only when the owner can state them; every
            // other field is refused so a submission can never steer another
            // flow, and the transcript is never opened to guess the mapping.
            if (HasSourceMappingSubmitExtras(request) ||
                string.IsNullOrEmpty(request.ResourceId) || string.IsNullOrEmpty(request.SourceLocator))
            {
                return Error("invalid_request");
            }

            try
            {
                var held = _sourceIngestion.SubmitMappingUnknown(
                    context, request.ResourceId, request.SourceLocator,
                    request.ZoneId, request.SourceFormat, request.SourceFormatVersion,
                    ResolveMappingDetail(request), operation);
                return new ServiceReply(CurrentProtocolVersion, "source_mapping_held", "owner", null,
                    SourceEnrollment: ToSourceEnrollmentWire(
                        new SourceEnrollmentStatus(
                            held.SourceId, "(unresolved)", "(unresolved)", 0, 0,
                            SourceEnrollmentLife.Quarantined, 0, held.RecordedAtUtc, null, held,
                            0, 0, 0, 0, null,
                            MetaBrain.Core.Domain.SourceAvailabilityObservation.NotChecked, null, null, null, null, false, 0, 0, 0)));
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

        if (string.Equals(request.Operation, SourceMappingResolveOperation, StringComparison.Ordinal))
        {
            // S5-T7 mapping resolution withdrawal carries only the source ID:
            // the held locator is never opened and no zone is ever defaulted.
            if (HasSourceCommandExtras(request) || string.IsNullOrEmpty(request.ResourceId))
            {
                return Error("invalid_request");
            }

            try
            {
                _sourceIngestion.RevokeMappingUnknown(context, request.ResourceId, operation);
                return new ServiceReply(CurrentProtocolVersion, "source_mapping_withdrawn", "owner", null);
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
                    SourceQuarantineReason.MappingUnknown => "mapping_unknown",
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
                    SourceQuarantineReason.MappingUnknown => "mapping_unknown",
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
    /// source); derivation cycles are refused with their own code so an owner
    /// caller can say which rule rejected the link. A duplicate create fails
    /// closed through the vault revision gate.
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
                ResourceRevision: created.Relation.Revision, Relation: ToRelationWire(created.Relation, created.Review, includeRequestEvidence: true));
        }
        catch (FormatException)
        {
            return Error("invalid_request");
        }
        catch (RelationCycleException)
        {
            return Error("relation_cycle");
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
    /// explicit recorded rationale instead of a silent edit. Reactivating a
    /// derivation edge is re-checked against the current graph and refused with
    /// the cycle code when the graph has moved since the link was retracted.
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
                ResourceRevision: result.Relation.Revision, Relation: ToRelationWire(result.Relation, result.Review, includeRequestEvidence: true));
        }
        catch (FormatException)
        {
            return Error("invalid_request");
        }
        catch (RelationCycleException)
        {
            return Error("relation_cycle");
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
                ResourceRevision: result.Relation.Revision, Relation: ToRelationWire(result.Relation, result.Review, includeRequestEvidence: true));
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
    private ServiceReply HandleAgentRelationCreate(
        AuthenticatedContext context,
        ServiceRequest request,
        IVaultOperation? operation,
        bool supersede)
    {
        var accessOperation = supersede ? LinkSupersedeOperation : LinkCreateOperation;
        if (context.Kind != PrincipalKind.Agent || operation is null ||
            HasAgentLinkCreateExtras(request) ||
            request.AccessOperation != accessOperation ||
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

            if (parsed.From is null || parsed.To is null ||
                (supersede
                    ? parsed.Kind != Core.Domain.RelationKind.Supersedes
                    : parsed.Kind == Core.Domain.RelationKind.Supersedes))
            {
                return ResourceDenied();
            }

            AgentAccessRequest Authorize() => new(
                accessOperation,
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

            var lookup = _agentSessions.LookupSession(
                request.SessionToken,
                beforeWrite.UnlockEpoch,
                beforeWrite.PolicyGeneration);
            if (lookup.Status != AgentSessionLookupStatus.Valid || lookup.Session is null ||
                !HasEndpointReadRights(lookup.Session, parsed.From, destinationScope: false) ||
                !HasEndpointReadRights(lookup.Session, parsed.To, destinationScope: true))
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
    /// Resolves endpoint labels or lists only relation metadata whose two
    /// pinned endpoints are readable in the caller's frozen snapshot.
    /// </summary>
    private ServiceReply HandleAgentRelationList(
        AuthenticatedContext context,
        ServiceRequest request,
        IVaultOperation? operation,
        bool resolve)
    {
        if (context.Kind != PrincipalKind.Agent || operation is null ||
            HasAgentRelationListExtras(request, resolve) ||
            request.AccessOperation != RelationListAccessOperation ||
            (resolve
                ? string.IsNullOrWhiteSpace(request.Query) || request.Query!.Length > 256
                : request.Query is not null))
        {
            return ResourceDenied();
        }

        try
        {
            var beforeRead = operation.LoadScopeGrantState();
            var decision = _agentSessions.AuthorizeSession(
                request.SessionToken,
                beforeRead.UnlockEpoch,
                beforeRead.PolicyGeneration,
                new AgentAccessRequest(RelationListAccessOperation));
            if (!decision.Allowed)
            {
                return ResourceDenied();
            }

            var lookup = _agentSessions.LookupSession(
                request.SessionToken,
                beforeRead.UnlockEpoch,
                beforeRead.PolicyGeneration);
            if (lookup.Status != AgentSessionLookupStatus.Valid || lookup.Session is null)
            {
                return ResourceDenied();
            }

            var snapshot = lookup.Session;
            if (resolve)
            {
                var result = new RelationService().ResolveEndpoints(
                    context,
                    request.Query!,
                    snapshot.Resources,
                    snapshot.DestinationResources,
                    snapshot.Operations.Contains(MemoryReadOperation, StringComparer.Ordinal),
                    snapshot.Operations.Contains(SourceReadOperation, StringComparer.Ordinal),
                    operation);
                var beforeServe = operation.LoadScopeGrantState();
                var recheck = _agentSessions.AuthorizeSession(
                    request.SessionToken,
                    beforeServe.UnlockEpoch,
                    beforeServe.PolicyGeneration,
                    new AgentAccessRequest(RelationListAccessOperation));
                if (!recheck.Allowed)
                {
                    return ResourceDenied();
                }

                foreach (var candidate in result.Candidates)
                {
                    operation.ValidateRegistration(candidate.ResourceId, candidate.Revision);
                }

                var ambiguous = result.HasMore || result.Candidates.Length > 1;
                var status = ambiguous
                    ? "relation_ambiguous"
                    : result.Candidates.Length == 1 ? "relation_resolved" : "relation_no_match";
                return new ServiceReply(
                    CurrentProtocolVersion, status, null, null,
                    Decision: recheck,
                    PolicyGeneration: recheck.PolicyGeneration,
                    RelationCandidates: result.Candidates.Select(candidate => new RelationEndpointCandidateWire(
                        candidate.ResourceId,
                        ToWireToken(candidate.EndpointKind.ToString()),
                        candidate.Revision,
                        candidate.Title)).ToArray(),
                    RelationCandidatesTruncated: result.HasMore);
            }

            bool EndpointAllowed(Core.Domain.RelationEndpoint endpoint) =>
                HasEndpointReadRights(snapshot, endpoint, destinationScope: true);
            var relations = new RelationService().List(context, operation, EndpointAllowed);
            var beforeServeList = operation.LoadScopeGrantState();
            var recheckList = _agentSessions.AuthorizeSession(
                request.SessionToken,
                beforeServeList.UnlockEpoch,
                beforeServeList.PolicyGeneration,
                new AgentAccessRequest(RelationListAccessOperation));
            if (!recheckList.Allowed)
            {
                return ResourceDenied();
            }

            var relationWires = new RelationWire[relations.Length];
            for (var index = 0; index < relations.Length; index++)
            {
                var listed = relations[index];
                operation.ValidateRegistration(listed.Relation!.Id, listed.Relation.Revision);
                relationWires[index] = ToRelationWire(listed.Relation, listed.Review!);
            }

            return new ServiceReply(
                CurrentProtocolVersion, "relations", null, null,
                Decision: recheckList,
                PolicyGeneration: recheckList.PolicyGeneration,
                Relations: relationWires);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return ResourceDenied();
        }
    }

    /// <summary>
    /// Agent unlink/undo requires its own grant action and exact read authority
    /// for both pinned endpoints. Undo restores only to proposed.
    /// </summary>
    private ServiceReply HandleAgentRelationTransition(
        AuthenticatedContext context,
        ServiceRequest request,
        IVaultOperation? operation,
        bool restore)
    {
        var accessOperation = restore ? LinkRestoreOperation : LinkRemoveOperation;
        if (context.Kind != PrincipalKind.Agent || operation is null ||
            HasAgentRelationMutationExtras(request) ||
            request.AccessOperation != accessOperation ||
            string.IsNullOrEmpty(request.ResourceId) ||
            request.ResourceRevision is not { } expectedRevision || expectedRevision < 1 ||
            string.IsNullOrEmpty(request.ContentBase64))
        {
            return ResourceDenied();
        }

        byte[]? draft = null;
        try
        {
            draft = Convert.FromBase64String(request.ContentBase64);
            var beforeRead = operation.LoadScopeGrantState();
            var decision = _agentSessions.AuthorizeSession(
                request.SessionToken,
                beforeRead.UnlockEpoch,
                beforeRead.PolicyGeneration,
                new AgentAccessRequest(accessOperation));
            if (!decision.Allowed)
            {
                return ResourceDenied();
            }

            var lookup = _agentSessions.LookupSession(
                request.SessionToken,
                beforeRead.UnlockEpoch,
                beforeRead.PolicyGeneration);
            if (lookup.Status != AgentSessionLookupStatus.Valid || lookup.Session is null)
            {
                return ResourceDenied();
            }

            bool EndpointAllowed(Core.Domain.RelationEndpoint endpoint) =>
                HasEndpointReadRights(lookup.Session, endpoint, destinationScope: true);
            var service = new RelationService();
            var readable = service.Read(context, request.ResourceId, expectedRevision, operation, EndpointAllowed);
            if (readable.Relation is null || readable.Review is null)
            {
                return ResourceDenied();
            }

            RelationWriteResult changed;
            try
            {
                changed = restore
                    ? service.RelinkAgent(context, request.ResourceId, expectedRevision, draft, operation)
                    : service.UnlinkAgent(context, request.ResourceId, expectedRevision, draft, operation);
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
                new AgentAccessRequest(accessOperation));
            if (!recheck.Allowed)
            {
                return ResourceDenied();
            }

            operation.ValidateRegistration(changed.Relation.Id, changed.Relation.Revision);
            return new ServiceReply(
                CurrentProtocolVersion, restore ? "relation_relinked" : "relation_unlinked", null, null,
                Decision: recheck,
                PolicyGeneration: recheck.PolicyGeneration,
                ResourceRevision: changed.Relation.Revision,
                Relation: ToRelationWire(changed.Relation, changed.Review));
        }
        catch (FormatException)
        {
            return ResourceDenied();
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
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

    private static RelationWire ToRelationWire(
        Core.Domain.Relation relation,
        Core.Domain.RelationReview review,
        bool includeRequestEvidence = false) => new(
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
        ToRelationReviewWire(review),
        includeRequestEvidence
            ? relation.RequestEvidence?.Select(entry => new RelationRequestEvidenceWire(
                ToWireToken(entry.Action.ToString()),
                entry.Text,
                ToWireToken(entry.Executor.ToString()),
                entry.RecordedAtUtc)).ToArray()
            : null);

    private static RelationEndpointWire ToRelationEndpointWire(Core.Domain.RelationEndpoint endpoint) => new(
        endpoint.ResourceId,
        ToWireToken(endpoint.EndpointKind.ToString()),
        endpoint.Revision,
        endpoint.Span);

    private static RelationReviewWire ToRelationReviewWire(Core.Domain.RelationReview review) => new(
        ToWireToken(review.State.ToString()),
        ReviewReasonTokens(review.Reasons),
        review.StaleResourceIds);

    private ServiceReply HandleIntentionScheduleList(
        AuthenticatedContext context,
        ServiceRequest request,
        IVaultOperation? operation)
    {
        if (context.Kind != PrincipalKind.Owner || operation is null || !HasNoOptionalFields(request))
        {
            return Error("invalid_request");
        }

        try
        {
            var rows = _intentionSchedule.ListScheduled(context, operation)
                .Select(ToScheduledIntentionWire)
                .ToArray();
            return new ServiceReply(
                CurrentProtocolVersion,
                "intentions_listed",
                "owner",
                null,
                IntentionSchedule: new IntentionScheduleWire(rows));
        }
        catch (UnauthorizedAccessException)
        {
            return Error("forbidden");
        }
        catch (ArgumentException)
        {
            return Error("invalid_request");
        }
        catch (InvalidDataException)
        {
            return Error("intention_schedule_unavailable");
        }
    }

    private ServiceReply HandleIntentionTriggerSet(
        AuthenticatedContext context,
        ServiceRequest request,
        IVaultOperation? operation)
    {
        if (context.Kind != PrincipalKind.Owner || operation is null ||
            !HasOnlyIntentionTriggerSetFields(request) || string.IsNullOrEmpty(request.ResourceId) ||
            string.IsNullOrEmpty(request.ContentBase64))
        {
            return Error("invalid_request");
        }

        byte[]? draftBytes = null;
        try
        {
            draftBytes = Convert.FromBase64String(request.ContentBase64);
            if (draftBytes.Length is <= 0 or > 4096)
            {
                return Error("invalid_request");
            }

            var draft = JsonSerializer.Deserialize<IntentionTimeTriggerDraft>(draftBytes,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (draft is null || draft.Cancel == (draft.DueAt is not null) ||
                draft.DueAt is { Length: > 64 })
            {
                return Error("invalid_request");
            }

            var lifecycle = _intentionSchedule.SetTimeTrigger(
                context,
                request.ResourceId,
                draft.DueAt,
                draft.ExpectedLifecycleRevision,
                operation);
            return new ServiceReply(
                CurrentProtocolVersion,
                draft.Cancel ? "intention_trigger_cleared" : "intention_trigger_set",
                "owner",
                null,
                ResourceRevision: lifecycle.Revision,
                Lifecycle: ToLifecycleWire(lifecycle));
        }
        catch (FormatException)
        {
            return Error("invalid_request");
        }
        catch (JsonException)
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
            if (draftBytes is { Length: > 0 })
            {
                CryptographicOperations.ZeroMemory(draftBytes);
            }
        }
    }

    private ServiceReply HandleIntentionScheduleRun(
        AuthenticatedContext context,
        ServiceRequest request,
        IVaultOperation? operation,
        CancellationToken cancellationToken)
    {
        if (context.Kind != PrincipalKind.Owner || operation is null || !HasNoOptionalFields(request))
        {
            return Error("invalid_request");
        }

        try
        {
            var pass = _intentionSchedule.MarkDue(context, operation, cancellationToken);
            return new ServiceReply(
                CurrentProtocolVersion,
                "intention_schedule_pass",
                "owner",
                null,
                IntentionSchedulePass: new IntentionSchedulePassWire(pass.NotificationsRecorded));
        }
        catch (UnauthorizedAccessException)
        {
            return Error("forbidden");
        }
        catch (ArgumentException)
        {
            return Error("invalid_request");
        }
        catch (InvalidDataException)
        {
            return Error("intention_schedule_unavailable");
        }
    }

    private static ScheduledIntentionWire ToScheduledIntentionWire(ScheduledIntention intention) => new(
        intention.ResourceId,
        intention.ZoneId,
        intention.LifecycleRevision,
        intention.State,
        intention.Title,
        intention.BodyPreview,
        intention.DueAt,
        intention.IsDue,
        intention.Notified,
        intention.NotifiedAtUtc,
        intention.Acknowledged,
        intention.AcknowledgedAtUtc);

    private static bool HasNoOptionalFields(ServiceRequest request) =>
        request == new ServiceRequest(request.ProtocolVersion, request.Operation);

    private static bool HasOnlyIntentionTriggerSetFields(ServiceRequest request) =>
        (request with { ResourceId = null, ContentBase64 = null }) ==
        new ServiceRequest(request.ProtocolVersion, request.Operation);

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
                    ToMemoryWire(retained.Subject, operation)));
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
        record.RetainedRevisions.Select(ToLifecycleRetainedRevisionWire).ToArray(),
        ToLifecycleStampWire(record.IntentionDueAt));

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

    private static bool HasEndpointReadRights(
        AgentSessionSnapshot snapshot,
        Core.Domain.RelationEndpoint endpoint,
        bool destinationScope)
    {
        var requiredOperation = endpoint.EndpointKind switch
        {
            Core.Domain.RelationEndpointKind.MemoryItem => MemoryReadOperation,
            Core.Domain.RelationEndpointKind.RegisteredSource => SourceReadOperation,
            _ => string.Empty
        };
        if (requiredOperation.Length == 0 ||
            !snapshot.Operations.Contains(requiredOperation, StringComparer.Ordinal))
        {
            return false;
        }

        return IsInGrantScope(snapshot.Resources, endpoint.ResourceId, endpoint.Revision) ||
            (destinationScope &&
                IsInGrantScope(snapshot.DestinationResources, endpoint.ResourceId, endpoint.Revision));
    }

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


    private static FeedbackWire ToFeedbackWire(Core.Domain.Feedback feedback) => new(
        feedback.Id,
        feedback.ZoneId,
        feedback.Revision,
        feedback.SchemaVersion,
        feedback.TargetResourceId,
        feedback.TargetRevision,
        ToWireToken(feedback.Kind.ToString()),
        feedback.Note,
        ToWireToken(feedback.Executor.ToString()),
        ToWireToken(feedback.CreationMethod.ToString()),
        feedback.RecordedAtUtc);

    /// <summary>
    /// Projects one proposal for its served audience. The computed citation
    /// review is produced only when a store is supplied, which is the owner
    /// review surface: the agent proposal reply carries none, because live
    /// citation state is owner-facing information and the agent's scope
    /// authorizes the proposal, not an existence oracle over its sources.
    /// </summary>
    private static ProposalWire ToProposalWire(Core.Domain.Proposal proposal, IManagedResourceStore? store)
    {
        return new ProposalWire(
            proposal.Id,
            proposal.ZoneId,
            proposal.Revision,
            proposal.SchemaVersion,
            ToWireToken(proposal.Kind.ToString()),
            proposal.DestinationResourceId,
            proposal.DestinationRevision,
            proposal.SubjectResourceId,
            proposal.SubjectRevision,
            proposal.IdempotencyKey,
            proposal.PayloadHash,
            ToProposalItemWire(proposal.Item),
            ToWireToken(proposal.Executor.ToString()),
            proposal.RecordedAtUtc,
            proposal.ClaimedUserConfirmed,
            store is null ? null : ToProposalCitationReviewWire(ProposalService.EvaluateReview(proposal, store)));
    }

    private static ProposalCitationReviewWire ToProposalCitationReviewWire(Core.Domain.ProposalReview review) => new(
        review.NeedsReview ? "needsReview" : "current",
        ProposalReviewTokens(review.Reasons),
        review.StaleSourceIds);

    private static string[] ProposalReviewTokens(Core.Domain.ProposalReviewReason reasons)
    {
        var tokens = new List<string>();
        if (reasons.HasFlag(Core.Domain.ProposalReviewReason.CitedSourceMissing))
        {
            tokens.Add("citedSourceMissing");
        }

        if (reasons.HasFlag(Core.Domain.ProposalReviewReason.CitedSourceRevisionChanged))
        {
            tokens.Add("citedSourceRevisionChanged");
        }

        if (reasons.HasFlag(Core.Domain.ProposalReviewReason.CitedSourceUnavailable))
        {
            tokens.Add("citedSourceUnavailable");
        }

        if (reasons.HasFlag(Core.Domain.ProposalReviewReason.CitedSourceLineageChanged))
        {
            tokens.Add("citedSourceLineageChanged");
        }

        return tokens.ToArray();
    }

    /// <summary>
    /// Projects the proposed item content verbatim: declared kind, author,
    /// creation method, evidence and level, title/body, stored source
    /// references with their approved revisions and spans, times and
    /// applicability. Nothing here upgrades, substitutes, or rewrites a
    /// declared value — the executor beside it is what the trusted context
    /// stamped.
    /// </summary>
    private static ProposalItemWire ToProposalItemWire(Core.Domain.MemoryItemDraft item) => new(
        ToWireToken(item.Kind!.Value.ToString()),
        ToWireToken(item.Author!.Value.ToString()),
        ToWireToken(item.CreationMethod!.Value.ToString()),
        ToWireToken(item.Evidence!.Value.ToString()),
        ToWireToken(item.Level!.Value.ToString()),
        item.Title,
        item.Body ?? string.Empty,
        Core.Domain.MemoryItemValidator.NormalizeSources(item.Sources),
        item.OccurredAtUtc,
        item.RecordedAtUtc ?? DateTimeOffset.MinValue,
        item.Applicability);

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

    private static MemoryItemWire ToMemoryWire(Core.Domain.MemoryItem item) => ToMemoryWire(item, null);

    /// <summary>
    /// S4-T3 item detail projection. The stored body and stored citation
    /// anchors are served exactly as committed under the already-authorized
    /// exact (ID, revision) address: anchors stay truthful historical records
    /// and are never rewritten, re-anchored, or verified into current claims.
    /// Each citation is projected against the live manifest with an honest
    /// availability rendering: the stored availability is reported verbatim,
    /// while the anchor status says whether the cited source revision is still
    /// the live registered revision (current), was superseded by a newer
    /// registered revision (superseded), or names no registered revision
    /// (unknown). A superseded or unknown anchor carries a plain warning so a
    /// changed, missing, or re-enrolled source can never be served silently as
    /// verified provenance. Projection reads the encrypted manifest only; it
    /// never decrypts registry content and never widens either grant, so an
    /// item-only grant still cannot call the source API and a source-only
    /// grant still cannot read items.
    /// </summary>
    private static MemoryItemWire ToMemoryWire(Core.Domain.MemoryItem item, IManagedResourceReader? manifest) => new(
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
        item.Applicability,
        ProjectCitations(item.Sources, manifest),
        ProjectCitationWarnings(item, item.Sources, manifest, manifest as IManagedResourceStore));

    private static MemoryCitationWire[]? ProjectCitations(Core.Domain.MemorySourceReference[] sources, IManagedResourceReader? manifest)
    {
        if (sources is null || sources.Length == 0)
        {
            return null;
        }

        var projected = new MemoryCitationWire[sources.Length];
        for (var index = 0; index < sources.Length; index++)
        {
            projected[index] = ProjectCitation(sources[index], manifest);
        }

        return projected;
    }

    private static MemoryCitationWire ProjectCitation(Core.Domain.MemorySourceReference source, IManagedResourceReader? manifest)
    {
        var availability = ToWireToken((source.Availability ?? Core.Domain.SourceAvailability.Unknown).ToString());
        if (source.SourceId is null || source.SourceRevision is not { } claimed || claimed < 1 || manifest is null)
        {
            return new MemoryCitationWire(
                source.SourceId, source.Locator, availability, source.SourceRevision, source.Span,
                "unknown",
                source.SourceId is null || source.SourceRevision is null
                    ? null
                    : "citation_source_unknown");
        }

        long? live;
        try
        {
            live = SourceRegistryService.TryGetLiveRevision(manifest, source.SourceId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new MemoryCitationWire(
                source.SourceId, source.Locator, availability, source.SourceRevision, source.Span,
                "unknown", "citation_source_unknown");
        }

        if (live is null)
        {
            return new MemoryCitationWire(
                source.SourceId, source.Locator, availability, source.SourceRevision, source.Span,
                "unknown", "citation_source_unknown");
        }

        return live.Value == claimed
            ? new MemoryCitationWire(
                source.SourceId, source.Locator, availability, source.SourceRevision, source.Span,
                "current", null)
            : new MemoryCitationWire(
                source.SourceId, source.Locator, availability, source.SourceRevision, source.Span,
                "superseded", "citation_source_superseded");
    }

    private static string[]? ProjectCitationWarnings(
        Core.Domain.MemoryItem item,
        Core.Domain.MemorySourceReference[] sources,
        IManagedResourceReader? manifest,
        IManagedResourceStore? lineageStore)
    {
        var warnings = ProjectCitationWarnings(sources, manifest);
        if (lineageStore is not null && DeclassifyService.EvaluateDerivative(item.Id, lineageStore))
        {
            // Generic fail-closed staleness only: the private lineage moved, but
            // no ancestor identity, name, locator, title or count crosses this
            // wire for either audience. The owner resolves the exact lineage
            // through the existing relation surface they can already read.
            warnings = (warnings ?? Array.Empty<string>()).Append(DeclassifyService.DerivativeStaleWarning).ToArray();
        }

        return warnings;
    }

    private static string[]? ProjectCitationWarnings(Core.Domain.MemorySourceReference[] sources, IManagedResourceReader? manifest)
    {
        if (sources is null || sources.Length == 0 || manifest is null)
        {
            return null;
        }

        string[]? warnings = null;
        foreach (var source in sources)
        {
            var citation = ProjectCitation(source, manifest);
            if (citation.Warning is null)
            {
                continue;
            }

            warnings ??= Array.Empty<string>();
            if (!warnings.Contains(citation.Warning, StringComparer.Ordinal))
            {
                warnings = warnings.Append(citation.Warning).ToArray();
            }
        }

        return warnings;
    }

    /// <summary>
    /// The camelCase token this surface uses for one enumeration name. Shared
    /// with <see cref="ScopedSearchAuthority"/> so a filter token and the token
    /// a reply carries are spelled by exactly one implementation.
    /// </summary>
    internal static string ToWireToken(string name)
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
    /// <summary>
    /// S4-T3 scoped item detail (get-by-ID). The session authority already
    /// authorized the exact approved (memory.read, ID, revision) triple before
    /// and after the encrypted read; rendering then projects the exact stored
    /// snapshot bytes with their stored citation anchors plus honest
    /// current/superseded/unknown availability warnings. The approved revision
    /// is required (no live-revision fallback): a frozen grant can never drift
    /// onto a newer canonical revision, and a superseded frozen revision fails
    /// closed with the same generic denial as a guessed private ID instead of
    /// serving the replacement body. Detail never opens registry, snapshot, or
    /// relation content: citations render as stored anchors only, and an
    /// item-only grant still cannot call the source or snapshot APIs.
    /// </summary>
    private ServiceReply HandleAgentMemoryRead(
        AuthenticatedContext context,
        ServiceRequest request,
        IVaultOperation? operation)
    {
        if (context.Kind != PrincipalKind.Agent || operation is null ||
            HasAgentResourceReadExtras(request) ||
            request.AccessOperation is not "memory.read" ||
            request.ResourceId is null ||
            request.ResourceRevision is not { } approvedRevision || approvedRevision < 1)
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
                new AgentAccessRequest(request.AccessOperation, request.ResourceId, approvedRevision));
            if (!decision.Allowed)
            {
                return ResourceDenied();
            }

            snapshot = operation.ReadContent(request.ResourceId, approvedRevision);
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
                new AgentAccessRequest(request.AccessOperation, request.ResourceId, approvedRevision));
            if (!recheck.Allowed)
            {
                return ResourceDenied();
            }

            operation.ValidateRegistration(request.ResourceId, approvedRevision);
            var wire = ToMemoryWire(MemoryItemService.ToItem(stored, approvedRevision), operation);
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
    /// S4-T2 scoped search over the protected private index, served only
    /// inside one redeemed session's frozen approved snapshot. The grant must
    /// carry the memory.search operation; the candidate set is the session's
    /// own frozen (ID, revision) pairs filtered by the query inside the index
    /// engine before any vault byte is opened. Only the served page is read
    /// from the vault at the exact frozen revisions, each re-authorized
    /// before and after the read with a final registration check, exactly
    /// like the get-by-ID path. Lock, revoke, expiry and policy-generation
    /// changes deny through the same session authority; a moved canonical
    /// set fails closed with search_index_stale instead of a live scan.
    /// Denials (unknown session, missing scope, unindexed generation,
    /// guessed IDs, stale revisions) share one generic code so the reply is
    /// never an existence oracle; totals count scope matches only.
    /// </summary>
    private ServiceReply HandleAgentMemorySearch(
        AuthenticatedContext context,
        ServiceRequest request,
        IVaultOperation? operation,
        CancellationToken cancellationToken)
    {
        if (context.Kind != PrincipalKind.Agent || operation is null ||
            HasAgentSearchExtras(request) ||
            request.AccessOperation is not MemorySearchAccessOperation)
        {
            return ResourceDenied();
        }

        if (_privateIndexStore is null)
        {
            return ResourceDenied();
        }

        AgentSessionLookupResult lookup;
        AgentSessionSnapshot session;
        try
        {
            var state = operation.LoadScopeGrantState();
            var precheck = _agentSessions.AuthorizeSession(
                request.SessionToken,
                state.UnlockEpoch,
                state.PolicyGeneration,
                new AgentAccessRequest(MemorySearchAccessOperation));
            if (!precheck.Allowed)
            {
                return ResourceDenied();
            }

            lookup = _agentSessions.LookupSession(request.SessionToken, state.UnlockEpoch, state.PolicyGeneration);
            if (lookup.Status != AgentSessionLookupStatus.Valid || lookup.Session is null)
            {
                return ResourceDenied();
            }

            session = lookup.Session;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return ResourceDenied();
        }

        var materialized = _privateIndexMaterialized;
        if (materialized is null)
        {
            return ResourceDenied();
        }

        try
        {
            var liveDigest = _privateIndex.Evaluate(operation);
            if (!string.Equals(materialized.CanonicalDigest, liveDigest, StringComparison.Ordinal))
            {
                return new ServiceReply(CurrentProtocolVersion, "denied", null, "search_index_stale",
                    Decision: new AuthorizationDecision(false, "search_index_stale", session.PolicyGeneration));
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return ResourceDenied();
        }

        var offset = request.SearchOffset ?? 0;
        var limit = request.SearchLimit ?? ScopedSearchAuthority.DefaultLimit;
        if (offset < 0 || limit < 1 || limit > ScopedSearchAuthority.MaximumLimit)
        {
            return ResourceDenied();
        }

        if (request.SearchAsOfUtc is { } searchAsOf && (searchAsOf.Offset != TimeSpan.Zero || searchAsOf > DateTimeOffset.UtcNow.AddMinutes(5)))
        {
            return ResourceDenied();
        }

        var authority = new ScopedSearchAuthority();
        var started = DateTimeOffset.UtcNow;
        IReadOnlyList<ScopedSearchAuthority.ScoredHit> page;
        int total;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            total = authority.Count(session, request.Query, request.SearchKind, request.SearchCurrent, materialized, _privateIndexStore, operation, request.SearchAsOfUtc);
            page = authority.Search(session, request.Query, request.SearchKind, request.SearchCurrent, offset, limit, materialized, _privateIndexStore, operation, request.SearchAsOfUtc);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            return ResourceDenied();
        }

        try
        {
            var state = operation.LoadScopeGrantState();
            var recheck = _agentSessions.AuthorizeSession(
                request.SessionToken,
                state.UnlockEpoch,
                state.PolicyGeneration,
                new AgentAccessRequest(MemorySearchAccessOperation));
            if (!recheck.Allowed)
            {
                return ResourceDenied();
            }

            // No blanket ValidateRegistrationForAll: one moved resource must
            // not deny the surviving scope. Each hit was already read at its
            // exact frozen revision (revision-gated, fail-closed per hit),
            // exactly like the get-by-ID path, and the recheck above covers
            // revoke/lock/expiry/policy changes for the whole call.
            var hits = page.Select(hit => ToSearchHitWire(hit, session)).ToArray();
            var elapsed = DateTimeOffset.UtcNow - started;
            _ = elapsed;
            return new ServiceReply(
                CurrentProtocolVersion, "memory_search", null, null,
                Decision: recheck,
                PolicyGeneration: recheck.PolicyGeneration,
                MemorySearch: new MemorySearchPageWire(
                    request.Query ?? string.Empty, total, offset, limit,
                    offset + hits.Length < total, hits));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return ResourceDenied();
        }
    }

    private static MemorySearchHitWire ToSearchHitWire(ScopedSearchAuthority.ScoredHit hit, AgentSessionSnapshot session)
    {
        var approved = session.Resources.FirstOrDefault(resource =>
            string.Equals(resource.ResourceId, hit.ResourceId, StringComparison.Ordinal));
        var frozenRevision = approved?.Revision ?? hit.Revision;
        var snippet = BuildSnippet(hit.Item.Body, hit.Document.Tokens);
        return new MemorySearchHitWire(
            hit.ResourceId,
            frozenRevision,
            ToWireToken(hit.Item.Kind.ToString()),
            hit.Item.Title ?? string.Empty,
            Math.Round(hit.Score, 3),
            snippet,
            hit.Item.OccurredAtUtc,
            hit.Item.RecordedAtUtc,
            hit.Stale);
    }

    /// <summary>
    /// S5-T4 owner review of one pending proposal. Publishing writes the
    /// content the owner reviewed as canonical memory content through the same
    /// revision-gated services a direct owner write uses, keeping the
    /// proposal's own attribution, and records the decision as the publication
    /// state of the resulting item through the shared lifecycle core. Rejecting
    /// records the owner's decision — with the rationale the owner gave —
    /// without ever applying the proposal to the item it would have revised.
    ///
    /// Both acts are owner-only, need an unlocked vault, name the proposal at
    /// exactly the revision the owner reviewed (a proposal that moved fails
    /// closed), and refuse every field their contract does not name. The
    /// proposal record itself is never edited: it stays the agent's pending
    /// write, and only this owner act can turn its content into canonical
    /// content.
    /// </summary>
    private ServiceReply HandleOwnerProposalReview(
        AuthenticatedContext context,
        ServiceRequest request,
        IVaultOperation? operation,
        bool publish)
    {
        if (context.Kind != PrincipalKind.Owner || operation is null || HasProposalReviewExtras(request) ||
            string.IsNullOrEmpty(request.ResourceId) ||
            request.ResourceRevision is not { } proposalRevision || proposalRevision < 1 ||
            string.IsNullOrEmpty(request.ContentBase64))
        {
            return Error("invalid_request");
        }

        byte[]? draft = null;
        try
        {
            draft = Convert.FromBase64String(request.ContentBase64);
            var service = new ProposalReviewService();
            var review = publish
                ? service.Publish(context, request.ResourceId, proposalRevision, draft, operation)
                : service.Reject(context, request.ResourceId, proposalRevision, draft, operation);
            return new ServiceReply(
                CurrentProtocolVersion,
                publish ? "proposal_published" : "proposal_rejected",
                "owner",
                null,
                ResourceRevision: review.Item.Revision,
                MemoryItem: ToMemoryWire(review.Item, operation),
                ProposalReview: ToProposalReviewWire(review));
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

    /// S6-T5 owner declassification preview/publication into an explicit shared
    /// destination zone. Preview renders the exact sanitized content beside the
    /// private pins it would stand on; publish writes the sanitized shared copy
    /// (create-only, empty citations) plus accepted encrypted derived_from
    /// lineage and the published lifecycle record. The proposal path pins the
    /// lineage to a pending create proposal's cited sources. Owner-only, needs
    /// an unlocked vault, and refuses every field its contract does not name.
    /// </summary>
    private ServiceReply HandleOwnerDeclassifyPreview(
        AuthenticatedContext context,
        ServiceRequest request,
        IVaultOperation? operation)
    {
        if (context.Kind != PrincipalKind.Owner || operation is null || HasDeclassifyExtras(request) ||
            string.IsNullOrEmpty(request.ResourceId) || string.IsNullOrEmpty(request.ZoneId) ||
            string.IsNullOrEmpty(request.ContentBase64))
        {
            return Error("invalid_request");
        }

        byte[]? draft = null;
        try
        {
            draft = Convert.FromBase64String(request.ContentBase64);
            var service = new DeclassifyService();
            var preview = service.Preview(context, request.ResourceId, request.ZoneId, draft, operation);
            return new ServiceReply(
                CurrentProtocolVersion,
                "declassify_preview",
                "owner",
                null,
                ResourceRevision: null,
                DeclassifyPreview: ToDeclassifyPreviewWire(preview));
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

    private ServiceReply HandleOwnerDeclassifyPublish(
        AuthenticatedContext context,
        ServiceRequest request,
        IVaultOperation? operation,
        bool fromProposal)
    {
        if (context.Kind != PrincipalKind.Owner || operation is null || HasDeclassifyExtras(request) ||
            string.IsNullOrEmpty(request.ContentBase64) ||
            (fromProposal
                ? string.IsNullOrEmpty(request.ResourceId) ||
                    request.ResourceRevision is not { } fromRevision || fromRevision < 1
                : string.IsNullOrEmpty(request.ResourceId) || string.IsNullOrEmpty(request.ZoneId)))
        {
            return Error("invalid_request");
        }

        byte[]? draft = null;
        try
        {
            draft = Convert.FromBase64String(request.ContentBase64);
            var service = new DeclassifyService();
            var result = fromProposal
                ? service.PublishFromProposal(context, request.ResourceId!, request.ResourceRevision!.Value, draft, operation)
                : service.Publish(context, request.ResourceId!, request.ZoneId!, draft, operation);
            return new ServiceReply(
                CurrentProtocolVersion,
                "declassified",
                "owner",
                null,
                ResourceRevision: result.DestinationRevision,
                MemoryItem: ToMemoryWire(result.Item, operation),
                Declassify: ToDeclassifyWire(result));
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

    private static DeclassifyPreviewWire ToDeclassifyPreviewWire(DeclassifyPreview preview) => new(
        preview.DestinationResourceId,
        preview.DestinationZoneId,
        ToProposalItemWire(new MemoryItemDraft(
            preview.Sanitized.Kind,
            preview.Sanitized.Author,
            preview.Sanitized.CreationMethod,
            preview.Sanitized.Evidence,
            preview.Sanitized.Level,
            preview.Sanitized.Title,
            preview.Sanitized.Body ?? string.Empty,
            preview.Sanitized.Sources,
            preview.Sanitized.OccurredAtUtc,
            preview.Sanitized.RecordedAtUtc ?? DateTimeOffset.MinValue,
            preview.Sanitized.Applicability)),
        preview.Pins.Select(pin => new DeclassifyPinWire(pin.ResourceId, pin.Revision)).ToArray(),
        preview.PinsCurrent,
        preview.MismatchedResourceIds);

    private static DeclassifyWire ToDeclassifyWire(DeclassifyResult result) => new(
        result.DestinationResourceId,
        result.DestinationRevision,
        result.LifecycleRevision,
        result.LineageRelationIds,
        result.DecisionApplied);

    private static ProposalReviewWire ToProposalReviewWire(ProposalReviewResult review) => new(
        review.ProposalId,
        review.ProposalRevision,
        ToWireToken(review.Kind.ToString()),
        review.ItemId,
        review.Item.Revision,
        Core.Domain.LifecycleTokens.Publication(review.Publication),
        review.LifecycleRevision,
        review.DecisionApplied);

    /// <summary>
    /// S5-T3 owner library search and browse over the owner's own unlocked
    /// vault (R10, R11, R23, R30). The service applies every filter — text,
    /// zone, collection, kind, abstraction level, time window, currentness —
    /// and ranks the page itself; the console renders this answer instead of
    /// ranking or reinterpreting it.
    ///
    /// A collection is owner selection metadata: it can only narrow this
    /// search and grants nothing, because what is searchable is still exactly
    /// what the owner's own vault holds. Candidates come from the materialized
    /// index generation and every served hit is read at its own revision
    /// through the revision-gated owner operation, so a row whose canonical
    /// revision moved is dropped instead of serving other bytes.
    /// </summary>
    private ServiceReply HandleOwnerLibrarySearch(AuthenticatedContext context, ServiceRequest request, IVaultOperation? operation)
    {
        if (context.Kind != PrincipalKind.Owner || HasLibrarySearchExtras(request))
        {
            return Error("invalid_request");
        }

        if (operation is null)
        {
            return Error("vault_locked");
        }

        if (_privateIndexStore is null)
        {
            return Error("index_unavailable");
        }

        var materialized = _privateIndexMaterialized;
        if (materialized is null)
        {
            // Nothing has been materialized for this unlocked epoch, so no
            // honest library answer exists yet: the owner rebuilds the index
            // (an explicit owner act) instead of this read scanning the vault.
            return Error("index_not_materialized");
        }

        var offset = request.SearchOffset ?? 0;
        var limit = request.SearchLimit ?? ScopedSearchAuthority.DefaultLimit;
        if (offset is < 0 or > MaximumLibraryOffset || limit < 1 || limit > ScopedSearchAuthority.MaximumLimit)
        {
            return Error("invalid_request");
        }

        if (request.Query is { Length: > MaximumLibraryQueryLength } ||
            request.Query?.Any(char.IsControl) == true)
        {
            return Error("invalid_request");
        }

        var zoneId = string.IsNullOrWhiteSpace(request.ZoneId) ? null : request.ZoneId;
        if (zoneId is not null && !DomainIdentifiers.IsSafeIdentifier(zoneId))
        {
            return Error("invalid_request");
        }

        var collectionId = string.IsNullOrWhiteSpace(request.CollectionId) ? null : request.CollectionId;
        if (collectionId is not null && !DomainIdentifiers.IsSafeIdentifier(collectionId))
        {
            return Error("invalid_request");
        }

        var kind = string.IsNullOrWhiteSpace(request.SearchKind) ? null : request.SearchKind;
        if (kind is not null && !IsLibraryKindToken(kind))
        {
            return Error("invalid_request");
        }

        var level = string.IsNullOrWhiteSpace(request.SearchLevel) ? null : request.SearchLevel;
        if (level is not null && !IsLibraryLevelToken(level))
        {
            return Error("invalid_request");
        }

        var current = "all";
        if (!string.IsNullOrWhiteSpace(request.SearchCurrent))
        {
            if (string.Equals(request.SearchCurrent, "current", StringComparison.OrdinalIgnoreCase))
            {
                current = "current";
            }
            else if (string.Equals(request.SearchCurrent, "history", StringComparison.OrdinalIgnoreCase))
            {
                current = "history";
            }
            else if (!string.Equals(request.SearchCurrent, "all", StringComparison.OrdinalIgnoreCase))
            {
                return Error("invalid_request");
            }
        }

        if (request.SearchFromUtc is { } from && request.SearchToUtc is { } to && from > to)
        {
            return Error("invalid_request");
        }

        if (request.SearchAsOfUtc is { } asOfUtc && (asOfUtc.Offset != TimeSpan.Zero || asOfUtc > DateTimeOffset.UtcNow.AddMinutes(5)))
        {
            return Error("invalid_request");
        }

        HashSet<string>? collectionMembers = null;
        if (collectionId is not null)
        {
            try
            {
                var state = operation.LoadScopeGrantState();
                var collection = state.Collections.FirstOrDefault(candidate =>
                    string.Equals(candidate.CollectionId, collectionId, StringComparison.Ordinal));
                if (collection is null)
                {
                    return Error("collection_unknown");
                }

                collectionMembers = new HashSet<string>(collection.ResourceIds, StringComparer.Ordinal);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                return Error("library_unavailable");
            }
        }

        // Same generation contract as the scoped search: a materialized
        // generation that no longer equals the live canonical set is refused
        // instead of scanned, because a row the vault has moved past can no
        // longer be served at the revision the index names — searching it would
        // report matches (and a page count) the owner could not open. The owner
        // rebuilds the index, which is an explicit owner act.
        try
        {
            if (!string.Equals(materialized.CanonicalDigest, _privateIndex.Evaluate(operation), StringComparison.Ordinal))
            {
                return Error("search_index_stale");
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return Error("library_unavailable");
        }

        var rows = materialized.Rows
            .Where(row => row.RecordKind == PrivateIndexRecordKind.MemoryItem)
            .Where(row => zoneId is null || string.Equals(row.ZoneId, zoneId, StringComparison.Ordinal))
            .Where(row => collectionMembers is null || collectionMembers.Contains(row.ResourceId))
            .ToArray();
        var zones = rows.ToDictionary(row => row.ResourceId, row => row.ZoneId, StringComparer.Ordinal);
        var scope = rows
            .Select(row => new ScopeResourceRevision(row.ResourceId, row.ZoneId, row.Revision))
            .ToArray();

        var authority = new ScopedSearchAuthority();
        ScopedSearchAuthority.OwnerLibraryPage page;
        try
        {
            page = authority.SearchOwner(
                scope,
                new ScopedSearchAuthority.OwnerLibraryQuery(
                    request.Query, kind, current, level, request.SearchFromUtc, request.SearchToUtc, offset, limit, request.SearchAsOfUtc),
                materialized,
                _privateIndexStore,
                operation);
        }
        catch (OperationCanceledException)
        {
            // A requested lock cancels the in-flight search at its next read
            // boundary; the private answer dies with the operation instead of
            // being served into an epoch that no longer exists.
            throw;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            return Error("library_unavailable");
        }

        var hits = page.Hits.Select(hit => new LibraryHitWire(
            hit.ResourceId,
            hit.Revision,
            zones.TryGetValue(hit.ResourceId, out var hitZone) ? hitZone : string.Empty,
            ToWireToken(hit.Item.Kind.ToString()),
            ToWireToken(hit.Item.Level.ToString()),
            hit.Item.Title,
            Math.Round(hit.Score, 3),
            BuildSnippet(hit.Item.Body, hit.Document.Tokens),
            hit.Item.OccurredAtUtc,
            hit.Item.RecordedAtUtc,
            hit.Document.Publication)).ToArray();

        return new ServiceReply(
            CurrentProtocolVersion,
            "library_page",
            "owner",
            null,
            Library: new LibraryPageWire(
                request.Query ?? string.Empty,
                zoneId,
                collectionId,
                kind,
                level,
                current,
                request.SearchFromUtc,
                request.SearchToUtc,
                page.Total,
                offset,
                limit,
                offset + hits.Length < page.Total,
                hits,
                request.SearchAsOfUtc));
    }

    /// <summary>
    /// S5-T5 owner relation workbench read: the whole relation set of this
    /// owner's vault, each record with its live revision and its computed review
    /// state. Owner authority is this vault's owner, so every endpoint is in
    /// scope; the relation store, the revision gate and the review evaluation
    /// stay exactly the shared ones, so this view can never disagree with the
    /// scoped agent projection about a relation.
    /// </summary>
    private ServiceReply HandleOwnerRelationList(AuthenticatedContext context, ServiceRequest request, IVaultOperation? operation)
    {
        if (context.Kind != PrincipalKind.Owner || HasRelationListExtras(request))
        {
            return Error("invalid_request");
        }

        if (operation is null)
        {
            return Error("vault_locked");
        }

        var listed = new RelationService().List(context, operation, _ => true);
        var wires = new RelationWire[listed.Length];
        for (var index = 0; index < listed.Length; index++)
        {
            var entry = listed[index];
            wires[index] = ToRelationWire(entry.Relation!, entry.Review!, includeRequestEvidence: true);
        }

        return new ServiceReply(CurrentProtocolVersion, "relations", "owner", null, Relations: wires);
    }

    /// <summary>
    /// True when the token names a known memory kind (never <c>unknown</c>) in
    /// the spelling this surface uses, so an unknown kind filter is refused
    /// instead of quietly matching nothing.
    /// </summary>
    private static bool IsLibraryKindToken(string token) =>
        Enum.GetNames<MemoryKind>().Any(name =>
            !string.Equals(name, nameof(MemoryKind.Unknown), StringComparison.Ordinal) &&
            string.Equals(ToWireToken(name), token, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// True when the token names a known abstraction level (never
    /// <c>unknown</c>) in the spelling this surface uses.
    /// </summary>
    private static bool IsLibraryLevelToken(string token) =>
        Enum.GetNames<AbstractionLevel>().Any(name =>
            !string.Equals(name, nameof(AbstractionLevel.Unknown), StringComparison.Ordinal) &&
            string.Equals(ToWireToken(name), token, StringComparison.OrdinalIgnoreCase));

    private static string BuildSnippet(string body, string[] documentTokens)
    {
        // One short window around the first index-token hit: enough to judge
        // relevance, never the full body (full text stays on get-by-ID).
        const int MaximumSnippetChars = 220;
        if (string.IsNullOrEmpty(body))
        {
            return string.Empty;
        }

        var folded = SearchText.Normalize(body);
        var best = 0;
        foreach (var token in documentTokens)
        {
            var at = folded.IndexOf(token, StringComparison.Ordinal);
            if (at >= 0 && (best == 0 || at < best))
            {
                best = at;
            }
        }

        var start = Math.Max(0, best - 40);
        var length = Math.Min(MaximumSnippetChars, body.Length - start);
        var snippet = body.Substring(start, length);
        return snippet.Length < body.Length ? snippet + "…" : snippet;
    }

    /// <summary>
    /// S4-T4 scoped brief over the session's frozen approved snapshot. The
    /// grant must carry the memory.context operation; the selection is the
    /// session's own frozen (ID, revision) pairs projected under the caller's
    /// character budget. Only memory items enter: protected namespaces
    /// (relations, lifecycle, archives, journals, snapshots, feedback) are
    /// excluded by construction. Each entry renders the exact frozen body
    /// window with its stored citations plus the same manifest-only
    /// current/superseded/unknown warnings as item detail; the reply adds
    /// honest omissions and an uncertainty ledger so a small budget can never
    /// silently turn partial context into a confident claim. Lock, revoke,
    /// expiry and policy-generation changes deny through the same session
    /// authority. The brief is memory data, never system instructions.
    /// </summary>
    private ServiceReply HandleAgentMemoryBrief(
        AuthenticatedContext context,
        ServiceRequest request,
        IVaultOperation? operation)
    {
        if (context.Kind != PrincipalKind.Agent || operation is null ||
            HasAgentBriefExtras(request) ||
            request.AccessOperation is not MemoryContextAccessOperation)
        {
            return ResourceDenied();
        }

        var budget = request.BudgetChars ?? ScopedBriefAuthority.DefaultBudgetChars;
        if (budget < ScopedBriefAuthority.MinimumBudgetChars || budget > ScopedBriefAuthority.MaximumBudgetChars)
        {
            return ResourceDenied();
        }

        AgentSessionSnapshot session;
        try
        {
            var state = operation.LoadScopeGrantState();
            var precheck = _agentSessions.AuthorizeSession(
                request.SessionToken,
                state.UnlockEpoch,
                state.PolicyGeneration,
                new AgentAccessRequest(MemoryContextAccessOperation));
            if (!precheck.Allowed)
            {
                return ResourceDenied();
            }

            var lookup = _agentSessions.LookupSession(request.SessionToken, state.UnlockEpoch, state.PolicyGeneration);
            if (lookup.Status != AgentSessionLookupStatus.Valid || lookup.Session is null)
            {
                return ResourceDenied();
            }

            session = lookup.Session;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return ResourceDenied();
        }

        try
        {
            var approvedScope = session.Resources
                .Where(resource => !IsProtectedBriefNamespace(resource.ResourceId))
                .OrderBy(resource => resource.ResourceId, StringComparer.Ordinal)
                .ToArray();
            var liveRevisions = operation.ListResources().ToDictionary(
                resource => resource.ResourceId, resource => resource.Revision, StringComparer.Ordinal);
            var authority = new ScopedBriefAuthority();
            var brief = authority.Build(
                session,
                request.Query,
                budget,
                approvedScope,
                approved => ReadBriefItem(operation, approved),
                (item, manifest) => (ProjectCitations(item.Sources, manifest), ProjectCitationWarnings(item, item.Sources, manifest, operation)),
                operation,
                resourceId => liveRevisions.TryGetValue(resourceId, out var live) &&
                    session.Resources.Any(resource =>
                        string.Equals(resource.ResourceId, resourceId, StringComparison.Ordinal) && resource.Revision == live));
            var state = operation.LoadScopeGrantState();
            var recheck = _agentSessions.AuthorizeSession(
                request.SessionToken,
                state.UnlockEpoch,
                state.PolicyGeneration,
                new AgentAccessRequest(MemoryContextAccessOperation));
            if (!recheck.Allowed)
            {
                return ResourceDenied();
            }

            return new ServiceReply(
                CurrentProtocolVersion, "memory_brief", null, null,
                Decision: recheck,
                PolicyGeneration: recheck.PolicyGeneration,
                MemoryBrief: brief with { PolicyGeneration = recheck.PolicyGeneration });
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            return ResourceDenied();
        }
    }

    private static MemoryItem? ReadBriefItem(IVaultOperation operation, ScopeResourceRevision approved)
    {
        byte[]? raw = null;
        try
        {
            raw = operation.ReadContent(approved.ResourceId, approved.Revision);
            var stored = Core.Domain.MemoryEnvelopeCodec.DecodeStored(raw);
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

    private static bool IsProtectedBriefNamespace(string resourceId) =>
        resourceId.StartsWith("rel-", StringComparison.Ordinal) ||
        resourceId.StartsWith("lc-", StringComparison.Ordinal) ||
        resourceId.StartsWith("arc-", StringComparison.Ordinal) ||
        resourceId.StartsWith("ingest-", StringComparison.Ordinal) ||
        resourceId.StartsWith("snap-", StringComparison.Ordinal) ||
        resourceId.StartsWith("fb-", StringComparison.Ordinal);

    /// <summary>
    /// S4-T4 agent feedback inside the owner-approved grant. The session must
    /// carry the memory.feedback operation; the seen target named in the draft
    /// must be inside the frozen scope at exactly the seen revision, or the
    /// whole write denies generically. The record is append-only usage data:
    /// it never edits the target, upgrades evidence, or rewrites beliefs.
    /// </summary>
    private ServiceReply HandleAgentFeedback(
        AuthenticatedContext context,
        ServiceRequest request,
        IVaultOperation? operation)
    {
        if (context.Kind != PrincipalKind.Agent || operation is null ||
            HasAgentFeedbackExtras(request) ||
            request.AccessOperation is not MemoryFeedbackAccessOperation ||
            string.IsNullOrEmpty(request.ContentBase64))
        {
            return ResourceDenied();
        }

        byte[]? draft = null;
        try
        {
            draft = Convert.FromBase64String(request.ContentBase64);
            Core.Domain.FeedbackDraft parsed;
            try
            {
                parsed = Core.Domain.FeedbackCodec.DecodeDraft(draft);
            }
            catch (ArgumentException)
            {
                return ResourceDenied();
            }

            if (string.IsNullOrEmpty(parsed.TargetResourceId) || parsed.TargetRevision is not > 0)
            {
                return ResourceDenied();
            }

            var beforeWrite = operation.LoadScopeGrantState();
            var decision = _agentSessions.AuthorizeSession(
                request.SessionToken,
                beforeWrite.UnlockEpoch,
                beforeWrite.PolicyGeneration,
                new AgentAccessRequest(MemoryFeedbackAccessOperation));
            if (!decision.Allowed)
            {
                return ResourceDenied();
            }

            var lookup = _agentSessions.LookupSession(
                request.SessionToken, beforeWrite.UnlockEpoch, beforeWrite.PolicyGeneration);
            if (lookup.Status != AgentSessionLookupStatus.Valid || lookup.Session is null)
            {
                return ResourceDenied();
            }

            var session = lookup.Session;
            var required = IsSourceTarget(operation, parsed.TargetResourceId)
                ? SourceReadOperation
                : MemoryReadOperation;
            if (!session.Operations.Contains(required, StringComparer.Ordinal) ||
                !IsInGrantScope(session.Resources, parsed.TargetResourceId, parsed.TargetRevision))
            {
                return ResourceDenied();
            }

            Core.Domain.Feedback created;
            try
            {
                created = new FeedbackService().CreateAgentFeedback(context, draft, operation);
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
                new AgentAccessRequest(MemoryFeedbackAccessOperation));
            if (!recheck.Allowed)
            {
                return ResourceDenied();
            }

            operation.ValidateRegistration(created.Id, created.Revision);
            return new ServiceReply(
                CurrentProtocolVersion, "feedback_created", null, null,
                Decision: recheck,
                PolicyGeneration: recheck.PolicyGeneration,
                ResourceRevision: created.Revision,
                Feedback: ToFeedbackWire(created));
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

    private static bool IsSourceTarget(IVaultOperation operation, string targetResourceId)
    {
        byte[]? raw = null;
        try
        {
            var live = operation.ListResources().FirstOrDefault(resource =>
                string.Equals(resource.ResourceId, targetResourceId, StringComparison.Ordinal));
            if (live is null)
            {
                return false;
            }

            raw = operation.ReadContent(targetResourceId, live.Revision);
            _ = Core.Domain.RegisteredSourceCodec.DecodeStored(raw);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            if (raw is { Length: > 0 })
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(raw);
            }
        }
    }

    /// <summary>
    /// S4-T4 scoped feedback read: the session must hold feedback.read on the
    /// exact feedback ID/revision. Item, source, and snapshot grants never
    /// satisfy this path.
    /// </summary>
    private ServiceReply HandleAgentFeedbackRead(
        AuthenticatedContext context,
        ServiceRequest request,
        IVaultOperation? operation)
    {
        if (context.Kind != PrincipalKind.Agent || operation is null ||
            HasAgentResourceReadExtras(request) ||
            request.AccessOperation is not FeedbackReadOperation ||
            string.IsNullOrEmpty(request.ResourceId))
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
            Core.Domain.StoredFeedback stored;
            try
            {
                stored = Core.Domain.FeedbackCodec.DecodeStored(snapshot);
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
            var liveRevision = operation.ListResources().FirstOrDefault(resource =>
                string.Equals(resource.ResourceId, request.ResourceId, StringComparison.Ordinal))?.Revision
                ?? throw new ManagedResourceUnavailableException();
            var wire = ToFeedbackWire(FeedbackService.ToFeedback(stored, liveRevision));
            return new ServiceReply(
                CurrentProtocolVersion, "feedback", null, null,
                Decision: recheck,
                PolicyGeneration: recheck.PolicyGeneration,
                ResourceRevision: wire.Revision,
                Feedback: wire);
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
    /// S4-T5 scoped agent proposal inside the existing owner-approved
    /// <c>proposal.create</c> grant: the session authority must hold the
    /// operation plus the exact inbox destination at the pinned revision
    /// (the draft can never supply its own permission). The proposal ID is
    /// derived server-side from that approved destination and the caller's
    /// idempotency key, the executor comes from the trusted context, and the
    /// record is create-only: an identical replay returns the stored
    /// proposal, a reuse of the same key with different content fails closed
    /// without mutation, and no byte of the subject item or any other
    /// canonical record is written here. The revise subject and every cited
    /// source revision are re-checked against the frozen snapshot before the
    /// write, so a guessed ID or an out-of-scope reference is the same
    /// generic denial, never an existence signal. A claimed user
    /// confirmation is stored as a claim only; it never upgrades evidence,
    /// executor, or any belief (R09/R12).
    /// </summary>
    private ServiceReply HandleAgentPropose(
        AuthenticatedContext context,
        ServiceRequest request,
        IVaultOperation? operation)
    {
        if (context.Kind != PrincipalKind.Agent || operation is null ||
            HasAgentProposeExtras(request) ||
            request.AccessOperation is not ProposalCreateAccessOperation ||
            string.IsNullOrEmpty(request.ContentBase64))
        {
            return ResourceDenied();
        }

        byte[]? draft = null;
        try
        {
            draft = Convert.FromBase64String(request.ContentBase64);
            Core.Domain.ProposalDraft parsed;
            try
            {
                parsed = Core.Domain.ProposalCodec.DecodeDraft(draft);
            }
            catch (ArgumentException)
            {
                return ResourceDenied();
            }

            if (string.IsNullOrEmpty(parsed.DestinationResourceId) || parsed.DestinationRevision is not > 0)
            {
                return ResourceDenied();
            }

            // Authorize the exact operation and the exact inbox destination
            // against the frozen snapshot before any proposal data is read.
            AgentAccessRequest Authorize() => new(
                ProposalCreateAccessOperation,
                ResourceId: null,
                ResourceRevision: null,
                DestinationResourceId: parsed.DestinationResourceId,
                DestinationRevision: parsed.DestinationRevision);

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

            var lookup = _agentSessions.LookupSession(
                request.SessionToken, beforeWrite.UnlockEpoch, beforeWrite.PolicyGeneration);
            if (lookup.Status != AgentSessionLookupStatus.Valid || lookup.Session is null)
            {
                return ResourceDenied();
            }

            var session = lookup.Session;

            // A revise proposal pins the subject item at the exact revision
            // it would replace: both must already sit inside the frozen
            // approved snapshot before the precondition is proven against
            // the store.
            if (parsed.Kind == Core.Domain.ProposalKind.Revise)
            {
                if (string.IsNullOrEmpty(parsed.SubjectResourceId) || parsed.SubjectRevision is not > 0 ||
                    !IsInGrantScope(session.Resources, parsed.SubjectResourceId, parsed.SubjectRevision))
                {
                    return ResourceDenied();
                }
            }

            // Every cited source revision must sit inside the frozen
            // snapshot too: a reference the grant never approved is the same
            // generic denial as a guessed one, so the surface is never an
            // existence oracle.
            if (parsed.Item?.Sources is { } citedSources)
            {
                foreach (var cited in citedSources)
                {
                    if (cited is null)
                    {
                        return ResourceDenied();
                    }

                    if (cited.SourceId is not null &&
                        !IsInGrantScope(session.Resources, cited.SourceId, cited.SourceRevision))
                    {
                        return ResourceDenied();
                    }
                }
            }

            Core.Domain.Proposal created;
            try
            {
                created = new ProposalService().CreateAgentProposal(context, session.GrantId, draft, operation);
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

            operation.ValidateRegistration(created.Id, created.Revision);
            return new ServiceReply(
                CurrentProtocolVersion, "proposal_created", null, null,
                Decision: recheck,
                PolicyGeneration: recheck.PolicyGeneration,
                ResourceRevision: created.Revision,
                Proposal: ToProposalWire(created, store: null));
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

    /// <summary>
    /// The S5-T3 owner library filters — abstraction level and the inclusive
    /// time window — belong to exactly one operation. <see cref="HandleAsync"/>
    /// refuses them on every other operation before dispatch, so an operation
    /// whose own contract does not name them can never silently ignore one.
    /// </summary>
    private static bool HasLibraryFilterFields(ServiceRequest request) =>
        request.SearchLevel is not null || request.SearchFromUtc is not null || request.SearchToUtc is not null;

    /// <summary>
    /// S5-T3 owner library search carries the query text, paging, the zone and
    /// collection selectors, the kind/level/currentness filters and the time
    /// window — and nothing else. Credentials, content, scope selection,
    /// sessions, catalog, egress, relation and snapshot fields are refused, so
    /// a library body can never steer another flow or mint authority.
    /// </summary>
    private static bool HasLibrarySearchExtras(ServiceRequest request) =>
        request.ResourceId is not null || request.ResourceRevision is not null ||
        request.Passphrase is not null || request.RecoveryCode is not null ||
        request.ContentBase64 is not null || request.ResourceIds is not null ||
        request.Operations is not null || request.DestinationResourceIds is not null ||
        request.ExpiresAtUtc is not null || request.Provider is not null ||
        request.Model is not null || request.MaximumCostUsd is not null ||
        request.PreviewId is not null || request.Token is not null ||
        request.SessionToken is not null || request.SessionId is not null ||
        request.AccessOperation is not null || request.DestinationResourceId is not null ||
        request.DestinationRevision is not null || request.EstimatedCostUsd is not null ||
        request.InputTokens is not null || request.OutputTokens is not null ||
        request.CatalogId is not null || request.Label is not null ||
        request.Description is not null || request.CatalogIds is not null ||
        request.BudgetChars is not null || request.Purpose is not null ||
        request.AgentId is not null || request.AgentContext is not null ||
        request.DisclosureContext is not null || request.RequestId is not null ||
        request.SourceLocator is not null || request.SourceFormat is not null ||
        request.SourceFormatVersion is not null || request.SourceTitle is not null ||
        request.SourceAuthor is not null || request.SourceCapturedAtUtc is not null ||
        request.SourceGeneration is not null || request.SnapshotRetainUntilUtc is not null;

    /// <summary>
    /// S5-T4 owner proposal review carries exactly the reviewed proposal ID,
    /// its exact revision and the reviewed content (publish) or the owner's
    /// rationale (reject) — and nothing else. Credentials, zone, scope,
    /// sessions, catalog, egress, source and snapshot fields are refused, so a
    /// review body can never steer another flow, name a zone, or mint
    /// authority.
    /// </summary>
    private static bool HasProposalReviewExtras(ServiceRequest request) =>
        request.Passphrase is not null || request.RecoveryCode is not null ||
        request.ZoneId is not null || request.ResourceIds is not null ||
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
        request.SearchOffset is not null || request.SearchLimit is not null ||
        request.SearchKind is not null || request.SearchCurrent is not null ||
        request.BudgetChars is not null || request.Purpose is not null ||
        request.AgentId is not null || request.AgentContext is not null ||
        request.DisclosureContext is not null || request.RequestId is not null ||
        request.SourceLocator is not null || request.SourceFormat is not null ||
        request.SourceFormatVersion is not null || request.SourceTitle is not null ||
        request.SourceAuthor is not null || request.SourceCapturedAtUtc is not null ||
        request.SourceGeneration is not null || request.SnapshotRetainUntilUtc is not null ||
        request.SearchLevel is not null || request.SearchFromUtc is not null ||
        request.SearchToUtc is not null;

    /// <summary>
    /// S6-T5 owner declassification carries the shared destination (resource ID
    /// for all three acts, zone for the two explicit-pins acts, proposal
    /// revision for the proposal act) and the sanitized/body draft — and
    /// nothing else. Credentials, scope selection, sessions, catalog, egress,
    /// source and snapshot fields are refused, so a declassification body can
    /// never steer another flow or mint authority. Identity-shaped body fields
    /// are inert: authority comes from the owner pipe plus the explicit pins
    /// the service revalidates, never from an agent claim.
    /// </summary>
    private static bool HasDeclassifyExtras(ServiceRequest request) =>
        request.Passphrase is not null || request.RecoveryCode is not null ||
        request.ResourceIds is not null || request.Operations is not null ||
        request.DestinationResourceIds is not null || request.ExpiresAtUtc is not null ||
        request.Provider is not null || request.Model is not null ||
        request.MaximumCostUsd is not null || request.PreviewId is not null ||
        request.CollectionId is not null || request.Token is not null ||
        request.SessionToken is not null || request.SessionId is not null ||
        request.AccessOperation is not null ||
        request.DestinationResourceId is not null || request.DestinationRevision is not null ||
        request.EstimatedCostUsd is not null || request.InputTokens is not null ||
        request.OutputTokens is not null || request.CatalogId is not null ||
        request.Label is not null || request.Description is not null ||
        request.Query is not null || request.CatalogIds is not null ||
        request.SearchOffset is not null || request.SearchLimit is not null ||
        request.SearchKind is not null || request.SearchCurrent is not null ||
        request.BudgetChars is not null || request.Purpose is not null ||
        request.AgentId is not null || request.AgentContext is not null ||
        request.DisclosureContext is not null || request.RequestId is not null ||
        request.SourceLocator is not null || request.SourceFormat is not null ||
        request.SourceFormatVersion is not null || request.SourceTitle is not null ||
        request.SourceAuthor is not null || request.SourceCapturedAtUtc is not null ||
        request.SourceGeneration is not null || request.SnapshotRetainUntilUtc is not null ||
        request.SearchLevel is not null || request.SearchFromUtc is not null ||
        request.SearchToUtc is not null;

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
    /// S5-T7 mapping review submission carries only the source ID, the
    /// submitted locator, the optional unresolved zone/format/version the
    /// owner can still state, and the unresolved-field detail. Zone travels
    /// here (unlike revoke/status/ingest) because the owner may know the zone
    /// while the format is unresolved, or vice versa; nothing is ever
    /// defaulted server-side. Every other field is refused.
    /// </summary>
    private static bool HasSourceMappingSubmitExtras(ServiceRequest request) =>
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
    /// Resolves the unresolved-field detail for a mapping review submission
    /// from the fields the owner could state: a missing zone, a missing
    /// format, or both. The detail is a bounded diagnostic token carried in
    /// the quarantine record; it never echoes the locator.
    /// </summary>
    private static string ResolveMappingDetail(ServiceRequest request)
    {
        var zoneMissing = string.IsNullOrEmpty(request.ZoneId);
        var formatMissing = string.IsNullOrEmpty(request.SourceFormat) || request.SourceFormatVersion is null;
        return zoneMissing && formatMissing ? "mapping_unresolved"
            : zoneMissing ? "zone_unresolved"
            : formatMissing ? "format_unresolved"
            : "mapping_unresolved";
    }


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
    /// S4-T2 scoped search carries only the session bearer, the access
    /// operation name, the query text and its paging/kind/current/as-of filters.
    /// Every identity, zone, scope, credential, catalog, egress and
    /// content-draft field is refused, so a query can never steer the search
    /// toward another scope or mint authority.
    /// </summary>
    private static bool HasAgentSearchExtras(ServiceRequest request) =>
        request.ZoneId is not null ||
        request.Passphrase is not null || request.RecoveryCode is not null ||
        request.ContentBase64 is not null || request.ResourceIds is not null ||
        request.Operations is not null || request.DestinationResourceIds is not null ||
        request.ExpiresAtUtc is not null || request.Provider is not null ||
        request.Model is not null || request.MaximumCostUsd is not null ||
        request.PreviewId is not null || request.CollectionId is not null ||
        request.Token is not null || request.SessionId is not null ||
        request.ResourceId is not null || request.ResourceRevision is not null ||
        request.DestinationResourceId is not null || request.DestinationRevision is not null ||
        request.EstimatedCostUsd is not null || request.InputTokens is not null ||
        request.OutputTokens is not null ||
        request.CatalogId is not null || request.Label is not null ||
        request.Description is not null ||
        request.CatalogIds is not null || request.Purpose is not null ||
        request.AgentId is not null || request.AgentContext is not null ||
        request.DisclosureContext is not null || request.RequestId is not null ||
        request.SourceLocator is not null || request.SourceFormat is not null ||
        request.SourceFormatVersion is not null || request.SourceTitle is not null ||
        request.SourceAuthor is not null || request.SourceCapturedAtUtc is not null ||
        request.SourceGeneration is not null || request.SnapshotRetainUntilUtc is not null ||
        request.SearchLevel is not null || request.SearchFromUtc is not null || request.SearchToUtc is not null;
    // Note: SearchAsOfUtc is intentionally NOT an extra: it is the S7-T1
    // as-of filter of the agent search contract, validated (UTC, not future)
    // in the handler itself.

    /// <summary>
    /// S4-T4 scoped brief carries only the session bearer, the access
    /// operation name, the query text, and the explicit character budget.
    /// Every identity, zone, scope, credential, catalog, egress, and
    /// content-draft field is refused, so a body can never steer the brief
    /// toward another scope or mint authority.
    /// </summary>
    private static bool HasAgentBriefExtras(ServiceRequest request) =>
        request.ZoneId is not null ||
        request.Passphrase is not null || request.RecoveryCode is not null ||
        request.ContentBase64 is not null || request.ResourceIds is not null ||
        request.Operations is not null || request.DestinationResourceIds is not null ||
        request.ExpiresAtUtc is not null || request.Provider is not null ||
        request.Model is not null || request.MaximumCostUsd is not null ||
        request.PreviewId is not null || request.CollectionId is not null ||
        request.Token is not null || request.SessionId is not null ||
        request.ResourceId is not null || request.ResourceRevision is not null ||
        request.DestinationResourceId is not null || request.DestinationRevision is not null ||
        request.EstimatedCostUsd is not null || request.InputTokens is not null ||
        request.OutputTokens is not null ||
        request.CatalogId is not null || request.Label is not null ||
        request.Description is not null ||
        request.CatalogIds is not null || request.Purpose is not null ||
        request.AgentId is not null || request.AgentContext is not null ||
        request.DisclosureContext is not null || request.RequestId is not null ||
        request.SourceLocator is not null || request.SourceFormat is not null ||
        request.SourceFormatVersion is not null || request.SourceTitle is not null ||
        request.SourceAuthor is not null || request.SourceCapturedAtUtc is not null ||
        request.SourceGeneration is not null || request.SnapshotRetainUntilUtc is not null ||
        request.SearchOffset is not null || request.SearchLimit is not null ||
        request.SearchKind is not null || request.SearchCurrent is not null;

    /// <summary>
    /// S4-T4 agent feedback carries only the session bearer, the access
    /// operation name, and the feedback draft. Identity, zone, scope,
    /// credential, catalog, egress, query, and paging fields are refused, so
    /// a draft can never steer the write toward another scope.
    /// </summary>
    private static bool HasAgentFeedbackExtras(ServiceRequest request) =>
        request.ZoneId is not null ||
        request.Passphrase is not null || request.RecoveryCode is not null ||
        request.ResourceIds is not null || request.Operations is not null ||
        request.DestinationResourceIds is not null || request.ExpiresAtUtc is not null ||
        request.Provider is not null || request.Model is not null ||
        request.MaximumCostUsd is not null || request.PreviewId is not null ||
        request.CollectionId is not null || request.Token is not null ||
        request.SessionId is not null ||
        request.ResourceId is not null || request.ResourceRevision is not null ||
        request.DestinationResourceId is not null || request.DestinationRevision is not null ||
        request.EstimatedCostUsd is not null || request.InputTokens is not null ||
        request.OutputTokens is not null || request.CatalogId is not null ||
        request.Label is not null || request.Description is not null ||
        request.CatalogIds is not null || request.Purpose is not null ||
        request.AgentId is not null || request.AgentContext is not null ||
        request.DisclosureContext is not null || request.RequestId is not null ||
        request.Query is not null || request.SearchOffset is not null ||
        request.SearchLimit is not null || request.SearchKind is not null ||
        request.SearchCurrent is not null || request.BudgetChars is not null ||
        request.SourceLocator is not null || request.SourceFormat is not null ||
        request.SourceFormatVersion is not null || request.SourceTitle is not null ||
        request.SourceAuthor is not null || request.SourceCapturedAtUtc is not null ||
        request.SourceGeneration is not null || request.SnapshotRetainUntilUtc is not null;

    /// <summary>
    /// Agent proposal carries only the session bearer, the access operation
    /// name, and the proposal draft: identity, zone, scope, and egress
    /// fields are never accepted from the agent envelope. The inbox
    /// destination, the revise subject, and every cited revision travel
    /// inside the base64 draft and are re-checked against the frozen
    /// snapshot in the handler, so the envelope itself cannot steer the
    /// write.
    /// </summary>
    private static bool HasAgentProposeExtras(ServiceRequest request) =>
        request.ZoneId is not null ||
        request.Passphrase is not null || request.RecoveryCode is not null ||
        request.ResourceIds is not null || request.Operations is not null ||
        request.DestinationResourceIds is not null || request.ExpiresAtUtc is not null ||
        request.Provider is not null || request.Model is not null ||
        request.MaximumCostUsd is not null || request.PreviewId is not null ||
        request.CollectionId is not null || request.Token is not null ||
        request.SessionId is not null ||
        request.ResourceId is not null || request.ResourceRevision is not null ||
        request.DestinationResourceId is not null || request.DestinationRevision is not null ||
        request.EstimatedCostUsd is not null || request.InputTokens is not null ||
        request.OutputTokens is not null || request.CatalogId is not null ||
        request.Label is not null || request.Description is not null ||
        request.CatalogIds is not null || request.Purpose is not null ||
        request.AgentId is not null || request.AgentContext is not null ||
        request.DisclosureContext is not null || request.RequestId is not null ||
        request.Query is not null || request.SearchOffset is not null ||
        request.SearchLimit is not null || request.SearchKind is not null ||
        request.SearchCurrent is not null || request.BudgetChars is not null ||
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
    /// The owner relation list carries no filter and no identity: it reads this
    /// owner's own relation set, so a request that names a resource, a revision,
    /// a zone, a query, a session, a credential or an egress claim is refused
    /// rather than silently narrowed.
    /// </summary>
    private static bool HasRelationListExtras(ServiceRequest request) =>
        request.ResourceId is not null || request.ResourceRevision is not null ||
        HasContentReadExtras(request);

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

    private static bool HasAgentRelationListExtras(ServiceRequest request, bool resolve) =>
        request.ZoneId is not null ||
        request.Passphrase is not null || request.RecoveryCode is not null ||
        request.ContentBase64 is not null || request.ResourceId is not null ||
        request.ResourceRevision is not null || request.ResourceIds is not null ||
        request.Operations is not null || request.DestinationResourceIds is not null ||
        request.ExpiresAtUtc is not null || request.Provider is not null ||
        request.Model is not null || request.MaximumCostUsd is not null ||
        request.PreviewId is not null || request.CollectionId is not null ||
        request.Token is not null || request.SessionId is not null ||
        request.DestinationResourceId is not null || request.DestinationRevision is not null ||
        request.EstimatedCostUsd is not null || request.InputTokens is not null ||
        request.OutputTokens is not null ||
        request.CatalogId is not null || request.Label is not null ||
        request.Description is not null ||
        request.CatalogIds is not null || request.Purpose is not null ||
        request.AgentId is not null || request.AgentContext is not null ||
        request.DisclosureContext is not null || request.RequestId is not null ||
        request.SearchOffset is not null || request.SearchLimit is not null ||
        request.SearchKind is not null || request.SearchCurrent is not null ||
        request.BudgetChars is not null ||
        request.SourceLocator is not null || request.SourceFormat is not null ||
        request.SourceFormatVersion is not null || request.SourceTitle is not null ||
        request.SourceAuthor is not null || request.SourceCapturedAtUtc is not null ||
        request.SourceGeneration is not null || request.SnapshotRetainUntilUtc is not null ||
        (!resolve && request.Query is not null);

    private static bool HasAgentRelationMutationExtras(ServiceRequest request) =>
        request.ZoneId is not null ||
        request.Passphrase is not null || request.RecoveryCode is not null ||
        request.ResourceIds is not null || request.Operations is not null ||
        request.DestinationResourceIds is not null || request.ExpiresAtUtc is not null ||
        request.Provider is not null || request.Model is not null ||
        request.MaximumCostUsd is not null || request.PreviewId is not null ||
        request.CollectionId is not null || request.Token is not null ||
        request.SessionId is not null ||
        request.DestinationResourceId is not null || request.DestinationRevision is not null ||
        request.EstimatedCostUsd is not null || request.InputTokens is not null ||
        request.OutputTokens is not null || request.CatalogId is not null ||
        request.Label is not null || request.Description is not null ||
        request.Query is not null || request.CatalogIds is not null ||
        request.Purpose is not null || request.AgentId is not null ||
        request.AgentContext is not null || request.DisclosureContext is not null ||
        request.RequestId is not null || request.SearchOffset is not null ||
        request.SearchLimit is not null || request.SearchKind is not null ||
        request.SearchCurrent is not null || request.BudgetChars is not null ||
        request.SourceLocator is not null || request.SourceFormat is not null ||
        request.SourceFormatVersion is not null || request.SourceTitle is not null ||
        request.SourceAuthor is not null || request.SourceCapturedAtUtc is not null ||
        request.SourceGeneration is not null || request.SnapshotRetainUntilUtc is not null;

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
