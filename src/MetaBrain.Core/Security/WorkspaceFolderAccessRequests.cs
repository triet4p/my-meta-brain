namespace MetaBrain.Core.Security;

/// <summary>A non-authoritative request to consider one D108 workspace folder grant.</summary>
public sealed record WorkspaceFolderAccessRequest(
    string RequestId,
    string WorkspaceFolderId,
    string Purpose,
    DateTimeOffset CreatedAtUtc,
    string Status,
    DateTimeOffset? ResolvedAtUtc = null);

public sealed class WorkspaceFolderAccessRequestException(string code)
    : ArgumentException("The workspace folder request is unavailable.")
{
    public string Code { get; } = code;
}

/// <summary>
/// Persists opaque agent requests separately from grants. Resolving a request
/// records owner review only and never issues or changes a workspace grant.
/// </summary>
public sealed class WorkspaceFolderAccessRequestAuthority
{
    public const int MaximumRequests = 1000;
    public const int MaximumPurposeLength = 512;
    public const string PendingStatus = "pending";
    public const string ResolvedStatus = "resolved";

    public WorkspaceFolderAccessRequest Submit(
        AuthenticatedContext actor,
        string? workspaceFolderId,
        string? purpose,
        IScopeGrantPersistence persistence)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(persistence);
        EnsureAgent(actor);
        if (!WorkspaceFolderGrantEvaluator.IsNodeId(workspaceFolderId))
        {
            throw new WorkspaceFolderAccessRequestException("request_unavailable");
        }

        var normalizedPurpose = NormalizePurpose(purpose);
        return persistence.UpdateScopeGrantState(state =>
        {
            state = OwnerScopeGrantAuthority.ValidateAndMigrateState(state);
            var requests = state.WorkspaceFolderRequests;
            if (requests.Length >= MaximumRequests)
            {
                throw new WorkspaceFolderAccessRequestException("request_unavailable");
            }

            string requestId;
            do
            {
                requestId = Guid.NewGuid().ToString("N");
            }
            while (Array.Exists(requests, request => request.RequestId == requestId));

            var request = new WorkspaceFolderAccessRequest(
                requestId,
                workspaceFolderId!,
                normalizedPurpose,
                DateTimeOffset.UtcNow,
                PendingStatus);
            return (state with
            {
                WorkspaceFolderRequests = requests.Append(request).ToArray()
            }, Clone(request));
        });
    }

    public (string RequestId, string Status) GetStatus(
        AuthenticatedContext actor,
        string? requestId,
        IScopeGrantPersistence persistence)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(persistence);
        EnsureAgent(actor);
        if (!Guid.TryParseExact(requestId, "N", out _))
        {
            throw new WorkspaceFolderAccessRequestException("request_unavailable");
        }

        var state = OwnerScopeGrantAuthority.ValidateAndMigrateState(persistence.LoadScopeGrantState());
        var request = Array.Find(state.WorkspaceFolderRequests, item => item.RequestId == requestId);
        return request is null
            ? throw new WorkspaceFolderAccessRequestException("request_unavailable")
            : (request.RequestId, request.Status);
    }

    public WorkspaceFolderAccessRequest[] List(
        AuthenticatedContext actor,
        IScopeGrantPersistence persistence)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(persistence);
        EnsureOwner(actor);
        return OwnerScopeGrantAuthority.ValidateAndMigrateState(persistence.LoadScopeGrantState())
            .WorkspaceFolderRequests
            .Select(Clone)
            .ToArray();
    }

    public WorkspaceFolderAccessRequest Resolve(
        AuthenticatedContext actor,
        string? requestId,
        IScopeGrantPersistence persistence)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(persistence);
        EnsureOwner(actor);
        if (!Guid.TryParseExact(requestId, "N", out _))
        {
            throw new WorkspaceFolderAccessRequestException("request_unavailable");
        }

        return persistence.UpdateScopeGrantState(state =>
        {
            state = OwnerScopeGrantAuthority.ValidateAndMigrateState(state);
            var index = Array.FindIndex(state.WorkspaceFolderRequests, item => item.RequestId == requestId);
            if (index < 0)
            {
                throw new WorkspaceFolderAccessRequestException("request_unavailable");
            }

            var request = state.WorkspaceFolderRequests[index];
            if (request.Status == ResolvedStatus)
            {
                return (state, Clone(request));
            }

            var resolved = request with
            {
                Status = ResolvedStatus,
                ResolvedAtUtc = DateTimeOffset.UtcNow
            };
            var requests = (WorkspaceFolderAccessRequest[])state.WorkspaceFolderRequests.Clone();
            requests[index] = resolved;
            return (state with { WorkspaceFolderRequests = requests }, Clone(resolved));
        });
    }

    internal static WorkspaceFolderAccessRequest[] NormalizeRequests(
        WorkspaceFolderAccessRequest[]? requests)
    {
        if (requests is null || requests.Length > MaximumRequests || requests.Any(request => request is null))
        {
            throw new ArgumentException("The encrypted workspace folder request state is invalid.", nameof(requests));
        }

        var normalized = requests.OrderBy(request => request.RequestId, StringComparer.Ordinal).ToArray();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var request in normalized)
        {
            var purpose = NormalizePurpose(request.Purpose);
            if (!Guid.TryParseExact(request.RequestId, "N", out _) || !ids.Add(request.RequestId) ||
                !WorkspaceFolderGrantEvaluator.IsNodeId(request.WorkspaceFolderId) ||
                purpose != request.Purpose || request.CreatedAtUtc.Offset != TimeSpan.Zero ||
                (request.Status == PendingStatus && request.ResolvedAtUtc is not null) ||
                (request.Status == ResolvedStatus &&
                    (request.ResolvedAtUtc is not { } resolvedAt || resolvedAt.Offset != TimeSpan.Zero || resolvedAt < request.CreatedAtUtc)) ||
                request.Status is not (PendingStatus or ResolvedStatus))
            {
                throw new ArgumentException("The encrypted workspace folder request record is invalid.", nameof(requests));
            }
        }

        return normalized;
    }

    private static string NormalizePurpose(string? purpose)
    {
        var normalized = purpose?.Trim();
        if (string.IsNullOrEmpty(normalized) || normalized.Length > MaximumPurposeLength ||
            normalized.Any(char.IsControl))
        {
            throw new WorkspaceFolderAccessRequestException("invalid_request");
        }

        return normalized;
    }

    private static WorkspaceFolderAccessRequest Clone(WorkspaceFolderAccessRequest request) => request with { };

    private static void EnsureAgent(AuthenticatedContext actor)
    {
        if (actor.Kind != PrincipalKind.Agent)
        {
            throw new UnauthorizedAccessException("An authenticated agent context is required.");
        }
    }

    private static void EnsureOwner(AuthenticatedContext actor)
    {
        if (actor.Kind != PrincipalKind.Owner)
        {
            throw new UnauthorizedAccessException("The owner control channel is required.");
        }
    }
}
