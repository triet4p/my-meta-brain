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
    private readonly HashSet<string> _sessionIds = new(StringComparer.Ordinal);

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
                _sessionsByVerifier[sessionVerifier] = session;
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
        var tokenBytes = AgentBearerEncoding.DecodeSessionToken(sessionToken);
        if (tokenBytes is null)
        {
            return new AgentSessionLookupResult(AgentSessionLookupStatus.Unknown, null);
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
            if (!_sessionsByVerifier.TryGetValue(verifier, out var session))
            {
                foreach (var candidate in _sessionsByVerifier.Values)
                {
                    if (AgentBearerEncoding.VerifierEquals(candidate.SessionVerifier, verifier))
                    {
                        session = candidate;
                        break;
                    }

                    session = null;
                }

                if (session is null)
                {
                    return new AgentSessionLookupResult(AgentSessionLookupStatus.Unknown, null);
                }
            }

            if (session.UnlockEpoch != currentUnlockEpoch)
            {
                _sessionsByVerifier.Remove(session.SessionVerifier);
                return new AgentSessionLookupResult(AgentSessionLookupStatus.Superseded, null);
            }

            if (session.PolicyGeneration != currentPolicyGeneration)
            {
                _sessionsByVerifier.Remove(session.SessionVerifier);
                return new AgentSessionLookupResult(AgentSessionLookupStatus.Revoked, null);
            }

            if (session.ExpiresAtUtc <= DateTimeOffset.UtcNow)
            {
                _sessionsByVerifier.Remove(session.SessionVerifier);
                return new AgentSessionLookupResult(AgentSessionLookupStatus.Expired, null);
            }

            return new AgentSessionLookupResult(AgentSessionLookupStatus.Valid, Snapshot(session));
        }
    }

    public void ClearSessions()
    {
        lock (_gate)
        {
            _sessionsByVerifier.Clear();
            _sessionIds.Clear();
        }
    }

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
