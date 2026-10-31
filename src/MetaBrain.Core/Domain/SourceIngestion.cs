using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MetaBrain.Core.Security;

namespace MetaBrain.Core.Domain;

/// <summary>
/// Shared bounds for the S3-T1 owner source enrollment and incremental
/// ingestion coordinator. Every limit is a product boundary, not a tuning
/// knob: exceeding one fails closed with an explicit diagnostic instead of
/// silently truncating, scanning, or dropping source data.
/// </summary>
public static class SourceIngestionLimits
{
    /// <summary>
    /// Largest enrolled source the coordinator will snapshot in one run. A
    /// larger file is refused explicitly (<c>source_too_large</c>) rather than
    /// partially read: a partial read of an unsized transcript is exactly the
    /// "partial tail committed as complete" failure this stage must not have.
    /// </summary>
    public const int MaximumSourceBytes = 16 * 1024 * 1024;

    public const int MaximumEnrollments = 512;

    public const int MaximumQuarantineRecords = 1024;

    /// <summary>
    /// Per-generation journal bounds. The journal is the durable pair of
    /// (checkpoint, accepted normalized events); hitting a bound stops
    /// ingestion with <c>journal_limit</c> instead of discarding events or
    /// advancing the checkpoint past data that would be lost. Raw-preserved
    /// records share this bound and never advance the checkpoint if they do not
    /// fit.
    /// </summary>
    public const int MaximumJournalBytes = 768 * 1024;

    public const int MaximumJournalEvents = 4096;

    public const int MaximumJournalDiagnostics = MaximumJournalEvents;

    public const int MaximumSourceStateBytes = 1024 * 1024;

    public const int MaximumLocatorLength = 4096;

    /// <summary>
    /// A source ID must leave room for the derived journal resource ID
    /// (<c>ingest-&lt;sourceId&gt;-g&lt;generation&gt;</c>) inside the shared
    /// 128-character resource-ID grammar.
    /// </summary>
    public const int MaximumSourceIdLength = 100;

    public const int MaximumFormatLength = 64;

    public const int MaximumEventIdLength = 128;

    public const int MaximumPayloadKindLength = 64;

    public const int MaximumDiagnosticCodeLength = 64;
}

/// <summary>
/// Attribution of one normalized event. Tool output is never rewritten as an
/// owner statement (R12): the author crosses the normalization contract as
/// the source itself declared it, and <see cref="Unknown"/> stays unknown.
/// </summary>
public enum SourceEventAuthor
{
    Unknown = 0,
    User = 1,
    Assistant = 2,
    Tool = 3,
    System = 4
}

/// <summary>
/// Context handed to a normalizer for one decode attempt: the enrolled source,
/// its current generation, and the absolute byte offset of the record inside
/// the generation. Spans are never taken from the normalizer; the coordinator
/// overwrites them with the exact consumed range, so a normalizer cannot
/// fabricate provenance (R10/R16).
/// </summary>
public sealed record SourceRecordContext(string SourceId, long Generation, long RecordOffset);

public enum SourceRecordDecodeKind
{
    Consumed = 1,
    NeedMoreBytes = 2,
    Unsupported = 3
}

/// <summary>
/// One normalized event as drafted by a format normalizer, before the
/// coordinator anchors it to its consumed byte range. The payload itself is
/// never carried: reference-only sources keep the original bytes where they
/// are, so the draft carries identity, attribution, time and a digest of the
/// exact record bytes instead of a copy of the body.
/// </summary>
public sealed record SourceEventDraft(
    string EventId,
    SourceEventAuthor Author,
    DateTimeOffset? OccurredAtUtc,
    string PayloadKind,
    string PayloadDigest);

/// <summary>
/// The minimal domain-normalization contract parsing consumers attach
/// through (S3-T2 Codex, S3-T3 OMP). The coordinator owns framing decisions,
/// bounds, idempotence and durability; the normalizer owns record boundaries
/// and interpretation of one record. <see cref="SourceRecordDecodeKind.NeedMoreBytes"/>
/// is how a normalizer reports an incomplete trailing record so the
/// coordinator can leave those bytes unconsumed; <see cref="SourceRecordDecodeKind.Unsupported"/>
/// is how an unknown schema/version is reported explicitly — the coordinator
/// never advances past it, so no event is silently dropped.
/// </summary>
public interface ISourceRecordNormalizer
{
    string Format { get; }

    int FormatVersion { get; }

    SourceRecordDecode Decode(ReadOnlySpan<byte> buffer, SourceRecordContext context);
}

public sealed record SourceRecordDecode(
    SourceRecordDecodeKind Kind,
    int ConsumedBytes = 0,
    SourceEventDraft? Event = null,
    string? Diagnostic = null,
    string? ChainIdentity = null,
    string? ChainParentId = null,
    bool IsVersionBarrier = false);

/// <summary>
/// One accepted normalized event as persisted in the encrypted per-generation
/// journal: stable identity, attribution, occurrence time, payload digest and
/// the exact source span in the generation the event was decoded from. Spans
/// anchor provenance so later consumers re-read the original bytes instead of
/// trusting a stored copy (R16/R18).
/// </summary>
public sealed record NormalizedSourceEvent(
    string EventId,
    SourceEventAuthor Author,
    DateTimeOffset? OccurredAtUtc,
    string PayloadKind,
    string PayloadDigest,
    SourceSpan Span);

/// <summary>
/// One explicit failure recorded beside the checkpoint: the byte offset the
/// coordinator stopped at and a bounded diagnostic code. No body, key or
/// transcript content is ever recorded (R36).
/// </summary>
public sealed record SourceIngestionDiagnostic(long Offset, string Code, DateTimeOffset RecordedAtUtc);
/// <summary>
/// One owner-preserved complete source record, stored in the encrypted
/// ingestion journal. These bytes do not represent an event and carry no
/// attribution. ChainIdentity is present only when an OMP envelope passed its
/// independent identity validation.
/// </summary>
public sealed record PreservedSourceRecord(
    long Offset,
    int Length,
    string Digest,
    DateTimeOffset RetainUntilUtc,
    byte[] Bytes,
    string? ChainIdentity = null)
{
    public long End => Offset + Length;
}

/// <summary>
/// Elements present only in journal schema v2. The raw record bytes live in
/// this same authenticated resource as the checkpoint, so a crash cannot
/// publish continuation without its preservation.
/// </summary>
public sealed record SourceIngestionJournalV2(
    PreservedSourceRecord[] PreservedRecords,
    long? VersionBarrierOffset = null);

public sealed record SourcePreservedRecordDescriptor(
    long Offset,
    int Length,
    string Digest,
    DateTimeOffset RetainUntilUtc);

public sealed record SourcePreservedRecordRead(
    SourcePreservedRecordDescriptor Descriptor,
    byte[] Content);

public sealed record SourceRecordDiagnostic(string Code, long Offset);

public sealed record SourceIngestionSummary(
    string Preservation,
    string Interpretation,
    string Derivation,
    long PendingTailBytes,
    long PreservedBytes,
    int UninterpretedRecords,
    long UnprocessedBytes,
    long PreservedPendingBytes,
    SourceRecordDiagnostic? LastDiagnostic,
    SourcePreservedRecordDescriptor[] PreservedRecords);


/// <summary>
/// The durable per-source generation journal: the generation anchor, the
/// consumed checkpoint, the accepted normalized events and the explicit
/// diagnostics, written as one encrypted vault resource so canonical output
/// and checkpoint are durable together. A crash leaves either the previous
/// revision or the new one, never a half-published mix, and retry re-derives
/// events from the unchanged source prefix (idempotence).
/// </summary>
public sealed record SourceIngestionJournal(
    int SchemaVersion,
    string SourceId,
    string ZoneId,
    long Generation,
    long AnchorLength,
    string AnchorDigest,
    long ConsumedOffset,
    NormalizedSourceEvent[] Events,
    SourceIngestionDiagnostic[] Diagnostics,
    DateTimeOffset LastIngestAtUtc,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    SourceIngestionJournalV2? Version2 = null)
{
    /// <summary>
    /// Reference-only journals retain the original v1 shape. Schema v2 is
    /// written only when it contains preserved bytes or a version barrier.
    /// </summary>
    public const int CurrentSchemaVersion = 1;
    public const int PreservationSchemaVersion = 2;
}

/// <summary>
/// Reserved resource-ID namespace for ingestion journals. Journals are
/// derived state, not canonical records, and the reserved prefix keeps them
/// enumerable from the encrypted manifest while preventing an item or source
/// ID from ever colliding with a journal.
/// </summary>
public static class SourceIngestionIds
{
    public static string JournalIdFor(string sourceId, long generation) =>
        DomainIdentifiers.IngestIdPrefix + sourceId + "-g" +
        generation.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static bool IsJournalId(string? value) =>
        value is not null && value.StartsWith(DomainIdentifiers.IngestIdPrefix, StringComparison.Ordinal) &&
        DomainIdentifiers.IsSafeIdentifier(value);
}

/// <summary>
/// Generation fingerprint over source bytes. The digest is the identity a
/// checkpoint is anchored to: the coordinator refuses to continue (and never
/// splices) when the consumed prefix no longer matches, which is what keeps a
/// replacement or truncation from being invisibly merged into the prior
/// generation.
/// </summary>
public static class SourceFingerprint
{
    public static string Compute(ReadOnlySpan<byte> content)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(content, hash);
        return Convert.ToBase64String(hash);
    }

    public static bool Matches(string? expectedDigest, ReadOnlySpan<byte> content)
    {
        if (string.IsNullOrEmpty(expectedDigest))
        {
            return false;
        }

        byte[]? expected;
        try
        {
            expected = Convert.FromBase64String(expectedDigest);
        }
        catch (FormatException)
        {
            return false;
        }

        if (expected.Length != 32)
        {
            return false;
        }

        Span<byte> actual = stackalloc byte[32];
        SHA256.HashData(content, actual);
        var equal = CryptographicOperations.FixedTimeEquals(expected, actual);
        CryptographicOperations.ZeroMemory(expected);
        return equal;
    }
}

public static class SourceIngestionJournalValidator
{
    public static void Validate(SourceIngestionJournal journal)
    {
        ArgumentNullException.ThrowIfNull(journal);
        if (journal.SchemaVersion is not (SourceIngestionJournal.CurrentSchemaVersion or
            SourceIngestionJournal.PreservationSchemaVersion))
        {
            throw new ArgumentException("The source ingestion journal schema version is unsupported.");
        }

        if (!SourceIngestionIds.IsJournalId(SourceIngestionIds.JournalIdFor(journal.SourceId, journal.Generation)) ||
            !DomainIdentifiers.IsDomainIdentifier(journal.SourceId) ||
            !DomainIdentifiers.IsSafeIdentifier(journal.ZoneId) ||
            journal.Generation < 1)
        {
            throw new ArgumentException("The source ingestion journal identity is invalid.");
        }

        if (journal.AnchorLength < 1 || !IsDigest(journal.AnchorDigest) ||
            journal.ConsumedOffset < 0 || journal.ConsumedOffset > journal.AnchorLength)
        {
            throw new ArgumentException("The source ingestion journal anchor or checkpoint is invalid.");
        }

        if (journal.Events is null || journal.Diagnostics is null)
        {
            throw new ArgumentException("The source ingestion journal payload is invalid.");
        }

        if (journal.Events.Length > SourceIngestionLimits.MaximumJournalEvents ||
            journal.Diagnostics.Length > SourceIngestionLimits.MaximumJournalDiagnostics)
        {
            throw new ArgumentException("The source ingestion journal exceeds its record bounds.");
        }

        var eventIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var normalized in journal.Events)
        {
            ValidateEvent(normalized, journal.AnchorLength);
            if (!eventIds.Add(normalized.EventId))
            {
                throw new ArgumentException("The source ingestion journal repeats an event identity.");
            }
        }

        var diagnostics = new HashSet<long>();
        foreach (var diagnostic in journal.Diagnostics)
        {
            if (diagnostic.Offset < 0 || diagnostic.Offset > journal.AnchorLength ||
                !SourceEnrollmentGrammar.IsDiagnosticCode(diagnostic.Code) || !diagnostics.Add(diagnostic.Offset))
            {
                throw new ArgumentException("The source ingestion journal diagnostic is invalid.");
            }

            if (diagnostic.RecordedAtUtc.Offset != TimeSpan.Zero)
            {
                throw new ArgumentException("The source ingestion journal diagnostic time must use UTC.");
            }
        }

        if (journal.LastIngestAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("The source ingestion journal time must use UTC.");
        }

        var preserved = journal.Version2?.PreservedRecords;
        if (journal.SchemaVersion == SourceIngestionJournal.CurrentSchemaVersion)
        {
            if (journal.Version2 is not null)
            {
                throw new ArgumentException("A v1 ingestion journal cannot contain v2-only state.");
            }
        }
        else if (preserved is null || preserved.Length > SourceIngestionLimits.MaximumJournalDiagnostics ||
                 preserved.Length == 0 && journal.Version2!.VersionBarrierOffset is null)
        {
            throw new ArgumentException("The v2 ingestion journal has no valid v2-only state.");
        }

        if (preserved is { Length: > 0 })
        {
            var ordered = preserved.OrderBy(record => record.Offset).ToArray();
            long previousEnd = -1;
            foreach (var record in ordered)
            {
                ValidatePreservedRecord(record, journal.AnchorLength);
                if (record.Offset < previousEnd)
                {
                    throw new ArgumentException("The source ingestion journal preserved spans overlap.");
                }

                previousEnd = record.End;
            }
        }

        if (journal.Version2?.VersionBarrierOffset is { } barrierOffset)
        {
            if (barrierOffset != journal.ConsumedOffset ||
                preserved is null ||
                !preserved.Any(record => record.Offset == barrierOffset &&
                    record.Length == journal.AnchorLength - barrierOffset))
            {
                throw new ArgumentException("The source ingestion journal version barrier is invalid.");
            }
        }

        ValidateCoverage(journal, preserved);
    }

    public static void ValidateEvent(NormalizedSourceEvent normalized, long anchorLength)
    {
        ArgumentNullException.ThrowIfNull(normalized);
        if (!DomainIdentifiers.IsSafeIdentifier(normalized.EventId) ||
            normalized.EventId.Length > SourceIngestionLimits.MaximumEventIdLength ||
            !Enum.IsDefined(normalized.Author) ||
            !SourceEnrollmentGrammar.IsDiagnosticCode(normalized.PayloadKind) ||
            normalized.PayloadKind.Length > SourceIngestionLimits.MaximumPayloadKindLength ||
            !IsDigest(normalized.PayloadDigest))
        {
            throw new ArgumentException("The source ingestion journal event is invalid.");
        }

        if (normalized.OccurredAtUtc is { } occurredAtUtc && occurredAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("The source ingestion journal event time must use UTC.");
        }

        if (normalized.Span.Start < 0 || normalized.Span.End <= normalized.Span.Start ||
            normalized.Span.End > anchorLength)
        {
            throw new ArgumentException("The source ingestion journal event span is invalid.");
        }
    }

    private static void ValidatePreservedRecord(PreservedSourceRecord record, long anchorLength)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.Offset < 0 || record.Length <= 0 ||
            record.End > anchorLength || record.Bytes is null ||
            record.Bytes.Length != record.Length || !IsDigest(record.Digest) ||
            !SourceFingerprint.Matches(record.Digest, record.Bytes) ||
            record.RetainUntilUtc.Offset != TimeSpan.Zero ||
            record.ChainIdentity is { } chainIdentity &&
            (!DomainIdentifiers.IsSafeIdentifier(chainIdentity) || chainIdentity.Length > 64))
        {
            throw new ArgumentException("The source ingestion journal preserved record is invalid.");
        }
    }

    private static void ValidateCoverage(
        SourceIngestionJournal journal,
        PreservedSourceRecord[]? preserved)
    {
        var spans = new List<(long Start, long End)>(journal.Events.Length + (preserved?.Length ?? 0));
        spans.AddRange(journal.Events.Select(record => (record.Span.Start, record.Span.End)));
        if (preserved is not null)
        {
            spans.AddRange(preserved
                .Where(record => record.Offset < journal.ConsumedOffset)
                .Select(record => (record.Offset, record.End)));
        }

        spans.Sort(static (left, right) => left.Start.CompareTo(right.Start));
        var next = 0L;
        foreach (var span in spans)
        {
            if (span.Start != next || span.End > journal.ConsumedOffset)
            {
                throw new ArgumentException("The source ingestion journal has a gap or overlap before its checkpoint.");
            }

            next = span.End;
        }

        if (next != journal.ConsumedOffset)
        {
            throw new ArgumentException("The source ingestion journal does not cover its checkpoint prefix.");
        }
    }

    private static bool IsDigest(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length != 44 || !value.EndsWith("=", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            return Convert.FromBase64String(value).Length == 32;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

/// <summary>
/// JSON envelope for the encrypted per-source journal. Size-bounded like the
/// other canonical envelopes: an oversized journal fails closed at encode
/// time so a write can never publish a resource the vault cannot read back.
/// </summary>
public static class SourceIngestionJournalCodec
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static byte[] Encode(SourceIngestionJournal journal)
    {
        ArgumentNullException.ThrowIfNull(journal);
        SourceIngestionJournalValidator.Validate(journal);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(journal, JsonOptions);
        if (bytes.Length is <= 0 or > SourceIngestionLimits.MaximumJournalBytes)
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw new ArgumentException("The source ingestion journal exceeds its storage limit.");
        }

        return bytes;
    }

    public static SourceIngestionJournal Decode(byte[] json)
    {
        ArgumentNullException.ThrowIfNull(json);
        SourceIngestionJournal? journal;
        try
        {
            journal = JsonSerializer.Deserialize<SourceIngestionJournal>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("The source ingestion journal schema is invalid.", ex);
        }

        if (journal is null)
        {
            throw new ArgumentException("The source ingestion journal schema is invalid.");
        }

        SourceIngestionJournalValidator.Validate(journal);
        return journal;
    }
}

/// <summary>
/// Registry of format normalizers keyed by exact (format, version). The
/// shipped service registers nothing: S3-T2/S3-T3 attach real parsers, and an
/// unregistered format is an explicit unsupported diagnostic, never a silent
/// best effort. A duplicate (format, version) registration fails loudly
/// instead of silently overriding an existing parser.
/// </summary>
public sealed class SourceNormalizerRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<(string Format, int FormatVersion), ISourceRecordNormalizer> _normalizers = new();

    public void Register(ISourceRecordNormalizer normalizer)
    {
        ArgumentNullException.ThrowIfNull(normalizer);
        if (!SourceEnrollmentGrammar.IsFormat(normalizer.Format) || normalizer.FormatVersion < 1)
        {
            throw new ArgumentException("A source normalizer must declare a valid format and version.");
        }

        lock (_gate)
        {
            if (!_normalizers.TryAdd((normalizer.Format, normalizer.FormatVersion), normalizer))
            {
                throw new InvalidOperationException("A source normalizer is already registered for this format and version.");
            }
        }
    }

    public bool TryResolve(string? format, int formatVersion, out ISourceRecordNormalizer? normalizer)
    {
        normalizer = null;
        if (format is null || formatVersion < 1)
        {
            return false;
        }

        lock (_gate)
        {
            return _normalizers.TryGetValue((format, formatVersion), out normalizer);
        }
    }

    public IReadOnlyList<(string Format, int FormatVersion)> SupportedFormats()
    {
        lock (_gate)
        {
            return _normalizers.Keys
                .OrderBy(key => key.Format, StringComparer.Ordinal)
                .ThenBy(key => key.FormatVersion)
                .ToArray();
        }
    }
}
