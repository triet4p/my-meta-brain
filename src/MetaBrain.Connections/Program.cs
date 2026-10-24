using System.Runtime.CompilerServices;

// S3-T1: the smoke project is the repository's permanent regression suite; it
// hosts the real service stack in-process to prove the valid incremental path
// with a nonshipping synthetic normalizer (no test code ships in the product
// binaries).
[assembly: InternalsVisibleTo("MetaBrain.S1T4.Smoke")]
// S8-T1: the installer creates the settings file, the data root and the
// runtime state of the installed service, and it reads the service status
// over the same owner channel the console uses. It shares these internals
// instead of re-implementing the settings format or a second status client.
[assembly: InternalsVisibleTo("MetaBrain.Installer")]

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
                // S9-T2 the one shared resolve/create/select/start/connect lifecycle every frontend uses.
                "lifecycle" => await InstanceLifecycleCli.RunAsync(args[1..]).ConfigureAwait(false),
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
        var background = options.HasFlag("--background");
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ConsoleCancelEventHandler cancel = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            stop.Cancel();
        };
        Console.CancelKeyPress += cancel;
        var runtimeDirectory = ServiceRuntime.ResolveRuntimeDirectory(settings);
        try
        {
            ServiceRuntime.EnsureRuntimeDirectory(settings);
            // A stop request left over from the instance this host replaces
            // must not carry over into the new one.
            ServiceRuntime.ClearStopRequest(runtimeDirectory);
            ServiceRuntime.WriteIdentity(runtimeDirectory, ServiceProcessIdentity.Current());
            if (background)
            {
                // The installed host runs without a console window and writes
                // its (body-free) diagnostics to the runtime log.
                BackgroundConsole.Attach(ServiceRuntime.ResolveLogPath(runtimeDirectory));
            }

            var stopWatcher = ServiceRuntime.WatchForStopRequestAsync(runtimeDirectory, stop);
            try
            {
                await new NamedPipeService(settings).RunAsync(stop.Token).ConfigureAwait(false);
            }
            finally
            {
                // The watcher polls until the stop request appears or the token
                // is cancelled; cancelling here releases it even when the
                // service stopped because of a failure instead of a stop order.
                stop.Cancel();
                await stopWatcher.ConfigureAwait(false);
            }

            return 0;
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
            ServiceRuntime.RemovePid(runtimeDirectory);
        }
    }

    private static int RunWindowsService(Options options)
    {
        WindowsServiceControl.Run(options.Required("--config"));
        return 0;
    }

    private static int Usage()
    {
        Console.Error.WriteLine("Commands: serve-console --config <owner-settings> [--background]; serve-service --config <owner-settings>; agent-mcp --agent-pipe <name> (scoped reads return the approved body in memory, no output file); owner {status|read|write --expected-revision|memory-create|memory-revise --expected-revision|memory-import --expected-revision|memory-read|source-create|source-revise --expected-revision|source-read|source-enroll --source-id --zone-id --format --format-version --input-file|source-relocate --source-id --source-generation --input-file|source-revoke --source-id|source-status --source-id|source-ingest --source-id|source-pause --source-id|source-resume --source-id|source-refresh-optin --source-id|source-refresh-optout --source-id|source-list|source-refresh|source-snapshot-list --source-id|source-snapshot-preserve --source-id --source-revision --retain-until-utc|source-snapshot-read --snapshot-id --snapshot-revision --output-file|source-snapshot-retain --snapshot-id --expected-revision --retain-until-utc|source-snapshot-purge --snapshot-id --expected-revision|research-capture --resource-id --zone-id --input-file [--title --source-url --source-author --source-date]|research-read --resource-id [--resource-revision --output-file]|relation-create|relation-accept --expected-revision|relation-reject --expected-revision|relation-unlink --expected-revision|relation-relink --expected-revision|relation-read|lifecycle-create|lifecycle-read|lifecycle-retained-read|lifecycle-publish|lifecycle-reject|lifecycle-withdraw|lifecycle-repropose|lifecycle-question-resolve|lifecycle-question-reopen|lifecycle-intention-complete|lifecycle-intention-cancel|lifecycle-intention-supersede|lifecycle-intention-notify|lifecycle-intention-acknowledge|lifecycle-validity-set|lifecycle-retain|proposal-read --resource-id [--resource-revision]|proposal-list|provision|unlock|recover|lock|grant|grants|collection-set|collections|catalog-preview|catalog-publish|catalog-list|catalog-withdraw|catalog-query|agent-catalog-list|request|request-status|request-preview|request-approve|request-reject|session|agent-collection-view|agent-relation-resolve|agent-relation-list|agent-relation-create|agent-relation-supersede|agent-relation-unlink|agent-relation-restore|redeem|sessions|revoke-session|authorize|index-status|index-rebuild|migrate}");
        return 2;
    }
}

internal sealed class Options
{
    private static readonly string[] KnownFlags = { "--background" };

    private readonly Dictionary<string, string> _values;
    private readonly HashSet<string> _flags;

    private Options(Dictionary<string, string> values, HashSet<string> flags)
    {
        _values = values;
        _flags = flags;
    }

    public string Required(string name) => _values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
        ? value
        : throw new ArgumentException("A required option is missing.");

    public bool HasFlag(string name) => _flags.Contains(name);

    public static Options Parse(string[] args, int start)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var flags = new HashSet<string>(StringComparer.Ordinal);
        for (var index = start; index < args.Length;)
        {
            var name = args[index];
            if (KnownFlags.Contains(name, StringComparer.Ordinal))
            {
                if (!flags.Add(name))
                {
                    throw new ArgumentException("Invalid command options.");
                }

                index++;
                continue;
            }

            if (index + 1 >= args.Length || !name.StartsWith("--", StringComparison.Ordinal) ||
                !values.TryAdd(name, args[index + 1]))
            {
                throw new ArgumentException("Invalid command options.");
            }

            index += 2;
        }

        return new Options(values, flags);
    }
}
