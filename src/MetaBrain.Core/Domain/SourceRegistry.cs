using System.Text.Json;
using System.Text.Json.Serialization;

namespace MetaBrain.Core.Domain;

/// <summary>
/// A provenance span inside a registered source revision: half-open
/// byte/character offsets [Start, End) into the source content as captured
/// for that revision. Spans are only meaningful anchored to a registered
/// source revision; boundaries are validated structurally at write time and
/// the anchor is checked against the live source revision, so a span can
/// never imply content the vault does not hold.
/// </summary>
public sealed record SourceSpan(long Start, long End);

/// <summary>
/// S2-T3 source registry: encrypted registered source metadata (title,
/// locator, version, availability) persisted as canonical vault resources
/// beside, but permission-independent from, memory items. A registered
/// source carries no body: originals stay outside vault protection (R16),
/// and encrypted snapshots/refresh belong to Sprint 3. Availability is an
/// explicit registrant assertion; unknown is explicit and is never reported
/// as available or verified (R18). Typed relations over items and registered
/// sources live in <see cref="Relations"/>; lifecycle (S2-T5) is delivered,
/// and owner collection membership plus the permission-filtered agent
/// collection view (S2-T6, see <see cref="CollectionViewAuthority"/>) are a
/// projection of the frozen grant snapshot rather than registry data, and the
/// private index (S2-T7, see <see cref="Index.PrivateIndexRow"/>) is a
/// derived projection of these canonical envelopes; none of them is
/// represented here.
/// <para>
/// S3-T4 adds the captured research note as one optional, additive field pair
/// on this same record (never a parallel record family): when the owner
/// captures unstructured research text, the verbatim text is persisted
/// encrypted in <see cref="Text"/> and the owner-declared origin role in
/// <see cref="Author"/>. A typed note has no original outside the vault, so
/// this text is the source's primary content, not a copy of external data
/// (the S2-T3 "no body" rule still holds for metadata-only registrations of
/// external sources). Both fields are absent for every other registration and
/// absent means unknown: nothing infers an author or content, and an ID
/// missing from the live vault is still only "not registered".
/// </para>
/// </summary>
public sealed record RegisteredSourceDraft(
    string? Title,
    string? Locator,
    string? Version,
    SourceAvailability? Availability,
    CreationMethod? CreationMethod,
    DateTimeOffset? CapturedAtUtc,
    DateTimeOffset? RecordedAtUtc,
    SpeakerRole? Author = null,
    string? Text = null);

/// <summary>
/// Server-persisted registered source (encrypted bytes). Identity comes from
/// trusted request params, executor from the trusted context, revision from
/// the vault descriptor. Registered sources of external material keep only
/// metadata and a private locator; the S3-T4 captured research note
/// additionally carries its verbatim text and declared author role, which are
/// the note's own primary content.
/// </summary>
public sealed record StoredRegisteredSource(
    int SchemaVersion,
    string Id,
    string ZoneId,
    ExecutorKind Executor,
    CreationMethod CreationMethod,
    string? Title,
    string? Locator,
    string? Version,
    SourceAvailability Availability,
    DateTimeOffset? CapturedAtUtc,
    DateTimeOffset RecordedAtUtc,
    SpeakerRole? Author = null,
    string? Text = null);

/// <summary>
/// Owner-facing read model: stored registered source plus the live revision.
/// <see cref="Text"/> is the verbatim captured research text when the source
/// is an S3-T4 captured note, and null for every metadata-only registration;
/// it is owner-only content and never crosses the metadata source wire.
/// </summary>
public sealed record RegisteredSource(
    string Id,
    string ZoneId,
    long Revision,
    int SchemaVersion,
    ExecutorKind Executor,
    CreationMethod CreationMethod,
    string? Title,
    string? Locator,
    string? Version,
    SourceAvailability Availability,
    DateTimeOffset? CapturedAtUtc,
    DateTimeOffset RecordedAtUtc,
    SpeakerRole? Author,
    string? Text);

public static class RegisteredSourceCodec
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumTitleLength = 512;
    public const int MaximumLocatorLength = 512;
    public const int MaximumVersionLength = 256;

    /// <summary>
    /// S3-T4 captured research text bound (mirrors the memory body bound). The
    /// stored record keeps its own 1 MiB byte cap in <see cref="EncodeStored"/>,
    /// which is what a text made of non-ASCII characters must also fit.
    /// </summary>
    public const int MaximumTextChars = 200_000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    /// <summary>
    /// Encodes a client-shaped draft (e.g. the S3-T4 capture service building
    /// one from trusted request fields) so it can pass through the same
    /// validated create path as a wire-supplied draft. The draft is validated
    /// by the create path, not here.
    /// </summary>
    public static byte[] EncodeDraft(RegisteredSourceDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return JsonSerializer.SerializeToUtf8Bytes(draft, JsonOptions);
    }

    public static RegisteredSourceDraft DecodeDraft(byte[] json)
    {
        ArgumentNullException.ThrowIfNull(json);
        RegisteredSourceDraft? draft;
        try
        {
            draft = JsonSerializer.Deserialize<RegisteredSourceDraft>(StripUtf8Preamble(json), JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("The source draft schema is invalid.", ex);
        }

        if (draft is null)
        {
            throw new ArgumentException("The source draft schema is invalid.");
        }

        return draft;
    }

    public static byte[] EncodeStored(StoredRegisteredSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        RegisteredSourceValidator.ValidateStored(source);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(source, JsonOptions);
        if (bytes.Length is <= 0 or > 1024 * 1024)
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
            throw new ArgumentException("The registered source exceeds its storage limit.");
        }

        return bytes;
    }

    public static StoredRegisteredSource DecodeStored(byte[] json)
    {
        ArgumentNullException.ThrowIfNull(json);
        StoredRegisteredSource? source;
        try
        {
            source = JsonSerializer.Deserialize<StoredRegisteredSource>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("The registered source schema is invalid.", ex);
        }

        if (source is null)
        {
            throw new ArgumentException("The registered source schema is invalid.");
        }

        RegisteredSourceValidator.ValidateStored(source);
        return source;
    }

    internal static JsonSerializerOptions Options => JsonOptions;

    private static ReadOnlySpan<byte> StripUtf8Preamble(byte[] json) =>
        json.Length >= 3 && json[0] == 0xEF && json[1] == 0xBB && json[2] == 0xBF
            ? new ReadOnlySpan<byte>(json, 3, json.Length - 3)
            : json;
}

public static class RegisteredSourceValidator
{
    public static void ValidateDraft(RegisteredSourceDraft draft, string sourceId, string zoneId)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (!DomainIdentifiers.IsDomainIdentifier(sourceId))
        {
            throw new ArgumentException("The source ID must be an opaque safe identifier.");
        }

        if (!DomainIdentifiers.IsSafeIdentifier(zoneId))
        {
            throw new ArgumentException("The source zone must be an opaque safe identifier.");
        }

        if (draft.CreationMethod is null || draft.CreationMethod == CreationMethod.Unknown)
        {
            throw new ArgumentException("A known creation method is required.");
        }

        if (draft.Availability is null)
        {
            throw new ArgumentException("An explicit source availability is required; unknown is never asserted as available.");
        }

        ValidateTitle(draft.Title);
        ValidateLocator(draft.Locator);
        ValidateVersion(draft.Version);
        ValidateTimes(draft.CapturedAtUtc, draft.RecordedAtUtc);
        ValidateCapturedText(draft.Author, draft.Text);
    }

    public static void ValidateStored(StoredRegisteredSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.SchemaVersion != RegisteredSourceCodec.CurrentSchemaVersion)
        {
            throw new ArgumentException("The registered source schema version is unsupported.");
        }

        if (!DomainIdentifiers.IsDomainIdentifier(source.Id))
        {
            throw new ArgumentException("The source ID must be an opaque safe identifier.");
        }

        if (!DomainIdentifiers.IsSafeIdentifier(source.ZoneId))
        {
            throw new ArgumentException("The source zone must be an opaque safe identifier.");
        }

        if (!Enum.IsDefined(source.Executor) || source.Executor == ExecutorKind.Unknown)
        {
            throw new ArgumentException("A known executor is required.");
        }

        if (!Enum.IsDefined(source.CreationMethod) || source.CreationMethod == CreationMethod.Unknown)
        {
            throw new ArgumentException("A known creation method is required.");
        }

        if (!Enum.IsDefined(source.Availability))
        {
            throw new ArgumentException("The source availability is invalid.");
        }

        ValidateTitle(source.Title);
        ValidateLocator(source.Locator);
        ValidateVersion(source.Version);
        ValidateCapturedText(source.Author, source.Text);

        if (source.RecordedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Recorded time must use UTC.");
        }

        if (source.CapturedAtUtc is not null && source.CapturedAtUtc.Value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Captured time must use UTC.");
        }

        if (source.CapturedAtUtc is not null && source.CapturedAtUtc.Value > source.RecordedAtUtc)
        {
            throw new ArgumentException("Captured time cannot follow recorded time.");
        }
    }

    private static void ValidateTitle(string? title)
    {
        if (title is null)
        {
            return;
        }

        if (title.Length is 0 or > RegisteredSourceCodec.MaximumTitleLength || title.Any(char.IsControl))
        {
            throw new ArgumentException("The source title must be 1-512 characters without control characters.");
        }
    }

    private static void ValidateLocator(string? locator)
    {
        if (locator is null)
        {
            return;
        }

        if (locator.Length is 0 or > RegisteredSourceCodec.MaximumLocatorLength || locator.Any(char.IsControl))
        {
            throw new ArgumentException("The source locator must be 1-512 characters without control characters.");
        }
    }

    private static void ValidateVersion(string? version)
    {
        if (version is null)
        {
            return;
        }

        if (version.Length is 0 or > RegisteredSourceCodec.MaximumVersionLength || version.Any(char.IsControl))
        {
            throw new ArgumentException("The source version must be 1-256 characters without control characters.");
        }
    }

    /// <summary>
    /// S3-T4: the captured research text and its declared origin role. Both are
    /// optional as a pair of unknowns, but an author role without captured text
    /// would attribute nothing, an explicitly declared role must be a known
    /// one (unknown is omission, never an asserted value), and the text must be
    /// bounded printable text like a memory body.
    /// </summary>
    private static void ValidateCapturedText(SpeakerRole? author, string? text)
    {
        if (author is null && text is null)
        {
            return;
        }

        if (text is null)
        {
            throw new ArgumentException("A captured source author requires the captured text it describes.");
        }

        if (author is not null && (!Enum.IsDefined(author.Value) || author.Value == SpeakerRole.Unknown))
        {
            throw new ArgumentException("The captured source author must be a known role; omission is the only unknown.");
        }

        if (string.IsNullOrWhiteSpace(text) || text.Length > RegisteredSourceCodec.MaximumTextChars)
        {
            throw new ArgumentException("The captured source text must be 1-200000 characters.");
        }

        if (text.Any(character => char.IsControl(character) && character is not '\n' and not '\r' and not '\t'))
        {
            throw new ArgumentException("The captured source text contains unsupported control characters.");
        }
    }

    private static void ValidateTimes(DateTimeOffset? capturedAtUtc, DateTimeOffset? recordedAtUtc)
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

        if (capturedAtUtc is not null)
        {
            if (capturedAtUtc.Value.Offset != TimeSpan.Zero)
            {
                throw new ArgumentException("Captured time must use UTC.");
            }

            if (capturedAtUtc.Value > recordedAtUtc.Value)
            {
                throw new ArgumentException("Captured time cannot follow recorded time.");
            }
        }
    }
}
