using System.Diagnostics;
using MetaBrain.Connections;

namespace MetaBrain.Installer;

internal enum ServiceStartOutcome
{
    Started = 0,
    AlreadyRunning = 1,
    Failed = 2
}

internal enum ServiceStopOutcome
{
    Stopped = 0,
    AlreadyStopped = 1,
    Failed = 2,
    Forced = 3
}

/// <summary>
/// The lifecycle of the installed application service. The host is an ordinary
/// per-user process: it needs no administrator rights, it keeps no key in
/// memory between runs, and every start begins locked. A stop is a clean
/// shutdown through the owner's own stop request, so the host drains and
/// locks the vault before it exits; the stop waits for the process to be
/// gone, and it only ever acts on the process it started, never on an
/// unrelated process that happens to reuse the pid.
/// </summary>
internal static class ServiceHost
{
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    internal static async Task<ServiceStartOutcome> StartAsync(InstallLayout layout)
    {
        if (!File.Exists(layout.ServiceExecutable))
        {
            throw new FileNotFoundException("The installed service host is missing.", layout.ServiceExecutable);
        }

        var controlPipe = ReadControlPipe(layout);
        if (await IsReachableAsync(controlPipe).ConfigureAwait(false))
        {
            return ServiceStartOutcome.AlreadyRunning;
        }

        Directory.CreateDirectory(layout.RuntimeDirectory);
        var start = new ProcessStartInfo(layout.ServiceExecutable)
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("serve-console");
        start.ArgumentList.Add("--config");
        start.ArgumentList.Add(layout.SettingsPath);
        start.ArgumentList.Add("--background");
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("The installed service host did not start.");

        var deadline = DateTime.UtcNow + ReadyTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await IsReachableAsync(controlPipe).ConfigureAwait(false))
            {
                return ServiceStartOutcome.Started;
            }

            if (process.HasExited)
            {
                throw new InvalidOperationException(
                    "The installed service host exited before it became reachable (exit code " +
                    process.ExitCode + ").");
            }

            await Task.Delay(PollInterval).ConfigureAwait(false);
        }

        throw new TimeoutException("The installed service host did not become reachable in time.");
    }

    internal static async Task<ServiceStopOutcome> StopAsync(InstallLayout layout)
    {
        var controlPipe = ReadControlPipe(layout);
        var identity = ServiceRuntime.ReadIdentity(layout.RuntimeDirectory);
        var reachable = await IsReachableAsync(controlPipe).ConfigureAwait(false);
        if (!reachable && identity is null)
        {
            return ServiceStopOutcome.AlreadyStopped;
        }

        ServiceRuntime.RequestStop(layout.RuntimeDirectory);
        var deadline = DateTime.UtcNow + StopTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!IsRunning(identity) && !await IsReachableAsync(controlPipe).ConfigureAwait(false))
            {
                return ServiceStopOutcome.Stopped;
            }

            await Task.Delay(PollInterval).ConfigureAwait(false);
        }

        // The host did not honour the stop request in time. Terminating it is
        // the last resort an uninstall needs; the vault key only ever lived in
        // that process, so the next start is locked either way. A forced stop
        // is reported as such: it is not the clean shutdown the owner asked
        // for.
        return Terminate(identity) ? ServiceStopOutcome.Forced : ServiceStopOutcome.Failed;
    }

    /// <summary>
    /// Reports whether the host this installation started is still running.
    /// The pid is checked against the start time the host recorded, so a
    /// recycled pid is never mistaken for the host.
    /// </summary>
    internal static bool IsRunning(ServiceProcessIdentity? identity)
    {
        if (identity is null)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(identity.ProcessId);
            if (process.HasExited)
            {
                return false;
            }

            return identity.StartedUtcTicks == 0 ||
                process.StartTime.ToUniversalTime().Ticks == identity.StartedUtcTicks;
        }
        catch (ArgumentException)
        {
            // The process is gone.
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool Terminate(ServiceProcessIdentity? identity)
    {
        if (identity is null || !IsRunning(identity))
        {
            return false;
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
            // The process is already gone.
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    internal static async Task<bool> IsReachableAsync(string controlPipe)
    {
        try
        {
            var status = await OwnerChannelClient.ReadStatusAsync(controlPipe, CancellationToken.None).ConfigureAwait(false);
            return status.Availability == OwnerChannelAvailability.Reachable;
        }
        catch (IOException)
        {
            return false;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    internal static string ReadControlPipe(InstallLayout layout)
    {
        var settings = ServiceSettingsLoader.Load(layout.SettingsPath);
        return settings.ControlPipe;
    }
}
