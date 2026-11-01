using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MetaBrain.Core.Security;

namespace MetaBrain.Connections;

/// <summary>One retained original-byte revision as the encrypted tree stores it.</summary>
internal sealed record WorkspaceRevisionRecord(long Revision, long Length, string Sha256, string StorageName);

/// <summary>One logical node as the encrypted tree stores it.</summary>
/// <remarks>
/// <see cref="State"/> is null only in a workspace written before this node
/// carried a lifecycle state, which is read as
/// <see cref="WorkspaceNodeStates.Live"/>. A tombstone keeps its identity,
/// parent, exact name, node revision, retained revisions and
/// <see cref="DeletionId"/> so one delete act can be restored as one unit.
/// </remarks>
internal sealed record WorkspaceNodeRecord(
    string Id,
    string Kind,
    string? ParentId,
    string Name,
    long Revision,
    long ContentRevision,
    WorkspaceRevisionRecord[] Revisions,
    string? State,
    string? DeletionId);

/// <summary>
/// One citation anchor as the encrypted tree stores it: a document id, one
/// exact content revision, a byte range and the digest of the anchored bytes.
/// It stores no path and no display name, so a rename or a move cannot rebind
/// it, and a stale anchor keeps its original revision rather than following
/// newer content.
/// </summary>
internal sealed record WorkspaceAnchorRecord(
    string Id,
    string FileId,
    long ContentRevision,
    long Offset,
    long Length,
    string Sha256,
    string? State);

/// <summary>
/// The whole canonical workspace state: the logical tree, its owner-authored
/// names and metadata, the retained revision descriptors, the citation
/// anchors and the workspace identity. It is persisted only as one
/// authenticated ciphertext.
/// </summary>
internal sealed record WorkspaceStateRecord(
    int FormatVersion,
    int MetadataSchemaVersion,
    string WorkspaceId,
    long Revision,
    string RootId,
    WorkspaceNodeRecord[] Nodes,
    WorkspaceAnchorRecord[]? Anchors);

/// <summary>
/// The one committed pointer. It carries the authenticated envelope version and
/// an opaque generation name; the generation is bound into the state's
/// associated data, so a state file cannot be moved to another generation.
/// </summary>
internal sealed record WorkspaceHeadRecord(int FormatVersion, string Generation);

/// <summary>One encrypted envelope, the same framing the vault files use.</summary>
internal sealed record WorkspaceEnvelopeRecord(int FormatVersion, string Nonce, string Ciphertext, string Tag);

/// <summary>What opening the committed workspace found.</summary>
/// <remarks>
/// <see cref="State"/> null with a null <see cref="ErrorCode"/> means no
/// workspace has ever been committed. A non-null <see cref="ErrorCode"/> means
/// a committed workspace exists but must not be served (authentication failure,
/// truncation, unreadable directory or a newer format): nothing was rewritten
/// or deleted, so the saved data stays where it is for recovery.
/// </remarks>
internal sealed record WorkspaceOpenResult(WorkspaceStateRecord? State, string? ErrorCode);

/// <summary>
/// One retained original-byte revision captured for the encrypted backup. The
/// bytes are plaintext only inside the backup capture and are sealed into the
/// backup container before anything is written; the caller zeroizes them.
/// </summary>
internal sealed record WorkspaceBackupContent(
    string FileId,
    long Revision,
    long Length,
    string Sha256,
    byte[] Content);

/// <summary>
/// The whole workspace state plus every retained revision of every live file
/// and every tombstone, captured at one committed point. The state keeps its
/// exact workspace/root/node identities, names, parent links, revisions and
/// anchors, so a restore reproduces the owner's workspace rather than
/// importing its bytes as new documents. Purged identities live in the
/// vault's one durable purge ledger, which travels in the same backup.
/// </summary>
internal sealed record WorkspaceBackupSnapshot(
    WorkspaceStateRecord State,
    WorkspaceBackupContent[] Contents);

/// <summary>
/// One content revision to stage before the state commit. Either the exact
/// bytes are supplied, or an existing retained revision is named and the store
/// streams it through decrypt and a fresh encrypt under the new subject, so a
/// copy never holds more than one original buffer at a time.
/// </summary>
internal sealed record StagedWorkspaceBlob(
    string FileId,
    WorkspaceRevisionRecord Revision,
    byte[]? Content,
    string? CopyFromFileId,
    WorkspaceRevisionRecord? CopyFromRevision);

/// <summary>
/// The original-byte persistence of the logical workspace: one encrypted tree,
/// one encrypted ciphertext per retained file revision and one atomically
/// replaced committed pointer, all sealed with the unlocked vault data key.
/// </summary>
/// <remarks>
/// <para>
/// Key and lifecycle: the store holds no key of its own and never unlocks. The
/// caller passes the live vault data key inside its unlocked lease; no agent
/// ever receives it and no separate key store exists.
/// </para>
/// <para>
/// Commit: a change stages its ciphertext (content first, then the new state
/// generation) and only then replaces <c>head.json</c> with one atomic
/// write-through move. The previous generation therefore stays authoritative
/// until the pointer swap, and the pointer names exactly one committed state.
/// Files a crash staged but never committed are unreachable from the committed
/// state and are reclaimed on the next successful open; nothing reachable from
/// the committed state is deleted.
/// </para>
/// <para>
/// Confidentiality: names, parent IDs, node metadata, revision descriptors and
/// the content itself exist on disk only inside AES-GCM envelopes whose
/// associated data binds the vault identity, this purpose and the exact
/// (workspace/file/revision) subject. Only the envelope version and opaque
/// storage names are public.
/// </para>
/// </remarks>
internal sealed class EncryptedWorkspaceStore
{
    internal const string FolderKind = WorkspaceNodeKinds.Folder;
    internal const string FileKind = WorkspaceNodeKinds.File;
    internal const string WorkspaceDirectoryName = "workspace";
    // S12-T3 adds the node lifecycle state, the deletion group and the
    // revision-pinned citation anchors to the authenticated state as optional
    // members. A state that omits them means exactly "every node is live and
    // there are no anchors", which is the shape S12-T2 wrote, so the extension
    // is additive and stays inside the same format: the workspace format has
    // never been released (1.1.0 has no workspace store, so no reader of an
    // older workspace format exists), nothing already committed changes
    // version or meaning, and a commit writes back the version it read. Any
    // other format or schema number, older or newer, is refused before a
    // mutation instead of being converted.
    internal const int CurrentFormatVersion = 1;
    internal const int CurrentMetadataSchemaVersion = 1;
    internal const int MaximumNameLength = 255;
    internal const int MaximumNodes = 100_000;
    internal const int MaximumFileRevisions = 1_000;
    // One act carries one original file inside the owner-channel request, and
    // that request is bounded at 2 MiB of JSON. A 1 MiB original (base64 of
    // 1.37 MiB plus envelope overhead) fits that bound with room to spare, so
    // the store refuses a larger original with a real code instead of the
    // transport cutting the line. A bigger format needs a separately approved
    // chunking specification, not an invented one here.
    internal const int MaximumContentBytes = 1024 * 1024;

    private const int CurrentEnvelopeVersion = 1;
    private const int MaximumStateBytes = 64 * 1024 * 1024;
    private const int MaximumHeadBytes = 64 * 1024;
    private const int MaximumStateEnvelopeBytes = MaximumStateBytes + (MaximumStateBytes / 3) + (64 * 1024);
    private const int MaximumBlobEnvelopeBytes = MaximumContentBytes + (MaximumContentBytes / 3) + (64 * 1024);
    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private const int Sha256HexLength = 64;
    private const int IdentifierHexLength = 32;
    private const string StateExtension = ".mbs";
    private const string BlobExtension = ".mbw";
    private const string HeadFileName = "head.json";
    private const string TemporarySuffix = ".tmp";
    private const string StateDirectoryName = "state";
    private const string BlobDirectoryName = "blobs";
    // One export writes original bytes outside the vault plus one versioned
    // metadata document. The document is a separate portable file; no exported
    // original is ever edited to carry it, and nothing is staged inside the
    // vault. The destination is validated as a new path on a real directory
    // tree before the first byte is written.
    internal const string ExportFormat = "metabrain-workspace-export/1";
    internal const int ExportMetadataSchemaVersion = 1;
    internal const string ExportSidecarName = "metabrain-workspace-export.json";
    private static readonly char[] InvalidExportNameCharacters = Path.GetInvalidFileNameChars();
    private static readonly string[] ReservedExportDeviceNames =
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _root;
    private readonly string _stateDirectory;
    private readonly string _blobDirectory;
    private readonly string _headPath;

    public EncryptedWorkspaceStore(string vaultRoot)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The encrypted workspace store is Windows-only.");
        }

        var root = Path.GetFullPath(vaultRoot);
        _root = Path.Combine(root, WorkspaceDirectoryName);
        _stateDirectory = Path.Combine(_root, StateDirectoryName);
        _blobDirectory = Path.Combine(_root, BlobDirectoryName);
        _headPath = Path.Combine(_root, HeadFileName);
    }

    /// <summary>
    /// Opens the committed workspace for one unlocked epoch and reclaims files
    /// that an interrupted commit staged but never published. A workspace that
    /// cannot be authenticated is reported, never repaired, overwritten or
    /// pruned: its files may be the only copy of the owner's data.
    /// </summary>
    public WorkspaceOpenResult Open(string vaultId, ReadOnlySpan<byte> dataKey)
    {
        if (string.IsNullOrEmpty(vaultId))
        {
            throw new ArgumentException("The vault identity is invalid.", nameof(vaultId));
        }

        try
        {
            if (!Directory.Exists(_root))
            {
                return new WorkspaceOpenResult(null, null);
            }

            EnsureRealDirectory(_root);
            if (!File.Exists(_headPath))
            {
                // No pointer means nothing was committed. Temporary files are
                // the residue of an interrupted first staging pass and are safe
                // to reap; any non-temporary file means the pointer is missing
                // while committed ciphertext may still exist, so the workspace
                // fails closed instead of being treated as empty.
                ReapDirectory(_root, _ => true);
                ReapDirectory(_stateDirectory, _ => true);
                ReapDirectory(_blobDirectory, _ => true);
                return HasCommittedLeftovers()
                    ? new WorkspaceOpenResult(null, WorkspaceErrorCodes.Unavailable)
                    : new WorkspaceOpenResult(null, null);
            }

            var generation = ReadHead().Generation;
            var state = NormalizeState(ReadState(vaultId, dataKey, generation));
            Reconcile(state, generation);
            return new WorkspaceOpenResult(state, null);
        }
        catch (WorkspaceRefusalException refusal)
        {
            return new WorkspaceOpenResult(null, refusal.Code);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
            JsonException or CryptographicException or ArgumentException or FormatException or NotSupportedException)
        {
            return new WorkspaceOpenResult(null, WorkspaceErrorCodes.Unavailable);
        }
    }

    /// <summary>
    /// Builds the empty workspace tree (revision 1, one root folder) without
    /// committing it, so a caller can validate its first act before the
    /// workspace exists and a refused act leaves nothing behind.
    /// </summary>
    public static WorkspaceStateRecord CreateEmptyState()
    {
        var rootId = NewIdentifier();
        return new WorkspaceStateRecord(
            CurrentFormatVersion,
            CurrentMetadataSchemaVersion,
            NewIdentifier(),
            1,
            rootId,
            new[]
            {
                new WorkspaceNodeRecord(
                    rootId,
                    FolderKind,
                    null,
                    string.Empty,
                    1,
                    0,
                    Array.Empty<WorkspaceRevisionRecord>(),
                    WorkspaceNodeStates.Live,
                    null)
            },
            Array.Empty<WorkspaceAnchorRecord>());
    }

    /// <summary>
    /// Commits the empty tree (revision 1) so the workspace has one committed
    /// pointer before its first mutation. A later interruption can then only
    /// leave unreachable generations, never an ambiguous missing pointer.
    /// </summary>
    public WorkspaceStateRecord Initialize(string vaultId, ReadOnlySpan<byte> dataKey)
    {
        var state = CreateEmptyState();
        Commit(vaultId, dataKey, state, Array.Empty<StagedWorkspaceBlob>());
        return state;
    }

    /// <summary>
    /// Validates one create against an existing tree without writing anything.
    /// The create methods run the same validation again immediately before
    /// their commit, so the gate holds at the commit point too.
    /// </summary>
    public static void ValidateNewNode(
        WorkspaceStateRecord state,
        string parentId,
        string name,
        int contentLength,
        long expectedTreeRevision)
    {
        ValidateExpectedTreeRevision(state, expectedTreeRevision);
        EnsureLiveFolder(state, parentId);
        EnsureValidName(name);
        EnsureUniqueSiblingName(state, parentId, name, exceptNodeId: null);
        EnsureNodeCapacity(state, additional: 1);
        EnsureContentSize(contentLength);
    }

    public WorkspaceStateRecord CreateFolder(
        string vaultId,
        ReadOnlySpan<byte> dataKey,
        WorkspaceStateRecord state,
        string parentId,
        string name,
        long expectedTreeRevision,
        out WorkspaceNodeRecord created)
    {
        ValidateNewNode(state, parentId, name, contentLength: 0, expectedTreeRevision);

        created = NewNode(NewIdentifier(), FolderKind, parentId, name);
        var next = AppendNode(state, created);
        Commit(vaultId, dataKey, next, Array.Empty<StagedWorkspaceBlob>());
        return next;
    }

    public WorkspaceStateRecord CreateFile(
        string vaultId,
        ReadOnlySpan<byte> dataKey,
        WorkspaceStateRecord state,
        string parentId,
        string name,
        byte[] content,
        long expectedTreeRevision,
        out WorkspaceNodeRecord created)
    {
        ArgumentNullException.ThrowIfNull(content);
        ValidateNewNode(state, parentId, name, content.Length, expectedTreeRevision);

        var revision = new WorkspaceRevisionRecord(1, content.Length, Digest(content), NewStorageName());
        created = NewNode(NewIdentifier(), FileKind, parentId, name) with
        {
            ContentRevision = 1,
            Revisions = new[] { revision }
        };
        var next = AppendNode(state, created);
        Commit(vaultId, dataKey, next, new[] { new StagedWorkspaceBlob(created.Id, revision, content, null, null) });
        return next;
    }

    /// <summary>
    /// The explicit replacement of one selected file's current content: the
    /// exact new bytes are appended as the next retained content revision of
    /// the same identity, gated by the file's own content revision. Identity,
    /// name, parent and relations are retained; the previous revision's
    /// ciphertext stays readable and is never deleted here. Every live citation
    /// anchor of the file is marked stale at its original revision instead of
    /// being silently rebound to the new bytes.
    /// </summary>
    public WorkspaceStateRecord ReviseFile(
        string vaultId,
        ReadOnlySpan<byte> dataKey,
        WorkspaceStateRecord state,
        string fileId,
        byte[] content,
        long expectedContentRevision,
        out WorkspaceNodeRecord revised)
    {
        ArgumentNullException.ThrowIfNull(content);
        var node = FindNode(state, fileId);
        if (node is null || !string.Equals(node.Kind, FileKind, StringComparison.Ordinal))
        {
            throw new WorkspaceRefusalException(WorkspaceErrorCodes.NotFound);
        }

        EnsureLive(node);
        if (expectedContentRevision < 1 || node.ContentRevision != expectedContentRevision)
        {
            throw new WorkspaceRefusalException(WorkspaceErrorCodes.Conflict);
        }

        EnsureContentSize(content.Length);
        if (node.Revisions.Length >= MaximumFileRevisions)
        {
            throw new WorkspaceRefusalException(WorkspaceErrorCodes.RevisionLimit);
        }

        var revision = checked(node.ContentRevision + 1);
        var created = new WorkspaceRevisionRecord(revision, content.Length, Digest(content), NewStorageName());
        var updated = node with
        {
            ContentRevision = revision,
            Revisions = node.Revisions.Append(created).ToArray()
        };
        var next = ReplaceNode(state, updated, MarkAnchorsStale(AnchorsOf(state), node.Id));
        Commit(vaultId, dataKey, next, new[] { new StagedWorkspaceBlob(node.Id, created, content, null, null) });
        revised = updated;
        return next;
    }

    /// <summary>
    /// S12-T5 captures every retained revision of every node, live or
    /// tombstoned, as a plaintext copy for the encrypted backup. The vault is
    /// sealed as a whole; these buffers exist only until the backup container
    /// is written and the caller zeroizes them. A revision that cannot be
    /// authenticated refuses the whole capture instead of producing a backup
    /// that silently omits the owner's bytes.
    /// </summary>
    public WorkspaceBackupSnapshot CaptureBackup(
        string vaultId,
        ReadOnlySpan<byte> dataKey,
        WorkspaceStateRecord state)
    {
        ValidateState(state);
        var contents = new List<WorkspaceBackupContent>();
        try
        {
            foreach (var node in state.Nodes)
            {
                foreach (var revision in node.Revisions)
                {
                    var content = ReadRevisionContent(vaultId, dataKey, node, revision);
                    contents.Add(new WorkspaceBackupContent(
                        node.Id, revision.Revision, revision.Length, revision.Sha256, content.Content));
                }
            }

            return new WorkspaceBackupSnapshot(state, contents.ToArray());
        }
        catch
        {
            foreach (var item in contents)
            {
                Clear(item.Content);
            }

            throw;
        }
    }

    /// <summary>
    /// S12-T5 materializes one captured workspace into a clean vault: the same
    /// workspace identity, tree revision, node ids, names, parents, retained
    /// revision numbers, anchors and tombstones, with the exact authenticated
    /// original bytes. Identities the backup's purge ledger reserved are
    /// filtered by the caller, so a restore never revives them. Only the opaque storage name of
    /// each ciphertext is fresh; identity and metadata are never renumbered.
    /// The capture must name exactly every retained revision once, so a
    /// truncated or padded backup refuses before anything is committed.
    /// </summary>
    public WorkspaceStateRecord RestoreBackup(
        string vaultId,
        ReadOnlySpan<byte> dataKey,
        WorkspaceStateRecord state,
        IReadOnlyList<WorkspaceBackupContent> contents)
    {
        ArgumentNullException.ThrowIfNull(contents);
        var normalized = NormalizeState(state);
        ValidateState(normalized);

        var supplied = new Dictionary<(string FileId, long Revision), WorkspaceBackupContent>();
        foreach (var item in contents)
        {
            if (item is null || !IsIdentifier(item.FileId) || item.Revision < 1 ||
                item.Length < 0 || item.Length > MaximumContentBytes ||
                !IsSha256Hex(item.Sha256) || item.Content is null ||
                item.Content.LongLength != item.Length ||
                !string.Equals(Digest(item.Content), item.Sha256, StringComparison.Ordinal) ||
                !supplied.TryAdd((item.FileId, item.Revision), item))
            {
                throw new InvalidDataException("The backup workspace content is invalid.");
            }
        }

        var nodes = new WorkspaceNodeRecord[normalized.Nodes.Length];
        var staged = new List<StagedWorkspaceBlob>();
        var matched = 0;
        for (var index = 0; index < normalized.Nodes.Length; index++)
        {
            var node = normalized.Nodes[index];
            if (node.Revisions.Length == 0)
            {
                nodes[index] = node;
                continue;
            }

            var revisions = new WorkspaceRevisionRecord[node.Revisions.Length];
            for (var revisionIndex = 0; revisionIndex < node.Revisions.Length; revisionIndex++)
            {
                var revision = node.Revisions[revisionIndex];
                if (!supplied.TryGetValue((node.Id, revision.Revision), out var captured) ||
                    captured.Length != revision.Length ||
                    !string.Equals(captured.Sha256, revision.Sha256, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("The backup workspace content does not match its metadata.");
                }

                var restored = revision with { StorageName = NewStorageName() };
                revisions[revisionIndex] = restored;
                staged.Add(new StagedWorkspaceBlob(node.Id, restored, captured.Content, null, null));
                matched++;
            }

            nodes[index] = node with { Revisions = revisions };
        }

        if (matched != supplied.Count)
        {
            throw new InvalidDataException("The backup workspace content names a revision no node retains.");
        }

        var restoredState = normalized with { Nodes = nodes };
        Commit(vaultId, dataKey, restoredState, staged);
        return restoredState;
    }

    /// <summary>
    /// Reads the exact original bytes of one retained revision. A revision of 0
    /// selects the latest; a positive revision selects that retained revision
    /// even after newer ones exist.
    /// </summary>
    public WorkspaceFileContent ReadContent(
        string vaultId,
        ReadOnlySpan<byte> dataKey,
        WorkspaceStateRecord state,
        string fileId,
        long revision)
    {
        var node = FindNode(state, fileId);
        if (node is null || !string.Equals(node.Kind, FileKind, StringComparison.Ordinal))
        {
            throw new WorkspaceRefusalException(WorkspaceErrorCodes.NotFound);
        }

        EnsureLive(node);
        var target = revision <= 0 ? node.ContentRevision : revision;
        var entry = node.Revisions.FirstOrDefault(candidate => candidate.Revision == target)
            ?? throw new WorkspaceRefusalException(WorkspaceErrorCodes.NotFound);
        return ReadRevisionContent(vaultId, dataKey, node, entry);
    }

    private WorkspaceFileContent ReadRevisionContent(
        string vaultId,
        ReadOnlySpan<byte> dataKey,
        WorkspaceNodeRecord node,
        WorkspaceRevisionRecord entry)
    {
        try
        {
            var raw = PrivateFile.ReadBytes(Path.Combine(_blobDirectory, entry.StorageName), MaximumBlobEnvelopeBytes);
            try
            {
                var content = Decrypt(DeserializeEnvelope(raw), dataKey, BlobAssociatedData(vaultId, node.Id, entry), MaximumContentBytes);
                if (content.Length != entry.Length || !string.Equals(Digest(content), entry.Sha256, StringComparison.Ordinal))
                {
                    Clear(content);
                    throw new InvalidDataException("The workspace content does not match its authenticated metadata.");
                }

                return new WorkspaceFileContent(entry.Revision, entry.Length, entry.Sha256, content);
            }
            finally
            {
                Clear(raw);
            }
        }
        catch (WorkspaceRefusalException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
            JsonException or CryptographicException or ArgumentException or FormatException)
        {
            throw new WorkspaceRefusalException(WorkspaceErrorCodes.Unavailable);
        }
    }

    /// <summary>
    /// Reports the exact plaintext scope one export would write. It resolves the
    /// retained revision of every file in the selection, checks the destination
    /// and every exact logical name against the destination filesystem, and
    /// returns the token that binds the act to this exact state. It reads only:
    /// no directory, file or temporary plaintext is created.
    /// </summary>
    public static WorkspaceExportPreviewInfo PreviewExport(
        WorkspaceStateRecord state,
        string nodeId,
        long contentRevision,
        string destination)
    {
        var node = FindLiveNode(state, nodeId);
        EnsureNotRoot(state, node);
        var canonicalDestination = ValidateExportDestination(destination);
        var scope = BuildExportScope(state, node, contentRevision);
        return new WorkspaceExportPreviewInfo(
            node.Id,
            KindOf(node),
            node.Name,
            state.Revision,
            node.Revision,
            scope.SelectedContentRevision,
            canonicalDestination,
            Path.Combine(canonicalDestination, ExportSidecarName),
            ComputeExportToken(state, node, scope, canonicalDestination),
            scope.Files,
            scope.Folders,
            scope.Anchors,
            scope.ExcludedTombstones,
            scope.TotalBytes);
    }

    /// <summary>
    /// Writes one previewed scope as original bytes outside the vault and one
    /// versioned metadata document beside them. The act refuses unless the
    /// recomputed preview matches the owner's token exactly, then creates one
    /// new destination directory, recreates the selected hierarchy with the
    /// exact logical names, writes every file with create-only semantics from
    /// its pinned retained revision, and writes the metadata document last. An
    /// interrupted or failed act removes exactly what it created; the vault is
    /// never modified by an export, and no temporary plaintext is staged.
    /// </summary>
    public WorkspaceExportInfo ExportNode(
        string vaultId,
        ReadOnlySpan<byte> dataKey,
        WorkspaceStateRecord state,
        string nodeId,
        long contentRevision,
        string destination,
        string previewToken,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(previewToken))
        {
            throw new WorkspaceRefusalException(WorkspaceErrorCodes.ExportConfirmation);
        }

        var preview = PreviewExport(state, nodeId, contentRevision, destination);
        if (!string.Equals(preview.Token, previewToken, StringComparison.Ordinal))
        {
            throw new WorkspaceRefusalException(WorkspaceErrorCodes.Conflict);
        }

        var node = FindLiveNode(state, nodeId);
        var createdDirectories = new List<string>(preview.Folders.Length + 1);
        var createdFiles = new List<string>(preview.Files.Length + 1);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureExportDestinationFree(preview.Destination);
            Directory.CreateDirectory(preview.Destination);
            createdDirectories.Add(preview.Destination);
            EnsureCreatedExportDirectory(preview.Destination);

            foreach (var folder in preview.Folders)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = ExportPath(preview.Destination, folder.Path);
                Directory.CreateDirectory(path);
                createdDirectories.Add(path);
                EnsureCreatedExportDirectory(path);
            }

            foreach (var file in preview.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = ExportPath(preview.Destination, file.Path);
                var content = ReadContent(vaultId, dataKey, state, file.FileId, file.ContentRevision);
                try
                {
                    if (content.Length != file.Length ||
                        !string.Equals(content.Sha256, file.Sha256, StringComparison.Ordinal))
                    {
                        throw new WorkspaceRefusalException(WorkspaceErrorCodes.Conflict);
                    }

                    using var stream = new FileStream(
                        path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.WriteThrough);
                    stream.Write(content.Content, 0, content.Content.Length);
                    stream.Flush(flushToDisk: true);
                    createdFiles.Add(path);
                }
                finally
                {
                    Clear(content.Content);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            var sidecarSha256 = WriteExportSidecar(state, node, preview);
            createdFiles.Add(preview.SidecarPath);
            return new WorkspaceExportInfo(
                node.Id,
                preview.Destination,
                preview.SidecarPath,
                state.Revision,
                preview.Folders.Length,
                preview.Files.Length,
                preview.TotalBytes,
                sidecarSha256);
        }
        catch (OperationCanceledException)
        {
            DeleteExportArtifacts(createdFiles, createdDirectories);
            throw;
        }
        catch (WorkspaceRefusalException)
        {
            DeleteExportArtifacts(createdFiles, createdDirectories);
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            System.Security.SecurityException or NotSupportedException)
        {
            var removed = DeleteExportArtifacts(createdFiles, createdDirectories);
            throw new WorkspaceRefusalException(
                removed ? WorkspaceErrorCodes.ExportFailed : WorkspaceErrorCodes.ExportIncomplete);
        }
    }

    /// <summary>One node of an export scope with its destination-relative path.</summary>
    private sealed record ExportScopeEntry(WorkspaceNodeRecord Node, string Path, long ExportedRevision);

    /// <summary>The whole resolved scope: the entries, the pinned revisions and what is withheld.</summary>
    private sealed record ExportScope(
        ExportScopeEntry[] Entries,
        WorkspaceExportFileInfo[] Files,
        WorkspaceExportFolderInfo[] Folders,
        WorkspaceAnchorInfo[] Anchors,
        int ExcludedTombstones,
        long TotalBytes,
        long SelectedContentRevision);

    /// <summary>
    /// Resolves the selected live node and its live descendants into the exact
    /// destination paths, pinned file revisions, in-scope anchors and the count
    /// of excluded tombstones. Every name that becomes a destination component
    /// must be representable there unchanged; a name that is not refuses
    /// instead of being rewritten, and two names that would collide under the
    /// destination filesystem's case-insensitive rule refuse as well.
    /// </summary>
    private static ExportScope BuildExportScope(
        WorkspaceStateRecord state,
        WorkspaceNodeRecord node,
        long requestedRevision)
    {
        if (requestedRevision < 0 || (requestedRevision > 0 && IsFolder(node)))
        {
            throw new WorkspaceRefusalException(WorkspaceErrorCodes.ExportRevision);
        }

        var subtree = CollectLiveSubtree(state, node);
        var entries = new List<ExportScopeEntry>(subtree.Count);
        var files = new List<WorkspaceExportFileInfo>();
        var folders = new List<WorkspaceExportFolderInfo>();
        var fileIds = new HashSet<string>(StringComparer.Ordinal);
        var relativePaths = new Dictionary<string, string>(StringComparer.Ordinal) { [node.Id] = string.Empty };
        var occupied = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var totalBytes = 0L;
        var selectedContentRevision = 0L;
        for (var index = 0; index < subtree.Count; index++)
        {
            var current = subtree[index];
            string path;
            if (index == 0)
            {
                path = string.Empty;
            }
            else
            {
                EnsureExportedName(current.Name);
                var parentPath = relativePaths[current.ParentId!];
                path = parentPath.Length == 0 ? current.Name : parentPath + "/" + current.Name;
                relativePaths[current.Id] = path;
            }

            var exportedRevision = 0L;
            if (IsFolder(current))
            {
                if (index > 0)
                {
                    ReserveExportPath(occupied, path);
                    folders.Add(new WorkspaceExportFolderInfo(current.Id, path));
                }
            }
            else
            {
                if (index == 0)
                {
                    EnsureExportedName(current.Name);
                    path = current.Name;
                    relativePaths[current.Id] = path;
                }

                ReserveExportPath(occupied, path);
                exportedRevision = index == 0
                    ? ResolveSelectedRevision(current, requestedRevision)
                    : current.ContentRevision;
                var revision = ResolveRevisionRecord(current, exportedRevision);
                files.Add(new WorkspaceExportFileInfo(
                    current.Id, path, revision.Revision, revision.Length, revision.Sha256));
                fileIds.Add(current.Id);
                totalBytes = checked(totalBytes + revision.Length);
                if (index == 0)
                {
                    selectedContentRevision = revision.Revision;
                }
            }

            entries.Add(new ExportScopeEntry(current, path, exportedRevision));
        }

        var anchors = new List<WorkspaceAnchorInfo>();
        foreach (var anchor in AnchorsOf(state))
        {
            if (!fileIds.Contains(anchor.FileId))
            {
                continue;
            }

            anchors.Add(new WorkspaceAnchorInfo(
                anchor.Id,
                anchor.FileId,
                anchor.ContentRevision,
                anchor.Offset,
                anchor.Length,
                anchor.Sha256,
                anchor.State ?? WorkspaceAnchorStates.Live));
        }

        anchors.Sort((left, right) => string.CompareOrdinal(left.AnchorId, right.AnchorId));
        return new ExportScope(
            entries.ToArray(),
            files.ToArray(),
            folders.ToArray(),
            anchors.ToArray(),
            IsFolder(node) ? CountExcludedTombstones(state, node.Id) : 0,
            totalBytes,
            selectedContentRevision);
    }

    /// <summary>
    /// The exact token that binds an export to the owner's preview: the
    /// workspace identity and revision, the selected node and its revision, the
    /// canonical destination and every in-scope node's identity, revision and
    /// exact path, in a stable order. Any change between preview and export
    /// makes the token differ, so the act refuses instead of exporting
    /// something the owner did not see.
    /// </summary>
    private static string ComputeExportToken(
        WorkspaceStateRecord state,
        WorkspaceNodeRecord node,
        ExportScope scope,
        string destination)
    {
        var ordered = new List<ExportScopeEntry>(scope.Entries);
        ordered.Sort((left, right) => string.CompareOrdinal(left.Node.Id, right.Node.Id));
        var text = new StringBuilder(256 + (ordered.Count * 80));
        text.Append("MetaBrain|workspace-export-preview|1|")
            .Append(state.WorkspaceId).Append('|')
            .Append(state.FormatVersion.ToString(CultureInfo.InvariantCulture)).Append('|')
            .Append(state.MetadataSchemaVersion.ToString(CultureInfo.InvariantCulture)).Append('|')
            .Append(state.Revision.ToString(CultureInfo.InvariantCulture)).Append('|')
            .Append(node.Id).Append('|')
            .Append(node.Revision.ToString(CultureInfo.InvariantCulture)).Append('|')
            .Append(node.Kind).Append('|')
            .Append(destination);
        foreach (var entry in ordered)
        {
            text.Append('|')
                .Append(entry.Node.Id).Append(':')
                .Append(entry.Node.Revision.ToString(CultureInfo.InvariantCulture)).Append(':')
                .Append(entry.ExportedRevision.ToString(CultureInfo.InvariantCulture)).Append(':')
                .Append(entry.Path);
        }

        return Digest(Encoding.UTF8.GetBytes(text.ToString()));
    }

    /// <summary>
    /// The retained revision an export of the selected file pins: the latest
    /// committed revision when the owner names none, otherwise exactly the
    /// revision the owner named. A later revision is never substituted.
    /// </summary>
    private static long ResolveSelectedRevision(WorkspaceNodeRecord node, long requestedRevision)
    {
        if (requestedRevision == 0)
        {
            return node.ContentRevision;
        }

        return ResolveRevisionRecord(node, requestedRevision).Revision;
    }

    private static WorkspaceRevisionRecord ResolveRevisionRecord(WorkspaceNodeRecord node, long revision)
    {
        foreach (var candidate in node.Revisions)
        {
            if (candidate.Revision == revision)
            {
                return candidate;
            }
        }

        throw new WorkspaceRefusalException(WorkspaceErrorCodes.NotFound);
    }

    /// <summary>
    /// Counts the encrypted tombstones whose retained parent chain passes
    /// through the selected folder. They are not part of the live tree, so they
    /// are not exported; reporting the number keeps the exclusion explicit
    /// instead of silent. The walk is memoised, so it is linear in the state.
    /// </summary>
    private static int CountExcludedTombstones(WorkspaceStateRecord state, string folderId)
    {
        var parents = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var candidate in state.Nodes)
        {
            parents[candidate.Id] = candidate.ParentId;
        }

        var underSelected = new Dictionary<string, bool>(StringComparer.Ordinal) { [folderId] = true };
        var count = 0;
        foreach (var candidate in state.Nodes)
        {
            if (!string.Equals(candidate.State, WorkspaceNodeStates.Deleted, StringComparison.Ordinal))
            {
                continue;
            }

            var chain = new List<string>();
            var cursor = candidate.ParentId;
            bool? matches = null;
            while (cursor is not null)
            {
                if (underSelected.TryGetValue(cursor, out var known))
                {
                    matches = known;
                    break;
                }

                chain.Add(cursor);
                cursor = parents.TryGetValue(cursor, out var parent) ? parent : null;
            }

            var result = matches ?? false;
            foreach (var id in chain)
            {
                underSelected[id] = result;
            }

            if (result)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Validates the owner's destination as a new directory on a real tree: an
    /// absolute path without a traversal segment or device namespace, a safe
    /// leaf name, an existing parent whose ancestors are real directories
    /// rather than reparse points, and no entry that already occupies the exact
    /// name under the destination filesystem's case-insensitive rule. A hostile
    /// or racing destination refuses instead of being trusted.
    /// </summary>
    private static string ValidateExportDestination(string? destination)
    {
        if (string.IsNullOrWhiteSpace(destination) ||
            destination.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            destination.StartsWith(@"\\.\", StringComparison.Ordinal) ||
            destination.StartsWith(@"\??\", StringComparison.Ordinal) ||
            !Path.IsPathFullyQualified(destination))
        {
            throw new WorkspaceRefusalException(WorkspaceErrorCodes.ExportDestination);
        }

        foreach (var segment in destination.Split(new[] { '\\', '/' }, StringSplitOptions.None))
        {
            if (segment is "." or "..")
            {
                throw new WorkspaceRefusalException(WorkspaceErrorCodes.ExportDestination);
            }
        }

        string canonical;
        try
        {
            canonical = Path.GetFullPath(destination);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new WorkspaceRefusalException(WorkspaceErrorCodes.ExportDestination);
        }

        if (canonical.Length == 0 || canonical.EndsWith(Path.DirectorySeparatorChar) ||
            canonical.EndsWith(Path.AltDirectorySeparatorChar))
        {
            throw new WorkspaceRefusalException(WorkspaceErrorCodes.ExportDestination);
        }

        var leaf = Path.GetFileName(canonical);
        var parent = Path.GetDirectoryName(canonical);
        if (string.IsNullOrEmpty(leaf) || string.IsNullOrEmpty(parent) ||
            !IsRepresentableExportName(leaf) || !Directory.Exists(parent))
        {
            throw new WorkspaceRefusalException(WorkspaceErrorCodes.ExportDestination);
        }

        try
        {
            for (var info = new DirectoryInfo(parent); info is not null; info = info.Parent)
            {
                var attributes = info.Attributes;
                if ((attributes & FileAttributes.Directory) == 0 ||
                    (attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new WorkspaceRefusalException(WorkspaceErrorCodes.ExportDestination);
                }
            }

            if (File.Exists(canonical) || Directory.Exists(canonical))
            {
                throw new WorkspaceRefusalException(WorkspaceErrorCodes.ExportDestination);
            }

            foreach (var entry in Directory.EnumerateFileSystemEntries(parent))
            {
                if (string.Equals(Path.GetFileName(entry), leaf, StringComparison.OrdinalIgnoreCase))
                {
                    throw new WorkspaceRefusalException(WorkspaceErrorCodes.ExportDestination);
                }
            }
        }
        catch (WorkspaceRefusalException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            System.Security.SecurityException or ArgumentException or NotSupportedException)
        {
            throw new WorkspaceRefusalException(WorkspaceErrorCodes.ExportDestination);
        }

        return canonical;
    }

    /// <summary>
    /// True when one exact logical name can be a destination path component
    /// unchanged: no separator or other invalid character, not a relative
    /// directory marker, not a reserved device name, and not ending in a dot or
    /// space that the destination filesystem would silently drop.
    /// </summary>
    private static bool IsRepresentableExportName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > MaximumNameLength ||
            name is "." or ".." || name[^1] is '.' or ' ')
        {
            return false;
        }

        if (name.IndexOfAny(InvalidExportNameCharacters) >= 0)
        {
            return false;
        }

        var stem = name.Split('.')[0];
        foreach (var reserved in ReservedExportDeviceNames)
        {
            if (string.Equals(stem, reserved, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static void EnsureExportedName(string name)
    {
        if (!IsRepresentableExportName(name))
        {
            throw new WorkspaceRefusalException(WorkspaceErrorCodes.ExportName);
        }
    }

    /// <summary>
    /// Claims one destination-relative path. A second selected node with the
    /// same path under the destination's case-insensitive rule, or a top-level
    /// name that would collide with the metadata document, refuses the export.
    /// </summary>
    private static void ReserveExportPath(Dictionary<string, string> occupied, string path)
    {
        if (path.Length == 0)
        {
            return;
        }

        if (path.IndexOf('/') < 0 &&
            string.Equals(path, ExportSidecarName, StringComparison.OrdinalIgnoreCase))
        {
            throw new WorkspaceRefusalException(WorkspaceErrorCodes.ExportCollision);
        }

        if (!occupied.TryAdd(path, path))
        {
            throw new WorkspaceRefusalException(WorkspaceErrorCodes.ExportCollision);
        }
    }

    private static string ExportPath(string destination, string relativePath)
    {
        var path = destination;
        foreach (var segment in relativePath.Split('/'))
        {
            path = Path.Combine(path, segment);
        }

        return path;
    }

    private static void EnsureExportDestinationFree(string destination)
    {
        if (File.Exists(destination) || Directory.Exists(destination))
        {
            throw new WorkspaceRefusalException(WorkspaceErrorCodes.ExportDestination);
        }
    }

    /// <summary>
    /// Confirms a directory this act just created is a real, empty directory:
    /// a reparse point planted at the destination would otherwise redirect
    /// every following write outside the previewed scope, and pre-existing
    /// content would mean the act did not create what the preview showed.
    /// </summary>
    private static void EnsureCreatedExportDirectory(string path)
    {
        var info = new DirectoryInfo(path);
        var attributes = info.Attributes;
        if ((attributes & FileAttributes.Directory) == 0 ||
            (attributes & FileAttributes.ReparsePoint) != 0 ||
            info.LinkTarget is not null)
        {
            throw new WorkspaceRefusalException(WorkspaceErrorCodes.ExportDestination);
        }

        using var entries = Directory.EnumerateFileSystemEntries(path).GetEnumerator();
        if (entries.MoveNext())
        {
            throw new WorkspaceRefusalException(WorkspaceErrorCodes.ExportDestination);
        }
    }

    /// <summary>
    /// Removes exactly what a failed export created, deepest first, and only
    /// empty directories. Pre-existing destination content is never deleted,
    /// and an unexpected entry makes the removal fail truthfully instead of
    /// deleting something the act did not write.
    /// </summary>
    private static bool DeleteExportArtifacts(List<string> files, List<string> directories)
    {
        var removed = true;
        foreach (var file in files)
        {
            try
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                System.Security.SecurityException)
            {
                removed = false;
            }
        }

        for (var index = directories.Count - 1; index >= 0; index--)
        {
            try
            {
                if (Directory.Exists(directories[index]))
                {
                    Directory.Delete(directories[index], recursive: false);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                System.Security.SecurityException)
            {
                removed = false;
            }
        }

        return removed;
    }

    /// <summary>
    /// Writes the versioned metadata document of one export: the workspace and
    /// export formats, the selected node, every in-scope node with its identity,
    /// exact name, parent, hierarchy path and retained revisions, and the
    /// anchors of those files. Nodes and anchors outside the scope are absent,
    /// never summarised. Relations are reported as unsupported rather than
    /// invented, because workspace relations are not implemented. The document
    /// is streamed with its digest computed on the same write, so no second copy
    /// of it is held.
    /// </summary>
    private static string WriteExportSidecar(
        WorkspaceStateRecord state,
        WorkspaceNodeRecord selected,
        WorkspaceExportPreviewInfo preview)
    {
        using var stream = new FileStream(
            preview.SidecarPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024,
            FileOptions.WriteThrough);
        using var hash = SHA256.Create();
        using (var crypto = new CryptoStream(stream, hash, CryptoStreamMode.Write, leaveOpen: true))
        {
            using var writer = new Utf8JsonWriter(crypto, new JsonWriterOptions { Indented = true });
            writer.WriteStartObject();
            writer.WriteString("format", ExportFormat);
            writer.WriteNumber("exportFormatVersion", 1);
            writer.WriteNumber("metadataSchemaVersion", ExportMetadataSchemaVersion);
            writer.WriteNumber("workspaceFormatVersion", state.FormatVersion);
            writer.WriteNumber("workspaceMetadataSchemaVersion", state.MetadataSchemaVersion);
            writer.WriteString("workspaceId", state.WorkspaceId);
            writer.WriteNumber("treeRevision", state.Revision);
            writer.WriteString("createdAtUtc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            writer.WriteStartObject("selected");
            writer.WriteString("nodeId", selected.Id);
            writer.WriteString("kind", IsFolder(selected) ? FolderKind : FileKind);
            writer.WriteString("name", selected.Name);
            writer.WriteNumber("nodeRevision", selected.Revision);
            writer.WriteNumber("contentRevision", preview.ContentRevision);
            writer.WriteEndObject();
            writer.WriteString(
                "disclosure",
                "Every exported file is plaintext outside the vault. A later lock, delete or purge does not revoke this copy.");

            writer.WriteStartArray("nodes");
            WriteExportNode(
                writer,
                selected,
                IsFolder(selected) ? string.Empty : selected.Name,
                preview.ContentRevision,
                isRoot: true);
            foreach (var folder in preview.Folders)
            {
                var node = FindNode(state, folder.FolderId)
                    ?? throw new WorkspaceRefusalException(WorkspaceErrorCodes.NotFound);
                WriteExportNode(writer, node, folder.Path, 0, isRoot: false);
            }

            foreach (var file in preview.Files)
            {
                if (string.Equals(file.FileId, selected.Id, StringComparison.Ordinal))
                {
                    continue;
                }

                var node = FindNode(state, file.FileId)
                    ?? throw new WorkspaceRefusalException(WorkspaceErrorCodes.NotFound);
                WriteExportNode(writer, node, file.Path, file.ContentRevision, isRoot: false);
            }

            writer.WriteEndArray();

            writer.WriteStartArray("anchors");
            foreach (var anchor in preview.Anchors)
            {
                writer.WriteStartObject();
                writer.WriteString("anchorId", anchor.AnchorId);
                writer.WriteString("fileId", anchor.FileId);
                writer.WriteNumber("contentRevision", anchor.ContentRevision);
                writer.WriteNumber("offset", anchor.Offset);
                writer.WriteNumber("length", anchor.Length);
                writer.WriteString("sha256", anchor.Sha256);
                writer.WriteString("state", anchor.State);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();

            writer.WriteStartArray("relations");
            writer.WriteEndArray();
            writer.WriteString(
                "relationSupport",
                "Workspace relations are not implemented in this export format version, so the relations array is empty rather than inferred.");
            writer.WriteStartArray("notes");
            writer.WriteStringValue("Only the selected node and the live nodes beneath it are included; every other node, the selected node's parent identity, titles, grants and private index stay in the vault.");
            writer.WriteStringValue("Each file is written from the retained revision this document names, which is the revision the owner previewed; a later revision is not substituted.");
            writer.WriteStringValue("Tombstones beneath the selected folder are not part of the live tree and are not exported; their count is reported in the preview.");
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.Flush();
        }

        stream.Flush(flushToDisk: true);
        return Convert.ToHexStringLower(hash.Hash ?? Array.Empty<byte>());
    }

    private static void WriteExportNode(
        Utf8JsonWriter writer,
        WorkspaceNodeRecord node,
        string path,
        long exportedRevision,
        bool isRoot)
    {
        writer.WriteStartObject();
        writer.WriteString("nodeId", node.Id);
        writer.WriteString("kind", KindOf(node) == WorkspaceNodeKind.Folder ? FolderKind : FileKind);
        if (isRoot)
        {
            writer.WriteNull("parentId");
        }
        else
        {
            writer.WriteString("parentId", node.ParentId);
        }

        writer.WriteString("name", node.Name);
        writer.WriteNumber("revision", node.Revision);
        writer.WriteString("path", path);
        if (!IsFolder(node))
        {
            var exported = ResolveRevisionRecord(node, exportedRevision);
            writer.WriteNumber("contentRevision", exported.Revision);
            writer.WriteNumber("length", exported.Length);
            writer.WriteString("sha256", exported.Sha256);
            writer.WriteStartArray("retainedRevisions");
            foreach (var revision in node.Revisions)
            {
                writer.WriteStartObject();
                writer.WriteNumber("revision", revision.Revision);
                writer.WriteNumber("length", revision.Length);
                writer.WriteString("sha256", revision.Sha256);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        writer.WriteEndObject();
    }

    /// <summary>
    /// Renames one live node inside its current parent. Only the node's name
    /// and its own metadata revision change: identity, parent, descendants,
    /// content revisions and citation anchors stay exactly as they were, and a
    /// name already held by a live sibling refuses the act before anything is
    /// written.
    /// </summary>
    public WorkspaceStateRecord RenameNode(
        string vaultId,
        ReadOnlySpan<byte> dataKey,
        WorkspaceStateRecord state,
        string nodeId,
        string name,
        long expectedTreeRevision,
        long expectedNodeRevision,
        out WorkspaceNodeRecord renamed)
    {
        ValidateExpectedTreeRevision(state, expectedTreeRevision);
        var node = FindLiveNode(state, nodeId);
        EnsureNotRoot(state, node);
        if (node.Revision != expectedNodeRevision)
        {
            throw new WorkspaceRefusalException(WorkspaceErrorCodes.Conflict);
        }

        EnsureValidName(name);
        EnsureUniqueSiblingName(state, node.ParentId!, name, node.Id);
        renamed = node with { Name = name, Revision = checked(node.Revision + 1) };
        var next = ReplaceNode(state, renamed, AnchorsOf(state));
        Commit(vaultId, dataKey, next, Array.Empty<StagedWorkspaceBlob>());
        return next;
    }

    /// <summary>
    /// Moves one live node to another live folder. Identity, descendant
    /// identities, content revisions and citation anchors stay exactly as they
    /// were; moving a folder into itself or its own subtree refuses as a cycle,
    /// and an occupied destination name refuses before anything is written.
    /// </summary>
    public WorkspaceStateRecord MoveNode(
        string vaultId,
        ReadOnlySpan<byte> dataKey,
        WorkspaceStateRecord state,
        string nodeId,
        string destinationParentId,
        long expectedTreeRevision,
        long expectedNodeRevision,
        out WorkspaceNodeRecord moved)
    {
        ValidateExpectedTreeRevision(state, expectedTreeRevision);
        var node = FindLiveNode(state, nodeId);
        EnsureNotRoot(state, node);
        if (node.Revision != expectedNodeRevision)
        {
            throw new WorkspaceRefusalException(WorkspaceErrorCodes.Conflict);
        }

        EnsureLiveFolder(state, destinationParentId);
        EnsureNotDescendant(state, node.Id, destinationParentId);
        EnsureUniqueSiblingName(state, destinationParentId, node.Name, node.Id);
        moved = node with { ParentId = destinationParentId, Revision = checked(node.Revision + 1) };
        var next = ReplaceNode(state, moved, AnchorsOf(state));
        Commit(vaultId, dataKey, next, Array.Empty<StagedWorkspaceBlob>());
        return next;
    }

    /// <summary>
    /// Copies one live file or one live folder subtree. Every copied node gets
    /// a fresh identity, the copy root gets the requested (or source) name, and
    /// every copied file starts its own content revision 1 from the source's
    /// latest original bytes re-sealed under its new identity. No anchor,
    /// grant or other cross-document reference is copied, and the destination
    /// must not be inside the copied folder.
    /// </summary>
    public WorkspaceStateRecord CopyNode(
        string vaultId,
        ReadOnlySpan<byte> dataKey,
        WorkspaceStateRecord state,
        string nodeId,
        string destinationParentId,
        string? name,
        long expectedTreeRevision,
        out WorkspaceCopyInfo info)
    {
        ValidateExpectedTreeRevision(state, expectedTreeRevision);
        var source = FindLiveNode(state, nodeId);
        EnsureLiveFolder(state, destinationParentId);
        if (string.Equals(source.Kind, FolderKind, StringComparison.Ordinal))
        {
            EnsureNotDescendant(state, source.Id, destinationParentId);
        }

        var copyName = name ?? source.Name;
        EnsureValidName(copyName);
        EnsureUniqueSiblingName(state, destinationParentId, copyName, exceptNodeId: null);
        var subtree = CollectLiveSubtree(state, source);
        EnsureNodeCapacity(state, subtree.Count);

        var identifiers = new Dictionary<string, string>(subtree.Count, StringComparer.Ordinal);
        foreach (var unit in subtree)
        {
            identifiers[unit.Id] = NewIdentifier();
        }

        var additions = new WorkspaceNodeRecord[subtree.Count];
        var staged = new List<StagedWorkspaceBlob>();
        var files = 0;
        for (var index = 0; index < subtree.Count; index++)
        {
            var unit = subtree[index];
            var isRoot = string.Equals(unit.Id, source.Id, StringComparison.Ordinal);
            var parentId = isRoot ? destinationParentId : identifiers[unit.ParentId!];
            var itemName = isRoot ? copyName : unit.Name;
            var newId = identifiers[unit.Id];
            if (string.Equals(unit.Kind, FileKind, StringComparison.Ordinal))
            {
                var latest = unit.Revisions[^1];
                var revision = new WorkspaceRevisionRecord(1, latest.Length, latest.Sha256, NewStorageName());
                additions[index] = NewNode(newId, FileKind, parentId, itemName) with
                {
                    ContentRevision = 1,
                    Revisions = new[] { revision }
                };
                staged.Add(new StagedWorkspaceBlob(newId, revision, null, unit.Id, latest));
                files++;
                continue;
            }

            additions[index] = NewNode(newId, FolderKind, parentId, itemName);
        }

        var next = AppendNodes(state, additions);
        Commit(vaultId, dataKey, next, staged);
        info = new WorkspaceCopyInfo(
            identifiers[source.Id],
            destinationParentId,
            copyName,
            next.Revision,
            additions.Length - files,
            files);
        return next;
    }

    /// <summary>
    /// Reports exactly what a delete would tombstone right now, with the
    /// confirmation token that binds the act to this preview. It reads only.
    /// </summary>
    public static WorkspaceDeletePreviewInfo PreviewDelete(WorkspaceStateRecord state, string nodeId)
    {
        var node = FindLiveNode(state, nodeId);
        EnsureNotRoot(state, node);
        var descendants = CollectLiveDescendants(state, node);
        var preview = new WorkspacePreviewNodeInfo[descendants.Count];
        for (var index = 0; index < descendants.Count; index++)
        {
            var descendant = descendants[index];
            preview[index] = new WorkspacePreviewNodeInfo(
                descendant.Id, KindOf(descendant), descendant.ParentId, descendant.Name, descendant.Revision);
        }

        return new WorkspaceDeletePreviewInfo(
            node.Id,
            KindOf(node),
            node.Name,
            state.Revision,
            node.Revision,
            ComputePreviewToken(state, node, descendants),
            preview);
    }

    /// <summary>
    /// Tombstones the selected live node and every live descendant as one
    /// deletion group, but only for exactly the preview the owner confirmed.
    /// Identity, exact names, parent, retained bytes and anchors are kept; the
    /// nodes leave the tree and their relations become unavailable, and the act
    /// is reversible with <see cref="RestoreNode"/>.
    /// </summary>
    public WorkspaceStateRecord DeleteNode(
        string vaultId,
        ReadOnlySpan<byte> dataKey,
        WorkspaceStateRecord state,
        string nodeId,
        long expectedTreeRevision,
        long expectedNodeRevision,
        string previewToken,
        out WorkspaceDeleteInfo info)
    {
        ValidateExpectedTreeRevision(state, expectedTreeRevision);
        var node = FindLiveNode(state, nodeId);
        EnsureNotRoot(state, node);
        if (node.Revision != expectedNodeRevision)
        {
            throw new WorkspaceRefusalException(WorkspaceErrorCodes.Conflict);
        }

        var descendants = CollectLiveDescendants(state, node);
        if (!string.Equals(previewToken, ComputePreviewToken(state, node, descendants), StringComparison.Ordinal))
        {
            throw new WorkspaceRefusalException(WorkspaceErrorCodes.Conflict);
        }

        var deletionId = NewIdentifier();
        var group = new HashSet<string>(StringComparer.Ordinal) { node.Id };
        foreach (var descendant in descendants)
        {
            group.Add(descendant.Id);
        }

        var nodes = new WorkspaceNodeRecord[state.Nodes.Length];
        var folders = 0;
        var files = 0;
        for (var index = 0; index < state.Nodes.Length; index++)
        {
            var candidate = state.Nodes[index];
            if (!group.Contains(candidate.Id))
            {
                nodes[index] = candidate;
                continue;
            }

            nodes[index] = candidate with
            {
                State = WorkspaceNodeStates.Deleted,
                DeletionId = deletionId,
                Revision = checked(candidate.Revision + 1)
            };
            if (IsFolder(candidate))
            {
                folders++;
                continue;
            }

            files++;
        }

        var next = Committed(state, nodes, AnchorsOf(state));
        Commit(vaultId, dataKey, next, Array.Empty<StagedWorkspaceBlob>());
        info = new WorkspaceDeleteInfo(node.Id, deletionId, next.Revision, folders, files);
        return next;
    }

    /// <summary>
    /// Restores the whole deletion group the selected tombstone belongs to,
    /// and only when every reinstated node's parent is live (or restored with
    /// it) and its exact name is unoccupied there. A tombstone never returns
    /// into an occupied location and never resurrects anything else.
    /// </summary>
    public WorkspaceStateRecord RestoreNode(
        string vaultId,
        ReadOnlySpan<byte> dataKey,
        WorkspaceStateRecord state,
        string nodeId,
        long expectedTreeRevision,
        out WorkspaceRestoreInfo info)
    {
        ValidateExpectedTreeRevision(state, expectedTreeRevision);
        var node = FindNode(state, nodeId)
            ?? throw new WorkspaceRefusalException(WorkspaceErrorCodes.NotFound);
        if (IsLive(node))
        {
            throw new WorkspaceRefusalException(WorkspaceErrorCodes.NotDeleted);
        }

        var deletionId = node.DeletionId;
        if (!IsIdentifier(deletionId))
        {
            throw new InvalidDataException("Invalid workspace tombstone.");
        }

        var group = new List<WorkspaceNodeRecord>();
        foreach (var candidate in state.Nodes)
        {
            if (!IsLive(candidate) && string.Equals(candidate.DeletionId, deletionId, StringComparison.Ordinal))
            {
                group.Add(candidate);
            }
        }

        if (group.Count == 0)
        {
            throw new InvalidDataException("Invalid workspace tombstone group.");
        }

        var groupIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in group)
        {
            groupIds.Add(member.Id);
        }

        foreach (var member in group)
        {
            if (member.ParentId is null)
            {
                throw new InvalidDataException("A workspace tombstone has no parent.");
            }

            var parent = FindNode(state, member.ParentId)
                ?? throw new InvalidDataException("A workspace tombstone has no parent.");
            if (groupIds.Contains(parent.Id))
            {
                continue;
            }

            // A restore only lands where the parent is live and the exact name
            // is still unoccupied among live siblings. Anything else is the
            // restorable-location refusal, never another act's code.
            if (!string.Equals(parent.Kind, FolderKind, StringComparison.Ordinal) || !IsLive(parent) ||
                IsSiblingNameOccupied(state, parent.Id, member.Name, exceptNodeId: null))
            {
                throw new WorkspaceRefusalException(WorkspaceErrorCodes.RestoreConflict);
            }
        }

        var nodes = new WorkspaceNodeRecord[state.Nodes.Length];
        for (var index = 0; index < state.Nodes.Length; index++)
        {
            var candidate = state.Nodes[index];
            nodes[index] = groupIds.Contains(candidate.Id)
                ? candidate with
                {
                    State = WorkspaceNodeStates.Live,
                    DeletionId = null,
                    Revision = checked(candidate.Revision + 1)
                }
                : candidate;
        }

        var next = Committed(state, nodes, AnchorsOf(state));
        Commit(vaultId, dataKey, next, Array.Empty<StagedWorkspaceBlob>());
        var restored = FindNode(next, nodeId)
            ?? throw new InvalidDataException("The restored workspace node disappeared.");
        var latest = restored.Revisions.Length > 0 ? restored.Revisions[^1] : null;
        info = new WorkspaceRestoreInfo(
            restored.Id,
            next.Revision,
            restored.Revision,
            restored.ContentRevision,
            latest?.Length ?? 0,
            latest?.Sha256,
            group.Count);
        return next;
    }

    /// <summary>Lists the encrypted tombstones, ordered by identity so the answer is stable.</summary>
    public static WorkspaceTombstoneInfo[] ListTombstones(WorkspaceStateRecord state)
    {
        var tombstones = new List<WorkspaceTombstoneInfo>();
        foreach (var node in state.Nodes)
        {
            if (IsLive(node))
            {
                continue;
            }

            tombstones.Add(new WorkspaceTombstoneInfo(
                node.Id,
                KindOf(node),
                node.ParentId,
                node.Name,
                node.DeletionId ?? string.Empty,
                node.Revision,
                node.ContentRevision));
        }

        tombstones.Sort((left, right) => string.CompareOrdinal(left.NodeId, right.NodeId));
        return tombstones.ToArray();
    }

    /// <summary>
    /// S12-T5 reports exactly what a purge of the selected encrypted tombstone
    /// would erase: the whole deletion group, every retained original-byte
    /// revision, the citation anchors naming its files, and the token binding
    /// the act to this tree revision. A live node is refused because purge is
    /// the separate, irreversible act that follows a tombstone; it is never a
    /// second delete. Nothing is written.
    /// </summary>
    public static WorkspacePurgePreviewInfo PreviewPurge(WorkspaceStateRecord state, string nodeId)
    {
        var node = FindNode(state, nodeId)
            ?? throw new WorkspaceRefusalException(WorkspaceErrorCodes.NotFound);
        if (IsLive(node))
        {
            throw new WorkspaceRefusalException(WorkspaceErrorCodes.NotDeleted);
        }

        EnsureNotRoot(state, node);
        var group = CollectPurgeGroup(state, node);
        var nodes = new WorkspacePurgeNodeInfo[group.Count];
        var revisions = 0;
        var bytes = 0L;
        for (var index = 0; index < group.Count; index++)
        {
            var member = group[index];
            nodes[index] = new WorkspacePurgeNodeInfo(
                member.Id, KindOf(member), member.ParentId, member.Name, member.Revision, member.ContentRevision);
            revisions += member.Revisions.Length;
            foreach (var revision in member.Revisions)
            {
                bytes = checked(bytes + revision.Length);
            }
        }

        var anchors = 0;
        foreach (var anchor in AnchorsOf(state))
        {
            if (group.Any(member => string.Equals(member.Id, anchor.FileId, StringComparison.Ordinal)))
            {
                anchors++;
            }
        }

        return new WorkspacePurgePreviewInfo(
            node.Id,
            node.DeletionId ?? string.Empty,
            state.Revision,
            ComputePurgeToken(state, node, group),
            nodes,
            revisions,
            bytes,
            anchors);
    }

    /// <summary>
    /// S12-T5 permanently removes one confirmed tombstone deletion group: its
    /// canonical nodes and anchors leave the state, so its retained ciphertext
    /// is unreachable and reclaimed by the commit. The vault's one durable
    /// purge ledger — the same encrypted ledger canonical records and sessions
    /// use — records the owner's reason and every purged identity before this
    /// commit, and every later backup carries it; a restore re-applies it, so
    /// no backup taken afterwards can resurrect the purged identities.
    /// Backups taken before the purge are disclosed as irrevocable copies,
    /// never claimed recalled.
    /// </summary>
    public WorkspaceStateRecord PurgeNode(
        string vaultId,
        ReadOnlySpan<byte> dataKey,
        WorkspaceStateRecord state,
        string nodeId,
        string purgeId,
        long expectedTreeRevision,
        string previewToken,
        out WorkspacePurgeInfo info)
    {
        if (string.IsNullOrEmpty(previewToken))
        {
            throw new WorkspaceRefusalException(WorkspaceErrorCodes.PurgeConfirmation);
        }

        ValidateExpectedTreeRevision(state, expectedTreeRevision);
        var preview = PreviewPurge(state, nodeId);
        if (!string.Equals(preview.Preview, previewToken, StringComparison.Ordinal))
        {
            throw new WorkspaceRefusalException(WorkspaceErrorCodes.Conflict);
        }

        var node = FindNode(state, nodeId)
            ?? throw new WorkspaceRefusalException(WorkspaceErrorCodes.NotFound);
        var group = CollectPurgeGroup(state, node);
        var groupIds = new HashSet<string>(group.Count, StringComparer.Ordinal);
        foreach (var member in group)
        {
            groupIds.Add(member.Id);
        }

        var nodes = new List<WorkspaceNodeRecord>(state.Nodes.Length - group.Count);
        foreach (var candidate in state.Nodes)
        {
            if (!groupIds.Contains(candidate.Id))
            {
                nodes.Add(candidate);
            }
        }

        var anchors = AnchorsOf(state);
        var keptAnchors = new List<WorkspaceAnchorRecord>(anchors.Length);
        foreach (var anchor in anchors)
        {
            if (!groupIds.Contains(anchor.FileId))
            {
                keptAnchors.Add(anchor);
            }
        }

        var next = Committed(state, nodes.ToArray(), keptAnchors.ToArray());
        Commit(vaultId, dataKey, next, Array.Empty<StagedWorkspaceBlob>());
        info = new WorkspacePurgeInfo(
            purgeId,
            node.Id,
            preview.DeletionId,
            next.Revision,
            group.Count,
            preview.ContentRevisionCount,
            preview.TotalBytes,
            preview.AnchorCount);
        return next;
    }

    /// <summary>
    /// Removes the nodes and anchors whose identities the durable purge ledger
    /// reserved, without needing a preview token. It is how a commit that was
    /// confirmed in the ledger but interrupted before its workspace commit
    /// finishes on the next unlock, and how a restored workspace is scrubbed
    /// before it is materialized. A state with no reserved identity is returned
    /// unchanged and nothing is written.
    /// </summary>
    public WorkspaceStateRecord ApplyPurgedIdentifiers(
        string vaultId,
        ReadOnlySpan<byte> dataKey,
        WorkspaceStateRecord state,
        IReadOnlySet<string> purgedIds)
    {
        ArgumentNullException.ThrowIfNull(purgedIds);
        var filtered = FilterPurgedState(state, purgedIds);
        if (ReferenceEquals(filtered, state))
        {
            return state;
        }

        Commit(vaultId, dataKey, filtered, Array.Empty<StagedWorkspaceBlob>());
        return filtered;
    }

    /// <summary>
    /// Drops every node the durable ledger reserved, and every anchor that
    /// named one of those nodes. It is a pure projection over one state: the
    /// retained ciphertext of a dropped node is simply not referenced by the
    /// returned state.
    /// </summary>
    public static WorkspaceStateRecord FilterPurgedState(
        WorkspaceStateRecord state,
        IReadOnlySet<string> purgedIds)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(purgedIds);
        if (purgedIds.Count == 0)
        {
            return state;
        }

        var nodes = state.Nodes.Where(node => !purgedIds.Contains(node.Id)).ToArray();
        var anchors = AnchorsOf(state).Where(anchor => !purgedIds.Contains(anchor.FileId)).ToArray();
        if (nodes.Length == state.Nodes.Length && anchors.Length == AnchorsOf(state).Length)
        {
            return state;
        }

        return state with { Nodes = nodes, Anchors = anchors };
    }

    /// <summary>
    /// Pins a citation anchor to the live file's current content revision. The
    /// digest covers exactly the selected byte range of that revision, so the
    /// anchor identifies content rather than a position in a mutable path. A
    /// stale file revision or an out-of-range span refuses before any write.
    /// </summary>
    public WorkspaceStateRecord CreateAnchor(
        string vaultId,
        ReadOnlySpan<byte> dataKey,
        WorkspaceStateRecord state,
        string fileId,
        long expectedContentRevision,
        long offset,
        long length,
        long expectedTreeRevision,
        out WorkspaceAnchorRecord created)
    {
        ValidateExpectedTreeRevision(state, expectedTreeRevision);
        var node = FindLiveNode(state, fileId);
        if (!string.Equals(node.Kind, FileKind, StringComparison.Ordinal))
        {
            throw new WorkspaceRefusalException(WorkspaceErrorCodes.NotFound);
        }

        if (expectedContentRevision < 1 || node.ContentRevision != expectedContentRevision)
        {
            throw new WorkspaceRefusalException(WorkspaceErrorCodes.Conflict);
        }

        var latest = node.Revisions[^1];
        if (offset < 0 || offset > latest.Length || length < 1 || length > latest.Length - offset)
        {
            throw new WorkspaceRefusalException(WorkspaceErrorCodes.AnchorInvalid);
        }

        var content = ReadContent(vaultId, dataKey, state, fileId, latest.Revision);
        string sha256;
        try
        {
            sha256 = Digest(content.Content.AsSpan((int)offset, (int)length));
        }
        finally
        {
            Clear(content.Content);
        }

        created = new WorkspaceAnchorRecord(
            NewIdentifier(),
            fileId,
            latest.Revision,
            offset,
            length,
            sha256,
            WorkspaceAnchorStates.Live);
        var existing = AnchorsOf(state);
        var anchors = new WorkspaceAnchorRecord[existing.Length + 1];
        existing.CopyTo(anchors, 0);
        anchors[^1] = created;
        var next = Committed(state, state.Nodes, anchors);
        Commit(vaultId, dataKey, next, Array.Empty<StagedWorkspaceBlob>());
        return next;
    }

    /// <summary>
    /// Lists the citation anchors of one live file. A stale anchor keeps the
    /// revision and range it was pinned to and is reported as stale; it is
    /// never rebound to newer content. A deleted file's relations are
    /// unavailable, so the act refuses instead of pretending they are gone.
    /// </summary>
    public static WorkspaceAnchorInfo[] ReadAnchors(WorkspaceStateRecord state, string fileId)
    {
        var node = FindLiveNode(state, fileId);
        if (!string.Equals(node.Kind, FileKind, StringComparison.Ordinal))
        {
            throw new WorkspaceRefusalException(WorkspaceErrorCodes.NotFound);
        }

        var anchors = AnchorsOf(state);
        var count = 0;
        foreach (var anchor in anchors)
        {
            if (string.Equals(anchor.FileId, fileId, StringComparison.Ordinal))
            {
                count++;
            }
        }

        var result = new WorkspaceAnchorInfo[count];
        var index = 0;
        foreach (var anchor in anchors)
        {
            if (!string.Equals(anchor.FileId, fileId, StringComparison.Ordinal))
            {
                continue;
            }

            result[index] = new WorkspaceAnchorInfo(
                anchor.Id,
                anchor.FileId,
                anchor.ContentRevision,
                anchor.Offset,
                anchor.Length,
                anchor.Sha256,
                string.Equals(anchor.State, WorkspaceAnchorStates.Stale, StringComparison.Ordinal)
                    ? WorkspaceAnchorStates.Stale
                    : WorkspaceAnchorStates.Live);
            index++;
        }

        return result;
    }

    /// <summary>
    /// The one commit point: stage the new content ciphertext, stage the new
    /// state generation, then replace the committed pointer atomically. A
    /// failure before the pointer swap removes the staged files and leaves the
    /// previous committed state authoritative.
    /// </summary>
    private void Commit(
        string vaultId,
        ReadOnlySpan<byte> dataKey,
        WorkspaceStateRecord next,
        IReadOnlyList<StagedWorkspaceBlob> staged)
    {
        byte[]? plaintext = null;
        var generation = NewIdentifier();
        var stagedPaths = new List<string>(staged.Count + 1);
        try
        {
            // The state is bounded by the same maximum the open path enforces,
            // so a commit can never publish a generation that could not be read
            // back. It is serialised before any content is staged, so the bound
            // is a precondition rather than a partial write.
            plaintext = JsonSerializer.SerializeToUtf8Bytes(next, JsonOptions);
            if (plaintext.Length > MaximumStateBytes)
            {
                throw new WorkspaceRefusalException(WorkspaceErrorCodes.TooLarge);
            }

            EnsureDirectories();
            foreach (var blob in staged)
            {
                var path = Path.Combine(_blobDirectory, blob.Revision.StorageName);
                WriteStagedBlob(vaultId, dataKey, blob, path);
                stagedPaths.Add(path);
            }

            var statePath = Path.Combine(_stateDirectory, generation + StateExtension);
            WriteEncrypted(statePath, plaintext, dataKey, StateAssociatedData(vaultId, generation));
            stagedPaths.Add(statePath);
            WriteHead(generation);
        }
        catch
        {
            foreach (var path in stagedPaths)
            {
                TryDeleteFile(path);
            }

            throw;
        }
        finally
        {
            Clear(plaintext);
        }

        // The pointer is committed. Everything below only reclaims superseded
        // or uncommitted files; it can never change the committed state.
        Reconcile(next, generation);
    }

    /// <summary>
    /// Stages one content revision. Supplied bytes are sealed directly; a copy
    /// decrypts exactly one retained source revision under its own subject and
    /// re-seals those bytes under the new identity, so no more than one
    /// original buffer is held at a time.
    /// </summary>
    private void WriteStagedBlob(
        string vaultId,
        ReadOnlySpan<byte> dataKey,
        StagedWorkspaceBlob blob,
        string path)
    {
        if (blob.Content is not null)
        {
            WriteEncrypted(path, blob.Content, dataKey, BlobAssociatedData(vaultId, blob.FileId, blob.Revision));
            return;
        }

        if (blob.CopyFromFileId is null || blob.CopyFromRevision is null)
        {
            throw new InvalidDataException("Invalid staged workspace content.");
        }

        byte[] raw;
        try
        {
            raw = PrivateFile.ReadBytes(
                Path.Combine(_blobDirectory, blob.CopyFromRevision.StorageName), MaximumBlobEnvelopeBytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new WorkspaceRefusalException(WorkspaceErrorCodes.Unavailable);
        }

        byte[]? content = null;
        try
        {
            try
            {
                content = Decrypt(
                    DeserializeEnvelope(raw),
                    dataKey,
                    BlobAssociatedData(vaultId, blob.CopyFromFileId, blob.CopyFromRevision),
                    MaximumContentBytes);
                if (content.Length != blob.CopyFromRevision.Length ||
                    !string.Equals(Digest(content), blob.CopyFromRevision.Sha256, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("The copied workspace content does not match its authenticated metadata.");
                }

                WriteEncrypted(path, content, dataKey, BlobAssociatedData(vaultId, blob.FileId, blob.Revision));
            }
            catch (WorkspaceRefusalException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
                JsonException or CryptographicException or ArgumentException or FormatException)
            {
                throw new WorkspaceRefusalException(WorkspaceErrorCodes.Unavailable);
            }
        }
        finally
        {
            Clear(raw);
            Clear(content);
        }
    }

    private WorkspaceHeadRecord ReadHead()
    {
        var raw = PrivateFile.ReadBytes(_headPath, MaximumHeadBytes);
        WorkspaceHeadRecord head;
        try
        {
            head = JsonSerializer.Deserialize<WorkspaceHeadRecord>(raw, JsonOptions)
                ?? throw new InvalidDataException("Invalid workspace pointer.");
        }
        catch (JsonException)
        {
            throw new InvalidDataException("Invalid workspace pointer.");
        }

        if (head.FormatVersion != CurrentEnvelopeVersion)
        {
            throw new WorkspaceRefusalException(WorkspaceErrorCodes.UnsupportedVersion);
        }

        if (!IsIdentifier(head.Generation))
        {
            throw new InvalidDataException("Invalid workspace generation.");
        }

        return head;
    }

    private WorkspaceStateRecord ReadState(string vaultId, ReadOnlySpan<byte> dataKey, string generation)
    {
        var raw = PrivateFile.ReadBytes(
            Path.Combine(_stateDirectory, generation + StateExtension), MaximumStateEnvelopeBytes);
        try
        {
            var plaintext = Decrypt(DeserializeEnvelope(raw), dataKey, StateAssociatedData(vaultId, generation), MaximumStateBytes);
            try
            {
                var state = JsonSerializer.Deserialize<WorkspaceStateRecord>(plaintext, JsonOptions)
                    ?? throw new InvalidDataException("Invalid workspace state.");
                ValidateState(state);
                return state;
            }
            finally
            {
                Clear(plaintext);
            }
        }
        finally
        {
            Clear(raw);
        }
    }

    private void WriteHead(string generation)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new WorkspaceHeadRecord(CurrentEnvelopeVersion, generation), JsonOptions);
        try
        {
            EncryptedVaultStore.AtomicWrite(_headPath, bytes);
        }
        finally
        {
            Clear(bytes);
        }
    }

    private static WorkspaceEnvelopeRecord DeserializeEnvelope(byte[] raw)
    {
        WorkspaceEnvelopeRecord envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<WorkspaceEnvelopeRecord>(raw, JsonOptions)
                ?? throw new InvalidDataException("Invalid workspace envelope.");
        }
        catch (JsonException)
        {
            throw new InvalidDataException("Invalid workspace envelope.");
        }

        if (envelope.FormatVersion != CurrentEnvelopeVersion)
        {
            throw new WorkspaceRefusalException(WorkspaceErrorCodes.UnsupportedVersion);
        }

        return envelope;
    }

    private static void WriteEncrypted(string path, ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> dataKey, byte[] associatedData)
    {
        byte[]? nonce = null;
        byte[]? ciphertext = null;
        byte[]? tag = null;
        byte[]? envelopeBytes = null;
        try
        {
            nonce = RandomNumberGenerator.GetBytes(NonceBytes);
            ciphertext = new byte[plaintext.Length];
            tag = new byte[TagBytes];
            using (var aes = new AesGcm(dataKey, TagBytes))
            {
                aes.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);
            }

            envelopeBytes = JsonSerializer.SerializeToUtf8Bytes(
                new WorkspaceEnvelopeRecord(
                    CurrentEnvelopeVersion,
                    Convert.ToBase64String(nonce),
                    Convert.ToBase64String(ciphertext),
                    Convert.ToBase64String(tag)),
                JsonOptions);
            EncryptedVaultStore.AtomicWrite(path, envelopeBytes);
        }
        finally
        {
            Clear(nonce);
            Clear(ciphertext);
            Clear(tag);
            Clear(envelopeBytes);
            Clear(associatedData);
        }
    }

    /// <summary>
    /// Decrypts one envelope. The zero-length plaintext is a valid workspace
    /// file, so an empty ciphertext passes the size check and still has to
    /// authenticate its tag.
    /// </summary>
    private static byte[] Decrypt(
        WorkspaceEnvelopeRecord envelope,
        ReadOnlySpan<byte> dataKey,
        byte[] associatedData,
        int maximumPlaintextBytes)
    {
        byte[]? nonce = null;
        byte[]? ciphertext = null;
        byte[]? tag = null;
        byte[]? plaintext = null;
        try
        {
            nonce = Convert.FromBase64String(envelope.Nonce);
            ciphertext = Convert.FromBase64String(envelope.Ciphertext);
            tag = Convert.FromBase64String(envelope.Tag);
            if (nonce.Length != NonceBytes || tag.Length != TagBytes || ciphertext.Length > maximumPlaintextBytes)
            {
                throw new InvalidDataException("Invalid workspace envelope size.");
            }

            plaintext = new byte[ciphertext.Length];
            using var aes = new AesGcm(dataKey, TagBytes);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, associatedData);
            var result = plaintext;
            plaintext = null;
            return result;
        }
        finally
        {
            Clear(nonce);
            Clear(ciphertext);
            Clear(tag);
            Clear(plaintext);
            Clear(associatedData);
        }
    }

    private void EnsureDirectories()
    {
        if (File.Exists(_root))
        {
            throw new InvalidDataException("Invalid workspace directory.");
        }

        if (Directory.Exists(_root))
        {
            EnsureRealDirectory(_root);
        }
        else
        {
            Directory.CreateDirectory(_root);
        }

        EnsureDirectory(_stateDirectory);
        EnsureDirectory(_blobDirectory);
    }

    private static void EnsureDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            EnsureRealDirectory(path);
            return;
        }

        if (File.Exists(path))
        {
            throw new InvalidDataException("Invalid workspace directory.");
        }

        Directory.CreateDirectory(path);
    }

    private static void EnsureRealDirectory(string path)
    {
        var directory = new DirectoryInfo(path);
        if (!directory.Exists || (directory.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("The workspace directory must be a real directory.");
        }
    }

    private bool HasCommittedLeftovers()
    {
        return Directory.Exists(_stateDirectory) && Directory.GetFiles(_stateDirectory, "*", SearchOption.TopDirectoryOnly).Length > 0 ||
            Directory.Exists(_blobDirectory) && Directory.GetFiles(_blobDirectory, "*", SearchOption.TopDirectoryOnly).Length > 0;
    }

    /// <summary>
    /// Reclaims files that the committed tree does not reference: a state
    /// generation other than the committed one, content ciphertext of a
    /// superseded or interrupted staging pass, and interrupted atomic-write
    /// temporary files. It never deletes the committed generation, a retained
    /// revision or a directory, and it never throws.
    /// </summary>
    private void Reconcile(WorkspaceStateRecord state, string generation)
    {
        var live = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in state.Nodes)
        {
            foreach (var revision in node.Revisions)
            {
                live.Add(revision.StorageName);
            }
        }

        var committedStateName = generation + StateExtension;
        ReapDirectory(_root, _ => true);
        ReapDirectory(_stateDirectory, name => string.Equals(name, committedStateName, StringComparison.Ordinal));
        ReapDirectory(_blobDirectory, live.Contains);
    }

    private static void ReapDirectory(string directory, Func<string, bool> keep)
    {
        string[] entries;
        try
        {
            if (!Directory.Exists(directory))
            {
                return;
            }

            entries = Directory.GetFiles(directory, "*", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var path in entries)
        {
            var name = Path.GetFileName(path);
            if (name is null)
            {
                continue;
            }

            if (!IsTemporaryName(name) && keep(name))
            {
                continue;
            }

            TryDeleteFile(path);
        }
    }

    private static bool IsTemporaryName(string name) =>
        name.Length > 4 && name[0] == '.' && name.EndsWith(TemporarySuffix, StringComparison.Ordinal);

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            var attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            {
                return;
            }

            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void ValidateState(WorkspaceStateRecord state)
    {
        if (state.FormatVersion != CurrentFormatVersion ||
            state.MetadataSchemaVersion != CurrentMetadataSchemaVersion)
        {
            throw new WorkspaceRefusalException(WorkspaceErrorCodes.UnsupportedVersion);
        }

        if (!IsIdentifier(state.WorkspaceId) || state.Revision < 1 || !IsIdentifier(state.RootId) ||
            state.Nodes is null || state.Nodes.Length == 0 || state.Nodes.Length > MaximumNodes)
        {
            throw new InvalidDataException("Invalid workspace state identity.");
        }

        var byId = new Dictionary<string, WorkspaceNodeRecord>(state.Nodes.Length, StringComparer.Ordinal);
        foreach (var node in state.Nodes)
        {
            if (node is null || !IsIdentifier(node.Id) || node.Revision < 1 || !byId.TryAdd(node.Id, node))
            {
                throw new InvalidDataException("Invalid workspace node.");
            }

            if (IsLive(node))
            {
                if (node.DeletionId is not null)
                {
                    throw new InvalidDataException("Invalid live workspace node.");
                }
            }
            else if (!string.Equals(node.State, WorkspaceNodeStates.Deleted, StringComparison.Ordinal) ||
                     !IsIdentifier(node.DeletionId))
            {
                throw new InvalidDataException("Invalid workspace tombstone.");
            }
        }

        var root = byId.TryGetValue(state.RootId, out var candidateRoot) ? candidateRoot : null;
        if (root is null || !IsLive(root) ||
            !string.Equals(root.Kind, FolderKind, StringComparison.Ordinal) || root.ParentId is not null ||
            root.Name is null || root.Name.Length != 0 || root.ContentRevision != 0 || root.Revisions is null || root.Revisions.Length != 0)
        {
            throw new InvalidDataException("Invalid workspace root.");
        }

        var siblingNames = new HashSet<(string ParentId, string Name)>();
        foreach (var node in state.Nodes)
        {
            if (node.Id == state.RootId)
            {
                continue;
            }

            if (node.ParentId is null || !byId.TryGetValue(node.ParentId, out var parent) ||
                !string.Equals(parent.Kind, FolderKind, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Invalid workspace parent.");
            }

            if (IsLive(node) && !IsLive(parent))
            {
                throw new InvalidDataException("A live workspace node has a tombstoned parent.");
            }

            if (!IsValidName(node.Name))
            {
                throw new InvalidDataException("Invalid workspace name.");
            }

            // A tombstone keeps its exact name but does not occupy it: the freed
            // name may be taken by a new node, and only live siblings must stay
            // distinct. That is what makes restore-into-an-unoccupied-location
            // a real check instead of a phantom collision.
            if (IsLive(node) && !siblingNames.Add((node.ParentId, node.Name)))
            {
                throw new InvalidDataException("Invalid workspace sibling names.");
            }
        }

        ValidateReachesRoot(state, byId);

        foreach (var node in state.Nodes)
        {
            if (string.Equals(node.Kind, FolderKind, StringComparison.Ordinal))
            {
                if (node.ContentRevision != 0 || node.Revisions is null || node.Revisions.Length != 0)
                {
                    throw new InvalidDataException("Invalid workspace folder.");
                }

                continue;
            }

            if (!string.Equals(node.Kind, FileKind, StringComparison.Ordinal) ||
                node.ContentRevision < 1 || node.Revisions is null ||
                node.Revisions.Length == 0 || node.Revisions.Length > MaximumFileRevisions ||
                node.ContentRevision != node.Revisions.Length)
            {
                throw new InvalidDataException("Invalid workspace file.");
            }

            for (var index = 0; index < node.Revisions.Length; index++)
            {
                var revision = node.Revisions[index];
                if (revision is null || revision.Revision != index + 1 || revision.Length < 0 ||
                    revision.Length > MaximumContentBytes || !IsSha256Hex(revision.Sha256) || !IsStorageName(revision.StorageName))
                {
                    throw new InvalidDataException("Invalid workspace content revision.");
                }
            }
        }

        ValidateAnchors(state, byId);
    }

    /// <summary>
    /// Validates every citation anchor against the node it names: the file must
    /// exist, the pinned revision must be one of its retained revisions, the
    /// byte range must be inside that revision, and a live anchor must still
    /// name the file's current revision. An anchor recorded live on older
    /// content would be a silent rebound and is refused as invalid state.
    /// </summary>
    private static void ValidateAnchors(WorkspaceStateRecord state, Dictionary<string, WorkspaceNodeRecord> byId)
    {
        if (state.Anchors is null)
        {
            return;
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var anchor in state.Anchors)
        {
            if (anchor is null || !IsIdentifier(anchor.Id) || !ids.Add(anchor.Id))
            {
                throw new InvalidDataException("Invalid workspace anchor.");
            }

            if (anchor.FileId is null || !byId.TryGetValue(anchor.FileId, out var file) ||
                !string.Equals(file.Kind, FileKind, StringComparison.Ordinal) ||
                anchor.ContentRevision < 1 || anchor.ContentRevision > file.Revisions.Length ||
                anchor.Offset < 0 || anchor.Length < 1 || !IsSha256Hex(anchor.Sha256))
            {
                throw new InvalidDataException("Invalid workspace anchor.");
            }

            var revision = file.Revisions[(int)anchor.ContentRevision - 1];
            if (revision.Revision != anchor.ContentRevision || anchor.Offset > revision.Length ||
                anchor.Length > revision.Length - anchor.Offset)
            {
                throw new InvalidDataException("Invalid workspace anchor range.");
            }

            if (string.Equals(anchor.State, WorkspaceAnchorStates.Stale, StringComparison.Ordinal))
            {
                continue;
            }

            if (!string.Equals(anchor.State, WorkspaceAnchorStates.Live, StringComparison.Ordinal) ||
                file.ContentRevision != anchor.ContentRevision)
            {
                throw new InvalidDataException("Invalid workspace anchor state.");
            }
        }
    }

    /// <summary>
    /// Proves every node reaches the root. A node graph whose only parent is
    /// the stored parent forms a cycle exactly when the walk revisits a node,
    /// which the step bound turns into a refusal instead of a hang.
    /// </summary>
    private static void ValidateReachesRoot(WorkspaceStateRecord state, Dictionary<string, WorkspaceNodeRecord> byId)
    {
        var depths = new Dictionary<string, int>(StringComparer.Ordinal) { [state.RootId] = 0 };
        foreach (var node in state.Nodes)
        {
            if (!IsLive(node) || depths.ContainsKey(node.Id))
            {
                continue;
            }

            var chain = new List<string>();
            var current = node;
            while (!depths.ContainsKey(current.Id))
            {
                if (chain.Count >= byId.Count)
                {
                    throw new InvalidDataException("Workspace tree has a cycle.");
                }

                chain.Add(current.Id);
                if (current.ParentId is null || !byId.TryGetValue(current.ParentId, out var parent))
                {
                    throw new InvalidDataException("Invalid workspace parent.");
                }

                current = parent;
            }

            var depth = depths[current.Id];
            for (var index = chain.Count - 1; index >= 0; index--)
            {
                depths[chain[index]] = ++depth;
            }
        }
    }

    private static void ValidateExpectedTreeRevision(WorkspaceStateRecord state, long expectedTreeRevision)
    {
        if (expectedTreeRevision < 0 || state.Revision != expectedTreeRevision)
        {
            throw new WorkspaceRefusalException(WorkspaceErrorCodes.Conflict);
        }
    }

    private static WorkspaceNodeRecord EnsureLiveFolder(WorkspaceStateRecord state, string parentId)
    {
        var parent = FindNode(state, parentId);
        if (parent is null || !string.Equals(parent.Kind, FolderKind, StringComparison.Ordinal))
        {
            throw new WorkspaceRefusalException(WorkspaceErrorCodes.InvalidParent);
        }

        EnsureLive(parent);
        return parent;
    }

    private static void EnsureLive(WorkspaceNodeRecord node)
    {
        if (!IsLive(node))
        {
            throw new WorkspaceRefusalException(WorkspaceErrorCodes.Deleted);
        }
    }

    private static void EnsureNotRoot(WorkspaceStateRecord state, WorkspaceNodeRecord node)
    {
        if (string.Equals(node.Id, state.RootId, StringComparison.Ordinal))
        {
            throw new WorkspaceRefusalException(WorkspaceErrorCodes.RootImmutable);
        }
    }

    private static WorkspaceNodeRecord FindLiveNode(WorkspaceStateRecord state, string? nodeId)
    {
        var node = FindNode(state, nodeId)
            ?? throw new WorkspaceRefusalException(WorkspaceErrorCodes.NotFound);
        EnsureLive(node);
        return node;
    }

    private static WorkspaceNodeRecord FindLiveFile(WorkspaceStateRecord state, string? fileId)
    {
        var node = FindLiveNode(state, fileId);
        if (!string.Equals(node.Kind, FileKind, StringComparison.Ordinal))
        {
            throw new WorkspaceRefusalException(WorkspaceErrorCodes.NotFound);
        }

        return node;
    }

    private static bool IsLive(WorkspaceNodeRecord node) =>
        node.State is null || string.Equals(node.State, WorkspaceNodeStates.Live, StringComparison.Ordinal);

    private static bool IsFolder(WorkspaceNodeRecord node) =>
        string.Equals(node.Kind, FolderKind, StringComparison.Ordinal);

    private static WorkspaceNodeKind KindOf(WorkspaceNodeRecord node) =>
        IsFolder(node) ? WorkspaceNodeKind.Folder : WorkspaceNodeKind.File;

    /// <summary>
    /// Refuses a destination that is the node itself or one of its live
    /// descendants, so a move or a copy cannot create a cycle or an infinite
    /// copy. The walk is bounded by the node count, so a corrupt graph refuses
    /// instead of hanging.
    /// </summary>
    private static void EnsureNotDescendant(WorkspaceStateRecord state, string ancestorId, string candidateId)
    {
        var current = FindNode(state, candidateId)
            ?? throw new WorkspaceRefusalException(WorkspaceErrorCodes.InvalidParent);
        var steps = 0;
        while (true)
        {
            if (string.Equals(current.Id, ancestorId, StringComparison.Ordinal))
            {
                throw new WorkspaceRefusalException(WorkspaceErrorCodes.Cycle);
            }

            if (current.ParentId is null)
            {
                return;
            }

            current = FindNode(state, current.ParentId)
                ?? throw new InvalidDataException("Invalid workspace parent.");
            if (++steps > state.Nodes.Length)
            {
                throw new InvalidDataException("Workspace tree has a cycle.");
            }
        }
    }

    private static void EnsureUniqueSiblingName(
        WorkspaceStateRecord state,
        string parentId,
        string name,
        string? exceptNodeId)
    {
        if (IsSiblingNameOccupied(state, parentId, name, exceptNodeId))
        {
            throw new WorkspaceRefusalException(WorkspaceErrorCodes.NameConflict);
        }
    }

    /// <summary>
    /// The one sibling-collision rule every act shares: the exact Unicode
    /// scalar sequence, ordinal and case-sensitive, compared only against live
    /// nodes. There is no normalization, no locale and no case folding, so "A"
    /// and "a" are distinct names and canonically equivalent sequences stay
    /// distinct, and a tombstone keeps its name without occupying it.
    /// </summary>
    private static bool IsSiblingNameOccupied(
        WorkspaceStateRecord state,
        string parentId,
        string name,
        string? exceptNodeId)
    {
        foreach (var node in state.Nodes)
        {
            if (!IsLive(node) ||
                (exceptNodeId is not null && string.Equals(node.Id, exceptNodeId, StringComparison.Ordinal)))
            {
                continue;
            }

            if (string.Equals(node.ParentId, parentId, StringComparison.Ordinal) &&
                string.Equals(node.Name, name, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static void EnsureNodeCapacity(WorkspaceStateRecord state, int additional)
    {
        if (additional < 1 || state.Nodes.Length > MaximumNodes - additional)
        {
            throw new WorkspaceRefusalException(WorkspaceErrorCodes.TreeLimit);
        }
    }

    private static WorkspaceNodeRecord NewNode(string id, string kind, string parentId, string name) =>
        new(
            id,
            kind,
            parentId,
            name,
            1,
            0,
            Array.Empty<WorkspaceRevisionRecord>(),
            WorkspaceNodeStates.Live,
            null);

    private static WorkspaceAnchorRecord[] AnchorsOf(WorkspaceStateRecord state) =>
        state.Anchors ?? Array.Empty<WorkspaceAnchorRecord>();

    /// <summary>
    /// Materialises the optional members a state may omit: a node without a
    /// lifecycle state is a live node and a missing anchor list is no anchors.
    /// This is defaulting inside the same format, never a version conversion:
    /// the format and metadata-schema numbers are left exactly as they were
    /// read, and nothing is written here.
    /// </summary>
    private static WorkspaceStateRecord NormalizeState(WorkspaceStateRecord state)
    {
        var anchors = state.Anchors ?? Array.Empty<WorkspaceAnchorRecord>();
        WorkspaceNodeRecord[]? nodes = null;
        for (var index = 0; index < state.Nodes.Length; index++)
        {
            var node = state.Nodes[index];
            if (node.State is not null)
            {
                continue;
            }

            nodes ??= (WorkspaceNodeRecord[])state.Nodes.Clone();
            nodes[index] = node with { State = WorkspaceNodeStates.Live, DeletionId = null };
        }

        if (nodes is null && state.Anchors is not null)
        {
            return state;
        }

        return state with { Nodes = nodes ?? state.Nodes, Anchors = anchors };
    }

    private static WorkspaceStateRecord Committed(
        WorkspaceStateRecord state,
        WorkspaceNodeRecord[] nodes,
        WorkspaceAnchorRecord[] anchors) =>
        state with
        {
            FormatVersion = CurrentFormatVersion,
            MetadataSchemaVersion = CurrentMetadataSchemaVersion,
            Revision = checked(state.Revision + 1),
            Nodes = nodes,
            Anchors = anchors
        };

    private static WorkspaceStateRecord AppendNode(WorkspaceStateRecord state, WorkspaceNodeRecord node)
    {
        var nodes = new WorkspaceNodeRecord[state.Nodes.Length + 1];
        state.Nodes.CopyTo(nodes, 0);
        nodes[^1] = node;
        return Committed(state, nodes, AnchorsOf(state));
    }

    private static WorkspaceStateRecord AppendNodes(WorkspaceStateRecord state, WorkspaceNodeRecord[] additions)
    {
        var nodes = new WorkspaceNodeRecord[checked(state.Nodes.Length + additions.Length)];
        state.Nodes.CopyTo(nodes, 0);
        additions.CopyTo(nodes, state.Nodes.Length);
        return Committed(state, nodes, AnchorsOf(state));
    }

    private static WorkspaceStateRecord ReplaceNode(
        WorkspaceStateRecord state,
        WorkspaceNodeRecord updated,
        WorkspaceAnchorRecord[] anchors)
    {
        var nodes = new WorkspaceNodeRecord[state.Nodes.Length];
        for (var index = 0; index < state.Nodes.Length; index++)
        {
            var candidate = state.Nodes[index];
            nodes[index] = string.Equals(candidate.Id, updated.Id, StringComparison.Ordinal) ? updated : candidate;
        }

        return Committed(state, nodes, anchors);
    }

    /// <summary>
    /// Enumerates the selected live node and its live descendants parent before
    /// children, so a copy can assign every fresh parent before its children
    /// refer to it. Tombstones are not part of a live tree and never travel
    /// with a copy.
    /// </summary>
    private static List<WorkspaceNodeRecord> CollectLiveSubtree(WorkspaceStateRecord state, WorkspaceNodeRecord source)
    {
        var children = new Dictionary<string, List<WorkspaceNodeRecord>>(StringComparer.Ordinal);
        foreach (var node in state.Nodes)
        {
            if (!IsLive(node) || node.ParentId is null)
            {
                continue;
            }

            if (!children.TryGetValue(node.ParentId, out var siblings))
            {
                siblings = new List<WorkspaceNodeRecord>();
                children[node.ParentId] = siblings;
            }

            siblings.Add(node);
        }

        var ordered = new List<WorkspaceNodeRecord>();
        var pending = new Queue<WorkspaceNodeRecord>();
        pending.Enqueue(source);
        while (pending.Count > 0)
        {
            var current = pending.Dequeue();
            ordered.Add(current);
            if (!children.TryGetValue(current.Id, out var descendants))
            {
                continue;
            }

            foreach (var descendant in descendants)
            {
                pending.Enqueue(descendant);
            }
        }

        return ordered;
    }

    private static List<WorkspaceNodeRecord> CollectLiveDescendants(WorkspaceStateRecord state, WorkspaceNodeRecord source)
    {
        var subtree = CollectLiveSubtree(state, source);
        subtree.RemoveAt(0);
        return subtree;
    }

    /// <summary>
    /// The confirmation token that binds a delete to exactly what the owner
    /// previewed: the workspace revision, the selected node's revision and the
    /// sorted identities and revisions of every live descendant. Any change
    /// between preview and delete refuses as a stale precondition.
    /// </summary>
    private static string ComputePreviewToken(
        WorkspaceStateRecord state,
        WorkspaceNodeRecord node,
        IReadOnlyList<WorkspaceNodeRecord> descendants)
    {
        var ordered = new List<WorkspaceNodeRecord>(descendants);
        ordered.Sort((left, right) => string.CompareOrdinal(left.Id, right.Id));
        var text = new StringBuilder(128 + (ordered.Count * 40));
        text.Append("MetaBrain|workspace-delete-preview|1|")
            .Append(state.WorkspaceId).Append('|')
            .Append(state.Revision.ToString(CultureInfo.InvariantCulture)).Append('|')
            .Append(node.Id).Append('|')
            .Append(node.Revision.ToString(CultureInfo.InvariantCulture));
        foreach (var descendant in ordered)
        {
            text.Append('|')
                .Append(descendant.Id).Append(':')
                .Append(descendant.Revision.ToString(CultureInfo.InvariantCulture));
        }

        return Digest(Encoding.UTF8.GetBytes(text.ToString()));
    }

    /// <summary>
    /// The tombstone group one purge erases: every node that shares the
    /// selected tombstone's deletion identity, ordered by identity. Restoring
    /// created them as one unit, so purging one erases exactly that unit.
    /// </summary>
    private static List<WorkspaceNodeRecord> CollectPurgeGroup(WorkspaceStateRecord state, WorkspaceNodeRecord node)
    {
        var deletionId = node.DeletionId;
        if (!IsIdentifier(deletionId))
        {
            throw new InvalidDataException("Invalid workspace tombstone.");
        }

        var group = new List<WorkspaceNodeRecord>();
        foreach (var candidate in state.Nodes)
        {
            if (!IsLive(candidate) && string.Equals(candidate.DeletionId, deletionId, StringComparison.Ordinal))
            {
                group.Add(candidate);
            }
        }

        if (group.Count == 0 || !group.Any(member => string.Equals(member.Id, node.Id, StringComparison.Ordinal)))
        {
            throw new InvalidDataException("Invalid workspace tombstone group.");
        }

        group.Sort((left, right) => string.CompareOrdinal(left.Id, right.Id));
        return group;
    }

    /// <summary>
    /// The token that binds a purge confirmation to the exact previewed state:
    /// the workspace and format identities, the tree revision, the selected
    /// tombstone and its deletion identity, and every group member's node
    /// revision plus each retained content revision descriptor. A later
    /// mutation, a different group or different retained bytes changes it.
    /// </summary>
    private static string ComputePurgeToken(
        WorkspaceStateRecord state,
        WorkspaceNodeRecord node,
        IReadOnlyList<WorkspaceNodeRecord> group)
    {
        var text = new StringBuilder(192 + (group.Count * 96));
        text.Append("MetaBrain|workspace-purge-preview|1|")
            .Append(state.WorkspaceId).Append('|')
            .Append(state.FormatVersion.ToString(CultureInfo.InvariantCulture)).Append('|')
            .Append(state.MetadataSchemaVersion.ToString(CultureInfo.InvariantCulture)).Append('|')
            .Append(state.Revision.ToString(CultureInfo.InvariantCulture)).Append('|')
            .Append(node.Id).Append('|')
            .Append(node.DeletionId).Append('|')
            .Append(node.Revision.ToString(CultureInfo.InvariantCulture));
        foreach (var member in group)
        {
            text.Append('|')
                .Append(member.Id).Append(':')
                .Append(member.Revision.ToString(CultureInfo.InvariantCulture)).Append(':')
                .Append(member.ContentRevision.ToString(CultureInfo.InvariantCulture));
            foreach (var revision in member.Revisions)
            {
                text.Append(',')
                    .Append(revision.Revision.ToString(CultureInfo.InvariantCulture)).Append(':')
                    .Append(revision.Length.ToString(CultureInfo.InvariantCulture)).Append(':')
                    .Append(revision.Sha256);
            }
        }

        return Digest(Encoding.UTF8.GetBytes(text.ToString()));
    }

    /// <summary>
    /// Marks the live anchors of one file stale after its content advanced.
    /// The anchor keeps its original revision and range; it is never rebound to
    /// the new bytes. The original array is returned when nothing changed.
    /// </summary>
    private static WorkspaceAnchorRecord[] MarkAnchorsStale(WorkspaceAnchorRecord[] anchors, string fileId)
    {
        var changed = false;
        foreach (var anchor in anchors)
        {
            if (string.Equals(anchor.FileId, fileId, StringComparison.Ordinal) &&
                string.Equals(anchor.State, WorkspaceAnchorStates.Live, StringComparison.Ordinal))
            {
                changed = true;
                break;
            }
        }

        if (!changed)
        {
            return anchors;
        }

        var next = new WorkspaceAnchorRecord[anchors.Length];
        for (var index = 0; index < anchors.Length; index++)
        {
            var anchor = anchors[index];
            next[index] = string.Equals(anchor.FileId, fileId, StringComparison.Ordinal) &&
                string.Equals(anchor.State, WorkspaceAnchorStates.Live, StringComparison.Ordinal)
                ? anchor with { State = WorkspaceAnchorStates.Stale }
                : anchor;
        }

        return next;
    }

    private static WorkspaceNodeRecord? FindNode(WorkspaceStateRecord state, string? nodeId)
    {
        if (string.IsNullOrEmpty(nodeId))
        {
            return null;
        }

        foreach (var node in state.Nodes)
        {
            if (string.Equals(node.Id, nodeId, StringComparison.Ordinal))
            {
                return node;
            }
        }

        return null;
    }

    private static void EnsureContentSize(int length)
    {
        if (length > MaximumContentBytes)
        {
            throw new WorkspaceRefusalException(WorkspaceErrorCodes.TooLarge);
        }
    }

    /// <summary>
    /// A logical name is one non-empty sequence of valid Unicode scalar values.
    /// An ill-formed sequence (a lone surrogate) is refused rather than
    /// replaced, and no normalization is applied.
    /// </summary>
    private static bool IsValidName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > MaximumNameLength)
        {
            return false;
        }

        for (var index = 0; index < name.Length; index++)
        {
            var character = name[index];
            if (character == '\0')
            {
                return false;
            }

            if (char.IsHighSurrogate(character))
            {
                if (index + 1 >= name.Length || !char.IsLowSurrogate(name[index + 1]))
                {
                    return false;
                }

                index++;
                continue;
            }

            if (char.IsLowSurrogate(character))
            {
                return false;
            }
        }

        return true;
    }

    private static void EnsureValidName(string? name)
    {
        if (!IsValidName(name))
        {
            throw new WorkspaceRefusalException(WorkspaceErrorCodes.InvalidName);
        }
    }

    private static string Digest(ReadOnlySpan<byte> content) => Convert.ToHexStringLower(SHA256.HashData(content));

    private static byte[] StateAssociatedData(string vaultId, string generation) =>
        Encoding.UTF8.GetBytes(
            $"MetaBrain|workspace-state|{CurrentEnvelopeVersion}|{vaultId}|{generation}");

    private static byte[] BlobAssociatedData(string vaultId, string fileId, WorkspaceRevisionRecord revision) =>
        Encoding.UTF8.GetBytes(
            $"MetaBrain|workspace-content|{CurrentEnvelopeVersion}|{vaultId}|{fileId}|{revision.Revision}|{revision.Length}|{revision.Sha256}");

    private static string NewIdentifier() => Guid.NewGuid().ToString("N");

    private static string NewStorageName() => Guid.NewGuid().ToString("N") + BlobExtension;

    private static bool IsIdentifier(string? value)
    {
        if (value is null || value.Length != IdentifierHexLength)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsStorageName(string? value) =>
        value is not null && value.Length == IdentifierHexLength + BlobExtension.Length &&
        value.EndsWith(BlobExtension, StringComparison.Ordinal) &&
        IsIdentifier(value[..IdentifierHexLength]);

    private static bool IsSha256Hex(string? value)
    {
        if (value is null || value.Length != Sha256HexLength)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }

    private static void Clear(byte[]? buffer)
    {
        if (buffer is { Length: > 0 })
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }
}
