using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Principal;
using MetaBrain.Connections;

namespace MetaBrain.Installer;

/// <summary>
/// The install and uninstall commands. An install lays the application files
/// down, creates the data root with the service settings, and leaves the
/// vault untouched, so installing over an existing instance is an upgrade of
/// the application files and never a reset of the data. An uninstall removes
/// the application and the autostart entry, and keeps the data root - the
/// vault, the source snapshots, the backups and the recovery material -
/// unless the owner explicitly asks for it to be removed.
/// </summary>
internal static class InstallCommand
{
    internal static async Task<int> RunAsync(string[] args)
    {
        var options = InstallerOptions.Parse(args,
            new[] { "--payload", "--install-root", "--data-root", "--control-pipe", "--agent-pipe" },
            "--autostart", "--start");
        var payload = Path.GetFullPath(options.Required("--payload"));
        var installRoot = Path.GetFullPath(options.Required("--install-root")).TrimEnd(Path.DirectorySeparatorChar);
        var dataRoot = Path.GetFullPath(options.Required("--data-root")).TrimEnd(Path.DirectorySeparatorChar);
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
        ValidatePayload(payload);

        var existing = InstallState.Read(Path.Combine(installRoot, InstallLayout.StateFileName));
        if (existing is not null && File.Exists(Path.Combine(installRoot, InstallLayout.ServiceDirectoryName, "MetaBrain.Connections.exe")))
        {
            // Replacing the application files of a running instance would fail
            // on the locked binaries, so the host is stopped first.
            var previous = InstallLayout.Create(installRoot, existing.DataRoot);
            await ServiceHost.StopAsync(previous).ConfigureAwait(false);
        }

        CopyPayload(payload, installRoot);
        Directory.CreateDirectory(dataRoot);
        var settings = EnsureSettings(dataRoot, ownerSid, requestedControlPipe, requestedAgentPipe);
        var state = new InstallState(
            InstallState.CurrentSchemaVersion,
            DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            dataRoot,
            ProductVersion());
        state.Write(Path.Combine(installRoot, InstallLayout.StateFileName));

        var layout = InstallLayout.Create(installRoot, dataRoot);
        var autostart = new AutostartRegistration();
        if (options.HasFlag("--autostart"))
        {
            autostart.Enable(AutostartRegistration.BuildCommand(layout.ServiceExecutable, layout.SettingsPath));
        }

        if (options.HasFlag("--start"))
        {
            var outcome = await ServiceHost.StartAsync(layout).ConfigureAwait(false);
            if (outcome == ServiceStartOutcome.Failed)
            {
                throw new InvalidOperationException("The installed service host did not start.");
            }
        }

        Console.WriteLine("INSTALLED");
        Console.WriteLine("install root=" + layout.InstallRoot);
        Console.WriteLine("data root=" + layout.DataRoot);
        Console.WriteLine("settings=" + layout.SettingsPath);
        Console.WriteLine("control pipe=" + settings.ControlPipe);
        Console.WriteLine("agent pipe=" + settings.AgentPipe);
        Console.WriteLine("owner console=" + layout.OwnerUiExecutable + " --control-pipe " + settings.ControlPipe);
        Console.WriteLine("autostart=" + (autostart.TryRead(out var command) ? "enabled" : "disabled"));
        if (command is not null)
        {
            Console.WriteLine("autostart command=" + command);
        }

        Console.WriteLine("vault state=uninitialized (the owner creates the vault explicitly and every start is locked)");
        return 0;
    }

    internal static async Task<int> UninstallAsync(string[] args)
    {
        if (args.Length != 0)
        {
            throw new ArgumentException("The uninstall command takes no options.");
        }

        var layout = InstallLayout.Locate();
        var stop = await ServiceHost.StopAsync(layout).ConfigureAwait(false);
        Console.WriteLine("service=" + (stop switch
        {
            ServiceStopOutcome.Stopped => "stopped",
            ServiceStopOutcome.Forced => "stopped (forced)",
            _ => "not running"
        }));

        var autostart = new AutostartRegistration();
        if (autostart.TryRead(out var command) && command is not null &&
            command.Contains(layout.ServiceExecutable, StringComparison.OrdinalIgnoreCase))
        {
            autostart.Disable();
            Console.WriteLine("autostart=disabled");
        }

        DeleteDirectory(layout.ServiceDirectory);
        DeleteDirectory(layout.OwnerUiDirectory);
        // The running installer executable cannot be deleted by itself, so the
        // rest of the installer directory is cleared now and the install root
        // is removed by a delayed delete once this process has exited.
        foreach (var file in Directory.EnumerateFiles(layout.InstallerDirectory))
        {
            if (!string.Equals(Path.GetFullPath(file), Path.GetFullPath(Environment.ProcessPath ?? string.Empty),
                    StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(file);
            }
        }

        var statePath = layout.StatePath;
        var marked = File.Exists(statePath);
        TryDelete(statePath);
        ScheduleRemoval(layout.InstallRoot, marked);

        // The data root holds the vault, the source snapshots, the backups and
        // the recovery material. An uninstall removes the application, never
        // the owner's data: deleting it is the owner's own separate act, not
        // something an installer may offer as a convenience.
        Console.WriteLine("data root=preserved (" + layout.DataRoot + ")");
        Console.WriteLine("vault=preserved (" + Path.Combine(layout.DataRoot, "vault") + ")");
        Console.WriteLine("source snapshots, backups and recovery material=preserved (" + layout.DataRoot + ")");
        Console.WriteLine("the vault, source snapshots, backups and recovery material were kept untouched");

        Console.WriteLine("UNINSTALLED");
        return 0;
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

    private static void ValidatePayload(string payload)
    {
        if (!Directory.Exists(payload))
        {
            throw new DirectoryNotFoundException("The payload directory does not exist: " + payload + ".");
        }

        foreach (var required in new[]
                 {
                     Path.Combine(InstallLayout.ServiceDirectoryName, "MetaBrain.Connections.exe"),
                     Path.Combine(InstallLayout.OwnerUiDirectoryName, "MetaBrain.OwnerUi.exe")
                 })
        {
            if (!File.Exists(Path.Combine(payload, required)))
            {
                throw new FileNotFoundException("The payload is incomplete; missing " + required + ".");
            }
        }
    }

    private static void CopyPayload(string payload, string installRoot)
    {
        Directory.CreateDirectory(installRoot);
        foreach (var directory in new[]
                 {
                     InstallLayout.ServiceDirectoryName,
                     InstallLayout.OwnerUiDirectoryName,
                     InstallLayout.InstallerDirectoryName
                 })
        {
            var source = Path.Combine(payload, directory);
            if (!Directory.Exists(source))
            {
                continue;
            }

            var target = Path.Combine(installRoot, directory);
            var runningDirectory = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar);
            if (!string.Equals(Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar), runningDirectory,
                    StringComparison.OrdinalIgnoreCase))
            {
                // A whole-directory replacement is what makes an upgrade leave
                // no stale application file behind.
                DeleteDirectory(target);
            }

            CopyDirectory(source, target);
        }
    }

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
        if (!marked || string.Equals(installRoot, Path.GetPathRoot(installRoot)?.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var start = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add("ping 127.0.0.1 -n 3 > nul & rmdir /s /q \"" + installRoot + "\"");
        try
        {
            Process.Start(start);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            Console.WriteLine("the install root could not be removed automatically: " + installRoot);
        }
    }

    private static string ProductVersion()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        return version is null ? "0.0.0" : version.ToString();
    }
}
