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
public sealed record WorkspaceContentRevisionInfo(long Revision, long Length, string Sha256);

/// <summary>
/// One node of the logical tree as the owner sees it. <see cref="ParentId"/> is
/// null only for the workspace root folder. <see cref="Name"/> is the exact
/// logical name (no normalization); <see cref="Revision"/> is the node's own
/// metadata/tree revision and advances when a later task renames or moves it,
/// while <see cref="ContentRevision"/> and <see cref="Revisions"/> describe the
/// original bytes of a file.
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
/// </summary>
public sealed record WorkspaceStatusInfo(
    string State,
    int FormatVersion,
    int MetadataSchemaVersion,
    long Revision,
    string? RootId,
    int FolderCount,
    int FileCount,
    int ContentRevisionCount);

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
/// The exact original bytes of one retained file revision, together with the
/// authenticated digest the tree records for them.
/// </summary>
public sealed record WorkspaceFileContent(long Revision, long Length, string Sha256, byte[] Content);

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
    /// exist. The caller owns (and zeroizes) the returned buffer.
    /// </summary>
    WorkspaceFileContent ReadWorkspaceFile(string fileId, long revision);
}
