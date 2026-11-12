namespace MetaBrain.Core.Security;

/// <summary>
/// The kind of one logical workspace node. A folder is a container whose
/// identity survives a rename or a move; a file owns one or more retained
/// content revisions.
/// </summary>
public enum WorkspaceNodeKind
{
    Folder = 1,
    File = 2
}

/// <summary>
/// The persisted kind names of a workspace node. They are part of the
/// authenticated workspace state, so they are fixed strings rather than enum
/// numbers and are shared by the store and the owner wire.
/// </summary>
public static class WorkspaceNodeKinds
{
    public const string Folder = "folder";
    public const string File = "file";
}

/// <summary>
/// The persisted lifecycle states of a workspace node. A live node is part of
/// the tree; a deleted node is an encrypted tombstone that still owns its
/// identity, name, parent, content revisions and relations, but occupies
/// nothing in the tree until it is restored into an unoccupied location.
/// </summary>
public static class WorkspaceNodeStates
{
    public const string Live = "live";
    public const string Deleted = "deleted";
}

/// <summary>
/// The persisted states of one citation anchor. An anchor binds a document id,
/// an exact content revision and a byte range with the digest of those bytes;
/// it never stores a path. <see cref="Live"/> means the anchored revision is
/// still the document's current revision, <see cref="Stale"/> means the
/// document's content advanced and the anchor was marked rather than rebound.
/// </summary>
public static class WorkspaceAnchorStates
{
    public const string Live = "live";
    public const string Stale = "stale";
}

/// <summary>
/// One retained original-byte revision of a file. The revision is separate
/// from the workspace tree revision: replacing content advances only this
/// counter, and a caller that pins a revision keeps addressing exactly those
/// bytes after later revisions exist.
/// </summary>
/// <remarks>
/// The digest is the SHA-256 of the exact original bytes as lowercase hex. It
/// is owner-visible metadata: it is not derived from a path and it is never
/// recomputed from a rewritten file.
/// </remarks>
public sealed record WorkspaceContentRevisionInfo(long Revision, long Length, string Sha256)
{
    public const int MaximumContentBytes = 1024 * 1024;
}

/// <summary>
/// One node of the logical tree as the owner sees it. <see cref="ParentId"/> is
/// null only for the workspace root folder. <see cref="Name"/> is the exact
/// logical name (no normalization); <see cref="Revision"/> is the node's own
/// metadata/tree revision and advances when the node is renamed, moved,
/// tombstoned or restored, while <see cref="ContentRevision"/> and
/// <see cref="Revisions"/> describe the original bytes of a file.
/// </summary>
public sealed record WorkspaceNodeInfo(
    string Id,
    WorkspaceNodeKind Kind,
    string? ParentId,
    string Name,
    long Revision,
    long ContentRevision,
    WorkspaceContentRevisionInfo[] Revisions);

/// <summary>
/// One consistent read of the logical workspace tree: the workspace identity,
/// the authenticated format/metadata-schema versions, the workspace (tree)
/// revision a caller must present as its structural precondition, and every
/// node.
/// </summary>
public sealed record WorkspaceSnapshot(
    string WorkspaceId,
    int FormatVersion,
    int MetadataSchemaVersion,
    long Revision,
    string RootId,
    WorkspaceNodeInfo[] Nodes);

/// <summary>
/// What one workspace-status read reports. <see cref="State"/> is
/// <see cref="WorkspaceStates.Ready"/> or
/// <see cref="WorkspaceStates.Uninitialized"/>; an unreadable or unsupported
/// workspace is a refusal, never an empty workspace. The revision is the
/// structural precondition value for the next tree change.
/// <see cref="DeletedNodeCount"/> counts the encrypted tombstones that are not
/// part of the tree but still retain identity, metadata and bytes.
/// </summary>
public sealed record WorkspaceStatusInfo(
    string State,
    int FormatVersion,
    int MetadataSchemaVersion,
    long Revision,
    string? RootId,
    int FolderCount,
    int FileCount,
    int ContentRevisionCount,
    int DeletedNodeCount,
    int PurgedNodeCount = 0);

/// <summary>
/// The outcome of one committed workspace mutation: the node it addressed and
/// the identities the caller needs for its next precondition (workspace tree
/// revision for structure, content revision for bytes).
/// </summary>
public sealed record WorkspaceMutationInfo(
    string NodeId,
    long WorkspaceRevision,
    long NodeRevision,
    long ContentRevision,
    long Length,
    string? Sha256);

/// <summary>
/// The outcome of one committed copy: the fresh root node the copy created,
/// where it now lives, and how many nodes were created beneath it. Every
/// copied node has its own identity, its own content revision and no inherited
/// relations.
/// </summary>
public sealed record WorkspaceCopyInfo(
    string NodeId,
    string? ParentId,
    string Name,
    long WorkspaceRevision,
    int FolderCount,
    int FileCount);

/// <summary>
/// The outcome of one committed tombstone: the selected node keeps its
/// identity, the deletion act got its own identifier so the whole subtree can
/// be restored as one unit, and the counts describe what became unavailable.
/// </summary>
public sealed record WorkspaceDeleteInfo(
    string NodeId,
    string DeletionId,
    long WorkspaceRevision,
    int FolderCount,
    int FileCount);

/// <summary>
/// The outcome of one committed restore: the selected node is live again with
/// its original identity, and <see cref="RestoredNodeCount"/> reports the whole
/// deletion group that was reinstated with it.
/// </summary>
public sealed record WorkspaceRestoreInfo(
    string NodeId,
    long WorkspaceRevision,
    long NodeRevision,
    long ContentRevision,
    long Length,
    string? Sha256,
    int RestoredNodeCount);

/// <summary>One encrypted tombstone as the owner sees it.</summary>
public sealed record WorkspaceTombstoneInfo(
    string NodeId,
    WorkspaceNodeKind Kind,
    string? ParentId,
    string Name,
    string DeletionId,
    long Revision,
    long ContentRevision);

/// <summary>One node that a folder delete would tombstone with the selected folder.</summary>
public sealed record WorkspacePreviewNodeInfo(
    string NodeId,
    WorkspaceNodeKind Kind,
    string? ParentId,
    string Name,
    long Revision);

/// <summary>
/// The preview of one pending delete: exactly what the act would tombstone at
/// the revision the owner is looking at, plus the confirmation token that binds
/// the act to that preview. Nothing is written by producing it.
/// </summary>
public sealed record WorkspaceDeletePreviewInfo(
    string NodeId,
    WorkspaceNodeKind Kind,
    string Name,
    long TreeRevision,
    long NodeRevision,
    string Preview,
    WorkspacePreviewNodeInfo[] Descendants);

/// <summary>
/// One revision-pinned citation anchor. It names the document id, the exact
/// content revision and the byte range, and carries the digest of the anchored
/// bytes; it never carries a mutable path or display name. A stale anchor keeps
/// its original revision and range and is never rebound to newer content.
/// </summary>
public sealed record WorkspaceAnchorInfo(
    string AnchorId,
    string FileId,
    long ContentRevision,
    long Offset,
    long Length,
    string Sha256,
    string State);

/// <summary>One tombstone member of a pending workspace purge.</summary>
public sealed record WorkspacePurgeNodeInfo(
    string NodeId,
    WorkspaceNodeKind Kind,
    string? ParentId,
    string Name,
    long Revision,
    long ContentRevision);

/// <summary>
/// The exact scope one pending workspace purge would erase: the whole
/// tombstone deletion group, every retained original-byte revision it still
/// holds, the citation anchors that name its files, and the token that binds
/// the act to this preview. Producing it writes nothing.
/// </summary>
public sealed record WorkspacePurgePreviewInfo(
    string NodeId,
    string DeletionId,
    long TreeRevision,
    string Preview,
    WorkspacePurgeNodeInfo[] Nodes,
    int ContentRevisionCount,
    long TotalBytes,
    int AnchorCount);

/// <summary>
/// The outcome of one committed workspace purge: the tombstone group is gone
/// from the canonical state, its retained ciphertext is unreferenced (and
/// reclaimed), and its identities stay reserved in the vault's single durable
/// purge ledger, which every later backup carries and a restore re-applies.
/// </summary>
public sealed record WorkspacePurgeInfo(
    string PurgeId,
    string NodeId,
    string DeletionId,
    long WorkspaceRevision,
    int NodesPurged,
    int ContentRevisionsPurged,
    long BytesPurged,
    int AnchorsPurged);

/// <summary>
/// The exact original bytes of one retained file revision, together with the
/// authenticated digest the tree records for them.
/// </summary>
public sealed record WorkspaceFileContent(long Revision, long Length, string Sha256, byte[] Content);

/// <summary>
/// One original file that an export writes outside the vault. <see cref="Path"/>
/// is the relative destination path built from the exact logical names, joined
/// with '/'; the bytes written are exactly the retained revision the preview
/// pinned, never a later revision.
/// </summary>
public sealed record WorkspaceExportFileInfo(
    string FileId,
    string Path,
    long ContentRevision,
    long Length,
    string Sha256);

/// <summary>One logical folder that an export recreates, with its relative path.</summary>
public sealed record WorkspaceExportFolderInfo(string FolderId, string Path);

/// <summary>
/// The exact scope one pending export would disclose: the selected node, the
/// destination that would be created, every file and folder beneath it at the
/// revisions the export pinned, and the anchors that belong to those files.
/// Nothing is written by producing it. <see cref="Token"/> binds the act to
/// this exact workspace revision, scope, pinned revisions and destination, so
/// a changed workspace, destination or selection refuses instead of exporting
/// something the owner did not see.
/// </summary>
/// <remarks>
/// <see cref="ExcludedTombstones"/> counts the encrypted tombstones beneath the
/// selected folder that are not part of the live tree and therefore not
/// exported; the number is reported so the exclusion is never silent.
/// <see cref="Anchors"/> carries only anchors of files inside the scope, so a
/// citation of a document outside the selection never leaves the vault.
/// </remarks>
public sealed record WorkspaceExportPreviewInfo(
    string NodeId,
    WorkspaceNodeKind Kind,
    string Name,
    long TreeRevision,
    long NodeRevision,
    long ContentRevision,
    string Destination,
    string SidecarPath,
    string Token,
    WorkspaceExportFileInfo[] Files,
    WorkspaceExportFolderInfo[] Folders,
    WorkspaceAnchorInfo[] Anchors,
    int ExcludedTombstones,
    long TotalBytes);

/// <summary>
/// The outcome of one committed plaintext export: where the owner's copy was
/// written, the versioned metadata document that travels with it, and what the
/// act disclosed. The vault itself is unchanged by the act.
/// </summary>
public sealed record WorkspaceExportInfo(
    string NodeId,
    string Destination,
    string SidecarPath,
    long TreeRevision,
    int FolderCount,
    int FileCount,
    long TotalBytes,
    string SidecarSha256);

/// <summary>
/// The parent selector that names the workspace root before its generated id
/// is known. A node id is always 32 lowercase hex characters, so this word can
/// never collide with an id; it is only a selector, never a stored name.
/// </summary>
public static class WorkspaceParentIds
{
    public const string Root = "root";
}

/// <summary>
/// The states a workspace status read can report.
/// </summary>
public static class WorkspaceStates
{
    /// <summary>A committed workspace tree exists and its state opened.</summary>
    public const string Ready = "ready";

    /// <summary>No workspace has been committed in this vault yet.</summary>
    public const string Uninitialized = "uninitialized";
}

/// <summary>
/// The refusals a workspace act can produce. Each code is a stable identity for
/// the owner, the audit trail and the diagnostics; none of them is a transport
/// failure and none of them is a silent fallback.
/// </summary>
public static class WorkspaceErrorCodes
{
    /// <summary>The tree/workspace revision the caller presented is stale.</summary>
    public const string Conflict = "workspace_conflict";

    /// <summary>An ordinal sibling name already holds the requested name.</summary>
    public const string NameConflict = "name_conflict";

    /// <summary>The logical name is not a valid Unicode scalar sequence.</summary>
    public const string InvalidName = "invalid_name";

    /// <summary>The named parent is absent or is not a folder.</summary>
    public const string InvalidParent = "workspace_invalid_parent";

    /// <summary>The named node or retained revision is not in the tree.</summary>
    public const string NotFound = "workspace_not_found";

    /// <summary>The bytes are larger than one bounded workspace envelope.</summary>
    public const string TooLarge = "workspace_too_large";

    /// <summary>One file already holds the maximum number of retained revisions.</summary>
    public const string RevisionLimit = "workspace_revision_limit";

    /// <summary>The workspace tree already holds the maximum number of nodes.</summary>
    public const string TreeLimit = "workspace_tree_limit";

    /// <summary>The committed workspace could not be authenticated or read.</summary>
    public const string Unavailable = "workspace_unreadable";

    /// <summary>The workspace format or metadata schema is newer than this build.</summary>
    public const string UnsupportedVersion = "workspace_unsupported_version";

    /// <summary>The selected node is an encrypted tombstone, not part of the live tree.</summary>
    public const string Deleted = "workspace_deleted";

    /// <summary>The selected node is live, so it is not a restorable tombstone.</summary>
    public const string NotDeleted = "workspace_not_deleted";

    /// <summary>The move or copy would place a folder inside its own subtree.</summary>
    public const string Cycle = "workspace_cycle";

    /// <summary>The tombstone cannot return because its location is occupied or its parent is not live.</summary>
    public const string RestoreConflict = "workspace_restore_conflict";

    /// <summary>The workspace root folder cannot be renamed, moved, copied, deleted or restored.</summary>
    public const string RootImmutable = "workspace_root_immutable";

    /// <summary>The anchored byte range is not inside the pinned content revision.</summary>
    public const string AnchorInvalid = "workspace_anchor_invalid";

    /// <summary>
    /// The destination the owner named cannot be used safely: it is not an
    /// absolute path, it contains a traversal segment or a device namespace,
    /// its parent is not a real directory, a component of the path is a
    /// reparse point, or an entry already occupies the exact name.
    /// </summary>
    public const string ExportDestination = "workspace_export_destination";

    /// <summary>
    /// One exact logical name cannot be a destination path component on this
    /// platform (a separator, a reserved device name, a trailing dot or space,
    /// or an invalid character). The name is refused, never rewritten.
    /// </summary>
    public const string ExportName = "workspace_export_name";

    /// <summary>
    /// Two selected names would occupy the same destination entry under the
    /// destination filesystem's case-insensitive rule, or a selected name would
    /// collide with the metadata document.
    /// </summary>
    public const string ExportCollision = "workspace_export_collision";

    /// <summary>The export act did not name the plaintext disclosure confirmation.</summary>
    public const string ExportConfirmation = "workspace_export_confirmation";

    /// <summary>
    /// A revision selector was supplied for a folder, or the selected file does
    /// not retain that revision. A later revision is never silently substituted.
    /// </summary>
    public const string ExportRevision = "workspace_export_revision";

    /// <summary>The purge act did not name the typed purge confirmation token.</summary>
    public const string PurgeConfirmation = "workspace_purge_confirmation";

    /// <summary>The plaintext export could not be written; everything the act created was removed.</summary>
    public const string ExportFailed = "workspace_export_failed";

    /// <summary>
    /// The plaintext export could not be written and the act could not remove
    /// everything it created, so the destination may hold a partial copy.
    /// </summary>
    public const string ExportIncomplete = "workspace_export_incomplete";
}

/// <summary>
/// One refusal of a workspace act. It carries only the stable code: the message
/// is generic so no private name, path or byte ever travels on it.
/// </summary>
public sealed class WorkspaceRefusalException : Exception
{
    public WorkspaceRefusalException(string code)
        : base("The workspace request was refused.")
    {
        Code = code;
    }

    /// <summary>One of the <see cref="WorkspaceErrorCodes"/> values.</summary>
    public string Code { get; }
}

/// <summary>
/// The private workspace persistence reachable only inside one unlocked vault
/// lease. The implementation keeps one authoritative commit point for the
/// tree, its owner-authored metadata and the retained original bytes, so an
/// interrupted act leaves either the previous committed state or the fully
/// committed one.
/// </summary>
/// <remarks>
/// The tree revision and the file content revision are separate
/// preconditions, as the owner-approved contract requires: a structural act
/// presents <c>expectedTreeRevision</c>, a byte replacement presents
/// <c>expectedContentRevision</c>, and neither silently stands in for the
/// other. Sibling names compare as exact Unicode scalar sequences, ordinal and
/// case-sensitive. Every method runs inside the caller's unlocked lease; a
/// locked vault has no workspace read or write.
/// </remarks>
public interface IWorkspacePersistence
{
    /// <summary>
    /// Reports the committed workspace state. It refuses with
    /// <see cref="WorkspaceErrorCodes.Unavailable"/> when a committed
    /// workspace exists but cannot be authenticated, so an unreadable
    /// workspace is never presented as empty.
    /// </summary>
    WorkspaceStatusInfo ReadWorkspaceStatus();

    /// <summary>
    /// Reads one consistent snapshot of the committed tree, or null when no
    /// workspace has been committed yet.
    /// </summary>
    WorkspaceSnapshot? ReadWorkspaceTree();

    /// <summary>
    /// Creates one logical folder under <paramref name="parentId"/> at the
    /// workspace revision the caller saw. A workspace that has never been
    /// initialized accepts <paramref name="expectedTreeRevision"/> 0: the
    /// empty tree the caller saw is created first, then the folder is added.
    /// </summary>
    WorkspaceMutationInfo CreateWorkspaceFolder(string parentId, string name, long expectedTreeRevision);

    /// <summary>
    /// Stores one file whose original bytes are kept exactly as supplied
    /// (including zero bytes and non-UTF-8 binary) as content revision 1,
    /// under the caller's structural precondition.
    /// </summary>
    WorkspaceMutationInfo CreateWorkspaceFile(string parentId, string name, byte[] content, long expectedTreeRevision);

    /// <summary>
    /// Stores new original bytes as the next content revision of an existing
    /// file, gated by that file's content revision. Earlier revisions remain
    /// readable at their own revision.
    /// </summary>
    WorkspaceMutationInfo ReviseWorkspaceFile(string fileId, byte[] content, long expectedContentRevision);

    /// <summary>
    /// Reads the exact original bytes of one retained revision. A
    /// <paramref name="revision"/> of 0 reads the latest committed revision;
    /// a positive revision reads that retained revision even after newer ones
    /// exist. The owner owns (and zeroizes) the returned buffer.
    /// </summary>
    WorkspaceFileContent ReadWorkspaceFile(string fileId, long revision);

    /// <summary>
    /// Renames one live node inside its current parent under both the tree and
    /// the node's own revision. Identity, parent, descendants, retained content
    /// revisions and citation anchors are untouched; an occupied sibling name
    /// refuses the act without changing anything.
    /// </summary>
    WorkspaceMutationInfo RenameWorkspaceNode(
        string nodeId,
        string name,
        long expectedTreeRevision,
        long expectedNodeRevision);

    /// <summary>
    /// Moves one live node to a destination folder under both the tree and the
    /// node's own revision. Identity, descendants, retained content revisions
    /// and citation anchors are untouched; a cycle, an occupied destination
    /// name or a non-folder destination refuses the act without changing
    /// anything.
    /// </summary>
    WorkspaceMutationInfo MoveWorkspaceNode(
        string nodeId,
        string destinationParentId,
        long expectedTreeRevision,
        long expectedNodeRevision);

    /// <summary>
    /// Copies one live file or one live folder subtree into a destination
    /// folder under the tree revision the caller saw. Every copied node gets a
    /// fresh identity and every copied file starts its own content revision 1
    /// from the source's latest original bytes; no grant, citation anchor or
    /// other cross-document reference is copied.
    /// </summary>
    WorkspaceCopyInfo CopyWorkspaceNode(
        string nodeId,
        string destinationParentId,
        string? name,
        long expectedTreeRevision);

    /// <summary>
    /// Reports exactly what a delete of the selected live node would tombstone
    /// at the current revision and the confirmation token that binds the act to
    /// that preview. Nothing is written.
    /// </summary>
    WorkspaceDeletePreviewInfo PreviewWorkspaceDelete(string nodeId);

    /// <summary>
    /// Tombstones the selected live node and every live descendant under both
    /// the tree and the node's own revision, but only for the exact preview the
    /// caller confirmed. Identity, names, retained bytes and anchors are kept,
    /// and the act is reversible with <see cref="RestoreWorkspaceNode"/>.
    /// </summary>
    WorkspaceDeleteInfo DeleteWorkspaceNode(
        string nodeId,
        long expectedTreeRevision,
        long expectedNodeRevision,
        string previewToken);

    /// <summary>
    /// Restores the deletion group the selected tombstone belongs to, but only
    /// when every reinstated node's location is unoccupied and its parent is
    /// live. A group whose location was taken refuses without changing
    /// anything.
    /// </summary>
    WorkspaceRestoreInfo RestoreWorkspaceNode(string nodeId, long expectedTreeRevision);

    /// <summary>Lists the encrypted tombstones the committed workspace retains.</summary>
    WorkspaceTombstoneInfo[] ReadWorkspaceTombstones();

    /// <summary>
    /// Pins one citation anchor to the live file's current content revision.
    /// The anchor records the document id, that exact revision, the byte range
    /// and the digest of the anchored bytes; it never records a path or name,
    /// so a later rename or move cannot rebind it.
    /// </summary>
    WorkspaceAnchorInfo CreateWorkspaceAnchor(
        string fileId,
        long expectedContentRevision,
        long offset,
        long length,
        long expectedTreeRevision);

    /// <summary>
    /// Lists the anchors of one live file. An anchor whose document content
    /// advanced keeps its original revision and is reported stale; a deleted
    /// file refuses because its relations are unavailable.
    /// </summary>
    WorkspaceAnchorInfo[] ReadWorkspaceAnchors(string fileId);

    /// <summary>
    /// Reports exactly what a purge of the selected encrypted tombstone would
    /// erase: the whole deletion group it belongs to, every retained
    /// original-byte revision, the citation anchors naming those files, and the
    /// token binding the act to this tree revision. A live node is refused
    /// because purge is separate from delete and always follows a tombstone.
    /// Nothing is written.
    /// </summary>
    WorkspacePurgePreviewInfo PreviewWorkspacePurge(string nodeId);

    /// <summary>
    /// Purges the whole deletion group of the selected tombstone, but only for
    /// exactly the preview the owner confirmed. The canonical nodes and their
    /// anchors are removed, their retained ciphertext becomes unreachable, and
    /// the owner's reason and every purged identity are written into the one
    /// durable purge ledger the vault already keeps for canonical records and
    /// sessions. A later backup carries that ledger and a restore re-applies
    /// it, so no backup taken afterwards can resurrect the purged identities.
    /// Backups and exports taken before the purge are disclosed, never
    /// recalled.
    /// </summary>
    WorkspacePurgeInfo PurgeWorkspaceNode(
        string nodeId,
        string reason,
        long expectedTreeRevision,
        string previewToken);

    /// <summary>
    /// Reports the exact plaintext scope one export of the selected live file
    /// or folder would write at <paramref name="destination"/>, the retained
    /// revision of every file in it, the versioned metadata document that
    /// travels with it, and the token that binds the act. Nothing is written,
    /// no temporary plaintext is staged, and a tombstone, the workspace root,
    /// an unusable destination or a name that cannot be represented on the
    /// destination filesystem refuses with a real code.
    /// </summary>
    WorkspaceExportPreviewInfo PreviewWorkspaceExport(string nodeId, long contentRevision, string destination);

    /// <summary>
    /// Writes the selected live file or folder as original bytes outside the
    /// vault at the destination the owner named, but only for exactly the
    /// scope, revisions and destination the owner previewed. Each file keeps
    /// its exact retained bytes; the metadata document is written next to the
    /// content, never inside an original file; the vault itself is unchanged.
    /// A stale preview, a changed destination or a changed revision refuses
    /// before anything is written, and a failure removes what the act created.
    /// </summary>
    WorkspaceExportInfo ExportWorkspaceNode(
        string nodeId,
        long contentRevision,
        string destination,
        string previewToken,
        CancellationToken cancellationToken);
}
