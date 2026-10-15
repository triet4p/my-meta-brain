using System.Windows;
using MetaBrain.Connections;

namespace MetaBrain.OwnerUi;

/// <summary>
/// Entry point of the owner console. The application opens one owner session
/// against an already-running service: it starts nothing, provisions nothing
/// and unlocks nothing, so a fresh start never implies owner consent for a
/// vault operation.
/// </summary>
public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var controlPipe = ParseControlPipe(e.Args);
        if (controlPipe is null)
        {
            Console.Error.WriteLine("Usage: MetaBrain.OwnerUi --control-pipe <owner-control-pipe>");
            Shutdown(2);
            return;
        }

        var window = new OwnerConsoleWindow(new OwnerConsoleSession(controlPipe));
        this.MainWindow = window;
        window.Show();
    }

    private static string? ParseControlPipe(string[] args)
    {
        for (var index = 0; index + 1 < args.Length; index++)
        {
            if (!string.Equals(args[index], "--control-pipe", StringComparison.Ordinal))
            {
                continue;
            }

            var value = args[index + 1];
            return OwnerChannelClient.IsValidControlPipe(value) ? value : null;
        }

        return null;
    }
}
