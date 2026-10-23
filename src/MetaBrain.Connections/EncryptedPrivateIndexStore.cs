using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using MetaBrain.Core.Index;
using MetaBrain.Core.Security;

namespace MetaBrain.Connections;

/// <summary>
/// S8-T5 the private index engine: a persistent, encrypted-at-rest index of
/// the owner's canonical records, with a byte-bounded cache of decrypted
/// records in process memory.
/// <para>
/// The index is a rebuildable derivative: the canonical encrypted vault stays
/// the only truth, and this file never holds canonical state, a body or a
/// grant. Every record in the file is individually sealed with the vault's
/// own AEAD under a key derived from the vault data key, so the file, its
/// staging file and any torn write are ciphertext or nothing at all: there
/// is no plaintext index, temp file, journal, WAL or cache path on disk.
/// </para>
/// <para>
/// Memory residency is bounded by construction. The engine keeps one
/// directory of the materialized generation (the identity, revision and file
/// offset of every indexed document) and a cache of decrypted records whose
/// size is capped in bytes and evicted least-recently-used. The postings
/// themselves are never all resident: a query reads the posting list of the
/// terms it asked for, streams the candidates and scores them in batches.
/// </para>
/// <para>
/// A key, a cache or a private read never outlives the unlocked epoch:
/// <see cref="Reset"/> drops the directory, the cache and the file handle,
/// and the engine reads no private byte while the vault is locked.
/// </para>
/// </summary>
internal sealed class EncryptedPrivateIndexStore : IPrivateIndexStore
{
    /// <summary>The directory the index lives in, inside the vault root.</summary>
    internal const string IndexDirectoryName = "private-index";

    /// <summary>The published generation file.</summary>
    internal const string PublishedFileName = "index.v1.bin";

    /// <summary>The staged generation file of an unfinished rebuild.</summary>
    internal const string StagingFileName = "index.v1.building";

    internal const string SpillFileName = "index.v1.spill";
    internal const string DirectorySpillFileName = "index.v1.directory-building";
    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private const int AssociatedDataBytes = 16 + 1 + sizeof(long) + sizeof(int);
    private const int KeyBytes = 32;
    private const int PlaintextHeaderBytes = 40;
    private const int DefaultCacheBudgetBytes = 16 * 1024 * 1024;
    private static readonly byte[] Magic = { (byte)'M', (byte)'B', (byte)'P', (byte)'I', (byte)'D', (byte)'X', (byte)'0', (byte)'1' };

    private readonly object _gate = new();
    private readonly string _directory;
    private readonly IPrivateIndexKeyProvider _keys;
    private readonly int _cacheBudgetBytes;
    private readonly RecordCache _cache;

    private FileStream? _file;
    private BufferedRecordReader? _reader;
    private byte[]? _fileId;
    private int _formatVersion;
    private int _rowCount;
    private string _canonicalDigest = string.Empty;
    private string[] _resourceIds = Array.Empty<string>();
    private long[] _revisions = Array.Empty<long>();
    private long[] _metaOffsets = Array.Empty<long>();
    private long[] _tokenOffsets = Array.Empty<long>();
    private long[] _bucketOffsets = Array.Empty<long>();
    private long[] _rowOffsets = Array.Empty<long>();
    private PrivateIndexSampleEntry[] _sample = Array.Empty<PrivateIndexSampleEntry>();
    private long _residentDirectoryBytes;
    private WriteScope? _writeScope;
    private bool _disposed;

    private readonly Encoder _queryTermEncoder = Encoding.UTF8.GetEncoder();
    private readonly IncrementalHash _queryTermHasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly byte[] _queryTermScratch = new byte[4096];

    // The per-query accumulators. They are engine state, so a query runs
    // under the engine's lock and resets exactly the entries it touched.
    private bool[] _touchedFlags = Array.Empty<bool>();
    private readonly List<int> _touched = new();
    private Dictionary<string, ScopeResourceRevision>? _boundScope;


    /// <summary>
    /// Opens an engine over the index directory of a vault. The engine never
    /// holds key material of its own: it borrows the index key from the
    /// provider for the operation it is running.
    /// </summary>
    internal EncryptedPrivateIndexStore(string vaultRoot, IPrivateIndexKeyProvider keys, int? cacheBudgetBytes = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(vaultRoot);
        ArgumentNullException.ThrowIfNull(keys);
        _directory = Path.Combine(vaultRoot, IndexDirectoryName);
        _keys = keys;
        _cacheBudgetBytes = cacheBudgetBytes is > 0 ? cacheBudgetBytes.Value : DefaultCacheBudgetBytes;
        _cache = new RecordCache(_cacheBudgetBytes);
    }

    /// <summary>The number of bytes the decrypted record cache holds at most.</summary>
    internal int CacheBudgetBytes => _cacheBudgetBytes;

    /// <summary>The number of bytes the decrypted record cache holds now.</summary>
    internal int CacheBytes
    {
        get
        {
            lock (_gate)
            {
                return _cache.Bytes;
            }
        }
    }

    /// <summary>
    /// The bytes the resident directory of the held generation costs in
    /// process memory. It is the one structure that grows with the vault, and
    /// it is bounded: a generation whose directory would not fit the budget
    /// is refused instead of being loaded.
    /// </summary>
    internal long ResidentDirectoryBytes
    {
        get
        {
            lock (_gate)
            {
                return _residentDirectoryBytes;
            }
        }
    }

    /// <summary>The number of indexed documents of the held generation.</summary>
    internal int DocumentCount
    {
        get
        {
            lock (_gate)
            {
                return _resourceIds.Length;
            }
        }
    }

    /// <summary>The number of distinct tokens of the held generation.</summary>
    internal int TokenCount
    {
        get
        {
            lock (_gate)
            {
                var count = 0;
                foreach (var offset in _bucketOffsets)
                {
                    if (offset >= 0)
                    {
                        count++;
                    }
                }

                return count;
            }
        }
    }

    /// <summary>The size of the published generation file on disk.</summary>
    internal long PublishedFileBytes
    {
        get
        {
            lock (_gate)
            {
                var path = Path.Combine(_directory, PublishedFileName);
                return File.Exists(path) ? new FileInfo(path).Length : 0L;
            }
        }
    }

    public int SchemaVersion
    {
        get
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                return _formatVersion;
            }
        }
    }

    public bool HasOpenGeneration
    {
        get
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                return _file is not null;
            }
        }
    }

    public int RowCount
    {
        get
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                return _rowCount;
            }
        }
    }

    /// <summary>
    /// The canonical digest of the generation the engine holds. It is read
    /// from the sealed directory of the generation, so it is authenticated
    /// with the generation itself.
    /// </summary>
    public string CanonicalDigest
    {
        get
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                return _canonicalDigest;
            }
        }
    }

    public void Open()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_file is not null)
            {
                return;
            }

            var path = Path.Combine(_directory, PublishedFileName);
            if (!File.Exists(path))
            {
                // An engine without a published generation is empty: the owner
                // rebuilds it from canonical.
                ResetGeneration();
                _formatVersion = PrivateIndexFormat.CurrentVersion;
                return;
            }

            var key = RequireKey();
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
            try
            {
                var header = new byte[PlaintextHeaderBytes];
                ReadExactly(stream, header, 0, header.Length);
                if (!header.AsSpan(0, Magic.Length).SequenceEqual(Magic))
                {
                    throw new InvalidDataException("The private index file is not a Meta Brain private index.");
                }

                var version = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8, 4));
                PrivateIndexFormat.RequireSupported(version);
                var headerOffset = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(12, 8));
                var headerLength = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(20, 4));
                var fileId = header.AsSpan(24, 16).ToArray();
                if (headerLength <= 0 || headerLength > PrivateIndexFormat.MaximumDirectoryRecordBytes)
                {
                    throw new InvalidDataException("The private index file header is invalid.");
                }

                var directoryFrameLength = SealedRecordFrameLength(headerLength);
                if (directoryFrameLength > stream.Length ||
                    headerOffset < PlaintextHeaderBytes ||
                    headerOffset != stream.Length - directoryFrameLength)
                {
                    throw new InvalidDataException("The private index file header is invalid.");
                }

                var reader = new BufferedRecordReader(stream);
                var payload = reader.Read(headerOffset, headerLength, key, fileId, RecordKind.Directory);
                DirectoryRecord directory;
                try
                {
                    directory = PrivateIndexRecords.ReadDirectory(payload);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(payload);
                }
                // The resident directory is bounded: a generation whose
                // directory would not fit the budget is refused instead of
                // being loaded, so the resident size of the engine is a
                // measured bound and not a function of the vault.
                var residentBytes = directory.ResidentBytes;
                if (residentBytes > PrivateIndexFormat.MaximumResidentDirectoryBytes)
                {
                    throw new InvalidDataException("The private index directory is larger than the engine's resident budget.");
                }

                _fileId = fileId;
                _formatVersion = version;
                _rowCount = directory.RowCount;
                _canonicalDigest = directory.CanonicalDigest;
                _resourceIds = directory.ResourceIds;
                _revisions = directory.Revisions;
                _metaOffsets = directory.MetaOffsets;
                _tokenOffsets = directory.TokenOffsets;
                _bucketOffsets = directory.BucketOffsets;
                _rowOffsets = directory.RowOffsets;
                _sample = directory.Sample;
                _residentDirectoryBytes = residentBytes;
                _file = stream;
                _reader = reader;
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            // The generation is replaced wholesale by the next committed write
            // scope, so clearing drops the resident generation and the cached
            // records without touching the published file.
            _reader?.Dispose();
            _reader = null;
            _file?.Dispose();
            _file = null;
            ResetGeneration();
            _cache.Clear();
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            // Drops every materialized row, every cached decrypted record and
            // the file handle, so no private derived data outlives the
            // unlocked epoch.
            _reader?.Dispose();
            _reader = null;
            _file?.Dispose();
            _file = null;
            ResetGeneration();
            _cache.Clear();
        }
    }

    public void Insert(PrivateIndexRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        ValidateRow(row);
        lock (_gate)
        {
            ThrowIfDisposed();
            RequireWriteScope().InsertResourceRow(row);
        }
    }

    public PrivateIndexRow[] SelectSample(int maximumRows)
    {
        if (maximumRows < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumRows));
        }

        lock (_gate)
        {
            ThrowIfDisposed();
            if (maximumRows == 0)
            {
                return Array.Empty<PrivateIndexRow>();
            }

            var count = Math.Min(maximumRows, _sample.Length);
            if (_file is null || _fileId is null || count == 0)
            {
                return Array.Empty<PrivateIndexRow>();
            }

            var rows = new PrivateIndexRow[count];
            for (var index = 0; index < count; index++)
            {
                var payload = _reader!.Read(_sample[index].Offset, -1, RequireKey(), _fileId, RecordKind.Resource);
                try
                {
                    rows[index] = PrivateIndexRecords.ReadResourceRow(payload);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(payload);
                }
            }

            return rows;
        }
    }

    public void InsertSearchDocument(SearchIndexDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        ValidateSearchDocument(document);
        lock (_gate)
        {
            ThrowIfDisposed();
            RequireWriteScope().InsertSearchDocument(document);
        }
    }

    public SearchIndexDocument? SelectSearchDocument(string resourceId, long revision)
    {
        if (string.IsNullOrEmpty(resourceId) || revision < 1)
        {
            return null;
        }

        lock (_gate)
        {
            ThrowIfDisposed();
            var ordinal = FindOrdinal(resourceId, revision);
            if (ordinal < 0)
            {
                return null;
            }

            return ReadDocument(ordinal, includeTokens: true);
        }
    }

    public SearchIndexDocument[] SelectSearchDocuments(IReadOnlyList<ScopeResourceRevision> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        lock (_gate)
        {
            ThrowIfDisposed();
            var documents = new List<SearchIndexDocument>(candidates.Count);
            foreach (var candidate in candidates)
            {
                var ordinal = FindOrdinal(candidate.ResourceId, candidate.Revision);
                if (ordinal < 0)
                {
                    continue;
                }

                var document = ReadDocument(ordinal, includeTokens: true);
                if (document is not null)
                {
                    documents.Add(document);
                }
            }

            return documents.ToArray();
        }
    }

    public IEnumerable<SearchIndexCandidateBatch> StreamSearchCandidates(
        IReadOnlyList<ScopeResourceRevision> scope,
        IReadOnlyList<string> tokens,
        int batchSize,
        bool includeTemporalRow,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(tokens);
        if (batchSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(batchSize));
        }

        return Stream(scope, tokens, batchSize, includeTemporalRow, cancellationToken);
    }

    public IPrivateIndexWriteScope BeginBulkWrite(string canonicalDigest)
    {
        ArgumentException.ThrowIfNullOrEmpty(canonicalDigest);
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_writeScope is not null)
            {
                throw new InvalidOperationException("A bulk write scope is already open on the private index engine.");
            }

            var key = RequireKey();
            Directory.CreateDirectory(_directory);
            var stagingPath = Path.Combine(_directory, StagingFileName);
            var postingSpillPath = Path.Combine(_directory, SpillFileName);
            var directorySpillPath = Path.Combine(_directory, DirectorySpillFileName);
            // Every unfinished ciphertext artifact is reaped before a new
            // generation starts; Open never consumes either spill.
            TryDelete(stagingPath);
            TryDelete(postingSpillPath);
            TryDelete(directorySpillPath);
            var stream = new FileStream(stagingPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.SequentialScan);
            PrivateIndexDirectorySpill? directorySpill = null;
            PrivateIndexPostingBuilder? postingBuilder = null;
            try
            {
                directorySpill = new PrivateIndexDirectorySpill(directorySpillPath, key);
                postingBuilder = new PrivateIndexPostingBuilder(postingSpillPath, key);
                var scope = new WriteScope(this, stream, key, RandomNumberGenerator.GetBytes(16), canonicalDigest, directorySpill, postingBuilder);
                _writeScope = scope;
                return scope;
            }
            catch
            {
                postingBuilder?.Dispose();
                directorySpill?.Dispose();
                stream.Dispose();
                TryDelete(stagingPath);
                throw;
            }
        }
    }


    // ------------------------------------------------------------- streaming

    private IEnumerable<SearchIndexCandidateBatch> Stream(
        IReadOnlyList<ScopeResourceRevision> scope,
        IReadOnlyList<string> tokens,
        int batchSize,
        bool includeTemporalRow,
        CancellationToken cancellationToken)
    {
        // The enumeration holds the engine's lock for its whole run, so two
        // queries never share the file handle or the accumulators. The lock
        // is released when the enumeration ends or is abandoned.
        Monitor.Enter(_gate);
        BufferedRecordReader? queryReader = null;
        var queryStarted = false;
        try
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            // The scope is bound inside the engine: a resource that is not in
            // the approved scope never leaves the engine, so an out-of-scope
            // identifier never crosses the engine's boundary.
            var bound = BindScope(scope);

            if (tokens.Count == 0)
            {
                foreach (var batch in StreamScope(scope, batchSize, cancellationToken))
                {
                    yield return batch;
                }

                yield break;
            }

            queryReader = _reader;
            if (queryReader is not null)
            {
                queryReader.BeginQuery(RequireKey());
                queryStarted = true;
            }
            foreach (var batch in StreamTerms(bound, tokens, batchSize, includeTemporalRow, cancellationToken))
            {
                yield return batch;
            }
        }
        finally
        {
            try
            {
                if (queryStarted)
                {
                    queryReader!.EndQuery();
                }
            }
            finally
            {
                Monitor.Exit(_gate);
            }
        }
    }

    /// <summary>
    /// Binds the caller's approved scope. The binding is reused while the
    /// scope does not change, so a repeated query over the same scope costs
    /// one comparison instead of a full rebind.
    /// </summary>
    private Dictionary<string, ScopeResourceRevision> BindScope(IReadOnlyList<ScopeResourceRevision> scope)
    {
        if (_boundScope is not null &&
            _boundScope.Count == scope.Count &&
            scope.All(entry => _boundScope.TryGetValue(entry.ResourceId, out var bound) &&
                bound.Revision == entry.Revision &&
                string.Equals(bound.ZoneId, entry.ZoneId, StringComparison.Ordinal)))
        {
            return _boundScope;
        }

        var boundScope = new Dictionary<string, ScopeResourceRevision>(scope.Count, StringComparer.Ordinal);
        foreach (var entry in scope)
        {
            boundScope[entry.ResourceId] = entry;
        }

        _boundScope = boundScope;
        return boundScope;
    }


    private IEnumerable<SearchIndexCandidateBatch> StreamScope(
        IReadOnlyList<ScopeResourceRevision> scope,
        int batchSize,
        CancellationToken cancellationToken)
    {
        var batch = new List<SearchIndexCandidate>(batchSize);
        foreach (var entry in scope)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ordinal = FindOrdinal(entry.ResourceId, entry.Revision);
            if (ordinal < 0)
            {
                continue;
            }

            var document = ReadDocument(ordinal, includeTokens: false);
            if (document is null)
            {
                continue;
            }

            batch.Add(new SearchIndexCandidate(
                new ScopeResourceRevision(entry.ResourceId, entry.ZoneId, entry.Revision),
                document));
            if (batch.Count == batchSize)
            {
                yield return new SearchIndexCandidateBatch(batch.ToArray());
                batch = new List<SearchIndexCandidate>(batchSize);
            }
        }

        if (batch.Count > 0)
        {
            yield return new SearchIndexCandidateBatch(batch.ToArray());
        }
    }

    private IEnumerable<SearchIndexCandidateBatch> StreamTerms(
        Dictionary<string, ScopeResourceRevision> revisions,
        IReadOnlyList<string> tokens,
        int batchSize,
        bool includeTemporalRow,
        CancellationToken cancellationToken)
    {
        var queryTokens = new HashSet<string>(tokens, StringComparer.Ordinal);
        EnsureAccumulators();
        _touched.Clear();
        var queryTokenBeacons = PrivateIndexRecords.CreateQueryTokenBeacons(queryTokens, out var queryUtf8);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var start = 0;
            while (start < tokens.Count)
            {
                var count = Math.Min(PrivateIndexFormat.MaximumQueryTermsPerPass, tokens.Count - start);
                var cursors = new PostingCursor[count];
                try
                {
                    for (var index = 0; index < count; index++)
                    {
                        cursors[index] = OpenPostingCursor(tokens[start + index]);
                    }
                    cancellationToken.ThrowIfCancellationRequested();

                    while (true)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var minimum = int.MaxValue;
                        for (var index = 0; index < count; index++)
                        {
                            if (cursors[index].HasCurrent && cursors[index].Current < minimum)
                            {
                                minimum = cursors[index].Current;
                            }
                        }

                        if (minimum == int.MaxValue)
                        {
                            break;
                        }

                        for (var index = 0; index < count; index++)
                        {
                            var cursor = cursors[index];
                            if (!cursor.HasCurrent || cursor.Current != minimum)
                            {
                                continue;
                            }

                            cursor.Advance();
                            var directoryIndex = minimum;
                            var resourceId = _resourceIds[directoryIndex];
                            if (!revisions.TryGetValue(resourceId, out var approved))
                            {
                                // The scope filter runs here, inside the engine.
                                continue;
                            }

                            var scoring = directoryIndex;
                            if (_revisions[directoryIndex] != approved.Revision)
                            {
                                // Resolve only the exact frozen revision; a newer row
                                // cannot answer an older approved scope.
                                scoring = FindOrdinal(resourceId, approved.Revision);
                                if (scoring < 0)
                                {
                                    continue;
                                }
                            }

                            if (!_touchedFlags[scoring])
                            {
                                _touchedFlags[scoring] = true;
                                _touched.Add(scoring);
                            }
                        }
                    }
                }
                finally
                {
                    foreach (var cursor in cursors)
                    {
                        cursor?.Dispose();
                    }
                }
                start += count;
            }

            _touched.Sort();
            var batch = new List<SearchIndexCandidate>(batchSize);
            foreach (var scoring in _touched)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _touchedFlags[scoring] = false;
                var document = ReadDocument(
                    scoring,
                    includeTokens: true,
                    includeTemporalRow: includeTemporalRow,
                    queryTokenBeacons: queryTokenBeacons,
                    queryUtf8: queryUtf8);
                if (document is null)
                {
                    continue;
                }

                // Only exact UTF-8 matches from the authenticated token record are retained.
                if (document.Tokens.Length == 0)
                {
                    continue;
                }

                var zone = revisions.TryGetValue(document.ResourceId, out var approvedZone)
                    ? approvedZone.ZoneId
                    : string.Empty;
                batch.Add(new SearchIndexCandidate(
                    new ScopeResourceRevision(document.ResourceId, zone, document.Revision),
                    document));
                if (batch.Count == batchSize)
                {
                    yield return new SearchIndexCandidateBatch(batch.ToArray());
                    batch = new List<SearchIndexCandidate>(batchSize);
                }
            }

            if (batch.Count > 0)
            {
                yield return new SearchIndexCandidateBatch(batch.ToArray());
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(queryUtf8);
            Array.Clear(queryTokenBeacons, 0, queryTokenBeacons.Length);
            ResetTouchedAccumulators();
        }
    }

    /// <summary>
    /// Resets exactly the candidate flags touched by the current query and
    /// clears the list when enumeration ends or is abandoned.
    /// </summary>
    private void ResetTouchedAccumulators()
    {
        foreach (var index in _touched)
        {
            _touchedFlags[index] = false;
        }

        _touched.Clear();
    }

    private void EnsureAccumulators()
    {
        var size = Math.Max(_resourceIds.Length, 1);
        if (_touchedFlags.Length >= size)
        {
            return;
        }

        _touchedFlags = new bool[size];
        _touched.Clear();
    }

    // ----------------------------------------------------------- the records

    /// <summary>
    /// Reads the posting list of one query term: the document ordinals that
    /// hold the term. The list is read from the sealed file and cached; it is
    /// never all resident at once.
    /// </summary>
    private void ComputeTermDigest(string token, Span<byte> digest)
    {
        _queryTermEncoder.Reset();
        var remaining = token.AsSpan();
        try
        {
            while (true)
            {
                _queryTermEncoder.Convert(
                    remaining,
                    _queryTermScratch,
                    flush: true,
                    out var charsUsed,
                    out var bytesUsed,
                    out var completed);
                try
                {
                    _queryTermHasher.AppendData(_queryTermScratch.AsSpan(0, bytesUsed));
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(_queryTermScratch.AsSpan(0, bytesUsed));
                }

                remaining = remaining[charsUsed..];
                if (completed)
                {
                    break;
                }

                if (charsUsed == 0 && bytesUsed == 0)
                {
                    throw new InvalidDataException("A private index query term could not be encoded.");
                }
            }

            if (!_queryTermHasher.TryGetHashAndReset(digest, out var written) || written != digest.Length)
            {
                throw new CryptographicException("The private index query term key could not be computed.");
            }
        }
        catch
        {
            var discarded = _queryTermScratch.AsSpan(0, PrivateIndexFormat.MaximumTermKeyBytes);
            _queryTermHasher.TryGetHashAndReset(discarded, out _);
            CryptographicOperations.ZeroMemory(discarded);
            throw;
        }
        finally
        {
            _queryTermEncoder.Reset();
            CryptographicOperations.ZeroMemory(_queryTermScratch);
        }
    }

    private PostingCursor OpenPostingCursor(string token)
    {
        if (_file is null || _fileId is null || _bucketOffsets.Length == 0)
        {
            return new PostingCursor(this, Array.Empty<PrivateIndexPostingChunk>());
        }

        Span<byte> digest = stackalloc byte[PrivateIndexFormat.MaximumTermKeyBytes];
        try
        {
            ComputeTermDigest(token, digest);
            var bucket = PrivateIndexPostingBuilder.BucketOfDigest(digest);
            if (bucket < 0 || bucket >= _bucketOffsets.Length || _bucketOffsets[bucket] < 0)
            {
                return new PostingCursor(this, Array.Empty<PrivateIndexPostingChunk>());
            }

            var termKey = Convert.ToHexString(digest);
            var entries = ReadBucket(bucket);
            if (!entries.TryGetValue(termKey, out var posting))
            {
                return new PostingCursor(this, Array.Empty<PrivateIndexPostingChunk>());
            }

            return new PostingCursor(this, ReadPostingRoot(posting.Offset, posting.Length));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(digest);
        }
    }

    private Dictionary<string, (long Offset, int Length)> ReadBucket(int bucket)
    {
        var key = CacheKey(RecordKind.Bucket, _bucketOffsets[bucket]);
        var bucketCacheHit = _cache.TryGet(key, out var cached);

        if (bucketCacheHit && cached is Dictionary<string, (long Offset, int Length)> entries)
        {
            return entries;
        }

        var rootPayload = _reader!.Read(_bucketOffsets[bucket], -1, RequireKey(), _fileId!, RecordKind.Bucket);
        PrivateIndexBucketPage[] pages;
        try
        {
            pages = PrivateIndexRecords.ReadBucketRoot(rootPayload);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(rootPayload);
        }

        var entryCount = 0;
        foreach (var page in pages)
        {
            entryCount = checked(entryCount + page.EntryCount);
        }

        entries = new Dictionary<string, (long Offset, int Length)>(entryCount, StringComparer.Ordinal);
        foreach (var page in pages)
        {
            var payload = _reader.Read(page.Offset, page.Length, RequireKey(), _fileId!, RecordKind.BucketPage);
            Dictionary<string, (long Offset, int Length)> pageEntries;
            try
            {
                pageEntries = PrivateIndexRecords.ReadBucketPage(payload);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(payload);
            }

            if (pageEntries.Count != page.EntryCount)
            {
                throw new InvalidDataException("The private index token bucket page count does not match its root.");
            }

            foreach (var entry in pageEntries)
            {
                if (!entries.TryAdd(entry.Key, entry.Value))
                {
                    throw new InvalidDataException("The private index token bucket contains duplicate terms.");
                }
            }
        }

        if (entries.Count != entryCount)
        {
            throw new InvalidDataException("The private index token bucket count does not match its root.");
        }

        _cache.Add(key, entries, EstimateBucketBytes(entries));
        return entries;
    }

    private PrivateIndexPostingChunk[] ReadPostingRoot(long offset, int length)
    {
        var key = CacheKey(RecordKind.Postings, offset);
        var postingRootCacheHit = _cache.TryGet(key, out var cached);

        if (postingRootCacheHit && cached is PrivateIndexPostingChunk[] chunks)
        {
            return chunks;
        }

        var payload = _reader!.Read(offset, length, RequireKey(), _fileId!, RecordKind.Postings);
        try
        {
            chunks = PrivateIndexRecords.ReadPostingRoot(payload, _resourceIds.Length);
            _cache.Add(key, chunks, chunks.Length * (sizeof(long) + sizeof(int) + sizeof(int)));
            return chunks;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    private int[] ReadPostingChunk(PrivateIndexPostingChunk chunk)
    {
        var key = CacheKey(RecordKind.PostingChunk, chunk.Offset);
        var postingChunkCacheHit = _cache.TryGet(key, out var cached);

        if (postingChunkCacheHit && cached is int[] ordinals)
        {
            return ordinals;
        }

        var payload = _reader!.Read(chunk.Offset, chunk.Length, RequireKey(), _fileId!, RecordKind.PostingChunk);
        try
        {
            ordinals = PrivateIndexRecords.ReadPostingChunk(payload, chunk.Count, _resourceIds.Length);
            _cache.Add(key, ordinals, ordinals.Length * sizeof(int));
            return ordinals;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    private sealed class PostingCursor : IDisposable
    {
        private readonly EncryptedPrivateIndexStore _store;
        private readonly PrivateIndexPostingChunk[] _chunks;
        private int _chunkIndex = -1;
        private int[]? _current;
        private int _position;
        private int _lastOrdinal = -1;
        internal PostingCursor(EncryptedPrivateIndexStore store, PrivateIndexPostingChunk[] chunks)
        {
            _store = store;
            _chunks = chunks;
            LoadNextChunk();
        }

        internal bool HasCurrent => _current is not null && _position < _current.Length;
        internal int Current => HasCurrent ? _current![_position] : throw new InvalidOperationException("The posting cursor is exhausted.");

        internal void Advance()
        {
            var consumed = Current;
            if (consumed <= _lastOrdinal)
            {
                throw new InvalidDataException("The private index posting chunks are not in ascending order.");
            }

            _lastOrdinal = consumed;
            if (_current is not null && _position + 1 < _current.Length)
            {
                _position++;
                if (Current <= _lastOrdinal)
                {
                    throw new InvalidDataException("The private index posting chunks are not in ascending order.");
                }

                return;
            }

            LoadNextChunk();
            if (HasCurrent && Current <= _lastOrdinal)
            {
                throw new InvalidDataException("The private index posting chunks are not in ascending order.");
            }
        }

        private void LoadNextChunk()
        {
            _chunkIndex++;
            if (_chunkIndex >= _chunks.Length)
            {
                _current = null;
                _position = 0;
                return;
            }

            _current = _store.ReadPostingChunk(_chunks[_chunkIndex]);
            _position = 0;
            if (HasCurrent && Current <= _lastOrdinal)
            {
                throw new InvalidDataException("The private index posting chunks are not in ascending order.");
            }
        }

        public void Dispose()
        {
            _current = null;
            _position = 0;
        }
    }

    /// <summary>
    /// Reads the search document of one ordinal. The temporal row and the
    /// token set are two sealed records, so a query that only needs the row
    /// never pays for the tokens.
    /// </summary>
    private SearchIndexDocument? ReadDocument(
        int ordinal,
        bool includeTokens,
        bool includeTemporalRow = true,
        PrivateIndexRecords.QueryTokenBeacon[]? queryTokenBeacons = null,
        byte[]? queryUtf8 = null)
    {
        if (_file is null || _fileId is null || ordinal < 0 || ordinal >= _resourceIds.Length)
        {
            return null;
        }

        var tokens = Array.Empty<string>();
        if (includeTokens)
        {
            var reader = _reader!;
            if (queryTokenBeacons is null)
            {
                var payload = reader.Read(_tokenOffsets[ordinal], -1, RequireKey(), _fileId!, RecordKind.Tokens);
                try
                {
                    tokens = PrivateIndexRecords.ReadTokens(payload);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(payload);
                }
            }
            else
            {
                var payloadLength = reader.ReadQueryTokenPayload(
                    _tokenOffsets[ordinal],
                    -1,
                    RequireKey(),
                    _fileId!,
                    RecordKind.Tokens);
                try
                {
                    tokens = PrivateIndexRecords.ReadMatchingTokens(
                        reader.QueryPayload(payloadLength),
                        queryUtf8 ?? throw new InvalidOperationException("Query token bytes are required for matched reads."),
                        queryTokenBeacons);
                }
                finally
                {
                    reader.ClearQueryPayload(payloadLength);
                }
            }
        }

        if (!includeTemporalRow)
        {
            // The ranking that only scores the query terms does not read the
            // temporal row at all: the record is left encrypted on disk and
            // the candidate carries its identity and its terms alone.
            return new SearchIndexDocument(
                _resourceIds[ordinal],
                _revisions[ordinal],
                string.Empty,
                string.Empty,
                tokens,
                null,
                DateTimeOffset.MinValue,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty);
        }

        var meta = _reader!.Read(_metaOffsets[ordinal], -1, RequireKey(), _fileId!, RecordKind.Document);
        (long Revision, string Kind, string Title, DateTimeOffset? OccurredAtUtc, DateTimeOffset RecordedAtUtc, string Publication, string Project, string Environment, string Version) row;
        try
        {
            row = PrivateIndexRecords.ReadMeta(meta);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(meta);
        }

        if (row.Revision != _revisions[ordinal])
        {
            throw new InvalidDataException("The private index document revision does not match its directory.");
        }

        return new SearchIndexDocument(
            _resourceIds[ordinal],
            row.Revision,
            row.Kind,
            row.Title,
            tokens,
            row.OccurredAtUtc,
            row.RecordedAtUtc,
            row.Publication,
            row.Project,
            row.Environment,
            row.Version);
    }

    /// <summary>
    /// Finds the ordinal of the document that holds one (resource, revision)
    /// pair. The directory is sorted, so the lookup is a binary search and
    /// never a scan of the generation.
    /// </summary>
    private int FindOrdinal(string resourceId, long revision)
    {
        var low = 0;
        var high = _resourceIds.Length - 1;
        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            var comparison = string.CompareOrdinal(_resourceIds[middle], resourceId);
            if (comparison < 0)
            {
                low = middle + 1;
                continue;
            }

            if (comparison > 0)
            {
                high = middle - 1;
                continue;
            }

            // The documents of one resource are adjacent in the sorted
            // directory, so the approved revision is found next to it.
            var start = middle;
            while (start > 0 && string.Equals(_resourceIds[start - 1], resourceId, StringComparison.Ordinal))
            {
                start--;
            }

            for (var index = start; index < _resourceIds.Length && string.Equals(_resourceIds[index], resourceId, StringComparison.Ordinal); index++)
            {
                if (_revisions[index] == revision)
                {
                    return index;
                }
            }

            return -1;
        }

        return -1;
    }

    internal enum RecordKind : byte
    {
        Directory = 1,
        Document = 2,
        Tokens = 3,
        Postings = 4,
        Bucket = 5,
        Resource = 6,
        SpillRun = 7,
        PostingChunk = 8,
        DirectorySpill = 9,
        BucketPage = 10
    }

    private static long CacheKey(RecordKind kind, long offset) => (offset << 8) | (byte)kind;

    private static byte[] AssociatedData(byte[] fileId, RecordKind kind, long offset)
    {
        var aad = new byte[AssociatedDataBytes];
        WriteAssociatedData(fileId, kind, offset, aad);
        return aad;
    }

    private static void WriteAssociatedData(byte[] fileId, RecordKind kind, long offset, Span<byte> aad)
    {
        if (fileId.Length != 16 || aad.Length != AssociatedDataBytes)
        {
            throw new InvalidDataException("The private index associated-data fields are invalid.");
        }

        fileId.AsSpan().CopyTo(aad);
        aad[16] = (byte)kind;
        BinaryPrimitives.WriteInt64LittleEndian(aad.Slice(17, sizeof(long)), offset);
        BinaryPrimitives.WriteInt32LittleEndian(aad.Slice(25, sizeof(int)), PrivateIndexFormat.CurrentVersion);
    }

    /// <summary>
    /// Seals one record. The record's own offset is bound into the
    /// associated data, so a record cannot be moved, reordered or replaced
    /// without the tag failing.
    /// </summary>
    internal static void WriteRecord(
        Stream stream,
        byte[] key,
        byte[] fileId,
        byte[] nonce,
        RecordKind kind,
        long offset,
        byte[] payload)
    {
        var ciphertext = new byte[payload.Length];
        var tag = new byte[TagBytes];
        var aad = AssociatedData(fileId, kind, offset);
        try
        {
            using (var aes = new AesGcm(key, TagBytes))
            {
                aes.Encrypt(nonce, payload, ciphertext, tag, aad);
            }

            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(length, payload.Length);
            stream.Write(length);
            stream.Write(nonce);
            stream.Write(ciphertext);
            stream.Write(tag);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(ciphertext);
            CryptographicOperations.ZeroMemory(tag);
            CryptographicOperations.ZeroMemory(aad);
        }
    }
    internal static int ReadSealedRecordLength(Stream stream, long offset, RecordKind kind)
    {
        if (offset < 0)
        {
            throw new InvalidDataException("The private index record offset is invalid.");
        }

        stream.Position = offset;
        Span<byte> lengthBytes = stackalloc byte[sizeof(int)];
        try
        {
            ReadExactly(stream, lengthBytes);
            var length = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);
            if (length <= 0 || length > MaximumSealedRecordBytes(kind))
            {
                throw new InvalidDataException("The private index record is invalid.");
            }

            return length;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(lengthBytes);
        }
    }

    internal static int SealedRecordFrameLength(int payloadLength) =>
        checked(sizeof(int) + NonceBytes + payloadLength + TagBytes);

    private static int MaximumSealedRecordBytes(RecordKind kind) => kind switch
    {
        RecordKind.SpillRun => PrivateIndexFormat.MaximumSpillRecordBytes,
        RecordKind.DirectorySpill => PrivateIndexDirectorySpill.PayloadBytes,
        RecordKind.PostingChunk => PrivateIndexFormat.MaximumPostingChunkBytes,
        _ => throw new InvalidDataException("The private index record kind is invalid.")
    };

    internal static byte[] ReadSealedRecord(
        Stream stream,
        byte[] key,
        byte[] fileId,
        RecordKind kind,
        long offset,
        int expectedLength)
    {
        stream.Position = offset;
        var lengthBytes = new byte[sizeof(int)];
        ReadExactly(stream, lengthBytes, 0, lengthBytes.Length);
        var length = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);
        var maximum = MaximumSealedRecordBytes(kind);
        if (length <= 0 || length > maximum || length != expectedLength)
        {
            throw new InvalidDataException("The private index record is invalid.");
        }

        var nonce = new byte[NonceBytes];
        var ciphertext = new byte[length];
        var tag = new byte[TagBytes];
        var plaintext = new byte[length];
        var aad = AssociatedData(fileId, kind, offset);
        try
        {
            ReadExactly(stream, nonce, 0, nonce.Length);
            ReadExactly(stream, ciphertext, 0, ciphertext.Length);
            ReadExactly(stream, tag, 0, tag.Length);
            using var aes = new AesGcm(key, TagBytes);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, aad);
            return plaintext;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(lengthBytes);
            CryptographicOperations.ZeroMemory(nonce);
            CryptographicOperations.ZeroMemory(ciphertext);
            CryptographicOperations.ZeroMemory(tag);
            CryptographicOperations.ZeroMemory(aad);
        }
    }

    private static void ReadExactly(Stream stream, byte[] buffer, int offset, int count)
    {
        var read = 0;
        while (read < count)
        {
            var current = stream.Read(buffer, offset + read, count - read);
            if (current <= 0)
            {
                throw new InvalidDataException("The private index file is truncated.");
            }

            read += current;
        }
    }

    private static void ReadExactly(Stream stream, Span<byte> buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var current = stream.Read(buffer[read..]);
            if (current <= 0)
            {
                throw new InvalidDataException("The private index file is truncated.");
            }

            read += current;
        }
    }

    /// <summary>
    /// A buffered reader over the generation file. The candidates of a query
    /// are read in ascending file order, so the records are served from one
    /// buffer instead of one file read per record: a query over a large scope
    /// costs a handful of reads instead of one per candidate. A record larger
    /// than the buffer is read into a right-sized buffer of its own.
    /// </summary>
    private sealed class BufferedRecordReader : IDisposable
    {
        private const int BufferBytes = 64 * 1024;
        private readonly FileStream _stream;
        private readonly byte[] _buffer = new byte[BufferBytes];
        private long _bufferStart = -1;
        private int _bufferLength;
        private readonly byte[] _queryAssociatedData = new byte[AssociatedDataBytes];
        private byte[] _queryPlaintext = Array.Empty<byte>();
        private byte[] _queryCiphertext = Array.Empty<byte>();
        private byte[]? _queryKey;
        private AesGcm? _queryDecryptor;
        private bool _disposed;

        public BufferedRecordReader(FileStream stream)
        {
            _stream = stream;
        }
        public void BeginQuery(byte[] key)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_queryDecryptor is not null || key.Length != KeyBytes)
            {
                throw new InvalidOperationException("The private index reader query context is invalid.");
            }

            CryptographicOperations.ZeroMemory(_queryAssociatedData);
            CryptographicOperations.ZeroMemory(_queryPlaintext);
            CryptographicOperations.ZeroMemory(_queryCiphertext);
            _queryKey = key;
            try
            {
                _queryDecryptor = new AesGcm(key, TagBytes);
            }
            catch
            {
                _queryKey = null;
                throw;
            }
        }

        public void EndQuery()
        {
            var decryptor = _queryDecryptor;
            _queryDecryptor = null;
            _queryKey = null;
            try
            {
                decryptor?.Dispose();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(_queryAssociatedData);
                CryptographicOperations.ZeroMemory(_queryPlaintext);
                CryptographicOperations.ZeroMemory(_queryCiphertext);
            }
        }

        public ReadOnlySpan<byte> QueryPayload(int length)
        {
            if (_queryDecryptor is null || length < 0 || length > _queryPlaintext.Length)
            {
                throw new InvalidOperationException("The private index query payload is unavailable.");
            }

            return _queryPlaintext.AsSpan(0, length);
        }

        public void ClearQueryPayload(int length)
        {
            if (length < 0 || length > _queryPlaintext.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(length));
            }

            CryptographicOperations.ZeroMemory(_queryPlaintext.AsSpan(0, length));
        }

        public int ReadQueryTokenPayload(long offset, int expectedLength, byte[] key, byte[] fileId, RecordKind kind)
        {
            if (kind != RecordKind.Tokens)
            {
                throw new InvalidDataException("The private index query reader only accepts token records.");
            }

            var decryptor = RequireQueryDecryptor(key);
            if (!TryReadLength(offset, out var length))
            {
                throw new InvalidDataException("The private index file is truncated.");
            }

            if (length <= 0 || length > MaximumBytes(kind) || (expectedLength >= 0 && length != expectedLength))
            {
                throw new InvalidDataException("The private index record is invalid.");
            }

            EnsureQueryPlaintextCapacity(length);
            var total = sizeof(int) + NonceBytes + length + TagBytes;
            var buffered = total <= _buffer.Length;
            if (!buffered)
            {
                EnsureQueryCiphertextCapacity(total);
            }

            ReadOnlySpan<byte> record = default;
            try
            {
                if (buffered)
                {
                    if (!Ensure(offset, total))
                    {
                        throw new InvalidDataException("The private index file is truncated.");
                    }

                    record = _buffer.AsSpan((int)(offset - _bufferStart), total);
                }
                else
                {
                    _stream.Position = offset;
                    ReadExactly(_stream, _queryCiphertext, 0, total);
                    record = _queryCiphertext.AsSpan(0, total);
                }

                WriteAssociatedData(fileId, kind, offset, _queryAssociatedData);
                decryptor.Decrypt(
                    record.Slice(sizeof(int), NonceBytes),
                    record.Slice(sizeof(int) + NonceBytes, length),
                    record.Slice(sizeof(int) + NonceBytes + length, TagBytes),
                    _queryPlaintext.AsSpan(0, length),
                    _queryAssociatedData);
                return length;
            }
            catch
            {
                CryptographicOperations.ZeroMemory(_queryPlaintext.AsSpan(0, length));
                throw;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(_queryAssociatedData);
                if (!buffered)
                {
                    CryptographicOperations.ZeroMemory(_queryCiphertext.AsSpan(0, total));
                }
            }
        }

        private AesGcm RequireQueryDecryptor(byte[] key)
        {
            var decryptor = _queryDecryptor;
            var queryKey = _queryKey;
            if (decryptor is null || queryKey is null || !queryKey.AsSpan().SequenceEqual(key))
            {
                throw new InvalidOperationException("The private index reader query key changed.");
            }

            return decryptor;
        }

        private void EnsureQueryPlaintextCapacity(int length)
        {
            if (_queryPlaintext.Length >= length)
            {
                return;
            }

            CryptographicOperations.ZeroMemory(_queryPlaintext);
            _queryPlaintext = new byte[length];
        }

        private void EnsureQueryCiphertextCapacity(int length)
        {
            if (_queryCiphertext.Length >= length)
            {
                return;
            }

            CryptographicOperations.ZeroMemory(_queryCiphertext);
            _queryCiphertext = new byte[length];
        }


        /// <summary>
        /// The authenticated format kind supplies a tight allocation bound
        /// before the length-prefixed ciphertext or plaintext is allocated.
        /// </summary>
        public int MaximumBytes(RecordKind kind) => kind switch
        {
            RecordKind.Directory => PrivateIndexFormat.MaximumDirectoryRecordBytes,
            RecordKind.Document => PrivateIndexFormat.MaximumDocumentRecordBytes,
            RecordKind.Tokens => PrivateIndexFormat.MaximumTokensRecordBytes,
            RecordKind.Postings => PrivateIndexFormat.MaximumPostingRootBytes,
            RecordKind.Bucket => PrivateIndexFormat.MaximumBucketRecordBytes,
            RecordKind.BucketPage => PrivateIndexFormat.MaximumBucketPageBytes,
            RecordKind.Resource => PrivateIndexFormat.MaximumResourceRecordBytes,
            RecordKind.PostingChunk => PrivateIndexFormat.MaximumPostingChunkBytes,
            RecordKind.SpillRun => PrivateIndexFormat.MaximumSpillRecordBytes,
            RecordKind.DirectorySpill => PrivateIndexDirectorySpill.PayloadBytes,
            _ => throw new InvalidDataException("The private index record kind is invalid.")
        };

        /// <summary>
        /// Reads and authenticates the record at <paramref name="offset"/>. The
        /// record's own length is read from the file, so a truncated or moved
        /// record fails closed here.
        /// </summary>
        public byte[] Read(long offset, int expectedLength, byte[] key, byte[] fileId, RecordKind kind)
        {
            if (!TryReadLength(offset, out var length))
            {
                throw new InvalidDataException("The private index file is truncated.");
            }

            if (length <= 0 || length > MaximumBytes(kind) || (expectedLength >= 0 && length != expectedLength))
            {
                throw new InvalidDataException("The private index record is invalid.");
            }

            var total = 4 + NonceBytes + length + TagBytes;
            byte[]? oversized = null;
            ReadOnlySpan<byte> record;
            if (total <= _buffer.Length)
            {
                if (!Ensure(offset, total))
                {
                    throw new InvalidDataException("The private index file is truncated.");
                }

                record = _buffer.AsSpan((int)(offset - _bufferStart), total);
            }
            else
            {
                oversized = new byte[total];
                _stream.Position = offset;
                ReadExactly(_stream, oversized, 0, total);
                record = oversized;
            }

            var plaintext = new byte[length];
            var decryptor = _queryDecryptor;
            var aad = decryptor is null ? AssociatedData(fileId, kind, offset) : _queryAssociatedData;
            try
            {
                if (decryptor is null)
                {
                    using var aes = new AesGcm(key, TagBytes);
                    aes.Decrypt(
                        record.Slice(4, NonceBytes),
                        record.Slice(4 + NonceBytes, length),
                        record.Slice(4 + NonceBytes + length, TagBytes),
                        plaintext,
                        aad);
                }
                else
                {
                    _ = RequireQueryDecryptor(key);
                    WriteAssociatedData(fileId, kind, offset, aad);
                    decryptor.Decrypt(
                        record.Slice(4, NonceBytes),
                        record.Slice(4 + NonceBytes, length),
                        record.Slice(4 + NonceBytes + length, TagBytes),
                        plaintext,
                        aad);
                }

                return plaintext;
            }
            catch
            {
                CryptographicOperations.ZeroMemory(plaintext);
                throw;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(aad);
                if (oversized is not null)
                {
                    CryptographicOperations.ZeroMemory(oversized);
                }
            }

        }
        /// <summary>Reads the record's own length from the file.</summary>
        private bool TryReadLength(long offset, out int length)
        {
            length = 0;
            if (Ensure(offset, 4))
            {
                length = BinaryPrimitives.ReadInt32LittleEndian(_buffer.AsSpan((int)(offset - _bufferStart), 4));
                return true;
            }

            var bytes = new byte[4];
            _stream.Position = offset;
            try
            {
                ReadExactly(_stream, bytes, 0, 4);
            }
            catch (InvalidDataException)
            {
                return false;
            }

            length = BinaryPrimitives.ReadInt32LittleEndian(bytes);
            return true;
        }

        /// <summary>
        /// Makes sure the buffer holds the requested bytes. A record that
        /// follows the buffered one is served from the buffer; anything else
        /// refills it from the file.
        /// </summary>
        private bool Ensure(long offset, int count)
        {
            if (_bufferStart >= 0 &&
                offset >= _bufferStart &&
                offset + count <= _bufferStart + _bufferLength)
            {
                return true;
            }

            _stream.Position = offset;
            var read = 0;
            while (read < _buffer.Length)
            {
                var current = _stream.Read(_buffer, read, _buffer.Length - read);
                if (current <= 0)
                {
                    break;
                }

                read += current;
            }

            _bufferStart = offset;
            _bufferLength = read;
            return read >= count;
        }
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                EndQuery();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(_buffer);
                _bufferStart = -1;
                _bufferLength = 0;
                CryptographicOperations.ZeroMemory(_queryAssociatedData);
                CryptographicOperations.ZeroMemory(_queryPlaintext);
                CryptographicOperations.ZeroMemory(_queryCiphertext);
                _queryPlaintext = Array.Empty<byte>();
                _queryCiphertext = Array.Empty<byte>();
            }
        }

    }


    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A staged file that cannot be removed is overwritten by the next
            // rebuild; it is ciphertext that was never published.
        }
    }

    private void ResetGeneration()
    {
        _fileId = null;
        _boundScope = null;
        _rowCount = 0;
        _canonicalDigest = string.Empty;
        _resourceIds = Array.Empty<string>();
        _revisions = Array.Empty<long>();
        _metaOffsets = Array.Empty<long>();
        _tokenOffsets = Array.Empty<long>();
        _bucketOffsets = Array.Empty<long>();
        _rowOffsets = Array.Empty<long>();
        _sample = Array.Empty<PrivateIndexSampleEntry>();
        _residentDirectoryBytes = 0;
    }

    private WriteScope RequireWriteScope() =>
        _writeScope ?? throw new InvalidOperationException("The private index engine is not in a bulk write scope.");

    private byte[] RequireKey()
    {
        var key = _keys.PrivateIndexKey;
        if (key is null || key.Length != KeyBytes)
        {
            throw new InvalidOperationException("The private index is locked.");
        }

        return key;
    }

    private static void ValidateRow(PrivateIndexRow row)
    {
        if (!IsUtf8Within(row.ResourceId, PrivateIndexFormat.MaximumIdentifierBytes) ||
            !IsUtf8Within(row.ZoneId, PrivateIndexFormat.MaximumIdentifierBytes) ||
            row.Revision < 1 || row.ResourceId.Contains(',') || row.ResourceId.Contains(';') ||
            row.ZoneId.Contains(',') || row.ZoneId.Contains(';') ||
            row.CanonicalSchemaVersion < 0 || !IsUtf8Within(row.State, PrivateIndexFormat.MaximumStateBytes) ||
            row.LinkedResourceIds is null || row.LinkedResourceIds.Length > PrivateIndexFormat.MaximumLinkedIds ||
            !Enum.IsDefined(row.RecordKind) ||
            row.LinkedResourceIds.Any(id => !IsUtf8Within(id, PrivateIndexFormat.MaximumIdentifierBytes) || id.Contains(',')))
        {
            throw new InvalidDataException("A private index row is not materializable.");
        }
    }

    private static void ValidateSearchDocument(SearchIndexDocument document)
    {
        if (document.Tokens is null)
        {
            throw new InvalidDataException("A search document is not materializable.");
        }

        _ = PrivateIndexRecords.MeasureTokensPayloadBytes(document.Tokens);
        if (!IsUtf8Within(document.ResourceId, PrivateIndexFormat.MaximumIdentifierBytes) || document.Revision < 1 ||
            !IsUtf8Within(document.Kind, PrivateIndexFormat.MaximumIdentifierBytes) ||
            !IsUtf8Within(document.Title, PrivateIndexFormat.MaximumMetadataFieldBytes) ||
            !IsUtf8Within(document.Publication, PrivateIndexFormat.MaximumIdentifierBytes) ||
            !IsUtf8Within(document.Project, PrivateIndexFormat.MaximumMetadataFieldBytes) ||
            !IsUtf8Within(document.Environment, PrivateIndexFormat.MaximumMetadataFieldBytes) ||
            !IsUtf8Within(document.Version, PrivateIndexFormat.MaximumMetadataFieldBytes) ||
            document.RecordedAtUtc.Offset != TimeSpan.Zero ||
            (document.OccurredAtUtc is not null && document.OccurredAtUtc.Value.Offset != TimeSpan.Zero))
        {
            throw new InvalidDataException("A search document is not materializable.");
        }
    }

    private static bool IsUtf8Within(string? value, int maximumBytes) =>
        value is not null && Encoding.UTF8.GetByteCount(value) <= maximumBytes;

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _writeScope?.Dispose();
            _writeScope = null;
            _reader?.Dispose();
            _reader = null;
            _file?.Dispose();
            _file = null;
            _cache.Clear();
            _queryTermEncoder.Reset();
            _queryTermHasher.Dispose();
            CryptographicOperations.ZeroMemory(_queryTermScratch);
        }

        GC.SuppressFinalize(this);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    // --------------------------------------------------------- the write path

    /// <summary>
    /// One bulk-write scope. Every insert lands in the staged generation; the
    /// scope publishes it exactly once, or drops it, so a partial generation
    /// is never observable.
    /// </summary>
    private sealed class WriteScope : IPrivateIndexWriteScope, IPrivateIndexGenerationSink
    {
        private readonly EncryptedPrivateIndexStore _store;
        private readonly FileStream _stream;
        private readonly byte[] _key;
        private readonly byte[] _fileId;
        private readonly string _canonicalDigest;
        private readonly PrivateIndexNonceSequence _nonces = new();
        private readonly PrivateIndexPostingBuilder _postings;
        private readonly PrivateIndexDirectorySpill _directorySpill;
        private readonly List<PrivateIndexSampleEntry> _sample = new();
        private string? _pendingMemoryId;
        private long _pendingMemoryRevision;
        private bool _pendingMemoryConsumed;
        private string? _lastDocumentId;
        private long _lastDocumentRevision;
        private long[]? _bucketOffsets;
        private int _rowCount;
        private int _documentCount;
        private long _position;
        private bool _finished;

        public WriteScope(
            EncryptedPrivateIndexStore store,
            FileStream stream,
            byte[] key,
            byte[] fileId,
            string canonicalDigest,
            PrivateIndexDirectorySpill directorySpill,
            PrivateIndexPostingBuilder postings)
        {
            _store = store;
            _stream = stream;
            _key = key;
            _fileId = fileId;
            _canonicalDigest = canonicalDigest;
            _directorySpill = directorySpill;
            _postings = postings;
            // The plaintext header area is reserved for the version and the
            // eventual directory locator; every content record is ciphertext.
            var header = new byte[PlaintextHeaderBytes];
            Magic.CopyTo(header, 0);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8, 4), PrivateIndexFormat.CurrentVersion);
            _stream.Write(header);
            _position = PlaintextHeaderBytes;
        }

        private byte[] NextNonce() => _nonces.Next();

        public void InsertResourceRow(PrivateIndexRow row)
        {
            if (_rowCount >= PrivateIndexFormat.MaximumIndexedRows)
            {
                throw new InvalidDataException("The private index row count exceeds the vault manifest limit.");
            }

            var payload = PrivateIndexRecords.WriteResourceRow(row);
            var offset = _position;
            try
            {
                WriteRecord(_stream, _key, _fileId, NextNonce(), RecordKind.Resource, offset, payload);
                _position += RecordLength(payload.Length);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(payload);
            }
            _directorySpill.AddRow(row.ResourceId, offset);
            _rowCount++;

            _pendingMemoryId = row.RecordKind == PrivateIndexRecordKind.MemoryItem ? row.ResourceId : null;
            _pendingMemoryRevision = row.Revision;
            _pendingMemoryConsumed = false;

            var sample = new PrivateIndexSampleEntry(row.ResourceId, row.Revision, offset);
            var insertion = 0;
            while (insertion < _sample.Count && CompareSample(_sample[insertion], sample) < 0)
            {
                insertion++;
            }

            if (insertion < PrivateIndexFormat.MaximumSnapshotRows)
            {
                _sample.Insert(insertion, sample);
                if (_sample.Count > PrivateIndexFormat.MaximumSnapshotRows)
                {
                    _sample.RemoveAt(_sample.Count - 1);
                }
            }
        }

        public void InsertSearchDocument(SearchIndexDocument document)
        {
            if (_pendingMemoryId is null || _pendingMemoryConsumed ||
                !string.Equals(_pendingMemoryId, document.ResourceId, StringComparison.Ordinal) ||
                _pendingMemoryRevision != document.Revision)
            {
                throw new InvalidDataException("A search document must match the immediately preceding materialized memory row.");
            }

            if (_documentCount >= PrivateIndexFormat.MaximumIndexedRows)
            {
                throw new InvalidDataException("The private index document count exceeds the vault manifest limit.");
            }

            if (_lastDocumentId is not null)
            {
                var order = string.CompareOrdinal(_lastDocumentId, document.ResourceId);
                if (order > 0 || (order == 0 && _lastDocumentRevision >= document.Revision))
                {
                    throw new InvalidDataException("The private index documents are not in strict resource/revision order.");
                }
            }

            _pendingMemoryConsumed = true;
            var ordinal = _documentCount++;
            var meta = PrivateIndexRecords.WriteMeta(document);
            var metaOffset = _position;
            try
            {
                WriteRecord(_stream, _key, _fileId, NextNonce(), RecordKind.Document, metaOffset, meta);
                _position += RecordLength(meta.Length);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(meta);
            }

            var tokens = PrivateIndexRecords.WriteTokens(document.Tokens);
            var tokenOffset = _position;
            try
            {
                WriteRecord(_stream, _key, _fileId, NextNonce(), RecordKind.Tokens, tokenOffset, tokens);
                _position += RecordLength(tokens.Length);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(tokens);
            }

            _directorySpill.AddDocument(document.ResourceId, document.Revision, metaOffset, tokenOffset, ordinal);
            _lastDocumentId = document.ResourceId;
            _lastDocumentRevision = document.Revision;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var token in document.Tokens)
            {
                if (seen.Add(token))
                {
                    _postings.Add(token, ordinal);
                }
            }
        }

        public long Write(RecordKind kind, byte[] payload)
        {
            var offset = _position;
            try
            {
                WriteRecord(_stream, _key, _fileId, NextNonce(), kind, offset, payload);
                _position += RecordLength(payload.Length);
                return offset;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(payload);
            }
        }

        public void WriteBuckets(long[] bucketOffsets) => _bucketOffsets = bucketOffsets;

        public void Commit()
        {
            if (_finished)
            {
                return;
            }

            _finished = true;
            var published = false;
            try
            {
                _postings.Merge(this);
                var bucketOffsets = _bucketOffsets ?? EmptyBucketOffsets();
                var directory = PrivateIndexRecords.WriteDirectory(
                    _canonicalDigest,
                    _rowCount,
                    _documentCount,
                    _directorySpill.ReadDocuments,
                    bucketOffsets,
                    _directorySpill.ReadRows,
                    _sample.ToArray());
                try
                {
                    var directoryOffset = _position;
                    var directoryLength = directory.Length;
                    WriteRecord(_stream, _key, _fileId, NextNonce(), RecordKind.Directory, directoryOffset, directory);
                    _position += RecordLength(directoryLength);

                    var header = new byte[PlaintextHeaderBytes];
                    Magic.CopyTo(header, 0);
                    BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8, 4), PrivateIndexFormat.CurrentVersion);
                    BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(12, 8), directoryOffset);
                    BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(20, 4), directoryLength);
                    _fileId.CopyTo(header, 24);
                    _stream.Position = 0;
                    _stream.Write(header);
                    _stream.Flush(flushToDisk: true);

                    // The directory is already sealed into the staging file.
                    // Release build-only buffers before Open decrypts and
                    // materializes the committed directory.
                    CryptographicOperations.ZeroMemory(directory);
                    directory = Array.Empty<byte>();
                    _postings.Dispose();
                    _directorySpill.Dispose();
                    _stream.Dispose();

                    var publishedPath = Path.Combine(_store._directory, PublishedFileName);
                    lock (_store._gate)
                    {
                        _store._reader?.Dispose();
                        _store._reader = null;
                        _store._file?.Dispose();
                        _store._file = null;
                        _store._cache.Clear();
                        File.Move(_stream.Name, publishedPath, overwrite: true);
                        published = true;

                        _store.ResetGeneration();
                        _store.Open();
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(directory);
                }
            }
            finally
            {
                if (!published)
                {
                    _stream.Dispose();
                    TryDelete(_stream.Name);
                }

                EndScope();
            }
        }

        public void Rollback()
        {
            if (_finished)
            {
                return;
            }

            _finished = true;
            try
            {
                _stream.Dispose();
                TryDelete(_stream.Name);
            }
            finally
            {
                EndScope();
            }
        }

        public void Dispose()
        {
            if (!_finished)
            {
                Rollback();
            }
        }

        private void EndScope()
        {
            _store._writeScope = null;
            _postings.Dispose();
            _directorySpill.Dispose();
            _sample.Clear();
            _pendingMemoryId = null;
            _lastDocumentId = null;
        }

        private static long RecordLength(int payloadLength) => 4 + NonceBytes + payloadLength + TagBytes;

        private static int CompareSample(PrivateIndexSampleEntry left, PrivateIndexSampleEntry right)
        {
            var id = string.CompareOrdinal(left.ResourceId, right.ResourceId);
            return id != 0 ? id : left.Revision.CompareTo(right.Revision);
        }

        private static long[] EmptyBucketOffsets()
        {
            var offsets = new long[PrivateIndexFormat.TokenBucketCount];
            Array.Fill(offsets, -1L);
            return offsets;
        }
    }

    private sealed class RowOrder : IComparer<PrivateIndexRow>
    {
        internal static readonly RowOrder Instance = new();

        public int Compare(PrivateIndexRow? x, PrivateIndexRow? y)
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

            return string.CompareOrdinal(x.ResourceId, y.ResourceId);
        }
    }

    /// <summary>
    /// The posting list of one term: the document ordinals that hold it,
    /// delta encoded. The list is built in memory during a rebuild and never
    /// held resident afterwards.
    /// </summary>
    private sealed class PostingList
    {
        private byte[] _buffer = new byte[256];
        private int _length;
        private long _last = -1;

        public void Append(long ordinal)
        {
            if (ordinal == _last)
            {
                return;
            }

            var delta = ordinal - _last - 1;
            _last = ordinal;
            while (delta >= 0x80)
            {
                Ensure(1);
                _buffer[_length++] = (byte)(delta | 0x80);
                delta >>= 7;
            }

            Ensure(1);
            _buffer[_length++] = (byte)delta;
        }

        public byte[] ToArray()
        {
            var result = new byte[_length];
            Array.Copy(_buffer, result, _length);
            return result;
        }

        private void Ensure(int extra)
        {
            if (_length + extra <= _buffer.Length)
            {
                return;
            }

            var size = _buffer.Length;
            while (size < _length + extra)
            {
                size *= 2;
            }

            Array.Resize(ref _buffer, size);
        }
    }

    /// <summary>
    /// The byte-bounded cache of decrypted records. The cache is capped in
    /// bytes and evicts the least recently used record first, so the resident
    /// size of the engine does not grow with the vault.
    /// </summary>
    private sealed class RecordCache
    {
        private readonly int _budget;
        private readonly Dictionary<long, CacheEntry> _entries = new();
        private readonly LinkedList<long> _order = new();
        private int _bytes;

        public RecordCache(int budget)
        {
            _budget = budget;
        }

        public int Bytes => _bytes;

        public bool TryGet(long key, out object? value)
        {
            if (!_entries.TryGetValue(key, out var entry))
            {
                value = null;
                return false;
            }

            _order.Remove(entry.Node);
            _order.AddFirst(entry.Node);
            value = entry.Value;
            return true;
        }

        public void Add(long key, object value, int bytes)
        {
            if (_entries.TryGetValue(key, out var existing))
            {
                _bytes -= existing.Bytes;
                _order.Remove(existing.Node);
                _entries.Remove(key);
            }

            if (bytes > _budget)
            {
                // A record that does not fit the budget is never cached; it is
                // read again on the next request instead of evicting the whole
                // cache for it.
                return;
            }

            var node = _order.AddFirst(key);
            _entries[key] = new CacheEntry(value, bytes, node);
            _bytes += bytes;
            while (_bytes > _budget && _order.Last is not null)
            {
                var last = _order.Last;
                var evicted = _entries[last.Value];
                _entries.Remove(last.Value);
                _order.Remove(last);
                _bytes -= evicted.Bytes;
            }
        }

        public void Clear()
        {
            _entries.Clear();
            _order.Clear();
            _bytes = 0;
        }

        private sealed record CacheEntry(object Value, int Bytes, LinkedListNode<long> Node);
    }

    private static int EstimateBucketBytes(Dictionary<string, (long Offset, int Length)> entries)
    {
        long bytes = 256 + ((long)entries.Count * 96);
        foreach (var entry in entries)
        {
            bytes += 24 + ((long)entry.Key.Length * sizeof(char));
        }

        return checked((int)bytes);
    }
}
