using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using MetaBrain.Core.Index;

namespace MetaBrain.Connections;

/// <summary>
/// S8-T5 the bounded builder of the postings of one private-index generation.
/// <para>
/// The postings of a generation are the pairs (term, document ordinal). A
/// vault holds as many pairs as it holds term occurrences, so the builder
/// never holds them all: it accumulates a bounded arena of pairs, sorts the
/// arena, and seals each digest group once with delta-varint ordinals into an
/// encrypted spill file.
/// The runs are then merged with a bounded fan-in — one more pass whenever
/// there are more runs than the fan-in allows — and the final merge writes
/// the posting list of every term and the bucket of every term into the
/// staged generation.
/// </para>
/// <para>
/// Every byte of the spill is sealed with the generation's own key under the
/// spill's own file identity, so the spill is ciphertext like the generation
/// itself, and it is deleted when the rebuild publishes or is abandoned. A
/// plaintext spill, a plaintext temporary file and a plaintext journal are
/// never written.
/// </para>
/// </summary>
internal sealed class PrivateIndexPostingBuilder : IDisposable
{
    /// <summary>The versioned header of one encrypted grouped-digest run chunk.</summary>
    private const int ScratchCodecVersion = 2;
    private const int ChunkHeaderBytes = 3 * sizeof(int);
    private readonly string _spillPath;
    private readonly byte[] _key;
    private readonly byte[] _fileId;
    private readonly FileStream _spill;
    private readonly PrivateIndexNonceSequence _spillNonces = new();
    private static readonly int MaximumRunLevelCount = CalculateMaximumRunLevelCount();
    private static readonly int MaximumRetainedRunCount =
        MaximumRunLevelCount * (PrivateIndexFormat.MergeFanIn - 1);
    private readonly Encoder _termEncoder = Encoding.UTF8.GetEncoder();
    private readonly IncrementalHash _termHasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private byte[] _termScratch = new byte[4096];
    private byte[] _arena = new byte[PrivateIndexFormat.MaximumPostingArenaBytes];
    private Pair[] _pairs = new Pair[PrivateIndexFormat.MaximumPostingPairsPerRun];
    private Pair[] _spillChunkPairs = new Pair[PrivateIndexFormat.MaximumSpillChunkPairs];
    private byte[] _spillChunkText = new byte[PrivateIndexFormat.MaximumSpillChunkPairs * PrivateIndexFormat.MaximumTermKeyBytes];
    private readonly List<List<RunRef>> _runLevels = new(MaximumRunLevelCount);
    private readonly ArenaOrder _order;
    private int _arenaLength;
    private int _pairCount;
    private long _totalPairCount;
    private long _spillPosition;
    private bool _disposed;

    /// <summary>One (term, document ordinal) pair, pointing into the arena.</summary>
    private struct Pair
    {
        public int Bucket;
        public int Offset;
        public int Length;
        public int Ordinal;
    }

    /// <summary>The byte range containing one contiguous sorted spill run.</summary>
    private readonly record struct RunRef(long StartOffset, long EndOffset);
    /// <summary>
    /// Opens the encrypted spill of one rebuild. The spill is a scratch file
    /// beside the staged generation: it holds ciphertext only and it never
    /// outlives the rebuild.
    /// </summary>
    internal PrivateIndexPostingBuilder(string spillPath, byte[] key)
    {
        ArgumentException.ThrowIfNullOrEmpty(spillPath);
        ArgumentNullException.ThrowIfNull(key);
        _spillPath = spillPath;
        _key = key;
        // The spill has its own file identity: a spill record can never be
        // read as a record of the staged or the published generation.
        _fileId = RandomNumberGenerator.GetBytes(16);
        Directory.CreateDirectory(Path.GetDirectoryName(spillPath)!);
        _spill = new FileStream(spillPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 64 * 1024, FileOptions.SequentialScan);
        _spillPosition = 0;
        _order = new ArenaOrder(this);
    }

    /// <summary>The number of compacted sorted runs currently retained.</summary>
    internal int RunCount
    {
        get
        {
            var count = 0;
            foreach (var level in _runLevels)
            {
                count += level.Count;
            }

            return count;
        }
    }

    /// <summary>
    /// Adds one term occurrence. The pair is buffered in the arena; when the
    /// arena is full the run is sorted, sealed into the spill and the arena
    /// starts again. The working set of the builder is therefore bounded by
    /// the arena and the pair table, not by the vault.
    /// </summary>
    internal void Add(string token, int ordinal)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var byteCount = Encoding.UTF8.GetByteCount(token);
        if (byteCount > PrivateIndexFormat.MaximumTokenBytes)
        {
            throw new InvalidDataException("A private index term is longer than the format allows.");
        }

        if (_totalPairCount >= PrivateIndexFormat.MaximumPostingPairs ||
            _pairCount >= _pairs.Length ||
            _arenaLength + PrivateIndexFormat.MaximumTermKeyBytes > _arena.Length)
        {
            if (_totalPairCount >= PrivateIndexFormat.MaximumPostingPairs)
            {
                throw new InvalidDataException("The private index posting count exceeds the format limit.");
            }

            FlushRun();
        }

        Span<byte> digest = stackalloc byte[PrivateIndexFormat.MaximumTermKeyBytes];
        try
        {
            ComputeTermDigest(token, digest);
            digest.CopyTo(_arena.AsSpan(_arenaLength, digest.Length));
            _pairs[_pairCount] = new Pair
            {
                Bucket = BucketOfDigest(digest),
                Offset = _arenaLength,
                Length = digest.Length,
                Ordinal = ordinal,
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(digest);
        }

        _pairCount++;
        _totalPairCount++;
        _arenaLength += PrivateIndexFormat.MaximumTermKeyBytes;
    }

    private void ComputeTermDigest(string token, Span<byte> digest)
    {
        _termEncoder.Reset();
        var remaining = token.AsSpan();
        try
        {
            while (true)
            {
                _termEncoder.Convert(
                    remaining,
                    _termScratch,
                    flush: true,
                    out var charsUsed,
                    out var bytesUsed,
                    out var completed);
                try
                {
                    _termHasher.AppendData(_termScratch.AsSpan(0, bytesUsed));
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(_termScratch.AsSpan(0, bytesUsed));
                }

                remaining = remaining[charsUsed..];
                if (completed)
                {
                    break;
                }

                if (charsUsed == 0 && bytesUsed == 0)
                {
                    throw new InvalidDataException("A private index term could not be encoded within its bound.");
                }
            }

            if (!_termHasher.TryGetHashAndReset(digest, out var written) || written != digest.Length)
            {
                throw new CryptographicException("The private index term key could not be computed.");
            }
        }
        catch
        {
            var discarded = _termScratch.AsSpan(0, PrivateIndexFormat.MaximumTermKeyBytes);
            if (_termHasher.TryGetHashAndReset(discarded, out _))
            {
                CryptographicOperations.ZeroMemory(discarded);
            }

            throw;
        }
        finally
        {
            _termEncoder.Reset();
        }
    }

    /// <summary>
    /// Merges the spilled runs into the final posting lists and the final
    /// term buckets of the generation. The merge reads the spill in bounded
    /// chunks and writes the generation's records through
    /// <paramref name="sink"/>.
    /// </summary>
    internal void Merge(IPrivateIndexGenerationSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_pairCount > 0)
        {
            FlushRun();
        }

        var runs = new List<RunRef>(MaximumRetainedRunCount);
        foreach (var level in _runLevels)
        {
            runs.AddRange(level);
        }

        if (runs.Count > MaximumRetainedRunCount)
        {
            throw new InvalidDataException("The private index merge exceeded its source-derived run-reference bound.");
        }

        while (runs.Count > PrivateIndexFormat.MergeFanIn)
        {
            var next = new List<RunRef>((runs.Count + PrivateIndexFormat.MergeFanIn - 1) / PrivateIndexFormat.MergeFanIn);
            for (var index = 0; index < runs.Count; index += PrivateIndexFormat.MergeFanIn)
            {
                var count = Math.Min(PrivateIndexFormat.MergeFanIn, runs.Count - index);
                if (count == 1)
                {
                    next.Add(runs[index]);
                }
                else
                {
                    next.Add(MergeToRun(runs.GetRange(index, count)));
                }
            }

            runs = next;
        }


        MergeToGeneration(runs, sink);
    }

    /// <summary>
    /// Merges a group of runs into one new run of the spill. The pairs of
    /// the group are read in order and sealed into new run chunks.
    /// </summary>
    private RunRef MergeToRun(IReadOnlyList<RunRef> group)
    {
        var startOffset = _spillPosition;
        var cursors = new List<RunCursor>(group.Count);
        var pairCount = 0;
        var textBytes = 0;
        try
        {
            foreach (var run in group)
            {
                cursors.Add(new RunCursor(_spill, _key, _fileId, run));
            }

            while (true)
            {
                var chosen = -1;
                for (var index = 0; index < cursors.Count; index++)
                {
                    if (!cursors[index].HasCurrent)
                    {
                        continue;
                    }

                    if (chosen < 0 || Compare(cursors[index], cursors[chosen]) < 0)
                    {
                        chosen = index;
                    }
                }

                if (chosen < 0)
                {
                    break;
                }

                var cursor = cursors[chosen];
                cursor.Token.CopyTo(_spillChunkText.AsSpan(textBytes, cursor.TokenLength));
                _spillChunkPairs[pairCount++] = new Pair
                {
                    Bucket = cursor.Bucket,
                    Offset = textBytes,
                    Length = cursor.TokenLength,
                    Ordinal = cursor.Ordinal,
                };
                textBytes += cursor.TokenLength;
                cursor.Advance();

                if (pairCount == PrivateIndexFormat.MaximumSpillChunkPairs)
                {
                    WriteChunk(_spillChunkPairs, pairCount, _spillChunkText, textBytes);
                    pairCount = 0;
                    textBytes = 0;
                }
            }

            if (pairCount > 0)
            {
                WriteChunk(_spillChunkPairs, pairCount, _spillChunkText, textBytes);
                pairCount = 0;
            }

            if (_spillPosition == startOffset)
            {
                throw new InvalidDataException("An empty private index spill run cannot be merged.");
            }

            return new RunRef(startOffset, _spillPosition);
        }
        finally
        {
            foreach (var cursor in cursors)
            {
                cursor.Dispose();
            }

            CryptographicOperations.ZeroMemory(_spillChunkText);
            Array.Clear(_spillChunkPairs, 0, _spillChunkPairs.Length);
        }
    }

    /// <summary>
    /// Merges the last runs into the generation: the posting list of every
    /// term and the term bucket of every bucket of terms.
    /// </summary>
    private void MergeToGeneration(IReadOnlyList<RunRef> runs, IPrivateIndexGenerationSink sink)
    {
        var cursors = new List<RunCursor>(runs.Count);

        var bucketOffsets = new long[PrivateIndexFormat.TokenBucketCount];
        Array.Fill(bucketOffsets, -1L);
        var currentBucket = -1;
        var currentToken = Array.Empty<byte>();
        var ordinals = new List<int>(PrivateIndexFormat.MaximumPostingChunkPostings);
        var chunks = new List<PrivateIndexPostingChunk>(PrivateIndexFormat.MaximumPostingChunks);
        var termPostingCount = 0;
        var lastOrdinal = -1;
        var entries = new Dictionary<string, (long Offset, int Length)>(StringComparer.Ordinal);
        try
        {
            foreach (var run in runs)
            {
                cursors.Add(new RunCursor(_spill, _key, _fileId, run));
            }

            while (true)
            {
                var chosen = -1;
                for (var index = 0; index < cursors.Count; index++)
                {
                    if (!cursors[index].HasCurrent)
                    {
                        continue;
                    }

                    if (chosen < 0 || Compare(cursors[index], cursors[chosen]) < 0)
                    {
                        chosen = index;
                    }
                }

                if (chosen < 0)
                {
                    break;
                }

                var cursor = cursors[chosen];
                var token = cursor.Token;
                if (cursor.Bucket != currentBucket)
                {
                    FlushTerm(ref currentToken, ordinals, chunks, entries, sink);
                    FlushBucket(currentBucket, entries, bucketOffsets, sink);
                    currentBucket = cursor.Bucket;
                    currentToken = token.ToArray();
                    termPostingCount = 0;
                    lastOrdinal = -1;
                }
                else if (!token.SequenceEqual(currentToken))
                {
                    FlushTerm(ref currentToken, ordinals, chunks, entries, sink);
                    currentToken = token.ToArray();
                    termPostingCount = 0;
                    lastOrdinal = -1;
                }

                if (cursor.Ordinal == lastOrdinal)
                {
                    // Digest collisions only widen the candidate selector;
                    // document-token verification decides exact membership.
                    cursor.Advance();
                    continue;
                }

                if (cursor.Ordinal < lastOrdinal || termPostingCount >= PrivateIndexFormat.MaximumIndexedRows)
                {
                    throw new InvalidDataException("The private index postings are not in ascending order or exceed the row limit.");
                }

                ordinals.Add(cursor.Ordinal);
                lastOrdinal = cursor.Ordinal;
                termPostingCount++;
                if (ordinals.Count == PrivateIndexFormat.MaximumPostingChunkPostings)
                {
                    FlushPostingChunk(ordinals, chunks, sink);
                }

                cursor.Advance();

            }
            FlushTerm(ref currentToken, ordinals, chunks, entries, sink);
            FlushBucket(currentBucket, entries, bucketOffsets, sink);
        }
        finally
        {
            foreach (var cursor in cursors)
            {
                cursor.Dispose();
            }
            CryptographicOperations.ZeroMemory(currentToken);
            ordinals.Clear();
            chunks.Clear();
            entries.Clear();
        }

        sink.WriteBuckets(bucketOffsets);
    }

    /// <summary>
    /// Writes the posting list of the term that was being collected and adds
    /// it to the bucket that is being collected.
    /// </summary>
    private static void FlushTerm(
        ref byte[] currentToken,
        List<int> ordinals,
        List<PrivateIndexPostingChunk> chunks,
        Dictionary<string, (long Offset, int Length)> entries,
        IPrivateIndexGenerationSink sink)
    {
        if (currentToken.Length == 0)
        {
            ordinals.Clear();
            chunks.Clear();
            CryptographicOperations.ZeroMemory(currentToken);
            currentToken = Array.Empty<byte>();
            return;
        }

        if (ordinals.Count > 0)
        {
            FlushPostingChunk(ordinals, chunks, sink);
        }

        if (chunks.Count == 0)
        {
            throw new InvalidDataException("A private index posting list is empty.");
        }

        var root = PrivateIndexRecords.WritePostingRoot(chunks);
        long rootOffset;
        try
        {
            rootOffset = sink.Write(EncryptedPrivateIndexStore.RecordKind.Postings, root);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(root);
        }

        var term = Convert.ToHexString(currentToken);
        if (!entries.ContainsKey(term) && entries.Count >= PrivateIndexFormat.MaximumBucketEntries)
        {
            throw new InvalidDataException("A private index token bucket holds more terms than the format allows.");
        }

        entries[term] = (rootOffset, root.Length);
        chunks.Clear();
        CryptographicOperations.ZeroMemory(currentToken);
        currentToken = Array.Empty<byte>();
    }

    private static void FlushPostingChunk(
        List<int> ordinals,
        List<PrivateIndexPostingChunk> chunks,
        IPrivateIndexGenerationSink sink)
    {
        if (ordinals.Count is < 1 or > PrivateIndexFormat.MaximumPostingChunkPostings ||
            chunks.Count >= PrivateIndexFormat.MaximumPostingChunks)
        {
            throw new InvalidDataException("A private index posting chunk exceeds its format bound.");
        }

        var count = ordinals.Count;
        var payload = PrivateIndexRecords.WritePostingChunk(ordinals, 0, count);
        try
        {
            var offset = sink.Write(EncryptedPrivateIndexStore.RecordKind.PostingChunk, payload);
            chunks.Add(new PrivateIndexPostingChunk(offset, payload.Length, count));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
            ordinals.Clear();
        }
    }

    /// <summary>
    /// Writes the term bucket that was being collected.
    /// </summary>
    private static void FlushBucket(
        int bucket,
        Dictionary<string, (long Offset, int Length)> entries,
        long[] bucketOffsets,
        IPrivateIndexGenerationSink sink)
    {
        if (bucket < 0 || entries.Count == 0)
        {
            entries.Clear();
            return;
        }

        var ordered = entries.ToArray();
        try
        {
            Array.Sort(ordered, static (left, right) =>
                PrivateIndexRecords.Utf8Order.Instance.Compare(left.Key, right.Key));
            var pages = new List<PrivateIndexBucketPage>(
                (ordered.Length + PrivateIndexFormat.MaximumBucketPageEntries - 1) /
                PrivateIndexFormat.MaximumBucketPageEntries);
            for (var start = 0; start < ordered.Length; start += PrivateIndexFormat.MaximumBucketPageEntries)
            {
                var count = Math.Min(PrivateIndexFormat.MaximumBucketPageEntries, ordered.Length - start);
                var payload = PrivateIndexRecords.WriteBucketPage(ordered, start, count);
                try
                {
                    var offset = sink.Write(EncryptedPrivateIndexStore.RecordKind.BucketPage, payload);
                    pages.Add(new PrivateIndexBucketPage(offset, payload.Length, count));
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(payload);
                }
            }

            var root = PrivateIndexRecords.WriteBucketRoot(pages);
            try
            {
                bucketOffsets[bucket] = sink.Write(EncryptedPrivateIndexStore.RecordKind.Bucket, root);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(root);
            }
        }
        finally
        {
            Array.Clear(ordered, 0, ordered.Length);
            entries.Clear();
        }
    }


    /// <summary>
    /// Sorts the arena's pairs and seals them into the spill as run chunks.
    /// </summary>
    private void FlushRun()
    {
        if (_pairCount == 0)
        {
            return;
        }

        Array.Sort(_pairs, 0, _pairCount, _order);
        var runStart = _spillPosition;
        var pairCount = 0;
        var textBytes = 0;
        try
        {
            for (var index = 0; index < _pairCount; index++)
            {
                var pair = _pairs[index];
                Array.Copy(_arena, pair.Offset, _spillChunkText, textBytes, pair.Length);
                _spillChunkPairs[pairCount++] = new Pair
                {
                    Bucket = pair.Bucket,
                    Offset = textBytes,
                    Length = pair.Length,
                    Ordinal = pair.Ordinal,
                };
                textBytes += pair.Length;
                if (pairCount == PrivateIndexFormat.MaximumSpillChunkPairs)
                {
                    WriteChunk(_spillChunkPairs, pairCount, _spillChunkText, textBytes);
                    pairCount = 0;
                    textBytes = 0;
                }
            }

            if (pairCount > 0)
            {
                WriteChunk(_spillChunkPairs, pairCount, _spillChunkText, textBytes);
                pairCount = 0;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(_spillChunkText);
            Array.Clear(_spillChunkPairs, 0, _spillChunkPairs.Length);
            CryptographicOperations.ZeroMemory(_arena.AsSpan(0, _arenaLength));
            Array.Clear(_pairs, 0, _pairCount);
            _pairCount = 0;
            _arenaLength = 0;
        }

        if (_spillPosition <= runStart)
        {
            throw new InvalidDataException("An empty private index spill run cannot be added.");
        }

        AddRun(new RunRef(runStart, _spillPosition));
    }

    private void AddRun(RunRef run)
    {
        var level = 0;
        while (true)
        {
            if (level == MaximumRunLevelCount)
            {
                throw new InvalidDataException("The private index merge exceeded its source-derived run-level bound.");
            }

            if (level == _runLevels.Count)
            {
                _runLevels.Add(new List<RunRef>(PrivateIndexFormat.MergeFanIn));
            }

            var runs = _runLevels[level];
            runs.Add(run);
            if (runs.Count < PrivateIndexFormat.MergeFanIn)
            {
                return;
            }

            run = MergeToRun(runs);
            runs.Clear();
            level++;
        }
    }

    private static int CalculateMaximumRunLevelCount()
    {
        var runCount =
            (PrivateIndexFormat.MaximumPostingPairs + PrivateIndexFormat.MaximumPostingPairsPerRun - 1) /
            PrivateIndexFormat.MaximumPostingPairsPerRun;
        var levelCount = 1;
        while (runCount >= PrivateIndexFormat.MergeFanIn)
        {
            runCount = (runCount + PrivateIndexFormat.MergeFanIn - 1) / PrivateIndexFormat.MergeFanIn;
            levelCount++;
        }

        return levelCount;
    }

    /// <summary>
    /// Seals one run chunk of the spill.
    /// </summary>
    private void WriteChunk(Pair[] pairs, int pairCount, byte[] textArena, int textBytes)
    {
        if (pairCount is < 1 or > PrivateIndexFormat.MaximumSpillChunkPairs ||
            textBytes < 0 || textBytes > textArena.Length)
        {
            throw new InvalidDataException("A private index spill chunk exceeds its byte budget.");
        }

        var measuredTextBytes = 0;
        var groupCount = 0;
        var previousBucket = -1;
        var previousOrdinal = -1;
        ReadOnlySpan<byte> previousDigest = default;
        for (var index = 0; index < pairCount; index++)
        {
            var pair = pairs[index];
            if (pair.Offset < 0 ||
                pair.Length != PrivateIndexFormat.MaximumTermKeyBytes ||
                pair.Offset > textBytes - pair.Length ||
                pair.Ordinal < 0 ||
                pair.Ordinal >= PrivateIndexFormat.MaximumIndexedRows)
            {
                throw new InvalidDataException("A private index spill pair is invalid.");
            }

            var digest = textArena.AsSpan(pair.Offset, pair.Length);
            var bucket = BucketOfDigest(digest);
            if (bucket != pair.Bucket)
            {
                throw new InvalidDataException("A private index spill pair has an invalid bucket.");
            }

            if (index == 0)
            {
                groupCount++;
            }
            else
            {
                var order = pair.Bucket.CompareTo(previousBucket);
                if (order == 0)
                {
                    order = CompareBytes(digest, previousDigest);
                }

                if (order < 0 || (order == 0 && pair.Ordinal < previousOrdinal))
                {
                    throw new InvalidDataException("A private index spill run is not sorted.");
                }

                if (order != 0)
                {
                    groupCount++;
                }
            }

            previousBucket = pair.Bucket;
            previousDigest = digest;
            previousOrdinal = pair.Ordinal;
            measuredTextBytes = checked(measuredTextBytes + pair.Length);
        }

        if (measuredTextBytes != textBytes)
        {
            throw new InvalidDataException("A private index spill chunk has an invalid digest arena.");
        }

        var size = ChunkHeaderBytes;
        var groupStart = 0;
        while (groupStart < pairCount)
        {
            var first = pairs[groupStart];
            var groupEnd = groupStart + 1;
            while (groupEnd < pairCount &&
                   pairs[groupEnd].Bucket == first.Bucket &&
                   CompareBytes(
                       textArena.AsSpan(first.Offset, first.Length),
                       textArena.AsSpan(pairs[groupEnd].Offset, pairs[groupEnd].Length)) == 0)
            {
                groupEnd++;
            }

            size = checked(size + first.Length + VarIntByteCount(groupEnd - groupStart) + VarIntByteCount(first.Ordinal));
            var previous = first.Ordinal;
            for (var index = groupStart + 1; index < groupEnd; index++)
            {
                var current = pairs[index].Ordinal;
                size = checked(size + VarIntByteCount(current - previous));
                previous = current;
            }

            groupStart = groupEnd;
        }

        if (size > PrivateIndexFormat.MaximumSpillChunkBytes)
        {
            throw new InvalidDataException("A private index spill chunk exceeds its byte budget.");
        }

        var payload = new byte[size];
        try
        {
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(0), ScratchCodecVersion);
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(sizeof(int)), pairCount);
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(2 * sizeof(int)), groupCount);
            var payloadOffset = ChunkHeaderBytes;
            groupStart = 0;
            while (groupStart < pairCount)
            {
                var first = pairs[groupStart];
                var groupEnd = groupStart + 1;
                while (groupEnd < pairCount &&
                       pairs[groupEnd].Bucket == first.Bucket &&
                       CompareBytes(
                           textArena.AsSpan(first.Offset, first.Length),
                           textArena.AsSpan(pairs[groupEnd].Offset, pairs[groupEnd].Length)) == 0)
                {
                    groupEnd++;
                }

                textArena.AsSpan(first.Offset, first.Length).CopyTo(payload.AsSpan(payloadOffset));
                payloadOffset += first.Length;
                WriteVarInt(payload, ref payloadOffset, groupEnd - groupStart);
                WriteVarInt(payload, ref payloadOffset, first.Ordinal);
                var previous = first.Ordinal;
                for (var index = groupStart + 1; index < groupEnd; index++)
                {
                    var current = pairs[index].Ordinal;
                    WriteVarInt(payload, ref payloadOffset, current - previous);
                    previous = current;
                }

                groupStart = groupEnd;
            }

            if (payloadOffset != payload.Length)
            {
                throw new InvalidDataException("A private index spill chunk changed while encoding.");
            }

            var offset = _spillPosition;
            _spill.Position = offset;
            EncryptedPrivateIndexStore.WriteRecord(
                _spill,
                _key,
                _fileId,
                _spillNonces.Next(),
                EncryptedPrivateIndexStore.RecordKind.SpillRun,
                offset,
                payload);
            _spillPosition = checked(offset + EncryptedPrivateIndexStore.SealedRecordFrameLength(payload.Length));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(textArena.AsSpan(0, textBytes));
            CryptographicOperations.ZeroMemory(payload);
            Array.Clear(pairs, 0, pairCount);
        }
    }

    private static int VarIntByteCount(int value)
    {
        if (value < 0)
        {
            throw new InvalidDataException("A private index spill delta is negative.");
        }

        var bytes = 1;
        while (value >= 0x80)
        {
            value >>= 7;
            bytes++;
        }

        return bytes;
    }

    private static void WriteVarInt(Span<byte> payload, ref int offset, int value)
    {
        if (value < 0)
        {
            throw new InvalidDataException("A private index spill delta is negative.");
        }

        var remaining = (uint)value;
        while (remaining >= 0x80)
        {
            payload[offset++] = (byte)((remaining & 0x7F) | 0x80);
            remaining >>= 7;
        }

        payload[offset++] = (byte)remaining;
    }

    private static int ReadVarInt(ReadOnlySpan<byte> payload, ref int offset)
    {
        uint value = 0;
        for (var shift = 0; shift < 35; shift += 7)
        {
            if ((uint)offset >= (uint)payload.Length)
            {
                throw new InvalidDataException("A private index spill delta is truncated.");
            }

            var current = payload[offset++];
            if (shift == 28 && current > 0x07)
            {
                throw new InvalidDataException("A private index spill delta is too large.");
            }

            value |= (uint)(current & 0x7F) << shift;
            if ((current & 0x80) == 0)
            {
                if (shift != 0 && (current & 0x7F) == 0)
                {
                    throw new InvalidDataException("A private index spill delta is not minimally encoded.");
                }

                return (int)value;
            }
        }

        throw new InvalidDataException("A private index spill delta uses more than five bytes.");
    }

    /// <summary>
    /// The order of two pairs: the bucket of the term, then the term's own
    /// bytes, then the document ordinal. The order is total, so the merge is
    /// deterministic.
    /// </summary>
    private static int Compare(RunCursor left, RunCursor right)
    {
        if (left.Bucket != right.Bucket)
        {
            return left.Bucket < right.Bucket ? -1 : 1;
        }

        var comparison = CompareBytes(left.Token, right.Token);
        if (comparison != 0)
        {
            return comparison;
        }

        return left.Ordinal.CompareTo(right.Ordinal);
    }

    /// <summary>The byte order of two byte sequences.</summary>
    internal static int CompareBytes(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        var shared = Math.Min(left.Length, right.Length);
        for (var index = 0; index < shared; index++)
        {
            if (left[index] != right[index])
            {
                return left[index] < right[index] ? -1 : 1;
            }
        }

        return left.Length.CompareTo(right.Length);
    }

    /// <summary>The bucket selected by the first four bytes of a term digest.</summary>
    internal static int BucketOfDigest(ReadOnlySpan<byte> digest)
    {
        if (digest.Length != PrivateIndexFormat.MaximumTermKeyBytes)
        {
            throw new InvalidDataException("The private index term key is invalid.");
        }

        var value = BinaryPrimitives.ReadUInt32LittleEndian(digest);
        return (int)(value % (uint)PrivateIndexFormat.TokenBucketCount);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _termEncoder.Reset();
        _termHasher.Dispose();
        CryptographicOperations.ZeroMemory(_termScratch);
        CryptographicOperations.ZeroMemory(_spillChunkText);
        CryptographicOperations.ZeroMemory(_arena.AsSpan(0, _arenaLength));
        Array.Clear(_pairs, 0, _pairCount);
        Array.Clear(_spillChunkPairs, 0, _spillChunkPairs.Length);
        _termScratch = Array.Empty<byte>();
        _arena = Array.Empty<byte>();
        _pairs = Array.Empty<Pair>();
        _spillChunkPairs = Array.Empty<Pair>();
        _spillChunkText = Array.Empty<byte>();
        _runLevels.Clear();
        try
        {
            _spill.Dispose();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The spill is ciphertext that was never published; the next
            // rebuild removes it.
        }

        try
        {
            if (File.Exists(_spillPath))
            {
                File.Delete(_spillPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The same: a spill that cannot be removed now is removed by the
            // next rebuild, and it is never read.
        }
    }

    /// <summary>
    /// The order of the arena's pairs. The terms are compared by their own
    /// bytes in the arena, so the sort never encodes a term again.
    /// </summary>
    private sealed class ArenaOrder : IComparer<Pair>
    {
        private readonly PrivateIndexPostingBuilder _builder;

        internal ArenaOrder(PrivateIndexPostingBuilder builder)
        {
            _builder = builder;
        }

        public int Compare(Pair x, Pair y)
        {
            if (x.Bucket != y.Bucket)
            {
                return x.Bucket < y.Bucket ? -1 : 1;
            }

            var comparison = CompareBytes(
                _builder._arena.AsSpan(x.Offset, x.Length),
                _builder._arena.AsSpan(y.Offset, y.Length));
            if (comparison != 0)
            {
                return comparison;
            }

            return x.Ordinal.CompareTo(y.Ordinal);
        }
    }

    /// <summary>
    /// A cursor over one run of the spill. The cursor reads one sealed chunk
    /// at a time, so the memory of a merge is bounded by the fan-in and the
    /// chunk size.
    /// </summary>
    private sealed class RunCursor : IDisposable
    {
        private readonly FileStream _spill;
        private readonly byte[] _key;
        private readonly byte[] _fileId;
        private readonly long _endOffset;
        private readonly GroupRef[] _groups = new GroupRef[PrivateIndexFormat.MaximumSpillChunkPairs];
        private long _nextOffset;
        private byte[]? _payload;
        private int _groupCount;
        private int _groupIndex;
        private int _ordinalIndex;
        private int _ordinalOffset;
        private int _currentOrdinal;

        private readonly struct GroupRef
        {
            internal GroupRef(int bucket, int digestOffset, int ordinalOffset, int count)
            {
                Bucket = bucket;
                DigestOffset = digestOffset;
                OrdinalOffset = ordinalOffset;
                Count = count;
            }

            internal int Bucket { get; }
            internal int DigestOffset { get; }
            internal int OrdinalOffset { get; }
            internal int Count { get; }
        }

        internal RunCursor(FileStream spill, byte[] key, byte[] fileId, RunRef run)
        {
            if (run.StartOffset < 0 || run.EndOffset <= run.StartOffset)
            {
                throw new InvalidDataException("The private index spill run is invalid.");
            }

            _spill = spill;
            _key = key;
            _fileId = fileId;
            _nextOffset = run.StartOffset;
            _endOffset = run.EndOffset;
            LoadNextChunk();
        }

        /// <summary>Whether the cursor still holds a pair.</summary>
        internal bool HasCurrent => _payload is not null && _groupIndex < _groupCount;

        /// <summary>The bucket of the current digest group.</summary>
        internal int Bucket => HasCurrent ? _groups[_groupIndex].Bucket : throw new InvalidOperationException("The spill cursor is exhausted.");

        /// <summary>The document ordinal of the current pair.</summary>
        internal int Ordinal => HasCurrent ? _currentOrdinal : throw new InvalidOperationException("The spill cursor is exhausted.");

        /// <summary>The digest shared by the current term group.</summary>
        internal ReadOnlySpan<byte> Token => !HasCurrent
            ? ReadOnlySpan<byte>.Empty
            : _payload!.AsSpan(_groups[_groupIndex].DigestOffset, PrivateIndexFormat.MaximumTermKeyBytes);

        /// <summary>The fixed digest length represented by the current group.</summary>
        internal int TokenLength => PrivateIndexFormat.MaximumTermKeyBytes;

        /// <summary>Moves the cursor to the next ordinal or digest group.</summary>
        internal void Advance()
        {
            var group = _groups[_groupIndex];
            if (_ordinalIndex + 1 < group.Count)
            {
                var delta = ReadVarInt(_payload!, ref _ordinalOffset);
                _currentOrdinal = checked(_currentOrdinal + delta);
                _ordinalIndex++;
                return;
            }

            _groupIndex++;
            if (_groupIndex < _groupCount)
            {
                LoadCurrentGroup();
                return;
            }

            LoadNextChunk();
        }

        private void LoadCurrentGroup()
        {
            var group = _groups[_groupIndex];
            var offset = group.OrdinalOffset;
            _currentOrdinal = ReadVarInt(_payload!, ref offset);
            _ordinalOffset = offset;
            _ordinalIndex = 0;
        }

        private void LoadNextChunk()
        {
            if (_payload is not null)
            {
                CryptographicOperations.ZeroMemory(_payload);
                _payload = null;
            }

            _groupCount = 0;
            _groupIndex = 0;
            _ordinalIndex = 0;
            _ordinalOffset = 0;
            _currentOrdinal = 0;
            Array.Clear(_groups, 0, _groups.Length);

            if (_nextOffset == _endOffset)
            {
                return;
            }

            if (_nextOffset > _endOffset)
            {
                throw new InvalidDataException("The private index spill run is invalid.");
            }

            var offset = _nextOffset;
            var length = EncryptedPrivateIndexStore.ReadSealedRecordLength(
                _spill,
                offset,
                EncryptedPrivateIndexStore.RecordKind.SpillRun);
            var frameLength = EncryptedPrivateIndexStore.SealedRecordFrameLength(length);
            if (frameLength > _endOffset - offset)
            {
                throw new InvalidDataException("The private index spill run is truncated.");
            }

            var payload = EncryptedPrivateIndexStore.ReadSealedRecord(
                _spill,
                _key,
                _fileId,
                EncryptedPrivateIndexStore.RecordKind.SpillRun,
                offset,
                length);
            try
            {
                _nextOffset = checked(offset + frameLength);
                ValidateChunk(payload);
                _payload = payload;
                payload = Array.Empty<byte>();
                _groupIndex = 0;
                LoadCurrentGroup();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(payload);
            }
        }

        private void ValidateChunk(byte[] payload)
        {
            if (payload.Length < ChunkHeaderBytes ||
                payload.Length > PrivateIndexFormat.MaximumSpillChunkBytes ||
                BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(0)) != ScratchCodecVersion)
            {
                throw new InvalidDataException("The private index spill chunk version or length is invalid.");
            }

            var pairCount = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(sizeof(int)));
            var groupCount = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(2 * sizeof(int)));
            if (pairCount is < 1 or > PrivateIndexFormat.MaximumSpillChunkPairs ||
                groupCount is < 1 or > PrivateIndexFormat.MaximumSpillChunkPairs ||
                groupCount > pairCount)
            {
                throw new InvalidDataException("The private index spill chunk counts are invalid.");
            }

            var payloadOffset = ChunkHeaderBytes;
            var pairsSeen = 0;
            var previousBucket = -1;
            var previousDigestOffset = -1;
            for (var groupIndex = 0; groupIndex < groupCount; groupIndex++)
            {
                if (payload.Length - payloadOffset < PrivateIndexFormat.MaximumTermKeyBytes)
                {
                    throw new InvalidDataException("The private index spill digest group is truncated.");
                }

                var digestOffset = payloadOffset;
                var digest = payload.AsSpan(digestOffset, PrivateIndexFormat.MaximumTermKeyBytes);
                var bucket = BucketOfDigest(digest);
                if (bucket < previousBucket ||
                    (bucket == previousBucket &&
                     previousDigestOffset >= 0 &&
                     CompareBytes(
                         payload.AsSpan(previousDigestOffset, PrivateIndexFormat.MaximumTermKeyBytes),
                         digest) >= 0))
                {
                    throw new InvalidDataException("The private index spill digest groups are not strictly sorted.");
                }

                payloadOffset += PrivateIndexFormat.MaximumTermKeyBytes;
                var count = ReadVarInt(payload, ref payloadOffset);
                if (count < 1 || count > pairCount - pairsSeen)
                {
                    throw new InvalidDataException("The private index spill digest group count is invalid.");
                }

                var ordinalOffset = payloadOffset;
                var ordinal = ReadVarInt(payload, ref payloadOffset);
                if (ordinal >= PrivateIndexFormat.MaximumIndexedRows)
                {
                    throw new InvalidDataException("The private index spill ordinal is invalid.");
                }

                for (var index = 1; index < count; index++)
                {
                    var delta = ReadVarInt(payload, ref payloadOffset);
                    var nextOrdinal = (long)ordinal + delta;
                    if (nextOrdinal >= PrivateIndexFormat.MaximumIndexedRows)
                    {
                        throw new InvalidDataException("The private index spill ordinal delta is invalid.");
                    }

                    ordinal = (int)nextOrdinal;
                }

                _groups[groupIndex] = new GroupRef(bucket, digestOffset, ordinalOffset, count);
                pairsSeen = checked(pairsSeen + count);
                previousBucket = bucket;
                previousDigestOffset = digestOffset;
            }

            if (pairsSeen != pairCount || payloadOffset != payload.Length)
            {
                throw new InvalidDataException("The private index spill chunk has trailing or missing pair data.");
            }

            _groupCount = groupCount;
        }

        public void Dispose()
        {
            if (_payload is not null)
            {
                CryptographicOperations.ZeroMemory(_payload);
                _payload = null;
            }

            Array.Clear(_groups, 0, _groups.Length);
            _groupCount = 0;
            _groupIndex = 0;
            _ordinalIndex = 0;
            _ordinalOffset = 0;
            _currentOrdinal = 0;
        }
    }
}

/// <summary>
/// The generation the postings are written into. The sink owns the staged
/// file, its key and its nonce sequence, so the postings and the buckets
/// are sealed exactly like every other record of the generation.
/// </summary>
internal interface IPrivateIndexGenerationSink
{
    /// <summary>Seals one record of the staged generation and returns its offset.</summary>
    long Write(EncryptedPrivateIndexStore.RecordKind kind, byte[] payload);

    /// <summary>Records the offset of every term bucket of the generation.</summary>
    void WriteBuckets(long[] bucketOffsets);
}
