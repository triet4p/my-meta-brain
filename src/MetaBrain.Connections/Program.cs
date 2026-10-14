using System.Runtime.CompilerServices;

// S3-T1: the smoke project is the repository's permanent regression suite; it
// hosts the real service stack in-process to prove the valid incremental path
// with a nonshipping synthetic normalizer (no test code ships in the product
// binaries).
[assembly: InternalsVisibleTo("MetaBrain.S1T4.Smoke")]

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
                "agent-mcp" => await McpAgentBridge.RunAsync(args[1..]).ConfigureAwait(false),
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
        Console.Error.WriteLine("Commands: serve-console --config <owner-settings>; serve-service --config <owner-settings>; agent-mcp --agent-pipe <name> (scoped reads return the approved body in memory, no output file); owner {status|read|write --expected-revision|memory-create|memory-revise --expected-revision|memory-import --expected-revision|memory-read|source-create|source-revise --expected-revision|source-read|source-enroll --source-id --zone-id --format --format-version --input-file|source-relocate --source-id --source-generation --input-file|source-revoke --source-id|source-status --source-id|source-ingest --source-id|source-pause --source-id|source-resume --source-id|source-refresh-optin --source-id|source-refresh-optout --source-id|source-list|source-refresh|source-snapshot-list --source-id|source-snapshot-preserve --source-id --source-revision --retain-until-utc|source-snapshot-read --snapshot-id --snapshot-revision --output-file|source-snapshot-retain --snapshot-id --expected-revision --retain-until-utc|source-snapshot-purge --snapshot-id --expected-revision|research-capture --resource-id --zone-id --input-file [--title --source-url --source-author --source-date]|research-read --resource-id [--resource-revision --output-file]|relation-create|relation-accept --expected-revision|relation-reject --expected-revision|relation-unlink --expected-revision|relation-relink --expected-revision|relation-read|lifecycle-create|lifecycle-read|lifecycle-retained-read|lifecycle-publish|lifecycle-reject|lifecycle-withdraw|lifecycle-repropose|lifecycle-question-resolve|lifecycle-question-reopen|lifecycle-intention-complete|lifecycle-intention-cancel|lifecycle-intention-supersede|lifecycle-intention-notify|lifecycle-intention-acknowledge|lifecycle-validity-set|lifecycle-retain|proposal-read --resource-id [--resource-revision]|proposal-list|provision|unlock|recover|lock|grant|grants|collection-set|collections|catalog-preview|catalog-publish|catalog-list|catalog-withdraw|catalog-query|agent-catalog-list|request|request-status|request-preview|request-approve|request-reject|session|agent-collection-view|redeem|sessions|revoke-session|authorize|index-status|index-rebuild|migrate}");
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
