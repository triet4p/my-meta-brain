using System.IO;
using System.Runtime.InteropServices;

namespace MetaBrain.Installer;

/// <summary>
/// The owner's per-user PATH, read and written exactly as the Windows
/// environment block holds it. The installer only ever appends or removes the
/// one CLI directory the owner explicitly asked for; it never rewrites, sorts
/// or normalizes another entry.
/// </summary>
internal interface IUserPathStore
{
    string Read();

    void Write(string value);
}

/// <summary>
/// The real per-user environment value. Writing it stores the owner's own
/// user-scope PATH (the supported Windows mechanism) and broadcasts the
/// environment change so newly started shells inherit it; no profile, system
/// PATH, registry key of its own or shared command directory is touched.
/// </summary>
internal sealed class WindowsUserPathStore : IUserPathStore
{
    private const uint WmSettingChange = 0x001A;
    private const uint SmtoAbortIfHung = 0x0002;
    private static readonly IntPtr HwndBroadcast = new(0xffff);

    public static WindowsUserPathStore Instance { get; } = new();

    public string Read() =>
        Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User) ?? string.Empty;

    public void Write(string value)
    {
        Environment.SetEnvironmentVariable("Path", value, EnvironmentVariableTarget.User);
        // Best effort: shells already running keep their inherited block, and
        // a logon always picks the stored value up. The result is deliberately
        // not treated as success or failure of the stored entry.
        _ = SendMessageTimeout(HwndBroadcast, WmSettingChange, IntPtr.Zero, "Environment",
            SmtoAbortIfHung, 5000, out _);
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd, uint message, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);
}

/// <summary>
/// The exact PATH text rules. Appending and removing are pure string
/// operations so the ownership contract is testable without touching a real
/// environment: existing entries and their order are preserved, the value is
/// never sorted or trimmed, and removal deletes exactly one occurrence of the
/// entry this installation added.
/// </summary>
internal static class UserPathText
{
    internal static string NormalizeEntry(string entry) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(entry));

    internal static bool Contains(string? path, string entry)
    {
        var wanted = NormalizeEntry(entry);
        foreach (var segment in Segments(path))
        {
            if (IsEntry(segment, wanted))
            {
                return true;
            }
        }

        return false;
    }

    internal static string Append(string? path, string entry)
    {
        if (Contains(path, entry))
        {
            return path ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return entry;
        }

        return path.EndsWith(';') ? path + entry : path + ";" + entry;
    }

    /// <summary>
    /// Removes one occurrence of exactly this entry, preferring the last one so
    /// an entry the owner already had earlier in PATH is left in place. Every
    /// other segment — including empty segments and their order — is kept
    /// verbatim.
    /// </summary>
    internal static (string Value, bool Removed) Remove(string? path, string entry)
    {
        if (string.IsNullOrEmpty(path))
        {
            return (path ?? string.Empty, false);
        }

        var segments = path.Split(';');
        var wanted = NormalizeEntry(entry);
        for (var index = segments.Length - 1; index >= 0; index--)
        {
            if (!IsEntry(segments[index], wanted))
            {
                continue;
            }

            var remaining = new List<string>(segments.Length - 1);
            for (var kept = 0; kept < segments.Length; kept++)
            {
                if (kept != index)
                {
                    remaining.Add(segments[kept]);
                }
            }

            return (string.Join(';', remaining), true);
        }

        return (path, false);
    }

    private static IEnumerable<string> Segments(string? path) =>
        string.IsNullOrEmpty(path) ? Array.Empty<string>() : path.Split(';');

    private static bool IsEntry(string segment, string normalizedEntry)
    {
        var candidate = segment.Trim();
        if (candidate.Length >= 2 && candidate[0] == '"' && candidate[^1] == '"')
        {
            candidate = candidate[1..^1];
        }

        if (candidate.Length == 0)
        {
            return false;
        }

        try
        {
            return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate)),
                normalizedEntry, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
