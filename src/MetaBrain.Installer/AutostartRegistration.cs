using Microsoft.Win32;

namespace MetaBrain.Installer;

/// <summary>
/// The per-user login autostart entry. It is opt-in: the installer writes it
/// only when the owner asks for it, and it never needs administrator rights
/// because it lives in the current user's Run key. The registered command
/// starts the same installed host the installer starts, in background mode,
/// so a login start is locked exactly like every other start.
/// </summary>
internal sealed class AutostartRegistration
{
    internal const string DefaultKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string ValueName = "MetaBrain";

    private readonly string _keyPath;

    internal AutostartRegistration(string keyPath = DefaultKeyPath) => _keyPath = keyPath;

    internal bool TryRead(out string? command)
    {
        using var key = Registry.CurrentUser.OpenSubKey(_keyPath, writable: false);
        var value = key?.GetValue(ValueName) as string;
        command = string.IsNullOrWhiteSpace(value) ? null : value;
        return command is not null;
    }

    internal void Enable(string command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(_keyPath, writable: true)
            ?? throw new InvalidOperationException("The autostart key could not be created.");
        key.SetValue(ValueName, command, RegistryValueKind.String);
    }

    internal void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_keyPath, writable: true);
        if (key is not null && key.GetValue(ValueName) is not null)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    internal static string BuildCommand(string serviceExecutable, string settingsPath) =>
        "\"" + serviceExecutable + "\" serve-console --config \"" + settingsPath + "\" --background";
}
