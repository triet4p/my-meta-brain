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
            return true;
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

            return _store.LoadScopeGrantState(_manifest, _dataKey);
        }
    }

    private void SaveScopeGrantState(OwnerScopeGrantState state)
    {
        lock (_contentGate)
        {
            if (_manifest is null || _dataKey is null)
            {
                throw new ManagedResourceUnavailableException();
            }

            _store.SaveScopeGrantState(_manifest, _dataKey, state);
        }
    }

    private byte[] ReadContent(string resourceId)
    {
        lock (_contentGate)
        {
            if (_manifest is null || _dataKey is null)
            {
                throw new ManagedResourceUnavailableException();
            }

            return _store.ReadResource(_manifest, _dataKey, resourceId);
        }
    }

    private void ValidateRegistration(string resourceId)
    {
        lock (_contentGate)
        {
            if (_manifest is null)
            {
                throw new ManagedResourceUnavailableException();
            }

            _store.ValidateRegistration(_manifest, resourceId);
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
        public void SaveScopeGrantState(OwnerScopeGrantState state) => GetOwner().SaveScopeGrantState(state);
        public bool TryGetZone(string resourceId, out string? zoneId) => GetOwner().TryGetZone(resourceId, out zoneId);
        public byte[] ReadContent(string resourceId) => GetOwner().ReadContent(resourceId);
        public void ValidateRegistration(string resourceId) => GetOwner().ValidateRegistration(resourceId);
        public long WriteContent(string resourceId, string zoneId, byte[] content) => GetOwner().WriteContent(resourceId, zoneId, content);

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.ReleaseOperation();
        }

        private VaultLifecycle GetOwner() =>
            _owner ?? throw new ObjectDisposedException(nameof(Operation));
    }
}
