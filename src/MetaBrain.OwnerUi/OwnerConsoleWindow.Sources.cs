using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using MetaBrain.Connections;

namespace MetaBrain.OwnerUi;

/// <summary>
/// Sources and organization view of the owner console (S5-T7/S11-T5): the
/// enrolled allowlist, owner-selected folder preview/enrollment and consent,
/// held mapping review, pause/resume, source status, refresh, collections and
/// explicit snapshot preservation with retention. Every act is one request to
/// the running service; no locator or transcript is echoed.
/// </summary>
public partial class OwnerConsoleWindow : System.Windows.Window
{
    private readonly List<CollectionMember> _collectionMembers = new();
    private OwnerSourceFolderPreview? _renderedFolderPreview;
    private OwnerLibraryHit[]? _renderedCollectionCandidates;

    /// <summary>
    /// Empties the sources drafts: folder selection/confirmation, mapping
    /// review, collection and snapshot forms. Always the whole draft, so no
    /// part of a finished epoch can be sent as if it belonged to this one.
    /// </summary>
    private void SyncSourcesForm()
    {
        EnrollSourceIdInput.Text = string.Empty;
        EnrollZoneInput.Text = string.Empty;
        EnrollFormatInput.Text = string.Empty;
        EnrollFormatVersionInput.Text = "1";
        EnrollLocatorInput.Text = string.Empty;
        MappingSourceIdInput.Text = string.Empty;
        MappingLocatorInput.Text = string.Empty;
        MappingZoneInput.Text = string.Empty;
        MappingFormatInput.Text = string.Empty;
        MappingFormatVersionInput.Text = string.Empty;
        CollectionIdInput.Text = string.Empty;
        _collectionMembers.Clear();
        _renderedCollectionCandidates = null;
        RefreshCollectionMemberList();
        CollectionMemberSearchInput.Text = string.Empty;
        CollectionMemberCandidatesList.ItemsSource = null;
        SnapshotSourceIdInput.Text = string.Empty;
        SnapshotSourceRevisionInput.Text = string.Empty;
        SnapshotReferenceLocatorInput.Text = string.Empty;
        SnapshotRetainInput.Text = string.Empty;
        FolderRootInput.Clear();
        FolderZoneInput.Text = string.Empty;
        FolderFormatInput.Text = string.Empty;
        FolderFormatVersionInput.Text = "1";
        FolderRecursiveCheck.IsChecked = false;
        FolderIncludeGlobsInput.Clear();
        FolderExcludeGlobsInput.Clear();
        FolderRawPreservationCheck.IsChecked = false;
        FolderRawPreservationUntilInput.Clear();
        FolderConfirmationInput.Clear();
        FolderPreviewList.ItemsSource = null;
        FolderPreviewSummaryText.Text = string.Empty;
        FolderMessageText.Text = string.Empty;
        FolderStateText.Text = string.Empty;
        SourceRawPreservationUntilInput.Clear();
        _renderedFolderPreview = null;
    }
    /// <summary>
    /// The source formats the shipped service understands. Anything else is the
    /// service's explicit unsupported diagnostic, never a silent best effort.
    /// </summary>
    private static readonly string[] KnownSourceFormats =
    {
        "codex.session.jsonl",
        "omp.session.jsonl",
    };

    private void InitializeSourcesControls()
    {
        foreach (var format in KnownSourceFormats)
        {
            EnrollFormatInput.Items.Add(format);
            FolderFormatInput.Items.Add(format);
        }

        EnrollFormatVersionInput.Text = "1";
        InitializeSnapshotControls();
        FolderFormatVersionInput.Text = "1";
        RefreshCollectionMemberList();
        FolderConfirmationInput.TextChanged += OnFolderConfirmationChanged;
    }
    private void RefreshEnrollPickerOptions()
    {
        var zoneText = EnrollZoneInput.Text;
        EnrollZoneInput.Items.Clear();
        foreach (var zone in _session.KnownZones())
        {
            EnrollZoneInput.Items.Add(zone);
        }

        EnrollZoneInput.Text = zoneText;
        var folderZoneText = FolderZoneInput.Text;
        FolderZoneInput.Items.Clear();
        foreach (var zone in _session.KnownZones())
        {
            FolderZoneInput.Items.Add(zone);
        }

        FolderZoneInput.Text = folderZoneText;
        RefreshSnapshotPickerOptions();
    }

    private void OnEnrollBrowseClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose the source file to enroll (the service reads exactly this file)",
            CheckFileExists = true,
            CheckPathExists = true,
        };
        if (dialog.ShowDialog() == true)
        {
            EnrollLocatorInput.Text = dialog.FileName;
        }

        Render();
    }

    private void OnFolderBrowseClicked(object sender, RoutedEventArgs e)
    {
        if (_filePicker.PickFolder("Choose the one folder to preview for source enrollment") is { } folder &&
            folder.Length > 0)
        {
            FolderRootInput.Text = folder;
        }

        Render();
    }

    private async void OnFolderPreviewClicked(object sender, RoutedEventArgs e)
    {
        var root = FolderRootInput.Text.Trim();
        var zone = FolderZoneInput.Text.Trim();
        var format = FolderFormatInput.Text.Trim();
        if (root.Length == 0 || !Path.IsPathRooted(root) || zone.Length == 0 || format.Length == 0)
        {
            _session.ReportFolderFormProblem(
                "Preview needs one selected absolute folder, an explicit zone and a declared format. Nothing was enrolled or read.");
            return;
        }

        if (!int.TryParse(FolderFormatVersionInput.Text.Trim(), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var formatVersion) || formatVersion < 1)
        {
            _session.ReportFolderFormProblem("Folder preview needs a positive format version. Nothing was enrolled or read.");
            return;
        }

        if (!TryParseFolderPatterns(FolderIncludeGlobsInput.Text, required: true, out var includes) ||
            !TryParseFolderPatterns(FolderExcludeGlobsInput.Text, required: false, out var excludes))
        {
            _session.ReportFolderFormProblem(
                "Include globs are required and include/exclude lists must be comma-separated non-empty relative patterns. Nothing was enrolled or read.");
            return;
        }

        DateTimeOffset? rawPreservationRetainUntilUtc = null;
        if (FolderRawPreservationCheck.IsChecked == true)
        {
            if (!TryParseExplicitUtc(FolderRawPreservationUntilInput.Text.Trim(), out var retainUntil))
            {
                _session.ReportFolderFormProblem(
                    "Raw-byte preservation consent needs a future ISO-8601 UTC deadline ending in Z or +00:00. Nothing was enrolled or preserved.");
                return;
            }

            rawPreservationRetainUntilUtc = retainUntil;
        }

        var previewTask = _session.PreviewSourceFolderAsync(
            root, zone, format, formatVersion, FolderRecursiveCheck.IsChecked == true,
            includes, excludes, rawPreservationRetainUntilUtc);
        FolderRootInput.Clear();
        FolderConfirmationInput.Clear();
        await previewTask;
    }

    private void OnFolderDiscardPreviewClicked(object sender, RoutedEventArgs e)
    {
        FolderConfirmationInput.Clear();
        _session.DiscardSourceFolderPreview();
    }

    private async void OnFolderEnrollConfirmClicked(object sender, RoutedEventArgs e)
    {
        var confirmation = FolderConfirmationInput.Text;
        if (!string.Equals(confirmation, "ENROLL", StringComparison.Ordinal))
        {
            _session.ReportFolderFormProblem(
                "Type ENROLL exactly to confirm the active folder preview. Nothing changed.");
            return;
        }

        await _session.EnrollSourceFolderAsync(confirmation);
        FolderConfirmationInput.Clear();
        if (_session.FolderPreview is null && _session.FolderEnrollment is not null)
        {
            FolderRawPreservationCheck.IsChecked = false;
            FolderRawPreservationUntilInput.Clear();
        }
    }

    private void OnFolderConfirmationChanged(object sender, TextChangedEventArgs e)
    {
        FolderEnrollConfirmButton.IsEnabled =
            _session.ReportedVaultState == OwnerVaultState.Unlocked &&
            !_session.SourcesActInFlight &&
            _session.FolderPreview is not null &&
            string.Equals(FolderConfirmationInput.Text, "ENROLL", StringComparison.Ordinal);
    }

    private async void OnFolderRefreshOptInClicked(object sender, RoutedEventArgs e)
    {
        if (SourcesList.SelectedItem is SourceRow row)
        {
            await _session.SetFolderRefreshOptInAsync(row.Source, optIn: true);
        }
    }

    private async void OnFolderRefreshOptOutClicked(object sender, RoutedEventArgs e)
    {
        if (SourcesList.SelectedItem is SourceRow row)
        {
            await _session.SetFolderRefreshOptInAsync(row.Source, optIn: false);
        }
    }

    private async void OnSourceRawPreservationOptInClicked(object sender, RoutedEventArgs e)
    {
        if (SourcesList.SelectedItem is not SourceRow row)
        {
            return;
        }

        if (!TryParseExplicitUtc(SourceRawPreservationUntilInput.Text.Trim(), out var retainUntil))
        {
            _session.ReportSourcesFormProblem(
                "Raw-byte preservation consent needs a future ISO-8601 UTC deadline ending in Z or +00:00. Nothing changed.");
            return;
        }

        await _session.SetSourceRawPreservationOptInAsync(
            row.Source, optIn: true, retainUntilUtc: retainUntil);
        SourceRawPreservationUntilInput.Clear();
    }

    private async void OnSourceRawPreservationOptOutClicked(object sender, RoutedEventArgs e)
    {
        if (SourcesList.SelectedItem is SourceRow row)
        {
            await _session.SetSourceRawPreservationOptInAsync(row.Source, optIn: false, retainUntilUtc: null);
            SourceRawPreservationUntilInput.Clear();
        }
    }

    private static bool TryParseFolderPatterns(string value, bool required, out string[] patterns)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            patterns = Array.Empty<string>();
            return !required;
        }

        patterns = value.Split(',', StringSplitOptions.TrimEntries);
        return patterns.Length > 0 && patterns.All(pattern => !string.IsNullOrWhiteSpace(pattern));
    }

    private async void OnEnrollClicked(object sender, RoutedEventArgs e)
    {
        var sourceId = EnrollSourceIdInput.Text.Trim();
        var zone = EnrollZoneInput.Text.Trim();
        var locator = EnrollLocatorInput.Text.Trim();
        var format = EnrollFormatInput.Text.Trim();
        var versionText = EnrollFormatVersionInput.Text.Trim();
        if (sourceId.Length == 0 || zone.Length == 0 || locator.Length == 0 || format.Length == 0)
        {
            _session.ReportSourcesFormProblem(
                "Enrolling needs the source ID you name, the zone it belongs to, the file on this machine, and the format that file speaks. Nothing was enrolled and no transcript was opened.");
            return;
        }

        if (!int.TryParse(versionText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var version) || version < 1)
        {
            _session.ReportSourcesFormProblem(
                "Enrolling needs a format version that is a positive integer. Nothing was enrolled and no transcript was opened.");
            return;
        }

        if (!Path.IsPathRooted(locator))
        {
            _session.ReportSourcesFormProblem(
                "Enrolling needs the full path of the file on this machine (for example C:\\data\\session.jsonl). Nothing was enrolled and no transcript was opened.");
            return;
        }

        await _session.EnrollSourceAsync(sourceId, zone, locator, format, version);
        // The locator travels only in the request just sent: clear the field
        // so no later capture shows the owner's private path.
        EnrollLocatorInput.Text = string.Empty;
    }

    private async void OnSourceRevokeClicked(object sender, RoutedEventArgs e)
    {
        if (SourcesList.SelectedItem is SourceRow row)
        {
            await _session.RevokeSourceAsync(row.Source);
        }
        else
        {
            _session.ReportSourcesFormProblem(
                "Revoking needs the enrolled source to revoke: choose its row first. Nothing changed.");
        }
    }

    private async void OnSourceRelocateClicked(object sender, RoutedEventArgs e)
    {
        if (SourcesList.SelectedItem is not SourceRow row)
        {
            _session.ReportSourcesFormProblem(
                "Relocating needs the enrolled source to move: choose its row first. Nothing changed.");
            return;
        }

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose the replacement file for " + row.Source.SourceId + " (bytes must match the enrolled generation)",
            CheckFileExists = true,
            CheckPathExists = true,
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        await _session.RelocateSourceAsync(row.Source, dialog.FileName);
    }

    private async void OnSourcesRefreshClicked(object sender, RoutedEventArgs e)
    {
        await _session.RefreshSourcesAsync().ConfigureAwait(true);
        await _session.RefreshCollectionsAsync().ConfigureAwait(true);
    }

    private async void OnSourceOpenClicked(object sender, RoutedEventArgs e) =>
        await OpenSelectedSourceAsync();

    private void OnSourcesSelectionChanged(object sender, SelectionChangedEventArgs e) => Render();

    private async void OnSourcesListKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter)
        {
            return;
        }

        e.Handled = true;
        await OpenSelectedSourceAsync();
    }

    private async Task OpenSelectedSourceAsync()
    {
        if (SourcesList.SelectedItem is SourceRow row)
        {
            await _session.OpenSourceAsync(row.Source);
        }
    }

    private async void OnSourceIngestClicked(object sender, RoutedEventArgs e)
    {
        if (SourcesList.SelectedItem is SourceRow row)
        {
            await _session.IngestSourceAsync(row.Source);
        }
    }

    private async void OnSourcePauseClicked(object sender, RoutedEventArgs e)
    {
        if (SourcesList.SelectedItem is SourceRow row)
        {
            await _session.PauseSourceAsync(row.Source);
        }
    }

    private async void OnSourceResumeClicked(object sender, RoutedEventArgs e)
    {
        if (SourcesList.SelectedItem is SourceRow row)
        {
            await _session.ResumeSourceAsync(row.Source);
        }
    }

    private async void OnSourceOptInClicked(object sender, RoutedEventArgs e)
    {
        if (SourcesList.SelectedItem is SourceRow row)
        {
            await _session.SetSourceRefreshOptInAsync(row.Source, optIn: true);
        }
    }

    private async void OnSourceOptOutClicked(object sender, RoutedEventArgs e)
    {
        if (SourcesList.SelectedItem is SourceRow row)
        {
            await _session.SetSourceRefreshOptInAsync(row.Source, optIn: false);
        }
    }

    private async void OnSourceSnapshotsClicked(object sender, RoutedEventArgs e)
    {
        if (SourcesList.SelectedItem is SourceRow row)
        {
            await _session.ListSnapshotsAsync(row.Source);
        }
    }

    private async void OnMappingSubmitClicked(object sender, RoutedEventArgs e)
    {
        var sourceId = MappingSourceIdInput.Text.Trim();
        var locator = MappingLocatorInput.Text.Trim();
        var zone = MappingZoneInput.Text.Trim();
        var format = MappingFormatInput.Text.Trim();
        var formatVersionText = MappingFormatVersionInput.Text.Trim();
        if (sourceId.Length == 0 || locator.Length == 0)
        {
            _session.ReportSourcesFormProblem(
                "This console held nothing for review: it needs the source ID you name and the locator file of the source whose mapping is unresolved. Nothing was stored and no transcript was opened.");
            return;
        }

        int? formatVersion = null;
        if (formatVersionText.Length > 0)
        {
            if (!int.TryParse(formatVersionText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed < 1)
            {
                _session.ReportSourcesFormProblem(
                    "This console held nothing for review: the format version is not a positive integer. Nothing was stored and no transcript was opened.");
                return;
            }

            formatVersion = parsed;
        }

        if (format.Length == 0 && formatVersion is not null)
        {
            _session.ReportSourcesFormProblem(
                "This console held nothing for review: a format version without a format names no mapping. Nothing was stored and no transcript was opened.");
            return;
        }

        await _session.SubmitMappingUnknownAsync(
            sourceId, locator,
            zone.Length == 0 ? null : zone,
            format.Length == 0 ? null : format,
            formatVersion);
        // The locator travels only in the request just sent: clear the field
        // so no later capture shows the owner's private path.
        MappingLocatorInput.Text = string.Empty;
    }

    private async void OnMappingWithdrawClicked(object sender, RoutedEventArgs e)
    {
        if (SourcesList.SelectedItem is SourceRow row && IsMappingUnknownRow(row))
        {
            await _session.WithdrawMappingUnknownAsync(row.Source);
        }
        else
        {
            _session.ReportSourcesFormProblem(
                "This console withdrew nothing: choose the held mapping-unknown row to withdraw first. Nothing changed.");
        }
    }

    private async void OnSourcesRefreshPassClicked(object sender, RoutedEventArgs e) =>
        await _session.RefreshSourcesPassAsync();

    private async void OnCollectionsRefreshClicked(object sender, RoutedEventArgs e) =>
        await _session.RefreshCollectionsAsync();

    private void OnCollectionsSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CollectionsList.SelectedItem is CollectionRow row)
        {
            CollectionIdInput.Text = row.Collection.CollectionId;
            _collectionMembers.Clear();
            foreach (var member in row.Collection.ResourceIds)
            {
                _collectionMembers.Add(new CollectionMember(member, null));
            }

            RefreshCollectionMemberList();
        }

        Render();
    }

    private async void OnCollectionsListKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter)
        {
            return;
        }

        e.Handled = true;
        Render();
    }

    private void OnCollectionNewClicked(object sender, RoutedEventArgs e)
    {
        CollectionsList.SelectedItem = null;
        CollectionIdInput.Clear();
        CollectionIdInput.Focus();
        _collectionMembers.Clear();
        RefreshCollectionMemberList();
        Render();
    }

    private void OnCollectionMemberSelectionChanged(object sender, SelectionChangedEventArgs e) => Render();

    private async void OnCollectionMemberSearchClicked(object sender, RoutedEventArgs e) =>
        await _session.SearchCollectionMembersAsync(CollectionMemberSearchInput.Text);

    private void OnCollectionMemberAddSelectedClicked(object sender, RoutedEventArgs e)
    {
        var added = 0;
        foreach (var row in CollectionMemberCandidatesList.SelectedItems.OfType<LibraryResultRow>())
        {
            if (AddCollectionMember(row.Hit.Id, row.Hit.Title))
            {
                added++;
            }
        }

        RefreshCollectionMemberList();
        if (added == 0)
        {
            _session.ReportSourcesFormProblem(
                "No member was added: choose one or more search results first (or the item is already in this collection). Nothing was saved.");
        }

        Render();
    }

    private void OnCollectionMemberAddOpenItemClicked(object sender, RoutedEventArgs e)
    {
        if (_session.LibraryItem is not { } item)
        {
            _session.ReportSourcesFormProblem(
                "No library item is open: open one in Library first, then add it here. Nothing was added.");
            return;
        }

        AddCollectionMember(item.Id, item.Title);
        RefreshCollectionMemberList();
        Render();
    }

    private void OnCollectionMemberRemoveClicked(object sender, RoutedEventArgs e)
    {
        var selectedIds = CollectionMembersList.SelectedItems
            .OfType<CollectionMember>()
            .Select(member => member.ResourceId)
            .ToHashSet(StringComparer.Ordinal);
        if (selectedIds.Count == 0)
        {
            _session.ReportSourcesFormProblem(
                "No member was removed: choose the member row(s) to remove first. Nothing was saved.");
            return;
        }

        _collectionMembers.RemoveAll(member => selectedIds.Contains(member.ResourceId));
        RefreshCollectionMemberList();
        Render();
    }

    private bool AddCollectionMember(string resourceId, string? title)
    {
        if (_collectionMembers.Any(member => string.Equals(member.ResourceId, resourceId, StringComparison.Ordinal)))
        {
            return false;
        }

        _collectionMembers.Add(new CollectionMember(resourceId, title));
        return true;
    }

    private void RefreshCollectionMemberList()
    {
        CollectionMembersList.ItemsSource = null;
        CollectionMembersList.ItemsSource = _collectionMembers.ToArray();
        CollectionMembersSummaryText.Text = _collectionMembers.Count == 0
            ? "No members chosen yet. Search the library or add the open item; a collection needs at least one member."
            : _collectionMembers.Count.ToString(CultureInfo.InvariantCulture) +
                " member(s) in this draft. Each line names the resource the service will resolve on save; the ID is shown for copying, not for typing.";
    }

    private async void OnCollectionSaveClicked(object sender, RoutedEventArgs e)
    {
        var collectionId = CollectionIdInput.Text.Trim();
        if (collectionId.Length == 0)
        {
            _session.ReportSourcesFormProblem(
                "This console saved no collection: name the collection first. Nothing was saved and no grant widened.");
            return;
        }

        if (_collectionMembers.Count == 0)
        {
            _session.ReportSourcesFormProblem(
                "This console saved no collection: add at least one library item as a member. Nothing was saved and no grant widened.");
            return;
        }

        await _session.SetCollectionAsync(collectionId, _collectionMembers.Select(member => member.ResourceId).ToArray());
    }

    private async void OnSnapshotsSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        await OpenSelectedSnapshotAsync();

    private async void OnSnapshotsListKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter)
        {
            return;
        }

        e.Handled = true;
        await OpenSelectedSnapshotAsync();
    }

    private async Task OpenSelectedSnapshotAsync()
    {
        // The selected row carries only the listed identity and revision: the
        // live read rechecks that revision on the service, so a moved
        // revision reports its conflict instead of opening a pinned copy.
        if (SnapshotsList.SelectedItem is SnapshotRow row)
        {
            await _session.OpenSnapshotAsync(row.Descriptor);
        }
        else
        {
            Render();
        }
    }

    private async Task RefreshSourcesForKeyboardAsync()
    {
        // The F5 keyboard act re-reads exactly what the Read buttons read:
        // the allowlist and the collections, through the same session acts.
        await _session.RefreshSourcesAsync().ConfigureAwait(true);
        await _session.RefreshCollectionsAsync().ConfigureAwait(true);
    }

    private async void OnSnapshotReferenceRegisterClicked(object sender, RoutedEventArgs e)
    {
        if (SourcesList.SelectedItem is not SourceRow row)
        {
            _session.ReportSnapshotFormProblem(
                "This console registered nothing: choose the enrolled source whose preservation reference to register first. Nothing changed.");
            return;
        }

        var locator = SnapshotReferenceLocatorInput.Text.Trim();
        if (locator.Length == 0)
        {
            _session.ReportSnapshotFormProblem(
                "This console registered nothing: it needs the enrolled locator file of the selected source, exactly as it was enrolled. Nothing was stored and no transcript was opened.");
            return;
        }

        await _session.RegisterSourceReferenceAsync(row.Source, locator);
        // The locator travels only in the request just sent: clear the field
        // so no later capture shows the owner's private path.
        SnapshotReferenceLocatorInput.Text = string.Empty;
    }
    private const string SnapshotRetainCustomItem = "Custom…";

    private static readonly (string Label, TimeSpan Span)[] SnapshotRetainPresets =
    {
        ("30 days", TimeSpan.FromDays(30)),
        ("90 days", TimeSpan.FromDays(90)),
        ("1 year", TimeSpan.FromDays(365)),
    };

    private void InitializeSnapshotControls()
    {
        SnapshotRetainPresetInput.Items.Add(SnapshotRetainCustomItem);
        foreach (var preset in SnapshotRetainPresets)
        {
            SnapshotRetainPresetInput.Items.Add(preset.Label);
        }

        SnapshotRetainPresetInput.SelectedItem = SnapshotRetainCustomItem;
    }

    private void OnSnapshotRetainPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SnapshotRetainPresetInput.SelectedItem is not string label)
        {
            return;
        }

        foreach (var preset in SnapshotRetainPresets)
        {
            if (string.Equals(preset.Label, label, StringComparison.Ordinal))
            {
                var instant = DateTimeOffset.UtcNow + preset.Span;
                SnapshotRetainInput.Text = instant.ToString("yyyy-MM-ddTHH:mm:ss\\Z", CultureInfo.InvariantCulture);
                break;
            }
        }

        Render();
    }

    /// <summary>
    /// Fills the preserve form from the selected enrolled source and its
    /// reference revision. No ID or revision to remember.
    /// </summary>
    private void OnSnapshotUseSelectedClicked(object sender, RoutedEventArgs e)
    {
        if (SourcesList.SelectedItem is not SourceRow row)
        {
            _session.ReportSnapshotFormProblem(
                "Filling the preserve form needs the enrolled source: choose its row first. Nothing was filled in.");
            return;
        }

        SnapshotSourceIdInput.Text = row.Source.SourceId;
        SnapshotSourceRevisionInput.Text = (row.Source.SourceReferenceRevision ?? 1).ToString(CultureInfo.InvariantCulture);
        Render();
    }

    /// <summary>
    /// Refreshes the snapshot source picker from the enrolled sources in this
    /// session, keeping the owner's typed text.
    /// </summary>
    private void RefreshSnapshotPickerOptions()
    {
        var text = SnapshotSourceIdInput.Text;
        SnapshotSourceIdInput.Items.Clear();
        foreach (var source in _session.Sources)
        {
            SnapshotSourceIdInput.Items.Add(source.SourceId);
        }

        SnapshotSourceIdInput.Text = text;
    }

    private async void OnSnapshotPreserveClicked(object sender, RoutedEventArgs e)
    {
        var sourceId = SnapshotSourceIdInput.Text.Trim();
        var revisionText = SnapshotSourceRevisionInput.Text.Trim();
        var retainText = SnapshotRetainInput.Text.Trim();
        if (sourceId.Length == 0 || revisionText.Length == 0 || retainText.Length == 0)
        {
            _session.ReportSnapshotFormProblem(
                "This console preserved nothing: it needs the source ID, its reference revision and an explicit future UTC retention. Nothing was preserved.");
            return;
        }

        if (!long.TryParse(revisionText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var revision) || revision < 1)
        {
            _session.ReportSnapshotFormProblem(
                "This console preserved nothing: the source revision is not a positive integer. Nothing was preserved.");
            return;
        }

        if (!TryParseExplicitUtc(retainText, out var retainUntilUtc))
        {
            _session.ReportSnapshotFormProblem(
                "This console preserved nothing: the retention must be an explicit future UTC instant (ISO 8601 with a Z or +00:00 suffix). Nothing was preserved.");
            return;
        }

        await _session.PreserveSnapshotAsync(sourceId, revision, retainUntilUtc);
    }

    private async void OnSnapshotRetainClicked(object sender, RoutedEventArgs e)
    {
        if (SnapshotsList.SelectedItem is not SnapshotRow row)
        {
            _session.ReportSnapshotFormProblem(
                "This console extended nothing: choose the preserved snapshot to extend first. Nothing changed.");
            return;
        }

        var retainText = SnapshotRetainInput.Text.Trim();
        if (!TryParseExplicitUtc(retainText, out var retainUntilUtc))
        {
            _session.ReportSnapshotFormProblem(
                "This console extended nothing: the retention must be an explicit future UTC instant (ISO 8601 with a Z or +00:00 suffix). Nothing changed.");
            return;
        }

        // The selected row may carry an older listed revision: open the live
        // snapshot first so the retain carries the revision the service
        // serves now. A moved revision reports its conflict with its
        // remediation instead of extending a pinned copy.
        await _session.OpenSnapshotAsync(row.Descriptor);
        if (_session.OpenSnapshot is not { } open || !string.Equals(open.Id, row.Descriptor.Id, StringComparison.Ordinal))
        {
            // The open already reported the exact refusal; retaining against
            // a snapshot the service did not serve would be a guess.
            return;
        }

        await _session.RetainSnapshotAsync(open, retainUntilUtc);
    }

    /// <summary>
    /// S9-T4 purges the selected preserved snapshot through the existing
    /// source-snapshot-purge operation. The snapshot's bytes authorize
    /// nothing afterwards. A stale listed revision reports its conflict
    /// with its remediation instead of purging a pinned copy.
    /// </summary>
    private async void OnSnapshotPurgeClicked(object sender, RoutedEventArgs e)
    {
        if (SnapshotsList.SelectedItem is not SnapshotRow row)
        {
            _session.ReportSnapshotFormProblem(
                "This console purged nothing: choose the preserved snapshot to purge first. Nothing changed.");
            return;
        }

        await _session.OpenSnapshotAsync(row.Descriptor);
        if (_session.OpenSnapshot is not { } open || !string.Equals(open.Id, row.Descriptor.Id, StringComparison.Ordinal))
        {
            return;
        }

        await _session.PurgeSnapshotAsync(row.Descriptor);
    }

    private static bool IsMappingUnknownRow(SourceRow row) =>
        row.Source.Quarantine is not null &&
        string.Equals(row.Source.Quarantine.Reason, "mapping_unknown", StringComparison.Ordinal);

    /// <summary>
    /// One collection member in the owner's draft: the exact live resource ID
    /// the service will resolve, with the title the search served when the
    /// owner picked it. The ID is secondary detail, never a typed input.
    /// </summary>
    private sealed record CollectionMember(string ResourceId, string? Title)
    {
        public override string ToString() =>
            (string.IsNullOrWhiteSpace(Title) ? "(title not loaded)" : Title) + "  ·  " + ResourceId;
    }

    private static bool TryParseExplicitUtc(string value, out DateTimeOffset timestamp)
    {
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out timestamp) &&
            timestamp.Offset == TimeSpan.Zero &&
            (value.EndsWith("Z", StringComparison.Ordinal) || value.EndsWith("+00:00", StringComparison.Ordinal)) &&
            timestamp > DateTimeOffset.UtcNow)
        {
            return true;
        }

        timestamp = default;
        return false;
    }

    /// <summary>
    /// Renders the sources console from the service's answers only: allowlist,
    /// source status, folder consent/preview, refresh entries, collections and
    /// snapshots. When the vault is not unlocked no sources content is shown;
    /// no locator, transcript or preserved bytes are rendered.
    /// </summary>
    private void RenderSources(bool attached)
    {
        var state = _session.ReportedVaultState;
        var inFlight = _session.SourcesActInFlight;
        var unlocked = state == OwnerVaultState.Unlocked;

        SourcesStateText.Text = state switch
        {
            OwnerVaultState.Unlocked =>
                "Vault: unlocked — the service serves the allowlist, the held mapping review, the collections and the snapshots. Nothing here is read without your explicit act.",
            OwnerVaultState.Locked =>
                "Vault: locked — no source, collection or snapshot is available or shown. Unlock in Vault & key to use this console.",
            OwnerVaultState.Locking =>
                "Vault: locking — the service is draining private work; the sources view of this screen is cleared.",
            OwnerVaultState.Uninitialized =>
                "Vault: not provisioned — there is no vault and therefore no source to tend.",
            _ => attached
                ? "Vault: unknown — no usable owner-channel answer, so no sources view is shown."
                : "Vault: not reported — this console holds no owner session."
        };

        if (!ReferenceEquals(_renderedSources, _session.Sources))
        {
            _renderedSources = _session.Sources;
            var previousId = (SourcesList.SelectedItem as SourceRow)?.Source.SourceId;
            var rows = _session.Sources.Select(source => new SourceRow(source)).ToArray();
            SourcesList.ItemsSource = rows;
            SourcesList.SelectedItem = previousId is null
                ? null
                : rows.FirstOrDefault(row => string.Equals(row.Source.SourceId, previousId, StringComparison.Ordinal));
        }

        RenderOutcome(SourcesMessageText, _session.SourcesMessage ?? (unlocked
            ? "No source has been read in this session. Read the allowlist to see each source's life, checkpoint, quarantine, availability and cache scope."
            : "No private sources act is offered while the service does not report the vault unlocked."));

        var open = _session.OpenSource;
        SourceDetailText.Text = open is null
            ? "No source is open. Choose a row to see its exact mapping, life, checkpoint, quarantine, availability and cache scope."
            : DescribeSourceDetail(open);
        RenderOutcome(SourceMessageText, _session.SourceMessage ?? (unlocked
            ? "No source act has been sent in this session. An uncertain mapping belongs in mapping review, never in a guessed zone."
            : "No private source act is offered while the service does not report the vault unlocked."));
        var ingest = _session.LastIngest;
        IngestOutcomeText.Text = ingest is null
            ? string.Empty
            : "ingest status=" + ingest.Status + "  checkpoint=" + ingest.ConsumedOffset.ToString(CultureInfo.InvariantCulture) +
                (string.IsNullOrEmpty(ingest.DiagnosticCode) ? string.Empty : "  diagnostic=" + ingest.DiagnosticCode);

        var selectedSource = SourcesList.SelectedItem as SourceRow;
        var folderPreview = _session.FolderPreview;
        if (!ReferenceEquals(_renderedFolderPreview, folderPreview))
        {
            _renderedFolderPreview = folderPreview;
            FolderPreviewList.ItemsSource = folderPreview?.Entries
                .Select(entry => new FolderPreviewRow(entry))
                .ToArray();
        }

        FolderPreviewSummaryText.Text = folderPreview is null
            ? "No active folder preview. Selection confirmation alone never opts into refresh or preservation."
            : "zone=" + folderPreview.ZoneId + "  format=" + folderPreview.Format + " v" +
                folderPreview.FormatVersion.ToString(CultureInfo.InvariantCulture) +
                "  recursive=" + (folderPreview.Recursive ? "true" : "false") +
                "\ninclude=" + (folderPreview.IncludePatterns.Length == 0 ? "(none)" : string.Join(", ", folderPreview.IncludePatterns)) +
                "  exclude=" + (folderPreview.ExcludePatterns.Length == 0 ? "(none)" : string.Join(", ", folderPreview.ExcludePatterns)) +
                "\nexpires=" + folderPreview.ExpiresAtUtc.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture) +
                "  rawPreservationRetainUntilUtc=" +
                (folderPreview.RawPreservationRetainUntilUtc?.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture) ?? "none");
        RenderOutcome(FolderMessageText, _session.FolderMessage ?? (unlocked
            ? "No folder preview or consent act has run in this session."
            : "No private folder preview or consent act is offered while the service does not report the vault unlocked."));
        FolderStateText.Text = DescribeFolderState(selectedSource?.Source);

        var selectedHeld = selectedSource is not null && IsMappingUnknownRow(selectedSource);
        RefreshEnrollPickerOptions();
        var folderDraftEnabled = unlocked && !inFlight && folderPreview is null;
        FolderRootInput.IsEnabled = folderDraftEnabled;
        FolderBrowseButton.IsEnabled = folderDraftEnabled;
        FolderZoneInput.IsEnabled = folderDraftEnabled;
        FolderFormatInput.IsEnabled = folderDraftEnabled;
        FolderFormatVersionInput.IsEnabled = folderDraftEnabled;
        FolderRecursiveCheck.IsEnabled = folderDraftEnabled;
        FolderIncludeGlobsInput.IsEnabled = folderDraftEnabled;
        FolderExcludeGlobsInput.IsEnabled = folderDraftEnabled;
        FolderRawPreservationCheck.IsEnabled = folderDraftEnabled;
        FolderRawPreservationUntilInput.IsEnabled = folderDraftEnabled;
        FolderPreviewButton.IsEnabled = folderDraftEnabled;
        FolderDiscardPreviewButton.IsEnabled = unlocked && !inFlight && folderPreview is not null;
        FolderConfirmationInput.IsEnabled = unlocked && !inFlight && folderPreview is not null;
        FolderEnrollConfirmButton.IsEnabled = unlocked && !inFlight && folderPreview is not null &&
            string.Equals(FolderConfirmationInput.Text, "ENROLL", StringComparison.Ordinal);
        FolderPreviewList.IsEnabled = unlocked && !inFlight;
        FolderRefreshOptInButton.IsEnabled = unlocked && !inFlight && selectedSource is not null &&
            selectedSource.Source.FolderId is not null && selectedSource.Source.FolderRefreshOptIn != true;
        FolderRefreshOptOutButton.IsEnabled = unlocked && !inFlight && selectedSource is not null &&
            selectedSource.Source.FolderId is not null && selectedSource.Source.FolderRefreshOptIn == true;
        SourceRawPreservationUntilInput.IsEnabled = unlocked && !inFlight && selectedSource is not null && !selectedHeld;
        SourceRawPreservationOptInButton.IsEnabled = unlocked && !inFlight && selectedSource is not null &&
            !selectedHeld && !selectedSource.Source.RawPreservationOptIn;
        SourceRawPreservationOptOutButton.IsEnabled = unlocked && !inFlight && selectedSource is not null &&
            !selectedHeld && selectedSource.Source.RawPreservationOptIn;
        SourcesRefreshButton.IsEnabled = unlocked && !inFlight;
        SourceOpenButton.IsEnabled = unlocked && !inFlight && selectedSource is not null;
        SourcesRefreshPassButton.IsEnabled = unlocked && !inFlight;
        SourcesList.IsEnabled = unlocked && !inFlight;
        SourceIngestButton.IsEnabled = unlocked && !inFlight && selectedSource is not null && !selectedHeld;
        SourcePauseButton.IsEnabled = unlocked && !inFlight && selectedSource is not null && !selectedHeld;
        SourceResumeButton.IsEnabled = unlocked && !inFlight && selectedSource is not null && !selectedHeld;
        SourceOptInButton.IsEnabled = unlocked && !inFlight && selectedSource is not null && !selectedHeld;
        SourceOptOutButton.IsEnabled = unlocked && !inFlight && selectedSource is not null && !selectedHeld;
        SourceSnapshotsButton.IsEnabled = unlocked && !inFlight && selectedSource is not null && !selectedHeld;
        EnrollSourceIdInput.IsEnabled = unlocked && !inFlight;
        EnrollZoneInput.IsEnabled = unlocked && !inFlight;
        EnrollFormatInput.IsEnabled = unlocked && !inFlight;
        EnrollFormatVersionInput.IsEnabled = unlocked && !inFlight;
        EnrollLocatorInput.IsEnabled = unlocked && !inFlight;
        EnrollBrowseButton.IsEnabled = unlocked && !inFlight;
        EnrollButton.IsEnabled = unlocked && !inFlight;
        SourceRevokeButton.IsEnabled = unlocked && !inFlight && selectedSource is not null && !selectedHeld;
        SourceRelocateButton.IsEnabled = unlocked && !inFlight && selectedSource is not null && !selectedHeld;
        MappingSourceIdInput.IsEnabled = unlocked && !inFlight;
        MappingLocatorInput.IsEnabled = unlocked && !inFlight;
        MappingZoneInput.IsEnabled = unlocked && !inFlight;
        MappingFormatInput.IsEnabled = unlocked && !inFlight;
        MappingFormatVersionInput.IsEnabled = unlocked && !inFlight;
        MappingSubmitButton.IsEnabled = unlocked && !inFlight;
        MappingWithdrawButton.IsEnabled = unlocked && !inFlight && selectedHeld;

        RenderOutcome(RefreshMessageText, _session.RefreshMessage ?? (unlocked
            ? "No refresh pass has run in this session. Only opted-in sources are read; every other source names its skip reason with no read."
            : "No private refresh act is offered while the service does not report the vault unlocked."));
        if (!ReferenceEquals(_renderedRefresh, _session.RefreshEntries))
        {
            _renderedRefresh = _session.RefreshEntries;
            RefreshList.ItemsSource = _session.RefreshEntries.Select(entry => new RefreshRow(entry)).ToArray();
        }

        RenderOutcome(CollectionsMessageText, _session.CollectionsMessage ?? (unlocked
            ? "No collection has been read in this session. Read them to see each collection's members; membership never grants access by itself."
            : "No private collection act is offered while the service does not report the vault unlocked."));
        if (!ReferenceEquals(_renderedCollections, _session.Collections))
        {
            _renderedCollections = _session.Collections;
            var previousId = (CollectionsList.SelectedItem as CollectionRow)?.Collection.CollectionId;
            var rows = _session.Collections.Select(collection => new CollectionRow(collection)).ToArray();
            CollectionsList.ItemsSource = rows;
            CollectionsList.SelectedItem = previousId is null
                ? null
                : rows.FirstOrDefault(row => string.Equals(row.Collection.CollectionId, previousId, StringComparison.Ordinal));
        }

        if (!ReferenceEquals(_renderedCollectionCandidates, _session.CollectionCandidates))
        {
            _renderedCollectionCandidates = _session.CollectionCandidates;
            CollectionMemberCandidatesList.ItemsSource = _session.CollectionCandidates
                .Select(hit => new LibraryResultRow(hit))
                .ToArray();
        }

        RenderOutcome(CollectionMemberSearchMessageText, _session.CollectionCandidateMessage ?? (unlocked
            ? "Search the library to choose members without recalling a resource ID."
            : "No private collection search is offered while the service does not report the vault unlocked."));
        CollectionsRefreshButton.IsEnabled = unlocked && !inFlight;
        CollectionsList.IsEnabled = unlocked && !inFlight;
        CollectionNewButton.IsEnabled = unlocked && !inFlight;
        CollectionIdInput.IsEnabled = unlocked && !inFlight;
        CollectionMemberSearchInput.IsEnabled = unlocked && !inFlight;
        CollectionMemberSearchButton.IsEnabled = unlocked && !inFlight;
        CollectionMemberCandidatesList.IsEnabled = unlocked && !inFlight;
        CollectionMemberAddButton.IsEnabled = unlocked && !inFlight && CollectionMemberCandidatesList.SelectedItems.Count > 0;
        CollectionMemberAddOpenItemButton.IsEnabled = unlocked && !inFlight;
        CollectionMembersList.IsEnabled = unlocked && !inFlight;
        CollectionMemberRemoveButton.IsEnabled = unlocked && !inFlight && CollectionMembersList.SelectedItems.Count > 0;
        CollectionSaveButton.IsEnabled = unlocked && !inFlight && _collectionMembers.Count > 0 &&
            !string.IsNullOrWhiteSpace(CollectionIdInput.Text);
        if (CollectionsList.SelectedItem is CollectionRow selectedCollection &&
            string.IsNullOrWhiteSpace(CollectionIdInput.Text))
        {
            CollectionIdInput.Text = selectedCollection.Collection.CollectionId;
            _collectionMembers.Clear();
            foreach (var member in selectedCollection.Collection.ResourceIds)
            {
                _collectionMembers.Add(new CollectionMember(member, null));
            }

            RefreshCollectionMemberList();
        }

        RenderOutcome(SnapshotMessageText, _session.SnapshotMessage ?? (unlocked
            ? "No snapshot has been listed in this session. Preservation is explicit and opt-in, never automatic."
            : "No private snapshot act is offered while the service does not report the vault unlocked."));
        if (!ReferenceEquals(_renderedSnapshots, _session.Snapshots))
        {
            _renderedSnapshots = _session.Snapshots;
            var previousId = (SnapshotsList.SelectedItem as SnapshotRow)?.Descriptor.Id;
            var rows = _session.Snapshots.Select(snapshot => new SnapshotRow(snapshot)).ToArray();
            SnapshotsList.ItemsSource = rows;
            SnapshotsList.SelectedItem = previousId is null
                ? null
                : rows.FirstOrDefault(row => string.Equals(row.Descriptor.Id, previousId, StringComparison.Ordinal));
        }

        var snapshot = _session.OpenSnapshot;
        SnapshotDetailText.Text = snapshot is null
            ? "No snapshot is open. Preserve one below, or list a source's snapshots and choose a row."
            : "snapshot=" + snapshot.Id + "  revision=" + snapshot.Revision.ToString(CultureInfo.InvariantCulture) +
                "\nsource=" + snapshot.SourceId + "  reference revision=" + snapshot.SourceRevision.ToString(CultureInfo.InvariantCulture) +
                "  generation=" + snapshot.SourceGeneration.ToString(CultureInfo.InvariantCulture) +
                "\nbytes=" + snapshot.SourceLength.ToString(CultureInfo.InvariantCulture) +
                "  captured=" + snapshot.CapturedAtUtc.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture) +
                "  retained until=" + snapshot.RetainUntilUtc.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture) +
                (snapshot.Purged ? "\n(purged — its bytes authorize nothing)" : string.Empty);

        SnapshotSourceIdInput.IsEnabled = unlocked && !inFlight;
        SnapshotSourceRevisionInput.IsEnabled = unlocked && !inFlight;
        SnapshotReferenceLocatorInput.IsEnabled = unlocked && !inFlight;
        SnapshotRetainInput.IsEnabled = unlocked && !inFlight;
        SnapshotPreserveButton.IsEnabled = unlocked && !inFlight;
        SnapshotReferenceRegisterButton.IsEnabled = unlocked && !inFlight && selectedSource is not null && !selectedHeld;
        var openSnapshot = SnapshotsList.SelectedItem as SnapshotRow;
        SnapshotRetainButton.IsEnabled = unlocked && !inFlight && openSnapshot is not null;
        SnapshotPurgeButton.IsEnabled = unlocked && !inFlight && openSnapshot is not null;
        SnapshotsList.IsEnabled = unlocked && !inFlight;
    }

    private static string DescribeSourceDetail(OwnerSourceEnrollment source)
    {
        if (source.Quarantine is not null &&
            string.Equals(source.Quarantine.Reason, "mapping_unknown", StringComparison.Ordinal))
        {
            return "held source=" + source.SourceId +
                "\nquarantine=" + source.Quarantine.Reason + "  detail=" + source.Quarantine.Detail +
                "  recorded=" + source.Quarantine.RecordedAtUtc.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture) +
                "\n(mapping unresolved — its transcript was never opened and no zone was defaulted; enroll it once known or withdraw the submission)";
        }

        var hasIngestionReport = source.HasIngestionReport;
        var saved = !hasIngestionReport
            ? "not reported"
            : source.EventCount == 0 && source.PreservedBytes == 0
                ? "none (enrollment is metadata only)"
                : "canonical_events=" + source.EventCount.ToString(CultureInfo.InvariantCulture) +
                    " preserved_bytes=" + source.PreservedBytes.ToString(CultureInfo.InvariantCulture);
        var diagnostic = source.LastDiagnostic is { } lastDiagnostic
            ? lastDiagnostic.Code + "@" + lastDiagnostic.Offset.ToString(CultureInfo.InvariantCulture)
            : hasIngestionReport ? "none" : "not reported";
        var preserved = source.PreservedRecords is { Length: > 0 } records
            ? string.Join("\n", records.Select(record =>
                "offset=" + record.Offset.ToString(CultureInfo.InvariantCulture) +
                " length=" + record.Length.ToString(CultureInfo.InvariantCulture) +
                " digest=" + record.Digest +
                " retainUntilUtc=" + record.RetainUntilUtc.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture) +
                "  read back one span with owner source-preserved-record-read --source-id " + source.SourceId +
                " --offset " + record.Offset.ToString(CultureInfo.InvariantCulture) + " --output-file <new-file>"))
            : hasIngestionReport ? "(none)" : "not reported";
        var remediation = hasIngestionReport &&
            source.LastDiagnostic is { } detailDiagnostic && source.PreservedBytes == 0
            ? "\nremediation: code=" + detailDiagnostic.Code + " at byte offset " +
                detailDiagnostic.Offset.ToString(CultureInfo.InvariantCulture) +
                "; exact bytes remain source-dependent. Explicitly opt into raw preservation, then ingest again, or keep the barrier pending for an adapter."
            : string.Empty;

        return "source=" + source.SourceId + "  zone=" + source.ZoneId +
            "  format=" + source.Format + " v" + source.FormatVersion.ToString(CultureInfo.InvariantCulture) +
            "  generation=" + source.Generation.ToString(CultureInfo.InvariantCulture) + "  life=" + source.Life +
            "\nsourceReferenceRevision=" + (source.SourceReferenceRevision?.ToString(CultureInfo.InvariantCulture) ?? "not reported") +
            "\navailability=" + source.Availability +
            (source.AvailabilityObservedAtUtc is null ? string.Empty : "  observed=" + source.AvailabilityObservedAtUtc.Value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture)) +
            "\nsaved=" + saved +
            "  indexed=" + (hasIngestionReport ? source.Derivation : "not reported") +
            "  interpreted=" + (hasIngestionReport ? source.Interpretation : "not reported") +
            "  error=" + diagnostic +
            "\npreservation=" + (hasIngestionReport ? source.Preservation : "not reported") +
            "  rawPreservationOptIn=" + (source.RawPreservationOptIn ? "true" : "false") +
            "  retainUntilUtc=" + (source.RawPreservationRetainUntilUtc?.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture) ?? "none") +
            "\ncheckpoint=" + (hasIngestionReport ? source.ConsumedOffset.ToString(CultureInfo.InvariantCulture) : "not reported") +
            "  pendingTailBytes=" + (hasIngestionReport ? source.PendingTailBytes.ToString(CultureInfo.InvariantCulture) : "not reported") +
            "  unprocessedBytes=" + (hasIngestionReport ? source.UnprocessedBytes.ToString(CultureInfo.InvariantCulture) : "not reported") +
            "  uninterpretedRecords=" + (hasIngestionReport ? source.UninterpretedRecords.ToString(CultureInfo.InvariantCulture) : "not reported") +
            "  preservedPendingBytes=" + (hasIngestionReport ? source.PreservedPendingBytes.ToString(CultureInfo.InvariantCulture) : "not reported") +
            "\nevents=" + (hasIngestionReport ? source.EventCount.ToString(CultureInfo.InvariantCulture) : "not reported") +
            "  diagnostics=" + (hasIngestionReport ? source.DiagnosticCount.ToString(CultureInfo.InvariantCulture) : "not reported") +
            "  journalRev=" + (hasIngestionReport ? source.JournalRevision.ToString(CultureInfo.InvariantCulture) : "not reported") +
            "\nrefreshOptIn=" + (source.RefreshOptIn ? "true" : "false") +
            "  refreshOptOutVeto=" + (source.RefreshOptOut ? "true" : "false") +
            "  folderId=" + (source.FolderId ?? "none") +
            "  folderRefreshOptIn=" + (source.FolderRefreshOptIn?.ToString().ToLowerInvariant() ?? "not_reported") +
            (source.PausedAtUtc is null ? string.Empty : "  paused=" + source.PausedAtUtc.Value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture)) +
            "\ncache journalBytes=" + (hasIngestionReport ? source.JournalCiphertextBytes.ToString(CultureInfo.InvariantCulture) : "not reported") +
            "  snapshotBytes=" + (hasIngestionReport ? source.SnapshotCiphertextBytes.ToString(CultureInfo.InvariantCulture) : "not reported") +
            "  snapshotRev=" + (hasIngestionReport ? source.SnapshotRevision.ToString(CultureInfo.InvariantCulture) : "not reported") +
            "\npreserved spans (metadata only):\n" + preserved +
            remediation +
            (source.Quarantine is null ? "\n(no quarantine)" :
                "\nquarantine=" + source.Quarantine.Reason + "  detail=" + source.Quarantine.Detail +
                "  recorded=" + source.Quarantine.RecordedAtUtc.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture));
    }

    private string DescribeFolderState(OwnerSourceEnrollment? source)
    {
        if (source?.FolderId is not { } folderId)
        {
            return "No folder member is selected. Folder consent remains separate from per-source refresh consent.";
        }

        var members = _session.Sources
            .Where(member => string.Equals(member.FolderId, folderId, StringComparison.Ordinal))
            .ToArray();
        return "folderId=" + folderId +
            "  refreshOptIn=" + (source.FolderRefreshOptIn?.ToString().ToLowerInvariant() ?? "not_reported") +
            "  listedMembers=" + members.Length.ToString(CultureInfo.InvariantCulture) +
            "  sourceOptOutVetoes=" + members.Count(member => member.RefreshOptOut).ToString(CultureInfo.InvariantCulture) +
            "  paused=" + members.Count(member => string.Equals(member.Life, "paused", StringComparison.Ordinal)).ToString(CultureInfo.InvariantCulture) +
            "  missing=" + members.Count(member => string.Equals(member.Availability, "missing", StringComparison.OrdinalIgnoreCase)).ToString(CultureInfo.InvariantCulture) +
            "\nThe folder refresh setting is service-owned; the member's source pause/revoke/opt-out remains an independent veto.";
    }


    /// <summary>One source exactly as the service listed it, rendered as one line. Never a locator.</summary>
    private sealed record SourceRow(OwnerSourceEnrollment Source)
    {
        public override string ToString() =>
            (Source.Quarantine is not null && string.Equals(Source.Quarantine.Reason, "mapping_unknown", StringComparison.Ordinal)
                ? "held " + Source.SourceId + "  mapping_unknown/" + Source.Quarantine.Detail
                : "source " + Source.SourceId + "  zone=" + Source.ZoneId + "  gen=" + Source.Generation.ToString(CultureInfo.InvariantCulture) +
                    "  life=" + Source.Life + "  availability=" + Source.Availability +
                    (Source.FolderId is null
                        ? string.Empty
                        : "  folder=" + Source.FolderId + "  folderRefreshOptIn=" +
                            (Source.FolderRefreshOptIn?.ToString().ToLowerInvariant() ?? "not_reported")));
    }

    /// <summary>One bounded metadata-only folder entry, without a root locator or transcript.</summary>
    private sealed record FolderPreviewRow(OwnerSourceFolderEntry Entry)
    {
        public override string ToString() =>
            (Entry.Included ? "INCLUDED " : "EXCLUDED ") +
            Entry.RelativePath +
            (Entry.Included || string.IsNullOrWhiteSpace(Entry.ExclusionReason)
                ? string.Empty
                : "  reason=" + Entry.ExclusionReason);
    }

    /// <summary>One refresh entry exactly as the service reported it, rendered as one line.</summary>
    private sealed record RefreshRow(OwnerSourceRefreshEntry Entry)
    {
        public override string ToString() =>
            "refresh " + Entry.SourceId + "  status=" + Entry.Status +
                "  consumed=" + Entry.ConsumedOffset.ToString(CultureInfo.InvariantCulture) +
                "  events+=" + Entry.EventsAdded.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>One collection exactly as the service listed it, rendered as one line.</summary>
    private sealed record CollectionRow(OwnerCollectionMapping Collection)
    {
        public override string ToString() =>
            "collection " + Collection.CollectionId + "  ·  " +
            Collection.ResourceIds.Length.ToString(CultureInfo.InvariantCulture) +
            (Collection.ResourceIds.Length == 1 ? " member" : " members") +
            " (names stay in the member draft below)";
    }

    /// <summary>One snapshot descriptor exactly as the service listed it, rendered as one line.</summary>
    private sealed record SnapshotRow(OwnerSourceSnapshotDescriptor Descriptor)
    {
        public override string ToString() =>
            "snapshot " + Descriptor.Id + "  rev=" + Descriptor.Revision.ToString(CultureInfo.InvariantCulture) +
                "  gen=" + Descriptor.Generation.ToString(CultureInfo.InvariantCulture);
    }
}
