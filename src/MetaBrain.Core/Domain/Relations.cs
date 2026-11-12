using System.Text.Json;
using System.Text.Json.Serialization;
using MetaBrain.Core.Security;

namespace MetaBrain.Core.Domain;

/// <summary>
/// S2-T4 typed relation kinds from the specification's initial relation set.
/// Embedding similarity is only a hint signal for <see cref="RelatedTo"/> and is
/// never inferred into an evidence or causal link here: every link is an
/// explicit, typed, attributable record.
/// </summary>
public enum RelationKind
{
    Unknown = 0,
    RelatedTo = 1,
    DerivedFrom = 2,
    Supports = 3,
    Contradicts = 4,
    Supersedes = 5,
    AppliesTo = 6,
    Addresses = 7
}

/// <summary>
/// Which kind of endpoint a relation records. Managed endpoints are verified
/// against encrypted memory/source records; workspace file/folder/passage
/// endpoints are verified against the authenticated workspace state and pins.
/// </summary>
public enum RelationEndpointKind
{
    Unknown = 0,
    MemoryItem = 1,
    RegisteredSource = 2,
    WorkspaceFile = 3,
    WorkspaceFolder = 4,
    WorkspacePassage = 5
}

/// <summary>
/// Relation lifecycle. proposed/accepted/rejected is the review lifecycle from
/// the specification; retracted is the explicit, recorded unlink state, distinct
/// from a review rejection so an unlinked link can be relinked through
/// proposed again without resurrecting a rejected one. Accepting a link never
/// asserts that the linked proposition is true.
/// </summary>
public enum RelationState
{
    Unknown = 0,
    Proposed = 1,
    Accepted = 2,
    Rejected = 3,
    Retracted = 4
}
/// <summary>
/// Agent-reported relation request actions. These entries preserve the claim
/// that accompanied an operation; they do not attest owner intent.
/// </summary>
public enum RelationRequestAction
{
    Unknown = 0,
    Create = 1,
    Supersede = 2,
    Remove = 3,
    Restore = 4
}

public sealed record RelationRequestEvidence(
    RelationRequestAction Action,
    string Text,
    ExecutorKind Executor,
    DateTimeOffset RecordedAtUtc);

/// <summary>
/// Where an edge originated, independent of the trusted executor and method
/// that recorded it. Legacy values are never inferred from current state.
/// </summary>
public enum RelationOrigin
{
    Legacy = 0,
    Owner = 1,
    ImportedFileLink = 2,
    AiSuggestion = 3,
    AgentSuggestion = 4,
    Unknown = 255
}

/// <summary>The immutable act appended to a newly-created workspace edge.</summary>
public enum RelationHistoryAction
{
    Unknown = 0,
    Created = 1,
    Accepted = 2,
    Rejected = 3,
    Retracted = 4,
    Restored = 5
}

/// <summary>
/// One attributed edge act. ExpectedRelationRevision is the exact relation
/// revision the actor saw before committing this act (zero for creation).
/// </summary>
public sealed record RelationHistoryAct(
    RelationHistoryAction Action,
    RelationState State,
    ExecutorKind Actor,
    CreationMethod Method,
    string Reason,
    DateTimeOffset RecordedAtUtc,
    long ExpectedRelationRevision);

/// <summary>
/// Additional immutable context for a workspace selector: file and passage
/// endpoints pin a digest; folder endpoints retain the tree revision observed
/// when the stable folder identity was selected.
/// </summary>
public sealed record WorkspaceRelationPin(string? Sha256 = null, long? TreeRevision = null);

/// <summary>
/// One evidence side of a managed relation. In the legacy managed contract,
/// related_to and contradicts are identity-only and have no single evidence
/// side. Workspace contradicts is bilateral pinned evidence and is checked on
/// both endpoints, rather than being represented as one side here.
/// </summary>
public enum RelationEvidenceSide
{
    None = 0,
    From = 1,
    To = 2
}

/// <summary>
/// Read-time review state of a relation: whether the link can still be trusted
/// as recorded. This is a computed, truthful evaluation over live state; it
/// never rewrites the stored link or the owner's conclusions.
/// </summary>
public enum RelationReviewState
{
    Current = 0,
    NeedsReview = 1
}

/// <summary>
/// Reasons a relation needs review. Several can combine on one record.
/// </summary>
[Flags]
public enum RelationReviewReason
{
    None = 0,
    EndpointMissing = 1,
    EndpointRevisionChanged = 2,
    SourceUnavailable = 4,
    DependencyNeedsReview = 8
}

/// <summary>
/// One relation endpoint: an opaque resource ID, its verified kind, the exact
/// revision the link was made against, and an optional evidence span. Managed
/// endpoints pin the live resource revision; workspace file/passage endpoints
/// may pin retained content revisions, while folder revisions are creation context.
/// </summary>
public sealed record RelationEndpoint(
    string ResourceId,
    RelationEndpointKind EndpointKind,
    long Revision,
    SourceSpan? Span = null,
    WorkspaceRelationPin? WorkspacePin = null);

/// <summary>
/// Client-supplied relation draft: no identity, executor, schema, or revision.
/// Identity comes from trusted request params (or is minted for agent links),
/// executor from the trusted <see cref="Security.AuthenticatedContext"/>,
/// revision from the shared vault descriptor.
/// </summary>
public sealed record RelationDraft(
    RelationKind? Kind,
    RelationState? State,
    RelationEndpoint? From,
    RelationEndpoint? To,
    CreationMethod? CreationMethod,
    string? Reason,
    string? StateReason,
    DateTimeOffset? OccurredAtUtc,
    DateTimeOffset? RecordedAtUtc,
    string? RequestEvidence = null,
    RelationOrigin? Origin = null);

/// <summary>
/// Client-supplied lifecycle transition draft. Only the rationale for the new
/// state travels here; endpoints, kind, and the original reason are immutable
/// after creation, so a link is never silently re-pointed or re-justified.
/// </summary>
public sealed record RelationTransitionDraft(
    string? StateReason,
    string? RequestEvidence = null,
    string? ActReason = null);

/// <summary>
/// Server-persisted relation (encrypted bytes). Revision lives in the vault
/// descriptor, not duplicated here; readers join it via ListResources.
/// </summary>
public sealed record StoredRelation(
    int SchemaVersion,
    string Id,
    string ZoneId,
    RelationKind Kind,
    RelationState State,
    RelationEndpoint From,
    RelationEndpoint To,
    ExecutorKind Executor,
    CreationMethod CreationMethod,
    string Reason,
    string? StateReason,
    DateTimeOffset? OccurredAtUtc,
    DateTimeOffset RecordedAtUtc,
    ExecutorKind StateExecutor,
    DateTimeOffset StateRecordedAtUtc,
    RelationRequestEvidence[]? RequestEvidence = null,
    RelationOrigin Origin = RelationOrigin.Legacy,
    RelationHistoryAct[]? History = null);

/// <summary>
/// Owner-facing read model: stored relation plus the live descriptor revision.
/// </summary>
public sealed record Relation(
    string Id,
    string ZoneId,
    long Revision,
    int SchemaVersion,
    RelationKind Kind,
    RelationState State,
    RelationEndpoint From,
    RelationEndpoint To,
    ExecutorKind Executor,
    CreationMethod CreationMethod,
    string Reason,
    string? StateReason,
    DateTimeOffset? OccurredAtUtc,
    DateTimeOffset RecordedAtUtc,
    ExecutorKind StateExecutor,
    DateTimeOffset StateRecordedAtUtc,
    RelationRequestEvidence[]? RequestEvidence = null,
    RelationOrigin Origin = RelationOrigin.Legacy,
    RelationHistoryAct[]? History = null);

/// <summary>
/// Computed review state for a stored relation, evaluated at read time against
/// the live manifest and registry. Resource IDs named here are always endpoints
/// of the served relation, never chain members outside the caller's authority.
/// </summary>
public sealed record RelationReview(
    RelationReviewState State,
    RelationReviewReason Reasons,
    string[] StaleResourceIds);

public static class RelationCodec
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumReasonLength = 2000;
    public const int MaximumStateReasonLength = 2000;
    public const int MaximumRequestEvidenceLength = 2000;
    public const int MaximumRequestEvidenceEntries = 64;
    public const int MaximumHistoryEntries = 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static RelationDraft DecodeDraft(byte[] json)
    {
        ArgumentNullException.ThrowIfNull(json);
        RelationDraft? draft;
        try
        {
            draft = JsonSerializer.Deserialize<RelationDraft>(StripUtf8Preamble(json), JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("The relation draft schema is invalid.", ex);
        }

        if (draft is null)
        {
            throw new ArgumentException("The relation draft schema is invalid.");
        }

        return draft;
    }

    public static RelationTransitionDraft DecodeTransitionDraft(byte[]? json)
    {
        if (json is null || json.Length == 0)
        {
            return new RelationTransitionDraft(null);
        }

        RelationTransitionDraft? draft;
        try
        {
            draft = JsonSerializer.Deserialize<RelationTransitionDraft>(StripUtf8Preamble(json), JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("The relation transition schema is invalid.", ex);
        }

        return draft ?? new RelationTransitionDraft(null);
    }

    /// <summary>
    /// Encodes one client-supplied relation draft with exactly the schema
    /// <see cref="DecodeDraft"/> reads, so a caller (owner console or CLI) never
    /// hand-builds JSON that this codec would decode differently. Encoding is
    /// not validation: the shared validator still runs on the server, which is
    /// the only place a draft becomes a record.
    /// </summary>
    public static byte[] EncodeDraft(RelationDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return JsonSerializer.SerializeToUtf8Bytes(draft, JsonOptions);
    }

    /// <summary>
    /// Encodes one client-supplied lifecycle rationale with exactly the schema
    /// <see cref="DecodeTransitionDraft"/> reads. A null rationale is an
    /// explicit "no rationale"; the server still decides whether the target
    /// state requires one.
    /// </summary>
    public static byte[] EncodeTransitionDraft(RelationTransitionDraft transition)
    {
        ArgumentNullException.ThrowIfNull(transition);
        return JsonSerializer.SerializeToUtf8Bytes(transition, JsonOptions);
    }

    public static byte[] EncodeStored(StoredRelation relation)
    {
        ArgumentNullException.ThrowIfNull(relation);
        RelationValidator.ValidateStored(relation);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(relation, JsonOptions);
        if (bytes.Length is <= 0 or > 1024 * 1024)
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
            throw new ArgumentException("The relation exceeds its storage limit.");
        }

        return bytes;
    }

    public static StoredRelation DecodeStored(byte[] json)
    {
        ArgumentNullException.ThrowIfNull(json);
        StoredRelation? relation;
        try
        {
            relation = JsonSerializer.Deserialize<StoredRelation>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("The relation schema is invalid.", ex);
        }

        if (relation is null)
        {
            throw new ArgumentException("The relation schema is invalid.");
        }

        RelationValidator.ValidateStored(relation);
        return relation;
    }

    internal static JsonSerializerOptions Options => JsonOptions;

    private static ReadOnlySpan<byte> StripUtf8Preamble(byte[] json) =>
        json.Length >= 3 && json[0] == 0xEF && json[1] == 0xBB && json[2] == 0xBF
            ? new ReadOnlySpan<byte>(json, 3, json.Length - 3)
            : json;
}

public static class RelationValidator
{
    public static void ValidateDraft(RelationDraft draft, string relationId, string zoneId)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (!DomainIdentifiers.IsRelationId(relationId))
        {
            throw new ArgumentException("The relation ID must be an opaque safe relation identifier.");
        }

        if (!DomainIdentifiers.IsSafeIdentifier(zoneId))
        {
            throw new ArgumentException("The relation zone must be an opaque safe identifier.");
        }

        if (draft.Kind is null || draft.Kind == RelationKind.Unknown)
        {
            throw new ArgumentException("A known relation kind is required.");
        }

        // A link is created as an explicit proposal or an explicit acceptance;
        // a terminal state can never be created directly.
        if (draft.State is not (RelationState.Proposed or RelationState.Accepted))
        {
            throw new ArgumentException("A relation is created as proposed or accepted, never as rejected or retracted.");
        }

        if (draft.CreationMethod is null || draft.CreationMethod == CreationMethod.Unknown)
        {
            throw new ArgumentException("A known creation method is required.");
        }

        ValidateReason(draft.Reason);
        ValidateStateReason(draft.StateReason, required: false);
        ValidateEndpoints(draft.From, draft.To);
        ValidateTimes(draft.OccurredAtUtc, draft.RecordedAtUtc);
        ValidateRequestEvidenceText(draft.RequestEvidence, required: false);
        if (draft.Origin is { } origin &&
            (!Enum.IsDefined(origin) || origin is RelationOrigin.Legacy or RelationOrigin.Unknown))
        {
            throw new ArgumentException("A new relation origin must be known and explicit.");
        }
    }

    public static void ValidateTransition(
        RelationTransitionDraft transition,
        RelationState current,
        RelationState target,
        bool requireActReason = false)
    {
        ArgumentNullException.ThrowIfNull(transition);

        // Relink deliberately carries no state rationale: it returns to a
        // proposal, while workspace history records the explicit act reason
        // separately in ActReason.
        if (target == RelationState.Proposed && transition.StateReason is not null)
        {
            throw new ArgumentException("A relink transition carries no state rationale; the restored state is proposed.");
        }

        var required = target is RelationState.Rejected or RelationState.Retracted;
        ValidateStateReason(transition.StateReason, required);
        if (requireActReason)
        {
            ValidateReason(transition.ActReason);
        }

        if (!IsTransitionAllowed(current, target))
        {
            throw new ArgumentException("The relation lifecycle transition is not allowed.");
        }
    }

    public static bool IsTransitionAllowed(RelationState current, RelationState target) => target switch
    {
        RelationState.Accepted => current == RelationState.Proposed,
        RelationState.Rejected => current is RelationState.Proposed or RelationState.Accepted,
        RelationState.Retracted => current is RelationState.Proposed or RelationState.Accepted,
        RelationState.Proposed => current == RelationState.Retracted,
        _ => false
    };

    public static void ValidateStored(StoredRelation relation)
    {
        ArgumentNullException.ThrowIfNull(relation);
        if (relation.SchemaVersion != RelationCodec.CurrentSchemaVersion)
        {
            throw new ArgumentException("The relation schema version is unsupported.");
        }

        if (!DomainIdentifiers.IsRelationId(relation.Id))
        {
            throw new ArgumentException("The relation ID must be an opaque safe relation identifier.");
        }

        if (!DomainIdentifiers.IsSafeIdentifier(relation.ZoneId))
        {
            throw new ArgumentException("The relation zone must be an opaque safe identifier.");
        }

        if (!Enum.IsDefined(relation.Kind) || relation.Kind == RelationKind.Unknown)
        {
            throw new ArgumentException("A known relation kind is required.");
        }

        if (!Enum.IsDefined(relation.State) || relation.State == RelationState.Unknown)
        {
            throw new ArgumentException("A known relation state is required.");
        }

        if (!Enum.IsDefined(relation.Executor) || relation.Executor == ExecutorKind.Unknown)
        {
            throw new ArgumentException("A known executor is required.");
        }

        if (!Enum.IsDefined(relation.StateExecutor) || relation.StateExecutor == ExecutorKind.Unknown)
        {
            throw new ArgumentException("A known state executor is required.");
        }

        if (!Enum.IsDefined(relation.CreationMethod) || relation.CreationMethod == CreationMethod.Unknown)
        {
            throw new ArgumentException("A known creation method is required.");
        }

        ValidateReason(relation.Reason);
        ValidateStateReason(relation.StateReason, required: false);
        ValidateEndpoints(relation.From, relation.To);
        ValidateTimes(relation.OccurredAtUtc, relation.RecordedAtUtc);
        if (!Enum.IsDefined(relation.Origin))
        {
            throw new ArgumentException("A relation origin is invalid.");
        }

        var hasWorkspaceEndpoint =
            RelationEndpointSemantics.IsWorkspace(relation.From.EndpointKind) ||
            RelationEndpointSemantics.IsWorkspace(relation.To.EndpointKind);
        if (hasWorkspaceEndpoint && relation.Origin == RelationOrigin.Legacy)
        {
            throw new ArgumentException("A workspace relation requires an explicit origin.");
        }

        if (relation.Origin == RelationOrigin.AgentSuggestion && hasWorkspaceEndpoint)
        {
            throw new ArgumentException("Workspace relation sharing is not enabled for agent-created links.");
        }

        if (relation.Origin == RelationOrigin.ImportedFileLink &&
            relation.From.EndpointKind is not (RelationEndpointKind.WorkspaceFile or RelationEndpointKind.WorkspacePassage) &&
            relation.To.EndpointKind is not (RelationEndpointKind.WorkspaceFile or RelationEndpointKind.WorkspacePassage))
        {
            throw new ArgumentException("An imported-file relation must name a workspace file or passage.");
        }


        ValidateHistory(relation, hasWorkspaceEndpoint);
        ValidateRequestEvidence(relation.RequestEvidence, relation.RecordedAtUtc);

        if (relation.RecordedAtUtc.Offset != TimeSpan.Zero || relation.StateRecordedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Relation times must use UTC.");
        }

        if (relation.StateRecordedAtUtc < relation.RecordedAtUtc)
        {
            throw new ArgumentException("A relation state cannot precede its creation.");
        }

        if (relation.StateRecordedAtUtc > DateTimeOffset.UtcNow.AddMinutes(5))
        {
            throw new ArgumentException("A relation state time cannot be in the future.");
        }
    }
    private static void ValidateHistory(StoredRelation relation, bool hasWorkspaceEndpoint)
    {
        var history = relation.History;
        if (history is null)
        {
            if (hasWorkspaceEndpoint)
            {
                throw new ArgumentException("A workspace relation must preserve its attributed history.");
            }

            return;
        }

        if (history.Length is 0 or > RelationCodec.MaximumHistoryEntries)
        {
            throw new ArgumentException("A relation history is empty or exceeds its event limit.");
        }

        RelationState? previousState = null;
        DateTimeOffset previousTime = DateTimeOffset.MinValue;
        for (var index = 0; index < history.Length; index++)
        {
            var act = history[index];
            if (act is null ||
                !Enum.IsDefined(act.Action) || act.Action == RelationHistoryAction.Unknown ||
                !Enum.IsDefined(act.State) || act.State is not (RelationState.Proposed or RelationState.Accepted or RelationState.Rejected or RelationState.Retracted) ||
                !Enum.IsDefined(act.Actor) || act.Actor == ExecutorKind.Unknown ||
                !Enum.IsDefined(act.Method) || act.Method == CreationMethod.Unknown ||
                act.ExpectedRelationRevision != index ||
                act.RecordedAtUtc.Offset != TimeSpan.Zero ||
                act.RecordedAtUtc < previousTime ||
                act.RecordedAtUtc > DateTimeOffset.UtcNow.AddMinutes(5))
            {
                throw new ArgumentException("A relation history act has invalid attribution, revision, state or time.");
            }

            ValidateReason(act.Reason);
            if (index == 0)
            {
                if (act.Action != RelationHistoryAction.Created || act.ExpectedRelationRevision != 0 ||
                    act.Actor != relation.Executor || act.Method != relation.CreationMethod ||
                    act.RecordedAtUtc != relation.RecordedAtUtc ||
                    !string.Equals(act.Reason, relation.Reason, StringComparison.Ordinal) ||
                    (relation.Origin is RelationOrigin.ImportedFileLink or RelationOrigin.AiSuggestion &&
                        act.State != RelationState.Proposed))
                {
                    throw new ArgumentException("A relation creation history act does not match its canonical record.");
                }
            }
            else if (previousState is not { } prior ||
                !RelationValidator.IsTransitionAllowed(prior, act.State) ||
                !HistoryActionMatchesState(act.Action, act.State))
            {
                throw new ArgumentException("A relation history transition is not a valid lifecycle act.");
            }

            previousState = act.State;
            previousTime = act.RecordedAtUtc;
        }

        var last = history[^1];
        if (last.State != relation.State || last.Actor != relation.StateExecutor ||
            last.RecordedAtUtc != relation.StateRecordedAtUtc)
        {
            throw new ArgumentException("The latest relation history act does not match its current state attribution.");
        }
    }

    private static bool HistoryActionMatchesState(RelationHistoryAction action, RelationState state) =>
        (action, state) is
            (RelationHistoryAction.Accepted, RelationState.Accepted) or
            (RelationHistoryAction.Rejected, RelationState.Rejected) or
            (RelationHistoryAction.Retracted, RelationState.Retracted) or
            (RelationHistoryAction.Restored, RelationState.Proposed);


    private static void ValidateEndpoints(RelationEndpoint? from, RelationEndpoint? to)
    {
        if (from is null || to is null)
        {
            throw new ArgumentException("A relation requires exactly two endpoints.");
        }

        ValidateEndpoint(from);
        ValidateEndpoint(to);
        if (RelationEndpointSemantics.SameLogicalEndpoint(from, to))
        {
            throw new ArgumentException("A relation requires two distinct logical endpoints.");
        }
    }

    private static void ValidateEndpoint(RelationEndpoint endpoint)
    {
        if (!DomainIdentifiers.IsDomainIdentifier(endpoint.ResourceId))
        {
            throw new ArgumentException("A relation endpoint must name an opaque domain or workspace identifier.");
        }

        if (!Enum.IsDefined(endpoint.EndpointKind) || endpoint.EndpointKind == RelationEndpointKind.Unknown)
        {
            throw new ArgumentException("A relation endpoint must name a known endpoint kind.");
        }

        if (endpoint.Revision < 1)
        {
            throw new ArgumentException("A relation endpoint must pin a positive revision.");
        }

        switch (endpoint.EndpointKind)
        {
            case RelationEndpointKind.MemoryItem:
                if (endpoint.Span is not null || endpoint.WorkspacePin is not null)
                {
                    throw new ArgumentException("A memory-item endpoint cannot carry a source span or workspace pin.");
                }

                return;
            case RelationEndpointKind.RegisteredSource:
                if (endpoint.WorkspacePin is not null)
                {
                    throw new ArgumentException("A registered-source endpoint cannot carry a workspace pin.");
                }

                ValidateSpan(endpoint.Span);
                return;
            case RelationEndpointKind.WorkspaceFile:
                if (endpoint.Span is not null || endpoint.WorkspacePin?.TreeRevision is not null)
                {
                    throw new ArgumentException("A workspace file endpoint pins its full-file digest without a span or tree revision.");
                }

                ValidateWorkspaceDigest(endpoint.WorkspacePin?.Sha256);
                return;
            case RelationEndpointKind.WorkspacePassage:
                ValidateWorkspaceSpan(endpoint.Span);
                if (endpoint.Span is null || endpoint.WorkspacePin?.TreeRevision is not null)
                {
                    throw new ArgumentException("A workspace passage endpoint requires a byte span and exact-content digest.");
                }

                ValidateWorkspaceDigest(endpoint.WorkspacePin?.Sha256);
                return;
            case RelationEndpointKind.WorkspaceFolder:
                if (endpoint.Span is not null || endpoint.WorkspacePin?.Sha256 is not null ||
                    endpoint.WorkspacePin?.TreeRevision is not > 0)
                {
                    throw new ArgumentException("A workspace folder endpoint requires its observed tree revision only.");
                }

                return;
            default:
                throw new ArgumentException("A relation endpoint kind is unsupported.");
        }
    }

    private static void ValidateSpan(SourceSpan? span)
    {
        if (span is null)
        {
            return;
        }

        if (span.Start < 0 || span.End <= span.Start ||
            span.End > MemoryEnvelopeCodec.MaximumSpanEnd ||
            span.End - span.Start > MemoryEnvelopeCodec.MaximumSpanLength)
        {
            throw new ArgumentException("A relation span must be a non-empty bounded range.");
        }
    }

    private static void ValidateWorkspaceSpan(SourceSpan? span)
    {
        if (span is null || span.Start < 0 || span.End <= span.Start ||
            span.End > WorkspaceContentRevisionInfo.MaximumContentBytes ||
            span.End - span.Start > WorkspaceContentRevisionInfo.MaximumContentBytes)
        {
            throw new ArgumentException("A workspace passage range must be non-empty and within the workspace content limit.");
        }
    }

    private static void ValidateWorkspaceDigest(string? sha256)
    {
        if (sha256 is not { Length: 64 } ||
            sha256.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException("A workspace relation pin requires a lowercase SHA-256 digest.");
        }
    }

    private static void ValidateReason(string? reason)
    {
        if (string.IsNullOrEmpty(reason) || reason!.Length > RelationCodec.MaximumReasonLength ||
            reason.Any(char.IsControl))
        {
            throw new ArgumentException("The relation reason must be 1-2000 characters without control characters.");
        }
    }

    private static void ValidateStateReason(string? stateReason, bool required)
    {
        if (stateReason is null)
        {
            if (required)
            {
                throw new ArgumentException("This relation lifecycle transition requires an explicit rationale.");
            }

            return;
        }

        if (stateReason.Length is 0 or > RelationCodec.MaximumStateReasonLength || stateReason.Any(char.IsControl))
        {
            throw new ArgumentException("The relation state rationale must be 1-2000 characters without control characters.");
        }
    }

    private static void ValidateTimes(DateTimeOffset? occurredAtUtc, DateTimeOffset? recordedAtUtc)
    {
        if (recordedAtUtc is null)
        {
            throw new ArgumentException("A recorded time in UTC is required.");
        }

        if (recordedAtUtc.Value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Recorded time must use UTC.");
        }

        if (recordedAtUtc.Value > DateTimeOffset.UtcNow.AddMinutes(5))
        {
            throw new ArgumentException("Recorded time cannot be in the future.");
        }

        if (occurredAtUtc is not null)
        {
            if (occurredAtUtc.Value.Offset != TimeSpan.Zero)
            {
                throw new ArgumentException("Occurred time must use UTC.");
            }

            if (occurredAtUtc.Value > recordedAtUtc.Value)
            {
                throw new ArgumentException("Occurred time cannot follow recorded time.");
            }
        }
    }
    private static void ValidateRequestEvidenceText(string? text, bool required)
    {
        if (text is null)
        {
            if (required)
            {
                throw new ArgumentException("An agent relation action requires the caller-reported request text.");
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(text) || text.Length > RelationCodec.MaximumRequestEvidenceLength ||
            text.Any(character => character == '\0' ||
                (char.IsControl(character) && character is not '\r' and not '\n' and not '\t')))
        {
            throw new ArgumentException("Relation request evidence must be non-empty, bounded text.");
        }
    }

    private static void ValidateRequestEvidence(
        RelationRequestEvidence[]? evidence,
        DateTimeOffset relationRecordedAtUtc)
    {
        if (evidence is null)
        {
            return;
        }

        if (evidence.Length > RelationCodec.MaximumRequestEvidenceEntries)
        {
            throw new ArgumentException("A relation carries too many request-evidence entries.");
        }

        var previousRecordedAtUtc = relationRecordedAtUtc;
        foreach (var entry in evidence)
        {
            if (entry is null ||
                !Enum.IsDefined(entry.Action) || entry.Action == RelationRequestAction.Unknown ||
                !Enum.IsDefined(entry.Executor) || entry.Executor == ExecutorKind.Unknown)
            {
                throw new ArgumentException("A relation request-evidence entry requires a known action and executor.");
            }

            ValidateRequestEvidenceText(entry.Text, required: true);
            if (entry.RecordedAtUtc.Offset != TimeSpan.Zero ||
                entry.RecordedAtUtc < previousRecordedAtUtc ||
                entry.RecordedAtUtc > DateTimeOffset.UtcNow.AddMinutes(5))
            {
                throw new ArgumentException("Relation request-evidence times must be ordered UTC values.");
            }

            previousRecordedAtUtc = entry.RecordedAtUtc;
        }
    }
}

/// <summary>
/// The derivation graph refuses a link (or a lifecycle transition that would
/// reactivate one) because adding it would make an item derive from, or
/// replace, something that already depends on it. It is a distinct refusal so
/// an owner-facing caller can say which rule rejected the act instead of
/// reporting an undifferentiated invalid request; the derivation graph itself
/// is still decided only here, never by a caller.
/// </summary>
public sealed class RelationCycleException : ArgumentException
{
    public RelationCycleException()
        : base("A derivation relation would create a cycle.")
    {
    }

    public RelationCycleException(string message)
        : base(message)
    {
    }

    public RelationCycleException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Contract-grounded relation semantics shared by the service, the cycle
/// guard, and the review evaluation. derived_from and supersedes form one
/// derivation DAG: a cycle in their union (pure or mixed) is rejected because
/// it asserts an item derives from, or replaces, something that already
/// depends on it. related_to is explicitly allowed to carry cycles.
/// </summary>
public static class RelationSemantics
{
    /// <summary>
    /// Legacy managed-memory/source semantics: evidence kinds pin the live
    /// evidence revision; related_to and contradicts remain identity-only.
    /// Workspace content assertions use PinsWorkspaceRevision instead, so the
    /// D107 bilateral contradicts pin does not change legacy behavior.
    /// </summary>
    public static bool PinsRevision(RelationKind kind) => kind
        is RelationKind.DerivedFrom
        or RelationKind.Supports
        or RelationKind.Supersedes
        or RelationKind.AppliesTo
        or RelationKind.Addresses;

    /// <summary>
    /// D107 workspace file/passage assertions pin both content endpoints for
    /// every semantic kind except identity-only related_to. Contradicts is
    /// bilateral evidence here, not a one-sided managed EvidenceSide.
    /// </summary>
    public static bool PinsWorkspaceRevision(RelationKind kind) => kind
        is RelationKind.DerivedFrom
        or RelationKind.Supports
        or RelationKind.Contradicts
        or RelationKind.Supersedes
        or RelationKind.AppliesTo
        or RelationKind.Addresses;

    /// <summary>
    /// Kinds whose union must stay acyclic.
    /// </summary>
    public static bool ConstrainsCycles(RelationKind kind) => kind
        is RelationKind.DerivedFrom
        or RelationKind.Supersedes;

    /// <summary>
    /// The evidence side for managed edges. Workspace contradicts is bilateral
    /// pinned evidence, so its workspace review path checks both endpoints
    /// rather than assigning a singular side here.
    /// </summary>
    public static RelationEvidenceSide EvidenceSide(RelationKind kind) => kind switch
    {
        RelationKind.DerivedFrom or RelationKind.Supports or RelationKind.Supersedes => RelationEvidenceSide.To,
        RelationKind.AppliesTo or RelationKind.Addresses => RelationEvidenceSide.From,
        _ => RelationEvidenceSide.None
    };

    public static RelationEvidenceSide DependentSide(RelationKind kind) => kind switch
    {
        RelationKind.DerivedFrom or RelationKind.Supports or RelationKind.Supersedes => RelationEvidenceSide.From,
        RelationKind.AppliesTo or RelationKind.Addresses => RelationEvidenceSide.To,
        _ => RelationEvidenceSide.None
    };
}

/// <summary>
/// Review identity includes the workspace content selector; constrained-cycle
/// identity is separately the stable logical resource, independent of revision
/// or passage span.
/// </summary>
public static class RelationEndpointSemantics
{
    public static bool IsWorkspace(RelationEndpointKind kind) =>
        kind is RelationEndpointKind.WorkspaceFile or
            RelationEndpointKind.WorkspaceFolder or
            RelationEndpointKind.WorkspacePassage;

    public static bool HasWorkspaceEndpoint(StoredRelation relation) =>
        IsWorkspace(relation.From.EndpointKind) || IsWorkspace(relation.To.EndpointKind);

    public static bool SameLogicalEndpoint(RelationEndpoint left, RelationEndpoint right)
    {
        if (!string.Equals(left.ResourceId, right.ResourceId, StringComparison.Ordinal))
        {
            return false;
        }

        var leftWorkspace = IsWorkspace(left.EndpointKind);
        var rightWorkspace = IsWorkspace(right.EndpointKind);
        if (!leftWorkspace && !rightWorkspace)
        {
            return true;
        }

        return leftWorkspace && rightWorkspace && IdentityOf(left) == IdentityOf(right);
    }

    public static RelationEndpointIdentity IdentityOf(RelationEndpoint endpoint)
    {
        if (endpoint.EndpointKind == RelationEndpointKind.WorkspacePassage)
        {
            return new RelationEndpointIdentity(
                endpoint.ResourceId,
                endpoint.EndpointKind,
                endpoint.Revision,
                endpoint.Span!.Start,
                endpoint.Span.End,
                endpoint.WorkspacePin!.Sha256);
        }

        return IsWorkspace(endpoint.EndpointKind)
            ? new RelationEndpointIdentity(endpoint.ResourceId, endpoint.EndpointKind, 0, 0, 0, null)
            : new RelationEndpointIdentity(endpoint.ResourceId, RelationEndpointKind.Unknown, 0, 0, 0, null);
    }

    /// <summary>
    /// Stable graph node identity for derived_from/supersedes. Workspace
    /// passages are evidence selectors on their stable file node: differing
    /// revisions or spans do not create distinct DAG nodes.
    /// </summary>
    public static RelationEndpointIdentity CycleIdentityOf(RelationEndpoint endpoint)
    {
        if (!IsWorkspace(endpoint.EndpointKind))
        {
            return new RelationEndpointIdentity(endpoint.ResourceId, RelationEndpointKind.Unknown, 0, 0, 0, null);
        }

        var nodeKind = endpoint.EndpointKind == RelationEndpointKind.WorkspaceFolder
            ? RelationEndpointKind.WorkspaceFolder
            : RelationEndpointKind.WorkspaceFile;
        return new RelationEndpointIdentity(endpoint.ResourceId, nodeKind, 0, 0, 0, null);
    }
}

public readonly record struct RelationEndpointIdentity(
    string ResourceId,
    RelationEndpointKind EndpointKind,
    long Revision,
    long Start,
    long End,
    string? Sha256);
