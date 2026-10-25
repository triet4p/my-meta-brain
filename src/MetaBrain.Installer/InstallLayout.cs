using System.Text.Json;
using System.Text.Json.Serialization;

namespace MetaBrain.Installer;

/// <summary>
/// The installed layout. The application files live in the install root, the
/// owner-private data (vault, source snapshots, backups, recovery material)
/// lives in the data root, and the two are deliberately separate so an
/// uninstall can remove the application while keeping the data, or upgrade
/// the application in place without touching the vault.
/// </summary>
internal sealed record InstallLayout(
    string InstallRoot,
    string ServiceDirectory,
    string OwnerUiDirectory,
    string McpDirectory,
    string CliDirectory,
    string InstallerDirectory,
    string ServiceExecutable,
    string OwnerUiExecutable,
    string McpExecutable,
    string CliExecutable,
    string SettingsPath,
    string DataRoot)
{
    internal const string StateFileName = "install-state.json";
    internal const string ServiceDirectoryName = "service";
    internal const string OwnerUiDirectoryName = "owner-ui";
    internal const string McpDirectoryName = "mcp";
    internal const string CliDirectoryName = "cli";
    internal const string InstallerDirectoryName = "installer";

    internal string StatePath => Path.Combine(InstallRoot, StateFileName);

    internal string RuntimeDirectory => Path.Combine(DataRoot, "runtime");

    /// <summary>
    /// The layout of an installation. The installer is installed under
    /// <c>&lt;install root&gt;/installer</c>, so the install root is the parent
    /// of the directory that holds this executable; a copy of the installer
    /// that was not installed walks up to the directory that carries the
    /// installation state. An explicit install root always wins.
    /// </summary>
    internal static InstallLayout Locate(string? installRoot = null)
    {
        if (!string.IsNullOrWhiteSpace(installRoot))
        {
            var explicitRoot = Path.GetFullPath(installRoot).TrimEnd(Path.DirectorySeparatorChar);
            var explicitState = InstallState.Read(Path.Combine(explicitRoot, StateFileName))
                ?? throw new InvalidOperationException("Meta Brain is not installed in " + explicitRoot + ".");
            return Create(explicitRoot, explicitState.DataRoot);
        }

        var directory = new DirectoryInfo(Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar));
        for (var depth = 0; depth < 4 && directory is not null; depth++, directory = directory.Parent)
        {
            var state = InstallState.Read(Path.Combine(directory.FullName, StateFileName));
            if (state is not null)
            {
                return Create(directory.FullName, state.DataRoot);
            }
        }

        throw new InvalidOperationException("Meta Brain is not installed here (no install-state.json).");
    }

    internal static InstallLayout Create(string installRoot, string dataRoot) =>
        new(
            Path.GetFullPath(installRoot).TrimEnd(Path.DirectorySeparatorChar),
            Path.Combine(Path.GetFullPath(installRoot), ServiceDirectoryName),
            Path.Combine(Path.GetFullPath(installRoot), OwnerUiDirectoryName),
            Path.Combine(Path.GetFullPath(installRoot), McpDirectoryName),
            Path.Combine(Path.GetFullPath(installRoot), CliDirectoryName),
            Path.Combine(Path.GetFullPath(installRoot), InstallerDirectoryName),
            Path.Combine(Path.GetFullPath(installRoot), ServiceDirectoryName, "MetaBrain.Connections.exe"),
            Path.Combine(Path.GetFullPath(installRoot), OwnerUiDirectoryName, "MetaBrain.OwnerUi.exe"),
            Path.Combine(Path.GetFullPath(installRoot), McpDirectoryName, "MetaBrain.Mcp.exe"),
            Path.Combine(Path.GetFullPath(installRoot), CliDirectoryName, "MetaBrain.Cli.exe"),
            Path.Combine(Path.GetFullPath(dataRoot), "service-settings.json"),
            Path.GetFullPath(dataRoot).TrimEnd(Path.DirectorySeparatorChar));
}

/// <summary>
/// The installer's own state file. It records where the instance lives and
/// when it was installed; it holds no key, no token and no private content,
/// and it is removed by an uninstall.
/// </summary>
internal sealed record InstallState(
    int SchemaVersion,
    string InstalledAtUtc,
    string DataRoot,
    string ProductVersion)
{
    internal const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    internal static InstallState? Read(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var state = JsonSerializer.Deserialize<InstallState>(File.ReadAllText(path), JsonOptions);
        return state is { SchemaVersion: CurrentSchemaVersion } &&
            !string.IsNullOrWhiteSpace(state.DataRoot)
            ? state
            : null;
    }

    internal void Write(string path) =>
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
}
