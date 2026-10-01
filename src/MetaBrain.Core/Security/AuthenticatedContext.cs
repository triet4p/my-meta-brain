using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("MetaBrain.Connections")]

namespace MetaBrain.Core.Security;

public enum PrincipalKind
{
    Owner,
    Agent
}

public enum ServicePermission
{
    InspectOwnerStatus
}

public sealed class AuthenticatedContext
{
    private AuthenticatedContext(PrincipalKind kind, string principalId, string sessionId)
    {
        Kind = kind;
        PrincipalId = principalId;
        SessionId = sessionId;
    }

    public PrincipalKind Kind { get; }
    public string PrincipalId { get; }
    public string SessionId { get; }

    internal static AuthenticatedContext ForOwner() => new(PrincipalKind.Owner, "owner", "owner");

    internal static AuthenticatedContext ForAgent(string principalId, string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(principalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return new AuthenticatedContext(PrincipalKind.Agent, principalId, sessionId);
    }
}

public static class AuthorizationPolicy
{
    public static bool Allows(AuthenticatedContext context, ServicePermission permission)
    {
        ArgumentNullException.ThrowIfNull(context);

        return permission switch
        {
            ServicePermission.InspectOwnerStatus => context.Kind == PrincipalKind.Owner,
            _ => false
        };
    }
}
