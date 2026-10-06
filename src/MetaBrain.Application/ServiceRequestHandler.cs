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
    string? SessionToken = null,
    string? SessionId = null,
    string? AccessOperation = null,
    long? ResourceRevision = null,
    string? DestinationResourceId = null,
    long? DestinationRevision = null,
    decimal? EstimatedCostUsd = null,
    long? InputTokens = null,
    long? OutputTokens = null,
    string? CatalogId = null,
    string? Label = null,
    string? Description = null,
    string? Query = null);

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
    AgentSessionSnapshot? AgentSession = null,
    AgentSessionSnapshot[]? AgentSessions = null,
    string? SessionId = null,
    OwnerCatalogPreview? CatalogPreview = null,
    OwnerCatalogEntry[]? CatalogEntries = null,
    PublishedCatalogEntry[]? PublishedCatalog = null,
    string? CatalogId = null);


public sealed class ServiceRequestHandler
{
    public const int CurrentProtocolVersion = 1;
    public const string ResourceReadOperation = "resource.read";
    public const string SourceReadOperation = "source.read";
    public const string ResourceWriteOperation = "resource.write";
    public const string OwnerScopePreviewOperation = "owner.scope.preview";
    public const string OwnerScopeIssueOperation = "owner.scope.issue";
    public const string OwnerScopeListOperation = "owner.scope.list";
    public const string OwnerScopeCollectionSetOperation = "owner.scope.collection.set";
    public const string OwnerScopeCollectionListOperation = "owner.scope.collection.list";
    public const string OwnerCatalogPreviewOperation = "owner.catalog.preview";
    public const string OwnerCatalogPublishOperation = "owner.catalog.publish";
    public const string OwnerCatalogListOperation = "owner.catalog.list";
    public const string OwnerCatalogWithdrawOperation = "owner.catalog.withdraw";
    public const string CatalogListOperation = "catalog.list";
    public const string CatalogQueryOperation = "catalog.query";
    public const string AgentRedeemOperation = "agent.session.redeem";
    public const string AgentSessionInspectOperation = "agent.session.inspect";
    public const string AgentAccessAuthorizeOperation = "agent.access.authorize";
    public const string AgentResourceReadOperation = "agent.resource.read";
    public const string OwnerSessionListOperation = "owner.sessions";
    public const string OwnerSessionRevokeOperation = "owner.session.revoke";
    private readonly GrantAuthority _grantAuthority;
    private readonly IVaultLifecycle _vault;
    private readonly OwnerScopeGrantAuthority _ownerScopeGrants;
    private readonly AgentSessionAuthority _agentSessions;
    private readonly OwnerCatalogAuthority _ownerCatalog;
    private readonly ICatalogProjectionStore? _projections;

    public ServiceRequestHandler(
        GrantAuthority grantAuthority,
        IVaultLifecycle vault,
        OwnerScopeGrantAuthority ownerScopeGrants,
        AgentSessionAuthority? agentSessions = null,
        OwnerCatalogAuthority? ownerCatalog = null,
        ICatalogProjectionStore? projections = null)
    {
        _grantAuthority = grantAuthority ?? throw new ArgumentNullException(nameof(grantAuthority));
        _vault = vault ?? throw new ArgumentNullException(nameof(vault));
        _ownerScopeGrants = ownerScopeGrants ?? throw new ArgumentNullException(nameof(ownerScopeGrants));
        _agentSessions = agentSessions ?? new AgentSessionAuthority();
        _ownerCatalog = ownerCatalog ?? new OwnerCatalogAuthority();
        _projections = projections;
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

        if (string.Equals(request.Operation, AgentResourceReadOperation, StringComparison.Ordinal))
        {
            return HandleAgentResourceRead(context, request, operation);
        }
        if (string.Equals(request.Operation, AgentAccessAuthorizeOperation, StringComparison.Ordinal))
        {
            return HandleAgentAccessAuthorize(context, request, operation);
        }

        if (string.Equals(request.Operation, CatalogListOperation, StringComparison.Ordinal))
        {
            if (HasCatalogDiscoveryExtras(request))
            {
                return CatalogDenied();
            }

            return ServeCatalogList();
        }

        if (string.Equals(request.Operation, CatalogQueryOperation, StringComparison.Ordinal))
        {
            return ServeCatalogQuery(request.Query, HasCatalogDiscoveryExtras(request, allowQuery: true));
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

        if (string.Equals(request.Operation, OwnerCatalogPreviewOperation, StringComparison.Ordinal))
        {
            return HandleCatalogPreview(context, request, operation);
        }

        if (string.Equals(request.Operation, OwnerCatalogPublishOperation, StringComparison.Ordinal))
        {
            return HandleCatalogPublish(context, request, operation);
        }

        if (string.Equals(request.Operation, OwnerCatalogListOperation, StringComparison.Ordinal))
        {
            return HandleCatalogList(context, operation);
        }

        if (string.Equals(request.Operation, OwnerCatalogWithdrawOperation, StringComparison.Ordinal))
        {
            return HandleCatalogWithdraw(context, request, operation);
        }

        if (string.Equals(request.Operation, OwnerSessionListOperation, StringComparison.Ordinal))
        {
            return HandleOwnerSessionList(request, operation);
        }

        if (string.Equals(request.Operation, OwnerSessionRevokeOperation, StringComparison.Ordinal))
        {
            return HandleOwnerSessionRevoke(request, operation);
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

    private ServiceReply HandleAgentResourceRead(
        AuthenticatedContext context,
        ServiceRequest request,
        IVaultOperation? operation)
    {
        if (context.Kind != PrincipalKind.Agent || operation is null ||
            HasAgentResourceReadExtras(request) ||
            request.AccessOperation is not (ResourceReadOperation or SourceReadOperation))
        {
            return ResourceDenied();
        }

        byte[]? snapshot = null;
        try
        {
            var beforeRead = operation.LoadScopeGrantState();
            var decision = _agentSessions.AuthorizeSession(
                request.SessionToken,
                beforeRead.UnlockEpoch,
                beforeRead.PolicyGeneration,
                new AgentAccessRequest(request.AccessOperation, request.ResourceId, request.ResourceRevision));
            if (!decision.Allowed || request.ResourceId is null)
            {
                return ResourceDenied();
            }

            snapshot = operation.ReadContent(request.ResourceId, request.ResourceRevision);
            var beforeServe = operation.LoadScopeGrantState();
            var recheck = _agentSessions.AuthorizeSession(
                request.SessionToken,
                beforeServe.UnlockEpoch,
                beforeServe.PolicyGeneration,
                new AgentAccessRequest(request.AccessOperation, request.ResourceId, request.ResourceRevision));
            if (!recheck.Allowed)
            {
                return ResourceDenied();
            }

            operation.ValidateRegistration(request.ResourceId, request.ResourceRevision);
            return new ServiceReply(
                CurrentProtocolVersion, "content", null, null,
                Decision: recheck,
                PolicyGeneration: recheck.PolicyGeneration,
                ContentBase64: Convert.ToBase64String(snapshot),
                ContentType: "application/octet-stream");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ResourceDenied();
        }
        finally
        {
            if (snapshot is { Length: > 0 })
            {
                CryptographicOperations.ZeroMemory(snapshot);
            }
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

    private ServiceReply HandleCatalogPreview(
        AuthenticatedContext context, ServiceRequest request, IVaultOperation? operation)
    {
        if (operation is null)
        {
            return Error("vault_locked");
        }

        try
        {
            var preview = _ownerCatalog.Preview(
                context, request.CatalogId, request.Label, request.Description,
                request.ResourceId, operation.ListResources(), operation);
            return new ServiceReply(CurrentProtocolVersion, "catalog_preview", "owner", null,
                CatalogPreview: preview, CatalogId: preview.CatalogId);
        }
        catch (OwnerCatalogValidationException ex)
        {
            return Error(ex.Code);
        }
        catch (InvalidDataException)
        {
            return Error("catalog_state_unavailable");
        }
        catch (IOException)
        {
            return Error("catalog_state_unavailable");
        }
    }

    private ServiceReply HandleCatalogPublish(
        AuthenticatedContext context, ServiceRequest request, IVaultOperation? operation)
    {
        if (operation is null)
        {
            return Error("vault_locked");
        }

        try
        {
            var liveResources = operation.ListResources();
            var published = _ownerCatalog.Publish(
                context, request.CatalogId, request.Label, request.Description,
                request.ResourceId, liveResources, operation);
            RefreshProjection(operation);
            return new ServiceReply(CurrentProtocolVersion, "catalog_published", "owner", null,
                CatalogEntries: new[] { published }, CatalogId: published.CatalogId);
        }
        catch (OwnerCatalogValidationException ex)
        {
            return Error(ex.Code);
        }
        catch (InvalidDataException)
        {
            return Error("catalog_state_unavailable");
        }
        catch (IOException)
        {
            return Error("catalog_state_unavailable");
        }
    }

    private ServiceReply HandleCatalogList(AuthenticatedContext context, IVaultOperation? operation)
    {
        if (operation is null)
        {
            return Error("vault_locked");
        }

        try
        {
            var entries = _ownerCatalog.List(context, operation).ToArray();
            return new ServiceReply(CurrentProtocolVersion, "catalog_entries", "owner", null,
                CatalogEntries: entries);
        }
        catch (InvalidDataException)
        {
            return Error("catalog_state_unavailable");
        }
        catch (IOException)
        {
            return Error("catalog_state_unavailable");
        }
    }

    private ServiceReply HandleCatalogWithdraw(
        AuthenticatedContext context, ServiceRequest request, IVaultOperation? operation)
    {
        if (operation is null)
        {
            return Error("vault_locked");
        }

        try
        {
            var withdrawn = _ownerCatalog.Withdraw(context, request.CatalogId, operation);
            RefreshProjection(operation);
            return new ServiceReply(CurrentProtocolVersion, "catalog_withdrawn", "owner", null,
                CatalogId: withdrawn);
        }
        catch (OwnerCatalogValidationException ex)
        {
            return Error(ex.Code);
        }
        catch (InvalidDataException)
        {
            return Error("catalog_state_unavailable");
        }
        catch (IOException)
        {
            return Error("catalog_state_unavailable");
        }
    }

    private void RefreshProjection(IVaultOperation operation)
    {
        if (_projections is null)
        {
            return;
        }

        var live = OwnerCatalogAuthority.ValidateAndNormalizeState(operation.LoadCatalogState()).Entries;
        var published = live
            .Select(OwnerCatalogAuthority.ToPublished)
            .ToArray();
        _projections.SaveProjections(published);
    }

    private ServiceReply ServeCatalogList()
    {
        try
        {
            var entries = _projections is null
                ? Array.Empty<PublishedCatalogEntry>()
                : OwnerCatalogAuthority.NormalizeProjections(_projections.LoadProjections());
            return new ServiceReply(CurrentProtocolVersion, "catalog", null, null,
                PublishedCatalog: entries);
        }
        catch (InvalidDataException)
        {
            return CatalogDenied();
        }
        catch (ArgumentException)
        {
            return CatalogDenied();
        }
        catch (IOException)
        {
            return CatalogDenied();
        }
    }

    private ServiceReply ServeCatalogQuery(string? query, bool hasExtras)
    {
        if (hasExtras || string.IsNullOrWhiteSpace(query) || query!.Length > 256)
        {
            return CatalogDenied();
        }

        var needle = query.Trim();
        try
        {
            var entries = _projections is null
                ? Array.Empty<PublishedCatalogEntry>()
                : OwnerCatalogAuthority.NormalizeProjections(_projections.LoadProjections());
            var matches = entries
                .Where(entry =>
                    entry.CatalogId.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                    entry.Label.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                    entry.Description.Contains(needle, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            return matches.Length == 0
                ? CatalogDenied()
                : new ServiceReply(CurrentProtocolVersion, "catalog", null, null,
                    PublishedCatalog: matches);
        }
        catch (InvalidDataException)
        {
            return CatalogDenied();
        }
        catch (ArgumentException)
        {
            return CatalogDenied();
        }
        catch (IOException)
        {
            return CatalogDenied();
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

        if (HasOwnerOnlyFields(request, allowGrantToken: true))
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
            var state = operation.LoadScopeGrantState();
            lookup = _agentSessions.LookupSession(request.SessionToken, state.UnlockEpoch, state.PolicyGeneration);
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
            _ => Error("session_unknown")
        };
    }

    private ServiceReply HandleAgentAccessAuthorize(
        AuthenticatedContext context,
        ServiceRequest request,
        IVaultOperation? operation)
    {
        if (context.Kind != PrincipalKind.Agent)
        {
            return Error("forbidden");
        }

        if (operation is null)
        {
            return Error("vault_locked");
        }

        if (HasOwnerOnlyFields(request, allowAccessFields: true))
        {
            return Error("forbidden");
        }

        try
        {
            var state = operation.LoadScopeGrantState();
            var decision = _agentSessions.AuthorizeSession(
                request.SessionToken,
                state.UnlockEpoch,
                state.PolicyGeneration,
                new AgentAccessRequest(
                    request.AccessOperation,
                    request.ResourceId,
                    request.ResourceRevision,
                    request.DestinationResourceId,
                    request.DestinationRevision,
                    request.Provider,
                    request.Model,
                    request.EstimatedCostUsd,
                    request.InputTokens,
                    request.OutputTokens));
            return new ServiceReply(
                CurrentProtocolVersion,
                "authorization_decision",
                "agent",
                null,
                Decision: decision,
                PolicyGeneration: state.PolicyGeneration);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return Error("authorization_unavailable");
        }
    }

    private ServiceReply HandleOwnerSessionList(ServiceRequest request, IVaultOperation? operation)
    {
        if (operation is null)
        {
            return Error("vault_locked");
        }

        if (HasSessionAdministrationExtras(request, allowSessionId: false))
        {
            return Error("invalid_request");
        }

        try
        {
            var state = operation.LoadScopeGrantState();
            var sessions = _agentSessions.ListSessions(state.UnlockEpoch, state.PolicyGeneration);
            return new ServiceReply(
                CurrentProtocolVersion,
                "sessions_listed",
                "owner",
                null,
                PolicyGeneration: state.PolicyGeneration,
                AgentSessions: sessions);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return Error("session_unavailable");
        }
    }

    private ServiceReply HandleOwnerSessionRevoke(ServiceRequest request, IVaultOperation? operation)
    {
        if (operation is null)
        {
            return Error("vault_locked");
        }

        if (HasSessionAdministrationExtras(request, allowSessionId: true))
        {
            return Error("invalid_request");
        }

        try
        {
            var state = operation.LoadScopeGrantState();
            var result = _agentSessions.RevokeSession(
                request.SessionId, state.UnlockEpoch, state.PolicyGeneration);
            return result.Status switch
            {
                AgentSessionRevocationStatus.Revoked =>
                    new ServiceReply(
                        CurrentProtocolVersion,
                        "session_revoked",
                        "owner",
                        null,
                        PolicyGeneration: state.PolicyGeneration,
                        SessionId: request.SessionId),
                AgentSessionRevocationStatus.Expired => Error("session_expired"),
                AgentSessionRevocationStatus.Superseded => Error("session_superseded"),
                _ => Error("session_unknown")
            };
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return Error("session_unavailable");
        }
    }

    private static long CurrentEpoch(IVaultOperation operation) =>
        operation.LoadScopeGrantState().UnlockEpoch;

    private static bool HasOwnerOnlyFields(
        ServiceRequest request,
        bool allowAccessFields = false,
        bool allowGrantToken = false) =>
        request.Passphrase is not null || request.RecoveryCode is not null ||
        request.ResourceIds is not null || request.Operations is not null ||
        request.DestinationResourceIds is not null || request.ExpiresAtUtc is not null ||
        request.MaximumCostUsd is not null || request.PreviewId is not null ||
        request.CollectionId is not null || request.ZoneId is not null ||
        request.ContentBase64 is not null || (!allowGrantToken && request.Token is not null) ||
        request.SessionId is not null || request.CatalogId is not null ||
        request.Label is not null || request.Description is not null || request.Query is not null ||
        (!allowAccessFields &&
         (request.AccessOperation is not null || request.ResourceId is not null ||
          request.ResourceRevision is not null || request.DestinationResourceId is not null ||
          request.DestinationRevision is not null || request.Provider is not null ||
          request.Model is not null || request.EstimatedCostUsd is not null ||
          request.InputTokens is not null || request.OutputTokens is not null));
    private static bool HasAgentResourceReadExtras(ServiceRequest request) =>
        request.ZoneId is not null ||
        request.Passphrase is not null || request.RecoveryCode is not null ||
        request.ContentBase64 is not null || request.ResourceIds is not null ||
        request.Operations is not null || request.DestinationResourceIds is not null ||
        request.ExpiresAtUtc is not null || request.Provider is not null ||
        request.Model is not null || request.MaximumCostUsd is not null ||
        request.PreviewId is not null || request.CollectionId is not null ||
        request.Token is not null || request.SessionId is not null ||
        request.DestinationResourceId is not null || request.DestinationRevision is not null ||
        request.EstimatedCostUsd is not null || request.InputTokens is not null ||
        request.OutputTokens is not null ||
        request.CatalogId is not null || request.Label is not null ||
        request.Description is not null || request.Query is not null;
    private static bool HasCatalogDiscoveryExtras(ServiceRequest request, bool allowQuery = false) =>
        request.ZoneId is not null ||
        request.Passphrase is not null || request.RecoveryCode is not null ||
        request.ContentBase64 is not null || request.ResourceIds is not null ||
        request.Operations is not null || request.DestinationResourceIds is not null ||
        request.ExpiresAtUtc is not null || request.Provider is not null ||
        request.Model is not null || request.MaximumCostUsd is not null ||
        request.PreviewId is not null || request.CollectionId is not null ||
        request.Token is not null || request.SessionId is not null ||
        request.SessionToken is not null || request.ResourceId is not null ||
        request.ResourceRevision is not null || request.AccessOperation is not null ||
        request.DestinationResourceId is not null || request.DestinationRevision is not null ||
        request.EstimatedCostUsd is not null || request.InputTokens is not null ||
        request.OutputTokens is not null || request.CatalogId is not null ||
        request.Label is not null || request.Description is not null ||
        (!allowQuery && request.Query is not null);
    private static bool HasSessionAdministrationExtras(ServiceRequest request, bool allowSessionId) =>
        request.ResourceId is not null || request.ZoneId is not null ||
        request.Passphrase is not null || request.RecoveryCode is not null ||
        request.ContentBase64 is not null || request.ResourceIds is not null ||
        request.Operations is not null || request.DestinationResourceIds is not null ||
        request.ExpiresAtUtc is not null || request.Provider is not null ||
        request.Model is not null || request.MaximumCostUsd is not null ||
        request.PreviewId is not null || request.CollectionId is not null ||
        request.Token is not null || request.SessionToken is not null ||
        request.AccessOperation is not null || request.ResourceRevision is not null ||
        request.DestinationResourceId is not null || request.DestinationRevision is not null ||
        request.EstimatedCostUsd is not null || request.InputTokens is not null ||
        request.OutputTokens is not null || request.CatalogId is not null ||
        request.Label is not null || request.Description is not null || request.Query is not null ||
        (!allowSessionId && request.SessionId is not null);
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

    private static ServiceReply CatalogDenied() =>
        new(CurrentProtocolVersion, "no_match", null, "catalog_no_match");

    private static ServiceReply Error(string error) => new(CurrentProtocolVersion, null, null, error);
}
