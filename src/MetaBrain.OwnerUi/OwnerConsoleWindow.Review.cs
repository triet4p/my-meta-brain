using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MetaBrain.Connections;

namespace MetaBrain.OwnerUi;

public partial class OwnerConsoleWindow
{
    private OwnerReviewListRow?[] _renderedReview = Array.Empty<OwnerReviewListRow?>();
    private bool _syncingReviewSelection;

    /// <summary>
    /// Renders the review queue exactly as the service reported it. The
    /// console shows the reasons, the source revisions and the availability
    /// the service computed; it never decides on its own that an item is
    /// wrong, stale or finished.
    /// </summary>
    private void RenderReview(bool attached)
    {
        var unlocked = attached && _session.ReportedVaultState == OwnerVaultState.Unlocked;
        var canAct = unlocked && !_session.ReviewActInFlight;
        var selected = SelectedReview();

        if (!ReferenceEquals(_renderedReview, _session.ReviewQueue))
        {
            var selectedId = selected?.Review.ResourceId;
            _renderedReview = _session.ReviewQueue.Select(entry => new OwnerReviewListRow(entry)).ToArray();
            _syncingReviewSelection = true;
            try
            {
                ReviewQueueList.ItemsSource = _renderedReview;
                ReviewQueueList.SelectedItem = selectedId is null
                    ? null
                    : _renderedReview.FirstOrDefault(row =>
                        row is not null && string.Equals(row.Review.ResourceId, selectedId, StringComparison.Ordinal));
            }
            finally
            {
                _syncingReviewSelection = false;
            }
        }

        selected = SelectedReview();
        RenderOutcome(ReviewQueueMessageText, _session.ReviewQueueMessage ??
            (unlocked
                ? "Read the service's own evaluation of what asks for your review."
                : "No review queue is available while locked or disconnected."));
        ReviewQueueRefreshButton.IsEnabled = canAct;
        ReviewQueueList.IsEnabled = unlocked;
        ReviewQueueRationaleInput.IsEnabled = canAct && selected is not null;
        ReviewQueueRecordButton.IsEnabled = canAct && selected is not null &&
            !string.IsNullOrWhiteSpace(ReviewQueueRationaleInput.Text);

        if (selected is null)
        {
            ReviewQueueDetailText.Text = "Select an item in the queue to see the exact source revisions and availability the service reported.";
            ReviewQueueCitationsText.Text = string.Empty;
            return;
        }

        var entry = selected.Review;
        ReviewQueueDetailText.Text =
            "id=" + entry.ResourceId +
            " revision=" + entry.ResourceRevision.ToString(CultureInfo.InvariantCulture) +
            " kind=" + entry.Kind +
            " state=" + entry.State +
            " reviewAfter=" + DescribeReviewStamp(entry.ReviewAfter) +
            " reasons=[" + string.Join(",", entry.Reasons) + "]" +
            " pending=[" + string.Join(",", entry.PendingReasons) + "]" +
            " lastReviewed=" + (entry.LastReviewedAtUtc?.ToString("O", CultureInfo.InvariantCulture) ?? "never");
        ReviewQueueCitationsText.Text = entry.Citations.Length == 0
            ? "No source is cited by this item, so no source moved."
            : string.Join(
                Environment.NewLine,
                entry.Citations.Select(citation =>
                    "source=" + citation.SourceId +
                    " cited=" + (citation.CitedRevision?.ToString(CultureInfo.InvariantCulture) ?? "none") +
                    " live=" + (citation.LiveRevision?.ToString(CultureInfo.InvariantCulture) ?? "none") +
                    " availability=" + citation.Availability +
                    " sourceRecorded=" + (citation.SourceRecordedAtUtc?.ToString("O", CultureInfo.InvariantCulture) ?? "none")));
    }

    private void OnReviewInputChanged(object sender, TextChangedEventArgs e) =>
        RenderReview(_session.Attached);

    /// <summary>
    /// Scrolls the shared content area back to the top of the review queue
    /// view, so a rendered screenshot of this console shows the view itself
    /// and not an empty part of the scroll range.
    /// </summary>
    internal void ScrollToReviewQueueTop()
    {
        ContentScrollViewer.ScrollToTop();
        ContentScrollViewer.UpdateLayout();
    }

    private async void OnReviewRefreshClicked(object sender, RoutedEventArgs e) =>
        await _session.RefreshReviewQueueAsync();

    private void OnReviewSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingReviewSelection)
        {
            return;
        }

        ReviewQueueRationaleInput.Clear();
        RenderReview(_session.Attached);
    }

    private async void OnReviewRecordClicked(object sender, RoutedEventArgs e)
    {
        var selected = SelectedReview();
        if (selected is null)
        {
            return;
        }

        var rationale = ReviewQueueRationaleInput.Text.Trim();
        if (rationale.Length == 0)
        {
            ReviewQueueMessageText.Text = "Write why you reviewed this item before recording the review.";
            return;
        }

        await _session.RecordReviewAsync(selected.Review.ResourceId, rationale);
        ReviewQueueRationaleInput.Clear();
    }

    private void OnReviewListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && SelectedReview() is not null)
        {
            e.Handled = true;
            ReviewQueueRationaleInput.Focus();
            return;
        }

        if (e.Key == Key.F5)
        {
            e.Handled = true;
            _ = _session.RefreshReviewQueueAsync();
        }
    }

    /// <summary>
    /// The review-after stamp exactly as the service reported it: its
    /// precision, its instant or its known wall-clock fields, and its offset.
    /// The console never fills in a date the owner did not record.
    /// </summary>
    private static string DescribeReviewStamp(OwnerTimelineStamp? stamp)
    {
        if (stamp is null)
        {
            return "none";
        }

        if (string.Equals(stamp.Precision, "unknown", StringComparison.Ordinal))
        {
            return "unknown";
        }

        var zone = stamp.TimeZoneOffsetMinutes is { } offset
            ? (offset < 0 ? "-" : "+") +
              (Math.Abs(offset) / 60).ToString("00", CultureInfo.InvariantCulture) + ":" +
              (Math.Abs(offset) % 60).ToString("00", CultureInfo.InvariantCulture)
            : "unknown-zone";
        if (string.Equals(stamp.Precision, "exact", StringComparison.Ordinal))
        {
            return "exact:" + (stamp.InstantUtc?.ToString("O", CultureInfo.InvariantCulture) ?? "none") + "@" + zone;
        }

        if (string.Equals(stamp.Precision, "day", StringComparison.Ordinal))
        {
            return "day:" + stamp.LocalYear + "-" + stamp.LocalMonth + "-" + stamp.LocalDay + "@" + zone;
        }

        if (string.Equals(stamp.Precision, "month", StringComparison.Ordinal))
        {
            return "month:" + stamp.LocalYear + "-" + stamp.LocalMonth + "@" + zone;
        }

        if (string.Equals(stamp.Precision, "quarter", StringComparison.Ordinal))
        {
            return "quarter:" + stamp.LocalYear + "-Q" + stamp.LocalQuarter + "@" + zone;
        }

        return "year:" + stamp.LocalYear + "@" + zone;
    }

    private OwnerReviewListRow? SelectedReview() => ReviewQueueList.SelectedItem as OwnerReviewListRow;

    internal sealed class OwnerReviewListRow(OwnerReviewEntry entry)
    {
        public OwnerReviewEntry Review { get; } = entry;

        public string Summary =>
            (string.IsNullOrEmpty(Review.Title) ? "(no title)" : Review.Title) +
            " | " + Review.State +
            " | " + Review.Kind +
            " | reasons=" + (Review.Reasons.Length == 0 ? string.Join(",", Review.UndeterminedReasons) : string.Join(",", Review.Reasons)) +
            " | pending=" + (Review.PendingReasons.Length == 0 ? "none" : string.Join(",", Review.PendingReasons)) +
            " | reviewAfter=" + DescribeReviewStamp(Review.ReviewAfter) +
            " | id=" + Review.ResourceId;

        public string BodyPreview =>
            "reviewed=" + (Review.LastReviewedAtUtc?.ToString("O", CultureInfo.InvariantCulture) ?? "never") +
            " | citations=" + Review.Citations.Length.ToString(CultureInfo.InvariantCulture);
    }
}
