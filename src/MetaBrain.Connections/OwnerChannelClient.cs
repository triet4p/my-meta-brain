using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MetaBrain.Application;

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
    int Limit = 10);

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
    string Publication);

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
    OwnerLibraryHit[] Hits);

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
/// One pending proposal as the owner reviews it: its zone, kind, pinned
/// subject, approved inbox destination, idempotency identity, payload hash,
/// executor and recorded time, the agent's confirmation claim (a claim only,
/// never verified by this surface) and the proposed item draft exactly as the
/// agent wrote it.
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
    OwnerReviewDraft Item);

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
                SearchToUtc: query.ToUtc),
            "library_page",
            cancellationToken).ConfigureAwait(false);

        return new OwnerLibrarySearchResult(reply.Outcome, reply.ErrorCode, ProjectLibraryPage(reply));
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

    private static async Task<OwnerReply> ReadReplyAsync(
        string controlPipe,
        ServiceRequest request,
        string expectedStatus,
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

            if (!HasText(reply, "status", expectedStatus))
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
            hits);
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
        Text(hit, "publication") ?? string.Empty);

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
                version));
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
