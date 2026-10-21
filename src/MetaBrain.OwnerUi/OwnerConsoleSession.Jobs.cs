using System.Globalization;
using MetaBrain.Connections;

namespace MetaBrain.OwnerUi;

/// <summary>One bounded job this console asks the service to run.</summary>
internal sealed record OwnerJobRunRequest(
    string Profile,
    OwnerJobInput[] Inputs,
    string ZoneId,
    string DestinationResourceId,
    long DestinationRevision,
    string Provider,
    string Model,
    string? Objective,
    string? TargetLevel);

/// <summary>What one note of a batch run produced, as the service reported it.</summary>
internal sealed record OwnerBatchStep(string ResourceId, long Revision, string? JobId, string State, string? FailureCode);

/// <summary>
/// Model jobs, provider/egress settings and the owner-enabled synthesis schedule
/// (S6-T6).
///
/// Every value here is an answer of the running service over the owner control
/// pipe: the service owns the job lifecycle, the encrypted provider
/// configuration, the encrypted schedule record and every due check. This
/// session holds no policy of its own, never decides that a scheduled run is
/// due, never cancels, publishes or resolves by itself, keeps no copy of the
/// API key after the one request that stores it, and drops every private view
/// of this screen with the private epoch it belonged to.
///
/// A batch is not a second job kind: it is one ordinary bounded job per selected
/// note, each created and started through the same shared lifecycle with its own
/// recorded state, so one failed note never hides another.
/// </summary>
internal sealed partial class OwnerConsoleSession
{
    private static readonly TimeSpan JobsActTimeout = TimeSpan.FromSeconds(60);

    private long _jobsFormVersion;

    /// <summary>True while one jobs act is in flight; further acts wait for its answer.</summary>
    public bool JobsActInFlight { get; private set; }

    /// <summary>Moves whenever this session drops the jobs form of an ended epoch.</summary>
    public long JobsFormVersion => _jobsFormVersion;

    /// <summary>The model/egress settings the service reported; never the API key.</summary>
    public OwnerProviderSettings? ProviderSettings { get; private set; }

    public string? ProviderMessage { get; private set; }

    /// <summary>The jobs the service listed, newest first. Never a proposed body.</summary>
    public OwnerJob[] Jobs { get; private set; } = Array.Empty<OwnerJob>();

    public string? JobsMessage { get; private set; }

    /// <summary>The job the owner opened, exactly as the service served it.</summary>
    public OwnerJob? JobsDetail { get; private set; }

    /// <summary>The job identity a run created and has not finished reporting on.</summary>
    public string? LastCreatedJobId { get; private set; }

    /// <summary>One line per note of the last batch, in the order the owner selected them.</summary>
    public OwnerBatchStep[] BatchSteps { get; private set; } = Array.Empty<OwnerBatchStep>();

    public string? BatchMessage { get; private set; }

    /// <summary>The encrypted spend ledger the service served.</summary>
    public OwnerSpendLedger? SpendLedger { get; private set; }

    public string? SpendMessage { get; private set; }

    /// <summary>The owner's stored schedule; null when the service reports none.</summary>
    public OwnerSchedule? Schedule { get; private set; }

    public string? ScheduleMessage { get; private set; }

    // ---- provider / egress settings ----

    /// <summary>Reads the configured model/egress settings and the frozen caps; never the key.</summary>
    public async Task RefreshProviderSettingsAsync()
    {
        if (TryBeginJobsAct() is not { } epoch)
        {
            return;
        }

        ProviderMessage = null;
        OwnerProviderResult result;
        try
        {
            result = await ExecuteJobsCallAsync(
                token => OwnerChannelClient.ReadProviderSettingsAsync(ControlPipe, token),
                code => new OwnerProviderResult(OwnerCommandOutcome.Unavailable, code, null)).ConfigureAwait(true);
        }
        finally
        {
            JobsActInFlight = false;
        }

        await ProbeAsync().ConfigureAwait(true);
        ApplyProviderSettingsAnswer(epoch, result);
        Append(DescribeInboxActForLog("read provider settings", result.Outcome, result.ErrorCode));
        RaiseChanged();
    }

    /// <summary>
    /// Stores the provider, model, endpoint and the credential the owner typed
    /// in one request. The service stores them encrypted and never returns the
    /// key, so this console reads the settings back without it and keeps no copy
    /// of the credential.
    /// </summary>
    public async Task SaveProviderSettingsAsync(string provider, string model, string baseUrl, string apiKey)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(apiKey);
        if (TryBeginJobsAct() is not { } epoch)
        {
            return;
        }

        ProviderMessage = null;
        OwnerProviderResult result;
        try
        {
            result = await ExecuteJobsCallAsync(
                token => OwnerChannelClient.SaveProviderSettingsAsync(ControlPipe, provider, model, baseUrl, apiKey, token),
                code => new OwnerProviderResult(OwnerCommandOutcome.Unavailable, code, null)).ConfigureAwait(true);
        }
        finally
        {
            JobsActInFlight = false;
        }

        await ProbeAsync().ConfigureAwait(true);
        if (epoch != _privateEpoch)
        {
            AppendIgnoredStaleInboxAnswer("save provider settings");
            RaiseChanged();
            return;
        }

        if (result.Outcome != OwnerCommandOutcome.Completed)
        {
            ProviderMessage = DescribeInboxAnswer("save provider settings", result.Outcome, result.ErrorCode);
            Append(DescribeInboxActForLog("save provider settings", result.Outcome, result.ErrorCode));
            RaiseChanged();
            return;
        }

        ProviderMessage = "The service stored the provider settings in the encrypted vault. It returns no key, so nothing on this screen shows or keeps the credential.";
        Append(DescribeInboxActForLog("save provider settings", result.Outcome, result.ErrorCode));
        RaiseChanged();
        await RefreshProviderSettingsAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Reports an input this console refuses to send, so the owner sees which
    /// field is unusable instead of a run that silently differs from what they
    /// typed. Never private content.
    /// </summary>
    public void ReportJobsInputProblem(string message)
    {
        JobsMessage = message;
        RaiseChanged();
    }

    internal bool ApplyProviderSettingsAnswer(long epoch, OwnerProviderResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (epoch != _privateEpoch)
        {
            AppendIgnoredStaleInboxAnswer("provider settings");
            return false;
        }

        if (result.Outcome == OwnerCommandOutcome.Completed && result.Settings is not null)
        {
            ProviderSettings = result.Settings;
            ProviderMessage = result.Settings.Configured
                ? "Configured: " + result.Settings.Provider + "/" + result.Settings.Model +
                    " at " + result.Settings.BaseUrl +
                    ". Caps enforced before every send: $" + result.Settings.MaximumJobCostUsd.ToString("0.####", CultureInfo.InvariantCulture) +
                    " per job, " + result.Settings.MaximumJobInputTokens.ToString(CultureInfo.InvariantCulture) + " input tokens, " +
                    result.Settings.MaximumJobOutputTokens.ToString(CultureInfo.InvariantCulture) + " output tokens, $" +
                    result.Settings.MaximumRolling30DayCostUsd.ToString("0.####", CultureInfo.InvariantCulture) +
                    " rolling 30 days. The API key is stored encrypted and is not shown."
                : "No provider is configured, so no model job can start and nothing can be sent.";
        }
        else
        {
            ProviderSettings = null;
            ProviderMessage = DescribeInboxAnswer("provider settings", result.Outcome, result.ErrorCode);
        }

        return true;
    }

    // ---- jobs ----

    /// <summary>Asks the service for every job it holds.</summary>
    public async Task RefreshJobsAsync()
    {
        if (TryBeginJobsAct() is not { } epoch)
        {
            return;
        }

        JobsMessage = null;
        OwnerJobListResult result;
        try
        {
            result = await ExecuteJobsCallAsync(
                token => OwnerChannelClient.ListJobsAsync(ControlPipe, token),
                code => new OwnerJobListResult(OwnerCommandOutcome.Unavailable, code, null)).ConfigureAwait(true);
        }
        finally
        {
            JobsActInFlight = false;
        }

        await ProbeAsync().ConfigureAwait(true);
        ApplyJobsListAnswer(epoch, result);
        Append(DescribeInboxActForLog("list jobs", result.Outcome, result.ErrorCode));
        RaiseChanged();
    }

    /// <summary>Reads one job at its live revision: state, scope, usage and failure.</summary>
    public async Task OpenJobAsync(OwnerJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (TryBeginJobsAct() is not { } epoch)
        {
            return;
        }

        OwnerJobResult result;
        try
        {
            result = await ExecuteJobsCallAsync(
                token => OwnerChannelClient.ReadJobAsync(ControlPipe, job.Id, token),
                code => new OwnerJobResult(OwnerCommandOutcome.Unavailable, code, null)).ConfigureAwait(true);
        }
        finally
        {
            JobsActInFlight = false;
        }

        await ProbeAsync().ConfigureAwait(true);
        ApplyJobDetailAnswer(epoch, result);
        Append(DescribeInboxActForLog("read job", result.Outcome, result.ErrorCode));
        RaiseChanged();
    }

    /// <summary>
    /// Runs one bounded job from the owner's explicit selection: the selected
    /// inputs each pinned to a revision, the destination inbox, and — for a
    /// synthesis job — the objective and requested level. Nothing is discovered
    /// or expanded, and the output is a pending proposal.
    /// </summary>
    public async Task RunJobAsync(OwnerJobRunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (TryBeginJobsAct() is not { } epoch)
        {
            return;
        }

        JobsMessage = null;
        BatchMessage = null;
        OwnerJobResult result;
        try
        {
            result = await ExecuteJobsCallAsync(
                token => RunOneJobAsync(request, token),
                code => new OwnerJobResult(OwnerCommandOutcome.Unavailable, code, null)).ConfigureAwait(true);
        }
        finally
        {
            JobsActInFlight = false;
        }

        await ProbeAsync().ConfigureAwait(true);
        ApplyJobRunAnswer(epoch, result);
        var outcomeMessage = JobsMessage;
        Append(DescribeInboxActForLog("run job", result.Outcome, result.ErrorCode));
        RaiseChanged();
        if (result.Outcome == OwnerCommandOutcome.Completed)
        {
            // The refreshed list also rewrites the panel's message, so the act's
            // own outcome is restored last: the owner keeps what the run did.
            await RefreshJobsAsync().ConfigureAwait(true);
            JobsMessage = outcomeMessage;
            RaiseChanged();
        }
    }

    /// <summary>
    /// Runs one ordinary bounded job per selected note, in the order the owner
    /// listed them. Each note keeps its own job identity and its own reported
    /// state, so a refusal or failure on one note is never hidden by another and
    /// nothing is retried automatically.
    /// </summary>
    public async Task RunBatchAsync(
        string profile,
        OwnerJobInput[] inputs,
        string zoneId,
        string destinationResourceId,
        long destinationRevision,
        string provider,
        string model)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(zoneId);
        ArgumentNullException.ThrowIfNull(destinationResourceId);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(model);
        if (TryBeginJobsAct() is not { } epoch)
        {
            return;
        }

        JobsMessage = null;
        BatchMessage = null;
        BatchSteps = Array.Empty<OwnerBatchStep>();
        var steps = new List<OwnerBatchStep>();
        try
        {
            foreach (var input in inputs)
            {
                if (epoch != _privateEpoch)
                {
                    break;
                }

                var request = new OwnerJobRunRequest(
                    profile,
                    new[] { input },
                    zoneId,
                    destinationResourceId,
                    destinationRevision,
                    provider,
                    model,
                    Objective: null,
                    TargetLevel: null);
                var result = await ExecuteJobsCallAsync(
                    token => RunOneJobAsync(request, token),
                    code => new OwnerJobResult(OwnerCommandOutcome.Unavailable, code, null)).ConfigureAwait(true);
                steps.Add(new OwnerBatchStep(
                    input.ResourceId,
                    input.Revision,
                    result.Job?.Id,
                    result.Job?.State ?? result.Outcome.ToString(),
                    result.Job?.Failure?.Code ?? result.ErrorCode));
                BatchSteps = steps.ToArray();
                RaiseChanged();
            }
        }
        finally
        {
            JobsActInFlight = false;
        }

        await ProbeAsync().ConfigureAwait(true);
        if (epoch != _privateEpoch)
        {
            AppendIgnoredStaleInboxAnswer("batch run");
            RaiseChanged();
            return;
        }

        var completed = BatchSteps.Count(step => string.Equals(step.State, "Succeeded", StringComparison.Ordinal));
        BatchMessage = "Batch finished: " + BatchSteps.Length.ToString(CultureInfo.InvariantCulture) + " note(s), " +
            completed.ToString(CultureInfo.InvariantCulture) + " job(s) succeeded. Every note is one bounded job with its own state; " +
            "each succeeded job committed pending proposal(s) that wait for your review in the Inbox, and no note was retried automatically.";
        Append("owner act: run batch -> " + BatchSteps.Length.ToString(CultureInfo.InvariantCulture) + " job(s) attempted");
        RaiseChanged();
        await RefreshJobsAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Cancels one job the owner selected. This is the owner's stop act: it is
    /// allowed while another jobs act is in flight, because stopping a running
    /// job must not wait for the act that started it.
    /// </summary>
    public async Task CancelJobAsync(OwnerJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (!Attached || ReportedVaultState != OwnerVaultState.Unlocked)
        {
            return;
        }

        var epoch = _privateEpoch;
        JobsMessage = null;
        LastCreatedJobId = null;
        OwnerJobResult result;
        try
        {
            result = await ExecuteJobsCallAsync(
                token => OwnerChannelClient.CancelJobAsync(ControlPipe, job.Id, token),
                code => new OwnerJobResult(OwnerCommandOutcome.Unavailable, code, null)).ConfigureAwait(true);
        }
        finally
        {
            JobsActInFlight = false;
        }

        await ProbeAsync().ConfigureAwait(true);
        if (epoch != _privateEpoch)
        {
            AppendIgnoredStaleInboxAnswer("cancel job");
            RaiseChanged();
            return;
        }

        if (result.Outcome == OwnerCommandOutcome.Completed && result.Job is not null)
        {
            JobsDetail = result.Job;
            JobsMessage = string.Equals(result.Job.State, "Cancelled", StringComparison.Ordinal)
                ? "The service reports job " + result.Job.Id + " as Cancelled. A cancelled job commits no new output; billable spend already sent stays recorded and is never hidden."
                : "The service reports job " + result.Job.Id + " as " + result.Job.State +
                    ": a job that already finished is returned unchanged, and cancelling cannot undo published content.";
        }
        else
        {
            JobsMessage = DescribeInboxAnswer("cancel job", result.Outcome, result.ErrorCode, result.ErrorMessage);
        }

        Append(DescribeInboxActForLog("cancel job", result.Outcome, result.ErrorCode));
        RaiseChanged();
        if (result.Outcome == OwnerCommandOutcome.Completed)
        {
            var cancelMessage = JobsMessage;
            await RefreshJobsAsync().ConfigureAwait(true);
            JobsMessage = cancelMessage;
            RaiseChanged();
        }
    }

    /// <summary>Reads the encrypted spend ledger: reservations, settlements and the rolling spend.</summary>
    public async Task RefreshSpendAsync()
    {
        if (TryBeginJobsAct() is not { } epoch)
        {
            return;
        }

        SpendMessage = null;
        OwnerSpendLedgerResult result;
        try
        {
            result = await ExecuteJobsCallAsync(
                token => OwnerChannelClient.ReadSpendLedgerAsync(ControlPipe, token),
                code => new OwnerSpendLedgerResult(OwnerCommandOutcome.Unavailable, code, null)).ConfigureAwait(true);
        }
        finally
        {
            JobsActInFlight = false;
        }

        await ProbeAsync().ConfigureAwait(true);
        ApplySpendAnswer(epoch, result);
        Append(DescribeInboxActForLog("read spend ledger", result.Outcome, result.ErrorCode));
        RaiseChanged();
    }

    internal bool ApplyJobsListAnswer(long epoch, OwnerJobListResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (epoch != _privateEpoch)
        {
            AppendIgnoredStaleInboxAnswer("jobs list");
            return false;
        }

        if (result.Outcome == OwnerCommandOutcome.Completed && result.Jobs is not null)
        {
            Jobs = result.Jobs;
            JobsMessage = result.Jobs.Length == 0
                ? "The service reports no model job: nothing has been structured in this vault yet."
                : "The service reports " + result.Jobs.Length.ToString(CultureInfo.InvariantCulture) +
                    " job(s), newest first. A job state is the service's record, and a succeeded job means pending proposals, never published content.";
        }
        else
        {
            JobsMessage = DescribeInboxAnswer("jobs list", result.Outcome, result.ErrorCode);
        }

        return true;
    }

    internal bool ApplyJobDetailAnswer(long epoch, OwnerJobResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (epoch != _privateEpoch)
        {
            AppendIgnoredStaleInboxAnswer("job detail");
            return false;
        }

        if (result.Outcome == OwnerCommandOutcome.Completed && result.Job is not null)
        {
            JobsDetail = result.Job;
            return true;
        }

        JobsDetail = null;
        JobsMessage = DescribeInboxAnswer("read job", result.Outcome, result.ErrorCode, result.ErrorMessage);
        return true;
    }

    internal bool ApplyJobRunAnswer(long epoch, OwnerJobResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (epoch != _privateEpoch)
        {
            AppendIgnoredStaleInboxAnswer("run job");
            return false;
        }

        if (result.Outcome == OwnerCommandOutcome.Completed && result.Job is not null)
        {
            JobsDetail = result.Job;
            LastCreatedJobId = null;
            JobsMessage = "The service ran job " + result.Job.Id + " (state " + result.Job.State + ")" +
                (result.Job.Usage is { } usage
                    ? " with " + usage.InputTokens.ToString(CultureInfo.InvariantCulture) + " input / " +
                        usage.OutputTokens.ToString(CultureInfo.InvariantCulture) + " output tokens and $" +
                        usage.CostUsd.ToString("0.######", CultureInfo.InvariantCulture) + " recorded spend"
                    : string.Empty) +
                (result.Job.Failure is { } failure ? "; failure " + failure.Code + ": " + failure.Message : string.Empty) +
                ". A succeeded job commits pending proposal(s) for review; nothing is published or resolved here.";
        }
        else if (result.Outcome == OwnerCommandOutcome.Completed)
        {
            JobsMessage = "The service completed the run without returning a job record, so this console shows nothing instead of guessing. A job created but not started stays Pending and can be cancelled from the list.";
        }
        else
        {
            JobsMessage = DescribeInboxAnswer("run job", result.Outcome, result.ErrorCode, result.ErrorMessage) +
                (LastCreatedJobId is { } jobId
                    ? " The created job " + jobId + " was not started and stays Pending; it can be cancelled from the list."
                    : string.Empty);
        }

        return true;
    }

    internal bool ApplySpendAnswer(long epoch, OwnerSpendLedgerResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (epoch != _privateEpoch)
        {
            AppendIgnoredStaleInboxAnswer("spend ledger");
            return false;
        }

        if (result.Outcome == OwnerCommandOutcome.Completed && result.Ledger is not null)
        {
            SpendLedger = result.Ledger;
            SpendMessage = "Rolling spend the service reports: $" +
                result.Ledger.RollingSpendUsd.ToString("0.######", CultureInfo.InvariantCulture) +
                " (including reservations)," + result.Ledger.Entries.Length.ToString(CultureInfo.InvariantCulture) +
                " ledger entr(ies). Reservations retained after an interrupted send are shown, never hidden.";
        }
        else
        {
            SpendLedger = null;
            SpendMessage = DescribeInboxAnswer("spend ledger", result.Outcome, result.ErrorCode);
        }

        return true;
    }

    // ---- owner-enabled synthesis schedule ----

    /// <summary>
    /// Enables the owner's synthesis schedule from the same explicit selection a
    /// manual job uses, plus the interval the owner chose. The service validates
    /// the interval, the selection and the destination against shared policy and
    /// stores one encrypted record; nothing runs at this moment.
    /// </summary>
    public async Task EnableScheduleAsync(
        int intervalMinutes,
        OwnerJobInput[] inputs,
        string objective,
        string targetLevel,
        string zoneId,
        string destinationResourceId,
        long destinationRevision,
        string provider,
        string model)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(objective);
        ArgumentNullException.ThrowIfNull(targetLevel);
        ArgumentNullException.ThrowIfNull(zoneId);
        ArgumentNullException.ThrowIfNull(destinationResourceId);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(model);
        if (TryBeginJobsAct() is not { } epoch)
        {
            return;
        }

        ScheduleMessage = null;
        OwnerScheduleResult result;
        try
        {
            result = await ExecuteJobsCallAsync(
                token => OwnerChannelClient.SaveScheduleAsync(
                    ControlPipe, intervalMinutes, inputs, objective, targetLevel,
                    zoneId, destinationResourceId, destinationRevision, provider, model, token),
                code => new OwnerScheduleResult(OwnerCommandOutcome.Unavailable, code, null)).ConfigureAwait(true);
        }
        finally
        {
            JobsActInFlight = false;
        }

        await ProbeAsync().ConfigureAwait(true);
        ApplyScheduleAnswer(epoch, result, "enable synthesis schedule");
        Append(DescribeInboxActForLog("enable synthesis schedule", result.Outcome, result.ErrorCode));
        RaiseChanged();
        if (result.Outcome == OwnerCommandOutcome.Completed)
        {
            await RefreshScheduleAsync().ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Disables the owner's schedule while keeping its configuration. This is what
    /// stops scheduled runs: a disabled schedule is never due.
    /// </summary>
    public async Task DisableScheduleAsync()
    {
        if (TryBeginJobsAct() is not { } epoch)
        {
            return;
        }

        ScheduleMessage = null;
        OwnerScheduleResult result;
        try
        {
            result = await ExecuteJobsCallAsync(
                token => OwnerChannelClient.DisableScheduleAsync(ControlPipe, token),
                code => new OwnerScheduleResult(OwnerCommandOutcome.Unavailable, code, null)).ConfigureAwait(true);
        }
        finally
        {
            JobsActInFlight = false;
        }

        await ProbeAsync().ConfigureAwait(true);
        ApplyScheduleAnswer(epoch, result, "disable synthesis schedule");
        Append(DescribeInboxActForLog("disable synthesis schedule", result.Outcome, result.ErrorCode));
        RaiseChanged();
        if (result.Outcome == OwnerCommandOutcome.Completed)
        {
            await RefreshScheduleAsync().ConfigureAwait(true);
        }
    }

    /// <summary>Reads the owner's stored schedule, or reports that none is configured.</summary>
    public async Task RefreshScheduleAsync()
    {
        if (TryBeginJobsAct() is not { } epoch)
        {
            return;
        }

        ScheduleMessage = null;
        OwnerScheduleResult result;
        try
        {
            result = await ExecuteJobsCallAsync(
                token => OwnerChannelClient.ReadScheduleAsync(ControlPipe, token),
                code => new OwnerScheduleResult(OwnerCommandOutcome.Unavailable, code, null)).ConfigureAwait(true);
        }
        finally
        {
            JobsActInFlight = false;
        }

        await ProbeAsync().ConfigureAwait(true);
        ApplyScheduleAnswer(epoch, result, "read synthesis schedule");
        Append(DescribeInboxActForLog("read synthesis schedule", result.Outcome, result.ErrorCode));
        RaiseChanged();
    }

    internal bool ApplyScheduleAnswer(long epoch, OwnerScheduleResult result, string act)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (epoch != _privateEpoch)
        {
            AppendIgnoredStaleInboxAnswer(act);
            return false;
        }

        if (result.Outcome == OwnerCommandOutcome.Completed && result.Schedule is null)
        {
            Schedule = null;
            ScheduleMessage = "The service holds no synthesis schedule: nothing is scheduled and nothing is sent automatically.";
            return true;
        }

        if (result.Outcome == OwnerCommandOutcome.Completed && result.Schedule is { } schedule)
        {
            Schedule = schedule;
            ScheduleMessage = schedule.Enabled
                ? "Enabled: interval " + schedule.IntervalMinutes.ToString(CultureInfo.InvariantCulture) +
                    " minute(s), next run due " + (schedule.NextDueAtUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "(none reported)") +
                    ". A run happens only while the vault is unlocked, each run is one bounded job under the same caps, and a slot missed while locked or while the service was down is not replayed."
                : "Disabled: the configuration is kept, and a disabled schedule is never due, so no scheduled run happens until you enable it again.";
            return true;
        }

        ScheduleMessage = DescribeInboxAnswer(act, result.Outcome, result.ErrorCode, result.ErrorMessage);
        return true;
    }

    /// <summary>
    /// One create-then-start through the shared lifecycle: the created identity is
    /// remembered before the start, so a start that does not answer leaves a
    /// cancelable Pending job rather than an anonymous one.
    /// </summary>
    private async Task<OwnerJobResult> RunOneJobAsync(OwnerJobRunRequest request, CancellationToken cancellationToken)
    {
        var created = await OwnerChannelClient.CreateJobAsync(
            ControlPipe,
            request.Inputs,
            request.ZoneId,
            request.DestinationResourceId,
            request.DestinationRevision,
            request.Provider,
            request.Model,
            request.Profile,
            request.Objective,
            request.TargetLevel,
            cancellationToken).ConfigureAwait(true);
        if (created.Outcome != OwnerCommandOutcome.Completed || created.Job is null)
        {
            return created;
        }

        LastCreatedJobId = created.Job.Id;
        return await OwnerChannelClient.StartJobAsync(ControlPipe, created.Job.Id, cancellationToken).ConfigureAwait(true);
    }

    private long? TryBeginJobsAct()
    {
        if (!Attached || JobsActInFlight || ReportedVaultState != OwnerVaultState.Unlocked)
        {
            return null;
        }

        JobsActInFlight = true;
        return _privateEpoch;
    }

    private async Task<T> ExecuteJobsCallAsync<T>(Func<CancellationToken, Task<T>> call, Func<string, T> onFailure)
    {
        try
        {
            using var timeout = new CancellationTokenSource(JobsActTimeout);
            return await call(timeout.Token).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return onFailure("act_failed:" + ex.GetType().Name);
        }
    }

    /// <summary>
    /// Drops every jobs view, batch report and schedule view of this session.
    /// Called with the other private state when the vault leaves the unlocked
    /// state, when the owner ends the session, and on a completed lock.
    /// </summary>
    private void ClearPrivateJobsState()
    {
        JobsActInFlight = false;
        _jobsFormVersion++;
        ProviderSettings = null;
        ProviderMessage = null;
        Jobs = Array.Empty<OwnerJob>();
        JobsMessage = null;
        JobsDetail = null;
        LastCreatedJobId = null;
        BatchSteps = Array.Empty<OwnerBatchStep>();
        BatchMessage = null;
        SpendLedger = null;
        SpendMessage = null;
        Schedule = null;
        ScheduleMessage = null;
    }
}
