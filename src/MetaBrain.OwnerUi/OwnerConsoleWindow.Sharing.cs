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
        ApproveResourceList.ItemsSource = null;
        ApproveOperationList.ItemsSource = null;
        ApproveDestinationIdsInput.Text = string.Empty;
        ApproveExpiryInput.Text = string.Empty;
        ApproveExpiryPresetInput.SelectedItem = ApproveExpiryCustomItem;
        ApproveProviderInput.Text = string.Empty;
        ApproveModelInput.Text = string.Empty;
        ApproveMaxCostInput.Text = string.Empty;
        ApproveHandoffInput.Text = string.Empty;
        _renderedApprovalPreview = null;
    }

    private async Task RefreshSharingAsync()
    {
        await _session.RefreshCatalogAsync().ConfigureAwait(true);
        await _session.RefreshAccessRequestsAsync().ConfigureAwait(true);
        await _session.RefreshSessionsAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Fills the mapped resource from the open library item: the exact ID the
    /// service served, at the revision it served. No ID to memorize.
    /// </summary>
    private void OnCatalogUseLibraryClicked(object sender, RoutedEventArgs e)
    {
        if (_session.LibraryItem is { } item)
        {
            CatalogResourceInput.Text = item.Id;
            Render();
        }
        else
        {
            _session.ReportSharingFormProblem(
                "No library item is open: open one in Library first, then pick it here. Nothing was filled in.");
        }
    }

    /// <summary>
    /// Fills the mapped resource from the saved capture of this session.
    /// </summary>
    private void OnCatalogUseCaptureClicked(object sender, RoutedEventArgs e)
    {
        if (_session.CaptureSource is { } saved)
        {
            CatalogResourceInput.Text = saved.Id;
            Render();
        }
        else
        {
            _session.ReportSharingFormProblem(
                "No capture from this session is saved yet: save one in Inbox first, then pick it here. Nothing was filled in.");
        }
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

    /// <summary>
    /// Friendly names for the service's scope operations, so the owner picks by
    /// meaning instead of typing an API token. The wire value travels; only the
    /// label is friendly.
    /// </summary>
    private static string DescribeOperation(string operation) => operation switch
    {
        "resource.read" => "Read item text (resource.read)",
        "source.read" => "Read source record (source.read)",
        "source.snapshot.read" => "Read preserved snapshot bytes (source.snapshot.read)",
        "memory.read" => "Read memory item detail (memory.read)",
        "memory.search" => "Search memory (memory.search)",
        "memory.context" => "Build brief/context (memory.context)",
        "memory.feedback" => "Leave feedback (memory.feedback)",
        "feedback.read" => "Read feedback records (feedback.read)",
        "relation.read" => "Read relations (relation.read)",
        "relation.list" => "List relations (relation.list)",
        "proposal.create" => "File proposals into an inbox (proposal.create)",
        "link.create" => "Create links (link.create)",
        "link.supersede" => "Replace links (link.supersede)",
        "link.remove" => "Remove links (link.remove)",
        "link.restore" => "Restore removed links (link.restore)",
        "provider.egress" => "Send to a model (provider.egress)",
        _ => operation
    };

    private const string ApproveExpiryCustomItem = "Custom…";

    private static readonly (string Label, TimeSpan Span)[] ApproveExpiryPresets =
    {
        ("30 minutes", TimeSpan.FromMinutes(30)),
        ("8 hours", TimeSpan.FromHours(8)),
        ("1 day", TimeSpan.FromDays(1)),
        ("7 days", TimeSpan.FromDays(7)),
        ("30 days", TimeSpan.FromDays(30)),
    };

    private void InitializeSharingControls()
    {
        ApproveExpiryPresetInput.Items.Add(ApproveExpiryCustomItem);
        foreach (var preset in ApproveExpiryPresets)
        {
            ApproveExpiryPresetInput.Items.Add(preset.Label);
        }

        ApproveExpiryPresetInput.SelectedItem = ApproveExpiryCustomItem;
    }

    private void OnApproveSelectionChanged(object sender, SelectionChangedEventArgs e) => Render();

    private void OnApproveExpiryPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ApproveExpiryPresetInput.SelectedItem is not string label)
        {
            return;
        }

        foreach (var preset in ApproveExpiryPresets)
        {
            if (string.Equals(preset.Label, label, StringComparison.Ordinal))
            {
                var instant = DateTimeOffset.UtcNow + preset.Span;
                ApproveExpiryInput.Text = instant.ToString("yyyy-MM-ddTHH:mm:ss\\Z", CultureInfo.InvariantCulture);
                break;
            }
        }

        Render();
    }

    private void OnApproveHandoffBrowseClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Choose the local handoff file for the sealed token",
            Filter = "Token handoff (*.handoff)|*.handoff|All files (*.*)|*.*",
            DefaultExt = ".handoff",
            OverwritePrompt = false,
            CheckPathExists = true,
        };
        if (dialog.ShowDialog() == true)
        {
            ApproveHandoffInput.Text = dialog.FileName;
        }

        Render();
    }

    private string[]? TickedResources()
    {
        var preview = _session.OpenRequestPreview;
        if (preview is null || ApproveResourceList.ItemsSource is null)
        {
            return null;
        }

        var ticked = ApproveResourceList.SelectedItems.OfType<ApprovalResourceRow>()
            .Select(row => row.Resource.ResourceId).ToArray();
        // All ticked (or an untouched list) approves the whole bound snapshot:
        // the service treats an omitted list as the full preview.
        return ticked.Length == preview.Resources.Length ? null : ticked;
    }

    private string[]? TickedOperations()
    {
        var preview = _session.OpenRequestPreview;
        if (preview is null || ApproveOperationList.ItemsSource is null)
        {
            return null;
        }

        var ticked = ApproveOperationList.SelectedItems.OfType<ApprovalOperationRow>()
            .Select(row => row.Operation).ToArray();
        return ticked.Length == 0 ? null : ticked;
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

        var destinations = SplitIdentifiers(ApproveDestinationIdsInput.Text);
        if (ApproveDestinationIdsInput.Text.Trim().Length > 0 && destinations is null)
        {
            _session.ReportSharingFormProblem(
                "Destinations: name each destination ID once, separated by commas, or leave the field empty when this approval needs no write destination. Nothing was minted.");
            return;
        }

        var expiryText = ApproveExpiryInput.Text.Trim();
        if (!DateTimeOffset.TryParse(expiryText, CultureInfo.InvariantCulture, DateTimeStyles.None, out var expiresAtUtc) ||
            expiresAtUtc.Offset != TimeSpan.Zero)
        {
            _session.ReportSharingFormProblem(
                "Expiry: type a UTC instant with an explicit Z suffix (for example 2026-10-21T08:00:00Z), or pick a duration above. The service requires a future UTC instant. Nothing was minted.");
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
                    "Cost limit: the value is not a number. Type a value in US dollars (at most $0.25), or clear it. Nothing was minted.");
                return;
            }

            maximumCostUsd = parsedCost;
        }

        var resourceIds = TickedResources();
        var operations = TickedOperations();
        var problem = OwnerConsoleSession.ValidateApprovalDraft(
            preview, resourceIds, operations, destinations, expiresAtUtc,
            provider.Length == 0 ? null : provider,
            model.Length == 0 ? null : model,
            maximumCostUsd,
            ApproveHandoffInput.Text);
        if (problem is not null)
        {
            _session.ReportSharingFormProblem(problem.Field + ": " + problem.Message);
            return;
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
        RenderOutcome(CatalogMessageText, _session.CatalogMessage ?? _session.CatalogWriteMessage ?? (unlocked
            ? "No catalog act has been sent in this session. Preview first, then publish exactly that metadata — nothing is autofilled from a private title, path or citation."
            : "No private catalog act is offered while the service does not report the vault unlocked."));

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

        RenderOutcome(RequestsMessageText, _session.AccessRequestsMessage ?? (unlocked
            ? "No access request has been read in this session. Read them to see each agent's purpose and catalogs."
            : "No private request act is offered while the service does not report the vault unlocked."));

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
        RenderOutcome(RequestDecisionText, _session.AccessDecisionMessage ?? (unlocked
            ? "No request decision has been sent in this session. Narrowing below the bound preview is the only direction offered; the service refuses any growth."
            : "No private request act is offered while the service does not report the vault unlocked."));
        RequestHandoffText.Text = _session.LastHandoffPath is null
            ? string.Empty
            : "grant=" + (_session.LastGrantId ?? "(unknown)") + "  local handoff=" + _session.LastHandoffPath +
                "  (DPAPI-protected; the token itself is never shown)";

        var selectedRequest = RequestsList.SelectedItem as AccessRequestRow;
        RequestsRefreshButton.IsEnabled = unlocked && !inFlight;
        RequestPreviewButton.IsEnabled = unlocked && !inFlight && selectedRequest is not null;
        RequestsList.IsEnabled = unlocked && !inFlight;

        // The narrowing pickers follow the bound preview: resources and operations
        // are the exact snapshot an approval would draw from. A new preview
        // rebuilds both lists with every box ticked (the whole snapshot); the
        // owner's unticks are the narrowing. Internal IDs stay visible on each
        // row as secondary copyable detail, never as something to memorize.
        if (!ReferenceEquals(_renderedApprovalPreview, bound))
        {
            _renderedApprovalPreview = bound;
            if (bound is null)
            {
                ApproveResourceList.ItemsSource = null;
                ApproveOperationList.ItemsSource = null;
            }
            else
            {
                var resourceRows = bound.Resources.Select(resource => new ApprovalResourceRow(resource)).ToArray();
                ApproveResourceList.ItemsSource = resourceRows;
                ApproveResourceList.SelectedItems.Clear();
                foreach (var row in resourceRows)
                {
                    ApproveResourceList.SelectedItems.Add(row);
                }

                var operationRows = bound.Operations.Select(operation => new ApprovalOperationRow(operation, DescribeOperation(operation))).ToArray();
                ApproveOperationList.ItemsSource = operationRows;
                ApproveOperationList.SelectedItems.Clear();
                foreach (var row in operationRows)
                {
                    ApproveOperationList.SelectedItems.Add(row);
                }

                if (string.IsNullOrWhiteSpace(ApproveExpiryInput.Text))
                {
                    ApproveExpiryPresetInput.SelectedItem = "1 day";
                    OnApproveExpiryPresetChanged(ApproveExpiryPresetInput, new SelectionChangedEventArgs(System.Windows.Controls.Primitives.Selector.SelectionChangedEvent, Array.Empty<object>(), Array.Empty<object>()));
                }

                if (!string.IsNullOrWhiteSpace(bound.Provider) && string.IsNullOrWhiteSpace(ApproveProviderInput.Text))
                {
                    ApproveProviderInput.Text = bound.Provider;
                }

                if (!string.IsNullOrWhiteSpace(bound.Model) && string.IsNullOrWhiteSpace(ApproveModelInput.Text))
                {
                    ApproveModelInput.Text = bound.Model;
                }
            }
        }

        var tickedResourceCount = ApproveResourceList.SelectedItems.Count;
        var tickedOperationCount = ApproveOperationList.SelectedItems.Count;
        var previewResourceCount = bound?.Resources.Length ?? 0;
        ApproveOperationsHelpText.Text = bound is null
            ? "Preview a pending request to see the operations it offers."
            : "Ticked " + tickedOperationCount.ToString(CultureInfo.InvariantCulture) + " of " + bound.Operations.Length.ToString(CultureInfo.InvariantCulture) +
                " operation(s). Unticked operations stay out of the grant; unticking everything approves read-only.";
        ApproveExpiryHelpText.Text = "Now: " + DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture) +
            " (your local time " + DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + "). " +
            "The expiry must be a future UTC instant with a Z suffix. After it passes, the session authorizes nothing." +
            (bound is null ? string.Empty : " Ticked " + tickedResourceCount.ToString(CultureInfo.InvariantCulture) + " of " + previewResourceCount.ToString(CultureInfo.InvariantCulture) + " resource(s).");
        RequestApproveButton.IsEnabled = unlocked && !inFlight && bound is not null;
        RequestRejectButton.IsEnabled = unlocked && !inFlight && selectedRequest is not null;
        ApproveResourceList.IsEnabled = unlocked && !inFlight && bound is not null;
        ApproveOperationList.IsEnabled = unlocked && !inFlight && bound is not null;
        ApproveDestinationIdsInput.IsEnabled = unlocked && !inFlight && bound is not null;
        ApproveExpiryPresetInput.IsEnabled = unlocked && !inFlight && bound is not null;
        ApproveExpiryInput.IsEnabled = unlocked && !inFlight && bound is not null;
        ApproveProviderInput.IsEnabled = unlocked && !inFlight && bound is not null;
        ApproveModelInput.IsEnabled = unlocked && !inFlight && bound is not null;
        ApproveMaxCostInput.IsEnabled = unlocked && !inFlight && bound is not null;
        ApproveHandoffInput.IsEnabled = unlocked && !inFlight && bound is not null;
        ApproveHandoffBrowseButton.IsEnabled = unlocked && !inFlight && bound is not null;

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

        RenderOutcome(SessionsMessageText, _session.SessionsMessage ?? (unlocked
            ? "No live session has been read in this session. Read them to see each session's effective frozen scope and expiry."
            : "No private session act is offered while the service does not report the vault unlocked."));

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
