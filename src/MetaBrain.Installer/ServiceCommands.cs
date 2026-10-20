using MetaBrain.Connections;

namespace MetaBrain.Installer;

/// <summary>
/// The day-to-day service commands: start, stop, restart, the health report
/// and the autostart entry. The health report reads the real owner channel,
/// so it reports the state the service actually holds, not the state the
/// installer believes it left behind. Every command accepts an explicit
/// <c>--install-root</c>, so the owner can address an installation that is
/// not the one the running installer belongs to.
/// </summary>
internal static class ServiceCommands
{
    internal static async Task<int> StartAsync(string[] args)
    {
        var options = InstallerOptions.Parse(args, new[] { "--install-root" });
        var layout = InstallLayout.Locate(options.Optional("--install-root"));
        var outcome = await ServiceHost.StartAsync(layout).ConfigureAwait(false);
        Console.WriteLine(outcome == ServiceStartOutcome.AlreadyRunning ? "service=already running" : "service=started");
        await ReportStatusAsync(layout).ConfigureAwait(false);
        return outcome == ServiceStartOutcome.Failed ? 1 : 0;
    }

    internal static async Task<int> StopAsync(string[] args)
    {
        var options = InstallerOptions.Parse(args, new[] { "--install-root" });
        var layout = InstallLayout.Locate(options.Optional("--install-root"));
        var outcome = await ServiceHost.StopAsync(layout).ConfigureAwait(false);
        Console.WriteLine(outcome switch
        {
            ServiceStopOutcome.Stopped => "service=stopped",
            ServiceStopOutcome.AlreadyStopped => "service=not running",
            ServiceStopOutcome.Forced => "service=stopped (forced; the host did not drain in time)",
            _ => "service=stop request timed out"
        });
        return outcome == ServiceStopOutcome.Failed ? 1 : 0;
    }

    internal static async Task<int> RestartAsync(string[] args)
    {
        var options = InstallerOptions.Parse(args, new[] { "--install-root" });
        var layout = InstallLayout.Locate(options.Optional("--install-root"));
        await ServiceHost.StopAsync(layout).ConfigureAwait(false);
        var outcome = await ServiceHost.StartAsync(layout).ConfigureAwait(false);
        Console.WriteLine(outcome == ServiceStartOutcome.AlreadyRunning ? "service=already running" : "service=started");
        // A restart is a fresh host: it holds no key, so the vault is locked
        // again and no session survives it.
        await ReportStatusAsync(layout).ConfigureAwait(false);
        return outcome == ServiceStartOutcome.Failed ? 1 : 0;
    }

    internal static async Task<int> StatusAsync(string[] args)
    {
        var options = InstallerOptions.Parse(args, new[] { "--install-root" });
        var layout = InstallLayout.Locate(options.Optional("--install-root"));
        var healthy = await ReportStatusAsync(layout).ConfigureAwait(false);
        return healthy ? 0 : 1;
    }

    internal static async Task<int> AutostartAsync(string[] args)
    {
        if (args.Length == 0)
        {
            return Usage();
        }

        var options = InstallerOptions.Parse(args[1..], new[] { "--install-root" });
        var layout = InstallLayout.Locate(options.Optional("--install-root"));
        var registration = new AutostartRegistration();

        switch (args[0])
        {
            case "enable":
                registration.Enable(AutostartRegistration.BuildCommand(layout.ServiceExecutable, layout.SettingsPath));
                Console.WriteLine("autostart=enabled");
                Console.WriteLine("autostart command=" + AutostartRegistration.BuildCommand(layout.ServiceExecutable, layout.SettingsPath));
                return 0;
            case "disable":
                registration.Disable();
                Console.WriteLine("autostart=disabled");
                return 0;
            case "status":
                Console.WriteLine(registration.TryRead(out var command) ? "autostart=enabled" : "autostart=disabled");
                if (command is not null)
                {
                    Console.WriteLine("autostart command=" + command);
                }

                return 0;
            default:
                return Usage();
        }
    }

    private static async Task<bool> ReportStatusAsync(InstallLayout layout)
    {
        Console.WriteLine("install root=" + layout.InstallRoot);
        Console.WriteLine("data root=" + layout.DataRoot);
        Console.WriteLine("settings=" + layout.SettingsPath);
        var settings = ServiceSettingsLoader.Load(layout.SettingsPath);
        Console.WriteLine("control pipe=" + settings.ControlPipe);
        Console.WriteLine("agent pipe=" + settings.AgentPipe);

        var identity = ServiceRuntime.ReadIdentity(layout.RuntimeDirectory);
        Console.WriteLine("service process=" + (identity is null
            ? "none"
            : identity.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) +
              " (running=" + (ServiceHost.IsRunning(identity) ? "yes" : "no") + ")"));

        var status = await OwnerChannelClient.ReadStatusAsync(settings.ControlPipe, CancellationToken.None).ConfigureAwait(false);
        Console.WriteLine("service=" + (status.Availability == OwnerChannelAvailability.Reachable ? "running" : "not reachable"));
        Console.WriteLine("vault=" + status.VaultState.ToString().ToLowerInvariant());
        if (status.ErrorCode is not null)
        {
            Console.WriteLine("error=" + status.ErrorCode);
        }

        var registration = new AutostartRegistration();
        Console.WriteLine(registration.TryRead(out var command) ? "autostart=enabled" : "autostart=disabled");
        if (command is not null)
        {
            Console.WriteLine("autostart command=" + command);
        }

        return status.Availability == OwnerChannelAvailability.Reachable;
    }

    private static int Usage()
    {
        Console.Error.WriteLine("autostart {enable|disable|status} [--install-root <dir>]");
        return 2;
    }
}
