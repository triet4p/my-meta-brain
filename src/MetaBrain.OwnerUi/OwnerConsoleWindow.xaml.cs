using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MetaBrain.Connections;

namespace MetaBrain.OwnerUi;

/// <summary>
/// The owner console window: an explicit owner session against the running
/// service, a small navigation shell, and an honest connection/locked state.
/// No private content is fetched, displayed or cached by this task's surfaces.
/// </summary>
public partial class OwnerConsoleWindow : System.Windows.Window
{
    private static readonly Brush NeutralBanner = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80));
    private static readonly Brush LockedBanner = new SolidColorBrush(Color.FromRgb(0xB4, 0x53, 0x09));
    private static readonly Brush DeniedBanner = new SolidColorBrush(Color.FromRgb(0x9A, 0x1C, 0x1C));
    private static readonly Brush UnlockedBanner = new SolidColorBrush(Color.FromRgb(0x15, 0x6B, 0x3B));

    private readonly OwnerConsoleSession _session;
    private readonly DispatcherTimer _timer;

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

    private void OnNavigationChanged(object sender, SelectionChangedEventArgs e)
    {
        var showVaultStatus = Navigation.SelectedIndex == 1;
        OwnerSessionPanel.Visibility = showVaultStatus ? Visibility.Collapsed : Visibility.Visible;
        VaultStatusPanel.Visibility = showVaultStatus ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Render()
    {
        var attached = _session.Attached;
        var status = _session.Status;
        var banner = Describe(attached, status);

        StateBanner.Background = banner.Color;
        StateHeadline.Text = banner.Headline;
        StateDetail.Text = banner.Detail;

        ConnectButton.IsEnabled = !attached;
        CheckButton.IsEnabled = attached;
        DetachButton.IsEnabled = attached;

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
                "The service is reachable and the vault is locked. Private content stays unavailable: this console fetches and caches no private data, and only an explicit owner unlock act (owner workflow S5-T2) changes the vault state.",
                LockedBanner),
            OwnerVaultState.Locking => ("Owner session active — vault locking",
                "The service is draining in-flight private operations before it clears the key.",
                LockedBanner),
            OwnerVaultState.Uninitialized => ("Owner session active — vault not provisioned",
                "The service is reachable and no vault exists yet. Provisioning the owner key is an explicit owner act of the owner workflow (S5-T2).",
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
            OwnerVaultState.Uninitialized => "No vault exists yet, so there is nothing to unlock. Provisioning the owner key is an explicit owner act of the owner workflow (S5-T2).",
            _ => "The service reported a vault state this client does not recognize, so private views are not treated as available."
        };
        VaultRawText.Text = "last check: " + lastCheck + " | service reported vaultState=" + OwnerConsoleSession.VaultToken(status.VaultState);
    }
}
