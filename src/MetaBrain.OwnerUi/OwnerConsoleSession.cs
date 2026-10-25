using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Principal;
using MetaBrain.Connections;

namespace MetaBrain.OwnerUi;

/// <summary>The explicit owner vault act this console can ask the service to perform.</summary>
internal enum OwnerVaultAct
{
    Provision,
    Unlock,
    UnlockWithRecoveryCode,
    Lock
}

/// <summary>
/// The owner application's own session with the running service.
/// </summary>
/// <remarks>
/// The session holds the pipe name this application may use, whether the owner
/// attached it, the last owner-channel status the service reported, the outcome
/// of the last explicit owner vault act, the private library views of the
/// currently unlocked epoch, and a short activity log of this process only. It
/// holds no vault key, no policy of its own, no on-disk cache and no copy of
/// any private content beyond the answers it is currently showing: every value
/// it shows is an answer of the running service on the owner control pipe, and
/// an unknown or failed answer is never presented as a known vault state. The
/// private library views and the owner's own query text are dropped whenever
/// the vault leaves the unlocked state they belong to, and an answer still in
/// flight for that state is dropped with them.
/// The owner credential travels from the owner's own input control to the
/// service in that one request and is not kept here; the recovery material the
/// service returns once for a fresh provisioning is held only while the owner
/// can still read it, and is dropped on lock, disconnect, end of session and on
/// every transition away from an unlocked vault.
/// </remarks>
internal sealed partial class OwnerConsoleSession
{
    private const int MaximumActivityEntries = 200;
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan VaultActTimeout = TimeSpan.FromSeconds(30);

    private bool _probing;

    public OwnerConsoleSession(string controlPipe)
    {
        ControlPipe = controlPipe;
        using var identity = WindowsIdentity.GetCurrent();
        OwnerAccount = identity.Name;
        OwnerAccountSid = identity.User?.Value ?? "(unavailable)";
    }

    /// <summary>Owner control pipe this session may use; never the agent pipe.</summary>
    public string ControlPipe { get; private set; }

    /// <summary>Windows account this application runs as; transport identity only.</summary>
    public string OwnerAccount { get; }

    public string OwnerAccountSid { get; }

    /// <summary>True once the owner explicitly attached this session.</summary>
    public bool Attached { get; private set; }

    /// <summary>Last owner-channel status; null while no session is attached.</summary>
    public OwnerChannelStatus? Status { get; private set; }

    public DateTimeOffset? LastCheckLocal { get; private set; }

    /// <summary>Vault state the service reported, or null when no reachable state exists.</summary>
    public OwnerVaultState? ReportedVaultState =>
        Attached && Status is { Availability: OwnerChannelAvailability.Reachable } ? Status.VaultState : null;

    /// <summary>
    /// Recovery material the service returned once for a fresh provisioning and
    /// that the owner has not dismissed yet. Never persisted, never logged, and
    /// dropped as soon as the vault leaves the unlocked state this answer
    /// belongs to.
    /// </summary>
    public string? RecoveryCodeDisplay { get; private set; }

    /// <summary>Outcome text of the last explicit owner vault act in this session.</summary>
    public string? VaultMessage { get; private set; }

    /// <summary>True while one owner vault act is in flight; further acts wait for its answer.</summary>
    public bool VaultOperationInFlight { get; private set; }

    /// <summary>
    /// Increments whenever this session drops the owner's sensitive console
    /// state (credential fields and the once-shown recovery material). The host
    /// clears its own input controls when it observes a new value.
    /// </summary>
    public long SensitiveStateVersion { get; private set; }

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

    /// <summary>
    /// Points this console at the control pipe of the owner's selected
    /// instance. The previous owner session ends: the reported state and every
    /// private view of the old pipe go with it, so nothing of another instance
    /// stays on screen. Called by the owner's explicit Start/Select act only.
    /// </summary>
    public void Repoint(string controlPipe)
    {
        if (string.IsNullOrWhiteSpace(controlPipe))
        {
            return;
        }

        if (Attached && string.Equals(ControlPipe, controlPipe, StringComparison.Ordinal))
        {
            return;
        }

        Detach();
        ControlPipe = controlPipe;
        Append("selected instance pipe=" + controlPipe);
        RaiseChanged();
    }

    /// <summary>
    /// Ends the session on the owner's request, drops the reported state and
    /// clears every sensitive console state of this session.
    /// </summary>
    public void Detach()
    {
        if (!Attached)
        {
            return;
        }

        Attached = false;
        Status = null;
        VaultMessage = null;
        ClearPrivateState();
        Append("ended owner session at the owner's request; cleared sensitive console state");
        RaiseChanged();
    }

    /// <summary>Probes again while a session is attached; a no-op otherwise.</summary>
    public Task CheckAsync() => Attached ? ProbeAsync() : Task.CompletedTask;

    /// <summary>
    /// Drops the once-shown recovery material at the owner's request. The owner
    /// is expected to keep their own copy outside this machine; nothing is
    /// copied anywhere by this console.
    /// </summary>
    public void HideRecoveryCode()
    {
        if (RecoveryCodeDisplay is null)
        {
            return;
        }

        RecoveryCodeDisplay = null;
        VaultMessage = "The recovery key is no longer on screen. Keep your own copy outside this machine: this console stored none, and without it a lost passphrase cannot be undone.";
        Append("hid the displayed recovery key; it is shown once only and is stored nowhere by this console");
        RaiseChanged();
    }

    /// <summary>
    /// Asks the service to create the vault with the owner's passphrase. The
    /// service mints the recovery material and returns it once in the answer.
    /// </summary>
    public Task ProvisionAsync(string passphrase) =>
        RunVaultActAsync(OwnerVaultAct.Provision, passphrase);

    /// <summary>Asks the service to open the vault with the owner's passphrase.</summary>
    public Task UnlockAsync(string passphrase) =>
        RunVaultActAsync(OwnerVaultAct.Unlock, passphrase);

    /// <summary>
    /// Asks the service to open the existing vault with the recovery material
    /// the owner kept outside the vault. Nothing is restored or reset.
    /// </summary>
    public Task UnlockWithRecoveryCodeAsync(string recoveryCode) =>
        RunVaultActAsync(OwnerVaultAct.UnlockWithRecoveryCode, recoveryCode);

    /// <summary>
    /// Asks the service to lock the vault. The service owns the lock lifecycle:
    /// it drains in-flight private operations, clears the key, invalidates
    /// pending tokens and live sessions and stops private work; this console
    /// then clears its own sensitive state.
    /// <para>
    /// Locking clears this console's private views, including capture text that
    /// was never saved, so the first lock request that arrives while such text
    /// is in the editor is refused with an explicit warning instead of silently
    /// discarding it. Pressing lock again with the same text discards it and
    /// locks. An external lock, or any other transition away from the unlocked
    /// vault, still clears the private state immediately — there is no state
    /// where a locked console keeps private text.
    /// </para>
    /// </summary>
    public Task LockAsync()
    {
        if (HasUnsavedCaptureText && !string.Equals(_lockWarnedText, CaptureDraft, StringComparison.Ordinal))
        {
            _lockWarnedText = CaptureDraft;
            VaultMessage = "The capture editor holds " + CaptureDraft.Length.ToString(CultureInfo.InvariantCulture) +
                " characters that were never saved. The service stores nothing of an unsaved capture, and locking clears the editor, so that text will be gone. Press Lock vault now again to discard it and lock.";
            Append("refused to lock while the capture editor held unsaved text; warned the owner instead");
            RaiseChanged();
            return Task.CompletedTask;
        }

        return RunVaultActAsync(OwnerVaultAct.Lock, credential: null);
    }

    internal static string VaultToken(OwnerVaultState state) => state switch
    {
        OwnerVaultState.Uninitialized => "uninitialized",
        OwnerVaultState.Locked => "locked",
        OwnerVaultState.Locking => "locking",
        OwnerVaultState.Unlocked => "unlocked",
        _ => "unknown"
    };

    private async Task RunVaultActAsync(OwnerVaultAct act, string? credential)
    {
        if (!Attached || VaultOperationInFlight)
        {
            return;
        }

        VaultOperationInFlight = true;
        VaultMessage = null;
        RaiseChanged();

        OwnerVaultCommandResult result;
        try
        {
            using var timeout = new CancellationTokenSource(VaultActTimeout);
            result = act switch
            {
                OwnerVaultAct.Provision => await OwnerChannelClient.ProvisionAsync(ControlPipe, credential!, timeout.Token).ConfigureAwait(true),
                OwnerVaultAct.Unlock => await OwnerChannelClient.UnlockAsync(ControlPipe, credential!, timeout.Token).ConfigureAwait(true),
                OwnerVaultAct.UnlockWithRecoveryCode => await OwnerChannelClient.UnlockWithRecoveryCodeAsync(ControlPipe, credential!, timeout.Token).ConfigureAwait(true),
                _ => await OwnerChannelClient.LockAsync(ControlPipe, timeout.Token).ConfigureAwait(true)
            };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            result = new OwnerVaultCommandResult(OwnerCommandOutcome.Unavailable, OwnerVaultState.Unknown,
                "act_failed:" + ex.GetType().Name, null);
        }
        finally
        {
            VaultOperationInFlight = false;
        }

        VaultMessage = DescribeAct(act, result);
        if (result.Outcome == OwnerCommandOutcome.Completed && act == OwnerVaultAct.Provision)
        {
            // The recovery material exists only in this answer. If the service
            // completed the provisioning but returned nothing the owner could
            // keep, this console says so instead of pretending a recovery key
            // exists.
            RecoveryCodeDisplay = string.IsNullOrEmpty(result.RecoveryCode) ? null : result.RecoveryCode;
            if (RecoveryCodeDisplay is null)
            {
                VaultMessage += " The service returned no recovery material, so there is nothing to save; treat this vault as having no recovery path.";
            }
        }

        if ((result.Outcome == OwnerCommandOutcome.Completed && act == OwnerVaultAct.Lock) ||
            result.Outcome == OwnerCommandOutcome.Refused)
        {
            // A completed lock clears every private console state, and a refused
            // credential leaves no usable unlocked epoch behind either.
            ClearPrivateState();
        }

        Append(DescribeActForLog(act, result));
        RaiseChanged();
        await ProbeAsync().ConfigureAwait(true);
    }

    private async Task ProbeAsync()
    {
        if (_probing || VaultOperationInFlight)
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
            var changed = !Equals(previous, status);
            if (changed)
            {
                Append("service state " + Describe(previous) + " -> " + Describe(status));
                // A status answer can change without the serving state moving:
                // the keyless format record and the rollback point live on the
                // status, and an owner maintenance act changes them while the
                // vault stays locked. That is not the end of a private epoch —
                // there is no unlocked private state to drop — and moving the
                // epoch here would discard the maintenance act's own answer as
                // if it were stale. A real lost service or a changed vault
                // state still clears the private views and the typed secrets.
                var servingStateMoved = previous is null ||
                    previous.Availability != status.Availability ||
                    previous.VaultState != status.VaultState;
                if (!HoldsUnlockedPrivateEpoch(status) && servingStateMoved)
                {
                    // Nothing private survives a state that is not the unlocked
                    // vault this console was reporting: the once-shown recovery
                    // material, any typed credential and every private library
                    // view go with it, and the private epoch moves so an answer
                    // still in flight cannot restore them.
                    ClearPrivateState();
                }
            }

            RaiseChanged();
        }
        finally
        {
            _probing = false;
        }
    }

    private static bool HoldsUnlockedPrivateEpoch(OwnerChannelStatus status) =>
        status.Availability == OwnerChannelAvailability.Reachable && status.VaultState == OwnerVaultState.Unlocked;

    private void ClearSensitiveState()
    {
        RecoveryCodeDisplay = null;
        SensitiveStateVersion++;
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

    private static string ActName(OwnerVaultAct act) => act switch
    {
        OwnerVaultAct.Provision => "provision owner key",
        OwnerVaultAct.Unlock => "unlock with the owner passphrase",
        OwnerVaultAct.UnlockWithRecoveryCode => "unlock with the owner recovery key",
        _ => "lock vault"
    };

    /// <summary>Owner-facing outcome of one vault act. Never contains a credential.</summary>
    private static string DescribeAct(OwnerVaultAct act, OwnerVaultCommandResult result) => result.Outcome switch
    {
        OwnerCommandOutcome.Completed => act switch
        {
            OwnerVaultAct.Provision => "The service created the vault and reports it unlocked. Save the recovery key shown below outside this machine: it is displayed once, this console stores it nowhere, and without it a lost passphrase cannot be undone.",
            OwnerVaultAct.Lock => "The service locked the vault and stopped private work. This console cleared the displayed recovery key and the credential fields.",
            _ => "The service opened the vault with the " + (act == OwnerVaultAct.UnlockWithRecoveryCode ? "recovery key" : "owner passphrase") + " you provided."
        },
        OwnerCommandOutcome.Refused => act switch
        {
            OwnerVaultAct.Provision when result.ErrorCode == "credential_required" =>
                "The service refused the passphrase: a new vault passphrase must be at least 12 characters. Nothing was created.",
            OwnerVaultAct.Provision when result.ErrorCode == "vault_already_initialized" =>
                "The service refused: a vault already exists. Nothing was overwritten.",
            OwnerVaultAct.Unlock or OwnerVaultAct.UnlockWithRecoveryCode =>
                "The service refused this credential (code: " + (result.ErrorCode ?? "unknown") + "). The vault stays locked and no private content was opened; this console cleared the credential field.",
            _ => "The service refused this request (code: " + (result.ErrorCode ?? "unknown") + "). Nothing changed."
        },
        OwnerCommandOutcome.OwnerAuthorityDenied =>
            "This endpoint refused owner authority for the request (code: " + (result.ErrorCode ?? "unknown") + "). An agent channel never performs owner vault operations.",
        OwnerCommandOutcome.ProtocolMismatch =>
            "The endpoint speaks a different protocol revision, so no vault act was attempted.",
        _ => "No owner-channel answer reached the service (code: " + (result.ErrorCode ?? "unknown") + "), so no vault act was performed."
    };

    private static string DescribeActForLog(OwnerVaultAct act, OwnerVaultCommandResult result) =>
        "owner act: " + ActName(act) + " -> " + result.Outcome.ToString().ToLowerInvariant() +
        (result.Outcome == OwnerCommandOutcome.Completed ? "" : " (" + (result.ErrorCode ?? "unknown") + ")");

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
