using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MetaBrain.Connections;

namespace MetaBrain.OwnerUi;

/// <summary>
/// The owner console window: an explicit owner session against the running
/// service, a small navigation shell, an honest connection/locked state, and
/// the explicit unlock/lock/key-recovery acts of the owner workflow. No private
/// content is fetched, displayed or cached by this task's surfaces, and every
/// vault act is an ordinary request of this console to the running service.
/// </summary>
public partial class OwnerConsoleWindow : System.Windows.Window
{
    private static readonly Brush NeutralBanner = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80));
    private static readonly Brush LockedBanner = new SolidColorBrush(Color.FromRgb(0xB4, 0x53, 0x09));
    private static readonly Brush DeniedBanner = new SolidColorBrush(Color.FromRgb(0x9A, 0x1C, 0x1C));
    private static readonly Brush UnlockedBanner = new SolidColorBrush(Color.FromRgb(0x15, 0x6B, 0x3B));

    private const int MinimumPassphraseLength = 12;

    /// <summary>
    /// What the recovery-key element holds once the key is off the screen. The
    /// element never keeps the key itself after a clear.
    /// </summary>
    private const string ClearedRecoveryKeyText = "-- no recovery key is on screen --";

    private readonly OwnerConsoleSession _session;
    private readonly DispatcherTimer _timer;
    private long _sensitiveStateVersion;
    private bool _clearingInputs;

    internal OwnerConsoleWindow(OwnerConsoleSession session)
    {
        _session = session;
        InitializeComponent();
        Navigation.SelectedIndex = 0;
        ActivityList.ItemsSource = _session.Activity;
        _session.Changed += OnSessionChanged;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _timer.Tick += OnTimerTick;
        _timer.Start();
        Render();
    }

    private void OnSessionChanged(object? sender, EventArgs e) => Render();

    private async void OnTimerTick(object? sender, EventArgs e) => await _session.CheckAsync();

    private async void OnConnectClicked(object sender, RoutedEventArgs e) => await _session.AttachAsync();

    private async void OnCheckClicked(object sender, RoutedEventArgs e) => await _session.CheckAsync();

    private void OnDetachClicked(object sender, RoutedEventArgs e) => _session.Detach();

    private void OnSecretFieldChanged(object sender, RoutedEventArgs e)
    {
        if (!_clearingInputs)
        {
            Render();
        }
    }

    private async void OnProvisionClicked(object sender, RoutedEventArgs e) =>
        await _session.ProvisionAsync(ProvisionPassphraseInput.Password);

    private async void OnUnlockClicked(object sender, RoutedEventArgs e) =>
        await _session.UnlockAsync(UnlockPassphraseInput.Password);

    private async void OnRecoverClicked(object sender, RoutedEventArgs e) =>
        await _session.UnlockWithRecoveryCodeAsync(RecoveryInput.Password);

    private async void OnLockClicked(object sender, RoutedEventArgs e) => await _session.LockAsync();

    private void OnHideRecoveryClicked(object sender, RoutedEventArgs e) => _session.HideRecoveryCode();

    private void OnNavigationChanged(object sender, SelectionChangedEventArgs e)
    {
        OwnerSessionPanel.Visibility = Navigation.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        VaultStatusPanel.Visibility = Navigation.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        VaultKeyPanel.Visibility = Navigation.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Render()
    {
        if (_sensitiveStateVersion != _session.SensitiveStateVersion)
        {
            // The session dropped its sensitive state (lock, lost connection,
            // ended session). The input controls of this window go with it.
            _sensitiveStateVersion = _session.SensitiveStateVersion;
            ClearSensitiveInputs();
        }

        var attached = _session.Attached;
        var status = _session.Status;
        var banner = Describe(attached, status);

        StateBanner.Background = banner.Color;
        StateHeadline.Text = banner.Headline;
        StateDetail.Text = banner.Detail;

        var inFlight = _session.VaultOperationInFlight;
        ConnectButton.IsEnabled = !attached;
        CheckButton.IsEnabled = attached && !inFlight;
        DetachButton.IsEnabled = attached && !inFlight;

        ControlPipeText.Text = _session.ControlPipe;
        OwnerAccountText.Text = _session.OwnerAccount + " (" + _session.OwnerAccountSid + ")";
        SessionStateText.Text = attached ? "attached to the owner control pipe" : "no owner session";
        ProtocolText.Text = OwnerChannelClient.ProtocolVersion.ToString(CultureInfo.InvariantCulture);
        LastCheckText.Text = LastCheckText_Value();
        StatusText.Text = "pipe=" + _session.ControlPipe +
            " | protocol=" + OwnerChannelClient.ProtocolVersion.ToString(CultureInfo.InvariantCulture) +
            " | account=" + _session.OwnerAccount +
            " | session=" + (attached ? "attached" : "none") +
            " | " + Summary(attached, status) +
            " | last check: " + LastCheckText_Value();

        RenderVaultStatus(attached, status);
        RenderVaultKey(attached);
    }

    private string LastCheckText_Value() => _session.LastCheckLocal is { } last
        ? last.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " local"
        : "never";

    private static string Summary(bool attached, OwnerChannelStatus? status)
    {
        if (!attached || status is null)
        {
            return "no service state reported";
        }

        return status.Availability == OwnerChannelAvailability.Reachable
            ? "vault=" + OwnerConsoleSession.VaultToken(status.VaultState)
            : "owner channel " + status.Availability + " (code: " + (status.ErrorCode ?? "unknown") + ")";
    }

    private static (string Headline, string Detail, Brush Color) Describe(bool attached, OwnerChannelStatus? status)
    {
        if (!attached || status is null)
        {
            return ("No owner session",
                "Choose “Connect to service” to open the owner control channel. Until then this console holds no session and reports no service or vault state.",
                NeutralBanner);
        }

        switch (status.Availability)
        {
            case OwnerChannelAvailability.ServiceUnavailable:
                return ("Service unavailable",
                    "The owner control pipe did not answer (code: " + (status.ErrorCode ?? "unknown") +
                    "). Start the service for this configuration and choose “Check again”. Nothing private is cached, assumed or shown while the service is unreachable.",
                    DeniedBanner);
            case OwnerChannelAvailability.OwnerAuthorityDenied:
                return ("Owner authority denied by this endpoint",
                    "The endpoint answered but refused owner authority for this request (code: " + (status.ErrorCode ?? "unknown") +
                    "). Check that the pipe name is the owner control pipe from the service settings: an agent channel never grants owner operations, and a body, Origin or localhost claim cannot create one.",
                    DeniedBanner);
            case OwnerChannelAvailability.ProtocolMismatch:
                return ("Service protocol mismatch",
                    "The endpoint speaks a different protocol revision (code: unsupported_protocol). Update the owner application or the service so both speak protocol " +
                    OwnerChannelClient.ProtocolVersion.ToString(CultureInfo.InvariantCulture) + ".",
                    DeniedBanner);
        }

        return status.VaultState switch
        {
            OwnerVaultState.Unlocked => ("Owner session active — vault unlocked by the owner",
                "The service reports the vault unlocked. Unlock and lock stay explicit owner acts: this host only reports the state it was told.",
                UnlockedBanner),
            OwnerVaultState.Locked => ("Owner session active — vault locked",
                "The service is reachable and the vault is locked. Private content stays unavailable: this console fetches and caches no private data, and only an explicit owner unlock act (Vault & key) changes the vault state.",
                LockedBanner),
            OwnerVaultState.Locking => ("Owner session active — vault locking",
                "The service is draining in-flight private operations before it clears the key.",
                LockedBanner),
            OwnerVaultState.Uninitialized => ("Owner session active — vault not provisioned",
                "The service is reachable and no vault exists yet. Provisioning the owner key is an explicit owner act of the owner workflow (Vault & key).",
                LockedBanner),
            _ => ("Owner session active — vault state unknown",
                "The service answered but reported a vault state this client does not recognize (code: " + (status.ErrorCode ?? "unknown") +
                "). An unknown state is never presented as unlocked.",
                LockedBanner)
        };
    }

    private void RenderVaultStatus(bool attached, OwnerChannelStatus? status)
    {
        var lastCheck = LastCheckText_Value();
        if (!attached || status is null)
        {
            VaultStateText.Text = "Not reported";
            VaultStateDetail.Text = "This console holds no owner session, so the service has reported no vault state and nothing is assumed about the vault.";
            VaultRawText.Text = "last check: " + lastCheck;
            return;
        }

        if (status.Availability != OwnerChannelAvailability.Reachable)
        {
            VaultStateText.Text = "Unknown — no owner-channel answer";
            VaultStateDetail.Text = "The owner channel returned " + status.Availability + " (code: " + (status.ErrorCode ?? "unknown") +
                "), so the vault state is unknown and stays unavailable. Unknown is never treated as unlocked.";
            VaultRawText.Text = "last check: " + lastCheck + " | availability=" + status.Availability + " (code: " + (status.ErrorCode ?? "none") + ")";
            return;
        }

        VaultStateText.Text = status.VaultState.ToString();
        VaultStateDetail.Text = status.VaultState switch
        {
            OwnerVaultState.Unlocked => "The owner unlocked the vault in the owner workflow. Private workbench views arrive with their own tasks and read private data only through the service.",
            OwnerVaultState.Locked => "The vault is locked. Private views stay unavailable and this console shows nothing private; unlock is an explicit owner act and never automatic.",
            OwnerVaultState.Locking => "The service is locking the vault: it drains in-flight private operations before clearing the key.",
            OwnerVaultState.Uninitialized => "No vault exists yet, so there is nothing to unlock. Provisioning the owner key is an explicit owner act of the owner workflow (Vault & key).",
            _ => "The service reported a vault state this client does not recognize, so private views are not treated as available."
        };
        VaultRawText.Text = "last check: " + lastCheck + " | service reported vaultState=" + OwnerConsoleSession.VaultToken(status.VaultState);
    }

    private void RenderVaultKey(bool attached)
    {
        var state = _session.ReportedVaultState;
        var inFlight = _session.VaultOperationInFlight;
        var recoveryCode = _session.RecoveryCodeDisplay;

        VaultKeyStateText.Text = state switch
        {
            OwnerVaultState.Uninitialized => "Vault: not provisioned — there is no vault to unlock yet.",
            OwnerVaultState.Locked => "Vault: locked — create or unlock requires your explicit act below.",
            OwnerVaultState.Unlocked => "Vault: unlocked by the owner — lock it when you stop working.",
            OwnerVaultState.Locking => "Vault: locking — the service is draining in-flight private operations.",
            _ => attached
                ? "Vault: unknown — no usable owner-channel answer, so nothing is assumed about the vault."
                : "Vault: not reported — this console holds no owner session."
        };

        VaultActStateText.Text = OfferedActsText(attached, state, inFlight);

        ProvisionPassphraseInput.IsEnabled = attached && !inFlight;
        ProvisionConfirmInput.IsEnabled = attached && !inFlight;
        UnlockPassphraseInput.IsEnabled = attached && !inFlight;
        RecoveryInput.IsEnabled = attached && !inFlight;

        var filled = CountFilledCredentialFields();
        SensitiveStateText.Text = filled == 0
            ? "Credential fields: all 4 empty — no credential of yours is waiting in this window."
            : "Credential fields: " + filled.ToString(CultureInfo.InvariantCulture) + " of 4 filled — held only until you send it, and cleared on lock, disconnect, end of session or a refused credential.";

        ProvisionPanel.Visibility = state == OwnerVaultState.Uninitialized ? Visibility.Visible : Visibility.Collapsed;
        UnlockPanel.Visibility = state == OwnerVaultState.Locked ? Visibility.Visible : Visibility.Collapsed;
        LockPanel.Visibility = state == OwnerVaultState.Unlocked ? Visibility.Visible : Visibility.Collapsed;

        RecoveryPanel.Visibility = Visibility.Visible;
        // The recovery key element is always present and its text is replaced -
        // never merely hidden - when the key leaves the screen, so the console
        // holds no displayed copy once it is cleared.
        RecoveryKeyText.Text = recoveryCode ?? ClearedRecoveryKeyText;
        RecoveryGuidanceText.Text = recoveryCode is null
            ? "No recovery key is on screen. The service returns it once, right after provisioning, and this console stored none: if you did not save it then, it cannot be shown again."
            : "Write this down or copy it into your own password manager now, outside this machine. It is the only way back into this vault if the passphrase is lost, and this console stores it nowhere.";

        var passphrase = ProvisionPassphraseInput.Password;
        var confirmation = ProvisionConfirmInput.Password;
        var provisioning = state == OwnerVaultState.Uninitialized && !inFlight;
        var provisionReady = provisioning && IsProvisionInputReady(passphrase, confirmation);
        ProvisionButton.IsEnabled = provisionReady;
        ProvisionHintText.Text = !provisioning
            ? "Provisioning is unavailable while the service reports another vault state."
            : passphrase.Length == 0
                ? "Enter a passphrase of at least " + MinimumPassphraseLength.ToString(CultureInfo.InvariantCulture) + " characters."
                : passphrase.Length < MinimumPassphraseLength
                    ? "Too short: the service requires at least " + MinimumPassphraseLength.ToString(CultureInfo.InvariantCulture) + " characters."
                    : !string.Equals(passphrase, confirmation, StringComparison.Ordinal)
                        ? "The two passphrases differ."
                        : "Ready: the service will create the vault and show the recovery key once.";

        var locked = state == OwnerVaultState.Locked && !inFlight;
        UnlockButton.IsEnabled = locked && UnlockPassphraseInput.Password.Length > 0;
        RecoverButton.IsEnabled = locked && RecoveryInput.Password.Length > 0;
        LockButton.IsEnabled = state == OwnerVaultState.Unlocked && !inFlight;
        HideRecoveryButton.IsEnabled = recoveryCode is not null;

        VaultMessageText.Text = _session.VaultMessage ??
            "No owner vault act has been sent in this session. Nothing here acts on the vault until you choose one.";
    }

    /// <summary>
    /// What the owner can do against the state the service reported. Stated
    /// plainly so a locked, provisioned or unreachable vault never looks like a
    /// screen full of unusable controls.
    /// </summary>
    private static string OfferedActsText(bool attached, OwnerVaultState? state, bool inFlight)
    {
        var offered = !attached
            ? "No act is offered: this console holds no owner session."
            : state switch
            {
                OwnerVaultState.Uninitialized => "Offered acts for the reported vault state: provisioning.",
                OwnerVaultState.Locked => "Offered acts for the reported vault state: unlock with the owner passphrase, or unlock with the owner recovery key.",
                OwnerVaultState.Unlocked => "Offered acts for the reported vault state: lock the vault.",
                _ => "No act is offered: the service has reported no unlocked, locked or uninitialized vault state."
            };

        return inFlight
            ? offered + " An owner vault act is in progress, so these buttons are held until the service answers."
            : offered;
    }

    private static bool IsProvisionInputReady(string passphrase, string confirmation) =>
        passphrase.Length >= MinimumPassphraseLength && string.Equals(passphrase, confirmation, StringComparison.Ordinal);

    /// <summary>
    /// How many credential fields currently hold input. Character counts and
    /// values are never exposed: the owner only needs to know whether something
    /// of theirs is still waiting on this screen.
    /// </summary>
    private int CountFilledCredentialFields() =>
        (ProvisionPassphraseInput.Password.Length > 0 ? 1 : 0) +
        (ProvisionConfirmInput.Password.Length > 0 ? 1 : 0) +
        (UnlockPassphraseInput.Password.Length > 0 ? 1 : 0) +
        (RecoveryInput.Password.Length > 0 ? 1 : 0);

    private void ClearSensitiveInputs()
    {
        _clearingInputs = true;
        try
        {
            ProvisionPassphraseInput.Clear();
            ProvisionConfirmInput.Clear();
            UnlockPassphraseInput.Clear();
            RecoveryInput.Clear();
        }
        finally
        {
            _clearingInputs = false;
        }
    }
}
