using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MetaBrain.Application;
using MetaBrain.Core.Domain;

namespace MetaBrain.Connections;

/// <summary>
/// What an owner-application client observed when it used the owner control
/// channel. These are transport facts, never a second policy interpretation:
/// the owner application holds no vault key, opens no vault file and caches no
/// private content, and it reaches the shared core/application policy only by
/// asking the running service over the authenticated owner control pipe.
/// </summary>
public enum OwnerChannelAvailability
{
    /// <summary>No owner-channel answer: no service, unknown pipe, ACL refusal or timeout.</summary>
    ServiceUnavailable = 0,

    /// <summary>
    /// The endpoint answered but refused owner authority for this request
    /// (for example the agent channel, or a caller that is not the owner SID).
    /// </summary>
    OwnerAuthorityDenied = 1,

    /// <summary>The endpoint answered with a different protocol revision.</summary>
    ProtocolMismatch = 2,

    /// <summary>The owner control channel answered with the running owner status.</summary>
    Reachable = 3
}

/// <summary>
/// The vault state exactly as the service reported it. <see cref="Unknown"/>
/// covers "no answer" and any value this client does not recognize; it is never
/// treated as unlocked.
/// </summary>
public enum OwnerVaultState
{
    Unknown = 0,
    Uninitialized = 1,
    Locked = 2,
    Locking = 3,
    Unlocked = 4
}

/// <summary>
/// The owner-channel status an owner application may present. <see cref="VaultState"/>
/// is only meaningful together with <see cref="OwnerChannelAvailability.Reachable"/>.
/// </summary>
public sealed record OwnerChannelStatus(
    OwnerChannelAvailability Availability,
    OwnerVaultState VaultState,
    string? ErrorCode);

/// <summary>
/// How an owner command ended, as the owner application observed it.
/// <see cref="Refused"/> is a concrete answer of the running service: the
/// endpoint understood the command and declined it, for example a wrong or
/// missing key (<c>unlock_denied</c>), a passphrase outside the accepted
/// policy (<c>credential_required</c>), an already initialized vault
/// (<c>vault_already_initialized</c>), an unknown library filter or a library
/// read with no materialized index generation. The vault state did not change
/// in that case, and the owner application must present it as a refusal, never
/// as an unavailable service.
/// </summary>
public enum OwnerCommandOutcome
{
    /// <summary>The service performed the owner command.</summary>
    Completed = 0,

    /// <summary>The service answered and refused the command; nothing changed.</summary>
    Refused = 1,

    /// <summary>The endpoint answered but refused owner authority for this request.</summary>
    OwnerAuthorityDenied = 2,

    /// <summary>The endpoint answered with a different protocol revision.</summary>
    ProtocolMismatch = 3,

    /// <summary>No owner-channel answer: no service, unknown pipe, access denial, timeout or malformed reply.</summary>
    Unavailable = 4
}

/// <summary>
/// Result of one owner vault command. <see cref="RecoveryCode"/> carries the
/// one-time recovery material the service returns for a fresh provisioning;
/// the owner application shows it once and persists it nowhere, and it stays
/// null for every other command.
/// </summary>
public sealed record OwnerVaultCommandResult(
    OwnerCommandOutcome Outcome,
    OwnerVaultState VaultState,
    string? ErrorCode,
    string? RecoveryCode);

/// <summary>
/// Which items an owner library search may return: <see cref="All"/> applies
/// no currentness filter, <see cref="Current"/> keeps live, unexpired,
/// published items, and <see cref="History"/> keeps everything the index still
/// holds so withdrawn, rejected or expired records stay reachable as history
/// instead of disappearing silently.
/// </summary>
public enum OwnerLibraryCurrentness
{
    All = 0,
    Current = 1,
    History = 2
}

    /// <summary>
    /// One owner library query, as the console states it. Every filter is applied
    /// by the service; the console sends the filter and renders the answer.
    /// <see cref="FromUtc"/> and <see cref="ToUtc"/> are inclusive bounds over the
    /// item's own occurred instant (its recorded instant when it has none).
    /// <see cref="AsOfUtc"/> is the S7-T1 as-of instant: the service replays
    /// lifecycle history only through that instant. It must be UTC and not in
    /// the future; the service refuses anything else.
    /// </summary>
    public sealed record OwnerLibraryQuery(
        string? Text = null,
        string? ZoneId = null,
        string? CollectionId = null,
        string? Kind = null,
        string? Level = null,
        OwnerLibraryCurrentness Current = OwnerLibraryCurrentness.All,
        DateTimeOffset? FromUtc = null,
        DateTimeOffset? ToUtc = null,
        int Offset = 0,
        int Limit = 10,
        DateTimeOffset? AsOfUtc = null,
        bool IncludeArchived = false);

/// <summary>
/// One owner library hit exactly as the service ranked and described it. The
/// snippet is short by design: full text stays on the get-by-ID read.
/// </summary>
public sealed record OwnerLibraryHit(
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
    string Publication)
{
    /// <summary>
    /// The owner's own recall decision for this item and the reason it is
    /// ordered where it is. The reason names only the facts that moved the
    /// hit; it never says the item is less true.
    /// </summary>
    public string RecallState { get; init; } = "normal";

    /// <summary>Why this hit ranks where it does, in the service's own words.</summary>
    public string RankingReason { get; init; } = string.Empty;
}

    /// <summary>
    /// One owner library page. A generation behind the vault is never served as a
    /// page: the service refuses that search (<c>search_index_stale</c>) until the
    /// owner rebuilds the index.
    /// </summary>
    public sealed record OwnerLibraryPage(
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
        OwnerLibraryHit[] Hits,
        DateTimeOffset? AsOfUtc = null);

public sealed record OwnerLibrarySearchResult(
    OwnerCommandOutcome Outcome,
    string? ErrorCode,
    OwnerLibraryPage? Page);

/// <summary>
/// One stored citation reference of a memory item: the anchor the item was
/// saved with (source ID, claimed source revision and span) plus the
/// availability the item declared. A revision here is a historical claim, not
/// the live registry revision.
/// </summary>
public sealed record OwnerItemSource(
    string? SourceId,
    string? Locator,
    string Availability,
    long? SourceRevision,
    long? SpanStart,
    long? SpanEnd);

/// <summary>
/// One projected citation of a memory item: the stored anchor plus the
/// service's current/superseded/unknown answer for it and the warning the
/// service attached. A missing or unregistered source answers
/// <c>unknown</c> with a warning instead of pretending to be verified.
/// </summary>
public sealed record OwnerItemCitation(
    string? SourceId,
    string? Locator,
    string Availability,
    long? SourceRevision,
    long? SpanStart,
    long? SpanEnd,
    string Status,
    string? Warning);

/// <summary>
/// One owner memory item at the exact revision the service served, with its
/// stored attribution (author, executor, creation method, evidence, level),
/// stored citations and warnings. This is the whole detail projection the
/// console shows and the only thing a title rename is built from.
/// </summary>
public sealed record OwnerItemDetail(
    string Id,
    string ZoneId,
    long Revision,
    string Kind,
    string Author,
    string Executor,
    string CreationMethod,
    string Evidence,
    string Level,
    string? Title,
    string Body,
    OwnerItemSource[] Sources,
    OwnerItemCitation[] Citations,
    string[] Warnings,
    DateTimeOffset? OccurredAtUtc,
    DateTimeOffset RecordedAtUtc,
    string? Project,
    string? Environment,
    string? Version);

public sealed record OwnerItemReadResult(
    OwnerCommandOutcome Outcome,
    string? ErrorCode,
    OwnerItemDetail? Item);

/// <summary>
/// One registered source at the exact registry revision the service served.
/// The locator is the owner's own enrollment locator; it stays inside the
/// owner channel.
/// </summary>
public sealed record OwnerSourceDetail(
    string Id,
    string ZoneId,
    long Revision,
    string? Title,
    string? Locator,
    string? Version,
    string Availability,
    string Author,
    string Executor,
    string CreationMethod,
    DateTimeOffset? CapturedAtUtc,
    DateTimeOffset RecordedAtUtc);

public sealed record OwnerSourceReadResult(
    OwnerCommandOutcome Outcome,
    string? ErrorCode,
    OwnerSourceDetail? Source);

/// <summary>
/// One materialized library index generation, summarized: the schema version,
/// the canonical set fingerprint it was read from, how many rows it holds and
/// whether canonical has moved since. The rows themselves stay inside the
/// service.
/// </summary>
public sealed record OwnerIndexReport(
    int SchemaVersion,
    string CanonicalDigest,
    int RowCount,
    bool Stale,
    bool CanonicalMoved);

public sealed record OwnerIndexResult(
    OwnerCommandOutcome Outcome,
    string? ErrorCode,
    OwnerIndexReport? Index);

/// <summary>
/// One capture the owner typed: the verbatim unstructured text with the zone
/// it belongs to and the optional metadata the owner chose to declare. No
/// provider, fetch or classification exists on this path (R19).
/// </summary>
public sealed record OwnerCaptureDraft(
    string ResourceId,
    string ZoneId,
    string Text,
    string? Title = null,
    string? Locator = null,
    string? Author = null,
    DateTimeOffset? CapturedAtUtc = null);

public sealed record OwnerCaptureResult(
    OwnerCommandOutcome Outcome,
    string? ErrorCode,
    OwnerSourceDetail? Source);

/// <summary>
/// One captured note read back at its exact registry revision: the registry
/// metadata and the verbatim text the vault holds. A registration that keeps
/// no captured text answers <c>research_note_unavailable</c> instead of
/// inventing content.
/// </summary>
public sealed record OwnerResearchNoteResult(
    OwnerCommandOutcome Outcome,
    string? ErrorCode,
    OwnerSourceDetail? Source,
    string? Text);

/// <summary>One pending proposal identity from the owner inbox: never its body.</summary>
public sealed record OwnerProposalIdentity(string Id, long Revision, string ZoneId);

public sealed record OwnerProposalListResult(
    OwnerCommandOutcome Outcome,
    string? ErrorCode,
    OwnerProposalIdentity[]? Proposals);

/// <summary>
/// The reviewed content of one proposal: the fields the owner may change, plus
/// the attribution the proposal declared, which travels back unchanged so the
/// service can verify that the review never rewrote an author, a creation
/// method or an evidence level.
/// </summary>
public sealed record OwnerReviewDraft(
    string Kind,
    string Author,
    string CreationMethod,
    string Evidence,
    string Level,
    string? Title,
    string Body,
    OwnerItemSource[] Sources,
    DateTimeOffset? OccurredAtUtc,
    string? Project,
    string? Environment,
    string? Version);

/// <summary>
/// Computed review state of one pending proposal as the service reported it:
/// whether a source the proposal cites moved, disappeared, became unavailable,
/// or was revoked since the proposal was recorded. It reports the staleness of
/// the cited evidence only; it never rewrites the proposal, its declared level
/// or evidence, or an owner's conclusion.
/// </summary>
public sealed record OwnerProposalCitationReview(
    string State,
    string[] Reasons,
    string[] StaleSourceIds);

/// <summary>
/// One pending proposal as the owner reviews it: its zone, kind, pinned
/// subject, approved inbox destination, idempotency identity, payload hash,
/// executor and recorded time, the agent's confirmation claim (a claim only,
/// never verified by this surface), the proposed item draft exactly as the
/// agent wrote it, and the computed review state of the sources it cites.
/// </summary>
public sealed record OwnerProposalDetail(
    string Id,
    string ZoneId,
    long Revision,
    string Kind,
    string DestinationResourceId,
    long DestinationRevision,
    string? SubjectResourceId,
    long? SubjectRevision,
    string IdempotencyKey,
    string PayloadHash,
    string Executor,
    DateTimeOffset RecordedAtUtc,
    bool ClaimedUserConfirmed,
    OwnerReviewDraft Item,
    OwnerProposalCitationReview? CitationReview = null);

public sealed record OwnerProposalReadResult(
    OwnerCommandOutcome Outcome,
    string? ErrorCode,
    OwnerProposalDetail? Proposal);

/// <summary>
/// One review outcome: which proposal was reviewed at which revision, the item
/// the review wrote, the publication state its lifecycle record carries now,
/// and whether this request is what recorded that decision.
/// </summary>
public sealed record OwnerReviewOutcome(
    string ProposalId,
    long ProposalRevision,
    string Kind,
    string ItemId,
    long ItemRevision,
    string Publication,
    long LifecycleRevision,
    bool DecisionApplied);

public sealed record OwnerReviewResult(
    OwnerCommandOutcome Outcome,
    string? ErrorCode,
    OwnerReviewOutcome? Review);

/// <summary>One item's publication record: its live revision and publication state.</summary>
public sealed record OwnerLifecycleState(
    string SubjectResourceId,
    long Revision,
    string Publication);

public sealed record OwnerLifecycleResult(
    OwnerCommandOutcome Outcome,
    string? ErrorCode,
    OwnerLifecycleState? Lifecycle);

/// <summary>
/// S7-T4 the owner's own recall decision for one subject memory item, as the
/// service reported it: the disposition, the reason the owner gave, who acted
/// and when, and the bounded act history that explains how the decision was
/// reached. It never carries the item's body and never says the item is less
/// true.
/// </summary>
public sealed record OwnerRecallState(
    string SubjectResourceId,
    string Disposition,
    string? Rationale,
    string? Actor,
    DateTimeOffset? RecordedAtUtc,
    OwnerRecallChange[] History);

/// <summary>
/// One recorded S7-T4 owner recall act: the disposition the act moved the
/// subject from, the disposition it moved it to, the actor, the UTC time and
/// the owner's own reason.
/// </summary>
public sealed record OwnerRecallChange(
    string From,
    string To,
    string Actor,
    DateTimeOffset RecordedAtUtc,
    string? Rationale);

/// <summary>
/// The outcome of one S7-T4 owner recall act or read on the owner channel.
/// </summary>
public sealed record OwnerRecallResult(
    OwnerCommandOutcome Outcome,
    string? ErrorCode,
    OwnerRecallState? Recall);

/// <summary>
/// One cited source as the S7-T6 review evaluation reported it: the exact
/// source ID the item cites, the exact revision it cited, the live revision
/// now, and what is known about that source's availability. "available",
/// "unavailable", "unknown" and "missing" stay distinct.
/// </summary>
public sealed record OwnerReviewCitation(
    string SourceId,
    long? CitedRevision,
    long? LiveRevision,
    string Availability,
    DateTimeOffset? SourceRecordedAtUtc);

/// <summary>
/// One item in the owner's S7-T6 review queue, exactly as the service reported
/// it: the identity the vault holds, the reasons the service computed, the
/// review-after stamp with its precision and offset, the reasons the owner
/// has not answered yet, and the owner's own last review.
/// </summary>
public sealed record OwnerReviewEntry(
    string ResourceId,
    string ZoneId,
    long ResourceRevision,
    string Kind,
    string? Title,
    string State,
    string[] Reasons,
    string[] UndeterminedReasons,
    string[] PendingReasons,
    string[] AcknowledgedReasons,
    OwnerTimelineStamp? ReviewAfter,
    DateTimeOffset? LastReviewedAtUtc,
    string? LastReviewedRationale,
    OwnerReviewCitation[] Citations);

/// <summary>The whole S7-T6 owner review queue as the service reported it.</summary>
public sealed record OwnerReviewQueue(
    OwnerReviewEntry[] Entries,
    int ScannedItems,
    bool Truncated);

/// <summary>The outcome of one S7-T6 review queue read on the owner channel.</summary>
public sealed record OwnerReviewQueueResult(
    OwnerCommandOutcome Outcome,
    string? ErrorCode,
    OwnerReviewQueue? Queue);

/// <summary>
/// One recorded S7-T6 owner review: the subject, when the owner looked, who
/// looked, the owner's own reason, and the reasons the owner saw then.
/// </summary>
public sealed record OwnerReviewRecord(
    string SubjectResourceId,
    DateTimeOffset ReviewedAtUtc,
    string Actor,
    string Rationale,
    string[] AcknowledgedReasons);

/// <summary>The outcome of one S7-T6 owner review act on the owner channel.</summary>
public sealed record OwnerReviewRecordResult(
    OwnerCommandOutcome Outcome,
    string? ErrorCode,
    OwnerReviewRecord? Review);

/// <summary>One canonical resource a purge preview would delete (ID + live revision).</summary>
public sealed record OwnerPurgeImpact(
    string ResourceId,
    string Kind,
    long Revision,
    string Reason);

/// <summary>Owner purge impact preview exactly as the service computed it.</summary>
public sealed record OwnerPurgePreview(
    string PurgeId,
    string TargetResourceId,
    long TargetRevision,
    string TargetZoneId,
    string TargetKind,
    OwnerPurgeImpact[] Impacted,
    string[] WithdrawnCatalogIds,
    bool GrantsTouched,
    bool SessionsTouched,
    long LiveGeneration,
    string S8BackupNote);

/// <summary>Owner purge preview outcome on the owner channel.</summary>
public sealed record OwnerPurgePreviewResult(
    OwnerCommandOutcome Outcome,
    string? ErrorCode,
    OwnerPurgePreview? Preview);

/// <summary>One completed (or resumed) owner purge outcome.</summary>
public sealed record OwnerPurgeOutcome(
    string PurgeId,
    string TargetResourceId,
    OwnerPurgeDeleted[] Deleted,
    string[] WithdrawnCatalogIds,
    long FinalGeneration,
    bool DecisionApplied);

/// <summary>One canonical resource a purge deleted (ID + revision only).</summary>
public sealed record OwnerPurgeDeleted(string ResourceId, long Revision);

/// <summary>Owner purge confirm/resume outcome on the owner channel.</summary>
public sealed record OwnerPurgeResult(
    OwnerCommandOutcome Outcome,
    string? ErrorCode,
    OwnerPurgeOutcome? Purge);
/// <summary>
/// One private evidence pin a declassification stands on, as the service
/// reported it on the owner channel only.
/// </summary>
public sealed record OwnerDeclassifyPin(string ResourceId, long Revision);

/// <summary>
/// Owner-only preview of a sanitized cross-zone publication: the exact content
/// the shared zone would receive beside the private pins it would stand on.
/// </summary>
public sealed record OwnerDeclassifyPreview(
    string DestinationResourceId,
    string DestinationZoneId,
    OwnerReviewDraft Item,
    OwnerDeclassifyPin[] Pins,
    bool PinsCurrent,
    string[] MismatchedResourceIds);

public sealed record OwnerDeclassifyPreviewResult(
    OwnerCommandOutcome Outcome,
    string? ErrorCode,
    OwnerDeclassifyPreview? Preview);

/// <summary>
/// One completed owner declassification: the shared destination, its revision,
/// the publication record revision, the encrypted lineage relation IDs and
/// whether this call is what wrote them.
/// </summary>
public sealed record OwnerDeclassifyOutcome(
    string DestinationResourceId,
    long DestinationRevision,
    long LifecycleRevision,
    string[] LineageRelationIds,
    bool DecisionApplied);

public sealed record OwnerDeclassifyResult(
    OwnerCommandOutcome Outcome,
    string? ErrorCode,
    OwnerDeclassifyOutcome? Published);

/// <summary>
/// One relation endpoint as the service projected it: the exact opaque ID and
/// verified kind the link pinned, the revision it was made against, and the
/// optional evidence span. Only the owner's own view names resources the owner
/// can already read; an agent view is filtered by that session's scope.
/// </summary>
public sealed record OwnerRelationEndpoint(
    string ResourceId,
    string EndpointKind,
    long Revision,
    long? SpanStart,
    long? SpanEnd);

/// <summary>
/// One caller-reported relation request that accompanied a create, unlink or
/// restore. It is the words the caller sent, recorded as a claim with its
/// executor and time: it is never owner confirmation, authorization or
/// declassification.
/// </summary>
public sealed record OwnerRelationRequestEvidence(
    string Action,
    string Text,
    string Executor,
    DateTimeOffset RecordedAtUtc);

/// <summary>
/// The service's computed review state of one relation: whether the link can
/// still be trusted as recorded, which rules flagged it, and which endpoints of
/// this relation are the reason. It never rewrites the stored link.
/// </summary>
public sealed record OwnerRelationReview(
    string State,
    string[] Reasons,
    string[] StaleResourceIds);

/// <summary>
/// One relation exactly as the service served it: identity, live revision,
/// kind, lifecycle state, both pinned endpoints, its provenance (executor,
/// creation method, reason, and the act that last changed its state) and any
/// request evidence an agent path recorded.
/// </summary>
public sealed record OwnerRelation(
    string Id,
    string ZoneId,
    long Revision,
    string Kind,
    string State,
    OwnerRelationEndpoint From,
    OwnerRelationEndpoint To,
    string Executor,
    string CreationMethod,
    string Reason,
    string? StateReason,
    DateTimeOffset? OccurredAtUtc,
    DateTimeOffset RecordedAtUtc,
    string StateExecutor,
    DateTimeOffset StateRecordedAtUtc,
    OwnerRelationReview Review,
    OwnerRelationRequestEvidence[] RequestEvidence);

public sealed record OwnerRelationListResult(
    OwnerCommandOutcome Outcome,
    string? ErrorCode,
    OwnerRelation[]? Relations);

public sealed record OwnerRelationResult(
    OwnerCommandOutcome Outcome,
    string? ErrorCode,
    OwnerRelation? Relation);

/// <summary>
/// One owner-approved catalog mapping exactly as the service stores it. The
/// resource/revision pair is the private mapping and stays on the owner
/// channel; only the catalog ID, label and description ever reach discovery.
/// </summary>
public sealed record OwnerCatalogMapping(
    string CatalogId,
    string Label,
    string Description,
    string ResourceId,
    long Revision,
    DateTimeOffset UpdatedAtUtc);

/// <summary>
/// What publication of one catalog entry would store, plus the live revision
/// of the mapped resource. Mutates nothing and mints no access.
/// </summary>
public sealed record OwnerCatalogPreviewEntry(
    string CatalogId,
    string Label,
    string Description,
    string ResourceId,
    long ResourceRevision,
    OwnerCatalogMapping? ExistingEntry);

public sealed record OwnerCatalogPreviewResult(
    OwnerCommandOutcome Outcome,
    string? ErrorCode,
    OwnerCatalogPreviewEntry? Preview);

public sealed record OwnerCatalogListResult(
    OwnerCommandOutcome Outcome,
    string? ErrorCode,
    OwnerCatalogMapping[]? Entries);

public sealed record OwnerCatalogPublishResult(
    OwnerCommandOutcome Outcome,
    string? ErrorCode,
    string? CatalogId);

public sealed record OwnerCatalogWithdrawResult(
    OwnerCommandOutcome Outcome,
    string? ErrorCode,
    string? CatalogId);

/// <summary>One concrete resource/revision pair inside a frozen owner-approved scope.</summary>
public sealed record OwnerScopeResource(string ResourceId, string ZoneId, long Revision);

/// <summary>
/// One access request as the owner sees it: the agent's catalog IDs, purpose,
/// declared provider/disclosure context and requested operations, plus the
/// resolved resource snapshot and the terminal decision, if any. A pending
/// request is never permission: only an explicit owner approval mints a grant.
/// </summary>
public sealed record OwnerAccessRequestInfo(
    string RequestId,
    string[] CatalogIds,
    string Purpose,
    string? AgentId,
    string? AgentContext,
    string? Provider,
    string? Model,
    string? DisclosureContext,
    string[] RequestedOperations,
    OwnerScopeResource[] ResolvedResources,
    DateTimeOffset CreatedAtUtc,
    string Status,
    string? ApprovedGrantId,
    DateTimeOffset? DecidedAtUtc);

/// <summary>One previewed request body: the exact resource bytes the vault holds, decoded for the owner's eyes only.</summary>
public sealed record OwnerAccessRequestBody(string ResourceId, string Text);

/// <summary>
/// The bound snapshot an approval would mint: the exact resources/revisions,
/// operations, purpose and disclosure the owner saw, with the bodies shown
/// once on the attached owner console. Held only in memory; a revision,
/// membership or catalog change between this preview and the approval fails
/// the approval closed.
/// </summary>
public sealed record OwnerAccessRequestPreviewInfo(
    string PreviewId,
    string RequestId,
    string[] CatalogIds,
    OwnerScopeResource[] Resources,
    string[] Operations,
    string Purpose,
    string? AgentId,
    string? AgentContext,
    string? Provider,
    string? Model,
    string? DisclosureContext,
    DateTimeOffset CreatedAtUtc,
    long PolicyGeneration,
    long UnlockEpoch,
    OwnerAccessRequestBody[] Bodies);

public sealed record OwnerAccessRequestListResult(
    OwnerCommandOutcome Outcome,
    string? ErrorCode,
    OwnerAccessRequestInfo[]? Requests);

public sealed record OwnerAccessRequestPreviewResult(
    OwnerCommandOutcome Outcome,
    string? ErrorCode,
    OwnerAccessRequestPreviewInfo? Preview);

/// <summary>
/// What one explicit owner approval minted. The bearer itself is never
/// returned here: it travels only into the DPAPI-protected local handoff file
/// at <see cref="HandoffPath"/>, and the console shows that path, never the token.
/// </summary>
public sealed record OwnerAccessApprovalResult(
    OwnerCommandOutcome Outcome,
    string? ErrorCode,
    string? GrantId,
    string? HandoffPath);

public sealed record OwnerAccessRejectResult(
    OwnerCommandOutcome Outcome,
    string? ErrorCode,
    string? RequestId);

/// <summary>One provider-egress ceiling inside a frozen session snapshot.</summary>
public sealed record OwnerSessionEgress(string Provider, string Model, decimal MaximumCostUsd);

/// <summary>
/// One live agent session as the owner sees it: the frozen grant snapshot
/// (concrete IDs/revisions, operations, destinations, expiry, egress) with no
/// verifier or bearer material. Added members or revisions never enter this
/// snapshot afterwards.
/// </summary>
public sealed record OwnerAgentSessionInfo(
    string SessionId,
    string GrantId,
    OwnerScopeResource[] Resources,
    string[] Operations,
    OwnerScopeResource[] Destinations,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    OwnerSessionEgress? Egress,
    long PolicyGeneration,
    long UnlockEpoch);

public sealed record OwnerSessionListResult(
    OwnerCommandOutcome Outcome,
    string? ErrorCode,
    long PolicyGeneration,
    OwnerAgentSessionInfo[]? Sessions);
public sealed record OwnerSessionRevokeResult(
    OwnerCommandOutcome Outcome,
    string? ErrorCode,
    string? SessionId);

/// <summary>
/// One enrolled source exactly as the service reported it. The private locator
/// is deliberately absent: the owner supplied it at enrollment, and the wire
/// carries only the mapping the service stores (source ID, zone, declared
/// format/version, generation and life state) plus the checkpoint, quarantine,
/// availability and encrypted cache scope the service computed. No transcript
/// content, locator, key or secret crosses this record.
/// </summary>
public sealed record OwnerSourceQuarantine(
    string Reason,
    string Detail,
    DateTimeOffset RecordedAtUtc);

/// <summary>
/// One enrolled source of the owner allowlist: its explicit mapping, its
/// enrollment/quarantine/pause life state, its persisted checkpoint summary,
/// the exact-locator availability observation (for a status read) and the
/// actual encrypted cache scope (journal/snapshot ciphertext sizes and the
/// snapshot revision, resolved from the live manifest). A list answer carries
/// no availability probe; a status answer carries the probe of the exact
/// enrolled locator only.
/// </summary>
public sealed record OwnerSourceEnrollment(
    string SourceId,
    string ZoneId,
    string Format,
    int FormatVersion,
    long Generation,
    string Life,
    long AnchorLength,
    DateTimeOffset EnrolledAtUtc,
    DateTimeOffset? RevokedAtUtc,
    OwnerSourceQuarantine? Quarantine,
    string Availability,
    DateTimeOffset? AvailabilityObservedAtUtc,
    DateTimeOffset? RelocatedAtUtc,
    long? SourceReferenceRevision,
    DateTimeOffset? PausedAtUtc,
    bool RefreshOptIn,
    long JournalCiphertextBytes,
    long SnapshotCiphertextBytes,
    long SnapshotRevision,
    long JournalRevision,
    long ConsumedOffset,
    int EventCount,
    int DiagnosticCount,
    DateTimeOffset? LastIngestAtUtc);

public sealed record OwnerSourceListResult(
    OwnerCommandOutcome Outcome,
    string? ErrorCode,
    OwnerSourceEnrollment[]? Sources);

public sealed record OwnerSourceStatusResult(
    OwnerCommandOutcome Outcome,
    string? ErrorCode,
    OwnerSourceEnrollment? Source);

/// <summary>
/// One ingestion outcome the service reported for a completed explicit owner
/// ingest: the status token (ingest_advanced, ingest_idle,
/// ingest_partial_pending), the checkpoint the run acted on and the enrolled
/// mapping it belongs to. The unsupported diagnostics arrive as explicit
/// status tokens but stay refusals: they carry no outcome here and remain on
/// the result's refusal outcome and error code instead of collapsing into a
/// generic failure, and an unregistered format is an explicit unsupported
/// refusal rather than a silent best effort.
/// </summary>
public sealed record OwnerSourceIngestOutcome(
    string Status,
    long ConsumedOffset,
    int EventsAdded,
    int DiagnosticsAdded,
    string? DiagnosticCode,
    OwnerSourceEnrollment? Enrollment);

public sealed record OwnerSourceIngestResult(
    OwnerCommandOutcome Outcome,
    string? ErrorCode,
    OwnerSourceIngestOutcome? Ingest);

/// <summary>
/// One refresh entry of an explicit owner refresh pass: the source, its
/// refresh status (an ingest outcome for an opted-in active source, or the
/// exact skip reason with no read for every other source), and the checkpoint
/// movement of that pass. No transcript content, locator or secret crosses
/// this record.
/// </summary>
public sealed record OwnerSourceRefreshEntry(
    string SourceId,
    string Status,
    long ConsumedOffset,
    int EventsAdded,
    int DiagnosticsAdded,
    string? DiagnosticCode);

public sealed record OwnerSourceRefreshResult(
    OwnerCommandOutcome Outcome,
    string? ErrorCode,
    OwnerSourceRefreshEntry[]? Entries);

/// <summary>
/// One owner collection exactly as the service stores it: the collection ID
/// the owner named and the member resource IDs it currently holds. A
/// collection is owner-side selection metadata only: adding a member never
/// widens a frozen grant or session, and the service resolves a collection
/// into concrete resource revisions only at grant-preview time.
/// </summary>
public sealed record OwnerCollectionMapping(
    string CollectionId,
    string[] ResourceIds);

public sealed record OwnerCollectionListResult(
    OwnerCommandOutcome Outcome,
    string? ErrorCode,
    OwnerCollectionMapping[]? Collections);

public sealed record OwnerCollectionSetResult(
    OwnerCommandOutcome Outcome,
    string? ErrorCode,
    OwnerCollectionMapping? Collection);

/// <summary>
/// One explicitly preserved source snapshot: its snapshot identity, the zone
/// it was preserved in, its live revision, the source generation it froze,
/// the source reference revision it was taken from, the exact byte length and
/// digest it fingerprints, and the owner-selected retention it carries.
/// Content itself leaves the service only through the explicit snapshot-read
/// path the owner names; this record carries no bytes.
/// </summary>
public sealed record OwnerSourceSnapshotDescriptor(
    string Id,
    string ZoneId,
    long Revision,
    long Generation);

public sealed record OwnerSourceSnapshotEntry(
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
    bool Purged);

public sealed record OwnerSourceSnapshotListResult(
    OwnerCommandOutcome Outcome,
    string? ErrorCode,
    OwnerSourceSnapshotDescriptor[]? Snapshots);

public sealed record OwnerSourceSnapshotResult(
    OwnerCommandOutcome Outcome,
    string? ErrorCode,
    OwnerSourceSnapshotEntry? Snapshot);

/// <summary>One explicitly selected job input, pinned to the revision the job may read.</summary>
public sealed record OwnerJobInput(string ResourceId, long Revision);

/// <summary>
/// The recorded scope boundaries of one model job: the selected inputs, the
/// destination inbox, the provider/model, and the caps that applied. It is the
/// service's own record, never a choice this client makes.
/// </summary>
public sealed record OwnerJobScope(
    OwnerJobInput[] Inputs,
    string DestinationZoneId,
    string DestinationResourceId,
    long DestinationRevision,
    string Provider,
    string Model,
    decimal MaximumCostUsd,
    long MaxInputTokens,
    long MaxOutputTokens,
    string? Profile,
    string? Objective,
    string? TargetLevel);

public sealed record OwnerJobUsage(long InputTokens, long OutputTokens, decimal CostUsd);

public sealed record OwnerJobFailure(string Code, string Message);

/// <summary>
/// One model job exactly as the service stored it: identity, state, recorded
/// scope, reported usage, failure reason and the proposals it committed. It is
/// never a proposed body and never evidence that output was published.
/// </summary>
public sealed record OwnerJob(
    string Id,
    string ZoneId,
    long Revision,
    string Kind,
    string State,
    OwnerJobScope Scope,
    OwnerJobUsage? Usage,
    OwnerJobFailure? Failure,
    string[] OutputProposalIds,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc);

public sealed record OwnerJobResult(OwnerCommandOutcome Outcome, string? ErrorCode, OwnerJob? Job);

public sealed record OwnerJobListResult(OwnerCommandOutcome Outcome, string? ErrorCode, OwnerJob[]? Jobs);

/// <summary>
/// The model/egress settings the service would use for a job, and the frozen
/// pre-send caps it enforces. The API key is never part of this answer.
/// </summary>
public sealed record OwnerProviderSettings(
    bool Configured,
    string? Provider,
    string? Model,
    string? BaseUrl,
    decimal MaximumJobCostUsd,
    long MaximumJobInputTokens,
    long MaximumJobOutputTokens,
    decimal MaximumRolling30DayCostUsd);

public sealed record OwnerProviderResult(OwnerCommandOutcome Outcome, string? ErrorCode, OwnerProviderSettings? Settings);

/// <summary>
/// The owner's stored synthesis schedule exactly as the service holds it: the
/// explicit selection, the interval, the destination, the provider/model, and
/// the observed timing of the last scheduled attempt.
/// </summary>
public sealed record OwnerSchedule(
    bool Enabled,
    int IntervalMinutes,
    OwnerJobInput[] Inputs,
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
/// A schedule answer: <see cref="Schedule"/> is null only when the service
/// reported that no schedule is configured.
/// </summary>
public sealed record OwnerScheduleResult(OwnerCommandOutcome Outcome, string? ErrorCode, OwnerSchedule? Schedule);

/// <summary>What one evaluation of the owner's schedule did, and nothing more.</summary>
public sealed record OwnerScheduleRun(
    OwnerCommandOutcome Outcome,
    string? ErrorCode,
    string? RunOutcome,
    string? JobId,
    string? JobState,
    string? FailureCode,
    DateTimeOffset? NextDueAtUtc,
    string? Message);
public sealed record OwnerScheduledIntention(
    string ResourceId,
    string ZoneId,
    long LifecycleRevision,
    string State,
    string? Title,
    string BodyPreview,
    DateTimeOffset? DueAt,
    bool IsDue,
    bool Notified,
    DateTimeOffset? NotifiedAtUtc,
    bool Acknowledged,
    DateTimeOffset? AcknowledgedAtUtc,
    string? ContextProject,
    string? ContextTask,
    bool ContextTriggerEnabled);

public sealed record OwnerIntentionScheduleListResult(
    OwnerCommandOutcome Outcome,
    string? ErrorCode,
    OwnerScheduledIntention[]? Intentions);

public sealed record OwnerIntentionSchedulePassResult(
    OwnerCommandOutcome Outcome,
    string? ErrorCode,
    int? NotificationsRecorded);

public sealed record OwnerSpendEntry(
    string EntryId,
    string JobId,
    DateTimeOffset TimestampUtc,
    string Provider,
    string Model,
    long InputTokens,
    long OutputTokens,
    decimal CostUsd,
    bool IsReservation);

public sealed record OwnerSpendLedger(
    decimal HistoricalReservationUsd,
    decimal RollingSpendUsd,
    OwnerSpendEntry[] Entries);

public sealed record OwnerSpendLedgerResult(OwnerCommandOutcome Outcome, string? ErrorCode, OwnerSpendLedger? Ledger);


/// <summary>
/// The owner-application client of the authenticated owner control channel.
/// It owns no key material and no authority: every call is an ordinary service
/// request whose owner authority the service derives from the caller's Windows
/// token on the owner pipe. Identity-shaped body fields, Origin/localhost or
/// agent claims are never consulted here or by the service.
/// </summary>
public static class OwnerChannelClient
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>
    /// Strict UTF-8 for captured text: a string this process cannot represent
    /// is refused rather than stored with substituted replacement characters,
    /// so what the owner typed is what a restart reads back.
    /// </summary>
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Protocol revision this client speaks; the service refuses other revisions.</summary>
    public static int ProtocolVersion => ServiceRequestHandler.CurrentProtocolVersion;

    /// <summary>True when the value can be used as a Windows named-pipe name.</summary>
    public static bool IsValidControlPipe(string? controlPipe) => ServiceSettingsLoader.IsSafeIdentifier(controlPipe);

    /// <summary>
    /// Asks the running service for its owner status over the owner control pipe
    /// and classifies the answer. Transport failures, refusals and unrecognized
    /// replies are reported honestly instead of being folded into a success.
    /// </summary>
    public static async Task<OwnerChannelStatus> ReadStatusAsync(string controlPipe, CancellationToken cancellationToken)
    {
        var reply = await ReadReplyAsync(
            controlPipe,
            new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, ServiceRequestHandler.OwnerStatusOperation),
            "running",
            cancellationToken).ConfigureAwait(false);

        return reply.Outcome switch
        {
            OwnerCommandOutcome.Completed => new OwnerChannelStatus(OwnerChannelAvailability.Reachable, reply.VaultState,
                reply.VaultState == OwnerVaultState.Unknown ? "vault_state_unknown" : null),
            OwnerCommandOutcome.OwnerAuthorityDenied => new OwnerChannelStatus(
                OwnerChannelAvailability.OwnerAuthorityDenied, OwnerVaultState.Unknown, reply.ErrorCode),
            OwnerCommandOutcome.ProtocolMismatch => new OwnerChannelStatus(
                OwnerChannelAvailability.ProtocolMismatch, OwnerVaultState.Unknown, reply.ErrorCode),
            _ => new OwnerChannelStatus(OwnerChannelAvailability.ServiceUnavailable, OwnerVaultState.Unknown, reply.ErrorCode)
        };
    }

    /// <summary>
    /// Creates the owner vault with the owner-chosen passphrase. The service
    /// mints the recovery material once and returns it in the result; nothing
    /// is persisted by this client.
    /// </summary>
    public static Task<OwnerVaultCommandResult> ProvisionAsync(string controlPipe, string passphrase, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(passphrase);
        return ExecuteVaultCommandAsync(
            controlPipe,
            new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, ServiceRequestHandler.OwnerVaultProvisionOperation,
                Passphrase: passphrase),
            "provisioned",
            cancellationToken);
    }

    /// <summary>Opens the existing vault with the owner passphrase.</summary>
    public static Task<OwnerVaultCommandResult> UnlockAsync(string controlPipe, string passphrase, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(passphrase);
        return ExecuteVaultCommandAsync(
            controlPipe,
            new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, ServiceRequestHandler.OwnerVaultUnlockOperation,
                Passphrase: passphrase),
            "unlocked",
            cancellationToken);
    }

    /// <summary>
    /// Opens the existing vault with the recovery material the owner kept
    /// outside the vault. This opens the same vault; it restores nothing and
    /// resets nothing.
    /// </summary>
    public static Task<OwnerVaultCommandResult> UnlockWithRecoveryCodeAsync(string controlPipe, string recoveryCode, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recoveryCode);
        return ExecuteVaultCommandAsync(
            controlPipe,
            new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, ServiceRequestHandler.OwnerVaultRecoverOperation,
                RecoveryCode: recoveryCode),
            "unlocked",
            cancellationToken);
    }

    /// <summary>
    /// Asks the service to lock the vault. The service owns the lock lifecycle:
    /// it drains in-flight private operations, clears the key, invalidates
    /// pending tokens and live sessions and stops private work.
    /// </summary>
    public static Task<OwnerVaultCommandResult> LockAsync(string controlPipe, CancellationToken cancellationToken) =>
        ExecuteVaultCommandAsync(
            controlPipe,
            new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, ServiceRequestHandler.OwnerVaultLockOperation),
            "locked",
            cancellationToken);

    /// <summary>
    /// Searches the owner's own library over the owner control pipe. The
    /// service applies the text, zone, collection, kind, level, time and
    /// currentness filters and ranks the page; this client only classifies the
    /// answer, so a refusal (unknown filter, no materialized index generation)
    /// is never presented as a result set.
    /// </summary>
    public static async Task<OwnerLibrarySearchResult> SearchLibraryAsync(
        string controlPipe,
        OwnerLibraryQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var reply = await ReadReplyAsync(
            controlPipe,
            new ServiceRequest(
                ServiceRequestHandler.CurrentProtocolVersion,
                ServiceRequestHandler.OwnerLibrarySearchOperation,
                ZoneId: query.ZoneId,
                CollectionId: query.CollectionId,
                Query: query.Text,
                SearchOffset: query.Offset,
                SearchLimit: query.Limit,
                SearchKind: query.Kind,
                SearchLevel: query.Level,
                SearchCurrent: CurrentToken(query.Current),
                SearchFromUtc: query.FromUtc,
                SearchToUtc: query.ToUtc,
                SearchAsOfUtc: query.AsOfUtc,
                IncludeArchived: query.IncludeArchived),
            "library_page",
            cancellationToken).ConfigureAwait(false);

        return new OwnerLibrarySearchResult(reply.Outcome, reply.ErrorCode, ProjectLibraryPage(reply));
    }

    /// <summary>
    /// Applies one S7-T4 owner recall act through the owner control pipe: the
    /// owner demotes, pins, sets aside or restores one subject memory item.
    /// The act is owner-only and needs an unlocked vault; it changes only how
    /// that item is offered and never touches its content, evidence, revision
    /// or any grant.
    /// </summary>
    public static async Task<OwnerRecallResult> ApplyRecallActAsync(
        string controlPipe,
        string resourceId,
        string act,
        string? rationale,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resourceId);
        ArgumentNullException.ThrowIfNull(act);
        var reply = await ReadReplyAsync(
            controlPipe,
            new ServiceRequest(
                ServiceRequestHandler.CurrentProtocolVersion,
                ServiceRequestHandler.OwnerRecallApplyOperation,
                ResourceId: resourceId,
                Label: act,
                Description: rationale),
            "recall_applied",
            cancellationToken).ConfigureAwait(false);
        return new OwnerRecallResult(reply.Outcome, reply.ErrorCode, ProjectRecallState(reply));
    }

    /// <summary>
    /// Reads the owner's own S7-T4 recall decision for one subject memory
    /// item, with the recorded act history that explains it.
    /// </summary>
    public static async Task<OwnerRecallResult> ReadRecallStateAsync(
        string controlPipe,
        string resourceId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resourceId);
        var reply = await ReadReplyAsync(
            controlPipe,
            new ServiceRequest(
                ServiceRequestHandler.CurrentProtocolVersion,
                ServiceRequestHandler.OwnerRecallReadOperation,
                ResourceId: resourceId),
            "recall_read",
            cancellationToken).ConfigureAwait(false);
        return new OwnerRecallResult(reply.Outcome, reply.ErrorCode, ProjectRecallState(reply));
    }

    /// <summary>
    /// Reads the owner's S7-T6 review queue through the owner control pipe:
    /// the items whose review-after deadline passed, whose cited source moved
    /// past the cited revision, whose cited source is gone, or whose cited
    /// source states that it is unavailable. The queue is a report; reading it
    /// changes nothing in the vault.
    /// </summary>
    public static async Task<OwnerReviewQueueResult> ListReviewQueueAsync(
        string controlPipe,
        CancellationToken cancellationToken)
    {
        var reply = await ReadReplyAsync(
            controlPipe,
            new ServiceRequest(
                ServiceRequestHandler.CurrentProtocolVersion,
                ServiceRequestHandler.OwnerReviewListOperation),
            "review_listed",
            cancellationToken).ConfigureAwait(false);
        return new OwnerReviewQueueResult(reply.Outcome, reply.ErrorCode, ProjectReviewQueue(reply));
    }

    /// <summary>
    /// Records one explicit S7-T6 owner review of one subject memory item
    /// through the owner control pipe. The act stores the owner's own
    /// rationale in the owner's private review state: the item's content,
    /// evidence, citations, times and states are untouched, and no belief is
    /// denied.
    /// </summary>
    public static async Task<OwnerReviewRecordResult> RecordReviewAsync(
        string controlPipe,
        string resourceId,
        string rationale,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resourceId);
        ArgumentNullException.ThrowIfNull(rationale);
        var reply = await ReadReplyAsync(
            controlPipe,
            new ServiceRequest(
                ServiceRequestHandler.CurrentProtocolVersion,
                ServiceRequestHandler.OwnerReviewRecordOperation,
                ResourceId: resourceId,
                Description: rationale),
            "review_recorded",
            cancellationToken).ConfigureAwait(false);
        return new OwnerReviewRecordResult(reply.Outcome, reply.ErrorCode, ProjectOwnerReview(reply));
    }

    /// <summary>
    /// S7-T5 owner purge preview through the owner control pipe: binds the
    /// live target revision and returns the exact impacted set. Writes nothing.
    /// </summary>
    public static async Task<OwnerPurgePreviewResult> PreviewPurgeAsync(
        string controlPipe,
        string resourceId,
        long revision,
        string reason,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resourceId);
        ArgumentNullException.ThrowIfNull(reason);
        var reply = await ReadReplyAsync(
            controlPipe,
            new ServiceRequest(
                ServiceRequestHandler.CurrentProtocolVersion,
                ServiceRequestHandler.OwnerPurgePreviewOperation,
                ResourceId: resourceId,
                ResourceRevision: revision,
                Description: reason),
            "purge_preview",
            cancellationToken).ConfigureAwait(false);
        return new OwnerPurgePreviewResult(reply.Outcome, reply.ErrorCode, ProjectPurgePreview(reply));
    }

    /// <summary>
    /// S7-T5 owner purge confirm: deletes the exact previewed set. The
    /// preview fields are rebound here so the service fails closed on change.
    /// </summary>
    public static async Task<OwnerPurgeResult> ConfirmPurgeAsync(
        string controlPipe,
        OwnerPurgePreview preview,
        string reason,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(reason);
        var reply = await ReadReplyAsync(
            controlPipe,
            new ServiceRequest(
                ServiceRequestHandler.CurrentProtocolVersion,
                ServiceRequestHandler.OwnerPurgeConfirmOperation,
                ResourceId: preview.PurgeId,
                ZoneId: preview.TargetResourceId,
                ResourceRevision: preview.TargetRevision,
                CatalogId: preview.TargetZoneId,
                Label: preview.TargetKind + "|PURGE",
                DestinationResourceIds: preview.Impacted.Select(item => $"{item.ResourceId}|{item.Revision}|{item.Kind}|{item.Reason}").ToArray(),
                DestinationRevision: preview.LiveGeneration,
                CatalogIds: preview.WithdrawnCatalogIds,
                Description: reason),
            "purged",
            cancellationToken).ConfigureAwait(false);
        return new OwnerPurgeResult(reply.Outcome, reply.ErrorCode, ProjectPurgeOutcome(reply));
    }

    /// <summary>
    /// S7-T5 owner purge resume after interruption: resumes one
    /// confirmed-but-incomplete intent by purge ID.
    /// </summary>
    public static async Task<OwnerPurgeResult> ResumePurgeAsync(
        string controlPipe,
        string purgeId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(purgeId);
        var reply = await ReadReplyAsync(
            controlPipe,
            new ServiceRequest(
                ServiceRequestHandler.CurrentProtocolVersion,
                ServiceRequestHandler.OwnerPurgeResumeOperation,
                ResourceId: purgeId),
            "purge_resumed",
            cancellationToken).ConfigureAwait(false);
        if (reply.Outcome != OwnerCommandOutcome.Completed)
        {
            var purged = await ReadReplyAsync(
                controlPipe,
                new ServiceRequest(
                    ServiceRequestHandler.CurrentProtocolVersion,
                    ServiceRequestHandler.OwnerPurgeResumeOperation,
                    ResourceId: purgeId),
                "purged",
                cancellationToken).ConfigureAwait(false);
            return new OwnerPurgeResult(purged.Outcome, purged.ErrorCode, ProjectPurgeOutcome(purged));
        }

        return new OwnerPurgeResult(reply.Outcome, reply.ErrorCode, ProjectPurgeOutcome(reply));
    }

    /// <summary>
    /// Reads one memory item at its exact revision (the live revision when none
    /// is named) with its stored attribution, citations and warnings.
    /// </summary>
    public static async Task<OwnerItemReadResult> ReadMemoryItemAsync(
        string controlPipe,
        string resourceId,
        long? revision,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resourceId);
        var reply = await ReadReplyAsync(
            controlPipe,
            new ServiceRequest(
                ServiceRequestHandler.CurrentProtocolVersion,
                ServiceRequestHandler.MemoryReadOperation,
                ResourceId: resourceId,
                ResourceRevision: revision),
            "memory",
            cancellationToken).ConfigureAwait(false);

        return new OwnerItemReadResult(reply.Outcome, reply.ErrorCode, ProjectItem(reply));
    }

    /// <summary>
    /// Renames one memory item's title through the existing revision-gated
    /// owner revise operation: the draft is rebuilt from the stored item the
    /// owner is looking at, so the ID, body, citations, times, applicability,
    /// attribution and evidence stay exactly what the vault already holds and
    /// only the title changes. A stale expected revision, or a cited source
    /// anchor that no longer names the live registry revision, is refused by
    /// the service instead of silently re-anchoring or overwriting.
    /// </summary>
    public static async Task<OwnerItemReadResult> RenameMemoryTitleAsync(
        string controlPipe,
        OwnerItemDetail item,
        string title,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(title);
        var draft = JsonSerializer.SerializeToUtf8Bytes(new
        {
            kind = item.Kind,
            author = item.Author,
            creationMethod = item.CreationMethod,
            evidence = item.Evidence,
            level = item.Level,
            title,
            body = item.Body,
            sources = item.Sources.Select(source => new
            {
                sourceId = source.SourceId,
                locator = source.Locator,
                availability = source.Availability,
                sourceRevision = source.SourceRevision,
                span = source.SpanStart is { } start && source.SpanEnd is { } end
                    ? new { start, end }
                    : null
            }).ToArray(),
            occurredAtUtc = item.OccurredAtUtc?.ToUniversalTime(),
            recordedAtUtc = item.RecordedAtUtc.ToUniversalTime(),
            applicability = item.Project is null && item.Environment is null && item.Version is null
                ? null
                : new { project = item.Project, environment = item.Environment, version = item.Version }
        });
        try
        {
            var reply = await ReadReplyAsync(
                controlPipe,
                new ServiceRequest(
                    ServiceRequestHandler.CurrentProtocolVersion,
                    ServiceRequestHandler.MemoryReviseOperation,
                    ResourceId: item.Id,
                    ZoneId: item.ZoneId,
                    ContentBase64: Convert.ToBase64String(draft),
                    ResourceRevision: item.Revision),
                "memory_revised",
                cancellationToken).ConfigureAwait(false);

            return new OwnerItemReadResult(reply.Outcome, reply.ErrorCode, ProjectItem(reply));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(draft);
        }
    }

    /// <summary>
    /// Registers the preservation reference of the owner's selected enrolled
    /// source through the existing S2-T3 registry create: the metadata-only
    /// record the snapshot preserve freezes. The owner names the enrolled
    /// locator explicitly in this request (request-file discipline, as with
    /// enrollment); the service validates it against the live enrollment
    /// before storing revision 1. No new authority, no transcript read.
    /// </summary>
    public static async Task<OwnerSourceReadResult> RegisterSourceReferenceAsync(
        string controlPipe,
        string sourceId,
        string zoneId,
        string locator,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sourceId);
        ArgumentNullException.ThrowIfNull(zoneId);
        ArgumentNullException.ThrowIfNull(locator);
        var draftJson = BuildSourceReferenceDraft(locator);
        try
        {
            var reply = await ReadReplyAsync(
                controlPipe,
                new ServiceRequest(
                    ServiceRequestHandler.CurrentProtocolVersion,
                    ServiceRequestHandler.SourceCreateOperation,
                    ResourceId: sourceId,
                    ZoneId: zoneId,
                    ContentBase64: Convert.ToBase64String(draftJson)),
                "source_created",
                cancellationToken).ConfigureAwait(false);

            return new OwnerSourceReadResult(reply.Outcome, reply.ErrorCode, ProjectSource(reply));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(draftJson);
        }
    }

    private static byte[] BuildSourceReferenceDraft(string locator)
    {
        // The registry requires explicit provenance beside the locator: the
        // owner asserts the live availability the service will re-observe,
        // with a direct-write creation method and UTC record time.
        var draft = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
        {
            title = (string?)null,
            locator,
            version = "reference-v1",
            availability = "available",
            creationMethod = "directWrite",
            capturedAtUtc = (DateTimeOffset?)null,
            recordedAtUtc = DateTimeOffset.UtcNow,
        });
        if (draft.Length is <= 0 or > 1024 * 1024)
        {
            CryptographicOperations.ZeroMemory(draft);
            throw new ArgumentException("The source reference draft is invalid.", nameof(locator));
        }

        return draft;
    }

    /// <summary>
    /// Reads one registered source at its exact registry revision (the live
    /// revision when none is named), which is how an owner opens the evidence
    /// behind a citation without widening anything else.
    /// </summary>
    public static async Task<OwnerSourceReadResult> ReadSourceAsync(
        string controlPipe,
        string resourceId,
        long? revision,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resourceId);
        var reply = await ReadReplyAsync(
            controlPipe,
            new ServiceRequest(
                ServiceRequestHandler.CurrentProtocolVersion,
                ServiceRequestHandler.SourceReadOperation,
                ResourceId: resourceId,
                ResourceRevision: revision),
            "source",
            cancellationToken).ConfigureAwait(false);

        return new OwnerSourceReadResult(reply.Outcome, reply.ErrorCode, ProjectSource(reply));
    }

    /// <summary>
    /// Materializes the library index generation for the currently unlocked
    /// vault. This is the explicit owner act the existing index lifecycle
    /// requires before a search can be answered, and the reply is a summary of
    /// the generation only — the rows never leave the service.
    /// </summary>
    public static async Task<OwnerIndexResult> RebuildLibraryIndexAsync(
        string controlPipe,
        CancellationToken cancellationToken)
    {
        var reply = await ReadReplyAsync(
            controlPipe,
            new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, ServiceRequestHandler.OwnerIndexRebuildOperation),
            "index_rebuilt",
            cancellationToken).ConfigureAwait(false);

        return new OwnerIndexResult(reply.Outcome, reply.ErrorCode, ProjectIndex(reply));
    }

    /// <summary>
    /// Captures one piece of unstructured text into the encrypted source
    /// registry through the S3-T4 owner operation: the text and its zone are
    /// the write, the metadata is optional and never inferred from the text or
    /// the locator, and no provider, fetch, classification or summary runs on
    /// this path (R19). The text is encoded strictly, so a string this process
    /// cannot represent as UTF-8 is refused by the caller instead of being
    /// stored with substituted characters.
    /// </summary>
    public static async Task<OwnerCaptureResult> CaptureResearchNoteAsync(
        string controlPipe,
        OwnerCaptureDraft draft,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var text = StrictUtf8.GetBytes(draft.Text);
        try
        {
            var reply = await ReadReplyAsync(
                controlPipe,
                new ServiceRequest(
                    ServiceRequestHandler.CurrentProtocolVersion,
                    ServiceRequestHandler.ResearchCaptureOperation,
                    ResourceId: draft.ResourceId,
                    ZoneId: draft.ZoneId,
                    ContentBase64: Convert.ToBase64String(text),
                    SourceTitle: draft.Title,
                    SourceLocator: draft.Locator,
                    SourceAuthor: draft.Author,
                    SourceCapturedAtUtc: draft.CapturedAtUtc),
                "research_captured",
                cancellationToken).ConfigureAwait(false);

            return new OwnerCaptureResult(reply.Outcome, reply.ErrorCode, ProjectSource(reply));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(text);
        }
    }

    /// <summary>
    /// Reads one captured note at its exact registry revision, returning the
    /// verbatim text the vault holds beside its registry metadata, so what the
    /// owner sees after a restart is the bytes that were stored and nothing
    /// reconstructed. A registration that keeps no captured text answers
    /// <c>research_note_unavailable</c> rather than an empty note.
    /// </summary>
    public static async Task<OwnerResearchNoteResult> ReadResearchNoteAsync(
        string controlPipe,
        string resourceId,
        long? revision,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resourceId);
        var reply = await ReadReplyAsync(
            controlPipe,
            new ServiceRequest(
                ServiceRequestHandler.CurrentProtocolVersion,
                ServiceRequestHandler.ResearchReadOperation,
                ResourceId: resourceId,
                ResourceRevision: revision),
            "research_note",
            cancellationToken).ConfigureAwait(false);

        return new OwnerResearchNoteResult(reply.Outcome, reply.ErrorCode, ProjectSource(reply), DecodeServedText(reply));
    }

    /// <summary>
    /// Lists the pending proposal inbox: identity, live revision and zone of
    /// each pending proposal, never a proposed body.
    /// </summary>
    public static async Task<OwnerProposalListResult> ListProposalsAsync(
        string controlPipe,
        CancellationToken cancellationToken)
    {
        var reply = await ReadReplyAsync(
            controlPipe,
            new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, ServiceRequestHandler.ProposalListOperation),
            "proposals",
            cancellationToken).ConfigureAwait(false);

        return new OwnerProposalListResult(reply.Outcome, reply.ErrorCode, ProjectProposalIdentities(reply));
    }

    /// <summary>
    /// Reads one pending proposal at its exact live revision (or the named
    /// one): its provenance plus the proposed item draft exactly as the agent
    /// wrote it.
    /// </summary>
    public static async Task<OwnerProposalReadResult> ReadProposalAsync(
        string controlPipe,
        string proposalId,
        long? revision,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(proposalId);
        var reply = await ReadReplyAsync(
            controlPipe,
            new ServiceRequest(
                ServiceRequestHandler.CurrentProtocolVersion,
                ServiceRequestHandler.ProposalReadOperation,
                ResourceId: proposalId,
                ResourceRevision: revision),
            "proposal",
            cancellationToken).ConfigureAwait(false);

        return new OwnerProposalReadResult(reply.Outcome, reply.ErrorCode, ProjectProposal(reply));
    }

    /// <summary>
    /// Publishes one pending proposal with the content the owner reviewed. The
    /// proposal is named at exactly the revision that was reviewed, and the
    /// attribution fields travel back as the proposal declared them: the
    /// service refuses a reviewed draft that names another author, creation
    /// method or evidence, so an approval can never relabel an AI-written
    /// record as user-written or user-confirmed.
    /// </summary>
    public static async Task<OwnerReviewResult> PublishProposalAsync(
        string controlPipe,
        OwnerProposalDetail proposal,
        OwnerReviewDraft reviewed,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(reviewed);
        var draft = BuildReviewDraft(
            reviewed.Kind, reviewed.Author, reviewed.CreationMethod, reviewed.Evidence, reviewed.Level,
            reviewed.Title, reviewed.Body, reviewed.Sources, reviewed.OccurredAtUtc,
            reviewed.Project, reviewed.Environment, reviewed.Version);
        try
        {
            var reply = await ReadReplyAsync(
                controlPipe,
                new ServiceRequest(
                    ServiceRequestHandler.CurrentProtocolVersion,
                    ServiceRequestHandler.OwnerProposalPublishOperation,
                    ResourceId: proposal.Id,
                    ResourceRevision: proposal.Revision,
                    ContentBase64: Convert.ToBase64String(draft)),
                "proposal_published",
                cancellationToken).ConfigureAwait(false);

            return new OwnerReviewResult(reply.Outcome, reply.ErrorCode, ProjectReview(reply));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(draft);
        }
    }

    /// <summary>
    /// Rejects one pending proposal with the rationale the owner gave, naming
    /// the proposal at exactly the revision that was reviewed. The proposal's
    /// own content is recorded as the rejected review item and the item the
    /// proposal would have revised is never touched.
    /// </summary>
    public static async Task<OwnerReviewResult> RejectProposalAsync(
        string controlPipe,
        OwnerProposalDetail proposal,
        string rationale,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(rationale);
        var draft = EncodeTransition(rationale, null);
        try
        {
            var reply = await ReadReplyAsync(
                controlPipe,
                new ServiceRequest(
                    ServiceRequestHandler.CurrentProtocolVersion,
                    ServiceRequestHandler.OwnerProposalRejectOperation,
                    ResourceId: proposal.Id,
                    ResourceRevision: proposal.Revision,
                    ContentBase64: Convert.ToBase64String(draft)),
                "proposal_rejected",
                cancellationToken).ConfigureAwait(false);

            return new OwnerReviewResult(reply.Outcome, reply.ErrorCode, ProjectReview(reply));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(draft);
        }
    }

    /// <summary>
    /// Previews one sanitized cross-zone publication: the exact content the
    /// shared zone would receive beside the private pins it would stand on.
    /// Mutates nothing and mints no access.
    /// </summary>
    public static async Task<OwnerDeclassifyPreviewResult> PreviewDeclassifyAsync(
        string controlPipe,
        string destinationResourceId,
        string destinationZoneId,
        OwnerDeclassifyPin[] pins,
        OwnerReviewDraft sanitized,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destinationResourceId);
        ArgumentNullException.ThrowIfNull(destinationZoneId);
        ArgumentNullException.ThrowIfNull(pins);
        ArgumentNullException.ThrowIfNull(sanitized);
        var draft = BuildDeclassifyDraft(pins, sanitized);
        try
        {
            var reply = await ReadReplyAsync(
                controlPipe,
                new ServiceRequest(
                    ServiceRequestHandler.CurrentProtocolVersion,
                    ServiceRequestHandler.OwnerDeclassifyPreviewOperation,
                    ResourceId: destinationResourceId,
                    ZoneId: destinationZoneId,
                    ContentBase64: Convert.ToBase64String(draft)),
                "declassify_preview",
                cancellationToken).ConfigureAwait(false);

            return new OwnerDeclassifyPreviewResult(reply.Outcome, reply.ErrorCode, ProjectDeclassifyPreview(reply));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(draft);
        }
    }

    /// <summary>
    /// Publishes one sanitized cross-zone publication with explicit private
    /// pins: the sanitized shared copy plus encrypted derived_from lineage.
    /// </summary>
    public static async Task<OwnerDeclassifyResult> PublishDeclassifyAsync(
        string controlPipe,
        string destinationResourceId,
        string destinationZoneId,
        OwnerDeclassifyPin[] pins,
        OwnerReviewDraft sanitized,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destinationResourceId);
        ArgumentNullException.ThrowIfNull(destinationZoneId);
        ArgumentNullException.ThrowIfNull(pins);
        ArgumentNullException.ThrowIfNull(sanitized);
        var draft = BuildDeclassifyDraft(pins, sanitized);
        try
        {
            var reply = await ReadReplyAsync(
                controlPipe,
                new ServiceRequest(
                    ServiceRequestHandler.CurrentProtocolVersion,
                    ServiceRequestHandler.OwnerDeclassifyPublishOperation,
                    ResourceId: destinationResourceId,
                    ZoneId: destinationZoneId,
                    ContentBase64: Convert.ToBase64String(draft)),
                "declassified",
                cancellationToken).ConfigureAwait(false);

            return new OwnerDeclassifyResult(reply.Outcome, reply.ErrorCode, ProjectDeclassify(reply));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(draft);
        }
    }

    /// <summary>
    /// Publishes one pending create proposal as a sanitized shared derivative:
    /// the proposal's cited sources become the encrypted lineage pins.
    /// </summary>
    public static async Task<OwnerDeclassifyResult> PublishDeclassifyProposalAsync(
        string controlPipe,
        OwnerProposalDetail proposal,
        string destinationResourceId,
        string destinationZoneId,
        OwnerReviewDraft sanitized,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(destinationResourceId);
        ArgumentNullException.ThrowIfNull(destinationZoneId);
        ArgumentNullException.ThrowIfNull(sanitized);
        var draft = BuildDeclassifyProposalDraft(destinationResourceId, destinationZoneId, sanitized);
        try
        {
            var reply = await ReadReplyAsync(
                controlPipe,
                new ServiceRequest(
                    ServiceRequestHandler.CurrentProtocolVersion,
                    ServiceRequestHandler.OwnerDeclassifyProposalPublishOperation,
                    ResourceId: proposal.Id,
                    ResourceRevision: proposal.Revision,
                    ContentBase64: Convert.ToBase64String(draft)),
                "declassified",
                cancellationToken).ConfigureAwait(false);

            return new OwnerDeclassifyResult(reply.Outcome, reply.ErrorCode, ProjectDeclassify(reply));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(draft);
        }
    }


    /// <summary>
    /// Reads the publication record of one memory item: the live revision of
    /// its lifecycle record and the publication state it carries. An item with
    /// no record yet has no publication decision, which the caller reports as
    /// "none" instead of inventing a state.
    /// </summary>
    public static async Task<OwnerLifecycleResult> ReadLifecycleAsync(
        string controlPipe,
        string subjectResourceId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subjectResourceId);
        var reply = await ReadReplyAsync(
            controlPipe,
            new ServiceRequest(
                ServiceRequestHandler.CurrentProtocolVersion,
                ServiceRequestHandler.LifecycleReadOperation,
                ResourceId: subjectResourceId),
            "lifecycle",
            cancellationToken).ConfigureAwait(false);

        return new OwnerLifecycleResult(reply.Outcome, reply.ErrorCode, ProjectLifecycle(reply));
    }

    /// <summary>
    /// S7-T1 decision history: the lifecycle record of one subject with its
    /// recorded states, temporal validity, history and retained archives,
    /// exactly as the service served it. Unknown, unavailable, tampered or
    /// wrongly shaped records share one generic outcome: a history read is
    /// never an existence oracle.
    /// </summary>
    public static async Task<OwnerTimelineResult> ReadTimelineAsync(
        string controlPipe,
        string subjectResourceId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subjectResourceId);
        var reply = await ReadReplyAsync(
            controlPipe,
            new ServiceRequest(
                ServiceRequestHandler.CurrentProtocolVersion,
                ServiceRequestHandler.LifecycleReadOperation,
                ResourceId: subjectResourceId),
            "lifecycle",
            cancellationToken).ConfigureAwait(false);

        return new OwnerTimelineResult(reply.Outcome, reply.ErrorCode, ProjectTimeline(reply));
    }

    /// <summary>
    /// S7-T1 retained historical revision: the exact archived bytes of one
    /// older subject revision, with both the subject revision and the
    /// archive's own vault revision. A superseded frozen read never drifts
    /// onto newer content.
    /// </summary>
    public static async Task<OwnerRetainedRevisionResult> ReadRetainedRevisionAsync(
        string controlPipe,
        string subjectResourceId,
        long subjectRevision,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subjectResourceId);
        var reply = await ReadReplyAsync(
            controlPipe,
            new ServiceRequest(
                ServiceRequestHandler.CurrentProtocolVersion,
                ServiceRequestHandler.LifecycleRetainedRevisionReadOperation,
                ResourceId: subjectResourceId,
                ResourceRevision: subjectRevision),
            "retained_revision",
            cancellationToken).ConfigureAwait(false);

        return new OwnerRetainedRevisionResult(reply.Outcome, reply.ErrorCode, ProjectRetainedRevision(reply));
    }

    /// <summary>
    /// Undoes a completed publish: the item goes from <c>published</c> to
    /// <c>withdrawn</c> as one explicit owner act with the owner's rationale,
    /// so it stops being current without being deleted, and keeps its content,
    /// its attribution and its whole publication history.
    /// </summary>
    public static Task<OwnerLifecycleResult> WithdrawReviewItemAsync(
        string controlPipe,
        OwnerLifecycleState lifecycle,
        string rationale,
        CancellationToken cancellationToken) =>
        TransitionLifecycleAsync(
            controlPipe, lifecycle, ServiceRequestHandler.LifecycleWithdrawOperation, "lifecycle_withdrawn", rationale, cancellationToken);

    /// <summary>
    /// Undoes a completed rejection: the review item goes from
    /// <c>rejected</c> back to <c>proposal</c> as one explicit owner act with
    /// the owner's rationale, so the same canonical record can be reviewed
    /// again instead of a second copy being written.
    /// </summary>
    public static Task<OwnerLifecycleResult> ReproposeReviewItemAsync(
        string controlPipe,
        OwnerLifecycleState lifecycle,
        string rationale,
        CancellationToken cancellationToken) =>
        TransitionLifecycleAsync(
            controlPipe, lifecycle, ServiceRequestHandler.LifecycleReProposeOperation, "lifecycle_reproposed", rationale, cancellationToken);

    // ---- S5-T5 explicit relations ----

    /// <summary>
    /// Lists every relation of the unlocked vault with its live revision, its
    /// provenance and the service's computed review state. Owner authority is
    /// this vault's owner, so the service filters no endpoint here: an agent
    /// session reads its own scoped projection instead of this one.
    /// </summary>
    public static async Task<OwnerRelationListResult> ListRelationsAsync(
        string controlPipe,
        CancellationToken cancellationToken)
    {
        var reply = await ReadReplyAsync(
            controlPipe,
            new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, ServiceRequestHandler.OwnerRelationListOperation),
            "relations",
            cancellationToken).ConfigureAwait(false);

        return new OwnerRelationListResult(reply.Outcome, reply.ErrorCode, ProjectRelations(reply));
    }

    /// <summary>
    /// Reads one relation at the named live revision (or the current one) with
    /// its computed review state, so an opened link is the record the vault
    /// holds and its staleness is the service's evaluation, never this client's
    /// guess.
    /// </summary>
    public static async Task<OwnerRelationResult> ReadRelationAsync(
        string controlPipe,
        string relationId,
        long? revision,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(relationId);
        var reply = await ReadReplyAsync(
            controlPipe,
            new ServiceRequest(
                ServiceRequestHandler.CurrentProtocolVersion,
                ServiceRequestHandler.RelationReadOperation,
                ResourceId: relationId,
                ResourceRevision: revision),
            "relation",
            cancellationToken).ConfigureAwait(false);

        return new OwnerRelationResult(reply.Outcome, reply.ErrorCode, ProjectRelation(reply));
    }

    /// <summary>
    /// Creates one typed owner link between two pinned endpoints. The draft is
    /// encoded by the shared relation codec, so this client cannot introduce a
    /// second draft schema; the service verifies both endpoints at their live
    /// revisions, refuses a derivation cycle with its own code and stores the
    /// link as one revision-gated canonical write.
    /// </summary>
    public static async Task<OwnerRelationResult> CreateRelationAsync(
        string controlPipe,
        string relationId,
        string zoneId,
        RelationKind kind,
        RelationState state,
        RelationEndpoint from,
        RelationEndpoint to,
        string reason,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        ArgumentNullException.ThrowIfNull(relationId);
        ArgumentNullException.ThrowIfNull(zoneId);
        ArgumentNullException.ThrowIfNull(reason);
        var draft = RelationCodec.EncodeDraft(new RelationDraft(
            kind,
            state,
            from,
            to,
            CreationMethod.DirectWrite,
            reason,
            StateReason: null,
            OccurredAtUtc: null,
            // The owner path records the link when the act is sent; the shared
            // validator requires a UTC recorded instant, and only the agent path
            // stamps one server-side.
            RecordedAtUtc: DateTimeOffset.UtcNow));
        try
        {
            var reply = await ReadReplyAsync(
                controlPipe,
                new ServiceRequest(
                    ServiceRequestHandler.CurrentProtocolVersion,
                    ServiceRequestHandler.RelationCreateOperation,
                    ResourceId: relationId,
                    ZoneId: zoneId,
                    ContentBase64: Convert.ToBase64String(draft)),
                "relation_created",
                cancellationToken).ConfigureAwait(false);

            return new OwnerRelationResult(reply.Outcome, reply.ErrorCode, ProjectRelation(reply));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(draft);
        }
    }

    /// <summary>
    /// Accepts a proposed link as the owner's own conclusion. The stored record
    /// keeps the claim that accompanied it; only the state, its executor and its
    /// time change, so confirming an agent's link never rewrites what the agent
    /// reported.
    /// </summary>
    public static Task<OwnerRelationResult> AcceptRelationAsync(
        string controlPipe,
        OwnerRelation relation,
        CancellationToken cancellationToken) =>
        TransitionRelationAsync(
            controlPipe, ServiceRequestHandler.RelationAcceptOperation, "relation_accepted", relation, rationale: null, cancellationToken);

    /// <summary>
    /// Rejects the link with the owner's explicit rationale, which the shared
    /// core records in the relation's history.
    /// </summary>
    public static Task<OwnerRelationResult> RejectRelationAsync(
        string controlPipe,
        OwnerRelation relation,
        string rationale,
        CancellationToken cancellationToken) =>
        TransitionRelationAsync(
            controlPipe, ServiceRequestHandler.RelationRejectOperation, "relation_rejected", relation, rationale, cancellationToken);

    /// <summary>Removes the link (retracted) with the owner's explicit rationale.</summary>
    public static Task<OwnerRelationResult> UnlinkRelationAsync(
        string controlPipe,
        OwnerRelation relation,
        string rationale,
        CancellationToken cancellationToken) =>
        TransitionRelationAsync(
            controlPipe, ServiceRequestHandler.RelationUnlinkOperation, "relation_unlinked", relation, rationale, cancellationToken);

    /// <summary>
    /// Undoes a removal: the link returns to proposed, never accepted, because
    /// the shared core refuses to re-assert an acceptance silently. A relink
    /// carries no rationale; the original link rationale stays the record's
    /// basis.
    /// </summary>
    public static Task<OwnerRelationResult> RelinkRelationAsync(
        string controlPipe,
        OwnerRelation relation,
        CancellationToken cancellationToken) =>
        TransitionRelationAsync(
            controlPipe, ServiceRequestHandler.RelationRelinkOperation, "relation_relinked", relation, rationale: null, cancellationToken);

    // ---- S5-T6 catalog/access-request/session console ----

    /// <summary>
    /// Shows what publication of one catalog entry would store, plus the live
    /// revision of the mapped resource. Mutates nothing and mints no access.
    /// The label and description are explicit owner text: unlike the CLI path,
    /// this console call carries no private-title/path/citation autofill, and
    /// the service derives nothing from the mapped resource either.
    /// </summary>
    public static async Task<OwnerCatalogPreviewResult> PreviewCatalogAsync(
        string controlPipe,
        string catalogId,
        string label,
        string description,
        string resourceId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(catalogId);
        ArgumentNullException.ThrowIfNull(label);
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(resourceId);
        var reply = await ReadReplyAsync(
            controlPipe,
            new ServiceRequest(
                ServiceRequestHandler.CurrentProtocolVersion,
                ServiceRequestHandler.OwnerCatalogPreviewOperation,
                ResourceId: resourceId,
                CatalogId: catalogId,
                Label: label,
                Description: description),
            "catalog_preview",
            cancellationToken).ConfigureAwait(false);

        return new OwnerCatalogPreviewResult(reply.Outcome, reply.ErrorCode, ProjectCatalogPreview(reply));
    }

    /// <summary>
    /// Publishes exactly the owner-typed label/description against the mapped
    /// resource. Only the ID/label/description triplet reaches discovery; the
    /// private mapping stays encrypted.
    /// </summary>
    public static async Task<OwnerCatalogPublishResult> PublishCatalogAsync(
        string controlPipe,
        string catalogId,
        string label,
        string description,
        string resourceId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(catalogId);
        ArgumentNullException.ThrowIfNull(label);
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(resourceId);
        var reply = await ReadReplyAsync(
            controlPipe,
            new ServiceRequest(
                ServiceRequestHandler.CurrentProtocolVersion,
                ServiceRequestHandler.OwnerCatalogPublishOperation,
                ResourceId: resourceId,
                CatalogId: catalogId,
                Label: label,
                Description: description),
            "catalog_published",
            cancellationToken).ConfigureAwait(false);

        return new OwnerCatalogPublishResult(
            reply.Outcome, reply.ErrorCode,
            reply.Outcome == OwnerCommandOutcome.Completed ? Text(reply.Payload, "catalogId") : null);
    }

    /// <summary>
    /// Lists the private owner catalog mappings on the owner channel only.
    /// Discovery stays public-triplet only.
    /// </summary>
    public static async Task<OwnerCatalogListResult> ListCatalogAsync(
        string controlPipe,
        CancellationToken cancellationToken)
    {
        var reply = await ReadReplyAsync(
            controlPipe,
            new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, ServiceRequestHandler.OwnerCatalogListOperation),
            "catalog_entries",
            cancellationToken).ConfigureAwait(false);

        return new OwnerCatalogListResult(reply.Outcome, reply.ErrorCode, ProjectCatalogEntries(reply));
    }

    /// <summary>
    /// Withdraws one catalog entry so future discovery converges to no-match.
    /// Already delivered copies cannot be recalled.
    /// </summary>
    public static async Task<OwnerCatalogWithdrawResult> WithdrawCatalogAsync(
        string controlPipe,
        string catalogId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(catalogId);
        var reply = await ReadReplyAsync(
            controlPipe,
            new ServiceRequest(
                ServiceRequestHandler.CurrentProtocolVersion,
                ServiceRequestHandler.OwnerCatalogWithdrawOperation,
                CatalogId: catalogId),
            "catalog_withdrawn",
            cancellationToken).ConfigureAwait(false);

        return new OwnerCatalogWithdrawResult(
            reply.Outcome, reply.ErrorCode,
            reply.Outcome == OwnerCommandOutcome.Completed ? Text(reply.Payload, "catalogId") ?? catalogId : null);
    }

    /// <summary>
    /// Lists the pending access requests with their purpose, catalogs and
    /// resolved snapshots. A listed request is never permission.
    /// </summary>
    public static async Task<OwnerAccessRequestListResult> ListAccessRequestsAsync(
        string controlPipe,
        CancellationToken cancellationToken)
    {
        var reply = await ReadReplyAsync(
            controlPipe,
            new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, ServiceRequestHandler.OwnerAccessRequestListOperation),
            "access_requests",
            cancellationToken).ConfigureAwait(false);

        return new OwnerAccessRequestListResult(reply.Outcome, reply.ErrorCode, ProjectAccessRequests(reply));
    }

    /// <summary>
    /// Binds one pending request to its frozen snapshot and decodes the exact
    /// resource bodies the vault holds, for the owner's eyes on this console
    /// only. Nothing is written to disk by the caller.
    /// </summary>
    public static async Task<OwnerAccessRequestPreviewResult> PreviewAccessRequestAsync(
        string controlPipe,
        string requestId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requestId);
        var reply = await ReadReplyAsync(
            controlPipe,
            new ServiceRequest(
                ServiceRequestHandler.CurrentProtocolVersion,
                ServiceRequestHandler.OwnerAccessRequestPreviewOperation,
                RequestId: requestId),
            "access_request_preview",
            cancellationToken).ConfigureAwait(false);

        return new OwnerAccessRequestPreviewResult(reply.Outcome, reply.ErrorCode, ProjectAccessRequestPreview(reply));
    }

    /// <summary>
    /// Runs one bounded ingest for an enrolled source. Completed runs
    /// (advanced/idle/partial) answer with their status; the unsupported
    /// diagnostics arrive as explicit status tokens and stay refusals with
    /// their own code here, as does every other refusal (unknown, revoked,
    /// paused, quarantined, missing, unreadable). The outcome is the
    /// service's own: an unregistered format stays an explicit unsupported
    /// refusal, never a completed save and never a silent best effort.
    /// </summary>
    public static async Task<OwnerSourceIngestResult> IngestSourceAsync(
        string controlPipe,
        string sourceId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sourceId);
        var reply = await ReadIngestReplyAsync(
            controlPipe,
            new ServiceRequest(
                ServiceRequestHandler.CurrentProtocolVersion,
                ServiceRequestHandler.SourceIngestOperation,
                ResourceId: sourceId),
            cancellationToken).ConfigureAwait(false);

        return ProjectSourceIngest(reply);
    }

    /// <summary>
    /// Approves exactly the narrowed scope the owner chose — a subset of the
    /// previewed resource IDs and of the requested operations — with an
    /// explicit UTC expiry and optional concrete destinations. Any growth past
    /// the previewed snapshot is refused by the service. The issued bearer is
    /// written only into a DPAPI-protected local handoff file created here;
    /// the returned record carries its path, never the token.
    /// </summary>
    public static async Task<OwnerAccessApprovalResult> ApproveAccessRequestAsync(
        string controlPipe,
        string requestId,
        string previewId,
        string[]? resourceIds,
        string[]? operations,
        string[]? destinationResourceIds,
        DateTimeOffset expiresAtUtc,
        string? provider,
        string? model,
        decimal? maximumCostUsd,
        string handoffPath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requestId);
        ArgumentNullException.ThrowIfNull(previewId);
        ArgumentNullException.ThrowIfNull(handoffPath);
        var reply = await ReadReplyAsync(
            controlPipe,
            new ServiceRequest(
                ServiceRequestHandler.CurrentProtocolVersion,
                ServiceRequestHandler.OwnerAccessRequestApproveOperation,
                ResourceIds: resourceIds,
                Operations: operations,
                DestinationResourceIds: destinationResourceIds,
                ExpiresAtUtc: expiresAtUtc,
                Provider: provider,
                Model: model,
                MaximumCostUsd: maximumCostUsd,
                PreviewId: previewId,
                RequestId: requestId),
            "access_request_approved",
            cancellationToken).ConfigureAwait(false);

        if (reply.Outcome != OwnerCommandOutcome.Completed ||
            !reply.Payload.TryGetProperty("grantId", out var grant) ||
            grant.ValueKind != JsonValueKind.String ||
            !reply.Payload.TryGetProperty("token", out var token) ||
            token.ValueKind != JsonValueKind.String)
        {
            return new OwnerAccessApprovalResult(reply.Outcome, reply.ErrorCode, null, null);
        }

        var tokenText = token.GetString() ?? string.Empty;
        OwnerTokenHandoff handoff;
        try
        {
            handoff = OwnerTokenHandoff.Create(handoffPath);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidDataException)
        {
            return new OwnerAccessApprovalResult(OwnerCommandOutcome.Refused, "handoff_destination_unavailable", null, null);
        }

        using (handoff)
        {
            try
            {
                handoff.WriteToken(tokenText);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.ComponentModel.Win32Exception)
            {
                return new OwnerAccessApprovalResult(OwnerCommandOutcome.Refused, "token_handoff_failed", null, null);
            }

            return new OwnerAccessApprovalResult(OwnerCommandOutcome.Completed, null, grant.GetString(), handoff.Path);
        }
    }

    /// <summary>
    /// Rejects one pending request with the owner's explicit decision. No
    /// grant is created by this path.
    /// </summary>
    public static async Task<OwnerAccessRejectResult> RejectAccessRequestAsync(
        string controlPipe,
        string requestId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requestId);
        var reply = await ReadReplyAsync(
            controlPipe,
            new ServiceRequest(
                ServiceRequestHandler.CurrentProtocolVersion,
                ServiceRequestHandler.OwnerAccessRequestRejectOperation,
                RequestId: requestId),
            "access_request_rejected",
            cancellationToken).ConfigureAwait(false);

        return new OwnerAccessRejectResult(reply.Outcome, reply.ErrorCode,
            reply.Outcome == OwnerCommandOutcome.Completed ? requestId : null);
    }

    /// <summary>
    /// Lists the live agent sessions with their effective frozen scopes,
    /// expiries and policy generations. Carries no verifier or bearer.
    /// </summary>
    public static async Task<OwnerSessionListResult> ListSessionsAsync(
        string controlPipe,
        CancellationToken cancellationToken)
    {
        var reply = await ReadReplyAsync(
            controlPipe,
            new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, ServiceRequestHandler.OwnerSessionListOperation),
            "sessions_listed",
            cancellationToken).ConfigureAwait(false);

        long generation = 0;
        if (reply.Payload.ValueKind == JsonValueKind.Object &&
            reply.Payload.TryGetProperty("policyGeneration", out var generationValue) &&
            generationValue.ValueKind == JsonValueKind.Number)
        {
            generation = generationValue.GetInt64();
        }

        return new OwnerSessionListResult(reply.Outcome, reply.ErrorCode, generation, ProjectSessions(reply));
    }

    /// <summary>
    /// Revokes one live session by its ID. The bearer, if already redeemed,
    /// authorizes nothing afterwards.
    /// </summary>
    public static async Task<OwnerSessionRevokeResult> RevokeSessionAsync(
        string controlPipe,
        string sessionId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        var reply = await ReadReplyAsync(
            controlPipe,
            new ServiceRequest(
                ServiceRequestHandler.CurrentProtocolVersion,
                ServiceRequestHandler.OwnerSessionRevokeOperation,
                SessionId: sessionId),
            "session_revoked",
            cancellationToken).ConfigureAwait(false);

        return new OwnerSessionRevokeResult(reply.Outcome, reply.ErrorCode,
            reply.Outcome == OwnerCommandOutcome.Completed ? sessionId : null);
    }
    // ---- S5-T7 sources and organization ----

    /// <summary>
    /// Lists every enrolled source with its life, checkpoint, refresh consent
    /// and actual encrypted cache scope. Held mapping-unknown submissions are
    /// reported beside the allowlist with their quarantine record, no mapping
    /// and no checkpoint. Listing never opens a transcript and never echoes a
    /// locator: the owner supplied every locator, and the wire carries none.
    /// </summary>
    public static async Task<OwnerSourceListResult> ListSourcesAsync(
        string controlPipe,
        CancellationToken cancellationToken)
    {
        var reply = await ReadReplyAsync(
            controlPipe,
            new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, ServiceRequestHandler.SourceListOperation),
            "sources_listed",
            cancellationToken).ConfigureAwait(false);

        return new OwnerSourceListResult(reply.Outcome, reply.ErrorCode, ProjectSourceEnrollments(reply));
    }

    /// <summary>
    /// Reads one source's status: its life, checkpoint, quarantine, exact
    /// availability observation and encrypted cache scope. A held
    /// mapping-unknown submission reports its quarantine record with no probe.
    /// </summary>
    public static async Task<OwnerSourceStatusResult> ReadSourceStatusAsync(
        string controlPipe,
        string sourceId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sourceId);
        var reply = await ReadReplyAsync(
            controlPipe,
            new ServiceRequest(
                ServiceRequestHandler.CurrentProtocolVersion,
                ServiceRequestHandler.SourceStatusOperation,
                ResourceId: sourceId),
            "source_status",
            cancellationToken).ConfigureAwait(false);

        return new OwnerSourceStatusResult(reply.Outcome, reply.ErrorCode, ProjectSourceEnrollment(reply));
    }


    /// <summary>
    /// Submits one source whose zone/format mapping the owner cannot yet
    /// resolve into the private quarantine. The locator travels only in this
    /// request; no transcript is opened and no zone is defaulted. The held
    /// reply names the unresolved field without echoing the locator.
    /// </summary>
    public static async Task<OwnerSourceStatusResult> SubmitMappingUnknownAsync(
        string controlPipe,
        string sourceId,
        string locator,
        string? zoneId,
        string? format,
        int? formatVersion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sourceId);
        ArgumentNullException.ThrowIfNull(locator);
        var reply = await ReadReplyAsync(
            controlPipe,
            new ServiceRequest(
                ServiceRequestHandler.CurrentProtocolVersion,
                ServiceRequestHandler.SourceMappingSubmitOperation,
                ResourceId: sourceId,
                ZoneId: zoneId,
                SourceLocator: locator,
                SourceFormat: format,
                SourceFormatVersion: formatVersion),
            "source_mapping_held",
            cancellationToken).ConfigureAwait(false);

        return new OwnerSourceStatusResult(reply.Outcome, reply.ErrorCode, ProjectSourceEnrollment(reply));
    }

    /// <summary>
    /// Withdraws one held mapping-unknown submission without enrolling it.
    /// The held locator is never opened.
    /// </summary>
    public static async Task<OwnerSourceIngestResult> WithdrawMappingUnknownAsync(
        string controlPipe,
        string sourceId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sourceId);
        var reply = await ReadReplyAsync(
            controlPipe,
            new ServiceRequest(
                ServiceRequestHandler.CurrentProtocolVersion,
                ServiceRequestHandler.SourceMappingResolveOperation,
                ResourceId: sourceId),
            "source_mapping_withdrawn",
            cancellationToken).ConfigureAwait(false);

        return new OwnerSourceIngestResult(reply.Outcome, reply.ErrorCode, null);
    }

    /// <summary>
    /// Pauses one enrolled source: stops status reads, ingest and refresh for
    /// that source with no transcript open and no checkpoint movement.
    /// </summary>
    public static async Task<OwnerSourceStatusResult> PauseSourceAsync(
        string controlPipe,
        string sourceId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sourceId);
        var reply = await ReadReplyAsync(
            controlPipe,
            new ServiceRequest(
                ServiceRequestHandler.CurrentProtocolVersion,
                ServiceRequestHandler.SourcePauseOperation,
                ResourceId: sourceId),
            "source_paused",
            cancellationToken).ConfigureAwait(false);

        return new OwnerSourceStatusResult(reply.Outcome, reply.ErrorCode, ProjectSourceEnrollment(reply));
    }

    /// <summary>
    /// Resumes one paused source from its persisted checkpoint: only new
    /// records are consumed, never duplicates.
    /// </summary>
    public static async Task<OwnerSourceStatusResult> ResumeSourceAsync(
        string controlPipe,
        string sourceId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sourceId);
        var reply = await ReadReplyAsync(
            controlPipe,
            new ServiceRequest(
                ServiceRequestHandler.CurrentProtocolVersion,
                ServiceRequestHandler.SourceResumeOperation,
                ResourceId: sourceId),
            "source_resumed",
            cancellationToken).ConfigureAwait(false);

        return new OwnerSourceStatusResult(reply.Outcome, reply.ErrorCode, ProjectSourceEnrollment(reply));
    }

    /// <summary>
    /// Records the owner's explicit per-source background-refresh consent.
    /// Refresh runs only for opted-in sources while unlocked.
    /// </summary>
    public static async Task<OwnerSourceStatusResult> SetSourceRefreshOptInAsync(
        string controlPipe,
        string sourceId,
        bool optIn,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sourceId);
        var reply = await ReadReplyAsync(
            controlPipe,
            new ServiceRequest(
                ServiceRequestHandler.CurrentProtocolVersion,
                optIn ? ServiceRequestHandler.SourceRefreshOptInOperation : ServiceRequestHandler.SourceRefreshOptOutOperation,
                ResourceId: sourceId),
            optIn ? "source_refresh_opted_in" : "source_refresh_opted_out",
            cancellationToken).ConfigureAwait(false);

        return new OwnerSourceStatusResult(reply.Outcome, reply.ErrorCode, ProjectSourceEnrollment(reply));
    }

    /// <summary>
    /// Runs one explicit idempotent refresh pass over the owner allowlist.
    /// Only opted-in active sources are ingested; every other source names
    /// its skip reason with no read.
    /// </summary>
    public static async Task<OwnerSourceRefreshResult> RefreshSourcesAsync(
        string controlPipe,
        CancellationToken cancellationToken)
    {
        var reply = await ReadReplyAsync(
            controlPipe,
            new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, ServiceRequestHandler.SourceRefreshOperation),
            "sources_refreshed",
            cancellationToken).ConfigureAwait(false);

        return new OwnerSourceRefreshResult(reply.Outcome, reply.ErrorCode, ProjectSourceRefresh(reply));
    }

    /// <summary>
    /// Lists the owner collections: the collection IDs the owner named and
    /// the member resource IDs each currently holds. Membership is selection
    /// metadata only and never widens a grant.
    /// </summary>
    public static async Task<OwnerCollectionListResult> ListCollectionsAsync(
        string controlPipe,
        CancellationToken cancellationToken)
    {
        var reply = await ReadReplyAsync(
            controlPipe,
            new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, ServiceRequestHandler.OwnerScopeCollectionListOperation),
            "scope_collections",
            cancellationToken).ConfigureAwait(false);

        return new OwnerCollectionListResult(reply.Outcome, reply.ErrorCode, ProjectCollections(reply));
    }

    /// <summary>
    /// Saves one owner collection membership: the exact member resource IDs
    /// the owner named against the collection ID the owner named. The service
    /// resolves every member against the live managed resources, so an unknown
    /// member is refused instead of stored; adding a member never widens a
    /// frozen grant or session.
    /// </summary>
    public static async Task<OwnerCollectionSetResult> SetCollectionAsync(
        string controlPipe,
        string collectionId,
        string[] resourceIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(collectionId);
        ArgumentNullException.ThrowIfNull(resourceIds);
        var reply = await ReadReplyAsync(
            controlPipe,
            new ServiceRequest(
                ServiceRequestHandler.CurrentProtocolVersion,
                ServiceRequestHandler.OwnerScopeCollectionSetOperation,
                ResourceIds: resourceIds,
                CollectionId: collectionId),
            "collection_set",
            cancellationToken).ConfigureAwait(false);

        return new OwnerCollectionSetResult(reply.Outcome, reply.ErrorCode, ProjectCollection(reply));
    }

    /// <summary>
    /// Lists the explicitly preserved snapshots of one enrolled source: one
    /// descriptor per generation with its live revision.
    /// </summary>
    public static async Task<OwnerSourceSnapshotListResult> ListSourceSnapshotsAsync(
        string controlPipe,
        string sourceId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sourceId);
        var reply = await ReadReplyAsync(
            controlPipe,
            new ServiceRequest(
                ServiceRequestHandler.CurrentProtocolVersion,
                ServiceRequestHandler.SourceSnapshotListOperation,
                ResourceId: sourceId),
            "source_snapshots",
            cancellationToken).ConfigureAwait(false);

        return new OwnerSourceSnapshotListResult(reply.Outcome, reply.ErrorCode, ProjectSnapshotDescriptors(reply));
    }

    /// <summary>
    /// Explicitly preserves one enrolled source generation: the exact bytes
    /// of the enrolled generation are frozen with the source reference
    /// revision, the generation anchor and the owner-selected future UTC
    /// retention. Nothing is migrated automatically.
    /// </summary>
    public static async Task<OwnerSourceSnapshotResult> PreserveSourceSnapshotAsync(
        string controlPipe,
        string sourceId,
        long sourceRevision,
        DateTimeOffset retainUntilUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sourceId);
        var reply = await ReadReplyAsync(
            controlPipe,
            new ServiceRequest(
                ServiceRequestHandler.CurrentProtocolVersion,
                ServiceRequestHandler.SourceSnapshotPreserveOperation,
                ResourceId: sourceId,
                ResourceRevision: sourceRevision,
                SnapshotRetainUntilUtc: retainUntilUtc),
            "source_snapshot_preserved",
            cancellationToken).ConfigureAwait(false);

        return new OwnerSourceSnapshotResult(reply.Outcome, reply.ErrorCode, ProjectSnapshot(reply));
    }

    /// <summary>
    /// Opens one explicitly preserved snapshot by its own snapshot ID and
    /// live revision: the service rechecks the live revision and serves the
    /// frozen metadata. Snapshot content itself never crosses this record;
    /// this is the inspect path the snapshot list rows open through.
    /// </summary>
    public static async Task<OwnerSourceSnapshotResult> ReadSourceSnapshotAsync(
        string controlPipe,
        string snapshotId,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshotId);
        var reply = await ReadReplyAsync(
            controlPipe,
            new ServiceRequest(
                ServiceRequestHandler.CurrentProtocolVersion,
                ServiceRequestHandler.SourceSnapshotReadOperation,
                ResourceId: snapshotId,
                ResourceRevision: expectedRevision),
            "source_snapshot",
            cancellationToken).ConfigureAwait(false);

        return new OwnerSourceSnapshotResult(reply.Outcome, reply.ErrorCode, ProjectSnapshot(reply));
    }

    /// <summary>
    /// Extends one preserved snapshot's retention to a new explicit future
    /// UTC instant.
    /// </summary>
    public static async Task<OwnerSourceSnapshotResult> RetainSourceSnapshotAsync(
        string controlPipe,
        string snapshotId,
        long expectedRevision,
        DateTimeOffset retainUntilUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshotId);
        var reply = await ReadReplyAsync(
            controlPipe,
            new ServiceRequest(
                ServiceRequestHandler.CurrentProtocolVersion,
                ServiceRequestHandler.SourceSnapshotRetainOperation,
                ResourceId: snapshotId,
                ResourceRevision: expectedRevision,
                SnapshotRetainUntilUtc: retainUntilUtc),
            "source_snapshot_retained",
            cancellationToken).ConfigureAwait(false);

        return new OwnerSourceSnapshotResult(reply.Outcome, reply.ErrorCode, ProjectSnapshot(reply));
    }


    /// <summary>
    /// Reports the model/egress settings the service would use for a job and the
    /// frozen pre-send caps it enforces. The API key is never part of the answer,
    /// and a missing configuration is reported as unconfigured.
    /// </summary>
    public static async Task<OwnerProviderResult> ReadProviderSettingsAsync(string controlPipe, CancellationToken cancellationToken)
    {
        var reply = await ReadReplyAsync(
            controlPipe,
            new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, ServiceRequestHandler.OwnerProviderStatusOperation),
            "provider_status",
            cancellationToken).ConfigureAwait(false);
        return new OwnerProviderResult(reply.Outcome, reply.ErrorCode, ProjectProviderSettings(reply));
    }

    /// <summary>
    /// Stores the owner's provider, model, endpoint and the credential the owner
    /// typed. The credential is one request body: the service stores it encrypted
    /// and neither echoes it nor includes it in any later status.
    /// </summary>
    public static async Task<OwnerProviderResult> SaveProviderSettingsAsync(
        string controlPipe,
        string provider,
        string model,
        string baseUrl,
        string apiKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(apiKey);
        var reply = await ReadReplyAsync(
            controlPipe,
            new ServiceRequest(
                ServiceRequestHandler.CurrentProtocolVersion,
                ServiceRequestHandler.OwnerProviderConfigOperation,
                Provider: provider,
                Model: model,
                BaseUrl: baseUrl,
                ApiKey: apiKey),
            "provider_configured",
            cancellationToken).ConfigureAwait(false);
        return new OwnerProviderResult(reply.Outcome, reply.ErrorCode, null);
    }

    /// <summary>Lists every model job the vault holds, newest first.</summary>
    public static async Task<OwnerJobListResult> ListJobsAsync(string controlPipe, CancellationToken cancellationToken)
    {
        var reply = await ReadReplyAsync(
            controlPipe,
            new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, ServiceRequestHandler.OwnerJobListOperation),
            "job_list",
            cancellationToken).ConfigureAwait(false);
        return new OwnerJobListResult(reply.Outcome, reply.ErrorCode, ProjectJobs(reply));
    }

    /// <summary>Reads one job at its live revision: state, recorded scope, usage and failure.</summary>
    public static async Task<OwnerJobResult> ReadJobAsync(string controlPipe, string jobId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(jobId);
        var reply = await ReadReplyAsync(
            controlPipe,
            new ServiceRequest(
                ServiceRequestHandler.CurrentProtocolVersion,
                ServiceRequestHandler.OwnerJobStatusOperation,
                JobId: jobId),
            "job_status",
            cancellationToken).ConfigureAwait(false);
        return new OwnerJobResult(reply.Outcome, reply.ErrorCode, ProjectJob(reply));
    }

    /// <summary>
    /// Creates one bounded job from the owner's explicit selection: each selected
    /// input pinned to one revision, the objective and requested level for a
    /// synthesis job, and the destination inbox. Nothing is discovered, expanded
    /// or sent by this call.
    /// </summary>
    public static async Task<OwnerJobResult> CreateJobAsync(
        string controlPipe,
        OwnerJobInput[] inputs,
        string zoneId,
        string destinationResourceId,
        long destinationRevision,
        string provider,
        string model,
        string? profile,
        string? objective,
        string? targetLevel,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(zoneId);
        ArgumentNullException.ThrowIfNull(destinationResourceId);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(model);
        var draft = BuildSelectionDraft(inputs, objective, targetLevel);
        try
        {
            var reply = await ReadReplyAsync(
                controlPipe,
                new ServiceRequest(
                    ServiceRequestHandler.CurrentProtocolVersion,
                    ServiceRequestHandler.OwnerJobCreateOperation,
                    ZoneId: zoneId,
                    DestinationResourceId: destinationResourceId,
                    DestinationRevision: destinationRevision,
                    Provider: provider,
                    Model: model,
                    Profile: profile,
                    ContentBase64: Convert.ToBase64String(draft)),
                "job_created",
                cancellationToken).ConfigureAwait(false);
            return new OwnerJobResult(reply.Outcome, reply.ErrorCode, ProjectJob(reply));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(draft);
        }
    }

    /// <summary>
    /// Starts one created job through the shared lifecycle. The answer state is
    /// the job's real state — succeeded, failed or cancelled — and never a
    /// summary this client invents.
    /// </summary>
    public static async Task<OwnerJobResult> StartJobAsync(string controlPipe, string jobId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(jobId);
        var reply = await ReadReplyAsync(
            controlPipe,
            new ServiceRequest(
                ServiceRequestHandler.CurrentProtocolVersion,
                ServiceRequestHandler.OwnerJobStartOperation,
                JobId: jobId),
            new[] { "job_completed", "job_failed", "job_cancelled" },
            cancellationToken).ConfigureAwait(false);
        return new OwnerJobResult(reply.Outcome, reply.ErrorCode, ProjectJob(reply));
    }

    /// <summary>
    /// Cancels one job the owner selected: the shared lifecycle stops the next
    /// send or commit, keeps billable spend recorded, and reports the state it
    /// reached. A job that already finished is returned unchanged.
    /// </summary>
    public static async Task<OwnerJobResult> CancelJobAsync(string controlPipe, string jobId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(jobId);
        var reply = await ReadReplyAsync(
            controlPipe,
            new ServiceRequest(
                ServiceRequestHandler.CurrentProtocolVersion,
                ServiceRequestHandler.OwnerJobCancelOperation,
                JobId: jobId),
            "job_cancelled",
            cancellationToken).ConfigureAwait(false);
        return new OwnerJobResult(reply.Outcome, reply.ErrorCode, ProjectJob(reply));
    }

    /// <summary>Reads the encrypted spend ledger: reservations, settlements and the rolling spend.</summary>
    public static async Task<OwnerSpendLedgerResult> ReadSpendLedgerAsync(string controlPipe, CancellationToken cancellationToken)
    {
        var reply = await ReadReplyAsync(
            controlPipe,
            new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, ServiceRequestHandler.OwnerJobLedgerOperation),
            "job_ledger",
            cancellationToken).ConfigureAwait(false);
        return new OwnerSpendLedgerResult(reply.Outcome, reply.ErrorCode, ProjectSpendLedger(reply));
    }

    /// <summary>
    /// Enables the owner's synthesis schedule as one encrypted record: the
    /// explicit selection, the interval, the destination and the provider/model
    /// the scheduled runs may use. The service's own policy bounds and the same
    /// job contract validate it; nothing is scheduled by this call.
    /// </summary>
    public static async Task<OwnerScheduleResult> SaveScheduleAsync(
        string controlPipe,
        int intervalMinutes,
        OwnerJobInput[] inputs,
        string objective,
        string targetLevel,
        string zoneId,
        string destinationResourceId,
        long destinationRevision,
        string provider,
        string model,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(objective);
        ArgumentNullException.ThrowIfNull(targetLevel);
        ArgumentNullException.ThrowIfNull(zoneId);
        ArgumentNullException.ThrowIfNull(destinationResourceId);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(model);
        var draft = BuildSelectionDraft(inputs, objective, targetLevel);
        try
        {
            var reply = await ReadReplyAsync(
                controlPipe,
                new ServiceRequest(
                    ServiceRequestHandler.CurrentProtocolVersion,
                    ServiceRequestHandler.OwnerScheduleSetOperation,
                    ZoneId: zoneId,
                    DestinationResourceId: destinationResourceId,
                    DestinationRevision: destinationRevision,
                    Provider: provider,
                    Model: model,
                    IntervalMinutes: intervalMinutes,
                    ContentBase64: Convert.ToBase64String(draft)),
                "schedule_enabled",
                cancellationToken).ConfigureAwait(false);
            return new OwnerScheduleResult(reply.Outcome, reply.ErrorCode, ProjectSchedule(reply));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(draft);
        }
    }

    /// <summary>
    /// Disables the owner's stored schedule, keeping its configuration: a
    /// disabled schedule is never due, so no scheduled run happens until the
    /// owner enables it again.
    /// </summary>
    public static async Task<OwnerScheduleResult> DisableScheduleAsync(string controlPipe, CancellationToken cancellationToken)
    {
        var reply = await ReadReplyAsync(
            controlPipe,
            new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, ServiceRequestHandler.OwnerScheduleDisableOperation),
            "schedule_disabled",
            cancellationToken).ConfigureAwait(false);
        return new OwnerScheduleResult(reply.Outcome, reply.ErrorCode, ProjectSchedule(reply));
    }

    /// <summary>
    /// Reads the owner's stored schedule. A completed answer with no schedule
    /// means the service holds no schedule at all, which is reported as such
    /// instead of as a disabled one.
    /// </summary>
    public static async Task<OwnerScheduleResult> ReadScheduleAsync(string controlPipe, CancellationToken cancellationToken)
    {
        var reply = await ReadReplyAsync(
            controlPipe,
            new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, ServiceRequestHandler.OwnerScheduleStatusOperation),
            new[] { "schedule_status", "schedule_absent" },
            cancellationToken).ConfigureAwait(false);
        return new OwnerScheduleResult(reply.Outcome, reply.ErrorCode, ProjectSchedule(reply));
    }

    public static async Task<OwnerIntentionScheduleListResult> ListScheduledIntentionsAsync(
        string controlPipe,
        CancellationToken cancellationToken)
    {
        var reply = await ReadReplyAsync(
            controlPipe,
            new ServiceRequest(
                ServiceRequestHandler.CurrentProtocolVersion,
                ServiceRequestHandler.OwnerIntentionScheduleListOperation),
            "intentions_listed",
            cancellationToken).ConfigureAwait(false);
        return new OwnerIntentionScheduleListResult(reply.Outcome, reply.ErrorCode, ProjectScheduledIntentions(reply));
    }

    /// <summary>
    /// Creates, edits, or clears one explicit owner time trigger. The due text is
    /// passed unchanged so the service can reject any timestamp without an explicit
    /// UTC offset; the expected lifecycle revision prevents a stale list row from
    /// overwriting a newer owner or scheduler act.
    /// </summary>
    public static async Task<OwnerLifecycleResult> SetIntentionTimeTriggerAsync(
        string controlPipe,
        string resourceId,
        string? dueAt,
        bool cancel,
        long? expectedLifecycleRevision,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resourceId);
        if (cancel == (dueAt is not null) || expectedLifecycleRevision is < 1)
        {
            throw new ArgumentException("Set a due timestamp or explicitly cancel it; a supplied lifecycle revision must be positive.");
        }

        var draft = JsonSerializer.SerializeToUtf8Bytes(
            new IntentionTimeTriggerDraft(dueAt, cancel, expectedLifecycleRevision));
        try
        {
            var reply = await ReadReplyAsync(
                controlPipe,
                new ServiceRequest(
                    ServiceRequestHandler.CurrentProtocolVersion,
                    ServiceRequestHandler.OwnerIntentionTriggerSetOperation,
                    ResourceId: resourceId,
                    ContentBase64: Convert.ToBase64String(draft)),
                cancel ? "intention_trigger_cleared" : "intention_trigger_set",
                cancellationToken).ConfigureAwait(false);
            return new OwnerLifecycleResult(reply.Outcome, reply.ErrorCode, ProjectLifecycle(reply));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(draft);
        }
    }
    /// <summary>
    /// Enables an explicit project/task context trigger or disables the
    /// currently configured trigger without changing the intention itself.
    /// The expected lifecycle revision protects an owner edit against a stale row.
    /// </summary>
    public static async Task<OwnerLifecycleResult> SetIntentionContextTriggerAsync(
        string controlPipe,
        string resourceId,
        string? project,
        string? task,
        bool disable,
        long? expectedLifecycleRevision,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resourceId);
        if (string.IsNullOrEmpty(resourceId) ||
            disable == (project is not null || task is not null) ||
            expectedLifecycleRevision is < 1)
        {
            throw new ArgumentException("Set a project/task context or explicitly disable it; a supplied lifecycle revision must be positive.");
        }

        var draft = JsonSerializer.SerializeToUtf8Bytes(
            new IntentionContextTriggerDraft(project, task, disable, expectedLifecycleRevision));
        try
        {
            var reply = await ReadReplyAsync(
                controlPipe,
                new ServiceRequest(
                    ServiceRequestHandler.CurrentProtocolVersion,
                    ServiceRequestHandler.OwnerIntentionContextTriggerSetOperation,
                    ResourceId: resourceId,
                    ContentBase64: Convert.ToBase64String(draft)),
                disable ? "intention_context_trigger_disabled" : "intention_context_trigger_enabled",
                cancellationToken).ConfigureAwait(false);
            return new OwnerLifecycleResult(reply.Outcome, reply.ErrorCode, ProjectLifecycle(reply));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(draft);
        }
    }

    public static async Task<OwnerIntentionSchedulePassResult> RunIntentionSchedulePassAsync(
        string controlPipe,
        CancellationToken cancellationToken)
    {
        var reply = await ReadReplyAsync(
            controlPipe,
            new ServiceRequest(
                ServiceRequestHandler.CurrentProtocolVersion,
                ServiceRequestHandler.OwnerIntentionScheduleRunOperation),
            "intention_schedule_pass",
            cancellationToken).ConfigureAwait(false);
        int? notifications = null;
        if (reply.Outcome == OwnerCommandOutcome.Completed &&
            reply.Payload.ValueKind == JsonValueKind.Object &&
            reply.Payload.TryGetProperty("intentionSchedulePass", out var pass) &&
            pass.ValueKind == JsonValueKind.Object)
        {
            notifications = Int32(pass, "notificationsRecorded");
        }
        return new OwnerIntentionSchedulePassResult(reply.Outcome, reply.ErrorCode, notifications);
    }

    public static async Task<OwnerLifecycleResult> AcknowledgeIntentionAsync(
        string controlPipe,
        OwnerScheduledIntention intention,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(intention);
        var reply = await ReadReplyAsync(
            controlPipe,
            new ServiceRequest(
                ServiceRequestHandler.CurrentProtocolVersion,
                ServiceRequestHandler.LifecycleAcknowledgeOperation,
                ResourceId: intention.ResourceId,
                ResourceRevision: intention.LifecycleRevision),
            "lifecycle_intention_acknowledged",
            cancellationToken).ConfigureAwait(false);
        return new OwnerLifecycleResult(reply.Outcome, reply.ErrorCode, ProjectLifecycle(reply));
    }

    public static Task<OwnerLifecycleResult> CompleteIntentionAsync(
        string controlPipe,
        OwnerScheduledIntention intention,
        string rationale,
        CancellationToken cancellationToken) =>
        TransitionLifecycleAsync(
            controlPipe,
            new OwnerLifecycleState(intention.ResourceId, intention.LifecycleRevision, "proposal"),
            ServiceRequestHandler.LifecycleCompleteIntentionOperation,
            "lifecycle_intention_completed",
            rationale,
            cancellationToken);

    public static Task<OwnerLifecycleResult> CancelIntentionAsync(
        string controlPipe,
        OwnerScheduledIntention intention,
        string rationale,
        CancellationToken cancellationToken) =>
        TransitionLifecycleAsync(
            controlPipe,
            new OwnerLifecycleState(intention.ResourceId, intention.LifecycleRevision, "proposal"),
            ServiceRequestHandler.LifecycleCancelIntentionOperation,
            "lifecycle_intention_cancelled",
            rationale,
            cancellationToken);

    private static async Task<OwnerRelationResult> TransitionRelationAsync(
        string controlPipe,
        string operation,
        string expectedStatus,
        OwnerRelation relation,
        string? rationale,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(relation);
        byte[]? draft = null;
        try
        {
            if (rationale is not null)
            {
                draft = RelationCodec.EncodeTransitionDraft(new RelationTransitionDraft(rationale));
            }

            var reply = await ReadReplyAsync(
                controlPipe,
                new ServiceRequest(
                    ServiceRequestHandler.CurrentProtocolVersion,
                    operation,
                    ResourceId: relation.Id,
                    ResourceRevision: relation.Revision,
                    ContentBase64: draft is null ? null : Convert.ToBase64String(draft)),
                expectedStatus,
                cancellationToken).ConfigureAwait(false);

            return new OwnerRelationResult(reply.Outcome, reply.ErrorCode, ProjectRelation(reply));
        }
        finally
        {
            if (draft is { Length: > 0 })
            {
                CryptographicOperations.ZeroMemory(draft);
            }
        }
    }
    private static async Task<OwnerLifecycleResult> TransitionLifecycleAsync(
        string controlPipe,
        OwnerLifecycleState lifecycle,
        string operation,
        string expectedStatus,
        string rationale,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lifecycle);
        ArgumentNullException.ThrowIfNull(rationale);
        var draft = EncodeTransition(rationale, null);
        try
        {
            var reply = await ReadReplyAsync(
                controlPipe,
                new ServiceRequest(
                    ServiceRequestHandler.CurrentProtocolVersion,
                    operation,
                    ResourceId: lifecycle.SubjectResourceId,
                    ContentBase64: Convert.ToBase64String(draft),
                    ResourceRevision: lifecycle.Revision),
                expectedStatus,
                cancellationToken).ConfigureAwait(false);

            return new OwnerLifecycleResult(reply.Outcome, reply.ErrorCode, ProjectLifecycle(reply));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(draft);
        }
    }

    /// <summary>
    /// The reviewed item draft exactly as the memory write service reads it.
    /// The recorded instant is the moment the owner's act is sent: this record
    /// is recorded now, whatever the proposed content declares about when the
    /// statement happened.
    /// </summary>
    private static byte[] BuildReviewDraft(
        string kind,
        string author,
        string creationMethod,
        string evidence,
        string level,
        string? title,
        string body,
        OwnerItemSource[] sources,
        DateTimeOffset? occurredAtUtc,
        string? project,
        string? environment,
        string? version) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            kind,
            author,
            creationMethod,
            evidence,
            level,
            title,
            body,
            sources = sources.Select(source => new
            {
                sourceId = source.SourceId,
                locator = source.Locator,
                availability = source.Availability,
                sourceRevision = source.SourceRevision,
                span = source.SpanStart is { } start && source.SpanEnd is { } end
                    ? new { start, end }
                    : null
            }).ToArray(),
            occurredAtUtc = occurredAtUtc?.ToUniversalTime(),
            recordedAtUtc = DateTimeOffset.UtcNow,
            applicability = project is null && environment is null && version is null
                ? null
                : new { project, environment, version }
        });

    private static byte[] BuildDeclassifyDraft(OwnerDeclassifyPin[] pins, OwnerReviewDraft sanitized) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            pins = pins.Select(pin => new { resourceId = pin.ResourceId, revision = pin.Revision }).ToArray(),
            item = new
            {
                kind = sanitized.Kind,
                author = sanitized.Author,
                creationMethod = sanitized.CreationMethod,
                evidence = sanitized.Evidence,
                level = sanitized.Level,
                title = sanitized.Title,
                body = sanitized.Body,
                sources = Array.Empty<object>(),
                occurredAtUtc = sanitized.OccurredAtUtc?.ToUniversalTime(),
                recordedAtUtc = DateTimeOffset.UtcNow,
                applicability = sanitized.Project is null && sanitized.Environment is null && sanitized.Version is null
                    ? null
                    : new { project = sanitized.Project, environment = sanitized.Environment, version = sanitized.Version }
            }
        });

    private static byte[] BuildDeclassifyProposalDraft(string destinationResourceId, string destinationZoneId, OwnerReviewDraft sanitized) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            destinationResourceId,
            destinationZoneId,
            item = new
            {
                kind = sanitized.Kind,
                author = sanitized.Author,
                creationMethod = sanitized.CreationMethod,
                evidence = sanitized.Evidence,
                level = sanitized.Level,
                title = sanitized.Title,
                body = sanitized.Body,
                sources = Array.Empty<object>(),
                occurredAtUtc = sanitized.OccurredAtUtc?.ToUniversalTime(),
                recordedAtUtc = DateTimeOffset.UtcNow,
                applicability = sanitized.Project is null && sanitized.Environment is null && sanitized.Version is null
                    ? null
                    : new { project = sanitized.Project, environment = sanitized.Environment, version = sanitized.Version }
            }
        });


    /// <summary>One lifecycle transition draft: the owner's words, nothing else.</summary>
    private static byte[] EncodeTransition(string rationale, string? replacementResourceId) =>
        JsonSerializer.SerializeToUtf8Bytes(new { rationale, replacementResourceId });

    /// <summary>
    /// The verbatim text the service served, or null when the answer carried
    /// none or could not be decoded as UTF-8; a served note is never
    /// reconstructed, replaced or guessed.
    /// </summary>
    private static string? DecodeServedText(OwnerReply reply)
    {
        if (reply.Outcome != OwnerCommandOutcome.Completed || reply.Payload.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var encoded = Text(reply.Payload, "contentBase64");
        if (string.IsNullOrEmpty(encoded))
        {
            return null;
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(encoded);
        }
        catch (FormatException)
        {
            return null;
        }

        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (ArgumentException)
        {
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static async Task<OwnerVaultCommandResult> ExecuteVaultCommandAsync(
        string controlPipe,
        ServiceRequest request,
        string expectedStatus,
        CancellationToken cancellationToken)
    {
        var reply = await ReadReplyAsync(controlPipe, request, expectedStatus, cancellationToken).ConfigureAwait(false);
        return new OwnerVaultCommandResult(reply.Outcome, reply.VaultState, reply.ErrorCode, reply.RecoveryCode);
    }

    /// <summary>
    /// One owner-channel answer, classified once for every owner command: the
    /// outcome, the service's own error code when it refused, the vault state
    /// and one-time recovery material a vault command may carry, and — only
    /// for a completed command — the reply payload the caller projects.
    /// </summary>
    private sealed record OwnerReply(
        OwnerCommandOutcome Outcome,
        OwnerVaultState VaultState,
        string? ErrorCode,
        string? RecoveryCode,
        JsonElement Payload)
    {
        public static OwnerReply Unavailable(string errorCode) =>
            new(OwnerCommandOutcome.Unavailable, OwnerVaultState.Unknown, errorCode, null, default);
    }

    private static Task<OwnerReply> ReadReplyAsync(
        string controlPipe,
        ServiceRequest request,
        string expectedStatus,
        CancellationToken cancellationToken) =>
        ReadReplyAsync(controlPipe, request, new[] { expectedStatus }, cancellationToken);

    private static async Task<OwnerReply> ReadReplyAsync(
        string controlPipe,
        ServiceRequest request,
        string[] expectedStatuses,
        CancellationToken cancellationToken)
    {
        if (!IsValidControlPipe(controlPipe))
        {
            throw new ArgumentException("The owner control pipe name is invalid.", nameof(controlPipe));
        }

        var requestJson = JsonSerializer.Serialize(request, JsonOptions);
        var result = await PipeClient.InvokeOwnerAsync(controlPipe, requestJson, cancellationToken).ConfigureAwait(false);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(result.Json);
        }
        catch (JsonException)
        {
            return OwnerReply.Unavailable("malformed_reply");
        }

        using (document)
        {
            var reply = document.RootElement;
            if (reply.ValueKind != JsonValueKind.Object)
            {
                return OwnerReply.Unavailable("malformed_reply");
            }

            if (reply.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
            {
                var code = error.GetString()!;
                return code switch
                {
                    "forbidden" or "unauthorized" => new OwnerReply(OwnerCommandOutcome.OwnerAuthorityDenied, OwnerVaultState.Unknown, code, null, default),
                    "unsupported_protocol" => new OwnerReply(OwnerCommandOutcome.ProtocolMismatch, OwnerVaultState.Unknown, code, null, default),
                    // Every other code came back from a running service that
                    // understood the request and declined it; only the codes this
                    // client fabricates describe a transport that never answered.
                    _ => IsLocalTransportCode(code)
                        ? OwnerReply.Unavailable(code)
                        : new OwnerReply(OwnerCommandOutcome.Refused, OwnerVaultState.Unknown, code, null, default)
                };
            }

            if (!expectedStatuses.Any(status => HasText(reply, "status", status)))
            {
                return OwnerReply.Unavailable("unexpected_reply");
            }

            // Every owner operation is answered on the owner plane only. A reply
            // claiming any other plane never becomes an owner session, an owner
            // vault act or an owner library answer in this client.
            if (!HasText(reply, "principal", "owner"))
            {
                return new OwnerReply(OwnerCommandOutcome.OwnerAuthorityDenied, OwnerVaultState.Unknown, "owner_plane_mismatch", null, default);
            }

            var state = reply.TryGetProperty("vaultState", out var vault) && vault.ValueKind == JsonValueKind.String
                ? MapVaultState(vault.GetString())
                : OwnerVaultState.Unknown;
            var recoveryCode = reply.TryGetProperty("recoveryCode", out var recovery) && recovery.ValueKind == JsonValueKind.String
                ? recovery.GetString()
                : null;
            return new OwnerReply(OwnerCommandOutcome.Completed, state, null, recoveryCode, reply.Clone());
        }
    }

    /// <summary>
    /// One selection draft in the shape the service decodes for a job or a
    /// schedule: the explicit inputs with pinned revisions, the objective and
    /// the requested level.
    /// </summary>
    private static byte[] BuildSelectionDraft(OwnerJobInput[] inputs, string? objective, string? targetLevel) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            inputs = inputs.Select(input => new { resourceId = input.ResourceId, revision = input.Revision }).ToArray(),
            objective,
            targetLevel
        });

    private static OwnerProviderSettings? ProjectProviderSettings(OwnerReply reply)
    {
        if (reply.Outcome != OwnerCommandOutcome.Completed ||
            !reply.Payload.TryGetProperty("providerStatus", out var settings) ||
            settings.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new OwnerProviderSettings(
            Flag(settings, "configured"),
            Text(settings, "provider"),
            Text(settings, "model"),
            Text(settings, "baseUrl"),
            DecimalValue(settings, "maximumJobCostUsd"),
            Int64(settings, "maximumJobInputTokens"),
            Int64(settings, "maximumJobOutputTokens"),
            DecimalValue(settings, "maximumRolling30DayCostUsd"));
    }

    private static OwnerJob[]? ProjectJobs(OwnerReply reply)
    {
        if (reply.Outcome != OwnerCommandOutcome.Completed ||
            !reply.Payload.TryGetProperty("jobs", out var jobs) ||
            jobs.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return jobs.EnumerateArray().Select(ProjectJobElement).ToArray();
    }

    private static OwnerJob? ProjectJob(OwnerReply reply)
    {
        if (reply.Outcome != OwnerCommandOutcome.Completed ||
            !reply.Payload.TryGetProperty("job", out var job) ||
            job.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return ProjectJobElement(job);
    }

    private static OwnerJob ProjectJobElement(JsonElement job)
    {
        var usage = job.TryGetProperty("usage", out var usageElement) && usageElement.ValueKind == JsonValueKind.Object
            ? new OwnerJobUsage(Int64(usageElement, "inputTokens"), Int64(usageElement, "outputTokens"), DecimalValue(usageElement, "costUsd"))
            : null;
        var failure = job.TryGetProperty("failure", out var failureElement) && failureElement.ValueKind == JsonValueKind.Object
            ? new OwnerJobFailure(Text(failureElement, "code") ?? "unknown", Text(failureElement, "message") ?? string.Empty)
            : null;
        var proposals = job.TryGetProperty("outputProposalIds", out var proposalElements) && proposalElements.ValueKind == JsonValueKind.Array
            ? proposalElements.EnumerateArray()
                .Where(id => id.ValueKind == JsonValueKind.String)
                .Select(id => id.GetString()!)
                .ToArray()
            : Array.Empty<string>();

        return new OwnerJob(
            Text(job, "id") ?? string.Empty,
            Text(job, "zoneId") ?? string.Empty,
            Int64(job, "revision"),
            Text(job, "kind") ?? string.Empty,
            Text(job, "state") ?? "Unknown",
            ProjectJobScope(job.TryGetProperty("scope", out var scopeElement) && scopeElement.ValueKind == JsonValueKind.Object ? scopeElement : null),
            usage,
            failure,
            proposals,
            Timestamp(job, "createdAtUtc") ?? DateTimeOffset.MinValue,
            Timestamp(job, "startedAtUtc"),
            Timestamp(job, "completedAtUtc"));
    }

    private static OwnerJobScope ProjectJobScope(JsonElement? scope)
    {
        var inputs = scope is { } scopeValue &&
                     scopeValue.TryGetProperty("inputs", out var inputElements) &&
                     inputElements.ValueKind == JsonValueKind.Array
            ? inputElements.EnumerateArray()
                .Where(input => input.ValueKind == JsonValueKind.Object)
                .Select(input => new OwnerJobInput(Text(input, "resourceId") ?? string.Empty, Int64(input, "revision")))
                .ToArray()
            : Array.Empty<OwnerJobInput>();

        return new OwnerJobScope(
            inputs,
            scope is { } zoneValue ? Text(zoneValue, "destinationZoneId") ?? string.Empty : string.Empty,
            scope is { } destinationValue ? Text(destinationValue, "destinationResourceId") ?? string.Empty : string.Empty,
            scope is { } destinationRevisionValue ? Int64(destinationRevisionValue, "destinationRevision") : 0,
            scope is { } providerValue ? Text(providerValue, "provider") ?? string.Empty : string.Empty,
            scope is { } modelValue ? Text(modelValue, "model") ?? string.Empty : string.Empty,
            scope is { } costValue ? DecimalValue(costValue, "maximumCostUsd") : 0m,
            scope is { } inputTokensValue ? Int64(inputTokensValue, "maxInputTokens") : 0,
            scope is { } outputTokensValue ? Int64(outputTokensValue, "maxOutputTokens") : 0,
            scope is { } profileValue ? Text(profileValue, "profile") : null,
            scope is { } objectiveValue ? Text(objectiveValue, "objective") : null,
            scope is { } levelValue ? Text(levelValue, "targetLevel") : null);
    }

    private static OwnerSchedule? ProjectSchedule(OwnerReply reply)
    {
        if (reply.Outcome != OwnerCommandOutcome.Completed ||
            !reply.Payload.TryGetProperty("synthesisSchedule", out var schedule) ||
            schedule.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var inputs = schedule.TryGetProperty("inputs", out var inputElements) && inputElements.ValueKind == JsonValueKind.Array
            ? inputElements.EnumerateArray()
                .Where(input => input.ValueKind == JsonValueKind.Object)
                .Select(input => new OwnerJobInput(Text(input, "resourceId") ?? string.Empty, Int64(input, "revision")))
                .ToArray()
            : Array.Empty<OwnerJobInput>();

        return new OwnerSchedule(
            Flag(schedule, "enabled"),
            Int32(schedule, "intervalMinutes"),
            inputs,
            Text(schedule, "objective") ?? string.Empty,
            Text(schedule, "targetLevel") ?? string.Empty,
            Text(schedule, "destinationZoneId") ?? string.Empty,
            Text(schedule, "destinationResourceId") ?? string.Empty,
            Int64(schedule, "destinationRevision"),
            Text(schedule, "provider") ?? string.Empty,
            Text(schedule, "model") ?? string.Empty,
            Timestamp(schedule, "createdAtUtc") ?? DateTimeOffset.MinValue,
            Timestamp(schedule, "updatedAtUtc") ?? DateTimeOffset.MinValue,
            Timestamp(schedule, "nextDueAtUtc"),
            Timestamp(schedule, "lastRunAtUtc"),
            Text(schedule, "lastRunJobId"),
            Text(schedule, "lastRunOutcome"));
    }
    private static OwnerScheduledIntention[]? ProjectScheduledIntentions(OwnerReply reply)
    {
        if (reply.Outcome != OwnerCommandOutcome.Completed ||
            !reply.Payload.TryGetProperty("intentionSchedule", out var schedule) ||
            schedule.ValueKind != JsonValueKind.Object ||
            !schedule.TryGetProperty("items", out var items) ||
            items.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var result = new OwnerScheduledIntention[items.GetArrayLength()];
        var index = 0;
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            result[index++] = new OwnerScheduledIntention(
                Text(item, "resourceId") ?? string.Empty,
                Text(item, "zoneId") ?? string.Empty,
                Int64(item, "lifecycleRevision"),
                Text(item, "state") ?? string.Empty,
                Text(item, "title"),
                Text(item, "bodyPreview") ?? string.Empty,
                Timestamp(item, "dueAt"),
                Flag(item, "isDue"),
                Flag(item, "notified"),
                Timestamp(item, "notifiedAtUtc"),
                Flag(item, "acknowledged"),
                Timestamp(item, "acknowledgedAtUtc"),
                Text(item, "contextProject"),
                Text(item, "contextTask"),
                Flag(item, "contextTriggerEnabled"));
        }

        return result;
    }


    private static OwnerSpendLedger? ProjectSpendLedger(OwnerReply reply)
    {
        if (reply.Outcome != OwnerCommandOutcome.Completed ||
            !reply.Payload.TryGetProperty("spendLedger", out var ledger) ||
            ledger.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var entries = ledger.TryGetProperty("entries", out var entryElements) && entryElements.ValueKind == JsonValueKind.Array
            ? entryElements.EnumerateArray()
                .Where(entry => entry.ValueKind == JsonValueKind.Object)
                .Select(entry => new OwnerSpendEntry(
                    Text(entry, "entryId") ?? string.Empty,
                    Text(entry, "jobId") ?? string.Empty,
                    Timestamp(entry, "timestampUtc") ?? DateTimeOffset.MinValue,
                    Text(entry, "provider") ?? string.Empty,
                    Text(entry, "model") ?? string.Empty,
                    Int64(entry, "inputTokens"),
                    Int64(entry, "outputTokens"),
                    DecimalValue(entry, "costUsd"),
                    Flag(entry, "isReservation")))
                .ToArray()
            : Array.Empty<OwnerSpendEntry>();

        return new OwnerSpendLedger(
            DecimalValue(ledger, "historicalReservationUsd"),
            DecimalValue(ledger, "rollingSpendUsd"),
            entries);
    }

    private static string CurrentToken(OwnerLibraryCurrentness currentness) => currentness switch
    {
        OwnerLibraryCurrentness.Current => "current",
        OwnerLibraryCurrentness.History => "history",
        _ => "all"
    };

    private static OwnerLibraryPage? ProjectLibraryPage(OwnerReply reply)
    {
        if (reply.Outcome != OwnerCommandOutcome.Completed ||
            !reply.Payload.TryGetProperty("library", out var payload) ||
            payload.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var hits = payload.TryGetProperty("hits", out var hitElements) && hitElements.ValueKind == JsonValueKind.Array
            ? hitElements.EnumerateArray().Select(ProjectLibraryHit).ToArray()
            : Array.Empty<OwnerLibraryHit>();

        return new OwnerLibraryPage(
            Text(payload, "query") ?? string.Empty,
            Text(payload, "zoneId"),
            Text(payload, "collectionId"),
            Text(payload, "kind"),
            Text(payload, "level"),
            Text(payload, "current") ?? "all",
            Timestamp(payload, "fromUtc"),
            Timestamp(payload, "toUtc"),
            Int32(payload, "total"),
            Int32(payload, "offset"),
            Int32(payload, "limit"),
            Flag(payload, "hasMore"),
            hits,
            Timestamp(payload, "asOfUtc"));
    }

    private static OwnerReviewQueue? ProjectReviewQueue(OwnerReply reply)
    {
        if (reply.Outcome != OwnerCommandOutcome.Completed ||
            !reply.Payload.TryGetProperty("reviewQueue", out var payload) ||
            payload.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var entries = payload.TryGetProperty("entries", out var entryElements) && entryElements.ValueKind == JsonValueKind.Array
            ? entryElements.EnumerateArray().Select(ProjectReviewEntry).ToArray()
            : Array.Empty<OwnerReviewEntry>();

        return new OwnerReviewQueue(
            entries,
            Int32(payload, "scannedItems"),
            Flag(payload, "truncated"));
    }

    private static OwnerReviewEntry ProjectReviewEntry(JsonElement entry)
    {
        var citations = entry.TryGetProperty("citations", out var citationElements) && citationElements.ValueKind == JsonValueKind.Array
            ? citationElements.EnumerateArray().Select(citation => new OwnerReviewCitation(
                Text(citation, "sourceId") ?? string.Empty,
                OptionalInt64(citation, "citedRevision"),
                OptionalInt64(citation, "liveRevision"),
                Text(citation, "availability") ?? "unknown",
                Timestamp(citation, "sourceRecordedAtUtc"))).ToArray()
            : Array.Empty<OwnerReviewCitation>();

        return new OwnerReviewEntry(
            Text(entry, "resourceId") ?? string.Empty,
            Text(entry, "zoneId") ?? string.Empty,
            Int64(entry, "resourceRevision"),
            Text(entry, "kind") ?? "unknown",
            Text(entry, "title"),
            Text(entry, "state") ?? "current",
            Strings(entry, "reasons"),
            Strings(entry, "undeterminedReasons"),
            Strings(entry, "pendingReasons"),
            Strings(entry, "acknowledgedReasons"),
            ProjectStamp(entry, "reviewAfter"),
            Timestamp(entry, "lastReviewedAtUtc"),
            Text(entry, "lastReviewedRationale"),
            citations);
    }

    private static OwnerReviewRecord? ProjectOwnerReview(OwnerReply reply)
    {
        if (reply.Outcome != OwnerCommandOutcome.Completed ||
            !reply.Payload.TryGetProperty("review", out var payload) ||
            payload.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new OwnerReviewRecord(
            Text(payload, "subjectResourceId") ?? string.Empty,
            Timestamp(payload, "reviewedAtUtc") ?? DateTimeOffset.MinValue,
            Text(payload, "actor") ?? string.Empty,
            Text(payload, "rationale") ?? string.Empty,
            Strings(payload, "acknowledgedReasons"));
    }

    private static OwnerRecallState? ProjectRecallState(OwnerReply reply)
    {
        if (reply.Outcome != OwnerCommandOutcome.Completed ||
            !reply.Payload.TryGetProperty("recall", out var payload) ||
            payload.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var history = payload.TryGetProperty("history", out var historyElements) && historyElements.ValueKind == JsonValueKind.Array
            ? historyElements.EnumerateArray().Select(change => new OwnerRecallChange(
                Text(change, "from") ?? string.Empty,
                Text(change, "to") ?? string.Empty,
                Text(change, "actor") ?? string.Empty,
                Timestamp(change, "recordedAtUtc") ?? DateTimeOffset.MinValue,
                Text(change, "rationale"))).ToArray()
            : Array.Empty<OwnerRecallChange>();

        return new OwnerRecallState(
            Text(payload, "subjectResourceId") ?? string.Empty,
            Text(payload, "disposition") ?? "normal",
            Text(payload, "rationale"),
            Text(payload, "actor"),
            Timestamp(payload, "recordedAtUtc"),
            history);
    }
    private static OwnerPurgePreview? ProjectPurgePreview(OwnerReply reply)
    {
        if (reply.Outcome != OwnerCommandOutcome.Completed ||
            !reply.Payload.TryGetProperty("purgePreview", out var payload) ||
            payload.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var impacted = payload.TryGetProperty("impacted", out var impactedElements) && impactedElements.ValueKind == JsonValueKind.Array
            ? impactedElements.EnumerateArray().Select(item => new OwnerPurgeImpact(
                Text(item, "resourceId") ?? string.Empty,
                Text(item, "kind") ?? "canonical",
                Int64(item, "revision"),
                Text(item, "reason") ?? string.Empty)).ToArray()
            : Array.Empty<OwnerPurgeImpact>();
        var catalogIds = payload.TryGetProperty("withdrawnCatalogIds", out var catalogElements) && catalogElements.ValueKind == JsonValueKind.Array
            ? catalogElements.EnumerateArray().Select(value => value.GetString()!).Where(id => id is not null).ToArray()
            : Array.Empty<string>();
        return new OwnerPurgePreview(
            Text(payload, "purgeId") ?? string.Empty,
            Text(payload, "targetResourceId") ?? string.Empty,
            Int64(payload, "targetRevision"),
            Text(payload, "targetZoneId") ?? string.Empty,
            Text(payload, "targetKind") ?? "canonical",
            impacted,
            catalogIds,
            Flag(payload, "grantsTouched"),
            Flag(payload, "sessionsTouched"),
            Int64(payload, "liveGeneration"),
            Text(payload, "s8BackupNote") ?? string.Empty);
    }

    private static OwnerPurgeOutcome? ProjectPurgeOutcome(OwnerReply reply)
    {
        if (reply.Outcome != OwnerCommandOutcome.Completed ||
            !reply.Payload.TryGetProperty("purge", out var payload) ||
            payload.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var deleted = payload.TryGetProperty("deleted", out var deletedElements) && deletedElements.ValueKind == JsonValueKind.Array
            ? deletedElements.EnumerateArray().Select(item => new OwnerPurgeDeleted(
                Text(item, "resourceId") ?? string.Empty,
                Int64(item, "revision"))).ToArray()
            : Array.Empty<OwnerPurgeDeleted>();
        var catalogIds = payload.TryGetProperty("withdrawnCatalogIds", out var catalogElements) && catalogElements.ValueKind == JsonValueKind.Array
            ? catalogElements.EnumerateArray().Select(value => value.GetString()!).Where(id => id is not null).ToArray()
            : Array.Empty<string>();
        return new OwnerPurgeOutcome(
            Text(payload, "purgeId") ?? string.Empty,
            Text(payload, "targetResourceId") ?? string.Empty,
            deleted,
            catalogIds,
            Int64(payload, "finalGeneration"),
            Flag(payload, "decisionApplied"));
    }

    private static OwnerLibraryHit ProjectLibraryHit(JsonElement hit) => new(
        Text(hit, "id") ?? string.Empty,
        Int64(hit, "revision"),
        Text(hit, "zoneId") ?? string.Empty,
        Text(hit, "kind") ?? string.Empty,
        Text(hit, "level") ?? string.Empty,
        Text(hit, "title"),
        Number(hit, "score"),
        Text(hit, "snippet") ?? string.Empty,
        Timestamp(hit, "occurredAtUtc"),
        Timestamp(hit, "recordedAtUtc") ?? DateTimeOffset.MinValue,
        Text(hit, "publication") ?? string.Empty)
    {
        RecallState = Text(hit, "recallState") ?? "normal",
        RankingReason = Text(hit, "rankingReason") ?? string.Empty,
    };

    private static OwnerItemDetail? ProjectItem(OwnerReply reply)
    {
        if (reply.Outcome != OwnerCommandOutcome.Completed ||
            !reply.Payload.TryGetProperty("memoryItem", out var item) ||
            item.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var sources = item.TryGetProperty("sources", out var sourceElements) && sourceElements.ValueKind == JsonValueKind.Array
            ? sourceElements.EnumerateArray().Select(ProjectItemSource).ToArray()
            : Array.Empty<OwnerItemSource>();
        var citations = item.TryGetProperty("citations", out var citationElements) && citationElements.ValueKind == JsonValueKind.Array
            ? citationElements.EnumerateArray().Select(ProjectItemCitation).ToArray()
            : Array.Empty<OwnerItemCitation>();
        var warnings = item.TryGetProperty("warnings", out var warningElements) && warningElements.ValueKind == JsonValueKind.Array
            ? warningElements.EnumerateArray()
                .Where(warning => warning.ValueKind == JsonValueKind.String)
                .Select(warning => warning.GetString()!)
                .ToArray()
            : Array.Empty<string>();

        var applicability = item.TryGetProperty("applicability", out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : (JsonElement?)null;

        return new OwnerItemDetail(
            Text(item, "id") ?? string.Empty,
            Text(item, "zoneId") ?? string.Empty,
            Int64(item, "revision"),
            Text(item, "kind") ?? string.Empty,
            Text(item, "author") ?? string.Empty,
            Text(item, "executor") ?? string.Empty,
            Text(item, "creationMethod") ?? string.Empty,
            Text(item, "evidence") ?? string.Empty,
            Text(item, "level") ?? string.Empty,
            Text(item, "title"),
            Text(item, "body") ?? string.Empty,
            sources,
            citations,
            warnings,
            Timestamp(item, "occurredAtUtc"),
            Timestamp(item, "recordedAtUtc") ?? DateTimeOffset.MinValue,
            applicability is { } applicabilityValue ? Text(applicabilityValue, "project") : null,
            applicability is { } environmentValue ? Text(environmentValue, "environment") : null,
            applicability is { } versionValue ? Text(versionValue, "version") : null);
    }

    private static OwnerItemSource ProjectItemSource(JsonElement source) => new(
        Text(source, "sourceId"),
        Text(source, "locator"),
        Text(source, "availability") ?? "unknown",
        OptionalInt64(source, "sourceRevision"),
        SpanStart(source),
        SpanEnd(source));

    private static OwnerItemCitation ProjectItemCitation(JsonElement citation) => new(
        Text(citation, "sourceId"),
        Text(citation, "locator"),
        Text(citation, "availability") ?? "unknown",
        OptionalInt64(citation, "sourceRevision"),
        SpanStart(citation),
        SpanEnd(citation),
        Text(citation, "status") ?? "unknown",
        Text(citation, "warning"));

    private static OwnerSourceDetail? ProjectSource(OwnerReply reply)
    {
        if (reply.Outcome != OwnerCommandOutcome.Completed ||
            !reply.Payload.TryGetProperty("source", out var source) ||
            source.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new OwnerSourceDetail(
            Text(source, "id") ?? string.Empty,
            Text(source, "zoneId") ?? string.Empty,
            Int64(source, "revision"),
            Text(source, "title"),
            Text(source, "locator"),
            Text(source, "version"),
            Text(source, "availability") ?? "unknown",
            Text(source, "author") ?? string.Empty,
            Text(source, "executor") ?? string.Empty,
            Text(source, "creationMethod") ?? string.Empty,
            Timestamp(source, "capturedAtUtc"),
            Timestamp(source, "recordedAtUtc") ?? DateTimeOffset.MinValue);
    }

    private static OwnerIndexReport? ProjectIndex(OwnerReply reply)
    {
        if (reply.Outcome != OwnerCommandOutcome.Completed ||
            !reply.Payload.TryGetProperty("privateIndex", out var index) ||
            index.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new OwnerIndexReport(
            Int32(index, "schemaVersion"),
            Text(index, "canonicalDigest") ?? string.Empty,
            Int32(index, "rowCount"),
            Flag(index, "stale"),
            Flag(index, "canonicalMoved"));
    }

    /// <summary>The pending inbox identities the service listed; never a proposed body.</summary>
    private static OwnerProposalIdentity[]? ProjectProposalIdentities(OwnerReply reply)
    {
        if (reply.Outcome != OwnerCommandOutcome.Completed ||
            reply.Payload.ValueKind != JsonValueKind.Object ||
            !reply.Payload.TryGetProperty("proposals", out var proposals) ||
            proposals.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return proposals.EnumerateArray()
            .Select(proposal => new OwnerProposalIdentity(
                Text(proposal, "id") ?? string.Empty,
                Int64(proposal, "revision"),
                Text(proposal, "zoneId") ?? string.Empty))
            .ToArray();
    }

    /// <summary>
    /// One pending proposal with its provenance and the proposed item draft,
    /// exactly as the agent wrote it. The user-confirmation value is the claim
    /// the record carries and is never presented as a verified confirmation.
    /// </summary>
    private static OwnerProposalDetail? ProjectProposal(OwnerReply reply)
    {
        if (reply.Outcome != OwnerCommandOutcome.Completed ||
            reply.Payload.ValueKind != JsonValueKind.Object ||
            !reply.Payload.TryGetProperty("proposal", out var proposal) ||
            proposal.ValueKind != JsonValueKind.Object ||
            !proposal.TryGetProperty("item", out var item) ||
            item.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var sources = item.TryGetProperty("sources", out var sourceElements) && sourceElements.ValueKind == JsonValueKind.Array
            ? sourceElements.EnumerateArray().Select(ProjectItemSource).ToArray()
            : Array.Empty<OwnerItemSource>();
        string? project = null;
        string? environment = null;
        string? version = null;
        if (item.TryGetProperty("applicability", out var applicability) && applicability.ValueKind == JsonValueKind.Object)
        {
            project = Text(applicability, "project");
            environment = Text(applicability, "environment");
            version = Text(applicability, "version");
        }

        OwnerProposalCitationReview? review = null;
        if (proposal.TryGetProperty("citationReview", out var reviewElement) && reviewElement.ValueKind == JsonValueKind.Object)
        {
            review = new OwnerProposalCitationReview(
                Text(reviewElement, "state") ?? "unknown",
                Strings(reviewElement, "reasons"),
                Strings(reviewElement, "staleSourceIds"));
        }

        return new OwnerProposalDetail(
            Text(proposal, "id") ?? string.Empty,
            Text(proposal, "zoneId") ?? string.Empty,
            Int64(proposal, "revision"),
            Text(proposal, "kind") ?? string.Empty,
            Text(proposal, "destinationResourceId") ?? string.Empty,
            Int64(proposal, "destinationRevision"),
            Text(proposal, "subjectResourceId"),
            OptionalInt64(proposal, "subjectRevision"),
            Text(proposal, "idempotencyKey") ?? string.Empty,
            Text(proposal, "payloadHash") ?? string.Empty,
            Text(proposal, "executor") ?? string.Empty,
            Timestamp(proposal, "recordedAtUtc") ?? DateTimeOffset.MinValue,
            Flag(proposal, "claimedUserConfirmed"),
            new OwnerReviewDraft(
                Text(item, "kind") ?? string.Empty,
                Text(item, "author") ?? string.Empty,
                Text(item, "creationMethod") ?? string.Empty,
                Text(item, "evidence") ?? string.Empty,
                Text(item, "level") ?? string.Empty,
                Text(item, "title"),
                Text(item, "body") ?? string.Empty,
                sources,
                Timestamp(item, "occurredAtUtc"),
                project,
                environment,
                version),
            review);
    }

    /// <summary>One review outcome as the service reported it.</summary>
    private static OwnerReviewOutcome? ProjectReview(OwnerReply reply)
    {
        if (reply.Outcome != OwnerCommandOutcome.Completed ||
            reply.Payload.ValueKind != JsonValueKind.Object ||
            !reply.Payload.TryGetProperty("proposalReview", out var review) ||
            review.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new OwnerReviewOutcome(
            Text(review, "proposalId") ?? string.Empty,
            Int64(review, "proposalRevision"),
            Text(review, "kind") ?? string.Empty,
            Text(review, "itemId") ?? string.Empty,
            Int64(review, "itemRevision"),
            Text(review, "publication") ?? string.Empty,
            Int64(review, "lifecycleRevision"),
            Flag(review, "decisionApplied"));
    }

    private static OwnerDeclassifyPreview? ProjectDeclassifyPreview(OwnerReply reply)
    {
        if (reply.Outcome != OwnerCommandOutcome.Completed ||
            reply.Payload.ValueKind != JsonValueKind.Object ||
            !reply.Payload.TryGetProperty("declassifyPreview", out var preview) ||
            preview.ValueKind != JsonValueKind.Object ||
            !preview.TryGetProperty("item", out var item) ||
            item.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var pins = preview.TryGetProperty("pins", out var pinElements) && pinElements.ValueKind == JsonValueKind.Array
            ? pinElements.EnumerateArray()
                .Where(element => element.ValueKind == JsonValueKind.Object)
                .Select(element => new OwnerDeclassifyPin(
                    Text(element, "resourceId") ?? string.Empty,
                    Int64(element, "revision")))
                .ToArray()
            : Array.Empty<OwnerDeclassifyPin>();

        return new OwnerDeclassifyPreview(
            Text(preview, "destinationResourceId") ?? string.Empty,
            Text(preview, "destinationZoneId") ?? string.Empty,
            ProjectDeclassifyDraft(item),
            pins,
            Flag(preview, "pinsCurrent"),
            Strings(preview, "mismatchedResourceIds"));
    }

    private static OwnerReviewDraft ProjectDeclassifyDraft(JsonElement item)
    {
        string? project = null;
        string? environment = null;
        string? version = null;
        if (item.TryGetProperty("applicability", out var applicability) && applicability.ValueKind == JsonValueKind.Object)
        {
            project = Text(applicability, "project");
            environment = Text(applicability, "environment");
            version = Text(applicability, "version");
        }

        return new OwnerReviewDraft(
            Text(item, "kind") ?? string.Empty,
            Text(item, "author") ?? string.Empty,
            Text(item, "creationMethod") ?? string.Empty,
            Text(item, "evidence") ?? string.Empty,
            Text(item, "level") ?? string.Empty,
            Text(item, "title"),
            Text(item, "body") ?? string.Empty,
            Array.Empty<OwnerItemSource>(),
            Timestamp(item, "occurredAtUtc"),
            project,
            environment,
            version);
    }

    private static OwnerDeclassifyOutcome? ProjectDeclassify(OwnerReply reply)
    {
        if (reply.Outcome != OwnerCommandOutcome.Completed ||
            reply.Payload.ValueKind != JsonValueKind.Object ||
            !reply.Payload.TryGetProperty("declassify", out var published) ||
            published.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new OwnerDeclassifyOutcome(
            Text(published, "destinationResourceId") ?? string.Empty,
            Int64(published, "destinationRevision"),
            Int64(published, "lifecycleRevision"),
            Strings(published, "lineageRelationIds"),
            Flag(published, "decisionApplied"));
    }


    /// <summary>
    /// The relation list the service served, in the order it served it. A reply
    /// that carries no array is no list, so the caller reports no relation list
    /// instead of an empty vault.
    /// </summary>
    private static OwnerRelation[]? ProjectRelations(OwnerReply reply)
    {
        if (reply.Outcome != OwnerCommandOutcome.Completed ||
            reply.Payload.ValueKind != JsonValueKind.Object ||
            !reply.Payload.TryGetProperty("relations", out var relations) ||
            relations.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var projected = new List<OwnerRelation>();
        foreach (var element in relations.EnumerateArray())
        {
            if (element.ValueKind == JsonValueKind.Object && ProjectRelationElement(element) is { } relation)
            {
                projected.Add(relation);
            }
        }

        return projected.ToArray();
    }

    private static OwnerRelation? ProjectRelation(OwnerReply reply) =>
        reply.Outcome == OwnerCommandOutcome.Completed &&
        reply.Payload.ValueKind == JsonValueKind.Object &&
        reply.Payload.TryGetProperty("relation", out var relation) &&
        relation.ValueKind == JsonValueKind.Object
            ? ProjectRelationElement(relation)
            : null;

    /// <summary>
    /// One relation element as the wire carried it. An element that does not
    /// carry both endpoints is not a relation this client can render, so it is
    /// reported as no relation rather than as a link with invented sides.
    /// </summary>
    private static OwnerRelation? ProjectRelationElement(JsonElement relation)
    {
        if (!relation.TryGetProperty("from", out var from) || from.ValueKind != JsonValueKind.Object ||
            !relation.TryGetProperty("to", out var to) || to.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var review = relation.TryGetProperty("review", out var reviewElement) && reviewElement.ValueKind == JsonValueKind.Object
            ? new OwnerRelationReview(
                Text(reviewElement, "state") ?? "unknown",
                Strings(reviewElement, "reasons"),
                Strings(reviewElement, "staleResourceIds"))
            : new OwnerRelationReview("unknown", Array.Empty<string>(), Array.Empty<string>());

        var evidence = new List<OwnerRelationRequestEvidence>();
        if (relation.TryGetProperty("requestEvidence", out var entries) && entries.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in entries.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                evidence.Add(new OwnerRelationRequestEvidence(
                    Text(entry, "action") ?? "unknown",
                    Text(entry, "text") ?? string.Empty,
                    Text(entry, "executor") ?? "unknown",
                    Timestamp(entry, "recordedAtUtc") ?? default));
            }
        }

        return new OwnerRelation(
            Text(relation, "id") ?? string.Empty,
            Text(relation, "zoneId") ?? string.Empty,
            Int64(relation, "revision"),
            Text(relation, "kind") ?? "unknown",
            Text(relation, "state") ?? "unknown",
            ProjectRelationEndpoint(from),
            ProjectRelationEndpoint(to),
            Text(relation, "executor") ?? "unknown",
            Text(relation, "creationMethod") ?? "unknown",
            Text(relation, "reason") ?? string.Empty,
            Text(relation, "stateReason"),
            Timestamp(relation, "occurredAtUtc"),
            Timestamp(relation, "recordedAtUtc") ?? default,
            Text(relation, "stateExecutor") ?? "unknown",
            Timestamp(relation, "stateRecordedAtUtc") ?? default,
            review,
            evidence.ToArray());
    }

    private static OwnerRelationEndpoint ProjectRelationEndpoint(JsonElement endpoint) =>
        new(
            Text(endpoint, "resourceId") ?? string.Empty,
            Text(endpoint, "endpointKind") ?? "unknown",
            Int64(endpoint, "revision"),
            SpanStart(endpoint),
            SpanEnd(endpoint));

    private static string[] Strings(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()!)
                .ToArray()
            : Array.Empty<string>();

    // ---- S5-T6 projections: catalog, access requests, sessions ----

    private static OwnerCatalogPreviewEntry? ProjectCatalogPreview(OwnerReply reply)
    {
        if (reply.Outcome != OwnerCommandOutcome.Completed ||
            reply.Payload.ValueKind != JsonValueKind.Object ||
            !reply.Payload.TryGetProperty("catalogPreview", out var preview) ||
            preview.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        OwnerCatalogMapping? existing = null;
        if (preview.TryGetProperty("existingEntry", out var current) && current.ValueKind == JsonValueKind.Object)
        {
            existing = ProjectCatalogMappingElement(current);
        }

        return new OwnerCatalogPreviewEntry(
            Text(preview, "catalogId") ?? string.Empty,
            Text(preview, "label") ?? string.Empty,
            Text(preview, "description") ?? string.Empty,
            Text(preview, "resourceId") ?? string.Empty,
            Int64(preview, "resourceRevision"),
            existing);
    }

    private static OwnerCatalogMapping[]? ProjectCatalogEntries(OwnerReply reply)
    {
        if (reply.Outcome != OwnerCommandOutcome.Completed ||
            reply.Payload.ValueKind != JsonValueKind.Object ||
            !reply.Payload.TryGetProperty("catalogEntries", out var entries) ||
            entries.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var projected = new List<OwnerCatalogMapping>();
        foreach (var element in entries.EnumerateArray())
        {
            if (element.ValueKind == JsonValueKind.Object && ProjectCatalogMappingElement(element) is { } mapping)
            {
                projected.Add(mapping);
            }
        }

        return projected.ToArray();
    }

    private static OwnerCatalogMapping? ProjectCatalogMappingElement(JsonElement entry) =>
        entry.TryGetProperty("catalogId", out var id) && id.ValueKind == JsonValueKind.String &&
        entry.TryGetProperty("resourceId", out var resource) && resource.ValueKind == JsonValueKind.String
            ? new OwnerCatalogMapping(
                id.GetString() ?? string.Empty,
                Text(entry, "label") ?? string.Empty,
                Text(entry, "description") ?? string.Empty,
                resource.GetString() ?? string.Empty,
                Int64(entry, "revision"),
                Timestamp(entry, "updatedAtUtc") ?? DateTimeOffset.MinValue)
            : null;

    private static OwnerAccessRequestInfo[]? ProjectAccessRequests(OwnerReply reply)
    {
        if (reply.Outcome != OwnerCommandOutcome.Completed ||
            reply.Payload.ValueKind != JsonValueKind.Object ||
            !reply.Payload.TryGetProperty("accessRequests", out var requests) ||
            requests.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var projected = new List<OwnerAccessRequestInfo>();
        foreach (var element in requests.EnumerateArray())
        {
            if (element.ValueKind == JsonValueKind.Object && ProjectAccessRequestElement(element) is { } request)
            {
                projected.Add(request);
            }
        }

        return projected.ToArray();
    }

    private static OwnerAccessRequestInfo? ProjectAccessRequestElement(JsonElement request) =>
        request.TryGetProperty("requestId", out var id) && id.ValueKind == JsonValueKind.String
            ? new OwnerAccessRequestInfo(
                id.GetString() ?? string.Empty,
                Strings(request, "catalogIds"),
                Text(request, "purpose") ?? string.Empty,
                Text(request, "agentId"),
                Text(request, "agentContext"),
                Text(request, "provider"),
                Text(request, "model"),
                Text(request, "disclosureContext"),
                Strings(request, "requestedOperations"),
                ProjectScopeResources(request, "resolvedResources"),
                Timestamp(request, "createdAtUtc") ?? DateTimeOffset.MinValue,
                Text(request, "status") ?? "unknown",
                Text(request, "approvedGrantId"),
                Timestamp(request, "decidedAtUtc"))
            : null;

    private static OwnerAccessRequestPreviewInfo? ProjectAccessRequestPreview(OwnerReply reply)
    {
        if (reply.Outcome != OwnerCommandOutcome.Completed ||
            reply.Payload.ValueKind != JsonValueKind.Object ||
            !reply.Payload.TryGetProperty("accessRequestPreview", out var preview) ||
            preview.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new OwnerAccessRequestPreviewInfo(
            Text(preview, "previewId") ?? string.Empty,
            Text(preview, "requestId") ?? string.Empty,
            Strings(preview, "catalogIds"),
            ProjectScopeResources(preview, "resources"),
            Strings(preview, "operations"),
            Text(preview, "purpose") ?? string.Empty,
            Text(preview, "agentId"),
            Text(preview, "agentContext"),
            Text(preview, "provider"),
            Text(preview, "model"),
            Text(preview, "disclosureContext"),
            Timestamp(preview, "createdAtUtc") ?? DateTimeOffset.MinValue,
            Int64(preview, "policyGeneration"),
            Int64(preview, "unlockEpoch"),
            DecodePreviewBodies(reply));
    }

    /// <summary>
    /// The preview bodies the service included for the owner's eyes. Each body
    /// is strict UTF-8 the vault holds; anything undecodable is reported as
    /// unavailable instead of reconstructed.
    /// </summary>
    private static OwnerAccessRequestBody[] DecodePreviewBodies(OwnerReply reply)
    {
        if (!reply.Payload.TryGetProperty("contentBase64", out var encoded) || encoded.ValueKind != JsonValueKind.String)
        {
            return Array.Empty<OwnerAccessRequestBody>();
        }

        byte[] outer;
        try
        {
            outer = Convert.FromBase64String(encoded.GetString() ?? string.Empty);
        }
        catch (FormatException)
        {
            return Array.Empty<OwnerAccessRequestBody>();
        }

        try
        {
            using var document = JsonDocument.Parse(outer);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Array.Empty<OwnerAccessRequestBody>();
            }

            var bodies = new List<OwnerAccessRequestBody>();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                byte[] body;
                try
                {
                    body = Convert.FromBase64String(property.Value.GetString() ?? string.Empty);
                }
                catch (FormatException)
                {
                    continue;
                }

                try
                {
                    bodies.Add(new OwnerAccessRequestBody(property.Name, StrictUtf8.GetString(body)));
                }
                catch (ArgumentException)
                {
                    bodies.Add(new OwnerAccessRequestBody(property.Name, "(the service served bytes this console cannot show as text)"));
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(body);
                }
            }

            return bodies.OrderBy(body => body.ResourceId, StringComparer.Ordinal).ToArray();
        }
        catch (JsonException)
        {
            return Array.Empty<OwnerAccessRequestBody>();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(outer);
        }
    }

    private static OwnerAgentSessionInfo[]? ProjectSessions(OwnerReply reply)
    {
        if (reply.Outcome != OwnerCommandOutcome.Completed ||
            reply.Payload.ValueKind != JsonValueKind.Object ||
            !reply.Payload.TryGetProperty("agentSessions", out var sessions) ||
            sessions.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var projected = new List<OwnerAgentSessionInfo>();
        foreach (var element in sessions.EnumerateArray())
        {
            if (element.ValueKind == JsonValueKind.Object && ProjectSessionElement(element) is { } session)
            {
                projected.Add(session);
            }
        }

        return projected.ToArray();
    }

    private static OwnerAgentSessionInfo? ProjectSessionElement(JsonElement session) =>
        session.TryGetProperty("sessionId", out var id) && id.ValueKind == JsonValueKind.String &&
        session.TryGetProperty("grantId", out var grant) && grant.ValueKind == JsonValueKind.String
            ? new OwnerAgentSessionInfo(
                id.GetString() ?? string.Empty,
                grant.GetString() ?? string.Empty,
                ProjectScopeResources(session, "resources"),
                Strings(session, "operations"),
                ProjectScopeResources(session, "destinationResources"),
                Timestamp(session, "createdAtUtc") ?? DateTimeOffset.MinValue,
                Timestamp(session, "expiresAtUtc") ?? DateTimeOffset.MinValue,
                ProjectSessionEgress(session),
                Int64(session, "policyGeneration"),
                Int64(session, "unlockEpoch"))
            : null;

    private static OwnerSessionEgress? ProjectSessionEgress(JsonElement session) =>
        session.TryGetProperty("egress", out var egress) && egress.ValueKind == JsonValueKind.Object &&
        egress.TryGetProperty("provider", out var provider) && provider.ValueKind == JsonValueKind.String &&
        egress.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.String &&
        egress.TryGetProperty("maximumCostUsd", out var ceiling) && ceiling.ValueKind == JsonValueKind.Number
            ? new OwnerSessionEgress(provider.GetString() ?? string.Empty, model.GetString() ?? string.Empty, ceiling.GetDecimal())
            : null;

    private static OwnerScopeResource[] ProjectScopeResources(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var resources) || resources.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<OwnerScopeResource>();
        }

        var projected = new List<OwnerScopeResource>();
        foreach (var entry in resources.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.Object &&
                entry.TryGetProperty("resourceId", out var id) && id.ValueKind == JsonValueKind.String &&
                entry.TryGetProperty("zoneId", out var zone) && zone.ValueKind == JsonValueKind.String &&
                entry.TryGetProperty("revision", out var revision) && revision.ValueKind == JsonValueKind.Number)
            {
                projected.Add(new OwnerScopeResource(
                    id.GetString() ?? string.Empty, zone.GetString() ?? string.Empty, revision.GetInt64()));
            }
        }

        return projected.ToArray();
    }
    // ---- S5-T7 sources and organization projections ----

    // Ingest has its own reply reader because the service reports explicit
    // run outcomes as status replies: the unsupported diagnostics arrive as
    // status tokens (not error-code replies), like advanced/idle/partial.
    // A status reply completes here with its token; an error-code reply
    // stays a refusal with the service's error code, so the console log and
    // outcome never present a refusal as a completed save.

    private static async Task<OwnerReply> ReadIngestReplyAsync(
        string controlPipe,
        ServiceRequest request,
        CancellationToken cancellationToken)
    {
        if (!IsValidControlPipe(controlPipe))
        {
            throw new ArgumentException("The owner control pipe name is invalid.", nameof(controlPipe));
        }

        var requestJson = System.Text.Json.JsonSerializer.Serialize(request, JsonOptions);
        var result = await PipeClient.InvokeOwnerAsync(controlPipe, requestJson, cancellationToken).ConfigureAwait(false);

        System.Text.Json.JsonDocument document;
        try
        {
            document = System.Text.Json.JsonDocument.Parse(result.Json);
        }
        catch (System.Text.Json.JsonException)
        {
            return OwnerReply.Unavailable("malformed_reply");
        }

        using (document)
        {
            var reply = document.RootElement;
            if (reply.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                return OwnerReply.Unavailable("malformed_reply");
            }

            if (reply.TryGetProperty("error", out var error) && error.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                var code = error.GetString()!;
                return code switch
                {
                    "forbidden" or "unauthorized" => new OwnerReply(OwnerCommandOutcome.OwnerAuthorityDenied, OwnerVaultState.Unknown, code, null, default),
                    "unsupported_protocol" => new OwnerReply(OwnerCommandOutcome.ProtocolMismatch, OwnerVaultState.Unknown, code, null, default),
                    _ => IsLocalTransportCode(code)
                        ? OwnerReply.Unavailable(code)
                        : new OwnerReply(OwnerCommandOutcome.Refused, OwnerVaultState.Unknown, code, null, default)
                };
            }

            if (!HasText(reply, "principal", "owner"))
            {
                return new OwnerReply(OwnerCommandOutcome.OwnerAuthorityDenied, OwnerVaultState.Unknown, "owner_plane_mismatch", null, default);
            }

            var state = reply.TryGetProperty("vaultState", out var vault) && vault.ValueKind == System.Text.Json.JsonValueKind.String
                ? MapVaultState(vault.GetString())
                : OwnerVaultState.Unknown;
            return new OwnerReply(OwnerCommandOutcome.Completed, state, null, null, reply.Clone());
        }
    }

    private static OwnerSourceEnrollment[]? ProjectSourceEnrollments(OwnerReply reply)
    {
        if (reply.Outcome != OwnerCommandOutcome.Completed ||
            reply.Payload.ValueKind != System.Text.Json.JsonValueKind.Object ||
            !reply.Payload.TryGetProperty("sourceEnrollments", out var enrollments) ||
            enrollments.ValueKind != System.Text.Json.JsonValueKind.Array)
        {
            return null;
        }

        var projected = new List<OwnerSourceEnrollment>();
        foreach (var element in enrollments.EnumerateArray())
        {
            if (element.ValueKind == System.Text.Json.JsonValueKind.Object &&
                ProjectSourceEnrollmentElement(element, null) is { } enrollment)
            {
                projected.Add(enrollment);
            }
        }

        return projected.ToArray();
    }

    private static OwnerSourceEnrollment? ProjectSourceEnrollment(OwnerReply reply)
    {
        if (reply.Outcome != OwnerCommandOutcome.Completed ||
            reply.Payload.ValueKind != System.Text.Json.JsonValueKind.Object ||
            !reply.Payload.TryGetProperty("sourceEnrollment", out var enrollment) ||
            enrollment.ValueKind != System.Text.Json.JsonValueKind.Object)
        {
            return null;
        }

        System.Text.Json.JsonElement? journal = null;
        if (reply.Payload.TryGetProperty("sourceIngestion", out var journalElement) &&
            journalElement.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            journal = journalElement;
        }

        return ProjectSourceEnrollmentElement(enrollment, journal);
    }

    /// <summary>
    /// One enrollment element as the wire carried it. The wire carries no
    /// locator by design; an element without its source ID is not an
    /// enrollment this client can render, so it is reported as no enrollment
    /// rather than as a mapping with an invented identity.
    /// </summary>
    private static OwnerSourceEnrollment? ProjectSourceEnrollmentElement(System.Text.Json.JsonElement enrollment, System.Text.Json.JsonElement? journal)
    {
        if (!enrollment.TryGetProperty("sourceId", out var id) || id.ValueKind != System.Text.Json.JsonValueKind.String)
        {
            return null;
        }

        OwnerSourceQuarantine? quarantine = null;
        if (enrollment.TryGetProperty("quarantine", out var quarantineElement) &&
            quarantineElement.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            var recordedAt = Timestamp(quarantineElement, "recordedAtUtc");
            if (recordedAt is not null)
            {
                quarantine = new OwnerSourceQuarantine(
                    Text(quarantineElement, "reason") ?? "unknown",
                    Text(quarantineElement, "detail") ?? string.Empty,
                    recordedAt.Value);
            }
        }

        return new OwnerSourceEnrollment(
            id.GetString() ?? string.Empty,
            Text(enrollment, "zoneId") ?? "(unresolved)",
            Text(enrollment, "format") ?? "(unresolved)",
            enrollment.TryGetProperty("formatVersion", out var formatVersion) && formatVersion.ValueKind == System.Text.Json.JsonValueKind.Number
                ? formatVersion.GetInt32() : 0,
            Int64(enrollment, "generation"),
            Text(enrollment, "life") ?? "unknown",
            Int64(enrollment, "anchorLength"),
            Timestamp(enrollment, "enrolledAtUtc") ?? DateTimeOffset.MinValue,
            Timestamp(enrollment, "revokedAtUtc"),
            quarantine,
            Text(enrollment, "availability") ?? "unknown",
            Timestamp(enrollment, "availabilityObservedAtUtc"),
            Timestamp(enrollment, "relocatedAtUtc"),
            OptionalInt64(enrollment, "sourceReferenceRevision"),
            Timestamp(enrollment, "pausedAtUtc"),
            Flag(enrollment, "refreshOptIn"),
            Int64(enrollment, "journalCiphertextBytes"),
            Int64(enrollment, "snapshotCiphertextBytes"),
            Int64(enrollment, "snapshotRevision"),
            journal is { } journalValue ? Int64(journalValue, "journalRevision") : 0,
            journal is { } journalValue2 ? Int64(journalValue2, "consumedOffset") : 0,
            journal is { } journalValue3 ? Int32(journalValue3, "eventCount") : 0,
            journal is { } journalValue4 ? Int32(journalValue4, "diagnosticCount") : 0,
            journal is { } journalValue5 ? Timestamp(journalValue5, "lastIngestUtc") : null);
    }

    private static OwnerSourceIngestResult ProjectSourceIngest(OwnerReply reply)
    {
        // The service's own shape is authoritative: a status reply carries
        // the run outcome token, and an error-code reply carries the refusal
        // code. The unsupported diagnostics are explicit refusals of the run
        // with their own refusal code: they keep a refusal outcome and the
        // service's code here so the console, the session log and the CLI
        // never present them as a completed save. Completed runs project
        // their status outcome with the checkpoint they acted on.
        if (reply.Outcome != OwnerCommandOutcome.Completed ||
            reply.Payload.ValueKind != System.Text.Json.JsonValueKind.Object ||
            !reply.Payload.TryGetProperty("status", out var status) ||
            status.ValueKind != System.Text.Json.JsonValueKind.String)
        {
            return new OwnerSourceIngestResult(reply.Outcome, reply.ErrorCode, null);
        }

        var statusText = status.GetString() ?? string.Empty;
        if (statusText is "ingest_unsupported_format" or "ingest_unsupported_record")
        {
            return new OwnerSourceIngestResult(OwnerCommandOutcome.Refused, statusText, null);
        }

        System.Text.Json.JsonElement? journal = null;
        if (reply.Payload.TryGetProperty("sourceIngestion", out var journalElement) &&
            journalElement.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            journal = journalElement;
        }

        OwnerSourceEnrollment? enrollment = null;
        if (reply.Payload.TryGetProperty("sourceEnrollment", out var enrollmentElement) &&
            enrollmentElement.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            enrollment = ProjectSourceEnrollmentElement(enrollmentElement, journal);
        }

        return new OwnerSourceIngestResult(reply.Outcome, null, new OwnerSourceIngestOutcome(
            status.GetString() ?? string.Empty,
            journal is { } journalValue ? Int64(journalValue, "consumedOffset") : 0,
            0,
            journal is { } journalValue2 ? Int32(journalValue2, "diagnosticCount") : 0,
            journal is { } journalValue3 && journalValue3.TryGetProperty("diagnosticCode", out var diagnostic) && diagnostic.ValueKind == System.Text.Json.JsonValueKind.String
                ? diagnostic.GetString() : null,
            enrollment));
    }

    private static OwnerSourceRefreshEntry[]? ProjectSourceRefresh(OwnerReply reply)
    {
        if (reply.Outcome != OwnerCommandOutcome.Completed ||
            reply.Payload.ValueKind != System.Text.Json.JsonValueKind.Object ||
            !reply.Payload.TryGetProperty("sourceRefresh", out var entries) ||
            entries.ValueKind != System.Text.Json.JsonValueKind.Array)
        {
            return null;
        }

        var projected = new List<OwnerSourceRefreshEntry>();
        foreach (var element in entries.EnumerateArray())
        {
            if (element.ValueKind != System.Text.Json.JsonValueKind.Object ||
                !element.TryGetProperty("sourceId", out var id) || id.ValueKind != System.Text.Json.JsonValueKind.String)
            {
                continue;
            }

            projected.Add(new OwnerSourceRefreshEntry(
                id.GetString() ?? string.Empty,
                Text(element, "status") ?? "unknown",
                Int64(element, "consumedOffset"),
                Int32(element, "eventsAdded"),
                Int32(element, "diagnosticsAdded"),
                Text(element, "diagnosticCode")));
        }

        return projected.ToArray();
    }

    private static OwnerCollectionMapping[]? ProjectCollections(OwnerReply reply)
    {
        if (reply.Outcome != OwnerCommandOutcome.Completed ||
            reply.Payload.ValueKind != System.Text.Json.JsonValueKind.Object ||
            !reply.Payload.TryGetProperty("scopeCollections", out var collections) ||
            collections.ValueKind != System.Text.Json.JsonValueKind.Array)
        {
            return null;
        }

        var projected = new List<OwnerCollectionMapping>();
        foreach (var element in collections.EnumerateArray())
        {
            if (ProjectCollectionElement(element) is { } collection)
            {
                projected.Add(collection);
            }
        }

        return projected.ToArray();
    }

    private static OwnerCollectionMapping? ProjectCollection(OwnerReply reply) =>
        reply.Outcome == OwnerCommandOutcome.Completed &&
        reply.Payload.ValueKind == System.Text.Json.JsonValueKind.Object &&
        reply.Payload.TryGetProperty("scopeCollections", out var collections) &&
        collections.ValueKind == System.Text.Json.JsonValueKind.Array &&
        collections.GetArrayLength() == 1 &&
        collections[0].ValueKind == System.Text.Json.JsonValueKind.Object
            ? ProjectCollectionElement(collections[0])
            : null;

    private static OwnerCollectionMapping? ProjectCollectionElement(System.Text.Json.JsonElement collection)
    {
        if (!collection.TryGetProperty("collectionId", out var id) || id.ValueKind != System.Text.Json.JsonValueKind.String)
        {
            return null;
        }

        var members = collection.TryGetProperty("resourceIds", out var resourceIds) &&
            resourceIds.ValueKind == System.Text.Json.JsonValueKind.Array
                ? resourceIds.EnumerateArray()
                    .Where(member => member.ValueKind == System.Text.Json.JsonValueKind.String)
                    .Select(member => member.GetString()!)
                    .ToArray()
                : Array.Empty<string>();
        return new OwnerCollectionMapping(id.GetString() ?? string.Empty, members);
    }

    private static OwnerSourceSnapshotDescriptor[]? ProjectSnapshotDescriptors(OwnerReply reply)
    {
        if (reply.Outcome != OwnerCommandOutcome.Completed ||
            reply.Payload.ValueKind != System.Text.Json.JsonValueKind.Object ||
            !reply.Payload.TryGetProperty("sourceSnapshots", out var snapshots) ||
            snapshots.ValueKind != System.Text.Json.JsonValueKind.Array)
        {
            return null;
        }

        var projected = new List<OwnerSourceSnapshotDescriptor>();
        foreach (var element in snapshots.EnumerateArray())
        {
            if (element.ValueKind == System.Text.Json.JsonValueKind.Object &&
                element.TryGetProperty("id", out var id) && id.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                projected.Add(new OwnerSourceSnapshotDescriptor(
                    id.GetString() ?? string.Empty,
                    Text(element, "zoneId") ?? string.Empty,
                    Int64(element, "revision"),
                    Int64(element, "generation")));
            }
        }

        return projected.ToArray();
    }

    private static OwnerSourceSnapshotEntry? ProjectSnapshot(OwnerReply reply)
    {
        if (reply.Outcome != OwnerCommandOutcome.Completed ||
            reply.Payload.ValueKind != System.Text.Json.JsonValueKind.Object ||
            !reply.Payload.TryGetProperty("sourceSnapshot", out var snapshot) ||
            snapshot.ValueKind != System.Text.Json.JsonValueKind.Object)
        {
            return null;
        }

        if (!snapshot.TryGetProperty("id", out var id) || id.ValueKind != System.Text.Json.JsonValueKind.String)
        {
            return null;
        }

        var capturedAt = Timestamp(snapshot, "capturedAtUtc");
        var retainUntil = Timestamp(snapshot, "retainUntilUtc");
        if (capturedAt is null || retainUntil is null)
        {
            return null;
        }

        return new OwnerSourceSnapshotEntry(
            id.GetString() ?? string.Empty,
            Text(snapshot, "zoneId") ?? string.Empty,
            Int64(snapshot, "revision"),
            Text(snapshot, "sourceId") ?? string.Empty,
            Int64(snapshot, "sourceRevision"),
            Int64(snapshot, "sourceGeneration"),
            Int64(snapshot, "sourceLength"),
            Text(snapshot, "sourceDigest") ?? string.Empty,
            capturedAt.Value,
            retainUntil.Value,
            snapshot.TryGetProperty("purged", out var purged) && purged.ValueKind == System.Text.Json.JsonValueKind.True);
    }


    /// <summary>One item's live publication record.</summary>
    private static OwnerLifecycleState? ProjectLifecycle(OwnerReply reply)
    {
        if (reply.Outcome != OwnerCommandOutcome.Completed ||
            reply.Payload.ValueKind != JsonValueKind.Object ||
            !reply.Payload.TryGetProperty("lifecycle", out var lifecycle) ||
            lifecycle.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new OwnerLifecycleState(
            Text(lifecycle, "subjectResourceId") ?? string.Empty,
            Int64(lifecycle, "revision"),
            Text(lifecycle, "publication") ?? string.Empty);
    }

    private static OwnerTimelineDetail? ProjectTimeline(OwnerReply reply)
    {
        if (reply.Outcome != OwnerCommandOutcome.Completed ||
            reply.Payload.ValueKind != JsonValueKind.Object ||
            !reply.Payload.TryGetProperty("lifecycle", out var lifecycle) ||
            lifecycle.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new OwnerTimelineDetail(
            Text(lifecycle, "id") ?? string.Empty,
            Text(lifecycle, "zoneId") ?? string.Empty,
            Int64(lifecycle, "revision"),
            Text(lifecycle, "subjectResourceId") ?? string.Empty,
            Text(lifecycle, "subjectKind") ?? string.Empty,
            Text(lifecycle, "publication") ?? string.Empty,
            Text(lifecycle, "question"),
            Text(lifecycle, "intention"),
            Flag(lifecycle, "notified"),
            Timestamp(lifecycle, "notifiedAtUtc"),
            Flag(lifecycle, "acknowledged"),
            Timestamp(lifecycle, "acknowledgedAtUtc"),
            Text(lifecycle, "supersededBy"),
            ProjectStamp(lifecycle, "occurred"),
            ProjectStamp(lifecycle, "recorded"),
            ProjectStamp(lifecycle, "validFrom"),
            ProjectStamp(lifecycle, "validUntil"),
            ProjectStamp(lifecycle, "reviewAfter"),
            Text(lifecycle, "executor") ?? string.Empty,
            Text(lifecycle, "creationMethod") ?? string.Empty,
            Text(lifecycle, "reason"),
            Timestamp(lifecycle, "recordedAtUtc") ?? DateTimeOffset.MinValue,
            lifecycle.TryGetProperty("history", out var history) && history.ValueKind == JsonValueKind.Array
                ? history.EnumerateArray().Select(ProjectTimelineChange).ToArray()
                : Array.Empty<OwnerTimelineChange>(),
            lifecycle.TryGetProperty("retainedRevisions", out var retained) && retained.ValueKind == JsonValueKind.Array
                ? retained.EnumerateArray().Select(ProjectTimelineRetained).ToArray()
                : Array.Empty<OwnerTimelineRetained>());
    }

    private static OwnerTimelineStamp? ProjectStamp(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var stamp) || stamp.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new OwnerTimelineStamp(
            Text(stamp, "precision") ?? "unknown",
            Timestamp(stamp, "instantUtc"),
            OptionalInt64(stamp, "localYear") is { } year ? (int)year : null,
            OptionalInt64(stamp, "localMonth") is { } month ? (int)month : null,
            OptionalInt64(stamp, "localDay") is { } day ? (int)day : null,
            OptionalInt64(stamp, "localQuarter") is { } quarter ? (int)quarter : null,
            OptionalInt64(stamp, "timeZoneOffsetMinutes") is { } zone ? (int)zone : null);
    }

    private static OwnerTimelineChange ProjectTimelineChange(JsonElement change) => new(
        Text(change, "aspect") ?? string.Empty,
        Text(change, "act") ?? string.Empty,
        Text(change, "fromState") ?? string.Empty,
        Text(change, "toState") ?? string.Empty,
        Text(change, "executor") ?? string.Empty,
        Timestamp(change, "recordedAtUtc") ?? DateTimeOffset.MinValue,
        Text(change, "rationale"),
        Text(change, "replacementResourceId"));

    private static OwnerTimelineRetained ProjectTimelineRetained(JsonElement entry) => new(
        OptionalInt64(entry, "subjectRevision") ?? 0,
        Text(entry, "archiveResourceId") ?? string.Empty,
        Text(entry, "executor") ?? string.Empty,
        Timestamp(entry, "recordedAtUtc") ?? DateTimeOffset.MinValue);

    private static OwnerRetainedRevisionDetail? ProjectRetainedRevision(OwnerReply reply)
    {
        if (reply.Outcome != OwnerCommandOutcome.Completed ||
            reply.Payload.ValueKind != JsonValueKind.Object ||
            !reply.Payload.TryGetProperty("retainedRevision", out var retained) ||
            retained.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var subject = retained.TryGetProperty("subject", out var subjectElement) && subjectElement.ValueKind == JsonValueKind.Object
            ? subjectElement
            : (JsonElement?)null;
        var sources = subject is { } subjectValue &&
            subjectValue.TryGetProperty("sources", out var sourceElements) && sourceElements.ValueKind == JsonValueKind.Array
            ? sourceElements.EnumerateArray().Select(element => new OwnerRetainedRevisionSource(
                Text(element, "sourceId"),
                Text(element, "locator"),
                Text(element, "availability") ?? "unknown",
                OptionalInt64(element, "sourceRevision"))).ToArray()
            : Array.Empty<OwnerRetainedRevisionSource>();

        return new OwnerRetainedRevisionDetail(
            subject is { } detail ? Text(detail, "id") ?? string.Empty : string.Empty,
            subject is { } detailZone ? Text(detailZone, "zoneId") ?? string.Empty : string.Empty,
            subject is { } detailRevision ? Int64(detailRevision, "revision") : 0,
            Int64(retained, "archiveRevision"),
            Text(retained, "archiveResourceId") ?? string.Empty,
            subject is { } detailKind ? Text(detailKind, "kind") ?? string.Empty : string.Empty,
            subject is { } detailTitle ? Text(detailTitle, "title") : null,
            subject is { } detailBody ? Text(detailBody, "body") ?? string.Empty : string.Empty,
            subject is { } detailAuthor ? Text(detailAuthor, "author") ?? string.Empty : string.Empty,
            subject is { } detailExecutor ? Text(detailExecutor, "executor") ?? string.Empty : string.Empty,
            subject is { } detailMethod ? Text(detailMethod, "creationMethod") ?? string.Empty : string.Empty,
            subject is { } detailEvidence ? Text(detailEvidence, "evidence") ?? string.Empty : string.Empty,
            subject is { } detailLevel ? Text(detailLevel, "level") ?? string.Empty : string.Empty,
            subject is { } detailOccurred ? Timestamp(detailOccurred, "occurredAtUtc") : null,
            subject is { } detailRecorded ? Timestamp(detailRecorded, "recordedAtUtc") ?? DateTimeOffset.MinValue : DateTimeOffset.MinValue,
            sources);
    }

    private static long? SpanStart(JsonElement element) =>
        element.TryGetProperty("span", out var span) && span.ValueKind == JsonValueKind.Object
            ? OptionalInt64(span, "start")
            : null;

    private static long? SpanEnd(JsonElement element) =>
        element.TryGetProperty("span", out var span) && span.ValueKind == JsonValueKind.Object
            ? OptionalInt64(span, "end")
            : null;

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int Int32(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : 0;

    private static long Int64(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt64()
            : 0;

    private static long? OptionalInt64(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt64()
            : null;

    private static double Number(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : 0;

    private static decimal DecimalValue(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDecimal()
            : 0m;

    private static bool Flag(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;

    private static DateTimeOffset? Timestamp(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String &&
        DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;

    /// <summary>
    /// Codes this client fabricates when there was no usable owner-channel
    /// answer. Every other error code came back from a running service that
    /// understood the request and declined it.
    /// </summary>
    private static bool IsLocalTransportCode(string? errorCode) => errorCode is
        "malformed_reply" or "unexpected_reply" or "transport_unavailable" or "transport_denied" or "transport_timeout";

    private static OwnerVaultState MapVaultState(string? reported) => reported switch
    {
        "uninitialized" => OwnerVaultState.Uninitialized,
        "locked" => OwnerVaultState.Locked,
        "locking" => OwnerVaultState.Locking,
        "unlocked" => OwnerVaultState.Unlocked,
        _ => OwnerVaultState.Unknown
    };

    private static bool HasText(JsonElement reply, string property, string expected) =>
        reply.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        string.Equals(value.GetString(), expected, StringComparison.Ordinal);
}
