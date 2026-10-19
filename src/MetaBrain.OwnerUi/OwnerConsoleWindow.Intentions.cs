using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using MetaBrain.Connections;
using MetaBrain.Core.Domain;

namespace MetaBrain.OwnerUi;

public partial class OwnerConsoleWindow
{
    private OwnerScheduledIntention[]? _renderedIntentions;
    private bool _syncingIntentionSelection;

    private void RenderIntentions(bool attached)
    {
        var unlocked = attached && _session.ReportedVaultState == OwnerVaultState.Unlocked;
        var canAct = unlocked && !_session.IntentionActInFlight;
        var selected = SelectedIntention();

        if (!ReferenceEquals(_renderedIntentions, _session.ScheduledIntentions))
        {
            var selectedId = selected?.Intention.ResourceId;
            _renderedIntentions = _session.ScheduledIntentions;
            var rows = _renderedIntentions.Select(intention => new OwnerIntentionListRow(intention)).ToArray();
            _syncingIntentionSelection = true;
            try
            {
                IntentionsList.ItemsSource = rows;
                IntentionsList.SelectedItem = selectedId is null
                    ? null
                    : rows.FirstOrDefault(row => string.Equals(row.Intention.ResourceId, selectedId, StringComparison.Ordinal));
            }
            finally
            {
                _syncingIntentionSelection = false;
            }
        }

        selected = SelectedIntention();
        IntentionsMessageText.Text = _session.IntentionsMessage ??
            (unlocked ? "Read the service's encrypted intention schedule." : "No private intention schedule is available while locked or disconnected.");
        IntentionsRefreshButton.IsEnabled = canAct;
        IntentionsList.IsEnabled = unlocked;
        IntentionResourceIdInput.IsEnabled = canAct;
        IntentionDueAtInput.IsEnabled = canAct;
        IntentionTriggerSetButton.IsEnabled = canAct &&
            !string.IsNullOrWhiteSpace(IntentionResourceIdInput.Text) &&
            IntentionTimeTrigger.TryParse(IntentionDueAtInput.Text.Trim(), out _);
        IntentionTriggerCancelButton.IsEnabled = canAct && selected is not null &&
            selected.Intention.DueAt is not null &&
            string.Equals(IntentionResourceIdInput.Text.Trim(), selected.Intention.ResourceId, StringComparison.Ordinal);
        IntentionContextProjectInput.IsEnabled = canAct;
        IntentionContextTaskInput.IsEnabled = canAct;
        IntentionContextTriggerSetButton.IsEnabled = canAct &&
            !string.IsNullOrWhiteSpace(IntentionResourceIdInput.Text) &&
            (!string.IsNullOrWhiteSpace(IntentionContextProjectInput.Text) ||
             !string.IsNullOrWhiteSpace(IntentionContextTaskInput.Text));
        IntentionContextTriggerDisableButton.IsEnabled = canAct && selected?.Intention.ContextTriggerEnabled == true;
        IntentionRationaleInput.IsEnabled = canAct && selected is not null;
        IntentionAcknowledgeButton.IsEnabled = canAct && selected is not null &&
            (selected.Intention.IsDue || selected.Intention.ContextTriggerEnabled) &&
            !selected.Intention.Acknowledged;
        IntentionCompleteButton.IsEnabled = canAct && selected is not null && !string.IsNullOrWhiteSpace(IntentionRationaleInput.Text);
        IntentionCancelButton.IsEnabled = canAct && selected is not null && !string.IsNullOrWhiteSpace(IntentionRationaleInput.Text);
    }

    private void OnIntentionInputChanged(object sender, TextChangedEventArgs e) =>
        RenderIntentions(_session.Attached);

    private async void OnIntentionsRefreshClicked(object sender, RoutedEventArgs e) =>
        await _session.RefreshIntentionsAsync();

    private void OnIntentionsSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingIntentionSelection || SelectedIntention() is not { } selected)
        {
            RenderIntentions(_session.Attached);
            return;
        }

        IntentionResourceIdInput.Text = selected.Intention.ResourceId;
        IntentionDueAtInput.Text = selected.Intention.DueAt?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty;
        IntentionContextProjectInput.Text = selected.Intention.ContextProject ?? string.Empty;
        IntentionContextTaskInput.Text = selected.Intention.ContextTask ?? string.Empty;
        IntentionRationaleInput.Clear();
        RenderIntentions(_session.Attached);
    }

    private async void OnIntentionTriggerSetClicked(object sender, RoutedEventArgs e)
    {
        var resourceId = IntentionResourceIdInput.Text.Trim();
        var dueAt = IntentionDueAtInput.Text.Trim();
        if (resourceId.Length == 0 || !IntentionTimeTrigger.TryParse(dueAt, out _))
        {
            IntentionsMessageText.Text = "Enter an intention resource ID and an exact ISO-8601 due time with Z or a numeric UTC offset. No local timezone is guessed.";
            return;
        }

        var selected = SelectedIntention();
        long? expectedRevision = selected is not null &&
            string.Equals(selected.Intention.ResourceId, resourceId, StringComparison.Ordinal)
                ? selected.Intention.LifecycleRevision
                : null;
        await _session.SetIntentionTimeTriggerAsync(resourceId, dueAt, expectedRevision);
    }

    private async void OnIntentionContextTriggerSetClicked(object sender, RoutedEventArgs e)
    {
        var resourceId = IntentionResourceIdInput.Text.Trim();
        var project = string.IsNullOrWhiteSpace(IntentionContextProjectInput.Text)
            ? null
            : IntentionContextProjectInput.Text.Trim();
        var task = string.IsNullOrWhiteSpace(IntentionContextTaskInput.Text)
            ? null
            : IntentionContextTaskInput.Text.Trim();
        if (resourceId.Length == 0 || (project is null && task is null))
        {
            IntentionsMessageText.Text = "Name an intention and at least one explicit project or task context. No context is inferred.";
            return;
        }

        var selected = SelectedIntention();
        long? expectedRevision = selected is not null &&
            string.Equals(selected.Intention.ResourceId, resourceId, StringComparison.Ordinal)
                ? selected.Intention.LifecycleRevision
                : null;
        await _session.EnableIntentionContextTriggerAsync(resourceId, project, task, expectedRevision);
    }

    private async void OnIntentionContextTriggerDisableClicked(object sender, RoutedEventArgs e)
    {
        if (SelectedIntention() is { Intention.ContextTriggerEnabled: true } selected)
        {
            await _session.DisableIntentionContextTriggerAsync(selected.Intention);
        }
    }

    private async void OnIntentionTriggerCancelClicked(object sender, RoutedEventArgs e)
    {
        if (SelectedIntention() is { } selected &&
            string.Equals(IntentionResourceIdInput.Text.Trim(), selected.Intention.ResourceId, StringComparison.Ordinal))
        {
            await _session.CancelIntentionTimeTriggerAsync(selected.Intention);
        }
    }

    private async void OnIntentionAcknowledgeClicked(object sender, RoutedEventArgs e)
    {
        if (SelectedIntention() is { } selected)
        {
            await _session.AcknowledgeIntentionAsync(selected.Intention);
        }
    }

    private async void OnIntentionCompleteClicked(object sender, RoutedEventArgs e)
    {
        if (SelectedIntention() is { } selected && !string.IsNullOrWhiteSpace(IntentionRationaleInput.Text))
        {
            await _session.CompleteIntentionAsync(selected.Intention, IntentionRationaleInput.Text.Trim());
            IntentionRationaleInput.Clear();
        }
    }

    private async void OnIntentionCancelClicked(object sender, RoutedEventArgs e)
    {
        if (SelectedIntention() is { } selected && !string.IsNullOrWhiteSpace(IntentionRationaleInput.Text))
        {
            await _session.CancelIntentionAsync(selected.Intention, IntentionRationaleInput.Text.Trim());
            IntentionRationaleInput.Clear();
        }
    }

    private void OnIntentionsListKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter && SelectedIntention() is { } selected)
        {
            IntentionResourceIdInput.Text = selected.Intention.ResourceId;
            IntentionDueAtInput.Text = selected.Intention.DueAt?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty;
            IntentionContextProjectInput.Text = selected.Intention.ContextProject ?? string.Empty;
            IntentionContextTaskInput.Text = selected.Intention.ContextTask ?? string.Empty;
            RenderIntentions(_session.Attached);
            e.Handled = true;
        }
    }


    private void ClearIntentionInputs()
    {
        IntentionResourceIdInput.Clear();
        IntentionDueAtInput.Clear();
        IntentionContextProjectInput.Clear();
        IntentionContextTaskInput.Clear();
        IntentionRationaleInput.Clear();
    }

    private OwnerIntentionListRow? SelectedIntention() => IntentionsList.SelectedItem as OwnerIntentionListRow;

    private sealed class OwnerIntentionListRow(OwnerScheduledIntention intention)
    {
        public OwnerScheduledIntention Intention { get; } = intention;

        public string Summary =>
            (string.IsNullOrEmpty(Intention.Title) ? "(no title)" : Intention.Title) +
            " | " + (Intention.IsDue ? "DUE" : "scheduled") +
            " | due=" + (Intention.DueAt?.ToString("O", CultureInfo.InvariantCulture) ?? "none") +
            " | context=" + (Intention.ContextTriggerEnabled ? "enabled" : Intention.ContextProject is not null || Intention.ContextTask is not null ? "disabled" : "none") +
            " | project=" + (Intention.ContextProject ?? "none") +
            " | task=" + (Intention.ContextTask ?? "none") +
            " | state=" + Intention.State +
            " | notified=" + Intention.Notified.ToString(CultureInfo.InvariantCulture) +
            " | acknowledged=" + Intention.Acknowledged.ToString(CultureInfo.InvariantCulture);

        public string BodyPreview => Intention.BodyPreview;
    }
}
