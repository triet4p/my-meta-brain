using System.Security.Cryptography;

namespace MetaBrain.Core.Security;

public sealed record OwnerAccessRequest(
    string RequestId,
    string[] CatalogIds,
    string Purpose,
    string? AgentId,
    string? AgentContext,
    string? Provider,
    string? Model,
    string? DisclosureContext,
    string[] RequestedOperations,
    ScopeResourceRevision[] ResolvedResources,
    DateTimeOffset CreatedAtUtc,
    string Status,
    string? ApprovedGrantId = null,
    DateTimeOffset? DecidedAtUtc = null);

public sealed record OwnerAccessRequestPreview(
    string PreviewId,
    string RequestId,
    string[] CatalogIds,
    ScopeResourceRevision[] Resources,
    string[] Operations,
    string Purpose,
    string? AgentId,
    string? AgentContext,
    string? Provider,
    string? Model,
    string? DisclosureContext,
    DateTimeOffset CreatedAtUtc,
    long PolicyGeneration,
    long UnlockEpoch);

public sealed class OwnerAccessRequestException : ArgumentException
{
    public string Code { get; }

    public OwnerAccessRequestException(string code, string message)
        : base(message)
    {
        Code = code;
    }
}

public sealed partial class OwnerScopeGrantAuthority
{
    public const int MaximumAccessRequests = 1000;
    public const int MaximumCatalogIdsPerRequest = 32;
    public const int MaximumPurposeLength = 512;
    public const int MaximumAgentTextLength = 128;
    public const int MaximumContextLength = 512;

    public const string RequestStatusPending = "pending";
    public const string RequestStatusApproved = "approved";
    public const string RequestStatusRejected = "rejected";

    private readonly Dictionary<string, PendingRequestPreview> _requestPreviews = new(StringComparer.Ordinal);

    private sealed record PendingRequestPreview(OwnerAccessRequestPreview Value, DateTimeOffset ExpiresAtUtc);

    public OwnerAccessRequest SubmitAccessRequest(
        AuthenticatedContext actor,
        string[]? catalogIds,
        string? purpose,
        string? agentId,
        string? agentContext,
        string? provider,
        string? model,
        string? disclosureContext,
        IReadOnlyList<string>? requestedOperations,
        OwnerCatalogState catalogState,
        IReadOnlyList<ScopeResourceRevision> liveResources,
        IScopeGrantPersistence persistence)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(catalogState);
        ArgumentNullException.ThrowIfNull(liveResources);
        ArgumentNullException.ThrowIfNull(persistence);
        EnsureAgent(actor);

        var normalizedCatalogIds = NormalizeRequestCatalogIds(catalogIds);
        var normalizedPurpose = NormalizeRequestText(purpose, 1, MaximumPurposeLength, nameof(purpose));
        var normalizedAgentId = NormalizeOptionalRequestText(agentId, 1, MaximumAgentTextLength, nameof(agentId));
        var normalizedAgentContext = NormalizeOptionalRequestText(agentContext, 0, MaximumContextLength, nameof(agentContext));
        var normalizedDisclosure = NormalizeOptionalRequestText(disclosureContext, 0, MaximumContextLength, nameof(disclosureContext));
        var normalizedProvider = NormalizeOptionalIdentifier(provider, nameof(provider));
        var normalizedModel = NormalizeOptionalIdentifier(model, nameof(model));
        var operationSnapshot = NormalizeOperations(requestedOperations);

        var resolved = ResolveRequestCatalogs(normalizedCatalogIds, catalogState, liveResources);

        lock (_gate)
        {
            ExpireRequestPreviewsLocked(DateTimeOffset.UtcNow);
            return persistence.UpdateScopeGrantState(state =>
            {
                var current = ValidateAndMigrateState(state);
                if (current.Requests.Length >= MaximumAccessRequests)
                {
                    throw new OwnerAccessRequestException(
                        "request_unavailable", "The owner access request capacity is exhausted.");
                }

                var now = DateTimeOffset.UtcNow;
                string requestId;
                do
                {
                    requestId = Guid.NewGuid().ToString("N");
                }
                while (Array.Exists(current.Requests, existing =>
                    string.Equals(existing.RequestId, requestId, StringComparison.Ordinal)));

                var request = new OwnerAccessRequest(
                    requestId,
                    (string[])normalizedCatalogIds.Clone(),
                    normalizedPurpose,
                    normalizedAgentId,
                    normalizedAgentContext,
                    normalizedProvider,
                    normalizedModel,
                    normalizedDisclosure,
                    (string[])operationSnapshot.Clone(),
                    CloneResources(resolved),
                    now,
                    RequestStatusPending);
                var requests = current.Requests.Append(request).OrderBy(item => item.RequestId, StringComparer.Ordinal).ToArray();
                var nextState = current with { Requests = requests };
                return (nextState, CloneRequest(request));
            });
        }
    }

    public IReadOnlyList<OwnerAccessRequest> ListAccessRequests(
        AuthenticatedContext actor,
        IScopeGrantPersistence persistence)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(persistence);
        EnsureOwner(actor);

        lock (_gate)
        {
            var state = ValidateAndMigrateState(persistence.LoadScopeGrantState());
            return Array.AsReadOnly(state.Requests.Select(CloneRequest).ToArray());
        }
    }

    public string GetAccessRequestStatus(
        AuthenticatedContext actor,
        string? requestId,
        IScopeGrantPersistence persistence)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(persistence);
        EnsureAgent(actor);
        if (!Guid.TryParseExact(requestId, "N", out _))
        {
            throw new OwnerAccessRequestException("request_unavailable", "The access request is unavailable.");
        }

        lock (_gate)
        {
            var state = ValidateAndMigrateState(persistence.LoadScopeGrantState());
            var existing = Array.Find(state.Requests, item =>
                string.Equals(item.RequestId, requestId, StringComparison.Ordinal));
            if (existing is null)
            {
                throw new OwnerAccessRequestException("request_unavailable", "The access request is unavailable.");
            }

            return existing.Status;
        }
    }

    public OwnerAccessRequestPreview PreviewAccessRequest(
        AuthenticatedContext actor,
        string? requestId,
        OwnerCatalogState catalogState,
        IReadOnlyList<ScopeResourceRevision> liveResources,
        IScopeGrantPersistence persistence)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(catalogState);
        ArgumentNullException.ThrowIfNull(liveResources);
        ArgumentNullException.ThrowIfNull(persistence);
        EnsureOwner(actor);
        if (!Guid.TryParseExact(requestId, "N", out _))
        {
            throw new OwnerAccessRequestException("request_unknown", "The access request does not exist.");
        }

        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            ExpireRequestPreviewsLocked(now);
            var state = ValidateAndMigrateState(persistence.LoadScopeGrantState());
            var existing = Array.Find(state.Requests, item =>
                string.Equals(item.RequestId, requestId, StringComparison.Ordinal))
                ?? throw new OwnerAccessRequestException("request_unknown", "The access request does not exist.");
            if (!string.Equals(existing.Status, RequestStatusPending, StringComparison.Ordinal))
            {
                throw new OwnerAccessRequestException("request_not_pending", "The access request is already decided.");
            }

            var resolved = ResolveRequestCatalogs(existing.CatalogIds, catalogState, liveResources);
            if (_requestPreviews.Count >= MaximumPendingPreviews)
            {
                throw new OwnerAccessRequestException(
                    "request_capacity_exhausted", "The owner request preview capacity is exhausted.");
            }

            var preview = new OwnerAccessRequestPreview(
                Guid.NewGuid().ToString("N"),
                existing.RequestId,
                (string[])existing.CatalogIds.Clone(),
                CloneResources(resolved),
                (string[])existing.RequestedOperations.Clone(),
                existing.Purpose,
                existing.AgentId,
                existing.AgentContext,
                existing.Provider,
                existing.Model,
                existing.DisclosureContext,
                now,
                state.PolicyGeneration,
                state.UnlockEpoch);
            _requestPreviews.Add(preview.PreviewId, new PendingRequestPreview(CloneRequestPreview(preview), now + PreviewLifetime));
            return CloneRequestPreview(preview);
        }
    }

    public OwnerScopeGrantIssue ApproveAccessRequest(
        AuthenticatedContext actor,
        string? previewId,
        string[]? narrowedResourceIds,
        IReadOnlyList<string>? narrowedOperations,
        string[]? destinationResourceIds,
        DateTimeOffset expiresAtUtc,
        string? provider,
        string? model,
        decimal? maximumCostUsd,
        OwnerCatalogState catalogState,
        IReadOnlyList<ScopeResourceRevision> liveResources,
        IScopeGrantPersistence persistence)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(catalogState);
        ArgumentNullException.ThrowIfNull(liveResources);
        ArgumentNullException.ThrowIfNull(persistence);
        EnsureOwner(actor);
        if (!Guid.TryParseExact(previewId, "N", out _))
        {
            throw new OwnerAccessRequestException("request_unknown", "The owner request preview is unavailable.");
        }

        ValidateExpiry(expiresAtUtc);

        OwnerAccessRequestPreview preview;
        lock (_gate)
        {
            if (!_requestPreviews.TryGetValue(previewId!, out var pending) ||
                pending.ExpiresAtUtc <= DateTimeOffset.UtcNow)
            {
                _requestPreviews.Remove(previewId!);
                throw new OwnerAccessRequestException("request_stale", "The owner request preview is stale; take a new preview.");
            }

            preview = CloneRequestPreview(pending.Value);
        }

        // Every catalog ID named in the preview must still resolve live. A withdrawn
        // catalog, an unregistered resource, or a republished mapping that changed the
        // resource/revision snapshot invalidates the shown bytes; the preview is removed
        // so it cannot be revived by republishing the same catalog ID later.
        ScopeResourceRevision[] currentLive;
        try
        {
            currentLive = ResolveRequestCatalogs(preview.CatalogIds, catalogState, liveResources, allowMissingAsUnavailable: false);
        }
        catch (OwnerAccessRequestException ex) when (string.Equals(ex.Code, "request_stale", StringComparison.Ordinal))
        {
            lock (_gate)
            {
                _requestPreviews.Remove(previewId!);
            }

            throw;
        }

        if (!SnapshotsEqual(currentLive, preview.Resources))
        {
            lock (_gate)
            {
                _requestPreviews.Remove(previewId!);
            }

            throw new OwnerAccessRequestException(
                "request_stale", "The catalog mapping or resource revision changed; take a new preview.");
        }

        var finalResources = SelectNarrowedResources(preview.Resources, narrowedResourceIds);
        var finalOperations = narrowedOperations is null
            ? new[] { "resource.read" }
            : NormalizeOperations(narrowedOperations);
        foreach (var operation in finalOperations)
        {
            if (!preview.Operations.Contains(operation, StringComparer.Ordinal))
            {
                throw new OwnerAccessRequestException(
                    "request_invalid", "The approved operations enlarge the previewed request scope.");
            }
        }

        var finalDestinations = ResolvePrivateDestinations(liveResources, destinationResourceIds);
        ValidateOperationScope(finalOperations, finalDestinations, provider is not null || model is not null || maximumCostUsd is not null
            ? new ScopeGrantEgress(
                provider ?? throw new OwnerAccessRequestException("request_invalid", "Provider egress fields must be supplied together."),
                model ?? throw new OwnerAccessRequestException("request_invalid", "Provider egress fields must be supplied together."),
                maximumCostUsd ?? throw new OwnerAccessRequestException("request_invalid", "Provider egress fields must be supplied together."))
            : null);

        ScopeGrantEgress? egress = null;
        if (finalOperations.Contains("provider.egress", StringComparer.Ordinal))
        {
            if (provider is null || model is null || maximumCostUsd is null)
            {
                throw new OwnerAccessRequestException("request_invalid", "Provider egress requires provider, model, and cost limit.");
            }

            if (!IsValidIdentifier(provider) || !IsValidIdentifier(model) ||
                maximumCostUsd <= 0 || maximumCostUsd > MaximumEgressJobCostUsd)
            {
                throw new OwnerAccessRequestException("request_invalid", "Provider egress fields are invalid or exceed the per-job maximum.");
            }

            if ((preview.Provider is not null && !string.Equals(preview.Provider, provider, StringComparison.Ordinal)) ||
                (preview.Model is not null && !string.Equals(preview.Model, model, StringComparison.Ordinal)))
            {
                throw new OwnerAccessRequestException(
                    "request_invalid", "The approved provider egress does not match the declared request disclosure.");
            }

            egress = new ScopeGrantEgress(provider, model, maximumCostUsd.Value);
        }
        else if (provider is not null || model is not null || maximumCostUsd is not null)
        {
            throw new OwnerAccessRequestException("request_invalid", "Egress fields require the provider.egress operation.");
        }

        var tokenBytes = RandomNumberGenerator.GetBytes(32);
        var verifierBytes = SHA256.HashData(tokenBytes);
        try
        {
            lock (_gate)
            {
                var issue = persistence.UpdateScopeGrantState(state =>
                {
                    var current = ValidateAndMigrateState(state);
                    if (current.PolicyGeneration != preview.PolicyGeneration ||
                        current.UnlockEpoch != preview.UnlockEpoch)
                    {
                        throw new OwnerAccessRequestException(
                            "request_stale", "The scope policy or lock epoch changed; take a new preview.");
                    }

                    var index = Array.FindIndex(current.Requests, item =>
                        string.Equals(item.RequestId, preview.RequestId, StringComparison.Ordinal));
                    if (index < 0)
                    {
                        throw new OwnerAccessRequestException("request_unknown", "The access request does not exist.");
                    }

                    if (!string.Equals(current.Requests[index].Status, RequestStatusPending, StringComparison.Ordinal))
                    {
                        throw new OwnerAccessRequestException("request_not_pending", "The access request is already decided.");
                    }

                    if (current.Grants.Length >= 10_000)
                    {
                        throw new OwnerAccessRequestException(
                            "request_unavailable", "The owner scope grant state cannot accept another grant.");
                    }

                    var grant = new PersistedScopeGrant(
                        Guid.NewGuid().ToString("N"),
                        EncodeBase64Url(verifierBytes),
                        CloneResources(finalResources),
                        (string[])finalOperations.Clone(),
                        CloneResources(finalDestinations),
                        DateTimeOffset.UtcNow,
                        expiresAtUtc,
                        egress,
                        current.PolicyGeneration,
                        current.UnlockEpoch);
                    var decidedAt = DateTimeOffset.UtcNow;
                    var approved = current.Requests[index] with
                    {
                        Status = RequestStatusApproved,
                        ApprovedGrantId = grant.GrantId,
                        DecidedAtUtc = decidedAt
                    };
                    var requests = (OwnerAccessRequest[])current.Requests.Clone();
                    requests[index] = approved;
                    Array.Sort(requests, (left, right) => string.CompareOrdinal(left.RequestId, right.RequestId));
                    var nextState = current with
                    {
                        Grants = current.Grants.Append(grant).ToArray(),
                        Requests = requests
                    };
                    var issued = new OwnerScopeGrantIssue("mb1_" + EncodeBase64Url(tokenBytes), CloneGrant(grant));
                    return (nextState, issued);
                });

                _requestPreviews.Remove(previewId!);
                RemoveRequestPreviewsLocked(preview.RequestId, except: null);
                return issue;
            }
        }
        catch (OwnerAccessRequestException ex) when (string.Equals(ex.Code, "request_stale", StringComparison.Ordinal) ||
            string.Equals(ex.Code, "request_not_pending", StringComparison.Ordinal) ||
            string.Equals(ex.Code, "request_unknown", StringComparison.Ordinal))
        {
            lock (_gate)
            {
                _requestPreviews.Remove(previewId!);
            }

            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(tokenBytes);
            CryptographicOperations.ZeroMemory(verifierBytes);
        }
    }

    public OwnerAccessRequest RejectAccessRequest(
        AuthenticatedContext actor,
        string? requestId,
        IScopeGrantPersistence persistence)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(persistence);
        EnsureOwner(actor);
        if (!Guid.TryParseExact(requestId, "N", out _))
        {
            throw new OwnerAccessRequestException("request_unknown", "The access request does not exist.");
        }

        lock (_gate)
        {
            var rejected = persistence.UpdateScopeGrantState(state =>
            {
                var current = ValidateAndMigrateState(state);
                var index = Array.FindIndex(current.Requests, item =>
                    string.Equals(item.RequestId, requestId, StringComparison.Ordinal));
                if (index < 0)
                {
                    throw new OwnerAccessRequestException("request_unknown", "The access request does not exist.");
                }

                if (!string.Equals(current.Requests[index].Status, RequestStatusPending, StringComparison.Ordinal))
                {
                    throw new OwnerAccessRequestException("request_not_pending", "The access request is already decided.");
                }

                var decided = current.Requests[index] with
                {
                    Status = RequestStatusRejected,
                    DecidedAtUtc = DateTimeOffset.UtcNow
                };
                var requests = (OwnerAccessRequest[])current.Requests.Clone();
                requests[index] = decided;
                var nextState = current with { Requests = requests };
                return (nextState, CloneRequest(decided));
            });

            RemoveRequestPreviewsLocked(requestId!, except: null);
            return rejected;
        }
    }

    public void ClearRequestPreviews()
    {
        lock (_gate)
        {
            _requestPreviews.Clear();
        }
    }

    private void RemoveRequestPreviewsLocked(string requestId, string? except)
    {
        foreach (var previewId in _requestPreviews
                     .Where(pair => string.Equals(pair.Value.Value.RequestId, requestId, StringComparison.Ordinal) &&
                         !string.Equals(pair.Key, except, StringComparison.Ordinal))
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _requestPreviews.Remove(previewId);
        }
    }

    private void ExpireRequestPreviewsLocked(DateTimeOffset now)
    {
        foreach (var expiredId in _requestPreviews
                     .Where(pair => pair.Value.ExpiresAtUtc <= now)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _requestPreviews.Remove(expiredId);
        }
    }

    private static ScopeResourceRevision[] ResolveRequestCatalogs(
        IReadOnlyList<string> catalogIds,
        OwnerCatalogState catalogState,
        IReadOnlyList<ScopeResourceRevision> liveResources,
        bool allowMissingAsUnavailable = true)
    {
        var resolved = new List<ScopeResourceRevision>(catalogIds.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var catalogId in catalogIds)
        {
            var mapped = OwnerCatalogAuthority.ResolveLive(catalogId, catalogState, liveResources)
                ?? throw new OwnerAccessRequestException(
                    allowMissingAsUnavailable ? "request_unavailable" : "request_stale",
                    allowMissingAsUnavailable
                        ? "The access request is unavailable."
                        : "The catalog mapping or resource revision changed; take a new preview.");
            if (seen.Add(mapped.ResourceId))
            {
                resolved.Add(new ScopeResourceRevision(mapped.ResourceId, liveResources.First(
                    resource => string.Equals(resource.ResourceId, mapped.ResourceId, StringComparison.Ordinal)).ZoneId, mapped.Revision));
            }
        }

        if (resolved.Count == 0)
        {
            throw new OwnerAccessRequestException("request_unavailable", "The access request is unavailable.");
        }

        return resolved.OrderBy(resource => resource.ResourceId, StringComparer.Ordinal).ToArray();
    }

    private static ScopeResourceRevision[] SelectNarrowedResources(
        ScopeResourceRevision[] previewed,
        string[]? narrowedResourceIds)
    {
        if (narrowedResourceIds is null)
        {
            return CloneResources(previewed);
        }

        if (narrowedResourceIds.Length == 0 || narrowedResourceIds.Length > MaximumCatalogIdsPerRequest)
        {
            throw new OwnerAccessRequestException("request_invalid", "The narrowed resource set is invalid.");
        }

        var byId = previewed.ToDictionary(resource => resource.ResourceId, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var selected = new ScopeResourceRevision[narrowedResourceIds.Length];
        for (var index = 0; index < narrowedResourceIds.Length; index++)
        {
            var resourceId = narrowedResourceIds[index];
            if (resourceId is null || !seen.Add(resourceId) || !byId.TryGetValue(resourceId, out var resource))
            {
                throw new OwnerAccessRequestException(
                    "request_invalid", "The narrowed resources enlarge the previewed request scope.");
            }

            selected[index] = resource with { };
        }

        return selected.OrderBy(resource => resource.ResourceId, StringComparer.Ordinal).ToArray();
    }

    private static ScopeResourceRevision[] ResolvePrivateDestinations(
        IReadOnlyList<ScopeResourceRevision> liveResources,
        string[]? destinationResourceIds)
    {
        if (destinationResourceIds is null || destinationResourceIds.Length == 0)
        {
            return Array.Empty<ScopeResourceRevision>();
        }

        var byId = liveResources.ToDictionary(resource => resource.ResourceId, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var selected = new ScopeResourceRevision[destinationResourceIds.Length];
        for (var index = 0; index < destinationResourceIds.Length; index++)
        {
            var resourceId = destinationResourceIds[index];
            if (resourceId is null || !seen.Add(resourceId) || !byId.TryGetValue(resourceId, out var live))
            {
                throw new OwnerAccessRequestException("request_invalid", "The approved destination scope is unknown.");
            }

            selected[index] = live with { };
        }

        return selected.OrderBy(resource => resource.ResourceId, StringComparer.Ordinal).ToArray();
    }

    private static bool SnapshotsEqual(ScopeResourceRevision[] left, ScopeResourceRevision[] right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        for (var index = 0; index < left.Length; index++)
        {
            if (!string.Equals(left[index].ResourceId, right[index].ResourceId, StringComparison.Ordinal) ||
                !string.Equals(left[index].ZoneId, right[index].ZoneId, StringComparison.Ordinal) ||
                left[index].Revision != right[index].Revision)
            {
                return false;
            }
        }

        return true;
    }

    private static string[] NormalizeRequestCatalogIds(string[]? catalogIds)
    {
        if (catalogIds is null || catalogIds.Length == 0 || catalogIds.Length > MaximumCatalogIdsPerRequest ||
            catalogIds.Any(id => id is null))
        {
            throw new OwnerAccessRequestException("request_unavailable", "The access request is unavailable.");
        }

        var normalized = catalogIds.Order(StringComparer.Ordinal).ToArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var catalogId in normalized)
        {
            if (!IsValidIdentifier(catalogId) || !seen.Add(catalogId))
            {
                throw new OwnerAccessRequestException("request_unavailable", "The access request is unavailable.");
            }
        }

        return normalized;
    }

    private static string NormalizeRequestText(string? value, int minimumLength, int maximumLength, string parameter)
    {
        if (string.IsNullOrWhiteSpace(value) || value!.Length < minimumLength || value.Length > maximumLength ||
            value.Any(char.IsControl))
        {
            throw new OwnerAccessRequestException("request_unavailable", "The access request is unavailable.");
        }

        return value;
    }

    private static string? NormalizeOptionalRequestText(string? value, int minimumLength, int maximumLength, string parameter)
    {
        if (value is null)
        {
            return null;
        }

        if (value.Length == 0 && minimumLength == 0)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(value) || value.Length < Math.Max(1, minimumLength) ||
            value.Length > maximumLength || value.Any(char.IsControl))
        {
            throw new OwnerAccessRequestException("request_unavailable", "The access request is unavailable.");
        }

        return value;
    }

    private static string? NormalizeOptionalIdentifier(string? value, string parameter)
    {
        if (value is null)
        {
            return null;
        }

        if (!IsValidIdentifier(value))
        {
            throw new OwnerAccessRequestException("request_unavailable", "The access request is unavailable.");
        }

        return value;
    }

    private static OwnerAccessRequest[] NormalizeAccessRequests(OwnerAccessRequest[]? requests)
    {
        if (requests is null)
        {
            throw new ArgumentException("The encrypted access request state is missing.", nameof(requests));
        }

        if (requests.Length > MaximumAccessRequests || requests.Any(item => item is null))
        {
            throw new ArgumentException("The encrypted access request state is invalid.", nameof(requests));
        }

        var normalized = requests.OrderBy(item => item.RequestId, StringComparer.Ordinal).ToArray();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < normalized.Length; index++)
        {
            var item = normalized[index];
            if (!Guid.TryParseExact(item.RequestId, "N", out _) || !ids.Add(item.RequestId) ||
                item.CatalogIds is null || item.CatalogIds.Length == 0 ||
                item.CatalogIds.Length > MaximumCatalogIdsPerRequest ||
                item.Purpose is null || item.Purpose.Length == 0 || item.Purpose.Length > MaximumPurposeLength ||
                item.Purpose.Any(char.IsControl) ||
                (item.AgentId is not null && (item.AgentId.Length == 0 || item.AgentId.Length > MaximumAgentTextLength ||
                    item.AgentId.Any(char.IsControl))) ||
                (item.AgentContext is not null && (item.AgentContext.Length > MaximumContextLength ||
                    item.AgentContext.Any(char.IsControl))) ||
                (item.DisclosureContext is not null && (item.DisclosureContext.Length > MaximumContextLength ||
                    item.DisclosureContext.Any(char.IsControl))) ||
                ((item.Provider is not null && !IsValidIdentifier(item.Provider)) ||
                    (item.Model is not null && !IsValidIdentifier(item.Model))) ||
                item.RequestedOperations is null || item.ResolvedResources is null ||
                item.CreatedAtUtc.Offset != TimeSpan.Zero ||
                (item.DecidedAtUtc is not null && (item.DecidedAtUtc.Value.Offset != TimeSpan.Zero ||
                    item.DecidedAtUtc.Value < item.CreatedAtUtc)) ||
                (item.Status is not (RequestStatusPending or RequestStatusApproved or RequestStatusRejected)) ||
                ((string.Equals(item.Status, RequestStatusPending, StringComparison.Ordinal) &&
                    (item.ApprovedGrantId is not null || item.DecidedAtUtc is not null)) ||
                (string.Equals(item.Status, RequestStatusApproved, StringComparison.Ordinal) &&
                    (!Guid.TryParseExact(item.ApprovedGrantId, "N", out _) || item.DecidedAtUtc is null)) ||
                (string.Equals(item.Status, RequestStatusRejected, StringComparison.Ordinal) &&
                    (item.ApprovedGrantId is not null || item.DecidedAtUtc is null))))
            {
                throw new ArgumentException("The encrypted access request record is invalid.", nameof(requests));
            }

            try
            {
                var catalogIds = NormalizeRequestCatalogIdsForState(item.CatalogIds);
                var operations = NormalizeOperations(item.RequestedOperations);
                var resources = NormalizeResources(item.ResolvedResources, allowEmpty: false);
                normalized[index] = item with
                {
                    CatalogIds = catalogIds,
                    RequestedOperations = operations,
                    ResolvedResources = resources
                };
            }
            catch (OwnerAccessRequestException ex)
            {
                throw new ArgumentException("The encrypted access request record is invalid.", nameof(requests), ex);
            }
            catch (ArgumentException ex)
            {
                throw new ArgumentException("The encrypted access request record is invalid.", nameof(requests), ex);
            }
        }

        return normalized;
    }

    private static string[] NormalizeRequestCatalogIdsForState(string[] catalogIds)
    {
        var normalized = catalogIds.Order(StringComparer.Ordinal).ToArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var catalogId in normalized)
        {
            if (!IsValidIdentifier(catalogId) || !seen.Add(catalogId))
            {
                throw new ArgumentException("The encrypted access request catalogs are invalid.");
            }
        }

        return normalized;
    }

    private static OwnerAccessRequest CloneRequest(OwnerAccessRequest request) => request with
    {
        CatalogIds = (string[])request.CatalogIds.Clone(),
        RequestedOperations = (string[])request.RequestedOperations.Clone(),
        ResolvedResources = CloneResources(request.ResolvedResources)
    };

    private static OwnerAccessRequestPreview CloneRequestPreview(OwnerAccessRequestPreview preview) => preview with
    {
        CatalogIds = (string[])preview.CatalogIds.Clone(),
        Resources = CloneResources(preview.Resources),
        Operations = (string[])preview.Operations.Clone()
    };

    private static void EnsureAgent(AuthenticatedContext actor)
    {
        if (actor.Kind != PrincipalKind.Agent)
        {
            throw new UnauthorizedAccessException("Access requests require the agent channel.");
        }
    }
}
