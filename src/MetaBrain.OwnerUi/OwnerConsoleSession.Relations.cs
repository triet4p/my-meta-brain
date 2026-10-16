using System.Globalization;
using MetaBrain.Connections;
using MetaBrain.Core.Domain;

namespace MetaBrain.OwnerUi;

/// <summary>
/// Explicit relations workbench of the owner console (S5-T5).
///
/// Every relation below is an answer of the running service on the owner
/// control pipe: the service mints relation identity, verifies both endpoints
/// at the live revisions the link pins, decides the derivation graph, computes
/// each link's review state and records the act's rationale. This session
/// sends the owner's explicit draft and renders what came back; it keeps no
/// second link store, no visibility policy of its own and no plaintext file, so
/// an agent-created link and an owner-created link are the same canonical
/// record seen through the same projection.
///
/// A related_to association and an evidence-bearing supports link stay
/// distinguishable here for exactly the reason the core keeps them apart: the
/// owner picks the kind, and this console never promotes a similarity or an
/// association into an evidence link.
///
/// Private view state — the list, the opened link, the candidate endpoints, the
/// owner's link draft and the links' answers — lives only in this process and is
/// dropped with the private epoch it belonged to: the epoch moves whenever the
/// vault leaves the unlocked state, and an asynchronous answer is applied only
/// while the epoch it was requested under is still current, after the service
/// has been asked for its state again. A late answer for a finished epoch is
/// dropped, so a lock cannot be undone by a reply that was already in flight.
/// </summary>
internal sealed partial class OwnerConsoleSession
{
    private static readonly TimeSpan RelationsActTimeout = TimeSpan.FromSeconds(20);
    private long _relationFormVersion;

    /// <summary>
    /// One endpoint the owner may pin into a link: a memory item the service's
    /// own library search ranked at its live revision, or a registered source
    /// the owner named. The kind is the endpoint kind the shared relation
    /// validator requires, and the service re-verifies it when the link is
    /// written.
    /// </summary>
    internal sealed record RelationEndpointPick(
        string ResourceId,
        string EndpointKind,
        long Revision,
        string ZoneId,
        string Label)
    {
        public override string ToString() => Label;
    }

    /// <summary>Every relation the service listed for this unlocked vault, in the order it served them.</summary>
    public OwnerRelation[] Relations { get; private set; } = Array.Empty<OwnerRelation>();

    /// <summary>Owner-facing outcome of the last relation listing. Never private content.</summary>
    public string? RelationsMessage { get; private set; }

    /// <summary>The relation the owner opened, exactly as the service served it at its revision.</summary>
    public OwnerRelation? OpenRelation { get; private set; }

    /// <summary>Owner-facing outcome of the last relation act. Never private content.</summary>
    public string? RelationMessage { get; private set; }

    /// <summary>Candidate endpoints the service's library search ranked for the link form.</summary>
    public RelationEndpointPick[] RelationCandidates { get; private set; } = Array.Empty<RelationEndpointPick>();

    /// <summary>Owner-facing note about the last candidate search.</summary>
    public string? RelationCandidateMessage { get; private set; }

    /// <summary>True while one relation act is in flight; further acts wait for its answer.</summary>
    public bool RelationsActInFlight { get; private set; }

    /// <summary>
    /// Increments whenever this session drops the owner's relation draft (the
    /// link form, the picked endpoints and the candidates of the unlocked
    /// epoch). The host clears its own relation controls when it observes a new
    /// value, so no draft of a finished epoch stays on screen.
    /// </summary>
    public long RelationFormVersion => _relationFormVersion;

    /// <summary>
    /// Asks the service for every relation of the unlocked vault with its live
    /// revision, provenance and computed review state.
    /// </summary>
    public async Task RefreshRelationsAsync()
    {
        if (TryBeginRelationsAct() is not { } epoch)
        {
            return;
        }

        RelationsMessage = null;
        var result = await ExecuteRelationsAsync(
            token => OwnerChannelClient.ListRelationsAsync(ControlPipe, token),
            code => new OwnerRelationListResult(OwnerCommandOutcome.Unavailable, code, null)).ConfigureAwait(true);

        await ProbeAsync().ConfigureAwait(true);
        ApplyRelationListAnswer(epoch, result);
        Append(DescribeRelationsActForLog("list relations", result.Outcome, result.ErrorCode));
        RaiseChanged();
    }

    /// <summary>
    /// Opens one link at the exact revision the list named, so its provenance —
    /// who recorded it, how, and what the caller reported — and its review
    /// state are the service's own answer rather than this console's reading of
    /// a stale list row.
    /// </summary>
    public async Task OpenRelationAsync(OwnerRelation relation)
    {
        ArgumentNullException.ThrowIfNull(relation);
        if (TryBeginRelationsAct() is not { } epoch)
        {
            return;
        }

        RelationMessage = null;
        var result = await ExecuteRelationsAsync(
            token => OwnerChannelClient.ReadRelationAsync(ControlPipe, relation.Id, relation.Revision, token),
            code => new OwnerRelationResult(OwnerCommandOutcome.Unavailable, code, null)).ConfigureAwait(true);

        await ProbeAsync().ConfigureAwait(true);
        ApplyRelationAnswer(epoch, "open relation", result);
        Append(DescribeRelationsActForLog("open relation", result.Outcome, result.ErrorCode));
        RaiseChanged();
    }

    /// <summary>
    /// Ranks candidate memory items for the link form through the owner library
    /// search the service already owns (the materialized index generation), and
    /// keeps them in this view's own slot: the library view's page is never
    /// replaced by a search the owner started here.
    /// </summary>
    public async Task SearchRelationCandidatesAsync(string query)
    {
        if (TryBeginRelationsAct() is not { } epoch)
        {
            return;
        }

        RelationCandidateMessage = null;
        var libraryQuery = new OwnerLibraryQuery(
            Text: string.IsNullOrWhiteSpace(query) ? null : query.Trim(),
            Limit: 10);
        var result = await ExecuteRelationsAsync(
            token => OwnerChannelClient.SearchLibraryAsync(ControlPipe, libraryQuery, token),
            code => new OwnerLibrarySearchResult(OwnerCommandOutcome.Unavailable, code, null)).ConfigureAwait(true);

        await ProbeAsync().ConfigureAwait(true);
        ApplyRelationCandidateAnswer(epoch, result);
        Append(DescribeRelationsActForLog("find endpoints", result.Outcome, result.ErrorCode));
        RaiseChanged();
    }

    /// <summary>
    /// Creates one typed link between the two endpoints the owner pinned, with
    /// the kind and the reason the owner chose. The draft is encoded by the
    /// shared relation codec and validated by the shared validator on the
    /// server, which also verifies both endpoints at their live revisions and
    /// refuses a derivation cycle with its own code. Nothing is retried, guessed
    /// or repaired here: a refusal leaves the owner's draft on the form.
    /// </summary>
    public async Task CreateRelationAsync(
        string relationId,
        string zoneId,
        string kindToken,
        string stateToken,
        RelationEndpointPick from,
        RelationEndpointPick to,
        string reason)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        if (TryBeginRelationsAct() is not { } epoch)
        {
            return;
        }

        if (!TryParseRelationKind(kindToken, out var kind) ||
            !TryParseOwnerState(stateToken, out var state) ||
            !TryParseEndpointKind(from.EndpointKind, out var fromKind) ||
            !TryParseEndpointKind(to.EndpointKind, out var toKind))
        {
            RelationsActInFlight = false;
            RelationMessage = "This console did not send the link: the kind, the recorded state or an endpoint kind is not one this console can state. Nothing was written.";
            RaiseChanged();
            return;
        }

        RelationMessage = null;
        var result = await ExecuteRelationsAsync(
            token => OwnerChannelClient.CreateRelationAsync(
                ControlPipe,
                relationId,
                zoneId,
                kind,
                state,
                new RelationEndpoint(from.ResourceId, fromKind, from.Revision),
                new RelationEndpoint(to.ResourceId, toKind, to.Revision),
                reason,
                token),
            code => new OwnerRelationResult(OwnerCommandOutcome.Unavailable, code, null)).ConfigureAwait(true);

        await ProbeAsync().ConfigureAwait(true);
        var applied = ApplyRelationAnswer(epoch, "create link", result);
        Append(DescribeRelationsActForLog("create link", result.Outcome, result.ErrorCode));
        RaiseChanged();
        if (applied && result.Outcome == OwnerCommandOutcome.Completed)
        {
            // The list this console was showing is behind the write it just made.
            await RefreshRelationsAsync().ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Confirms the link the owner opened as the owner's own conclusion. The
    /// record keeps the claim that came with it (an agent's words stay an
    /// agent's words) and only its state, executor and time change.
    /// </summary>
    public Task AcceptRelationAsync() => TransitionOpenRelationAsync(RelationAct.Confirm, rationale: null);

    /// <summary>Rejects the opened link with the owner's rationale, which the core records.</summary>
    public Task RejectRelationAsync(string rationale) => TransitionOpenRelationAsync(RelationAct.Reject, rationale);

    /// <summary>Removes the opened link (retracted) with the owner's explicit rationale.</summary>
    public Task UnlinkRelationAsync(string rationale) => TransitionOpenRelationAsync(RelationAct.Remove, rationale);

    /// <summary>
    /// Undoes a removal: the link returns to proposed, never straight to
    /// accepted, because the shared core refuses to re-assert an acceptance
    /// silently. A relink carries no rationale; the original reason stays the
    /// record's basis.
    /// </summary>
    public Task RelinkRelationAsync() => TransitionOpenRelationAsync(RelationAct.UndoRemoval, rationale: null);

    private enum RelationAct
    {
        Confirm,
        Reject,
        Remove,
        UndoRemoval
    }

    private async Task TransitionOpenRelationAsync(RelationAct act, string? rationale)
    {
        var relation = OpenRelation;
        if (relation is null || TryBeginRelationsAct() is not { } epoch)
        {
            return;
        }

        if (act is RelationAct.Reject or RelationAct.Remove && string.IsNullOrWhiteSpace(rationale))
        {
            RelationsActInFlight = false;
            RelationMessage = "The shared core requires an explicit rationale for this act, so this console did not send it. Write the rationale and try again; the link is unchanged.";
            RaiseChanged();
            return;
        }

        RelationMessage = null;
        var text = rationale?.Trim();
        var result = await ExecuteRelationsAsync(
            token => act switch
            {
                RelationAct.Confirm => OwnerChannelClient.AcceptRelationAsync(ControlPipe, relation, token),
                RelationAct.Reject => OwnerChannelClient.RejectRelationAsync(ControlPipe, relation, text!, token),
                RelationAct.Remove => OwnerChannelClient.UnlinkRelationAsync(ControlPipe, relation, text!, token),
                _ => OwnerChannelClient.RelinkRelationAsync(ControlPipe, relation, token)
            },
            code => new OwnerRelationResult(OwnerCommandOutcome.Unavailable, code, null)).ConfigureAwait(true);

        await ProbeAsync().ConfigureAwait(true);
        var applied = ApplyRelationAnswer(epoch, DescribeRelationActForLog(act), result);
        Append(DescribeRelationsActForLog(DescribeRelationActForLog(act), result.Outcome, result.ErrorCode));
        RaiseChanged();
        if (applied && result.Outcome == OwnerCommandOutcome.Completed)
        {
            // The relationship's revision and state moved, so the list this
            // console was showing is behind the write it just made.
            await RefreshRelationsAsync().ConfigureAwait(true);
        }
    }

    private static string DescribeRelationActForLog(RelationAct act) => act switch
    {
        RelationAct.Confirm => "confirm link",
        RelationAct.Reject => "reject link",
        RelationAct.Remove => "remove link",
        _ => "undo removal"
    };

    /// <summary>
    /// Reports a relation-form value this console refuses to send, so the owner
    /// sees which input is unusable instead of a silently different link. Never
    /// private content.
    /// </summary>
    public void ReportRelationFormProblem(string message)
    {
        RelationMessage = message;
        RaiseChanged();
    }

    /// <summary>Applies one relation listing requested under <paramref name="epoch"/>.</summary>
    internal bool ApplyRelationListAnswer(long epoch, OwnerRelationListResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (epoch != _privateEpoch)
        {
            AppendIgnoredStaleRelationsAnswer("relation list");
            return false;
        }

        if (result.Outcome == OwnerCommandOutcome.Completed && result.Relations is not null)
        {
            Relations = result.Relations;
            RelationsMessage = DescribeRelationsListAnswer(result.Relations);
        }
        else
        {
            RelationsMessage = DescribeRelationsAnswer("list", result.Outcome, result.ErrorCode);
            ApplyRelationsRefusal(result.Outcome, result.ErrorCode);
        }

        return true;
    }

    /// <summary>Applies one candidate search requested under <paramref name="epoch"/>.</summary>
    internal bool ApplyRelationCandidateAnswer(long epoch, OwnerLibrarySearchResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (epoch != _privateEpoch)
        {
            AppendIgnoredStaleRelationsAnswer("endpoint search");
            return false;
        }

        if (result.Outcome == OwnerCommandOutcome.Completed && result.Page is not null)
        {
            RelationCandidates = result.Page.Hits
                .Select(hit => new RelationEndpointPick(
                    hit.Id,
                    Token(nameof(RelationEndpointKind.MemoryItem)),
                    hit.Revision,
                    hit.ZoneId,
                    hit.Id + "@" + hit.Revision.ToString(CultureInfo.InvariantCulture) +
                    "  zone=" + hit.ZoneId + "  kind=" + hit.Kind +
                    "  title=" + (string.IsNullOrEmpty(hit.Title) ? "(none)" : hit.Title)))
                .ToArray();
            RelationCandidateMessage = RelationCandidates.Length == 0
                ? "The service's library search matched no indexed memory item for this text, so no endpoint candidate is offered. A registered source is named directly instead."
                : "The service ranked " + RelationCandidates.Length.ToString(CultureInfo.InvariantCulture) +
                    " candidate memory item(s) from the materialized library index. An item's revision here is the live revision the link would pin.";
        }
        else
        {
            RelationCandidates = Array.Empty<RelationEndpointPick>();
            RelationCandidateMessage = DescribeRelationsAnswer("endpoint search", result.Outcome, result.ErrorCode);
            ApplyRelationsRefusal(result.Outcome, result.ErrorCode);
        }

        return true;
    }

    /// <summary>
    /// Applies one relation act requested under <paramref name="epoch"/>. A
    /// completed write replaces the opened link with the record the service
    /// wrote; a refusal changes nothing, so the owner's draft and the link that
    /// was opened stay exactly as they were.
    /// </summary>
    internal bool ApplyRelationAnswer(long epoch, string act, OwnerRelationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (epoch != _privateEpoch)
        {
            AppendIgnoredStaleRelationsAnswer(act);
            return false;
        }

        if (result.Outcome != OwnerCommandOutcome.Completed || result.Relation is null)
        {
            RelationMessage = DescribeRelationsAnswer(act, result.Outcome, result.ErrorCode);
            ApplyRelationsRefusal(result.Outcome, result.ErrorCode);
            return true;
        }

        OpenRelation = result.Relation;
        RelationMessage = DescribeRelationWriteAnswer(act, result.Relation);
        return true;
    }

    /// <summary>
    /// Drops the relation views, the candidate list and the owner's link draft
    /// of the epoch that just ended, and moves the private epoch so an answer
    /// already in flight cannot restore any of them.
    /// </summary>
    private void ClearPrivateRelationsState()
    {
        _privateEpoch++;
        Relations = Array.Empty<OwnerRelation>();
        RelationsMessage = null;
        OpenRelation = null;
        RelationMessage = null;
        RelationCandidates = Array.Empty<RelationEndpointPick>();
        RelationCandidateMessage = null;
        _relationFormVersion++;
    }

    private void ApplyRelationsRefusal(OwnerCommandOutcome outcome, string? errorCode)
    {
        if (outcome == OwnerCommandOutcome.Refused &&
            errorCode is "vault_locked" or "resource_unavailable" or "relation_unavailable")
        {
            ClearPrivateRelationsState();
        }
    }

    private void AppendIgnoredStaleRelationsAnswer(string act) =>
        Append("ignored a stale " + act + " answer: the unlocked vault state it belonged to is gone, so the cleared relation view stays cleared");

    private long? TryBeginRelationsAct()
    {
        if (!Attached || RelationsActInFlight || ReportedVaultState != OwnerVaultState.Unlocked)
        {
            return null;
        }

        RelationsActInFlight = true;
        RelationMessage = null;
        RaiseChanged();
        return _privateEpoch;
    }

    private async Task<T> ExecuteRelationsAsync<T>(Func<CancellationToken, Task<T>> call, Func<string, T> onFailure)
    {
        try
        {
            using var timeout = new CancellationTokenSource(RelationsActTimeout);
            return await call(timeout.Token).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return onFailure("act_failed:" + ex.GetType().Name);
        }
        finally
        {
            RelationsActInFlight = false;
        }
    }

    /// <summary>
    /// The relation kinds this console offers are the shared domain enum: an
    /// unknown token is refused here instead of being sent as an invented kind.
    /// </summary>
    private static bool TryParseRelationKind(string token, out RelationKind kind)
    {
        foreach (var candidate in Enum.GetValues<RelationKind>())
        {
            if (candidate != RelationKind.Unknown &&
                string.Equals(Token(candidate.ToString()), token, StringComparison.Ordinal))
            {
                kind = candidate;
                return true;
            }
        }

        kind = RelationKind.Unknown;
        return false;
    }

    /// <summary>
    /// A link the owner records is either their own conclusion (accepted) or a
    /// proposal they confirm later; nothing else is a legal creation state.
    /// </summary>
    private static bool TryParseOwnerState(string token, out RelationState state)
    {
        foreach (var candidate in new[] { RelationState.Accepted, RelationState.Proposed })
        {
            if (string.Equals(Token(candidate.ToString()), token, StringComparison.Ordinal))
            {
                state = candidate;
                return true;
            }
        }

        state = RelationState.Unknown;
        return false;
    }

    private static bool TryParseEndpointKind(string token, out RelationEndpointKind kind)
    {
        foreach (var candidate in Enum.GetValues<RelationEndpointKind>())
        {
            if (candidate != RelationEndpointKind.Unknown &&
                string.Equals(Token(candidate.ToString()), token, StringComparison.Ordinal))
            {
                kind = candidate;
                return true;
            }
        }

        kind = RelationEndpointKind.Unknown;
        return false;
    }

    /// <summary>
    /// The spelling this console uses for a domain enum member: the same
    /// lower-camel token the wire carries, so a picked endpoint, a chosen kind
    /// and a recorded state read the same here and in a service reply.
    /// </summary>
    private static string Token(string member) =>
        member.Length == 0 ? member : char.ToLowerInvariant(member[0]) + member[1..];

    /// <summary>
    /// What the service served for the relation list, counted by state, so the
    /// owner can tell a confirmed link from a claim an agent left for review.
    /// </summary>
    private static string DescribeRelationsListAnswer(OwnerRelation[] relations)
    {
        if (relations.Length == 0)
        {
            return "The service answered with an empty relation set: this vault holds no link yet.";
        }

        var proposed = relations.Count(relation => string.Equals(relation.State, Token(nameof(RelationState.Proposed)), StringComparison.Ordinal));
        var accepted = relations.Count(relation => string.Equals(relation.State, Token(nameof(RelationState.Accepted)), StringComparison.Ordinal));
        var removed = relations.Count(relation => string.Equals(relation.State, Token(nameof(RelationState.Retracted)), StringComparison.Ordinal));
        var rejected = relations.Count(relation => string.Equals(relation.State, Token(nameof(RelationState.Rejected)), StringComparison.Ordinal));
        var needingReview = relations.Count(relation =>
            string.Equals(relation.Review.State, Token(nameof(RelationReviewState.NeedsReview)), StringComparison.Ordinal));
        var fromAgents = relations.Count(relation =>
            string.Equals(relation.Executor, Token(nameof(ExecutorKind.Agent)), StringComparison.Ordinal));
        return "The service listed " + relations.Length.ToString(CultureInfo.InvariantCulture) +
            " relation(s) of this vault: accepted=" + accepted.ToString(CultureInfo.InvariantCulture) +
            " proposed=" + proposed.ToString(CultureInfo.InvariantCulture) +
            " retracted=" + removed.ToString(CultureInfo.InvariantCulture) +
            " rejected=" + rejected.ToString(CultureInfo.InvariantCulture) +
            ", of which " + fromAgents.ToString(CultureInfo.InvariantCulture) +
            " were recorded by an agent session (a claim, not your confirmation) and " +
            needingReview.ToString(CultureInfo.InvariantCulture) +
            " need review against current state.";
    }

    private static string DescribeRelationWriteAnswer(string act, OwnerRelation relation)
    {
        var evidence = relation.RequestEvidence.Length == 0
            ? "no caller-reported words are attached"
            : relation.RequestEvidence.Length.ToString(CultureInfo.InvariantCulture) + " caller-reported entry(ies) are attached (a claim, never your confirmation)";
        return act switch
        {
            "create link" =>
                "The service stored " + relation.Kind + " as relation " + relation.Id + " revision " +
                relation.Revision.ToString(CultureInfo.InvariantCulture) + " in state " + relation.State +
                "; executor=" + relation.Executor + ", " + evidence + ".",
            "confirm link" =>
                "The service recorded your confirmation of " + relation.Id + " (state " + relation.State +
                ", state executor=" + relation.StateExecutor + "); the claim that came with the link is unchanged.",
            "reject link" =>
                "The service recorded your rejection of " + relation.Id + " with your rationale (state " + relation.State + ").",
            "remove link" =>
                "The service removed " + relation.Id + " (state " + relation.State +
                ") with your rationale; the record and its endpoints stay readable, and undo returns it to proposed.",
            _ =>
                "The service undid the removal: " + relation.Id + " is " + relation.State +
                " again, not accepted, so confirming it stays your explicit act."
        };
    }

    private static string DescribeRelationsAnswer(string act, OwnerCommandOutcome outcome, string? errorCode)
    {
        var code = errorCode ?? "unknown";
        return outcome switch
        {
            OwnerCommandOutcome.Completed =>
                "The service completed the relation act (" + act + ") but returned no relation payload, so this console shows nothing instead of guessing.",
            OwnerCommandOutcome.Refused => code switch
            {
                "relation_cycle" =>
                    "The service refused this link with its own cycle rule (code: relation_cycle): an item would derive from, or replace, something that already depends on it. related_to is allowed to carry a cycle, so the kind you chose is the difference; nothing was written.",
                "vault_locked" =>
                    "The vault is locked, so the service serves no relation. Unlock the vault (Vault & key) and read the relation set again.",
                "resource_unavailable" =>
                    "The service did not serve that relation (code: resource_unavailable). It may have moved or been removed; nothing else was substituted.",
                "relation_unavailable" =>
                    "The service could not answer this relation read (code: relation_unavailable). No relation content was served.",
                "invalid_request" => act switch
                {
                    "create link" =>
                        "The service refused this link as invalid (code: invalid_request): an endpoint that is not at its live revision or not the kind it claims, a reason the shared validator rejects, an identity that is not a safe relation ID, or a duplicate ID are all refused here. Nothing was written and your draft is unchanged.",
                    _ =>
                        "The service refused this relation act as invalid (code: invalid_request): the act may not be legal from the link's current state, or a rationale is required. Nothing changed."
                },
                _ => "The service refused this relation request (code: " + code + "). Nothing was read or changed."
            },
            OwnerCommandOutcome.OwnerAuthorityDenied =>
                "This endpoint refused owner authority for the relation request (code: " + code + "). An agent channel never serves the owner relation set.",
            OwnerCommandOutcome.ProtocolMismatch =>
                "The endpoint speaks a different protocol revision, so no relation request was performed.",
            _ => "No owner-channel answer reached the service (code: " + code + "), so no relation request was performed."
        };
    }

    private static string DescribeRelationsActForLog(string act, OwnerCommandOutcome outcome, string? errorCode) =>
        "owner act: relations " + act + " -> " + outcome.ToString().ToLowerInvariant() +
        (outcome == OwnerCommandOutcome.Completed ? "" : " (" + (errorCode ?? "unknown") + ")");
}
