using System.Security.Cryptography;
using MetaBrain.Core.Security;

namespace MetaBrain.Application;

public sealed record ServiceRequest(
    int ProtocolVersion,
    string Operation,
    string? ResourceId = null,
    string? ZoneId = null,
    string? Passphrase = null,
    string? RecoveryCode = null,
    string? ContentBase64 = null);

public sealed record ServiceReply(
    int ProtocolVersion,
    string? Status,
    string? Principal,
    string? Error,
    AuthorizationDecision? Decision = null,
    long? PolicyGeneration = null,
    string? ContentBase64 = null,
    string? ContentType = null,
    string? VaultState = null,
    string? RecoveryCode = null,
    long? ResourceRevision = null);

public sealed class ServiceRequestHandler
{
    public const int CurrentProtocolVersion = 1;
    public const string ResourceReadOperation = "resource.read";
    public const string ResourceWriteOperation = "resource.write";
    private readonly GrantAuthority _grantAuthority;
    private readonly IVaultLifecycle _vault;

    public ServiceRequestHandler(GrantAuthority grantAuthority, IVaultLifecycle vault)
    {
        _grantAuthority = grantAuthority ?? throw new ArgumentNullException(nameof(grantAuthority));
        _vault = vault ?? throw new ArgumentNullException(nameof(vault));
    }

    public async Task<ServiceReply> HandleAsync(
        AuthenticatedContext context,
        ServiceRequest request,
        IVaultOperation? operation)
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

            return new ServiceReply(CurrentProtocolVersion, "running", "owner", null, VaultState: _vault.State);
        }

        if (context.Kind != PrincipalKind.Owner)
        {
            return Error("forbidden");
        }

        if (string.Equals(request.Operation, "vault.provision", StringComparison.Ordinal))
        {
            if (request.Passphrase is not { Length: >= 12 and <= 4096 })
            {
                return Error("credential_required");
            }

            if (!_vault.Provision(request.Passphrase, out var recoveryCode) || recoveryCode is null)
            {
                return Error("vault_already_initialized");
            }

            return new ServiceReply(CurrentProtocolVersion, "provisioned", "owner", null,
                VaultState: _vault.State, RecoveryCode: recoveryCode);
        }

        if (string.Equals(request.Operation, "vault.unlock", StringComparison.Ordinal) ||
            string.Equals(request.Operation, "vault.recover", StringComparison.Ordinal))
        {
            var recovery = string.Equals(request.Operation, "vault.recover", StringComparison.Ordinal);
            var credential = recovery ? request.RecoveryCode : request.Passphrase;
            if (credential is null || credential.Length is 0 or > 4096 || !_vault.Unlock(credential, recovery))
            {
                return Error("unlock_denied");
            }

            return new ServiceReply(CurrentProtocolVersion, "unlocked", "owner", null, VaultState: _vault.State);
        }

        if (string.Equals(request.Operation, "vault.lock", StringComparison.Ordinal))
        {
            await _vault.LockAsync().ConfigureAwait(false);
            return new ServiceReply(CurrentProtocolVersion, "locked", "owner", null, VaultState: _vault.State);
        }

        if (string.Equals(request.Operation, ResourceReadOperation, StringComparison.Ordinal))
        {
            return HandleResourceRead(context, request.ResourceId, operation);
        }

        if (string.Equals(request.Operation, ResourceWriteOperation, StringComparison.Ordinal))
        {
            return HandleResourceWrite(context, request, operation);
        }

        return Error("unknown_operation");
    }

    private ServiceReply HandleResourceRead(AuthenticatedContext context, string? resourceId, IVaultOperation? operation)
    {
        if (operation is null)
        {
            return ResourceDenied();
        }

        if (!operation.TryGetZone(resourceId!, out var zoneId) || zoneId is null)
        {
            return ResourceDenied();
        }

        byte[] snapshot;
        try
        {
            snapshot = operation.ReadContent(resourceId!);
        }
        catch (ManagedResourceUnavailableException)
        {
            return ResourceDenied();
        }

        try
        {
            var decision = _grantAuthority.Authorize(
                context, new AccessRequest(resourceId, zoneId, ResourceReadOperation));
            if (!decision.Allowed)
            {
                CryptographicOperations.ZeroMemory(snapshot);
                return ResourceDenied();
            }

            var recheck = _grantAuthority.Authorize(
                context, new AccessRequest(resourceId, zoneId, ResourceReadOperation));
            if (!recheck.Allowed)
            {
                CryptographicOperations.ZeroMemory(snapshot);
                return ResourceDenied();
            }

            try
            {
                operation.ValidateRegistration(resourceId!);
            }
            catch (ManagedResourceUnavailableException)
            {
                CryptographicOperations.ZeroMemory(snapshot);
                return ResourceDenied();
            }

            return new ServiceReply(
                CurrentProtocolVersion, "content", null, null,
                Decision: recheck,
                PolicyGeneration: recheck.PolicyGeneration,
                ContentBase64: Convert.ToBase64String(snapshot),
                ContentType: "application/octet-stream");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(snapshot);
        }
    }

    private ServiceReply HandleResourceWrite(AuthenticatedContext context, ServiceRequest request, IVaultOperation? operation)
    {
        if (operation is null || string.IsNullOrEmpty(request.ResourceId) || string.IsNullOrEmpty(request.ZoneId) ||
            string.IsNullOrEmpty(request.ContentBase64))
        {
            return ResourceDenied();
        }

        byte[]? content = null;
        try
        {
            content = Convert.FromBase64String(request.ContentBase64);
            var decision = _grantAuthority.Authorize(
                context, new AccessRequest(request.ResourceId, request.ZoneId, ResourceWriteOperation));
            if (!decision.Allowed)
            {
                return ResourceDenied();
            }

            var revision = operation.WriteContent(request.ResourceId, request.ZoneId, content);
            return new ServiceReply(CurrentProtocolVersion, "written", null, null,
                Decision: decision, PolicyGeneration: decision.PolicyGeneration, ResourceRevision: revision);
        }
        catch (FormatException)
        {
            return Error("invalid_request");
        }
        catch (ManagedResourceUnavailableException)
        {
            return ResourceDenied();
        }
        finally
        {
            if (content is { Length: > 0 })
            {
                CryptographicOperations.ZeroMemory(content);
            }
        }
    }

    private static ServiceReply ResourceDenied() =>
        new(CurrentProtocolVersion, "denied", null, "resource_unavailable",
            Decision: new AuthorizationDecision(false, "resource_unavailable", 0));

    private static ServiceReply Error(string error) => new(CurrentProtocolVersion, null, null, error);
}
