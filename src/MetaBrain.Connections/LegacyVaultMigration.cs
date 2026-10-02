using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using MetaBrain.Application;
using MetaBrain.Core.Security;

namespace MetaBrain.Connections;

internal sealed record VaultMigrationResult(
    string RecoveryCode,
    int ResourceCount,
    bool LegacyPolicyPreserved,
    string? CleanupWarning);

internal static class LegacyVaultMigration
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly SecurityIdentifier SystemSid = new("S-1-5-18");
    private static readonly SecurityIdentifier AdministratorsSid = new("S-1-5-32-544");

    public static VaultMigrationResult Migrate(string settingsPath, string passphrase)
    {
        var fullSettingsPath = Path.GetFullPath(settingsPath);
        var settings = ServiceSettingsLoader.LoadLegacyForMigration(fullSettingsPath);
        EnsureServiceStopped(settings.ControlPipe);
        var ownerSid = new SecurityIdentifier(settings.OwnerSid).Value;
        var resourceRoot = settings.LegacyResourceRootPath;
        var bindings = settings.ManagedResources ?? Array.Empty<ManagedResourceBinding>();
        var seeds = new List<VaultResourceSeed>(bindings.Length + 1);
        var sources = new List<(string Path, byte[] Content)>(bindings.Length + 1);
        var vaultPath = Path.Combine(Path.GetDirectoryName(fullSettingsPath)!, "vault");
        var store = new EncryptedVaultStore(vaultPath, ownerSid);
        if (store.Exists)
        {
            throw new IOException("An encrypted vault already exists; migration will not overwrite it.");
        }

        try
        {
            if (bindings.Length > 0)
            {
                var reader = new ManagedResourceFileReader(resourceRoot, ownerSid);
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
                var policyText = ProtectedFile.ReadPrivateFile(settings.LegacyPolicyStorePath, ownerSid, 4 * 1024 * 1024).Text;
                var policyContent = Encoding.UTF8.GetBytes(policyText);
                const string policyResourceId = "service-policy-state";
                if (bindings.Any(binding => string.Equals(binding.ResourceId, policyResourceId, StringComparison.Ordinal)))
                {
                    CryptographicOperations.ZeroMemory(policyContent);
                    throw new InvalidDataException("A legacy resource uses the reserved private policy identifier.");
                }

                sources.Add((settings.LegacyPolicyStorePath, policyContent));
                seeds.Add(new VaultResourceSeed(policyResourceId, "owner-private", policyContent, InternalOnly: true));
            }

            var material = store.Create(passphrase, seeds);
            try
            {
                WriteEncryptedSettings(fullSettingsPath, settings.OwnerSid, settings.ControlPipe);
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

                var policyPreserved = sources.Any(source =>
                    string.Equals(source.Path, settings.LegacyPolicyStorePath, StringComparison.OrdinalIgnoreCase));
                return new VaultMigrationResult(material.RecoveryCode, bindings.Length, policyPreserved, cleanupWarning);
            }
            catch
            {
                DeleteCreatedVault(vaultPath);
                throw;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(material.DataKey);
            }
        }
        finally
        {
            foreach (var source in sources)
            {
                CryptographicOperations.ZeroMemory(source.Content);
            }
        }
    }

    private static void EnsureServiceStopped(string controlPipe)
    {
        var request = JsonSerializer.Serialize(new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, "owner.status"), JsonOptions);
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
                FileSystemAclExtensions.SetAccessControl(new FileInfo(temporaryPath), CreateFileSecurity(ownerSid));
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

    private static FileSecurity CreateFileSecurity(string ownerSid)
    {
        var owner = new SecurityIdentifier(ownerSid);
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(owner);
        security.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(SystemSid, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(AdministratorsSid, FileSystemRights.FullControl, AccessControlType.Allow));
        return security;
    }

    private static void DeleteCreatedVault(string vaultPath)
    {
        if (Directory.Exists(vaultPath) && (File.GetAttributes(vaultPath) & FileAttributes.ReparsePoint) == 0)
        {
            Directory.Delete(vaultPath, recursive: true);
        }
    }
}
