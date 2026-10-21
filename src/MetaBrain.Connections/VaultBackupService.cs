using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MetaBrain.Core.Security;

namespace MetaBrain.Connections;

/// <summary>
/// One captured record of an encrypted snapshot: the canonical identity, the
/// zone it belongs to, the exact revision it was read at and the encrypted
/// record bytes as the vault stored them.
/// </summary>
internal sealed record VaultBackupRecord(
    string ResourceId,
    string ZoneId,
    int SchemaVersion,
    long Revision,
    string ContentBase64);

/// <summary>
/// The dedicated owner states one snapshot carries. They are the encrypted
/// side files of the vault: the scope grants, the published catalog, the
/// source enrollments, the recall policy, the review state, the purge ledger
/// and the S8-T4 private operations state.
/// </summary>
/// <remarks>
/// The operations state travels with the snapshot for the same reason every
/// other dedicated state does: a restored vault is a complete vault. A
/// backup taken before a purge still holds the audit bytes of that moment,
/// exactly as it still holds the canonical bytes of that moment: the copy is
/// irrevocable, and the purge watermark that a later backup carries keeps the
/// purged entries from coming back.
/// </remarks>
internal sealed record VaultBackupStates(
    OwnerScopeGrantState ScopeGrants,
    OwnerCatalogState Catalog,
    SourceEnrollmentState SourceEnrollment,
    OwnerRecallPolicyState RecallPolicy,
    OwnerReviewState ReviewState,
    OwnerPurgeLedgerState PurgeLedger,
    OperationsAuditState? OperationsAudit = null);

/// <summary>
/// The captured point in time: the canonical records with their exact
/// revisions, the dedicated owner states and the manifest revision the
/// capture was taken at.
/// </summary>
internal sealed record VaultBackupSnapshot(
    VaultBackupRecord[] Records,
    VaultBackupStates States,
    long ManifestRevision);

/// <summary>
/// The plaintext of one snapshot, before it is encrypted into the container.
/// </summary>
internal sealed record VaultBackupPayload(
    int FormatVersion,
    string BackupId,
    DateTimeOffset CreatedAtUtc,
    string VaultId,
    long ManifestRevision,
    VaultBackupRecord[] Records,
    VaultBackupStates States);

/// <summary>
/// The container that is written to disk. The key envelope is the live vault
/// header: the passphrase-wrapped and the recovery-wrapped form of the data
/// key, never the data key itself. The ciphertext is the whole snapshot.
/// </summary>
internal sealed record VaultBackupContainer(
    int FormatVersion,
    string BackupId,
    DateTimeOffset CreatedAtUtc,
    string VaultId,
    JsonElement KeyEnvelope,
    string Nonce,
    string Tag,
    string Ciphertext);

/// <summary>
/// S8-T2 the encrypted backup and restore authority over the live vault.
/// <para>
/// A backup is one encrypted file that holds a revision-gated snapshot of the
/// canonical records and of the dedicated owner states. The file never holds
/// the data key, a credential, a session, a token or any plaintext: the
/// snapshot itself is encrypted with the data key, and the key envelope the
/// file carries is the same password-wrapped material the live vault header
/// already holds, so the owner can open the backup with the passphrase or
/// with the recovery code and nobody else can open it at all.
/// </para>
/// <para>
/// A restore reproduces the owner's data into a clean directory: the same
/// record IDs, the same zones, the same revisions, the same relations,
/// sources, lifecycle, intention, archive and deletion state. It never
/// reproduces live authority: the restored scope state carries no live grant,
/// no session and no redeemable token, and the tokens that were already used
/// stay used.
/// </para>
/// </summary>
internal sealed class VaultBackupService : IVaultBackupService
{
    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly EncryptedVaultStore _store;
    private readonly VaultLifecycle _vault;

    public VaultBackupService(EncryptedVaultStore store, VaultLifecycle vault)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _vault = vault ?? throw new ArgumentNullException(nameof(vault));
    }

    public VaultBackupSummary CreateBackup(IVaultOperation operation, string destinationPath)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (string.IsNullOrWhiteSpace(destinationPath))
        {
            throw new ArgumentException("A backup destination is required.", nameof(destinationPath));
        }

        if (!_vault.TryExportBackupKey(out var dataKey))
        {
            throw new ManagedResourceUnavailableException();
        }

        try
        {
            var keyEnvelope = _store.ReadKeyEnvelopeJson();
            var backupId = Guid.NewGuid().ToString("N");
            var createdAtUtc = DateTimeOffset.UtcNow;
            var snapshot = _vault.CaptureSnapshot(() => CaptureSnapshot(operation));
            var records = snapshot.Records;
            var manifestRevision = snapshot.ManifestRevision;
            var states = snapshot.States;
            var payload = new VaultBackupPayload(
                VaultBackupFormat.CurrentVersion,
                backupId,
                createdAtUtc,
                _vault.CurrentVaultId,
                manifestRevision,
                records,
                states);
            var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
            try
            {
                if (payloadBytes.LongLength > VaultBackupFormat.MaximumContainerBytes)
                {
                    throw new IOException("The encrypted snapshot exceeds the backup size bound.");
                }

                var (nonce, tag, ciphertext) = EncryptPayload(payloadBytes, dataKey, backupId, _vault.CurrentVaultId);
                var container = new VaultBackupContainer(
                    VaultBackupFormat.CurrentVersion,
                    backupId,
                    createdAtUtc,
                    _vault.CurrentVaultId,
                    ParseEnvelopeElement(keyEnvelope),
                    nonce,
                    tag,
                    ciphertext);
                var containerBytes = JsonSerializer.SerializeToUtf8Bytes(container, JsonOptions);
                try
                {
                    var path = WriteContainerAtomically(destinationPath, containerBytes);
                    return new VaultBackupSummary(
                        backupId,
                        path,
                        containerBytes.LongLength,
                        records.Length,
                        7,
                        manifestRevision,
                        createdAtUtc);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(containerBytes);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(payloadBytes);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    public VaultRestoreSummary Restore(string backupPath, string targetDirectory, string credential, bool useRecoveryCode)
    {
        if (string.IsNullOrWhiteSpace(backupPath) || string.IsNullOrWhiteSpace(targetDirectory))
        {
            throw new ArgumentException("A backup file and a clean target directory are required.");
        }

        if (string.IsNullOrEmpty(credential))
        {
            throw new ArgumentException("A passphrase or a recovery code is required.");
        }

        var fullBackupPath = Path.GetFullPath(backupPath);
        var fullTarget = Path.GetFullPath(targetDirectory);
        if (Directory.Exists(fullTarget) || File.Exists(fullTarget))
        {
            throw new IOException("The restore target must be a directory that does not exist yet.");
        }

        var containerBytes = PrivateFile.ReadBytes(fullBackupPath, VaultBackupFormat.MaximumContainerBytes);
        try
        {
            VaultBackupContainer? container;
            try
            {
                container = JsonSerializer.Deserialize<VaultBackupContainer>(containerBytes, JsonOptions);
            }
            catch (JsonException ex)
            {
                throw new VaultBackupCorruptedException("The encrypted backup container is invalid.", ex);
            }

            if (container is null ||
                container.FormatVersion != VaultBackupFormat.CurrentVersion ||
                string.IsNullOrEmpty(container.BackupId) ||
                string.IsNullOrEmpty(container.VaultId) ||
                container.KeyEnvelope.ValueKind != JsonValueKind.Object)
            {
                throw new VaultBackupCorruptedException("The encrypted backup container is invalid.");
            }

            var keyEnvelopeJson = container.KeyEnvelope.GetRawText();
            if (!_store.TryUnwrapBackupKey(keyEnvelopeJson, credential, useRecoveryCode, out var dataKey, out var envelopeVaultId) ||
                dataKey is null)
            {
                throw new VaultBackupDeniedException("The backup could not be opened with the supplied credential.");
            }

            if (!string.Equals(envelopeVaultId, container.VaultId, StringComparison.Ordinal))
            {
                CryptographicOperations.ZeroMemory(dataKey);
                throw new VaultBackupCorruptedException("The encrypted backup container is invalid.");
            }

            try
            {
                var payloadBytes = DecryptPayload(container, dataKey);
                try
                {
                    var payload = JsonSerializer.Deserialize<VaultBackupPayload>(payloadBytes, JsonOptions)
                        ?? throw new VaultBackupCorruptedException("The encrypted backup payload is invalid.");
                    if (payload.FormatVersion != VaultBackupFormat.CurrentVersion ||
                        !string.Equals(payload.VaultId, container.VaultId, StringComparison.Ordinal) ||
                        !string.Equals(payload.BackupId, container.BackupId, StringComparison.Ordinal) ||
                        payload.Records is null || payload.States is null)
                    {
                        throw new VaultBackupCorruptedException("The encrypted backup payload is invalid.");
                    }

                    var written = MaterializeRestoredVault(fullTarget, container, payload, dataKey);
                    return new VaultRestoreSummary(
                        container.BackupId,
                        written,
                        payload.VaultId,
                        payload.Records.Length,
                        payload.ManifestRevision,
                        payload.CreatedAtUtc,
                        0,
                        payload.States.ScopeGrants.ConsumedTokenVerifiers?.Length ?? 0);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(payloadBytes);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(dataKey);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(containerBytes);
        }
    }

    /// <summary>
    /// Captures the canonical records and the dedicated owner states as one
    /// point in time. The caller holds the vault's content gate for the whole
    /// capture, so no record can move between the moment the manifest is
    /// listed and the moment its bytes are read; the revision gate on every
    /// read is the second check on the same property.
    /// </summary>
    private VaultBackupSnapshot CaptureSnapshot(IVaultOperation operation)
    {
        var resources = operation.ListResources();
        var records = new VaultBackupRecord[resources.Count];
        for (var index = 0; index < resources.Count; index++)
        {
            var resource = resources[index];
            byte[] content;
            try
            {
                content = operation.ReadContent(resource.ResourceId, resource.Revision);
            }
            catch (ManagedResourceUnavailableException ex)
            {
                throw new IOException("A record moved while the snapshot was captured.", ex);
            }

            try
            {
                records[index] = new VaultBackupRecord(
                    resource.ResourceId,
                    resource.ZoneId,
                    _store.ResourceSchemaVersion,
                    resource.Revision,
                    Convert.ToBase64String(content));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(content);
            }
        }

        var states = new VaultBackupStates(
            operation.LoadScopeGrantState(),
            operation.LoadCatalogState(),
            operation.LoadSourceEnrollmentState(),
            operation.LoadRecallPolicyState(),
            operation.LoadReviewState(),
            operation.LoadPurgeLedgerState(),
            operation.LoadOperationsAuditState());
        return new VaultBackupSnapshot(records, states, _vault.CurrentManifestRevision);
    }

    /// <summary>
    /// Writes the restored vault into a staging directory and moves it into
    /// place only when every file is written. A failed restore leaves the
    /// target untouched: there is no partial vault and no reset.
    /// </summary>
    private string MaterializeRestoredVault(
        string targetDirectory,
        VaultBackupContainer container,
        VaultBackupPayload payload,
        byte[] dataKey)
    {
        var parent = Path.GetDirectoryName(targetDirectory)
            ?? throw new InvalidDataException("The restore target has no parent directory.");
        Directory.CreateDirectory(parent);
        var staging = targetDirectory + "." + Guid.NewGuid().ToString("N") + ".staging";
        try
        {
            Directory.CreateDirectory(staging);
            var store = new EncryptedVaultStore(staging);
            store.WriteRestoredHeader(container.KeyEnvelope.GetRawText());

            var descriptors = new VaultResourceDescriptor[payload.Records.Length];
            for (var index = 0; index < payload.Records.Length; index++)
            {
                var record = payload.Records[index];
                if (string.IsNullOrEmpty(record.ResourceId) || string.IsNullOrEmpty(record.ZoneId) ||
                    record.Revision < 1)
                {
                    throw new VaultBackupCorruptedException("The encrypted backup payload is invalid.");
                }

                var descriptor = new VaultResourceDescriptor(
                    record.ResourceId,
                    record.ZoneId,
                    record.SchemaVersion,
                    record.Revision,
                    Guid.NewGuid().ToString("N") + ".mbv");
                descriptors[index] = descriptor;
            }

            var manifest = new VaultManifest(
                store.ManifestSchemaVersion,
                payload.VaultId,
                Math.Max(1, payload.ManifestRevision),
                descriptors);

            for (var index = 0; index < payload.Records.Length; index++)
            {
                var record = payload.Records[index];
                byte[] content;
                try
                {
                    content = Convert.FromBase64String(record.ContentBase64);
                }
                catch (FormatException ex)
                {
                    throw new VaultBackupCorruptedException("The encrypted backup payload is invalid.", ex);
                }

                try
                {
                    store.WriteRestoredResource(payload.VaultId, descriptors[index], content, dataKey);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(content);
                }
            }

            store.WriteRestoredManifest(payload.VaultId, manifest, dataKey);
            var restoredGrants = VaultRestorePolicy.RestoreScopeGrantState(payload.States.ScopeGrants);
            store.SaveScopeGrantState(manifest, dataKey, restoredGrants);
            store.SaveCatalogState(manifest, dataKey, payload.States.Catalog);
            store.SaveSourceEnrollmentState(manifest, dataKey, payload.States.SourceEnrollment);
            store.SaveRecallPolicyState(manifest, dataKey, payload.States.RecallPolicy);
            store.SaveReviewState(manifest, dataKey, payload.States.ReviewState);
            store.SavePurgeLedgerState(manifest, dataKey, payload.States.PurgeLedger);
            // A backup taken before the operations state existed restores an
            // empty one: the audit trail of the source installation is not
            // invented, and the restored vault starts with no private
            // operations history.
            store.SaveOperationsAuditState(
                manifest,
                dataKey,
                payload.States.OperationsAudit ?? OperationsAuditState.Empty);
            Directory.Move(staging, targetDirectory);
            return targetDirectory;
        }
        catch
        {
            if (Directory.Exists(staging))
            {
                try
                {
                    Directory.Delete(staging, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }

            throw;
        }
    }

    private static string WriteContainerAtomically(string destinationPath, byte[] containerBytes)
    {
        var fullPath = Path.GetFullPath(destinationPath);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidDataException("The backup destination has no parent directory.");
        Directory.CreateDirectory(directory);
        if (File.Exists(fullPath))
        {
            throw new IOException("The backup file already exists; it will not be overwritten.");
        }

        var temporaryPath = Path.Combine(directory, "." + Guid.NewGuid().ToString("N") + ".backup.tmp");
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.WriteThrough))
            {
                stream.Write(containerBytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, fullPath);
            return fullPath;
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
    }

    private static JsonElement ParseEnvelopeElement(string keyEnvelopeJson)
    {
        using var document = JsonDocument.Parse(keyEnvelopeJson);
        return document.RootElement.Clone();
    }

    private static (string Nonce, string Tag, string Ciphertext) EncryptPayload(
        byte[] plaintext,
        byte[] dataKey,
        string backupId,
        string vaultId)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var tag = new byte[TagBytes];
        var ciphertext = new byte[plaintext.Length];
        var associatedData = BackupAssociatedData(backupId, vaultId);
        try
        {
            using var aes = new AesGcm(dataKey, TagBytes);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);
            return (Convert.ToBase64String(nonce), Convert.ToBase64String(tag), Convert.ToBase64String(ciphertext));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(nonce);
            CryptographicOperations.ZeroMemory(tag);
            CryptographicOperations.ZeroMemory(ciphertext);
            CryptographicOperations.ZeroMemory(associatedData);
        }
    }

    private static byte[] DecryptPayload(VaultBackupContainer container, byte[] dataKey)
    {
        byte[]? nonce = null;
        byte[]? tag = null;
        byte[]? ciphertext = null;
        byte[]? plaintext = null;
        try
        {
            nonce = Convert.FromBase64String(container.Nonce);
            tag = Convert.FromBase64String(container.Tag);
            ciphertext = Convert.FromBase64String(container.Ciphertext);
            if (nonce.Length != NonceBytes || tag.Length != TagBytes ||
                ciphertext.Length == 0 || ciphertext.Length > VaultBackupFormat.MaximumContainerBytes)
            {
                throw new InvalidDataException("The encrypted backup container is invalid.");
            }

            var associatedData = BackupAssociatedData(container.BackupId, container.VaultId);
            try
            {
                plaintext = new byte[ciphertext.Length];
                using var aes = new AesGcm(dataKey, TagBytes);
                aes.Decrypt(nonce, ciphertext, tag, plaintext, associatedData);
                var result = plaintext;
                plaintext = null;
                return result;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(associatedData);
            }
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException)
        {
            throw new VaultBackupCorruptedException("The encrypted backup could not be opened.", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(nonce);
            CryptographicOperations.ZeroMemory(tag);
            CryptographicOperations.ZeroMemory(ciphertext);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static byte[] BackupAssociatedData(string backupId, string vaultId) =>
        Encoding.UTF8.GetBytes(
            $"MetaBrain|{VaultBackupFormat.Purpose}|{VaultBackupFormat.CurrentVersion}|{vaultId}|{backupId}");
}
