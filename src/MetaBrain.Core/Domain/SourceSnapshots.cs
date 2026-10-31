using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using MetaBrain.Core.Security;

namespace MetaBrain.Core.Domain;

/// <summary>
/// One observed state at the exact owner-enrolled locator. A missing path is
/// not guessed to have moved: relocation is a separate owner-confirmed action.
/// </summary>
public enum SourceAvailabilityObservation
{
    NotChecked = 0,
    Available = 1,
    Missing = 2,
    Changed = 3,
    Unreadable = 4,
    TooLarge = 5,
    Empty = 6
}

/// <summary>
/// One immutable selected copy of an enrolled source. The byte payload and
/// lineage header are encoded together as one ordinary revision-gated vault
/// resource, so a crash cannot publish metadata without its content (or vice
/// versa). Resource identity, zone and revision are independently protected by
/// the shared vault manifest and agent session policy.
/// </summary>
public sealed record StoredSourceSnapshot(
    int SchemaVersion,
    string Id,
    string ZoneId,
    string SourceId,
    long SourceRevision,
    long SourceGeneration,
    long SourceLength,
    string SourceDigest,
    DateTimeOffset CapturedAtUtc,
    DateTimeOffset RetainUntilUtc,
    bool Purged);

public sealed record SourceSnapshotMetadata(StoredSourceSnapshot Stored, long Revision);

public sealed record SourceSnapshot(
    StoredSourceSnapshot Stored,
    long Revision,
    byte[] Content);

/// <summary>
/// Reserved resource-ID namespace for source-generation snapshots. The stable
/// source/generation identity lets the owner find retained generations after
/// restart; a purge or retention change advances the canonical revision, so a
/// grant frozen to an older revision never becomes valid again.
/// </summary>
public static class SourceSnapshotIds
{
    public const string Prefix = DomainIdentifiers.SourceSnapshotIdPrefix;

    public static string For(string sourceId, long generation) =>
        Prefix + sourceId + "-g" + generation.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static bool IsSnapshotId(string? value) =>
        value is not null && value.StartsWith(Prefix, StringComparison.Ordinal) &&
        DomainIdentifiers.IsSafeIdentifier(value);

    public static bool BelongsToSource(string? value, string sourceId)
    {
        var prefix = For(sourceId, 1)[..^1];
        return IsSnapshotId(value) && value!.StartsWith(prefix, StringComparison.Ordinal) &&
            long.TryParse(value.AsSpan(prefix.Length), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var generation) && generation > 0;
    }
}

/// <summary>
/// Binary envelope for a selected snapshot: magic + bounded JSON lineage
/// header + exact source bytes. The entire envelope is encrypted by the normal
/// managed-resource store; no plaintext snapshot, index, or sidecar file is
/// created. Purging revises the same resource to a content-free tombstone,
/// which makes the ordinary staged-ciphertext commit remove the prior body.
/// </summary>
public static class SourceSnapshotCodec
{
    private const int PrefixBytes = 8;
    private static ReadOnlySpan<byte> Magic => "MBSS"u8;
    public const int MaximumHeaderBytes = 8 * 1024;
    public const int MaximumStoredBytes = SourceIngestionLimits.MaximumSourceBytes + PrefixBytes + MaximumHeaderBytes;
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public static byte[] EncodeStored(StoredSourceSnapshot snapshot, ReadOnlySpan<byte> content)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidateStored(snapshot, content);
        var header = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions);
        if (header.Length is <= 0 or > MaximumHeaderBytes)
        {
            CryptographicOperations.ZeroMemory(header);
            throw new ArgumentException("The source snapshot header exceeds its storage limit.");
        }

        var encoded = new byte[PrefixBytes + header.Length + content.Length];
        Magic.CopyTo(encoded);
        BinaryPrimitives.WriteInt32BigEndian(encoded.AsSpan(Magic.Length, sizeof(int)), header.Length);
        header.CopyTo(encoded, PrefixBytes);
        content.CopyTo(encoded.AsSpan(PrefixBytes + header.Length));
        CryptographicOperations.ZeroMemory(header);
        return encoded;
    }

    public static SourceSnapshot DecodeStored(byte[] encoded, long revision)
    {
        ArgumentNullException.ThrowIfNull(encoded);
        if (revision < 1 || encoded.Length < PrefixBytes || !encoded.AsSpan(0, Magic.Length).SequenceEqual(Magic))
        {
            throw new ArgumentException("The source snapshot envelope is invalid.");
        }

        var headerLength = BinaryPrimitives.ReadInt32BigEndian(encoded.AsSpan(Magic.Length, sizeof(int)));
        if (headerLength is <= 0 or > MaximumHeaderBytes || PrefixBytes + headerLength > encoded.Length)
        {
            throw new ArgumentException("The source snapshot header is invalid.");
        }

        StoredSourceSnapshot? stored;
        try
        {
            stored = JsonSerializer.Deserialize<StoredSourceSnapshot>(
                encoded.AsSpan(PrefixBytes, headerLength), JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("The source snapshot schema is invalid.", ex);
        }

        if (stored is null)
        {
            throw new ArgumentException("The source snapshot schema is invalid.");
        }

        var content = encoded.AsSpan(PrefixBytes + headerLength).ToArray();
        try
        {
            ValidateStored(stored, content);
            return new SourceSnapshot(stored, revision, content);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(content);
            throw;
        }
    }

    public static void ValidateStored(StoredSourceSnapshot snapshot, ReadOnlySpan<byte> content)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.SchemaVersion != CurrentSchemaVersion ||
            !SourceSnapshotIds.IsSnapshotId(snapshot.Id) ||
            !string.Equals(snapshot.Id, SourceSnapshotIds.For(snapshot.SourceId, snapshot.SourceGeneration), StringComparison.Ordinal) ||
            !DomainIdentifiers.IsDomainIdentifier(snapshot.SourceId) ||
            !DomainIdentifiers.IsSafeIdentifier(snapshot.ZoneId) ||
            snapshot.SourceLength is < 1 or > SourceIngestionLimits.MaximumSourceBytes ||
            !SourceEnrollmentGrammar.IsAnchorDigest(snapshot.SourceDigest) ||
            snapshot.CapturedAtUtc.Offset != TimeSpan.Zero ||
            snapshot.RetainUntilUtc.Offset != TimeSpan.Zero ||
            snapshot.RetainUntilUtc <= snapshot.CapturedAtUtc ||
            snapshot.CapturedAtUtc > DateTimeOffset.UtcNow.AddMinutes(5))
        {
            throw new ArgumentException("The source snapshot lineage or retention metadata is invalid.");
        }

        if (snapshot.Purged)
        {
            if (!content.IsEmpty)
            {
                throw new ArgumentException("A purged source snapshot cannot retain content.");
            }

            return;
        }

        if (content.Length != snapshot.SourceLength ||
            !SourceFingerprint.Matches(snapshot.SourceDigest, content))
        {
            throw new ArgumentException("The source snapshot content does not match its lineage digest.");
        }
    }
}
