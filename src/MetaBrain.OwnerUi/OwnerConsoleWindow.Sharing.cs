using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using MetaBrain.Connections;

namespace MetaBrain.OwnerUi;

/// <summary>
/// Sharing view of the owner console (S5-T6): catalog publication, pending
/// access requests with purpose, owner narrowing/approval/rejection, the
/// DPAPI-protected local token handoff path, and the live session console
/// with effective scope, expiry and revocation.
/// </summary>
public partial class OwnerConsoleWindow : System.Windows.Window
{
    /// <summary>
    /// Empties the sharing drafts: the publication form, the narrowing form
    /// and the handoff path display. Always the whole draft, so no part of a
    /// finished epoch can be sent as if it belonged to this one. The bound
    /// preview itself stays a session answer and is cleared with the session.
    /// </summary>
    private void SyncSharingForm()
    {
        CatalogIdInput.Text = string.Empty;
        CatalogResourceInput.Text = string.Empty;
        CatalogLabelInput.Text = string.Empty;
        CatalogDescriptionInput.Text = string.Empty;
        ApproveResourceIdsInput.Text = string.Empty;
        ApproveOperationsInput.Text = string.Empty;
        ApproveDestinationIdsInput.Text = string.Empty;
        ApproveExpiryInput.Text = string.Empty;
        ApproveProviderInput.Text = string.Empty;
        ApproveModelInput.Text = string.Empty;
        ApproveMaxCostInput.Text = string.Empty;
        ApproveHandoffInput.Text = string.Empty;
    }

    private async Task RefreshSharingAsync()
    {
        await _session.RefreshCatalogAsync().ConfigureAwait(true);
        await _session.RefreshAccessRequestsAsync().ConfigureAwait(true);
        await _session.RefreshSessionsAsync().ConfigureAwait(true);
    }

    private async void OnCatalogPreviewClicked(object sender, RoutedEventArgs e) =>
        await _session.PreviewCatalogAsync(
            CatalogIdInput.Text.Trim(), CatalogLabelInput.Text, CatalogDescriptionInput.Text, CatalogResourceInput.Text.Trim());

    private async void OnCatalogPublishClicked(object sender, RoutedEventArgs e)
    {
        var catalogId = CatalogIdInput.Text.Trim();
        var resourceId = CatalogResourceInput.Text.Trim();
        var label = CatalogLabelInput.Text;
        var description = CatalogDescriptionInput.Text;
        if (catalogId.Length == 0 || resourceId.Length == 0 || label.Length == 0)
        {
            _session.ReportSharingFormProblem(
                "This console did not publish: it needs the catalog ID you name, the mapped resource ID and the public label. Nothing was published and no discovery changed.");
            return;
        }

        await _session.PublishCatalogAsync(catalogId, label, description, resourceId);
    }

    private async void OnCatalogRefreshClicked(object sender, RoutedEventArgs e) =>
        await _session.RefreshCatalogAsync();

    private async void OnCatalogWithdrawClicked(object sender, RoutedEventArgs e)
    {
        if (CatalogList.SelectedItem is CatalogRow row)
        {
            await _session.WithdrawCatalogAsync(row.Entry.CatalogId);
        }
    }

    private void OnCatalogSelectionChanged(object sender, SelectionChangedEventArgs e) => Render();

    private async void OnCatalogListKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter)
        {
            return;
        }

        e.Handled = true;
        if (CatalogList.SelectedItem is CatalogRow row)
        {
            CatalogIdInput.Text = row.Entry.CatalogId;
            CatalogResourceInput.Text = row.Entry.ResourceId;
            CatalogLabelInput.Text = row.Entry.Label;
            CatalogDescriptionInput.Text = row.Entry.Description;
            Render();
        }
    }

    private async void OnRequestsRefreshClicked(object sender, RoutedEventArgs e) =>
        await _session.RefreshAccessRequestsAsync();

    private async void OnRequestPreviewClicked(object sender, RoutedEventArgs e) =>
        await PreviewSelectedRequestAsync();

    private void OnRequestsSelectionChanged(object sender, SelectionChangedEventArgs e) => Render();

    private async void OnRequestsListKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter)
        {
            return;
        }

        e.Handled = true;
        await PreviewSelectedRequestAsync();
    }

    private async Task PreviewSelectedRequestAsync()
    {
        if (RequestsList.SelectedItem is AccessRequestRow row)
        {
            await _session.PreviewAccessRequestAsync(row.Request);
        }
    }

    private async void OnRequestApproveClicked(object sender, RoutedEventArgs e)
    {
        var preview = _session.OpenRequestPreview;
        if (preview is null)
        {
            _session.ReportSharingFormProblem(
                "This console did not approve anything: preview a pending request first, so the approval binds the exact snapshot you saw. Nothing was minted.");
            return;
        }

        var resourceIds = SplitIdentifiers(ApproveResourceIdsInput.Text);
        var operations = SplitIdentifiers(ApproveOperationsInput.Text);
        var destinations = SplitIdentifiers(ApproveDestinationIdsInput.Text);
        if (ApproveResourceIdsInput.Text.Trim().Length > 0 && resourceIds is null)
        {
            _session.ReportSharingFormProblem(
                "This console did not approve the request: the resource IDs are not a nonempty comma-separated list of unique IDs. Nothing was minted.");
            return;
        }

        if (ApproveOperationsInput.Text.Trim().Length > 0 && operations is null)
        {
            _session.ReportSharingFormProblem(
                "This console did not approve the request: the operations are not a nonempty comma-separated list of unique operations. Nothing was minted.");
            return;
        }

        if (ApproveDestinationIdsInput.Text.Trim().Length > 0 && destinations is null)
        {
            _session.ReportSharingFormProblem(
                "This console did not approve the request: the destination IDs are not a nonempty comma-separated list of unique IDs. Nothing was minted.");
            return;
        }

        var expiryText = ApproveExpiryInput.Text.Trim();
        var hasExplicitUtcSuffix = expiryText.EndsWith("Z", StringComparison.Ordinal) ||
            expiryText.EndsWith("+00:00", StringComparison.Ordinal);
        if (!hasExplicitUtcSuffix ||
            !DateTimeOffset.TryParse(expiryText, CultureInfo.InvariantCulture, DateTimeStyles.None, out var expiresAtUtc) ||
            expiresAtUtc.Offset != TimeSpan.Zero)
        {
            _session.ReportSharingFormProblem(
                "This console did not approve the request: the expiry must be an explicit UTC instant (ISO 8601 with a Z or +00:00 suffix). Nothing was minted.");
            return;
        }

        var provider = ApproveProviderInput.Text.Trim();
        var model = ApproveModelInput.Text.Trim();
        var costText = ApproveMaxCostInput.Text.Trim();
        decimal? maximumCostUsd = null;
        if (costText.Length > 0)
        {
            if (!decimal.TryParse(costText, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsedCost))
            {
                _session.ReportSharingFormProblem(
                    "This console did not approve the request: the cost limit is not a number. Nothing was minted.");
                return;
            }

            maximumCostUsd = parsedCost;
        }

        await _session.ApproveAccessRequestAsync(
            resourceIds, operations, destinations, expiresAtUtc,
            provider.Length == 0 ? null : provider,
            model.Length == 0 ? null : model,
            maximumCostUsd,
            ApproveHandoffInput.Text);
    }

    private async void OnRequestRejectClicked(object sender, RoutedEventArgs e)
    {
        if (RequestsList.SelectedItem is AccessRequestRow row)
        {
            await _session.RejectAccessRequestAsync(row.Request);
        }
        else
        {
            _session.ReportSharingFormProblem(
                "This console rejected nothing: choose the pending request to reject first. Nothing changed.");
        }
    }

    private async void OnSessionsRefreshClicked(object sender, RoutedEventArgs e) =>
        await _session.RefreshSessionsAsync();

    private async void OnSessionRevokeClicked(object sender, RoutedEventArgs e)
    {
        if (SessionsList.SelectedItem is AgentSessionRow row)
        {
            await _session.RevokeSessionAsync(row.Session);
        }
    }

    private void OnSessionsSelectionChanged(object sender, SelectionChangedEventArgs e) => Render();

    private async void OnSessionsListKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter)
        {
            return;
        }

        e.Handled = true;
        Render();
    }

    private static string[]? SplitIdentifiers(string text)
    {
        if (text.Trim().Length == 0)
        {
            return null;
        }

        var identifiers = text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return identifiers.Length > 0 && identifiers.Distinct(StringComparer.Ordinal).Count() == identifiers.Length
            ? identifiers
            : null;
    }

    /// <summary>
    /// Renders the sharing console from the service's answers only: the
    /// private catalog mappings, the publication preview, the pending
    /// requests with purpose, the bound request preview with its bodies, the
    /// local handoff path of the last approval, and the live sessions with
    /// their effective frozen scopes. When the vault is not unlocked no
    /// sharing content is shown at all, and the bearer itself is never shown:
    /// only the DPAPI-protected handoff path is.
    /// </summary>
    private void RenderSharing(bool attached)
    {
        var state = _session.ReportedVaultState;
        var inFlight = _session.SharingActInFlight;
        var unlocked = state == OwnerVaultState.Unlocked;

        SharingStateText.Text = state switch
        {
            OwnerVaultState.Unlocked =>
                "Vault: unlocked — the service serves the private catalog, the pending requests and the live sessions. A request here is never permission: only an approval below mints a grant.",
            OwnerVaultState.Locked =>
                "Vault: locked — no catalog, request or session is available or shown. Unlock in Vault & key to use this console.",
            OwnerVaultState.Locking =>
                "Vault: locking — the service is draining private work; the sharing view of this screen is cleared.",
            OwnerVaultState.Uninitialized =>
                "Vault: not provisioned — there is no vault and therefore nothing to publish or approve.",
            _ => attached
                ? "Vault: unknown — no usable owner-channel answer, so no sharing view is shown."
                : "Vault: not reported — this console holds no owner session."
        };

        if (!ReferenceEquals(_renderedCatalog, _session.CatalogEntries))
        {
            _renderedCatalog = _session.CatalogEntries;
            var previousId = (CatalogList.SelectedItem as CatalogRow)?.Entry.CatalogId;
            var rows = _session.CatalogEntries.Select(entry => new CatalogRow(entry)).ToArray();
            CatalogList.ItemsSource = rows;
            CatalogList.SelectedItem = previousId is null
                ? null
                : rows.FirstOrDefault(row => string.Equals(row.Entry.CatalogId, previousId, StringComparison.Ordinal));
        }

        var preview = _session.CatalogPreview;
        CatalogPreviewText.Text = preview is null
            ? (unlocked
                ? "No publication preview has been answered in this session. A preview shows exactly what would be stored and mints nothing."
                : string.Empty)
            : "preview id=" + preview.CatalogId + "  label=" + preview.Label + "  description=" + preview.Description +
                "\nmaps resource=" + preview.ResourceId + "  live revision=" + preview.ResourceRevision.ToString(CultureInfo.InvariantCulture) +
                (preview.ExistingEntry is null ? "  (no entry with this ID is published yet)"
                    : "  (an entry with this ID is already published and would be replaced)") +
                "\nOnly the ID, label and description above would be public; the private mapping stays encrypted.";
        CatalogMessageText.Text = _session.CatalogMessage ?? _session.CatalogWriteMessage ?? (unlocked
            ? "No catalog act has been sent in this session. Preview first, then publish exactly that metadata — nothing is autofilled from a private title, path or citation."
            : "No private catalog act is offered while the service does not report the vault unlocked.");

        var selectedCatalog = CatalogList.SelectedItem as CatalogRow;
        CatalogPreviewButton.IsEnabled = unlocked && !inFlight;
        CatalogPublishButton.IsEnabled = unlocked && !inFlight;
        CatalogRefreshButton.IsEnabled = unlocked && !inFlight;
        CatalogList.IsEnabled = unlocked && !inFlight;
        CatalogWithdrawButton.IsEnabled = unlocked && !inFlight && selectedCatalog is not null;
        CatalogIdInput.IsEnabled = unlocked && !inFlight;
        CatalogResourceInput.IsEnabled = unlocked && !inFlight;
        CatalogLabelInput.IsEnabled = unlocked && !inFlight;
        CatalogDescriptionInput.IsEnabled = unlocked && !inFlight;

        if (!ReferenceEquals(_renderedRequests, _session.AccessRequests))
        {
            _renderedRequests = _session.AccessRequests;
            var previousId = (RequestsList.SelectedItem as AccessRequestRow)?.Request.RequestId;
            var rows = _session.AccessRequests.Select(request => new AccessRequestRow(request)).ToArray();
            RequestsList.ItemsSource = rows;
            RequestsList.SelectedItem = previousId is null
                ? null
                : rows.FirstOrDefault(row => string.Equals(row.Request.RequestId, previousId, StringComparison.Ordinal));
        }

        RequestsMessageText.Text = _session.AccessRequestsMessage ?? (unlocked
            ? "No access request has been read in this session. Read them to see each agent's purpose and catalogs."
            : "No private request act is offered while the service does not report the vault unlocked.");

        var bound = _session.OpenRequestPreview;
        RequestPreviewSummaryText.Text = bound is null
            ? "No bound request preview is open. Preview a pending request to bind its exact snapshot before approving anything."
            : "bound preview=" + bound.PreviewId + "  request=" + bound.RequestId + "  status=pending" +
                "\ncatalogs=" + (bound.CatalogIds.Length == 0 ? "(none)" : string.Join(",", bound.CatalogIds)) +
                "\nresources=" + (bound.Resources.Length == 0 ? "(none)" : string.Join("; ", bound.Resources.Select(resource =>
                    resource.ResourceId + " zone=" + resource.ZoneId + " revision=" + resource.Revision.ToString(CultureInfo.InvariantCulture)))) +
                "\noperations=" + (bound.Operations.Length == 0 ? "(none)" : string.Join(",", bound.Operations)) +
                "\npurpose=" + bound.Purpose +
                "\nagent=" + (bound.AgentId ?? "(none)") + "  context=" + (bound.AgentContext ?? "(none)") +
                "\ndisclosure provider=" + (bound.Provider ?? "(none)") + "  model=" + (bound.Model ?? "(none)") +
                "  context=" + (bound.DisclosureContext ?? "(none)") +
                "\npolicy generation=" + bound.PolicyGeneration.ToString(CultureInfo.InvariantCulture) +
                "  unlock epoch=" + bound.UnlockEpoch.ToString(CultureInfo.InvariantCulture) +
                "\nAn approval below mints only a narrowed subset of these IDs and operations — never more.";
        RequestPreviewBodiesText.Text = bound is null
            ? string.Empty
            : bound.Bodies.Length == 0
                ? "(the service returned no preview body for this bound snapshot)"
                : string.Join("\n\n", bound.Bodies.Select(body =>
                    "--- preview body resource=" + body.ResourceId + " ---\n" + body.Text +
                    "\n--- end preview body resource=" + body.ResourceId + " ---"));
        RequestDecisionText.Text = _session.AccessDecisionMessage ?? (unlocked
            ? "No request decision has been sent in this session. Narrowing below the bound preview is the only direction offered; the service refuses any growth."
            : "No private request act is offered while the service does not report the vault unlocked.");
        RequestHandoffText.Text = _session.LastHandoffPath is null
            ? string.Empty
            : "grant=" + (_session.LastGrantId ?? "(unknown)") + "  local handoff=" + _session.LastHandoffPath +
                "  (DPAPI-protected; the token itself is never shown)";

        var selectedRequest = RequestsList.SelectedItem as AccessRequestRow;
        RequestsRefreshButton.IsEnabled = unlocked && !inFlight;
        RequestPreviewButton.IsEnabled = unlocked && !inFlight && selectedRequest is not null;
        RequestsList.IsEnabled = unlocked && !inFlight;
        RequestApproveButton.IsEnabled = unlocked && !inFlight && bound is not null;
        RequestRejectButton.IsEnabled = unlocked && !inFlight && selectedRequest is not null;
        ApproveResourceIdsInput.IsEnabled = unlocked && !inFlight && bound is not null;
        ApproveOperationsInput.IsEnabled = unlocked && !inFlight && bound is not null;
        ApproveDestinationIdsInput.IsEnabled = unlocked && !inFlight && bound is not null;
        ApproveExpiryInput.IsEnabled = unlocked && !inFlight && bound is not null;
        ApproveProviderInput.IsEnabled = unlocked && !inFlight && bound is not null;
        ApproveModelInput.IsEnabled = unlocked && !inFlight && bound is not null;
        ApproveMaxCostInput.IsEnabled = unlocked && !inFlight && bound is not null;
        ApproveHandoffInput.IsEnabled = unlocked && !inFlight && bound is not null;

        if (!ReferenceEquals(_renderedSessions, _session.AgentSessions))
        {
            _renderedSessions = _session.AgentSessions;
            var previousId = (SessionsList.SelectedItem as AgentSessionRow)?.Session.SessionId;
            var rows = _session.AgentSessions.Select(session => new AgentSessionRow(session)).ToArray();
            SessionsList.ItemsSource = rows;
            SessionsList.SelectedItem = previousId is null
                ? null
                : rows.FirstOrDefault(row => string.Equals(row.Session.SessionId, previousId, StringComparison.Ordinal));
        }

        SessionsMessageText.Text = _session.SessionsMessage ?? (unlocked
            ? "No live session has been read in this session. Read them to see each session's effective frozen scope and expiry."
            : "No private session act is offered while the service does not report the vault unlocked.");

        var openSession = SessionsList.SelectedItem as AgentSessionRow;
        SessionDetailText.Text = openSession is null
            ? "No session is selected. Choose a row to see its frozen resources, operations, expiry and egress."
            : "session=" + openSession.Session.SessionId + "  grant=" + openSession.Session.GrantId +
                "  generation=" + openSession.Session.PolicyGeneration.ToString(CultureInfo.InvariantCulture) +
                "  epoch=" + openSession.Session.UnlockEpoch.ToString(CultureInfo.InvariantCulture) +
                "\ncreated=" + openSession.Session.CreatedAtUtc.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture) +
                "  expires=" + openSession.Session.ExpiresAtUtc.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture) +
                "\nresources=" + (openSession.Session.Resources.Length == 0 ? "(none)" : string.Join("; ", openSession.Session.Resources.Select(resource =>
                    resource.ResourceId + " zone=" + resource.ZoneId + " revision=" + resource.Revision.ToString(CultureInfo.InvariantCulture)))) +
                "\noperations=" + (openSession.Session.Operations.Length == 0 ? "(none)" : string.Join(",", openSession.Session.Operations)) +
                "\ndestinations=" + (openSession.Session.Destinations.Length == 0 ? "(none)" : string.Join("; ", openSession.Session.Destinations.Select(resource =>
                    resource.ResourceId + " zone=" + resource.ZoneId + " revision=" + resource.Revision.ToString(CultureInfo.InvariantCulture)))) +
                "\negress=" + (openSession.Session.Egress is null
                    ? "(none)"
                    : openSession.Session.Egress.Provider + "/" + openSession.Session.Egress.Model +
                        " maxCostUsd=" + openSession.Session.Egress.MaximumCostUsd.ToString(CultureInfo.InvariantCulture)) +
                "\nAdded members or revisions never enter this frozen scope afterwards; expiry, revoke, lock and restart end it.";

        SessionsRefreshButton.IsEnabled = unlocked && !inFlight;
        SessionRevokeButton.IsEnabled = unlocked && !inFlight && openSession is not null;
        SessionsList.IsEnabled = unlocked && !inFlight;
    }
}
