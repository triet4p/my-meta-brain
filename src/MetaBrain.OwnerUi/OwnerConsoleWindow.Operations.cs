using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using MetaBrain.Connections;

namespace MetaBrain.OwnerUi;

/// <summary>One row of the operations view: a headline and the detail under it.</summary>
internal sealed record OwnerOperationsRow(string Title, string Detail);

/// <summary>
/// The operations and audit view of the owner console (S8-T4): the health of
/// the running vault, the private audit trail, the error diagnostics and the
/// retention choice.
/// </summary>
/// <remarks>
/// The view renders what the service answered and nothing else. It never
/// composes a record, a title or a secret of its own, and it never asks the
/// owner for an internal identifier: the retention choice is two pickers with
/// plain numbers, and every other action is a button.
/// </remarks>
public partial class OwnerConsoleWindow : System.Windows.Window
{
    private static readonly int[] RetentionDayChoices = { 7, 30, 90, 365 };
    private static readonly int[] RetentionRecordChoices = { 100, 500, 1000, 5000 };

    /// <summary>
    /// Fills the two retention pickers with the plain choices the owner can
    /// reason about, and selects the choice the service currently applies.
    /// </summary>
    private void InitializeOperationsControls()
    {
        foreach (var days in RetentionDayChoices)
        {
            OperationsRetentionDaysInput.Items.Add(days);
        }

        foreach (var records in RetentionRecordChoices)
        {
            OperationsRetentionRecordsInput.Items.Add(records);
        }

        SyncOperationsRetentionInputs();
    }

    /// <summary>
    /// Puts the service's current retention choice into the pickers, so the
    /// owner sees what applies now before changing it.
    /// </summary>
    private void SyncOperationsRetentionInputs()
    {
        var days = _session.OperationsRetentionDays;
        if (!RetentionDayChoices.Contains(days))
        {
            days = 30;
        }

        var records = _session.OperationsMaximumRecords;
        if (!RetentionRecordChoices.Contains(records))
        {
            records = 500;
        }

        OperationsRetentionDaysInput.SelectedItem = days;
        OperationsRetentionRecordsInput.SelectedItem = records;
    }

    /// <summary>
    /// Renders the operations view: the health of the vault, the audit trail,
    /// the diagnostics and the outcome of the last act.
    /// </summary>
    private void RenderOperations(bool attached)
    {
        var health = _session.OperationsHealth;
        var unlocked = attached && _session.ReportedVaultState == OwnerVaultState.Unlocked;

        OperationsRefreshButton.IsEnabled = unlocked && !_session.OperationsActInFlight;
        OperationsRetentionButton.IsEnabled = unlocked && !_session.OperationsActInFlight;

        if (health is null)
        {
            OperationsHealthText.Text = unlocked
                ? "The service has not answered the health summary yet. Choose Refresh health, audit and diagnostics."
                : "The operations view needs the unlocked vault. Unlock it in Vault & key, then choose Refresh health, audit and diagnostics.";
            OperationsSpendText.Text = string.Empty;
            OperationsLogText.Text = string.Empty;
        }
        else
        {
            OperationsHealthText.Text =
                "Vault: " + health.VaultState +
                "  |  private index: " + (health.PrivateIndexMaterialized ? "built" : "not built") +
                " (" + health.PrivateIndexRows.ToString(CultureInfo.InvariantCulture) + " rows)" +
                "  |  provider: " + (health.ProviderConfigured ? "configured" : "not configured") +
                "  |  last error: " + (string.IsNullOrEmpty(health.LastErrorCode) ? "none recorded" : health.LastErrorCode);
            OperationsSpendText.Text =
                "Rolling spend: " + OwnerChannelClient.FormatOperationsCost(health.RollingSpendUsd) +
                "  |  audit entries: " + health.AuditEntries.ToString(CultureInfo.InvariantCulture) +
                "  |  diagnostics: " + health.DiagnosticEntries.ToString(CultureInfo.InvariantCulture) +
                "  |  retention: " + health.RetentionDays.ToString(CultureInfo.InvariantCulture) +
                " days, at most " + health.MaximumRecords.ToString(CultureInfo.InvariantCulture) + " records";
            OperationsLogText.Text =
                "Safe operational log: " + (health.OperationalLogName ?? "not written yet") +
                " (" + health.OperationalLogBytes.ToString(CultureInfo.InvariantCulture) + " bytes, " +
                health.OperationalLogEntries.ToString(CultureInfo.InvariantCulture) +
                " lines). It holds operation names and error codes only — never a body, a title, a credential or a token.";
        }

        var audit = _session.OperationsAudit;
        OperationsAuditCountText.Text = audit.Length == 0
            ? "No recorded operation yet."
            : "The service recorded " + audit.Length.ToString(CultureInfo.InvariantCulture) +
              " operation(s), newest first.";
        OperationsAuditList.ItemsSource = audit.Select(entry => new OwnerOperationsRow(
            entry.RecordedAtUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) +
            "  " + entry.Actor + "  " + entry.Action,
            "outcome: " + entry.Outcome +
            "  |  error code: " + (string.IsNullOrEmpty(entry.ErrorCode) ? "none" : entry.ErrorCode) +
            "  |  cost: " + OwnerChannelClient.FormatOperationsCost(entry.CostUsd) +
            (string.IsNullOrEmpty(entry.ResourceId) ? string.Empty : "  |  record: " + entry.ResourceId))).ToArray();

        var diagnostics = _session.OperationsDiagnostics;
        OperationsDiagnosticsCountText.Text = diagnostics.Length == 0
            ? "No failure has been recorded."
            : "The service recorded " + diagnostics.Length.ToString(CultureInfo.InvariantCulture) +
              " failure(s), newest first.";
        OperationsDiagnosticsList.ItemsSource = diagnostics.Select(entry => new OwnerOperationsRow(
            entry.RecordedAtUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) +
            "  " + entry.Component + "  " + entry.Code,
            entry.OwnerMessage)).ToArray();

        OperationsMessageText.Text = _session.OperationsMessage ?? string.Empty;
    }

    private void OnOperationsRefreshClicked(object sender, RoutedEventArgs e) =>
        _ = _session.RefreshOperationsAsync();

    private void OnOperationsRetentionChanged(object sender, SelectionChangedEventArgs e) => Render();

    private async void OnOperationsRetentionClicked(object sender, RoutedEventArgs e)
    {
        var days = OperationsRetentionDaysInput.SelectedItem is int chosenDays ? chosenDays : 30;
        var records = OperationsRetentionRecordsInput.SelectedItem is int chosenRecords ? chosenRecords : 500;
        var purgeLog = OperationsPurgeLogCheck.IsChecked == true;
        await _session.ApplyOperationsRetentionAsync(days, records, purgeLog).ConfigureAwait(true);
    }
}
