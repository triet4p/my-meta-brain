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
/// The lifecycle of the installed application service, fronted by one shared
/// instance lifecycle. The host is an ordinary per-user process: it needs no
/// administrator rights, it keeps no key in memory between runs, and every
/// start begins locked. Start, stop, restart and status resolve the installed
/// instance and delegate to the same lifecycle every frontend uses, so
/// concurrent starts can never produce two writers and no frontend keeps a
/// second control path. A stop is a clean shutdown through the owner's own
/// stop request, so the host drains and locks the vault before it exits; the
/// stop waits for the process to be gone, and it only ever acts on the
/// process it started, never on an unrelated process that reuses the pid.
/// </summary>
internal static class ServiceHost
{
    internal static Task<ServiceStartOutcome> StartAsync(InstallLayout layout)
    {
        var handle = InstanceLifecycle.ResolveFromSettingsPath(layout.SettingsPath);
        return StartSharedAsync(handle, layout.ServiceExecutable);
    }

    internal static Task<ServiceStopOutcome> StopAsync(InstallLayout layout)
    {
        var handle = InstanceLifecycle.ResolveFromSettingsPath(layout.SettingsPath);
        return StopSharedAsync(handle);
    }

    /// <summary>
    /// Reports whether the host this installation started is still running.
    /// The pid is checked against the start time the host recorded, so a
    /// recycled pid is never mistaken for the host.
    /// </summary>
    internal static bool IsRunning(ServiceProcessIdentity? identity) =>
        ServiceRuntime.IsProcessRunning(identity);

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
        catch (InstanceLifecycleException)
        {
            return false;
        }
    }

    internal static string ReadControlPipe(InstallLayout layout)
    {
        var settings = ServiceSettingsLoader.Load(layout.SettingsPath);
        return settings.ControlPipe;
    }

    private static async Task<ServiceStartOutcome> StartSharedAsync(InstanceHandle handle, string hostExecutable)
    {
        try
        {
            var outcome = await InstanceLifecycle.EnsureStartedAsync(handle, hostExecutable, CancellationToken.None).ConfigureAwait(false);
            return outcome == InstanceStartOutcome.Started ? ServiceStartOutcome.Started : ServiceStartOutcome.AlreadyRunning;
        }
        catch (InstanceLifecycleException ex) when (string.Equals(ex.Code, "host_unresponsive", StringComparison.Ordinal) ||
            string.Equals(ex.Code, "host_exited", StringComparison.Ordinal) ||
            string.Equals(ex.Code, "start_timeout", StringComparison.Ordinal) ||
            string.Equals(ex.Code, "host_missing", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(ex.Message, ex);
        }
        catch (InstanceLifecycleException ex) when (string.Equals(ex.Code, "version_mismatch", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(ex.Message, ex);
        }
    }

    private static async Task<ServiceStopOutcome> StopSharedAsync(InstanceHandle handle)
    {
        try
        {
            var outcome = await InstanceLifecycle.StopAsync(handle, CancellationToken.None).ConfigureAwait(false);
            return outcome switch
            {
                InstanceStopOutcome.Stopped => ServiceStopOutcome.Stopped,
                InstanceStopOutcome.Forced => ServiceStopOutcome.Forced,
                _ => ServiceStopOutcome.AlreadyStopped,
            };
        }
        catch (InstanceLifecycleException ex) when (string.Equals(ex.Code, "stop_failed", StringComparison.Ordinal))
        {
            return ServiceStopOutcome.Failed;
        }
    }
}
