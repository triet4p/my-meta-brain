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
    ManagedResourceBinding[]? ManagedResources = null)
{
    public const int CurrentSchemaVersion = 1;
    public string PolicyStorePath { get; init; } = string.Empty;
    public string ResourceRootPath { get; init; } = string.Empty;
}

internal static partial class ServiceSettingsLoader
{
    private const int MaximumSettingsBytes = 128 * 1024;
    private static readonly Regex SafeIdentifier = SafeIdentifierRegex();
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static ServiceSettings Load(string path)
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

        Validate(settings, privateSettings.OwnerSid);
        return settings with
        {
            PolicyStorePath = Path.Combine(directory.FullName, "grant-policy.json"),
            ResourceRootPath = Path.Combine(directory.FullName, "resources"),
        };
    }

    public static bool IsSafeIdentifier(string? value) => value is not null && SafeIdentifier.IsMatch(value);

    private static void Validate(ServiceSettings settings, string fileOwnerSid)
    {
        if (settings.SchemaVersion != ServiceSettings.CurrentSchemaVersion || string.IsNullOrWhiteSpace(settings.OwnerSid))
        {
            throw new InvalidDataException("Unsupported or invalid owner service settings schema.");
        }

        var configuredOwnerSid = new SecurityIdentifier(settings.OwnerSid).Value;
        if (!string.Equals(configuredOwnerSid, new SecurityIdentifier(fileOwnerSid).Value, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The configured owner SID must match the settings file owner.");
        }

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
