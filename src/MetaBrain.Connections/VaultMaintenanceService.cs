using System.Security.Cryptography;
using MetaBrain.Core.Security;

namespace MetaBrain.Connections;

/// <summary>
/// S8-T3 the one owner-facing maintenance authority: the schema upgrade, its
/// rollback and the keyless format status, over the live vault directory.
/// <para>
/// Every act takes the lifecycle's exclusive maintenance lease, so the vault
/// must already be locked and no private operation may be running while the
/// migration writes. The credential is used only to open the key envelope for
/// the duration of the act; it is never stored, logged, written or passed to
/// a child process, and the vault is left exactly as it was found: locked.
/// </para>
/// </summary>
internal sealed class VaultMaintenanceService : IVaultMaintenanceService
{
    private readonly EncryptedVaultStore _store;
    private readonly VaultLifecycle _vault;
    private readonly VaultSchemaUpgradeEngine _engine;

    public VaultMaintenanceService(EncryptedVaultStore store, VaultLifecycle vault)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _vault = vault ?? throw new ArgumentNullException(nameof(vault));
        _engine = new VaultSchemaUpgradeEngine(store);
    }

    public VaultFormatStatus ReadFormatStatus() => _engine.ReadStatus();

    public VaultUpgradeSummary Upgrade(string credential, bool useRecoveryCode)
    {
        ArgumentException.ThrowIfNullOrEmpty(credential);
        using var lease = BeginMaintenance();
        byte[]? key = null;
        try
        {
            if (!_store.TryUnlock(credential, useRecoveryCode, out key, out var manifest) ||
                key is null || manifest is null)
            {
                throw new VaultMaintenanceDeniedException("vault_denied");
            }

            return _engine.Upgrade(manifest, key);
        }
        finally
        {
            if (key is not null)
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }
    }

    public VaultRollbackSummary Rollback(string credential, bool useRecoveryCode, bool discardOnly)
    {
        ArgumentException.ThrowIfNullOrEmpty(credential);
        using var lease = BeginMaintenance();
        byte[]? key = null;
        try
        {
            if (!_store.TryUnlock(credential, useRecoveryCode, out key, out var manifest) ||
                key is null || manifest is null)
            {
                throw new VaultMaintenanceDeniedException("vault_denied");
            }

            return _engine.Rollback(manifest, key, discardOnly);
        }
        finally
        {
            if (key is not null)
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }
    }

    private IDisposable BeginMaintenance() =>
        _vault.TryBeginMaintenance() ?? throw new VaultMaintenanceDeniedException("vault_busy");
}
