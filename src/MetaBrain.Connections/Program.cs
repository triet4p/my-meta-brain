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
// S9-T4: the owner console resolves/selects/starts the shared host through
// the same InstanceLifecycle every frontend uses, over these internals.
[assembly: InternalsVisibleTo("MetaBrain.OwnerUi")]
// S9-T6: the packaged CLI entrypoint is the owner CLI product front over the
// same lifecycle and owner commands; it keeps no second implementation.
[assembly: InternalsVisibleTo("MetaBrain.Cli")]

namespace MetaBrain.Connections;

/// <summary>
/// Product bootstrap for the packaged MCP executable. It selects or creates
/// the existing instance and starts the shared host locked before exposing
/// only the existing agent-channel MCP bridge.
/// </summary>
public static class McpAgentProduct
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("Meta Brain MCP requires Windows named pipes.");
            return 2;
        }

        if (args.Length == 1 && args[0] is "--version" or "-v")
        {
            // MCP keeps stdout for JSON-RPC, so an informational render goes to stderr.
            Console.Error.WriteLine(ProductInfo.EntrypointName + " " + ProductInfo.Version);
            return 0;
        }

        if (args.Length == 1 && args[0] is "--help" or "-h")
        {
            Console.Error.WriteLine("MetaBrain.Mcp.exe " + ProductInfo.Version + " — agent MCP server (stdio) for the shared Meta Brain instance");
            Console.Error.WriteLine("usage: MetaBrain.Mcp.exe --data-root <dir> | --config <service-settings.json>");
            Console.Error.WriteLine("Starts or connects the same shared locked host. JSON-RPC stays on stdout; diagnostics go to stderr.");
            Console.Error.WriteLine("Owner decisions stay in the separately installed CLI or Owner UI; neither is included in this MCP package.");
            Console.Error.WriteLine("Standalone MCP package layout: mcp/ (this entrypoint), service/ (shared host), licenses/, package-manifest.json, release notes.");
            return 0;
        }

        try
        {
            var instance = McpProductOptions.Parse(args);
            var handle = instance.SettingsPath is { } settingsPath
                ? InstanceLifecycle.ResolveFromSettingsPath(settingsPath)
                : InstanceLifecycle.Create(instance.DataRoot!);
            var hostExecutable = ProductHost.Resolve();
            await InstanceLifecycle.EnsureStartedAsync(handle, hostExecutable, CancellationToken.None).ConfigureAwait(false);
            Console.Error.WriteLine("Meta Brain MCP connected to the shared locked host.");
            return await McpAgentBridge.RunAsync(new[] { "--agent-pipe", handle.AgentPipe }, hostExecutable).ConfigureAwait(false);
        }
        catch (InstanceLifecycleException ex)
        {
            Console.Error.WriteLine("MCP startup failed (" + ex.Code + ").");
            return 1;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Console.Error.WriteLine("MCP startup failed (" + ex.GetType().Name + ").");
            return 1;
        }
    }

    private sealed record McpProductOptions(string? DataRoot, string? SettingsPath)
    {
        public static McpProductOptions Parse(string[] args)
        {
            if (args.Length != 2 || args[0] is not ("--data-root" or "--config") ||
                string.IsNullOrWhiteSpace(args[1]))
            {
                throw new ArgumentException("Pass exactly one instance selector.");
            }

            return args[0] switch
            {
                "--data-root" => new McpProductOptions(args[1], null),
                "--config" => new McpProductOptions(null, args[1]),
                _ => throw new ArgumentException("Unknown instance selector."),
            };
        }
    }
}

/// <summary>
/// Product bootstrap for the packaged owner CLI (<c>cli/MetaBrain.Cli.exe</c>).
/// It exposes the existing shared lifecycle and owner commands under the
/// product entrypoint; it adds no operation, reaches no vault file and applies
/// no policy of its own. The shared host's own service modes stay internal to
/// <c>service/MetaBrain.Connections.exe</c>.
/// </summary>
public static class OwnerCliProduct
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("Meta Brain CLI is Windows-only.");
            return 2;
        }

        if (args.Length == 1 && args[0] is "--version" or "-v")
        {
            Console.WriteLine(ProductInfo.EntrypointName + " " + ProductInfo.Version);
            return 0;
        }

        if (args.Length == 1 && args[0] is "--help" or "-h")
        {
            Help();
            return 0;
        }

        if (args.Length == 0)
        {
            Help();
            return 2;
        }

        return args[0] switch
        {
            "lifecycle" => await InstanceLifecycleCli.RunAsync(args[1..]).ConfigureAwait(false),
            "owner" => await OwnerControlCli.RunAsync(args[1..]).ConfigureAwait(false),
            _ => UnknownCommand(args[0])
        };
    }

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine("ERROR unknown_command: " + command + " is not a Meta Brain CLI command.");
        Console.Error.WriteLine("HINT run `MetaBrain.Cli.exe --help`; setup verbs are `lifecycle <verb>` and owner workflows are `owner <command>`.");
        return 2;
    }

    private static void Help()
    {
        Console.Error.WriteLine("Meta Brain CLI " + ProductInfo.Version + " — owner terminal product for the shared Meta Brain instance");
        Console.Error.WriteLine("usage:");
        Console.Error.WriteLine("  MetaBrain.Cli.exe lifecycle {start|stop|restart|status|connect} --data-root <dir> | --config <service-settings.json>");
        Console.Error.WriteLine("  MetaBrain.Cli.exe owner <command> --control-pipe <pipe> [options]");
        Console.Error.WriteLine("  MetaBrain.Cli.exe owner --help            every owner workflow group");
        Console.Error.WriteLine("  MetaBrain.Cli.exe owner <verb> --help     the exact inputs of one command");
        Console.Error.WriteLine("release layout (one shared instance, one locked host, one vault):");
        Console.Error.WriteLine("  cli\\MetaBrain.Cli.exe               this owner CLI");
        Console.Error.WriteLine("  owner-ui\\MetaBrain.OwnerUi.exe      owner console: same instance, same owner workflows");
        Console.Error.WriteLine("  mcp\\MetaBrain.Mcp.exe               agent MCP server over stdio: same instance, agent-scoped tools only");
        Console.Error.WriteLine("  service\\MetaBrain.Connections.exe   shared runtime host: started by lifecycle, never a product entrypoint");
        Console.Error.WriteLine("  installer\\MetaBrain.Installer.exe   per-user install/upgrade/uninstall; always keeps the data root");
        Console.Error.WriteLine("A clean --data-root receives only the nonsensitive service-settings.json; an existing root keeps its");
        Console.Error.WriteLine("settings, endpoints, vault, sources and backups. Starting a frontend never provisions, unlocks, publishes,");
        Console.Error.WriteLine("approves or purges anything.");
        Console.Error.WriteLine("Credentials travel only through secure prompts (never argv/env/config/log/model context); destructive or");
        Console.Error.WriteLine("authority-minting acts print a frozen preview and require a typed confirmation.");
        Console.Error.WriteLine("exit codes: 0 done, 2 usage (nothing changed), 3 refused/locked/unavailable/denied, 4 invalid input or");
        Console.Error.WriteLine("unwritable output, 5 a job that ended without completing, 1 transport or unexpected failure.");
    }
}


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
        // The shared host carries the internal service modes and the same
        // lifecycle/owner/agent commands the product frontends reach through
        // this assembly. The packaged CLI entrypoint (cli\MetaBrain.Cli.exe)
        // and the MCP entrypoint (mcp\MetaBrain.Mcp.exe) never need a hand-made
        // settings file, a PowerShell helper or a direct vault write; the
        // grouped owner text below is the command surface they share.
        Console.Error.WriteLine("MetaBrain shared host " + ProductInfo.Version + ": service\\MetaBrain.Connections.exe serve-console|serve-service --config <service-settings.json> [--background] (started by the shared lifecycle; not a product entrypoint)");
        Console.Error.WriteLine("  product CLI: cli\\MetaBrain.Cli.exe lifecycle {start|stop|restart|status|connect} --config <service-settings.json> | --data-root <dir>");
        Console.Error.WriteLine("  setup: lifecycle start|status|connect|stop|restart — resolve/create/select the instance and start or connect the shared locked host");
        Console.Error.WriteLine("MetaBrain CLI: MetaBrain.Cli.exe owner <command> --control-pipe <pipe> [options]  (owner <command> --help shows one command)");
        Console.Error.WriteLine("  vault: owner {status|provision|unlock|recover|lock|upgrade|upgrade-with-recovery-code|rollback|rollback-with-recovery-code|health|audit|diagnostics|retention|backup|restore|restore-with-recovery-code|export} — provision/recovery/unlock/lock, audit/diagnostics/retention, encrypted backup/restore/export and supported upgrade");
        Console.Error.WriteLine("  capture: owner {memory-create|memory-revise|memory-import|memory-read|read|write|source-create|source-revise|source-read|research-capture|research-read|feedback-create|feedback-read} — capture and read-back with --body/--title/--note/--input-file (an advanced owner-owned file may carry the exact draft JSON instead)");
        Console.Error.WriteLine("  library: owner {library|memory-read|source-read|research-read|relation-read|lifecycle-read|lifecycle-retained-read|recall-read|review-list|review-record|index-status|index-rebuild} — filter/detail/evidence/current-history/revise/index recovery");
        Console.Error.WriteLine("  review: owner {proposal-list|proposal-read|proposal-publish|proposal-reject|lifecycle-publish|lifecycle-reject|lifecycle-withdraw|lifecycle-repropose|lifecycle-question-resolve|lifecycle-question-reopen|relation-list|relation-read|relation-create|relation-accept|relation-reject|relation-unlink|relation-relink|declassify-preview|declassify-publish|declassify-proposal-publish} — proposal review/publish/reject/undo and relations");
        Console.Error.WriteLine("  sharing: owner {catalog-preview|catalog-publish|catalog-list|catalog-withdraw|collections|collection-set|grants|grant|requests|request-preview|request-approve|request-reject|sessions|session|revoke-session|redeem} (+ agent-mcp transport) — catalog/requests/preview/narrow/approve/reject/handoff/sessions/revoke");
        Console.Error.WriteLine("  sources: owner {collections|source-list|source-status|source-enroll|source-folder-enroll|source-relocate|source-revoke|source-ingest|source-pause|source-resume|source-refresh-optin|source-refresh-optout|source-refresh|source-mapping-submit|source-mapping-resolve|source-snapshot-list|source-snapshot-preserve|source-snapshot-read|source-snapshot-retain|source-snapshot-purge} — zone/collection and source mapping/enroll/ingest/refresh/preservation");
        Console.Error.WriteLine("  planning: owner {lifecycle-create|lifecycle-validity-set|lifecycle-retain|lifecycle-intention-complete|lifecycle-intention-cancel|lifecycle-intention-supersede|lifecycle-intention-notify|lifecycle-intention-acknowledge|intentions|intention-trigger-set|intention-trigger-clear|intention-context-set|intention-context-disable|intention-schedule-run|recall-apply|purge-preview|purge-confirm|purge-resume} — intentions/review/lifecycle/archive/purge");
        Console.Error.WriteLine("  models: owner {provider-config|provider-status|job-create|job-start|job-cancel|job-status|job-list|job-ledger|schedule-set|schedule-disable|schedule-status|schedule-run} — provider configuration/jobs/schedules inside consent (config/refusal paths only without owner consent; no real provider call is made to pass a gate)");
        Console.Error.WriteLine("Credentials travel only through secure interactive prompts (vault passphrase, recovery key, provider key); they are never argv/env/config/log/model values. Destructive or authority-minting acts print a frozen preview and require a typed confirmation (ISSUE/SET/PUBLISH/APPROVE/REJECT/PURGE). Every failure is truthful with the next action; exit codes: 0 ok, 2 usage, 3 refused/locked/unavailable/denied, 4 invalid input or unwritable output, 5 job finished non-ok, 1 transport/unexpected.");
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
