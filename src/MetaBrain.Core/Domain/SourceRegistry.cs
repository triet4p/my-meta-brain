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
/// projection of the frozen grant snapshot rather than registry data, so the
/// private index (S2-T7) alone stays pending and none of them is represented
/// here.
/// </summary>
public sealed record RegisteredSourceDraft(
    string? Title,
    string? Locator,
    string? Version,
    SourceAvailability? Availability,
    CreationMethod? CreationMethod,
    DateTimeOffset? CapturedAtUtc,
    DateTimeOffset? RecordedAtUtc);

/// <summary>
/// Server-persisted registered source (encrypted bytes). Identity comes from
/// trusted request params, executor from the trusted context, revision from
/// the vault descriptor. Registered sources keep only metadata and a private
/// locator; no source body is ever stored at this layer.
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
    DateTimeOffset RecordedAtUtc);

/// <summary>
/// Owner-facing read model: stored registered source plus the live revision.
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
    DateTimeOffset RecordedAtUtc);

public static class RegisteredSourceCodec
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumTitleLength = 512;
    public const int MaximumLocatorLength = 512;
    public const int MaximumVersionLength = 256;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

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
