using System.Security.Cryptography;
using System.Text;
using MetaBrain.Core.Domain;
using MetaBrain.Core.Index;
using MetaBrain.Core.Security;

namespace MetaBrain.Connections;

/// <summary>
/// S12-T5 what this live installation can contribute to one restore. A
/// restore of a backup of this same vault is refused while the live vault is
/// locked, because the encrypted purge ledger cannot be read without the data
/// key; an unrelated backup or a not-yet-created live vault contributes no
/// guard. The unlocked same-vault case carries the ledger and consumed-token
/// state the restore unions.
/// </summary>
internal sealed record VaultRestoreGuards(
    bool SameVault,
    bool Locked,
    OwnerPurgeLedgerState? PurgeLedger,
    OwnerScopeGrantState? ScopeGrants);

internal sealed class VaultLifecycle : IVaultLifecycle, IDisposable, IPrivateIndexKeyProvider
{
    private static readonly byte[] IndexKeyInfo = Encoding.UTF8.GetBytes("MetaBrain|private-index|v1");

    private readonly object _stateGate = new();
    private readonly object _contentGate = new();
    private readonly EncryptedVaultStore _store;
    private readonly EncryptedWorkspaceStore _workspaceStore;
    private byte[]? _dataKey;
    private byte[]? _indexKey;
    private VaultManifest? _manifest;
    private WorkspaceStateRecord? _workspace;
    private string? _workspaceUnavailableCode;
    private int _activeOperations;
    private bool _locking;
    private bool _maintenance;
    private TaskCompletionSource? _operationsDrained;
    private CancellationTokenSource? _lockCancellation;
    private CancellationTokenSource _refreshCancellation = new();
    private long _lockEpoch;
    private volatile bool _sourceEnrollmentRecoveryRequired;

    public VaultLifecycle(EncryptedVaultStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _workspaceStore = new EncryptedWorkspaceStore(_store.RootPath);
    }

    public string State
    {
        get
        {
            lock (_stateGate)
            {
                if (_locking)
                {
                    return "locking";
                }

                if (_maintenance)
                {
                    return "upgrading";
                }

                return _dataKey is not null ? "unlocked" : _store.Exists ? "locked" : "uninitialized";
            }
        }
    }

    public bool Provision(string passphrase, out string? recoveryCode)
    {
        ArgumentNullException.ThrowIfNull(passphrase);
        lock (_stateGate)
        {
            recoveryCode = null;
            if (_dataKey is not null || _locking || _maintenance || _activeOperations != 0 || _store.Exists)
            {
                return false;
            }

            var material = _store.Create(passphrase, Array.Empty<VaultResourceSeed>());
            _dataKey = material.DataKey;
            _indexKey = DeriveIndexKey(_dataKey);
            _manifest = material.Manifest;
            recoveryCode = material.RecoveryCode;
            ResetRefreshSignalLocked();
            _lockEpoch++;
            return true;
        }
    }

    public bool Unlock(string credential, bool useRecoveryCode)
    {
        ArgumentNullException.ThrowIfNull(credential);
        lock (_stateGate)
        {
            if (_locking || _maintenance || _activeOperations != 0)
            {
                return false;
            }

            if (_dataKey is not null)
            {
                return true;
            }

            if (!_store.TryUnlock(credential, useRecoveryCode, out var key, out var manifest) || key is null || manifest is null)
            {
                return false;
            }

            _dataKey = key;
            _indexKey = DeriveIndexKey(_dataKey);
            _manifest = manifest;
            try
            {
                // Crash/interruption recovery on every successful unlock:
                // staged-but-unpublished ciphertext and atomic-write temp
                // files are never visible to readers; reaping them here keeps
                // no half-published state across restarts.
                _store.ReconcileUnpublished(manifest);
                OpenWorkspaceLocked();
                RecoverPendingFolderRegistrations();
                BumpUnlockEpochLocked();
                ResetRefreshSignalLocked();
                _lockEpoch++;
                return true;
            }
            catch
            {
                ClearUnlockedState();
                throw;
            }
        }
    }

    public Task LockAsync()
    {
        lock (_stateGate)
        {
            if (_dataKey is null)
            {
                return Task.CompletedTask;
            }

            _locking = true;
            // A requested lock stops in-flight index-rebuild reads at their next
            // batch boundary instead of letting them run to completion: the
            // signal is observed by VaultLifecycle.ReadBatch (the only batch
            // path, used exclusively by the index rebuild), so the drain that
            // follows clears the key after only the bounded active I/O
            // finishes. Single reads, writes, and every non-index operation keep
            // their existing drain-only semantics. S3-T6 extends the same
            // requested-lock signal to the owner-opt-in background refresh loop:
            // cancelling the refresh signal stops an in-flight refresh pass at
            // its next per-source boundary; the refresh loop observes no other
            // lock state and holds no plaintext across the boundary.
            lock (_contentGate)
            {
                _lockCancellation ??= new CancellationTokenSource();
                _lockCancellation.Cancel();
            }
            _refreshCancellation.Cancel();

            if (_activeOperations == 0)
            {
                ClearUnlockedState();
                return Task.CompletedTask;
            }

            _operationsDrained ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return _operationsDrained.Task;
        }
    }

    public IVaultOperation? TryBeginOperation()
    {
        lock (_stateGate)
        {
            if (_dataKey is null || _locking || _maintenance || _sourceEnrollmentRecoveryRequired)
            {
                return null;
            }

            _activeOperations++;
            return new Operation(this);
        }
    }

    /// <summary>
    /// S8-T3 the exclusive maintenance lease for the owner schema upgrade and
    /// rollback. It is available only while the vault is locked, no operation
    /// is in flight, no lock is draining and no other maintenance act is
    /// running, so the migration is the only writer for its whole duration;
    /// meanwhile provision, unlock and every private operation refuse. The
    /// vault is never unlocked by the lease: the migration opens the key
    /// itself and clears it before the lease ends.
    /// </summary>
    public IDisposable? TryBeginMaintenance()
    {
        lock (_stateGate)
        {
            if (_dataKey is not null || _locking || _maintenance || _activeOperations != 0 || !_store.Exists)
            {
                return null;
            }

            _maintenance = true;
            return new Maintenance(this);
        }
    }

    public CancellationToken RefreshCancellation
    {
        get
        {
            lock (_stateGate)
            {
                return _refreshCancellation.Token;
            }
        }
    }

    public long LockEpoch
    {
        get
        {
            lock (_stateGate)
            {
                return _lockEpoch;
            }
        }
    }

    /// <summary>
    /// S8-T2 the identity of the unlocked vault. It is the same identity the
    /// encrypted envelopes are bound to, so a restored vault keeps the
    /// binding of every record it carries.
    /// </summary>
    internal string CurrentVaultId
    {
        get
        {
            lock (_contentGate)
            {
                return _manifest?.VaultId ?? string.Empty;
            }
        }
    }

    /// <summary>
    /// S8-T2 the live manifest revision. It is the revision of the manifest
    /// that names the records a snapshot captured.
    /// </summary>
    internal long CurrentManifestRevision
    {
        get
        {
            lock (_contentGate)
            {
                return _manifest?.Revision ?? 0;
            }
        }
    }

    /// <summary>
    /// S8-T2 captures a snapshot under the same gate every read and every
    /// write takes, so the capture is one point in time: no write can move a
    /// record between the moment the manifest is listed and the moment its
    /// bytes are read. A write that arrives during the capture waits for it
    /// and then commits normally, so the snapshot is consistent and the
    /// waiting write is neither lost nor half applied.
    /// </summary>
    internal T CaptureSnapshot<T>(Func<T> capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        lock (_contentGate)
        {
            return capture();
        }
    }

    /// <summary>
    /// S8-T2 a copy of the unlocked data key for the one act that needs it
    /// outside the store: sealing an encrypted snapshot of the vault. The copy
    /// never leaves the process except as the key of the snapshot ciphertext,
    /// and the caller owns it and must clear it.
    /// </summary>
    internal bool TryExportBackupKey(out byte[] dataKey)
    {
        lock (_contentGate)
        {
            if (_dataKey is null)
            {
                dataKey = Array.Empty<byte>();
                return false;
            }

            dataKey = (byte[])_dataKey.Clone();
            return true;
        }
    }

    public void Dispose()
    {
        LockAsync().GetAwaiter().GetResult();
    }

    /// <summary>
    /// S12-T5 reads the anti-resurrection state a restore must not roll back:
    /// the durable purge ledger and the consumed-token verifiers of the live
    /// installation. An unlocked vault answers only for the same vault id, so
    /// a backup of one installation is never governed by another
    /// installation's deletions. A locked vault cannot decrypt its own ledger;
    /// it still names the same vault through the public header, so the restore
    /// refuses instead of materializing data the live installation has purged
    /// since the backup was taken.
    /// </summary>
    internal VaultRestoreGuards ReadRestoreGuards(string vaultId)
    {
        lock (_contentGate)
        {
            if (_manifest is null || _dataKey is null)
            {
                var sameLockedVault = _store.TryReadVaultId(out var lockedVaultId) &&
                    string.Equals(lockedVaultId, vaultId, StringComparison.Ordinal);
                return new VaultRestoreGuards(sameLockedVault, sameLockedVault, null, null);
            }

            if (!string.Equals(_manifest.VaultId, vaultId, StringComparison.Ordinal))
            {
                return new VaultRestoreGuards(false, false, null, null);
            }

            return new VaultRestoreGuards(
                true,
                false,
                OwnerPurgeLedgerAuthority.ValidateAndNormalizeState(
                    _store.LoadPurgeLedgerState(_manifest, _dataKey)),
                OwnerScopeGrantAuthority.ValidateAndMigrateState(
                    _store.LoadScopeGrantState(_manifest, _dataKey)));
        }
    }

    private bool TryGetZone(string resourceId, out string? zoneId)
    {
        lock (_contentGate)
        {
            EnsureSourceEnrollmentRecoveryComplete();
            if (_manifest is null)
            {
                zoneId = null;
                return false;
            }

            return _store.TryGetZone(_manifest, resourceId, out zoneId);
        }
    }

    private IReadOnlyList<ScopeResourceRevision> ListResources()
    {
        lock (_contentGate)
        {
            EnsureSourceEnrollmentRecoveryComplete();
            if (_manifest is null)
            {
                throw new ManagedResourceUnavailableException();
            }

            return _store.ListResources(_manifest);
        }
    }

    private OwnerScopeGrantState LoadScopeGrantState()
    {
        lock (_contentGate)
        {
            if (_manifest is null || _dataKey is null)
            {
                throw new ManagedResourceUnavailableException();
            }

            return OwnerScopeGrantAuthority.ValidateAndMigrateState(
                _store.LoadScopeGrantState(_manifest, _dataKey));
        }
    }

    private T UpdateScopeGrantState<T>(
        Func<OwnerScopeGrantState, (OwnerScopeGrantState State, T Result)> update)
    {
        lock (_contentGate)
        {
            if (_manifest is null || _dataKey is null)
            {
                throw new ManagedResourceUnavailableException();
            }

            var state = OwnerScopeGrantAuthority.ValidateAndMigrateState(
                _store.LoadScopeGrantState(_manifest, _dataKey));
            var (nextState, result) = update(state);
            if (!ReferenceEquals(nextState, state))
            {
                nextState = OwnerScopeGrantAuthority.ValidateAndMigrateState(nextState);
                _store.SaveScopeGrantState(_manifest, _dataKey, nextState);
            }

            return result;
        }
    }

    private OwnerCatalogState LoadCatalogState()
    {
        lock (_contentGate)
        {
            if (_manifest is null || _dataKey is null)
            {
                throw new ManagedResourceUnavailableException();
            }

            return OwnerCatalogAuthority.ValidateAndNormalizeState(
                _store.LoadCatalogState(_manifest, _dataKey));
        }
    }

    private T UpdateCatalogState<T>(
        Func<OwnerCatalogState, (OwnerCatalogState State, T Result)> update)
    {
        lock (_contentGate)
        {
            if (_manifest is null || _dataKey is null)
            {
                throw new ManagedResourceUnavailableException();
            }

            var state = OwnerCatalogAuthority.ValidateAndNormalizeState(
                _store.LoadCatalogState(_manifest, _dataKey));
            var (nextState, result) = update(state);
            if (!ReferenceEquals(nextState, state))
            {
                nextState = OwnerCatalogAuthority.ValidateAndNormalizeState(nextState);
                _store.SaveCatalogState(_manifest, _dataKey, nextState);
            }

            return result;
        }
    }

    private SourceEnrollmentState LoadSourceEnrollmentState()
    {
        lock (_contentGate)
        {
            EnsureSourceEnrollmentRecoveryComplete();
            if (_manifest is null || _dataKey is null)
            {
                throw new ManagedResourceUnavailableException();
            }

            return SourceEnrollmentAuthority.ValidateAndNormalizeState(
                _store.LoadSourceEnrollmentState(_manifest, _dataKey));
        }
    }

    private T UpdateSourceEnrollmentState<T>(
        Func<SourceEnrollmentState, (SourceEnrollmentState State, T Result)> update) =>
        UpdateSourceEnrollmentStateWithRollback(update, rollback: null);
    private void BeginFolderRegistrationRecovery(IReadOnlyList<string> sourceIds)
    {
        ArgumentNullException.ThrowIfNull(sourceIds);
        var pendingIds = sourceIds.ToArray();
        if (pendingIds.Length is <= 0 or > SourceIngestionLimits.MaximumEnrollments)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceIds));
        }

        UpdateSourceEnrollmentState(state =>
        {
            if (state.PendingFolderSourceIds is { Length: > 0 })
            {
                throw new InvalidDataException("A folder registration recovery is already pending.");
            }

            return (state with { PendingFolderSourceIds = pendingIds }, true);
        });
    }

    private void CompleteFolderRegistrationRecovery()
    {
        UpdateSourceEnrollmentState(state =>
            (state with { PendingFolderSourceIds = Array.Empty<string>() }, true));
        _sourceEnrollmentRecoveryRequired = false;
    }

    private void RequireFolderRegistrationRecovery() =>
        _sourceEnrollmentRecoveryRequired = true;

    private T UpdateSourceEnrollmentStateWithRollback<T>(
        Func<SourceEnrollmentState, (SourceEnrollmentState State, T Result)> update,
        Action? rollback)
    {
        lock (_contentGate)
        {
            EnsureSourceEnrollmentRecoveryComplete();
            if (_manifest is null || _dataKey is null)
            {
                throw new ManagedResourceUnavailableException();
            }

            var state = SourceEnrollmentAuthority.ValidateAndNormalizeState(
                _store.LoadSourceEnrollmentState(_manifest, _dataKey));
            try
            {
                var (nextState, result) = update(state);
                if (!ReferenceEquals(nextState, state))
                {
                    nextState = SourceEnrollmentAuthority.ValidateAndNormalizeState(nextState);
                    _store.SaveSourceEnrollmentState(_manifest, _dataKey, nextState);
                    if (nextState.PendingFolderSourceIds is not { Length: > 0 })
                    {
                        _sourceEnrollmentRecoveryRequired = false;
                    }
                }

                return result;
            }
            catch
            {
                rollback?.Invoke();
                throw;
            }
        }
    }

    private void RecoverPendingFolderRegistrations()
    {
        lock (_contentGate)
        {
            _sourceEnrollmentRecoveryRequired = true;
            if (_manifest is null || _dataKey is null)
            {
                throw new ManagedResourceUnavailableException();
            }

            var state = SourceEnrollmentAuthority.ValidateAndNormalizeState(
                _store.LoadSourceEnrollmentState(_manifest, _dataKey));
            var pendingIds = state.PendingFolderSourceIds!;
            for (var index = pendingIds.Length - 1; index >= 0; index--)
            {
                if (_manifest.FindResource(pendingIds[index]) is not null)
                {
                    _manifest = _store.DeleteResource(_manifest, _dataKey, pendingIds[index]);
                }
            }

            if (pendingIds.Length > 0)
            {
                _store.SaveSourceEnrollmentState(
                    _manifest, _dataKey, state with { PendingFolderSourceIds = Array.Empty<string>() });
            }

            _sourceEnrollmentRecoveryRequired = false;
        }
    }

    private void EnsureSourceEnrollmentRecoveryComplete()
    {
        if (_sourceEnrollmentRecoveryRequired)
        {
            throw new ManagedResourceUnavailableException();
        }
    }
    private OwnerRecallPolicyState LoadRecallPolicyState()
    {
        lock (_contentGate)
        {
            if (_manifest is null || _dataKey is null)
            {
                throw new ManagedResourceUnavailableException();
            }

            return OwnerRecallPolicyAuthority.ValidateAndNormalizeState(
                _store.LoadRecallPolicyState(_manifest, _dataKey));
        }
    }

    private T UpdateRecallPolicyState<T>(
        Func<OwnerRecallPolicyState, (OwnerRecallPolicyState State, T Result)> update)
    {
        lock (_contentGate)
        {
            if (_manifest is null || _dataKey is null)
            {
                throw new ManagedResourceUnavailableException();
            }

            var state = OwnerRecallPolicyAuthority.ValidateAndNormalizeState(
                _store.LoadRecallPolicyState(_manifest, _dataKey));
            var (nextState, result) = update(state);
            if (!ReferenceEquals(nextState, state))
            {
                nextState = OwnerRecallPolicyAuthority.ValidateAndNormalizeState(nextState);
                _store.SaveRecallPolicyState(_manifest, _dataKey, nextState);
            }

            return result;
        }
    }
    private OwnerReviewState LoadReviewState()
    {
        lock (_contentGate)
        {
            if (_manifest is null || _dataKey is null)
            {
                throw new ManagedResourceUnavailableException();
            }

            return OwnerReviewAuthority.ValidateAndNormalizeState(
                _store.LoadReviewState(_manifest, _dataKey));
        }
    }

    private T UpdateReviewState<T>(
        Func<OwnerReviewState, (OwnerReviewState State, T Result)> update)
    {
        lock (_contentGate)
        {
            if (_manifest is null || _dataKey is null)
            {
                throw new ManagedResourceUnavailableException();
            }

            var state = OwnerReviewAuthority.ValidateAndNormalizeState(
                _store.LoadReviewState(_manifest, _dataKey));
            var (nextState, result) = update(state);
            if (!ReferenceEquals(nextState, state))
            {
                nextState = OwnerReviewAuthority.ValidateAndNormalizeState(nextState);
                _store.SaveReviewState(_manifest, _dataKey, nextState);
            }

            return result;
        }
    }

    private OwnerPurgeLedgerState LoadPurgeLedgerState()
    {
        lock (_contentGate)
        {
            if (_manifest is null || _dataKey is null)
            {
                throw new ManagedResourceUnavailableException();
            }

            return OwnerPurgeLedgerAuthority.ValidateAndNormalizeState(
                _store.LoadPurgeLedgerState(_manifest, _dataKey));
        }
    }

    private T UpdatePurgeLedgerState<T>(
        Func<OwnerPurgeLedgerState, (OwnerPurgeLedgerState State, T Result)> update)
    {
        lock (_contentGate)
        {
            if (_manifest is null || _dataKey is null)
            {
                throw new ManagedResourceUnavailableException();
            }

            var state = OwnerPurgeLedgerAuthority.ValidateAndNormalizeState(
                _store.LoadPurgeLedgerState(_manifest, _dataKey));
            var (nextState, result) = update(state);
            if (!ReferenceEquals(nextState, state))
            {
                nextState = OwnerPurgeLedgerAuthority.ValidateAndNormalizeState(nextState);
                _store.SavePurgeLedgerState(_manifest, _dataKey, nextState);
            }

            return result;
        }
    }

    /// <summary>
    /// S8-T4 the private operations state: the owner audit trail, the error
    /// diagnostics, the retention choice and the purge tombstones. It lives in
    /// one dedicated encrypted file of the vault, so it exists only while the
    /// vault is unlocked and it is never a canonical resource.
    /// </summary>
    private OperationsAuditState LoadOperationsAuditState()
    {
        lock (_contentGate)
        {
            if (_manifest is null || _dataKey is null)
            {
                throw new ManagedResourceUnavailableException();
            }

            return OperationsAuditAuthority.ValidateAndNormalizeState(
                _store.LoadOperationsAuditState(_manifest, _dataKey));
        }
    }

    private T UpdateOperationsAuditState<T>(
        Func<OperationsAuditState, (OperationsAuditState State, T Result)> update)
    {
        lock (_contentGate)
        {
            if (_manifest is null || _dataKey is null)
            {
                throw new ManagedResourceUnavailableException();
            }

            var state = OperationsAuditAuthority.ValidateAndNormalizeState(
                _store.LoadOperationsAuditState(_manifest, _dataKey));
            var (nextState, result) = update(state);
            if (!ReferenceEquals(nextState, state))
            {
                nextState = OperationsAuditAuthority.ValidateAndNormalizeState(nextState);
                _store.SaveOperationsAuditState(_manifest, _dataKey, nextState);
            }

            return result;
        }
    }

    private bool DeleteCanonicalResource(string resourceId)
    {
        lock (_contentGate)
        {
            EnsureSourceEnrollmentRecoveryComplete();
            if (_manifest is null || _dataKey is null)
            {
                throw new ManagedResourceUnavailableException();
            }

            var exists = _manifest.Resources.Any(resource =>
                string.Equals(resource.ResourceId, resourceId, StringComparison.Ordinal));
            if (!exists)
            {
                return false;
            }

            _manifest = _store.DeleteResource(_manifest, _dataKey, resourceId);
            return true;
        }
    }

    /// <summary>
    /// S12-T2 opens the committed workspace for one unlocked epoch and
    /// reclaims only files an interrupted commit staged but never published. A
    /// workspace that cannot be authenticated keeps every file untouched: the
    /// epoch reports the refusal and each workspace act refuses, while the
    /// rest of the vault stays usable.
    /// </summary>
    private void OpenWorkspaceLocked()
    {
        _workspace = null;
        _workspaceUnavailableCode = null;
        if (_manifest is null || _dataKey is null)
        {
            return;
        }

        var result = _workspaceStore.Open(_manifest.VaultId, _dataKey);
        _workspace = result.State;
        _workspaceUnavailableCode = result.ErrorCode;
        if (_workspace is null || result.ErrorCode is not null)
        {
            return;
        }

        // The purge ledger is the one authority for purged identities. A
        // confirmed workspace purge writes it before the workspace commit, so
        // an interrupted commit leaves a reservation the next unlock finishes:
        // the reserved nodes and their anchors leave the served state and their
        // ciphertext is reclaimed by this commit. A ledger that cannot be
        // validated refuses workspace acts instead of serving a state whose
        // deletion record is unknown; the vault itself still unlocks.
        try
        {
            var reserved = OwnerPurgeLedgerAuthority.ReservedWorkspaceIdentifiers(
                OwnerPurgeLedgerAuthority.ValidateAndNormalizeState(
                    _store.LoadPurgeLedgerState(_manifest, _dataKey)));
            if (reserved.Count != 0)
            {
                _workspace = _workspaceStore.ApplyPurgedIdentifiers(
                    _manifest.VaultId, _dataKey, _workspace, reserved);
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or
            WorkspaceRefusalException)
        {
            _workspace = null;
            _workspaceUnavailableCode = WorkspaceErrorCodes.Unavailable;
        }
    }

    private void EnsureWorkspaceAvailableLocked()
    {
        EnsureSourceEnrollmentRecoveryComplete();
        if (_manifest is null || _dataKey is null)
        {
            throw new ManagedResourceUnavailableException();
        }

        if (_workspaceUnavailableCode is not null)
        {
            throw new WorkspaceRefusalException(_workspaceUnavailableCode);
        }
    }

    private WorkspaceStatusInfo ReadWorkspaceStatus()
    {
        lock (_contentGate)
        {
            EnsureWorkspaceAvailableLocked();
            if (_workspace is null)
            {
                return new WorkspaceStatusInfo(
                    WorkspaceStates.Uninitialized,
                    EncryptedWorkspaceStore.CurrentFormatVersion,
                    EncryptedWorkspaceStore.CurrentMetadataSchemaVersion,
                    0,
                    null,
                    0,
                    0,
                    0,
                    0);
            }

            var folders = 0;
            var files = 0;
            var revisions = 0;
            var deleted = 0;
            foreach (var node in _workspace.Nodes)
            {
                if (string.Equals(node.State, WorkspaceNodeStates.Deleted, StringComparison.Ordinal))
                {
                    deleted++;
                }

                if (string.Equals(node.Kind, EncryptedWorkspaceStore.FolderKind, StringComparison.Ordinal))
                {
                    if (!string.Equals(node.Id, _workspace.RootId, StringComparison.Ordinal) &&
                        !string.Equals(node.State, WorkspaceNodeStates.Deleted, StringComparison.Ordinal))
                    {
                        folders++;
                    }

                    continue;
                }

                if (string.Equals(node.State, WorkspaceNodeStates.Deleted, StringComparison.Ordinal))
                {
                    continue;
                }

                files++;
                revisions += node.Revisions.Length;
            }

            return new WorkspaceStatusInfo(
                WorkspaceStates.Ready,
                _workspace.FormatVersion,
                _workspace.MetadataSchemaVersion,
                _workspace.Revision,
                _workspace.RootId,
                folders,
                files,
                revisions,
                deleted,
                CountReservedWorkspaceIdentifiersLocked());
        }
    }

    private WorkspaceSnapshot? ReadWorkspaceTree()
    {
        lock (_contentGate)
        {
            EnsureWorkspaceAvailableLocked();
            if (_workspace is null)
            {
                return null;
            }

            return new WorkspaceSnapshot(
                _workspace.WorkspaceId,
                _workspace.FormatVersion,
                _workspace.MetadataSchemaVersion,
                _workspace.Revision,
                _workspace.RootId,
                _workspace.Nodes
                    .Where(node => !string.Equals(node.State, WorkspaceNodeStates.Deleted, StringComparison.Ordinal))
                    .Select(ToWorkspaceNodeInfo)
                    .ToArray());
        }
    }

    private static WorkspaceNodeInfo ToWorkspaceNodeInfo(WorkspaceNodeRecord node) =>
        new(
            node.Id,
            string.Equals(node.Kind, EncryptedWorkspaceStore.FileKind, StringComparison.Ordinal)
                ? WorkspaceNodeKind.File
                : WorkspaceNodeKind.Folder,
            node.ParentId,
            node.Name,
            node.Revision,
            node.ContentRevision,
            node.Revisions
                .Select(revision => new WorkspaceContentRevisionInfo(revision.Revision, revision.Length, revision.Sha256))
                .ToArray());

    private WorkspaceMutationInfo CreateWorkspaceFolder(string parentId, string name, long expectedTreeRevision)
    {
        lock (_contentGate)
        {
            EnsureWorkspaceAvailableLocked();
            var (state, resolvedParentId, expectedTreeRevisionValue) = PrepareWorkspaceMutationLocked(
                expectedTreeRevision, parentId, name, contentLength: 0);
            var next = _workspaceStore.CreateFolder(
                _manifest!.VaultId,
                _dataKey!,
                state,
                resolvedParentId,
                name,
                expectedTreeRevisionValue,
                out var created);
            _workspace = next;
            return new WorkspaceMutationInfo(created.Id, next.Revision, created.Revision, 0, 0, null);
        }
    }

    private WorkspaceMutationInfo CreateWorkspaceFile(
        string parentId,
        string name,
        byte[] content,
        long expectedTreeRevision)
    {
        lock (_contentGate)
        {
            EnsureWorkspaceAvailableLocked();
            var (state, resolvedParentId, expectedTreeRevisionValue) = PrepareWorkspaceMutationLocked(
                expectedTreeRevision, parentId, name, content.Length);
            var next = _workspaceStore.CreateFile(
                _manifest!.VaultId,
                _dataKey!,
                state,
                resolvedParentId,
                name,
                content,
                expectedTreeRevisionValue,
                out var created);
            _workspace = next;
            return DescribeWorkspaceMutation(created, next.Revision);
        }
    }

    private WorkspaceMutationInfo ReviseWorkspaceFile(string fileId, byte[] content, long expectedContentRevision)
    {
        lock (_contentGate)
        {
            EnsureWorkspaceAvailableLocked();
            if (_workspace is null)
            {
                throw new WorkspaceRefusalException(WorkspaceErrorCodes.NotFound);
            }

            var next = _workspaceStore.ReviseFile(
                _manifest!.VaultId,
                _dataKey!,
                _workspace,
                fileId,
                content,
                expectedContentRevision,
                out var revised);
            _workspace = next;
            return DescribeWorkspaceMutation(revised, next.Revision);
        }
    }

    private WorkspaceFileContent ReadWorkspaceFile(string fileId, long revision)
    {
        lock (_contentGate)
        {
            EnsureWorkspaceAvailableLocked();
            if (_workspace is null)
            {
                throw new WorkspaceRefusalException(WorkspaceErrorCodes.NotFound);
            }

            return _workspaceStore.ReadContent(_manifest!.VaultId, _dataKey!, _workspace, fileId, revision);
        }
    }

    private static WorkspaceMutationInfo DescribeWorkspaceMutation(WorkspaceNodeRecord node, long workspaceRevision)
    {
        var revision = node.Revisions.Length > 0 ? node.Revisions[^1] : null;
        return new WorkspaceMutationInfo(
            node.Id,
            workspaceRevision,
            node.Revision,
            node.ContentRevision,
            revision?.Length ?? 0,
            revision?.Sha256);
    }

    private WorkspaceMutationInfo RenameWorkspaceNode(
        string nodeId,
        string name,
        long expectedTreeRevision,
        long expectedNodeRevision)
    {
        lock (_contentGate)
        {
            EnsureWorkspaceAvailableLocked();
            if (_workspace is null)
            {
                throw new WorkspaceRefusalException(WorkspaceErrorCodes.NotFound);
            }

            var next = _workspaceStore.RenameNode(
                _manifest!.VaultId,
                _dataKey!,
                _workspace,
                nodeId,
                name,
                expectedTreeRevision,
                expectedNodeRevision,
                out var renamed);
            _workspace = next;
            return DescribeWorkspaceMutation(renamed, next.Revision);
        }
    }

    private WorkspaceMutationInfo MoveWorkspaceNode(
        string nodeId,
        string destinationParentId,
        long expectedTreeRevision,
        long expectedNodeRevision)
    {
        lock (_contentGate)
        {
            EnsureWorkspaceAvailableLocked();
            if (_workspace is null)
            {
                throw new WorkspaceRefusalException(WorkspaceErrorCodes.NotFound);
            }

            var next = _workspaceStore.MoveNode(
                _manifest!.VaultId,
                _dataKey!,
                _workspace,
                nodeId,
                string.Equals(destinationParentId, WorkspaceParentIds.Root, StringComparison.Ordinal)
                    ? _workspace.RootId
                    : destinationParentId,
                expectedTreeRevision,
                expectedNodeRevision,
                out var moved);
            _workspace = next;
            return DescribeWorkspaceMutation(moved, next.Revision);
        }
    }

    private WorkspaceCopyInfo CopyWorkspaceNode(
        string nodeId,
        string destinationParentId,
        string? name,
        long expectedTreeRevision)
    {
        lock (_contentGate)
        {
            EnsureWorkspaceAvailableLocked();
            if (_workspace is null)
            {
                throw new WorkspaceRefusalException(WorkspaceErrorCodes.NotFound);
            }

            var next = _workspaceStore.CopyNode(
                _manifest!.VaultId,
                _dataKey!,
                _workspace,
                nodeId,
                string.Equals(destinationParentId, WorkspaceParentIds.Root, StringComparison.Ordinal)
                    ? _workspace.RootId
                    : destinationParentId,
                name,
                expectedTreeRevision,
                out var copy);
            _workspace = next;
            return copy;
        }
    }

    private WorkspaceDeletePreviewInfo PreviewWorkspaceDelete(string nodeId)
    {
        lock (_contentGate)
        {
            EnsureWorkspaceAvailableLocked();
            if (_workspace is null)
            {
                throw new WorkspaceRefusalException(WorkspaceErrorCodes.NotFound);
            }

            return EncryptedWorkspaceStore.PreviewDelete(_workspace, nodeId);
        }
    }

    private WorkspaceDeleteInfo DeleteWorkspaceNode(
        string nodeId,
        long expectedTreeRevision,
        long expectedNodeRevision,
        string previewToken)
    {
        lock (_contentGate)
        {
            EnsureWorkspaceAvailableLocked();
            if (_workspace is null)
            {
                throw new WorkspaceRefusalException(WorkspaceErrorCodes.NotFound);
            }

            var next = _workspaceStore.DeleteNode(
                _manifest!.VaultId,
                _dataKey!,
                _workspace,
                nodeId,
                expectedTreeRevision,
                expectedNodeRevision,
                previewToken,
                out var deleted);
            _workspace = next;
            return deleted;
        }
    }

    private WorkspaceRestoreInfo RestoreWorkspaceNode(string nodeId, long expectedTreeRevision)
    {
        lock (_contentGate)
        {
            EnsureWorkspaceAvailableLocked();
            if (_workspace is null)
            {
                throw new WorkspaceRefusalException(WorkspaceErrorCodes.NotFound);
            }

            var next = _workspaceStore.RestoreNode(
                _manifest!.VaultId,
                _dataKey!,
                _workspace,
                nodeId,
                expectedTreeRevision,
                out var restored);
            _workspace = next;
            return restored;
        }
    }

    private WorkspaceTombstoneInfo[] ReadWorkspaceTombstones()
    {
        lock (_contentGate)
        {
            EnsureWorkspaceAvailableLocked();
            return _workspace is null
                ? Array.Empty<WorkspaceTombstoneInfo>()
                : EncryptedWorkspaceStore.ListTombstones(_workspace);
        }
    }

    /// <summary>
    /// S12-T5 previews one irreversible purge of an encrypted tombstone's
    /// whole deletion group under the current committed tree revision. It reads
    /// only; the token it returns binds the confirmation to exactly this state,
    /// its retained byte descriptors and its anchors.
    /// </summary>
    private WorkspacePurgePreviewInfo PreviewWorkspacePurge(string nodeId)
    {
        lock (_contentGate)
        {
            EnsureWorkspaceAvailableLocked();
            if (_workspace is null)
            {
                throw new WorkspaceRefusalException(WorkspaceErrorCodes.NotFound);
            }

            return EncryptedWorkspaceStore.PreviewPurge(_workspace, nodeId);
        }
    }

    /// <summary>
    /// S12-T5 commits one previewed purge: the tombstone group leaves the
    /// canonical state and its ciphertext becomes unreachable. The owner's
    /// reason and every purged identity are first written to the vault's one
    /// durable purge ledger — the same encrypted ledger canonical records and
    /// sessions use — so the reservation, the backup that carries it and the
    /// restore that re-applies it are a single mechanism instead of a second
    /// workspace-only watermark.
    /// </summary>
    private WorkspacePurgeInfo PurgeWorkspaceNode(
        string nodeId,
        string reason,
        long expectedTreeRevision,
        string previewToken)
    {
        lock (_contentGate)
        {
            EnsureWorkspaceAvailableLocked();
            if (_workspace is null)
            {
                throw new WorkspaceRefusalException(WorkspaceErrorCodes.NotFound);
            }

            if (_manifest is null || _dataKey is null)
            {
                throw new ManagedResourceUnavailableException();
            }

            var preview = EncryptedWorkspaceStore.PreviewPurge(_workspace, nodeId);
            if (!string.Equals(preview.Preview, previewToken, StringComparison.Ordinal) ||
                preview.TreeRevision != expectedTreeRevision)
            {
                throw new WorkspaceRefusalException(WorkspaceErrorCodes.Conflict);
            }

            string purgeId;
            try
            {
                purgeId = ReserveWorkspacePurgeLocked(preview, reason);
            }
            catch (InvalidDataException)
            {
                throw new WorkspaceRefusalException(WorkspaceErrorCodes.PurgeConfirmation);
            }

            try
            {
                var next = _workspaceStore.PurgeNode(
                    _manifest.VaultId,
                    _dataKey,
                    _workspace,
                    nodeId,
                    purgeId,
                    expectedTreeRevision,
                    previewToken,
                    out var purged);
                _workspace = next;
                return purged;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The ledger entry is already durable, so the identity can
                // never be resurrected. The next unlock re-applies it and
                // removes whatever this failed commit left behind.
                throw new WorkspaceRefusalException(WorkspaceErrorCodes.Unavailable);
            }
        }
    }

    /// <summary>
    /// Appends the canonical deletion record for one confirmed workspace purge
    /// before any workspace bytes are removed. A group larger than the ledger's
    /// per-intent bound is recorded as several intents with fresh IDs; every
    /// node identity is reserved exactly once either way. A group whose
    /// identities are already reserved by an interrupted act returns the
    /// existing reservation instead of inventing a second one.
    /// </summary>
    private string ReserveWorkspacePurgeLocked(WorkspacePurgePreviewInfo preview, string reason)
    {
        var validatedReason = OwnerPurgeLedgerAuthority.NormalizeReason(reason);
        var target = preview.Nodes.First(node =>
            string.Equals(node.NodeId, preview.NodeId, StringComparison.Ordinal));
        var purgeId = Guid.NewGuid().ToString("N");
        UpdatePurgeLedgerState(state =>
        {
            var validated = OwnerPurgeLedgerAuthority.ValidateAndNormalizeState(state);
            var reserved = OwnerPurgeLedgerAuthority.ReservedWorkspaceIdentifiers(validated);
            if (preview.Nodes.All(node => reserved.Contains(node.NodeId)))
            {
                return (validated, true);
            }

            var refs = preview.Nodes
                .Select(node => new PurgedResourceRef(node.NodeId, node.Revision))
                .ToArray();
            var now = DateTimeOffset.UtcNow;
            var next = validated;
            for (var offset = 0; offset < refs.Length; offset += OwnerPurgeLedgerAuthority.MaximumImpactedPerPurge)
            {
                var chunk = refs
                    .Skip(offset)
                    .Take(OwnerPurgeLedgerAuthority.MaximumImpactedPerPurge)
                    .ToArray();
                var intent = OwnerPurgeLedgerAuthority.CreateWorkspaceIntent(
                    offset == 0 ? purgeId : Guid.NewGuid().ToString("N"),
                    target.NodeId,
                    target.Revision,
                    target.Kind == WorkspaceNodeKind.Folder ? EncryptedWorkspaceStore.FolderKind : EncryptedWorkspaceStore.FileKind,
                    chunk,
                    validatedReason,
                    now,
                    preview.TreeRevision);
                next = OwnerPurgeLedgerAuthority.Append(next, intent);
            }

            return (next, true);
        });

        return purgeId;
    }

    /// <summary>
    /// The count of workspace identities the durable ledger reserved, for the
    /// owner status line. It reads the same ledger a purge writes and a restore
    /// re-applies, so the reported number never comes from a second watermark.
    /// </summary>
    private int CountReservedWorkspaceIdentifiersLocked()
    {
        if (_manifest is null || _dataKey is null)
        {
            return 0;
        }

        return OwnerPurgeLedgerAuthority.ReservedWorkspaceIdentifiers(
            OwnerPurgeLedgerAuthority.ValidateAndNormalizeState(
                _store.LoadPurgeLedgerState(_manifest, _dataKey))).Count;
    }

    /// <summary>
    /// S12-T5 captures the committed workspace, its tombstones, every retained
    /// original-byte revision and its anchors as one point-in-time plaintext
    /// copy for the encrypted backup. It must run inside
    /// <see cref="CaptureSnapshot{T}"/>; a workspace that cannot be
    /// authenticated refuses the backup instead of being silently omitted.
    /// The durable purge ledger travels in the same snapshot, so a restore sees
    /// exactly which identities this point in time had already purged.
    /// A vault that has never committed a workspace returns null.
    /// </summary>
    internal WorkspaceBackupSnapshot? CaptureWorkspaceBackup()
    {
        lock (_contentGate)
        {
            EnsureWorkspaceAvailableLocked();
            if (_manifest is null || _dataKey is null)
            {
                throw new ManagedResourceUnavailableException();
            }

            return _workspace is null
                ? null
                : _workspaceStore.CaptureBackup(_manifest.VaultId, _dataKey, _workspace);
        }
    }

    private WorkspaceAnchorInfo CreateWorkspaceAnchor(
        string fileId,
        long expectedContentRevision,
        long offset,
        long length,
        long expectedTreeRevision)
    {
        lock (_contentGate)
        {
            EnsureWorkspaceAvailableLocked();
            if (_workspace is null)
            {
                throw new WorkspaceRefusalException(WorkspaceErrorCodes.NotFound);
            }

            var next = _workspaceStore.CreateAnchor(
                _manifest!.VaultId,
                _dataKey!,
                _workspace,
                fileId,
                expectedContentRevision,
                offset,
                length,
                expectedTreeRevision,
                out var created);
            _workspace = next;
            return new WorkspaceAnchorInfo(
                created.Id,
                created.FileId,
                created.ContentRevision,
                created.Offset,
                created.Length,
                created.Sha256,
                created.State ?? WorkspaceAnchorStates.Live);
        }
    }

    private WorkspaceAnchorInfo[] ReadWorkspaceAnchors(string fileId)
    {
        lock (_contentGate)
        {
            EnsureWorkspaceAvailableLocked();
            if (_workspace is null)
            {
                throw new WorkspaceRefusalException(WorkspaceErrorCodes.NotFound);
            }

            return EncryptedWorkspaceStore.ReadAnchors(_workspace, fileId);
        }
    }

    /// <summary>
    /// S12-T4 previews one plaintext export of the selected live node. It
    /// resolves the scope and the pinned revisions from the one committed
    /// snapshot and validates the destination; it writes nothing and stages no
    /// temporary plaintext.
    /// </summary>
    private WorkspaceExportPreviewInfo PreviewWorkspaceExport(string nodeId, long contentRevision, string destination)
    {
        lock (_contentGate)
        {
            EnsureWorkspaceAvailableLocked();
            if (_workspace is null)
            {
                throw new WorkspaceRefusalException(WorkspaceErrorCodes.NotFound);
            }

            return EncryptedWorkspaceStore.PreviewExport(_workspace, nodeId, contentRevision, destination);
        }
    }

    /// <summary>
    /// S12-T4 writes the previewed scope as original bytes outside the vault.
    /// The act runs against one committed snapshot under the content gate, so
    /// the revision the owner previewed is exactly the revision that is read
    /// and written, and the workspace itself is never modified.
    /// </summary>
    private WorkspaceExportInfo ExportWorkspaceNode(
        string nodeId,
        long contentRevision,
        string destination,
        string previewToken,
        CancellationToken cancellationToken)
    {
        lock (_contentGate)
        {
            EnsureWorkspaceAvailableLocked();
            if (_workspace is null)
            {
                throw new WorkspaceRefusalException(WorkspaceErrorCodes.NotFound);
            }

            return _workspaceStore.ExportNode(
                _manifest!.VaultId,
                _dataKey!,
                _workspace,
                nodeId,
                contentRevision,
                destination,
                previewToken,
                cancellationToken);
        }
    }

    /// <summary>
    /// Creates the empty tree a caller saw before the first commit (revision
    /// 0) so its precondition stays truthful, while every later act is gated
    /// by the committed tree revision. A caller that presents 0 after the
    /// workspace exists is stale and is refused by the store's revision gate,
    /// and the first act is validated before the empty tree is committed, so a
    /// refused act still leaves nothing behind.
    /// </summary>
    private (WorkspaceStateRecord State, string ParentId, long ExpectedTreeRevision) PrepareWorkspaceMutationLocked(
        long expectedTreeRevision,
        string parentId,
        string name,
        int contentLength)
    {
        if (_workspace is not null)
        {
            return (_workspace, ResolveWorkspaceParentId(_workspace, parentId), expectedTreeRevision);
        }

        if (expectedTreeRevision != 0)
        {
            throw new WorkspaceRefusalException(WorkspaceErrorCodes.Conflict);
        }

        var empty = EncryptedWorkspaceStore.CreateEmptyState();
        EncryptedWorkspaceStore.ValidateNewNode(
            empty, ResolveWorkspaceParentId(empty, parentId), name, contentLength, empty.Revision);
        _workspace = _workspaceStore.Initialize(_manifest!.VaultId, _dataKey!);
        return (_workspace, _workspace.RootId, _workspace.Revision);
    }

    private static string ResolveWorkspaceParentId(WorkspaceStateRecord state, string parentId) =>
        string.Equals(parentId, WorkspaceParentIds.Root, StringComparison.Ordinal) ? state.RootId : parentId;

    private byte[] ReadContent(string resourceId, long? expectedRevision = null)
    {
        lock (_contentGate)
        {
            EnsureSourceEnrollmentRecoveryComplete();
            if (_manifest is null || _dataKey is null)
            {
                throw new ManagedResourceUnavailableException();
            }

            return _store.ReadResource(_manifest, _dataKey, resourceId, expectedRevision);
        }
    }

    /// <summary>
    /// Reads a bounded batch of envelopes concurrently inside the current
    /// unlocked lease. The immutable manifest/dataKey references are snapshotted
    /// once under the content gate (guarding the locked state exactly like
    /// <see cref="ReadContent"/>), then the stateless per-file reads are
    /// overlapped; the batch is bounded so no more than the batch plaintext is
    /// resident. This stays inside the operation that authorized it, so lock's
    /// drain and key-clear boundaries are unchanged; the shared key is never
    /// zeroed here (it belongs to the lifecycle and survives until lock).
    /// When a lock has been requested while a rebuild is in flight, the
    /// request-token is linked with the lock signal so the rebuild stops at
    /// this batch boundary and the drain that clears the key is bounded by the
    /// active I/O instead of the whole remaining vault.
    /// </summary>
    public byte[][] ReadBatch(IReadOnlyList<ScopeResourceRevision> batch, int maxDegreeOfParallelism, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        VaultManifest manifest;
        byte[] key;
        CancellationTokenSource? lockCancellation;
        lock (_contentGate)
        {
            EnsureSourceEnrollmentRecoveryComplete();
            if (_manifest is null || _dataKey is null)
            {
                throw new ManagedResourceUnavailableException();
            }

            manifest = _manifest;
            key = _dataKey;
            lockCancellation = _lockCancellation;
        }

        if (lockCancellation is null)
        {
            return _store.ReadBatch(manifest, key, batch, maxDegreeOfParallelism, cancellationToken);
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lockCancellation.Token);
        return _store.ReadBatch(manifest, key, batch, maxDegreeOfParallelism, linked.Token);
    }

    private void ValidateRegistration(string resourceId, long? expectedRevision = null)
    {
        lock (_contentGate)
        {
            EnsureSourceEnrollmentRecoveryComplete();
            if (_manifest is null)
            {
                throw new ManagedResourceUnavailableException();
            }

            _store.ValidateRegistration(_manifest, resourceId, expectedRevision);
        }
    }

    private long WriteContent(string resourceId, string zoneId, byte[] content)
    {
        lock (_contentGate)
        {
            EnsureSourceEnrollmentRecoveryComplete();
            if (_manifest is null || _dataKey is null)
            {
                throw new ManagedResourceUnavailableException();
            }

            var current = _manifest.Resources.FirstOrDefault(resource =>
                string.Equals(resource.ResourceId, resourceId, StringComparison.Ordinal));
            var next = _store.WriteResource(_manifest, _dataKey, resourceId, zoneId, content, current?.Revision ?? 0);
            _manifest = next;
            return next.Resources.First(resource => string.Equals(resource.ResourceId, resourceId, StringComparison.Ordinal)).Revision;
        }
    }

    private long WriteContent(string resourceId, string zoneId, byte[] content, long expectedRevision)
    {
        lock (_contentGate)
        {
            EnsureSourceEnrollmentRecoveryComplete();
            if (_manifest is null || _dataKey is null)
            {
                throw new ManagedResourceUnavailableException();
            }

            var next = _store.WriteResource(_manifest, _dataKey, resourceId, zoneId, content, expectedRevision);
            _manifest = next;
            return next.Resources.First(resource => string.Equals(resource.ResourceId, resourceId, StringComparison.Ordinal)).Revision;
        }
    }

    private void ReleaseOperation()
    {
        lock (_stateGate)
        {
            if (_activeOperations <= 0)
            {
                throw new InvalidOperationException("Vault operation lease accounting failed.");
            }

            _activeOperations--;
            if (_activeOperations == 0 && _locking)
            {
                ClearUnlockedState();
            }
        }
    }

    private void BumpUnlockEpochLocked()
    {
        // Caller holds _stateGate; _contentGate serializes migration and epoch changes
        // with every grant update. Invalid state fails closed instead of resetting grants.
        lock (_contentGate)
        {
            if (_manifest is null || _dataKey is null)
            {
                return;
            }

            var persistedState = _store.LoadScopeGrantState(_manifest, _dataKey);
            var state = OwnerScopeGrantAuthority.ValidateAndMigrateState(persistedState);
            if (state.UnlockEpoch == long.MaxValue)
            {
                throw new IOException("The owner scope unlock epoch is exhausted.");
            }

            var nextEpoch = state.UnlockEpoch + 1;
            var grants = persistedState.SchemaVersion is 2 or 3
                ? state.Grants.Select(grant => grant with
                {
                    PolicyGeneration = state.PolicyGeneration,
                    UnlockEpoch = nextEpoch
                }).ToArray()
                : state.Grants;
            var nextState = state with { UnlockEpoch = nextEpoch, Grants = grants };
            _store.SaveScopeGrantState(_manifest, _dataKey, nextState);
        }
    }

    /// <summary>
    /// S8-T5: the key the private index is encrypted with. It is derived
    /// from the vault's own data key with a fixed label, so there is no
    /// separately stored key, no key file and no autonomous unlock: the key
    /// exists only inside an unlocked epoch and is cleared with it.
    /// <para>
    /// The bytes belong to the lifecycle. A caller may read them for the
    /// operation it is running and must not keep or modify them.
    /// </para>
    /// </summary>
    byte[]? IPrivateIndexKeyProvider.PrivateIndexKey
    {
        get
        {
            lock (_stateGate)
            {
                return _indexKey;
            }
        }
    }

    /// <summary>
    /// Derives the private index key from the vault data key. The derivation
    /// is a standard HKDF over the vault's own key material with a fixed
    /// label, so the index key is bound to this vault and to this purpose and
    /// is never the data key itself.
    /// </summary>
    private static byte[] DeriveIndexKey(byte[] dataKey) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, dataKey, 32, salt: null, info: IndexKeyInfo);

    private void ClearUnlockedState()
    {
        if (_dataKey is not null)
        {
            CryptographicOperations.ZeroMemory(_dataKey);
            _dataKey = null;
        }

        if (_indexKey is not null)
        {
            // S8-T5: the private index key leaves the process with the
            // unlocked epoch. A locked vault has no key for the index, so no
            // private index byte can be read.
            CryptographicOperations.ZeroMemory(_indexKey);
            _indexKey = null;
        }

        _manifest = null;
        // S12-T2: the workspace tree belongs to the unlocked epoch. It is
        // plaintext metadata in memory, so it leaves with the key; a locked
        // vault has no workspace tree, no workspace name and no workspace
        // revision to serve.
        _workspace = null;
        _workspaceUnavailableCode = null;
        _locking = false;
        // The lock signal belongs to this locked epoch: the drain that reached
        // this point has already stopped the in-flight batch reads, and no
        // operation remains (lock waits for _activeOperations == 0), so the
        // source can be reset for the next unlock epoch. S3-T6 keeps the
        // refresh-cancellation signal cancelled across the locked epoch: it is
        // reset only on the next successful provision/unlock, so a background
        // refresh loop that sampled the old token can never resume reading
        // with a stale lease after the key is cleared.
        lock (_contentGate)
        {
            var lockCancellation = _lockCancellation;
            _lockCancellation = null;
            lockCancellation?.Dispose();
        }

        _lockEpoch++;
        var drained = _operationsDrained;
        _operationsDrained = null;
        drained?.TrySetResult();
    }

    private void ResetRefreshSignalLocked()
    {
        var previous = _refreshCancellation;
        _refreshCancellation = new CancellationTokenSource();
        previous.Dispose();
    }

    private sealed class Maintenance : IDisposable
    {
        private VaultLifecycle? _owner;

        public Maintenance(VaultLifecycle owner) => _owner = owner;

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            if (owner is null)
            {
                return;
            }

            lock (owner._stateGate)
            {
                owner._maintenance = false;
            }
        }
    }

    private sealed class Operation : IVaultOperation
    {
        private VaultLifecycle? _owner;

        public Operation(VaultLifecycle owner) => _owner = owner;

        public IReadOnlyList<ScopeResourceRevision> ListResources() => GetOwner().ListResources();
        public OwnerScopeGrantState LoadScopeGrantState() => GetOwner().LoadScopeGrantState();
        public T UpdateScopeGrantState<T>(
            Func<OwnerScopeGrantState, (OwnerScopeGrantState State, T Result)> update) =>
            GetOwner().UpdateScopeGrantState(update);
        public OwnerCatalogState LoadCatalogState() => GetOwner().LoadCatalogState();
        public T UpdateCatalogState<T>(
            Func<OwnerCatalogState, (OwnerCatalogState State, T Result)> update) =>
            GetOwner().UpdateCatalogState(update);
        public SourceEnrollmentState LoadSourceEnrollmentState() => GetOwner().LoadSourceEnrollmentState();
        public T UpdateSourceEnrollmentState<T>(
            Func<SourceEnrollmentState, (SourceEnrollmentState State, T Result)> update) =>
            GetOwner().UpdateSourceEnrollmentState(update);
        public T UpdateSourceEnrollmentStateWithRollback<T>(
            Func<SourceEnrollmentState, (SourceEnrollmentState State, T Result)> update,
            Action rollback) =>
            GetOwner().UpdateSourceEnrollmentStateWithRollback(update, rollback);
        public void BeginFolderRegistrationRecovery(IReadOnlyList<string> sourceIds) =>
            GetOwner().BeginFolderRegistrationRecovery(sourceIds);
        public void CompleteFolderRegistrationRecovery() =>
            GetOwner().CompleteFolderRegistrationRecovery();
        public void RequireFolderRegistrationRecovery() =>
            GetOwner().RequireFolderRegistrationRecovery();
        public OwnerRecallPolicyState LoadRecallPolicyState() => GetOwner().LoadRecallPolicyState();
        public T UpdateRecallPolicyState<T>(
            Func<OwnerRecallPolicyState, (OwnerRecallPolicyState State, T Result)> update) =>
            GetOwner().UpdateRecallPolicyState(update);
        public OwnerReviewState LoadReviewState() => GetOwner().LoadReviewState();
        public T UpdateReviewState<T>(
            Func<OwnerReviewState, (OwnerReviewState State, T Result)> update) =>
            GetOwner().UpdateReviewState(update);
        public OwnerPurgeLedgerState LoadPurgeLedgerState() => GetOwner().LoadPurgeLedgerState();
        public T UpdatePurgeLedgerState<T>(
            Func<OwnerPurgeLedgerState, (OwnerPurgeLedgerState State, T Result)> update) =>
            GetOwner().UpdatePurgeLedgerState(update);
        public OperationsAuditState LoadOperationsAuditState() => GetOwner().LoadOperationsAuditState();
        public T UpdateOperationsAuditState<T>(
            Func<OperationsAuditState, (OperationsAuditState State, T Result)> update) =>
            GetOwner().UpdateOperationsAuditState(update);
        public bool DeleteCanonicalResource(string resourceId) => GetOwner().DeleteCanonicalResource(resourceId);
        public bool TryGetZone(string resourceId, out string? zoneId) => GetOwner().TryGetZone(resourceId, out zoneId);
        public byte[] ReadContent(string resourceId, long? expectedRevision = null) => GetOwner().ReadContent(resourceId, expectedRevision);
        public byte[][] ReadBatch(IReadOnlyList<ScopeResourceRevision> batch, int maxDegreeOfParallelism, CancellationToken cancellationToken) => GetOwner().ReadBatch(batch, maxDegreeOfParallelism, cancellationToken);
        public void ValidateRegistration(string resourceId, long? expectedRevision = null) => GetOwner().ValidateRegistration(resourceId, expectedRevision);
        public long WriteContent(string resourceId, string zoneId, byte[] content) => GetOwner().WriteContent(resourceId, zoneId, content);
        public long WriteContent(string resourceId, string zoneId, byte[] content, long expectedRevision) => GetOwner().WriteContent(resourceId, zoneId, content, expectedRevision);
        public WorkspaceStatusInfo ReadWorkspaceStatus() => GetOwner().ReadWorkspaceStatus();
        public WorkspaceSnapshot? ReadWorkspaceTree() => GetOwner().ReadWorkspaceTree();
        public WorkspaceMutationInfo CreateWorkspaceFolder(string parentId, string name, long expectedTreeRevision) =>
            GetOwner().CreateWorkspaceFolder(parentId, name, expectedTreeRevision);
        public WorkspaceMutationInfo CreateWorkspaceFile(string parentId, string name, byte[] content, long expectedTreeRevision) =>
            GetOwner().CreateWorkspaceFile(parentId, name, content, expectedTreeRevision);
        public WorkspaceMutationInfo ReviseWorkspaceFile(string fileId, byte[] content, long expectedContentRevision) =>
            GetOwner().ReviseWorkspaceFile(fileId, content, expectedContentRevision);
        public WorkspaceFileContent ReadWorkspaceFile(string fileId, long revision) =>
            GetOwner().ReadWorkspaceFile(fileId, revision);
        public WorkspaceMutationInfo RenameWorkspaceNode(string nodeId, string name, long expectedTreeRevision, long expectedNodeRevision) =>
            GetOwner().RenameWorkspaceNode(nodeId, name, expectedTreeRevision, expectedNodeRevision);
        public WorkspaceMutationInfo MoveWorkspaceNode(string nodeId, string destinationParentId, long expectedTreeRevision, long expectedNodeRevision) =>
            GetOwner().MoveWorkspaceNode(nodeId, destinationParentId, expectedTreeRevision, expectedNodeRevision);
        public WorkspaceCopyInfo CopyWorkspaceNode(string nodeId, string destinationParentId, string? name, long expectedTreeRevision) =>
            GetOwner().CopyWorkspaceNode(nodeId, destinationParentId, name, expectedTreeRevision);
        public WorkspaceDeletePreviewInfo PreviewWorkspaceDelete(string nodeId) =>
            GetOwner().PreviewWorkspaceDelete(nodeId);
        public WorkspaceDeleteInfo DeleteWorkspaceNode(string nodeId, long expectedTreeRevision, long expectedNodeRevision, string previewToken) =>
            GetOwner().DeleteWorkspaceNode(nodeId, expectedTreeRevision, expectedNodeRevision, previewToken);
        public WorkspaceRestoreInfo RestoreWorkspaceNode(string nodeId, long expectedTreeRevision) =>
            GetOwner().RestoreWorkspaceNode(nodeId, expectedTreeRevision);
        public WorkspaceTombstoneInfo[] ReadWorkspaceTombstones() => GetOwner().ReadWorkspaceTombstones();
        public WorkspacePurgePreviewInfo PreviewWorkspacePurge(string nodeId) =>
            GetOwner().PreviewWorkspacePurge(nodeId);
        public WorkspacePurgeInfo PurgeWorkspaceNode(string nodeId, string reason, long expectedTreeRevision, string previewToken) =>
            GetOwner().PurgeWorkspaceNode(nodeId, reason, expectedTreeRevision, previewToken);
        public WorkspaceAnchorInfo CreateWorkspaceAnchor(string fileId, long expectedContentRevision, long offset, long length, long expectedTreeRevision) =>
            GetOwner().CreateWorkspaceAnchor(fileId, expectedContentRevision, offset, length, expectedTreeRevision);
        public WorkspaceAnchorInfo[] ReadWorkspaceAnchors(string fileId) => GetOwner().ReadWorkspaceAnchors(fileId);
        public WorkspaceExportPreviewInfo PreviewWorkspaceExport(string nodeId, long contentRevision, string destination) =>
            GetOwner().PreviewWorkspaceExport(nodeId, contentRevision, destination);
        public WorkspaceExportInfo ExportWorkspaceNode(
            string nodeId,
            long contentRevision,
            string destination,
            string previewToken,
            CancellationToken cancellationToken) =>
            GetOwner().ExportWorkspaceNode(nodeId, contentRevision, destination, previewToken, cancellationToken);

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.ReleaseOperation();
        }

        private VaultLifecycle GetOwner()
        {
            var owner = _owner ?? throw new ObjectDisposedException(nameof(Operation));
            owner.EnsureSourceEnrollmentRecoveryComplete();
            return owner;
        }
    }
}
