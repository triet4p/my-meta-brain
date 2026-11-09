using System.Globalization;
using MetaBrain.Connections;
using MetaBrain.Installer;

namespace MetaBrain.S1T4.Smoke;

internal static class S15T6RuntimePrerequisiteSmoke
{
    internal static async Task RunAsync()
    {
        var minimum = VisualCppRuntimePrerequisite.MinimumVersion;
        Require(VisualCppRuntimePrerequisite.ParseInstalledVersion("v14.43.34808.0") is { } parsed && parsed >= minimum,
            "The x64 runtime version string did not parse at the required minimum.");
        Require(VisualCppRuntimePrerequisite.ParseInstalledVersion(" 14.50.35719.0 ") is { } newer && newer >= minimum,
            "A newer x64 runtime version was rejected.");
        Require(VisualCppRuntimePrerequisite.ParseInstalledVersion("v14.43.34807.0") is { } older && older < minimum,
            "An older x64 runtime version was accepted.");
        Require(VisualCppRuntimePrerequisite.ParseInstalledVersion("not-a-version") is null,
            "A malformed runtime version was accepted.");
        Require(VisualCppRuntimePrerequisite.ParseInstalledVersion(null) is null,
            "A missing runtime version was accepted.");
        Require(!VisualCppRuntimePrerequisite.IsSatisfied(null) &&
                !VisualCppRuntimePrerequisite.IsSatisfied(new Version(14, 43, 34807)) &&
                VisualCppRuntimePrerequisite.IsSatisfied(minimum),
            "The x64 runtime minimum-version boundary is incorrect.");

        var setupMissing = await CaptureErrorAsync(() =>
            CliSetup.InstallAsync(Array.Empty<string>(), () => null));
        Require(setupMissing.ExitCode == 1, "CLI setup did not refuse a missing x64 runtime.");
        Require(setupMissing.Error.Contains(VisualCppRuntimePrerequisite.OfficialDownloadPage, StringComparison.Ordinal) &&
                setupMissing.Error.Contains("before extracting", StringComparison.Ordinal),
            "CLI setup refusal omitted the official instructions or pre-extraction boundary.");
        await ExpectArgumentExceptionAsync(
            () => CliSetup.InstallAsync(Array.Empty<string>(), () => minimum),
            "CLI setup did not proceed to argument validation after a supported runtime check.");

        var installerMissing = await CaptureErrorAsync(() =>
            InstallCommand.RunAsync(Array.Empty<string>(), () => null));
        Require(installerMissing.ExitCode == 1, "The direct installer API did not refuse a missing x64 runtime.");
        Require(installerMissing.Error.Contains("before stopping the shared host", StringComparison.Ordinal),
            "The direct installer refusal did not occur before service or installation changes.");
        await ExpectArgumentExceptionAsync(
            () => InstallCommand.RunAsync(Array.Empty<string>(), () => minimum),
            "The direct installer API did not proceed to argument validation after a supported runtime check.");

        var mcpDataRoot = Path.Combine(AppContext.BaseDirectory, "s15-t6-prerequisite-refusal-" + Guid.NewGuid().ToString("N"));
        Require(!Directory.Exists(mcpDataRoot), "The MCP refusal fixture path already exists.");
        var mcpMissing = await CaptureErrorAsync(() =>
            McpAgentProduct.RunAsync(new[] { "--data-root", mcpDataRoot }, () => null));
        Require(mcpMissing.ExitCode == 1, "MCP startup did not refuse a missing x64 runtime.");
        Require(mcpMissing.Error.Contains(VisualCppRuntimePrerequisite.OfficialDownloadPage, StringComparison.Ordinal) &&
                !Directory.Exists(mcpDataRoot),
            "MCP startup omitted the official instructions or created the data root before refusal.");
        var mcpSupportedInvalidArguments = await CaptureErrorAsync(() =>
            McpAgentProduct.RunAsync(Array.Empty<string>(), () => minimum));
        Require(mcpSupportedInvalidArguments.ExitCode == 1 &&
                mcpSupportedInvalidArguments.Error.Contains("ArgumentException", StringComparison.Ordinal),
            "MCP startup did not proceed to argument validation after a supported runtime check.");

        var hostVersion = VisualCppRuntimePrerequisite.ReadInstalledX64Version();
        Console.WriteLine(hostVersion is null
            ? "Read-only host check: x64 Visual C++ v14 runtime missing or version unavailable."
            : "Read-only host check: x64 Visual C++ v14 runtime " + hostVersion + ".");
        Console.WriteLine("PASS S15-T6 x64 runtime refusal and supported-version branches; no installer, download, registry write, or data-root creation was run.");
    }

    private static async Task<(int ExitCode, string Error)> CaptureErrorAsync(Func<Task<int>> operation)
    {
        var originalError = Console.Error;
        using var capturedError = new StringWriter(CultureInfo.InvariantCulture);
        try
        {
            Console.SetError(capturedError);
            var exitCode = await operation().ConfigureAwait(false);
            return (exitCode, capturedError.ToString());
        }
        finally
        {
            Console.SetError(originalError);
        }
    }

    private static async Task ExpectArgumentExceptionAsync(Func<Task<int>> operation, string failureMessage)
    {
        try
        {
            _ = await operation().ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            return;
        }

        throw new InvalidOperationException(failureMessage);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
