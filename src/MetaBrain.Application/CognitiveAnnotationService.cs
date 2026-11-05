using System.Security.Cryptography;
using MetaBrain.Core.Domain;
using MetaBrain.Core.Security;

namespace MetaBrain.Application;

/// <summary>A revision-gated owner annotation update lost an optimistic race.</summary>
public sealed class CognitiveAnnotationConflictException : Exception
{
    public CognitiveAnnotationConflictException()
        : base("The cognitive annotation revision changed before the update committed.")
    {
    }
}

/// <summary>
/// S14-T2 owner annotation persistence over the existing encrypted canonical
/// resource store. Each annotation has its own managed-resource revision and
/// append-only history, references an existing workspace anchor, and never
/// copies source bytes or writes derived index state.
/// </summary>
public sealed class CognitiveAnnotationService
{
    private const string WorkspaceZoneId = "workspace";
    private readonly TimeProvider _timeProvider;

    public CognitiveAnnotationService(TimeProvider? timeProvider = null) =>
        _timeProvider = timeProvider ?? TimeProvider.System;

    public CognitiveAnnotation Create(
        AuthenticatedContext context,
        string fileId,
        byte[] draftJson,
        IVaultOperation store)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(draftJson);
        var draft = CognitiveAnnotationCodec.DecodeDraft(draftJson);
        return Create(context, fileId, draft, store, ExecutorOf(context));
    }

    internal CognitiveAnnotation CreateModelSuggestion(
        AuthenticatedContext context,
        string fileId,
        string anchorId,
        CognitiveFacet[] facets,
        IVaultOperation store)
    {
        ArgumentNullException.ThrowIfNull(facets);
        var draft = new CognitiveAnnotationDraft(
            anchorId,
            facets,
            Kind: null,
            Author: null,
            CreationMethod: null,
            Evidence: null,
            Level: null,
            Suggestion: true);
        CognitiveAnnotationValidator.ValidateDraft(draft);
        return Create(context, fileId, draft, store, ExecutorKind.StructuringJob);
    }

    private CognitiveAnnotation Create(
        AuthenticatedContext context,
        string fileId,
        CognitiveAnnotationDraft draft,
        IVaultOperation store,
        ExecutorKind executor)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(store);
        EnsureOwner(context);
        if (!DomainIdentifiers.IsDomainIdentifier(fileId))
        {
            throw new ArgumentException("A cognitive annotation names an existing workspace file.");
        }

        CognitiveAnnotationValidator.ValidateDraft(draft);
        var anchor = RequireLiveAnchor(store, fileId, draft.AnchorId!);
        var facets = CognitiveAnnotationValidator.NormalizeFacets(draft.Facets);
        var origin = draft.Suggestion == true
            ? CognitiveAnnotationOrigin.AiSuggestion
            : CognitiveAnnotationOrigin.Owner;
        var reviewStatus = origin == CognitiveAnnotationOrigin.AiSuggestion
            ? CognitiveAnnotationReviewStatus.Pending
            : CognitiveAnnotationReviewStatus.Accepted;
        var now = _timeProvider.GetUtcNow().ToUniversalTime();
        var id = CognitiveAnnotationId();
        var initial = new StoredCognitiveAnnotation(
            CognitiveAnnotationCodec.CurrentSchemaVersion,
            id,
            WorkspaceZoneId,
            anchor,
            facets,
            draft.Kind ?? MemoryKind.Unknown,
            draft.Author ?? SpeakerRole.Unknown,
            executor,
            draft.CreationMethod ?? CreationMethod.Unknown,
            draft.Evidence ?? EvidenceLevel.Unknown,
            draft.Level ?? AbstractionLevel.Unknown,
            origin,
            reviewStatus,
            now,
            Array.Empty<CognitiveAnnotationVersion>());
        var version = new CognitiveAnnotationVersion(
            1,
            CognitiveAnnotationAction.Created,
            CognitiveAnnotationReviewStatus.Unknown,
            CognitiveAnnotationCodec.SnapshotOf(initial),
            executor,
            now,
            draft.Reason);
        var stored = initial with { History = new[] { version } };
        var payload = CognitiveAnnotationCodec.EncodeStored(stored);
        long revision;
        try
        {
            revision = store.WriteContent(id, WorkspaceZoneId, payload, expectedRevision: 0);
        }
        catch (ManagedResourceUnavailableException ex)
        {
            throw new ArgumentException("The cognitive annotation could not be created in the encrypted vault.", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }

        return ToReadModel(stored, revision, store);
    }

    public CognitiveAnnotationIdentity[] List(AuthenticatedContext context, IVaultOperation store)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(store);
        EnsureOwner(context);
        return store.ListResources()
            .Where(resource => DomainIdentifiers.IsCognitiveAnnotationId(resource.ResourceId))
            .Select(resource => new CognitiveAnnotationIdentity(resource.ResourceId, resource.Revision, resource.ZoneId))
            .OrderBy(annotation => annotation.Id, StringComparer.Ordinal)
            .ToArray();
    }

    public CognitiveAnnotation? Read(
        AuthenticatedContext context,
        string annotationId,
        long? expectedRevision,
        IVaultOperation store)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(store);
        EnsureOwner(context);
        if (string.IsNullOrEmpty(annotationId) || !DomainIdentifiers.IsCognitiveAnnotationId(annotationId))
        {
            return null;
        }

        var descriptor = FindDescriptor(store, annotationId);
        if (descriptor is null)
        {
            return null;
        }

        var revision = expectedRevision ?? descriptor.Revision;
        var stored = ReadStoredAtRevision(store, annotationId, revision);
        return ToReadModel(stored, revision, store);
    }

    public CognitiveAnnotation Correct(
        AuthenticatedContext context,
        string annotationId,
        long expectedRevision,
        byte[] correctionJson,
        IVaultOperation store)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(correctionJson);
        ArgumentNullException.ThrowIfNull(store);
        EnsureOwner(context);
        var current = ReadStoredAtRevision(store, annotationId, expectedRevision);
        var draft = CognitiveAnnotationCodec.DecodeCorrectionDraft(correctionJson);
        var updated = current with
        {
            Facets = draft.Facets is null
                ? current.Facets
                : CognitiveAnnotationValidator.NormalizeFacets(draft.Facets),
            Kind = draft.Kind ?? current.Kind,
            Author = draft.Author ?? current.Author,
            CreationMethod = draft.CreationMethod ?? current.CreationMethod,
            Evidence = draft.Evidence ?? current.Evidence,
            Level = draft.Level ?? current.Level
        };
        var nextRevision = NextRevision(expectedRevision);
        updated = AppendVersion(
            updated,
            nextRevision,
            CognitiveAnnotationAction.Corrected,
            current.ReviewStatus,
            ExecutorOf(context),
            _timeProvider.GetUtcNow().ToUniversalTime(),
            draft.Reason);
        return WriteUpdated(updated, expectedRevision, store);
    }

    public CognitiveAnnotation Review(
        AuthenticatedContext context,
        string annotationId,
        long expectedRevision,
        bool accept,
        byte[]? reviewJson,
        IVaultOperation store)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(store);
        EnsureOwner(context);
        var current = ReadStoredAtRevision(store, annotationId, expectedRevision);
        if (current.ReviewStatus != CognitiveAnnotationReviewStatus.Pending)
        {
            throw new ArgumentException("Only a pending cognitive suggestion can be reviewed.");
        }

        var draft = CognitiveAnnotationCodec.DecodeReviewDraft(reviewJson);
        if (!accept)
        {
            CognitiveAnnotationValidator.ValidateReason(draft.Reason, required: true);
        }

        var nextStatus = accept
            ? CognitiveAnnotationReviewStatus.Accepted
            : CognitiveAnnotationReviewStatus.Rejected;
        var updated = current with { ReviewStatus = nextStatus };
        var nextRevision = NextRevision(expectedRevision);
        updated = AppendVersion(
            updated,
            nextRevision,
            accept ? CognitiveAnnotationAction.Accepted : CognitiveAnnotationAction.Rejected,
            current.ReviewStatus,
            ExecutorOf(context),
            _timeProvider.GetUtcNow().ToUniversalTime(),
            draft.Reason);
        return WriteUpdated(updated, expectedRevision, store);
    }

    private CognitiveAnnotation WriteUpdated(
        StoredCognitiveAnnotation updated,
        long expectedRevision,
        IVaultOperation store)
    {
        var payload = CognitiveAnnotationCodec.EncodeStored(updated);
        try
        {
            var revision = store.WriteContent(updated.Id, updated.ZoneId, payload, expectedRevision);
            return ToReadModel(updated, revision, store);
        }
        catch (ManagedResourceUnavailableException)
        {
            if (FindDescriptor(store, updated.Id) is { } live && live.Revision != expectedRevision)
            {
                throw new CognitiveAnnotationConflictException();
            }

            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    private static StoredCognitiveAnnotation ReadStoredAtRevision(
        IVaultOperation store,
        string annotationId,
        long expectedRevision)
    {
        if (expectedRevision < 1 || string.IsNullOrEmpty(annotationId) ||
            !DomainIdentifiers.IsCognitiveAnnotationId(annotationId))
        {
            throw new ArgumentException("The cognitive annotation ID and positive revision are required.");
        }

        var descriptor = FindDescriptor(store, annotationId);
        if (descriptor is null)
        {
            throw new ArgumentException("The cognitive annotation is unavailable.");
        }

        if (descriptor.Revision != expectedRevision)
        {
            throw new CognitiveAnnotationConflictException();
        }

        byte[]? raw = null;
        try
        {
            raw = store.ReadContent(annotationId, expectedRevision);
            var stored = CognitiveAnnotationCodec.DecodeStored(raw);
            if (!string.Equals(stored.Id, annotationId, StringComparison.Ordinal) ||
                !string.Equals(stored.ZoneId, descriptor.ZoneId, StringComparison.Ordinal) ||
                stored.History[^1].Revision != expectedRevision)
            {
                throw new InvalidDataException("The cognitive annotation identity does not match its encrypted manifest revision.");
            }

            return stored;
        }
        catch (ManagedResourceUnavailableException)
        {
            if (FindDescriptor(store, annotationId) is { } live && live.Revision != expectedRevision)
            {
                throw new CognitiveAnnotationConflictException();
            }

            throw;
        }
        finally
        {
            if (raw is { Length: > 0 })
            {
                CryptographicOperations.ZeroMemory(raw);
            }
        }
    }

    private static CognitiveAnnotation ToReadModel(
        StoredCognitiveAnnotation stored,
        long revision,
        IVaultOperation store)
    {
        var (anchor, availability) = ResolveAnchorState(stored.Anchor, store);
        return new CognitiveAnnotation(
            stored.Id,
            stored.ZoneId,
            revision,
            stored.SchemaVersion,
            anchor,
            stored.Facets,
            stored.Kind,
            stored.Author,
            stored.Executor,
            stored.CreationMethod,
            stored.Evidence,
            stored.Level,
            stored.Origin,
            stored.ReviewStatus,
            stored.RecordedAtUtc,
            stored.History,
            availability);
    }

    private static (WorkspaceAnchorInfo Anchor, SourceAvailability Availability) ResolveAnchorState(
        WorkspaceAnchorInfo pinned,
        IVaultOperation store)
    {
        WorkspaceAnchorInfo? current;
        try
        {
            current = store.ReadWorkspaceAnchors(pinned.FileId)
                .FirstOrDefault(anchor => string.Equals(anchor.AnchorId, pinned.AnchorId, StringComparison.Ordinal));
        }
        catch (WorkspaceRefusalException ex) when (
            ex.Code is WorkspaceErrorCodes.NotFound or WorkspaceErrorCodes.Deleted)
        {
            return (pinned, SourceAvailability.Unavailable);
        }

        if (current is null)
        {
            return (pinned, SourceAvailability.Unavailable);
        }

        if (!string.Equals(current.FileId, pinned.FileId, StringComparison.Ordinal) ||
            current.ContentRevision != pinned.ContentRevision ||
            current.Offset != pinned.Offset ||
            current.Length != pinned.Length ||
            !string.Equals(current.Sha256, pinned.Sha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The workspace anchor identity changed after annotation creation.");
        }

        return (current, SourceAvailability.Available);
    }

    private static WorkspaceAnchorInfo RequireLiveAnchor(IVaultOperation store, string fileId, string anchorId)
    {
        WorkspaceAnchorInfo? anchor;
        try
        {
            anchor = store.ReadWorkspaceAnchors(fileId)
                .FirstOrDefault(candidate => string.Equals(candidate.AnchorId, anchorId, StringComparison.Ordinal));
        }
        catch (WorkspaceRefusalException ex) when (
            ex.Code is WorkspaceErrorCodes.NotFound or WorkspaceErrorCodes.Deleted)
        {
            throw new ArgumentException("The selected workspace anchor is unavailable.", ex);
        }

        if (anchor is null || !string.Equals(anchor.State, WorkspaceAnchorStates.Live, StringComparison.Ordinal))
        {
            throw new ArgumentException("A cognitive annotation requires an existing live workspace anchor.");
        }

        CognitiveAnnotationValidator.ValidateAnchor(anchor, requireLive: true);
        return anchor;
    }

    private static StoredCognitiveAnnotation AppendVersion(
        StoredCognitiveAnnotation updated,
        long revision,
        CognitiveAnnotationAction action,
        CognitiveAnnotationReviewStatus previousStatus,
        ExecutorKind actor,
        DateTimeOffset now,
        string? reason)
    {
        if (updated.History.Length >= CognitiveAnnotationValidator.MaximumHistoryEntries)
        {
            throw new ArgumentException("The cognitive annotation history is full.");
        }

        var history = new CognitiveAnnotationVersion[updated.History.Length + 1];
        Array.Copy(updated.History, history, updated.History.Length);
        history[^1] = new CognitiveAnnotationVersion(
            revision,
            action,
            previousStatus,
            CognitiveAnnotationCodec.SnapshotOf(updated),
            actor,
            now,
            reason);
        return updated with { History = history };
    }

    private static long NextRevision(long revision)
    {
        if (revision == long.MaxValue)
        {
            throw new ArgumentException("The cognitive annotation revision limit has been reached.");
        }

        return revision + 1;
    }

    private static ScopeResourceRevision? FindDescriptor(IVaultOperation store, string resourceId) =>
        store.ListResources().FirstOrDefault(resource =>
            string.Equals(resource.ResourceId, resourceId, StringComparison.Ordinal));

    private static string CognitiveAnnotationId() =>
        DomainIdentifiers.CognitiveAnnotationIdPrefix +
        Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    private static ExecutorKind ExecutorOf(AuthenticatedContext context) => context.Kind switch
    {
        PrincipalKind.Owner => ExecutorKind.Owner,
        PrincipalKind.Agent => ExecutorKind.Agent,
        _ => ExecutorKind.Unknown
    };

    private static void EnsureOwner(AuthenticatedContext context)
    {
        if (context.Kind != PrincipalKind.Owner)
        {
            throw new ArgumentException("Cognitive annotations are owner-only canonical state.");
        }
    }
}
