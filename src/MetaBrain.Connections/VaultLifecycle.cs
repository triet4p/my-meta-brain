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
                BumpUnlockEpochLocked();
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

            var next = _store.WriteResource(_manifest, _dataKey, resourceId, zoneId, content);
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
            var grants = persistedState.SchemaVersion == 2
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
        var drained = _operationsDrained;
        _operationsDrained = null;
        drained?.TrySetResult();
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
        public bool TryGetZone(string resourceId, out string? zoneId) => GetOwner().TryGetZone(resourceId, out zoneId);
        public byte[] ReadContent(string resourceId, long? expectedRevision = null) => GetOwner().ReadContent(resourceId, expectedRevision);
        public void ValidateRegistration(string resourceId, long? expectedRevision = null) => GetOwner().ValidateRegistration(resourceId, expectedRevision);
        public long WriteContent(string resourceId, string zoneId, byte[] content) => GetOwner().WriteContent(resourceId, zoneId, content);

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.ReleaseOperation();
        }

        private VaultLifecycle GetOwner() =>
            _owner ?? throw new ObjectDisposedException(nameof(Operation));
    }
}
