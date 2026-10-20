using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using MetaBrain.Connections;
using MetaBrain.Core.Domain;

namespace MetaBrain.OwnerUi;

/// <summary>
/// Jobs and automation view of the owner console (S6-T6): the model/egress
/// settings, one bounded job or a batch of one job per note, the real job list
/// with cancel, the spend ledger, and the synthesis schedule the owner enables.
///
/// Every act here is one request to the running service: this window decides no
/// due time, starts nothing by itself, keeps no key and never publishes. A run
/// is only ever the exact selection the owner typed on this screen.
/// </summary>
public partial class OwnerConsoleWindow : System.Windows.Window
{
    private OwnerJob[]? _renderedJobs;
    private readonly List<JobInputSelection> _jobInputs = new();

    /// <summary>
    /// The fixed enumerations this view offers are the service's own spellings
    /// (profiles and synthesis levels). The "(none)" level belongs to the
    /// single-note profiles, which the service refuses an objective or level for.
    /// </summary>
    private void InitializeJobsControls()
    {
        foreach (var profile in new[]
                 {
                     StructuringProfiles.ResearchNote,
                     StructuringProfiles.CodingSession,
                     StructuringProfiles.Trend,
                     StructuringProfiles.Synthesis
                 })
        {
            JobsProfileInput.Items.Add(profile);
        }

        JobsProfileInput.SelectedItem = StructuringProfiles.ResearchNote;
        foreach (var level in new[] { AnyFilterItem, SynthesisLevels.L2, SynthesisLevels.L3Plus })
        {
            JobsLevelInput.Items.Add(level);
        }

        JobsLevelInput.SelectedItem = AnyFilterItem;
        SyncJobsForm();
    }

    /// <summary>
    /// Empties the jobs drafts: the provider settings, the selection, the
    /// destination and the schedule interval. Always the whole draft, so no part
    /// of a finished epoch can be sent as if it belonged to this one.
    /// </summary>
    private void SyncJobsForm()
    {
        ProviderNameInput.Text = string.Empty;
        ProviderModelInput.Text = string.Empty;
        ProviderBaseUrlInput.Text = string.Empty;
        ProviderKeyInput.Clear();
        _jobInputs.Clear();
        JobsInputsList.ItemsSource = null;
        JobsObjectiveInput.Text = string.Empty;
        JobsZoneInput.Text = string.Empty;
        JobsDestinationInput.Text = string.Empty;
        JobsDestinationRevisionInput.Text = string.Empty;
        JobsProviderInput.Text = string.Empty;
        JobsModelInput.Text = string.Empty;
        ScheduleIntervalInput.Text = string.Empty;
    }

    private void OnJobsProfileChanged(object sender, SelectionChangedEventArgs e) => Render();

    private void OnProviderReadClicked(object sender, RoutedEventArgs e) =>
        _ = _session.RefreshProviderSettingsAsync();

    private async void OnProviderSaveClicked(object sender, RoutedEventArgs e)
    {
        var provider = ProviderNameInput.Text.Trim();
        var model = ProviderModelInput.Text.Trim();
        var baseUrl = ProviderBaseUrlInput.Text.Trim();
        if (provider.Length == 0 || model.Length == 0 || baseUrl.Length == 0)
        {
            _session.ReportJobsInputProblem(
                "Saving model settings needs the provider, the model and the endpoint you want; nothing was sent and the key field is unchanged.");
            return;
        }

        if (ProviderKeyInput.Password.Length == 0)
        {
            _session.ReportJobsInputProblem(
                "Saving model settings needs the API key in the key field; this console never sends an empty credential and keeps no copy of it.");
            return;
        }

        var pending = _session.SaveProviderSettingsAsync(provider, model, baseUrl, ProviderKeyInput.Password);
        if (_session.JobsActInFlight)
        {
            // The credential is already inside the one request; the field does
            // not keep it on screen afterwards.
            ProviderKeyInput.Clear();
        }

        await pending.ConfigureAwait(true);
    }

    /// <summary>
    /// Appends the open library item to the job inputs at its live revision.
    /// </summary>
    private void OnJobsAddLibraryClicked(object sender, RoutedEventArgs e)
    {
        if (_session.LibraryItem is not { } item)
        {
            _session.ReportJobsInputProblem(
                "No library item is open: open one in Library first, then add it here. Nothing was added.");
            return;
        }

        AppendJobsInput(item.Id, item.Revision, item.Title);
    }

    /// <summary>
    /// Appends the saved capture's registered source to the job inputs.
    /// </summary>
    private void OnJobsAddCaptureClicked(object sender, RoutedEventArgs e)
    {
        if (_session.CaptureSource is not { } saved)
        {
            _session.ReportJobsInputProblem(
                "No capture from this session is saved yet: save one in Inbox first, then add it here. Nothing was added.");
            return;
        }

        AppendJobsInput(saved.Id, saved.Revision, null);
    }

    private void OnJobsRemoveInputClicked(object sender, RoutedEventArgs e)
    {
        var selected = JobsInputsList.SelectedItems.OfType<JobInputSelection>().ToArray();
        if (selected.Length == 0)
        {
            _session.ReportJobsInputProblem(
                "No input was removed: choose the input row(s) to remove first. Nothing was sent.");
            return;
        }

        foreach (var input in selected)
        {
            _jobInputs.Remove(input);
        }

        RefreshJobInputs();
        Render();
    }

    private void OnJobsInputSelectionChanged(object sender, SelectionChangedEventArgs e) => Render();

    private void AppendJobsInput(string resourceId, long revision, string? title)
    {
        if (_jobInputs.Any(input => string.Equals(input.ResourceId, resourceId, StringComparison.Ordinal)))
        {
            _session.ReportJobsInputProblem(
                "That note is already in the selection: a job reads each input once. Nothing was added.");
            return;
        }

        _jobInputs.Add(new JobInputSelection(resourceId, revision, title));
        RefreshJobInputs();
        Render();
    }

    private void RefreshJobInputs()
    {
        JobsInputsList.ItemsSource = null;
        JobsInputsList.ItemsSource = _jobInputs.ToArray();
    }

    /// <summary>
    /// Replaces the job selection with the given already-served resource
    /// revisions. The buttons and cross-view actions use the same list; this
    /// entry point exists for callers that already hold a service answer (the
    /// console itself and the permanent WPF regression suite) and never accepts
    /// a typed resourceId@revision line.
    /// </summary>
    internal void SetJobInputSelection(params (string ResourceId, long Revision)[] inputs)
    {
        _jobInputs.Clear();
        foreach (var (resourceId, revision) in inputs)
        {
            _jobInputs.Add(new JobInputSelection(resourceId, revision, null));
        }

        RefreshJobInputs();
        Render();
    }

    /// <summary>
    /// One input of the owner's job selection: the exact resource the service
    /// served and the live revision the run pins. The title is display only;
    /// the service validates the ID and revision again.
    /// </summary>
    private sealed record JobInputSelection(string ResourceId, long Revision, string? Title)
    {
        public override string ToString() =>
            (string.IsNullOrWhiteSpace(Title) ? "(title not loaded)" : Title) + "  ·  " + ResourceId +
            " @ revision " + Revision.ToString(CultureInfo.InvariantCulture);
    }
    /// <summary>
    /// Fills the destination inbox from the open library item: the exact ID and
    /// live revision the service served. No ID or revision to memorize.
    /// </summary>
    private void OnJobsDestinationUseLibraryClicked(object sender, RoutedEventArgs e)
    {
        if (_session.LibraryItem is not { } item)
        {
            _session.ReportJobsInputProblem(
                "No library item is open: open one in Library first, then pick it here. Nothing was filled in.");
            return;
        }

        JobsDestinationInput.Text = item.Id;
        JobsDestinationRevisionInput.Text = item.Revision.ToString(CultureInfo.InvariantCulture);
        Render();
    }

    /// <summary>
    /// Refreshes the destination-zone picker from the zones the service named
    /// in this session, keeping the owner's typed text.
    /// </summary>
    private void RefreshJobsPickerOptions()
    {
        var zoneText = JobsZoneInput.Text;
        JobsZoneInput.Items.Clear();
        foreach (var zone in _session.KnownZones())
        {
            JobsZoneInput.Items.Add(zone);
        }

        JobsZoneInput.Text = zoneText;
    }
    private async void OnJobsRunClicked(object sender, RoutedEventArgs e) => await RunSelectedJobAsync();

    private async void OnJobsBatchClicked(object sender, RoutedEventArgs e) => await RunSelectedBatchAsync();

    private async void OnJobsRefreshClicked(object sender, RoutedEventArgs e)
    {
        await _session.RefreshJobsAsync().ConfigureAwait(true);
        await _session.RefreshSpendAsync().ConfigureAwait(true);
        await _session.RefreshScheduleAsync().ConfigureAwait(true);
    }

    private async void OnJobsSpendClicked(object sender, RoutedEventArgs e) => await _session.RefreshSpendAsync();

    private async void OnJobsCancelClicked(object sender, RoutedEventArgs e)
    {
        var job = (JobsList.SelectedItem as JobRow)?.Job ?? _session.JobsDetail;
        if (job is null)
        {
            _session.ReportJobsInputProblem(
                "Cancelling needs one selected job: choose the job row you want stopped. Nothing was sent.");
            return;
        }

        await _session.CancelJobAsync(job).ConfigureAwait(true);
    }

    private void OnJobsSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (JobsList.SelectedItem is JobRow row)
        {
            _ = _session.OpenJobAsync(row.Job);
        }
    }

    private async void OnJobsListKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter)
        {
            return;
        }

        e.Handled = true;
        if (JobsList.SelectedItem is JobRow row)
        {
            await _session.OpenJobAsync(row.Job).ConfigureAwait(true);
        }
    }

    private async void OnScheduleEnableClicked(object sender, RoutedEventArgs e)
    {
        if (!TryReadJobsSelection(out var inputs, out var zoneId, out var destinationId, out var destinationRevision,
                out var provider, out var model, out var objective, out var targetLevel, out var problem))
        {
            _session.ReportJobsInputProblem(problem!);
            return;
        }

        if (!TryReadScheduleInterval(out var intervalMinutes, out var intervalProblem))
        {
            _session.ReportJobsInputProblem(intervalProblem!);
            return;
        }

        await _session.EnableScheduleAsync(
            intervalMinutes, inputs, objective!, targetLevel!, zoneId, destinationId, destinationRevision, provider, model).ConfigureAwait(true);
    }

    private async void OnScheduleDisableClicked(object sender, RoutedEventArgs e) =>
        await _session.DisableScheduleAsync().ConfigureAwait(true);

    private async void OnScheduleRefreshClicked(object sender, RoutedEventArgs e) =>
        await _session.RefreshScheduleAsync().ConfigureAwait(true);

    private async Task RunSelectedJobAsync()
    {
        if (!TryReadJobsSelection(out var inputs, out var zoneId, out var destinationId, out var destinationRevision,
                out var provider, out var model, out var objective, out var targetLevel, out var problem))
        {
            _session.ReportJobsInputProblem(problem!);
            return;
        }

        var profile = SelectedProfile();
        if (!ValidateSelectionForProfile(profile, inputs, objective, targetLevel, isBatch: false, out var profileProblem))
        {
            _session.ReportJobsInputProblem(profileProblem!);
            return;
        }

        var request = new OwnerJobRunRequest(profile, inputs, zoneId, destinationId, destinationRevision, provider, model, objective, targetLevel);
        await _session.RunJobAsync(request).ConfigureAwait(true);
    }

    private async Task RunSelectedBatchAsync()
    {
        var profile = SelectedProfile();
        if (string.Equals(profile, StructuringProfiles.Synthesis, StringComparison.Ordinal))
        {
            _session.ReportJobsInputProblem(
                "A batch is one bounded job per selected note, and a synthesis job requires at least two selected inputs, so a synthesis batch would be refused by the service. Run one synthesis job over the whole selection instead.");
            return;
        }

        if (!TryReadJobsSelection(out var inputs, out var zoneId, out var destinationId, out var destinationRevision,
                out var provider, out var model, out var objective, out var targetLevel, out var problem))
        {
            _session.ReportJobsInputProblem(problem!);
            return;
        }

        _ = objective;
        _ = targetLevel;
        if (inputs.Length == 0)
        {
            _session.ReportJobsInputProblem(
                "A batch needs at least one note: add it from Library or Inbox above. Nothing was sent.");
            return;
        }

        await _session.RunBatchAsync(profile, inputs, zoneId, destinationId, destinationRevision, provider, model).ConfigureAwait(true);
    }

    /// <summary>
    /// Reads the selection the owner typed. Every line must name one exact
    /// resource pinned to a positive revision; a malformed line is refused with
    /// its own message instead of being guessed, and the destination, revision,
    /// and provider/model must be present.
    /// </summary>
    private bool TryReadJobsSelection(
        out OwnerJobInput[] inputs,
        out string zoneId,
        out string destinationId,
        out long destinationRevision,
        out string provider,
        out string model,
        out string? objective,
        out string? targetLevel,
        out string? problem)
    {
        inputs = Array.Empty<OwnerJobInput>();
        zoneId = JobsZoneInput.Text.Trim();
        destinationId = JobsDestinationInput.Text.Trim();
        provider = JobsProviderInput.Text.Trim();
        model = JobsModelInput.Text.Trim();
        objective = JobsObjectiveInput.Text.Trim();
        targetLevel = null;
        destinationRevision = 0;
        problem = null;

        var selected = new List<OwnerJobInput>();
        foreach (var input in _jobInputs)
        {
            if (string.IsNullOrWhiteSpace(input.ResourceId) || input.Revision < 1)
            {
                problem = "One selected input does not pin a positive revision, so this console sent nothing. Remove it and add the item again.";
                return false;
            }

            selected.Add(new OwnerJobInput(input.ResourceId, input.Revision));
        }

        inputs = selected.ToArray();
        if (zoneId.Length == 0 || destinationId.Length == 0)
        {
            problem = "A run needs the destination zone and the destination resource ID inside your own vault; nothing was sent.";
            return false;
        }

        if (!long.TryParse(JobsDestinationRevisionInput.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out destinationRevision) ||
            destinationRevision < 1)
        {
            problem = "The destination revision must be a positive integer (the revision of the destination item the service holds); nothing was sent.";
            return false;
        }

        if (provider.Length == 0 || model.Length == 0)
        {
            problem = "A run needs the provider and model the service will use; nothing was sent. Save the model settings first if the vault has none.";
            return false;
        }

        var levelItem = JobsLevelInput.SelectedItem as string;
        targetLevel = string.IsNullOrEmpty(levelItem) || string.Equals(levelItem, AnyFilterItem, StringComparison.Ordinal)
            ? null
            : levelItem;
        if (objective!.Length == 0)
        {
            objective = null;
        }

        return true;
    }

    private bool TryReadScheduleInterval(out int intervalMinutes, out string? problem)
    {
        intervalMinutes = 0;
        problem = null;
        if (!int.TryParse(ScheduleIntervalInput.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out intervalMinutes))
        {
            problem = "Enabling the schedule needs an interval in whole minutes (the service accepts " +
                SynthesisSchedulePolicy.MinimumIntervalMinutes.ToString(CultureInfo.InvariantCulture) + " to " +
                SynthesisSchedulePolicy.MaximumIntervalMinutes.ToString(CultureInfo.InvariantCulture) + "); nothing was sent.";
            return false;
        }

        if (!SynthesisSchedulePolicy.IsValidInterval(intervalMinutes))
        {
            problem = "The interval must be between " + SynthesisSchedulePolicy.MinimumIntervalMinutes.ToString(CultureInfo.InvariantCulture) +
                " and " + SynthesisSchedulePolicy.MaximumIntervalMinutes.ToString(CultureInfo.InvariantCulture) +
                " minutes; nothing was sent.";
            return false;
        }

        return true;
    }

    private string SelectedProfile() =>
        JobsProfileInput.SelectedItem as string ?? StructuringProfiles.ResearchNote;

    /// <summary>
    /// What this console can check before a request: a synthesis job names two or
    /// more inputs and the objective and level it must address, and a single-note
    /// profile names exactly one input and no objective or level. The service
    /// revalidates all of it.
    /// </summary>
    private static bool ValidateSelectionForProfile(
        string profile,
        OwnerJobInput[] inputs,
        string? objective,
        string? targetLevel,
        bool isBatch,
        out string? problem)
    {
        problem = null;
        if (string.Equals(profile, StructuringProfiles.Synthesis, StringComparison.Ordinal))
        {
            if (inputs.Length < 2)
            {
                problem = "A synthesis job needs at least two selected inputs, each pinned to a revision; nothing was sent.";
                return false;
            }

            if (objective is null)
            {
                problem = "A synthesis job needs the objective you want addressed; nothing was sent, and no agenda is invented for you.";
                return false;
            }

            if (targetLevel is null)
            {
                problem = "A synthesis job needs the level you request (" + SynthesisLevels.L2 + " or " + SynthesisLevels.L3Plus + "); nothing was sent.";
                return false;
            }

            return true;
        }

        if (inputs.Length != 1)
        {
            problem = isBatch
                ? "Each note of a batch is its own single-input job, so a batch needs at least one selected note."
                : "The profile " + profile + " reads exactly one selected input; select one note, or choose the synthesis profile for several.";
            return false;
        }

        if (objective is not null || targetLevel is not null)
        {
            problem = "An objective and a level belong to a synthesis job only; clear them or choose the synthesis profile.";
            return false;
        }

        return true;
    }

    private void RenderJobs(bool attached)
    {
        var state = _session.ReportedVaultState;
        var inFlight = _session.JobsActInFlight;
        var unlocked = state == OwnerVaultState.Unlocked;

        JobsStateText.Text = state switch
        {
            OwnerVaultState.Unlocked =>
                "Vault: unlocked — the service serves the job list, the model settings, the spend ledger and your schedule. No job runs and no provider is called until you start one.",
            OwnerVaultState.Locked =>
                "Vault: locked — no job list, schedule or spend view is available, and no scheduled run happens while locked. Unlock in Vault & key to use this console.",
            OwnerVaultState.Locking =>
                "Vault: locking — the service is draining in-flight private work, including any running job; the views of this screen are cleared.",
            OwnerVaultState.Uninitialized =>
                "Vault: not provisioned — there is no vault and therefore no job to run.",
            _ => attached
                ? "Vault: unknown — no usable owner-channel answer, so no jobs view is shown."
                : "Vault: not reported — this console holds no owner session."
        };
        RefreshJobsPickerOptions();

        var settings = _session.ProviderSettings;
        ProviderSettingsText.Text = settings is null
            ? "No model settings have been read in this session."
            : settings.Configured
                ? "configured=true provider=" + settings.Provider + " model=" + settings.Model + " baseUrl=" + settings.BaseUrl +
                    " caps: jobUsd=" + settings.MaximumJobCostUsd.ToString("0.####", CultureInfo.InvariantCulture) +
                    " maxIn=" + settings.MaximumJobInputTokens.ToString(CultureInfo.InvariantCulture) +
                    " maxOut=" + settings.MaximumJobOutputTokens.ToString(CultureInfo.InvariantCulture) +
                    " rolling30d=" + settings.MaximumRolling30DayCostUsd.ToString("0.####", CultureInfo.InvariantCulture)
                : "configured=false (the vault holds no provider configuration)";
        RenderOutcome(ProviderMessageText, _session.ProviderMessage ?? (unlocked
            ? "No model-settings act has been sent in this session. Saving settings stores the key encrypted; no key is ever shown back."
            : "No model-settings act is offered while the service does not report the vault unlocked."));

        if (!ReferenceEquals(_renderedJobs, _session.Jobs))
        {
            _renderedJobs = _session.Jobs;
            var previousId = (JobsList.SelectedItem as JobRow)?.Job.Id;
            var rows = _session.Jobs.Select(job => new JobRow(job)).ToArray();
            JobsList.ItemsSource = rows;
            JobsList.SelectedItem = previousId is null
                ? null
                : rows.FirstOrDefault(row => string.Equals(row.Job.Id, previousId, StringComparison.Ordinal));
        }

        RenderOutcome(JobsMessageText, _session.JobsMessage ?? (unlocked
            ? "No job act has been sent in this session. A run reads only the IDs you added at the revisions they pin."
            : "No job act is offered while the service does not report the vault unlocked."));

        BatchMessageText.Text = _session.BatchSteps.Length == 0
            ? string.Empty
            : string.Join("\n", _session.BatchSteps.Select(step =>
                "note " + step.ResourceId + "@" + step.Revision.ToString(CultureInfo.InvariantCulture) +
                " job=" + (step.JobId ?? "(none)") + " state=" + step.State +
                (step.FailureCode is null ? string.Empty : " fail=" + step.FailureCode))) +
                (_session.BatchMessage is null ? string.Empty : "\n" + _session.BatchMessage);

        JobDetailText.Text = DescribeJobDetail(_session.JobsDetail);
        SpendText.Text = _session.SpendMessage ?? "No spend read has been served in this session.";

        var schedule = _session.Schedule;
        ScheduleStateText.Text = schedule is null
            ? "schedule=none"
            : "schedule enabled=" + (schedule.Enabled ? "true" : "false") +
                " intervalMinutes=" + schedule.IntervalMinutes.ToString(CultureInfo.InvariantCulture) +
                " level=" + schedule.TargetLevel +
                " inputs=" + schedule.Inputs.Length.ToString(CultureInfo.InvariantCulture) +
                " destination=" + schedule.DestinationResourceId + "@" + schedule.DestinationRevision.ToString(CultureInfo.InvariantCulture) +
                " nextDue=" + (schedule.NextDueAtUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "none") +
                " lastRun=" + (schedule.LastRunAtUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "none") +
                " lastJob=" + (schedule.LastRunJobId ?? "none") +
                " lastOutcome=" + (schedule.LastRunOutcome ?? "none");
        RenderOutcome(ScheduleMessageText, _session.ScheduleMessage ?? (unlocked
            ? "No schedule act has been sent in this session. The schedule is off until you enable it here."
            : "No schedule act is offered while the service does not report the vault unlocked."));

        ProviderSaveButton.IsEnabled = unlocked && !inFlight;
        ProviderReadButton.IsEnabled = unlocked && !inFlight;
        JobsInputsList.IsEnabled = unlocked && !inFlight;
        JobsAddLibraryButton.IsEnabled = unlocked && !inFlight;
        JobsAddCaptureButton.IsEnabled = unlocked && !inFlight;
        JobsRemoveInputButton.IsEnabled = unlocked && !inFlight && JobsInputsList.SelectedItems.Count > 0;
        JobsDestinationUseLibraryButton.IsEnabled = unlocked && !inFlight;
        JobsRunButton.IsEnabled = unlocked && !inFlight;
        JobsBatchButton.IsEnabled = unlocked && !inFlight;
        JobsRefreshButton.IsEnabled = unlocked && !inFlight;
        JobsSpendButton.IsEnabled = unlocked && !inFlight;
        ScheduleEnableButton.IsEnabled = unlocked && !inFlight;
        ScheduleDisableButton.IsEnabled = unlocked && !inFlight && schedule is { Enabled: true };
        ScheduleRefreshButton.IsEnabled = unlocked && !inFlight;
        JobsCancelButton.IsEnabled = unlocked && ((JobsList.SelectedItem as JobRow)?.Job ?? _session.JobsDetail) is not null;
    }

    /// <summary>
    /// One job exactly as the service recorded it: identity, state, the scope it
    /// was allowed to read, the usage and cost it reported, its failure reason and
    /// the proposals it committed. Never a proposed body.
    /// </summary>
    private static string DescribeJobDetail(OwnerJob? job)
    {
        if (job is null)
        {
            return "No job is open. Choose a row to see its exact state, scope, usage and the proposals it committed.";
        }

        var inputs = job.Scope.Inputs.Length == 0
            ? "(none recorded)"
            : string.Join(", ", job.Scope.Inputs.Select(input =>
                input.ResourceId + "@" + input.Revision.ToString(CultureInfo.InvariantCulture)));
        var usage = job.Usage is { } reported
            ? "in=" + reported.InputTokens.ToString(CultureInfo.InvariantCulture) +
                " out=" + reported.OutputTokens.ToString(CultureInfo.InvariantCulture) +
                " costUsd=" + reported.CostUsd.ToString("0.######", CultureInfo.InvariantCulture)
            : "in=0 out=0 costUsd=0 (no usage reported)";
        var failure = job.Failure is { } reason ? " failure=" + reason.Code + ": " + reason.Message : string.Empty;
        var proposals = job.OutputProposalIds.Length == 0
            ? " proposals=(none)"
            : " proposals=" + string.Join(",", job.OutputProposalIds);
        return "job " + job.Id + " state=" + job.State + " kind=" + job.Kind +
            " profile=" + (job.Scope.Profile ?? "(none)") +
            " level=" + (job.Scope.TargetLevel ?? "(none)") +
            "\ninputs=" + inputs +
            "\ndestination=" + job.Scope.DestinationResourceId + "@" + job.Scope.DestinationRevision.ToString(CultureInfo.InvariantCulture) +
            " zone=" + job.Scope.DestinationZoneId +
            "\nprovider=" + job.Scope.Provider + " model=" + job.Scope.Model +
            " caps: jobUsd=" + job.Scope.MaximumCostUsd.ToString("0.####", CultureInfo.InvariantCulture) +
            " maxIn=" + job.Scope.MaxInputTokens.ToString(CultureInfo.InvariantCulture) +
            " maxOut=" + job.Scope.MaxOutputTokens.ToString(CultureInfo.InvariantCulture) +
            "\n" + usage + failure +
            "\ncreated=" + job.CreatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) +
            " started=" + (job.StartedAtUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "none") +
            " completed=" + (job.CompletedAtUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "none") +
            proposals +
            (job.Scope.Objective is null ? string.Empty : "\nobjective=" + job.Scope.Objective);
    }

    private async Task RefreshJobsForKeyboardAsync()
    {
        await _session.RefreshJobsAsync().ConfigureAwait(true);
        await _session.RefreshSpendAsync().ConfigureAwait(true);
        await _session.RefreshScheduleAsync().ConfigureAwait(true);
        await _session.RefreshProviderSettingsAsync().ConfigureAwait(true);
    }

    /// <summary>One job row exactly as the service listed it, rendered as one line.</summary>
    private sealed record JobRow(OwnerJob Job)
    {
        public override string ToString() =>
            Job.Id + "  state=" + Job.State +
            "  profile=" + (Job.Scope.Profile ?? "-") +
            "  inputs=" + Job.Scope.Inputs.Length.ToString(CultureInfo.InvariantCulture) +
            (Job.Usage is { } usage
                ? "  cost=" + usage.CostUsd.ToString("0.######", CultureInfo.InvariantCulture)
                : string.Empty) +
            (Job.Failure is { } failure ? "  fail=" + failure.Code : string.Empty) +
            "  created=" + Job.CreatedAtUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
    }
}
