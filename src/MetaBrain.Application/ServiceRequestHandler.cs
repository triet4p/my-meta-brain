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
    string? ContentBase64 = null,
    string[]? ResourceIds = null,
    string[]? Operations = null,
    string[]? DestinationResourceIds = null,
    DateTimeOffset? ExpiresAtUtc = null,
    string? Provider = null,
    string? Model = null,
    decimal? MaximumCostUsd = null,
    string? PreviewId = null,
    string? CollectionId = null,
    string? Token = null,
    string? SessionToken = null);

public sealed record OwnerScopeGrantSummary(
    string GrantId,
    ScopeResourceRevision[] Resources,
    string[] Operations,
    ScopeResourceRevision[] DestinationResources,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    ScopeGrantEgress? Egress,
    long PolicyGeneration);

public sealed record OwnerScopeCollectionSummary(string CollectionId, string[] ResourceIds);

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
    long? ResourceRevision = null,
    OwnerScopeGrantPreview? ScopePreview = null,
    OwnerScopeGrantSummary[]? ScopeGrants = null,
    OwnerScopeCollectionSummary[]? ScopeCollections = null,
    string? CollectionId = null,
    string? GrantId = null,
    string? Token = null,
    string? SessionToken = null,
    AgentSessionSnapshot? AgentSession = null);


public sealed class ServiceRequestHandler
{
    public const int CurrentProtocolVersion = 1;
    public const string ResourceReadOperation = "resource.read";
    public const string ResourceWriteOperation = "resource.write";
    public const string OwnerScopePreviewOperation = "owner.scope.preview";
    public const string OwnerScopeIssueOperation = "owner.scope.issue";
    public const string OwnerScopeListOperation = "owner.scope.list";
    public const string OwnerScopeCollectionSetOperation = "owner.scope.collection.set";
    public const string OwnerScopeCollectionListOperation = "owner.scope.collection.list";
    public const string AgentRedeemOperation = "agent.session.redeem";
    public const string AgentSessionInspectOperation = "agent.session.inspect";
    private readonly GrantAuthority _grantAuthority;
    private readonly IVaultLifecycle _vault;
    private readonly OwnerScopeGrantAuthority _ownerScopeGrants;
    private readonly AgentSessionAuthority _agentSessions;

    public ServiceRequestHandler(
        GrantAuthority grantAuthority,
        IVaultLifecycle vault,
        OwnerScopeGrantAuthority ownerScopeGrants,
        AgentSessionAuthority? agentSessions = null)
    {
        _grantAuthority = grantAuthority ?? throw new ArgumentNullException(nameof(grantAuthority));
        _vault = vault ?? throw new ArgumentNullException(nameof(vault));
        _ownerScopeGrants = ownerScopeGrants ?? throw new ArgumentNullException(nameof(ownerScopeGrants));
        _agentSessions = agentSessions ?? new AgentSessionAuthority();
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

        if (string.Equals(request.Operation, AgentRedeemOperation, StringComparison.Ordinal))
        {
            return HandleAgentRedeem(context, request, operation);
        }

        if (string.Equals(request.Operation, AgentSessionInspectOperation, StringComparison.Ordinal))
        {
            return HandleAgentSessionInspect(context, request, operation);
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
            _ownerScopeGrants.ClearPreviews();
            _agentSessions.ClearSessions();
            return new ServiceReply(CurrentProtocolVersion, "locked", "owner", null, VaultState: _vault.State);
        }

        if (string.Equals(request.Operation, OwnerScopePreviewOperation, StringComparison.Ordinal))
        {
            return HandleScopePreview(context, request, operation);
        }

        if (string.Equals(request.Operation, OwnerScopeIssueOperation, StringComparison.Ordinal))
        {
            return HandleScopeIssue(context, request.PreviewId, operation);
        }

        if (string.Equals(request.Operation, OwnerScopeListOperation, StringComparison.Ordinal))
        {
            return HandleScopeList(context, operation);
        }
        if (string.Equals(request.Operation, OwnerScopeCollectionSetOperation, StringComparison.Ordinal))
        {
            return HandleScopeCollectionSet(context, request.CollectionId, request.ResourceIds, operation);
        }

        if (string.Equals(request.Operation, OwnerScopeCollectionListOperation, StringComparison.Ordinal))
        {
            return HandleScopeCollectionList(context, operation);
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

    private ServiceReply HandleScopePreview(
        AuthenticatedContext context,
        ServiceRequest request,
        IVaultOperation? operation)
    {
        if (operation is null)
        {
            return Error("vault_locked");
        }

        if (request.ExpiresAtUtc is not { } expiresAtUtc)
        {
            return Error("invalid_grant_request");
        }

        try
        {
            var available = operation.ListResources();
            if (request.CollectionId is not null && (request.ZoneId is not null || request.ResourceIds is { Length: > 0 }))
            {
                throw new ArgumentException("Select a collection, zone, or exact resources.");
            }

            var resources = request.CollectionId is null
                ? ResolveResourceSelection(available, request.ZoneId, request.ResourceIds)
                : Array.Empty<ScopeResourceRevision>();
            var destinations = request.DestinationResourceIds is { Length: > 0 } destinationIds
                ? ResolveResourceIds(available, destinationIds)
                : Array.Empty<ScopeResourceRevision>();
            var hasEgress = request.Provider is not null || request.Model is not null || request.MaximumCostUsd is not null;
            var egress = hasEgress
                ? request.Provider is not null && request.Model is not null && request.MaximumCostUsd is not null
                    ? new ScopeGrantEgress(request.Provider, request.Model, request.MaximumCostUsd.Value)
                    : throw new ArgumentException("Provider egress fields must be supplied together.")
                : null;
            var preview = request.CollectionId is null
                ? _ownerScopeGrants.Preview(context, resources, request.Operations, destinations, expiresAtUtc, egress, operation)
                : _ownerScopeGrants.PreviewCollection(
                    context, request.CollectionId, available, request.Operations, destinations, expiresAtUtc, egress, operation);
            return new ServiceReply(CurrentProtocolVersion, "scope_preview", "owner", null,
                PolicyGeneration: preview.PolicyGeneration, ScopePreview: preview);
        }
        catch (ArgumentException)
        {
            return Error("invalid_grant_request");
        }
        catch (InvalidDataException)
        {
            return Error("scope_state_unavailable");
        }
        catch (IOException)
        {
            return Error("scope_state_unavailable");
        }
    }

    private ServiceReply HandleScopeIssue(
        AuthenticatedContext context,
        string? previewId,
        IVaultOperation? operation)
    {
        if (operation is null)
        {
            return Error("vault_locked");
        }

        try
        {
            var issued = _ownerScopeGrants.Issue(context, previewId, operation);
            return new ServiceReply(CurrentProtocolVersion, "token_issued", "owner", null,
                PolicyGeneration: issued.Grant.PolicyGeneration, GrantId: issued.Grant.GrantId, Token: issued.Token);
        }
        catch (ArgumentException)
        {
            return Error("invalid_grant_request");
        }
        catch (InvalidOperationException)
        {
            return Error("scope_preview_unavailable");
        }
        catch (InvalidDataException)
        {
            return Error("scope_state_unavailable");
        }
        catch (IOException)
        {
            return Error("scope_state_unavailable");
        }
    }

    private ServiceReply HandleScopeList(AuthenticatedContext context, IVaultOperation? operation)
    {
        if (operation is null)
        {
            return Error("vault_locked");
        }

        try
        {
            var grants = _ownerScopeGrants.List(context, operation)
                .Select(grant => new OwnerScopeGrantSummary(
                    grant.GrantId, grant.Resources, grant.Operations, grant.DestinationResources,
                    grant.CreatedAtUtc, grant.ExpiresAtUtc, grant.Egress, grant.PolicyGeneration))
                .ToArray();
            return new ServiceReply(CurrentProtocolVersion, "scope_grants", "owner", null,
                PolicyGeneration: grants.Length == 0 ? 0 : grants.Max(grant => grant.PolicyGeneration),
                ScopeGrants: grants);
        }
        catch (InvalidDataException)
        {
            return Error("scope_state_unavailable");
        }
        catch (IOException)
        {
            return Error("scope_state_unavailable");
        }
    }

    private ServiceReply HandleScopeCollectionSet(
        AuthenticatedContext context,
        string? collectionId,
        string[]? resourceIds,
        IVaultOperation? operation)
    {
        if (operation is null)
        {
            return Error("vault_locked");
        }

        if (resourceIds is not { Length: > 0 })
        {
            return Error("invalid_collection_request");
        }

        try
        {
            var resources = ResolveResourceIds(operation.ListResources(), resourceIds);
            var collection = _ownerScopeGrants.SetCollection(context, collectionId, resources, operation);
            var summary = new OwnerScopeCollectionSummary(collection.CollectionId, collection.ResourceIds);
            return new ServiceReply(CurrentProtocolVersion, "collection_set", "owner", null,
                ScopeCollections: new[] { summary }, CollectionId: collection.CollectionId);
        }
        catch (ArgumentException)
        {
            return Error("invalid_collection_request");
        }
        catch (InvalidDataException)
        {
            return Error("scope_state_unavailable");
        }
        catch (IOException)
        {
            return Error("scope_state_unavailable");
        }
    }

    private ServiceReply HandleScopeCollectionList(AuthenticatedContext context, IVaultOperation? operation)
    {
        if (operation is null)
        {
            return Error("vault_locked");
        }

        try
        {
            var collections = _ownerScopeGrants.ListCollections(context, operation)
                .Select(collection => new OwnerScopeCollectionSummary(collection.CollectionId, collection.ResourceIds))
                .ToArray();
            return new ServiceReply(CurrentProtocolVersion, "scope_collections", "owner", null,
                ScopeCollections: collections);
        }
        catch (InvalidDataException)
        {
            return Error("scope_state_unavailable");
        }
        catch (IOException)
        {
            return Error("scope_state_unavailable");
        }
    }

    private ServiceReply HandleAgentRedeem(AuthenticatedContext context, ServiceRequest request, IVaultOperation? operation)
    {
        if (context.Kind != PrincipalKind.Agent)
        {
            return Error("forbidden");
        }

        if (operation is null)
        {
            return Error("vault_locked");
        }

        if (HasOwnerOnlyFields(request))
        {
            return Error("forbidden");
        }

        AgentRedeemResult redeemed;
        try
        {
            redeemed = _agentSessions.Redeem(request.Token, operation, CurrentEpoch(operation));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return Error("token_unavailable");
        }

        return redeemed.Status switch
        {
            AgentRedeemStatus.Issued when redeemed.SessionToken is not null && redeemed.Session is not null =>
                new ServiceReply(CurrentProtocolVersion, "session_issued", "agent", null,
                    PolicyGeneration: redeemed.Session.PolicyGeneration,
                    SessionToken: redeemed.SessionToken, AgentSession: redeemed.Session),
            AgentRedeemStatus.Unknown => Error("token_unknown"),
            AgentRedeemStatus.Used => Error("token_used"),
            AgentRedeemStatus.Revoked => Error("token_revoked"),
            AgentRedeemStatus.Superseded => Error("token_superseded"),
            AgentRedeemStatus.Expired => Error("token_expired"),
            _ => Error("token_unavailable"),
        };
    }

    private ServiceReply HandleAgentSessionInspect(AuthenticatedContext context, ServiceRequest request, IVaultOperation? operation)
    {
        if (context.Kind != PrincipalKind.Agent)
        {
            return Error("forbidden");
        }

        if (operation is null)
        {
            return Error("vault_locked");
        }

        if (HasOwnerOnlyFields(request))
        {
            return Error("forbidden");
        }

        AgentSessionLookupResult lookup;
        try
        {
            lookup = _agentSessions.LookupSession(request.SessionToken, CurrentEpoch(operation), CurrentGeneration(operation));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return Error("session_unavailable");
        }

        return lookup.Status switch
        {
            AgentSessionLookupStatus.Valid when lookup.Session is not null =>
                new ServiceReply(CurrentProtocolVersion, "session_valid", "agent", null,
                    PolicyGeneration: lookup.Session.PolicyGeneration, AgentSession: lookup.Session),
            AgentSessionLookupStatus.Revoked => Error("session_revoked"),
            AgentSessionLookupStatus.Superseded => Error("session_superseded"),
            AgentSessionLookupStatus.Expired => Error("session_expired"),
            _ => Error("session_unknown"),
        };
    }

    private static long CurrentEpoch(IVaultOperation operation) =>
        operation.LoadScopeGrantState().UnlockEpoch;

    private static long CurrentGeneration(IVaultOperation operation) =>
        operation.LoadScopeGrantState().PolicyGeneration;

    private static bool HasOwnerOnlyFields(ServiceRequest request) =>
        request.Passphrase is not null || request.RecoveryCode is not null ||
        request.ResourceIds is not null || request.Operations is not null ||
        request.DestinationResourceIds is not null || request.ExpiresAtUtc is not null ||
        request.Provider is not null || request.Model is not null ||
        request.MaximumCostUsd is not null || request.PreviewId is not null ||
        request.CollectionId is not null || request.ResourceId is not null ||
        request.ZoneId is not null || request.ContentBase64 is not null;

    private static ScopeResourceRevision[] ResolveResourceSelection(
        IReadOnlyList<ScopeResourceRevision> available,
        string? zoneId,
        string[]? resourceIds)
    {
        if (zoneId is not null && resourceIds is { Length: > 0 })
        {
            throw new ArgumentException("Select a zone or exact resources, not both.");
        }

        if (zoneId is not null)
        {
            var selected = available.Where(resource => string.Equals(resource.ZoneId, zoneId, StringComparison.Ordinal)).ToArray();
            if (selected.Length == 0)
            {
                throw new ArgumentException("The selected zone has no managed resources.");
            }

            return selected;
        }

        if (resourceIds is not { Length: > 0 })
        {
            throw new ArgumentException("Select a zone or one or more exact resources.");
        }

        return ResolveResourceIds(available, resourceIds);
    }

    private static ScopeResourceRevision[] ResolveResourceIds(
        IReadOnlyList<ScopeResourceRevision> available,
        IReadOnlyList<string> resourceIds)
    {
        var byId = available.ToDictionary(resource => resource.ResourceId, StringComparer.Ordinal);
        var selected = new ScopeResourceRevision[resourceIds.Count];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < resourceIds.Count; index++)
        {
            var resourceId = resourceIds[index];
            if (resourceId is null || !seen.Add(resourceId) || !byId.TryGetValue(resourceId, out var resource))
            {
                throw new ArgumentException("The scope contains an unknown or duplicate resource.");
            }

            selected[index] = resource;
        }

        return selected;
    }

    private static ServiceReply ResourceDenied() =>
        new(CurrentProtocolVersion, "denied", null, "resource_unavailable",
            Decision: new AuthorizationDecision(false, "resource_unavailable", 0));

    private static ServiceReply Error(string error) => new(CurrentProtocolVersion, null, null, error);
}
