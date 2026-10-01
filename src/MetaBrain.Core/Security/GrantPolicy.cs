using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("MetaBrain.Application")]
[assembly: InternalsVisibleTo("MetaBrain.Connections")]

namespace MetaBrain.Core.Security;


public sealed record EgressGrant(string Provider, string Model, decimal MaximumCostUsd);

public sealed record GrantCreateRequest(
    string PrincipalId,
    string SessionId,
    string? ResourceId,
    string? ZoneId,
    string Operation,
    DateTimeOffset ExpiresAtUtc,
    EgressGrant? Egress);

public sealed record AccessGrant(
    string GrantId,
    string PrincipalId,
    string SessionId,
    string? ResourceId,
    string? ZoneId,
    string Operation,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    EgressGrant? Egress);

public sealed record GrantPolicyState(int SchemaVersion, long PolicyGeneration, AccessGrant[] Grants)
{
    public const int CurrentSchemaVersion = 1;
    public static GrantPolicyState Empty { get; } = new(CurrentSchemaVersion, 0, Array.Empty<AccessGrant>());
}

public interface IGrantStore
{
    GrantPolicyState Load();
    void Save(GrantPolicyState state);
}

public sealed class EgressContext
{
    private EgressContext(string? provider, string? model, decimal? estimatedCostUsd, bool trusted)
    {
        Provider = provider;
        Model = model;
        EstimatedCostUsd = estimatedCostUsd;
        IsTrusted = trusted;
    }

    public string? Provider { get; }
    public string? Model { get; }
    public decimal? EstimatedCostUsd { get; }
    internal bool IsTrusted { get; }

    internal static EgressContext FromTrustedProviderCall(string? provider, string? model, decimal? estimatedCostUsd) =>
        new(provider, model, estimatedCostUsd, trusted: true);

    internal static EgressContext FromUntrustedRequest(string? provider, string? model, decimal? estimatedCostUsd) =>
        new(provider, model, estimatedCostUsd, trusted: false);
}

public sealed record AccessRequest(
    string? ResourceId,
    string? ZoneId,
    string Operation,
    EgressContext? EgressContext = null);

public sealed record AuthorizationDecision(
    bool Allowed,
    string Reason,
    long PolicyGeneration,
    decimal? MaximumCostUsd = null);

public sealed class GrantAuthority
{
    public const string ProviderEgressOperation = "provider.egress";
    private readonly object _gate = new();
    private readonly IGrantStore _store;
    private GrantPolicyState _state;
    private bool _available = true;

    public GrantAuthority(IGrantStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _state = CopyAndValidate(store.Load());
        lock (_gate)
        {
            ExpireGrants(DateTimeOffset.UtcNow);
        }
    }
    public long PolicyGeneration
    {
        get
        {
            lock (_gate)
            {
                return _state.PolicyGeneration;
            }
        }
    }


    public AccessGrant Create(AuthenticatedContext actor, GrantCreateRequest request)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(request);
        EnsureOwner(actor);

        lock (_gate)
        {
            EnsureAvailable();
            var now = DateTimeOffset.UtcNow;
            ExpireGrants(now);
            ValidateCreateRequest(request, now);

            var grant = new AccessGrant(
                Guid.NewGuid().ToString("N"),
                request.PrincipalId,
                request.SessionId,
                request.ResourceId,
                request.ZoneId,
                request.Operation,
                now,
                request.ExpiresAtUtc.ToUniversalTime(),
                request.Egress);
            Commit(_state.Grants.Append(grant).ToArray());
            return grant;
        }
    }

    public IReadOnlyList<AccessGrant> List(AuthenticatedContext actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        EnsureOwner(actor);

        lock (_gate)
        {
            EnsureAvailable();
            ExpireGrants(DateTimeOffset.UtcNow);
            return Array.AsReadOnly((AccessGrant[])_state.Grants.Clone());
        }
    }

    public AccessGrant? Get(AuthenticatedContext actor, string grantId)
    {
        ArgumentNullException.ThrowIfNull(actor);
        EnsureOwner(actor);
        if (!IsGrantId(grantId))
        {
            return null;
        }

        lock (_gate)
        {
            EnsureAvailable();
            ExpireGrants(DateTimeOffset.UtcNow);
            return _state.Grants.FirstOrDefault(grant => string.Equals(grant.GrantId, grantId, StringComparison.Ordinal));
        }
    }

    public bool Revoke(AuthenticatedContext actor, string grantId)
    {
        ArgumentNullException.ThrowIfNull(actor);
        EnsureOwner(actor);
        if (!IsGrantId(grantId))
        {
            return false;
        }

        lock (_gate)
        {
            EnsureAvailable();
            ExpireGrants(DateTimeOffset.UtcNow);
            var remaining = _state.Grants.Where(grant => !string.Equals(grant.GrantId, grantId, StringComparison.Ordinal)).ToArray();
            if (remaining.Length == _state.Grants.Length)
            {
                return false;
            }

            Commit(remaining);
            return true;
        }
    }

    public AuthorizationDecision InspectAgentAccess(
        AuthenticatedContext actor,
        string principalId,
        string sessionId,
        AccessRequest request)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(request);
        EnsureOwner(actor);

        if (IsOwnerTarget(principalId, sessionId))
        {
            return Authorize(AuthenticatedContext.ForOwner(), request);
        }

        if (!IsValidIdentifier(principalId) || !IsValidIdentifier(sessionId))
        {
            return Deny("agent_not_found");
        }

        return Authorize(AuthenticatedContext.ForAgent(principalId, sessionId), request);
    }

    public AuthorizationDecision Authorize(AuthenticatedContext context, AccessRequest request)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);

        lock (_gate)
        {
            try
            {
                ExpireGrants(DateTimeOffset.UtcNow);
            }
            catch (Exception ex) when (IsStoreFailure(ex))
            {
                _available = false;
                return Deny("policy_unavailable");
            }

            if (!IsValidAccessRequest(request))
            {
                return Deny("invalid_request");
            }

            if (!string.Equals(request.Operation, ProviderEgressOperation, StringComparison.Ordinal) && request.EgressContext is not null)
            {
                return Deny("unexpected_egress_context");
            }

            if (!string.Equals(request.Operation, ProviderEgressOperation, StringComparison.Ordinal) && context.Kind == PrincipalKind.Owner)
            {
                return Allow("owner_bypass");
            }

            if (request.Operation == ProviderEgressOperation)
            {
                var egress = request.EgressContext;
                if (egress is null || !IsValidIdentifier(egress.Provider) || !IsValidIdentifier(egress.Model))
                {
                    return Deny("egress_context_unknown");
                }

                if (egress.EstimatedCostUsd is null or <= 0)
                {
                    return Deny("egress_unpriced");
                }

                if (!egress.IsTrusted)
                {
                    return Deny("untrusted_egress_context");
                }
            }

            if (!_available)
            {
                return Deny("policy_unavailable");
            }

            var candidates = _state.Grants.Where(grant =>
                string.Equals(grant.PrincipalId, context.PrincipalId, StringComparison.Ordinal) &&
                string.Equals(grant.SessionId, context.SessionId, StringComparison.Ordinal) &&
                string.Equals(grant.Operation, request.Operation, StringComparison.Ordinal) &&
                ScopeMatches(grant, request));

            if (request.Operation == ProviderEgressOperation)
            {
                var egress = request.EgressContext!;
                AccessGrant? matching = null;
                AccessGrant? allowed = null;
                foreach (var candidate in candidates)
                {
                    if (candidate.Egress is null ||
                        !string.Equals(candidate.Egress.Provider, egress.Provider, StringComparison.Ordinal) ||
                        !string.Equals(candidate.Egress.Model, egress.Model, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    matching ??= candidate;
                    if (egress.EstimatedCostUsd <= candidate.Egress.MaximumCostUsd)
                    {
                        allowed = candidate;
                        break;
                    }
                }

                if (matching is null)
                {
                    return Deny("egress_not_granted");
                }

                if (allowed is null)
                {
                    return Deny("egress_cost_exceeded");
                }

                return Allow("grant_allowed", allowed.Egress!.MaximumCostUsd);
            }

            return candidates.Any()
                ? Allow("grant_allowed")
                : Deny("grant_not_found");
        }
    }

    private void ExpireGrants(DateTimeOffset now)
    {
        var live = _state.Grants.Where(grant => grant.ExpiresAtUtc > now).ToArray();
        if (live.Length != _state.Grants.Length)
        {
            Commit(live);
        }
    }

    private void Commit(AccessGrant[] grants)
    {
        if (_state.PolicyGeneration == long.MaxValue)
        {
            _available = false;
            throw new IOException("The policy generation is exhausted.");
        }

        var next = new GrantPolicyState(GrantPolicyState.CurrentSchemaVersion, _state.PolicyGeneration + 1, grants);
        try
        {
            _store.Save(next);
            _state = next;
        }
        catch
        {
            _available = false;
            throw;
        }
    }

    private GrantPolicyState CopyAndValidate(GrantPolicyState state)
    {
        if (state is null || state.SchemaVersion != GrantPolicyState.CurrentSchemaVersion || state.PolicyGeneration < 0 || state.Grants is null)
        {
            throw new InvalidDataException("Invalid durable grant policy state.");
        }

        var grants = (AccessGrant[])state.Grants.Clone();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var grant in grants)
        {
            if (grant is null || !IsGrantId(grant.GrantId) || !ids.Add(grant.GrantId) ||
                !IsValidIdentifier(grant.PrincipalId) || !IsValidIdentifier(grant.SessionId) ||
                !HasValidScope(grant.ResourceId, grant.ZoneId) || !IsValidIdentifier(grant.Operation) ||
                grant.CreatedAtUtc.Offset != TimeSpan.Zero || grant.ExpiresAtUtc.Offset != TimeSpan.Zero ||
                grant.ExpiresAtUtc <= grant.CreatedAtUtc || !HasValidEgressGrant(grant.Operation, grant.Egress))
            {
                throw new InvalidDataException("Invalid durable grant record.");
            }
        }

        return new GrantPolicyState(state.SchemaVersion, state.PolicyGeneration, grants);
    }

    private void ValidateCreateRequest(GrantCreateRequest request, DateTimeOffset now)
    {
        if (!IsValidIdentifier(request.PrincipalId) || !IsValidIdentifier(request.SessionId) ||
            !HasValidScope(request.ResourceId, request.ZoneId) || !IsValidIdentifier(request.Operation) ||
            request.ExpiresAtUtc <= now || !HasValidEgressGrant(request.Operation, request.Egress))
        {
            throw new ArgumentException("Invalid grant request.", nameof(request));
        }
    }

    private static bool IsValidAccessRequest(AccessRequest request) =>
        HasValidAccessScope(request.ResourceId, request.ZoneId) && IsValidIdentifier(request.Operation);

    private static bool ScopeMatches(AccessGrant grant, AccessRequest request) =>
        grant.ResourceId is not null && string.Equals(grant.ResourceId, request.ResourceId, StringComparison.Ordinal) ||
        grant.ZoneId is not null && string.Equals(grant.ZoneId, request.ZoneId, StringComparison.Ordinal);

    private static bool HasValidScope(string? resourceId, string? zoneId) =>
        IsValidIdentifier(resourceId) != IsValidIdentifier(zoneId) &&
        (resourceId is null || IsValidIdentifier(resourceId)) &&
        (zoneId is null || IsValidIdentifier(zoneId));

    private static bool HasValidAccessScope(string? resourceId, string? zoneId) =>
        (resourceId is not null || zoneId is not null) &&
        (resourceId is null || IsValidIdentifier(resourceId)) &&
        (zoneId is null || IsValidIdentifier(zoneId));

    private static bool HasValidEgressGrant(string operation, EgressGrant? egress)
    {
        if (operation == ProviderEgressOperation)
        {
            return egress is not null && IsValidIdentifier(egress.Provider) && IsValidIdentifier(egress.Model) && egress.MaximumCostUsd > 0;
        }

        return egress is null;
    }

    private static bool IsOwnerTarget(string principalId, string sessionId) =>
        string.Equals(principalId, "owner", StringComparison.Ordinal) && string.Equals(sessionId, "owner", StringComparison.Ordinal);

    private static bool IsGrantId(string? value) => Guid.TryParseExact(value, "N", out _);

    private static bool IsValidIdentifier(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 128 || !IsAsciiAlphaNumeric(value[0]))
        {
            return false;
        }

        for (var index = 1; index < value.Length; index++)
        {
            var character = value[index];
            if (!IsAsciiAlphaNumeric(character) && character is not '.' and not '_' and not '-')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsAsciiAlphaNumeric(char value) =>
        value is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9';

    private static bool IsStoreFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or System.Security.SecurityException;

    private void EnsureAvailable()
    {
        if (!_available)
        {
            throw new IOException("The durable grant policy is unavailable.");
        }
    }

    private static void EnsureOwner(AuthenticatedContext actor)
    {
        if (actor.Kind != PrincipalKind.Owner)
        {
            throw new UnauthorizedAccessException("Grant management requires the authenticated owner channel.");
        }
    }

    private AuthorizationDecision Allow(string reason, decimal? maximumCostUsd = null) =>
        new(true, reason, _state.PolicyGeneration, maximumCostUsd);

    private AuthorizationDecision Deny(string reason) =>
        new(false, reason, _state.PolicyGeneration);
}
