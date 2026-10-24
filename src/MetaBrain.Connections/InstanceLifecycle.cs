using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;

namespace MetaBrain.Connections;

/// <summary>
/// The one shared instance lifecycle every frontend uses. The owner CLI, the
/// owner console and the agent bridge all resolve the same instance (one data
/// root, one settings file, one pair of pipe endpoints) and start or connect
/// to the same locked host through this implementation; no frontend keeps a
/// second start/stop/status path. The installer delegates its service control
/// to it as well.
/// </summary>
/// <remarks>
/// The lifecycle creates ordinary files with the ordinary permissions of the
/// owner account and never changes a permission or an owner. It provisions no
/// vault, unlocks nothing and mints no grant, token or session: every start
/// begins locked and the owner acts explicitly afterwards. All persisted
/// values are nonsensitive (paths, pipe names, process identity, log text);
/// no key, credential, token or private content ever passes through here.
/// </remarks>
internal sealed record InstanceHandle(
    string SettingsPath,
    string DataRoot,
    string RuntimeDirectory,
    string ControlPipe,
    string AgentPipe);

/// <summary>The outcome of ensuring the shared host is running.</summary>
internal enum InstanceStartOutcome
{
    Started = 0,
    AlreadyRunning = 1
}

/// <summary>The outcome of asking the shared host to stop.</summary>
internal enum InstanceStopOutcome
{
    Stopped = 0,
    AlreadyStopped = 1,
    Forced = 2
}

/// <summary>
/// What the shared lifecycle observed about one instance: the owner-channel
/// answer, the recorded host process and whether that process is still the
/// host that wrote the record.
/// </summary>
internal sealed record InstanceStatus(
    OwnerChannelStatus Channel,
    int? HostProcessId,
    bool HostProcessRunning,
    string SettingsPath,
    string DataRoot,
    string ControlPipe,
    string AgentPipe);

/// <summary>
/// A truthful lifecycle failure: a stable machine-readable code plus the
/// owner's next action. The code is the identity of the failure; the
/// remediation tells the owner how to recover.
/// </summary>
internal sealed class InstanceLifecycleException(string code, string message, string remediation)
    : InvalidOperationException(message)
{
    public string Code { get; } = code;
    public string Remediation { get; } = remediation;
}

/// <summary>
/// S9-T2 shared resolve/create/select/start/connect lifecycle. One
/// implementation for the packaged lifecycle verbs, the installer service
/// commands and the future CLI/UI/MCP entrypoints.
/// </summary>
internal static class InstanceLifecycle
{
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    private const string StartClaimFileName = "host.start.lock";

    /// <summary>
    /// Selects the instance described by an explicit settings file. The file
    /// must exist and validate; nothing is created or changed.
    /// </summary>
    internal static InstanceHandle ResolveFromSettingsPath(string settingsPath)
    {
        if (string.IsNullOrWhiteSpace(settingsPath))
        {
            throw new InstanceLifecycleException(
                "invalid_settings_path",
                "The service settings path is missing.",
                "Pass --config <service-settings.json> or --data-root <dir> that holds it.");
        }

        var fullPath = Path.GetFullPath(settingsPath);
        ServiceSettings settings;
        try
        {
            settings = ServiceSettingsLoader.Load(fullPath);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException
            or PlatformNotSupportedException or ArgumentException or System.Text.Json.JsonException)
        {
            throw new InstanceLifecycleException(
                "invalid_settings",
                "The service settings file cannot be used (" + ex.GetType().Name + "): " + ex.Message,
                "Keep the existing file untouched and fix the named problem, or start from a clean data root so the lifecycle creates fresh settings.");
        }

        return new InstanceHandle(fullPath, settings.DataRootPath,
            ServiceRuntime.ResolveRuntimeDirectory(settings), settings.ControlPipe, settings.AgentPipe);
    }

    /// <summary>
    /// Selects the instance that lives in a data root. The settings file must
    /// already exist; use <see cref="Create"/> to initialize a clean root.
    /// </summary>
    internal static InstanceHandle ResolveFromDataRoot(string dataRoot)
    {
        if (string.IsNullOrWhiteSpace(dataRoot))
        {
            throw new InstanceLifecycleException(
                "invalid_data_root",
                "The data root is missing.",
                "Pass --data-root <dir>; for a new instance point it at an empty directory and run 'lifecycle start'.");
        }

        var path = ServiceSettingsFile.ResolvePath(Path.GetFullPath(dataRoot));
        if (!File.Exists(path))
        {
            throw new InstanceLifecycleException(
                "instance_not_initialized",
                "This data root holds no service settings: " + path + ".",
                "Run 'lifecycle start --data-root <dir>' to create the settings, or point --config at the settings file of an existing instance.");
        }

        return ResolveFromSettingsPath(path);
    }

    /// <summary>
    /// Creates the instance in a data root, or keeps the existing one. An
    /// existing settings file is loaded, validated and kept byte-identical:
    /// pipe endpoints an owner already configured in a client never move and
    /// no vault, snapshot, backup or recovery material is touched.
    /// </summary>
    internal static InstanceHandle Create(string dataRoot, string? controlPipe = null, string? agentPipe = null)
    {
        if (string.IsNullOrWhiteSpace(dataRoot))
        {
            throw new InstanceLifecycleException(
                "invalid_data_root",
                "The data root is missing.",
                "Pass --data-root <dir>; for a new instance use an empty directory.");
        }

        var root = Path.GetFullPath(dataRoot);
        if (Directory.Exists(root) && (new DirectoryInfo(root).Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InstanceLifecycleException(
                "invalid_data_root",
                "The data root must not be a reparse point: " + root + ".",
                "Point --data-root at a real directory; a link could resolve outside the instance.");
        }

        if (controlPipe is not null && !ServiceSettingsLoader.IsSafeIdentifier(controlPipe))
        {
            throw new InstanceLifecycleException(
                "invalid_endpoint",
                "The owner pipe endpoint name is invalid.",
                "Use letters, digits, '.', '_' or '-' (up to 128 characters); omit the option to generate one.");
        }

        if (agentPipe is not null && !ServiceSettingsLoader.IsSafeIdentifier(agentPipe))
        {
            throw new InstanceLifecycleException(
                "invalid_endpoint",
                "The agent pipe endpoint name is invalid.",
                "Use letters, digits, '.', '_' or '-' (up to 128 characters); omit the option to generate one.");
        }

        if (controlPipe is not null && agentPipe is not null &&
            string.Equals(controlPipe, agentPipe, StringComparison.OrdinalIgnoreCase))
        {
            throw new InstanceLifecycleException(
                "invalid_endpoint",
                "The owner and agent pipe endpoints must differ.",
                "Choose two different endpoint names or omit both to generate them.");
        }

        var path = ServiceSettingsFile.ResolvePath(root);
        if (File.Exists(path))
        {
            var kept = ResolveFromSettingsPath(path);
            if ((controlPipe is not null && !string.Equals(controlPipe, kept.ControlPipe, StringComparison.Ordinal)) ||
                (agentPipe is not null && !string.Equals(agentPipe, kept.AgentPipe, StringComparison.Ordinal)))
            {
                throw new InstanceLifecycleException(
                    "endpoints_kept",
                    "This data root already has pipe endpoints (" + kept.ControlPipe + " / " + kept.AgentPipe + "); they were kept.",
                    "A client already configured for this instance keeps working. To use different endpoints, start from a clean data root.");
            }

            return kept;
        }

        Directory.CreateDirectory(root);
        var settings = ServiceSettingsFile.Create(root, CurrentOwnerSid(),
            controlPipe ?? NewEndpointName("owner"),
            agentPipe ?? NewEndpointName("agent"));
        return new InstanceHandle(path, settings.DataRootPath,
            ServiceRuntime.ResolveRuntimeDirectory(settings), settings.ControlPipe, settings.AgentPipe);
    }

    /// <summary>
    /// Starts the shared host for this instance, or attaches to the host that
    /// is already running it. Concurrent callers serialize on the instance
    /// start claim, so two launches can never produce two writers: exactly one
    /// host owns the data root afterwards.
    /// </summary>
    internal static async Task<InstanceStartOutcome> EnsureStartedAsync(
        InstanceHandle handle, string hostExecutable, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (string.IsNullOrWhiteSpace(hostExecutable) || !File.Exists(hostExecutable))
        {
            throw new InstanceLifecycleException(
                "host_missing",
                "The service host executable is missing: " + hostExecutable + ".",
                "Reinstall the application payload so the service host is present, then try again.");
        }

        Directory.CreateDirectory(handle.RuntimeDirectory);
        using var claim = AcquireStartClaim(handle.RuntimeDirectory);

        var status = await OwnerChannelClient.ReadStatusAsync(handle.ControlPipe, CancellationToken.None).ConfigureAwait(false);
        if (status.Availability == OwnerChannelAvailability.Reachable)
        {
            // Reachable means SOME host answers on this endpoint. It is only
            // this instance's host when this data root recorded that host: the
            // pid file must name a process that is still running. Otherwise
            // the endpoint belongs to a different live instance and this
            // start must not alias it.
            var recorded = ServiceRuntime.ReadIdentity(handle.RuntimeDirectory);
            if (ServiceRuntime.IsProcessRunning(recorded))
            {
                return InstanceStartOutcome.AlreadyRunning;
            }

            throw new InstanceLifecycleException(
                "endpoint_occupied",
                "The owner endpoint '" + handle.ControlPipe + "' is already answered by a different running host.",
                "Run 'lifecycle status' against the instance that owns this endpoint, or give this instance distinct endpoints and try again.");
        }
        if (status.Availability == OwnerChannelAvailability.ProtocolMismatch)
        {
            throw new InstanceLifecycleException(
                "version_mismatch",
                "The running host speaks a different protocol revision.",
                "Upgrade the application payload to the build this instance was created with, then run 'lifecycle restart'.");
        }

        var identity = ServiceRuntime.ReadIdentity(handle.RuntimeDirectory);
        if (ServiceRuntime.IsProcessRunning(identity))
        {
            throw new InstanceLifecycleException(
                "host_unresponsive",
                "The recorded host process (" + identity!.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ") is alive but the owner channel does not answer.",
                "Inspect '" + ServiceRuntime.ResolveLogPath(handle.RuntimeDirectory) + "' for the failure, then run 'lifecycle stop' and 'lifecycle start'.");
        }

        using var process = Process.Start(new ProcessStartInfo(hostExecutable)
        {
            UseShellExecute = false,
            CreateNoWindow = true
        }.WithLifecycleArgs(handle.SettingsPath))
            ?? throw new InstanceLifecycleException(
                "host_not_started",
                "The service host process did not start.",
                "Check that the host executable runs on this machine, then try again.");

        var deadline = DateTime.UtcNow + ReadyTimeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var probe = await OwnerChannelClient.ReadStatusAsync(handle.ControlPipe, CancellationToken.None).ConfigureAwait(false);
            if (probe.Availability == OwnerChannelAvailability.Reachable)
            {
                return InstanceStartOutcome.Started;
            }

            if (probe.Availability == OwnerChannelAvailability.ProtocolMismatch)
            {
                throw new InstanceLifecycleException(
                    "version_mismatch",
                    "The host that just started speaks a different protocol revision.",
                    "Upgrade the application payload to the build this instance was created with, then run 'lifecycle restart'.");
            }

            if (process.HasExited)
            {
                throw new InstanceLifecycleException(
                    "host_exited",
                    "The service host exited before it became reachable (exit code " + process.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture) + ").",
                    "The configured endpoint may already be claimed by another host: give this instance distinct endpoints. Otherwise inspect '" + ServiceRuntime.ResolveLogPath(handle.RuntimeDirectory) + "' for the failure.");
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }

        throw new InstanceLifecycleException(
            "start_timeout",
            "The service host did not become reachable in time.",
            "Inspect '" + ServiceRuntime.ResolveLogPath(handle.RuntimeDirectory) + "' for the failure, then run 'lifecycle stop' and 'lifecycle start'.");
    }

    /// <summary>
    /// Reads the real owner channel, so the report is the state the service
    /// actually holds, not the state a previous command left behind.
    /// </summary>
    internal static async Task<InstanceStatus> CheckStatusAsync(InstanceHandle handle, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handle);
        var channel = await OwnerChannelClient.ReadStatusAsync(handle.ControlPipe, cancellationToken).ConfigureAwait(false);
        var identity = ServiceRuntime.ReadIdentity(handle.RuntimeDirectory);
        return new InstanceStatus(channel, identity?.ProcessId,
            ServiceRuntime.IsProcessRunning(identity),
            handle.SettingsPath, handle.DataRoot, handle.ControlPipe, handle.AgentPipe);
    }

    /// <summary>
    /// Asks the shared host to stop. The host drains, locks the vault and
    /// exits; the stop waits for the process to be gone and only ever acts on
    /// the process that wrote the identity record. A frontend exiting never
    /// stops the host: only this explicit stop does.
    /// </summary>
    internal static async Task<InstanceStopOutcome> StopAsync(InstanceHandle handle, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handle);
        var identity = ServiceRuntime.ReadIdentity(handle.RuntimeDirectory);
        var reachable = (await OwnerChannelClient.ReadStatusAsync(handle.ControlPipe, CancellationToken.None).ConfigureAwait(false))
            .Availability == OwnerChannelAvailability.Reachable;
        if (!reachable && identity is null)
        {
            return InstanceStopOutcome.AlreadyStopped;
        }

        ServiceRuntime.RequestStop(handle.RuntimeDirectory);
        var deadline = DateTime.UtcNow + StopTimeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var gone = !ServiceRuntime.IsProcessRunning(identity);
            var quiet = (await OwnerChannelClient.ReadStatusAsync(handle.ControlPipe, CancellationToken.None).ConfigureAwait(false))
                .Availability != OwnerChannelAvailability.Reachable;
            if (gone && quiet)
            {
                return InstanceStopOutcome.Stopped;
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }

        // The host did not honour the stop request in time. Terminating it is
        // the last resort an uninstall needs; the key only ever lived in that
        // process, so the next start is locked either way.
        return Terminate(identity)
            ? InstanceStopOutcome.Forced
            : throw new InstanceLifecycleException(
                "stop_failed",
                "The service host did not stop in time and could not be terminated.",
                "End the recorded host process by hand, then run 'lifecycle status' to confirm it is gone.");
    }

    /// <summary>
    /// Stops the host and starts it again. The restart is a fresh host: it
    /// holds no key, the vault is locked again and no agent session survives
    /// it. Vault bytes, snapshots, backups and recovery material survive.
    /// </summary>
    internal static async Task<InstanceStartOutcome> RestartAsync(
        InstanceHandle handle, string hostExecutable, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handle);
        await StopAsync(handle, cancellationToken).ConfigureAwait(false);
        return await EnsureStartedAsync(handle, hostExecutable, cancellationToken).ConfigureAwait(false);
    }

    internal static string CurrentOwnerSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value
            ?? throw new InstanceLifecycleException(
                "owner_unknown",
                "The current account has no user SID.",
                "Run the lifecycle as the owner account the instance belongs to.");
    }

    private static string NewEndpointName(string role)
    {
        var suffix = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        return "metabrain." + role + "." + suffix;
    }

    private static FileStream AcquireStartClaim(string runtimeDirectory)
    {
        // The claim serializes concurrent starters of the same instance: the
        // second opener blocks until the first has either published a reachable
        // host or failed, so it can never spawn a second writer beside it.
        var path = Path.Combine(runtimeDirectory, StartClaimFileName);
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            }
            catch (IOException)
            {
                Thread.Sleep(PollInterval);
            }
        }
    }

    private static bool Terminate(ServiceProcessIdentity? identity)
    {
        if (identity is null || !ServiceRuntime.IsProcessRunning(identity))
        {
            return true;
        }

        try
        {
            using var process = Process.GetProcessById(identity.ProcessId);
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}

internal static class InstanceLifecycleProcessExtensions
{
    internal static ProcessStartInfo WithLifecycleArgs(this ProcessStartInfo start, string settingsPath)
    {
        start.ArgumentList.Add("serve-console");
        start.ArgumentList.Add("--config");
        start.ArgumentList.Add(settingsPath);
        start.ArgumentList.Add("--background");
        return start;
    }
}
