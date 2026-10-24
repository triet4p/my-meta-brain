using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MetaBrain.Application;
using MetaBrain.Core.Security;

namespace MetaBrain.Connections;

internal sealed record VaultMigrationResult(
    string RecoveryCode,
    int ResourceCount,
    bool LegacyPolicyPreserved,
    string? CleanupWarning,
    bool Resumed);

/// <summary>
/// S8-T3 the explicit owner cutover from the inherited plaintext settings and
/// resource files to the encrypted vault.
/// <para>
/// The cutover creates the encrypted vault from the legacy bytes, writes the
/// current settings file, and only then removes the plaintext copies it
/// migrated. It no longer changes the permissions or the owner of any path: the
/// privacy of the migrated data comes from the owner's key and the
/// authenticated envelopes, exactly like the rest of the vault, and the
/// migration is not on the daily path (the service refuses legacy settings, so
/// it runs with the service stopped).
/// </para>
/// <para>
/// A cutover that is interrupted after the vault exists but before the settings
/// file is written is resumed instead of being refused: the vault is opened
/// with the same passphrase and every legacy binding must already be stored at
/// its first revision with exactly the same bytes. Anything else is a
/// different vault and is refused untouched.
/// </para>
/// </summary>
internal static class LegacyVaultMigration
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private const string PolicyResourceId = "service-policy-state";

    public static VaultMigrationResult Migrate(string settingsPath, string passphrase)
    {
        var fullSettingsPath = Path.GetFullPath(settingsPath);
        var settings = ServiceSettingsLoader.LoadLegacyForMigration(fullSettingsPath);
        EnsureServiceStopped(settings.ControlPipe);
        var resourceRoot = settings.LegacyResourceRootPath;
        var bindings = settings.ManagedResources ?? Array.Empty<ManagedResourceBinding>();
        var seeds = new List<VaultResourceSeed>(bindings.Length + 1);
        var sources = new List<(string Path, byte[] Content)>(bindings.Length + 1);
        var vaultPath = Path.Combine(Path.GetDirectoryName(fullSettingsPath)!, "vault");
        var store = new EncryptedVaultStore(vaultPath);
        var resumed = store.Exists;
        try
        {
            ReadLegacyContent(settings, resourceRoot, bindings, sources, seeds);
            string? recoveryCode = null;
            if (resumed)
            {
                VerifyInterruptedCutover(store, passphrase, seeds);
            }
            else
            {
                var material = store.Create(passphrase, seeds);
                try
                {
                    recoveryCode = material.RecoveryCode;
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(material.DataKey);
                }
            }

            WriteEncryptedSettings(fullSettingsPath, settings.OwnerSid, settings.ControlPipe);
            var cleanupWarning = DeleteMigratedPlaintext(sources);
            var policyPreserved = sources.Any(source =>
                string.Equals(source.Path, settings.LegacyPolicyStorePath, StringComparison.OrdinalIgnoreCase));
            return new VaultMigrationResult(recoveryCode ?? string.Empty, bindings.Length, policyPreserved, cleanupWarning, resumed);
        }
        catch
        {
            // A fresh cutover that failed before the settings write leaves no
            // vault behind. An interrupted cutover being resumed keeps the
            // vault: it is exactly the state the next attempt completes from.
            if (!resumed)
            {
                DeleteCreatedVault(vaultPath);
            }

            throw;
        }
        finally
        {
            foreach (var source in sources)
            {
                CryptographicOperations.ZeroMemory(source.Content);
            }
        }
    }

    private static void ReadLegacyContent(
        ServiceSettings settings,
        string resourceRoot,
        ManagedResourceBinding[] bindings,
        List<(string Path, byte[] Content)> sources,
        List<VaultResourceSeed> seeds)
    {
        if (bindings.Length > 0)
        {
            var reader = new ManagedResourceFileReader(resourceRoot);
            foreach (var binding in bindings)
            {
                reader.Register(binding.ResourceId, binding.ZoneId, binding.FileName);
            }

            foreach (var binding in bindings)
            {
                var content = reader.ReadContent(binding.ResourceId);
                var path = Path.Combine(resourceRoot, binding.FileName);
                sources.Add((path, content));
                seeds.Add(new VaultResourceSeed(binding.ResourceId, binding.ZoneId, content));
            }
        }

        if (File.Exists(settings.LegacyPolicyStorePath))
        {
            var policyText = PrivateFile.Read(settings.LegacyPolicyStorePath, 4 * 1024 * 1024).Text;
            var policyContent = Encoding.UTF8.GetBytes(policyText);
            if (bindings.Any(binding => string.Equals(binding.ResourceId, PolicyResourceId, StringComparison.Ordinal)))
            {
                CryptographicOperations.ZeroMemory(policyContent);
                throw new InvalidDataException("A legacy resource uses the reserved private policy identifier.");
            }

            sources.Add((settings.LegacyPolicyStorePath, policyContent));
            seeds.Add(new VaultResourceSeed(PolicyResourceId, "owner-private", policyContent, InternalOnly: true));
        }
    }

    /// <summary>
    /// Confirms that an existing vault is the one an interrupted cutover of
    /// this settings file created: the passphrase opens it and every legacy
    /// record is already stored at its first revision with exactly the legacy
    /// bytes. A different vault, a different passphrase or a moved legacy file
    /// fails closed without touching anything.
    /// </summary>
    private static void VerifyInterruptedCutover(
        EncryptedVaultStore store,
        string passphrase,
        List<VaultResourceSeed> seeds)
    {
        if (!store.TryUnlock(passphrase, useRecoveryCode: false, out var dataKey, out var manifest) ||
            dataKey is null || manifest is null)
        {
            throw new IOException("An encrypted vault already exists and the passphrase does not open it; migration will not overwrite it.");
        }

        try
        {
            foreach (var seed in seeds)
            {
                var content = store.ReadResourceIncludingInternal(manifest, dataKey, seed.ResourceId, expectedRevision: 1);
                try
                {
                    if (!content.AsSpan().SequenceEqual(seed.Content))
                    {
                        throw new IOException("An encrypted vault already exists with different content; migration will not overwrite it.");
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(content);
                }
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    private static string? DeleteMigratedPlaintext(List<(string Path, byte[] Content)> sources)
    {
        string? cleanupWarning = null;
        foreach (var source in sources)
        {
            try
            {
                if (File.Exists(source.Path))
                {
                    File.Delete(source.Path);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                cleanupWarning = "One or more configured legacy plaintext files remain; the encrypted vault and settings cutover succeeded, but those exact source copies need owner cleanup.";
            }
        }

        return cleanupWarning;
    }

    private static void EnsureServiceStopped(string controlPipe)
    {
        var request = JsonSerializer.Serialize(new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, ServiceRequestHandler.OwnerStatusOperation), JsonOptions);
        var result = PipeClient.InvokeOwnerAsync(controlPipe, request, CancellationToken.None).GetAwaiter().GetResult();
        if (result.Success)
        {
            throw new InvalidOperationException("Stop the owner service before migrating its settings and resources.");
        }

        try
        {
            using var document = JsonDocument.Parse(result.Json);
            if (!document.RootElement.TryGetProperty("error", out var error) ||
                !string.Equals(error.GetString(), "transport_unavailable", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The owner service state is uncertain; migration was not started.");
            }
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("The owner service state is uncertain; migration was not started.");
        }
    }

    /// <summary>
    /// Writes the current settings file atomically with the ordinary
    /// permissions of the owner account. The cutover deliberately applies no
    /// permission change and takes no ownership: the owner's data is protected
    /// by the key and the encrypted envelopes, not by rewriting the
    /// permissions of a path.
    /// </summary>
    private static void WriteEncryptedSettings(string settingsPath, string ownerSid, string controlPipe)
    {
        var content = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = ServiceSettings.CurrentSchemaVersion,
            ownerSid,
            controlPipe,
            agentPipe = controlPipe + ".agent"
        }, JsonOptions);
        var directory = Path.GetDirectoryName(settingsPath) ?? throw new InvalidDataException("Invalid service settings path.");
        var temporaryPath = Path.Combine(directory, ".service-settings." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, settingsPath, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(content);
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static void DeleteCreatedVault(string vaultPath)
    {
        if (Directory.Exists(vaultPath) && (File.GetAttributes(vaultPath) & FileAttributes.ReparsePoint) == 0)
        {
            Directory.Delete(vaultPath, recursive: true);
        }
    }
}
