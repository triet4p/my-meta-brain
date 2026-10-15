using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Principal;
using MetaBrain.Connections;

namespace MetaBrain.OwnerUi;

/// <summary>
/// The owner application's own session with the running service.
/// </summary>
/// <remarks>
/// The session holds the pipe name this application may use, whether the owner
/// attached it, the last owner-channel status the service reported, and a short
/// activity log of this process only. It holds no vault key, no private
/// content, no cached private view and no policy of its own: every value it
/// shows is an answer of the running service on the owner control pipe, and an
/// unknown or failed answer is never presented as a known vault state.
/// </remarks>
internal sealed class OwnerConsoleSession
{
    private const int MaximumActivityEntries = 200;
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    private bool _probing;

    public OwnerConsoleSession(string controlPipe)
    {
        ControlPipe = controlPipe;
        using var identity = WindowsIdentity.GetCurrent();
        OwnerAccount = identity.Name;
        OwnerAccountSid = identity.User?.Value ?? "(unavailable)";
    }

    /// <summary>Owner control pipe this session may use; never the agent pipe.</summary>
    public string ControlPipe { get; }

    /// <summary>Windows account this application runs as; transport identity only.</summary>
    public string OwnerAccount { get; }

    public string OwnerAccountSid { get; }

    /// <summary>True once the owner explicitly attached this session.</summary>
    public bool Attached { get; private set; }

    /// <summary>Last owner-channel status; null while no session is attached.</summary>
    public OwnerChannelStatus? Status { get; private set; }

    public DateTimeOffset? LastCheckLocal { get; private set; }

    /// <summary>Activity of this process only (explicit owner acts and state transitions).</summary>
    public ObservableCollection<string> Activity { get; } = new();

    public event EventHandler? Changed;

    /// <summary>Opens (or re-opens) the owner session and probes the service once.</summary>
    public async Task AttachAsync()
    {
        if (!Attached)
        {
            Attached = true;
            Append("attached owner session pipe=" + ControlPipe);
        }

        await ProbeAsync().ConfigureAwait(true);
    }

    /// <summary>Ends the session on the owner's request and drops the reported state.</summary>
    public void Detach()
    {
        if (!Attached)
        {
            return;
        }

        Attached = false;
        Status = null;
        Append("ended owner session at the owner's request");
        RaiseChanged();
    }

    /// <summary>Probes again while a session is attached; a no-op otherwise.</summary>
    public Task CheckAsync() => Attached ? ProbeAsync() : Task.CompletedTask;

    internal static string VaultToken(OwnerVaultState state) => state switch
    {
        OwnerVaultState.Uninitialized => "uninitialized",
        OwnerVaultState.Locked => "locked",
        OwnerVaultState.Locking => "locking",
        OwnerVaultState.Unlocked => "unlocked",
        _ => "unknown"
    };

    private async Task ProbeAsync()
    {
        if (_probing)
        {
            return;
        }

        _probing = true;
        try
        {
            var previous = Status;
            OwnerChannelStatus status;
            try
            {
                using var timeout = new CancellationTokenSource(ProbeTimeout);
                status = await OwnerChannelClient.ReadStatusAsync(ControlPipe, timeout.Token).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A probe that does not complete must become an honest
                // unavailable state; a previously reachable state is never kept.
                status = new OwnerChannelStatus(OwnerChannelAvailability.ServiceUnavailable, OwnerVaultState.Unknown, "probe_failed:" + ex.GetType().Name);
            }

            LastCheckLocal = DateTimeOffset.Now;
            Status = status;
            if (!Equals(previous, status))
            {
                Append("service state " + Describe(previous) + " -> " + Describe(status));
            }

            RaiseChanged();
        }
        finally
        {
            _probing = false;
        }
    }

    private static string Describe(OwnerChannelStatus? status) => status is null
        ? "no-session"
        : status.Availability switch
        {
            OwnerChannelAvailability.Reachable => "reachable(vault=" + VaultToken(status.VaultState) + ")",
            OwnerChannelAvailability.OwnerAuthorityDenied => "owner-authority-denied(" + (status.ErrorCode ?? "unknown") + ")",
            OwnerChannelAvailability.ProtocolMismatch => "protocol-mismatch(" + (status.ErrorCode ?? "unknown") + ")",
            _ => "service-unavailable(" + (status.ErrorCode ?? "unknown") + ")"
        };

    private void Append(string entry)
    {
        Activity.Add(DateTimeOffset.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  " + entry);
        while (Activity.Count > MaximumActivityEntries)
        {
            Activity.RemoveAt(0);
        }
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
