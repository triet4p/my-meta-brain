using MetaBrain.Connections;

namespace MetaBrain.OwnerUi;

/// <summary>
/// Owner-only time/context-trigger view state. Every scheduled intention/title/body/label is
/// an answer of the unlocked owner service and is discarded with its private epoch; this
/// session holds no trigger database, clock policy or notification queue of its own.
/// </summary>
internal sealed partial class OwnerConsoleSession
{
    private static readonly TimeSpan IntentionActTimeout = TimeSpan.FromSeconds(10);
    private bool _intentionActInFlight;

    public OwnerScheduledIntention[] ScheduledIntentions { get; private set; } = Array.Empty<OwnerScheduledIntention>();

    /// <summary>
    /// Pending intentions the service ranked for the schedule form's picker.
    /// The owner never has to type an intention resource ID: choosing a row
    /// fills the same identity the service will verify at its live revision.
    /// </summary>
    public OwnerLibraryHit[] IntentionCandidates { get; private set; } = Array.Empty<OwnerLibraryHit>();

    public string? IntentionCandidateMessage { get; private set; }

    public string? IntentionsMessage { get; private set; }

    public bool IntentionActInFlight => _intentionActInFlight;

    public async Task RefreshIntentionsAsync()
    {
        var epoch = TryBeginIntentionAct();
        if (epoch is null)
        {
            return;
        }

        OwnerIntentionScheduleListResult result;
        try
        {
            result = await ExecuteIntentionCallAsync(
                token => OwnerChannelClient.ListScheduledIntentionsAsync(ControlPipe, token),
                code => new OwnerIntentionScheduleListResult(OwnerCommandOutcome.Unavailable, code, null)).ConfigureAwait(true);
        }
        finally
        {
            _intentionActInFlight = false;
        }

        await ProbeAsync().ConfigureAwait(true);
        if (epoch != _privateEpoch)
        {
            Append("ignored a stale intention schedule answer; the private view stays cleared");
            RaiseChanged();
            return;
        }

        if (result.Outcome == OwnerCommandOutcome.Completed && result.Intentions is not null)
        {
            ScheduledIntentions = result.Intentions;
            var dueCount = ScheduledIntentions.Count(intention => intention.IsDue);
            IntentionsMessage = ScheduledIntentions.Length == 0
                ? "No pending intention has an enabled time or context trigger."
                : dueCount.ToString(System.Globalization.CultureInfo.InvariantCulture) + " due of " +
                    ScheduledIntentions.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                    " pending intention(s) with a time or context trigger. Acknowledgement never completes an intention.";
        }
        else
        {
            ScheduledIntentions = Array.Empty<OwnerScheduledIntention>();
            IntentionsMessage = DescribeIntentionResult("read scheduled intentions", result.Outcome, result.ErrorCode);
        }

        Append("read scheduled intentions -> " + result.Outcome.ToString().ToLowerInvariant() +
            (result.Outcome == OwnerCommandOutcome.Completed ? string.Empty : " (" + (result.ErrorCode ?? "unknown") + ")"));
        RaiseChanged();
    }

    /// <summary>
    /// Ranks pending intentions through the same owner library search the
    /// Library view uses, so the trigger form can be filled from service data
    /// instead of a remembered resource ID. Only kind=intention items can carry
    /// a trigger; the service still validates the subject on the write.
    /// </summary>
    public async Task SearchIntentionCandidatesAsync(string query)
    {
        if (TryBeginIntentionAct() is not { } epoch)
        {
            return;
        }

        IntentionCandidateMessage = null;
        var libraryQuery = new OwnerLibraryQuery(
            Text: string.IsNullOrWhiteSpace(query) ? null : query.Trim(),
            Kind: "intention",
            Limit: 20);
        var result = await ExecuteIntentionCallAsync(
            token => OwnerChannelClient.SearchLibraryAsync(ControlPipe, libraryQuery, token),
            code => new OwnerLibrarySearchResult(OwnerCommandOutcome.Unavailable, code, null)).ConfigureAwait(true);
        _intentionActInFlight = false;

        await ProbeAsync().ConfigureAwait(true);
        if (epoch != _privateEpoch)
        {
            Append("ignored a stale intention candidate answer; the private view stays cleared");
            RaiseChanged();
            return;
        }

        if (result.Outcome == OwnerCommandOutcome.Completed && result.Page is not null)
        {
            IntentionCandidates = result.Page.Hits;
            IntentionCandidateMessage = IntentionCandidates.Length == 0
                ? "The service found no pending intention for that search. Save an intention in Inbox/Library first, or search another word."
                : "The service ranked " + IntentionCandidates.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                    " pending intention(s). Choose one; the trigger write rechecks its live lifecycle revision.";
        }
        else
        {
            IntentionCandidates = Array.Empty<OwnerLibraryHit>();
            IntentionCandidateMessage = result.ErrorCode is "index_not_materialized" or "search_index_stale"
                ? "The service cannot search the library index yet (" + result.ErrorCode +
                    "). Rebuild the index in Library, then search again. Nothing was scheduled."
                : DescribeIntentionResult("search pending intentions", result.Outcome, result.ErrorCode);
        }

        Append("search pending intentions -> " + result.Outcome.ToString().ToLowerInvariant() +
            (result.Outcome == OwnerCommandOutcome.Completed ? string.Empty : " (" + (result.ErrorCode ?? "unknown") + ")"));
        RaiseChanged();
    }

    public Task SetIntentionTimeTriggerAsync(string resourceId, string dueAt, long? expectedLifecycleRevision) =>
        RunIntentionTransitionAsync(
            "set intention time trigger",
            token => OwnerChannelClient.SetIntentionTimeTriggerAsync(
                controlPipe: ControlPipe,
                resourceId: resourceId,
                dueAt: dueAt,
                cancel: false,
                expectedLifecycleRevision: expectedLifecycleRevision,
                cancellationToken: token));

    public Task EnableIntentionContextTriggerAsync(
        string resourceId,
        string? project,
        string? task,
        long? expectedLifecycleRevision) =>
        RunIntentionTransitionAsync(
            "enable intention context trigger",
            token => OwnerChannelClient.SetIntentionContextTriggerAsync(
                controlPipe: ControlPipe,
                resourceId: resourceId,
                project: project,
                task: task,
                disable: false,
                expectedLifecycleRevision: expectedLifecycleRevision,
                cancellationToken: token));

    public Task DisableIntentionContextTriggerAsync(OwnerScheduledIntention intention) =>
        RunIntentionTransitionAsync(
            "disable intention context trigger",
            token => OwnerChannelClient.SetIntentionContextTriggerAsync(
                controlPipe: ControlPipe,
                resourceId: intention.ResourceId,
                project: null,
                task: null,
                disable: true,
                expectedLifecycleRevision: intention.LifecycleRevision,
                cancellationToken: token));
    public Task CancelIntentionTimeTriggerAsync(OwnerScheduledIntention intention) =>
        RunIntentionTransitionAsync(
            "cancel intention time trigger",
            token => OwnerChannelClient.SetIntentionTimeTriggerAsync(
                controlPipe: ControlPipe,
                resourceId: intention.ResourceId,
                dueAt: null,
                cancel: true,
                expectedLifecycleRevision: intention.LifecycleRevision,
                cancellationToken: token));

    public Task AcknowledgeIntentionAsync(OwnerScheduledIntention intention) =>
        RunIntentionTransitionAsync(
            "acknowledge intention reminder",
            token => OwnerChannelClient.AcknowledgeIntentionAsync(ControlPipe, intention, token));

    public Task CompleteIntentionAsync(OwnerScheduledIntention intention, string rationale) =>
        RunIntentionTransitionAsync(
            "complete intention",
            token => OwnerChannelClient.CompleteIntentionAsync(ControlPipe, intention, rationale, token));

    public Task CancelIntentionAsync(OwnerScheduledIntention intention, string rationale) =>
        RunIntentionTransitionAsync(
            "cancel intention",
            token => OwnerChannelClient.CancelIntentionAsync(ControlPipe, intention, rationale, token));

    private async Task RunIntentionTransitionAsync(
        string act,
        Func<CancellationToken, Task<OwnerLifecycleResult>> call)
    {
        var epoch = TryBeginIntentionAct();
        if (epoch is null)
        {
            return;
        }

        OwnerLifecycleResult result;
        try
        {
            result = await ExecuteIntentionCallAsync(
                call,
                code => new OwnerLifecycleResult(OwnerCommandOutcome.Unavailable, code, null)).ConfigureAwait(true);
        }
        finally
        {
            _intentionActInFlight = false;
        }

        await ProbeAsync().ConfigureAwait(true);
        if (epoch != _privateEpoch)
        {
            Append("ignored a stale " + act + " answer; the private view stays cleared");
            RaiseChanged();
            return;
        }

        IntentionsMessage = DescribeIntentionResult(act, result.Outcome, result.ErrorCode);
        Append(act + " -> " + result.Outcome.ToString().ToLowerInvariant() +
            (result.Outcome == OwnerCommandOutcome.Completed ? string.Empty : " (" + (result.ErrorCode ?? "unknown") + ")"));
        RaiseChanged();
        if (result.Outcome == OwnerCommandOutcome.Completed)
        {
            await RefreshIntentionsAsync().ConfigureAwait(true);
        }
    }

    private long? TryBeginIntentionAct()
    {
        if (!Attached || _intentionActInFlight || ReportedVaultState != OwnerVaultState.Unlocked)
        {
            return null;
        }

        _intentionActInFlight = true;
        IntentionsMessage = null;
        RaiseChanged();
        return _privateEpoch;
    }

    private static async Task<T> ExecuteIntentionCallAsync<T>(
        Func<CancellationToken, Task<T>> call,
        Func<string, T> onFailure)
    {
        try
        {
            using var timeout = new CancellationTokenSource(IntentionActTimeout);
            return await call(timeout.Token).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return onFailure("act_failed:" + ex.GetType().Name);
        }
    }

    private void ClearPrivateIntentionsState()
    {
        _intentionActInFlight = false;
        ScheduledIntentions = Array.Empty<OwnerScheduledIntention>();
        IntentionCandidates = Array.Empty<OwnerLibraryHit>();
        IntentionCandidateMessage = null;
        IntentionsMessage = null;
    }

    private static string DescribeIntentionResult(string act, OwnerCommandOutcome outcome, string? errorCode) => outcome switch
    {
        OwnerCommandOutcome.Completed => "The service " + act + ". Acknowledgement and notification are separate from completion.",
        OwnerCommandOutcome.Refused => "The service refused to " + act + " (" + (errorCode ?? "unknown") +
            "). Nothing was changed. Read the schedule again before retrying: a row whose lifecycle revision moved since this list was read is the usual cause, and the save button never guesses a newer revision.",
        OwnerCommandOutcome.OwnerAuthorityDenied => "The endpoint refused owner authority for " + act + ".",
        OwnerCommandOutcome.ProtocolMismatch => "The endpoint speaks a different protocol revision; " + act + " did not run.",
        _ => "No owner-channel answer reached the service for " + act + " (" + (errorCode ?? "unknown") + ")."
    };
}
