using System.Security.Cryptography;

namespace MetaBrain.Core.Security;

/// <summary>
/// Atomically consumes single-use owner-issued scope tokens and mints bounded
/// memory-only agent sessions. The durable consumed-token tombstone is persisted
/// before the session bearer is disclosed, so concurrent redemption has exactly
/// one winner and crash/restart cannot resurrect or reissue a used token.
/// Session material lives only in this process; lock and process exit drop it.
/// </summary>
public sealed class AgentSessionAuthority
{
    private readonly object _gate = new();
    private readonly Dictionary<string, AgentSession> _sessionsByVerifier = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _sessionVerifierById = new(StringComparer.Ordinal);
    private readonly HashSet<string> _sessionIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AgentSessionLookupStatus> _terminalByVerifier = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AgentSessionLookupStatus> _terminalById = new(StringComparer.Ordinal);

    public AgentRedeemResult Redeem(
        string? token,
        IScopeGrantPersistence persistence,
        long currentUnlockEpoch)
    {
        var tokenBytes = AgentBearerEncoding.DecodeGrantToken(token);
        if (tokenBytes is null)
        {
            return new AgentRedeemResult(AgentRedeemStatus.Unknown, null, null);
        }

        string verifier;
        try
        {
            verifier = AgentBearerEncoding.ComputeVerifier(tokenBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(tokenBytes);
        }

        lock (_gate)
        {
            var consumption = persistence.UpdateScopeGrantState<
                (AgentRedeemStatus Status, PersistedScopeGrant? Grant)>(state =>
                {
                    if (state.SchemaVersion != OwnerScopeGrantState.CurrentSchemaVersion ||
                        state.UnlockEpoch != currentUnlockEpoch)
                    {
                        return (state, (AgentRedeemStatus.Superseded, null));
                    }

                    foreach (var consumedVerifier in state.ConsumedTokenVerifiers)
                    {
                        if (AgentBearerEncoding.VerifierEquals(consumedVerifier, verifier))
                        {
                            return (state, (AgentRedeemStatus.Used, null));
                        }
                    }

                    var grant = state.Grants.FirstOrDefault(candidate =>
                        AgentBearerEncoding.VerifierEquals(candidate.TokenVerifier, verifier));
                    if (grant is null)
                    {
                        return (state, (AgentRedeemStatus.Unknown, null));
                    }

                    if (grant.ExpiresAtUtc <= DateTimeOffset.UtcNow)
                    {
                        return (state, (AgentRedeemStatus.Expired, null));
                    }

                    if (grant.UnlockEpoch != currentUnlockEpoch)
                    {
                        return (state, (AgentRedeemStatus.Superseded, null));
                    }

                    if (grant.PolicyGeneration != state.PolicyGeneration)
                    {
                        return (state, (AgentRedeemStatus.Revoked, null));
                    }

                    var consumed = state.ConsumedTokenVerifiers.Append(verifier)
                        .Order(StringComparer.Ordinal).ToArray();
                    return (
                        state with { ConsumedTokenVerifiers = consumed },
                        (AgentRedeemStatus.Issued, grant));
                });
            if (consumption.Status != AgentRedeemStatus.Issued || consumption.Grant is null)
            {
                return new AgentRedeemResult(consumption.Status, null, null);
            }

            var createdAtUtc = DateTimeOffset.UtcNow;
            if (consumption.Grant.ExpiresAtUtc <= createdAtUtc)
            {
                return new AgentRedeemResult(AgentRedeemStatus.Expired, null, null);
            }

            var sessionBytes = RandomNumberGenerator.GetBytes(32);
            try
            {
                var sessionVerifier = AgentBearerEncoding.ComputeVerifier(sessionBytes);
                var sessionToken = AgentBearerEncoding.EncodeSessionToken(sessionBytes);
                string sessionId;
                do
                {
                    sessionId = Guid.NewGuid().ToString("N");
                }
                while (!_sessionIds.Add(sessionId));

                var grant = consumption.Grant;
                var session = new AgentSession(
                    sessionId,
                    grant.GrantId,
                    sessionVerifier,
                    grant.Resources.Select(resource => resource with { }).ToArray(),
                    (string[])grant.Operations.Clone(),
                    grant.DestinationResources.Select(resource => resource with { }).ToArray(),
                    createdAtUtc,
                    grant.ExpiresAtUtc,
                    grant.Egress is null ? null : grant.Egress with { },
                    grant.PolicyGeneration,
                    currentUnlockEpoch);
                _sessionsByVerifier.Add(sessionVerifier, session);
                _sessionVerifierById.Add(sessionId, sessionVerifier);
                return new AgentRedeemResult(
                    AgentRedeemStatus.Issued,
                    sessionToken,
                    Snapshot(session));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(sessionBytes);
            }
        }
    }

    public AgentSessionLookupResult LookupSession(
        string? sessionToken,
        long currentUnlockEpoch,
        long currentPolicyGeneration)
    {
        var verifier = TryComputeVerifier(sessionToken);
        if (verifier is null)
        {
            return new AgentSessionLookupResult(AgentSessionLookupStatus.Unknown, null);
        }

        lock (_gate)
        {
            var status = LookupVerifierLocked(
                verifier, currentUnlockEpoch, currentPolicyGeneration, out var session);
            return new AgentSessionLookupResult(status, session is null ? null : Snapshot(session));
        }
    }

    public AuthorizationDecision AuthorizeSession(
        string? sessionToken,
        long currentUnlockEpoch,
        long currentPolicyGeneration,
        AgentAccessRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var verifier = TryComputeVerifier(sessionToken);
        if (verifier is null)
        {
            return Deny("session_unknown", currentPolicyGeneration);
        }

        lock (_gate)
        {
            var status = LookupVerifierLocked(
                verifier, currentUnlockEpoch, currentPolicyGeneration, out var session);
            if (status != AgentSessionLookupStatus.Valid || session is null)
            {
                return Deny(StatusError(status), currentPolicyGeneration);
            }

            return AuthorizeSnapshot(session, request, currentPolicyGeneration);
        }
    }

    public AgentSessionSnapshot[] ListSessions(long currentUnlockEpoch, long currentPolicyGeneration)
    {
        lock (_gate)
        {
            foreach (var session in _sessionsByVerifier.Values.ToArray())
            {
                _ = GetLiveStatusLocked(session, currentUnlockEpoch, currentPolicyGeneration);
            }

            return _sessionsByVerifier.Values
                .Select(Snapshot)
                .OrderBy(session => session.SessionId, StringComparer.Ordinal)
                .ToArray();
        }
    }

    public AgentSessionRevocationResult RevokeSession(
        string? sessionId,
        long currentUnlockEpoch,
        long currentPolicyGeneration)
    {
        if (!Guid.TryParseExact(sessionId, "N", out _))
        {
            return new AgentSessionRevocationResult(AgentSessionRevocationStatus.Unknown);
        }

        lock (_gate)
        {
            if (!_sessionVerifierById.TryGetValue(sessionId!, out var verifier) ||
                !_sessionsByVerifier.TryGetValue(verifier, out var session))
            {
                return new AgentSessionRevocationResult(
                    _terminalById.TryGetValue(sessionId!, out var terminal)
                        ? ToRevocationStatus(terminal)
                        : AgentSessionRevocationStatus.Unknown);
            }

            var status = GetLiveStatusLocked(session, currentUnlockEpoch, currentPolicyGeneration);
            if (status != AgentSessionLookupStatus.Valid)
            {
                return new AgentSessionRevocationResult(ToRevocationStatus(status));
            }

            RemoveSessionLocked(session, AgentSessionLookupStatus.Revoked);
            return new AgentSessionRevocationResult(AgentSessionRevocationStatus.Revoked);
        }
    }

    public void ClearSessions()
    {
        lock (_gate)
        {
            _sessionsByVerifier.Clear();
            _sessionVerifierById.Clear();
            _sessionIds.Clear();
            _terminalByVerifier.Clear();
            _terminalById.Clear();
        }
    }

    private AgentSessionLookupStatus LookupVerifierLocked(
        string verifier,
        long currentUnlockEpoch,
        long currentPolicyGeneration,
        out AgentSession? session)
    {
        if (!_sessionsByVerifier.TryGetValue(verifier, out session))
        {
            return _terminalByVerifier.TryGetValue(verifier, out var terminal)
                ? terminal
                : AgentSessionLookupStatus.Unknown;
        }

        var status = GetLiveStatusLocked(session, currentUnlockEpoch, currentPolicyGeneration);
        if (status != AgentSessionLookupStatus.Valid)
        {
            session = null;
        }

        return status;
    }

    private AgentSessionLookupStatus GetLiveStatusLocked(
        AgentSession session,
        long currentUnlockEpoch,
        long currentPolicyGeneration)
    {
        var status = session.UnlockEpoch != currentUnlockEpoch
            ? AgentSessionLookupStatus.Superseded
            : session.PolicyGeneration != currentPolicyGeneration
                ? AgentSessionLookupStatus.Revoked
                : session.ExpiresAtUtc <= DateTimeOffset.UtcNow
                    ? AgentSessionLookupStatus.Expired
                    : AgentSessionLookupStatus.Valid;

        if (status != AgentSessionLookupStatus.Valid)
        {
            RemoveSessionLocked(session, status);
        }

        return status;
    }

    private void RemoveSessionLocked(AgentSession session, AgentSessionLookupStatus status)
    {
        _sessionsByVerifier.Remove(session.SessionVerifier);
        _sessionVerifierById.Remove(session.SessionId);
        _terminalByVerifier[session.SessionVerifier] = status;
        _terminalById[session.SessionId] = status;
    }

    private static AuthorizationDecision AuthorizeSnapshot(
        AgentSession session,
        AgentAccessRequest request,
        long policyGeneration)
    {
        if (string.IsNullOrWhiteSpace(request.Operation))
        {
            return Deny("invalid_request", policyGeneration);
        }

        if (!session.Operations.Contains(request.Operation, StringComparer.Ordinal))
        {
            return Deny("resource_unavailable", policyGeneration);
        }

        switch (request.Operation)
        {
            case "resource.read":
                if (request.DestinationResourceId is not null || request.DestinationRevision is not null ||
                    HasEgressFields(request))
                {
                    return Deny("invalid_request", policyGeneration);
                }

                return IsInScope(session.Resources, request.ResourceId, request.ResourceRevision)
                    ? Allow(policyGeneration)
                    : Deny("resource_unavailable", policyGeneration);

            case "proposal.create":
                if (request.ResourceId is not null || request.ResourceRevision is not null ||
                    request.DestinationResourceId is null || request.DestinationRevision is null ||
                    HasEgressFields(request))
                {
                    return Deny("invalid_request", policyGeneration);
                }

                return IsInScope(session.DestinationResources,
                    request.DestinationResourceId, request.DestinationRevision)
                    ? Allow(policyGeneration)
                    : Deny("resource_unavailable", policyGeneration);

            case "link.create":
                if (HasEgressFields(request))
                {
                    return Deny("invalid_request", policyGeneration);
                }

                return IsInScope(session.Resources, request.ResourceId, request.ResourceRevision) &&
                    IsInScope(session.DestinationResources,
                        request.DestinationResourceId, request.DestinationRevision)
                    ? Allow(policyGeneration)
                    : Deny("resource_unavailable", policyGeneration);

            case "provider.egress":
                return AuthorizeEgress(session, request, policyGeneration);

            default:
                return Deny("invalid_request", policyGeneration);
        }
    }

    private static AuthorizationDecision AuthorizeEgress(
        AgentSession session,
        AgentAccessRequest request,
        long policyGeneration)
    {
        if (request.ResourceId is not null || request.ResourceRevision is not null ||
            request.DestinationResourceId is not null || request.DestinationRevision is not null)
        {
            return Deny("invalid_request", policyGeneration);
        }

        if (session.Egress is null ||
            !string.Equals(session.Egress.Provider, request.Provider, StringComparison.Ordinal) ||
            !string.Equals(session.Egress.Model, request.Model, StringComparison.Ordinal))
        {
            return Deny("egress_not_granted", policyGeneration);
        }

        if (request.EstimatedCostUsd is null or <= 0)
        {
            return Deny("egress_unpriced", policyGeneration);
        }

        if (request.EstimatedCostUsd > session.Egress.MaximumCostUsd ||
            request.EstimatedCostUsd > OwnerScopeGrantAuthority.MaximumEgressJobCostUsd)
        {
            return Deny("egress_cost_exceeded", policyGeneration);
        }

        if (request.InputTokens is null or < 0 || request.OutputTokens is null or < 0)
        {
            return Deny("egress_budget_unknown", policyGeneration);
        }

        if (request.InputTokens > OwnerScopeGrantAuthority.MaximumEgressInputTokens ||
            request.OutputTokens > OwnerScopeGrantAuthority.MaximumEgressOutputTokens)
        {
            return Deny("egress_budget_exceeded", policyGeneration);
        }

        // The agent pipe carries untrusted request metadata, not a priced provider
        // call context. No provider adapter exists here to create trusted estimates.
        return Deny("untrusted_egress_context", policyGeneration);
    }

    private static bool HasEgressFields(AgentAccessRequest request) =>
        request.Provider is not null || request.Model is not null ||
        request.EstimatedCostUsd is not null || request.InputTokens is not null ||
        request.OutputTokens is not null;

    private static bool IsInScope(
        IReadOnlyList<ScopeResourceRevision> scope,
        string? resourceId,
        long? revision) =>
        !string.IsNullOrWhiteSpace(resourceId) && revision is > 0 &&
        scope.Any(resource =>
            string.Equals(resource.ResourceId, resourceId, StringComparison.Ordinal) &&
            resource.Revision == revision.Value);

    private static string? TryComputeVerifier(string? sessionToken)
    {
        var tokenBytes = AgentBearerEncoding.DecodeSessionToken(sessionToken);
        if (tokenBytes is null)
        {
            return null;
        }

        try
        {
            return AgentBearerEncoding.ComputeVerifier(tokenBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(tokenBytes);
        }
    }

    private static string StatusError(AgentSessionLookupStatus status) => status switch
    {
        AgentSessionLookupStatus.Revoked => "session_revoked",
        AgentSessionLookupStatus.Superseded => "session_superseded",
        AgentSessionLookupStatus.Expired => "session_expired",
        _ => "session_unknown"
    };

    private static AgentSessionRevocationStatus ToRevocationStatus(AgentSessionLookupStatus status) => status switch
    {
        AgentSessionLookupStatus.Revoked => AgentSessionRevocationStatus.Revoked,
        AgentSessionLookupStatus.Superseded => AgentSessionRevocationStatus.Superseded,
        AgentSessionLookupStatus.Expired => AgentSessionRevocationStatus.Expired,
        _ => AgentSessionRevocationStatus.Unknown
    };

    private static AuthorizationDecision Allow(long policyGeneration) =>
        new(true, "scope_allowed", policyGeneration);

    private static AuthorizationDecision Deny(string reason, long policyGeneration) =>
        new(false, reason, policyGeneration);

    private static AgentSessionSnapshot Snapshot(AgentSession session) => new(
        session.SessionId,
        session.GrantId,
        session.Resources.Select(resource => resource with { }).ToArray(),
        (string[])session.Operations.Clone(),
        session.DestinationResources.Select(resource => resource with { }).ToArray(),
        session.CreatedAtUtc,
        session.ExpiresAtUtc,
        session.Egress is null ? null : session.Egress with { },
        session.PolicyGeneration,
        session.UnlockEpoch);
}
