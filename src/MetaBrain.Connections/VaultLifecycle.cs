using System.Security.Cryptography;
using MetaBrain.Core.Security;

namespace MetaBrain.Connections;

internal sealed class VaultLifecycle : IVaultLifecycle, IDisposable
{
    private readonly object _stateGate = new();
    private readonly object _contentGate = new();
    private readonly EncryptedVaultStore _store;
    private byte[]? _dataKey;
    private VaultManifest? _manifest;
    private int _activeOperations;
    private bool _locking;
    private TaskCompletionSource? _operationsDrained;
    private CancellationTokenSource? _lockCancellation;
    private CancellationTokenSource _refreshCancellation = new();
    private long _lockEpoch;

    public VaultLifecycle(EncryptedVaultStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
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
            if (_dataKey is not null || _locking || _activeOperations != 0 || _store.Exists)
            {
                return false;
            }

            var material = _store.Create(passphrase, Array.Empty<VaultResourceSeed>());
            _dataKey = material.DataKey;
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
            if (_locking || _activeOperations != 0)
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
            _manifest = manifest;
            try
            {
                // Crash/interruption recovery on every successful unlock:
                // staged-but-unpublished ciphertext and atomic-write temp
                // files are never visible to readers; reaping them here keeps
                // no half-published state across restarts.
                _store.ReconcileUnpublished(manifest);
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
            if (_dataKey is null || _locking)
            {
                return null;
            }

            _activeOperations++;
            return new Operation(this);
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

    public void Dispose()
    {
        LockAsync().GetAwaiter().GetResult();
    }

    private bool TryGetZone(string resourceId, out string? zoneId)
    {
        lock (_contentGate)
        {
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
            if (_manifest is null || _dataKey is null)
            {
                throw new ManagedResourceUnavailableException();
            }

            return SourceEnrollmentAuthority.ValidateAndNormalizeState(
                _store.LoadSourceEnrollmentState(_manifest, _dataKey));
        }
    }

    private T UpdateSourceEnrollmentState<T>(
        Func<SourceEnrollmentState, (SourceEnrollmentState State, T Result)> update)
    {
        lock (_contentGate)
        {
            if (_manifest is null || _dataKey is null)
            {
                throw new ManagedResourceUnavailableException();
            }

            var state = SourceEnrollmentAuthority.ValidateAndNormalizeState(
                _store.LoadSourceEnrollmentState(_manifest, _dataKey));
            var (nextState, result) = update(state);
            if (!ReferenceEquals(nextState, state))
            {
                nextState = SourceEnrollmentAuthority.ValidateAndNormalizeState(nextState);
                _store.SaveSourceEnrollmentState(_manifest, _dataKey, nextState);
            }

            return result;
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

    private bool DeleteCanonicalResource(string resourceId)
    {
        lock (_contentGate)
        {
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

    private byte[] ReadContent(string resourceId, long? expectedRevision = null)
    {
        lock (_contentGate)
        {
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

    private void ClearUnlockedState()
    {
        if (_dataKey is not null)
        {
            CryptographicOperations.ZeroMemory(_dataKey);
            _dataKey = null;
        }

        _manifest = null;
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
        public bool DeleteCanonicalResource(string resourceId) => GetOwner().DeleteCanonicalResource(resourceId);
        public bool TryGetZone(string resourceId, out string? zoneId) => GetOwner().TryGetZone(resourceId, out zoneId);
        public byte[] ReadContent(string resourceId, long? expectedRevision = null) => GetOwner().ReadContent(resourceId, expectedRevision);
        public byte[][] ReadBatch(IReadOnlyList<ScopeResourceRevision> batch, int maxDegreeOfParallelism, CancellationToken cancellationToken) => GetOwner().ReadBatch(batch, maxDegreeOfParallelism, cancellationToken);
        public void ValidateRegistration(string resourceId, long? expectedRevision = null) => GetOwner().ValidateRegistration(resourceId, expectedRevision);
        public long WriteContent(string resourceId, string zoneId, byte[] content) => GetOwner().WriteContent(resourceId, zoneId, content);
        public long WriteContent(string resourceId, string zoneId, byte[] content, long expectedRevision) => GetOwner().WriteContent(resourceId, zoneId, content, expectedRevision);

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.ReleaseOperation();
        }

        private VaultLifecycle GetOwner() =>
            _owner ?? throw new ObjectDisposedException(nameof(Operation));
    }
}
