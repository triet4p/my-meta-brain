using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using MetaBrain.Core.Security;

namespace MetaBrain.Core.Domain;

public enum CognitiveAnnotationOrigin
{
    Unknown = 0,
    Owner = 1,
    AiSuggestion = 2
}

public enum CognitiveAnnotationReviewStatus
{
    Unknown = 0,
    Pending = 1,
    Accepted = 2,
    Rejected = 3
}

public enum CognitiveAnnotationAction
{
    Unknown = 0,
    Created = 1,
    Corrected = 2,
    Accepted = 3,
    Rejected = 4
}

/// <summary>
/// Owner-supplied annotation fields. Identity, zone, trusted executor, anchor
/// data, review status, schema version and record time are derived by the
/// service; absent classification axes stay explicitly unknown.
/// </summary>
public sealed record CognitiveAnnotationDraft(
    string? AnchorId,
    CognitiveFacet[]? Facets,
    MemoryKind? Kind,
    SpeakerRole? Author,
    CreationMethod? CreationMethod,
    EvidenceLevel? Evidence,
    AbstractionLevel? Level,
    bool? Suggestion = null,
    string? Reason = null);

/// <summary>
/// A partial owner correction. Null keeps an axis unchanged; an explicit
/// Unknown value clears it without inventing replacement metadata. The anchor,
/// origin and review state are not editable through this draft.
/// </summary>
public sealed record CognitiveAnnotationCorrectionDraft(
    CognitiveFacet[]? Facets,
    MemoryKind? Kind,
    SpeakerRole? Author,
    CreationMethod? CreationMethod,
    EvidenceLevel? Evidence,
    AbstractionLevel? Level,
    string? Reason = null);

public sealed record CognitiveAnnotationReviewDraft(string? Reason);

/// <summary>The immutable review/version state captured for one stored revision.</summary>
public sealed record CognitiveAnnotationSnapshot(
    CognitiveFacet[] Facets,
    MemoryKind Kind,
    SpeakerRole Author,
    ExecutorKind Executor,
    CreationMethod CreationMethod,
    EvidenceLevel Evidence,
    AbstractionLevel Level,
    CognitiveAnnotationOrigin Origin,
    CognitiveAnnotationReviewStatus ReviewStatus);

/// <summary>
/// One append-only state snapshot inside the encrypted annotation record. The
/// managed-resource descriptor remains the revision authority; this history
/// makes each owner's change and review decision inspectable after later edits.
/// </summary>
public sealed record CognitiveAnnotationVersion(
    long Revision,
    CognitiveAnnotationAction Action,
    CognitiveAnnotationReviewStatus PreviousReviewStatus,
    CognitiveAnnotationSnapshot State,
    ExecutorKind Actor,
    DateTimeOffset RecordedAtUtc,
    string? Reason);

/// <summary>
/// Canonical encrypted annotation. It references an existing, exact workspace
/// anchor and never copies the original file bytes. Unknown kind, author,
/// executor, creation method, evidence and abstraction level are preserved.
/// </summary>
public sealed record StoredCognitiveAnnotation(
    int SchemaVersion,
    string Id,
    string ZoneId,
    WorkspaceAnchorInfo Anchor,
    CognitiveFacet[] Facets,
    MemoryKind Kind,
    SpeakerRole Author,
    ExecutorKind Executor,
    CreationMethod CreationMethod,
    EvidenceLevel Evidence,
    AbstractionLevel Level,
    CognitiveAnnotationOrigin Origin,
    CognitiveAnnotationReviewStatus ReviewStatus,
    DateTimeOffset RecordedAtUtc,
    CognitiveAnnotationVersion[] History);

/// <summary>Owner read model joined with the live workspace anchor state.</summary>
public sealed record CognitiveAnnotation(
    string Id,
    string ZoneId,
    long Revision,
    int SchemaVersion,
    WorkspaceAnchorInfo Anchor,
    CognitiveFacet[] Facets,
    MemoryKind Kind,
    SpeakerRole Author,
    ExecutorKind Executor,
    CreationMethod CreationMethod,
    EvidenceLevel Evidence,
    AbstractionLevel Level,
    CognitiveAnnotationOrigin Origin,
    CognitiveAnnotationReviewStatus ReviewStatus,
    DateTimeOffset RecordedAtUtc,
    CognitiveAnnotationVersion[] History,
    SourceAvailability SourceAvailability);

/// <summary>Lean canonical identity for the owner review/list surface.</summary>
public sealed record CognitiveAnnotationIdentity(string Id, long Revision, string ZoneId);

public static class CognitiveAnnotationCodec
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumPayloadBytes = 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static CognitiveAnnotationDraft DecodeDraft(byte[] json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var draft = Deserialize<CognitiveAnnotationDraft>(json, "The cognitive annotation draft schema is invalid.");
        CognitiveAnnotationValidator.ValidateDraft(draft);
        return draft;
    }

    public static CognitiveAnnotationCorrectionDraft DecodeCorrectionDraft(byte[] json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var draft = Deserialize<CognitiveAnnotationCorrectionDraft>(json, "The cognitive annotation correction schema is invalid.");
        CognitiveAnnotationValidator.ValidateCorrectionDraft(draft);
        return draft;
    }

    public static CognitiveAnnotationReviewDraft DecodeReviewDraft(byte[]? json)
    {
        if (json is null || json.Length == 0)
        {
            return new CognitiveAnnotationReviewDraft(null);
        }

        var draft = Deserialize<CognitiveAnnotationReviewDraft>(json, "The cognitive annotation review schema is invalid.");
        CognitiveAnnotationValidator.ValidateReason(draft.Reason, required: false);
        return draft;
    }

    public static byte[] EncodeStored(StoredCognitiveAnnotation annotation)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        CognitiveAnnotationValidator.ValidateStored(annotation);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(annotation, JsonOptions);
        if (bytes.Length is <= 0 or > MaximumPayloadBytes)
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw new ArgumentException("The cognitive annotation exceeds its storage limit.");
        }

        return bytes;
    }

    public static StoredCognitiveAnnotation DecodeStored(byte[] json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var annotation = Deserialize<StoredCognitiveAnnotation>(json, "The cognitive annotation schema is invalid.");
        CognitiveAnnotationValidator.ValidateStored(annotation);
        return annotation;
    }

    internal static CognitiveAnnotationSnapshot SnapshotOf(StoredCognitiveAnnotation annotation) => new(
        annotation.Facets,
        annotation.Kind,
        annotation.Author,
        annotation.Executor,
        annotation.CreationMethod,
        annotation.Evidence,
        annotation.Level,
        annotation.Origin,
        annotation.ReviewStatus);

    internal static bool SnapshotsEqual(CognitiveAnnotationSnapshot left, CognitiveAnnotationSnapshot right) =>
        left.Kind == right.Kind &&
        left.Author == right.Author &&
        left.Executor == right.Executor &&
        left.CreationMethod == right.CreationMethod &&
        left.Evidence == right.Evidence &&
        left.Level == right.Level &&
        left.Origin == right.Origin &&
        left.ReviewStatus == right.ReviewStatus &&
        left.Facets.SequenceEqual(right.Facets);

    private static T Deserialize<T>(byte[] json, string message)
    {
        if (json.Length is <= 0 or > MaximumPayloadBytes)
        {
            throw new ArgumentException(message);
        }

        try
        {
            return JsonSerializer.Deserialize<T>(StripUtf8Preamble(json), JsonOptions)
                ?? throw new ArgumentException(message);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException(message, ex);
        }
    }

    private static ReadOnlySpan<byte> StripUtf8Preamble(byte[] json) =>
        json.Length >= 3 && json[0] == 0xEF && json[1] == 0xBB && json[2] == 0xBF
            ? new ReadOnlySpan<byte>(json, 3, json.Length - 3)
            : json;
}

public static class CognitiveAnnotationValidator
{
    public const int MaximumHistoryEntries = 256;
    public const int MaximumReasonLength = 2000;
    private const string WorkspaceZoneId = "workspace";

    public static void ValidateDraft(CognitiveAnnotationDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (string.IsNullOrEmpty(draft.AnchorId) || !DomainIdentifiers.IsSafeIdentifier(draft.AnchorId))
        {
            throw new ArgumentException("A cognitive annotation names one existing workspace anchor.");
        }

        ValidateFacets(draft.Facets ?? Array.Empty<CognitiveFacet>());
        ValidateOptionalEnum(draft.Kind);
        ValidateOptionalEnum(draft.Author);
        ValidateOptionalEnum(draft.CreationMethod);
        ValidateOptionalEnum(draft.Evidence);
        ValidateOptionalEnum(draft.Level);
        ValidateReason(draft.Reason, required: false);
    }

    public static void ValidateCorrectionDraft(CognitiveAnnotationCorrectionDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (draft.Facets is not null)
        {
            ValidateFacets(draft.Facets);
        }

        ValidateOptionalEnum(draft.Kind);
        ValidateOptionalEnum(draft.Author);
        ValidateOptionalEnum(draft.CreationMethod);
        ValidateOptionalEnum(draft.Evidence);
        ValidateOptionalEnum(draft.Level);
        ValidateReason(draft.Reason, required: false);
    }

    public static void ValidateReason(string? reason, bool required)
    {
        if (required && string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("Rejecting a cognitive suggestion requires a reason.");
        }

        if (reason is not null &&
            (reason.Length == 0 || reason.Length > MaximumReasonLength ||
             !string.Equals(reason, reason.Trim(), StringComparison.Ordinal) || reason.Any(char.IsControl)))
        {
            throw new ArgumentException("A cognitive annotation reason must be trimmed, non-empty and within its length limit.");
        }
    }

    public static void ValidateStored(StoredCognitiveAnnotation annotation)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        if (annotation.SchemaVersion != CognitiveAnnotationCodec.CurrentSchemaVersion ||
            !DomainIdentifiers.IsCognitiveAnnotationId(annotation.Id) ||
            !DomainIdentifiers.IsSafeIdentifier(annotation.ZoneId) ||
            !string.Equals(annotation.ZoneId, WorkspaceZoneId, StringComparison.Ordinal))
        {
            throw new ArgumentException("The cognitive annotation identity or schema is invalid.");
        }

        ValidateAnchor(annotation.Anchor, requireLive: true);
        ValidateFacets(annotation.Facets);
        ValidateRequiredEnum(annotation.Kind);
        ValidateRequiredEnum(annotation.Author);
        ValidateRequiredEnum(annotation.Executor);
        ValidateRequiredEnum(annotation.CreationMethod);
        ValidateRequiredEnum(annotation.Evidence);
        ValidateRequiredEnum(annotation.Level);
        ValidateRequiredEnum(annotation.Origin);
        ValidateRequiredEnum(annotation.ReviewStatus);
        ValidateRecordedAt(annotation.RecordedAtUtc);

        var history = annotation.History;
        if (history is null || history.Length is 0 or > MaximumHistoryEntries)
        {
            throw new ArgumentException("A cognitive annotation has a bounded non-empty revision history.");
        }

        var previousTime = DateTimeOffset.MinValue;
        CognitiveAnnotationSnapshot? previousState = null;
        for (var index = 0; index < history.Length; index++)
        {
            var version = history[index] ?? throw new ArgumentException("A cognitive annotation history entry is invalid.");
            if (version.Revision != index + 1 ||
                !Enum.IsDefined(version.Action) || version.Action == CognitiveAnnotationAction.Unknown ||
                !Enum.IsDefined(version.PreviousReviewStatus) ||
                !Enum.IsDefined(version.Actor) || version.Actor == ExecutorKind.Unknown ||
                version.RecordedAtUtc.Offset != TimeSpan.Zero ||
                version.RecordedAtUtc > DateTimeOffset.UtcNow.AddMinutes(5) ||
                version.RecordedAtUtc < previousTime)
            {
                throw new ArgumentException("A cognitive annotation history entry has invalid revision, actor or time.");
            }

            ValidateReason(version.Reason, required: version.Action == CognitiveAnnotationAction.Rejected);
            ValidateSnapshot(version.State);
            if (version.State.Origin != annotation.Origin)
            {
                throw new ArgumentException("A cognitive annotation's origin cannot change across revisions.");
            }

            if (index == 0)
            {
                if (version.Action != CognitiveAnnotationAction.Created ||
                    version.PreviousReviewStatus != CognitiveAnnotationReviewStatus.Unknown ||
                    version.Revision != 1 || version.RecordedAtUtc != annotation.RecordedAtUtc ||
                    (annotation.Origin == CognitiveAnnotationOrigin.Owner && version.State.ReviewStatus != CognitiveAnnotationReviewStatus.Accepted) ||
                    (annotation.Origin == CognitiveAnnotationOrigin.AiSuggestion && version.State.ReviewStatus != CognitiveAnnotationReviewStatus.Pending))
                {
                    throw new ArgumentException("A cognitive annotation's first revision has an invalid creation state.");
                }
            }
            else
            {
                if (version.Action == CognitiveAnnotationAction.Created || previousState is null ||
                    version.PreviousReviewStatus != previousState.ReviewStatus)
                {
                    throw new ArgumentException("A cognitive annotation history transition is invalid.");
                }

                if (version.Action == CognitiveAnnotationAction.Corrected &&
                    version.State.ReviewStatus != version.PreviousReviewStatus)
                {
                    throw new ArgumentException("A correction does not change review status.");
                }

                if (version.Action == CognitiveAnnotationAction.Accepted &&
                    (version.PreviousReviewStatus != CognitiveAnnotationReviewStatus.Pending ||
                     version.State.ReviewStatus != CognitiveAnnotationReviewStatus.Accepted))
                {
                    throw new ArgumentException("Only a pending cognitive suggestion can be accepted.");
                }

                if (version.Action == CognitiveAnnotationAction.Rejected &&
                    (version.PreviousReviewStatus != CognitiveAnnotationReviewStatus.Pending ||
                     version.State.ReviewStatus != CognitiveAnnotationReviewStatus.Rejected))
                {
                    throw new ArgumentException("Only a pending cognitive suggestion can be rejected.");
                }
            }

            previousState = version.State;
            previousTime = version.RecordedAtUtc;
        }

        if (previousState is null || !CognitiveAnnotationCodec.SnapshotsEqual(previousState, CognitiveAnnotationCodec.SnapshotOf(annotation)))
        {
            throw new ArgumentException("The cognitive annotation does not match its latest revision snapshot.");
        }
    }

    internal static CognitiveFacet[] NormalizeFacets(CognitiveFacet[]? facets)
    {
        var normalized = facets is null ? Array.Empty<CognitiveFacet>() : (CognitiveFacet[])facets.Clone();
        ValidateFacets(normalized);
        Array.Sort(normalized);
        return normalized;
    }

    internal static void ValidateAnchor(WorkspaceAnchorInfo anchor, bool requireLive)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        if (!DomainIdentifiers.IsDomainIdentifier(anchor.FileId) ||
            !DomainIdentifiers.IsSafeIdentifier(anchor.AnchorId) ||
            anchor.ContentRevision < 1 || anchor.Offset < 0 || anchor.Length < 1 ||
            anchor.Offset > MemoryEnvelopeCodec.MaximumSpanEnd - anchor.Length ||
            anchor.Sha256 is not { Length: 64 } || anchor.Sha256.Any(character => !IsLowerHex(character)) ||
            (!string.Equals(anchor.State, "live", StringComparison.Ordinal) &&
             !string.Equals(anchor.State, "stale", StringComparison.Ordinal)) ||
            (requireLive && !string.Equals(anchor.State, "live", StringComparison.Ordinal)))
        {
            throw new ArgumentException("A cognitive annotation preserves one exact, revision-pinned workspace anchor.");
        }
    }

    private static void ValidateSnapshot(CognitiveAnnotationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidateFacets(snapshot.Facets);
        ValidateRequiredEnum(snapshot.Kind);
        ValidateRequiredEnum(snapshot.Author);
        ValidateRequiredEnum(snapshot.Executor);
        ValidateRequiredEnum(snapshot.CreationMethod);
        ValidateRequiredEnum(snapshot.Evidence);
        ValidateRequiredEnum(snapshot.Level);
        ValidateRequiredEnum(snapshot.Origin);
        ValidateRequiredEnum(snapshot.ReviewStatus);
    }

    private static void ValidateFacets(CognitiveFacet[]? facets)
    {
        if (facets is null || facets.Length > CognitiveTaxonomy.Definitions.Count)
        {
            throw new ArgumentException("A cognitive annotation has at most one of each known facet.");
        }

        var seen = new HashSet<CognitiveFacet>();
        foreach (var facet in facets)
        {
            if (!Enum.IsDefined(facet) || !seen.Add(facet))
            {
                throw new ArgumentException("A cognitive annotation facet set contains an unknown or duplicate facet.");
            }
        }
    }

    private static void ValidateOptionalEnum<TEnum>(TEnum? value) where TEnum : struct, Enum
    {
        if (value is { } actual && !Enum.IsDefined(actual))
        {
            throw new ArgumentException("A cognitive annotation axis is unknown to this version.");
        }
    }

    private static void ValidateRequiredEnum<TEnum>(TEnum value) where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentException("A cognitive annotation value is unknown to this version.");
        }
    }

    private static void ValidateRecordedAt(DateTimeOffset value)
    {
        if (value.Offset != TimeSpan.Zero || value > DateTimeOffset.UtcNow.AddMinutes(5))
        {
            throw new ArgumentException("Cognitive annotation times must be UTC and not in the future.");
        }
    }

    private static bool IsLowerHex(char character) => character is >= '0' and <= '9' or >= 'a' and <= 'f';

}
