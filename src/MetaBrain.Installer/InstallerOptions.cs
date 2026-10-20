namespace MetaBrain.Installer;

/// <summary>
/// The installer's own option parser: named options with a value plus the
/// small set of explicit flags the installer accepts. Every flag is an owner
/// decision the installer never takes by itself, and an option the command
/// does not know is refused instead of being ignored.
/// </summary>
internal sealed class InstallerOptions
{
    private readonly Dictionary<string, string> _values;
    private readonly HashSet<string> _flags;

    private InstallerOptions(Dictionary<string, string> values, HashSet<string> flags)
    {
        _values = values;
        _flags = flags;
    }

    public string Required(string name) => _values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
        ? value
        : throw new ArgumentException("A required option is missing: " + name + ".");

    public string? Optional(string name) => _values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
        ? value
        : null;

    public bool HasFlag(string name) => _flags.Contains(name);

    public static InstallerOptions Parse(string[] args, string[] allowedOptions, params string[] allowedFlags)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var flags = new HashSet<string>(StringComparer.Ordinal);
        var permittedValues = new HashSet<string>(allowedOptions, StringComparer.Ordinal);
        var permittedFlags = new HashSet<string>(allowedFlags, StringComparer.Ordinal);
        for (var index = 0; index < args.Length;)
        {
            var name = args[index];
            if (permittedFlags.Contains(name))
            {
                if (!flags.Add(name))
                {
                    throw new ArgumentException("A flag was given twice: " + name + ".");
                }

                index++;
                continue;
            }

            if (index + 1 >= args.Length || !permittedValues.Contains(name) ||
                !values.TryAdd(name, args[index + 1]))
            {
                throw new ArgumentException("Invalid command options.");
            }

            index += 2;
        }

        return new InstallerOptions(values, flags);
    }
}
