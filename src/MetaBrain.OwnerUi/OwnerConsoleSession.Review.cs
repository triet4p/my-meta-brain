using System.Globalization;
using System.Text;
using MetaBrain.Connections;
using MetaBrain.Core.Domain;

namespace MetaBrain.OwnerUi;

/// <summary>
/// Capture and proposal review of the owner console (S5-T4).
///
/// Every value here is an answer of the running service on the owner control
/// pipe: the service owns the encrypted source write, the proposal inbox, the
/// reviewed item write, the publication decision and its history. This session
/// never writes a vault file, never ranks or reinterprets an answer, and never
/// decides a review by itself.
///
/// Two private-state rules follow from the acceptance criteria. The owner's
/// capture text lives only in this process — the editor holds it, the service
/// holds the encrypted copy of what was saved, and nothing is written to disk
/// — so text that was never saved is dropped with the private epoch, and a lock
/// the owner asks for is refused once with an explicit warning while such text
/// is still in the editor. And an asynchronous answer is applied only while the
/// private epoch it was requested under is still current, after the service has
/// been asked for its state again, so a lock cannot be undone by a reply that
/// was already in flight.
/// </summary>
internal sealed partial class OwnerConsoleSession
{
    private static readonly TimeSpan InboxActTimeout = TimeSpan.FromSeconds(20);

    /// <summary>Strict UTF-8: text this process cannot represent is refused instead of being saved with replaced characters.</summary>
    private static readonly UTF8Encoding StrictInboxUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private long _reviewDraftVersion;
    private long _captureDraftVersion;
    private string? _lockWarnedText;

    // ---- capture ----

    /// <summary>The text the capture editor currently holds. This process only; never persisted.</summary>
    public string CaptureDraft { get; private set; } = string.Empty;

    /// <summary>Moves whenever the session replaces or drops the editor text, so the window can follow it.</summary>
    public long CaptureDraftVersion => _captureDraftVersion;

    /// <summary>The exact text this session last saved, or null when nothing here has been saved.</summary>
    public string? CaptureSavedText { get; private set; }

    /// <summary>The registered source the last capture wrote in this session.</summary>
    public OwnerSourceDetail? CaptureSource { get; private set; }

    /// <summary>Owner-facing outcome of the last capture or read-back. Never private content.</summary>
    public string? CaptureMessage { get; private set; }

    /// <summary>The source a read-back served, and the verbatim text it carried.</summary>
    public OwnerSourceDetail? CaptureReadBackSource { get; private set; }

    public string? CaptureReadBackText { get; private set; }

    public string? CaptureReadBackMessage { get; private set; }

    /// <summary>True while one inbox act is in flight; further acts wait for its answer.</summary>
    public bool InboxActInFlight { get; private set; }

    /// <summary>True when the editor holds text the vault does not have.</summary>
    public bool HasUnsavedCaptureText =>
        CaptureDraft.Length > 0 && !string.Equals(CaptureDraft, CaptureSavedText, StringComparison.Ordinal);

    /// <summary>Reports what the owner's capture editor now holds. This process only.</summary>
    public void SetCaptureDraft(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        CaptureDraft = text;
    }

    /// <summary>Drops the editor text and the read-back view at the owner's request.</summary>
    public void ClearCaptureDraft()
    {
        CaptureDraft = string.Empty;
        _captureDraftVersion++;
        CaptureSavedText = null;
        CaptureSource = null;
        CaptureReadBackSource = null;
        CaptureReadBackText = null;
        CaptureReadBackMessage = null;
        CaptureMessage = "The capture editor is empty. Nothing was written anywhere by clearing it.";
        Append("cleared the capture editor and the read-back view at the owner's request");
        RaiseChanged();
    }

    /// <summary>
    /// Reports an input this console refuses to send, so the owner sees which
    /// field is unusable instead of a capture that silently differs from what
    /// they typed. Never private content.
    /// </summary>
    public void ReportInboxInputProblem(string message)
    {
        CaptureMessage = message;
        RaiseChanged();
    }

    /// <summary>
    /// Saves the text the owner typed as an encrypted registered source through
    /// the S3-T4 capture operation: text and zone are the write, the metadata is
    /// optional and never inferred, and no provider, fetch, classification or
    /// summary runs (R19). A text this process cannot encode as UTF-8 is refused
    /// here with an explanation, and the editor keeps it, so a malformed note is
    /// never saved as different characters and never lost.
    /// </summary>
    public async Task CaptureAsync(
        string resourceId,
        string zoneId,
        string? title,
        string? locator,
        string? author,
        DateTimeOffset? capturedAtUtc)
    {
        if (!TryEncodeCaptureText(CaptureDraft, out var encodingProblem))
        {
            CaptureMessage = encodingProblem;
            Append("refused to send a capture this process cannot encode as UTF-8; the editor text was kept");
            RaiseChanged();
            return;
        }

        var text = CaptureDraft;
        if (TryBeginInboxAct() is not { } epoch)
        {
            return;
        }

        CaptureMessage = null;
        var draft = new OwnerCaptureDraft(resourceId, zoneId, text, title, locator, author, capturedAtUtc);
        var result = await ExecuteInboxAsync(
            token => OwnerChannelClient.CaptureResearchNoteAsync(ControlPipe, draft, token),
            code => new OwnerCaptureResult(OwnerCommandOutcome.Unavailable, code, null)).ConfigureAwait(true);

        await ProbeAsync().ConfigureAwait(true);
        ApplyCaptureAnswer(epoch, text, result);
        Append(DescribeInboxActForLog("capture", result.Outcome, result.ErrorCode));
        RaiseChanged();
    }

    /// <summary>
    /// Reads one captured note back at the revision the vault holds — or the
    /// exact revision the owner named — and shows the verbatim text and the
    /// registry metadata the service served. This is the same read that proves a
    /// saved note still exists with exactly its text after a restart, and it
    /// never reconstructs, replaces or summarizes the note.
    /// </summary>
    public async Task ReadBackCaptureAsync(string sourceId, long? revision)
    {
        if (string.IsNullOrEmpty(sourceId) || TryBeginInboxAct() is not { } epoch)
        {
            return;
        }

        CaptureReadBackMessage = null;
        var result = await ExecuteInboxAsync(
            token => OwnerChannelClient.ReadResearchNoteAsync(ControlPipe, sourceId, revision, token),
            code => new OwnerResearchNoteResult(OwnerCommandOutcome.Unavailable, code, null, null)).ConfigureAwait(true);

        OwnerSourceReadResult metadata = result.Outcome == OwnerCommandOutcome.Completed && result.Source is not null
            ? new OwnerSourceReadResult(OwnerCommandOutcome.Completed, null, result.Source)
            : await ExecuteInboxAsync(
                token => OwnerChannelClient.ReadSourceAsync(ControlPipe, sourceId, revision, token),
                code => new OwnerSourceReadResult(OwnerCommandOutcome.Unavailable, code, null)).ConfigureAwait(true);

        await ProbeAsync().ConfigureAwait(true);
        ApplyReadBackAnswer(epoch, sourceId, result, metadata);
        Append(DescribeInboxActForLog("read capture", result.Outcome, result.ErrorCode));
        RaiseChanged();
    }

    /// <summary>Applies one capture answer requested under <paramref name="epoch"/>.</summary>
    internal bool ApplyCaptureAnswer(long epoch, string savedText, OwnerCaptureResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (epoch != _privateEpoch)
        {
            AppendIgnoredStaleInboxAnswer("capture");
            return false;
        }

        if (result.Outcome == OwnerCommandOutcome.Completed && result.Source is not null)
        {
            CaptureSource = result.Source;
            CaptureSavedText = savedText;
            CaptureMessage = "The service saved the text as registered source " + result.Source.Id +
                " revision " + result.Source.Revision.ToString(CultureInfo.InvariantCulture) +
                " (" + StrictInboxUtf8.GetByteCount(savedText).ToString(CultureInfo.InvariantCulture) +
                " UTF-8 bytes), encrypted in the vault under zone " + result.Source.ZoneId +
                ". Nothing was fetched, classified or summarized and no provider was needed, so the note is safe whether or not any structuring step ever runs.";
        }
        else
        {
            CaptureMessage = DescribeInboxAnswer("capture", result.Outcome, result.ErrorCode);
            ApplyInboxRefusal(result.Outcome, result.ErrorCode);
        }

        return true;
    }

    /// <summary>Applies one read-back answer requested under <paramref name="epoch"/>.</summary>
    internal bool ApplyReadBackAnswer(
        long epoch,
        string sourceId,
        OwnerResearchNoteResult result,
        OwnerSourceReadResult metadata)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (epoch != _privateEpoch)
        {
            AppendIgnoredStaleInboxAnswer("read capture");
            return false;
        }

        if (result.Outcome == OwnerCommandOutcome.Completed && result.Source is not null && result.Text is not null)
        {
            CaptureReadBackSource = result.Source;
            CaptureReadBackText = result.Text;
            CaptureReadBackMessage = "The service served the exact text it holds for " + result.Source.Id +
                " revision " + result.Source.Revision.ToString(CultureInfo.InvariantCulture) +
                ": " + StrictInboxUtf8.GetByteCount(result.Text).ToString(CultureInfo.InvariantCulture) +
                " UTF-8 bytes, unchanged and not reconstructed.";
            return true;
        }

        if (result.ErrorCode == "research_note_unavailable" && metadata.Outcome == OwnerCommandOutcome.Completed)
        {
            CaptureReadBackSource = metadata.Source;
            CaptureReadBackText = null;
            CaptureReadBackMessage = "The service holds " + sourceId +
                " as registry metadata only: this ID keeps no captured text, so there is nothing to show. No text was invented for it.";
            return true;
        }

        CaptureReadBackSource = null;
        CaptureReadBackText = null;
        CaptureReadBackMessage = DescribeInboxAnswer("read capture", result.Outcome, result.ErrorCode);
        ApplyInboxRefusal(result.Outcome, result.ErrorCode);
        return true;
    }

    // ---- proposal review ----

    /// <summary>Pending proposal identities the service listed, newest list first. Never a proposed body.</summary>
    public OwnerProposalIdentity[] ReviewProposals { get; private set; } = Array.Empty<OwnerProposalIdentity>();

    public string? ReviewInboxMessage { get; private set; }

    /// <summary>The proposal under review, exactly as the service served it.</summary>
    public OwnerProposalDetail? ReviewProposal { get; private set; }

    /// <summary>The proposal's own item draft: the baseline the editor starts from.</summary>
    public OwnerReviewDraft? ReviewDraft { get; private set; }

    /// <summary>Moves whenever the session replaces the editor content.</summary>
    public long ReviewDraftVersion => _reviewDraftVersion;

    /// <summary>The canonical item this review writes: the reviewed item of the proposal, or the item a revise proposal pins.</summary>
    public string ReviewItemId { get; private set; } = string.Empty;

    /// <summary>
    /// The live publication record of <see cref="ReviewItemId"/>, when one exists.
    /// </summary>
    public OwnerLifecycleState? ReviewItemLifecycle { get; private set; }

    /// <summary>
    /// For a revise proposal, the live publication record of the item it pins:
    /// the context of the decision, never the decision itself.
    /// </summary>
    public OwnerLifecycleState? ReviewSubjectLifecycle { get; private set; }

    public string? ReviewItemStateMessage { get; private set; }

    /// <summary>The first source the proposal cites, as the service served it at its revision.</summary>
    public OwnerSourceDetail? ReviewSource { get; private set; }

    public string? ReviewSourceText { get; private set; }

    public string? ReviewSourceMessage { get; private set; }

    /// <summary>
    /// The first cited memory item the service served at the revision the
    /// citation pins, when that citation names an item rather than a registered
    /// source. The pane shows its exact body text, the same detail the owner
    /// library serves; a citation the service does not serve at its pinned
    /// revision leaves this unset and is reported as such instead of being
    /// replaced with another revision or another resource.
    /// </summary>
    public OwnerItemDetail? ReviewCitedItem { get; private set; }

    /// <summary>
    /// What the service's computed review state says about the sources this
    /// pending proposal cites: unchanged, or moved/missing/unavailable/revoked
    /// since the proposal was recorded. A stale citation means the output needs
    /// review before any decision; the proposal content itself is never
    /// rewritten.
    /// </summary>
    public string? ReviewStalenessMessage { get; private set; }

    /// <summary>The last decision this console recorded in this session.</summary>
    public OwnerReviewOutcome? ReviewOutcome { get; private set; }

    public string? ReviewMessage { get; private set; }

    /// <summary>
    /// True when this review can be undone here: the decision is recorded on
    /// the review item the decision itself wrote (never on the item a revise
    /// proposal merely pins) and that record currently sits on a decision.
    /// </summary>
    public bool ReviewDecisionIsUndoable =>
        ReviewItemLifecycle is { } lifecycle &&
        !string.Equals(lifecycle.SubjectResourceId, ReviewProposal?.SubjectResourceId, StringComparison.Ordinal) &&
        lifecycle.Publication is "published" or "rejected";

    /// <summary>Asks the service for the current pending proposal inbox.</summary>
    public async Task RefreshReviewInboxAsync()
    {
        if (TryBeginInboxAct() is not { } epoch)
        {
            return;
        }

        ReviewInboxMessage = null;
        var result = await ExecuteInboxAsync(
            token => OwnerChannelClient.ListProposalsAsync(ControlPipe, token),
            code => new OwnerProposalListResult(OwnerCommandOutcome.Unavailable, code, null)).ConfigureAwait(true);

        await ProbeAsync().ConfigureAwait(true);
        ApplyProposalListAnswer(epoch, result);
        Append(DescribeInboxActForLog("proposal inbox", result.Outcome, result.ErrorCode));
        RaiseChanged();
    }

    /// <summary>
    /// Opens one pending proposal: its exact content and provenance, the state
    /// of the canonical item a decision would write, and the first source it
    /// cites read at the revision the citation names — so the owner reviews the
    /// proposed content beside the evidence it points at.
    /// </summary>
    public async Task OpenReviewProposalAsync(OwnerProposalIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (TryBeginInboxAct() is not { } epoch)
        {
            return;
        }

        ReviewMessage = null;
        var read = await ExecuteInboxAsync(
            token => OwnerChannelClient.ReadProposalAsync(ControlPipe, identity.Id, identity.Revision, token),
            code => new OwnerProposalReadResult(OwnerCommandOutcome.Unavailable, code, null)).ConfigureAwait(true);

        if (read.Outcome != OwnerCommandOutcome.Completed || read.Proposal is null)
        {
            await ProbeAsync().ConfigureAwait(true);
            ApplyProposalFailure(epoch, read.Outcome, read.ErrorCode);
            Append(DescribeInboxActForLog("open proposal", read.Outcome, read.ErrorCode));
            RaiseChanged();
            return;
        }

        var proposal = read.Proposal;
        var itemId = MemoryItemValidator.ReviewedItemIdFor(proposal.Id);
        var lifecycle = await ExecuteInboxAsync(
            token => OwnerChannelClient.ReadLifecycleAsync(ControlPipe, itemId, token),
            code => new OwnerLifecycleResult(OwnerCommandOutcome.Unavailable, code, null)).ConfigureAwait(true);

        // A revise proposal also carries the state of the item it pins: that is
        // the context of the decision, not the decision itself.
        var subject = new OwnerLifecycleResult(OwnerCommandOutcome.Completed, null, null);
        if (proposal.Kind == "revise" && !string.IsNullOrEmpty(proposal.SubjectResourceId))
        {
            subject = await ExecuteInboxAsync(
                token => OwnerChannelClient.ReadLifecycleAsync(ControlPipe, proposal.SubjectResourceId!, token),
                code => new OwnerLifecycleResult(OwnerCommandOutcome.Unavailable, code, null)).ConfigureAwait(true);
        }

        OwnerSourceDetail? source = null;
        OwnerItemDetail? citedItem = null;
        string? sourceText = null;
        string? sourceMessage = null;
        var citations = proposal.Item.Sources
            .Where(cited => !string.IsNullOrEmpty(cited.SourceId))
            .ToArray();
        if (citations.Length == 0)
        {
            sourceMessage = "This proposal cites no source, so there is no evidence to read beside the content.";
        }
        else
        {
            // A multi-source synthesis cites several inputs; the pane serves the
            // first one the service can actually read beside the content, while
            // every citation stays listed with its pinned revision. The proposal
            // itself is never rewritten to make a citation resolvable.
            foreach (var citation in citations)
            {
                var note = await ExecuteInboxAsync(
                    token => OwnerChannelClient.ReadResearchNoteAsync(ControlPipe, citation.SourceId!, citation.SourceRevision, token),
                    code => new OwnerResearchNoteResult(OwnerCommandOutcome.Unavailable, code, null, null)).ConfigureAwait(true);
                if (note.Outcome == OwnerCommandOutcome.Completed && note.Source is not null)
                {
                    source = note.Source;
                    sourceText = note.Text;
                    sourceMessage = (note.Text is null
                            ? "The service holds " + citation.SourceId + " as registry metadata only: this citation keeps no captured text."
                            : "The service served the exact text of " + note.Source.Id +
                                " revision " + note.Source.Revision.ToString(CultureInfo.InvariantCulture) +
                                " as the evidence this proposal cites") +
                        (citations.Length > 1
                            ? " (first of " + citations.Length.ToString(CultureInfo.InvariantCulture) + " cited inputs; every citation is listed above)."
                            : ".");
                    break;
                }

                var metadata = await ExecuteInboxAsync(
                    token => OwnerChannelClient.ReadSourceAsync(ControlPipe, citation.SourceId!, citation.SourceRevision, token),
                    code => new OwnerSourceReadResult(OwnerCommandOutcome.Unavailable, code, null)).ConfigureAwait(true);
                if (metadata.Source is not null)
                {
                    source = metadata.Source;
                    sourceMessage = "The citation names " + citation.SourceId + " and the service served registry metadata for it without captured text.";
                    break;
                }

                // A citation can also name a memory item instead of a registered
                // source: a higher-level synthesis cites the lower-level item it
                // stands on. The service serves that item at exactly the revision
                // the citation pins, through the same owner detail read the
                // library uses, so the owner compares the proposal with the cited
                // item's own text. A revision the service does not serve stays
                // reported as a refusal — it is never swapped for a newer one,
                // and the pane never shows another resource in its place.
                var item = await ExecuteInboxAsync(
                    token => OwnerChannelClient.ReadMemoryItemAsync(ControlPipe, citation.SourceId!, citation.SourceRevision, token),
                    code => new OwnerItemReadResult(OwnerCommandOutcome.Unavailable, code, null)).ConfigureAwait(true);
                if (item.Outcome == OwnerCommandOutcome.Completed && item.Item is not null)
                {
                    citedItem = item.Item;
                    sourceText = item.Item.Body;
                    sourceMessage = DescribeCitedItem(citation.SourceRevision, item.Item, citations.Length);
                    break;
                }

                sourceMessage = "No evidence was served beside the content for the citation " + citation.SourceId +
                    (citation.SourceRevision is { } pinned
                        ? "@" + pinned.ToString(CultureInfo.InvariantCulture)
                        : " (no revision pinned)") +
                    ": the service answered neither registered-source text nor a memory item at that revision (memory read: " +
                    (item.ErrorCode ?? "unknown") + "), and nothing was substituted for it.";
            }
        }

        await ProbeAsync().ConfigureAwait(true);
        ApplyProposalAnswer(epoch, proposal, itemId, lifecycle, subject.Lifecycle, source, citedItem, sourceText, sourceMessage);
        Append(DescribeInboxActForLog("open proposal", OwnerCommandOutcome.Completed, null));
        RaiseChanged();
    }

    /// <summary>
    /// Publishes the proposal under review with the content the owner edited.
    /// The proposal is named at the revision this console opened, and the
    /// attribution travels back exactly as the proposal declared it, so an
    /// approval never relabels an AI-written note as user-written or
    /// user-confirmed.
    /// </summary>
    public async Task PublishReviewProposalAsync(OwnerReviewDraft reviewed)
    {
        ArgumentNullException.ThrowIfNull(reviewed);
        var proposal = ReviewProposal;
        if (proposal is null || TryBeginInboxAct() is not { } epoch)
        {
            return;
        }

        ReviewMessage = null;
        var result = await ExecuteInboxAsync(
            token => OwnerChannelClient.PublishProposalAsync(ControlPipe, proposal, reviewed, token),
            code => new OwnerReviewResult(OwnerCommandOutcome.Unavailable, code, null)).ConfigureAwait(true);

        await ProbeAsync().ConfigureAwait(true);
        ApplyReviewAnswer(epoch, "publish", result);
        Append(DescribeInboxActForLog("publish proposal", result.Outcome, result.ErrorCode));
        RaiseChanged();
    }

    /// <summary>
    /// Rejects the proposal under review with the owner's rationale. The
    /// proposed content is recorded as the rejected review item and the item a
    /// revise proposal would have revised keeps its revision, its content and
    /// its own publication state. A rationale is required by the shared
    /// lifecycle core and is recorded in the item's history.
    /// </summary>
    public async Task RejectReviewProposalAsync(string rationale)
    {
        var proposal = ReviewProposal;
        if (proposal is null || TryBeginInboxAct() is not { } epoch)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(rationale))
        {
            InboxActInFlight = false;
            ReviewMessage = "A rejection needs an explicit rationale; the service records it with the decision and refuses a rejection without one. Nothing was sent.";
            RaiseChanged();
            return;
        }

        ReviewMessage = null;
        var result = await ExecuteInboxAsync(
            token => OwnerChannelClient.RejectProposalAsync(ControlPipe, proposal, rationale.Trim(), token),
            code => new OwnerReviewResult(OwnerCommandOutcome.Unavailable, code, null)).ConfigureAwait(true);

        await ProbeAsync().ConfigureAwait(true);
        ApplyReviewAnswer(epoch, "reject", result);
        Append(DescribeInboxActForLog("reject proposal", result.Outcome, result.ErrorCode));
        RaiseChanged();
    }

    /// <summary>
    /// Undoes the decision this console recorded for the review item: a
    /// published review item is withdrawn and a rejected one is proposed
    /// again, as one explicit owner act with the owner's rationale. The item
    /// keeps its content, its attribution and its history, no AI-written text
    /// is relabelled, and no second copy of the item is written — the same
    /// canonical record changes publication state.
    /// </summary>
    public async Task UndoReviewDecisionAsync(string rationale)
    {
        var lifecycle = ReviewItemLifecycle;
        if (lifecycle is null || !ReviewDecisionIsUndoable || TryBeginInboxAct() is not { } epoch)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(rationale))
        {
            InboxActInFlight = false;
            ReviewMessage = "Undoing a decision needs an explicit rationale; the service records it with the act. Nothing was sent.";
            RaiseChanged();
            return;
        }

        var withdrawing = string.Equals(lifecycle.Publication, "published", StringComparison.Ordinal);
        ReviewMessage = null;
        var result = await ExecuteInboxAsync(
            token => withdrawing
                ? OwnerChannelClient.WithdrawReviewItemAsync(ControlPipe, lifecycle, rationale.Trim(), token)
                : OwnerChannelClient.ReproposeReviewItemAsync(ControlPipe, lifecycle, rationale.Trim(), token),
            code => new OwnerLifecycleResult(OwnerCommandOutcome.Unavailable, code, null)).ConfigureAwait(true);

        await ProbeAsync().ConfigureAwait(true);
        ApplyUndoAnswer(epoch, withdrawing, result);
        Append(DescribeInboxActForLog(withdrawing ? "withdraw review item" : "re-propose review item", result.Outcome, result.ErrorCode));
        RaiseChanged();
    }

    /// <summary>Applies one inbox listing requested under <paramref name="epoch"/>.</summary>
    internal bool ApplyProposalListAnswer(long epoch, OwnerProposalListResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (epoch != _privateEpoch)
        {
            AppendIgnoredStaleInboxAnswer("proposal inbox");
            return false;
        }

        if (result.Outcome == OwnerCommandOutcome.Completed && result.Proposals is not null)
        {
            ReviewProposals = result.Proposals;
            ReviewInboxMessage = result.Proposals.Length == 0
                ? "The service reports no pending proposals: nothing is waiting for your review."
                : "The service reports " + result.Proposals.Length.ToString(CultureInfo.InvariantCulture) +
                    " pending proposal(s). A proposal is a pending write only: it is not a memory item, it changed nothing, and it stays pending until you publish or reject it.";
        }
        else
        {
            ReviewInboxMessage = DescribeInboxAnswer("proposal inbox", result.Outcome, result.ErrorCode);
            ApplyInboxRefusal(result.Outcome, result.ErrorCode);
        }

        return true;
    }

    /// <summary>Applies one opened proposal requested under <paramref name="epoch"/>.</summary>
    internal bool ApplyProposalAnswer(
        long epoch,
        OwnerProposalDetail proposal,
        string itemId,
        OwnerLifecycleResult lifecycle,
        OwnerLifecycleState? subjectLifecycle,
        OwnerSourceDetail? source,
        OwnerItemDetail? citedItem,
        string? sourceText,
        string? sourceMessage)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        if (epoch != _privateEpoch)
        {
            AppendIgnoredStaleInboxAnswer("open proposal");
            return false;
        }

        ReviewProposal = proposal;
        ReviewDraft = proposal.Item;
        ReviewItemId = itemId;
        ReviewItemLifecycle = lifecycle.Lifecycle;
        ReviewSubjectLifecycle = subjectLifecycle;
        ReviewSource = source;
        ReviewCitedItem = citedItem;
        ReviewSourceText = sourceText;
        ReviewSourceMessage = sourceMessage;
        ReviewStalenessMessage = DescribeProposalStaleness(proposal);
        ReviewOutcome = null;
        _reviewDraftVersion++;
        ReviewItemStateMessage = DescribeReviewItemState(proposal, itemId, lifecycle.Lifecycle, subjectLifecycle);
        ReviewMessage = "Ben dat de review proposal " + proposal.Id + " revision " +
            proposal.Revision.ToString(CultureInfo.InvariantCulture) + " (executor " + proposal.Executor +
            ", claimed user confirmation: " + (proposal.ClaimedUserConfirmed ? "claimed (never verified here)" : "no claim") +
            "). The proposal's own content and attribution are below; editing changes what the record says, never who wrote it.";
        return true;
    }

    /// <summary>
    /// What the review pane says about the state of the item a decision acts on:
    /// the review item the decision is recorded on, and — for a revise proposal
    /// — the state of the item the proposal pins. An item with no record yet has
    /// no decision, and that is stated as such instead of as an invented state.
    /// </summary>
    private static string DescribeReviewItemState(
        OwnerProposalDetail proposal,
        string itemId,
        OwnerLifecycleState? reviewItem,
        OwnerLifecycleState? subject)
    {
        var revise = string.Equals(proposal.Kind, "revise", StringComparison.Ordinal);
        var context = !revise
            ? string.Empty
            : subject is null
                ? " The item this proposal would revise has no publication record yet."
                : " The item this proposal would revise is " + subject.SubjectResourceId +
                    " with publication state " + subject.Publication + ".";
        if (reviewItem is not null)
        {
            return "The review item " + itemId + " has publication state " + reviewItem.Publication +
                " (record revision " + reviewItem.Revision.ToString(CultureInfo.InvariantCulture) +
                "); undo acts on that record." + context;
        }

        return revise
            ? "No decision has been recorded for this proposal yet." + context
            : "No item has been written for this proposal yet: it is still exactly what the agent submitted.";
    }

    /// <summary>
    /// What the review pane says about the live state of the sources a pending
    /// proposal cites: whether the output still matches the evidence it was
    /// recorded against. A citation that moved, disappeared, became unavailable,
    /// or was revoked is reported as needing review, and so is a source that
    /// changed underneath a cited memory item's own pinned lineage; the proposal
    /// is never silently updated to match.
    /// </summary>
    private static string DescribeProposalStaleness(OwnerProposalDetail proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        var review = proposal.CitationReview;
        if (review is null)
        {
            return "The service reported no review state for the sources this proposal cites.";
        }

        if (!string.Equals(review.State, "needsReview", StringComparison.Ordinal))
        {
            return "Citations unchanged: every source this proposal cites is still at the revision it names.";
        }

        var reasons = review.Reasons.Length == 0 ? "(none reported)" : string.Join(", ", review.Reasons);
        var stale = review.StaleSourceIds.Length == 0 ? "(none named to you)" : string.Join(", ", review.StaleSourceIds);
        var lineage = Array.IndexOf(review.Reasons, "citedSourceLineageChanged") >= 0
            ? " The report also names a source below a cited memory item: the cited item is still at the revision this proposal pins, but one of the sources that item's own citations pin no longer matches live state, so the item cannot be traced to its recorded roots as it stands."
            : string.Empty;
        return "NEEDS REVIEW: a source this proposal cites changed since the proposal was recorded (" + reasons +
            "). Cited source(s): " + stale + "." + lineage +
            " The proposal's content and citations are unchanged — re-read the cited evidence before you publish, edit, or reject it.";
    }

    /// <summary>
    /// What the pane says about a cited memory item the service served: the item
    /// identity, the revision actually served and how it relates to the revision
    /// the citation pins, and the stored attribution the comparison must not
    /// lose. A citation that pins no revision is stated as such instead of being
    /// presented as pinned.
    /// </summary>
    private static string DescribeCitedItem(long? pinnedRevision, OwnerItemDetail item, int citationCount)
    {
        var revision = pinnedRevision is { } pinned
            ? pinned == item.Revision
                ? "revision " + item.Revision.ToString(CultureInfo.InvariantCulture) + ", exactly the revision the citation pins"
                : "revision " + item.Revision.ToString(CultureInfo.InvariantCulture) +
                    " served for pinned revision " + pinned.ToString(CultureInfo.InvariantCulture)
            : "revision " + item.Revision.ToString(CultureInfo.InvariantCulture) +
                ", the revision the vault holds now because the citation pins none";
        var detail = "The service served the cited memory item " + item.Id + " at " + revision +
            " (kind=" + item.Kind + ", level=" + item.Level + ", author=" + item.Author +
            ", creationMethod=" + item.CreationMethod + ", evidence=" + item.Evidence +
            ") as the evidence this proposal cites";
        return citationCount > 1
            ? detail + " (first of " + citationCount.ToString(CultureInfo.InvariantCulture) +
                " cited inputs; every citation is listed above)."
            : detail + ".";
    }

    /// <summary>Applies one failed open requested under <paramref name="epoch"/>.</summary>
    internal bool ApplyProposalFailure(long epoch, OwnerCommandOutcome outcome, string? errorCode)
    {
        if (epoch != _privateEpoch)
        {
            AppendIgnoredStaleInboxAnswer("open proposal");
            return false;
        }

        ReviewMessage = DescribeInboxAnswer("open proposal", outcome, errorCode);
        ApplyInboxRefusal(outcome, errorCode);
        return true;
    }

    /// <summary>Applies one decision answer requested under <paramref name="epoch"/>.</summary>
    internal bool ApplyReviewAnswer(long epoch, string act, OwnerReviewResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (epoch != _privateEpoch)
        {
            AppendIgnoredStaleInboxAnswer(act + " proposal");
            return false;
        }

        if (result.Outcome == OwnerCommandOutcome.Completed && result.Review is not null)
        {
            var review = result.Review;
            ReviewOutcome = review;
            ReviewItemId = review.ItemId;
            ReviewItemLifecycle = new OwnerLifecycleState(review.ItemId, review.LifecycleRevision, review.Publication);
            var published = string.Equals(review.Publication, "published", StringComparison.Ordinal);
            ReviewItemStateMessage = "The service recorded " + review.ItemId + " at revision " +
                review.ItemRevision.ToString(CultureInfo.InvariantCulture) + " with publication state " +
                review.Publication + " (record revision " + review.LifecycleRevision.ToString(CultureInfo.InvariantCulture) + ").";
            ReviewMessage = !review.DecisionApplied
                ? "This review was already recorded exactly like this: the service returned the existing item and decision and wrote nothing a second time."
                : published
                    ? "The service wrote the reviewed content as canonical item " + review.ItemId +
                        " and recorded your publication decision on it. Its author, creation method and evidence level are the proposal's own, so nothing AI-written became user-written or user-confirmed, and the pending proposal record is untouched."
                    : "The service recorded your rejection of this proposal. The proposed content is stored as the rejected review item " +
                        review.ItemId + " (its author, creation method and evidence are the proposal's own), no revision was applied to any other item, and the item's history carries your rationale.";
        }
        else
        {
            ReviewMessage = DescribeInboxAnswer(act + " proposal", result.Outcome, result.ErrorCode);
            ApplyInboxRefusal(result.Outcome, result.ErrorCode);
        }

        return true;
    }

    /// <summary>Applies one undo answer requested under <paramref name="epoch"/>.</summary>
    internal bool ApplyUndoAnswer(long epoch, bool withdrawing, OwnerLifecycleResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (epoch != _privateEpoch)
        {
            AppendIgnoredStaleInboxAnswer(withdrawing ? "withdraw" : "re-propose");
            return false;
        }

        if (result.Outcome == OwnerCommandOutcome.Completed && result.Lifecycle is not null)
        {
            ReviewItemLifecycle = result.Lifecycle;
            ReviewMessage = withdrawing
                ? "The service withdrew " + result.Lifecycle.SubjectResourceId +
                    ": the item keeps its content, its author, its creation method, its evidence and its whole publication history, it is no longer current in a search, and no copy of it exists."
                : "The service returned " + result.Lifecycle.SubjectResourceId +
                    " to proposal: the same canonical record is in review again and can be published without writing a second copy, and nothing about it was rewritten.";
            ReviewItemStateMessage = "The review item's publication state is now " + result.Lifecycle.Publication +
                " (record revision " + result.Lifecycle.Revision.ToString(CultureInfo.InvariantCulture) + ").";
        }
        else
        {
            ReviewMessage = DescribeInboxAnswer(withdrawing ? "withdraw verification" : "re-propose verification", result.Outcome, result.ErrorCode);
            ApplyInboxRefusal(result.Outcome, result.ErrorCode);
        }

        return true;
    }

    /// <summary>
    /// Drops every private capture and review view and filter of this session,
    /// including the editor text, and moves the private epoch so an answer that
    /// was already in flight for the cleared state cannot restore any of it.
    /// Called when the vault leaves the unlocked state this session was
    /// reporting, when the owner ends the session, and on a completed lock.
    /// </summary>
    private void ClearPrivateInboxState()
    {
        _privateEpoch++;
        _captureDraftVersion++;
        _reviewDraftVersion++;
        CaptureDraft = string.Empty;
        CaptureSavedText = null;
        CaptureSource = null;
        CaptureMessage = null;
        CaptureReadBackSource = null;
        CaptureReadBackText = null;
        CaptureReadBackMessage = null;
        ReviewProposals = Array.Empty<OwnerProposalIdentity>();
        ReviewInboxMessage = null;
        ReviewProposal = null;
        ReviewDraft = null;
        ReviewItemId = string.Empty;
        ReviewItemLifecycle = null;
        ReviewSubjectLifecycle = null;
        ReviewItemStateMessage = null;
        ReviewSource = null;
        ReviewCitedItem = null;
        ReviewSourceText = null;
        ReviewSourceMessage = null;
        ReviewStalenessMessage = null;
        ReviewOutcome = null;
        ReviewMessage = null;
        ClearPrivateDeclassifyState();
        ClearPrivateJobsState();
        _lockWarnedText = null;
    }

    private void ApplyInboxRefusal(OwnerCommandOutcome outcome, string? errorCode)
    {
        if (outcome == OwnerCommandOutcome.Refused &&
            errorCode is "vault_locked" or "resource_unavailable" or "forbidden")
        {
            ClearPrivateInboxState();
        }
    }

    private void AppendIgnoredStaleInboxAnswer(string act) =>
        Append("ignored a stale " + act + " answer: the unlocked vault state it belonged to is gone, so the cleared private view stays cleared");

    private long? TryBeginInboxAct()
    {
        if (!Attached || InboxActInFlight || ReportedVaultState != OwnerVaultState.Unlocked)
        {
            return null;
        }

        InboxActInFlight = true;
        return _privateEpoch;
    }

    private async Task<T> ExecuteInboxAsync<T>(Func<CancellationToken, Task<T>> call, Func<string, T> onFailure)
    {
        try
        {
            using var timeout = new CancellationTokenSource(InboxActTimeout);
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

    /// <summary>
    /// True when this process can represent the text as UTF-8. Text that cannot
    /// is refused with an explanation instead of being saved with substituted
    /// characters, and the editor keeps it.
    /// </summary>
    private static bool TryEncodeCaptureText(string text, out string? problem)
    {
        try
        {
            _ = StrictInboxUtf8.GetByteCount(text);
            problem = null;
            return true;
        }
        catch (EncoderFallbackException)
        {
            problem = "This text cannot be encoded as UTF-8, so the service would have to store different characters: the capture was not sent and your text is still in the editor. Remove or replace the unpaired surrogate character and save again.";
            return false;
        }
    }

    private static string DescribeInboxActForLog(string act, OwnerCommandOutcome outcome, string? errorCode) =>
        "owner act: " + act + " -> " + outcome.ToString().ToLowerInvariant() +
        (outcome == OwnerCommandOutcome.Completed ? "" : " (" + (errorCode ?? "unknown") + ")");

    private static string DescribeInboxAnswer(string act, OwnerCommandOutcome outcome, string? errorCode, string? errorMessage = null)
    {
        var code = errorCode ?? "unknown";
        var answer = outcome switch
        {
            OwnerCommandOutcome.Completed =>
                "The service completed the " + act + " but returned no payload, so this console shows nothing instead of guessing.",
            OwnerCommandOutcome.Refused => code switch
            {
                "vault_locked" =>
                    "The vault is locked, so the service answered no " + act + " and nothing was written or read. Unlock the vault and try again; your text stays in the editor until you lock.",
                "invalid_request" =>
                    "The service refused this " + act + " as invalid (code: invalid_request) and changed nothing: an unknown or empty identifier, a moved revision, a proposal that is no longer there, or content the shared contract rejects all answer the same way.",
                "research_note_unavailable" =>
                    "That ID keeps no captured text: the service registered registry metadata without a note body, so there is no text to show and none was invented.",
                "resource_unavailable" =>
                    "The service did not serve that private resource at that revision (code: resource_unavailable). It may have moved or been removed; nothing else was substituted.",
                "forbidden" =>
                    "This endpoint refused owner authority for the request (code: forbidden). An agent channel never performs owner capture or review acts.",
                _ => "The service refused this " + act + " (code: " + code + "). Nothing was written or changed."
            },
            OwnerCommandOutcome.OwnerAuthorityDenied =>
                "This endpoint refused owner authority for the " + act + " (code: " + code + "). An agent channel never serves the owner inbox or review.",
            OwnerCommandOutcome.ProtocolMismatch =>
                "The endpoint speaks a different protocol revision, so no " + act + " was performed.",
            _ => "No owner-channel answer reached the service (code: " + code + "), so no " + act + " was performed."
        };
        return answer + RefusalSentence(outcome, errorMessage);
    }

    /// <summary>
    /// The owner's own sentence of a refusal, when the service sent one with
    /// its typed code. The code stays the identity of the refusal and the
    /// sentence is the explanation the owner reads; a refusal that carries no
    /// sentence keeps the console's own wording.
    /// </summary>
    private static string RefusalSentence(OwnerCommandOutcome outcome, string? errorMessage) =>
        outcome == OwnerCommandOutcome.Refused && !string.IsNullOrWhiteSpace(errorMessage)
            ? " The service said: " + errorMessage
            : string.Empty;
}
