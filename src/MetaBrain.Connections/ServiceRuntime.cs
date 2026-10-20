using System.Globalization;

namespace MetaBrain.Connections;

/// <summary>
/// The identity of a running host. The pid alone is not an identity: Windows
/// reuses process ids, so a stop that only knows the pid could address an
/// unrelated process. The start time pins the id to the process that wrote
/// it, and a stop only ever acts on the process it started.
/// </summary>
internal sealed record ServiceProcessIdentity(int ProcessId, long StartedUtcTicks)
{
    internal static ServiceProcessIdentity Current()
    {
        try
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            return new ServiceProcessIdentity(process.Id, process.StartTime.ToUniversalTime().Ticks);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // A host that cannot read its own start time still runs; the pid
            // alone is enough to recognise it, just not to prove it.
            return new ServiceProcessIdentity(Environment.ProcessId, 0);
        }
    }
}

/// <summary>
/// The runtime state the installed service host keeps inside the data root:
/// the process identity file, the stop request and the host log. All of it
/// lives under the data root, none of it holds key, token or private
/// content, and it belongs to the data root, so an uninstall that keeps the
/// data root keeps it as well.
/// </summary>
internal static class ServiceRuntime
{
    internal const string RuntimeDirectoryName = "runtime";
    internal const string PidFileName = "service.pid";
    internal const string StopRequestFileName = "service.stop";
    internal const string LogFileName = "service.log";

    private static readonly TimeSpan StopPollInterval = TimeSpan.FromMilliseconds(200);

    internal static string ResolveRuntimeDirectory(ServiceSettings settings) =>
        Path.Combine(settings.DataRootPath, RuntimeDirectoryName);

    internal static string ResolvePidPath(string runtimeDirectory) => Path.Combine(runtimeDirectory, PidFileName);

    internal static string ResolveStopRequestPath(string runtimeDirectory) => Path.Combine(runtimeDirectory, StopRequestFileName);

    internal static string ResolveLogPath(string runtimeDirectory) => Path.Combine(runtimeDirectory, LogFileName);

    internal static void EnsureRuntimeDirectory(ServiceSettings settings) =>
        Directory.CreateDirectory(ResolveRuntimeDirectory(settings));

    /// <summary>
    /// Removes a stop request left behind by an earlier run. A fresh host must
    /// not inherit a pending stop order from the instance it replaces.
    /// </summary>
    internal static void ClearStopRequest(string runtimeDirectory)
    {
        var path = ResolveStopRequestPath(runtimeDirectory);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Records the running host. The file holds the pid and the start time of
    /// the process that wrote it, so a later stop can tell this host apart
    /// from an unrelated process that happens to reuse the pid.
    /// </summary>
    internal static void WriteIdentity(string runtimeDirectory, ServiceProcessIdentity identity)
    {
        var path = ResolvePidPath(runtimeDirectory);
        File.WriteAllText(path,
            identity.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) + " " +
            identity.StartedUtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    internal static void RemovePid(string runtimeDirectory)
    {
        var path = ResolvePidPath(runtimeDirectory);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    internal static ServiceProcessIdentity? ReadIdentity(string runtimeDirectory)
    {
        var path = ResolvePidPath(runtimeDirectory);
        if (!File.Exists(path))
        {
            return null;
        }

        string text;
        try
        {
            text = File.ReadAllText(path).Trim();
        }
        catch (IOException)
        {
            return null;
        }

        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 ||
            !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var processId) ||
            !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var startedUtcTicks) ||
            processId <= 0)
        {
            return null;
        }

        return new ServiceProcessIdentity(processId, startedUtcTicks);
    }

    /// <summary>
    /// Asks a running host to stop. The request is a file inside the runtime
    /// directory of the installed instance, so it reaches the host that owns
    /// that instance and nothing else.
    /// </summary>
    internal static void RequestStop(string runtimeDirectory)
    {
        Directory.CreateDirectory(runtimeDirectory);
        File.WriteAllText(ResolveStopRequestPath(runtimeDirectory), "stop");
    }

    /// <summary>
    /// Watches for a stop request and cancels the host when one appears. The
    /// host then drains, locks the vault and exits, so a stop is a clean
    /// shutdown rather than a kill.
    /// </summary>
    internal static async Task WatchForStopRequestAsync(string runtimeDirectory, CancellationTokenSource host)
    {
        var path = ResolveStopRequestPath(runtimeDirectory);
        while (!host.Token.IsCancellationRequested)
        {
            if (File.Exists(path))
            {
                host.Cancel();
                return;
            }

            try
            {
                await Task.Delay(StopPollInterval, host.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
