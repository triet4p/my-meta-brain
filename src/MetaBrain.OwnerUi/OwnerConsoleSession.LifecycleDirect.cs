using MetaBrain.Application;
using MetaBrain.Connections;

namespace MetaBrain.OwnerUi;

/// <summary>
/// S9-T4 direct lifecycle acts of the owner console: create one subject
/// lifecycle record and run its transitions (publish, reject, withdraw,
/// re-propose, question resolve/reopen, intention complete/cancel/supersede/
/// notify/acknowledge, validity set, revision retain) through the existing
/// shared lifecycle operations. Every act names the subject and the live
/// revision it replaces; the rationale travels in the draft body. A stale
/// expected revision fails closed and publishes nothing. New domain semantics
/// are never introduced here: the operations, statuses and guards are the
/// service's own.
/// </summary>
internal sealed partial class OwnerConsoleSession
{
    /// <summary>Owner-facing outcome of the last direct lifecycle act. Never private content.</summary>
    public string? LifecycleMessage { get; private set; }

    /// <summary>The subject lifecycle record the last direct act served, if any.</summary>
    public OwnerLifecycleState? DirectLifecycle { get; private set; }

    /// <summary>
    /// Creates one subject lifecycle record through the existing
    /// lifecycle-create operation. The draft carries the creation method and
    /// the owner's reason; stamps stay null like the CLI friendly path.
    /// </summary>
    public async Task CreateDirectLifecycleAsync(string subjectId, string? reason, string? method)
    {
        if (string.IsNullOrWhiteSpace(subjectId) || TryBeginLifecycleDirectAct() is not { } epoch)
        {
            return;
        }

        var draft = OwnerMemoryDrafts.BuildLifecycleCreate(reason?.Trim(), method?.Trim());
        OwnerLifecycleResult result;
        try
        {
            result = await ExecuteLifecycleDirectAsync(
                token => OwnerChannelClient.CreateLifecycleAsync(ControlPipe, subjectId.Trim(), draft, token),
                code => new OwnerLifecycleResult(OwnerCommandOutcome.Unavailable, code, null)).ConfigureAwait(true);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(draft);
        }

        await ProbeAsync().ConfigureAwait(true);
        ApplyDirectLifecycleAnswer(epoch, "create lifecycle", result);
        Append(DescribeLifecycleDirectActForLog("create lifecycle", result.Outcome, result.ErrorCode));
        RaiseChanged();
    }

    /// <summary>
    /// Runs one lifecycle transition through the existing shared transition
    /// operation on the subject at its live revision. The owner's explicit
    /// validity instants travel only on the SetValidity act (every other act
    /// carries null stamps); the service validates precision, order and fit.
    /// </summary>
    public async Task TransitionDirectLifecycleAsync(
        string subjectId,
        long expectedRevision,
        LifecycleDirectAct act,
        string? reason,
        string? replacementResourceId,
        DateTimeOffset? validFromUtc = null,
        DateTimeOffset? validUntilUtc = null,
        DateTimeOffset? reviewAfterUtc = null)
    {
        if (string.IsNullOrWhiteSpace(subjectId) || expectedRevision < 1 ||
            TryBeginLifecycleDirectAct() is not { } epoch)
        {
            return;
        }

        var (operation, expectedStatus) = LifecycleDirectOperation(act);
        var draft = OwnerMemoryDrafts.BuildLifecycleTransition(reason?.Trim(), replacementResourceId?.Trim(), validFromUtc, validUntilUtc, reviewAfterUtc);
        OwnerLifecycleResult result;
        try
        {
            result = await ExecuteLifecycleDirectAsync(
                token => OwnerChannelClient.TransitionLifecycleAsync(
                    ControlPipe, subjectId.Trim(), expectedRevision, operation, expectedStatus, draft, token),
                code => new OwnerLifecycleResult(OwnerCommandOutcome.Unavailable, code, null)).ConfigureAwait(true);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(draft);
        }

        await ProbeAsync().ConfigureAwait(true);
        ApplyDirectLifecycleAnswer(epoch, DescribeDirectAct(act), result);
        Append(DescribeLifecycleDirectActForLog(DescribeDirectAct(act), result.Outcome, result.ErrorCode));
        RaiseChanged();
    }

    private long? TryBeginLifecycleDirectAct()
    {
        if (!Attached || InboxActInFlight || ReportedVaultState != OwnerVaultState.Unlocked)
        {
            return null;
        }

        InboxActInFlight = true;
        LifecycleMessage = null;
        RaiseChanged();
        return _privateEpoch;
    }

    private async Task<T> ExecuteLifecycleDirectAsync<T>(Func<CancellationToken, Task<T>> call, Func<string, T> onFailure)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            return await call(timeout.Token).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return onFailure("act_failed:" + ex.GetType().Name);
        }
        finally
        {
            InboxActInFlight = false;
        }
    }

    internal bool ApplyDirectLifecycleAnswer(long epoch, string act, OwnerLifecycleResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (epoch != _privateEpoch)
        {
            AppendIgnoredStaleInboxAnswer(act);
            return false;
        }

        if (result.Outcome == OwnerCommandOutcome.Completed && result.Lifecycle is not null)
        {
            DirectLifecycle = result.Lifecycle;
            LifecycleMessage = "The service recorded " + result.Lifecycle.SubjectResourceId +
                " at record revision " + result.Lifecycle.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                " with publication state " + result.Lifecycle.Publication + ".";
        }
        else
        {
            LifecycleMessage = DescribeLifecycleDirectAnswer(act, result.Outcome, result.ErrorCode);
        }

        return true;
    }

    private static string DescribeLifecycleDirectAnswer(string act, OwnerCommandOutcome outcome, string? errorCode)
    {
        var code = errorCode ?? "unknown";
        return outcome switch
        {
            OwnerCommandOutcome.Completed =>
                "The service completed the lifecycle act (" + act + ") but returned no lifecycle payload, so this console shows nothing instead of guessing.",
            OwnerCommandOutcome.Refused =>
                "The service refused this lifecycle act (code: " + code + "). Nothing changed; a moved revision or an act that is illegal from the current state answers the same way.",
            OwnerCommandOutcome.OwnerAuthorityDenied =>
                "This endpoint refused owner authority for the lifecycle request (code: " + code + "). An agent channel never performs lifecycle acts.",
            OwnerCommandOutcome.ProtocolMismatch =>
                "The endpoint speaks a different protocol revision, so no lifecycle act was performed.",
            _ => "No owner-channel answer reached the service (code: " + code + "), so no lifecycle act was performed."
        };
    }

    private static string DescribeLifecycleDirectActForLog(string act, OwnerCommandOutcome outcome, string? errorCode) =>
        "owner act: direct lifecycle " + act + " -> " + outcome.ToString().ToLowerInvariant() +
        (outcome == OwnerCommandOutcome.Completed ? "" : " (" + (errorCode ?? "unknown") + ")");

    internal enum LifecycleDirectAct
    {
        Publish,
        Reject,
        Withdraw,
        Repropose,
        ResolveQuestion,
        ReopenQuestion,
        CompleteIntention,
        CancelIntention,
        SupersedeIntention,
        NotifyIntention,
        AcknowledgeIntention,
        SetValidity,
        RetainRevision
    }

    internal static (string Operation, string ExpectedStatus) LifecycleDirectOperation(LifecycleDirectAct act) => act switch
    {
        LifecycleDirectAct.Publish => (ServiceRequestHandler.LifecyclePublishOperation, "lifecycle_published"),
        LifecycleDirectAct.Reject => (ServiceRequestHandler.LifecycleRejectProposalOperation, "lifecycle_rejected"),
        LifecycleDirectAct.Withdraw => (ServiceRequestHandler.LifecycleWithdrawOperation, "lifecycle_withdrawn"),
        LifecycleDirectAct.Repropose => (ServiceRequestHandler.LifecycleReProposeOperation, "lifecycle_reproposed"),
        LifecycleDirectAct.ResolveQuestion => (ServiceRequestHandler.LifecycleResolveQuestionOperation, "lifecycle_question_resolved"),
        LifecycleDirectAct.ReopenQuestion => (ServiceRequestHandler.LifecycleReopenQuestionOperation, "lifecycle_question_reopened"),
        LifecycleDirectAct.CompleteIntention => (ServiceRequestHandler.LifecycleCompleteIntentionOperation, "lifecycle_intention_completed"),
        LifecycleDirectAct.CancelIntention => (ServiceRequestHandler.LifecycleCancelIntentionOperation, "lifecycle_intention_cancelled"),
        LifecycleDirectAct.SupersedeIntention => (ServiceRequestHandler.LifecycleSupersedeIntentionOperation, "lifecycle_intention_superseded"),
        LifecycleDirectAct.NotifyIntention => (ServiceRequestHandler.LifecycleNotifyOperation, "lifecycle_intention_notified"),
        LifecycleDirectAct.AcknowledgeIntention => (ServiceRequestHandler.LifecycleAcknowledgeOperation, "lifecycle_intention_acknowledged"),
        LifecycleDirectAct.SetValidity => (ServiceRequestHandler.LifecycleSetValidityOperation, "lifecycle_validity_set"),
        _ => (ServiceRequestHandler.LifecycleRetainRevisionOperation, "lifecycle_revision_retained")
    };

    private static string DescribeDirectAct(LifecycleDirectAct act) => act switch
    {
        LifecycleDirectAct.Publish => "publish lifecycle",
        LifecycleDirectAct.Reject => "reject lifecycle",
        LifecycleDirectAct.Withdraw => "withdraw lifecycle",
        LifecycleDirectAct.Repropose => "re-propose lifecycle",
        LifecycleDirectAct.ResolveQuestion => "resolve question",
        LifecycleDirectAct.ReopenQuestion => "reopen question",
        LifecycleDirectAct.CompleteIntention => "complete intention",
        LifecycleDirectAct.CancelIntention => "cancel intention",
        LifecycleDirectAct.SupersedeIntention => "supersede intention",
        LifecycleDirectAct.NotifyIntention => "notify intention",
        LifecycleDirectAct.AcknowledgeIntention => "acknowledge intention",
        LifecycleDirectAct.SetValidity => "set validity",
        _ => "retain revision"
    };
}
