using System.Globalization;
using System.Buffers.Binary;
using System.Text;
using System.Security.Cryptography;
using MetaBrain.Core.Index;

namespace MetaBrain.Connections;

/// <summary>
/// The sealed record payloads of the private index file. Every payload is
/// written before it is sealed, so the plaintext of a record exists only in
/// the memory of the write or the read that owns it, and never in a file, a
/// temp file or a journal.
/// </summary>
internal static class PrivateIndexRecords
{

    internal readonly record struct QueryTokenBeacon(
        string Token,
        int Utf8Offset,
        int Utf8Length) : IComparable<QueryTokenBeacon>
    {
        public int CompareTo(QueryTokenBeacon other) => Utf8Order.Instance.Compare(Token, other.Token);
    }

    /// <summary>
    /// The temporal row of one search document: the fields the ranking and
    /// the filters read. The body is never here.
    /// </summary>
    internal static byte[] WriteMeta(SearchIndexDocument document)
    {
        using var buffer = new MemoryStream(256);
        using (var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(document.Revision);
            writer.Write(document.Kind);
            writer.Write(document.Title);
            writer.Write(document.OccurredAtUtc?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty);
            writer.Write(document.RecordedAtUtc.ToString("O", CultureInfo.InvariantCulture));
            writer.Write(document.Publication);
            writer.Write(document.Project);
            writer.Write(document.Environment);
            writer.Write(document.Version);
        }

        return buffer.ToArray();
    }

    /// <summary>The temporal row of one search document, read back.</summary>
    internal static (long Revision, string Kind, string Title, DateTimeOffset? OccurredAtUtc, DateTimeOffset RecordedAtUtc, string Publication, string Project, string Environment, string Version) ReadMeta(byte[] payload)
    {
        using var buffer = new MemoryStream(payload, writable: false);
        using var reader = new BinaryReader(buffer, Encoding.UTF8, leaveOpen: true);
        var revision = reader.ReadInt64();
        var kind = ReadBoundedString(reader, PrivateIndexFormat.MaximumIdentifierBytes);
        var title = ReadBoundedString(reader, PrivateIndexFormat.MaximumMetadataFieldBytes);
        var occurred = ReadBoundedString(reader, 64);
        var recorded = ReadBoundedString(reader, 64);
        var publication = ReadBoundedString(reader, PrivateIndexFormat.MaximumIdentifierBytes);
        var project = ReadBoundedString(reader, PrivateIndexFormat.MaximumMetadataFieldBytes);
        var environment = ReadBoundedString(reader, PrivateIndexFormat.MaximumMetadataFieldBytes);
        var version = ReadBoundedString(reader, PrivateIndexFormat.MaximumMetadataFieldBytes);

        DateTimeOffset? occurredAt = null;
        if (!string.IsNullOrEmpty(occurred) &&
            DateTimeOffset.TryParse(occurred, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsedOccurred))
        {
            occurredAt = parsedOccurred;
        }

        if (!DateTimeOffset.TryParse(recorded, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var recordedAt) ||
            revision < 1 || buffer.Position != buffer.Length)
        {
            throw new InvalidDataException("The private index row metadata is invalid.");
        }

        return (revision, kind, title, occurredAt, recordedAt, publication, project, environment, version);
    }

    /// <summary>The token set of one search document.</summary>
    internal static byte[] WriteTokens(string[] tokens)
    {
        var size = MeasureTokensPayloadBytes(tokens);
        var payload = new byte[size];
        try
        {
            BinaryPrimitives.WriteInt32LittleEndian(payload, tokens.Length);
            var offset = sizeof(int);
            foreach (var token in tokens)
            {
                var tokenBytes = Encoding.UTF8.GetByteCount(token);
                WriteSevenBitLength(payload, ref offset, tokenBytes);
                var written = Encoding.UTF8.GetBytes(token.AsSpan(), payload.AsSpan(offset, tokenBytes));
                if (written != tokenBytes)
                {
                    throw new InvalidDataException("The private index token encoding changed during serialization.");
                }

                offset += written;
            }

            if (offset != size)
            {
                throw new InvalidDataException("The private index token record size changed during serialization.");
            }

            return payload;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(payload);
            throw;
        }
    }

    internal static int MeasureTokensPayloadBytes(string[] tokens)
    {
        if (tokens is null || tokens.Length > PrivateIndexFormat.MaximumSearchTokens)
        {
            throw new InvalidDataException("The private index document has an invalid token count.");
        }

        long size = sizeof(int);
        foreach (var token in tokens)
        {
            if (string.IsNullOrEmpty(token))
            {
                throw new InvalidDataException("The private index document has an empty token.");
            }

            var tokenBytes = Encoding.UTF8.GetByteCount(token);
            if (tokenBytes > PrivateIndexFormat.MaximumTokenBytes)
            {
                throw new InvalidDataException("A private index term is longer than the format allows.");
            }

            size = checked(size + SevenBitLengthBytes(tokenBytes) + tokenBytes);
            if (size > PrivateIndexFormat.MaximumTokensRecordBytes)
            {
                throw new InvalidDataException("The private index token record exceeds its byte bound.");
            }
        }

        return checked((int)size);
    }

    /// <summary>
    /// The token set of one search document, read back in the engine's own
    /// text order. The order is the one the served excerpt is built from, so
    /// it is the engine's byte order over the token text, exactly as the
    /// previous engine's own text order was.
    /// </summary>
    internal static string[] ReadTokens(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Length > PrivateIndexFormat.MaximumTokensRecordBytes)
        {
            throw new InvalidDataException("The private index token set exceeds its byte bound.");
        }

        if (payload.Length < sizeof(int))
        {
            throw new EndOfStreamException();
        }

        var count = BinaryPrimitives.ReadInt32LittleEndian(payload);
        if (count < 0 || count > PrivateIndexFormat.MaximumSearchTokens ||
            count > (payload.Length - sizeof(int)) / 2)
        {
            throw new InvalidDataException("The private index document has an invalid token count.");
        }

        var tokens = new string[count];
        var offset = sizeof(int);
        for (var index = 0; index < count; index++)
        {
            var byteCount = Read7BitEncodedInt(payload, ref offset);
            if (byteCount < 0 || byteCount > PrivateIndexFormat.MaximumTokenBytes)
            {
                throw new InvalidDataException("The private index string is larger than its format bound.");
            }

            if (byteCount == 0)
            {
                throw new InvalidDataException("The private index document has an empty token.");
            }

            if (byteCount > payload.Length - offset)
            {
                throw new InvalidDataException("The private index string is truncated.");
            }

            try
            {
                tokens[index] = StrictUtf8.GetString(payload.AsSpan(offset, byteCount));
            }
            catch (DecoderFallbackException ex)
            {
                throw new InvalidDataException("The private index string is not valid UTF-8.", ex);
            }

            offset += byteCount;
        }

        if (offset != payload.Length)
        {
            throw new InvalidDataException("The private index token set has trailing data.");
        }

        Array.Sort(tokens, Utf8Order.Instance);
        return tokens;
    }

    /// <summary>
    /// Precomputes the exact UTF-8 bytes and full digest for the distinct
    /// query terms. Invalid UTF-16 terms remain in the caller's posting pass
    /// but cannot match a strict UTF-8 document token.
    /// </summary>
    internal static QueryTokenBeacon[] CreateQueryTokenBeacons(
        IEnumerable<string> queryTokens,
        out byte[] queryUtf8)
    {
        ArgumentNullException.ThrowIfNull(queryTokens);
        queryUtf8 = Array.Empty<byte>();
        var beaconCount = 0;
        long totalBytes = 0;
        foreach (var token in queryTokens)
        {
            try
            {
                var byteCount = StrictUtf8.GetByteCount(token.AsSpan());
                totalBytes = checked(totalBytes + byteCount);
                beaconCount++;
            }
            catch (EncoderFallbackException)
            {
                // The existing posting lookup still receives this query
                // string; it simply cannot match a strict UTF-8 token.
            }
        }

        var bytes = totalBytes == 0 ? Array.Empty<byte>() : new byte[checked((int)totalBytes)];
        var beacons = new QueryTokenBeacon[beaconCount];
        var offset = 0;
        var index = 0;
        try
        {
            foreach (var token in queryTokens)
            {
                int byteCount;
                try
                {
                    byteCount = StrictUtf8.GetByteCount(token.AsSpan());
                }
                catch (EncoderFallbackException)
                {
                    continue;
                }

                var tokenBytes = bytes.AsSpan(offset, byteCount);
                var written = StrictUtf8.GetBytes(token.AsSpan(), tokenBytes);
                if (written != byteCount)
                {
                    throw new InvalidDataException("The private index query token encoding changed.");
                }

                beacons[index++] = new QueryTokenBeacon(token, offset, byteCount);
                offset += byteCount;
            }

            if (index != beacons.Length || offset != bytes.Length)
            {
                throw new InvalidDataException("The private index query token set changed during encoding.");
            }

            Array.Sort(beacons);
            queryUtf8 = bytes;
            return beacons;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(bytes);
            Array.Clear(beacons, 0, beacons.Length);
            throw;
        }
    }

    /// <summary>
    /// Validates every authenticated token entry but materializes only exact
    /// query-token matches using exact UTF-8 byte equality. The caller
    /// authenticates and clears the payload buffer and query UTF-8 arena.
    /// </summary>
    internal static string[] ReadMatchingTokens(
        ReadOnlySpan<byte> payload,
        ReadOnlySpan<byte> queryUtf8,
        QueryTokenBeacon[] beacons)
    {
        ArgumentNullException.ThrowIfNull(beacons);
        if (payload.Length > PrivateIndexFormat.MaximumTokensRecordBytes)
        {
            throw new InvalidDataException("The private index token set exceeds its byte bound.");
        }

        if (payload.Length < sizeof(int))
        {
            throw new EndOfStreamException();
        }

        var count = BinaryPrimitives.ReadInt32LittleEndian(payload);
        if (count < 0 || count > PrivateIndexFormat.MaximumSearchTokens)
        {
            throw new InvalidDataException("The private index document has an invalid token count.");
        }

        var offset = sizeof(int);
        string? firstMatch = null;
        List<string>? matched = null;
        for (var index = 0; index < count; index++)
        {
            var byteCount = Read7BitEncodedInt(payload, ref offset);
            if (byteCount < 0 || byteCount > PrivateIndexFormat.MaximumTokenBytes)
            {
                throw new InvalidDataException("The private index string is larger than its format bound.");
            }

            if (byteCount == 0)
            {
                throw new InvalidDataException("The private index document has an empty token.");
            }

            if (byteCount > payload.Length - offset)
            {
                throw new InvalidDataException("The private index string is truncated.");
            }

            var tokenBytes = payload.Slice(offset, byteCount);
            offset += byteCount;
            try
            {
                _ = StrictUtf8.GetCharCount(tokenBytes);
            }
            catch (DecoderFallbackException ex)
            {
                throw new InvalidDataException("The private index string is not valid UTF-8.", ex);
            }

            if (beacons.Length == 0)
            {
                continue;
            }

            var beaconIndex = FindTokenStart(tokenBytes, queryUtf8, beacons);
            while (beaconIndex < beacons.Length)
            {
                var beacon = beacons[beaconIndex++];
                if (!tokenBytes.SequenceEqual(queryUtf8.Slice(beacon.Utf8Offset, beacon.Utf8Length)))
                {
                    break;
                }

                if (firstMatch is null)
                {
                    firstMatch = beacon.Token;
                }
                else
                {
                    matched ??= new List<string>(4) { firstMatch };
                    matched.Add(beacon.Token);
                }
            }
        }

        if (offset != payload.Length)
        {
            throw new InvalidDataException("The private index token set has trailing data.");
        }

        if (firstMatch is null)
        {
            return Array.Empty<string>();
        }

        if (matched is null)
        {
            return new[] { firstMatch };
        }

        var result = matched.ToArray();
        Array.Sort(result, Utf8Order.Instance);
        return result;
    }

    private static int FindTokenStart(
        ReadOnlySpan<byte> tokenBytes,
        ReadOnlySpan<byte> queryUtf8,
        QueryTokenBeacon[] beacons)
    {
        var low = 0;
        var high = beacons.Length;
        while (low < high)
        {
            var middle = low + ((high - low) / 2);
            var beacon = beacons[middle];
            var comparison = tokenBytes.SequenceCompareTo(queryUtf8.Slice(beacon.Utf8Offset, beacon.Utf8Length));
            if (comparison > 0)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    private static int Read7BitEncodedInt(ReadOnlySpan<byte> payload, ref int offset)
    {
        uint result = 0;
        for (var shift = 0; shift < 35; shift += 7)
        {
            if (offset >= payload.Length)
            {
                throw new EndOfStreamException();
            }

            var current = payload[offset++];
            if (shift == 28 && current > 0x0F)
            {
                throw new FormatException("The private index string length is too large.");
            }

            result |= (uint)(current & 0x7F) << shift;
            if ((current & 0x80) == 0)
            {
                if (shift != 0 && (current & 0x7F) == 0)
                {
                    throw new FormatException("The private index string length is not minimally encoded.");
                }

                return unchecked((int)result);
            }
        }

        throw new FormatException("The private index string length uses more than five bytes.");
    }

    /// <summary>
    /// One materialized row: the identity, the zone, the revision, the kind,
    /// the state and the canonical IDs the record references. It carries no
    /// body, no title and no token.
    /// </summary>
    internal static byte[] WriteResourceRow(PrivateIndexRow row)
    {
        using var buffer = new MemoryStream(256);
        using (var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(row.ResourceId);
            writer.Write(row.ZoneId);
            writer.Write(row.Revision);
            writer.Write(row.CanonicalSchemaVersion);
            writer.Write(row.RecordKind.ToString().ToLowerInvariant());
            writer.Write(row.State);
            writer.Write(string.Join(',', row.LinkedResourceIds));
        }

        return buffer.ToArray();
    }

    /// <summary>One materialized row, read back.</summary>
    internal static PrivateIndexRow ReadResourceRow(byte[] payload)
    {
        using var buffer = new MemoryStream(payload, writable: false);
        using var reader = new BinaryReader(buffer, Encoding.UTF8, leaveOpen: true);
        var resourceId = ReadBoundedString(reader, PrivateIndexFormat.MaximumIdentifierBytes);
        var zoneId = ReadBoundedString(reader, PrivateIndexFormat.MaximumIdentifierBytes);
        var revision = reader.ReadInt64();
        var canonicalSchemaVersion = reader.ReadInt32();
        var recordKind = ReadBoundedString(reader, PrivateIndexFormat.MaximumIdentifierBytes);
        var state = ReadBoundedString(reader, PrivateIndexFormat.MaximumStateBytes);
        var linked = ReadBoundedString(reader, PrivateIndexFormat.MaximumLinkedIdsPayloadBytes);
        if (string.IsNullOrEmpty(resourceId) || string.IsNullOrEmpty(zoneId) || revision < 1 ||
            canonicalSchemaVersion < 0 || string.IsNullOrEmpty(state) || buffer.Position != buffer.Length)
        {
            throw new InvalidDataException("The private index resource row is invalid.");
        }

        var linkedIds = string.IsNullOrEmpty(linked)
            ? Array.Empty<string>()
            : linked.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (linkedIds.Length > PrivateIndexFormat.MaximumLinkedIds ||
            linkedIds.Any(id => id.Length == 0 || Encoding.UTF8.GetByteCount(id) > PrivateIndexFormat.MaximumIdentifierBytes))
        {
            throw new InvalidDataException("The private index resource row has invalid linked IDs.");
        }

        return new PrivateIndexRow(
            resourceId,
            zoneId,
            revision,
            canonicalSchemaVersion,
            Enum.TryParse<PrivateIndexRecordKind>(recordKind, ignoreCase: true, out var kind) && Enum.IsDefined(kind)
                ? kind
                : PrivateIndexRecordKind.Unknown,
            state,
            linkedIds);
    }

    internal static byte[] WriteBucketPage(
        KeyValuePair<string, (long Offset, int Length)>[] entries,
        int start,
        int count)
    {
        if (count is < 1 or > PrivateIndexFormat.MaximumBucketPageEntries ||
            start < 0 || start > entries.Length - count)
        {
            throw new InvalidDataException("The private index token bucket page is invalid.");
        }

        var size = sizeof(int);
        for (var index = start; index < start + count; index++)
        {
            var entry = entries[index];
            if (!IsBucketKey(entry.Key))
            {
                throw new InvalidDataException("The private index token bucket key is invalid.");
            }

            size += SevenBitLengthBytes(PrivateIndexFormat.MaximumBucketKeyBytes) +
                PrivateIndexFormat.MaximumBucketKeyBytes + sizeof(long) + sizeof(int);
        }

        if (size > PrivateIndexFormat.MaximumBucketPageBytes)
        {
            throw new InvalidDataException("The private index token bucket page exceeds its byte budget.");
        }

        var payload = new byte[size];
        using var buffer = new MemoryStream(payload, 0, payload.Length, writable: true, publiclyVisible: true);
        buffer.SetLength(0);
        using (var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(count);
            for (var index = start; index < start + count; index++)
            {
                var entry = entries[index];
                writer.Write(entry.Key);
                writer.Write(entry.Value.Offset);
                writer.Write(entry.Value.Length);
            }
        }

        if (buffer.Length != size)
        {
            CryptographicOperations.ZeroMemory(payload);
            throw new InvalidDataException("The private index token bucket page size changed during serialization.");
        }

        return payload;
    }

    internal static byte[] WriteBucketRoot(IReadOnlyList<PrivateIndexBucketPage> pages)
    {
        if (pages.Count is < 1 or > PrivateIndexFormat.MaximumBucketPages)
        {
            throw new InvalidDataException("The private index token bucket root is invalid.");
        }

        var payload = new byte[sizeof(int) + pages.Count * (sizeof(long) + sizeof(int) + sizeof(int))];
        BinaryPrimitives.WriteInt32LittleEndian(payload, pages.Count);
        var offset = sizeof(int);
        var entries = 0;
        foreach (var page in pages)
        {
            if (page.Offset < 0 || page.Length <= 0 || page.Length > PrivateIndexFormat.MaximumBucketPageBytes ||
                page.EntryCount is < 1 or > PrivateIndexFormat.MaximumBucketPageEntries)
            {
                throw new InvalidDataException("The private index token bucket root is invalid.");
            }

            BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(offset), page.Offset);
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(offset + sizeof(long)), page.Length);
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(offset + sizeof(long) + sizeof(int)), page.EntryCount);
            offset += sizeof(long) + sizeof(int) + sizeof(int);
            entries += page.EntryCount;
        }

        if (entries > PrivateIndexFormat.MaximumBucketEntries ||
            payload.Length > PrivateIndexFormat.MaximumBucketRecordBytes)
        {
            CryptographicOperations.ZeroMemory(payload);
            throw new InvalidDataException("The private index token bucket root exceeds its bound.");
        }

        return payload;
    }

    internal static PrivateIndexBucketPage[] ReadBucketRoot(byte[] payload)
    {
        if (payload.Length < sizeof(int))
        {
            throw new InvalidDataException("The private index token bucket root is invalid.");
        }

        var count = BinaryPrimitives.ReadInt32LittleEndian(payload);
        if (count is < 1 or > PrivateIndexFormat.MaximumBucketPages ||
            payload.Length != sizeof(int) + count * (sizeof(long) + sizeof(int) + sizeof(int)))
        {
            throw new InvalidDataException("The private index token bucket root is invalid.");
        }

        var pages = new PrivateIndexBucketPage[count];
        var offset = sizeof(int);
        var entries = 0;
        for (var index = 0; index < count; index++)
        {
            var pageOffset = BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(offset));
            var length = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(offset + sizeof(long)));
            var pageEntries = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(offset + sizeof(long) + sizeof(int)));
            if (pageOffset < 0 || length <= 0 || length > PrivateIndexFormat.MaximumBucketPageBytes ||
                pageEntries is < 1 or > PrivateIndexFormat.MaximumBucketPageEntries)
            {
                throw new InvalidDataException("The private index token bucket root is invalid.");
            }

            pages[index] = new PrivateIndexBucketPage(pageOffset, length, pageEntries);
            entries = checked(entries + pageEntries);
            offset += sizeof(long) + sizeof(int) + sizeof(int);
        }

        if (entries > PrivateIndexFormat.MaximumBucketEntries)
        {
            throw new InvalidDataException("The private index token bucket root exceeds its entry bound.");
        }

        return pages;
    }

    internal static Dictionary<string, (long Offset, int Length)> ReadBucketPage(byte[] payload)
    {
        using var buffer = new MemoryStream(payload, writable: false);
        using var reader = new BinaryReader(buffer, Encoding.UTF8, leaveOpen: true);
        var count = reader.ReadInt32();
        if (count is < 1 or > PrivateIndexFormat.MaximumBucketPageEntries)
        {
            throw new InvalidDataException("The private index token bucket page is invalid.");
        }

        var entries = new Dictionary<string, (long, int)>(count, StringComparer.Ordinal);
        string? previous = null;
        for (var index = 0; index < count; index++)
        {
            var token = ReadBoundedString(reader, PrivateIndexFormat.MaximumBucketKeyBytes);
            var offset = reader.ReadInt64();
            var length = reader.ReadInt32();
            if (!IsBucketKey(token) || offset < 0 ||
                length <= 0 || length > PrivateIndexFormat.MaximumPostingRootBytes ||
                (previous is not null && Utf8Order.Instance.Compare(previous, token) >= 0) ||
                !entries.TryAdd(token, (offset, length)))
            {
                throw new InvalidDataException("The private index token bucket page is invalid.");
            }

            previous = token;
        }

        if (buffer.Position != buffer.Length)
        {
            throw new InvalidDataException("The private index token bucket page has trailing data.");
        }

        return entries;
    }

    private static bool IsBucketKey(string? key)
    {
        if (key is null || key.Length != PrivateIndexFormat.MaximumBucketKeyBytes)
        {
            return false;
        }

        foreach (var character in key)
        {
            var isDigit = character >= '0' && character <= '9';
            var isHexLetter = character >= 'A' && character <= 'F';
            if (!isDigit && !isHexLetter)
            {
                return false;
            }
        }

        return true;
    }

    private static int SevenBitLengthBytes(int value) =>
        value < 0x80 ? 1 : value < 0x4000 ? 2 : value < 0x20_0000 ? 3 : value < 0x1000_0000 ? 4 : 5;

    private static void WriteSevenBitLength(byte[] payload, ref int offset, int value)
    {
        if (value < 0)
        {
            throw new InvalidDataException("The private index token length is invalid.");
        }

        var remaining = (uint)value;
        while (remaining >= 0x80)
        {
            payload[offset++] = (byte)(remaining | 0x80);
            remaining >>= 7;
        }

        payload[offset++] = (byte)remaining;
    }

    internal static byte[] WritePostingRoot(IReadOnlyList<PrivateIndexPostingChunk> chunks)
    {
        if (chunks.Count is < 1 or > PrivateIndexFormat.MaximumPostingChunks)
        {
            throw new InvalidDataException("The private index posting root is invalid.");
        }

        var payload = new byte[sizeof(int) + (chunks.Count * (sizeof(long) + sizeof(int) + sizeof(int)))];
        BinaryPrimitives.WriteInt32LittleEndian(payload, chunks.Count);
        var offset = sizeof(int);
        var totalPostings = 0;
        foreach (var chunk in chunks)
        {
            if (chunk.Offset < 0 || chunk.Length <= 0 ||
                chunk.Length > PrivateIndexFormat.MaximumPostingChunkBytes ||
                chunk.Count is < 1 or > PrivateIndexFormat.MaximumPostingChunkPostings)
            {
                throw new InvalidDataException("The private index posting root is invalid.");
            }

            BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(offset), chunk.Offset);
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(offset + sizeof(long)), chunk.Length);
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(offset + sizeof(long) + sizeof(int)), chunk.Count);
            offset += sizeof(long) + sizeof(int) + sizeof(int);
            totalPostings += chunk.Count;
        }

        if (payload.Length > PrivateIndexFormat.MaximumPostingRootBytes ||
            totalPostings > PrivateIndexFormat.MaximumIndexedRows)
        {
            CryptographicOperations.ZeroMemory(payload);
            throw new InvalidDataException("The private index posting root exceeds its bound.");
        }

        return payload;
    }

    internal static PrivateIndexPostingChunk[] ReadPostingRoot(byte[] payload, int documentCount)
    {
        if (payload.Length < sizeof(int))
        {
            throw new InvalidDataException("The private index posting root is invalid.");
        }

        var count = BinaryPrimitives.ReadInt32LittleEndian(payload);
        if (count is < 1 or > PrivateIndexFormat.MaximumPostingChunks ||
            payload.Length != sizeof(int) + (count * (sizeof(long) + sizeof(int) + sizeof(int))))
        {
            throw new InvalidDataException("The private index posting root is invalid.");
        }

        var chunks = new PrivateIndexPostingChunk[count];
        var offset = sizeof(int);
        var totalPostings = 0;
        for (var index = 0; index < count; index++)
        {
            var chunkOffset = BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(offset));
            var length = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(offset + sizeof(long)));
            var postings = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(offset + sizeof(long) + sizeof(int)));
            if (chunkOffset < 0 || length <= 0 || length > PrivateIndexFormat.MaximumPostingChunkBytes ||
                postings is < 1 or > PrivateIndexFormat.MaximumPostingChunkPostings)
            {
                throw new InvalidDataException("The private index posting root is invalid.");
            }

            chunks[index] = new PrivateIndexPostingChunk(chunkOffset, length, postings);
            totalPostings = checked(totalPostings + postings);
            offset += sizeof(long) + sizeof(int) + sizeof(int);
        }

        if (totalPostings > documentCount)
        {
            throw new InvalidDataException("The private index posting root exceeds the document count.");
        }

        return chunks;
    }

    internal static byte[] WritePostingChunk(IReadOnlyList<int> ordinals, int start, int count)
    {
        if (count is < 1 or > PrivateIndexFormat.MaximumPostingChunkPostings ||
            start < 0 || start > ordinals.Count - count)
        {
            throw new InvalidDataException("The private index posting chunk is invalid.");
        }

        var byteCount = 0;
        var previous = -1;
        for (var index = start; index < start + count; index++)
        {
            var ordinal = ordinals[index];
            if (ordinal <= previous || ordinal >= PrivateIndexFormat.MaximumIndexedRows)
            {
                throw new InvalidDataException("The private index postings are not strictly ascending.");
            }

            var delta = ordinal - previous - 1;
            byteCount += delta < 0x80 ? 1 : delta < 0x4000 ? 2 : 3;
            previous = ordinal;
        }

        if (byteCount > PrivateIndexFormat.MaximumPostingChunkBytes)
        {
            throw new InvalidDataException("The private index posting chunk exceeds its byte budget.");
        }

        var payload = new byte[byteCount];
        var offset = 0;
        previous = -1;
        for (var index = start; index < start + count; index++)
        {
            var delta = ordinals[index] - previous - 1;
            while (delta >= 0x80)
            {
                payload[offset++] = (byte)(delta | 0x80);
                delta >>= 7;
            }

            payload[offset++] = (byte)delta;
            previous = ordinals[index];
        }

        return payload;
    }

    internal static int[] ReadPostingChunk(byte[] payload, int expectedCount, int documentCount)
    {
        if (payload.Length > PrivateIndexFormat.MaximumPostingChunkBytes ||
            expectedCount is < 1 or > PrivateIndexFormat.MaximumPostingChunkPostings)
        {
            throw new InvalidDataException("The private index posting chunk is invalid.");
        }

        var ordinals = new int[expectedCount];
        var offset = 0;
        var current = -1L;
        for (var index = 0; index < expectedCount; index++)
        {
            var delta = 0L;
            var shift = 0;
            var terminated = false;
            while (offset < payload.Length && shift <= 14)
            {
                var value = payload[offset++];
                delta |= (long)(value & 0x7F) << shift;
                if ((value & 0x80) == 0)
                {
                    terminated = true;
                    break;
                }

                shift += 7;
            }

            if (!terminated)
            {
                throw new InvalidDataException("The private index posting chunk is truncated.");
            }

            current += delta + 1;
            if (current < 0 || current >= documentCount)
            {
                throw new InvalidDataException("The private index posting chunk is invalid.");
            }

            ordinals[index] = (int)current;
        }

        if (offset != payload.Length)
        {
            throw new InvalidDataException("The private index posting chunk has trailing data.");
        }

        return ordinals;
    }

    /// <summary>
    /// The directory of a generation: every indexed document, every token
    /// bucket and the bounded sample of the materialized rows. The directory
    /// is the one structure the engine keeps resident, and it holds identity
    /// and offsets only, never a body and never a token set.
    /// </summary>
    internal static byte[] WriteDirectory(
        string canonicalDigest,
        int rowCount,
        int documentCount,
        Func<IEnumerable<PrivateIndexDirectoryDocument>> documents,
        long[] bucketOffsets,
        Func<IEnumerable<PrivateIndexDirectoryRow>> rows,
        PrivateIndexSampleEntry[] sample)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(bucketOffsets);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(sample);
        PrivateIndexFormat.RequireIndexedRowCount(rowCount);
        if (documentCount < 0 || documentCount > rowCount ||
            bucketOffsets.Length != PrivateIndexFormat.TokenBucketCount ||
            sample.Length > PrivateIndexFormat.MaximumSnapshotRows)
        {
            throw new InvalidDataException("The private index directory is invalid.");
        }

        using var counter = new CountingStream();
        using (var writer = new BinaryWriter(counter, Encoding.UTF8, leaveOpen: true))
        {
            WriteDirectoryContents(writer, canonicalDigest, rowCount, documentCount, documents, bucketOffsets, rows, sample);
        }

        if (counter.Count is <= 0 or > PrivateIndexFormat.MaximumDirectoryRecordBytes)
        {
            throw new InvalidDataException("The private index directory exceeds its byte budget.");
        }

        var expectedLength = checked((int)counter.Count);
        using var buffer = new MemoryStream(expectedLength);
        using (var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true))
        {
            WriteDirectoryContents(writer, canonicalDigest, rowCount, documentCount, documents, bucketOffsets, rows, sample);
        }

        if (buffer.Length != expectedLength || buffer.Capacity != expectedLength)
        {
            throw new InvalidDataException("The private index directory size changed during serialization.");
        }

        return buffer.GetBuffer();
    }

    private static void WriteDirectoryContents(
        BinaryWriter writer,
        string canonicalDigest,
        int rowCount,
        int documentCount,
        Func<IEnumerable<PrivateIndexDirectoryDocument>> documents,
        long[] bucketOffsets,
        Func<IEnumerable<PrivateIndexDirectoryRow>> rows,
        PrivateIndexSampleEntry[] sample)
    {
        writer.Write(PrivateIndexFormat.CurrentVersion);
        writer.Write(canonicalDigest);
        writer.Write(rowCount);
        writer.Write(documentCount);
        writer.Write(bucketOffsets.Length);
        writer.Write(rowCount);
        writer.Write(sample.Length);

        var count = 0;
        foreach (var document in documents())
        {
            if (count >= documentCount || document.FileOrdinal != count)
            {
                throw new InvalidDataException("The private index document directory is not in file order.");
            }

            writer.Write(document.ResourceId);
            writer.Write(document.Revision);
            writer.Write(document.MetaOffset);
            writer.Write(document.TokenOffset);
            writer.Write(document.FileOrdinal);
            count++;
        }

        if (count != documentCount)
        {
            throw new InvalidDataException("The private index document directory count is inconsistent.");
        }

        foreach (var offset in bucketOffsets)
        {
            writer.Write(offset);
        }

        count = 0;
        foreach (var row in rows())
        {
            if (count >= rowCount || row.Offset < 0)
            {
                throw new InvalidDataException("The private index resource-row directory is invalid.");
            }

            writer.Write(row.Offset);
            count++;
        }

        if (count != rowCount)
        {
            throw new InvalidDataException("The private index resource-row directory count is inconsistent.");
        }

        foreach (var row in sample)
        {
            writer.Write(row.ResourceId);
            writer.Write(row.Revision);
            writer.Write(row.Offset);
        }
    }

    /// <summary>The directory of a generation, read back.</summary>
    internal static DirectoryRecord ReadDirectory(byte[] payload)
    {
        using var buffer = new MemoryStream(payload, writable: false);
        using var reader = new BinaryReader(buffer, Encoding.UTF8, leaveOpen: true);
        var version = reader.ReadInt32();
        PrivateIndexFormat.RequireSupported(version);
        var canonicalDigest = ReadBoundedString(reader, 256);
        var rowCount = reader.ReadInt32();
        var documentCount = reader.ReadInt32();
        var bucketCount = reader.ReadInt32();
        var rowOffsetCount = reader.ReadInt32();
        var sampleCount = reader.ReadInt32();
        PrivateIndexFormat.RequireIndexedRowCount(rowCount);
        if (documentCount < 0 || documentCount > rowCount ||
            bucketCount != PrivateIndexFormat.TokenBucketCount ||
            rowOffsetCount != rowCount ||
            sampleCount < 0 || sampleCount > PrivateIndexFormat.MaximumSnapshotRows || sampleCount > rowCount)
        {
            throw new InvalidDataException("The private index directory is invalid.");
        }
        if (EstimateResidentUpperBound(
                documentCount,
                bucketCount,
                rowOffsetCount,
                sampleCount) > PrivateIndexFormat.MaximumResidentDirectoryBytes)
        {
            throw new InvalidDataException("The private index directory exceeds its resident byte budget.");
        }


        var resourceIds = new string[documentCount];
        var revisions = new long[documentCount];
        var metaOffsets = new long[documentCount];
        var tokenOffsets = new long[documentCount];
        string? previousId = null;
        long previousRevision = 0;
        for (var index = 0; index < documentCount; index++)
        {
            var resourceId = ReadBoundedString(reader, 128);
            var revision = reader.ReadInt64();
            var metaOffset = reader.ReadInt64();
            var tokenOffset = reader.ReadInt64();
            var fileOrdinal = reader.ReadInt32();
            if (string.IsNullOrEmpty(resourceId) || revision < 1 ||
                metaOffset < 0 || tokenOffset < 0 || fileOrdinal != index ||
                (previousId is not null && (string.CompareOrdinal(previousId, resourceId) > 0 ||
                    (string.Equals(previousId, resourceId, StringComparison.Ordinal) && previousRevision >= revision))))
            {
                throw new InvalidDataException("The private index directory is invalid.");
            }

            resourceIds[index] = resourceId;
            revisions[index] = revision;
            metaOffsets[index] = metaOffset;
            tokenOffsets[index] = tokenOffset;
            previousId = resourceId;
            previousRevision = revision;
        }

        var bucketOffsets = new long[bucketCount];
        for (var index = 0; index < bucketCount; index++)
        {
            var offset = reader.ReadInt64();
            if (offset < -1)
            {
                throw new InvalidDataException("The private index directory is invalid.");
            }

            bucketOffsets[index] = offset;
        }

        var rowOffsets = new long[rowOffsetCount];
        for (var index = 0; index < rowOffsetCount; index++)
        {
            var offset = reader.ReadInt64();
            if (offset < 0)
            {
                throw new InvalidDataException("The private index directory is invalid.");
            }

            rowOffsets[index] = offset;
        }

        var sample = new PrivateIndexSampleEntry[sampleCount];
        previousId = null;
        previousRevision = 0;
        for (var index = 0; index < sampleCount; index++)
        {
            var resourceId = ReadBoundedString(reader, 128);
            var revision = reader.ReadInt64();
            var offset = reader.ReadInt64();
            if (string.IsNullOrEmpty(resourceId) || revision < 1 || offset < 0 ||
                (previousId is not null && (string.CompareOrdinal(previousId, resourceId) > 0 ||
                    (string.Equals(previousId, resourceId, StringComparison.Ordinal) && previousRevision >= revision))))
            {
                throw new InvalidDataException("The private index directory sample is invalid.");
            }

            sample[index] = new PrivateIndexSampleEntry(resourceId, revision, offset);
            previousId = resourceId;
            previousRevision = revision;
        }

        if (buffer.Position != buffer.Length)
        {
            throw new InvalidDataException("The private index directory has trailing data.");
        }

        var residentBytes = EstimateResidentBytes(
            resourceIds,
            revisions.Length,
            metaOffsets.Length,
            tokenOffsets.Length,
            bucketOffsets.Length,
            rowOffsets.Length,
            sample);
        return new DirectoryRecord(
            canonicalDigest,
            rowCount,
            resourceIds,
            revisions,
            metaOffsets,
            tokenOffsets,
            bucketOffsets,
            rowOffsets,
            sample,
            residentBytes);
    }

    private static string ReadBoundedString(BinaryReader reader, int maximumUtf8Bytes)
    {
        var byteCount = reader.Read7BitEncodedInt();
        if (byteCount < 0 || byteCount > maximumUtf8Bytes)
        {
            throw new InvalidDataException("The private index string is larger than its format bound.");
        }

        var bytes = reader.ReadBytes(byteCount);
        if (bytes.Length != byteCount)
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw new InvalidDataException("The private index string is truncated.");
        }

        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException("The private index string is not valid UTF-8.", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private static long EstimateResidentBytes(
        string[] resourceIds,
        int revisions,
        int metaOffsets,
        int tokenOffsets,
        int bucketOffsets,
        int rowOffsets,
        PrivateIndexSampleEntry[] sample)
    {
        long bytes = 7 * IntPtr.Size + 128;
        bytes += (long)resourceIds.Length * IntPtr.Size;
        foreach (var resourceId in resourceIds)
        {
            bytes += 24 + ((long)resourceId.Length * sizeof(char));
        }

        bytes += (long)(revisions + metaOffsets + tokenOffsets + bucketOffsets + rowOffsets) * sizeof(long);
        bytes += (long)sample.Length * (sizeof(long) * 2 + IntPtr.Size);
        foreach (var entry in sample)
        {
            bytes += 24 + ((long)entry.ResourceId.Length * sizeof(char));
        }

        return bytes;
    }

    private static long EstimateResidentUpperBound(
        int documentCount,
        int bucketCount,
        int rowOffsetCount,
        int sampleCount)
    {
        var maximumIdentifierBytes = PrivateIndexFormat.MaximumIdentifierBytes;
        var maximumStringBytes = 24L + ((long)maximumIdentifierBytes * sizeof(char));
        var bytes = 7L * IntPtr.Size + 128;
        bytes += (long)documentCount * (IntPtr.Size + maximumStringBytes);
        bytes += (long)(documentCount * 3 + bucketCount + rowOffsetCount) * sizeof(long);
        bytes += (long)sampleCount *
                 ((sizeof(long) * 2L) + IntPtr.Size + maximumStringBytes);
        return bytes;
    }


    /// <summary>
    /// The engine's own text order: the byte order of the token text, which
    /// is the order the previous engine's own text order produced.
    /// </summary>
    private sealed class CountingStream : Stream
    {
        internal long Count { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => Count;
        public override long Position
        {
            get => Count;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Count += count;
        public override void Write(ReadOnlySpan<byte> buffer) => Count += buffer.Length;
        public override void WriteByte(byte value) => Count++;
    }

    internal sealed class Utf8Order : IComparer<string>
    {
        internal static readonly Utf8Order Instance = new();

        public int Compare(string? x, string? y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }

            if (x is null)
            {
                return -1;
            }

            if (y is null)
            {
                return 1;
            }

            var shared = Math.Min(x.Length, y.Length);
            for (var index = 0; index < shared; index++)
            {
                var left = x[index];
                var right = y[index];
                if (left <= 0x7F && right <= 0x7F)
                {
                    if (left != right)
                    {
                        return left < right ? -1 : 1;
                    }

                    continue;
                }

                if (left <= 0x7F)
                {
                    return -1;
                }

                if (right <= 0x7F)
                {
                    return 1;
                }

                return CompareNonAsciiSuffix(x, index, y, index);
            }

            return x.Length.CompareTo(y.Length);
        }

        private static int CompareNonAsciiSuffix(string left, int leftOffset, string right, int rightOffset)
        {
            Span<byte> leftBytes = stackalloc byte[4];
            Span<byte> rightBytes = stackalloc byte[4];
            try
            {
                while (leftOffset < left.Length && rightOffset < right.Length)
                {
                    var leftRune = Rune.TryGetRuneAt(left, leftOffset, out var decodedLeft)
                        ? decodedLeft
                        : Rune.ReplacementChar;
                    var rightRune = Rune.TryGetRuneAt(right, rightOffset, out var decodedRight)
                        ? decodedRight
                        : Rune.ReplacementChar;
                    var leftChars = leftRune.Utf16SequenceLength;
                    var rightChars = rightRune.Utf16SequenceLength;
                    var leftLength = leftRune.EncodeToUtf8(leftBytes);
                    var rightLength = rightRune.EncodeToUtf8(rightBytes);
                    var runeShared = Math.Min(leftLength, rightLength);
                    for (var index = 0; index < runeShared; index++)
                    {
                        if (leftBytes[index] != rightBytes[index])
                        {
                            return leftBytes[index] < rightBytes[index] ? -1 : 1;
                        }
                    }

                    if (leftLength != rightLength)
                    {
                        return leftLength.CompareTo(rightLength);
                    }

                    leftOffset += leftChars;
                    rightOffset += rightChars;
                }

                return (left.Length - leftOffset).CompareTo(right.Length - rightOffset);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(leftBytes);
                CryptographicOperations.ZeroMemory(rightBytes);
            }
        }
    }
}

internal readonly record struct PrivateIndexPostingChunk(long Offset, int Length, int Count);
internal readonly record struct PrivateIndexBucketPage(long Offset, int Length, int EntryCount);

internal readonly record struct PrivateIndexSampleEntry(string ResourceId, long Revision, long Offset);

/// <summary>The resident directory of one materialized generation.</summary>
internal sealed record DirectoryRecord(
    string CanonicalDigest,
    int RowCount,
    string[] ResourceIds,
    long[] Revisions,
    long[] MetaOffsets,
    long[] TokenOffsets,
    long[] BucketOffsets,
    long[] RowOffsets,
    PrivateIndexSampleEntry[] Sample,
    long ResidentBytes);
