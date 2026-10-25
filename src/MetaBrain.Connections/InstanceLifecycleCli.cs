using MetaBrain.Application;

namespace MetaBrain.Connections;

/// <summary>
/// The packaged minimal lifecycle verbs: one runnable surface over the shared
/// <see cref="InstanceLifecycle"/> for the future CLI, UI and MCP entrypoints.
/// Every verb resolves the same instance (from <c>--config</c> or
/// <c>--data-root</c>), creates the settings on a clean root without any
/// hand-written file, keeps an existing root byte-identical, and only writes
/// nonsensitive configuration. No verb provisions, unlocks, approves or
/// purges; no verb carries a key, credential, token or private content.
/// </summary>
internal static class InstanceLifecycleCli
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0)
        {
            return Usage();
        }

        try
        {
            return args[0] switch
            {
                "start" => await StartAsync(args[1..]).ConfigureAwait(false),
                "stop" => await StopAsync(args[1..]).ConfigureAwait(false),
                "restart" => await RestartAsync(args[1..]).ConfigureAwait(false),
                "status" => await StatusAsync(args[1..]).ConfigureAwait(false),
                "connect" => await ConnectAsync(args[1..]).ConfigureAwait(false),
                _ => Usage()
            };
        }
        catch (InstanceLifecycleException ex)
        {
            Console.Error.WriteLine("ERROR " + ex.Code + ": " + ex.Message);
            Console.Error.WriteLine("HINT " + ex.Remediation);
            return 4;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            Console.Error.WriteLine("lifecycle command failed (" + ex.GetType().Name + "): " + ex.Message);
            return 1;
        }
    }

    private static InstanceHandle Resolve(string[] args)
    {
        var config = Option(args, "--config");
        var dataRoot = Option(args, "--data-root");
        if (config is not null && dataRoot is not null)
        {
            throw new InstanceLifecycleException(
                "ambiguous_instance",
                "Pass either --config or --data-root, not both.",
                "Use --config <service-settings.json> for an existing instance, or --data-root <dir> to select or create one.");
        }

        if (config is not null)
        {
            return InstanceLifecycle.ResolveFromSettingsPath(config);
        }

        if (dataRoot is not null)
        {
            var path = ServiceSettingsFile.ResolvePath(Path.GetFullPath(dataRoot));
            return File.Exists(path)
                ? InstanceLifecycle.ResolveFromDataRoot(dataRoot)
                : InstanceLifecycle.Create(dataRoot, Option(args, "--control-pipe"), Option(args, "--agent-pipe"));
        }

        throw new InstanceLifecycleException(
            "instance_not_selected",
            "No instance was selected.",
            "Pass --config <service-settings.json> or --data-root <dir>.");
    }

    private static string HostExecutable(string[] args)
    {
        var host = Option(args, "--host");
        if (!string.IsNullOrWhiteSpace(host))
        {
            return Path.GetFullPath(host);
        }

        return ProductHost.Resolve();
    }

    private static async Task<int> StartAsync(string[] args)
    {
        RejectUnknown(args, "--config", "--data-root", "--control-pipe", "--agent-pipe", "--host");
        var handle = Resolve(args);
        var outcome = await InstanceLifecycle.EnsureStartedAsync(handle, HostExecutable(args), CancellationToken.None).ConfigureAwait(false);
        Console.WriteLine(outcome == InstanceStartOutcome.Started ? "service=started (locked)" : "service=already running");
        return await ReportAsync(handle).ConfigureAwait(false);
    }

    private static async Task<int> StopAsync(string[] args)
    {
        RejectUnknown(args, "--config", "--data-root");
        var handle = ResolveExisting(args);
        var outcome = await InstanceLifecycle.StopAsync(handle, CancellationToken.None).ConfigureAwait(false);
        Console.WriteLine(outcome switch
        {
            InstanceStopOutcome.Stopped => "service=stopped",
            InstanceStopOutcome.Forced => "service=stopped (forced)",
            _ => "service=already stopped"
        });
        return 0;
    }

    private static async Task<int> RestartAsync(string[] args)
    {
        RejectUnknown(args, "--config", "--data-root", "--host");
        var handle = ResolveExisting(args);
        var outcome = await InstanceLifecycle.RestartAsync(handle, HostExecutable(args), CancellationToken.None).ConfigureAwait(false);
        Console.WriteLine(outcome == InstanceStartOutcome.Started ? "service=restarted (locked)" : "service=already running");
        Console.WriteLine("sessions are process memory: no agent session survives a restart");
        return await ReportAsync(handle).ConfigureAwait(false);
    }

    private static async Task<int> StatusAsync(string[] args)
    {
        RejectUnknown(args, "--config", "--data-root");
        var handle = ResolveExisting(args);
        return await ReportAsync(handle).ConfigureAwait(false);
    }

    private static async Task<int> ConnectAsync(string[] args)
    {
        // T3–T5 own the entry-specific UX; connect is the readiness gate they
        // share: it never starts a host, it only proves the shared host is
        // reachable and reports the real version, vault state and endpoints.
        RejectUnknown(args, "--config", "--data-root");
        var handle = ResolveExisting(args);
        var status = await InstanceLifecycle.CheckStatusAsync(handle, CancellationToken.None).ConfigureAwait(false);
        if (status.Channel.Availability == OwnerChannelAvailability.ProtocolMismatch)
        {
            throw new InstanceLifecycleException(
                "version_mismatch",
                "The running host speaks a different protocol revision.",
                "Upgrade the application payload to the build this instance was created with, then run 'lifecycle restart'.");
        }

        if (status.Channel.Availability != OwnerChannelAvailability.Reachable)
        {
            var code = status.Channel.ErrorCode ?? "service_unavailable";
            throw new InstanceLifecycleException(
                code,
                "The shared host is not reachable: " + code + ".",
                "Run 'lifecycle start' to start it, or check the endpoints with 'lifecycle status'.");
        }

        Console.WriteLine("connected protocol=" + ServiceRequestHandler.CurrentProtocolVersion +
            " vault=" + status.Channel.VaultState.ToString().ToLowerInvariant());
        Console.WriteLine("control pipe=" + handle.ControlPipe);
        Console.WriteLine("agent pipe=" + handle.AgentPipe);
        return 0;
    }

    private static async Task<int> ReportAsync(InstanceHandle handle)
    {
        var status = await InstanceLifecycle.CheckStatusAsync(handle, CancellationToken.None).ConfigureAwait(false);
        Console.WriteLine("settings=" + handle.SettingsPath);
        Console.WriteLine("data root=" + handle.DataRoot);
        Console.WriteLine("control pipe=" + handle.ControlPipe);
        Console.WriteLine("agent pipe=" + handle.AgentPipe);
        Console.WriteLine("service=" + (status.Channel.Availability == OwnerChannelAvailability.Reachable ? "running" : "not reachable") +
            (status.Channel.ErrorCode is null ? string.Empty : " (" + status.Channel.ErrorCode + ")"));
        Console.WriteLine("vault=" + status.Channel.VaultState.ToString().ToLowerInvariant());
        Console.WriteLine("host process=" + (status.HostProcessId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none") +
            " (running=" + (status.HostProcessRunning ? "yes" : "no") + ")");
        Console.WriteLine(status.Channel.Availability != OwnerChannelAvailability.Reachable
            ? "start the host when this instance has none running; stopping this frontend never stops the shared host"
            : "leaving this frontend never stops the shared host; use 'lifecycle stop' to end it explicitly");
        return status.Channel.Availability == OwnerChannelAvailability.Reachable ? 0 : 3;
    }

    private static InstanceHandle ResolveExisting(string[] args)
    {
        var config = Option(args, "--config");
        var dataRoot = Option(args, "--data-root");
        if (config is not null && dataRoot is not null)
        {
            throw new InstanceLifecycleException(
                "ambiguous_instance",
                "Pass either --config or --data-root, not both.",
                "Use --config <service-settings.json> for an existing instance, or --data-root <dir> for one.");
        }

        if (config is not null)
        {
            return InstanceLifecycle.ResolveFromSettingsPath(config);
        }

        if (dataRoot is not null)
        {
            return InstanceLifecycle.ResolveFromDataRoot(dataRoot);
        }

        throw new InstanceLifecycleException(
            "instance_not_selected",
            "No instance was selected.",
            "Pass --config <service-settings.json> or --data-root <dir>.");
    }

    private static string? Option(string[] args, string name)
    {
        for (var index = 0; index + 1 < args.Length; index += 2)
        {
            if (string.Equals(args[index], name, StringComparison.Ordinal))
            {
                return args[index + 1];
            }
        }

        return null;
    }

    private static void RejectUnknown(string[] args, params string[] allowed)
    {
        for (var index = 0; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length || !allowed.Contains(args[index], StringComparer.Ordinal))
            {
                throw new InstanceLifecycleException(
                    "invalid_options",
                    "An option is not valid for this command.",
                    "See 'lifecycle' usage: only the documented options are accepted, never ignored.");
            }
        }

        if (args.Length % 2 != 0)
        {
            throw new InstanceLifecycleException(
                "invalid_options",
                "An option is missing its value.",
                "Pass every option as '<name> <value>'.");
        }
    }

    private static int Usage()
    {
        Console.Error.WriteLine("Lifecycle: " + ProductInfo.EntrypointName + " lifecycle {start|stop|restart|status|connect} --config <service-settings.json> | --data-root <dir> [--control-pipe <name> --agent-pipe <name>] [--host <MetaBrain.Connections.exe>]");
        Console.Error.WriteLine("start resolves, creates (clean root only) or selects the instance and starts the shared locked host; a concurrent start never creates a second host.");
        Console.Error.WriteLine("The shared host is service\\MetaBrain.Connections.exe beside this entrypoint; --host names another one explicitly.");
        Console.Error.WriteLine("stop asks the shared host to drain, lock and exit; restart is a fresh locked host with no surviving session; status reports the real owner channel; connect proves reachability without starting.");
        Console.Error.WriteLine("Only stop ends the shared host; closing a frontend never does.");
        return 2;
    }
}
