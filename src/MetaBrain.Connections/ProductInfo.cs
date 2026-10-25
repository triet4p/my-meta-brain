using System.Reflection;

namespace MetaBrain.Connections;

/// <summary>
/// The one product version every executable reports and the release bundle is
/// named after. It is the shared build metadata (<c>Directory.Build.props</c>),
/// so the CLI, the owner console, the MCP entrypoint, the shared host and the
/// installer always report the same value.
/// </summary>
internal static class ProductInfo
{
    internal const string ProductName = "Meta Brain";

    /// <summary>The product version, without a source-revision suffix.</summary>
    internal static string Version { get; } = ReadVersion();

    /// <summary>The file name of the entrypoint that is running.</summary>
    internal static string EntrypointName =>
        Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "MetaBrain");

    private static string ReadVersion()
    {
        var assembly = typeof(ProductInfo).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var revision = informational.IndexOf('+', StringComparison.Ordinal);
            return revision < 0 ? informational : informational[..revision];
        }

        var version = assembly.GetName().Version;
        return version is null ? "0.0.0" : version.ToString(3);
    }
}
