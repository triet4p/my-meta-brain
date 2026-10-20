using System.Globalization;
using MetaBrain.Connections;

namespace MetaBrain.OwnerUi;

/// <summary>
/// Owner-only review-queue view state. Every entry, reason, source revision
/// and availability in this view is an answer of the unlocked owner service
/// and is discarded with its private epoch; this session holds no review
/// database, no clock policy and no verdict of its own. Reading the queue
/// changes nothing in the vault, and recording a review changes only the
/// owner's own review state: no item's content, evidence, citations, times or
/// states are touched, and no belief is denied.
/// </summary>
internal sealed partial class OwnerConsoleSession
{
    private static readonly TimeSpan ReviewActTimeout = TimeSpan.FromSeconds(60);
    private bool _reviewActInFlight;

    /// <summary>The items the service reported as asking for the owner's attention.</summary>
    public OwnerReviewEntry[] ReviewQueue { get; private set; } = Array.Empty<OwnerReviewEntry>();

    /// <summary>How many live items the service read for this evaluation.</summary>
    public int ReviewScannedItems { get; private set; }

    /// <summary>True when the service stopped its scan at its bound, so the queue is not the whole vault.</summary>
    public bool ReviewTruncated { get; private set; }

    /// <summary>Owner-facing outcome of the last review read or act. Never private content.</summary>
    public string? ReviewQueueMessage { get; private set; }

    /// <summary>True while one review read or act is in flight.</summary>
    public bool ReviewActInFlight => _reviewActInFlight;

    /// <summary>
    /// Reads the owner's review queue from the service. The evaluation is the
    /// service's own: this console renders what it is told and never decides
    /// that an item is wrong, stale or finished.
    /// </summary>
    public async Task RefreshReviewQueueAsync()
    {
        var epoch = TryBeginReviewAct();
        if (epoch is null)
        {
            return;
        }

        OwnerReviewQueueResult result;
        try
        {
            result = await ExecuteReviewCallAsync(
                token => OwnerChannelClient.ListReviewQueueAsync(ControlPipe, token),
                code => new OwnerReviewQueueResult(OwnerCommandOutcome.Unavailable, code, null)).ConfigureAwait(true);
        }
        finally
        {
            _reviewActInFlight = false;
        }

        await ProbeAsync().ConfigureAwait(true);
        if (epoch != _privateEpoch)
        {
            Append("ignored a stale review queue answer; the private view stays cleared");
            RaiseChanged();
            return;
        }

        if (result.Outcome == OwnerCommandOutcome.Completed && result.Queue is not null)
        {
            ReviewQueue = result.Queue.Entries;
            ReviewScannedItems = result.Queue.ScannedItems;
            ReviewTruncated = result.Queue.Truncated;
            var needsReview = 0;
            var undetermined = 0;
            foreach (var entry in ReviewQueue)
            {
                if (string.Equals(entry.State, "needs_review", StringComparison.Ordinal))
                {
                    needsReview++;
                }
                else if (string.Equals(entry.State, "undetermined", StringComparison.Ordinal))
                {
                    undetermined++;
                }
            }

            ReviewQueueMessage = ReviewQueue.Length == 0
                ? "The service found nothing that asks for your review: no deadline has passed, no cited source moved past the revision an item cited, and no cited source is gone or unavailable."
                : needsReview.ToString(CultureInfo.InvariantCulture) + " item(s) need review, " +
                    undetermined.ToString(CultureInfo.InvariantCulture) + " cannot be decided, of " +
                    ReviewScannedItems.ToString(CultureInfo.InvariantCulture) + " live item(s) read" +
                    (ReviewTruncated ? "; the scan stopped at its bound, so this is not the whole vault" : string.Empty) +
                    ". Recording a review says that you looked; it never changes what an item says.";
        }
        else
        {
            ReviewQueue = Array.Empty<OwnerReviewEntry>();
            ReviewScannedItems = 0;
            ReviewTruncated = false;
            ReviewQueueMessage = DescribeReviewResult("read the review queue", result.Outcome, result.ErrorCode);
        }

        Append("read review queue -> " + result.Outcome.ToString().ToLowerInvariant() +
            (result.Outcome == OwnerCommandOutcome.Completed ? string.Empty : " (" + (result.ErrorCode ?? "unknown") + ")"));
        RaiseChanged();
    }

    /// <summary>
    /// Records one owner review of one item. The owner's own reason is
    /// required; the service records what the owner saw and when. The item
    /// itself is not rewritten, its evidence is not downgraded, and no
    /// intention is completed by this act.
    /// </summary>
    public async Task RecordReviewAsync(string resourceId, string rationale)
    {
        if (string.IsNullOrWhiteSpace(resourceId) || string.IsNullOrWhiteSpace(rationale))
        {
            ReviewQueueMessage = "Select an item and write why you reviewed it before recording the review.";
            RaiseChanged();
            return;
        }

        var epoch = TryBeginReviewAct();
        if (epoch is null)
        {
            return;
        }

        OwnerReviewRecordResult result;
        try
        {
            result = await ExecuteReviewCallAsync(
                token => OwnerChannelClient.RecordReviewAsync(ControlPipe, resourceId, rationale, token),
                code => new OwnerReviewRecordResult(OwnerCommandOutcome.Unavailable, code, null)).ConfigureAwait(true);
        }
        finally
        {
            _reviewActInFlight = false;
        }

        await ProbeAsync().ConfigureAwait(true);
        if (epoch != _privateEpoch)
        {
            Append("ignored a stale review answer; the private view stays cleared");
            RaiseChanged();
            return;
        }

        var recorded = result.Outcome == OwnerCommandOutcome.Completed && result.Review is not null
            ? "The service recorded your review of " + result.Review.SubjectResourceId +
              " at " + result.Review.ReviewedAtUtc.ToString("u", CultureInfo.InvariantCulture) +
              ". The item keeps its own content, evidence and state; a source that moved stays reported as moved."
            : DescribeReviewResult("record the review", result.Outcome, result.ErrorCode);

        Append("record review -> " + result.Outcome.ToString().ToLowerInvariant() +
            (result.Outcome == OwnerCommandOutcome.Completed ? string.Empty : " (" + (result.ErrorCode ?? "unknown") + ")"));
        if (result.Outcome == OwnerCommandOutcome.Completed)
        {
            // The queue is refreshed first so the owner sees the updated
            // reasons; the outcome of the act stays on screen afterwards.
            await RefreshReviewQueueAsync().ConfigureAwait(true);
            ReviewMessage = recorded;
            RaiseChanged();
            return;
        }

        ReviewMessage = recorded;
        RaiseChanged();
    }

    private long? TryBeginReviewAct()
    {
        if (!Attached || _reviewActInFlight || ReportedVaultState != OwnerVaultState.Unlocked)
        {
            return null;
        }

        _reviewActInFlight = true;
        ReviewQueueMessage = null;
        RaiseChanged();
        return _privateEpoch;
    }

    private static async Task<T> ExecuteReviewCallAsync<T>(
        Func<CancellationToken, Task<T>> call,
        Func<string, T> onFailure)
    {
        try
        {
            using var timeout = new CancellationTokenSource(ReviewActTimeout);
            return await call(timeout.Token).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return onFailure("act_failed:" + ex.GetType().Name);
        }
    }

    private void ClearPrivateReviewState()
    {
        _reviewActInFlight = false;
        ReviewQueue = Array.Empty<OwnerReviewEntry>();
        ReviewScannedItems = 0;
        ReviewTruncated = false;
        ReviewQueueMessage = null;
    }

    private static string DescribeReviewResult(string act, OwnerCommandOutcome outcome, string? errorCode) => outcome switch
    {
        OwnerCommandOutcome.Completed => "The service " + act + ".",
        OwnerCommandOutcome.Refused => "The service refused to " + act + " (" + (errorCode ?? "unknown") + ").",
        OwnerCommandOutcome.OwnerAuthorityDenied => "The endpoint refused owner authority to " + act + ".",
        OwnerCommandOutcome.ProtocolMismatch => "The endpoint speaks a different protocol revision; " + act + " did not run.",
        _ => "No owner-channel answer reached the service for " + act + " (" + (errorCode ?? "unknown") + ")."
    };
}
