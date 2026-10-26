using MetaBrain.Connections;
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;

namespace MetaBrain.Installer;

/// <summary>
/// Front door compiled only into the dedicated CLI setup executable. It carries
/// the exact release payload and delegates installation/removal to the shared
/// installer so roots, settings, lifecycle and component ownership stay single.
/// </summary>
internal static class CliSetup
{
    private const string PayloadResourceName = "MetaBrain.Installer.CliPayload.zip";
    private static readonly Assembly Assembly = typeof(CliSetup).Assembly;
    private static readonly string? CandidateIdentity = Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(attribute => attribute.Key == "CliSetupCandidateIdentity")?.Value;
    private static readonly string? ExpectedPayloadSha256 = Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(attribute => attribute.Key == "CliSetupPayloadSha256")?.Value;

    internal static bool IsBundledSetup => Assembly.GetManifestResourceInfo(PayloadResourceName) is not null;

    internal static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 1 && args[0] == "--verify-payload")
        {
            await VerifyEmbeddedPayloadAsync().ConfigureAwait(false);
            return 0;
        }
        if (args.Length == 1 && args[0] is "--help" or "-h" or "help")
        {
            WriteHelp();
            return 0;
        }

        if (args.Length == 1 && args[0] is "--version" or "-v")
        {
            Console.WriteLine("MetaBrain CLI Setup " + ProductInfo.Version);
            return 0;
        }

        try
        {
            if (args.Length == 0)
            {
                args = PromptForInstall();
                if (args.Length == 0)
                {
                    Console.WriteLine("CANCELLED — no installation was changed.");
                    return 0;
                }
            }

            return args[0] switch
            {
                "setup" => await RunAsync(args[1..]).ConfigureAwait(false),
                "install" => await InstallAsync(args[1..]).ConfigureAwait(false),
                "uninstall" => await UninstallAsync(args[1..]).ConfigureAwait(false),
                _ => throw new ArgumentException("Use install, uninstall, --help, or --version.")
            };
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("CLI setup failed: " + exception.Message);
            return 1;
        }
    }

    private static string[] PromptForInstall()
    {
        Console.WriteLine("Meta Brain CLI Setup " + ProductInfo.Version);
        Console.WriteLine("Unsigned setup; SHA-256 checks bytes but does not authenticate the publisher.");
        Console.WriteLine("Enter n or q at the confirmation prompt to cancel without changing the installation.");
        Console.Write("Install or upgrade the CLI? [Y/n] ");
        var confirmation = Console.ReadLine();
        if (confirmation is null || (confirmation.Trim().Length > 0 &&
            !confirmation.Trim().Equals("y", StringComparison.OrdinalIgnoreCase) &&
            !confirmation.Trim().Equals("yes", StringComparison.OrdinalIgnoreCase)))
        {
            return Array.Empty<string>();
        }

        var defaultRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MetaBrain", "App");
        Console.Write("Install root [" + defaultRoot + "]: ");
        var installRoot = Console.ReadLine();
        installRoot = string.IsNullOrWhiteSpace(installRoot) ? defaultRoot : installRoot;
        var existing = InstallState.Read(Path.Combine(Path.GetFullPath(installRoot), InstallLayout.StateFileName));
        var defaultData = existing?.DataRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MetaBrain", "Data");
        Console.Write("Data root [" + defaultData + "]: ");
        var dataRoot = Console.ReadLine();
        dataRoot = string.IsNullOrWhiteSpace(dataRoot) ? defaultData : dataRoot;
        return new[] { "install", "--install-root", installRoot, "--data-root", dataRoot };
    }
    private static Task VerifyEmbeddedPayloadAsync()
    {
        using var payload = Assembly.GetManifestResourceStream(PayloadResourceName)
            ?? throw new InvalidOperationException("This setup executable does not contain its release payload.");
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload)).ToLowerInvariant();
        if (ExpectedPayloadSha256 is null || !string.Equals(digest, ExpectedPayloadSha256, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Embedded setup payload hash differs from the candidate build metadata.");
        }

        payload.Position = 0;
        using var archive = new ZipArchive(payload, ZipArchiveMode.Read, leaveOpen: true);
        var files = archive.Entries
            .Where(entry => !string.IsNullOrEmpty(entry.Name))
            .Select(entry =>
            {
                using var entryStream = entry.Open();
                var fileHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(entryStream)).ToLowerInvariant();
                return new { path = entry.FullName.Replace('\\', '/'), bytes = entry.Length, sha256 = fileHash };
            })
            .OrderBy(file => file.path, StringComparer.Ordinal)
            .ToArray();
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { bytes = payload.Length, sha256 = digest, files }));
        return Task.CompletedTask;
    }

    private static async Task<int> InstallAsync(string[] args)
    {
        var options = InstallerOptions.Parse(args, new[] { "--install-root", "--data-root" });
        var installRoot = Path.GetFullPath(options.Required("--install-root"));
        var existing = InstallState.Read(Path.Combine(installRoot, InstallLayout.StateFileName));
        var dataRoot = options.Optional("--data-root") ?? existing?.DataRoot ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MetaBrain", "Data");
        var fullDataRoot = Path.GetFullPath(dataRoot);
        var temporaryRoot = Path.Combine(Path.GetTempPath(), "MetaBrain-CLI-Setup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryRoot);
        try
        {
            await using (var payload = Assembly.GetManifestResourceStream(PayloadResourceName)
                ?? throw new InvalidOperationException("This setup executable does not contain its release payload."))
            {
                var payloadHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload)).ToLowerInvariant();
                if (ExpectedPayloadSha256 is null || !string.Equals(payloadHash, ExpectedPayloadSha256, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("The embedded candidate payload failed its SHA-256 integrity check.");
                }

                payload.Position = 0;
                using var archive = new ZipArchive(payload, ZipArchiveMode.Read, leaveOpen: false);
                archive.ExtractToDirectory(temporaryRoot);
            }

            var installer = Path.Combine(temporaryRoot, InstallLayout.InstallerDirectoryName, "MetaBrain.Installer.exe");
            var start = new ProcessStartInfo(installer) { UseShellExecute = false, WorkingDirectory = temporaryRoot };
            foreach (var argument in new[]
                     {
                         "install", "--payload", temporaryRoot,
                         "--install-root", installRoot,
                         "--data-root", fullDataRoot,
                         "--component", "cli",
                         "--candidate-id", CandidateIdentity ?? throw new InvalidOperationException("The setup candidate identity is missing.")
                     })
            {
                start.ArgumentList.Add(argument);
            }

            using var process = Process.Start(start)
                ?? throw new InvalidOperationException("The shared installer could not be started.");
            await process.WaitForExitAsync().ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                Console.Error.WriteLine("The shared installer refused or failed the CLI installation (exit " + process.ExitCode + "). No success is reported; review the message above and correct the issue before retrying.");
                return process.ExitCode;
            }

            Console.WriteLine("CLI installed at " + Path.Combine(installRoot, InstallLayout.CliDirectoryName, "MetaBrain.Cli.exe"));
            Console.WriteLine("Use the installed executable path directly; setup does not modify PATH or profiles.");
            return 0;
        }
        finally
        {
            try
            {
                Directory.Delete(temporaryRoot, recursive: true);
            }
            catch (IOException)
            {
                Console.Error.WriteLine("Setup payload cleanup could not complete; remove this temporary directory after closing any related process: " + temporaryRoot);
            }
            catch (UnauthorizedAccessException)
            {
                Console.Error.WriteLine("Setup payload cleanup was denied; remove this temporary directory after closing any related process: " + temporaryRoot);
            }
        }
    }

    private static async Task<int> UninstallAsync(string[] args)
    {
        var options = InstallerOptions.Parse(args, new[] { "--install-root" });
        var installRoot = Path.GetFullPath(options.Required("--install-root"));
        var state = InstallState.Read(Path.Combine(installRoot, InstallLayout.StateFileName))
            ?? throw new InvalidOperationException("Meta Brain CLI is not registered in this install root: " + installRoot);
        if (state.Components?.Contains("cli", StringComparer.Ordinal) != true)
        {
            throw new InvalidOperationException("The shared installation does not have the CLI component registered.");
        }
        var remainingComponents = (state.Components ?? Array.Empty<string>())
            .Where(component => !string.Equals(component, "cli", StringComparison.Ordinal))
            .ToArray();

        var installer = Path.Combine(installRoot, InstallLayout.InstallerDirectoryName, "MetaBrain.Installer.exe");
        if (!File.Exists(installer))
        {
            throw new FileNotFoundException("The shared installer is missing; reinstall the matching CLI candidate before uninstalling.", installer);
        }

        var start = new ProcessStartInfo(installer) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(installer)! };
        start.ArgumentList.Add("uninstall");
        start.ArgumentList.Add("--component");
        start.ArgumentList.Add("cli");
        start.ArgumentList.Add("--defer-root-removal");
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("The shared installer could not be started.");
        await process.WaitForExitAsync().ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            Console.Error.WriteLine("The shared installer did not complete CLI removal (exit " + process.ExitCode + ").");
            return process.ExitCode;
        }

        if (remainingComponents.Length == 0)
        {
            if (string.Equals(installRoot, Path.GetPathRoot(installRoot)?.TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine("Refusing to remove a drive-root installation path; application cleanup requires an ordinary install directory. Data remains at " + state.DataRoot);
                return 1;
            }

            try
            {
                if (Directory.Exists(installRoot) &&
                    (new DirectoryInfo(installRoot).Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException("The install root became a reparse point.");
                }

                if (Directory.Exists(installRoot))
                {
                    Directory.Delete(installRoot, recursive: true);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine("CLI files were removed, but the application root could not be deleted. Close related processes and retry cleanup of " + installRoot + "; data remains at " + state.DataRoot + ". " + exception.Message);
                return 1;
            }

            if (Directory.Exists(installRoot))
            {
                Console.Error.WriteLine("CLI files were removed, but the application root remains. Close related processes and retry cleanup of " + installRoot + "; data remains at " + state.DataRoot);
                return 1;
            }
        }
        else
        {
            var updated = InstallState.Read(Path.Combine(installRoot, InstallLayout.StateFileName));
            if (updated?.Components?.Contains("cli", StringComparer.Ordinal) == true ||
                Directory.Exists(Path.Combine(installRoot, InstallLayout.CliDirectoryName)))
            {
                Console.Error.WriteLine("CLI component files remain after uninstall. Close processes using them and retry.");
                return 1;
            }
        }

        Console.WriteLine("CLI uninstalled; shared runtime and data are retained when another component depends on them.");
        return 0;
    }

    private static void WriteHelp()
    {
        Console.WriteLine("Meta Brain CLI Setup " + ProductInfo.Version);
        Console.WriteLine("Install interactively, or use: setup.exe install --install-root <dir> [--data-root <dir>]");
        Console.WriteLine("At the confirmation prompt, enter n or q to cancel without changing the installation.");
        Console.WriteLine("Remove only the CLI component: setup.exe uninstall --install-root <dir>");
        Console.WriteLine("The shared runtime and data root are preserved when another component uses the installation.");
        Console.WriteLine("The installed CLI is cli\\MetaBrain.Cli.exe. No PATH/profile, autostart, service, registry or permission changes are made.");
        Console.WriteLine("Setup is unsigned; SHA-256 does not authenticate the publisher.");
        Console.WriteLine("The self-contained .NET host may extract runtime support files under the current user's %TEMP%\\.net bundle cache; setup does not change environment settings.");
    }
}
