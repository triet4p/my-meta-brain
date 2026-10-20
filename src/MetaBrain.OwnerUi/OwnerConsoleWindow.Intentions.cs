using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using MetaBrain.Connections;
using MetaBrain.Core.Domain;

namespace MetaBrain.OwnerUi;

public partial class OwnerConsoleWindow
{
    private OwnerScheduledIntention[]? _renderedIntentions;
    private OwnerLibraryHit[]? _renderedIntentionCandidates;
    private bool _syncingIntentionSelection;
    private string _intentionResourceId = string.Empty;
    private string _composedDueAtIso = string.Empty;
    private string _composedDuePreview = string.Empty;
    private string _composedDueProblem = string.Empty;

    private void InitializeIntentionControls()
    {
        if (IntentionZoneInput.Items.Count > 0)
        {
            return;
        }

        IntentionZoneInput.Items.Add(IntentionTimeEntry.LocalOption());
        IntentionZoneInput.Items.Add(IntentionTimeEntry.UtcOption());
        IntentionZoneInput.SelectedIndex = 0;
        IntentionDateInput.SelectedDate = DateTime.Today;
    }

    private void RenderIntentions(bool attached)
    {
        InitializeIntentionControls();
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

        if (!ReferenceEquals(_renderedIntentionCandidates, _session.IntentionCandidates))
        {
            _renderedIntentionCandidates = _session.IntentionCandidates;
            IntentionCandidatesList.ItemsSource = _session.IntentionCandidates
                .Select(hit => new LibraryResultRow(hit))
                .ToArray();
        }

        selected = SelectedIntention();
        RenderOutcome(IntentionsMessageText, _session.IntentionsMessage ??
            (unlocked ? "Read the service's encrypted intention schedule." : "No private intention schedule is available while locked or disconnected."));
        RenderOutcome(IntentionSearchMessageText, _session.IntentionCandidateMessage ?? (unlocked
            ? "Search for a pending intention by a word from its title or body."
            : "No private intention search is offered while the service does not report the vault unlocked."));
        IntentionsRefreshButton.IsEnabled = canAct;
        IntentionsList.IsEnabled = unlocked;
        IntentionSearchInput.IsEnabled = canAct;
        IntentionSearchButton.IsEnabled = canAct;
        IntentionCandidatesList.IsEnabled = unlocked;
        IntentionUseSelectedButton.IsEnabled = canAct && selected is not null;
        IntentionChosenText.Text = DescribeChosenIntention();
        IntentionDateInput.IsEnabled = canAct;
        IntentionTimeInput.IsEnabled = canAct;
        IntentionZoneInput.IsEnabled = canAct;
        ComposeIntentionDue();
        IntentionDuePreviewText.Text = _composedDuePreview;
        IntentionTriggerSetButton.IsEnabled = canAct &&
            _intentionResourceId.Length > 0 &&
            _composedDueAtIso.Length > 0;
        IntentionTriggerCancelButton.IsEnabled = canAct && selected is not null &&
            selected.Intention.DueAt is not null &&
            string.Equals(_intentionResourceId, selected.Intention.ResourceId, StringComparison.Ordinal);
        IntentionContextProjectInput.IsEnabled = canAct;
        IntentionContextTaskInput.IsEnabled = canAct;
        IntentionContextTriggerSetButton.IsEnabled = canAct &&
            _intentionResourceId.Length > 0 &&
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

    private string DescribeChosenIntention()
    {
        if (_intentionResourceId.Length == 0)
        {
            return "No intention chosen yet. Search above, or choose a row from the schedule list; the resource ID is shown here for copying, never required as input.";
        }

        var chosen = _session.IntentionCandidates.FirstOrDefault(hit =>
            string.Equals(hit.Id, _intentionResourceId, StringComparison.Ordinal));
        var scheduled = _session.ScheduledIntentions.FirstOrDefault(intention =>
            string.Equals(intention.ResourceId, _intentionResourceId, StringComparison.Ordinal));
        var title = chosen?.Title ?? scheduled?.Title;
        return (string.IsNullOrWhiteSpace(title) ? "(title not loaded)" : title) +
            "\nID " + _intentionResourceId +
            (scheduled is null ? "  ·  scheduled: no" : "  ·  lifecycle revision " + scheduled.LifecycleRevision.ToString(CultureInfo.InvariantCulture)) +
            (chosen is null ? string.Empty : "  ·  item revision " + chosen.Revision.ToString(CultureInfo.InvariantCulture));
    }

    private void ComposeIntentionDue()
    {
        if (IntentionTimeEntry.TryCompose(
                IntentionDateInput.SelectedDate,
                IntentionTimeInput.Text,
                IntentionZoneInput.SelectedItem as IntentionTimeOption,
                out _composedDueAtIso,
                out _composedDuePreview,
                out _composedDueProblem))
        {
            _composedDuePreview = "Due: " + _composedDuePreview;
            return;
        }

        // A partially filled form is not an error yet: the preview explains
        // what is still needed, and the send button stays disabled.
        _composedDuePreview = "Due time not ready: " + _composedDueProblem;
    }

    private void OnIntentionInputChanged(object sender, TextChangedEventArgs e) =>
        RenderIntentions(_session.Attached);

    private void OnIntentionTimeTextChanged(object sender, TextChangedEventArgs e) =>
        RenderIntentions(_session.Attached);

    private void OnIntentionTimeInputChanged(object sender, SelectionChangedEventArgs e) =>
        RenderIntentions(_session.Attached);

    private void OnIntentionZoneChanged(object sender, SelectionChangedEventArgs e) =>
        RenderIntentions(_session.Attached);

    private async void OnIntentionsRefreshClicked(object sender, RoutedEventArgs e) =>
        await _session.RefreshIntentionsAsync();

    private async void OnIntentionSearchClicked(object sender, RoutedEventArgs e) =>
        await _session.SearchIntentionCandidatesAsync(IntentionSearchInput.Text);

    private void OnIntentionCandidateSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IntentionCandidatesList.SelectedItem is LibraryResultRow row)
        {
            _intentionResourceId = row.Hit.Id;
            IntentionChosenText.Text = DescribeChosenIntention();
        }

        RenderIntentions(_session.Attached);
    }

    private void OnIntentionsSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingIntentionSelection)
        {
            return;
        }

        if (SelectedIntention() is { } selected)
        {
            UseIntention(selected.Intention);
        }

        RenderIntentions(_session.Attached);
    }

    private void OnIntentionUseSelectedClicked(object sender, RoutedEventArgs e)
    {
        if (SelectedIntention() is { } selected)
        {
            UseIntention(selected.Intention);
            RenderIntentions(_session.Attached);
        }
    }

    private async void OnIntentionTriggerSetClicked(object sender, RoutedEventArgs e)
    {
        if (_intentionResourceId.Length == 0)
        {
            IntentionsMessageText.Text = "No trigger was sent: choose the intention first (search above or pick a schedule row). Nothing was changed.";
            return;
        }

        if (_composedDueAtIso.Length == 0)
        {
            IntentionsMessageText.Text = "No trigger was sent. " + _composedDueProblem;
            return;
        }

        var selected = SelectedIntention();
        long? expectedRevision = selected is not null &&
            string.Equals(selected.Intention.ResourceId, _intentionResourceId, StringComparison.Ordinal)
                ? selected.Intention.LifecycleRevision
                : null;
        await _session.SetIntentionTimeTriggerAsync(_intentionResourceId, _composedDueAtIso, expectedRevision);
    }

    private async void OnIntentionContextTriggerSetClicked(object sender, RoutedEventArgs e)
    {
        var project = string.IsNullOrWhiteSpace(IntentionContextProjectInput.Text)
            ? null
            : IntentionContextProjectInput.Text.Trim();
        var task = string.IsNullOrWhiteSpace(IntentionContextTaskInput.Text)
            ? null
            : IntentionContextTaskInput.Text.Trim();
        if (_intentionResourceId.Length == 0 || (project is null && task is null))
        {
            IntentionsMessageText.Text = "No context trigger was sent: choose an intention and name at least one explicit project or task context. No context is inferred.";
            return;
        }

        var selected = SelectedIntention();
        long? expectedRevision = selected is not null &&
            string.Equals(selected.Intention.ResourceId, _intentionResourceId, StringComparison.Ordinal)
                ? selected.Intention.LifecycleRevision
                : null;
        await _session.EnableIntentionContextTriggerAsync(_intentionResourceId, project, task, expectedRevision);
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
            string.Equals(_intentionResourceId, selected.Intention.ResourceId, StringComparison.Ordinal))
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
            UseIntention(selected.Intention);
            RenderIntentions(_session.Attached);
            e.Handled = true;
        }
    }

    /// <summary>
    /// Fills the trigger form from one scheduled row: its identity, its stored
    /// wall clock and the offset it was saved with. The owner can then change
    /// any of them; the generated UTC instant is always shown before send.
    /// </summary>
    private void UseIntention(OwnerScheduledIntention intention)
    {
        _intentionResourceId = intention.ResourceId;
        if (intention.DueAt is { } due)
        {
            IntentionDateInput.SelectedDate = due.Date;
            IntentionTimeInput.Text = due.ToString("HH:mm", CultureInfo.InvariantCulture);
            SelectIntentionZone(due);
        }

        IntentionContextProjectInput.Text = intention.ContextProject ?? string.Empty;
        IntentionContextTaskInput.Text = intention.ContextTask ?? string.Empty;
        IntentionRationaleInput.Clear();
        IntentionChosenText.Text = DescribeChosenIntention();
    }

    private void SelectIntentionZone(DateTimeOffset due)
    {
        EnsureNoStoredIntentionZone();
        if (due.Offset == TimeSpan.Zero)
        {
            SelectIntentionZoneBy(option => !option.UseLocal && option.Offset == TimeSpan.Zero);
            return;
        }

        var local = DateTime.SpecifyKind(due.DateTime, DateTimeKind.Unspecified);
        if (!TimeZoneInfo.Local.IsInvalidTime(local) &&
            !TimeZoneInfo.Local.IsAmbiguousTime(local) &&
            TimeZoneInfo.Local.GetUtcOffset(local) == due.Offset)
        {
            SelectIntentionZoneBy(option => option.UseLocal);
            return;
        }

        var stored = IntentionTimeEntry.StoredOffsetOption(due.Offset);
        IntentionZoneInput.Items.Add(stored);
        IntentionZoneInput.SelectedItem = stored;
    }

    private void SelectIntentionZoneBy(Func<IntentionTimeOption, bool> match)
    {
        foreach (var option in IntentionZoneInput.Items.OfType<IntentionTimeOption>())
        {
            if (match(option))
            {
                IntentionZoneInput.SelectedItem = option;
                return;
            }
        }
    }

    private void EnsureNoStoredIntentionZone()
    {
        foreach (var option in IntentionZoneInput.Items.OfType<IntentionTimeOption>()
                     .Where(option => !option.UseLocal && option.Offset != TimeSpan.Zero)
                     .ToArray())
        {
            IntentionZoneInput.Items.Remove(option);
        }
    }

    /// <summary>
    /// Chooses one intention identity for the trigger form without a typed ID.
    /// The picker and the schedule row use the same identity; this entry point
    /// exists for callers that already hold a service answer.
    /// </summary>
    internal void SelectIntentionIdentity(string resourceId)
    {
        _intentionResourceId = resourceId;
        IntentionChosenText.Text = DescribeChosenIntention();
        RenderIntentions(_session.Attached);
    }

    /// <summary>
    /// Fills the date, time and offset from an explicit ISO-8601 instant that
    /// already carries its offset (a previous schedule answer). The form still
    /// recomposes and shows the resulting UTC instant before send.
    /// </summary>
    internal bool TrySetIntentionDue(string dueAtText)
    {
        if (!DateTimeOffset.TryParse(dueAtText, CultureInfo.InvariantCulture, DateTimeStyles.None, out var due))
        {
            return false;
        }

        IntentionDateInput.SelectedDate = due.Date;
        IntentionTimeInput.Text = due.ToString("HH:mm", CultureInfo.InvariantCulture);
        SelectIntentionZone(due);
        RenderIntentions(_session.Attached);
        return _composedDueAtIso.Length > 0;
    }

    private void ClearIntentionInputs()
    {
        _intentionResourceId = string.Empty;
        IntentionChosenText.Text = string.Empty;
        IntentionSearchInput.Clear();
        IntentionCandidatesList.SelectedItem = null;
        IntentionDateInput.SelectedDate = DateTime.Today;
        IntentionTimeInput.Clear();
        EnsureNoStoredIntentionZone();
        SelectIntentionZoneBy(option => option.UseLocal);
        IntentionContextProjectInput.Clear();
        IntentionContextTaskInput.Clear();
        IntentionRationaleInput.Clear();
        _composedDueAtIso = string.Empty;
        _composedDuePreview = string.Empty;
        _composedDueProblem = string.Empty;
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
