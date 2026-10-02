using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MetaBrain.Connections;

internal sealed record ManagedResourceBinding(
    string ResourceId,
    string ZoneId,
    string FileName);

internal sealed record ServiceSettings(
    int SchemaVersion,
    string OwnerSid,
    string ControlPipe,
    string AgentPipe,
    ManagedResourceBinding[]? ManagedResources = null)
{
    public const int LegacySchemaVersion = 1;
    public const int CurrentSchemaVersion = 3;
    internal string VaultDirectoryPath { get; init; } = string.Empty;
    internal string LegacyPolicyStorePath { get; init; } = string.Empty;
    internal string LegacyResourceRootPath { get; init; } = string.Empty;
}

internal static partial class ServiceSettingsLoader
{
    private const int MaximumSettingsBytes = 128 * 1024;
    private static readonly Regex SafeIdentifier = SafeIdentifierRegex();
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static ServiceSettings Load(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var (settings, ownerSid, directory) = ReadAndValidate(fullPath);
        if (settings.SchemaVersion == ServiceSettings.LegacySchemaVersion)
        {
            throw new InvalidDataException("Legacy plaintext settings require the explicit owner migration command before service startup.");
        }

        var migrateSchemaTwo = settings.SchemaVersion == 2;
        if (migrateSchemaTwo)
        {
            settings = settings with
            {
                SchemaVersion = ServiceSettings.CurrentSchemaVersion,
                AgentPipe = string.IsNullOrWhiteSpace(settings.AgentPipe)
                    ? settings.ControlPipe + ".agent"
                    : settings.AgentPipe
            };
        }

        if (settings.SchemaVersion != ServiceSettings.CurrentSchemaVersion ||
            settings.ManagedResources is { Length: > 0 })
        {
            throw new InvalidDataException("Unsupported or invalid encrypted-vault settings schema.");
        }

        ValidateOwner(settings, ownerSid);
        ValidatePipeName(settings.ControlPipe);
        ValidatePipeName(settings.AgentPipe);
        if (string.Equals(settings.ControlPipe, settings.AgentPipe, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Owner and agent pipe endpoints must differ.");
        }

        if (migrateSchemaTwo)
        {
            WriteCurrentSettingsAtomically(fullPath, settings);
        }

        return settings with { VaultDirectoryPath = Path.Combine(directory.FullName, "vault") };
    }

    public static ServiceSettings LoadLegacyForMigration(string path)
    {
        var (settings, ownerSid, directory) = ReadAndValidate(path);
        if (settings.SchemaVersion != ServiceSettings.LegacySchemaVersion)
        {
            throw new InvalidDataException("Only the current ACL-only settings format can be migrated.");
        }

        ValidateLegacy(settings, ownerSid);
        return settings with
        {
            LegacyPolicyStorePath = Path.Combine(directory.FullName, "grant-policy.json"),
            LegacyResourceRootPath = Path.Combine(directory.FullName, "resources"),
        };
    }

    public static bool IsSafeIdentifier(string? value) => value is not null && SafeIdentifier.IsMatch(value);

    private static (ServiceSettings Settings, string OwnerSid, DirectoryInfo Directory) ReadAndValidate(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The owner service boundary is Windows-only.");
        }

        var fullPath = Path.GetFullPath(path);
        var file = new FileInfo(fullPath);
        var directory = file.Directory ?? throw new InvalidDataException("Invalid service settings path.");
        if (!file.Exists || (file.Attributes & FileAttributes.ReparsePoint) != 0 ||
            !directory.Exists || (directory.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Service settings must be a regular file in a restricted directory.");
        }

        using var identity = WindowsIdentity.GetCurrent();
        var serviceSid = identity.User?.Value ?? throw new InvalidDataException("The service process has no user SID.");
        var privateSettings = ProtectedFile.ReadPrivateFile(fullPath, serviceSid, MaximumSettingsBytes);
        var settings = JsonSerializer.Deserialize<ServiceSettings>(privateSettings.Text, JsonOptions)
            ?? throw new InvalidDataException("Invalid service settings.");
        return (settings, privateSettings.OwnerSid, directory);
    }

    private static void ValidateOwner(ServiceSettings settings, string fileOwnerSid)
    {
        if (string.IsNullOrWhiteSpace(settings.OwnerSid) ||
            !string.Equals(new SecurityIdentifier(settings.OwnerSid).Value,
                new SecurityIdentifier(fileOwnerSid).Value, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The configured owner SID must match the settings file owner.");
        }
    }
    private static void WriteCurrentSettingsAtomically(string settingsPath, ServiceSettings settings)
    {
        var file = new FileInfo(settingsPath);
        var security = FileSystemAclExtensions.GetAccessControl(
            file, AccessControlSections.Access | AccessControlSections.Owner);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var content = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = ServiceSettings.CurrentSchemaVersion,
            ownerSid = settings.OwnerSid,
            controlPipe = settings.ControlPipe,
            agentPipe = settings.AgentPipe
        }, JsonOptions);
        var temporaryPath = Path.Combine(file.DirectoryName
            ?? throw new InvalidDataException("Invalid service settings path."),
            ".service-settings." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(
                temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                FileSystemAclExtensions.SetAccessControl(new FileInfo(temporaryPath), security);
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

    private static void ValidateLegacy(ServiceSettings settings, string fileOwnerSid)
    {
        ValidateOwner(settings, fileOwnerSid);
        ValidatePipeName(settings.ControlPipe);
        var resources = settings.ManagedResources ?? Array.Empty<ManagedResourceBinding>();
        var resourceIds = new HashSet<string>(StringComparer.Ordinal);
        var fileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var binding in resources)
        {
            if (binding is null || !IsSafeIdentifier(binding.ResourceId) || !IsSafeIdentifier(binding.ZoneId) ||
                !resourceIds.Add(binding.ResourceId) || !IsPlainFileName(binding.FileName) || !fileNames.Add(binding.FileName))
            {
                throw new InvalidDataException("Managed resource bindings must have unique IDs and plain file names.");
            }
        }
    }


    private static bool IsPlainFileName(string? fileName) =>
        !string.IsNullOrEmpty(fileName) && fileName!.Length <= 128 &&
        fileName!.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
        !fileName!.Contains('/') && !fileName!.Contains('\\') &&
        !string.Equals(fileName, ".", StringComparison.Ordinal) &&
        !string.Equals(fileName, "..", StringComparison.Ordinal);

    private static void ValidatePipeName(string name)
    {
        if (!IsSafeIdentifier(name))
        {
            throw new InvalidDataException("Invalid owner pipe endpoint name.");
        }
    }

    [GeneratedRegex("\\A[A-Za-z0-9][A-Za-z0-9._-]{0,127}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex SafeIdentifierRegex();
}
