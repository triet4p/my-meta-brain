using System.Text.Json;
using System.Text.Json.Serialization;

namespace MetaBrain.Core.Domain;

/// <summary>
/// S4-T4 agent/owner feedback kind. Feedback says how one seen memory revision
/// behaved in use; it never upgrades evidence, verification, or confirmation.
/// </summary>
public enum FeedbackKind
{
    Unknown = 0,
    Useful = 1,
    Wrong = 2,
    Stale = 3
}

/// Client-supplied feedback draft: the seen target plus the verdict on it.
/// Identity comes from the server (minted feedback ID for agent records,
/// trusted request params for owner writes), executor from the trusted
/// context, revision from the vault descriptor. The target revision is the
/// revision the giver actually saw (the frozen grant revision for an agent);
/// it stays a truthful historical anchor even after the target moves.
/// </summary>
public sealed record FeedbackDraft(
    string? TargetResourceId,
    long? TargetRevision,
    FeedbackKind? Kind,
    string? Note,
    CreationMethod? CreationMethod = null);
/// <summary>
/// Server-persisted feedback (encrypted bytes). Revision lives in the vault
/// descriptor, not duplicated here; readers join it via ListResources.
/// </summary>
public sealed record StoredFeedback(
    int SchemaVersion,
    string Id,
    string ZoneId,
    string TargetResourceId,
    long TargetRevision,
    FeedbackKind Kind,
    string? Note,
    ExecutorKind Executor,
    CreationMethod CreationMethod,
    DateTimeOffset RecordedAtUtc);

/// <summary>
/// Owner-facing read model: stored feedback plus the live descriptor revision.
/// </summary>
public sealed record Feedback(
    string Id,
    string ZoneId,
    long Revision,
    int SchemaVersion,
    string TargetResourceId,
    long TargetRevision,
    FeedbackKind Kind,
    string? Note,
    ExecutorKind Executor,
    CreationMethod CreationMethod,
    DateTimeOffset RecordedAtUtc);

public static class FeedbackCodec
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumNoteLength = 2000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static FeedbackDraft DecodeDraft(byte[] json)
    {
        ArgumentNullException.ThrowIfNull(json);
        FeedbackDraft? draft;
        try
        {
            draft = JsonSerializer.Deserialize<FeedbackDraft>(StripUtf8Preamble(json), JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("The feedback draft schema is invalid.", ex);
        }

        if (draft is null)
        {
            throw new ArgumentException("The feedback draft schema is invalid.");
        }

        return draft;
    }

    public static byte[] EncodeStored(StoredFeedback feedback)
    {
        ArgumentNullException.ThrowIfNull(feedback);
        FeedbackValidator.ValidateStored(feedback);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(feedback, JsonOptions);
        if (bytes.Length is <= 0 or > 1024 * 1024)
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
            throw new ArgumentException("The feedback record exceeds its storage limit.");
        }

        return bytes;
    }
    public static StoredFeedback DecodeStored(byte[] json)
    {
        ArgumentNullException.ThrowIfNull(json);
        StoredFeedback? feedback;
        try
        {
            feedback = JsonSerializer.Deserialize<StoredFeedback>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("The feedback schema is invalid.", ex);
        }

        if (feedback is null)
        {
            throw new ArgumentException("The feedback schema is invalid.");
        }

        FeedbackValidator.ValidateStored(feedback);
        return feedback;
    }

    internal static JsonSerializerOptions Options => JsonOptions;

    private static ReadOnlySpan<byte> StripUtf8Preamble(byte[] json) =>
        json.Length >= 3 && json[0] == 0xEF && json[1] == 0xBB && json[2] == 0xBF
            ? new ReadOnlySpan<byte>(json, 3, json.Length - 3)
            : json;
}

public static class FeedbackValidator
{
    public static void ValidateDraft(FeedbackDraft draft, string feedbackId, string zoneId)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (!DomainIdentifiers.IsFeedbackId(feedbackId))
        {
            throw new ArgumentException("The feedback ID must be an opaque safe feedback identifier.");
        }

        if (!DomainIdentifiers.IsSafeIdentifier(zoneId))
        {
            throw new ArgumentException("The feedback zone must be an opaque safe identifier.");
        }

        ValidateTarget(draft.TargetResourceId, draft.TargetRevision);
        ValidateKind(draft.Kind);
        ValidateNote(draft.Note);
        if (draft.CreationMethod is null || draft.CreationMethod == CreationMethod.Unknown)
        {
            throw new ArgumentException("A known creation method is required.");
        }
    }

    public static void ValidateStored(StoredFeedback feedback)
    {
        ArgumentNullException.ThrowIfNull(feedback);
        if (feedback.SchemaVersion != FeedbackCodec.CurrentSchemaVersion)
        {
            throw new ArgumentException("The feedback schema version is unsupported.");
        }

        if (!DomainIdentifiers.IsFeedbackId(feedback.Id))
        {
            throw new ArgumentException("The feedback ID must be an opaque safe feedback identifier.");
        }

        if (!DomainIdentifiers.IsSafeIdentifier(feedback.ZoneId))
        {
            throw new ArgumentException("The feedback zone must be an opaque safe identifier.");
        }

        ValidateTarget(feedback.TargetResourceId, feedback.TargetRevision);
        ValidateKind(feedback.Kind);
        ValidateNote(feedback.Note);

        if (!Enum.IsDefined(feedback.Executor) || feedback.Executor == ExecutorKind.Unknown)
        {
            throw new ArgumentException("A known executor is required.");
        }

        if (!Enum.IsDefined(feedback.CreationMethod) || feedback.CreationMethod == CreationMethod.Unknown)
        {
            throw new ArgumentException("A known creation method is required.");
        }

        if (feedback.RecordedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Recorded time must use UTC.");
        }
    }

    private static void ValidateTarget(string? targetResourceId, long? targetRevision)
    {
        if (string.IsNullOrEmpty(targetResourceId) || !DomainIdentifiers.IsDomainIdentifier(targetResourceId))
        {
            throw new ArgumentException("Feedback targets one opaque memory item or registered source identifier.");
        }

        if (targetRevision is not > 0)
        {
            throw new ArgumentException("Feedback anchors to the positive seen target revision.");
        }
    }

    private static void ValidateKind(FeedbackKind? kind)
    {
        if (kind is null || !Enum.IsDefined(kind.Value) || kind.Value == FeedbackKind.Unknown)
        {
            throw new ArgumentException("A known feedback kind (useful, wrong, or stale) is required.");
        }
    }

    private static void ValidateNote(string? note)
    {
        if (note is null)
        {
            return;
        }

        if (note.Length is 0 or > FeedbackCodec.MaximumNoteLength || note.Any(char.IsControl))
        {
            throw new ArgumentException("The feedback note must be 1-2000 characters without control characters.");
        }
    }
}
