using System.Text.Json;
using System.Text.Json.Serialization;

namespace MetaBrain.Core.Domain;

/// <summary>
/// S2-T1 domain envelope: one common model for source references and typed
/// memory metadata. Persisted only as encrypted bytes through the shared S1
/// vault store; no plaintext files, no parallel key hierarchy. Zone owns,
/// collection organizes externally, level describes abstraction: the three
/// are never interchangeable (envelope carries ZoneId + Level, never a
/// CollectionId). The S2-T3 source registry itself lives in
/// <see cref="SourceRegistry"/>; item citations only reference registered
/// sources by opaque ID plus revision-anchored spans, never by reading the
/// registry. Typed relations between items and sources live in
/// <see cref="Relations"/>; domain lifecycle (S2-T5), collections/views
/// (S2-T6) and the private index (S2-T7) remain pending and are not
/// represented here beyond the frozen S1 scope snapshot.
/// </summary>
public enum MemoryKind
{
    Unknown = 0,
    Claim = 1,
    Observation = 2,
    Question = 3,
    Hypothesis = 4,
    Decision = 5,
    Lesson = 6,
    Procedure = 7,
    Synthesis = 8,
    Preference = 9,
    Intention = 10
}

public enum SpeakerRole
{
    Unknown = 0,
    User = 1,
    Assistant = 2,
    Tool = 3,
    External = 4
}

public enum ExecutorKind
{
    Unknown = 0,
    Owner = 1,
    Agent = 2,
    StructuringJob = 3
}

public enum CreationMethod
{
    Unknown = 0,
    DirectWrite = 1,
    VerbatimExtract = 2,
    Paraphrase = 3,
    Synthesis = 4
}

public enum EvidenceLevel
{
    Unknown = 0,
    Unverified = 1,
    ReportedSource = 2,
    UserConfirmed = 3,
    ExperimentalObservation = 4
}

public enum AbstractionLevel
{
    Unknown = 0,
    L0 = 1,
    L1 = 2,
    L2 = 3,
    L3Plus = 4
}

public enum SourceAvailability
{
    Unknown = 0,
    Available = 1,
    Unavailable = 2
}

public sealed record MemorySourceReference(
    string? SourceId,
    string? Locator,
    SourceAvailability? Availability,
    long? SourceRevision = null,
    SourceSpan? Span = null);

public sealed record MemoryApplicability(
    string? Project,
    string? Environment,
    string? Version);

/// <summary>
/// Client-supplied draft: no identity, executor, schema, or revision.
/// Identity comes from the trusted request params (opaque ResourceId/ZoneId),
/// executor comes from the trusted <see cref="Security.AuthenticatedContext"/>,
/// revision comes from the shared vault descriptor. Unknown optional fields
/// stay null and are never fabricated into verified values.
/// </summary>
public sealed record MemoryItemDraft(
    MemoryKind? Kind,
    SpeakerRole? Author,
    CreationMethod? CreationMethod,
    EvidenceLevel? Evidence,
    AbstractionLevel? Level,
    string? Title,
    string? Body,
    MemorySourceReference[]? Sources,
    DateTimeOffset? OccurredAtUtc,
    DateTimeOffset? RecordedAtUtc,
    MemoryApplicability? Applicability);

/// <summary>
/// Server-persisted envelope bytes (encrypted). Revision lives in the vault
/// descriptor, not duplicated here; readers join it via ListResources.
/// </summary>
public sealed record StoredMemoryEnvelope(
    int SchemaVersion,
    string Id,
    string ZoneId,
    MemoryKind Kind,
    SpeakerRole Author,
    ExecutorKind Executor,
    CreationMethod CreationMethod,
    EvidenceLevel Evidence,
    AbstractionLevel Level,
    string? Title,
    string Body,
    MemorySourceReference[] Sources,
    DateTimeOffset? OccurredAtUtc,
    DateTimeOffset RecordedAtUtc,
    MemoryApplicability? Applicability);

/// <summary>
/// Owner-facing read model: stored envelope plus the live descriptor revision.
/// </summary>
public sealed record MemoryItem(
    string Id,
    string ZoneId,
    long Revision,
    int SchemaVersion,
    MemoryKind Kind,
    SpeakerRole Author,
    ExecutorKind Executor,
    CreationMethod CreationMethod,
    EvidenceLevel Evidence,
    AbstractionLevel Level,
    string? Title,
    string Body,
    MemorySourceReference[] Sources,
    DateTimeOffset? OccurredAtUtc,
    DateTimeOffset RecordedAtUtc,
    MemoryApplicability? Applicability);

public static class MemoryEnvelopeCodec
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumSources = 32;
    public const int MaximumTitleLength = 512;
    public const int MaximumBodyChars = 200_000;
    public const int MaximumLocatorLength = 512;
    public const int MaximumApplicabilityLength = 128;
    public const long MaximumSpanEnd = 1_000_000_000;
    public const long MaximumSpanLength = 1_048_576;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static MemoryItemDraft DecodeDraft(byte[] json)
    {
        ArgumentNullException.ThrowIfNull(json);
        MemoryItemDraft? draft;
        try
        {
            draft = JsonSerializer.Deserialize<MemoryItemDraft>(StripUtf8Preamble(json), JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("The memory draft schema is invalid.", ex);
        }

        if (draft is null)
        {
            throw new ArgumentException("The memory draft schema is invalid.");
        }

        return draft;
    }

    public static byte[] EncodeStored(StoredMemoryEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        MemoryItemValidator.ValidateStored(envelope);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
        if (bytes.Length is <= 0 or > 1024 * 1024)
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
            throw new ArgumentException("The memory envelope exceeds its storage limit.");
        }

        return bytes;
    }

    public static StoredMemoryEnvelope DecodeStored(byte[] json)
    {
        ArgumentNullException.ThrowIfNull(json);
        StoredMemoryEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<StoredMemoryEnvelope>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("The memory envelope schema is invalid.", ex);
        }

        if (envelope is null)
        {
            throw new ArgumentException("The memory envelope schema is invalid.");
        }

        MemoryItemValidator.ValidateStored(envelope);
        return envelope;
    }

    internal static JsonSerializerOptions Options => JsonOptions;

    private static ReadOnlySpan<byte> StripUtf8Preamble(byte[] json) =>
        json.Length >= 3 && json[0] == 0xEF && json[1] == 0xBB && json[2] == 0xBF
            ? new ReadOnlySpan<byte>(json, 3, json.Length - 3)
            : json;
}

public static class MemoryItemValidator
{
    public static void ValidateDraft(MemoryItemDraft draft, string resourceId, string zoneId)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (!DomainIdentifiers.IsNonRelationIdentifier(resourceId))
        {
            throw new ArgumentException("The memory ID must be an opaque safe identifier.");
        }

        if (!DomainIdentifiers.IsSafeIdentifier(zoneId))
        {
            throw new ArgumentException("The memory zone must be an opaque safe identifier.");
        }

        if (draft.Kind is null || draft.Kind == MemoryKind.Unknown)
        {
            throw new ArgumentException("A known memory kind is required.");
        }

        if (draft.Author is null || draft.Author == SpeakerRole.Unknown)
        {
            throw new ArgumentException("A known author role is required.");
        }

        if (draft.CreationMethod is null || draft.CreationMethod == CreationMethod.Unknown)
        {
            throw new ArgumentException("A known creation method is required.");
        }

        if (draft.Evidence is null || draft.Evidence == EvidenceLevel.Unknown)
        {
            throw new ArgumentException("An explicit evidence level is required; unknown is not fabricated.");
        }

        if (draft.Level is null || draft.Level == AbstractionLevel.Unknown)
        {
            throw new ArgumentException("A known abstraction level is required; level never implies access.");
        }

        ValidateTitle(draft.Title);
        ValidateBody(draft.Body);
        ValidateSources(draft.Sources);
        ValidateTimes(draft.OccurredAtUtc, draft.RecordedAtUtc);
        ValidateApplicability(draft.Applicability);

        if (draft.Kind == MemoryKind.Question && draft.Evidence != EvidenceLevel.Unverified)
        {
            throw new ArgumentException("A question cannot carry belief evidence; it stays unverified until resolved by a separate lifecycle step.");
        }
    }
    public static void ValidateStored(StoredMemoryEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.SchemaVersion != MemoryEnvelopeCodec.CurrentSchemaVersion)
        {
            throw new ArgumentException("The memory envelope schema version is unsupported.");
        }

        if (!DomainIdentifiers.IsNonRelationIdentifier(envelope.Id))
        {
            throw new ArgumentException("The memory ID must be an opaque safe identifier.");
        }

        if (!DomainIdentifiers.IsSafeIdentifier(envelope.ZoneId))
        {
            throw new ArgumentException("The memory zone must be an opaque safe identifier.");
        }

        if (!Enum.IsDefined(envelope.Kind) || envelope.Kind == MemoryKind.Unknown)
        {
            throw new ArgumentException("A known memory kind is required.");
        }

        if (!Enum.IsDefined(envelope.Author) || envelope.Author == SpeakerRole.Unknown)
        {
            throw new ArgumentException("A known author role is required.");
        }

        if (!Enum.IsDefined(envelope.Executor) || envelope.Executor == ExecutorKind.Unknown)
        {
            throw new ArgumentException("A known executor is required.");
        }

        if (!Enum.IsDefined(envelope.CreationMethod) || envelope.CreationMethod == CreationMethod.Unknown)
        {
            throw new ArgumentException("A known creation method is required.");
        }

        if (!Enum.IsDefined(envelope.Evidence) || envelope.Evidence == EvidenceLevel.Unknown)
        {
            throw new ArgumentException("An explicit evidence level is required.");
        }

        if (!Enum.IsDefined(envelope.Level) || envelope.Level == AbstractionLevel.Unknown)
        {
            throw new ArgumentException("A known abstraction level is required.");
        }

        ValidateTitle(envelope.Title);
        ValidateBody(envelope.Body);
        ValidateSources(envelope.Sources);

        if (envelope.RecordedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Recorded time must use UTC.");
        }

        if (envelope.OccurredAtUtc is not null && envelope.OccurredAtUtc.Value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Occurred time must use UTC.");
        }
        if (envelope.OccurredAtUtc is not null && envelope.OccurredAtUtc.Value > envelope.RecordedAtUtc)
        {
            throw new ArgumentException("Occurred time cannot follow recorded time.");
        }

        ValidateApplicability(envelope.Applicability);

        if (envelope.Kind == MemoryKind.Question && envelope.Evidence != EvidenceLevel.Unverified)
        {
            throw new ArgumentException("A question cannot carry belief evidence; it stays unverified until resolved by a separate lifecycle step.");
        }
    }
    public static MemorySourceReference[] NormalizeSources(MemorySourceReference[]? sources)
    {
        if (sources is null || sources.Length == 0)
        {
            return Array.Empty<MemorySourceReference>();
        }

        var normalized = new MemorySourceReference[sources.Length];
        for (var index = 0; index < sources.Length; index++)
        {
            var source = sources[index] ?? throw new ArgumentException("A memory source reference is invalid.");
            normalized[index] = source with { Availability = source.Availability ?? SourceAvailability.Unknown };
        }

        return normalized;
    }

    public static string? NormalizeTitle(string? title) => title;

    public static MemoryApplicability? NormalizeApplicability(MemoryApplicability? applicability) => applicability;

    private static void ValidateTitle(string? title)
    {
        if (title is null)
        {
            return;
        }

        if (title.Length is 0 or > MemoryEnvelopeCodec.MaximumTitleLength || title.Any(char.IsControl))
        {
            throw new ArgumentException("The memory title must be 1-512 characters without control characters.");
        }
    }

    private static void ValidateBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body) || body!.Length > MemoryEnvelopeCodec.MaximumBodyChars)
        {
            throw new ArgumentException("The memory body must be 1-200000 characters.");
        }

        if (body.Any(character => char.IsControl(character) && character is not '\n' and not '\r' and not '\t'))
        {
            throw new ArgumentException("The memory body contains unsupported control characters.");
        }
    }

    private static void ValidateSources(MemorySourceReference[]? sources)
    {
        if (sources is null || sources.Length == 0)
        {
            return;
        }

        if (sources.Length > MemoryEnvelopeCodec.MaximumSources)
        {
            throw new ArgumentException("The memory source set is too large.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            if (source is null)
            {
                throw new ArgumentException("A memory source reference is invalid.");
            }

            if (source.SourceId is not null && !DomainIdentifiers.IsNonRelationIdentifier(source.SourceId))
            {
                throw new ArgumentException("A memory source ID must be an opaque safe identifier.");
            }

            if (source.SourceRevision is <= 0)
            {
                throw new ArgumentException("A citation source revision must name a positive source revision.");
            }

            if (source.Span is not null)
            {
                if (source.SourceId is null)
                {
                    throw new ArgumentException("A citation span requires a registered source ID.");
                }

                if (source.SourceRevision is null)
                {
                    throw new ArgumentException("A citation span must be anchored to a source revision.");
                }

                var span = source.Span;
                if (span.Start < 0 || span.End <= span.Start ||
                    span.End > MemoryEnvelopeCodec.MaximumSpanEnd ||
                    span.End - span.Start > MemoryEnvelopeCodec.MaximumSpanLength)
                {
                    throw new ArgumentException("A citation span must be a non-empty bounded range.");
                }
            }
            else if (source.SourceRevision is not null && source.SourceId is null)
            {
                throw new ArgumentException("A citation source revision requires a registered source ID.");
            }

            if (source.Locator is not null &&
                (source.Locator.Length is 0 or > MemoryEnvelopeCodec.MaximumLocatorLength ||
                 source.Locator.Any(char.IsControl)))
            {
                throw new ArgumentException("A memory source locator must be 1-512 characters without control characters.");
            }

            var availability = source.Availability ?? SourceAvailability.Unknown;
            if (!Enum.IsDefined(availability))
            {
                throw new ArgumentException("A memory source availability is invalid.");
            }

            if (availability == SourceAvailability.Available &&
                source.SourceId is null && source.Locator is null)
            {
                throw new ArgumentException("An available source requires an ID or locator; unknown is never reported as available.");
            }

            var key = (source.SourceId ?? string.Empty) + "\u0001" + (source.Locator ?? string.Empty);
            if (!seen.Add(key))
            {
                throw new ArgumentException("Duplicate memory source references do not count as independent confirmation.");
            }
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

    private static void ValidateApplicability(MemoryApplicability? applicability)
    {
        if (applicability is null)
        {
            return;
        }

        foreach (var value in new[] { applicability.Project, applicability.Environment, applicability.Version })
        {
            if (value is null)
            {
                continue;
            }

            if (value.Length is 0 or > MemoryEnvelopeCodec.MaximumApplicabilityLength || value.Any(char.IsControl))
            {
                throw new ArgumentException("Memory applicability fields must be 1-128 characters without control characters.");
            }
        }
    }
}

/// <summary>
/// One opaque-identifier grammar shared by every domain envelope so an ID
/// can never carry path or traversal text. Identifiers are stable keys: they
/// never derive from titles or paths (R10).
/// </summary>
internal static class DomainIdentifiers
{
    /// <summary>
    /// Reserved resource-ID prefix for typed relations. Relation records are
    /// canonical resources on the shared store, and this prefix is what makes
    /// the relation set enumerable from the encrypted manifest without
    /// decrypting items or sources (and without a second link index). Items
    /// and registered sources reject the prefix so a resource ID can never be
    /// mistaken for a relation.
    /// </summary>
    public const string RelationIdPrefix = "rel-";

    public static bool IsSafeIdentifier(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 128 || !IsAsciiAlphaNumeric(value[0]))
        {
            return false;
        }

        for (var index = 1; index < value.Length; index++)
        {
            var character = value[index];
            if (!IsAsciiAlphaNumeric(character) && character is not '.' and not '_' and not '-')
            {
                return false;
            }
        }

        return true;
    }

    public static bool IsRelationId(string? value) =>
        value is not null && value.StartsWith(RelationIdPrefix, StringComparison.Ordinal) && IsSafeIdentifier(value);

    public static bool IsNonRelationIdentifier(string? value) =>
        IsSafeIdentifier(value) && !value!.StartsWith(RelationIdPrefix, StringComparison.Ordinal);

    public static bool IsAsciiAlphaNumeric(char value) =>
        value is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9';
}
