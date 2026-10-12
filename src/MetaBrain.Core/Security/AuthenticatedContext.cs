using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("MetaBrain.Connections")]
[assembly: InternalsVisibleTo("MetaBrain.Application")]
// S3-T1: the smoke project is the repository's permanent regression suite;
// it needs the owner-context factory to drive the real handler/coordinator
// stack in-process (the synthetic-normalizer phase runs the actual production
// code path, not a mock, and no test code ships in the product binaries).
[assembly: InternalsVisibleTo("MetaBrain.S1T4.Smoke")]

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
