using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MetaBrain.Core.Domain;

/// <summary>
/// S4-T5 agent proposal kind. A proposal is a pending owner-review write: the
/// agent proposes that a memory item be created, or that an existing item be
/// revised at one exact frozen revision. It is never a write to the item
/// itself and never a publication: recording a proposal mutates nothing but
/// the proposal's own canonical record, and only an owner act can turn it
/// into canonical content.
/// </summary>
public enum ProposalKind
{
    Unknown = 0,
    Create = 1,
    Revise = 2
}

/// <summary>
/// Agent-supplied proposal draft. Identity (the proposal ID) is derived
/// server-side from the approved inbox destination plus the caller's
/// idempotency key, the executor comes from the trusted
/// <see cref="Security.AuthenticatedContext"/>, and the recorded time is
/// server-stamped: a body can never name the identity, the executor, or the
/// time of the record it writes. The item payload is an ordinary memory
/// draft (R10/R12 apply at validation time); the optional user-confirmation
/// claim is recorded as a claim only and never upgrades evidence.
/// </summary>
public sealed record ProposalDraft(
    ProposalKind? Kind,
    string? IdempotencyKey,
    string? DestinationResourceId,
    long? DestinationRevision,
    string? SubjectResourceId = null,
    long? SubjectRevision = null,
    MemoryItemDraft? Item = null,
    bool? UserConfirmed = null);

/// <summary>
/// Server-persisted proposal (encrypted bytes). Revision lives in the vault
/// descriptor, not duplicated here; readers join it via ListResources. The
/// payload hash pins the exact semantic content the first write of this
/// idempotency key carried, so a retry can be told apart from a reuse of the
/// same key for different content.
/// </summary>
public sealed record StoredProposal(
    int SchemaVersion,
    string Id,
    string ZoneId,
    ProposalKind Kind,
    string DestinationResourceId,
    long DestinationRevision,
    string? SubjectResourceId,
    long? SubjectRevision,
    string IdempotencyKey,
    string PayloadHash,
    MemoryItemDraft Item,
    ExecutorKind Executor,
    DateTimeOffset RecordedAtUtc,
    bool ClaimedUserConfirmed);

/// <summary>
/// Owner-facing read model: stored proposal plus the live descriptor
/// revision. The pinned payload hash travels with it so a reader can tell
/// exactly which semantic content the record committed to.
/// </summary>
public sealed record Proposal(
    string Id,
    string ZoneId,
    long Revision,
    int SchemaVersion,
    ProposalKind Kind,
    string DestinationResourceId,
    long DestinationRevision,
    string? SubjectResourceId,
    long? SubjectRevision,
    string IdempotencyKey,
    string PayloadHash,
    MemoryItemDraft Item,
    ExecutorKind Executor,
    DateTimeOffset RecordedAtUtc,
    bool ClaimedUserConfirmed);

/// <summary>
/// Lean inbox identity for owner listing: ID, live revision, and owning zone
/// from the encrypted manifest only — never the proposed body.
/// </summary>
public sealed record ProposalIdentity(string Id, long Revision, string ZoneId);

/// <summary>
/// Reasons a pending proposal needs review before it can still be trusted as
/// recorded. Several can combine on one record. The flags name why the cited
/// evidence moved — directly, or deeper in a cited memory item's own pinned
/// lineage — never whether the proposed content is true.
/// </summary>
[Flags]
public enum ProposalReviewReason
{
    None = 0,
    CitedSourceMissing = 1,
    CitedSourceRevisionChanged = 2,
    CitedSourceUnavailable = 4,
    CitedSourceLineageChanged = 8
}

/// <summary>
/// Computed read-time review state of one served proposal: whether the pending
/// output still matches the evidence it cited. This is a truthful evaluation
/// over live state; it never rewrites the proposal, the cited source, or an
/// owner's conclusion, and it never upgrades or downgrades declared evidence.
/// </summary>
public sealed record ProposalReview(
    bool NeedsReview,
    ProposalReviewReason Reasons,
    string[] StaleSourceIds);

public static class ProposalCodec
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumPayloadBytes = 1024 * 1024;
    public const int MaximumIdempotencyKeyLength = 128;
    public const int PayloadHashLength = 64;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static ProposalDraft DecodeDraft(byte[] json)
    {
        ArgumentNullException.ThrowIfNull(json);
        ProposalDraft? draft;
        try
        {
            draft = JsonSerializer.Deserialize<ProposalDraft>(StripUtf8Preamble(json), JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("The proposal draft schema is invalid.", ex);
        }

        if (draft is null)
        {
            throw new ArgumentException("The proposal draft schema is invalid.");
        }

        return draft;
    }

    public static byte[] EncodeStored(StoredProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ProposalValidator.ValidateStored(proposal);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(proposal, JsonOptions);
        if (bytes.Length is <= 0 or > MaximumPayloadBytes)
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw new ArgumentException("The proposal record exceeds its storage limit.");
        }

        return bytes;
    }

    public static StoredProposal DecodeStored(byte[] json)
    {
        ArgumentNullException.ThrowIfNull(json);
        StoredProposal? proposal;
        try
        {
            proposal = JsonSerializer.Deserialize<StoredProposal>(StripUtf8Preamble(json), JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("The proposal schema is invalid.", ex);
        }

        if (proposal is null)
        {
            throw new ArgumentException("The proposal schema is invalid.");
        }

        ProposalValidator.ValidateStored(proposal);
        return proposal;
    }

    internal static JsonSerializerOptions Options => JsonOptions;

    private static ReadOnlySpan<byte> StripUtf8Preamble(byte[] json) =>
        json.Length >= 3 && json[0] == 0xEF && json[1] == 0xBB && json[2] == 0xBF
            ? new ReadOnlySpan<byte>(json, 3, json.Length - 3)
            : json;
}

public static class ProposalValidator
{
    /// <summary>
    /// Validates one agent proposal draft: the kind, the caller idempotency
    /// key, the inbox destination, the revise subject coherence, and the
    /// proposed item content through the one shared memory-draft content
    /// validator. R12: belief-grade evidence is a user assertion, so an
    /// agent proposal can never carry user-confirmed evidence, whatever the
    /// body claims.
    /// </summary>
    public static void ValidateDraft(ProposalDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (draft.Kind is null || draft.Kind == ProposalKind.Unknown ||
            !Enum.IsDefined(draft.Kind.Value))
        {
            throw new ArgumentException("A known proposal kind (create or revise) is required.");
        }

        ValidateIdempotencyKey(draft.IdempotencyKey);

        if (string.IsNullOrEmpty(draft.DestinationResourceId) ||
            !DomainIdentifiers.IsDomainIdentifier(draft.DestinationResourceId))
        {
            throw new ArgumentException("The proposal inbox destination must be an opaque safe identifier.");
        }

        if (draft.DestinationRevision is not > 0)
        {
            throw new ArgumentException("The proposal inbox destination revision is required.");
        }

        if (draft.Kind == ProposalKind.Revise)
        {
            if (string.IsNullOrEmpty(draft.SubjectResourceId) ||
                !DomainIdentifiers.IsDomainIdentifier(draft.SubjectResourceId))
            {
                throw new ArgumentException("A revise proposal names the opaque item it would revise.");
            }

            if (draft.SubjectRevision is not > 0)
            {
                throw new ArgumentException("A revise proposal pins the exact subject revision it replaces.");
            }
        }
        else if (draft.SubjectResourceId is not null || draft.SubjectRevision is not null)
        {
            throw new ArgumentException("A create proposal carries no subject item or revision.");
        }

        if (draft.Item is null)
        {
            throw new ArgumentException("A proposal carries the proposed item content.");
        }

        MemoryItemValidator.ValidateProposedItem(draft.Item);

        // A body cannot grant confirmation: belief-grade evidence is refused
        // on the agent path even with a user speaker, and the claim flag is
        // only ever recorded as a claim (see StoredProposal.ClaimedUserConfirmed).
        if (draft.Item.Evidence == EvidenceLevel.UserConfirmed)
        {
            throw new ArgumentException("An agent proposal cannot assert user-confirmed evidence.");
        }
    }

    public static void ValidateStored(StoredProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        if (proposal.SchemaVersion != ProposalCodec.CurrentSchemaVersion)
        {
            throw new ArgumentException("The proposal schema version is unsupported.");
        }

        if (!DomainIdentifiers.IsProposalId(proposal.Id))
        {
            throw new ArgumentException("The proposal ID must be an opaque safe proposal identifier.");
        }

        if (!DomainIdentifiers.IsSafeIdentifier(proposal.ZoneId))
        {
            throw new ArgumentException("The proposal zone must be an opaque safe identifier.");
        }

        if (proposal.Kind is not (ProposalKind.Create or ProposalKind.Revise))
        {
            throw new ArgumentException("A stored proposal is a create or revise proposal.");
        }

        if (string.IsNullOrEmpty(proposal.DestinationResourceId) ||
            !DomainIdentifiers.IsDomainIdentifier(proposal.DestinationResourceId) ||
            proposal.DestinationRevision is not > 0)
        {
            throw new ArgumentException("The stored proposal names its approved inbox destination.");
        }

        if (proposal.Kind == ProposalKind.Revise)
        {
            if (string.IsNullOrEmpty(proposal.SubjectResourceId) ||
                !DomainIdentifiers.IsDomainIdentifier(proposal.SubjectResourceId) ||
                proposal.SubjectRevision is not > 0)
            {
                throw new ArgumentException("A stored revise proposal names the item and revision it would revise.");
            }
        }
        else if (proposal.SubjectResourceId is not null || proposal.SubjectRevision is not null)
        {
            throw new ArgumentException("A stored create proposal carries no subject item or revision.");
        }

        ValidateIdempotencyKey(proposal.IdempotencyKey);

        if (string.IsNullOrEmpty(proposal.PayloadHash) ||
            proposal.PayloadHash.Length != ProposalCodec.PayloadHashLength ||
            proposal.PayloadHash.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("The stored proposal carries its content hash.");
        }

        if (proposal.Item is null)
        {
            throw new ArgumentException("A stored proposal carries the proposed item content.");
        }

        MemoryItemValidator.ValidateProposedItem(proposal.Item);
        if (proposal.Item.Evidence == EvidenceLevel.UserConfirmed)
        {
            throw new ArgumentException("A stored agent proposal never carries user-confirmed evidence.");
        }

        if (!Enum.IsDefined(proposal.Executor) || proposal.Executor == ExecutorKind.Unknown)
        {
            throw new ArgumentException("A known executor is required.");
        }

        if (proposal.RecordedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Recorded time must use UTC.");
        }
    }

    /// <summary>
    /// One deterministic proposal ID per (owner-approved grant, inbox
    /// destination, idempotency key): re-deriving the same operation after a
    /// retry, a session re-redeem, or a service restart resolves to the same
    /// record, so a repeated operation cannot create a second proposal. The
    /// grant is part of the identity on purpose: two distinct grants are two
    /// distinct owner authorizations, so a key chosen under one grant can
    /// neither collide with nor read back a proposal written under another,
    /// and a replay only ever resolves inside the authorization unit that
    /// first wrote it.
    /// </summary>
    public static string DeriveProposalId(string grantId, string destinationResourceId, string idempotencyKey)
    {
        if (string.IsNullOrEmpty(grantId) || !DomainIdentifiers.IsSafeIdentifier(grantId))
        {
            throw new ArgumentException("The approved grant identity must be an opaque safe identifier.");
        }

        if (string.IsNullOrEmpty(destinationResourceId) || !DomainIdentifiers.IsDomainIdentifier(destinationResourceId))
        {
            throw new ArgumentException("The proposal inbox destination must be an opaque safe identifier.");
        }

        ValidateIdempotencyKey(idempotencyKey);
        var material = DomainIdentifiers.ProposalIdPrefix + grantId + "|" + destinationResourceId + "|" + idempotencyKey;
        return DomainIdentifiers.ProposalIdPrefix +
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
    }

    /// <summary>
    /// SHA-256 over the canonical semantic content of a stored proposal
    /// (everything except the hash field itself and the server-stamped
    /// recorded times — the record's and the proposed item's — which are
    /// properties of the first write rather than of the operation). A retry
    /// with identical content reproduces the hash; a reuse of the same
    /// idempotency key with different content does not.
    /// </summary>
    public static string ComputePayloadHash(StoredProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        var payload = proposal with
        {
            PayloadHash = string.Empty,
            RecordedAtUtc = default,
            Item = proposal.Item is { } item ? item! with { RecordedAtUtc = default } : proposal.Item
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, ProposalCodec.Options);
        try
        {
            return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static void ValidateIdempotencyKey(string? key)
    {
        if (string.IsNullOrEmpty(key) || key.Length > ProposalCodec.MaximumIdempotencyKeyLength ||
            !DomainIdentifiers.IsSafeIdentifier(key))
        {
            throw new ArgumentException("The proposal idempotency key must be an opaque safe identifier (1-128 characters).");
        }
    }
}
