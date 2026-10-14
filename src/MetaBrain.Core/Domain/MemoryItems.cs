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
/// <see cref="Relations"/>; the domain lifecycle and temporal validity of an
/// item live in <see cref="Lifecycle"/> as one canonical record per subject
/// with the retained-revision archive; owner collection membership and the
/// permission-filtered agent collection view (S2-T6) live outside this
/// envelope in <see cref="CollectionViewAuthority"/> as a projection of the
/// frozen grant snapshot, and the private index (S2-T7, see
/// <see cref="Index.PrivateIndexRow"/>) is a derived projection of these
/// canonical envelopes that never re-enters this model.
/// Neither is represented here beyond the frozen S1 scope snapshot.
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
        if (!DomainIdentifiers.IsDomainIdentifier(resourceId))
        {
            throw new ArgumentException("The memory ID must be an opaque safe identifier.");
        }

        if (!DomainIdentifiers.IsSafeIdentifier(zoneId))
        {
            throw new ArgumentException("The memory zone must be an opaque safe identifier.");
        }

        ValidateProposedItem(draft);
    }

    /// <summary>
    /// Content validation for one memory draft, shared by the canonical owner
    /// write path and the S4-T5 proposal path: the same one memory-draft
    /// policy validates a proposed item before it is recorded, so a proposal
    /// can never carry content a canonical item could not. Identity (ID,
    /// zone) is not checked here: a proposed item has no resource ID until an
    /// owner publishes it.
    /// </summary>
    public static void ValidateProposedItem(MemoryItemDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);

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

        // R12: belief-grade evidence is a user assertion, so it can never be
        // written onto an assistant, tool, or external speaker. An AI-written
        // note stays AI-written no matter who saves it.
        if (draft.Evidence == EvidenceLevel.UserConfirmed && draft.Author != SpeakerRole.User)
        {
            throw new ArgumentException("User-confirmed evidence requires the user as the original speaker.");
        }

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

        if (!DomainIdentifiers.IsDomainIdentifier(envelope.Id))
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

            if (source.SourceId is not null && !DomainIdentifiers.IsDomainIdentifier(source.SourceId))
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
/// never derive from titles or paths (R10). Four namespaces are reserved for
/// records that are not plain memory items or registered sources: typed
/// relations (<see cref="RelationIdPrefix"/>), lifecycle records
/// (<see cref="LifecycleIdPrefix"/>), retained historical revisions
/// (<see cref="ArchiveIdPrefix"/>), and S4-T5 proposals
/// (<see cref="ProposalIdPrefix"/>). An item or a registered source ID in
/// one of those namespaces would collide with its derived record, so they
/// are refused here.
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

    /// <summary>
    /// Reserved resource-ID prefix for lifecycle records. A lifecycle record
    /// is canonical on the shared store and its ID is derived from the subject
    /// memory item, so exactly one record covers one subject.
    /// </summary>
    public const string LifecycleIdPrefix = "lc-";

    /// <summary>
    /// Reserved resource-ID prefix for a retained historical revision. The
    /// archive keeps the exact stored bytes of one superseded revision of a
    /// subject item, addressed by subject and revision so the history of an
    /// item stays retrievable after later revisions.
    /// </summary>
    public const string ArchiveIdPrefix = "arc-";

    /// <summary>
    /// Reserved resource-ID prefix for an S3-T1 source-ingestion journal: the
    /// durable per-source (checkpoint, normalized events) pair. Journals are
    /// derived ingestion state, and the reserved prefix keeps them enumerable
    /// from the encrypted manifest while preventing an item or registered
    /// source ID from colliding with one.
    /// </summary>
    public const string IngestIdPrefix = "ingest-";
    /// <summary>
    /// Reserved resource-ID prefix for full encrypted source snapshots. The
    /// snapshot API uses this prefix to keep snapshot bodies in a distinct
    /// operation and namespace from source references and memory items.
    /// </summary>
    public const string SourceSnapshotIdPrefix = "snap-";
    /// <summary>
    /// Reserved resource-ID prefix for S4-T4 agent/owner feedback records.
    /// Feedback is canonical usage data beside items and sources, enumerable
    /// from the encrypted manifest without decrypting item or source bodies.
    /// Items and registered sources reject the prefix so a memory or source ID
    /// can never be mistaken for a feedback record.
    /// </summary>
    public const string FeedbackIdPrefix = "fb-";

    /// <summary>
    /// Reserved resource-ID prefix for S4-T5 agent proposals. A proposal is
    /// a pending owner-review write over its own canonical record beside
    /// items, sources, and feedback: the reserved prefix keeps the inbox
    /// enumerable from the encrypted manifest without decrypting an item or
    /// source body, and the deterministic ID derivation in
    /// <see cref="ProposalValidator.DeriveProposalId"/> keeps a repeated
    /// operation on one stable record. Items and registered sources reject
    /// the prefix so a memory or source ID can never be mistaken for a
    /// proposal.
    /// </summary>
    public const string ProposalIdPrefix = "prop-";


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

    public static bool IsLifecycleId(string? value) =>
        value is not null && value.StartsWith(LifecycleIdPrefix, StringComparison.Ordinal) && IsSafeIdentifier(value);

    public static bool IsArchiveId(string? value) =>
        value is not null && value.StartsWith(ArchiveIdPrefix, StringComparison.Ordinal) && IsSafeIdentifier(value);

    public static bool IsFeedbackId(string? value) =>
        value is not null && value.StartsWith(FeedbackIdPrefix, StringComparison.Ordinal) && IsSafeIdentifier(value);

    public static bool IsProposalId(string? value) =>
        value is not null && value.StartsWith(ProposalIdPrefix, StringComparison.Ordinal) && IsSafeIdentifier(value);
    /// <summary>
    /// An identifier that names a memory item or a registered source: never a
    /// relation, lifecycle record, retained revision, ingestion journal,
    /// preserved source snapshot, feedback record, or proposal.
    /// </summary>
    public static bool IsDomainIdentifier(string? value) =>
        IsSafeIdentifier(value) &&
        !value!.StartsWith(RelationIdPrefix, StringComparison.Ordinal) &&
        !value.StartsWith(LifecycleIdPrefix, StringComparison.Ordinal) &&
        !value.StartsWith(ArchiveIdPrefix, StringComparison.Ordinal) &&
        !value.StartsWith(IngestIdPrefix, StringComparison.Ordinal) &&
        !value.StartsWith(SourceSnapshotIdPrefix, StringComparison.Ordinal) &&
        !value.StartsWith(FeedbackIdPrefix, StringComparison.Ordinal) &&
        !value.StartsWith(ProposalIdPrefix, StringComparison.Ordinal);

    /// <summary>
    /// Lifecycle record ID derived from its subject: the ID is never chosen by
    /// a caller, so one subject has at most one lifecycle record.
    /// </summary>
    public static string LifecycleIdFor(string subjectResourceId) => LifecycleIdPrefix + subjectResourceId;

    /// <summary>
    /// Retained-revision archive ID derived from its subject and the exact
    /// revision it keeps: a revision is retained at most once, and the stored
    /// ID always names the subject and revision it holds.
    /// </summary>
    public static string ArchiveIdFor(string subjectResourceId, long revision) =>
        ArchiveIdPrefix + subjectResourceId + "-r" + revision.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static bool IsAsciiAlphaNumeric(char value) =>
        value is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9';
}
