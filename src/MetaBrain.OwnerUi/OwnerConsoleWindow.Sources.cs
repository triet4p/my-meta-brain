using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using MetaBrain.Connections;

namespace MetaBrain.OwnerUi;

/// <summary>
/// Sources and organization view of the owner console (S5-T7): the enrolled
/// allowlist with life/checkpoint/quarantine/availability/cache scope, the
/// held mapping review with explicit resolution, pause/resume, refresh
/// consent and passes, owner collections, and explicit snapshot preservation
/// with retention. Every act is one request to the running service; no
/// locator is ever echoed.
/// </summary>
public partial class OwnerConsoleWindow : System.Windows.Window
{
    /// <summary>
    /// Empties the sources drafts: the mapping-review form, the collection
    /// form and the snapshot form. Always the whole draft, so no part of a
    /// finished epoch can be sent as if it belonged to this one.
    /// </summary>
    private void SyncSourcesForm()
    {
        MappingSourceIdInput.Text = string.Empty;
        MappingLocatorInput.Text = string.Empty;
        MappingZoneInput.Text = string.Empty;
        MappingFormatInput.Text = string.Empty;
        MappingFormatVersionInput.Text = string.Empty;
        CollectionIdInput.Text = string.Empty;
        CollectionMembersInput.Text = string.Empty;
        SnapshotSourceIdInput.Text = string.Empty;
        SnapshotSourceRevisionInput.Text = string.Empty;
        SnapshotReferenceLocatorInput.Text = string.Empty;
        SnapshotRetainInput.Text = string.Empty;
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

    private void OnCollectionsSelectionChanged(object sender, SelectionChangedEventArgs e) => Render();

    private async void OnCollectionsListKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter)
        {
            return;
        }

        e.Handled = true;
        Render();
    }

    private async void OnCollectionSaveClicked(object sender, RoutedEventArgs e)
    {
        var collectionId = CollectionIdInput.Text.Trim();
        var membersText = CollectionMembersInput.Text.Trim();
        if (collectionId.Length == 0 || membersText.Length == 0)
        {
            _session.ReportSourcesFormProblem(
                "This console saved no collection: it needs the collection ID you name and the member resource IDs. Nothing was saved and no grant widened.");
            return;
        }

        var members = SplitCollectionMembers(membersText);
        if (members is null)
        {
            _session.ReportSourcesFormProblem(
                "This console saved no collection: the members are not a nonempty comma-separated list of unique IDs. Nothing was saved and no grant widened.");
            return;
        }

        await _session.SetCollectionAsync(collectionId, members);
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

    private static bool IsMappingUnknownRow(SourceRow row) =>
        row.Source.Quarantine is not null &&
        string.Equals(row.Source.Quarantine.Reason, "mapping_unknown", StringComparison.Ordinal);

    private static string[]? SplitCollectionMembers(string text)
    {
        if (text.Trim().Length == 0)
        {
            return null;
        }

        var members = text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return members.Length > 0 && members.Distinct(StringComparer.Ordinal).Count() == members.Length
            ? members
            : null;
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
    /// Renders the sources console from the service's answers only: the
    /// allowlist with life/checkpoint/quarantine/availability/cache scope,
    /// the held mapping review, the refresh entries, the collections and the
    /// snapshots. When the vault is not unlocked no sources content is shown
    /// at all, and no locator is ever shown: the wire carries none.
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

        SourcesMessageText.Text = _session.SourcesMessage ?? (unlocked
            ? "No source has been read in this session. Read the allowlist to see each source's life, checkpoint, quarantine, availability and cache scope."
            : "No private sources act is offered while the service does not report the vault unlocked.");

        var open = _session.OpenSource;
        SourceDetailText.Text = open is null
            ? "No source is open. Choose a row to see its exact mapping, life, checkpoint, quarantine, availability and cache scope."
            : DescribeSourceDetail(open);
        SourceMessageText.Text = _session.SourceMessage ?? (unlocked
            ? "No source act has been sent in this session. An uncertain mapping belongs in mapping review, never in a guessed zone."
            : "No private source act is offered while the service does not report the vault unlocked.");
        var ingest = _session.LastIngest;
        IngestOutcomeText.Text = ingest is null
            ? string.Empty
            : "ingest status=" + ingest.Status + "  checkpoint=" + ingest.ConsumedOffset.ToString(CultureInfo.InvariantCulture) +
                (string.IsNullOrEmpty(ingest.DiagnosticCode) ? string.Empty : "  diagnostic=" + ingest.DiagnosticCode);

        var selectedSource = SourcesList.SelectedItem as SourceRow;
        var selectedHeld = selectedSource is not null && IsMappingUnknownRow(selectedSource);
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
        MappingSourceIdInput.IsEnabled = unlocked && !inFlight;
        MappingLocatorInput.IsEnabled = unlocked && !inFlight;
        MappingZoneInput.IsEnabled = unlocked && !inFlight;
        MappingFormatInput.IsEnabled = unlocked && !inFlight;
        MappingFormatVersionInput.IsEnabled = unlocked && !inFlight;
        MappingSubmitButton.IsEnabled = unlocked && !inFlight;
        MappingWithdrawButton.IsEnabled = unlocked && !inFlight && selectedHeld;

        RefreshMessageText.Text = _session.RefreshMessage ?? (unlocked
            ? "No refresh pass has run in this session. Only opted-in sources are read; every other source names its skip reason with no read."
            : "No private refresh act is offered while the service does not report the vault unlocked.");
        if (!ReferenceEquals(_renderedRefresh, _session.RefreshEntries))
        {
            _renderedRefresh = _session.RefreshEntries;
            RefreshList.ItemsSource = _session.RefreshEntries.Select(entry => new RefreshRow(entry)).ToArray();
        }

        CollectionsMessageText.Text = _session.CollectionsMessage ?? (unlocked
            ? "No collection has been read in this session. Read them to see each collection's members; membership never grants access by itself."
            : "No private collection act is offered while the service does not report the vault unlocked.");
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

        var selectedCollection = CollectionsList.SelectedItem as CollectionRow;
        CollectionsRefreshButton.IsEnabled = unlocked && !inFlight;
        CollectionsList.IsEnabled = unlocked && !inFlight;
        CollectionIdInput.IsEnabled = unlocked && !inFlight;
        CollectionMembersInput.IsEnabled = unlocked && !inFlight;
        CollectionSaveButton.IsEnabled = unlocked && !inFlight;
        if (selectedCollection is not null && string.IsNullOrWhiteSpace(CollectionIdInput.Text))
        {
            CollectionIdInput.Text = selectedCollection.Collection.CollectionId;
            CollectionMembersInput.Text = string.Join(",", selectedCollection.Collection.ResourceIds);
        }

        SnapshotMessageText.Text = _session.SnapshotMessage ?? (unlocked
            ? "No snapshot has been listed in this session. Preservation is explicit and opt-in, never automatic."
            : "No private snapshot act is offered while the service does not report the vault unlocked.");
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

        return "source=" + source.SourceId + "  zone=" + source.ZoneId +
            "  format=" + source.Format + " v" + source.FormatVersion.ToString(CultureInfo.InvariantCulture) +
            "  generation=" + source.Generation.ToString(CultureInfo.InvariantCulture) + "  life=" + source.Life +
            "\navailability=" + source.Availability +
            (source.AvailabilityObservedAtUtc is null ? string.Empty : "  observed=" + source.AvailabilityObservedAtUtc.Value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture)) +
            "\ncheckpoint=" + source.ConsumedOffset.ToString(CultureInfo.InvariantCulture) +
            "  events=" + source.EventCount.ToString(CultureInfo.InvariantCulture) +
            "  diagnostics=" + source.DiagnosticCount.ToString(CultureInfo.InvariantCulture) +
            "  journalRev=" + source.JournalRevision.ToString(CultureInfo.InvariantCulture) +
            "\nrefreshOptIn=" + (source.RefreshOptIn ? "true" : "false") +
            (source.PausedAtUtc is null ? string.Empty : "  paused=" + source.PausedAtUtc.Value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture)) +
            "\ncache journalBytes=" + source.JournalCiphertextBytes.ToString(CultureInfo.InvariantCulture) +
            "  snapshotBytes=" + source.SnapshotCiphertextBytes.ToString(CultureInfo.InvariantCulture) +
            "  snapshotRev=" + source.SnapshotRevision.ToString(CultureInfo.InvariantCulture) +
            (source.Quarantine is null ? "\n(no quarantine)" :
                "\nquarantine=" + source.Quarantine.Reason + "  detail=" + source.Quarantine.Detail +
                "  recorded=" + source.Quarantine.RecordedAtUtc.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture));
    }

    /// <summary>One source exactly as the service listed it, rendered as one line. Never a locator.</summary>
    private sealed record SourceRow(OwnerSourceEnrollment Source)
    {
        public override string ToString() =>
            (Source.Quarantine is not null && string.Equals(Source.Quarantine.Reason, "mapping_unknown", StringComparison.Ordinal)
                ? "held " + Source.SourceId + "  mapping_unknown/" + Source.Quarantine.Detail
                : "source " + Source.SourceId + "  zone=" + Source.ZoneId + "  gen=" + Source.Generation.ToString(CultureInfo.InvariantCulture) +
                    "  life=" + Source.Life + "  availability=" + Source.Availability);
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
            "collection " + Collection.CollectionId + "  members=" +
            (Collection.ResourceIds.Length == 0 ? "(none)" : string.Join(",", Collection.ResourceIds));
    }

    /// <summary>One snapshot descriptor exactly as the service listed it, rendered as one line.</summary>
    private sealed record SnapshotRow(OwnerSourceSnapshotDescriptor Descriptor)
    {
        public override string ToString() =>
            "snapshot " + Descriptor.Id + "  rev=" + Descriptor.Revision.ToString(CultureInfo.InvariantCulture) +
                "  gen=" + Descriptor.Generation.ToString(CultureInfo.InvariantCulture);
    }
}
