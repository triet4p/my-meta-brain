using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using MetaBrain.Connections;

namespace MetaBrain.OwnerUi;

/// <summary>
/// The owner console's backup, restore and export view (S8-T2).
/// <para>
/// The view is a form over three service acts. It owns nothing but the
/// owner's own choices: the file a snapshot goes to, the file and folder a
/// restore reads and writes, the credential of that restore, the records the
/// owner picked for the export and the file the readable copy goes to. Every
/// answer it shows is the answer of the running service, and the credential
/// is read once, sent once and never kept.
/// </para>
/// <para>
/// The forms live and die with the private epoch: when the session drops the
/// private state — lock, lost service, end of session — the form version
/// moves and this view clears every path, the export selection and the
/// credential field, so nothing of a finished epoch stays on screen or can be
/// sent later as if it were current.
/// </para>
/// </summary>
public partial class OwnerConsoleWindow
{
    private readonly IOwnerFilePicker _filePicker;
    private OwnerExportScopeCandidate[]? _renderedExportCandidates;
    private bool _syncingExportSelection;
    private string _exportScopeSignature = string.Empty;

    /// <summary>
    /// One candidate of the export scope, rendered title first: the owner picks
    /// what leaves the vault by reading its title, never by remembering an
    /// identifier.
    /// </summary>
    private sealed record ExportCandidateRow(OwnerExportScopeCandidate Candidate)
    {
        public string Title => Candidate.Title;

        public string Detail => Candidate.Detail;
    }

    /// <summary>
    /// The two credentials a restore can use. The label is what the owner
    /// reads; the flag is what the request carries.
    /// </summary>
    private sealed record RestoreCredentialOption(string Label, bool UseRecoveryCode)
    {
        public override string ToString() => Label;
    }

    private void InitializeBackupControls()
    {
        if (RestoreCredentialModeInput.Items.Count > 0)
        {
            return;
        }

        RestoreCredentialModeInput.Items.Add(new RestoreCredentialOption("Owner passphrase", false));
        RestoreCredentialModeInput.Items.Add(new RestoreCredentialOption("Recovery key", true));
        RestoreCredentialModeInput.SelectedIndex = 0;
    }

    private void RenderBackupExport(bool attached)
    {
        InitializeBackupControls();
        var unlocked = attached && _session.ReportedVaultState == OwnerVaultState.Unlocked;
        var busy = _session.BackupActInFlight;

        if (_backupFormVersion != _session.BackupFormVersion)
        {
            // The session dropped the drafts of the epoch that ended (lock,
            // lost service, end of session): the paths, the export selection
            // and the credential of that epoch go with it.
            _backupFormVersion = _session.BackupFormVersion;
            ClearBackupInputs();
        }

        if (!ReferenceEquals(_renderedExportCandidates, _session.ExportCandidates))
        {
            var kept = ExportCandidatesList.SelectedItems
                .OfType<ExportCandidateRow>()
                .Select(row => row.Candidate.Id)
                .ToHashSet(StringComparer.Ordinal);
            _renderedExportCandidates = _session.ExportCandidates;
            var rows = _renderedExportCandidates.Select(candidate => new ExportCandidateRow(candidate)).ToArray();
            _syncingExportSelection = true;
            try
            {
                ExportCandidatesList.ItemsSource = rows;
                ExportCandidatesList.SelectedItems.Clear();
                foreach (var row in rows.Where(row => kept.Contains(row.Candidate.Id)))
                {
                    ExportCandidatesList.SelectedItems.Add(row);
                }
            }
            finally
            {
                _syncingExportSelection = false;
            }
        }

        var selection = SelectedExportRows();
        var scope = selection.Count;

        RenderOutcome(BackupMessageText, _session.BackupMessage ??
            (unlocked
                ? "Choose where the encrypted snapshot should be written, then create the backup."
                : "The vault is locked, so the service takes no snapshot. Unlock it in Vault & key first."));
        RenderOutcome(RestoreMessageText, _session.RestoreMessage ??
            (attached
                ? "Choose a backup file and a new folder, then restore. A restore works while the vault is locked."
                : "No owner session: connect to the service to restore a backup."));
        RenderOutcome(ExportCandidateMessageText, _session.ExportCandidateMessage ??
            (unlocked
                ? "Search for the records you want to copy out of the vault."
                : "The vault is locked, so the service reads no records. Unlock it in Vault & key first."));
        RenderOutcome(ExportSourceMessageText, _session.ExportSourceMessage ??
            (unlocked
                ? "Choose a record above: the service then names the sources it cites, so you can decide whether a cited source may leave the vault with it."
                : "The vault is locked, so the service reads no records. Unlock it in Vault & key first."));
        RenderOutcome(ExportMessageText, _session.ExportMessage ??
            (unlocked
                ? "Choose the records and the file, then write the readable copy. Nothing is exported before you ask for it."
                : "The vault is locked, so the service reads no records. Unlock it in Vault & key first."));

        BackupPathInput.IsEnabled = !busy;
        BackupBrowseButton.IsEnabled = !busy;
        BackupRunButton.IsEnabled = unlocked && !busy;
        RestoreBackupPathInput.IsEnabled = !busy;
        RestoreBackupBrowseButton.IsEnabled = !busy;
        RestoreTargetPathInput.IsEnabled = !busy;
        RestoreTargetBrowseButton.IsEnabled = !busy;
        RestoreCredentialModeInput.IsEnabled = !busy;
        RestoreCredentialInput.IsEnabled = !busy;
        // The restore reads a backup file and writes a new folder, so the
        // service serves it while the vault is locked.
        RestoreRunButton.IsEnabled = attached && !busy;
        ExportSearchInput.IsEnabled = unlocked && !busy;
        ExportSearchButton.IsEnabled = unlocked && !busy;
        ExportCandidatesList.IsEnabled = unlocked && !busy;
        ExportIncludeRelationsCheck.IsEnabled = unlocked && !busy;
        ExportIncludeLifecycleCheck.IsEnabled = unlocked && !busy;
        ExportPathInput.IsEnabled = unlocked && !busy;
        ExportBrowseButton.IsEnabled = unlocked && !busy;
        ExportRunButton.IsEnabled = unlocked && !busy && scope > 0;

        BackupPathHintText.Text = BackupPathInput.Text.Length == 0
            ? "The snapshot is written to one new file. A backup never overwrites a file you already have."
            : "The snapshot will be written to " + BackupPathInput.Text + ".";
        RestoreTargetHintText.Text = RestoreTargetPathInput.Text.Length == 0
            ? "The restored vault is written to one new folder. The folder must not exist yet, and the live vault is never replaced."
            : Directory.Exists(RestoreTargetPathInput.Text)
                ? "That folder already exists. Choose a folder that does not exist yet: the service refuses to write into it."
                : "The restored vault will be written to " + RestoreTargetPathInput.Text + ". Your live vault stays untouched.";
        RestoreCredentialHintText.Text = SelectedRestoreCredentialOption() is { UseRecoveryCode: true }
            ? "The recovery key you saved outside the vault opens this backup. It is sent in one request, never stored and never shown again."
            : "The owner passphrase that opened this vault opens this backup. It is sent in one request, never stored and never shown again.";
        ExportScopeText.Text = scope == 0
            ? "No record is selected for the export. Search above and choose the records you want to copy out of the vault."
            : scope.ToString(CultureInfo.InvariantCulture) + " record(s) selected for the export: " +
              string.Join(", ", selection.Select(row => row.Title)) +
              ". A citation to a record outside this selection is withheld whole.";
    }

    private void OnBackupBrowseClicked(object sender, RoutedEventArgs e)
    {
        var chosen = _filePicker.PickSaveFile("Choose where to write the encrypted backup", "metabrain-backup.mbbackup");
        if (!string.IsNullOrWhiteSpace(chosen))
        {
            BackupPathInput.Text = chosen;
        }

        RenderBackupExport(_session.Attached);
    }

    private async void OnBackupRunClicked(object sender, RoutedEventArgs e)
    {
        await _session.BackupVaultAsync(BackupPathInput.Text);
        RenderBackupExport(_session.Attached);
    }

    private void OnRestoreBackupBrowseClicked(object sender, RoutedEventArgs e)
    {
        var chosen = _filePicker.PickOpenFile("Choose the backup file to restore");
        if (!string.IsNullOrWhiteSpace(chosen))
        {
            RestoreBackupPathInput.Text = chosen;
        }

        RenderBackupExport(_session.Attached);
    }

    private void OnRestoreTargetBrowseClicked(object sender, RoutedEventArgs e)
    {
        var chosen = _filePicker.PickFolder("Choose the new folder for the restored vault");
        if (!string.IsNullOrWhiteSpace(chosen))
        {
            RestoreTargetPathInput.Text = chosen;
        }

        RenderBackupExport(_session.Attached);
    }

    private void OnRestoreCredentialModeChanged(object sender, SelectionChangedEventArgs e) =>
        RenderBackupExport(_session.Attached);

    private async void OnRestoreRunClicked(object sender, RoutedEventArgs e)
    {
        var useRecoveryCode = SelectedRestoreCredentialOption() is { UseRecoveryCode: true };
        var credential = RestoreCredentialInput.Password;
        try
        {
            // The credential exists in this one call only: it is read here,
            // sent in exactly one request and never kept by this console.
            await _session.RestoreVaultAsync(
                RestoreBackupPathInput.Text,
                RestoreTargetPathInput.Text,
                credential,
                useRecoveryCode);
        }
        finally
        {
            RestoreCredentialInput.Clear();
            credential = string.Empty;
            RenderBackupExport(_session.Attached);
        }
    }

    private async void OnExportSearchClicked(object sender, RoutedEventArgs e)
    {
        await _session.SearchExportCandidatesAsync(ExportSearchInput.Text);
        RenderBackupExport(_session.Attached);
    }

    private async void OnExportSearchKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter)
        {
            return;
        }

        e.Handled = true;
        await _session.SearchExportCandidatesAsync(ExportSearchInput.Text);
        RenderBackupExport(_session.Attached);
    }

    private void OnExportCandidatesSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingExportSelection)
        {
            return;
        }

        RenderBackupExport(_session.Attached);
        RefreshCitedSourcesAsync();
    }

    /// <summary>
    /// Asks the service to name the sources the selected records cite, so the
    /// owner can decide whether a cited source may leave the vault too. The
    /// request only runs when the chosen records really changed, and it never
    /// chooses a source by itself.
    /// </summary>
    private async void RefreshCitedSourcesAsync()
    {
        var selected = SelectedExportRows()
            .Select(row => row.Candidate)
            .Where(candidate => !string.Equals(candidate.Kind, "source", StringComparison.Ordinal))
            .ToArray();
        var signature = string.Join("|", selected.Select(candidate => candidate.Id + "@" + candidate.Revision.ToString(CultureInfo.InvariantCulture)));
        if (string.Equals(signature, _exportScopeSignature, StringComparison.Ordinal))
        {
            return;
        }

        _exportScopeSignature = signature;
        await _session.RefreshExportSourceCandidatesAsync(
            selected.Select(candidate => (candidate.Id, candidate.Revision)).ToArray()).ConfigureAwait(true);
        RenderBackupExport(_session.Attached);
    }

    private void OnExportBrowseClicked(object sender, RoutedEventArgs e)
    {
        var chosen = _filePicker.PickSaveFile("Choose where to write the readable export", "metabrain-export.md");
        if (!string.IsNullOrWhiteSpace(chosen))
        {
            ExportPathInput.Text = chosen;
        }

        RenderBackupExport(_session.Attached);
    }

    private async void OnExportRunClicked(object sender, RoutedEventArgs e)
    {
        var ids = SelectedExportRows().Select(row => row.Candidate.Id).ToArray();
        await _session.ExportRecordsAsync(
            ids,
            ExportIncludeRelationsCheck.IsChecked == true,
            ExportIncludeLifecycleCheck.IsChecked == true,
            ExportPathInput.Text);
        RenderBackupExport(_session.Attached);
    }

    private List<ExportCandidateRow> SelectedExportRows() =>
        ExportCandidatesList.SelectedItems.OfType<ExportCandidateRow>().ToList();

    private RestoreCredentialOption? SelectedRestoreCredentialOption() =>
        RestoreCredentialModeInput.SelectedItem as RestoreCredentialOption;

    /// <summary>
    /// Drops every draft of this view: the chosen paths, the search text, the
    /// export selection and the restore credential. It runs when the session
    /// drops the private state, so nothing of a finished epoch stays on
    /// screen and nothing of it can be sent later as if it were current.
    /// </summary>
    private void ClearBackupInputs()
    {
        _syncingExportSelection = true;
        try
        {
            BackupPathInput.Clear();
            RestoreBackupPathInput.Clear();
            RestoreTargetPathInput.Clear();
            RestoreCredentialInput.Clear();
            ExportSearchInput.Clear();
            ExportPathInput.Clear();
            ExportCandidatesList.ItemsSource = null;
            ExportCandidatesList.SelectedItems.Clear();
            ExportIncludeRelationsCheck.IsChecked = false;
            ExportIncludeLifecycleCheck.IsChecked = false;
        }
        finally
        {
            _syncingExportSelection = false;
        }

        _renderedExportCandidates = null;
        _exportScopeSignature = string.Empty;
    }
}
