using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace MetaBrain.Connections;

/// <summary>
/// A scratch stream for directory metadata during one private-index rebuild.
/// Each fixed-size entry is sealed with the vault-derived index key and a
/// spill-specific file identity; the file is deleted on commit/rollback and
/// is reaped before the next rebuild.
/// </summary>
internal sealed class PrivateIndexDirectorySpill : IDisposable
{
    internal const int PayloadBytes = 166;
    private const int IdentifierOffset = 2;
    private const int IdentifierCapacity = 128;
    private const int RevisionOffset = IdentifierOffset + IdentifierCapacity;
    private const int MetaOffsetOffset = RevisionOffset + sizeof(long);
    private const int TokenOffsetOffset = MetaOffsetOffset + sizeof(long);
    private const int FileOrdinalOffset = TokenOffsetOffset + sizeof(long);
    private const int RowOffsetOffset = FileOrdinalOffset + sizeof(int);
    private const byte RowEntry = 1;
    private const byte DocumentEntry = 2;
    private const int FrameBytes = sizeof(int) + 12 + PayloadBytes + 16;

    private readonly string _path;
    private readonly byte[] _key;
    private readonly byte[] _fileId = RandomNumberGenerator.GetBytes(16);
    private readonly byte[] _payload = new byte[PayloadBytes];
    private readonly PrivateIndexNonceSequence _nonces = new();
    private readonly FileStream _stream;
    private long _position;
    private bool _disposed;

    internal PrivateIndexDirectorySpill(string path, byte[] key)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(key);
        _path = path;
        _key = key;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.SequentialScan);
    }

    internal void AddRow(string resourceId, long offset)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Array.Clear(_payload);
        _payload[0] = RowEntry;
        WriteIdentifier(resourceId);
        BinaryPrimitives.WriteInt64LittleEndian(_payload.AsSpan(RowOffsetOffset), offset);
        Append();
    }

    internal void AddDocument(string resourceId, long revision, long metaOffset, long tokenOffset, int fileOrdinal)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Array.Clear(_payload);
        _payload[0] = DocumentEntry;
        WriteIdentifier(resourceId);
        BinaryPrimitives.WriteInt64LittleEndian(_payload.AsSpan(RevisionOffset), revision);
        BinaryPrimitives.WriteInt64LittleEndian(_payload.AsSpan(MetaOffsetOffset), metaOffset);
        BinaryPrimitives.WriteInt64LittleEndian(_payload.AsSpan(TokenOffsetOffset), tokenOffset);
        BinaryPrimitives.WriteInt32LittleEndian(_payload.AsSpan(FileOrdinalOffset), fileOrdinal);
        Append();
    }

    internal IEnumerable<PrivateIndexDirectoryDocument> ReadDocuments()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _stream.Flush();
        for (var offset = 0L; offset < _position; offset += FrameBytes)
        {
            var payload = EncryptedPrivateIndexStore.ReadSealedRecord(
                _stream,
                _key,
                _fileId,
                EncryptedPrivateIndexStore.RecordKind.DirectorySpill,
                offset,
                PayloadBytes);
            try
            {
                if (payload[0] != DocumentEntry)
                {
                    continue;
                }

                yield return new PrivateIndexDirectoryDocument(
                    ReadIdentifier(payload),
                    BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(RevisionOffset)),
                    BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(MetaOffsetOffset)),
                    BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(TokenOffsetOffset)),
                    BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(FileOrdinalOffset)));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(payload);
            }
        }
    }

    internal IEnumerable<PrivateIndexDirectoryRow> ReadRows()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _stream.Flush();
        for (var offset = 0L; offset < _position; offset += FrameBytes)
        {
            var payload = EncryptedPrivateIndexStore.ReadSealedRecord(
                _stream,
                _key,
                _fileId,
                EncryptedPrivateIndexStore.RecordKind.DirectorySpill,
                offset,
                PayloadBytes);
            try
            {
                if (payload[0] != RowEntry)
                {
                    continue;
                }

                yield return new PrivateIndexDirectoryRow(
                    ReadIdentifier(payload),
                    BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(RowOffsetOffset)));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(payload);
            }
        }
    }

    private void WriteIdentifier(string resourceId)
    {
        if (string.IsNullOrEmpty(resourceId) || resourceId.Length > IdentifierCapacity)
        {
            throw new InvalidDataException("The private index directory identifier is invalid.");
        }

        _payload[1] = checked((byte)resourceId.Length);
        for (var index = 0; index < resourceId.Length; index++)
        {
            var character = resourceId[index];
            if (!char.IsAscii(character))
            {
                throw new InvalidDataException("The private index directory identifier is invalid.");
            }

            _payload[IdentifierOffset + index] = (byte)character;
        }
    }

    private static string ReadIdentifier(byte[] payload)
    {
        var length = payload[1];
        if (length is 0 or > IdentifierCapacity)
        {
            throw new InvalidDataException("The private index directory identifier is invalid.");
        }

        return Encoding.ASCII.GetString(payload, IdentifierOffset, length);
    }

    private void Append()
    {
        try
        {
            EncryptedPrivateIndexStore.WriteRecord(
                _stream,
                _key,
                _fileId,
                _nonces.Next(),
                EncryptedPrivateIndexStore.RecordKind.DirectorySpill,
                _position,
                _payload);
            _position += FrameBytes;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(_payload);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stream.Dispose();
        CryptographicOperations.ZeroMemory(_payload);
        CryptographicOperations.ZeroMemory(_fileId);
        try
        {
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The encrypted spill is not read by Open and is reaped at the next rebuild.
        }
    }
}

internal readonly record struct PrivateIndexDirectoryDocument(
    string ResourceId,
    long Revision,
    long MetaOffset,
    long TokenOffset,
    int FileOrdinal);

internal readonly record struct PrivateIndexDirectoryRow(string ResourceId, long Offset);
