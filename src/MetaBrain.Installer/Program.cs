using MetaBrain.Connections;

namespace MetaBrain.Installer;

/// <summary>
/// The shared per-user installer and runtime lifecycle commands. The dedicated
/// CLI setup routes install/remove through this implementation so all
/// frontends use one installation state and owner-data location.
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
        if (CliSetup.IsBundledSetup)
        {
            return await CliSetup.RunAsync(args).ConfigureAwait(false);
        }
        if (args.Length == 1 && args[0] is "--version" or "-v")
        {
            Console.WriteLine("MetaBrain.Installer " + ProductInfo.Version);
            return 0;
        }

        if (args.Length == 1 && args[0] is "--help" or "-h" or "help")
        {
            WriteUsage();
            return 0;
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
        WriteUsage();
        return 2;
    }

    private static void WriteUsage()
    {
        Console.Error.WriteLine("Meta Brain installer " + ProductInfo.Version + " — per-user install/upgrade/uninstall; always keeps the data root");
        Console.Error.WriteLine("Commands: install --payload <dir> --install-root <dir> --data-root <dir> [--component cli|owner-ui|mcp|all] [--start] | start | stop | restart | status | health | uninstall [--component cli|owner-ui|mcp|all]");
        Console.Error.WriteLine("Release payload layout: cli/MetaBrain.Cli.exe, owner-ui/MetaBrain.OwnerUi.exe, mcp/MetaBrain.Mcp.exe, service/MetaBrain.Connections.exe, installer/MetaBrain.Installer.exe.");
        Console.Error.WriteLine("Installed layout: <install root>/{cli,owner-ui,mcp,service,installer} plus install-state.json; the data root holds service-settings.json, vault/, runtime/, source snapshots, backups and recovery material.");
        Console.Error.WriteLine("Every service command (start, stop, restart, status, health) accepts --install-root <dir> to address an installation other than the one this installer belongs to.");
        Console.Error.WriteLine("An install or uninstall may name one component; it preserves the shared runtime while any registered component remains and always preserves the data root.");
        Console.Error.WriteLine("The install root and the data root must not overlap: give them sibling directories.");
        Console.Error.WriteLine("Credentials and every owner decision stay in the CLI, the owner console or the MCP client; the installer never provisions, unlocks, publishes, approves or purges.");
    }
}
