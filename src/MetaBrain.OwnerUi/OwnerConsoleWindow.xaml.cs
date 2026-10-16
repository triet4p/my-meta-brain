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

    /// <summary>Filter item that means "the service applies no such filter".</summary>
    private const string AnyFilterItem = "(any)";

    private const int DefaultLibraryPageSize = 10;

    /// <summary>
    /// What the recovery-key element holds once the key is off the screen. The
    /// element never keeps the key itself after a clear.
    /// </summary>
    private const string ClearedRecoveryKeyText = "-- no recovery key is on screen --";

    private readonly OwnerConsoleSession _session;
    private readonly DispatcherTimer _timer;
    private long _sensitiveStateVersion;
    private long _libraryFilterVersion;
    private bool _clearingInputs;
    private OwnerLibraryPage? _renderedPage;
    private OwnerItemDetail? _renderedItem;

    internal OwnerConsoleWindow(OwnerConsoleSession session)
    {
        _session = session;
        InitializeComponent();
        Navigation.SelectedIndex = 0;
        ActivityList.ItemsSource = _session.Activity;
        InitializeLibraryControls();
        LibraryTitleInput.TextChanged += (_, _) => Render();
        LibraryResults.MouseDoubleClick += async (_, _) => await OpenSelectedLibraryResultAsync();
        _session.Changed += OnSessionChanged;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _timer.Tick += OnTimerTick;
        _timer.Start();
        Render();
    }

    /// <summary>
    /// The two fixed enumerations this view filters by are the service's own
    /// spellings, and the "(any)" item means the filter is not sent at all. The
    /// free-text boxes carry IDs the owner already knows (a zone or a collection
    /// from the owner collection commands): an unknown ID is answered by the
    /// service as a refusal, never silently ignored here.
    /// </summary>
    private void InitializeLibraryControls()
    {
        foreach (var kind in new[]
                 {
                     AnyFilterItem, "claim", "observation", "question", "hypothesis",
                     "decision", "lesson", "procedure", "synthesis", "preference", "intention"
                 })
        {
            LibraryKindInput.Items.Add(kind);
        }

        foreach (var level in new[] { AnyFilterItem, "l0", "l1", "l2", "l3Plus" })
        {
            LibraryLevelInput.Items.Add(level);
        }

        foreach (var current in new[] { "all", "current", "history" })
        {
            LibraryCurrentInput.Items.Add(current);
        }

        foreach (var size in new[] { 10, 25, 50 })
        {
            LibraryPageSizeInput.Items.Add(size);
        }

        LibraryPageSizeInput.SelectedItem = DefaultLibraryPageSize;
        SyncLibraryFilterInputs();
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
        LibraryPanel.Visibility = Navigation.SelectedIndex == 3 ? Visibility.Visible : Visibility.Collapsed;
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

        if (_libraryFilterVersion != _session.LibraryFilterVersion)
        {
            // The session dropped the filters with the private state (lock, end
            // of session): the input controls of this window go with them.
            _libraryFilterVersion = _session.LibraryFilterVersion;
            SyncLibraryFilterInputs();
        }

        RenderVaultStatus(attached, status);
        RenderVaultKey(attached);
        RenderLibrary(attached);
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

    /// <summary>
    /// Keyboard workflow of the whole console: Ctrl+F reaches the library search
    /// box, Ctrl+L asks the service to lock the vault (the same owner act as the
    /// button) and F5 re-runs the library search. Nothing here acts on the vault
    /// on its own; every shortcut is an owner key press that reaches the same
    /// service operation as the corresponding control.
    /// </summary>
    private void OnWindowPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        var modifiers = System.Windows.Input.Keyboard.Modifiers;
        if (e.Key == System.Windows.Input.Key.F && modifiers.HasFlag(System.Windows.Input.ModifierKeys.Control))
        {
            Navigation.SelectedIndex = 3;
            LibraryQueryInput.Focus();
            LibraryQueryInput.SelectAll();
            e.Handled = true;
            return;
        }

        if (e.Key == System.Windows.Input.Key.L && modifiers.HasFlag(System.Windows.Input.ModifierKeys.Control))
        {
            if (_session.ReportedVaultState == OwnerVaultState.Unlocked && !_session.VaultOperationInFlight)
            {
                Navigation.SelectedIndex = 2;
                _ = _session.LockAsync();
            }

            e.Handled = true;
            return;
        }

        if (e.Key == System.Windows.Input.Key.F5 && Navigation.SelectedIndex == 3)
        {
            e.Handled = true;
            _ = SearchLibraryFromInputsAsync(0);
        }
    }

    private async void OnLibrarySearchClicked(object sender, RoutedEventArgs e) =>
        await SearchLibraryFromInputsAsync(0);

    private async void OnLibraryQueryKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter)
        {
            return;
        }

        e.Handled = true;
        await SearchLibraryFromInputsAsync(0);
    }

    private async void OnLibraryPreviousClicked(object sender, RoutedEventArgs e)
    {
        if (_session.LibraryPage is not { Offset: > 0 } page)
        {
            return;
        }

        await SearchLibraryFromInputsAsync(Math.Max(0, page.Offset - page.Limit));
    }

    private async void OnLibraryNextClicked(object sender, RoutedEventArgs e)
    {
        if (_session.LibraryPage is not { HasMore: true } page)
        {
            return;
        }

        await SearchLibraryFromInputsAsync(page.Offset + page.Limit);
    }

    private void OnLibraryPageSizeChanged(object sender, SelectionChangedEventArgs e) => Render();

    private async void OnLibraryRebuildClicked(object sender, RoutedEventArgs e) =>
        await _session.RebuildLibraryIndexAsync();

    private void OnLibraryResetClicked(object sender, RoutedEventArgs e)
    {
        _session.SetLibraryQuery(new OwnerLibraryQuery { Limit = CurrentPageSize() });
        SyncLibraryFilterInputs();
        Render();
    }

    private void OnLibraryResultChanged(object sender, SelectionChangedEventArgs e) => Render();

    private async void OnLibraryResultsKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter)
        {
            return;
        }

        e.Handled = true;
        await OpenSelectedLibraryResultAsync();
    }

    private async Task OpenSelectedLibraryResultAsync()
    {
        if (LibraryResults.SelectedItem is LibraryResultRow row)
        {
            await _session.OpenLibraryItemAsync(row.Hit);
        }
    }

    private async void OnLibraryCitationKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter)
        {
            return;
        }

        e.Handled = true;
        await OpenSelectedCitationAsync();
    }

    private async void OnLibraryOpenSourceClicked(object sender, RoutedEventArgs e) =>
        await OpenSelectedCitationAsync();

    private async Task OpenSelectedCitationAsync()
    {
        if (SelectedCitation() is { SourceId: { } sourceId, SourceRevision: { } revision })
        {
            await _session.OpenLibrarySourceAsync(sourceId, revision);
        }
    }

    private async void OnLibraryRenameClicked(object sender, RoutedEventArgs e) =>
        await _session.RenameLibraryItemAsync(LibraryTitleInput.Text);

    private async void OnLibraryTitleKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter)
        {
            return;
        }

        e.Handled = true;
        await _session.RenameLibraryItemAsync(LibraryTitleInput.Text);
    }

    private OwnerItemCitation? SelectedCitation() =>
        LibraryCitations.SelectedItem is LibraryCitationRow row ? row.Citation : null;

    private int CurrentPageSize() =>
        LibraryPageSizeInput.SelectedItem is int size ? size : DefaultLibraryPageSize;

    private async Task SearchLibraryFromInputsAsync(int offset)
    {
        if (!TryReadLibraryFilters(offset, out var query))
        {
            return;
        }

        // The filters the owner set become the query of this session, so the
        // page that comes back is described by the same values it was asked with.
        _session.SetLibraryQuery(query);
        await _session.SearchLibraryAsync();
    }

    /// <summary>
    /// Reads the owner's filters. A date this console does not understand is
    /// refused with a plain explanation instead of being dropped silently into a
    /// different time window.
    /// </summary>
    private bool TryReadLibraryFilters(int offset, out OwnerLibraryQuery query)
    {
        query = new OwnerLibraryQuery();
        if (!TryParseLibraryTimestamp(LibraryFromInput.Text, out var fromUtc) ||
            !TryParseLibraryTimestamp(LibraryToInput.Text, out var toUtc))
        {
            _session.ReportLibraryInputProblem(
                "This console did not send the search: a From/To value is not a date or date-time it understands. Use yyyy-MM-dd or yyyy-MM-ddTHH:mm in UTC.");
            return false;
        }

        query = new OwnerLibraryQuery(
            Text: string.IsNullOrWhiteSpace(LibraryQueryInput.Text) ? null : LibraryQueryInput.Text.Trim(),
            ZoneId: FilterText(LibraryZoneInput.Text),
            CollectionId: FilterText(LibraryCollectionInput.Text),
            Kind: SelectedFilter(LibraryKindInput),
            Level: SelectedFilter(LibraryLevelInput),
            Current: (LibraryCurrentInput.SelectedItem as string) switch
            {
                "current" => OwnerLibraryCurrentness.Current,
                "history" => OwnerLibraryCurrentness.History,
                _ => OwnerLibraryCurrentness.All
            },
            FromUtc: fromUtc,
            ToUtc: toUtc,
            Offset: offset,
            Limit: CurrentPageSize());
        return true;
    }

    private static bool TryParseLibraryTimestamp(string text, out DateTimeOffset? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        if (DateTimeOffset.TryParse(text.Trim(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            value = parsed;
            return true;
        }

        return false;
    }

    private static string? FilterText(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static string? SelectedFilter(System.Windows.Controls.ComboBox box) =>
        box.SelectedItem is string value && !string.Equals(value, AnyFilterItem, StringComparison.Ordinal) ? value : null;

    /// <summary>
    /// Puts the session's own filter state back into the input controls. Used at
    /// startup and whenever the session drops the filters with the private state,
    /// so the window never shows a filter that is no longer in force.
    /// </summary>
    private void SyncLibraryFilterInputs()
    {
        var query = _session.LibraryQuery;
        LibraryQueryInput.Text = query.Text ?? string.Empty;
        LibraryZoneInput.Text = query.ZoneId ?? string.Empty;
        LibraryCollectionInput.Text = query.CollectionId ?? string.Empty;
        LibraryKindInput.SelectedItem = query.Kind ?? AnyFilterItem;
        LibraryLevelInput.SelectedItem = query.Level ?? AnyFilterItem;
        LibraryCurrentInput.SelectedItem = query.Current switch
        {
            OwnerLibraryCurrentness.Current => "current",
            OwnerLibraryCurrentness.History => "history",
            _ => "all"
        };
        LibraryFromInput.Text = string.Empty;
        LibraryToInput.Text = string.Empty;
    }

    /// <summary>
    /// Renders the library view from the service's answers only. When the vault
    /// is not unlocked this shows no private content at all, and the page and
    /// detail collections are dropped so a stale selection cannot be reopened.
    /// </summary>
    private void RenderLibrary(bool attached)
    {
        var state = _session.ReportedVaultState;
        var inFlight = _session.LibraryActInFlight;
        var unlocked = state == OwnerVaultState.Unlocked;

        LibraryStateText.Text = state switch
        {
            OwnerVaultState.Unlocked =>
                "Vault: unlocked — the service can search your library. A materialized index generation must exist before it answers a search.",
            OwnerVaultState.Locked =>
                "Vault: locked — no private library content is available or shown. Unlock in Vault & key, then rebuild the index.",
            OwnerVaultState.Locking =>
                "Vault: locking — the service is draining private work; the private views of this screen are cleared.",
            OwnerVaultState.Uninitialized =>
                "Vault: not provisioned — there is no library to search yet.",
            _ => attached
                ? "Vault: unknown — no usable owner-channel answer, so no private view is shown."
                : "Vault: not reported — this console holds no owner session."
        };

        LibraryIndexText.Text = _session.LibraryIndex is { } index
            ? "Index generation " + ShortDigest(index.CanonicalDigest) + ": " + index.RowCount.ToString(CultureInfo.InvariantCulture) +
                " rows, schema " + index.SchemaVersion.ToString(CultureInfo.InvariantCulture) +
                (index.Stale ? " — already behind the vault" : string.Empty)
            : "No index generation has been materialized in this session: the service answers a search only after Rebuild library index.";

        var page = _session.LibraryPage;
        if (!ReferenceEquals(_renderedPage, page))
        {
            _renderedPage = page;
            LibraryResults.ItemsSource = page is null
                ? null
                : page.Hits.Select(hit => new LibraryResultRow(hit)).ToArray();
        }

        LibraryPageText.Text = page is null
            ? (unlocked
                ? "No library page is shown. Search to read the current index generation."
                : "No private library page is shown.")
            : "Page: total=" + page.Total.ToString(CultureInfo.InvariantCulture) +
                " offset=" + page.Offset.ToString(CultureInfo.InvariantCulture) +
                " limit=" + page.Limit.ToString(CultureInfo.InvariantCulture) +
                " hasMore=" + (page.HasMore ? "yes" : "no") +
                " | filters: text=" + (string.IsNullOrEmpty(page.Query) ? "(none: browse)" : "\"" + page.Query + "\"") +
                " zone=" + (page.ZoneId ?? "(any)") + " collection=" + (page.CollectionId ?? "(any)") +
                " kind=" + (page.Kind ?? "(any)") + " level=" + (page.Level ?? "(any)") + " show=" + page.Current +
                " from=" + Stamp(page.FromUtc) + " to=" + Stamp(page.ToUtc) +
                " | this page was answered from an index generation that still matched the vault";

        var detail = _session.LibraryItem;
        if (!ReferenceEquals(_renderedItem, detail))
        {
            _renderedItem = detail;
            LibraryCitations.ItemsSource = detail is null
                ? null
                : detail.Citations.Select(citation => new LibraryCitationRow(citation)).ToArray();
            LibraryTitleInput.Text = detail?.Title ?? string.Empty;
        }

        LibraryDetailSummaryText.Text = detail is null
            ? (unlocked
                ? "No item is open. Choose a result and press Enter (or double-click it) to read its exact revision."
                : "No private item detail is shown.")
            : "id=" + detail.Id + "  revision=" + detail.Revision.ToString(CultureInfo.InvariantCulture) +
                "  zone=" + detail.ZoneId + "  kind=" + detail.Kind + "  level=" + detail.Level +
                "  evidence=" + detail.Evidence + "  author=" + detail.Author + "  executor=" + detail.Executor +
                "  creation=" + detail.CreationMethod + "  stored citations=" + detail.Citations.Length.ToString(CultureInfo.InvariantCulture);
        LibraryDetailTitleText.Text = detail is null
            ? string.Empty
            : "Title as served: " + (detail.Title is { Length: > 0 } servedTitle ? servedTitle : "(none stored)");
        LibraryDetailBodyText.Text = detail?.Body ?? string.Empty;
        LibraryDetailApplicabilityText.Text = detail is null
            ? string.Empty
            : "occurred=" + Stamp(detail.OccurredAtUtc) + "  recorded=" + Stamp(detail.RecordedAtUtc) +
                "  applicability: project=" + (detail.Project ?? "(none)") +
                " environment=" + (detail.Environment ?? "(none)") +
                " version=" + (detail.Version ?? "(none)");
        LibraryDetailWarningsText.Text = detail is { Warnings.Length: > 0 } warningItem
            ? "warnings from the service: " + string.Join(", ", warningItem.Warnings)
            : string.Empty;

        var source = _session.LibrarySource;
        LibrarySourceText.Text = source is null
            ? (unlocked ? "No source is open. Choose a citation and press Enter to open that registry revision." : "No private source detail is shown.")
            : "id=" + source.Id + "  revision=" + source.Revision.ToString(CultureInfo.InvariantCulture) +
                "  zone=" + source.ZoneId + "  availability=" + source.Availability +
                "  title=" + (source.Title ?? "(none)") + "  version=" + (source.Version ?? "(none)") +
                "  captured=" + Stamp(source.CapturedAtUtc) + "  author=" + source.Author +
                "  executor=" + source.Executor + "  creation=" + source.CreationMethod +
                "  locator=" + (source.Locator ?? "(none)");

        var citation = SelectedCitation();
        var canOpenCitation = unlocked && !inFlight &&
            citation is { SourceId: { Length: > 0 }, SourceRevision: >= 1 };

        LibrarySearchButton.IsEnabled = unlocked && !inFlight;
        LibraryRebuildButton.IsEnabled = unlocked && !inFlight;
        LibraryResetButton.IsEnabled = !inFlight;
        LibraryPreviousButton.IsEnabled = unlocked && !inFlight && page is { Offset: > 0 };
        LibraryNextButton.IsEnabled = unlocked && !inFlight && page is { HasMore: true };
        LibraryOpenSourceButton.IsEnabled = canOpenCitation;
        LibraryRenameButton.IsEnabled = unlocked && !inFlight && detail is not null && !string.IsNullOrWhiteSpace(LibraryTitleInput.Text);
        LibraryQueryInput.IsEnabled = unlocked && !inFlight;
        LibraryZoneInput.IsEnabled = unlocked && !inFlight;
        LibraryCollectionInput.IsEnabled = unlocked && !inFlight;
        LibraryKindInput.IsEnabled = unlocked && !inFlight;
        LibraryLevelInput.IsEnabled = unlocked && !inFlight;
        LibraryCurrentInput.IsEnabled = unlocked && !inFlight;
        LibraryFromInput.IsEnabled = unlocked && !inFlight;
        LibraryToInput.IsEnabled = unlocked && !inFlight;
        LibraryPageSizeInput.IsEnabled = unlocked && !inFlight;
        LibraryResults.IsEnabled = unlocked && !inFlight;
        LibraryCitations.IsEnabled = unlocked && !inFlight;
        LibraryTitleInput.IsEnabled = unlocked && !inFlight;

        LibraryMessageText.Text = _session.LibraryMessage ?? (unlocked
            ? "No library act has been sent in this session. Nothing private is read until you search."
            : "No private library act is offered while the service does not report the vault unlocked.");
    }

    private static string Stamp(DateTimeOffset? value) =>
        value is { } instant
            ? instant.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture)
            : "(none)";

    private static string ShortDigest(string digest) => digest.Length <= 12 ? digest : digest[..12];

    /// <summary>One search hit exactly as the service listed it, rendered as one line.</summary>
    private sealed record LibraryResultRow(OwnerLibraryHit Hit)
    {
        public override string ToString()
        {
            var title = string.IsNullOrEmpty(Hit.Title) ? "(no title)" : Hit.Title;
            return title + "  |  kind=" + Hit.Kind + " level=" + Hit.Level + " zone=" + Hit.ZoneId +
                " rev=" + Hit.Revision.ToString(CultureInfo.InvariantCulture) +
                "  |  occurred=" + ShortStamp(Hit.OccurredAtUtc) + " recorded=" + ShortStamp(Hit.RecordedAtUtc) +
                "  |  publication=" + Hit.Publication +
                " score=" + Hit.Score.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static string ShortStamp(DateTimeOffset? value) =>
            value is { } instant ? instant.ToUniversalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "(none)";
    }

    /// <summary>
    /// One stored citation as the service projected it: the anchor it claims,
    /// the current/superseded/unknown answer and any warning.
    /// </summary>
    private sealed record LibraryCitationRow(OwnerItemCitation Citation)
    {
        public override string ToString()
        {
            var span = Citation.SpanStart is { } start && Citation.SpanEnd is { } end
                ? start.ToString(CultureInfo.InvariantCulture) + "-" + end.ToString(CultureInfo.InvariantCulture)
                : "no span";
            return (Citation.SourceId ?? "(no source id)") +
                "  rev=" + (Citation.SourceRevision?.ToString(CultureInfo.InvariantCulture) ?? "(none)") +
                "  span=" + span + "  status=" + Citation.Status + "  availability=" + Citation.Availability +
                (Citation.Warning is null ? string.Empty : "  warning=" + Citation.Warning);
        }
    }
}
