using MetaBrain.Connections;

namespace MetaBrain.OwnerUi;

/// <summary>
/// Owner-only time-trigger view state. Every scheduled intention/title/body is an answer
/// of the unlocked owner service and is discarded with its private epoch; this session
/// holds no schedule database, clock policy or notification queue of its own.
/// </summary>
internal sealed partial class OwnerConsoleSession
{
    private static readonly TimeSpan IntentionActTimeout = TimeSpan.FromSeconds(10);
    private bool _intentionActInFlight;

    public OwnerScheduledIntention[] ScheduledIntentions { get; private set; } = Array.Empty<OwnerScheduledIntention>();

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
                ? "No pending intention has an enabled exact time trigger."
                : dueCount.ToString(System.Globalization.CultureInfo.InvariantCulture) + " due of " +
                    ScheduledIntentions.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                    " pending scheduled intention(s). Acknowledgement never completes an intention.";
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
            "acknowledge due intention",
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
        IntentionsMessage = null;
    }

    private static string DescribeIntentionResult(string act, OwnerCommandOutcome outcome, string? errorCode) => outcome switch
    {
        OwnerCommandOutcome.Completed => "The service " + act + ". Acknowledgement and notification are separate from completion.",
        OwnerCommandOutcome.Refused => "The service refused to " + act + " (" + (errorCode ?? "unknown") + ").",
        OwnerCommandOutcome.OwnerAuthorityDenied => "The endpoint refused owner authority for " + act + ".",
        OwnerCommandOutcome.ProtocolMismatch => "The endpoint speaks a different protocol revision; " + act + " did not run.",
        _ => "No owner-channel answer reached the service for " + act + " (" + (errorCode ?? "unknown") + ")."
    };
}
