namespace MetaBrain.Connections;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("Windows named-pipe service only.");
            return 2;
        }

        try
        {
            if (args.Length == 0)
            {
                return Usage();
            }

            return args[0] switch
            {
                "serve-console" => await RunConsoleServiceAsync(Options.Parse(args, 1), CancellationToken.None).ConfigureAwait(false),
                "serve-service" => RunWindowsService(Options.Parse(args, 1)),
                "owner" => await OwnerControlCli.RunAsync(args[1..]).ConfigureAwait(false),
                _ => Usage()
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("service command failed (" + ex.GetType().Name + ")");
            return 1;
        }
    }

    private static async Task<int> RunConsoleServiceAsync(Options options, CancellationToken cancellationToken)
    {
        var settings = ServiceSettingsLoader.Load(options.Required("--config"));
        using var stop = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            stop.Cancel();
        };
        Console.CancelKeyPress += cancel;
        try
        {
            await new NamedPipeService(settings).RunAsync(stop.Token).ConfigureAwait(false);
            return 0;
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
        }
    }

    private static int RunWindowsService(Options options)
    {
        WindowsServiceControl.Run(options.Required("--config"));
        return 0;
    }

    private static int Usage()
    {
        Console.Error.WriteLine("Commands: serve-console --config <owner-settings>; serve-service --config <owner-settings>; owner {status|read|write|provision|unlock|recover|lock}; owner migrate --config <legacy-owner-settings>.");
        return 2;
    }
}

internal sealed class Options
{
    private readonly Dictionary<string, string> _values;

    private Options(Dictionary<string, string> values) => _values = values;

    public string Required(string name) => _values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
        ? value
        : throw new ArgumentException("A required option is missing.");

    public static Options Parse(string[] args, int start)
    {
        if ((args.Length - start) % 2 != 0)
        {
            throw new ArgumentException("Invalid command options.");
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = start; index < args.Length; index += 2)
        {
            if (!args[index].StartsWith("--", StringComparison.Ordinal) || !values.TryAdd(args[index], args[index + 1]))
            {
                throw new ArgumentException("Invalid command options.");
            }
        }

        return new Options(values);
    }
}
