using System.Runtime.InteropServices;
using System.Text;

namespace MetaBrain.Connections;

/// <summary>
/// The installed host runs without a console window: it is started by the
/// installer or by the login autostart entry, not by a person watching a
/// terminal. The host's own diagnostics (readiness, refusals, failure types)
/// go to the runtime log instead of a window that nobody sees. Nothing but
/// operational messages is written: the service never logs a key, a token or
/// private content.
/// </summary>
internal static class BackgroundConsole
{
    private const int SwHide = 0;

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll", EntryPoint = "ShowWindowW", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int command);

    internal static void Attach(string logPath)
    {
        HideConsoleWindow();
        if (!File.Exists(logPath))
        {
            // The file is created empty first, so the first diagnostic byte
            // never lands in a file that does not exist yet.
            using var created = new FileStream(logPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        }

        var stream = new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        Console.SetOut(writer);
        Console.SetError(writer);
    }

    /// <summary>
    /// Hiding the window is cosmetic: a host that cannot reach the window
    /// API still runs correctly with its diagnostics in the runtime log.
    /// </summary>
    private static void HideConsoleWindow()
    {
        try
        {
            var window = GetConsoleWindow();
            if (window != IntPtr.Zero)
            {
                ShowWindow(window, SwHide);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }
    }
}
