using MetaBrain.Core.Security;

namespace MetaBrain.Application;

public sealed record ServiceRequest(
    int ProtocolVersion,
    string Operation,
    string? ResourceId = null);

public sealed record ServiceReply(
    int ProtocolVersion,
    string? Status,
    string? Principal,
    string? Error,
    AuthorizationDecision? Decision = null,
    long? PolicyGeneration = null,
    string? ContentBase64 = null,
    string? ContentType = null);

public sealed class ServiceRequestHandler
{
    public const int CurrentProtocolVersion = 1;
    public const string ResourceReadOperation = "resource.read";
    private readonly GrantAuthority _grantAuthority;
    private readonly ManagedResourceCatalog _resourceCatalog;
    private readonly IManagedResourceReader? _resourceReader;

    public ServiceRequestHandler(
        GrantAuthority grantAuthority,
        ManagedResourceCatalog resourceCatalog,
        IManagedResourceReader? resourceReader)
    {
        _grantAuthority = grantAuthority ?? throw new ArgumentNullException(nameof(grantAuthority));
        _resourceCatalog = resourceCatalog ?? throw new ArgumentNullException(nameof(resourceCatalog));
        _resourceReader = resourceReader;
    }

    public ServiceReply Handle(AuthenticatedContext context, ServiceRequest request)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);

        if (request.ProtocolVersion != CurrentProtocolVersion)
        {
            return Error("unsupported_protocol");
        }

        if (string.Equals(request.Operation, "owner.status", StringComparison.Ordinal))
        {
            if (!AuthorizationPolicy.Allows(context, ServicePermission.InspectOwnerStatus))
            {
                return Error("forbidden");
            }

            return new ServiceReply(CurrentProtocolVersion, "running", "owner", null);
        }

        if (string.Equals(request.Operation, "resource.read", StringComparison.Ordinal))
        {
            return HandleResourceRead(context, request.ResourceId);
        }

        return Error("unknown_operation");
    }

    private ServiceReply HandleResourceRead(AuthenticatedContext context, string? resourceId)
    {
        // The opaque ID is validated by the catalog lookup before any grant or
        // filesystem work: unknown, malformed, and path-like IDs all fail here
        // with one indistinguishable error, and no request path text ever
        // reaches the filesystem authority.
        if (!_resourceCatalog.TryGetZone(resourceId, out var zoneId) || zoneId is null)
        {
            return ResourceDenied();
        }

        if (_resourceReader is null)
        {
            return ResourceDenied();
        }

        // Pre-open the managed file through the single service-owned reader so
        // the file handle exists before the first authorization check. This
        // closes the check-then-open race where revocation between authorize
        // and open could otherwise serve a stale grant.
        byte[] snapshot;
        try
        {
            snapshot = _resourceReader.ReadContent(resourceId!);
        }
        catch (ManagedResourceUnavailableException)
        {
            return ResourceDenied();
        }

        try
        {
            // Authorize the opaque ID/zone under the current policy generation.
            var decision = _grantAuthority.Authorize(
                context, new AccessRequest(resourceId, zoneId, ResourceReadOperation));
            if (!decision.Allowed)
            {
                CryptographicClear(snapshot);
                return ResourceDenied();
            }

            // Re-check under the latest generation after the read: revocation
            // between the first check and now advances the generation and fails
            // closed here instead of serving the pre-revocation snapshot.
            var recheck = _grantAuthority.Authorize(
                context, new AccessRequest(resourceId, zoneId, ResourceReadOperation));
            if (!recheck.Allowed)
            {
                CryptographicClear(snapshot);
                return ResourceDenied();
            }

            // Confirm the registration did not change between the two grant
            // checks (covers mapping replacement races on the same ID).
            try
            {
                _resourceReader.ValidateRegistration(resourceId!);
            }
            catch (ManagedResourceUnavailableException)
            {
                CryptographicClear(snapshot);
                return ResourceDenied();
            }

            var reply = new ServiceReply(
                CurrentProtocolVersion, "content", null, null,
                Decision: recheck, PolicyGeneration: recheck.PolicyGeneration,
                ContentBase64: Convert.ToBase64String(snapshot),
                ContentType: "application/octet-stream");
            CryptographicClear(snapshot);
            return reply;
        }
        finally
        {
            CryptographicClear(snapshot);
        }
    }

    private static void CryptographicClear(byte[] buffer)
    {
        if (buffer.Length > 0)
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(buffer);
        }
    }

    private static ServiceReply ResourceDenied() =>
        new(CurrentProtocolVersion, "denied", null, "resource_unavailable",
            Decision: new AuthorizationDecision(false, "resource_unavailable", 0));

    private static ServiceReply Error(string error) => new(CurrentProtocolVersion, null, null, error);
}
