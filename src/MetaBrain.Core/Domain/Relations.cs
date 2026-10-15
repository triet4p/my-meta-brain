using System.Text.Json;
using System.Text.Json.Serialization;

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
/// Which kind of endpoint a relation records. Endpoints are never raw managed
/// resources: a relation links memory items and registered sources only, and
/// the kind is verified against the decoded endpoint at write time.
/// </summary>
public enum RelationEndpointKind
{
    Unknown = 0,
    MemoryItem = 1,
    RegisteredSource = 2
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
/// Which endpoint of a relation carries the evidence for the other. related_to
/// and contradicts are identity assertions with no evidence side; every other
/// kind cites one endpoint as the basis of the other.
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
/// revision the link was made against, and an optional evidence span. The
/// revision is a truthful historical record: only the live revision is retained
/// by the vault, so a pinned revision cannot be re-read as current content.
/// </summary>
public sealed record RelationEndpoint(
    string ResourceId,
    RelationEndpointKind EndpointKind,
    long Revision,
    SourceSpan? Span = null);

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
    string? RequestEvidence = null);

/// <summary>
/// Client-supplied lifecycle transition draft. Only the rationale for the new
/// state travels here; endpoints, kind, and the original reason are immutable
/// after creation, so a link is never silently re-pointed or re-justified.
/// </summary>
public sealed record RelationTransitionDraft(string? StateReason, string? RequestEvidence = null);

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
    RelationRequestEvidence[]? RequestEvidence = null);

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
    RelationRequestEvidence[]? RequestEvidence = null);

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
    }

    public static void ValidateTransition(RelationTransitionDraft transition, RelationState current, RelationState target)
    {
        ArgumentNullException.ThrowIfNull(transition);

        // Relink deliberately carries no rationale: the restored state is a
        // proposal, and the original link rationale stays the record's basis.
        if (target == RelationState.Proposed && transition.StateReason is not null)
        {
            throw new ArgumentException("A relink transition carries no rationale; the restored state is proposed.");
        }

        var required = target is RelationState.Rejected or RelationState.Retracted;
        ValidateStateReason(transition.StateReason, required);

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

    private static void ValidateEndpoints(RelationEndpoint? from, RelationEndpoint? to)
    {
        if (from is null || to is null)
        {
            throw new ArgumentException("A relation requires exactly two endpoints.");
        }

        // Endpoints are memory items or registered sources: never a relation
        // (reserved ID prefix) and never each other ("hai endpoint").
        ValidateEndpoint(from);
        ValidateEndpoint(to);
        if (string.Equals(from.ResourceId, to.ResourceId, StringComparison.Ordinal))
        {
            throw new ArgumentException("A relation requires two distinct endpoints.");
        }
    }

    private static void ValidateEndpoint(RelationEndpoint endpoint)
    {
        if (!DomainIdentifiers.IsDomainIdentifier(endpoint.ResourceId))
        {
            throw new ArgumentException("A relation endpoint must be an opaque memory item or registered source identifier.");
        }

        if (!Enum.IsDefined(endpoint.EndpointKind) || endpoint.EndpointKind == RelationEndpointKind.Unknown)
        {
            throw new ArgumentException("A relation endpoint must name a known endpoint kind.");
        }

        if (endpoint.Revision < 1)
        {
            throw new ArgumentException("A relation endpoint must pin a positive revision.");
        }

        if (endpoint.Span is null)
        {
            return;
        }

        if (endpoint.EndpointKind != RelationEndpointKind.RegisteredSource)
        {
            throw new ArgumentException("A relation span must anchor to a registered source endpoint.");
        }

        var span = endpoint.Span;
        if (span.Start < 0 || span.End <= span.Start ||
            span.End > MemoryEnvelopeCodec.MaximumSpanEnd ||
            span.End - span.Start > MemoryEnvelopeCodec.MaximumSpanLength)
        {
            throw new ArgumentException("A relation span must be a non-empty bounded range.");
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
/// Contract-grounded relation semantics shared by the service, the cycle
/// guard, and the review evaluation. derived_from and supersedes form one
/// derivation DAG: a cycle in their union (pure or mixed) is rejected because
/// it asserts an item derives from, or replaces, something that already
/// depends on it. related_to is explicitly allowed to carry cycles.
/// </summary>
public static class RelationSemantics
{
    /// <summary>
    /// Evidence links keep the exact revision (and optional span) they used, so
    /// a change to the cited endpoint marks the link for review. related_to and
    /// contradicts assert identity, not a revision of record.
    /// </summary>
    public static bool PinsRevision(RelationKind kind) => kind
        is RelationKind.DerivedFrom
        or RelationKind.Supports
        or RelationKind.Supersedes
        or RelationKind.AppliesTo
        or RelationKind.Addresses;

    /// <summary>
    /// Kinds whose union must stay acyclic.
    /// </summary>
    public static bool ConstrainsCycles(RelationKind kind) => kind
        is RelationKind.DerivedFrom
        or RelationKind.Supersedes;

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
