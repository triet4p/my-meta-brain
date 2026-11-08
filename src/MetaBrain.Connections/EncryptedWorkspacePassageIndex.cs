using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using MetaBrain.Core.Index;
using MetaBrain.Core.Security;
using MetaBrain.Connections.DocumentBridge;

namespace MetaBrain.Connections;


/// <summary>Encrypted, bounded workspace passage/vector generation.</summary>
internal sealed class EncryptedWorkspacePassageIndex : IWorkspacePassageIndexEngine
{
    private const int CurrentVersion = 1;
    private const int HeaderBytes = 40;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private const int AssociatedDataBytes = 16 + 1 + sizeof(long) + sizeof(int);
    private const int MaximumRecordBytes = 8192;
    private const int MaximumModelTokens = 512;
    private const int VectorDimensions = 384;
    private const int MaximumAnchorPage = 512;
    private const int PassageRecord = 11;
    private const int PostingRecord = 12;
    private const int FooterRecord = 13;
    private static readonly byte[] Magic = { (byte)'M', (byte)'B', (byte)'W', (byte)'P', (byte)'A', (byte)'S', (byte)'S', (byte)'1' };
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private readonly object _gate = new();
    private readonly string _directory;
    private readonly string _publishedPath;
    private readonly IPrivateIndexKeyProvider _keys;
    private bool _disposed;

    internal EncryptedWorkspacePassageIndex(string vaultRoot, IPrivateIndexKeyProvider keys)
    {
        ArgumentException.ThrowIfNullOrEmpty(vaultRoot);
        ArgumentNullException.ThrowIfNull(keys);
        _directory = Path.Combine(vaultRoot, "workspace-passage-index");
        _publishedPath = Path.Combine(_directory, "passages.v1.bin");
        _keys = keys;
    }

    public WorkspacePassageIndexStatus ReadStatus(
        IWorkspacePersistence workspace,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            var snapshot = workspace.ReadWorkspaceTree();
            var currentDigest = WorkspaceDigest(snapshot);
            var fileCount = snapshot?.Nodes.Count(node => node.Kind == WorkspaceNodeKind.File) ?? 0;
            if (!File.Exists(_publishedPath))
            {
                return new WorkspacePassageIndexStatus(
                    CurrentVersion,
                    fileCount == 0 ? "empty" : "not_materialized",
                    currentDigest,
                    snapshot?.Revision ?? 0,
                    0,
                    0,
                    0,
                    0,
                    VectorDimensions,
                    fileCount != 0,
                    fileCount == 0 ? null : "workspace_index_not_materialized");
            }

            var key = RequireKey();
            using var aes = new AesGcm(key, TagBytes);
            using var stream = new FileStream(_publishedPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
            var generation = ScanGeneration(stream, aes, onPassage: null, cancellationToken);
            var stale = snapshot is null ||
                !string.Equals(generation.Footer.WorkspaceDigest, currentDigest, StringComparison.Ordinal) ||
                generation.Footer.WorkspaceRevision != snapshot.Revision ||
                !string.Equals(generation.Footer.WorkspaceId, snapshot.WorkspaceId, StringComparison.Ordinal);
            return new WorkspacePassageIndexStatus(
                CurrentVersion,
                stale ? "stale" : generation.Footer.UnsupportedFileCount > 0 ? "partial" : "ready",
                currentDigest,
                snapshot?.Revision ?? 0,
                generation.Footer.FileCount,
                generation.Footer.PassageCount,
                generation.Footer.UnsupportedFileCount,
                generation.Footer.PostingCount,
                generation.Footer.VectorDimensions,
                stale,
                stale ? "workspace_index_stale" :
                    generation.Footer.UnsupportedFileCount > 0 ? "workspace_index_unsupported_files" : null);
        }
    }

    public WorkspacePassageIndexStatus Rebuild(
        IWorkspacePersistence workspace,
        string modelDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (string.IsNullOrWhiteSpace(modelDirectory))
        {
            throw new WorkspacePassageIndexException("workspace_index_model_missing");
        }

        lock (_gate)
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = workspace.ReadWorkspaceTree();
            if (snapshot is null)
            {
                throw new WorkspacePassageIndexException("workspace_index_workspace_not_initialized");
            }
            var workspaceDigest = WorkspaceDigest(snapshot);
            var workspaceId = snapshot?.WorkspaceId ?? string.Empty;
            var workspaceRevision = snapshot?.Revision ?? 0;
            Directory.CreateDirectory(_directory);
            foreach (var staleStage in Directory.EnumerateFiles(_directory, "*.building", SearchOption.TopDirectoryOnly))
            {
                File.Delete(staleStage);
            }

            using var model = new PinnedE5WorkspacePassageEmbeddingModel(modelDirectory);
            using var writer = new GenerationWriter(
                _directory,
                _publishedPath,
                RequireKey(),
                workspaceDigest,
                workspaceId,
                workspaceRevision,
                model.ModelRevision,
                model.Dimensions);

            var indexedFileCount = 0;
            var unsupportedFileCount = 0;
            if (snapshot is not null)
            {
                foreach (var node in snapshot.Nodes)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (node.Kind != WorkspaceNodeKind.File)
                    {
                        continue;
                    }

                    indexedFileCount++;

                    var descriptor = node.Revisions.SingleOrDefault(item => item.Revision == node.ContentRevision)
                        ?? throw new WorkspacePassageIndexException("workspace_index_source_revision_invalid");
                    if (descriptor.Length < 0 || descriptor.Length > DocumentSurface.MaximumTextBytes)
                    {
                        throw new WorkspacePassageIndexException("workspace_index_source_limit");
                    }

                    var file = workspace.ReadWorkspaceFile(node.Id, node.ContentRevision);
                    try
                    {
                        if (file.Revision != node.ContentRevision || file.Length != file.Content.LongLength ||
                            file.Length != descriptor.Length ||
                            !string.Equals(file.Sha256, descriptor.Sha256, StringComparison.Ordinal) ||
                            !string.Equals(Digest(file.Content), descriptor.Sha256, StringComparison.Ordinal))
                        {
                            throw new WorkspacePassageIndexException("workspace_index_source_changed");
                        }

                        var analysis = DocumentSurface.Analyze(node.Name, file.Content);
                        if (analysis.Text is null || analysis.Profile.Format == DocumentFormat.Unsupported)
                        {
                            unsupportedFileCount++;
                            continue;
                        }


                        var chunkOrdinal = 0;
                        foreach (var draft in GuardContentRefusals(WorkspacePassageParser.ParseAnalyzed(
                            node.Id,
                            node.ContentRevision,
                            node.Name,
                            file.Content,
                            analysis,
                            cancellationToken)))
                        {
                            EncodeDraft(
                                draft,
                                node.Id,
                                node.ContentRevision,
                                file.Content.LongLength,
                                model,
                                writer,
                                ref chunkOrdinal,
                                cancellationToken);
                        }
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(file.Content);
                    }
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            var currentSnapshot = workspace.ReadWorkspaceTree();
            if (!string.Equals(WorkspaceDigest(currentSnapshot), workspaceDigest, StringComparison.Ordinal) ||
                (currentSnapshot?.Revision ?? 0) != workspaceRevision ||
                !string.Equals(currentSnapshot?.WorkspaceId ?? string.Empty, workspaceId, StringComparison.Ordinal))
            {
                throw new WorkspacePassageIndexException("workspace_index_canonical_moved");
            }

            writer.Commit(indexedFileCount, unsupportedFileCount);
            var status = ReadStatus(workspace, cancellationToken);
            if (status.Stale)
            {
                throw new WorkspacePassageIndexException("workspace_index_canonical_moved");
            }

            return status;
        }
    }

    public WorkspacePassageAnchorPage ReadAnchors(
        IWorkspacePersistence workspace,
        string fileId,
        long contentRevision,
        int offset,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (string.IsNullOrWhiteSpace(fileId) || contentRevision < 1 || offset < 0 || limit is < 1 or > MaximumAnchorPage)
        {
            throw new WorkspacePassageIndexException("workspace_index_anchor_request_invalid");
        }

        lock (_gate)
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = workspace.ReadWorkspaceTree();
            var currentDigest = WorkspaceDigest(snapshot);
            if (!File.Exists(_publishedPath))
            {
                throw new WorkspacePassageIndexException("workspace_index_not_materialized");
            }

            var key = RequireKey();
            using var aes = new AesGcm(key, TagBytes);
            using var stream = new FileStream(_publishedPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
            var matches = new List<WorkspacePassageAnchor>(Math.Min(limit, 64));
            var pageStart = offset;
            var pageEnd = checked(offset + limit);
            var total = 0;
            var generation = ScanGeneration(stream, aes, (anchor, _, _) =>
            {
                if (string.Equals(anchor.FileId, fileId, StringComparison.Ordinal) && anchor.ContentRevision == contentRevision)
                {
                    if (total >= pageStart && total < pageEnd)
                    {
                        matches.Add(anchor);
                    }

                    total++;
                }
            }, cancellationToken);
            if (snapshot is null ||
                !string.Equals(generation.Footer.WorkspaceDigest, currentDigest, StringComparison.Ordinal) ||
                generation.Footer.WorkspaceRevision != snapshot.Revision ||
                !string.Equals(generation.Footer.WorkspaceId, snapshot.WorkspaceId, StringComparison.Ordinal))
            {
                throw new WorkspacePassageIndexException("workspace_index_stale");
            }
            var sourceNode = snapshot.Nodes.FirstOrDefault(node =>
                node.Kind == WorkspaceNodeKind.File &&
                string.Equals(node.Id, fileId, StringComparison.Ordinal) &&
                node.ContentRevision == contentRevision);
            if (sourceNode is null)
            {
                throw new WorkspacePassageIndexException("workspace_index_anchor_revision_unavailable");
            }

            var descriptor = sourceNode.Revisions.SingleOrDefault(item => item.Revision == contentRevision)
                ?? throw new WorkspacePassageIndexException("workspace_index_source_revision_invalid");
            WorkspaceFileContent sourceFile;
            try
            {
                sourceFile = workspace.ReadWorkspaceFile(fileId, contentRevision);
            }
            catch (WorkspaceRefusalException)
            {
                throw new WorkspacePassageIndexException("workspace_index_source_unavailable");
            }

            try
            {
                if (sourceFile.Revision != contentRevision || sourceFile.Length != sourceFile.Content.LongLength ||
                    sourceFile.Length != descriptor.Length ||
                    !string.Equals(sourceFile.Sha256, descriptor.Sha256, StringComparison.Ordinal) ||
                    !string.Equals(Digest(sourceFile.Content), descriptor.Sha256, StringComparison.Ordinal))
                {
                    throw new WorkspacePassageIndexException("workspace_index_source_changed");
                }

                foreach (var anchor in matches)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (anchor.ByteOffset < 0 || anchor.ByteLength < 1 ||
                        anchor.ByteOffset > sourceFile.Content.LongLength ||
                        anchor.ByteLength > sourceFile.Content.LongLength - anchor.ByteOffset ||
                        !string.Equals(
                            Digest(sourceFile.Content.AsSpan(
                                checked((int)anchor.ByteOffset),
                                checked((int)anchor.ByteLength))),
                            anchor.Sha256,
                            StringComparison.Ordinal))
                    {
                        throw new WorkspacePassageIndexException("workspace_index_anchor_invalid");
                    }
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(sourceFile.Content);
            }

            return new WorkspacePassageAnchorPage(
                fileId,
                contentRevision,
                total,
                offset,
                limit,
                total > pageEnd,
                matches.ToArray());
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            // The engine retains no decrypted generation, model, or cache between calls.
        }
    }

    /// <summary>
    /// S15-T3 hybrid semantic read: encodes the query with the pinned local
    /// model and scores every passage whose exact file identity and content
    /// revision belong to the caller's scope. The authenticated generation must
    /// describe the live workspace before and after the scan, so a stale
    /// generation refuses instead of pretending to be current. Every distinct
    /// scoped anchor is returned (chunks that share one anchor keep their best
    /// score); the shared ranking owns filtering, totals and the final order.
    /// </summary>
    public WorkspacePassageSemanticResult SearchSemantic(
        IWorkspacePersistence workspace,
        string modelDirectory,
        string query,
        IReadOnlyCollection<WorkspacePassageScope> scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(scope);
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new WorkspacePassageIndexException("workspace_index_query_invalid");
        }

        if (string.IsNullOrWhiteSpace(modelDirectory))
        {
            throw new WorkspacePassageIndexException("workspace_index_model_missing");
        }

        lock (_gate)
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = workspace.ReadWorkspaceTree();
            if (!File.Exists(_publishedPath))
            {
                throw new WorkspacePassageIndexException("workspace_index_not_materialized");
            }

            var key = RequireKey();
            using var aes = new AesGcm(key, TagBytes);
            using var stream = new FileStream(_publishedPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
            var header = ReadHeaderAndFooter(stream, aes);
            EnsureCurrent(header, snapshot);
            // The full scan below re-reads the header from the start of the
            // file; the footer pre-check above left the stream at its end.
            stream.Position = 0;

            var allowed = new HashSet<WorkspacePassageScope>(scope);
            using var model = new PinnedE5WorkspacePassageEmbeddingModel(modelDirectory);
            int[]? tokenIds = null;
            float[]? queryVector = null;
            try
            {
                tokenIds = model.TokenizeQuery(query, cancellationToken);
                if (tokenIds.Length is < 1 or > MaximumModelTokens)
                {
                    throw new WorkspacePassageIndexException("workspace_index_token_limit");
                }

                queryVector = model.Encode(tokenIds, cancellationToken);
                if (queryVector.Length != VectorDimensions)
                {
                    throw new WorkspacePassageIndexException("workspace_index_embedding_dimensions_invalid");
                }

                var matches = new Dictionary<(string FileId, long Revision, long Offset, long Length), WorkspacePassageSemanticMatch>();
                var scopedPassages = 0;
                var scan = ScanGeneration(stream, aes, (anchor, vector, _) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!allowed.Contains(new WorkspacePassageScope(anchor.FileId, anchor.ContentRevision)))
                    {
                        return;
                    }

                    scopedPassages++;
                    var similarity = Dot(queryVector, vector);
                    if (!double.IsFinite(similarity))
                    {
                        throw new WorkspacePassageIndexException("workspace_index_embedding_invalid");
                    }

                    var matchKey = (anchor.FileId, anchor.ContentRevision, anchor.ByteOffset, anchor.ByteLength);
                    if (matches.TryGetValue(matchKey, out var existing) && existing.Similarity >= similarity)
                    {
                        return;
                    }

                    matches[matchKey] = new WorkspacePassageSemanticMatch(
                        anchor.FileId,
                        anchor.ContentRevision,
                        anchor.ByteOffset,
                        anchor.ByteLength,
                        anchor.Sha256,
                        anchor.Kind,
                        anchor.Attribution,
                        anchor.ChunkOrdinal,
                        similarity);
                }, cancellationToken);
                EnsureCurrent(scan.Footer, snapshot);

                var ordered = new List<WorkspacePassageSemanticMatch>(matches.Values);
                ordered.Sort(static (left, right) =>
                {
                    var similarity = right.Similarity.CompareTo(left.Similarity);
                    if (similarity != 0)
                    {
                        return similarity;
                    }

                    var file = string.CompareOrdinal(left.FileId, right.FileId);
                    if (file != 0)
                    {
                        return file;
                    }

                    var offset = left.ByteOffset.CompareTo(right.ByteOffset);
                    return offset != 0 ? offset : left.ByteLength.CompareTo(right.ByteLength);
                });

                return new WorkspacePassageSemanticResult(
                    model.ModelRevision,
                    model.Dimensions,
                    tokenIds.Length,
                    scopedPassages,
                    ordered.ToArray());
            }
            finally
            {
                if (tokenIds is not null)
                {
                    CryptographicOperations.ZeroMemory(System.Runtime.InteropServices.MemoryMarshal.AsBytes(tokenIds.AsSpan()));
                }

                if (queryVector is not null)
                {
                    CryptographicOperations.ZeroMemory(System.Runtime.InteropServices.MemoryMarshal.AsBytes(queryVector.AsSpan()));
                }
            }
        }
    }

    private static WorkspaceFooter ReadHeaderAndFooter(FileStream stream, AesGcm aes)
    {
        if (stream.Length < HeaderBytes)
        {
            throw new InvalidDataException("workspace_index_generation_truncated");
        }

        var header = new byte[HeaderBytes];
        byte[]? fileId = null;
        try
        {
            ReadExactly(stream, header);
            if (!header.AsSpan(0, Magic.Length).SequenceEqual(Magic) ||
                BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8, 4)) != CurrentVersion)
            {
                throw new InvalidDataException("workspace_index_format_invalid");
            }

            var footerOffset = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(12, 8));
            var footerLength = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(20, 4));
            fileId = header.AsSpan(24, 16).ToArray();
            if (footerOffset < HeaderBytes || footerLength <= 0 || footerLength > MaximumRecordBytes ||
                footerOffset > stream.Length ||
                checked(footerOffset + RecordFrameLength(footerLength)) != stream.Length)
            {
                throw new InvalidDataException("workspace_index_header_invalid");
            }

            stream.Position = footerOffset;
            var (footerType, footerPayload) = ReadRecord(stream, aes, fileId, footerOffset, stream.Length);
            try
            {
                if (footerType != FooterRecord)
                {
                    throw new InvalidDataException("workspace_index_footer_invalid");
                }

                return ReadFooter(footerPayload);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(footerPayload);
            }
        }
        finally
        {
            if (fileId is not null)
            {
                CryptographicOperations.ZeroMemory(fileId);
            }

            CryptographicOperations.ZeroMemory(header);
        }
    }

    private static void EnsureCurrent(WorkspaceFooter footer, WorkspaceSnapshot? snapshot)
    {
        if (snapshot is null ||
            !string.Equals(footer.WorkspaceDigest, WorkspaceDigest(snapshot), StringComparison.Ordinal) ||
            footer.WorkspaceRevision != snapshot.Revision ||
            !string.Equals(footer.WorkspaceId, snapshot.WorkspaceId, StringComparison.Ordinal))
        {
            throw new WorkspacePassageIndexException("workspace_index_stale");
        }
    }

    private static double Dot(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        if (left.Length != right.Length || left.Length != VectorDimensions)
        {
            throw new WorkspacePassageIndexException("workspace_index_embedding_dimensions_invalid");
        }

        double sum = 0;
        for (var index = 0; index < left.Length; index++)
        {
            sum += (double)left[index] * right[index];
        }

        return sum;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
        }

        GC.SuppressFinalize(this);
    }

    private void EncodeDraft(
        WorkspacePassageDraft draft,
        string sourceFileId,
        long sourceRevision,
        long sourceLength,
        IWorkspacePassageEmbeddingModel model,
        GenerationWriter writer,
        ref int chunkOrdinal,
        CancellationToken cancellationToken)
    {
        if (draft.ByteOffset < 0 || draft.ByteLength < 1 || draft.ByteOffset > sourceLength ||
            draft.ByteLength > sourceLength - draft.ByteOffset || draft.Text.Length == 0)
        {
            throw new WorkspacePassageIndexException("workspace_index_anchor_invalid");
        }

        var pending = new Stack<(int Start, int Length)>();
        pending.Push((0, draft.Text.Length));
        while (pending.Count != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var segment = pending.Pop();
            var tokenIds = model.Tokenize(draft.Text.AsSpan(segment.Start, segment.Length), cancellationToken);
            if (tokenIds.Length > MaximumModelTokens)
            {
                CryptographicOperations.ZeroMemory(System.Runtime.InteropServices.MemoryMarshal.AsBytes(tokenIds.AsSpan()));
                var split = FindScalarSplit(draft.Text, segment.Start, segment.Length);
                if (split <= segment.Start || split >= segment.Start + segment.Length)
                {
                    throw new WorkspacePassageIndexException("workspace_index_single_scalar_token_limit");
                }

                var rightLength = segment.Start + segment.Length - split;
                pending.Push((split, rightLength));
                pending.Push((segment.Start, split - segment.Start));
                continue;
            }

            if (tokenIds.Length == 0)
            {
                CryptographicOperations.ZeroMemory(System.Runtime.InteropServices.MemoryMarshal.AsBytes(tokenIds.AsSpan()));
                throw new WorkspacePassageIndexException("workspace_index_tokenizer_empty");
            }

            float[]? vector = null;
            try
            {
                vector = model.Encode(tokenIds, cancellationToken);
                if (vector.Length != model.Dimensions || vector.Length != VectorDimensions)
                {
                    throw new WorkspacePassageIndexException("workspace_index_embedding_dimensions_invalid");
                }

                writer.WritePassage(draft, sourceFileId, sourceRevision, chunkOrdinal++, tokenIds, vector);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(System.Runtime.InteropServices.MemoryMarshal.AsBytes(tokenIds.AsSpan()));
                if (vector is not null)
                {
                    CryptographicOperations.ZeroMemory(System.Runtime.InteropServices.MemoryMarshal.AsBytes(vector.AsSpan()));
                }
            }
        }
    }

    /// <summary>
    /// The parser refuses invalid source content with its own stable codes.
    /// Those are owner-visible content refusals, so they reach the caller as a
    /// passage-index refusal instead of being flattened into a generic
    /// infrastructure or authorization error.
    /// </summary>
    private static IEnumerable<WorkspacePassageDraft> GuardContentRefusals(IEnumerable<WorkspacePassageDraft> drafts)
    {
        using var enumerator = drafts.GetEnumerator();
        while (true)
        {
            WorkspacePassageDraft? current = null;
            var hasNext = false;
            try
            {
                hasNext = enumerator.MoveNext();
                if (hasNext)
                {
                    current = enumerator.Current;
                }
            }
            catch (InvalidDataException ex) when (ex.Message.StartsWith("workspace_index_", StringComparison.Ordinal))
            {
                throw new WorkspacePassageIndexException(ex.Message);
            }

            if (!hasNext)
            {
                yield break;
            }

            yield return current!;
        }
    }

    private static int FindScalarSplit(string text, int start, int length)
    {
        var end = checked(start + length);
        var scalarCount = 0;
        for (var cursor = start; cursor < end; scalarCount++)
        {
            var status = Rune.DecodeFromUtf16(text.AsSpan(cursor, end - cursor), out _, out var consumed);
            if (status != System.Buffers.OperationStatus.Done)
            {
                throw new WorkspacePassageIndexException("workspace_index_text_encoding_invalid");
            }

            cursor += consumed;
        }

        if (scalarCount < 2)
        {
            return start;
        }

        var target = scalarCount / 2;
        var count = 0;
        var position = start;
        while (position < end && count < target)
        {
            _ = Rune.DecodeFromUtf16(text.AsSpan(position, end - position), out _, out var consumed);
            position += consumed;
            count++;
        }

        return position;
    }

    private delegate void PassageVisitor(WorkspacePassageAnchor anchor, ReadOnlySpan<float> vector, long ordinal);

    private GenerationScan ScanGeneration(
        FileStream stream,
        AesGcm aes,
        PassageVisitor? onPassage,
        CancellationToken cancellationToken)
    {
        if (stream.Length < HeaderBytes)
        {
            throw new InvalidDataException("workspace_index_generation_truncated");
        }

        var header = new byte[HeaderBytes];
        byte[]? fileId = null;
        try
        {
            ReadExactly(stream, header);
            if (!header.AsSpan(0, Magic.Length).SequenceEqual(Magic) ||
                BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8, 4)) != CurrentVersion)
            {
                throw new InvalidDataException("workspace_index_format_invalid");
            }

            var footerOffset = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(12, 8));
            var footerLength = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(20, 4));
            fileId = header.AsSpan(24, 16).ToArray();
            if (footerOffset < HeaderBytes || footerLength <= 0 || footerLength > MaximumRecordBytes ||
                footerOffset > stream.Length ||
                checked(footerOffset + RecordFrameLength(footerLength)) != stream.Length)
            {
                throw new InvalidDataException("workspace_index_header_invalid");
            }

            stream.Position = HeaderBytes;
            long passageCount = 0;
            long postingCount = 0;
            while (stream.Position < footerOffset)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var recordStart = stream.Position;
                var (type, payload) = ReadRecord(stream, aes, fileId, recordStart, footerOffset);
                try
                {
                    if (type == PassageRecord)
                    {
                        var anchor = ReadPassageMetadata(payload, passageCount, out var vectorOffset);
                        onPassage?.Invoke(
                            anchor,
                            System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(
                                payload.AsSpan(vectorOffset, VectorDimensions * sizeof(float))),
                            passageCount);
                        passageCount++;
                    }
                    else if (type == PostingRecord)
                    {
                        ReadPosting(payload, passageCount);
                        postingCount++;
                    }
                    else
                    {
                        throw new InvalidDataException("workspace_index_record_type_invalid");
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(payload);
                }
            }

            if (stream.Position != footerOffset)
            {
                throw new InvalidDataException("workspace_index_record_boundary_invalid");
            }

            var (footerType, footerPayload) = ReadRecord(stream, aes, fileId, footerOffset, stream.Length);
            try
            {
                if (footerType != FooterRecord)
                {
                    throw new InvalidDataException("workspace_index_footer_invalid");
                }

                var footer = ReadFooter(footerPayload);
                if (footer.PassageCount != passageCount || footer.PostingCount != postingCount ||
                    footer.VectorDimensions != VectorDimensions)
                {
                    throw new InvalidDataException("workspace_index_generation_invalid");
                }

                return new GenerationScan(footer);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(footerPayload);
            }
        }
        finally
        {
            if (fileId is not null)
            {
                CryptographicOperations.ZeroMemory(fileId);
            }

            CryptographicOperations.ZeroMemory(header);
        }
    }

    private static (byte Type, byte[] Payload) ReadRecord(
        FileStream stream,
        AesGcm aes,
        byte[] fileId,
        long offset,
        long maximumOffset)
    {
        Span<byte> frame = stackalloc byte[5];
        ReadExactly(stream, frame);
        var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(frame[..4]);
        var type = frame[4];
        if (payloadLength < 1 || payloadLength > MaximumRecordBytes ||
            type is not (PassageRecord or PostingRecord or FooterRecord) ||
            checked(offset + RecordFrameLength(payloadLength)) > maximumOffset ||
            (type == PostingRecord && payloadLength != 12))
        {
            throw new InvalidDataException("workspace_index_record_invalid");
        }

        var nonce = new byte[NonceBytes];
        var ciphertext = new byte[payloadLength];
        Span<byte> tag = stackalloc byte[TagBytes];
        var plaintext = new byte[payloadLength];
        Span<byte> associatedData = stackalloc byte[AssociatedDataBytes];
        try
        {
            ReadExactly(stream, nonce);
            ReadExactly(stream, ciphertext);
            ReadExactly(stream, tag);
            WriteAssociatedData(fileId, type, offset, associatedData);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, associatedData);
            return (type, plaintext);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(nonce);
            CryptographicOperations.ZeroMemory(ciphertext);
            CryptographicOperations.ZeroMemory(tag);
            CryptographicOperations.ZeroMemory(associatedData);
        }
    }

    private static void WriteAssociatedData(byte[] fileId, byte type, long offset, Span<byte> destination)
    {
        if (fileId.Length != 16 || destination.Length != AssociatedDataBytes)
        {
            throw new InvalidDataException("workspace_index_associated_data_invalid");
        }

        fileId.CopyTo(destination);
        destination[16] = type;
        BinaryPrimitives.WriteInt64LittleEndian(destination[17..25], offset);
        BinaryPrimitives.WriteInt32LittleEndian(destination[25..], CurrentVersion);
    }

    private static void WriteRecord(
        FileStream stream,
        AesGcm aes,
        byte[] fileId,
        PrivateIndexNonceSequence nonces,
        byte type,
        ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 1 || payload.Length > MaximumRecordBytes)
        {
            throw new WorkspacePassageIndexException("workspace_index_record_limit");
        }

        var offset = stream.Position;

        var nonce = nonces.Next();
        var ciphertext = GC.AllocateUninitializedArray<byte>(payload.Length);
        Span<byte> tag = stackalloc byte[TagBytes];
        Span<byte> associatedData = stackalloc byte[AssociatedDataBytes];
        Span<byte> frame = stackalloc byte[5];
        try
        {
            WriteAssociatedData(fileId, type, offset, associatedData);
            aes.Encrypt(nonce, payload, ciphertext, tag, associatedData);
            BinaryPrimitives.WriteInt32LittleEndian(frame[..4], payload.Length);
            frame[4] = type;
            stream.Write(frame);
            stream.Write(nonce);
            stream.Write(ciphertext);
            stream.Write(tag);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(nonce);
            CryptographicOperations.ZeroMemory(ciphertext);
            CryptographicOperations.ZeroMemory(tag);
            CryptographicOperations.ZeroMemory(associatedData);
            CryptographicOperations.ZeroMemory(frame);
        }
    }


    private static WorkspacePassageAnchor ReadPassageMetadata(byte[] payload, long expectedOrdinal, out int vectorOffset)
    {
        using var buffer = new MemoryStream(payload, writable: false);
        using var reader = new BinaryReader(buffer, StrictUtf8, leaveOpen: true);
        if (reader.ReadInt32() != CurrentVersion || reader.ReadInt64() != expectedOrdinal)
        {
            throw new InvalidDataException("workspace_index_passage_order_invalid");
        }

        var fileId = ReadString(reader, 128);
        var revision = reader.ReadInt64();
        var offset = reader.ReadInt64();
        var length = reader.ReadInt64();
        var digestBytes = reader.ReadBytes(32);
        var kind = ReadString(reader, 64);
        var attribution = ReadString(reader, 32);
        var chunkOrdinal = reader.ReadInt32();
        var dimensions = reader.ReadInt32();
        var vectorBytes = checked(dimensions * sizeof(float));
        vectorOffset = checked((int)buffer.Position);
        if (fileId.Length == 0 || revision < 1 || offset < 0 || length < 1 || digestBytes.Length != 32 ||
            kind.Length == 0 || attribution.Length == 0 || chunkOrdinal < 0 || dimensions != VectorDimensions ||
            buffer.Length - vectorOffset != vectorBytes)
        {
            CryptographicOperations.ZeroMemory(digestBytes);
            throw new InvalidDataException("workspace_index_passage_invalid");
        }

        var digest = Convert.ToHexString(digestBytes).ToLowerInvariant();
        CryptographicOperations.ZeroMemory(digestBytes);
        buffer.Position = buffer.Length;
        return new WorkspacePassageAnchor(fileId, revision, offset, length, digest, kind, attribution, chunkOrdinal);
    }

    private static void ReadPosting(byte[] payload, long passageCount)
    {
        var passageOrdinal = BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(0, sizeof(long)));
        var tokenId = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(sizeof(long), sizeof(int)));
        if (passageOrdinal < 0 || passageOrdinal >= passageCount || tokenId < 0)
        {
            throw new InvalidDataException("workspace_index_posting_invalid");
        }
    }

    private static WorkspaceFooter ReadFooter(byte[] payload)
    {
        using var buffer = new MemoryStream(payload, writable: false);
        using var reader = new BinaryReader(buffer, StrictUtf8, leaveOpen: true);
        if (reader.ReadInt32() != CurrentVersion)
        {
            throw new InvalidDataException("workspace_index_footer_version_invalid");
        }

        var workspaceId = ReadString(reader, 128);
        var digest = ReadString(reader, 64);
        var workspaceRevision = reader.ReadInt64();
        var fileCount = reader.ReadInt32();
        var unsupportedFileCount = reader.ReadInt32();
        var passageCount = reader.ReadInt64();
        var postingCount = reader.ReadInt64();
        var dimensions = reader.ReadInt32();
        var modelRevision = ReadString(reader, 64);
        if (buffer.Position != buffer.Length || fileCount < 0 || unsupportedFileCount < 0 ||
            passageCount < 0 || postingCount < 0 || workspaceRevision < 0 ||
            digest.Length != 64 || digest.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')) ||
            dimensions != VectorDimensions || modelRevision.Length == 0)
        {
            throw new InvalidDataException("workspace_index_footer_invalid");
        }

        return new WorkspaceFooter(
            workspaceId, digest, workspaceRevision, fileCount, unsupportedFileCount,
            passageCount, postingCount, dimensions, modelRevision);
    }

    private static string ReadString(BinaryReader reader, int maximumBytes)
    {
        var length = reader.ReadInt32();
        if (length < 0 || length > maximumBytes)
        {
            throw new InvalidDataException("workspace_index_string_invalid");
        }

        var bytes = reader.ReadBytes(length);
        if (bytes.Length != length)
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw new InvalidDataException("workspace_index_record_truncated");
        }

        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            throw new InvalidDataException("workspace_index_string_invalid");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static byte[] WriteString(string value)
    {
        var bytes = StrictUtf8.GetBytes(value);
        return bytes;
    }

    private static string WorkspaceDigest(WorkspaceSnapshot? snapshot)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendString(hash, snapshot?.WorkspaceId ?? string.Empty);
        AppendInt32(hash, snapshot?.FormatVersion ?? 0);
        AppendInt32(hash, snapshot?.MetadataSchemaVersion ?? 0);
        AppendInt64(hash, snapshot?.Revision ?? 0);
        AppendString(hash, snapshot?.RootId ?? string.Empty);
        if (snapshot is not null)
        {
            foreach (var node in snapshot.Nodes.OrderBy(candidate => candidate.Id, StringComparer.Ordinal))
            {
                AppendString(hash, node.Id);
                AppendInt32(hash, (int)node.Kind);
                AppendString(hash, node.ParentId ?? string.Empty);
                AppendString(hash, node.Name);
                AppendInt64(hash, node.Revision);
                AppendInt64(hash, node.ContentRevision);
                if (node.Kind == WorkspaceNodeKind.File)
                {
                    var descriptor = node.Revisions.SingleOrDefault(item => item.Revision == node.ContentRevision)
                        ?? throw new WorkspacePassageIndexException("workspace_index_source_revision_invalid");
                    AppendInt64(hash, descriptor.Length);
                    AppendString(hash, descriptor.Sha256);
                }
            }
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void AppendString(IncrementalHash hash, string value)
    {
        var bytes = StrictUtf8.GetBytes(value);
        try
        {
            AppendInt32(hash, bytes.Length);
            hash.AppendData(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static void AppendInt32(IncrementalHash hash, int value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
        hash.AppendData(buffer);
    }

    private static void AppendInt64(IncrementalHash hash, long value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(buffer, value);
        hash.AppendData(buffer);
    }

    private static string Digest(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private byte[] RequireKey()
    {
        var key = _keys.PrivateIndexKey;
        if (key is null || key.Length != 32)
        {
            throw new WorkspacePassageIndexException("vault_locked");
        }

        return key;
    }

    private static int RecordFrameLength(int payloadLength) => checked(sizeof(int) + sizeof(byte) + NonceBytes + payloadLength + TagBytes);

    private static void ReadExactly(Stream stream, Span<byte> destination)
    {
        var offset = 0;
        while (offset < destination.Length)
        {
            var count = stream.Read(destination[offset..]);
            if (count <= 0)
            {
                throw new EndOfStreamException("workspace_index_record_truncated");
            }

            offset += count;
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed record WorkspaceFooter(
        string WorkspaceId,
        string WorkspaceDigest,
        long WorkspaceRevision,
        int FileCount,
        int UnsupportedFileCount,
        long PassageCount,
        long PostingCount,
        int VectorDimensions,
        string ModelRevision);

    private sealed record GenerationScan(WorkspaceFooter Footer);

    private sealed class GenerationWriter : IDisposable
    {
        private readonly string _stagePath;
        private readonly string _publishedPath;
        private readonly byte[] _key;
        private readonly byte[] _fileId = RandomNumberGenerator.GetBytes(16);
        private readonly AesGcm _aes;
        private readonly PrivateIndexNonceSequence _nonces = new();
        private readonly FileStream _stream;
        private readonly string _workspaceDigest;
        private readonly string _workspaceId;
        private readonly long _workspaceRevision;
        private readonly string _modelRevision;
        private readonly int _dimensions;
        private long _passageCount;
        private long _postingCount;
        private bool _committed;
        private bool _disposed;

        internal GenerationWriter(
            string directory,
            string publishedPath,
            byte[] key,
            string workspaceDigest,
            string workspaceId,
            long workspaceRevision,
            string modelRevision,
            int dimensions)
        {
            if (key.Length != 32 || dimensions != VectorDimensions)
            {
                throw new WorkspacePassageIndexException("workspace_index_key_or_model_invalid");
            }

            _key = key;
            _publishedPath = publishedPath;
            _workspaceDigest = workspaceDigest;
            _workspaceId = workspaceId;
            _workspaceRevision = workspaceRevision;
            _modelRevision = modelRevision;
            _dimensions = dimensions;
            _aes = new AesGcm(_key, TagBytes);
            _stagePath = Path.Combine(directory, ".passages." + Guid.NewGuid().ToString("N") + ".building");
            _stream = new FileStream(_stagePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 64 * 1024, FileOptions.WriteThrough | FileOptions.SequentialScan);
            var header = new byte[HeaderBytes];
            try
            {
                Magic.CopyTo(header, 0);
                BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8, 4), CurrentVersion);
                _fileId.CopyTo(header, 24);
                _stream.Write(header);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(header);
            }
        }

        internal void WritePassage(
            WorkspacePassageDraft draft,
            string sourceFileId,
            long sourceRevision,
            int chunkOrdinal,
            ReadOnlySpan<int> tokenIds,
            ReadOnlySpan<float> vector)
        {
            if (tokenIds.Length is < 1 or > MaximumModelTokens || vector.Length != VectorDimensions || sourceRevision < 1)
            {
                throw new WorkspacePassageIndexException("workspace_index_embedding_invalid");
            }

            var payload = BuildPassagePayload(draft, sourceFileId, sourceRevision, _passageCount, chunkOrdinal, vector);
            try
            {
                WriteRecord(_stream, _aes, _fileId, _nonces, PassageRecord, payload);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(payload);
            }

            Span<byte> posting = stackalloc byte[sizeof(long) + sizeof(int)];
            var unique = new HashSet<int>();
            try
            {
                foreach (var tokenId in tokenIds)
                {
                    if (!unique.Add(tokenId))
                    {
                        continue;
                    }

                    BinaryPrimitives.WriteInt64LittleEndian(posting[..sizeof(long)], _passageCount);
                    BinaryPrimitives.WriteInt32LittleEndian(posting[sizeof(long)..], tokenId);
                    WriteRecord(_stream, _aes, _fileId, _nonces, PostingRecord, posting);
                    _postingCount++;
                    CryptographicOperations.ZeroMemory(posting);
                }

                _passageCount++;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(posting);
                unique.Clear();
            }
        }

        internal void Commit(int fileCount, int unsupportedFileCount)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_committed)
            {
                throw new InvalidOperationException("workspace_index_generation_already_committed");
            }

            var footerPayload = BuildFooterPayload(fileCount, unsupportedFileCount);
            var footerOffset = _stream.Position;
            try
            {
                WriteRecord(_stream, _aes, _fileId, _nonces, FooterRecord, footerPayload);
                var footerLength = footerPayload.Length;
                var header = new byte[HeaderBytes];
                try
                {
                    Magic.CopyTo(header, 0);
                    BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8, 4), CurrentVersion);
                    BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(12, 8), footerOffset);
                    BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(20, 4), footerLength);
                    _fileId.CopyTo(header, 24);
                    _stream.Position = 0;
                    _stream.Write(header);
                    _stream.Flush(flushToDisk: true);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(header);
                }

                _stream.Dispose();
                File.Move(_stagePath, _publishedPath, overwrite: true);
                _committed = true;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(footerPayload);
            }
        }

        private byte[] BuildPassagePayload(
            WorkspacePassageDraft draft,
            string sourceFileId,
            long sourceRevision,
            long ordinal,
            int chunkOrdinal,
            ReadOnlySpan<float> vector)
        {
            var fileId = StrictUtf8.GetBytes(sourceFileId);
            var kind = StrictUtf8.GetBytes(draft.Kind);
            var attribution = StrictUtf8.GetBytes(draft.Attribution);
            var digest = Convert.FromHexString(draft.AnchorSha256);
            var payload = new byte[checked(4 + 8 + 4 + fileId.Length + 8 * 3 + 32 + 4 + kind.Length + 4 + attribution.Length + 4 + 4 + (vector.Length * sizeof(float)))];
            try
            {
                var position = 0;
                BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(position, 4), CurrentVersion);
                position += 4;
                BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(position, 8), ordinal);
                position += 8;
                WriteBytes(payload, ref position, fileId);
                BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(position, 8), sourceRevision);
                position += 8;
                BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(position, 8), draft.ByteOffset);
                position += 8;
                BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(position, 8), draft.ByteLength);
                position += 8;
                digest.CopyTo(payload, position);
                position += 32;
                WriteBytes(payload, ref position, kind);
                WriteBytes(payload, ref position, attribution);
                BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(position, 4), chunkOrdinal);
                position += 4;
                BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(position, 4), vector.Length);
                position += 4;
                foreach (var value in vector)
                {
                    if (!float.IsFinite(value))
                    {
                        throw new WorkspacePassageIndexException("workspace_index_embedding_invalid");
                    }

                    BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(position, 4), BitConverter.SingleToInt32Bits(value));
                    position += 4;
                }

                if (position != payload.Length)
                {
                    throw new WorkspacePassageIndexException("workspace_index_record_invalid");
                }

                return payload;
            }
            catch
            {
                CryptographicOperations.ZeroMemory(payload);
                throw;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(fileId);
                CryptographicOperations.ZeroMemory(kind);
                CryptographicOperations.ZeroMemory(attribution);
                CryptographicOperations.ZeroMemory(digest);
            }
        }

        private byte[] BuildFooterPayload(int fileCount, int unsupportedFileCount)
        {
            var workspaceId = StrictUtf8.GetBytes(_workspaceId);
            var digest = StrictUtf8.GetBytes(_workspaceDigest);
            var model = StrictUtf8.GetBytes(_modelRevision);
            var payload = new byte[checked(4 + 4 + workspaceId.Length + 4 + digest.Length + 8 + 4 + 4 + 8 + 8 + 4 + 4 + model.Length)];
            try
            {
                var position = 0;
                BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(position, 4), CurrentVersion);
                position += 4;
                WriteBytes(payload, ref position, workspaceId);
                WriteBytes(payload, ref position, digest);
                BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(position, 8), _workspaceRevision);
                position += 8;
                BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(position, 4), fileCount);
                position += 4;
                BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(position, 4), unsupportedFileCount);
                position += 4;
                BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(position, 8), _passageCount);
                position += 8;
                BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(position, 8), _postingCount);
                position += 8;
                BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(position, 4), _dimensions);
                position += 4;
                WriteBytes(payload, ref position, model);
                if (position != payload.Length)
                {
                    throw new WorkspacePassageIndexException("workspace_index_footer_invalid");
                }

                return payload;
            }
            catch
            {
                CryptographicOperations.ZeroMemory(payload);
                throw;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(workspaceId);
                CryptographicOperations.ZeroMemory(digest);
                CryptographicOperations.ZeroMemory(model);
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
            _aes.Dispose();
            CryptographicOperations.ZeroMemory(_fileId);
            if (!_committed && File.Exists(_stagePath))
            {
                File.Delete(_stagePath);
            }
        }
    }

    private static void WriteBytes(byte[] destination, ref int position, byte[] value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(destination.AsSpan(position, 4), value.Length);
        position += 4;
        value.CopyTo(destination, position);
        position += value.Length;
    }

    private static void WriteBytes(byte[] destination, ref int position, ReadOnlySpan<byte> value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(destination.AsSpan(position, 4), value.Length);
        position += 4;
        value.CopyTo(destination.AsSpan(position));
        position += value.Length;
    }
}
