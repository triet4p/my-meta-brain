using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using MetaBrain.Connections;
using MetaBrain.Installer;

namespace MetaBrain.S1T4.Smoke;

/// <summary>
/// G2-T1 proof for the packaged command and its explicit user-PATH ownership
/// contract. The real installer runs against isolated synthetic roots for
/// install, upgrade and uninstall; the per-user PATH is a fixture store so no
/// real environment value is ever read or written. The native
/// <c>metabrain.exe</c> entry is exercised from a foreign working directory,
/// and the historical <c>MetaBrain.Cli.exe</c> command set is checked to stay
/// unchanged.
/// </summary>
internal static class G2T1InstallPathSmoke
{
    private const string FirstEntry = @"C:\first";
    private const string QuotedEntry = @"C:\Quoted Entry";
    private const string LastEntry = @"C:\last";

    internal static async Task RunAsync(string installer)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The G2-T1 install/PATH smoke is Windows-only.");
        }

        VerifyPathTextRules();

        using var fixture = S12T5InstallFixture.Create();
        fixture.ReplaceWithRealCandidatePayload(installer);

        await VerifyOptInUpgradeAndRemovalAsync(fixture).ConfigureAwait(false);
        await VerifyPreExistingEntryIsNotClaimedAsync(fixture).ConfigureAwait(false);
        await VerifyWriteFailureIsTruthfulAsync(fixture).ConfigureAwait(false);
        await VerifyLiveFrontDoorAsync(fixture).ConfigureAwait(false);
        await VerifyLegacySettingsMigrationAsync(fixture).ConfigureAwait(false);

        Console.WriteLine("PASS G2-T1 native metabrain entry, workspace-first defaults, single live host, legacy plaintext settings migration, opt-in user PATH append/upgrade/removal ownership, pre-existing entry preservation and truthful write failure");
    }

    private static void VerifyPathTextRules()
    {
        var baseline = FirstEntry + ";\"" + QuotedEntry + "\";" + LastEntry;
        Require(UserPathText.Append(baseline, @"C:\new") == baseline + @";C:\new",
            "Appending did not preserve the existing PATH text and order.");
        Require(UserPathText.Append(baseline, @"c:\FIRST") == baseline,
            "Appending did not treat an existing entry case-insensitively.");
        Require(UserPathText.Append(@"C:\a;C:\b;", @"C:\c") == @"C:\a;C:\b;C:\c",
            "Appending after a trailing separator introduced an empty entry.");
        var (removedValue, removed) = UserPathText.Remove(@"C:\a;C:\b;C:\a", @"C:\a");
        Require(removed && removedValue == @"C:\a;C:\b",
            "Removal did not delete exactly the last occurrence and keep the rest in order.");
        Require(!UserPathText.Remove(@"C:\a", @"C:\b").Removed,
            "Removal reported an entry that is not present.");
    }

    private static async Task VerifyOptInUpgradeAndRemovalAsync(S12T5InstallFixture fixture)
    {
        var installRoot = Path.Combine(fixture.CasesRoot, "opt-in", "install");
        var dataRoot = Path.Combine(fixture.CasesRoot, "opt-in", "data");
        var cliDirectory = Path.Combine(installRoot, "cli");
        var baseline = FirstEntry + ";\"" + QuotedEntry + "\";" + LastEntry;
        var store = new FakeUserPathStore(baseline);

        var installExit = await InstallCommand.RunAsync(
            InstallArguments(fixture, installRoot, dataRoot, addToPath: true),
            RuntimePresent, store).ConfigureAwait(false);
        Require(installExit == 0, "The opt-in CLI install did not complete.");
        Require(store.Value == baseline + ";" + cliDirectory,
            "The opt-in install did not append exactly the CLI directory to the user PATH: " + store.Value);

        var state = InstallState.Read(Path.Combine(installRoot, InstallStateFileName));
        Require(state?.CommandPathOwned == true &&
                string.Equals(state.CommandPath, cliDirectory, StringComparison.OrdinalIgnoreCase),
            "The install state did not record the PATH entry it added.");

        var cliExecutable = Path.Combine(cliDirectory, "MetaBrain.Cli.exe");
        var commandExecutable = Path.Combine(cliDirectory, "metabrain.exe");
        Require(File.Exists(cliExecutable) && File.Exists(commandExecutable),
            "The install did not create both native entrypoints.");
        var cliBytes = await File.ReadAllBytesAsync(cliExecutable).ConfigureAwait(false);
        var commandBytes = await File.ReadAllBytesAsync(commandExecutable).ConfigureAwait(false);
        Require(cliBytes.AsSpan().SequenceEqual(commandBytes),
            "The installed metabrain command is not byte-identical to the CLI apphost.");

        var version = await RunEntrypointAsync(fixture.Root, commandExecutable, "--version").ConfigureAwait(false);
        Require(version.ExitCode == 0 && version.StandardOutput.Trim() == "metabrain " + ProductInfo.Version,
            "The installed metabrain command did not report its own name and the product version: " +
            version.StandardOutput.Trim() + " / " + version.StandardError.Trim());

        var help = await RunEntrypointAsync(fixture.Root, commandExecutable, "--help").ConfigureAwait(false);
        Require(help.ExitCode == 0 && help.StandardError.Contains("workspace-first", StringComparison.Ordinal),
            "The installed metabrain command did not present the workspace-first front door.");

        // A front-door default must be the exact workspace operation, not a
        // new command. The absent pipe is answered by the same workspace-read
        // code path as the explicit verb, so the exit code and the service
        // error are identical; the workspace command group reports a service
        // failure as invalid-input/unavailable exit 4. The legacy CLI still
        // reports the default verb as unknown below, so routing is observable.
        const string absentPipe = "metabrain.g2t1.absent";
        const string absentFileId = "0123456789abcdef0123456789abcdef";
        var read = await RunEntrypointAsync(fixture.Root, commandExecutable,
            "read", "--control-pipe", absentPipe, "--file-id", absentFileId).ConfigureAwait(false);
        var workspaceRead = await RunEntrypointAsync(fixture.Root, commandExecutable,
            "workspace-read", "--control-pipe", absentPipe, "--file-id", absentFileId).ConfigureAwait(false);
        Require(read.ExitCode == workspaceRead.ExitCode && read.ExitCode != 0 &&
                !read.StandardError.Contains("unknown_command", StringComparison.Ordinal) &&
                !read.StandardOutput.Contains("unknown_command", StringComparison.Ordinal) &&
                read.StandardError.Contains("transport_unavailable", StringComparison.Ordinal),
            "The default read did not route to the exact workspace-read path (exit=" +
            read.ExitCode.ToString(CultureInfo.InvariantCulture) + "; stderr=" + read.StandardError.Trim() +
            "; explicit exit=" + workspaceRead.ExitCode.ToString(CultureInfo.InvariantCulture) + ").");

        var search = await RunEntrypointAsync(fixture.Root, commandExecutable,
            "search", "--control-pipe", absentPipe, "--query", "g2-t1-routing").ConfigureAwait(false);
        var workspaceSearch = await RunEntrypointAsync(fixture.Root, commandExecutable,
            "workspace-search", "--control-pipe", absentPipe, "--query", "g2-t1-routing").ConfigureAwait(false);
        Require(search.ExitCode == workspaceSearch.ExitCode && search.ExitCode != 0 &&
                !search.StandardError.Contains("unknown_command", StringComparison.Ordinal) &&
                !search.StandardOutput.Contains("unknown_command", StringComparison.Ordinal) &&
                search.StandardError.Contains("transport_unavailable", StringComparison.Ordinal),
            "The default search did not route to the exact workspace-search path (exit=" +
            search.ExitCode.ToString(CultureInfo.InvariantCulture) + "; stderr=" + search.StandardError.Trim() +
            "; explicit exit=" + workspaceSearch.ExitCode.ToString(CultureInfo.InvariantCulture) + ").");

        var legacyRead = await RunEntrypointAsync(fixture.Root, cliExecutable,
            "read", "--control-pipe", "metabrain.g2t1.absent", "--file-id", "0123456789abcdef0123456789abcdef")
            .ConfigureAwait(false);
        Require(legacyRead.ExitCode == 2 &&
                legacyRead.StandardError.Contains("unknown_command", StringComparison.Ordinal),
            "The historical CLI unexpectedly accepted a workspace-first default verb.");
        var legacyVersion = await RunEntrypointAsync(fixture.Root, cliExecutable, "--version").ConfigureAwait(false);
        Require(legacyVersion.ExitCode == 0 && legacyVersion.StandardOutput.StartsWith("MetaBrain.Cli ", StringComparison.Ordinal),
            "The historical CLI entrypoint output changed.");

        var upgradeExit = await InstallCommand.RunAsync(
            InstallArguments(fixture, installRoot, dataRoot, addToPath: true),
            RuntimePresent, store).ConfigureAwait(false);
        Require(upgradeExit == 0 && store.Value == baseline + ";" + cliDirectory,
            "The in-place upgrade duplicated or changed the owned PATH entry: " + store.Value);

        var uninstallExit = await InstallCommand.UninstallAsync(
            new[] { "--component", "cli", "--defer-root-removal" }, installRoot, store).ConfigureAwait(false);
        Require(uninstallExit == 0, "The CLI uninstall did not complete.");
        Require(store.Value == baseline,
            "Uninstall did not remove exactly the entry it added and preserve every other entry: " + store.Value);
        Require(!Directory.Exists(cliDirectory), "Uninstall did not remove the CLI component.");
    }

    private static async Task VerifyPreExistingEntryIsNotClaimedAsync(S12T5InstallFixture fixture)
    {
        var installRoot = Path.Combine(fixture.CasesRoot, "pre-existing", "install");
        var dataRoot = Path.Combine(fixture.CasesRoot, "pre-existing", "data");
        var cliDirectory = Path.Combine(installRoot, "cli");
        var baseline = FirstEntry + ";" + LastEntry;
        var store = new FakeUserPathStore(baseline + ";" + cliDirectory);

        var installExit = await InstallCommand.RunAsync(
            InstallArguments(fixture, installRoot, dataRoot, addToPath: true),
            RuntimePresent, store).ConfigureAwait(false);
        Require(installExit == 0, "The install beside a pre-existing PATH entry did not complete.");
        Require(store.Value == baseline + ";" + cliDirectory,
            "The install duplicated an entry the owner already had: " + store.Value);
        var state = InstallState.Read(Path.Combine(installRoot, InstallStateFileName));
        Require(state?.CommandPathOwned == false,
            "The install claimed a PATH entry it did not add.");

        var uninstallExit = await InstallCommand.UninstallAsync(
            new[] { "--component", "cli", "--defer-root-removal" }, installRoot, store).ConfigureAwait(false);
        Require(uninstallExit == 0, "The uninstall beside a pre-existing PATH entry did not complete.");
        Require(store.Value == baseline + ";" + cliDirectory,
            "Uninstall removed an entry this installation did not add: " + store.Value);
    }

    private static async Task VerifyWriteFailureIsTruthfulAsync(S12T5InstallFixture fixture)
    {
        var installRoot = Path.Combine(fixture.CasesRoot, "write-failure", "install");
        var dataRoot = Path.Combine(fixture.CasesRoot, "write-failure", "data");
        var baseline = FirstEntry + ";" + LastEntry;
        var store = new FakeUserPathStore(baseline) { ThrowOnWrite = true };

        try
        {
            await InstallCommand.RunAsync(
                InstallArguments(fixture, installRoot, dataRoot, addToPath: true),
                RuntimePresent, store).ConfigureAwait(false);
            throw new InvalidOperationException("The install reported success although the user PATH write failed.");
        }
        catch (IOException)
        {
        }

        Require(store.Value == baseline, "The failed PATH write changed the fixture PATH.");
        var state = InstallState.Read(Path.Combine(installRoot, InstallStateFileName));
        Require(state is not null && state.CommandPath is null && !state.CommandPathOwned,
            "The failed PATH write left a claimed entry in the install state.");
    }

    /// <summary>
    /// The changed runtime path with a real service: the installed native
    /// command starts the installed shared host twice (one host, one attach),
    /// provisions the synthetic vault, imports one file, then reads and
    /// searches it through the front-door defaults. The explicit workspace
    /// verbs are the same operations; nothing here uses a settings switch or a
    /// second dispatch table.
    /// </summary>
    private static async Task VerifyLiveFrontDoorAsync(S12T5InstallFixture fixture)
    {
        var installRoot = Path.Combine(fixture.CasesRoot, "live", "install");
        var dataRoot = Path.Combine(fixture.CasesRoot, "live", "data");
        var installExit = await InstallCommand.RunAsync(
            InstallArguments(fixture, installRoot, dataRoot, addToPath: false, component: "all"),
            RuntimePresent, new FakeUserPathStore(string.Empty)).ConfigureAwait(false);
        Require(installExit == 0, "The live front-door install did not complete.");

        var commandExecutable = Path.Combine(installRoot, "cli", "metabrain.exe");
        var serviceExecutable = Path.Combine(installRoot, "service", "MetaBrain.Connections.exe");
        Require(File.Exists(commandExecutable) && File.Exists(serviceExecutable),
            "The live front-door install did not create both the native command and the shared host.");

        using var live = SmokeFixture.CreateUninitializedAt(
            Path.Combine(fixture.CasesRoot, "live", "service-fixture"));
        var startArguments = new[]
        {
            "lifecycle", "start", "--config", live.SettingsPath, "--host", serviceExecutable
        };
        var starts = await Task.WhenAll(
            RunEntrypointUncapturedAsync(fixture.Root, commandExecutable, startArguments),
            RunEntrypointUncapturedAsync(fixture.Root, commandExecutable, startArguments)).ConfigureAwait(false);
        try
        {
            Require(starts.All(exitCode => exitCode == 0),
                "A concurrent lifecycle start failed: " + string.Join(", ", starts));
            var firstStatus = await RunEntrypointAsync(fixture.Root, commandExecutable,
                "lifecycle", "status", "--config", live.SettingsPath).ConfigureAwait(false);
            Require(firstStatus.ExitCode == 0 &&
                    firstStatus.StandardOutput.Contains("service=running", StringComparison.Ordinal) &&
                    !firstStatus.StandardOutput.Contains("host process=none", StringComparison.Ordinal),
                "The live front-door instance did not report one running host (" + firstStatus.StandardOutput.Trim() + ").");
            var firstHostLine = StatusHostLine(firstStatus.StandardOutput);

            var attach = await RunEntrypointUncapturedAsync(fixture.Root, commandExecutable, startArguments)
                .ConfigureAwait(false);
            Require(attach == 0, "A start beside the running host failed.");
            var secondStatus = await RunEntrypointAsync(fixture.Root, commandExecutable,
                "lifecycle", "status", "--config", live.SettingsPath).ConfigureAwait(false);
            Require(secondStatus.ExitCode == 0 &&
                    string.Equals(firstHostLine, StatusHostLine(secondStatus.StandardOutput), StringComparison.Ordinal),
                "A second start replaced the shared host instead of attaching to it.");

            var passphrase = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
            var provision = await RunEntrypointWithInputAsync(fixture.Root, commandExecutable,
                passphrase + Environment.NewLine + passphrase + Environment.NewLine,
                "owner", "provision", "--control-pipe", live.ControlPipe).ConfigureAwait(false);
            Require(provision.ExitCode == 0 &&
                    provision.StandardOutput.Contains("VAULT provisioned", StringComparison.Ordinal),
                "The native command could not provision its synthetic vault: " + provision.StandardError.Trim());

            var folderName = "g2-t1-live";
            var mkdir = await RunEntrypointAsync(fixture.Root, commandExecutable,
                "owner", "workspace-mkdir", "--control-pipe", live.ControlPipe,
                "--parent-id", "root", "--name", folderName, "--expected-tree-revision", "0").ConfigureAwait(false);
            Require(mkdir.ExitCode == 0, "The native command could not create the live workspace folder: " +
                mkdir.StandardError.Trim());
            var folderId = OutputToken(mkdir.StandardOutput, "id");
            var treeRevision = OutputToken(mkdir.StandardOutput, "revision");

            var marker = "g2-t1-live-routing-marker-" + Guid.NewGuid().ToString("N");
            var content = "G2-T1 live routing content: " + marker + "\n";
            var inputPath = Path.Combine(live.OutputDirectory, "g2-t1-live.txt");
            await File.WriteAllTextAsync(inputPath, content, new UTF8Encoding(false)).ConfigureAwait(false);
            var import = await RunEntrypointAsync(fixture.Root, commandExecutable,
                "owner", "workspace-import", "--control-pipe", live.ControlPipe,
                "--parent-id", folderId, "--name", "g2-t1-live.txt", "--input-file", inputPath,
                "--expected-tree-revision", treeRevision).ConfigureAwait(false);
            Require(import.ExitCode == 0, "The native command could not import the live workspace file: " +
                import.StandardError.Trim());
            var fileId = OutputToken(import.StandardOutput, "id");

            var read = await RunEntrypointAsync(fixture.Root, commandExecutable,
                "read", "--control-pipe", live.ControlPipe, "--file-id", fileId).ConfigureAwait(false);
            Require(read.ExitCode == 0 && read.StandardOutput == content,
                "The installed metabrain read did not return the exact workspace bytes (exit=" +
                read.ExitCode.ToString(CultureInfo.InvariantCulture) + "; stderr=" + read.StandardError.Trim() + ").");

            var search = await RunEntrypointAsync(fixture.Root, commandExecutable,
                "search", "--control-pipe", live.ControlPipe, "--query", marker).ConfigureAwait(false);
            Require(search.ExitCode == 0 && search.StandardOutput.Contains("HIT ", StringComparison.Ordinal) &&
                    search.StandardOutput.Contains("fileId=" + fileId, StringComparison.Ordinal),
                "The installed metabrain search did not find the live workspace file: " + search.StandardOutput.Trim());

            Console.WriteLine("PASS installed metabrain serializes two lifecycle starts to one host, then reads and searches the live workspace with the same binary");
        }
        finally
        {
            await RunEntrypointAsync(fixture.Root, commandExecutable,
                "lifecycle", "stop", "--config", live.SettingsPath).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Preserved legacy settings path: service startup refuses schema-1
    /// plaintext settings until the owner explicitly runs the separate
    /// <c>owner migrate</c>; that command encrypts the configured originals,
    /// rewrites the settings as schema 3, and the migrated service returns the
    /// exact bytes by resource ID. This is the D114 path, not the workspace
    /// record migration and not an automatic cutover.
    /// </summary>
    private static async Task VerifyLegacySettingsMigrationAsync(S12T5InstallFixture fixture)
    {
        var installRoot = Path.Combine(fixture.CasesRoot, "legacy-settings", "install");
        var dataRoot = Path.Combine(fixture.CasesRoot, "legacy-settings", "data");
        var installExit = await InstallCommand.RunAsync(
            InstallArguments(fixture, installRoot, dataRoot, addToPath: false, component: "all"),
            RuntimePresent, new FakeUserPathStore(string.Empty)).ConfigureAwait(false);
        Require(installExit == 0, "The legacy-settings install did not complete.");

        var legacyCli = Path.Combine(installRoot, "cli", "MetaBrain.Cli.exe");
        var commandExecutable = Path.Combine(installRoot, "cli", "metabrain.exe");
        var serviceExecutable = Path.Combine(installRoot, "service", "MetaBrain.Connections.exe");
        var legacyRoot = Path.Combine(fixture.CasesRoot, "legacy-settings", "service");
        var resources = Path.Combine(legacyRoot, "resources");
        Directory.CreateDirectory(resources);
        var resourceId = "g2-t1-legacy-resource";
        var marker = "G2-T1 legacy plaintext marker " + Guid.NewGuid().ToString("N");
        var resourcePath = Path.Combine(resources, "legacy.txt");
        await File.WriteAllTextAsync(resourcePath, marker, new UTF8Encoding(false)).ConfigureAwait(false);
        var controlPipe = "MetaBrainG2T1Legacy-" + Guid.NewGuid().ToString("N");
        var settingsPath = Path.Combine(legacyRoot, "service-settings.json");
        var ownerSid = WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("The legacy-settings fixture has no Windows owner SID.");
        await File.WriteAllTextAsync(settingsPath, JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            ownerSid,
            controlPipe,
            managedResources = new[]
            {
                new { resourceId, zoneId = "workspace", fileName = "legacy.txt" }
            }
        }), new UTF8Encoding(false)).ConfigureAwait(false);

        var refusal = await RunEntrypointAsync(fixture.Root, commandExecutable,
            "lifecycle", "start", "--config", settingsPath, "--host", serviceExecutable).ConfigureAwait(false);
        Require(refusal.ExitCode != 0 &&
                refusal.StandardError.Contains("Legacy plaintext settings require the explicit owner migration command",
                    StringComparison.Ordinal),
            "The service did not fail closed on schema-1 plaintext settings: " + refusal.StandardError.Trim());

        var passphrase = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
        var migrate = await RunEntrypointWithInputAsync(fixture.Root, legacyCli,
            passphrase + Environment.NewLine + passphrase + Environment.NewLine,
            "owner", "migrate", "--config", settingsPath).ConfigureAwait(false);
        Require(migrate.ExitCode == 0 &&
                migrate.StandardOutput.Contains("MIGRATION encrypted resources=1", StringComparison.Ordinal) &&
                migrate.StandardOutput.Contains("RECOVERY KEY", StringComparison.Ordinal),
            "The preserved owner migrate path did not encrypt the legacy settings: " + migrate.StandardError.Trim());

        using (var migratedSettings = JsonDocument.Parse(await File.ReadAllTextAsync(settingsPath).ConfigureAwait(false)))
        {
            Require(migratedSettings.RootElement.GetProperty("schemaVersion").GetInt32() == 3 &&
                    !migratedSettings.RootElement.TryGetProperty("managedResources", out _),
                "The preserved owner migrate path did not write schema-3 settings without plaintext bindings.");
        }

        Require(!File.Exists(resourcePath),
            "The preserved owner migrate path left the configured legacy plaintext original in place.");

        var start = await RunEntrypointUncapturedAsync(fixture.Root, commandExecutable,
            "lifecycle", "start", "--config", settingsPath, "--host", serviceExecutable).ConfigureAwait(false);
        Require(start == 0, "The migrated legacy instance did not start.");
        try
        {
            var unlock = await RunEntrypointWithInputAsync(fixture.Root, commandExecutable,
                passphrase + Environment.NewLine,
                "owner", "unlock", "--control-pipe", controlPipe).ConfigureAwait(false);
            Require(unlock.ExitCode == 0 && unlock.StandardOutput.Contains("VAULT unlocked", StringComparison.Ordinal),
                "The migrated legacy vault did not unlock: " + unlock.StandardError.Trim());
            var read = await RunEntrypointAsync(fixture.Root, commandExecutable,
                "owner", "read", "--control-pipe", controlPipe, "--resource-id", resourceId).ConfigureAwait(false);
            Require(read.ExitCode == 0 && read.StandardOutput == marker,
                "The migrated legacy resource did not return its exact bytes (exit=" +
                read.ExitCode.ToString(CultureInfo.InvariantCulture) + "; stderr=" + read.StandardError.Trim() + ").");
        }
        finally
        {
            await RunEntrypointAsync(fixture.Root, commandExecutable,
                "lifecycle", "stop", "--config", settingsPath).ConfigureAwait(false);
        }

        Console.WriteLine("PASS the explicit owner migrate path still refuses plaintext startup, encrypts schema-1 settings, and serves the exact legacy bytes");
    }

    private static string StatusHostLine(string output)
    {
        foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("host process=", StringComparison.Ordinal))
            {
                return line;
            }
        }

        throw new InvalidOperationException("The lifecycle status did not report a host process.");
    }

    private static string OutputToken(string output, string key)
    {
        foreach (var token in output.Split(new[] { ' ', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.StartsWith(key + "=", StringComparison.Ordinal))
            {
                return token[(key.Length + 1)..];
            }
        }

        throw new InvalidOperationException("The command output did not carry " + key + ": " + output.Trim());
    }

    private static string[] InstallArguments(
        S12T5InstallFixture fixture, string installRoot, string dataRoot, bool addToPath, string component = "cli")
    {
        var arguments = new List<string>
        {
            "--payload", fixture.PayloadDirectory,
            "--install-root", installRoot,
            "--data-root", dataRoot,
            "--component", component,
            "--candidate-id", "g2-t1"
        };
        if (addToPath)
        {
            arguments.Add("--add-to-path");
        }

        return arguments.ToArray();
    }

    private static Version? RuntimePresent() => new(14, 43, 34808);

    private static Task<(int ExitCode, string StandardOutput, string StandardError)> RunEntrypointAsync(
        string workingDirectory, string executable, params string[] arguments) =>
        RunEntrypointCoreAsync(workingDirectory, executable, standardInput: null, arguments);

    /// <summary>
    /// Runs a command whose child (the shared host) inherits this process's
    /// output handles. Capturing stdout would wait for the long-lived host to
    /// close the pipe, so lifecycle spawns are not captured; their effect is
    /// observed through the real status channel instead.
    /// </summary>
    private static async Task<int> RunEntrypointUncapturedAsync(
        string workingDirectory, string executable, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            WorkingDirectory = workingDirectory
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("The installed command could not be started.");
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);
        return process.ExitCode;
    }

    private static Task<(int ExitCode, string StandardOutput, string StandardError)> RunEntrypointWithInputAsync(
        string workingDirectory, string executable, string standardInput, params string[] arguments) =>
        RunEntrypointCoreAsync(workingDirectory, executable, standardInput, arguments);

    private static async Task<(int ExitCode, string StandardOutput, string StandardError)> RunEntrypointCoreAsync(
        string workingDirectory, string executable, string? standardInput, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = standardInput is not null,
            WorkingDirectory = workingDirectory
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("The installed command could not be started.");
        if (standardInput is not null)
        {
            await process.StandardInput.WriteAsync(standardInput).ConfigureAwait(false);
            process.StandardInput.Close();
        }

        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);
        var standardOutput = await output.ConfigureAwait(false);
        var standardError = await error.ConfigureAwait(false);
        if (standardOutput.Contains("Unhandled exception", StringComparison.Ordinal) ||
            standardError.Contains("Unhandled exception", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The installed command failed to start: " + standardError);
        }

        return (process.ExitCode, standardOutput, standardError);
    }

    private const string InstallStateFileName = "install-state.json";

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class FakeUserPathStore(string value) : IUserPathStore
    {
        public string Value { get; private set; } = value;

        public bool ThrowOnWrite { get; init; }

        public string Read() => Value;

        public void Write(string updated)
        {
            if (ThrowOnWrite)
            {
                throw new IOException("synthetic user PATH write refusal");
            }

            Value = updated;
        }
    }
}
