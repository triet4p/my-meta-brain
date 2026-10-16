using System.Globalization;
using MetaBrain.Connections;

namespace MetaBrain.OwnerUi;

/// <summary>
/// Catalog publication, access-request approval and session console of the
/// owner console (S5-T6).
///
/// Every value here is an answer of the running service on the owner control
/// pipe: the service owns the encrypted catalog mapping, the public
/// projection, the pending requests, the preview snapshots, the issued
/// grants, the DPAPI handoff path and the live sessions. This session sends
/// the owner's explicit acts and renders what came back; it keeps no second
/// catalog, request, grant or session store, no visibility policy of its own
/// and no plaintext file.
///
/// Publication labels and descriptions are always the exact text the owner
/// typed on this screen: this console never fills them from a private title,
/// path, citation or any other private metadata, and the service derives
/// nothing from the mapped resource either. A pending request is never
/// permission: only an explicit owner approval with its bound preview mints a
/// grant, the bearer travels only into a DPAPI-protected local handoff file,
/// and this console shows that path, never the token. Added members or
/// revisions never enter a frozen scope afterwards.
///
/// Private view state — the catalog mappings, the publication draft, the
/// pending requests, the bound request preview, the issued handoff path, the
/// live sessions and the owner's narrowing draft — lives only in this process
/// and is dropped with the private epoch it belonged to: the epoch moves
/// whenever the vault leaves the unlocked state, and an asynchronous answer
/// is applied only while the epoch it was requested under is still current,
/// after the service has been asked for its state again. A late answer for a
/// finished epoch is dropped, so a lock cannot be undone by a reply that was
/// already in flight. Ending the owner session drops the same state.
/// </summary>
internal sealed partial class OwnerConsoleSession
{
    private static readonly TimeSpan SharingActTimeout = TimeSpan.FromSeconds(20);
    private long _sharingFormVersion;

    /// <summary>Every private owner catalog mapping the service listed, in the order it served them.</summary>
    public OwnerCatalogMapping[] CatalogEntries { get; private set; } = Array.Empty<OwnerCatalogMapping>();

    /// <summary>Owner-facing outcome of the last catalog listing. Never private content.</summary>
    public string? CatalogMessage { get; private set; }

    /// <summary>What the last publication preview would store, plus the live mapped revision.</summary>
    public OwnerCatalogPreviewEntry? CatalogPreview { get; private set; }

    /// <summary>Owner-facing outcome of the last publication preview or write. Never private content.</summary>
    public string? CatalogWriteMessage { get; private set; }

    /// <summary>Every access request the service listed, in the order it served them.</summary>
    public OwnerAccessRequestInfo[] AccessRequests { get; private set; } = Array.Empty<OwnerAccessRequestInfo>();

    /// <summary>Owner-facing outcome of the last request listing. Never private content.</summary>
    public string? AccessRequestsMessage { get; private set; }

    /// <summary>The bound request snapshot the owner previewed, with its exact bodies for the owner's eyes only.</summary>
    public OwnerAccessRequestPreviewInfo? OpenRequestPreview { get; private set; }

    /// <summary>Owner-facing outcome of the last request preview, narrowing, approval or rejection. Never a bearer.</summary>
    public string? AccessDecisionMessage { get; private set; }

    /// <summary>The DPAPI-protected local handoff path the last approval minted, or null when no approval is shown.</summary>
    public string? LastHandoffPath { get; private set; }

    /// <summary>The grant ID the last approval minted, or null when no approval is shown.</summary>
    public string? LastGrantId { get; private set; }

    /// <summary>Every live agent session the service listed, with its effective frozen scope.</summary>
    public OwnerAgentSessionInfo[] AgentSessions { get; private set; } = Array.Empty<OwnerAgentSessionInfo>();

    /// <summary>Policy generation the last session listing reported.</summary>
    public long SessionPolicyGeneration { get; private set; }

    /// <summary>Owner-facing outcome of the last session listing or revocation. Never a bearer.</summary>
    public string? SessionsMessage { get; private set; }

    /// <summary>True while one sharing act is in flight; further sharing acts wait for its answer.</summary>
    public bool SharingActInFlight { get; private set; }

    /// <summary>
    /// Increments whenever this session drops the owner's sharing drafts (the
    /// publication form, the request narrowing form and the handoff display).
    /// The host clears its own sharing controls when it observes a new value,
    /// so no draft of a finished epoch stays on screen.
    /// </summary>
    public long SharingFormVersion => _sharingFormVersion;

    /// <summary>
    /// Reads the private catalog mappings of the unlocked vault. Discovery
    /// stays public-triplet only; only this owner channel sees the mapping.
    /// </summary>
    public async Task RefreshCatalogAsync()
    {
        if (TryBeginSharingAct() is not { } epoch)
        {
            return;
        }

        CatalogMessage = null;
        var result = await ExecuteSharingAsync(
            token => OwnerChannelClient.ListCatalogAsync(ControlPipe, token),
            code => new OwnerCatalogListResult(OwnerCommandOutcome.Unavailable, code, null)).ConfigureAwait(true);

        await ProbeAsync().ConfigureAwait(true);
        ApplyCatalogListAnswer(epoch, result);
        Append(DescribeSharingActForLog("read catalog mappings", result.Outcome, result.ErrorCode));
        RaiseChanged();
    }

    /// <summary>
    /// Shows what publication of one catalog entry would store, plus the live
    /// mapped revision. The label and description are the exact text the owner
    /// typed: nothing on this path fills them from a private title, path or
    /// citation. Mutates nothing and mints no access.
    /// </summary>
    public async Task PreviewCatalogAsync(string catalogId, string label, string description, string resourceId)
    {
        if (TryBeginSharingAct() is not { } epoch)
        {
            return;
        }

        CatalogWriteMessage = null;
        var result = await ExecuteSharingAsync(
            token => OwnerChannelClient.PreviewCatalogAsync(ControlPipe, catalogId, label, description, resourceId, token),
            code => new OwnerCatalogPreviewResult(OwnerCommandOutcome.Unavailable, code, null)).ConfigureAwait(true);

        await ProbeAsync().ConfigureAwait(true);
        ApplyCatalogPreviewAnswer(epoch, result);
        Append(DescribeSharingActForLog("preview catalog publication", result.Outcome, result.ErrorCode));
        RaiseChanged();
    }

    /// <summary>
    /// Publishes exactly the owner-typed label/description against the mapped
    /// resource. Only the ID/label/description triplet reaches discovery; the
    /// private mapping stays encrypted.
    /// </summary>
    public async Task PublishCatalogAsync(string catalogId, string label, string description, string resourceId)
    {
        if (TryBeginSharingAct() is not { } epoch)
        {
            return;
        }

        CatalogWriteMessage = null;
        var result = await ExecuteSharingAsync(
            token => OwnerChannelClient.PublishCatalogAsync(ControlPipe, catalogId, label, description, resourceId, token),
            code => new OwnerCatalogPublishResult(OwnerCommandOutcome.Unavailable, code, null)).ConfigureAwait(true);

        await ProbeAsync().ConfigureAwait(true);
        var applied = ApplyCatalogWriteAnswer(epoch, "publish catalog", result);
        Append(DescribeSharingActForLog("publish catalog", result.Outcome, result.ErrorCode));
        RaiseChanged();
        if (applied && result.Outcome == OwnerCommandOutcome.Completed)
        {
            await RefreshCatalogAsync().ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Withdraws one catalog entry so future discovery converges to no-match.
    /// Already delivered copies cannot be recalled.
    /// </summary>
    public async Task WithdrawCatalogAsync(string catalogId)
    {
        if (string.IsNullOrWhiteSpace(catalogId) || TryBeginSharingAct() is not { } epoch)
        {
            return;
        }

        CatalogWriteMessage = null;
        var result = await ExecuteSharingAsync(
            token => OwnerChannelClient.WithdrawCatalogAsync(ControlPipe, catalogId.Trim(), token),
            code => new OwnerCatalogWithdrawResult(OwnerCommandOutcome.Unavailable, code, null)).ConfigureAwait(true);

        await ProbeAsync().ConfigureAwait(true);
        var applied = ApplyCatalogWriteAnswer(epoch, "withdraw catalog", result);
        Append(DescribeSharingActForLog("withdraw catalog", result.Outcome, result.ErrorCode));
        RaiseChanged();
        if (applied && result.Outcome == OwnerCommandOutcome.Completed)
        {
            await RefreshCatalogAsync().ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Reads the pending access requests with their purpose, catalogs and
    /// resolved snapshots. A listed request is never permission.
    /// </summary>
    public async Task RefreshAccessRequestsAsync()
    {
        if (TryBeginSharingAct() is not { } epoch)
        {
            return;
        }

        AccessRequestsMessage = null;
        var result = await ExecuteSharingAsync(
            token => OwnerChannelClient.ListAccessRequestsAsync(ControlPipe, token),
            code => new OwnerAccessRequestListResult(OwnerCommandOutcome.Unavailable, code, null)).ConfigureAwait(true);

        await ProbeAsync().ConfigureAwait(true);
        ApplyAccessRequestListAnswer(epoch, result);
        Append(DescribeSharingActForLog("read access requests", result.Outcome, result.ErrorCode));
        RaiseChanged();
    }

    /// <summary>
    /// Binds one pending request to its frozen snapshot and shows its exact
    /// bodies for the owner's eyes on this console only. Nothing is written to
    /// disk by this read.
    /// </summary>
    public async Task PreviewAccessRequestAsync(OwnerAccessRequestInfo request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (TryBeginSharingAct() is not { } epoch)
        {
            return;
        }

        AccessDecisionMessage = null;
        var result = await ExecuteSharingAsync(
            token => OwnerChannelClient.PreviewAccessRequestAsync(ControlPipe, request.RequestId, token),
            code => new OwnerAccessRequestPreviewResult(OwnerCommandOutcome.Unavailable, code, null)).ConfigureAwait(true);

        await ProbeAsync().ConfigureAwait(true);
        ApplyAccessRequestPreviewAnswer(epoch, result);
        Append(DescribeSharingActForLog("preview access request", result.Outcome, result.ErrorCode));
        RaiseChanged();
    }

    /// <summary>
    /// Approves exactly the narrowed scope the owner chose: a subset of the
    /// bound preview's resource IDs and of its requested operations, with an
    /// explicit UTC expiry and optional concrete destinations. Any growth past
    /// the bound snapshot is refused by the service. The issued bearer is
    /// written only into a DPAPI-protected local handoff file; this session
    /// keeps its path, never the token. The default when the owner names no
    /// operations is read-only.
    /// </summary>
    public async Task ApproveAccessRequestAsync(
        string[]? resourceIds,
        string[]? operations,
        string[]? destinationResourceIds,
        DateTimeOffset expiresAtUtc,
        string? provider,
        string? model,
        decimal? maximumCostUsd,
        string handoffPath)
    {
        var preview = OpenRequestPreview;
        if (preview is null || TryBeginSharingAct() is not { } epoch)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(handoffPath))
        {
            SharingActInFlight = false;
            AccessDecisionMessage = "This console did not approve the request: name the local handoff file the DPAPI-protected token file will be written to. Nothing was minted.";
            RaiseChanged();
            return;
        }

        AccessDecisionMessage = null;
        var result = await ExecuteSharingAsync(
            token => OwnerChannelClient.ApproveAccessRequestAsync(
                ControlPipe, preview.RequestId, preview.PreviewId,
                resourceIds, operations, destinationResourceIds,
                expiresAtUtc, provider, model, maximumCostUsd,
                handoffPath.Trim(), token),
            code => new OwnerAccessApprovalResult(OwnerCommandOutcome.Unavailable, code, null, null)).ConfigureAwait(true);

        await ProbeAsync().ConfigureAwait(true);
        var applied = ApplyAccessApprovalAnswer(epoch, result);
        var decisionText = AccessDecisionMessage;
        var handoffShown = LastHandoffPath;
        var grantShown = LastGrantId;
        Append(DescribeSharingActForLog("approve access request", result.Outcome, result.ErrorCode));
        RaiseChanged();
        if (applied && result.Outcome == OwnerCommandOutcome.Completed)
        {
            await RefreshAccessRequestsAsync().ConfigureAwait(true);
            await RefreshSessionsAsync().ConfigureAwait(true);
            // The chained listing refreshes belong to newer epochs: restore
            // the approval's own handoff display, which is the only place the
            // console shows the local path of the bearer it just minted.
            LastHandoffPath = handoffShown;
            LastGrantId = grantShown;
            AccessDecisionMessage = decisionText;
            RaiseChanged();
        }
    }

    /// <summary>
    /// Rejects one pending request with the owner's explicit decision. No
    /// grant is created by this path.
    /// </summary>
    public async Task RejectAccessRequestAsync(OwnerAccessRequestInfo request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (TryBeginSharingAct() is not { } epoch)
        {
            return;
        }

        AccessDecisionMessage = null;
        var result = await ExecuteSharingAsync(
            token => OwnerChannelClient.RejectAccessRequestAsync(ControlPipe, request.RequestId, token),
            code => new OwnerAccessRejectResult(OwnerCommandOutcome.Unavailable, code, null)).ConfigureAwait(true);

        await ProbeAsync().ConfigureAwait(true);
        var applied = ApplyAccessRejectAnswer(epoch, result);
        Append(DescribeSharingActForLog("reject access request", result.Outcome, result.ErrorCode));
        RaiseChanged();
        if (applied && result.Outcome == OwnerCommandOutcome.Completed)
        {
            await RefreshAccessRequestsAsync().ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Reads the live agent sessions with their effective frozen scopes,
    /// expiries and policy generations. Carries no verifier or bearer.
    /// </summary>
    public async Task RefreshSessionsAsync()
    {
        if (TryBeginSharingAct() is not { } epoch)
        {
            return;
        }

        SessionsMessage = null;
        var result = await ExecuteSharingAsync(
            token => OwnerChannelClient.ListSessionsAsync(ControlPipe, token),
            code => new OwnerSessionListResult(OwnerCommandOutcome.Unavailable, code, 0, null)).ConfigureAwait(true);

        await ProbeAsync().ConfigureAwait(true);
        ApplySessionListAnswer(epoch, result);
        Append(DescribeSharingActForLog("read sessions", result.Outcome, result.ErrorCode));
        RaiseChanged();
    }

    /// <summary>
    /// Revokes one live session by its ID. The bearer, if already redeemed,
    /// authorizes nothing afterwards.
    /// </summary>
    public async Task RevokeSessionAsync(OwnerAgentSessionInfo session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (TryBeginSharingAct() is not { } epoch)
        {
            return;
        }

        SessionsMessage = null;
        var result = await ExecuteSharingAsync(
            token => OwnerChannelClient.RevokeSessionAsync(ControlPipe, session.SessionId, token),
            code => new OwnerSessionRevokeResult(OwnerCommandOutcome.Unavailable, code, null)).ConfigureAwait(true);

        await ProbeAsync().ConfigureAwait(true);
        var applied = ApplySessionRevokeAnswer(epoch, result);
        var revokeText = SessionsMessage;
        Append(DescribeSharingActForLog("revoke session", result.Outcome, result.ErrorCode));
        RaiseChanged();
        if (applied && result.Outcome == OwnerCommandOutcome.Completed)
        {
            await RefreshSessionsAsync().ConfigureAwait(true);
            // The chained listing refresh belongs to a newer epoch: restore
            // the revocation's own message, which is the only place the
            // console confirms the bearer authorizes nothing afterwards.
            SessionsMessage = revokeText;
            RaiseChanged();
        }
    }

    /// <summary>
    /// Reports a sharing-form value this console refuses to send, so the owner
    /// sees which input is unusable instead of a silently different approval.
    /// Never a bearer or private content.
    /// </summary>
    public void ReportSharingFormProblem(string message)
    {
        AccessDecisionMessage = message;
        RaiseChanged();
    }

    /// <summary>Applies one catalog listing requested under <paramref name="epoch"/>.</summary>
    internal bool ApplyCatalogListAnswer(long epoch, OwnerCatalogListResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (epoch != _privateEpoch)
        {
            AppendIgnoredStaleSharingAnswer("catalog list");
            return false;
        }

        if (result.Outcome == OwnerCommandOutcome.Completed && result.Entries is not null)
        {
            CatalogEntries = result.Entries;
            CatalogMessage = DescribeCatalogListAnswer(result.Entries);
        }
        else
        {
            CatalogMessage = DescribeSharingAnswer("catalog list", result.Outcome, result.ErrorCode);
            ApplySharingRefusal(result.Outcome, result.ErrorCode);
        }

        return true;
    }

    /// <summary>Applies one catalog preview requested under <paramref name="epoch"/>.</summary>
    internal bool ApplyCatalogPreviewAnswer(long epoch, OwnerCatalogPreviewResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (epoch != _privateEpoch)
        {
            AppendIgnoredStaleSharingAnswer("catalog preview");
            return false;
        }

        if (result.Outcome == OwnerCommandOutcome.Completed && result.Preview is not null)
        {
            CatalogPreview = result.Preview;
            CatalogWriteMessage = DescribeCatalogPreviewAnswer(result.Preview);
        }
        else
        {
            CatalogWriteMessage = DescribeSharingAnswer("catalog preview", result.Outcome, result.ErrorCode);
            ApplySharingRefusal(result.Outcome, result.ErrorCode);
        }

        return true;
    }

    /// <summary>Applies one catalog write requested under <paramref name="epoch"/>.</summary>
    internal bool ApplyCatalogWriteAnswer(long epoch, string act, OwnerCatalogPublishResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (epoch != _privateEpoch)
        {
            AppendIgnoredStaleSharingAnswer(act);
            return false;
        }

        CatalogWriteMessage = result.Outcome == OwnerCommandOutcome.Completed
            ? (act == "publish catalog"
                ? "The service published catalog entry " + (result.CatalogId ?? "(unknown)") + ": only the exact label/description you typed is public now, and the private mapping stays encrypted."
                : "The service withdrew catalog entry " + (result.CatalogId ?? "(unknown)") + ": future discovery serves no_match for it (already delivered copies cannot be recalled).")
            : DescribeSharingAnswer(act, result.Outcome, result.ErrorCode);
        if (result.Outcome != OwnerCommandOutcome.Completed)
        {
            ApplySharingRefusal(result.Outcome, result.ErrorCode);
        }

        return true;
    }

    /// <summary>Applies one catalog withdrawal requested under <paramref name="epoch"/>.</summary>
    internal bool ApplyCatalogWriteAnswer(long epoch, string act, OwnerCatalogWithdrawResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (epoch != _privateEpoch)
        {
            AppendIgnoredStaleSharingAnswer(act);
            return false;
        }

        CatalogWriteMessage = result.Outcome == OwnerCommandOutcome.Completed
            ? "The service withdrew catalog entry " + (result.CatalogId ?? "(unknown)") + ": future discovery serves no_match for it (already delivered copies cannot be recalled)."
            : DescribeSharingAnswer(act, result.Outcome, result.ErrorCode);
        if (result.Outcome != OwnerCommandOutcome.Completed)
        {
            ApplySharingRefusal(result.Outcome, result.ErrorCode);
        }

        return true;
    }

    /// <summary>Applies one request listing requested under <paramref name="epoch"/>.</summary>
    internal bool ApplyAccessRequestListAnswer(long epoch, OwnerAccessRequestListResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (epoch != _privateEpoch)
        {
            AppendIgnoredStaleSharingAnswer("access requests");
            return false;
        }

        if (result.Outcome == OwnerCommandOutcome.Completed && result.Requests is not null)
        {
            AccessRequests = result.Requests;
            // A new listing invalidates any bound preview from an older one,
            // but the handoff display of an approval that just completed stays:
            // it is the only place the console shows the local path of the
            // bearer it just minted, and the service answer carries it too.
            OpenRequestPreview = null;
            AccessRequestsMessage = DescribeAccessRequestListAnswer(result.Requests);
        }
        else
        {
            AccessRequestsMessage = DescribeSharingAnswer("access requests", result.Outcome, result.ErrorCode);
            ApplySharingRefusal(result.Outcome, result.ErrorCode);
        }

        return true;
    }

    /// <summary>Applies one bound request preview requested under <paramref name="epoch"/>.</summary>
    internal bool ApplyAccessRequestPreviewAnswer(long epoch, OwnerAccessRequestPreviewResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (epoch != _privateEpoch)
        {
            AppendIgnoredStaleSharingAnswer("request preview");
            return false;
        }

        if (result.Outcome == OwnerCommandOutcome.Completed && result.Preview is not null)
        {
            OpenRequestPreview = result.Preview;
            LastHandoffPath = null;
            LastGrantId = null;
            AccessDecisionMessage = DescribeAccessRequestPreviewAnswer(result.Preview);
        }
        else
        {
            AccessDecisionMessage = DescribeSharingAnswer("request preview", result.Outcome, result.ErrorCode);
            ApplySharingRefusal(result.Outcome, result.ErrorCode);
        }

        return true;
    }

    /// <summary>Applies one approval requested under <paramref name="epoch"/>.</summary>
    internal bool ApplyAccessApprovalAnswer(long epoch, OwnerAccessApprovalResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (epoch != _privateEpoch)
        {
            AppendIgnoredStaleSharingAnswer("request approval");
            return false;
        }

        if (result.Outcome == OwnerCommandOutcome.Completed)
        {
            LastGrantId = result.GrantId;
            LastHandoffPath = result.HandoffPath;
            OpenRequestPreview = null;
            AccessDecisionMessage = "The service minted grant " + (result.GrantId ?? "(unknown)") +
                " for exactly the narrowed scope you approved. The bearer is in the DPAPI-protected local handoff file " +
                (result.HandoffPath ?? "(unknown)") +
                " — this console shows that path, never the token. Hand that file to the connector on this machine; redemption consumes it once.";
        }
        else
        {
            AccessDecisionMessage = DescribeSharingAnswer("request approval", result.Outcome, result.ErrorCode);
            ApplySharingRefusal(result.Outcome, result.ErrorCode);
        }

        return true;
    }

    /// <summary>Applies one rejection requested under <paramref name="epoch"/>.</summary>
    internal bool ApplyAccessRejectAnswer(long epoch, OwnerAccessRejectResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (epoch != _privateEpoch)
        {
            AppendIgnoredStaleSharingAnswer("request rejection");
            return false;
        }

        if (result.Outcome == OwnerCommandOutcome.Completed)
        {
            OpenRequestPreview = null;
            LastHandoffPath = null;
            LastGrantId = null;
            AccessDecisionMessage = "The service rejected request " + (result.RequestId ?? "(unknown)") + ": no grant was created and nothing was minted.";
        }
        else
        {
            AccessDecisionMessage = DescribeSharingAnswer("request rejection", result.Outcome, result.ErrorCode);
            ApplySharingRefusal(result.Outcome, result.ErrorCode);
        }

        return true;
    }

    /// <summary>Applies one session listing requested under <paramref name="epoch"/>.</summary>
    internal bool ApplySessionListAnswer(long epoch, OwnerSessionListResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (epoch != _privateEpoch)
        {
            AppendIgnoredStaleSharingAnswer("session list");
            return false;
        }

        if (result.Outcome == OwnerCommandOutcome.Completed && result.Sessions is not null)
        {
            AgentSessions = result.Sessions;
            SessionPolicyGeneration = result.PolicyGeneration;
            SessionsMessage = DescribeSessionListAnswer(result.Sessions, result.PolicyGeneration);
        }
        else
        {
            SessionsMessage = DescribeSharingAnswer("session list", result.Outcome, result.ErrorCode);
            ApplySharingRefusal(result.Outcome, result.ErrorCode);
        }

        return true;
    }

    /// <summary>Applies one session revocation requested under <paramref name="epoch"/>.</summary>
    internal bool ApplySessionRevokeAnswer(long epoch, OwnerSessionRevokeResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (epoch != _privateEpoch)
        {
            AppendIgnoredStaleSharingAnswer("session revocation");
            return false;
        }

        SessionsMessage = result.Outcome == OwnerCommandOutcome.Completed
            ? "The service revoked session " + (result.SessionId ?? "(unknown)") + ": its bearer authorizes nothing afterwards."
            : DescribeSharingAnswer("session revocation", result.Outcome, result.ErrorCode);
        if (result.Outcome != OwnerCommandOutcome.Completed)
        {
            ApplySharingRefusal(result.Outcome, result.ErrorCode);
        }

        return true;
    }

    /// <summary>
    /// Drops the catalog, request, preview, handoff and session views plus the
    /// owner's sharing drafts of the epoch that just ended, and moves the
    /// private epoch so an answer already in flight cannot restore any of them.
    /// </summary>
    private void ClearPrivateSharingState()
    {
        _privateEpoch++;
        CatalogEntries = Array.Empty<OwnerCatalogMapping>();
        CatalogMessage = null;
        CatalogPreview = null;
        CatalogWriteMessage = null;
        AccessRequests = Array.Empty<OwnerAccessRequestInfo>();
        AccessRequestsMessage = null;
        OpenRequestPreview = null;
        AccessDecisionMessage = null;
        LastHandoffPath = null;
        LastGrantId = null;
        AgentSessions = Array.Empty<OwnerAgentSessionInfo>();
        SessionPolicyGeneration = 0;
        SessionsMessage = null;
        _sharingFormVersion++;
    }

    private void ApplySharingRefusal(OwnerCommandOutcome outcome, string? errorCode)
    {
        if (outcome == OwnerCommandOutcome.Refused &&
            errorCode is "vault_locked" or "resource_unavailable" or "request_unavailable" or "catalog_state_unavailable" or "session_unavailable")
        {
            ClearPrivateSharingState();
        }
    }

    private void AppendIgnoredStaleSharingAnswer(string act) =>
        Append("ignored a stale " + act + " answer: the unlocked vault state it belonged to is gone, so the cleared sharing view stays cleared");

    private long? TryBeginSharingAct()
    {
        if (!Attached || SharingActInFlight || ReportedVaultState != OwnerVaultState.Unlocked)
        {
            return null;
        }

        SharingActInFlight = true;
        AccessDecisionMessage = null;
        RaiseChanged();
        return _privateEpoch;
    }

    private async Task<T> ExecuteSharingAsync<T>(Func<CancellationToken, Task<T>> call, Func<string, T> onFailure)
    {
        try
        {
            using var timeout = new CancellationTokenSource(SharingActTimeout);
            return await call(timeout.Token).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return onFailure("act_failed:" + ex.GetType().Name);
        }
        finally
        {
            SharingActInFlight = false;
        }
    }

    private static string DescribeCatalogListAnswer(OwnerCatalogMapping[] entries)
    {
        if (entries.Length == 0)
        {
            return "The service answered with an empty owner catalog: nothing is published yet, and discovery serves no_match.";
        }

        return "The service listed " + entries.Length.ToString(CultureInfo.InvariantCulture) +
            " private catalog mapping(s). Only each entry's ID, label and description are public; the resource/revision pair beside them stays on this owner channel.";
    }

    private static string DescribeCatalogPreviewAnswer(OwnerCatalogPreviewEntry preview) =>
        "The service previewed catalog entry " + preview.CatalogId + ": exactly the label/description you typed would be public, " +
        "mapped to your resource " + preview.ResourceId + " at its live revision " +
        preview.ResourceRevision.ToString(CultureInfo.InvariantCulture) +
        (preview.ExistingEntry is null ? " (no entry with this ID is published yet)."
            : " (an entry with this ID is already published and would be replaced).") +
        " Nothing was published by this preview and no access was minted.";

    private static string DescribeAccessRequestListAnswer(OwnerAccessRequestInfo[] requests)
    {
        if (requests.Length == 0)
        {
            return "The service answered with no access request. A request appears here only after an agent asks for published catalog entries.";
        }

        var pending = requests.Count(request => string.Equals(request.Status, "pending", StringComparison.Ordinal));
        return "The service listed " + requests.Length.ToString(CultureInfo.InvariantCulture) +
            " access request(s), of which " + pending.ToString(CultureInfo.InvariantCulture) +
            " are still pending. A pending request is never permission: only your explicit approval below mints a grant.";
    }

    private static string DescribeAccessRequestPreviewAnswer(OwnerAccessRequestPreviewInfo preview) =>
        "The service bound request " + preview.RequestId + " to preview " + preview.PreviewId + ": " +
        preview.Resources.Length.ToString(CultureInfo.InvariantCulture) + " concrete resource(s) at their exact revisions, " +
        "operations " + (preview.Operations.Length == 0 ? "(none)" : string.Join(",", preview.Operations)) +
        ", purpose \"" + preview.Purpose + "\". " +
        "An approval below mints only a narrowed subset of these IDs and operations — never more — " +
        "and any revision, membership or catalog change before the approval fails it closed, so a stale preview can never mint.";

    private static string DescribeSessionListAnswer(OwnerAgentSessionInfo[] sessions, long generation) =>
        sessions.Length == 0
            ? "The service answered with no live agent session at policy generation " + generation.ToString(CultureInfo.InvariantCulture) + "."
            : "The service listed " + sessions.Length.ToString(CultureInfo.InvariantCulture) +
                " live session(s) at policy generation " + generation.ToString(CultureInfo.InvariantCulture) +
                ". Each row is the session's effective frozen scope: concrete IDs/revisions, operations, expiry and egress. " +
                "Added members or revisions never enter a frozen scope afterwards.";

    private static string DescribeSharingAnswer(string act, OwnerCommandOutcome outcome, string? errorCode)
    {
        var code = errorCode ?? "unknown";
        return outcome switch
        {
            OwnerCommandOutcome.Completed =>
                "The service completed the sharing act (" + act + ") but returned no sharing payload, so this console shows nothing instead of guessing.",
            OwnerCommandOutcome.Refused => code switch
            {
                "vault_locked" =>
                    "The vault is locked, so the service serves no catalog, request or session. Unlock the vault (Vault & key) and read them again.",
                "catalog_unknown" =>
                    "The service has no catalog entry with that ID (code: catalog_unknown). Nothing was withdrawn or changed.",
                "catalog_unknown_resource" =>
                    "The mapped private resource is not registered at a live revision (code: catalog_unknown_resource). Nothing was published.",
                "invalid_catalog_request" =>
                    "The service refused this publication as invalid (code: invalid_catalog_request): the catalog ID, the owner label/description or the mapped resource is not one this console can state. Nothing was published and your draft is unchanged.",
                "request_unknown" =>
                    "The service has no such pending request or preview (code: request_unknown). It may have been decided already; nothing was minted.",
                "request_not_pending" =>
                    "The service refused because the request is already decided (code: request_not_pending). A decided request never mints a second grant.",
                "request_stale" =>
                    "The service refused the bound preview as stale (code: request_stale): a resource revision, collection membership, catalog mapping or policy generation changed after the preview. Take a new preview; nothing was minted.",
                "request_invalid" =>
                    "The service refused the narrowed scope as invalid (code: request_invalid): it names a resource or operation outside the bound preview, or the expiry/provider disclosure is not one the preview allows. Nothing was minted and the preview is unchanged.",
                "request_unavailable" =>
                    "The service could not answer this request read (code: request_unavailable). No private content was served.",
                "handoff_destination_unavailable" =>
                    "The service approved the scope but this console could not create the local handoff file (code: handoff_destination_unavailable). Choose a new file in an existing folder; nothing readable was left behind.",
                "token_handoff_failed" =>
                    "The service approved the scope but this console could not write the DPAPI-protected handoff file (code: token_handoff_failed). Nothing readable was left behind.",
                "resource_unavailable" =>
                    "The service did not serve that sharing record (code: resource_unavailable). It may have moved or been removed; nothing else was substituted.",
                "session_unknown" or "session_unavailable" =>
                    "The service could not answer this session read (code: " + code + "). No session content was served.",
                "session_expired" =>
                    "The service reports that session as expired (code: session_expired). Its bearer authorizes nothing afterwards.",
                "session_superseded" =>
                    "The service reports that session as superseded by a lock, restart or policy change (code: session_superseded). Its bearer authorizes nothing afterwards.",
                "invalid_request" => act switch
                {
                    "publish catalog" =>
                        "The service refused this publication as invalid (code: invalid_request). Nothing was published and your draft is unchanged.",
                    _ =>
                        "The service refused this sharing act as invalid (code: invalid_request). Nothing changed."
                },
                _ => "The service refused this sharing request (code: " + code + "). Nothing was read or changed."
            },
            OwnerCommandOutcome.OwnerAuthorityDenied =>
                "This endpoint refused owner authority for the sharing request (code: " + code + "). An agent channel never serves the owner catalog, requests or sessions.",
            OwnerCommandOutcome.ProtocolMismatch =>
                "The endpoint speaks a different protocol revision, so no sharing request was performed.",
            _ => "No owner-channel answer reached the service (code: " + code + "), so no sharing request was performed."
        };
    }

    private static string DescribeSharingActForLog(string act, OwnerCommandOutcome outcome, string? errorCode) =>
        "owner act: sharing " + act + " -> " + outcome.ToString().ToLowerInvariant() +
        (outcome == OwnerCommandOutcome.Completed ? "" : " (" + (errorCode ?? "unknown") + ")");
}
