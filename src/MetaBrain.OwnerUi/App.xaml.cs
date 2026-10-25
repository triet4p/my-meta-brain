using System.Windows;
using MetaBrain.Connections;

namespace MetaBrain.OwnerUi;

/// <summary>
/// Entry point of the owner console. The application opens on the Owner
/// session view with no instance selected: it starts nothing, provisions
/// nothing and unlocks nothing, so a fresh start never implies owner consent
/// for a vault operation. The owner picks the data-root folder, starts the
/// shared locked host, and connects — all as explicit acts on that view.
/// Closing this console never stops the shared host; only Stop service ends it.
/// A legacy --control-pipe argument still attaches the owner session to that
/// pipe for review tooling, but every lifecycle act stays on the new surface.
/// </summary>
public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var controlPipe = ParseControlPipe(e.Args);
        var session = new OwnerConsoleSession(controlPipe ?? "metabrain.owner.not-selected");
        if (controlPipe is not null)
        {
            _ = session.AttachAsync();
        }

        var window = new OwnerConsoleWindow(session, new OwnerInstanceSession());
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
