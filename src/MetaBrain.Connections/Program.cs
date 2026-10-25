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

        if (args.Length == 1 && args[0] is "--help" or "-h")
        {
            Console.Error.WriteLine("MetaBrain.Mcp.exe --data-root <dir> | --config <service-settings.json>");
            return 0;
        }

        try
        {
            var instance = McpProductOptions.Parse(args);
            var handle = instance.SettingsPath is { } settingsPath
                ? InstanceLifecycle.ResolveFromSettingsPath(settingsPath)
                : InstanceLifecycle.Create(instance.DataRoot!);
            var installRoot = Directory.GetParent(
                Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar))?.FullName;
            if (installRoot is null)
            {
                Console.Error.WriteLine("MCP startup failed (package_layout).");
                return 1;
            }

            var hostExecutable = Path.Combine(installRoot, "service", "MetaBrain.Connections.exe");
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
        // S9-T3 the CLI is a product entrypoint: one executable with contextual
        // help, owner inputs and secure prompts over the shared lifecycle. Every
        // owner workflow below is served by an existing shared operation; the
        // help names the command, never raw JSON assembly, PowerShell glue, a
        // UI switch or a direct vault write. Run `owner <verb> --help` for the
        // exact inputs of one command.
        Console.Error.WriteLine("MetaBrain CLI: MetaBrain.Connections.exe lifecycle {start|stop|restart|status|connect} --config <service-settings.json> | --data-root <dir>");
        Console.Error.WriteLine("  setup: lifecycle start|status|connect|stop|restart — resolve/create/select the instance and start or connect the shared locked host");
        Console.Error.WriteLine("MetaBrain CLI: MetaBrain.Connections.exe owner <command> --control-pipe <pipe> [options]  (owner <command> --help shows one command)");
        Console.Error.WriteLine("  vault: owner {status|provision|unlock|recover|lock|upgrade|upgrade-with-recovery-code|rollback|rollback-with-recovery-code|health|audit|diagnostics|retention|backup|restore|restore-with-recovery-code|export} — provision/recovery/unlock/lock, audit/diagnostics/retention, encrypted backup/restore/export and supported upgrade");
        Console.Error.WriteLine("  capture: owner {memory-create|memory-revise|memory-import|memory-read|read|write|source-create|source-revise|source-read|research-capture|research-read|feedback-create|feedback-read} — capture and read-back with --body/--title/--note/--input-file (an advanced owner-owned file may carry the exact draft JSON instead)");
        Console.Error.WriteLine("  library: owner {library|memory-read|source-read|research-read|relation-read|lifecycle-read|lifecycle-retained-read|recall-read|review-list|review-record|index-status|index-rebuild} — filter/detail/evidence/current-history/revise/index recovery");
        Console.Error.WriteLine("  review: owner {proposal-list|proposal-read|proposal-publish|proposal-reject|lifecycle-publish|lifecycle-reject|lifecycle-withdraw|lifecycle-repropose|lifecycle-question-resolve|lifecycle-question-reopen|relation-list|relation-read|relation-create|relation-accept|relation-reject|relation-unlink|relation-relink|declassify-preview|declassify-publish|declassify-proposal-publish} — proposal review/publish/reject/undo and relations");
        Console.Error.WriteLine("  sharing: owner {catalog-preview|catalog-publish|catalog-list|catalog-withdraw|collections|collection-set|grants|grant|requests|request-preview|request-approve|request-reject|sessions|session|revoke-session|redeem} (+ agent-mcp transport) — catalog/requests/preview/narrow/approve/reject/handoff/sessions/revoke");
        Console.Error.WriteLine("  sources: owner {collections|source-list|source-status|source-enroll|source-relocate|source-revoke|source-ingest|source-pause|source-resume|source-refresh-optin|source-refresh-optout|source-refresh|source-mapping-submit|source-mapping-resolve|source-snapshot-list|source-snapshot-preserve|source-snapshot-read|source-snapshot-retain|source-snapshot-purge} — zone/collection and source mapping/enroll/ingest/refresh/preservation");
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
