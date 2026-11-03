using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;
using MetaBrain.Connections;

namespace MetaBrain.Installer;

/// <summary>
/// The shared install and uninstall commands. Component registrations and
/// versioned installation state remain authoritative for all frontends; owner
/// data is kept outside the application root and is never removed here.
/// </summary>
internal static class InstallCommand
{
    internal static async Task<int> RunAsync(string[] args)
    {
        var options = InstallerOptions.Parse(args,
            new[] { "--payload", "--install-root", "--data-root", "--control-pipe", "--agent-pipe", "--component", "--candidate-id" },
            "--start");
        var payload = Path.GetFullPath(options.Required("--payload"));
        var installRoot = Path.GetFullPath(options.Required("--install-root")).TrimEnd(Path.DirectorySeparatorChar);
        var dataRoot = Path.GetFullPath(options.Required("--data-root")).TrimEnd(Path.DirectorySeparatorChar);
        var component = NormalizeComponent(options.Optional("--component") ?? "all");
        var requestedControlPipe = options.Optional("--control-pipe");
        var requestedAgentPipe = options.Optional("--agent-pipe");
        ValidatePipeName(requestedControlPipe);
        ValidatePipeName(requestedAgentPipe);
        if (requestedControlPipe is not null && requestedAgentPipe is not null &&
            string.Equals(requestedControlPipe, requestedAgentPipe, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The owner and agent pipe endpoints must differ.");
        }

        var ownerSid = CurrentOwnerSid();
        ValidateRoots(installRoot, dataRoot);
        ValidatePayload(payload, component);

        var statePath = Path.Combine(installRoot, InstallLayout.StateFileName);
        var existing = InstallState.Read(statePath);
        if (existing is null && (File.Exists(statePath) ||
            new[] { InstallLayout.ServiceDirectoryName, InstallLayout.CliDirectoryName,
                InstallLayout.LegacyOwnerUiDirectoryName, InstallLayout.McpDirectoryName }.Any(directory =>
                Directory.Exists(Path.Combine(installRoot, directory)))))
        {
            throw new InvalidOperationException("The selected install root contains an unrecognized or unsupported Meta Brain installation. Use its matching installer or choose an empty install root; no files were changed.");
        }

        // D090/D6: the installed transition is refused by default before the
        // running host is stopped or any file changes. A pre-1.2 WPF
        // installation, an unknown component set and an in-place upgrade from
        // a release before 1.2.0 are all mixed candidates; the owner installs
        // the current release beside them with its own install root, data root
        // and endpoints. An existing 1.2 CLI/MCP installation is upgraded in
        // place as the release contract requires.
        if (existing is null && HasLegacyWpfMarkers(installRoot))
        {
            throw new InvalidOperationException(LegacyInstallRefusal(installRoot, dataRoot));
        }

        if (existing is not null)
        {
            // 1.2 supports the CLI and the MCP server only (CR03/D089). A
            // state from before 1.2.0 is refused first, so an actual installed
            // 1.1 WPF layout receives the support-transition message instead
            // of an incidental candidate-identity error.
            if (!string.Equals(existing.ProductVersion, ProductInfo.Version, StringComparison.Ordinal) &&
                IsOlderThan12(existing.ProductVersion))
            {
                throw new InvalidOperationException(
                    "The install root holds a " + existing.ProductVersion +
                    " installation. This build refuses to upgrade a 1.1 WPF installation or an unknown/mixed candidate in place; it stopped nothing and changed no files. " +
                    LegacyRemediation(installRoot, dataRoot));
            }

            var unknownComponents = (existing.Components ?? System.Array.Empty<string>())
                .Where(component => component is not ("cli" or "mcp"))
                .ToArray();
            if (unknownComponents.Length != 0)
            {
                throw new InvalidOperationException(
                    "The install root registers an unknown or mixed component set (" +
                    string.Join(",", unknownComponents) + "); this release installs the CLI and the MCP server only. " +
                    "This build refuses to change that installation. " +
                    LegacyRemediation(installRoot, dataRoot));
            }
        }

        var candidateIdentity = options.Optional("--candidate-id") ?? existing?.CandidateIdentity ?? "legacy";
        var incomingComponents = ComponentsFor(component);
        var existingComponents = existing?.Components ?? Array.Empty<string>();
        var hasOtherComponent = existingComponents.Except(incomingComponents, StringComparer.Ordinal).Any();
        if (existing is not null && hasOtherComponent &&
            !string.Equals(existing.CandidateIdentity ?? "legacy", candidateIdentity, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("A different release candidate is registered for another frontend. Install the matching complete candidate or remove the other frontend before updating the shared runtime.");
        }

        if (existing is not null && hasOtherComponent &&
            !string.Equals(existing.ProductVersion, ProductInfo.Version, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("A different shared runtime version is registered for another frontend. Install the matching complete candidate or remove the other frontend before updating.");
        }

        if (existing is not null && !string.Equals(
                Path.GetFullPath(existing.DataRoot).TrimEnd(Path.DirectorySeparatorChar),
                dataRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The selected data root differs from the existing shared installation. Re-run setup with its recorded data root: " + existing.DataRoot);
        }

        var dataRootOwnerPath = Path.Combine(dataRoot, InstallLayout.DataRootMarkerFileName);
        var dataRootOwner = DataRootOwner.Read(dataRootOwnerPath);
        if (dataRootOwner is not null &&
            !string.Equals(Path.GetFullPath(dataRootOwner.InstallRoot).TrimEnd(Path.DirectorySeparatorChar),
                installRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The selected data root already belongs to the installation at " + dataRootOwner.InstallRoot +
                ". This build refuses to share one live data root between two installations; it stopped nothing and changed no files. " +
                LegacyRemediation(installRoot, dataRoot));
        }

        var existingOwnsDataRoot = existing is not null && string.Equals(
            Path.GetFullPath(existing.DataRoot).TrimEnd(Path.DirectorySeparatorChar), dataRoot, StringComparison.OrdinalIgnoreCase);
        if (dataRootOwner is null && !existingOwnsDataRoot && HasMetaBrainData(dataRoot))
        {
            throw new InvalidOperationException(
                "The selected data root already contains Meta Brain data from an installation this installer does not manage. " +
                "It was not opened, migrated or changed. " + LegacyRemediation(installRoot, dataRoot));
        }

        if (existing is not null && File.Exists(Path.Combine(installRoot, InstallLayout.ServiceDirectoryName, "MetaBrain.Connections.exe")))
        {
            var previous = InstallLayout.Create(installRoot, existing.DataRoot);
            await RefuseForeignOrOldEndpointAsync(previous).ConfigureAwait(false);
            var stop = await ServiceHost.StopAsync(previous).ConfigureAwait(false);
            if (stop is ServiceStopOutcome.Failed or ServiceStopOutcome.Forced)
            {
                throw new InvalidOperationException("The shared runtime did not drain cleanly. No files were changed; stop Meta Brain and retry the upgrade.");
            }
        }

        Directory.CreateDirectory(dataRoot);
        new DataRootOwner(
            DataRootOwner.CurrentSchemaVersion,
            installRoot,
            candidateIdentity,
            ProductInfo.Version,
            DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture)).Write(dataRootOwnerPath);
        var settings = EnsureSettings(dataRoot, ownerSid, requestedControlPipe, requestedAgentPipe);
        CopyPayload(payload, installRoot, component);
        var components = existing?.Components?.ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>(StringComparer.Ordinal);
        foreach (var installedComponent in ComponentsFor(component))
        {
            components.Add(installedComponent);
        }

        var state = new InstallState(
            InstallState.CurrentSchemaVersion,
            existing?.InstalledAtUtc ?? DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            dataRoot,
            ProductInfo.Version,
            components.ToArray(),
            candidateIdentity);
        state.Write(statePath);
        var layout = InstallLayout.Create(installRoot, dataRoot);

        if (options.HasFlag("--start"))
        {
            var outcome = await ServiceHost.StartAsync(layout).ConfigureAwait(false);
            if (outcome == ServiceStartOutcome.Failed)
            {
                throw new InvalidOperationException("The installed service host did not start.");
            }
        }

        Console.WriteLine("INSTALLED");
        Console.WriteLine("product version=" + ProductInfo.Version);
        Console.WriteLine("install root=" + layout.InstallRoot);
        Console.WriteLine("data root=" + layout.DataRoot);
        Console.WriteLine("settings=" + layout.SettingsPath);
        Console.WriteLine("control pipe=" + settings.ControlPipe);
        Console.WriteLine("agent pipe=" + settings.AgentPipe);
        Console.WriteLine("cli=" + layout.CliExecutable);
        Console.WriteLine("mcp=" + layout.McpExecutable);
        Console.WriteLine("shared host=" + layout.ServiceExecutable);
        Console.WriteLine("installed components=" + string.Join(",", components.Order(StringComparer.Ordinal)));
        Console.WriteLine("vault state=uninitialized (the owner creates the vault explicitly and every start is locked)");
        return 0;
    }

    internal static async Task<int> UninstallAsync(string[] args)
    {
        var options = InstallerOptions.Parse(args, new[] { "--component" }, "--defer-root-removal");
        var component = NormalizeComponent(options.Optional("--component") ?? "all");
        var deferRootRemoval = options.HasFlag("--defer-root-removal");
        if (deferRootRemoval && component == "all")
        {
            throw new ArgumentException("--defer-root-removal is valid only for a single component uninstall.");
        }
        var layout = InstallLayout.Locate();
        var state = InstallState.Read(layout.StatePath);
        var components = state?.Components?.ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>(StringComparer.Ordinal);
        foreach (var removedComponent in ComponentsFor(component))
        {
            components.Remove(removedComponent);
        }

        if (component == "all" || components.Count == 0)
        {
            var stop = await ServiceHost.StopAsync(layout).ConfigureAwait(false);
            if (stop is ServiceStopOutcome.Failed or ServiceStopOutcome.Forced)
            {
                throw new InvalidOperationException("The shared runtime did not drain cleanly. No files were changed; stop Meta Brain and retry uninstall.");
            }

            Console.WriteLine("service=" + (stop == ServiceStopOutcome.Stopped ? "stopped" : "not running"));
        }
        else
        {
            Console.WriteLine("service=left running for remaining shared components");
        }
        var removedDirectories = component switch
        {
            "cli" => new[] { InstallLayout.CliDirectoryName },
            "mcp" => new[] { InstallLayout.McpDirectoryName },
            _ => new[]
            {
                InstallLayout.ServiceDirectoryName, InstallLayout.McpDirectoryName,
                InstallLayout.CliDirectoryName
            }
        };
        foreach (var directory in removedDirectories)
        {
            var removedPath = Path.Combine(layout.InstallRoot, directory);
            DeleteDirectory(removedPath);
            if (Directory.Exists(removedPath))
            {
                throw new IOException("The frontend files could not be removed. Close processes using " + removedPath + " and retry.");
            }
        }

        if (component != "all" && components.Count != 0)
        {
            state = state! with { Components = components.ToArray() };
            state.Write(layout.StatePath);
            Console.WriteLine("shared runtime=preserved (installed components: " +
                string.Join(",", components.Order(StringComparer.Ordinal)) + ")");
        }
        else
        {
            var marked = File.Exists(layout.StatePath);
            if (component != "all")
            {
                DeleteDirectory(layout.ServiceDirectory);
                if (Directory.Exists(layout.ServiceDirectory))
                {
                    throw new IOException("The shared runtime could not be removed. Close Meta Brain processes and retry.");
                }
            }

            if (!deferRootRemoval)
            {
                ScheduleRemoval(layout.InstallRoot, marked);
            }

            foreach (var file in Directory.EnumerateFiles(layout.InstallerDirectory))
            {
                if (!string.Equals(Path.GetFullPath(file), Path.GetFullPath(Environment.ProcessPath ?? string.Empty),
                        StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(file);
                }
            }

            TryDelete(layout.StatePath);
        }

        Console.WriteLine("data root=preserved (" + layout.DataRoot + ")");
        Console.WriteLine(component == "all" || components.Count == 0
            ? deferRootRemoval
                ? "UNINSTALL READY FOR SETUP WRAPPER (application files only; data root preserved)"
                : "UNINSTALL SCHEDULED (application files only; data root preserved)"
            : "COMPONENT UNINSTALLED (shared runtime preserved)");
        return 0;
    }

    /// <summary>
    /// Root-level Owner UI artifacts identify a legacy (1.1) WPF installation
    /// whose layout this shared installer does not manage. It is refused before
    /// anything is copied, stopped or removed.
    /// </summary>
    private static readonly string[] LegacyWpfMarkers =
    {
        "MetaBrain.OwnerUi.exe",
        "MetaBrain.OwnerUi.dll",
        "MetaBrain.OwnerUi.Setup.exe",
        "MetaBrain.OwnerUi.Setup.dll"
    };

    private static bool HasLegacyWpfMarkers(string installRoot) =>
        LegacyWpfMarkers.Any(name => File.Exists(Path.Combine(installRoot, name)));

    private static string LegacyInstallRefusal(string installRoot, string dataRoot) =>
        "The install root contains a 1.1 WPF application this build does not manage. " +
        "It stopped nothing and changed no files. " + LegacyRemediation(installRoot, dataRoot);

    /// <summary>
    /// Truthful, non-destructive remediation: a second installation beside the
    /// existing one, never a replacement, uninstall or shared data root.
    /// </summary>
    private static string LegacyRemediation(string installRoot, string dataRoot) =>
        "Install " + ProductInfo.Version + " into a separate install root and a distinct data root with distinct control/agent endpoints (for example a new root outside " +
        installRoot + " and a new data root instead of " + dataRoot +
        "); the existing 1.1 installation and data stay untouched. " +
        "This build does not replace, uninstall or edit the old installation.";

    private static bool IsOlderThan12(string version) =>
        Version.TryParse(version, out var parsed) && parsed < new Version(1, 2, 0);

    private static bool HasMetaBrainData(string dataRoot)
    {
        if (!Directory.Exists(dataRoot))
        {
            return false;
        }

        if (File.Exists(Path.Combine(dataRoot, "service-settings.json")))
        {
            return true;
        }

        return new[] { "vault", "private-index", "backups", "workspace", "runtime" }
            .Any(directory => Directory.Exists(Path.Combine(dataRoot, directory)));
    }

    /// <summary>
    /// Before an in-place upgrade stops anything, prove the endpoint belongs to
    /// this installation and speaks this build's protocol. A manually mixed
    /// binary or a pre-existing endpoint is refused rather than stopped.
    /// </summary>
    private static async Task RefuseForeignOrOldEndpointAsync(InstallLayout layout)
    {
        if (!File.Exists(layout.SettingsPath))
        {
            return;
        }

        string controlPipe;
        try
        {
            controlPipe = ServiceHost.ReadControlPipe(layout);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException)
        {
            return;
        }

        var status = await OwnerChannelClient.ReadStatusAsync(controlPipe, CancellationToken.None).ConfigureAwait(false);
        if (status.Availability == OwnerChannelAvailability.ProtocolMismatch)
        {
            throw new InvalidOperationException(
                "The host on endpoint '" + controlPipe +
                "' speaks a different protocol revision. It was not stopped and no files were changed. " +
                "Stop that host with its own build, or install this release beside it with distinct endpoints.");
        }

        if (status.Availability == OwnerChannelAvailability.Reachable)
        {
            var identity = ServiceRuntime.ReadIdentity(layout.RuntimeDirectory);
            if (!ServiceRuntime.IsProcessRunning(identity))
            {
                throw new InvalidOperationException(
                    "The owner endpoint '" + controlPipe +
                    "' is answered by a host this installation did not start. It was not stopped and no files were changed. " +
                    "Give this installation distinct endpoints or stop the other host explicitly.");
            }
        }
    }

    internal static string CurrentOwnerSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value
            ?? throw new InvalidOperationException("The current account has no user SID.");
    }

    private static void ValidateRoots(string installRoot, string dataRoot)
    {
        if (string.Equals(installRoot, dataRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The install root and the data root must be different directories.");
        }
        foreach (var path in new[] { installRoot, dataRoot })
        {
            if (string.Equals(path, Path.GetPathRoot(path)?.TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("The install and data roots must not be drive roots.");
            }
        }

        if (IsInside(dataRoot, installRoot))
        {
            throw new ArgumentException("The data root must not live inside the install root: an uninstall removes the install root.");
        }

        if (IsInside(installRoot, dataRoot))
        {
            throw new ArgumentException("The install root must not live inside the data root.");
        }

        foreach (var path in new[] { installRoot, dataRoot })
        {
            if (Directory.Exists(path) && (new DirectoryInfo(path).Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new ArgumentException("The install and data roots must not be reparse points: " + path + ".");
            }
        }
    }

    private static bool IsInside(string candidate, string root)
    {
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return candidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static void ValidatePayload(string payload, string component)
    {
        if (!Directory.Exists(payload))
        {
            throw new DirectoryNotFoundException("The payload directory does not exist: " + payload + ".");
        }

        foreach (var required in DirectoriesFor(component)
                     .SelectMany(directory => directory switch
                     {
                         InstallLayout.ServiceDirectoryName => new[] { Path.Combine(directory, "MetaBrain.Connections.exe") },
                         InstallLayout.CliDirectoryName => new[] { Path.Combine(directory, "MetaBrain.Cli.exe") },
                         InstallLayout.McpDirectoryName => new[] { Path.Combine(directory, "MetaBrain.Mcp.exe") },
                         InstallLayout.InstallerDirectoryName => new[] { Path.Combine(directory, "MetaBrain.Installer.exe") },
                         _ => Array.Empty<string>()
                     }))
        {
            if (!File.Exists(Path.Combine(payload, required)))
            {
                throw new FileNotFoundException("The payload is incomplete; missing " + required + ".");
            }
        }
    }

    private static void CopyPayload(string payload, string installRoot, string component)
    {
        Directory.CreateDirectory(installRoot);
        var transactionRoot = Path.Combine(installRoot, ".metabrain-stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(transactionRoot);
        var runningDirectory = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar);
        var committed = new List<(string Target, string? Backup)>();
        try
        {
            foreach (var directory in DirectoriesFor(component))
            {
                CopyDirectory(Path.Combine(payload, directory), Path.Combine(transactionRoot, "new-" + directory));
            }

            foreach (var directory in DirectoriesFor(component))
            {
                var target = Path.Combine(installRoot, directory);
                if (string.Equals(Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar), runningDirectory,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string? backup = null;
                if (Directory.Exists(target))
                {
                    backup = Path.Combine(transactionRoot, "old-" + directory);
                    Directory.Move(target, backup);
                }

                try
                {
                    Directory.Move(Path.Combine(transactionRoot, "new-" + directory), target);
                    committed.Add((target, backup));
                }
                catch
                {
                    if (backup is not null && Directory.Exists(backup))
                    {
                        Directory.Move(backup, target);
                    }

                    throw;
                }
            }
        }
        catch
        {
            foreach (var (target, backup) in committed.AsEnumerable().Reverse())
            {
                if (Directory.Exists(target))
                {
                    Directory.Delete(target, recursive: true);
                }

                if (backup is not null && Directory.Exists(backup))
                {
                    Directory.Move(backup, target);
                }
            }

            throw;
        }
        finally
        {
            if (Directory.Exists(transactionRoot))
            {
                Directory.Delete(transactionRoot, recursive: true);
            }
        }
    }

    private static string NormalizeComponent(string component) => component switch
    {
        "all" or "cli" or "mcp" => component,
        "owner-ui" => throw new ArgumentException("The Owner UI is the frozen 1.1.0 WPF product and is not installed by this release. Use --component cli, mcp, or all."),
        _ => throw new ArgumentException("Component must be cli, mcp, or all.")
    };

    private static string[] ComponentsFor(string component) => component switch
    {
        "all" => new[] { "cli", "mcp" },
        _ => new[] { component }
    };

    private static string[] DirectoriesFor(string component) => component switch
    {
        "cli" => new[] { InstallLayout.ServiceDirectoryName, InstallLayout.CliDirectoryName, InstallLayout.InstallerDirectoryName },
        "mcp" => new[] { InstallLayout.ServiceDirectoryName, InstallLayout.McpDirectoryName, InstallLayout.InstallerDirectoryName },
        _ => new[]
        {
            InstallLayout.ServiceDirectoryName, InstallLayout.McpDirectoryName,
            InstallLayout.CliDirectoryName, InstallLayout.InstallerDirectoryName
        }
    };

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(target, Path.GetFileName(directory)));
        }
    }

    private static ServiceSettings EnsureSettings(string dataRoot, string ownerSid, string? controlPipe, string? agentPipe)
    {
        var path = ServiceSettingsFile.ResolvePath(dataRoot);
        if (File.Exists(path))
        {
            // An existing installation keeps its pipe names, so a client that
            // is already configured for this instance keeps working.
            var existing = ServiceSettingsLoader.Load(path);
            if (controlPipe is not null || agentPipe is not null)
            {
                Console.WriteLine("the existing pipe endpoints were kept; an upgrade does not move a configured client");
            }

            return existing;
        }

        return ServiceSettingsFile.Create(dataRoot, ownerSid,
            controlPipe ?? NewEndpointName("owner"),
            agentPipe ?? NewEndpointName("agent"));
    }

    /// <summary>
    /// A pipe endpoint name becomes part of a command line and of a client's
    /// configuration, so it is limited to the safe identifier shape the
    /// service accepts for its own endpoints.
    /// </summary>
    private static void ValidatePipeName(string? pipeName)
    {
        if (pipeName is not null && !ServiceSettingsLoader.IsSafeIdentifier(pipeName))
        {
            throw new ArgumentException("A pipe endpoint name must be a safe identifier: " + pipeName + ".");
        }
    }

    private static string NewEndpointName(string role)
    {
        var suffix = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        return "metabrain." + role + "." + suffix;
    }

    private static void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // A file that is still open (for example the running host) stays;
            // the delayed removal below retries once this process exited.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Removes the install root once this process has exited. The delete runs
    /// from a detached command shell after a short delay, because the running
    /// installer executable is part of that directory. The removal is only
    /// scheduled for a directory that carried this installation's own state
    /// file, and never for a drive root.
    /// </summary>
    private static void ScheduleRemoval(string installRoot, bool marked)
    {
        if (!marked)
        {
            throw new InvalidOperationException("The install root is not marked as a Meta Brain installation; automatic removal was refused.");
        }

        if (string.Equals(installRoot, Path.GetPathRoot(installRoot)?.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The install root is a drive root and cannot be removed.");
        }

        var start = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            Arguments = "/d /s /c \"ping 127.0.0.1 -n 3 > nul & rmdir /s /q \"" + installRoot + "\"\""
        };
        try
        {
            using var helper = Process.Start(start)
                ?? throw new InvalidOperationException("The install-root removal helper could not be started.");
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException)
        {
            throw new InvalidOperationException("Could not schedule removal of the install root: " + installRoot + ". Close the installer process and retry.", exception);
        }
    }
}
