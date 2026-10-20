using System.Security.Cryptography;
using System.Text.Json;

namespace MetaBrain.Connections;

/// <summary>
/// Creates the service settings file for an installed instance. The file is
/// the single source of truth for the owner SID and the two pipe endpoint
/// names, and it lives in the data root. An upgrade reuses the existing
/// file, so the pipe names an owner already configured in a client keep
/// working.
/// </summary>
internal static class ServiceSettingsFile
{
    internal const string FileName = "service-settings.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    internal static string ResolvePath(string dataRoot) => Path.Combine(dataRoot, FileName);

    internal static ServiceSettings Create(string dataRoot, string ownerSid, string controlPipe, string agentPipe)
    {
        var root = Path.GetFullPath(dataRoot);
        Directory.CreateDirectory(root);
        var path = ResolvePath(root);
        var settings = new ServiceSettings(ServiceSettings.CurrentSchemaVersion, ownerSid, controlPipe, agentPipe)
        {
            DataRootPath = root,
            VaultDirectoryPath = Path.Combine(root, "vault")
        };
        WriteAtomically(path, settings);
        return settings;
    }

    private static void WriteAtomically(string path, ServiceSettings settings)
    {
        var content = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = settings.SchemaVersion,
            ownerSid = settings.OwnerSid,
            controlPipe = settings.ControlPipe,
            agentPipe = settings.AgentPipe
        }, JsonOptions);
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(
                temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, path, overwrite: true);
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
}
