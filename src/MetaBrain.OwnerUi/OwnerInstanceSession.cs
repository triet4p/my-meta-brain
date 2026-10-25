using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using MetaBrain.Connections;

namespace MetaBrain.OwnerUi;

/// <summary>
/// The instance the owner console serves: one data root (one settings file,
/// one pair of pipe endpoints) plus the same shared locked host every
/// frontend uses. No vault is provisioned, unlocked or approved here: every
/// start begins locked and the owner acts explicitly afterwards on Vault &amp; key.
/// </summary>
internal sealed partial class OwnerInstanceSession
{
    private static readonly TimeSpan LifecycleTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    private bool _instanceActInFlight;
    private bool _probing;

    public OwnerInstanceSession()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        OwnerAccount = identity.Name;
    }

    /// <summary>The data root this console serves; empty while none is selected.</summary>
    public string DataRoot { get; private set; } = string.Empty;

    /// <summary>True once the owner selected an instance (or created one on a clean root).</summary>
    public bool InstanceSelected => !string.IsNullOrWhiteSpace(DataRoot);

    /// <summary>Windows account this application runs as; transport identity only.</summary>
    public string OwnerAccount { get; }

    /// <summary>Last shared-lifecycle status of the selected instance; null while none is selected.</summary>
    public InstanceStatus? LastStatus { get; private set; }

    public DateTimeOffset? LastCheckLocal { get; private set; }

    /// <summary>Owner-facing outcome of the last Start/Stop/Restart/Check act. Never a credential.</summary>
    public string? InstanceMessage { get; private set; }

    /// <summary>True while one Start/Stop/Restart/Check act is in flight; further acts wait for its answer.</summary>
    public bool InstanceActInFlight => _instanceActInFlight;

    /// <summary>Activity of this process only (explicit owner instance acts and state transitions).</summary>
    public ObservableCollection<string> Activity { get; } = new();

    public event EventHandler? Changed;

    /// <summary>
    /// The control pipe of the selected instance: the owner console attaches
    /// its owner session to this pipe only. Empty while none is selected.
    /// </summary>
    public string ControlPipe => LastStatus?.ControlPipe ?? string.Empty;

    public string AgentPipe => LastStatus?.AgentPipe ?? string.Empty;

    /// <summary>
    /// Selects the instance that lives in a data root, or creates it on a
    /// clean root with fresh endpoints. An existing settings file is kept
    /// byte-identical: pipe endpoints an owner already configured never move
    /// and no vault material is touched. Nothing is started here.
    /// </summary>
    public async Task SelectAsync(string dataRoot)
    {
        if (_instanceActInFlight)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(dataRoot))
        {
            InstanceMessage = "This console selected no instance: choose the data-root folder first. Nothing was created and nothing was started.";
            RaiseChanged();
            return;
        }

        _instanceActInFlight = true;
        InstanceMessage = null;
        RaiseChanged();
        try
        {
            var root = Path.GetFullPath(dataRoot.Trim());
            var handle = await Task.Run(() => ResolveOrCreate(root), CancellationToken.None).ConfigureAwait(true);
            DataRoot = handle.DataRoot;
            Append("selected instance data-root=" + handle.DataRoot);
            InstanceMessage = "Selected " + handle.DataRoot + " (settings " +
                handle.SettingsPath + ", control " + handle.ControlPipe +
                ", agent " + handle.AgentPipe + "). Nothing is started yet: choose Start service below.";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            InstanceMessage = DescribeLifecycleFailure("select the instance", ex);
        }
        finally
        {
            _instanceActInFlight = false;
        }

        await ProbeAsync().ConfigureAwait(true);
        RaiseChanged();
    }

    /// <summary>Clears the selected instance. The shared host keeps running: ending this selection never stops it.</summary>
    public void ClearSelection()
    {
        if (!InstanceSelected)
        {
            return;
        }

        DataRoot = string.Empty;
        LastStatus = null;
        InstanceMessage = "Cleared the selected instance in this console. The shared host keeps running: closing or clearing this selection never stops it — use Stop service for an explicit stop.";
        Append("cleared instance selection at the owner's request; shared host untouched");
        RaiseChanged();
    }

    /// <summary>
    /// Starts the shared host for the selected instance, or attaches to the
    /// host that already runs it. Concurrent starters serialize on the
    /// instance: two launches never produce two writers.
    /// </summary>
    public async Task StartAsync() => await RunLifecycleActAsync(
        "start the service",
        "service-started",
        static (handle, host, token) => InstanceLifecycle.EnsureStartedAsync(handle, host, token)).ConfigureAwait(true);

    /// <summary>
    /// Stops the shared host for the selected instance: the host drains,
    /// locks the vault and exits. The only act that ends the shared host.
    /// </summary>
    public async Task StopAsync() => await RunLifecycleActAsync(
        "stop the service",
        "service-stopped",
        static async (handle, host, token) =>
        {
            _ = host;
            var outcome = await InstanceLifecycle.StopAsync(handle, token).ConfigureAwait(false);
            return outcome == InstanceStopOutcome.AlreadyStopped ? InstanceStartOutcome.AlreadyRunning : InstanceStartOutcome.Started;
        }).ConfigureAwait(true);

    /// <summary>
    /// Restarts the shared host for the selected instance: a fresh host with
    /// the vault locked again and no surviving agent session.
    /// </summary>
    public async Task RestartAsync() => await RunLifecycleActAsync(
        "restart the service",
        "service-restarted",
        static (handle, host, token) => InstanceLifecycle.RestartAsync(handle, host, token)).ConfigureAwait(true);

    /// <summary>
    /// Reads the real owner channel, so the report is the state the service
    /// actually holds, not the state a previous act left behind.
    /// </summary>
    public async Task CheckAsync()
    {
        if (_instanceActInFlight || !InstanceSelected)
        {
            return;
        }

        _instanceActInFlight = true;
        RaiseChanged();
        try
        {
            await ProbeAsync().ConfigureAwait(true);
            InstanceMessage = LastStatus is null
                ? "No owner-channel answer reached the service, so the instance state is unknown. Nothing was started."
                : DescribeStatus(LastStatus);
        }
        finally
        {
            _instanceActInFlight = false;
        }

        RaiseChanged();
    }

    private async Task RunLifecycleActAsync(
        string act,
        string logAct,
        Func<InstanceHandle, string, CancellationToken, Task<InstanceStartOutcome>> run)
    {
        if (_instanceActInFlight)
        {
            return;
        }

        if (!InstanceSelected)
        {
            InstanceMessage = "This console did not " + act + ": no instance is selected yet. Choose the data-root folder first.";
            RaiseChanged();
            return;
        }

        _instanceActInFlight = true;
        InstanceMessage = null;
        RaiseChanged();
        try
        {
            var handle = await Task.Run(
                () => InstanceLifecycle.ResolveFromDataRoot(DataRoot), CancellationToken.None).ConfigureAwait(true);
            var host = ResolveHostExecutable();
            using var timeout = new CancellationTokenSource(LifecycleTimeout);
            var outcome = await run(handle, host, timeout.Token).ConfigureAwait(true);
            Append("owner act: " + logAct + " -> " +
                (outcome == InstanceStartOutcome.Started ? "started" : "already running") +
                " instance=" + handle.DataRoot);
            InstanceMessage = act switch
            {
                "start the service" => outcome == InstanceStartOutcome.Started
                    ? "The service started locked for " + handle.DataRoot + ". The vault is locked: unlock it explicitly in Vault & key."
                    : "The service is already running for " + handle.DataRoot + ": this launch did not start a second host.",
                "stop the service" => outcome == InstanceStartOutcome.Started
                    ? "The service stopped for " + handle.DataRoot + ": it drained, locked the vault and exited."
                    : "The service was already stopped for " + handle.DataRoot + ": nothing was running.",
                _ => outcome == InstanceStartOutcome.Started
                    ? "The service restarted locked for " + handle.DataRoot + ": a fresh host, no surviving session."
                    : "The shared host is already running for " + handle.DataRoot + ": the restart kept the running host."
            };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            InstanceMessage = DescribeLifecycleFailure(act, ex);
        }
        finally
        {
            _instanceActInFlight = false;
        }

        await ProbeAsync().ConfigureAwait(true);
        RaiseChanged();
    }

    private static InstanceHandle ResolveOrCreate(string root)
    {
        var path = ServiceSettingsFile.ResolvePath(root);
        return File.Exists(path)
            ? InstanceLifecycle.ResolveFromDataRoot(root)
            : InstanceLifecycle.Create(root);
    }

    /// <summary>
    /// The shared host for this console in a supported product layout: the
    /// release layout (<c>service/MetaBrain.Connections.exe</c> beside
    /// <c>owner-ui/MetaBrain.OwnerUi.exe</c>, the same one the CLI and MCP
    /// entrypoints resolve) or the flat developer layout. Never a user-typed
    /// path.
    /// </summary>
    private static string ResolveHostExecutable() => ProductHost.Resolve();

    private async Task ProbeAsync()
    {
        if (_probing || !InstanceSelected)
        {
            return;
        }

        _probing = true;
        try
        {
            var previous = LastStatus;
            InstanceStatus? status = null;
            try
            {
                var handle = InstanceLifecycle.ResolveFromDataRoot(DataRoot);
                using var timeout = new CancellationTokenSource(ProbeTimeout);
                status = await InstanceLifecycle.CheckStatusAsync(handle, timeout.Token).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                InstanceMessage ??= DescribeLifecycleFailure("read the instance state", ex);
            }

            LastCheckLocal = DateTimeOffset.Now;
            LastStatus = status;
            if (!Equals(previous?.ControlPipe, status?.ControlPipe) ||
                previous?.HostProcessRunning != status?.HostProcessRunning ||
                !Equals(previous?.Channel, status?.Channel))
            {
                Append("instance state -> " + (status is null ? "unknown" : DescribeStatus(status)));
            }

            RaiseChanged();
        }
        finally
        {
            _probing = false;
        }
    }

    private static string DescribeStatus(InstanceStatus status)
    {
        var channel = status.Channel.Availability == OwnerChannelAvailability.Reachable
            ? "running"
            : "not reachable (" + (status.Channel.ErrorCode ?? "unknown") + ")";
        var vault = status.Channel.VaultState switch
        {
            OwnerVaultState.Uninitialized => "uninitialized",
            OwnerVaultState.Locked => "locked",
            OwnerVaultState.Locking => "locking",
            OwnerVaultState.Unlocked => "unlocked",
            _ => "unknown"
        };
        return "Instance " + status.DataRoot + ": service=" + channel + ", vault=" + vault +
            ", host process=" + (status.HostProcessId?.ToString(CultureInfo.InvariantCulture) ?? "none") +
            " (running=" + (status.HostProcessRunning ? "yes" : "no") + "). " +
            (status.Channel.Availability == OwnerChannelAvailability.Reachable
                ? "Closing this console never stops the shared host; use Stop service for an explicit stop."
                : "Start the service when this instance has none running; closing this console never stops a shared host.");
    }

    private static string DescribeLifecycleFailure(string act, Exception ex)
    {
        return ex is InstanceLifecycleException lifecycle
            ? "This console did not " + act + " (" + lifecycle.Code + "): " + lifecycle.Message +
                " Next step: " + lifecycle.Remediation
            : "No owner-channel answer reached the service while trying to " + act +
                " (" + ex.GetType().Name + "). Nothing was started, stopped or changed.";
    }

    private void Append(string entry)
    {
        Activity.Add(DateTimeOffset.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  " + entry);
        while (Activity.Count > 200)
        {
            Activity.RemoveAt(0);
        }
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
