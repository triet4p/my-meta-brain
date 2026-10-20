namespace MetaBrain.Installer;

/// <summary>
/// The Meta Brain installer. It installs the application service, the owner
/// console and the installer itself for the current user, starts the service
/// on request, reports its health, and removes the installation again. It
/// never needs administrator rights and never touches the vault, the source
/// snapshots, the backups or the recovery material unless the owner asks for
/// it explicitly.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("The Meta Brain installer is Windows-only.");
            return 2;
        }

        if (args.Length == 0)
        {
            return Usage();
        }

        try
        {
            return args[0] switch
            {
                "install" => await InstallCommand.RunAsync(args[1..]).ConfigureAwait(false),
                "start" => await ServiceCommands.StartAsync(args[1..]).ConfigureAwait(false),
                "stop" => await ServiceCommands.StopAsync(args[1..]).ConfigureAwait(false),
                "restart" => await ServiceCommands.RestartAsync(args[1..]).ConfigureAwait(false),
                "status" or "health" => await ServiceCommands.StatusAsync(args[1..]).ConfigureAwait(false),
                "autostart" => await ServiceCommands.AutostartAsync(args[1..]).ConfigureAwait(false),
                "uninstall" => await InstallCommand.UninstallAsync(args[1..]).ConfigureAwait(false),
                _ => Usage()
            };
        }
        catch (Exception ex)
        {
            // The failure type and message are reported; the stack trace is
            // not, because a trace carries paths of the owner's own machine
            // into a log the installer does not control.
            Console.Error.WriteLine("installer command failed (" + ex.GetType().Name + "): " + ex.Message);
            return 1;
        }
    }

    private static int Usage()
    {
        Console.Error.WriteLine("Commands: install --payload <dir> --install-root <dir> --data-root <dir> [--control-pipe <name>] [--agent-pipe <name>] [--autostart] [--start] | start | stop | restart | status | health | autostart {enable|disable|status} | uninstall");
        Console.Error.WriteLine("Every service command (start, stop, restart, status, health) and the autostart command accept --install-root <dir> to address an installation other than the one this installer belongs to.");
        Console.Error.WriteLine("uninstall takes no options and always acts on the installation this installer belongs to; an option a command does not know is refused, never ignored.");
        Console.Error.WriteLine("install lays the application down for the current user, keeps the vault and the rest of the data root, and starts locked; --autostart is an explicit opt-in for a login start; uninstall removes the application and always preserves the data root.");
        Console.Error.WriteLine("The install root and the data root must not overlap: give them sibling directories.");
        return 2;
    }
}
