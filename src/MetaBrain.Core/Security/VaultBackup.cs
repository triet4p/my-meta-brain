namespace MetaBrain.Core.Security;

/// <summary>
/// S8-T2 the encrypted backup container contract.
/// <para>
/// One backup is one file. The file carries a small plaintext envelope that
/// names the backup, the vault it came from and the two password-wrapped
/// forms of the vault data key, plus one AEAD ciphertext that holds the whole
/// snapshot: the canonical records with their exact revisions, the dedicated
/// owner states, and nothing else. The plaintext envelope never holds the
/// data key itself, a credential, a token or any record content: it holds
/// only the same wrapped key material the live vault header already holds,
/// so the owner can open the backup with the passphrase or with the recovery
/// code, and nobody else can open it at all.
/// </para>
/// <para>
/// The backup is a copy of encrypted bytes, never a second copy of the key.
/// A backup plus the owner's credential is the owner's own data; a backup
/// alone is ciphertext that no reader in this product can open.
/// </para>
/// </summary>
public static class VaultBackupFormat
{
    /// <summary>The container format version this build writes.</summary>
    public const int CurrentVersion = 2;

    /// <summary>
    /// The container format versions this build reads. Version 1 is the
    /// pre-workspace snapshot format; it restores canonical records and owner
    /// states and truthfully restores no workspace because it never held one.
    /// Version 2 carries the workspace state, retained originals, tombstones
    /// and tombstones. A version this build does not know is refused
    /// before anything is written rather than silently dropping part of the
    /// snapshot.
    /// </summary>
    public static bool IsSupportedVersion(int version) => version is 1 or 2;

    /// <summary>The file extension the owner sees for a backup file.</summary>
    public const string FileExtension = ".mbbackup";

    /// <summary>The associated-data purpose that binds a ciphertext to its backup.</summary>
    public const string Purpose = "vault-backup";

    /// <summary>
    /// The largest container this build reads. A backup of the encrypted vault
    /// is bounded by the encrypted vault itself; the bound exists so a
    /// corrupted or hostile file cannot make the owner process allocate
    /// without limit.
    /// </summary>
    public const long MaximumContainerBytes = 512L * 1024 * 1024;
}

/// <summary>
/// What the owner learns from one completed backup. It names the backup, where
/// it was written, how large it is and what it captured. It never names a
/// record, a title, a body, a citation, a key or a token.
/// </summary>
public sealed record VaultBackupSummary(
    string BackupId,
    string Path,
    long Bytes,
    int RecordCount,
    int StateCount,
    long ManifestRevision,
    DateTimeOffset CreatedAtUtc,
    int WorkspaceNodeCount = 0,
    int WorkspaceContentRevisionCount = 0,
    int WorkspaceTombstoneCount = 0);

/// <summary>
/// What the owner learns from one completed restore. It names the restored
/// vault, where it was written and what it deliberately did not carry over.
/// </summary>
public sealed record VaultRestoreSummary(
    string BackupId,
    string Path,
    string VaultId,
    int RecordCount,
    long ManifestRevision,
    DateTimeOffset CreatedAtUtc,
    int LiveGrantsRestored,
    int ConsumedTokensPreserved,
    int WorkspaceNodeCount = 0,
    int WorkspaceContentRevisionCount = 0,
    int PurgedRecordsWithheld = 0,
    int PurgedWorkspaceNodesWithheld = 0,
    int PurgedCatalogEntriesWithheld = 0);

/// <summary>
/// S8-T2 the restore policy: what a restored vault may and may not become.
/// <para>
/// A restore reproduces the owner's data: every record, its exact revision,
/// its relations, its sources, its lifecycle and intention states, its
/// archive and retained revisions, and the fact that something was deleted.
/// It never reproduces live authority: no scope grant survives as a live
/// grant, no session survives, and no token that was already used becomes
/// redeemable again. The consumed-token list is carried over precisely so a
/// used token stays used.
/// </para>
/// </summary>
public static class VaultRestorePolicy
{
    /// <summary>
    /// The scope-grant state a restored vault starts from. Every live grant is
    /// dropped, the unlock epoch restarts at zero and the consumed-token
    /// verifiers are carried over so a token that was already redeemed can
    /// never be redeemed a second time after a restore.
    /// </summary>
    public static OwnerScopeGrantState RestoreScopeGrantState(OwnerScopeGrantState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state with
        {
            Grants = Array.Empty<PersistedScopeGrant>(),
            UnlockEpoch = 0
        };
    }

    /// <summary>
    /// The scope-grant state a restored vault starts from when the live
    /// installation of the same vault is still unlocked. It drops every live
    /// grant and restarts the unlock epoch exactly like
    /// <see cref="RestoreScopeGrantState(OwnerScopeGrantState)"/>, and it
    /// carries the union of the backup's consumed-token verifiers and the live
    /// ones. Restoring a backup taken before a token was redeemed therefore
    /// cannot make that token redeemable again.
    /// </summary>
    public static OwnerScopeGrantState RestoreScopeGrantState(
        OwnerScopeGrantState state,
        OwnerScopeGrantState? liveState)
    {
        var restored = RestoreScopeGrantState(state);
        if (liveState is null)
        {
            return restored;
        }

        var consumed = (restored.ConsumedTokenVerifiers ?? Array.Empty<string>())
            .Concat(liveState.ConsumedTokenVerifiers ?? Array.Empty<string>())
            .Where(verifier => !string.IsNullOrEmpty(verifier))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(verifier => verifier, StringComparer.Ordinal)
            .ToArray();
        return restored with { ConsumedTokenVerifiers = consumed };
    }

    /// <summary>
    /// The ledger a restore re-applies: the backup's durable deletion record
    /// unioned with the live installation's record of the same vault. A backup
    /// taken before a purge carries no entry for it, while the live ledger
    /// does; unioning them means restoring an older backup cannot resurrect an
    /// identity this installation already purged. Both sides are validated, so
    /// a corrupt record refuses the restore rather than being silently
    /// repaired or dropped.
    /// </summary>
    public static OwnerPurgeLedgerState MergePurgeLedgerState(
        OwnerPurgeLedgerState state,
        OwnerPurgeLedgerState? liveState)
    {
        ArgumentNullException.ThrowIfNull(state);
        var restored = OwnerPurgeLedgerAuthority.ValidateAndNormalizeState(state);
        if (liveState is null)
        {
            return restored;
        }

        var live = OwnerPurgeLedgerAuthority.ValidateAndNormalizeState(liveState);
        var byId = new Dictionary<string, OwnerPurgeIntent>(StringComparer.Ordinal);
        foreach (var intent in restored.Intents)
        {
            byId[intent.PurgeId] = intent;
        }

        foreach (var intent in live.Intents)
        {
            // The live record is the newer authority for an ID both sides
            // know; a backup never un-applies a deletion.
            byId[intent.PurgeId] = intent;
        }

        var merged = byId.Values.OrderBy(intent => intent.PurgeId, StringComparer.Ordinal).ToArray();
        if (merged.Length > OwnerPurgeLedgerAuthority.MaximumIntents)
        {
            throw new InvalidDataException("The purge ledger exceeds its bounds.");
        }

        return OwnerPurgeLedgerAuthority.ValidateAndNormalizeState(
            restored with { Intents = merged });
    }
}

/// <summary>
/// S8-T2 the backup could not be opened because the credential was wrong or
/// missing. The restore fails closed: nothing is written, no fallback key is
/// minted, no data is reset and no recovery is invented.
/// </summary>
public sealed class VaultBackupDeniedException : Exception
{
    public VaultBackupDeniedException(string message)
        : base(message)
    {
    }

    public VaultBackupDeniedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// S12-T5 the restore would materialize a backup of the live vault while that
/// vault is locked. The live installation's purge ledger is encrypted, so the
/// restore cannot prove it re-applies every deletion made after the backup was
/// taken. It refuses before writing anything; the owner unlocks the live vault
/// and retries, and the normal restore then unions the two ledgers.
/// </summary>
public sealed class VaultRestoreRequiresUnlockException : Exception
{
    public VaultRestoreRequiresUnlockException(string message)
        : base(message)
    {
    }

    public VaultRestoreRequiresUnlockException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// S8-T2 the backup container is not readable: it was tampered with,
/// truncated, or written in another format. The restore fails closed and
/// writes nothing.
/// </summary>
public sealed class VaultBackupCorruptedException : Exception
{
    public VaultBackupCorruptedException(string message)
        : base(message)
    {
    }

    public VaultBackupCorruptedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// S8-T2 the owner-facing backup and restore authority. The application layer
/// calls this through the owner channel; the adapter that implements it owns
/// the encrypted file format, the key handling and the clean target
/// directory. There is exactly one implementation, so the owner CLI, the
/// owner console and any later surface all reach the same snapshot rules.
/// </summary>
public interface IVaultBackupService
{
    /// <summary>
    /// Writes one encrypted snapshot of the unlocked vault to
    /// <paramref name="destinationPath"/>. The snapshot is revision-gated:
    /// every record is captured at the exact revision the live manifest held
    /// when it was read, and a record that moved during the capture is read
    /// again at its new revision. Nothing about the live vault is changed by
    /// a backup, and the backup never carries the data key, a credential, a
    /// session or a token.
    /// </summary>
    VaultBackupSummary CreateBackup(IVaultOperation operation, string destinationPath);

    /// <summary>
    /// Restores one encrypted backup into a clean directory. The owner must
    /// present the passphrase or the recovery code: a wrong or missing
    /// credential, a tampered container or a corrupted container fails closed
    /// with nothing written and no reset, no fallback and no new key. The
    /// restored vault carries the owner's data and none of the live
    /// authority: no grant, no session and no redeemable token.
    /// </summary>
    VaultRestoreSummary Restore(string backupPath, string targetDirectory, string credential, bool useRecoveryCode);
}
