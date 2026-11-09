using Microsoft.Win32;
using System.Security;

namespace MetaBrain.Connections;

/// <summary>
/// Read-only detection for the native tokenizer's supported x64 MSVC runtime.
/// The installer never obtains or installs this prerequisite.
/// </summary>
internal static class VisualCppRuntimePrerequisite
{
    internal const string OfficialDownloadPage = "https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist?view=msvc-170";
    internal static Version MinimumVersion { get; } = new(14, 43, 34808);

    internal static Version? ReadInstalledX64Version()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            using var localMachine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var runtimeKey = localMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64", writable: false);
            return ParseInstalledVersion(runtimeKey?.GetValue("Version") as string);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            return null;
        }
    }

    internal static Version? ParseInstalledVersion(string? value)
    {
        if (value is null)
        {
            return null;
        }

        value = value.Trim();
        if (value.StartsWith('v') || value.StartsWith('V'))
        {
            value = value[1..];
        }

        return Version.TryParse(value, out var version) ? version : null;
    }

    internal static bool IsSatisfied(Version? installedVersion) =>
        installedVersion is not null && installedVersion >= MinimumVersion;

    internal static bool TryReportRefusal(Version? installedVersion, string productName, TextWriter error)
    {
        if (IsSatisfied(installedVersion))
        {
            return false;
        }

        error.WriteLine($"{productName} requires Microsoft Visual C++ v14 x64 Redistributable {MinimumVersion} or later.");
        error.WriteLine(installedVersion is null
            ? "The installed x64 runtime is missing or its version could not be verified."
            : $"The installed x64 runtime version {installedVersion} is too old.");
        error.WriteLine("Install the prerequisite directly from Microsoft, then run this command again:");
        error.WriteLine(OfficialDownloadPage);
        error.WriteLine("Meta Brain does not download, bundle, or install this prerequisite.");
        return true;
    }
}
