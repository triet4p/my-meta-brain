using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MetaBrain.Connections;

namespace MetaBrain.OwnerUi;

/// <summary>
/// The owner console window: an explicit owner session against the running
/// service, navigation across owner views, an honest connection/locked state,
/// and the explicit unlock/lock/key-recovery acts. Private views are read only
/// over the owner pipe while unlocked and cleared with the private session state;
/// every vault act remains an ordinary request to the running service.
/// </summary>
public partial class OwnerConsoleWindow : System.Windows.Window
{
    private static readonly Brush NeutralBanner = FrozenBrush(Color.FromRgb(0x6B, 0x72, 0x80));
    private static readonly Brush LockedBanner = FrozenBrush(Color.FromRgb(0xB4, 0x53, 0x09));
    private static readonly Brush DeniedBanner = FrozenBrush(Color.FromRgb(0x9A, 0x1C, 0x1C));
    private static readonly Brush UnlockedBanner = FrozenBrush(Color.FromRgb(0x15, 0x6B, 0x3B));

    private static readonly Brush OutcomeSuccessBrush = FrozenBrush(Color.FromRgb(0x15, 0x6B, 0x3B));
    private static readonly Brush OutcomeRefusedBrush = FrozenBrush(Color.FromRgb(0xB4, 0x53, 0x09));
    private static readonly Brush OutcomeErrorBrush = FrozenBrush(Color.FromRgb(0x9A, 0x1C, 0x1C));
    private static readonly Brush OutcomeInfoBrush = FrozenBrush(Color.FromRgb(0x3F, 0x46, 0x50));

    /// <summary>
    /// Renders one owner-facing outcome with a label, an icon and a visual
    /// style — never by color alone. Success carries “✓”, a refusal carries
    /// “✕ Not done”, an error carries “⚠”, and neutral information carries no
    /// icon. The classification comes from the message's own wording, never
    /// from a guess about the service: refused acts say what was not
    /// saved/minted and what to do next.
    /// </summary>
    private static void RenderOutcome(System.Windows.Controls.TextBlock target, string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            target.Text = string.Empty;
            target.Foreground = OutcomeInfoBrush;
            target.FontWeight = FontWeights.Normal;
            return;
        }

        var refused = message.StartsWith("This console did not", StringComparison.Ordinal) ||
            message.StartsWith("This endpoint refused", StringComparison.Ordinal) ||
            message.StartsWith("The service refused", StringComparison.Ordinal) ||
            message.StartsWith("No owner-channel answer", StringComparison.Ordinal) ||
            message.StartsWith("No capture from", StringComparison.Ordinal) ||
            message.StartsWith("No library act", StringComparison.Ordinal) ||
            message.StartsWith("NEEDS REVIEW", StringComparison.Ordinal) ||
            message.StartsWith("UNSAVED", StringComparison.Ordinal) ||
            message.StartsWith("A rejection needs", StringComparison.Ordinal) ||
            message.StartsWith("Undoing a decision needs", StringComparison.Ordinal) ||
            message.StartsWith("The shared core requires", StringComparison.Ordinal) ||
            message.StartsWith("Cancelling needs", StringComparison.Ordinal) ||
            message.StartsWith("Saving model settings needs", StringComparison.Ordinal);
        var error = !refused && (message.StartsWith("No vault act was performed", StringComparison.Ordinal) ||
            message.Contains("act_failed", StringComparison.Ordinal) ||
            message.Contains("malformed_reply", StringComparison.Ordinal) ||
            message.Contains("unexpected_reply", StringComparison.Ordinal) ||
            message.Contains("transport_", StringComparison.Ordinal));
        var success = !refused && !error && (message.StartsWith("The service ", StringComparison.Ordinal) ||
            message.StartsWith("Saved capture", StringComparison.Ordinal) ||
            message.StartsWith("Batch finished", StringComparison.Ordinal) ||
            message.StartsWith("Rolling spend", StringComparison.Ordinal) ||
            message.StartsWith("Configured", StringComparison.Ordinal) ||
            message.StartsWith("Enabled", StringComparison.Ordinal) ||
            message.StartsWith("Disabled", StringComparison.Ordinal));
        if (refused)
        {
            target.Text = "✕ Not done — " + message;
            target.Foreground = OutcomeRefusedBrush;
            target.FontWeight = FontWeights.SemiBold;
        }
        else if (error)
        {
            target.Text = "⚠ Error — " + message;
            target.Foreground = OutcomeErrorBrush;
            target.FontWeight = FontWeights.SemiBold;
        }
        else if (success)
        {
            target.Text = "✓ " + message;
            target.Foreground = OutcomeSuccessBrush;
            target.FontWeight = FontWeights.SemiBold;
        }
        else
        {
            target.Text = message;
            target.Foreground = OutcomeInfoBrush;
            target.FontWeight = FontWeights.Normal;
        }
    }

    private static SolidColorBrush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private const int MinimumPassphraseLength = 12;

    /// <summary>Filter item that means "the service applies no such filter".</summary>
    private const string AnyFilterItem = "(any)";

    private const int DefaultLibraryPageSize = 10;

    /// <summary>The service's own memory-kind spellings, shared by the views that name a kind.</summary>
    private static readonly string[] MemoryKindTokens =
    {
        "claim", "observation", "question", "hypothesis", "decision",
        "lesson", "procedure", "synthesis", "preference", "intention"
    };

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
    private OwnerTimelineDetail? _renderedTimeline;
    private OwnerRetainedRevisionDetail? _renderedRetained;
    private long _captureDraftVersion;
    private long _reviewDraftVersion;
    private bool _syncingInboxEditors;
    private OwnerProposalIdentity[]? _renderedProposals;
    private OwnerSourceDetail? _renderedCaptureSource;
    private long _relationFormVersion;
    private OwnerRelation[]? _renderedRelations;
    private string? _renderedRelationFilter;
    private OwnerConsoleSession.RelationEndpointPick[]? _renderedRelationCandidates;
    private OwnerConsoleSession.RelationEndpointPick? _relationSubject;
    private OwnerConsoleSession.RelationEndpointPick? _relationObject;
    private long _sharingFormVersion;
    private OwnerCatalogMapping[]? _renderedCatalog;
    private OwnerAccessRequestInfo[]? _renderedRequests;
    private OwnerAccessRequestPreviewInfo? _renderedApprovalPreview;
    private OwnerAgentSessionInfo[]? _renderedSessions;
    private long _sourcesFormVersion;
    private long _jobsFormVersion;
    private long _backupFormVersion;
    private OwnerSourceEnrollment[]? _renderedSources;
    private OwnerSourceRefreshEntry[]? _renderedRefresh;
    private OwnerCollectionMapping[]? _renderedCollections;
    private OwnerSourceSnapshotDescriptor[]? _renderedSnapshots;

    internal OwnerConsoleWindow(OwnerConsoleSession session, IOwnerFilePicker? filePicker = null)
    {
        _session = session;
        _filePicker = filePicker ?? new OwnerSystemFilePicker();
        InitializeComponent();
        Navigation.SelectedIndex = 0;
        ActivityList.ItemsSource = _session.Activity;
        InitializeLibraryControls();
        InitializeInboxControls();
        InitializeRelationControls();
        InitializeJobsControls();
        InitializeSharingControls();
        InitializeSourcesControls();
        InitializeBackupControls();
        // The session may already hold answers (a window opened mid-workflow):
        // take its editor text, filters, drafts and lists now instead of
        // showing empty controls beside reported answers.
        _captureDraftVersion = _session.CaptureDraftVersion;
        _reviewDraftVersion = _session.ReviewDraftVersion;
        _libraryFilterVersion = _session.LibraryFilterVersion;
        _relationFormVersion = _session.RelationFormVersion;
        _sharingFormVersion = _session.SharingFormVersion;
        _sourcesFormVersion = _session.SourcesFormVersion;
        _jobsFormVersion = _session.JobsFormVersion;
        _backupFormVersion = _session.BackupFormVersion;
        SyncInboxEditors();
        LibraryResults.MouseDoubleClick += async (_, _) => await OpenSelectedLibraryResultAsync();
        RelationsList.MouseDoubleClick += async (_, _) => await InspectSelectedRelationAsync();
        _session.Changed += OnSessionChanged;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _timer.Tick += OnTimerTick;
        _timer.Start();
        Render();
    }
    /// <summary>
    /// Stops this view from following the session: no timer tick and no
    /// re-render on later session answers. The session itself is untouched.
    /// </summary>
    internal void DetachView()
    {
        _timer.Stop();
        _session.Changed -= OnSessionChanged;
        ActivityList.ItemsSource = null;
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
        foreach (var kind in new[] { AnyFilterItem }.Concat(MemoryKindTokens))
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

    /// <summary>
    /// Renders the session's latest answers. The session is a plain object
    /// that any thread may answer on, so a change that arrives from another
    /// thread is handed to the dispatcher instead of touching a control of
    /// this window from the wrong thread.
    /// </summary>
    private void OnSessionChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
            {
                Dispatcher.BeginInvoke(new Action(Render));
            }

            return;
        }

        Render();
    }

    private async void OnTimerTick(object? sender, EventArgs e)
    {
        await _session.CheckAsync();
        if (Navigation.SelectedIndex == 9)
        {
            await _session.RefreshIntentionsAsync();
        }
    }

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
    /// <summary>
    /// Task-oriented navigation: each view names where the work continues, so
    /// the owner never needs a guide for the next button.
    /// </summary>
    private void OnGoInboxClicked(object sender, RoutedEventArgs e) => Navigation.SelectedIndex = 4;

    private void OnGoRelationsClicked(object sender, RoutedEventArgs e) => Navigation.SelectedIndex = 5;

    private void OnGoSharingClicked(object sender, RoutedEventArgs e) => Navigation.SelectedIndex = 6;

    private void OnGoLibraryClicked(object sender, RoutedEventArgs e) => Navigation.SelectedIndex = 3;

    private void OnGoSourcesClicked(object sender, RoutedEventArgs e) => Navigation.SelectedIndex = 7;

    private void OnGoJobsClicked(object sender, RoutedEventArgs e) => Navigation.SelectedIndex = 8;

    private void OnNavigationChanged(object sender, SelectionChangedEventArgs e)
    {
        OwnerSessionPanel.Visibility = Navigation.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        VaultStatusPanel.Visibility = Navigation.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        VaultKeyPanel.Visibility = Navigation.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        LibraryPanel.Visibility = Navigation.SelectedIndex == 3 ? Visibility.Visible : Visibility.Collapsed;
        InboxPanel.Visibility = Navigation.SelectedIndex == 4 ? Visibility.Visible : Visibility.Collapsed;
        RelationsPanel.Visibility = Navigation.SelectedIndex == 5 ? Visibility.Visible : Visibility.Collapsed;
        SharingPanel.Visibility = Navigation.SelectedIndex == 6 ? Visibility.Visible : Visibility.Collapsed;
        SourcesPanel.Visibility = Navigation.SelectedIndex == 7 ? Visibility.Visible : Visibility.Collapsed;
        JobsPanel.Visibility = Navigation.SelectedIndex == 8 ? Visibility.Visible : Visibility.Collapsed;
        IntentionsPanel.Visibility = Navigation.SelectedIndex == 9 ? Visibility.Visible : Visibility.Collapsed;
        ReviewQueuePanel.Visibility = Navigation.SelectedIndex == 10 ? Visibility.Visible : Visibility.Collapsed;
        BackupExportPanel.Visibility = Navigation.SelectedIndex == 11 ? Visibility.Visible : Visibility.Collapsed;
        if (Navigation.SelectedIndex == 9)
        {
            _ = _session.RefreshIntentionsAsync();
        }

        if (Navigation.SelectedIndex == 10)
        {
            _ = _session.RefreshReviewQueueAsync();
        }

        // Keyboard and orientation: the scrolled content returns to the top of
        // the newly shown view, and focus lands on its first actionable
        // control, so Tab starts inside the view and every view is reachable
        // without a pointer.
        ContentScrollViewer.ScrollToTop();
        FocusView(Navigation.SelectedIndex);
    }

    /// <summary>
    /// Moves keyboard focus to the first actionable control of the shown view.
    /// </summary>
    private void FocusView(int index)
    {
        var target = index switch
        {
            0 => (System.Windows.IInputElement?)ConnectButton,
            2 => (System.Windows.IInputElement?)ProvisionPassphraseInput,
            3 => (System.Windows.IInputElement?)LibraryQueryInput,
            4 => (System.Windows.IInputElement?)CaptureTextInput,
            5 => (System.Windows.IInputElement?)RelationsRefreshButton,
            6 => (System.Windows.IInputElement?)CatalogPreviewButton,
            7 => (System.Windows.IInputElement?)SourcesRefreshButton,
            8 => (System.Windows.IInputElement?)JobsRunButton,
            9 => (System.Windows.IInputElement?)IntentionsRefreshButton,
            10 => (System.Windows.IInputElement?)ReviewQueueRefreshButton,
            11 => (System.Windows.IInputElement?)BackupPathInput,
            _ => null,
        };
        target?.Focus();
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
        StatusText.Text = (attached ? "Session: attached" : "Session: none") +
            " | " + FriendlySummary(attached, status) +
            " | last check: " + LastCheckText_Value();

        if (_libraryFilterVersion != _session.LibraryFilterVersion)
        {
            // The session dropped the filters with the private state (lock, end
            // of session): the input controls of this window go with them.
            _libraryFilterVersion = _session.LibraryFilterVersion;
            SyncLibraryFilterInputs();
        }

        if (_captureDraftVersion != _session.CaptureDraftVersion || _reviewDraftVersion != _session.ReviewDraftVersion)
        {
            // The session dropped or replaced the inbox editor content with the
            // private state it belonged to (lock, end of session, a newly opened
            // proposal): the input controls of this window go with it.
            _captureDraftVersion = _session.CaptureDraftVersion;
            _reviewDraftVersion = _session.ReviewDraftVersion;
            SyncInboxEditors();
        }

        if (_relationFormVersion != _session.RelationFormVersion)
        {
            // The session dropped the relation draft of the epoch that ended
            // (lock, lost service, end of session): the relation controls of
            // this window go with it, so no draft of a finished epoch stays on
            // screen and none of it can be sent later as if it were current.
            _relationFormVersion = _session.RelationFormVersion;
            SyncRelationForm();
        }

        if (_sharingFormVersion != _session.SharingFormVersion)
        {
            // The session dropped the sharing drafts of the epoch that ended
            // (lock, lost service, end of session): the publication, narrowing
            // and handoff controls of this window go with them, so no draft of
            // a finished epoch stays on screen and none can be sent later.
            _sharingFormVersion = _session.SharingFormVersion;
            SyncSharingForm();
        }

        if (_sourcesFormVersion != _session.SourcesFormVersion)
        {
            // The session dropped the sources drafts of the epoch that ended
            // (lock, lost service, end of session): the mapping-review,
            // collection and snapshot controls of this window go with them.
            _sourcesFormVersion = _session.SourcesFormVersion;
            SyncSourcesForm();
        }

        if (_jobsFormVersion != _session.JobsFormVersion)
        {
            // The session dropped the jobs drafts of the epoch that ended
            // (lock, lost service, end of session): the selection, destination
            // and schedule controls of this window go with them.
            _jobsFormVersion = _session.JobsFormVersion;
            SyncJobsForm();
        }

        RenderVaultStatus(attached, status);
        RenderVaultKey(attached);
        RenderLibrary(attached);
        RenderInbox(attached);
        RenderRelations(attached);
        RenderSharing(attached);
        RenderSources(attached);
        RenderJobs(attached);
        RenderIntentions(attached);
        RenderReview(attached);
        RenderBackupExport(attached);
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
    /// <summary>
    /// The status-bar summary in owner words: session and vault state without
    /// pipe names, protocol numbers, account SIDs or wire codes.
    /// </summary>
    private static string FriendlySummary(bool attached, OwnerChannelStatus? status)
    {
        if (!attached || status is null)
        {
            return "service: not contacted yet";
        }

        if (status.Availability != OwnerChannelAvailability.Reachable)
        {
            return status.Availability switch
            {
                OwnerChannelAvailability.ServiceUnavailable => "service: unreachable — start it, then Check again",
                OwnerChannelAvailability.OwnerAuthorityDenied => "service: connected but not the owner channel",
                OwnerChannelAvailability.ProtocolMismatch => "service: version mismatch — update app or service",
                _ => "service: no usable answer",
            };
        }

        return status.VaultState switch
        {
            OwnerVaultState.Unlocked => "vault: unlocked",
            OwnerVaultState.Locked => "vault: locked",
            OwnerVaultState.Locking => "vault: locking",
            OwnerVaultState.Uninitialized => "vault: not created yet",
            _ => "vault: unknown state",
        };
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

        RenderOutcome(VaultMessageText, _session.VaultMessage ??
            "No owner vault act has been sent in this session. Nothing here acts on the vault until you choose one.");
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
            ClearIntentionInputs();
            ReviewRationaleInput.Clear();
            ClearBackupInputs();
        }
        finally
        {
            _clearingInputs = false;
        }
    }

    /// <summary>
    /// Keyboard workflow of the whole console: Ctrl+F reaches the library search
    /// box, Ctrl+L asks the service to lock the vault (the same owner act as the
    /// button), and F5 refreshes the active view's owner query, including the
    /// private intention schedule. Every shortcut calls its ordinary service operation.
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

        if (e.Key == System.Windows.Input.Key.F5 && Navigation.SelectedIndex == 5)
        {
            e.Handled = true;
            _ = _session.RefreshRelationsAsync();
        }

        if (e.Key == System.Windows.Input.Key.F5 && Navigation.SelectedIndex == 6)
        {
            e.Handled = true;
            _ = RefreshSharingAsync();
        }

        if (e.Key == System.Windows.Input.Key.F5 && Navigation.SelectedIndex == 7)
        {
            e.Handled = true;
            _ = RefreshSourcesForKeyboardAsync();
        }
        if (e.Key == System.Windows.Input.Key.F5 && Navigation.SelectedIndex == 9)
        {
            e.Handled = true;
            _ = _session.RefreshIntentionsAsync();
        }

        if (e.Key == System.Windows.Input.Key.F5 && Navigation.SelectedIndex == 8)
        {
            e.Handled = true;
            _ = RefreshJobsForKeyboardAsync();
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

    private async void OnLibraryHistoryClicked(object sender, RoutedEventArgs e) =>
        await _session.ReadLibraryTimelineAsync();

    /// <summary>
    /// S7-T4 the owner asks the service what recall decision the open item
    /// carries. The console shows the answer; it never derives one.
    /// </summary>
    private async void OnLibraryRecallReadClicked(object sender, RoutedEventArgs e) =>
        await _session.ReadRecallStateAsync();

    private async void OnLibraryRecallDemoteClicked(object sender, RoutedEventArgs e) =>
        await _session.ApplyRecallActAsync("demote", LibraryRecallReasonInput.Text);

    private async void OnLibraryRecallPinClicked(object sender, RoutedEventArgs e) =>
        await _session.ApplyRecallActAsync("pin", LibraryRecallReasonInput.Text);

    private async void OnLibraryRecallArchiveClicked(object sender, RoutedEventArgs e) =>
        await _session.ApplyRecallActAsync("archive", LibraryRecallReasonInput.Text);

    private async void OnLibraryRecallRestoreClicked(object sender, RoutedEventArgs e) =>
        await _session.ApplyRecallActAsync("restore", LibraryRecallReasonInput.Text);
    /// <summary>
    /// S7-T5 the owner previews the exact purge impact for the open item.
    /// The console shows the IDs the service would delete; it never deletes.
    /// </summary>
    private async void OnLibraryPurgePreviewClicked(object sender, RoutedEventArgs e) =>
        await _session.PreviewPurgeAsync(LibraryPurgeReasonInput.Text);

    /// <summary>
    /// S7-T5 the owner confirms the exact previewed purge by typing PURGE.
    /// The console sends the typed confirmation with the bound preview; the
    /// service fails closed on any change.
    /// </summary>
    private async void OnLibraryPurgeConfirmClicked(object sender, RoutedEventArgs e) =>
        await _session.ConfirmPurgeAsync(LibraryPurgeConfirmInput.Text, LibraryPurgeReasonInput.Text);

    private async void OnLibraryRetainedClicked(object sender, RoutedEventArgs e)
    {
        if (_session.LibraryTimeline is { RetainedRevisions.Length: > 0 } timeline)
        {
            var oldest = timeline.RetainedRevisions[0];
            foreach (var entry in timeline.RetainedRevisions)
            {
                if (entry.SubjectRevision < oldest.SubjectRevision)
                {
                    oldest = entry;
                }
            }

            await _session.OpenLibraryRetainedAsync(oldest.SubjectRevision);
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
    /// <summary>
    /// Pins the open library item as the relation subject and moves to
    /// Relations: the live revision travels, no ID to retype.
    /// </summary>
    private void OnLibraryUseAsRelationSubjectClicked(object sender, RoutedEventArgs e)
    {
        if (_session.LibraryItem is not { } item)
        {
            return;
        }

        _relationSubject = new OwnerConsoleSession.RelationEndpointPick(
            item.Id, "memoryItem", item.Revision, item.ZoneId,
            item.Id + "@" + item.Revision.ToString(CultureInfo.InvariantCulture) + "  zone=" + item.ZoneId + "  kind=" + item.Kind);
        if (string.IsNullOrWhiteSpace(RelationZoneInput.Text))
        {
            RelationZoneInput.Text = item.ZoneId;
        }

        Navigation.SelectedIndex = 5;
        Render();
    }

    /// <summary>
    /// Pins the open library item as the relation object and moves to Relations.
    /// </summary>
    private void OnLibraryUseAsRelationObjectClicked(object sender, RoutedEventArgs e)
    {
        if (_session.LibraryItem is not { } item)
        {
            return;
        }

        _relationObject = new OwnerConsoleSession.RelationEndpointPick(
            item.Id, "memoryItem", item.Revision, item.ZoneId,
            item.Id + "@" + item.Revision.ToString(CultureInfo.InvariantCulture) + "  zone=" + item.ZoneId + "  kind=" + item.Kind);
        Navigation.SelectedIndex = 5;
        Render();
    }

    /// <summary>
    /// Appends the open library item to the job inputs and moves to Jobs.
    /// </summary>
    private void OnLibraryUseInJobsClicked(object sender, RoutedEventArgs e)
    {
        if (_session.LibraryItem is not { } item)
        {
            return;
        }

        AppendJobsInput(item.Id, item.Revision, item.Title);
        Navigation.SelectedIndex = 8;
        Render();
    }

    /// <summary>
    /// Fills the catalog mapping from the open library item and moves to Sharing.
    /// </summary>
    private void OnLibraryUseInCatalogClicked(object sender, RoutedEventArgs e)
    {
        if (_session.LibraryItem is not { } item)
        {
            return;
        }

        CatalogResourceInput.Text = item.Id;
        Navigation.SelectedIndex = 6;
        Render();
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
        if (!TryParseOwnerUtcDate(LibraryFromInput.Text, out var fromUtc) ||
            !TryParseOwnerUtcDate(LibraryToInput.Text, out var toUtc) ||
            !TryParseOwnerUtcDate(LibraryAsOfInput.Text, out var asOfUtc))
        {
            _session.ReportLibraryInputProblem(
                "This console did not send the search: a From/To/As-of value is not a date or date-time it understands. Use yyyy-MM-dd or yyyy-MM-ddTHH:mm in UTC.");
            return false;
        }

        query = new OwnerLibraryQuery(
            Text: string.IsNullOrWhiteSpace(LibraryQueryInput.Text) ? null : LibraryQueryInput.Text.Trim(),
            ZoneId: EditableFilterText(LibraryZoneInput),
            CollectionId: EditableFilterText(LibraryCollectionInput),
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
            Limit: CurrentPageSize(),
            AsOfUtc: asOfUtc,
            IncludeArchived: LibraryIncludeArchivedInput.IsChecked == true);
        return true;
    }

    /// <summary>
    /// Reads one owner-supplied UTC date-time. A value this console does not
    /// understand is refused with a plain explanation instead of being dropped
    /// silently into a different window or a different capture.
    /// </summary>
    private static bool TryParseOwnerUtcDate(string text, out DateTimeOffset? value)
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

    private static string? EditableFilterText(System.Windows.Controls.ComboBox box)
    {
        var text = box.Text;
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private static string? SelectedFilter(System.Windows.Controls.ComboBox box) =>
        box.SelectedItem is string value && !string.Equals(value, AnyFilterItem, StringComparison.Ordinal) ? value : null;


    /// <summary>
    /// Puts the session's own filter state back into the input controls. Used at
    /// startup and whenever the session drops the filters with the private state,
    /// so the window never shows a filter that is no longer in force.
    /// </summary>
    private void SyncLibraryFilterInputs()
    {
        RefreshLibraryPickerOptions();
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
        LibraryAsOfInput.Text = string.Empty;
    }

    /// <summary>
    /// Refreshes the zone/collection pickers from the zones and collections the
    /// service named in this session, keeping the owner's typed text.
    /// </summary>
    private void RefreshLibraryPickerOptions()
    {
        var zoneText = LibraryZoneInput.Text;
        LibraryZoneInput.Items.Clear();
        foreach (var zone in _session.KnownZones())
        {
            LibraryZoneInput.Items.Add(zone);
        }

        LibraryZoneInput.Text = zoneText;
        var collectionText = LibraryCollectionInput.Text;
        LibraryCollectionInput.Items.Clear();
        foreach (var collection in _session.KnownCollections())
        {
            LibraryCollectionInput.Items.Add(collection);
        }

        LibraryCollectionInput.Text = collectionText;
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
        RefreshLibraryPickerOptions();

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
                " asOf=" + Stamp(page.AsOfUtc) +
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

        var timeline = _session.LibraryTimeline;
        if (!ReferenceEquals(_renderedTimeline, timeline))
        {
            _renderedTimeline = timeline;
        }

        LibraryHistoryText.Text = timeline is null
            ? (unlocked
                ? "No decision history is open. Open an item, then read its decision history."
                : "No private decision history is shown.")
            : DescribeTimeline(timeline);
        var retained = _session.LibraryRetained;
        if (!ReferenceEquals(_renderedRetained, retained))
        {
            _renderedRetained = retained;
        }

        LibraryRetainedText.Text = retained is null
            ? string.Empty
            : "retained subject revision=" + retained.SubjectRevision.ToString(CultureInfo.InvariantCulture) +
                " archive=" + retained.ArchiveResourceId + "@" + retained.ArchiveRevision.ToString(CultureInfo.InvariantCulture) +
                " kind=" + retained.Kind + " title=" + (retained.Title ?? "(none)") + " author=" + retained.Author +
                " evidence=" + retained.Evidence + " occurred=" + Stamp(retained.OccurredAtUtc) +
                " recorded=" + Stamp(retained.RecordedAtUtc) + "\n" + retained.Body;

        var recall = _session.LibraryRecall;
        LibraryRecallText.Text = recall is null
            ? (unlocked
                ? "No recall decision is open. Open an item, then read its recall state."
                : "No private recall decision is shown.")
            : "state=" + recall.Disposition +
                " actor=" + (recall.Actor ?? "(none)") +
                " recorded=" + Stamp(recall.RecordedAtUtc) +
                (string.IsNullOrWhiteSpace(recall.Rationale) ? string.Empty : "\nreason: " + recall.Rationale) +
                (recall.History.Length == 0
                    ? string.Empty
                    : "\nacts: " + string.Join(" | ", recall.History.Select(change =>
                        change.From + " -> " + change.To + " (" + change.Actor + " at " + Stamp(change.RecordedAtUtc) + ")" +
                        (string.IsNullOrWhiteSpace(change.Rationale) ? string.Empty : ": " + change.Rationale))));

        // The recall acts act on the open item only, and only while the vault
        // is unlocked. A protected item cannot be demoted or set aside without
        // being released first, so the console does not offer that act as
        // available: the service refuses it and the owner sees why.
        var canAct = unlocked && detail is not null && !inFlight;
        var recallState = recall?.Disposition ?? "normal";
        LibraryRecallReadButton.IsEnabled = canAct;
        LibraryRecallDemoteButton.IsEnabled = canAct && recallState is not ("demoted" or "archived");
        LibraryRecallPinButton.IsEnabled = canAct && recallState is not ("pinned" or "archived");
        LibraryRecallArchiveButton.IsEnabled = canAct && recallState is not ("archived" or "pinned");
        LibraryRecallRestoreButton.IsEnabled = canAct && recallState != "normal";
        LibraryRecallReasonInput.IsEnabled = canAct;
        var purge = _session.LibraryPurgePreview;
        var purged = _session.LibraryPurge;
        LibraryPurgeText.Text = purged is not null
            ? "purged target=" + purged.TargetResourceId + " deleted=" +
                purged.Deleted.Length.ToString(CultureInfo.InvariantCulture) +
                " catalog=" + string.Join(",", purged.WithdrawnCatalogIds) +
                " generation=" + purged.FinalGeneration.ToString(CultureInfo.InvariantCulture)
            : purge is null
                ? (unlocked
                    ? "No purge preview is open. Open an item, type the owner reason, then preview the exact impact."
                    : "No private purge preview is shown.")
                : "preview=" + purge.PurgeId + " target=" + purge.TargetResourceId + "@" + purge.TargetRevision.ToString(CultureInfo.InvariantCulture) +
                    " impacted=" + string.Join(",", purge.Impacted.Select(item => item.ResourceId + "@" + item.Revision.ToString(CultureInfo.InvariantCulture))) +
                    " catalog=" + string.Join(",", purge.WithdrawnCatalogIds) +
                    " grants=" + purge.GrantsTouched.ToString(CultureInfo.InvariantCulture) +
                    " sessions=" + purge.SessionsTouched.ToString(CultureInfo.InvariantCulture) +
                    "\nS8: " + purge.S8BackupNote;
        LibraryPurgePreviewButton.IsEnabled = canAct;
        LibraryPurgeConfirmButton.IsEnabled = canAct && purge is not null;
        LibraryPurgeReasonInput.IsEnabled = canAct;
        LibraryPurgeConfirmInput.IsEnabled = canAct && purge is not null;

        RenderOutcome(LibraryMessageText, _session.LibraryMessage ?? (unlocked
            ? "No library act has been sent in this session. Nothing private is read until you search."
            : "No private library act is offered while the service does not report the vault unlocked."));
        if (!unlocked)
        {
            LibraryPurgeReasonInput.Text = string.Empty;
            LibraryPurgeConfirmInput.Text = string.Empty;
        }

        LibraryHistoryButton.IsEnabled = unlocked && !inFlight && detail is not null;
        LibraryRetainedButton.IsEnabled = unlocked && !inFlight && timeline is { RetainedRevisions.Length: > 0 };
        LibraryUseAsRelationSubjectButton.IsEnabled = canAct;
        LibraryUseAsRelationObjectButton.IsEnabled = canAct;
        LibraryUseInJobsButton.IsEnabled = canAct;
        LibraryUseInCatalogButton.IsEnabled = canAct;
    }

    private static string DescribeTimeline(OwnerTimelineDetail timeline)
    {
        var lines = new List<string>
        {
            "subject=" + timeline.SubjectResourceId + " kind=" + timeline.SubjectKind +
                " publication=" + timeline.Publication +
                (timeline.Question is null ? string.Empty : " question=" + timeline.Question) +
                (timeline.Intention is null ? string.Empty : " intention=" + timeline.Intention) +
                (string.IsNullOrEmpty(timeline.SupersededBy) ? string.Empty : " supersededBy=" + timeline.SupersededBy),
            "occurred=" + DescribeStamp(timeline.Occurred) + " recorded=" + DescribeStamp(timeline.Recorded) +
                " validFrom=" + DescribeStamp(timeline.ValidFrom) + " validUntil=" + DescribeStamp(timeline.ValidUntil) +
                " reviewAfter=" + DescribeStamp(timeline.ReviewAfter),
        };
        foreach (var change in timeline.History)
        {
            lines.Add(
                change.RecordedAtUtc.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture) +
                " " + change.Aspect + "/" + change.Act + " " + change.FromState + "->" + change.ToState +
                " by=" + change.Executor +
                (string.IsNullOrEmpty(change.Rationale) ? string.Empty : " rationale=" + change.Rationale) +
                (string.IsNullOrEmpty(change.ReplacementResourceId) ? string.Empty : " replacement=" + change.ReplacementResourceId));
        }

        foreach (var entry in timeline.RetainedRevisions)
        {
            lines.Add(
                "retained revision=" + entry.SubjectRevision.ToString(CultureInfo.InvariantCulture) +
                " archive=" + entry.ArchiveResourceId + " by=" + entry.Executor +
                " at=" + entry.RecordedAtUtc.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture));
        }

        return string.Join("\n", lines);
    }

    private static string DescribeStamp(OwnerTimelineStamp? stamp)
    {
        if (stamp is null)
        {
            return "none";
        }

        if (string.Equals(stamp.Precision, "unknown", StringComparison.Ordinal))
        {
            return "unknown";
        }

        var zone = stamp.TimeZoneOffsetMinutes is { } offset
            ? (offset < 0 ? "-" : "+") + (Math.Abs(offset) / 60).ToString("00", CultureInfo.InvariantCulture) + ":" +
                (Math.Abs(offset) % 60).ToString("00", CultureInfo.InvariantCulture)
            : "unknown";
        if (string.Equals(stamp.Precision, "exact", StringComparison.Ordinal))
        {
            return "exact:" + (stamp.InstantUtc?.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture) ?? "(none)") + "@" + zone;
        }

        return stamp.Precision + ":" + (stamp.LocalYear?.ToString(CultureInfo.InvariantCulture) ?? "?") +
            (stamp.LocalMonth is { } month ? "-" + month.ToString("00", CultureInfo.InvariantCulture) : string.Empty) +
            (stamp.LocalDay is { } day ? "-" + day.ToString("00", CultureInfo.InvariantCulture) : string.Empty) +
            (stamp.LocalQuarter is { } quarter ? "-Q" + quarter.ToString(CultureInfo.InvariantCulture) : string.Empty) +
            "@" + zone;
    }

    // ---- S5-T4 inbox: capture and proposal review ----

    /// <summary>
    /// The review editor names a kind and a level from the service's own
    /// spellings. A kind is required (there is no "(any)" here): the reviewed
    /// item is either this kind or it is not reviewed at all.
    /// </summary>
    private void InitializeInboxControls()
    {
        foreach (var kind in MemoryKindTokens)
        {
            ReviewKindInput.Items.Add(kind);
        }

        foreach (var level in new[] { "l0", "l1", "l2", "l3Plus" })
        {
            ReviewLevelInput.Items.Add(level);
        }
    }

    /// <summary>
    /// Puts the session's inbox state back into the input controls: the capture
    /// editor, the review editor and the rationale. Used at startup and whenever
    /// the session replaces or drops that state (a newly opened proposal, a lock,
    /// the end of the session), so this window never keeps text the session no
    /// longer holds.
    /// </summary>
    private void SyncInboxEditors()
    {
        _syncingInboxEditors = true;
        try
        {
            CaptureTextInput.Text = _session.CaptureDraft;
            ReviewRationaleInput.Text = string.Empty;
            var draft = _session.ReviewDraft;
            ReviewKindInput.SelectedItem = draft?.Kind;
            ReviewLevelInput.SelectedItem = draft?.Level;
            ReviewTitleInput.Text = draft?.Title ?? string.Empty;
            ReviewBodyInput.Text = draft?.Body ?? string.Empty;
        }
        finally
        {
            _syncingInboxEditors = false;
        }
    }

    private void OnCaptureTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingInboxEditors)
        {
            return;
        }

        _session.SetCaptureDraft(CaptureTextInput.Text);
        RenderCaptureHint();
    }

    /// <summary>
    /// Shows whether the editor holds text the vault does not have. This is the
    /// fact the lock warning and the "saved" claim both come from, so it is
    /// never inferred from the button having been pressed.
    /// </summary>
    private void RenderCaptureHint()
    {
        var length = _session.CaptureDraft.Length;
        CaptureHintText.Text = _session.HasUnsavedCaptureText
            ? "UNSAVED: the editor holds " + length.ToString(CultureInfo.InvariantCulture) +
                " characters the vault does not have. Locking asks before discarding them; Clear editor discards them now."
            : length == 0
                ? "The editor is empty. Nothing is written until you press Save capture."
                : "The editor text is exactly the text this session saved.";
    }

    private async void OnCaptureSaveClicked(object sender, RoutedEventArgs e)
    {
        if (!TryReadCaptureFields(out var resourceId, out var zoneId, out var title, out var locator, out var author, out var capturedAt))
        {
            return;
        }

        await _session.CaptureAsync(resourceId, zoneId, title, locator, author, capturedAt);
    }

    private void OnCaptureClearClicked(object sender, RoutedEventArgs e) => _session.ClearCaptureDraft();

    private async void OnCaptureReadBackClicked(object sender, RoutedEventArgs e)
    {
        var sourceId = CaptureReadBackIdInput.Text.Trim();
        if (sourceId.Length == 0)
        {
            _session.ReportInboxInputProblem("This console did not send the read: name the note ID (or a source ID) the service should serve.");
            return;
        }

        long? exact = null;
        var revisionText = CaptureReadBackRevisionInput.Text.Trim();
        if (revisionText.Length > 0)
        {
            if (!long.TryParse(revisionText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed < 1)
            {
                _session.ReportInboxInputProblem("This console did not send the read: the revision must be a positive number, or empty for the live revision.");
                return;
            }

            exact = parsed;
        }

        await _session.ReadBackCaptureAsync(sourceId, exact);
    }
    /// <summary>
    /// Fills the read-back from the saved capture of this session: the exact ID
    /// the service stored, with the live revision. No ID to remember.
    /// </summary>
    private void OnCaptureReadBackUseSavedClicked(object sender, RoutedEventArgs e)
    {
        if (_session.CaptureSource is { } saved)
        {
            CaptureReadBackIdInput.Text = saved.Id;
            CaptureReadBackRevisionInput.Text = string.Empty;
            Render();
        }
        else
        {
            _session.ReportInboxInputProblem("No capture from this session is saved yet: save one above first, then read it back. Nothing was sent.");
        }
    }

    /// <summary>
    /// Reads the capture fields. An empty ID or zone is refused here with an
    /// explanation instead of being sent as a request the service would refuse
    /// without saying which field was wrong; the text stays in the editor either
    /// way.
    /// </summary>
    private bool TryReadCaptureFields(
        out string resourceId,
        out string zoneId,
        out string? title,
        out string? locator,
        out string? author,
        out DateTimeOffset? capturedAtUtc)
    {
        resourceId = CaptureResourceInput.Text.Trim();
        zoneId = CaptureZoneInput.Text.Trim();
        title = FilterText(CaptureTitleInput.Text);
        locator = FilterText(CaptureLocatorInput.Text);
        author = FilterText(CaptureAuthorInput.Text);
        capturedAtUtc = null;

        if (resourceId.Length == 0 || zoneId.Length == 0)
        {
            _session.ReportInboxInputProblem(
                "This console did not send the capture: the note needs an ID you name and the zone it belongs to. Your text is still in the editor.");
            return false;
        }

        if (!TryParseOwnerUtcDate(CaptureDateInput.Text, out capturedAtUtc))
        {
            _session.ReportInboxInputProblem(
                "This console did not send the capture: the source date is not a date it understands. Use yyyy-MM-dd or yyyy-MM-ddTHH:mm in UTC, or leave it empty. Your text is still in the editor.");
            return false;
        }

        return true;
    }

    private async void OnReviewInboxClicked(object sender, RoutedEventArgs e) =>
        await _session.RefreshReviewInboxAsync();

    private async void OnReviewOpenClicked(object sender, RoutedEventArgs e) => await OpenSelectedProposalAsync();

    private async void OnReviewProposalKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter)
        {
            return;
        }

        e.Handled = true;
        await OpenSelectedProposalAsync();
    }

    private void OnReviewProposalSelectionChanged(object sender, SelectionChangedEventArgs e) => Render();

    private async Task OpenSelectedProposalAsync()
    {
        if (ReviewProposals.SelectedItem is ProposalRow row)
        {
            await _session.OpenReviewProposalAsync(row.Identity);
        }
    }

    private async void OnReviewPublishClicked(object sender, RoutedEventArgs e)
    {
        if (BuildReviewedDraft() is not { } reviewed)
        {
            _session.ReportInboxInputProblem("This console did not send the review: open a proposal and choose a kind and a level for it.");
            return;
        }

        await _session.PublishReviewProposalAsync(reviewed);
    }

    private async void OnReviewRejectClicked(object sender, RoutedEventArgs e) =>
        await _session.RejectReviewProposalAsync(ReviewRationaleInput.Text);

    private async void OnReviewUndoClicked(object sender, RoutedEventArgs e) =>
        await _session.UndoReviewDecisionAsync(ReviewRationaleInput.Text);

    /// <summary>
    /// Previews the sanitized publication of the open proposal into the
    /// explicit destination: the exact shared content beside its private pins.
    /// </summary>
    private async void OnDeclassifyPreviewClicked(object sender, RoutedEventArgs e)
    {
        if (BuildSanitizedDraft() is not { } sanitized)
        {
            _session.ReportInboxInputProblem("This console did not send the preview: open a proposal and choose a kind and a level for the sanitized copy.");
            return;
        }

        await _session.PreviewDeclassifiedAsync(DeclassifyDestinationInput.Text.Trim(), DeclassifyZoneInput.Text.Trim(), sanitized);
    }

    /// <summary>
    /// Publishes the sanitized content into the explicit destination once the
    /// owner types PUBLISH for exactly that content. The shared copy carries
    /// no citations; private lineage stays encrypted.
    /// </summary>
    private async void OnDeclassifyPublishClicked(object sender, RoutedEventArgs e)
    {
        if (BuildSanitizedDraft() is not { } sanitized)
        {
            _session.ReportInboxInputProblem("This console did not send the publication: open a proposal and choose a kind and a level for the sanitized copy.");
            return;
        }

        await _session.PublishDeclassifiedAsync(
            DeclassifyDestinationInput.Text.Trim(), DeclassifyZoneInput.Text.Trim(), sanitized, DeclassifyConfirmInput.Text.Trim());
    }

    /// <summary>
    /// The sanitized content this console sends: the owner's edited title,
    /// body and metadata with no citations, keeping the proposal's author,
    /// creation method and evidence level. This view offers no control that
    /// changes attribution, and the service refuses a sanitized draft that
    /// names different ones or carries any citation.
    /// </summary>
    private OwnerReviewDraft? BuildSanitizedDraft()
    {
        var proposal = _session.ReviewProposal;
        if (proposal is null || ReviewKindInput.SelectedItem is not string kind || ReviewLevelInput.SelectedItem is not string level)
        {
            return null;
        }

        return new OwnerReviewDraft(
            kind,
            proposal.Item.Author,
            proposal.Item.CreationMethod,
            proposal.Item.Evidence,
            level,
            ReviewTitleInput.Text,
            ReviewBodyInput.Text,
            Array.Empty<OwnerItemSource>(),
            proposal.Item.OccurredAtUtc,
            proposal.Item.Project,
            proposal.Item.Environment,
            proposal.Item.Version);
    }

    /// <summary>
    /// The reviewed content this console sends: the owner's edits for the content
    /// fields, and the attribution and citations exactly as the proposal carried
    /// them. This view offers no control that changes an author, a creation
    /// method or an evidence level, and the service independently refuses a
    /// reviewed draft that names different ones — so approving a proposal can
    /// never relabel an AI-written note as user-written or user-confirmed.
    /// </summary>
    private OwnerReviewDraft? BuildReviewedDraft()
    {
        var proposal = _session.ReviewProposal;
        if (proposal is null || ReviewKindInput.SelectedItem is not string kind || ReviewLevelInput.SelectedItem is not string level)
        {
            return null;
        }

        return new OwnerReviewDraft(
            kind,
            proposal.Item.Author,
            proposal.Item.CreationMethod,
            proposal.Item.Evidence,
            level,
            ReviewTitleInput.Text,
            ReviewBodyInput.Text,
            proposal.Item.Sources,
            proposal.Item.OccurredAtUtc,
            proposal.Item.Project,
            proposal.Item.Environment,
            proposal.Item.Version);
    }

    /// <summary>
    /// Renders the inbox from the service's answers only: the capture state, the
    /// proposal inbox, the opened proposal with the source it cites beside the
    /// editor, and the publication state of the item a decision writes. When the
    /// vault is not unlocked this shows no private content at all and every
    /// private text box is empty.
    /// </summary>
    private void RenderInbox(bool attached)
    {
        RefreshCapturePickerOptions();
        RefreshDeclassifyPickerOptions();
        var unlocked = attached && _session.ReportedVaultState == OwnerVaultState.Unlocked;
        var inFlight = _session.InboxActInFlight;
        RenderCaptureHint();
        RenderOutcome(CaptureMessageText, _session.CaptureMessage ?? (unlocked
            ? "No capture has been sent in this session. Nothing private is written until you press Save capture."
            : "No private capture act is offered while the service does not report the vault unlocked."));
        CaptureSavedText.Text = _session.CaptureSource is null
            ? "No capture from this session is saved; the editor text is not in the vault."
            : "Saved capture: id=" + _session.CaptureSource.Id +
                " revision=" + _session.CaptureSource.Revision.ToString(CultureInfo.InvariantCulture) +
                " zone=" + _session.CaptureSource.ZoneId +
                " title=" + (_session.CaptureSource.Title ?? "(none)") +
                " availability=" + _session.CaptureSource.Availability +
                " speaker=" + _session.CaptureSource.Author +
                " locator=" + (_session.CaptureSource.Locator ?? "(none)");

        if (!ReferenceEquals(_renderedCaptureSource, _session.CaptureSource) && _session.CaptureSource is { } savedSource)
        {
            _renderedCaptureSource = savedSource;
            CaptureReadBackIdInput.Text = savedSource.Id;
        }

        RenderOutcome(CaptureReadBackMessageText, _session.CaptureReadBackMessage ?? "No read-back has been sent in this session.");
        CaptureReadBackSourceText.Text = _session.CaptureReadBackSource is { } readBack
            ? "id=" + readBack.Id + "  revision=" + readBack.Revision.ToString(CultureInfo.InvariantCulture) +
                "  zone=" + readBack.ZoneId + "  availability=" + readBack.Availability +
                "  title=" + (readBack.Title ?? "(none)") + "  captured=" + Stamp(readBack.CapturedAtUtc) +
                "  locator=" + (readBack.Locator ?? "(none)")
            : string.Empty;
        CaptureReadBackText.Text = _session.CaptureReadBackText ?? string.Empty;

        var proposals = _session.ReviewProposals;
        if (!ReferenceEquals(_renderedProposals, proposals))
        {
            _renderedProposals = proposals;
            ReviewProposals.ItemsSource = proposals.Select(identity => new ProposalRow(identity)).ToArray();
        }

        RenderOutcome(ReviewInboxMessageText, _session.ReviewInboxMessage ?? (unlocked
            ? "No inbox listing has been asked for in this session."
            : "No private review act is offered while the service does not report the vault unlocked."));

        var proposal = _session.ReviewProposal;
        ReviewProvenanceText.Text = proposal is null
            ? "No proposal is open. Refresh the inbox, choose a pending proposal and open it."
            : "proposal=" + proposal.Id + "  revision=" + proposal.Revision.ToString(CultureInfo.InvariantCulture) +
                "  kind=" + proposal.Kind + "  zone=" + proposal.ZoneId +
                "  executor=" + proposal.Executor + "  recorded=" + Stamp(proposal.RecordedAtUtc) +
                "  inbox=" + proposal.DestinationResourceId + "@" + proposal.DestinationRevision.ToString(CultureInfo.InvariantCulture) +
                "  idempotency=" + proposal.IdempotencyKey +
                "  payloadHash=" + ShortDigest(proposal.PayloadHash) +
                (proposal.SubjectResourceId is null
                    ? "  pin=none (a create proposal)"
                    : "  pin=" + proposal.SubjectResourceId + "@" + (proposal.SubjectRevision ?? 0).ToString(CultureInfo.InvariantCulture)) +
                "  claimedUserConfirmed=" + (proposal.ClaimedUserConfirmed ? "claimed (never verified here)" : "no claim");
        ReviewItemStateText.Text = proposal is null ? string.Empty : _session.ReviewItemStateMessage ?? string.Empty;
        ReviewStalenessText.Text = proposal is null ? string.Empty : _session.ReviewStalenessMessage ?? string.Empty;
        ReviewAttributionText.Text = proposal is null
            ? string.Empty
            : "author=" + proposal.Item.Author + "  creation=" + proposal.Item.CreationMethod +
                "  evidence=" + proposal.Item.Evidence + "  (fixed by the proposal; read-only)";
        ReviewCitationsText.Text = proposal is null
            ? string.Empty
            : "stored citations: " + (proposal.Item.Sources.Length == 0
                ? "(none)"
                : string.Join("; ", proposal.Item.Sources.Select(cited =>
                    (cited.SourceId ?? "(no source id)") + "@" +
                    (cited.SourceRevision?.ToString(CultureInfo.InvariantCulture) ?? "(none)"))));
        ReviewSourceMetaText.Text = _session.ReviewSource is { } reviewSource
            ? "id=" + reviewSource.Id + "  revision=" + reviewSource.Revision.ToString(CultureInfo.InvariantCulture) +
                "  zone=" + reviewSource.ZoneId + "  availability=" + reviewSource.Availability +
                "  title=" + (reviewSource.Title ?? "(none)") + "  captured=" + Stamp(reviewSource.CapturedAtUtc) +
                "  locator=" + (reviewSource.Locator ?? "(none)")
            : _session.ReviewCitedItem is { } citedItem
                ? "memory item id=" + citedItem.Id + "  revision=" + citedItem.Revision.ToString(CultureInfo.InvariantCulture) +
                    "  zone=" + citedItem.ZoneId + "  kind=" + citedItem.Kind + "  level=" + citedItem.Level +
                    "  author=" + citedItem.Author + "  executor=" + citedItem.Executor +
                    "  creation=" + citedItem.CreationMethod + "  evidence=" + citedItem.Evidence +
                    "  title=" + (citedItem.Title ?? "(none)") + "  recorded=" + Stamp(citedItem.RecordedAtUtc)
                : string.Empty;
        ReviewSourceMessageText.Text = proposal is null ? string.Empty : _session.ReviewSourceMessage ?? string.Empty;
        ReviewSourceText.Text = _session.ReviewSourceText ?? string.Empty;
        RenderOutcome(ReviewMessageText, _session.ReviewMessage ?? (unlocked
            ? "No review act has been sent in this session. Publishing writes the content you see; rejecting records your decision without applying the proposal."
            : "No private review act is offered while the service does not report the vault unlocked."));
        var declassified = _session.DeclassifyPreview;
        DeclassifyPreviewText.Text = declassified is null
            ? string.Empty
            : "shared=" + declassified.DestinationResourceId + "  zone=" + declassified.DestinationZoneId +
                "  pins=" + string.Join("; ", declassified.Pins.Select(pin => pin.ResourceId + "@" + pin.Revision.ToString(CultureInfo.InvariantCulture))) +
                "  pinsCurrent=" + declassified.PinsCurrent.ToString(CultureInfo.InvariantCulture) +
                (declassified.MismatchedResourceIds.Length == 0 ? string.Empty : "  mismatched=" + string.Join(";", declassified.MismatchedResourceIds)) +
                "  title=" + (declassified.Item.Title ?? "(none)") + "  bodyChars=" + declassified.Item.Body.Length.ToString(CultureInfo.InvariantCulture) +
                "  sharedCitations=0";
        RenderOutcome(DeclassifyMessageText, _session.DeclassifyMessage ?? (unlocked
            ? "No sanitized publication has been previewed in this session. The shared copy carries the edited content above with no citations."
            : "No private declassification act is offered while the service does not report the vault unlocked."));

        CaptureSaveButton.IsEnabled = unlocked && !inFlight;
        CaptureClearButton.IsEnabled = !inFlight;
        CaptureReadBackButton.IsEnabled = unlocked && !inFlight;
        CaptureTextInput.IsEnabled = unlocked && !inFlight;
        CaptureResourceInput.IsEnabled = unlocked && !inFlight;
        CaptureZoneInput.IsEnabled = unlocked && !inFlight;
        CaptureTitleInput.IsEnabled = unlocked && !inFlight;
        CaptureLocatorInput.IsEnabled = unlocked && !inFlight;
        CaptureAuthorInput.IsEnabled = unlocked && !inFlight;
        CaptureDateInput.IsEnabled = unlocked && !inFlight;
        ReviewInboxButton.IsEnabled = unlocked && !inFlight;
        ReviewOpenButton.IsEnabled = unlocked && !inFlight && ReviewProposals.SelectedItem is ProposalRow;
        ReviewProposals.IsEnabled = unlocked && !inFlight;
        ReviewKindInput.IsEnabled = unlocked && !inFlight && proposal is not null;
        ReviewLevelInput.IsEnabled = unlocked && !inFlight && proposal is not null;
        ReviewTitleInput.IsEnabled = unlocked && !inFlight && proposal is not null;
        ReviewBodyInput.IsEnabled = unlocked && !inFlight && proposal is not null;
        ReviewRationaleInput.IsEnabled = unlocked && !inFlight && proposal is not null;
        ReviewPublishButton.IsEnabled = unlocked && !inFlight && proposal is not null;
        ReviewRejectButton.IsEnabled = unlocked && !inFlight && proposal is not null;
        ReviewUndoButton.IsEnabled = unlocked && !inFlight && _session.ReviewDecisionIsUndoable;
        DeclassifyDestinationInput.IsEnabled = unlocked && !inFlight && proposal is not null;
        DeclassifyZoneInput.IsEnabled = unlocked && !inFlight && proposal is not null;
        DeclassifyPreviewButton.IsEnabled = unlocked && !inFlight && proposal is not null;
        DeclassifyConfirmInput.IsEnabled = unlocked && !inFlight && proposal is not null;
        DeclassifyPublishButton.IsEnabled = unlocked && !inFlight && proposal is not null;
    }

    /// <summary>
    /// Refreshes the capture-zone picker from the zones the service named in
    /// this session, keeping the owner's typed text.
    /// </summary>
    private void RefreshCapturePickerOptions()
    {
        var text = CaptureZoneInput.Text;
        CaptureZoneInput.Items.Clear();
        foreach (var zone in _session.KnownZones())
        {
            CaptureZoneInput.Items.Add(zone);
        }

        CaptureZoneInput.Text = text;
    }
    /// <summary>
    /// Refreshes the shared-zone picker from the zones the service named in
    /// this session, keeping the owner's typed text.
    /// </summary>
    private void RefreshDeclassifyPickerOptions()
    {
        var text = DeclassifyZoneInput.Text;
        DeclassifyZoneInput.Items.Clear();
        foreach (var zone in _session.KnownZones())
        {
            DeclassifyZoneInput.Items.Add(zone);
        }

        DeclassifyZoneInput.Text = text;
    }
    // ---- S5-T5 explicit relations workbench ----

    private const string RelationFilterAll = "all";
    private const string RelationFilterProposed = "proposed";
    private const string RelationFilterAccepted = "accepted";
    private const string RelationFilterRetracted = "retracted";
    private const string RelationFilterNeedsReview = "needs review";

    /// <summary>
    /// The kinds, recorded states and display filters this view offers are the
    /// shared domain spellings: the owner picks the kind explicitly, and the
    /// service still validates whatever is sent and computes the review state.
    /// </summary>
    private void InitializeRelationControls()
    {
        foreach (var kind in new[]
                 {
                     "relatedTo", "supports", "derivedFrom", "contradicts", "supersedes", "appliesTo", "addresses"
                 })
        {
            RelationKindInput.Items.Add(kind);
        }

        foreach (var state in new[] { "accepted", "proposed" })
        {
            RelationStateInput.Items.Add(state);
        }

        foreach (var filter in new[]
                 {
                     RelationFilterAll, RelationFilterProposed, RelationFilterAccepted,
                     RelationFilterRetracted, RelationFilterNeedsReview
                 })
        {
            RelationsFilterInput.Items.Add(filter);
        }

        RelationKindInput.SelectedItem = "relatedTo";
        RelationStateInput.SelectedItem = "accepted";
        RelationsFilterInput.SelectedItem = RelationFilterAll;
    }

    /// <summary>
    /// Empties the relation form: the link draft, the picked endpoints, the
    /// typed source anchor and the candidate query. Always the whole draft, so
    /// no part of a finished epoch can be sent as if it belonged to this one.
    /// </summary>
    private void SyncRelationForm()
    {
        _relationSubject = null;
        _relationObject = null;
        RelationIdInput.Text = string.Empty;
        RelationZoneInput.Text = string.Empty;
        RelationReasonInput.Text = string.Empty;
        RelationRationaleInput.Text = string.Empty;
        RelationSourcePickerInput.ItemsSource = null;
        RelationSourcePickerInput.SelectedItem = null;
        RelationCandidateQueryInput.Text = string.Empty;
        RelationKindInput.SelectedItem = "relatedTo";
        RelationStateInput.SelectedItem = "accepted";
    }

    private async void OnRelationsRefreshClicked(object sender, RoutedEventArgs e) =>
        await _session.RefreshRelationsAsync();

    private async void OnRelationsInspectClicked(object sender, RoutedEventArgs e) =>
        await InspectSelectedRelationAsync();

    private void OnRelationsFilterChanged(object sender, SelectionChangedEventArgs e) => Render();

    private void OnRelationsSelectionChanged(object sender, SelectionChangedEventArgs e) => Render();

    private async void OnRelationsListKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter)
        {
            return;
        }

        e.Handled = true;
        await InspectSelectedRelationAsync();
    }

    private async Task InspectSelectedRelationAsync()
    {
        if (RelationsList.SelectedItem is RelationRow row)
        {
            await _session.OpenRelationAsync(row.Relation);
        }
    }

    private async void OnRelationConfirmClicked(object sender, RoutedEventArgs e) =>
        await _session.AcceptRelationAsync();

    private async void OnRelationRejectClicked(object sender, RoutedEventArgs e) =>
        await _session.RejectRelationAsync(RelationRationaleInput.Text);

    private async void OnRelationRemoveClicked(object sender, RoutedEventArgs e) =>
        await _session.UnlinkRelationAsync(RelationRationaleInput.Text);

    private async void OnRelationUndoClicked(object sender, RoutedEventArgs e) =>
        await _session.RelinkRelationAsync();

    private async void OnRelationCandidateSearchClicked(object sender, RoutedEventArgs e) =>
        await _session.SearchRelationCandidatesAsync(RelationCandidateQueryInput.Text);

    private async void OnRelationCandidateQueryKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter)
        {
            return;
        }

        e.Handled = true;
        await _session.SearchRelationCandidatesAsync(RelationCandidateQueryInput.Text);
    }

    private void OnRelationCandidateSelectionChanged(object sender, SelectionChangedEventArgs e) => Render();

    private void OnRelationCandidateKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter)
        {
            return;
        }

        e.Handled = true;
        SetEndpointFromCandidate(asSubject: true);
    }

    private void OnRelationSetSubjectClicked(object sender, RoutedEventArgs e) => SetEndpointFromCandidate(asSubject: true);

    private void OnRelationSetObjectClicked(object sender, RoutedEventArgs e) => SetEndpointFromCandidate(asSubject: false);

    private void OnRelationUseSourceSubjectClicked(object sender, RoutedEventArgs e) => SetEndpointFromTypedSource(asSubject: true);

    private void OnRelationUseSourceObjectClicked(object sender, RoutedEventArgs e) => SetEndpointFromTypedSource(asSubject: false);

    /// <summary>
    /// Pins one candidate the service's library search ranked. The candidate
    /// already carries the live revision and the zone the service reported, so
    /// the link's zone box is prefilled from it (and stays editable: the
    /// service validates the request, this console only proposes it).
    /// </summary>
    private void SetEndpointFromCandidate(bool asSubject)
    {
        if (RelationCandidates.SelectedItem is not OwnerConsoleSession.RelationEndpointPick pick)
        {
            return;
        }

        if (asSubject)
        {
            _relationSubject = pick;
            if (string.IsNullOrWhiteSpace(RelationZoneInput.Text))
            {
                RelationZoneInput.Text = pick.ZoneId;
            }
        }
        else
        {
            _relationObject = pick;
        }

        Render();
    }

    /// <summary>
    /// Pins a registered source the owner named. Kept for compatibility: the
    /// picker path reads the live revision from the service instead.
    /// </summary>
    private void SetEndpointFromTypedSource(bool asSubject)
    {
        _session.ReportRelationFormProblem(
            "Name no source by hand: read Sources, pick the enrolled source, and pin it — its live revision travels. Nothing was pinned.");
    }

    /// <summary>
    /// Pins the open library item as a relation endpoint with its live revision.
    /// </summary>
    private void OnRelationUseLibrarySubjectClicked(object sender, RoutedEventArgs e) => SetEndpointFromLibraryItem(asSubject: true);

    /// <summary>
    /// Pins the open library item as a relation endpoint with its live revision.
    /// </summary>
    private void OnRelationUseLibraryObjectClicked(object sender, RoutedEventArgs e) => SetEndpointFromLibraryItem(asSubject: false);

    private void SetEndpointFromLibraryItem(bool asSubject)
    {
        if (_session.LibraryItem is not { } item)
        {
            _session.ReportRelationFormProblem(
                "No library item is open: open one in Library first, then pick it here. Nothing was pinned.");
            return;
        }

        var pick = new OwnerConsoleSession.RelationEndpointPick(
            item.Id, "memoryItem", item.Revision, item.ZoneId,
            item.Id + "@" + item.Revision.ToString(CultureInfo.InvariantCulture) + "  zone=" + item.ZoneId + "  kind=" + item.Kind);
        if (asSubject)
        {
            _relationSubject = pick;
            if (string.IsNullOrWhiteSpace(RelationZoneInput.Text))
            {
                RelationZoneInput.Text = item.ZoneId;
            }
        }
        else
        {
            _relationObject = pick;
        }

        Render();
    }

    private async void OnRelationUsePickedSourceSubjectClicked(object sender, RoutedEventArgs e) =>
        await SetEndpointFromPickedSourceAsync(asSubject: true);

    private async void OnRelationUsePickedSourceObjectClicked(object sender, RoutedEventArgs e) =>
        await SetEndpointFromPickedSourceAsync(asSubject: false);

    /// <summary>
    /// Pins the enrolled source picked from the Sources answers at its live
    /// registry revision, read from the service now. The console never invents
    /// a revision: only a source the service serves now can be pinned.
    /// </summary>
    private async Task SetEndpointFromPickedSourceAsync(bool asSubject)
    {
        if (RelationSourcePickerInput.SelectedItem is not RelationSourceRow row)
        {
            _session.ReportRelationFormProblem(
                "No enrolled source is picked: read Sources first, then pick one here. Nothing was pinned.");
            return;
        }

        if (await _session.ResolveSourceEndpointAsync(row.Source.SourceId).ConfigureAwait(true) is not { } pick)
        {
            return;
        }

        if (asSubject)
        {
            _relationSubject = pick;
        }
        else
        {
            _relationObject = pick;
        }

        Render();
    }

    private async void OnRelationCreateClicked(object sender, RoutedEventArgs e)
    {
        if (_relationSubject is not { } subject || _relationObject is not { } target)
        {
            _session.ReportRelationFormProblem(
                "This console did not send the link: pick the subject endpoint and the object endpoint first. Nothing was written.");
            return;
        }

        var relationId = RelationIdInput.Text.Trim();
        var zoneId = RelationZoneInput.Text.Trim();
        var reason = RelationReasonInput.Text.Trim();
        if (relationId.Length == 0 || zoneId.Length == 0 || reason.Length == 0)
        {
            _session.ReportRelationFormProblem(
                "This console did not send the link: it needs the relation ID you name, the zone the link belongs to and the reason the shared validator requires. Nothing was written and your draft is unchanged.");
            return;
        }

        await _session.CreateRelationAsync(
            relationId,
            zoneId,
            RelationKindInput.SelectedItem as string ?? string.Empty,
            RelationStateInput.SelectedItem as string ?? string.Empty,
            subject,
            target,
            reason);
    }

    /// <summary>
    /// Renders the relation workbench from the service's answers only: the
    /// relation set, the opened link with its provenance and review state, and
    /// the endpoints the owner picked. When the vault is not unlocked no
    /// relation content is shown at all.
    /// </summary>
    private void RenderRelations(bool attached)
    {
        var state = _session.ReportedVaultState;
        var inFlight = _session.RelationsActInFlight;
        var unlocked = state == OwnerVaultState.Unlocked;

        RelationsStateText.Text = state switch
        {
            OwnerVaultState.Unlocked =>
                "Vault: unlocked — the service serves this vault's relation set. Each link pins the revisions it was made against, so a changed endpoint is reported instead of hidden.",
            OwnerVaultState.Locked =>
                "Vault: locked — no relation is available or shown. Unlock in Vault & key to read the relation set.",
            OwnerVaultState.Locking =>
                "Vault: locking — the service is draining private work; the relation view of this screen is cleared.",
            OwnerVaultState.Uninitialized =>
                "Vault: not provisioned — there is no vault and therefore no link.",
            _ => attached
                ? "Vault: unknown — no usable owner-channel answer, so no relation view is shown."
                : "Vault: not reported — this console holds no owner session."
        };

        var filter = RelationsFilterInput.SelectedItem as string ?? RelationFilterAll;
        if (!ReferenceEquals(_renderedRelations, _session.Relations) ||
            !string.Equals(_renderedRelationFilter, filter, StringComparison.Ordinal))
        {
            _renderedRelations = _session.Relations;
            _renderedRelationFilter = filter;
            var previousId = (RelationsList.SelectedItem as RelationRow)?.Relation.Id;
            var rows = _session.Relations
                .Where(relation => MatchesRelationFilter(relation, filter))
                .Select(relation => new RelationRow(relation))
                .ToArray();
            RelationsList.ItemsSource = rows;
            RelationsList.SelectedItem = previousId is null
                ? null
                : rows.FirstOrDefault(row => string.Equals(row.Relation.Id, previousId, StringComparison.Ordinal));
        }

        RenderOutcome(RelationsMessageText, _session.RelationsMessage ?? (unlocked
            ? "No relation set has been read in this session. Read it to see every link, including the proposals agents left for your review."
            : "No private relation act is offered while the service does not report the vault unlocked."));

        var open = _session.OpenRelation;
        RelationProvenanceText.Text = open is null
            ? "No relation is open. Read the relation set, choose a row and inspect it."
            : "id=" + open.Id + "  revision=" + open.Revision.ToString(CultureInfo.InvariantCulture) +
                "  zone=" + open.ZoneId + "  kind=" + open.Kind + "  state=" + open.State +
                "  executor=" + open.Executor + "  creation=" + open.CreationMethod +
                "  recorded=" + Stamp(open.RecordedAtUtc) + "  occurred=" + Stamp(open.OccurredAtUtc) +
                "  stateExecutor=" + open.StateExecutor + "  stateRecorded=" + Stamp(open.StateRecordedAtUtc) +
                "\nreason: " + open.Reason +
                (open.StateReason is null ? string.Empty : "\nstate reason: " + open.StateReason);
        RelationEndpointsText.Text = open is null
            ? string.Empty
            : "subject: " + DescribeRelationEndpoint(open.From) + "\nobject:  " + DescribeRelationEndpoint(open.To);
        RelationConfirmationText.Text = open is null ? string.Empty : DescribeRelationConfirmation(open);
        RelationEvidenceText.Text = open is null ? string.Empty : DescribeRelationEvidence(open);
        RelationReviewText.Text = open is null ? string.Empty : DescribeRelationReview(open);
        RenderOutcome(RelationMessageText, _session.RelationMessage ?? (unlocked
            ? "No relation act has been sent in this session. Confirming records your own conclusion; removing and rejecting record your rationale."
            : "No private relation act is offered while the service does not report the vault unlocked."));

        if (!ReferenceEquals(_renderedRelationCandidates, _session.RelationCandidates))
        {
            _renderedRelationCandidates = _session.RelationCandidates;
            RelationCandidates.ItemsSource = _session.RelationCandidates;
        }

        RelationCandidateMessageText.Text = _session.RelationCandidateMessage ?? (unlocked
            ? "No endpoint search has been sent in this session. The service's library index answers the search, and a registered source is named by ID and revision."
            : "No private endpoint search is offered while the service does not report the vault unlocked.");

        var selectedRelation = RelationsList.SelectedItem as RelationRow;
        var candidateSelected = RelationCandidates.SelectedItem is OwnerConsoleSession.RelationEndpointPick;
        RefreshRelationPickerOptions();
        var sourcePicked = RelationSourcePickerInput.SelectedItem is RelationSourceRow;

        RelationsRefreshButton.IsEnabled = unlocked && !inFlight;
        RelationsInspectButton.IsEnabled = unlocked && !inFlight && selectedRelation is not null;
        RelationsList.IsEnabled = unlocked && !inFlight;
        RelationsFilterInput.IsEnabled = !inFlight;
        RelationConfirmButton.IsEnabled = unlocked && !inFlight && open is { State: "proposed" };
        RelationRejectButton.IsEnabled = unlocked && !inFlight && open is { State: "proposed" or "accepted" };
        RelationRemoveButton.IsEnabled = unlocked && !inFlight && open is { State: "proposed" or "accepted" };
        RelationUndoButton.IsEnabled = unlocked && !inFlight && open is { State: "retracted" };
        RelationRationaleInput.IsEnabled = unlocked && !inFlight && open is { State: "proposed" or "accepted" };
        RelationCandidateSearchButton.IsEnabled = unlocked && !inFlight;
        RelationCandidateQueryInput.IsEnabled = unlocked && !inFlight;
        RelationCandidates.IsEnabled = unlocked && !inFlight;
        RelationSetSubjectButton.IsEnabled = unlocked && !inFlight && candidateSelected;
        RelationSetObjectButton.IsEnabled = unlocked && !inFlight && candidateSelected;
        RelationUseLibrarySubjectButton.IsEnabled = unlocked && !inFlight && _session.LibraryItem is not null;
        RelationUseLibraryObjectButton.IsEnabled = unlocked && !inFlight && _session.LibraryItem is not null;
        RelationSourcePickerInput.IsEnabled = unlocked && !inFlight;
        RelationUseSourceSubjectButton.IsEnabled = unlocked && !inFlight && sourcePicked;
        RelationUseSourceObjectButton.IsEnabled = unlocked && !inFlight && sourcePicked;
        RelationIdInput.IsEnabled = unlocked && !inFlight;
        RelationZoneInput.IsEnabled = unlocked && !inFlight;
        RelationKindInput.IsEnabled = unlocked && !inFlight;
        RelationStateInput.IsEnabled = unlocked && !inFlight;
        RelationReasonInput.IsEnabled = unlocked && !inFlight;
        RelationCreateButton.IsEnabled = unlocked && !inFlight;

        RelationSubjectText.Text = "subject: " + (_relationSubject is { } subject ? subject.Label : "(none picked yet)");
        RelationObjectText.Text = "object:  " + (_relationObject is { } target ? target.Label : "(none picked yet)");
    }
    /// <summary>
    /// Refreshes the zone and source pickers from the service answers in this
    /// session, keeping the owner's typed zone text.
    /// </summary>
    private void RefreshRelationPickerOptions()
    {
        var zoneText = RelationZoneInput.Text;
        RelationZoneInput.Items.Clear();
        foreach (var zone in _session.KnownZones())
        {
            RelationZoneInput.Items.Add(zone);
        }

        RelationZoneInput.Text = zoneText;
        var previousId = (RelationSourcePickerInput.SelectedItem as RelationSourceRow)?.Source.SourceId;
        var rows = _session.Sources
            .Where(source => source.Quarantine is null || !string.Equals(source.Quarantine.Reason, "mapping_unknown", StringComparison.Ordinal))
            .Select(source => new RelationSourceRow(source)).ToArray();
        RelationSourcePickerInput.ItemsSource = rows;
        RelationSourcePickerInput.SelectedItem = previousId is null
            ? RelationSourcePickerInput.SelectedItem
            : rows.FirstOrDefault(row => string.Equals(row.Source.SourceId, previousId, StringComparison.Ordinal));
    }

    /// <summary>
    /// One enrolled source as one pinnable endpoint row. The live registry
    /// revision is read when the owner pins it, so the row shows the identity
    /// the service listed, never a remembered number.
    /// </summary>
    private sealed record RelationSourceRow(OwnerSourceEnrollment Source)
    {
        public override string ToString() =>
            Source.SourceId + "  zone=" + Source.ZoneId + "  life=" + Source.Life;
    }

    private static bool MatchesRelationFilter(OwnerRelation relation, string filter) => filter switch
    {
        RelationFilterProposed => string.Equals(relation.State, "proposed", StringComparison.Ordinal),
        RelationFilterAccepted => string.Equals(relation.State, "accepted", StringComparison.Ordinal),
        RelationFilterRetracted => string.Equals(relation.State, "retracted", StringComparison.Ordinal),
        RelationFilterNeedsReview => string.Equals(relation.Review.State, "needsReview", StringComparison.Ordinal),
        _ => true
    };

    private static string DescribeRelationEndpoint(OwnerRelationEndpoint endpoint)
    {
        var span = endpoint.SpanStart is { } start && endpoint.SpanEnd is { } end
            ? "  span=" + start.ToString(CultureInfo.InvariantCulture) + "-" + end.ToString(CultureInfo.InvariantCulture)
            : "  span=none";
        return endpoint.ResourceId + "@" + endpoint.Revision.ToString(CultureInfo.InvariantCulture) +
            "  endpointKind=" + endpoint.EndpointKind + span;
    }

    /// <summary>
    /// Who recorded the link, who last changed its state and what that means.
    /// An agent's proposed link is never rendered as the owner's confirmation.
    /// </summary>
    private static string DescribeRelationConfirmation(OwnerRelation relation)
    {
        var byAgent = string.Equals(relation.Executor, "agent", StringComparison.Ordinal);
        var stateExecutor = relation.StateExecutor;
        return relation.State switch
        {
            "proposed" when byAgent =>
                "NOT CONFIRMED BY YOU: this link was recorded by an agent session (executor=agent), so the reported words below are that caller's claim, never your confirmation. Confirm link records your own conclusion; Reject link records your decision instead.",
            "proposed" =>
                "PROPOSED: the link is stored as a proposal (executor=" + relation.Executor + "). Confirming it records your own conclusion.",
            "accepted" when byAgent && string.Equals(stateExecutor, "owner", StringComparison.Ordinal) =>
                "CONFIRMED BY YOU (owner): the link was proposed by an agent session and you accepted it. The acceptance is recorded as your act and the agent's claim below stays unchanged.",
            "accepted" =>
                "ACCEPTED by " + stateExecutor + " (recorded by executor=" + relation.Executor + "). Acceptance records the link as a conclusion of " + stateExecutor + "; it never asserts that the linked statement is true.",
            "rejected" =>
                "REJECTED by " + stateExecutor + ": your review decision is recorded and the link is not asserted.",
            "retracted" =>
                "RETRACTED by " + stateExecutor + ": the link is removed, not deleted. Undo removal returns it to proposed so you confirm it explicitly again.",
            _ => "state=" + relation.State + " (state executor=" + stateExecutor + ")"
        };
    }

    private static string DescribeRelationEvidence(OwnerRelation relation) =>
        relation.RequestEvidence.Length == 0
            ? "caller-reported words: none on this link"
            : "caller-reported words (a claim of the caller, never your confirmation):\n" +
                string.Join("\n", relation.RequestEvidence.Select(entry =>
                    "  " + entry.Action + " by executor=" + entry.Executor + " at " +
                    entry.RecordedAtUtc.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture) +
                    ": \"" + entry.Text + "\""));

    /// <summary>
    /// The service's computed review state in the owner's words: a current link
    /// and a link that needs review are different answers, and the reasons come
    /// from the service, never from this console's reading of the revisions.
    /// </summary>
    private static string DescribeRelationReview(OwnerRelation relation)
    {
        var review = relation.Review;
        if (string.Equals(review.State, "current", StringComparison.Ordinal))
        {
            return "Review state: current — the service found no missing endpoint, no changed pinned revision and no unavailable cited source for this link.";
        }

        var reasons = review.Reasons.Length == 0 ? "(reason not named)" : string.Join(", ", review.Reasons);
        var stale = review.StaleResourceIds.Length == 0 ? "(none named to you)" : string.Join(", ", review.StaleResourceIds);
        return "NEEDS REVIEW: the service flagged this link (" + reasons + "). Endpoint(s) it names to you: " + stale +
            ". A changed revision means the link pinned evidence that has since moved; the stored link is not rewritten, so the record stays the truthful history — read the named endpoint and remove or re-link it if it no longer holds.";
    }

    /// <summary>One relation exactly as the service listed it, rendered as one line.</summary>
    private sealed record RelationRow(OwnerRelation Relation)
    {
        public override string ToString()
        {
            var review = string.Equals(Relation.Review.State, "needsReview", StringComparison.Ordinal)
                ? "  [NEEDS REVIEW:" + string.Join("/", Relation.Review.Reasons) + "]"
                : string.Empty;
            return Relation.Kind + "  " + Relation.From.ResourceId + "@" + Relation.From.Revision.ToString(CultureInfo.InvariantCulture) +
                " -> " + Relation.To.ResourceId + "@" + Relation.To.Revision.ToString(CultureInfo.InvariantCulture) +
                "  state=" + Relation.State + "  executor=" + Relation.Executor +
                "  id=" + Relation.Id + "  rev=" + Relation.Revision.ToString(CultureInfo.InvariantCulture) +
                "  review=" + Relation.Review.State + review;
        }
    }

    private static string Stamp(DateTimeOffset? value) =>
        value is { } instant
            ? instant.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture)
            : "(none)";

    private static string ShortDigest(string digest) => digest.Length <= 12 ? digest : digest[..12];

    /// <summary>One pending proposal exactly as the service listed it, rendered as one line.</summary>
    private sealed record ProposalRow(OwnerProposalIdentity Identity)
    {
        public override string ToString() =>
            "proposal " + Identity.Id + "  rev=" + Identity.Revision.ToString(CultureInfo.InvariantCulture) +
            "  zone=" + Identity.ZoneId;
    }

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
                "  |  recall=" + Hit.RecallState +
                (string.IsNullOrWhiteSpace(Hit.RankingReason) ? string.Empty : " (" + Hit.RankingReason + ")") +
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

    /// <summary>
    /// One catalog mapping exactly as the service listed it, rendered as one line.
    /// </summary>
    private sealed record CatalogRow(OwnerCatalogMapping Entry)
    {
        public override string ToString() =>
            "catalog " + Entry.CatalogId + "  label=" + Entry.Label +
            "  resource=" + Entry.ResourceId + "@" + Entry.Revision.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// One access request exactly as the service listed it, rendered as one line.
    /// </summary>
    private sealed record AccessRequestRow(OwnerAccessRequestInfo Request)
    {
        public override string ToString() =>
            "request " + Request.RequestId + "  status=" + Request.Status +
            "  purpose=" + Request.Purpose +
            "  catalogs=" + (Request.CatalogIds.Length == 0 ? "(none)" : string.Join(",", Request.CatalogIds)) +
            "  operations=" + (Request.RequestedOperations.Length == 0 ? "(none)" : string.Join(",", Request.RequestedOperations));
    }

    /// <summary>
    /// One live session exactly as the service listed it, rendered as one line.
    /// </summary>
    private sealed record AgentSessionRow(OwnerAgentSessionInfo Session)
    {
        public override string ToString() =>
            "session " + Session.SessionId + "  grant=" + Session.GrantId +
            "  expires=" + Session.ExpiresAtUtc.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture) +
            "  operations=" + (Session.Operations.Length == 0 ? "(none)" : string.Join(",", Session.Operations));
    }
    /// <summary>
    /// One bound-preview resource as one tickable approval row. The ID stays on
    /// the row as secondary copyable detail, never as something to memorize.
    /// </summary>
    private sealed record ApprovalResourceRow(OwnerScopeResource Resource)
    {
        public override string ToString() =>
            Resource.ResourceId + "  zone=" + Resource.ZoneId +
            "  revision=" + Resource.Revision.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>One bound-preview operation as one tickable approval row.</summary>
    private sealed record ApprovalOperationRow(string Operation, string Label)
    {
        public override string ToString() => Label;
    }
}
