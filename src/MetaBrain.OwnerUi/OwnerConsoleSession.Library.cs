using System.Globalization;
using MetaBrain.Connections;

namespace MetaBrain.OwnerUi;

/// <summary>
/// Library and evidence detail of the owner console (S5-T3).
///
/// Every value here is an answer of the running service on the owner control
/// pipe: the service owns the search, the filters, the ranking, the citations
/// and the revision gate, and this session never derives, re-ranks or repairs
/// one. Private view state — the page, the open item, the open source and the
/// owner's own query text — lives only in this process, is never written to
/// any file, and is dropped with the private epoch it belonged to.
///
/// The private epoch is the lock boundary. It moves whenever the vault leaves
/// the unlocked state this session was reporting, and an asynchronous answer is
/// applied only while the epoch it was requested under is still current, after
/// the service has been asked for its state again. A late answer for a finished
/// epoch is dropped, so a lock cannot be undone by a reply that was already in
/// flight; the periodic status probe only ever re-reports service state and
/// never fetches private content.
/// </summary>
internal sealed partial class OwnerConsoleSession
{
    private static readonly TimeSpan LibraryActTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan LibraryRebuildTimeout = TimeSpan.FromMinutes(25);

    private long _privateEpoch;
    private long _libraryFilterVersion;

    /// <summary>
    /// Filters of the next library search, exactly as the owner set them. The
    /// service applies them; this session only carries them to the request and
    /// keeps them until the owner changes them or they are cleared with the
    /// private state.
    /// </summary>
    public OwnerLibraryQuery LibraryQuery { get; private set; } = new();

    /// <summary>Increments when the filters are reset (lock, end of session).</summary>
    public long LibraryFilterVersion => _libraryFilterVersion;

    /// <summary>Last library page the service answered, or null when none is shown.</summary>
    public OwnerLibraryPage? LibraryPage { get; private set; }

    /// <summary>Open item detail at the exact revision the service served.</summary>
    public OwnerItemDetail? LibraryItem { get; private set; }

    /// <summary>Open source at the exact registry revision the service served.</summary>
    public OwnerSourceDetail? LibrarySource { get; private set; }

    /// <summary>Last library index generation the service reported.</summary>
    public OwnerIndexReport? LibraryIndex { get; private set; }

    /// <summary>
    /// S7-T4 the owner's own recall decision for the open item, as the service
    /// reported it. It is the owner's curation state, never a fact about the
    /// item's truth.
    /// </summary>
    public OwnerRecallState? LibraryRecall { get; private set; }

    /// <summary>
    /// S7-T5 owner purge impact preview for the open item, as the service
    /// computed it. It carries IDs and revisions only — never any body.
    /// A confirm rebinds this exact preview, so a changed target fails closed.
    /// </summary>
    public OwnerPurgePreview? LibraryPurgePreview { get; private set; }

    /// <summary>S7-T5 last purge outcome for the open item.</summary>
    public OwnerPurgeOutcome? LibraryPurge { get; private set; }
    /// <summary>S7-T1 decision history of the open item, exactly as the service served it.</summary>
    public OwnerTimelineDetail? LibraryTimeline { get; private set; }

    /// <summary>S7-T1 retained historical revision opened from the history, or null when none is shown.</summary>
    public OwnerRetainedRevisionDetail? LibraryRetained { get; private set; }

    /// <summary>Owner-facing outcome of the last library act. Never private content.</summary>
    public string? LibraryMessage { get; private set; }

    /// <summary>True while one library act is in flight; further acts wait for its answer.</summary>
    public bool LibraryActInFlight { get; private set; }

    /// <summary>
    /// The private epoch async library answers are bound to. A reply may only be
    /// applied while this value is unchanged; it moves whenever the private
    /// state is cleared.
    /// </summary>
    public long PrivateEpoch => _privateEpoch;

    /// <summary>Applies the owner's filters for the next search (this process only).</summary>
    public void SetLibraryQuery(OwnerLibraryQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        LibraryQuery = query;
        RaiseChanged();
    }

    /// <summary>
    /// Reports a filter value this console refuses to send, so the owner sees
    /// which input is unusable instead of a silently different search. Never
    /// private content.
    /// </summary>
    public void ReportLibraryInputProblem(string message)
    {
        LibraryMessage = message;
        RaiseChanged();
    }

    /// <summary>
    /// Asks the service for one library page under the owner's current filters.
    /// </summary>
    public async Task SearchLibraryAsync()
    {
        if (TryBeginLibraryAct() is not { } epoch)
        {
            return;
        }

        var result = await ExecuteLibraryRequestAsync(
            LibraryActTimeout,
            token => OwnerChannelClient.SearchLibraryAsync(ControlPipe, LibraryQuery, token),
            errorCode => new OwnerLibrarySearchResult(OwnerCommandOutcome.Unavailable, errorCode, null)).ConfigureAwait(true);

        await ProbeAsync().ConfigureAwait(true);
        ApplyLibraryPageAnswer(epoch, result);
        Append(DescribeLibraryActForLog("search", result.Outcome, result.ErrorCode));
        RaiseChanged();
    }

    /// <summary>
    /// Opens one hit's detail at the exact revision the page named. The service
    /// serves that revision or refuses; it is never swapped for a newer one.
    /// </summary>
    public async Task OpenLibraryItemAsync(OwnerLibraryHit hit)
    {
        ArgumentNullException.ThrowIfNull(hit);
        if (TryBeginLibraryAct() is not { } epoch)
        {
            return;
        }

        var result = await ExecuteLibraryRequestAsync(
            LibraryActTimeout,
            token => OwnerChannelClient.ReadMemoryItemAsync(ControlPipe, hit.Id, hit.Revision, token),
            errorCode => new OwnerItemReadResult(OwnerCommandOutcome.Unavailable, errorCode, null)).ConfigureAwait(true);

        await ProbeAsync().ConfigureAwait(true);
        ApplyItemAnswer(epoch, result, renamed: false);
        Append(DescribeLibraryActForLog("open item", result.Outcome, result.ErrorCode));
        RaiseChanged();
    }

    /// <summary>
    /// Opens one citation's source at the exact registry revision the citation
    /// named, so evidence is checked against the revision it claims.
    /// </summary>
    public async Task OpenLibrarySourceAsync(string sourceId, long revision)
    {
        if (string.IsNullOrEmpty(sourceId) || revision < 1 || TryBeginLibraryAct() is not { } epoch)
        {
            return;
        }

        var result = await ExecuteLibraryRequestAsync(
            LibraryActTimeout,
            token => OwnerChannelClient.ReadSourceAsync(ControlPipe, sourceId, revision, token),
            errorCode => new OwnerSourceReadResult(OwnerCommandOutcome.Unavailable, errorCode, null)).ConfigureAwait(true);

        await ProbeAsync().ConfigureAwait(true);
        ApplySourceAnswer(epoch, result);
        Append(DescribeLibraryActForLog("open source", result.Outcome, result.ErrorCode));
        RaiseChanged();
    }

    /// <summary>
    /// S7-T1 decision history of the open item: the recorded lifecycle states,
    /// validity and history with every actor, time and rationale. History keeps
    /// original motivations; a contradiction is never newest-wins.
    /// </summary>
    public async Task ReadLibraryTimelineAsync()
    {
        var item = LibraryItem;
        if (item is null || TryBeginLibraryAct() is not { } epoch)
        {
            return;
        }

        var result = await ExecuteLibraryRequestAsync(
            LibraryActTimeout,
            token => OwnerChannelClient.ReadTimelineAsync(ControlPipe, item.Id, token),
            errorCode => new OwnerTimelineResult(OwnerCommandOutcome.Unavailable, errorCode, null)).ConfigureAwait(true);

        await ProbeAsync().ConfigureAwait(true);
        ApplyTimelineAnswer(epoch, result);
        Append(DescribeLibraryActForLog("decision history", result.Outcome, result.ErrorCode));
        RaiseChanged();
    }

    /// <summary>
    /// S7-T1 retained historical revision: the exact archived bytes of one
    /// older subject revision named by the history. The live item is never
    /// rewritten and a superseded frozen read never drifts onto newer content.
    /// </summary>
    public async Task OpenLibraryRetainedAsync(long subjectRevision)
    {
        var item = LibraryItem;
        if (item is null || subjectRevision < 1 || TryBeginLibraryAct() is not { } epoch)
        {
            return;
        }

        var result = await ExecuteLibraryRequestAsync(
            LibraryActTimeout,
            token => OwnerChannelClient.ReadRetainedRevisionAsync(ControlPipe, item.Id, subjectRevision, token),
            errorCode => new OwnerRetainedRevisionResult(OwnerCommandOutcome.Unavailable, errorCode, null)).ConfigureAwait(true);

        await ProbeAsync().ConfigureAwait(true);
        ApplyRetainedAnswer(epoch, result);
        Append(DescribeLibraryActForLog("retained revision", result.Outcome, result.ErrorCode));
        RaiseChanged();
    }

    /// <summary>
    /// Saves a new title through the existing revision-gated owner revise
    /// operation. The item keeps its ID: only a new revision of the same item is
    /// written, with the body, citations, times, applicability, attribution and
    /// evidence the vault already held.
    /// </summary>
    public async Task RenameLibraryItemAsync(string title)
    {
        var item = LibraryItem;
        if (item is null || string.IsNullOrWhiteSpace(title) || TryBeginLibraryAct() is not { } epoch)
        {
            return;
        }

        var result = await ExecuteLibraryRequestAsync(
            LibraryActTimeout,
            token => OwnerChannelClient.RenameMemoryTitleAsync(ControlPipe, item, title.Trim(), token),
            errorCode => new OwnerItemReadResult(OwnerCommandOutcome.Unavailable, errorCode, null)).ConfigureAwait(true);

        await ProbeAsync().ConfigureAwait(true);
        ApplyItemAnswer(epoch, result, renamed: true);
        Append(DescribeLibraryActForLog("rename title", result.Outcome, result.ErrorCode));
        RaiseChanged();
    }

    /// <summary>
    /// S7-T4 applies one owner recall act to the open item: the owner demotes,
    /// pins, sets aside or restores it.
    /// <para>
    /// The service decides what the act means and refuses an act that would
    /// cross a protection the owner set. This console only sends the act the
    /// owner chose and the reason the owner typed; it never derives an act,
    /// never guesses a rationale and never touches the item's content.
    /// </para>
    /// </summary>
    public async Task ApplyRecallActAsync(string act, string? rationale)
    {
        var item = LibraryItem;
        if (item is null || string.IsNullOrWhiteSpace(act) || TryBeginLibraryAct() is not { } epoch)
        {
            return;
        }

        var result = await ExecuteLibraryRequestAsync(
            LibraryActTimeout,
            token => OwnerChannelClient.ApplyRecallActAsync(ControlPipe, item.Id, act, rationale, token),
            errorCode => new OwnerRecallResult(OwnerCommandOutcome.Unavailable, errorCode, null)).ConfigureAwait(true);

        await ProbeAsync().ConfigureAwait(true);
        ApplyRecallAnswer(epoch, result, act);
        Append(DescribeLibraryActForLog("recall " + act, result.Outcome, result.ErrorCode));
        RaiseChanged();
    }

    /// <summary>
    /// S7-T4 reads the owner's own recall decision for the open item, so the
    /// console can show what the owner decided and why before changing it.
    /// </summary>
    public async Task ReadRecallStateAsync()
    {
        var item = LibraryItem;
        if (item is null || TryBeginLibraryAct() is not { } epoch)
        {
            return;
        }

        var result = await ExecuteLibraryRequestAsync(
            LibraryActTimeout,
            token => OwnerChannelClient.ReadRecallStateAsync(ControlPipe, item.Id, token),
            errorCode => new OwnerRecallResult(OwnerCommandOutcome.Unavailable, errorCode, null)).ConfigureAwait(true);

        await ProbeAsync().ConfigureAwait(true);
        ApplyRecallAnswer(epoch, result, "read");
        Append(DescribeLibraryActForLog("recall read", result.Outcome, result.ErrorCode));
        RaiseChanged();
    }
    /// <summary>
    /// S7-T5 previews the owner purge of the open item at its exact revision:
    /// the service binds the live revision and returns the exact impacted set.
    /// Writes nothing; the preview must be confirmed unchanged.
    /// </summary>
    public async Task PreviewPurgeAsync(string reason)
    {
        var item = LibraryItem;
        if (item is null || string.IsNullOrWhiteSpace(reason) || TryBeginLibraryAct() is not { } epoch)
        {
            return;
        }

        var result = await ExecuteLibraryRequestAsync(
            LibraryActTimeout,
            token => OwnerChannelClient.PreviewPurgeAsync(ControlPipe, item.Id, item.Revision, reason.Trim(), token),
            errorCode => new OwnerPurgePreviewResult(OwnerCommandOutcome.Unavailable, errorCode, null)).ConfigureAwait(true);

        await ProbeAsync().ConfigureAwait(true);
        ApplyPurgePreviewAnswer(epoch, result);
        Append(DescribeLibraryActForLog("purge preview", result.Outcome, result.ErrorCode));
        RaiseChanged();
    }

    /// <summary>
    /// S7-T5 confirms the exact previewed purge: deletes every listed
    /// canonical copy after the owner typed PURGE in the confirmation box,
    /// withdraws the mapped catalog entries, and records the durable ledger.
    /// </summary>
    public async Task ConfirmPurgeAsync(string confirmation, string reason)
    {
        var preview = LibraryPurgePreview;
        if (preview is null || TryBeginLibraryAct() is not { } epoch)
        {
            return;
        }

        var result = await ExecuteLibraryRequestAsync(
            new System.TimeSpan(0, 0, 30),
            token => OwnerChannelClient.ConfirmPurgeAsync(ControlPipe, preview, reason.Trim(), token),
            errorCode => new OwnerPurgeResult(OwnerCommandOutcome.Unavailable, errorCode, null)).ConfigureAwait(true);

        await ProbeAsync().ConfigureAwait(true);
        ApplyPurgeAnswer(epoch, result);
        Append(DescribeLibraryActForLog("purge confirm", result.Outcome, result.ErrorCode));
        RaiseChanged();
    }

    /// <summary>
    /// Applies one purge preview answer requested under <paramref name="epoch"/>,
    /// with the same epoch guard as every other library answer.
    /// </summary>
    internal bool ApplyPurgePreviewAnswer(long epoch, OwnerPurgePreviewResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (epoch != _privateEpoch)
        {
            AppendIgnoredStaleAnswer("purge preview");
            return false;
        }

        if (result.Outcome == OwnerCommandOutcome.Completed && result.Preview is not null)
        {
            LibraryPurgePreview = result.Preview;
            LibraryPurge = null;
            LibraryMessage = "The service previewed the exact purge impact: " +
                result.Preview.Impacted.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                " canonical copies and " +
                result.Preview.WithdrawnCatalogIds.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                " catalog entries. Confirm deletes exactly this set; a changed target fails closed.";
            return true;
        }

        LibraryMessage = "The service refused the purge preview (code: " + (result.ErrorCode ?? "unknown") +
            "). Nothing was previewed and nothing was deleted.";
        ApplyPrivateRefusal(result.Outcome, result.ErrorCode);
        return true;
    }

    /// <summary>
    /// Applies one purge outcome requested under <paramref name="epoch"/>, with
    /// the same epoch guard as every other library answer.
    /// </summary>
    internal bool ApplyPurgeAnswer(long epoch, OwnerPurgeResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (epoch != _privateEpoch)
        {
            AppendIgnoredStaleAnswer("purge confirm");
            return false;
        }

        if (result.Outcome == OwnerCommandOutcome.Completed && result.Purge is not null)
        {
            LibraryPurge = result.Purge;
            LibraryPurgePreview = null;
            LibraryPage = null;
            LibraryItem = null;
            LibrarySource = null;
            LibraryTimeline = null;
            LibraryRetained = null;
            LibraryRecall = null;
            LibraryMessage = "The service purged " +
                result.Purge.Deleted.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                " canonical copies, withdrew " +
                result.Purge.WithdrawnCatalogIds.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                " catalog entries, and advanced the scope generation to " +
                result.Purge.FinalGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                ". Rebuild the library index before searching again.";
            return true;
        }

        LibraryMessage = "The service refused the purge confirm (code: " + (result.ErrorCode ?? "unknown") +
            "). Nothing was deleted.";
        ApplyPrivateRefusal(result.Outcome, result.ErrorCode);
        return true;
    }

    /// <summary>
    /// Applies one recall answer requested under <paramref name="epoch"/>, with
    /// the same epoch guard as every other library answer.
    /// </summary>
    internal bool ApplyRecallAnswer(long epoch, OwnerRecallResult result, string act)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (epoch != _privateEpoch)
        {
            AppendIgnoredStaleAnswer("recall " + act);
            return false;
        }

        if (result.Outcome == OwnerCommandOutcome.Completed && result.Recall is not null)
        {
            LibraryRecall = result.Recall;
            LibraryMessage = "The service recorded the owner's recall decision: this item is now " +
                result.Recall.Disposition + " and the service reports it that way in recall." +
                (string.IsNullOrWhiteSpace(result.Recall.Rationale) ? string.Empty : " Reason: " + result.Recall.Rationale);
            return true;
        }

        LibraryMessage = "The service refused the recall act (code: " + (result.ErrorCode ?? "unknown") +
            "). The item keeps the recall decision it already had." +
            RefusalSentence(result.Outcome, result.ErrorMessage);
        ApplyPrivateRefusal(result.Outcome, result.ErrorCode);
        return true;
    }

    /// <summary>
    /// Materializes the library index generation for the unlocked vault. The
    /// service requires this explicit owner act before it can answer a search;
    /// a successful rebuild drops any page that came from an earlier generation
    /// instead of presenting it as the new one.
    /// </summary>
    public async Task RebuildLibraryIndexAsync()
    {
        if (TryBeginLibraryAct() is not { } epoch)
        {
            return;
        }

        var result = await ExecuteLibraryRequestAsync(
            LibraryRebuildTimeout,
            token => OwnerChannelClient.RebuildLibraryIndexAsync(ControlPipe, token),
            errorCode => new OwnerIndexResult(OwnerCommandOutcome.Unavailable, errorCode, null)).ConfigureAwait(true);

        await ProbeAsync().ConfigureAwait(true);
        ApplyIndexAnswer(epoch, result);
        Append(DescribeLibraryActForLog("rebuild index", result.Outcome, result.ErrorCode));
        RaiseChanged();
    }

    /// <summary>
    /// Applies a library page answer requested under <paramref name="epoch"/>.
    /// Returns false and changes nothing when that private epoch is gone, which
    /// is the only guard between a late answer and a cleared private view. This
    /// is the exact seam every asynchronous library act uses.
    /// </summary>
    internal bool ApplyLibraryPageAnswer(long epoch, OwnerLibrarySearchResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (epoch != _privateEpoch)
        {
            AppendIgnoredStaleAnswer("search");
            return false;
        }

        if (result.Outcome == OwnerCommandOutcome.Completed && result.Page is not null)
        {
            LibraryPage = result.Page;
            // A new page invalidates any detail and source opened from the old one.
            LibraryItem = null;
            LibrarySource = null;
            LibraryTimeline = null;
            LibraryRetained = null;
            LibraryRecall = null;
            LibraryPurgePreview = null;
            LibraryPurge = null;
            LibraryMessage = DescribeLibraryAnswer("search", result.Outcome, result.ErrorCode, true);
        }
        else
        {
            LibraryMessage = DescribeLibraryAnswer("search", result.Outcome, result.ErrorCode, false);
            ApplyPrivateRefusal(result.Outcome, result.ErrorCode);
        }

        return true;
    }

    /// <summary>
    /// Applies one item answer requested under <paramref name="epoch"/>, with the
    /// same epoch guard as a page answer.
    /// </summary>
    internal bool ApplyItemAnswer(long epoch, OwnerItemReadResult result, bool renamed)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (epoch != _privateEpoch)
        {
            AppendIgnoredStaleAnswer(renamed ? "rename" : "item detail");
            return false;
        }

        var act = renamed ? "rename" : "item detail";
        if (result.Outcome != OwnerCommandOutcome.Completed || result.Item is null)
        {
            LibraryMessage = DescribeLibraryAnswer(act, result.Outcome, result.ErrorCode, false);
            ApplyPrivateRefusal(result.Outcome, result.ErrorCode);
            return true;
        }

        var previous = LibraryItem;
        LibraryItem = result.Item;
        LibraryMessage = DescribeLibraryAnswer(act, result.Outcome, result.ErrorCode, true);
        if (!renamed)
        {
            LibrarySource = null;
            LibraryTimeline = null;
            LibraryRetained = null;
            return true;
        }

        if (previous is not null &&
            (previous.Revision != result.Item.Revision || !string.Equals(previous.Id, result.Item.Id, StringComparison.Ordinal)))
        {
            // The library generation described the previous revision of this
            // item, so the page it produced is behind this write. It is dropped
            // rather than shown as if it still matched the vault.
            LibraryPage = null;
            LibrarySource = null;
            LibraryTimeline = null;
            LibraryRetained = null;
            LibraryMessage = "The service saved the new title as ID " + result.Item.Id +
                " revision " + result.Item.Revision.ToString(CultureInfo.InvariantCulture) +
                ": the same item, one new revision, with the stored body, citations, times and attribution unchanged. " +
                "The library index is behind this write, so rebuild the library index before searching again.";
        }

        return true;
    }

    /// <summary>
    /// Applies one source answer requested under <paramref name="epoch"/>, with
    /// the same epoch guard as a page answer.
    /// </summary>
    internal bool ApplySourceAnswer(long epoch, OwnerSourceReadResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (epoch != _privateEpoch)
        {
            AppendIgnoredStaleAnswer("source detail");
            return false;
        }

        if (result.Outcome == OwnerCommandOutcome.Completed && result.Source is not null)
        {
            LibrarySource = result.Source;
            LibraryMessage = DescribeLibraryAnswer("source detail", result.Outcome, result.ErrorCode, true);
        }
        else
        {
            LibraryMessage = DescribeLibraryAnswer("source detail", result.Outcome, result.ErrorCode, false);
            ApplyPrivateRefusal(result.Outcome, result.ErrorCode);
        }

        return true;
    }

    internal bool ApplyTimelineAnswer(long epoch, OwnerTimelineResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (epoch != _privateEpoch)
        {
            AppendIgnoredStaleAnswer("decision history");
            return false;
        }

        if (result.Outcome == OwnerCommandOutcome.Completed && result.Timeline is not null)
        {
            LibraryTimeline = result.Timeline;
            LibraryMessage = "The service served the recorded decision history of this item with its validity and every recorded act.";
        }
        else
        {
            LibraryMessage = DescribeLibraryAnswer("decision history", result.Outcome, result.ErrorCode, false);
            ApplyPrivateRefusal(result.Outcome, result.ErrorCode);
        }

        return true;
    }

    internal bool ApplyRetainedAnswer(long epoch, OwnerRetainedRevisionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (epoch != _privateEpoch)
        {
            AppendIgnoredStaleAnswer("retained revision");
            return false;
        }

        if (result.Outcome == OwnerCommandOutcome.Completed && result.Retained is not null)
        {
            LibraryRetained = result.Retained;
            LibraryMessage = "The service served the exact retained revision the history named; nothing else was substituted.";
        }
        else
        {
            LibraryMessage = DescribeLibraryAnswer("retained revision", result.Outcome, result.ErrorCode, false);
            ApplyPrivateRefusal(result.Outcome, result.ErrorCode);
        }

        return true;
    }

    /// <summary>
    /// Applies one index answer requested under <paramref name="epoch"/>, with
    /// the same epoch guard as a page answer.
    /// </summary>
    internal bool ApplyIndexAnswer(long epoch, OwnerIndexResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (epoch != _privateEpoch)
        {
            AppendIgnoredStaleAnswer("index rebuild");
            return false;
        }

        if (result.Outcome != OwnerCommandOutcome.Completed || result.Index is null)
        {
            LibraryMessage = DescribeLibraryAnswer("rebuild index", result.Outcome, result.ErrorCode, false);
            ApplyPrivateRefusal(result.Outcome, result.ErrorCode);
            return true;
        }

        LibraryIndex = result.Index;
        LibraryPage = null;
        LibraryItem = null;
        LibrarySource = null;
        LibraryTimeline = null;
        LibraryRetained = null;
        LibraryMessage = "The service materialized index generation " +
            ShortDigest(result.Index.CanonicalDigest) + " with " +
            result.Index.RowCount.ToString(CultureInfo.InvariantCulture) +
            " rows" + (result.Index.Stale ? ", and it is already behind the vault: rebuild again to include later writes" : string.Empty) +
            ". Search now reads this generation.";
        return true;
    }

    /// <summary>
    /// Drops every private view and every private filter of this session. Called
    /// when the vault leaves the unlocked state this session was reporting, when
    /// the owner ends the session, and on a completed lock: afterwards the epoch
    /// has moved, so no answer that was already in flight can restore anything.
    /// </summary>
    private void ClearPrivateState()
    {
        ClearSensitiveState();
        ClearPrivateLibraryState();
        ClearPrivateInboxState();
        ClearPrivateRelationsState();
        ClearPrivateSharingState();
        ClearPrivateSourcesState();
        ClearPrivateIntentionsState();
        ClearPrivateReviewState();
        ClearPrivateBackupState();
        ClearPrivateOperationsState();
        // stops reporting one.
        LibraryIndex = null;
        LibraryMessage = null;
        LibraryQuery = new OwnerLibraryQuery();
        _libraryFilterVersion++;
    }

    /// <summary>
    /// Drops the private library views and moves the private epoch while keeping
    /// the owner's filters and the owner-facing message. Used when the service
    /// answers that this private view cannot exist (locked vault, no index
    /// generation, unavailable resource): the views go, the explanation of the
    /// refusal stays.
    /// </summary>
    private void ClearPrivateLibraryState()
    {
        _privateEpoch++;
        LibraryPage = null;
        LibraryItem = null;
        LibrarySource = null;
        LibraryTimeline = null;
        LibraryRetained = null;
        LibraryRecall = null;
        LibraryPurgePreview = null;
        LibraryPurge = null;
    }

    private void ApplyPrivateRefusal(OwnerCommandOutcome outcome, string? errorCode)
    {
        if (outcome == OwnerCommandOutcome.Refused &&
            errorCode is "vault_locked" or "resource_unavailable" or "library_unavailable" or "index_unavailable" or "index_not_materialized")
        {
            ClearPrivateLibraryState();
        }
    }

    private void AppendIgnoredStaleAnswer(string act) =>
        Append("ignored a stale " + act + " answer: the unlocked vault state it belonged to is gone, so the cleared private view stays cleared");

    /// <summary>
    /// Starts one library act only while the service still reports the vault
    /// unlocked and no other library act is in flight. Returns the private epoch
    /// the request will belong to, or null when no act is offered.
    /// </summary>
    private long? TryBeginLibraryAct()
    {
        if (!Attached || LibraryActInFlight || ReportedVaultState != OwnerVaultState.Unlocked)
        {
            return null;
        }

        LibraryActInFlight = true;
        LibraryMessage = null;
        RaiseChanged();
        return _privateEpoch;
    }

    private async Task<T> ExecuteLibraryRequestAsync<T>(
        TimeSpan timeoutValue,
        Func<CancellationToken, Task<T>> call,
        Func<string, T> onFailure)
    {
        try
        {
            using var timeout = new CancellationTokenSource(timeoutValue);
            return await call(timeout.Token).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return onFailure("act_failed:" + ex.GetType().Name);
        }
        finally
        {
            LibraryActInFlight = false;
        }
    }

    private static string DescribeLibraryActForLog(string act, OwnerCommandOutcome outcome, string? errorCode) =>
        "owner act: library " + act + " -> " + outcome.ToString().ToLowerInvariant() +
        (outcome == OwnerCommandOutcome.Completed ? "" : " (" + (errorCode ?? "unknown") + ")");

    private static string DescribeLibraryAnswer(string act, OwnerCommandOutcome outcome, string? errorCode, bool hasPayload)
    {
        var code = errorCode ?? "unknown";
        return outcome switch
        {
            OwnerCommandOutcome.Completed when hasPayload => act switch
            {
                "search" => "The service searched the vault and answered a library page under the filters this console sent.",
                "item detail" => "The service served the item at the exact revision the page named.",
                "source detail" => "The service served the registry revision this citation named; the locator shown is yours.",
                _ => "The service performed the library index rebuild and reports the generation below."
            },
            OwnerCommandOutcome.Completed =>
                "The service completed the library act but returned no library payload, so this console shows nothing instead of guessing.",
            OwnerCommandOutcome.Refused => code switch
            {
                "index_not_materialized" =>
                    "The service has no materialized library index for this unlocked vault, so it answered no search. Rebuild the library index to search.",
                "index_unavailable" =>
                    "The service cannot use a library index right now (code: index_unavailable). Nothing private was searched or shown.",
                "search_index_stale" =>
                    "The library index is behind the vault (a write happened after it was built), so the service refuses to search it: a match it named could no longer be opened at that revision. Rebuild the library index, then search again.",
                "collection_unknown" =>
                    "The service has no owner collection with that ID, so no search was performed. Collections are owner selection metadata and never grant access by themselves.",
                "vault_locked" =>
                    "The vault is locked, so the service answers no private library read. Unlock the vault (Vault & key) and rebuild the index to search again.",
                "resource_unavailable" =>
                    "The service did not serve that private resource at that revision (code: resource_unavailable). It may have moved or been removed; nothing else was substituted.",
                "library_unavailable" =>
                    "The service could not answer this library read (code: library_unavailable). No private content was served.",
                "invalid_request" =>
                    "The service refused this library read as invalid (code: invalid_request). Nothing private was read; an existing owner read answers a revision the vault no longer holds with this same code.",
                _ => "The service refused this library request (code: " + code + "). Nothing private was read or changed."
            },
            OwnerCommandOutcome.OwnerAuthorityDenied =>
                "This endpoint refused owner authority for the library request (code: " + code + "). An agent channel never serves the owner library.",
            OwnerCommandOutcome.ProtocolMismatch =>
                "The endpoint speaks a different protocol revision, so no library request was performed.",
            _ => "No owner-channel answer reached the service (code: " + code + "), so no library request was performed."
        };
    }

    private static string ShortDigest(string digest) =>
        digest.Length <= 12 ? digest : digest[..12];
}
