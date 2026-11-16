using System.Security.Cryptography;

namespace MetaBrain.Core.Security;

/// <summary>One exact file revision selected by an owner folder preview.</summary>
public sealed record WorkspaceFolderScopeMember(
    string FileId,
    long ContentRevision,
    long Length,
    string Sha256,
    long AccessLifecycleGeneration);

/// <summary>The workspace member projection safe to include in an agent session.</summary>
public sealed record WorkspaceFolderSessionMember(string FileId, long ContentRevision);

/// <summary>
/// Owner-only preview of one frozen workspace folder selection. It reports ids,
/// pinned revision descriptors and exclusions, never paths or display names.
/// </summary>
public sealed record WorkspaceFolderGrantPreview(
    string PreviewId,
    string WorkspaceId,
    string FolderId,
    long TreeRevision,
    string[] FolderIds,
    WorkspaceFolderScopeMember[] Members,
    string[] ExcludedFolderIds,
    int ExcludedFolderCount,
    int ExcludedFileCount,
    string[] Operations,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    ScopeGrantEgress? Egress,
    long PolicyGeneration,
    long WorkspaceScopeGeneration,
    long UnlockEpoch,
    DateTimeOffset PreviewExpiresAtUtc);

/// <summary>The encrypted authority minted from one approved folder preview.</summary>
public sealed record WorkspaceFolderScope(
    string WorkspaceId,
    string FolderId,
    long TreeRevision,
    WorkspaceFolderScopeMember[] Members,
    string[] ExcludedFolderIds);

/// <summary>Owner inspection of a pinned member against the live workspace.</summary>
public sealed record WorkspaceFolderMemberInspection(
    string FileId,
    long PinnedRevision,
    long? CurrentRevision,
    long Length,
    string Sha256,
    bool Stale,
    bool Available);

/// <summary>Owner-only inspection of one workspace folder grant.</summary>
public sealed record WorkspaceFolderGrantSummary(
    string GrantId,
    string WorkspaceId,
    string FolderId,
    long ApprovedTreeRevision,
    WorkspaceFolderMemberInspection[] Members,
    string[] ExcludedFolderIds,
    string[] Operations,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    ScopeGrantEgress? Egress,
    long PolicyGeneration,
    int ScopeSemanticsVersion,
    long ScopeSemanticsGeneration,
    DateTimeOffset? RevokedAtUtc);

/// <summary>A consistent view of encrypted policy and the live workspace tree.</summary>
public sealed record WorkspaceFolderGrantSnapshot(OwnerScopeGrantState State, WorkspaceSnapshot? Workspace);

/// <summary>
/// The persistence boundary for folder grants. Implementations serialize the
/// policy state, workspace tree events and exact reads under one vault commit
/// gate, so a concurrent move, delete, revoke or lock cannot race an approval
/// or disclose bytes after authority was withdrawn.
/// </summary>
public interface IWorkspaceFolderGrantPersistence : IScopeGrantPersistence
{
    WorkspaceFolderGrantSnapshot ReadWorkspaceFolderGrantSnapshot();

    T UpdateWorkspaceFolderGrantState<T>(
        Func<OwnerScopeGrantState, WorkspaceSnapshot?, (OwnerScopeGrantState State, T Result)> update);

    WorkspaceFileContent ReadWorkspaceFileForGrant(
        string grantId,
        int scopeSemanticsVersion,
        long scopeSemanticsGeneration,
        string fileId,
        long revision);

    bool IsWorkspaceFileAuthorizedForGrant(
        string grantId,
        int scopeSemanticsVersion,
        long scopeSemanticsGeneration,
        string fileId,
        long revision);
}

/// <summary>A stable owner-safe refusal from the workspace-folder grant API.</summary>
public sealed class WorkspaceFolderGrantRefusalException : InvalidOperationException
{
    public WorkspaceFolderGrantRefusalException(string code)
        : base("The workspace folder grant operation is unavailable.")
    {
        Code = code;
    }

    public string Code { get; }
}

/// <summary>
/// Shared identity-based folder-scope checks used by approval inspection and
/// the encrypted persistence read boundary. Folder identity narrows a frozen
/// member set; it never enrolls a new identity or content revision.
/// </summary>
public static class WorkspaceFolderGrantEvaluator
{
    private const int MaximumWorkspaceNodes = 100_000;

    public static WorkspaceFolderScope NormalizeScope(WorkspaceFolderScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (!IsNodeId(scope.WorkspaceId) || !IsNodeId(scope.FolderId) || scope.TreeRevision < 1 ||
            scope.Members is null || scope.Members.Length > MaximumWorkspaceNodes ||
            scope.ExcludedFolderIds is null || scope.ExcludedFolderIds.Length > MaximumWorkspaceNodes ||
            scope.Members.Any(member => member is null))
        {
            throw new ArgumentException("The workspace folder scope is invalid.", nameof(scope));
        }

        var excluded = scope.ExcludedFolderIds.Order(StringComparer.Ordinal).ToArray();
        var excludedIds = new HashSet<string>(StringComparer.Ordinal);
        if (excluded.Any(id => !IsNodeId(id) || !excludedIds.Add(id)) || excludedIds.Contains(scope.FolderId))
        {
            throw new ArgumentException("The workspace folder exclusions are invalid.", nameof(scope));
        }

        var members = scope.Members.OrderBy(member => member.FileId, StringComparer.Ordinal).ToArray();
        var memberIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in members)
        {
            if (!IsNodeId(member.FileId) || !memberIds.Add(member.FileId) ||
                member.ContentRevision < 1 || member.Length < 0 || member.AccessLifecycleGeneration < 0 ||
                !IsSha256(member.Sha256))
            {
                throw new ArgumentException("The workspace folder members are invalid.", nameof(scope));
            }
        }

        return scope with { Members = members, ExcludedFolderIds = excluded };
    }

    public static WorkspaceFolderScope CloneScope(WorkspaceFolderScope scope) => scope with
    {
        Members = scope.Members.Select(member => member with { }).ToArray(),
        ExcludedFolderIds = (string[])scope.ExcludedFolderIds.Clone()
    };


    /// <summary>Checks one exact pin and the current live identity subtree.</summary>
    public static bool AllowsRead(
        WorkspaceSnapshot? currentWorkspace,
        WorkspaceFolderScope scope,
        string? fileId,
        long revision)
    {
        if (currentWorkspace is null || revision < 1 ||
            !string.Equals(currentWorkspace.WorkspaceId, scope.WorkspaceId, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(fileId))
        {
            return false;
        }

        var member = FindMember(scope.Members, fileId);
        if (member is null || member.ContentRevision != revision)
        {
            return false;
        }

        var nodes = BuildNodeIndex(currentWorkspace);
        var exclusions = new HashSet<string>(scope.ExcludedFolderIds, StringComparer.Ordinal);
        return IsMemberLive(nodes, scope, member, exclusions);
    }


    internal static Dictionary<string, WorkspaceNodeInfo> BuildNodeIndex(WorkspaceSnapshot workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (workspace.Nodes is null || workspace.Nodes.Length > MaximumWorkspaceNodes)
        {
            throw new InvalidDataException("The workspace tree is invalid.");
        }

        var nodes = new Dictionary<string, WorkspaceNodeInfo>(workspace.Nodes.Length, StringComparer.Ordinal);
        foreach (var node in workspace.Nodes)
        {
            if (node is null || !nodes.TryAdd(node.Id, node))
            {
                throw new InvalidDataException("The workspace tree is invalid.");
            }
        }

        return nodes;
    }


    internal static WorkspaceFolderScopeMember? FindMember(
        IReadOnlyList<WorkspaceFolderScopeMember> members,
        string fileId)
    {
        var low = 0;
        var high = members.Count - 1;
        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            var comparison = string.CompareOrdinal(members[middle].FileId, fileId);
            if (comparison == 0)
            {
                return members[middle];
            }

            if (comparison < 0)
            {
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return null;
    }

    internal static bool IsMemberLive(
        IReadOnlyDictionary<string, WorkspaceNodeInfo> nodes,
        WorkspaceFolderScope scope,
        WorkspaceFolderScopeMember member) =>
        IsMemberLive(nodes, scope, member, new HashSet<string>(scope.ExcludedFolderIds, StringComparer.Ordinal));

    internal static bool IsMemberLive(
        IReadOnlyDictionary<string, WorkspaceNodeInfo> nodes,
        WorkspaceFolderScope scope,
        WorkspaceFolderScopeMember member,
        IReadOnlySet<string> exclusions)
    {
        if (!nodes.TryGetValue(member.FileId, out var file) || file.Kind != WorkspaceNodeKind.File ||
            file.AccessLifecycleGeneration != member.AccessLifecycleGeneration ||
            !file.Revisions.Any(revision =>
                revision.Revision == member.ContentRevision && revision.Length == member.Length &&
                string.Equals(revision.Sha256, member.Sha256, StringComparison.Ordinal)) ||
            !nodes.TryGetValue(scope.FolderId, out var folder) || folder.Kind != WorkspaceNodeKind.Folder)
        {
            return false;
        }

        var current = file;
        while (true)
        {
            if (exclusions.Contains(current.Id))
            {
                return false;
            }

            if (string.Equals(current.Id, scope.FolderId, StringComparison.Ordinal))
            {
                return true;
            }

            if (current.ParentId is null || !nodes.TryGetValue(current.ParentId, out current!))
            {
                return false;
            }
        }
    }


    internal static bool IsNodeId(string? value)
    {
        if (value is null || value.Length != 32)
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

    private static bool IsSha256(string? value)
    {
        if (value is null || value.Length != 64)
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
}

/// <summary>
/// Owner approval authority for the zone-independent workspace-folder subject.
/// The approval snapshot is persisted in the encrypted scope state, while only
/// the exact member ids/revisions are disclosed in an agent session.
/// </summary>
public sealed class WorkspaceFolderGrantAuthority
{
    public const int CurrentScopeSemanticsVersion = 1;
    public const string WorkspaceFileReadOperation = "workspace.file.read";

    private const int MaximumPendingPreviews = 1024;
    private const int MaximumWorkspaceNodes = 100_000;
    private static readonly TimeSpan PreviewLifetime = TimeSpan.FromMinutes(5);
    private readonly object _gate = new();
    private readonly Dictionary<string, PendingPreview> _previews = new(StringComparer.Ordinal);

    private sealed record PendingPreview(WorkspaceFolderGrantPreview Value, DateTimeOffset ExpiresAtUtc);

    private sealed record Selection(
        string WorkspaceId,
        string FolderId,
        long TreeRevision,
        string[] FolderIds,
        WorkspaceFolderScopeMember[] Members,
        string[] ExcludedFolderIds,
        int ExcludedFolderCount,
        int ExcludedFileCount);

    public WorkspaceFolderGrantPreview Preview(
        AuthenticatedContext actor,
        string? folderId,
        IReadOnlyList<string>? excludedFolderIds,
        IReadOnlyList<string>? operations,
        DateTimeOffset expiresAtUtc,
        ScopeGrantEgress? egress,
        IWorkspaceFolderGrantPersistence persistence)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(persistence);
        EnsureOwner(actor);
        ValidateExpiry(expiresAtUtc);
        var operationSnapshot = NormalizeOperations(operations);
        OwnerScopeGrantAuthority.ValidateOperationScope(
            operationSnapshot,
            Array.Empty<ScopeResourceRevision>(),
            egress);

        var observed = persistence.ReadWorkspaceFolderGrantSnapshot();
        var state = OwnerScopeGrantAuthority.ValidateAndMigrateState(observed.State);
        if (observed.Workspace is null)
        {
            throw new ArgumentException("A live workspace folder is required.", nameof(folderId));
        }

        var selection = ResolveSelection(observed.Workspace, folderId, excludedFolderIds);
        lock (_gate)
        {
            return StorePreviewLocked(state, selection, operationSnapshot, expiresAtUtc, egress);
        }
    }

    public OwnerScopeGrantIssue Issue(
        AuthenticatedContext actor,
        string? previewId,
        IWorkspaceFolderGrantPersistence persistence)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(persistence);
        EnsureOwner(actor);
        if (!Guid.TryParseExact(previewId, "N", out _))
        {
            throw new WorkspaceFolderGrantRefusalException("scope_preview_unavailable");
        }

        lock (_gate)
        {
            if (!_previews.TryGetValue(previewId!, out var pending) || pending.ExpiresAtUtc <= DateTimeOffset.UtcNow)
            {
                _previews.Remove(previewId!);
                throw new WorkspaceFolderGrantRefusalException("scope_preview_unavailable");
            }

            var preview = pending.Value;
            if (preview.ExpiresAtUtc <= DateTimeOffset.UtcNow)
            {
                _previews.Remove(previewId!);
                throw new WorkspaceFolderGrantRefusalException("scope_preview_unavailable");
            }

            var tokenBytes = RandomNumberGenerator.GetBytes(32);
            var verifierBytes = SHA256.HashData(tokenBytes);
            try
            {
                var issue = persistence.UpdateWorkspaceFolderGrantState((state, workspace) =>
                {
                    state = OwnerScopeGrantAuthority.ValidateAndMigrateState(state);
                    if (state.PolicyGeneration != preview.PolicyGeneration ||
                        state.WorkspaceScopeGeneration != preview.WorkspaceScopeGeneration ||
                        state.UnlockEpoch != preview.UnlockEpoch || workspace is null ||
                        workspace.Revision != preview.TreeRevision ||
                        !string.Equals(workspace.WorkspaceId, preview.WorkspaceId, StringComparison.Ordinal))
                    {
                        throw new WorkspaceFolderGrantRefusalException("request_stale");
                    }

                    var current = ResolveSelection(workspace, preview.FolderId, preview.ExcludedFolderIds);
                    if (!SelectionMatches(preview, current))
                    {
                        throw new WorkspaceFolderGrantRefusalException("request_stale");
                    }

                    if (state.Grants.Length >= 10_000)
                    {
                        throw new IOException("The owner scope grant state cannot accept another grant.");
                    }

                    if (state.WorkspaceScopeGeneration == long.MaxValue)
                    {
                        throw new IOException("The workspace scope generation is exhausted.");
                    }

                    var grant = new PersistedScopeGrant(
                        Guid.NewGuid().ToString("N"),
                        EncodeBase64Url(verifierBytes),
                        Array.Empty<ScopeResourceRevision>(),
                        (string[])preview.Operations.Clone(),
                        Array.Empty<ScopeResourceRevision>(),
                        DateTimeOffset.UtcNow,
                        preview.ExpiresAtUtc,
                        preview.Egress is null ? null : preview.Egress with { },
                        state.PolicyGeneration,
                        state.UnlockEpoch,
                        new WorkspaceFolderScope(
                            preview.WorkspaceId,
                            preview.FolderId,
                            preview.TreeRevision,
                            preview.Members.Select(member => member with { }).ToArray(),
                            (string[])preview.ExcludedFolderIds.Clone()),
                        CurrentScopeSemanticsVersion,
                        1);
                    var nextState = state with
                    {
                        SchemaVersion = OwnerScopeGrantState.CurrentSchemaVersion,
                        Grants = state.Grants.Append(grant).ToArray(),
                        WorkspaceScopeGeneration = state.WorkspaceScopeGeneration + 1
                    };
                    var issued = new OwnerScopeGrantIssue(
                        "mb1_" + EncodeBase64Url(tokenBytes),
                        grant);
                    return (nextState, issued);
                });
                _previews.Remove(previewId!);
                return issue;
            }
            catch (WorkspaceFolderGrantRefusalException)
            {
                _previews.Remove(previewId!);
                throw;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(tokenBytes);
                CryptographicOperations.ZeroMemory(verifierBytes);
            }
        }
    }

    public PersistedScopeGrant Revoke(
        AuthenticatedContext actor,
        string? grantId,
        IWorkspaceFolderGrantPersistence persistence)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(persistence);
        EnsureOwner(actor);
        if (!Guid.TryParseExact(grantId, "N", out _))
        {
            throw new WorkspaceFolderGrantRefusalException("workspace_grant_unavailable");
        }

        return persistence.UpdateWorkspaceFolderGrantState((state, _) =>
        {
            state = OwnerScopeGrantAuthority.ValidateAndMigrateState(state);
            var index = Array.FindIndex(state.Grants, grant =>
                string.Equals(grant.GrantId, grantId, StringComparison.Ordinal) &&
                grant.WorkspaceFolderScope is not null);
            if (index < 0)
            {
                throw new WorkspaceFolderGrantRefusalException("workspace_grant_unavailable");
            }

            var grant = state.Grants[index];
            if (grant.RevokedAtUtc is not null)
            {
                return (state, CloneGrant(grant));
            }

            if (state.WorkspaceScopeGeneration == long.MaxValue || grant.ScopeSemanticsGeneration == long.MaxValue)
            {
                throw new IOException("The workspace scope generation is exhausted.");
            }

            var revoked = grant with
            {
                RevokedAtUtc = DateTimeOffset.UtcNow,
                ScopeSemanticsGeneration = grant.ScopeSemanticsGeneration + 1
            };
            var grants = (PersistedScopeGrant[])state.Grants.Clone();
            grants[index] = revoked;
            var nextState = state with
            {
                SchemaVersion = OwnerScopeGrantState.CurrentSchemaVersion,
                Grants = grants,
                WorkspaceScopeGeneration = state.WorkspaceScopeGeneration + 1
            };
            return (nextState, CloneGrant(revoked));
        });
    }

    public WorkspaceFolderGrantSummary[] List(
        AuthenticatedContext actor,
        IWorkspaceFolderGrantPersistence persistence)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(persistence);
        EnsureOwner(actor);
        var observed = persistence.ReadWorkspaceFolderGrantSnapshot();
        var state = OwnerScopeGrantAuthority.ValidateAndMigrateState(observed.State);
        return state.Grants
            .Where(grant => grant.WorkspaceFolderScope is not null)
            .OrderBy(grant => grant.GrantId, StringComparer.Ordinal)
            .Select(grant => Summarize(grant, state, observed.Workspace))
            .ToArray();
    }

    public void ClearPreviews()
    {
        lock (_gate)
        {
            _previews.Clear();
        }
    }

    internal static bool IsValidFolderOperations(
        IReadOnlyCollection<string> operations,
        ScopeGrantEgress? egress) =>
        operations.Contains(WorkspaceFileReadOperation, StringComparer.Ordinal) &&
        operations.All(operation =>
            string.Equals(operation, WorkspaceFileReadOperation, StringComparison.Ordinal) ||
            string.Equals(operation, "provider.egress", StringComparison.Ordinal)) &&
        operations.Contains("provider.egress", StringComparer.Ordinal) == (egress is not null);

    internal static bool IsActiveGrant(
        OwnerScopeGrantState state,
        PersistedScopeGrant grant,
        int scopeSemanticsVersion,
        long scopeSemanticsGeneration) =>
        grant.WorkspaceFolderScope is not null && grant.RevokedAtUtc is null &&
        grant.ScopeSemanticsVersion == scopeSemanticsVersion &&
        grant.ScopeSemanticsGeneration == scopeSemanticsGeneration &&
        grant.ExpiresAtUtc > DateTimeOffset.UtcNow &&
        grant.PolicyGeneration == state.PolicyGeneration &&
        grant.UnlockEpoch == state.UnlockEpoch;

    private WorkspaceFolderGrantPreview StorePreviewLocked(
        OwnerScopeGrantState state,
        Selection selection,
        string[] operations,
        DateTimeOffset expiresAtUtc,
        ScopeGrantEgress? egress)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var expiredId in _previews
                     .Where(pair => pair.Value.ExpiresAtUtc <= now)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _previews.Remove(expiredId);
        }

        if (_previews.Count >= MaximumPendingPreviews)
        {
            throw new IOException("The workspace folder preview capacity is exhausted.");
        }

        var preview = new WorkspaceFolderGrantPreview(
            Guid.NewGuid().ToString("N"),
            selection.WorkspaceId,
            selection.FolderId,
            selection.TreeRevision,
            (string[])selection.FolderIds.Clone(),
            selection.Members.Select(member => member with { }).ToArray(),
            (string[])selection.ExcludedFolderIds.Clone(),
            selection.ExcludedFolderCount,
            selection.ExcludedFileCount,
            (string[])operations.Clone(),
            now,
            expiresAtUtc,
            egress is null ? null : egress with { },
            state.PolicyGeneration,
            state.WorkspaceScopeGeneration,
            state.UnlockEpoch,
            now + PreviewLifetime);
        _previews.Add(preview.PreviewId, new PendingPreview(preview, preview.PreviewExpiresAtUtc));
        return ClonePreview(preview);
    }

    private static Selection ResolveSelection(
        WorkspaceSnapshot workspace,
        string? folderId,
        IReadOnlyList<string>? excludedFolderIds)
    {
        if (!WorkspaceFolderGrantEvaluator.IsNodeId(workspace.WorkspaceId) ||
            !WorkspaceFolderGrantEvaluator.IsNodeId(folderId) || workspace.Revision < 1 ||
            workspace.Nodes is null || workspace.Nodes.Length > MaximumWorkspaceNodes)
        {
            throw new ArgumentException("A valid workspace folder selection is required.", nameof(folderId));
        }

        var nodes = WorkspaceFolderGrantEvaluator.BuildNodeIndex(workspace);
        if (!nodes.TryGetValue(folderId!, out var folder) || folder.Kind != WorkspaceNodeKind.Folder)
        {
            throw new ArgumentException("The selected workspace folder is unavailable.", nameof(folderId));
        }

        var requestedExclusions = excludedFolderIds ?? Array.Empty<string>();
        if (requestedExclusions.Count > MaximumWorkspaceNodes)
        {
            throw new ArgumentException("The workspace folder exclusion set is too large.", nameof(excludedFolderIds));
        }

        var exclusions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var excludedId in requestedExclusions)
        {
            if (!WorkspaceFolderGrantEvaluator.IsNodeId(excludedId) || !exclusions.Add(excludedId) ||
                !nodes.TryGetValue(excludedId, out var excluded) || excluded.Kind != WorkspaceNodeKind.Folder ||
                string.Equals(excludedId, folderId, StringComparison.Ordinal))
            {
                throw new ArgumentException("A workspace exclusion must name a distinct live descendant folder.", nameof(excludedFolderIds));
            }
        }

        var childrenByParent = new Dictionary<string, List<WorkspaceNodeInfo>>(StringComparer.Ordinal);
        foreach (var node in workspace.Nodes)
        {
            if (node.ParentId is not { } parentId)
            {
                continue;
            }

            if (!childrenByParent.TryGetValue(parentId, out var children))
            {
                children = new List<WorkspaceNodeInfo>();
                childrenByParent.Add(parentId, children);
            }

            children.Add(node);
        }

        var folderIds = new List<string>();
        var members = new List<WorkspaceFolderScopeMember>();
        var excludedFolders = 0;
        var excludedFiles = 0;
        var encounteredExclusions = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<(WorkspaceNodeInfo Node, bool Excluded)>();
        pending.Push((folder, false));
        while (pending.Count > 0)
        {
            var (node, excludedAncestor) = pending.Pop();
            var excluded = excludedAncestor || exclusions.Contains(node.Id);
            if (exclusions.Contains(node.Id))
            {
                encounteredExclusions.Add(node.Id);
            }

            if (excluded)
            {
                if (node.Kind == WorkspaceNodeKind.Folder)
                {
                    excludedFolders++;
                }
                else
                {
                    excludedFiles++;
                }
            }
            else if (node.Kind == WorkspaceNodeKind.Folder)
            {
                folderIds.Add(node.Id);
            }
            else
            {
                var pinned = node.Revisions?.FirstOrDefault(revision => revision.Revision == node.ContentRevision)
                    ?? throw new InvalidDataException("A workspace file is missing its current retained revision.");
                members.Add(new WorkspaceFolderScopeMember(
                    node.Id,
                    pinned.Revision,
                    pinned.Length,
                    pinned.Sha256,
                    node.AccessLifecycleGeneration));
            }

            if (childrenByParent.TryGetValue(node.Id, out var children))
            {
                foreach (var child in children)
                {
                    pending.Push((child, excluded));
                }
            }
        }

        if (encounteredExclusions.Count != exclusions.Count)
        {
            throw new ArgumentException("A workspace exclusion must name a distinct live descendant folder.", nameof(excludedFolderIds));
        }

        folderIds.Sort(StringComparer.Ordinal);
        members.Sort((left, right) => string.CompareOrdinal(left.FileId, right.FileId));
        var sortedExclusions = exclusions.Order(StringComparer.Ordinal).ToArray();
        return new Selection(
            workspace.WorkspaceId,
            folderId!,
            workspace.Revision,
            folderIds.ToArray(),
            members.ToArray(),
            sortedExclusions,
            excludedFolders,
            excludedFiles);
    }


    private static bool SelectionMatches(WorkspaceFolderGrantPreview preview, Selection selection) =>
        preview.TreeRevision == selection.TreeRevision &&
        string.Equals(preview.WorkspaceId, selection.WorkspaceId, StringComparison.Ordinal) &&
        string.Equals(preview.FolderId, selection.FolderId, StringComparison.Ordinal) &&
        preview.FolderIds.SequenceEqual(selection.FolderIds, StringComparer.Ordinal) &&
        preview.ExcludedFolderIds.SequenceEqual(selection.ExcludedFolderIds, StringComparer.Ordinal) &&
        preview.Members.SequenceEqual(selection.Members) &&
        preview.ExcludedFolderCount == selection.ExcludedFolderCount &&
        preview.ExcludedFileCount == selection.ExcludedFileCount;

    private static WorkspaceFolderGrantSummary Summarize(
        PersistedScopeGrant grant,
        OwnerScopeGrantState state,
        WorkspaceSnapshot? workspace)
    {
        var scope = grant.WorkspaceFolderScope!;
        Dictionary<string, WorkspaceNodeInfo>? nodes = workspace is null
            ? null
            : WorkspaceFolderGrantEvaluator.BuildNodeIndex(workspace);
        var grantActive = IsActiveGrant(state, grant, grant.ScopeSemanticsVersion, grant.ScopeSemanticsGeneration);
        var exclusions = new HashSet<string>(scope.ExcludedFolderIds, StringComparer.Ordinal);
        var members = scope.Members.Select(member =>
        {
            var currentRevision = nodes is not null && nodes.TryGetValue(member.FileId, out var node) &&
                node.Kind == WorkspaceNodeKind.File
                ? node.ContentRevision
                : (long?)null;
            var available = grantActive && nodes is not null &&
                WorkspaceFolderGrantEvaluator.IsMemberLive(nodes, scope, member, exclusions);
            return new WorkspaceFolderMemberInspection(
                member.FileId,
                member.ContentRevision,
                currentRevision,
                member.Length,
                member.Sha256,
                currentRevision is null || currentRevision.Value != member.ContentRevision,
                available);
        }).ToArray();
        return new WorkspaceFolderGrantSummary(
            grant.GrantId,
            scope.WorkspaceId,
            scope.FolderId,
            scope.TreeRevision,
            members,
            (string[])scope.ExcludedFolderIds.Clone(),
            (string[])grant.Operations.Clone(),
            grant.CreatedAtUtc,
            grant.ExpiresAtUtc,
            grant.Egress is null ? null : grant.Egress with { },
            grant.PolicyGeneration,
            grant.ScopeSemanticsVersion,
            grant.ScopeSemanticsGeneration,
            grant.RevokedAtUtc);
    }

    private static string[] NormalizeOperations(IReadOnlyList<string>? operations)
    {
        var requested = operations is { Count: > 0 } ? operations : new[] { WorkspaceFileReadOperation };
        var normalized = requested.Order(StringComparer.Ordinal).ToArray();
        if (normalized.Any(operation =>
                !string.Equals(operation, WorkspaceFileReadOperation, StringComparison.Ordinal) &&
                !string.Equals(operation, "provider.egress", StringComparison.Ordinal)) ||
            normalized.Distinct(StringComparer.Ordinal).Count() != normalized.Length)
        {
            throw new ArgumentException("The workspace folder grant contains unsupported or duplicate operations.", nameof(operations));
        }

        return normalized;
    }

    private static void ValidateExpiry(DateTimeOffset expiresAtUtc)
    {
        if (expiresAtUtc.Offset != TimeSpan.Zero || expiresAtUtc <= DateTimeOffset.UtcNow)
        {
            throw new ArgumentException("Grant expiry must be a future UTC instant.", nameof(expiresAtUtc));
        }
    }

    private static WorkspaceFolderGrantPreview ClonePreview(WorkspaceFolderGrantPreview preview) => preview with
    {
        FolderIds = (string[])preview.FolderIds.Clone(),
        Members = preview.Members.Select(member => member with { }).ToArray(),
        ExcludedFolderIds = (string[])preview.ExcludedFolderIds.Clone(),
        Operations = (string[])preview.Operations.Clone(),
        Egress = preview.Egress is null ? null : preview.Egress with { }
    };

    private static PersistedScopeGrant CloneGrant(PersistedScopeGrant grant) => grant with
    {
        Resources = grant.Resources.Select(resource => resource with { }).ToArray(),
        Operations = (string[])grant.Operations.Clone(),
        DestinationResources = grant.DestinationResources.Select(resource => resource with { }).ToArray(),
        WorkspaceFolderScope = grant.WorkspaceFolderScope is null
            ? null
            : WorkspaceFolderGrantEvaluator.CloneScope(grant.WorkspaceFolderScope),
        Egress = grant.Egress is null ? null : grant.Egress with { }
    };

    private static string EncodeBase64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static void EnsureOwner(AuthenticatedContext actor)
    {
        if (actor.Kind != PrincipalKind.Owner)
        {
            throw new UnauthorizedAccessException("Workspace folder grant management requires the authenticated owner workflow.");
        }
    }
}
