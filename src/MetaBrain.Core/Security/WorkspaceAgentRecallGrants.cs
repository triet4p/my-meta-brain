using System.Security.Cryptography;
using MetaBrain.Core.Domain;
using MetaBrain.Core.Index;

namespace MetaBrain.Core.Security;

public sealed record WorkspaceAgentRecallFileSelection(string FileId, long ContentRevision, string FullSha256);

public sealed record WorkspaceAgentRecallSelection(
    string[] Operations,
    string[] AnnotationIds,
    WorkspaceAgentRecallFileSelection[] Files,
    string[] RelationIds);

public sealed record WorkspaceAgentRecallAnnotationPin(
    string AnnotationId,
    long Revision,
    string AnchorId,
    string FileId,
    long ContentRevision,
    long Offset,
    long Length,
    string Sha256);

public sealed record WorkspaceAgentRecallFilePin(
    string FileId,
    long ContentRevision,
    long Length,
    string Sha256,
    long AccessLifecycleGeneration);

public sealed record WorkspaceAgentRecallScope(
    string WorkspaceId,
    long TreeRevision,
    string[] Operations,
    WorkspaceAgentRecallAnnotationPin[] Annotations,
    WorkspaceAgentRecallFilePin[] Files,
    WorkspaceContextPackRelationPin[] Relations,
    int? IndexSchemaVersion,
    string? IndexGeneration);

public sealed record WorkspaceAgentRecallRelationPreview(Relation Relation, RelationReview Review);

public sealed record WorkspaceAgentRecallGrantPreview(
    string PreviewId,
    WorkspaceAgentRecallScope Scope,
    CognitiveAnnotation[] Annotations,
    WorkspaceAgentRecallRelationPreview[] Relations,
    WorkspacePassageIndexStatus? PassageIndex,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    long PolicyGeneration,
    long WorkspaceScopeGeneration,
    long UnlockEpoch,
    DateTimeOffset PreviewExpiresAtUtc);

public sealed record WorkspaceAgentRecallGrantSummary(
    string GrantId,
    WorkspaceAgentRecallScope Scope,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    long PolicyGeneration,
    int SemanticsVersion,
    long SemanticsGeneration,
    DateTimeOffset? RevokedAtUtc);

/// <summary>Owner-approved frozen authority for selected workspace recall operations.</summary>
public sealed class WorkspaceAgentRecallGrantAuthority
{
    public const int CurrentScopeSemanticsVersion = 1;
    public const string WorkspaceAgentRecallOperation = "workspace.agent.recall";
    public const string CognitiveAnnotationReadOperation = "workspace.cognitive.annotation.read";
    public const string HybridSearchOperation = "workspace.hybrid.search";
    public const string RelationRecallOperation = "workspace.relation.recall";
    public const int MaximumAnnotations = 100;
    public const int MaximumFiles = 100;
    public const int MaximumRelations = 100;
    public const int MaximumOutputBytes = 64 * 1024;

    private static readonly TimeSpan PreviewLifetime = TimeSpan.FromMinutes(5);
    private const int MaximumPendingPreviews = 1024;
    private static readonly HashSet<string> AllowedOperations = new(StringComparer.Ordinal)
    {
        CognitiveAnnotationReadOperation,
        HybridSearchOperation,
        RelationRecallOperation
    };

    private readonly object _gate = new();
    private readonly Dictionary<string, PendingPreview> _previews = new(StringComparer.Ordinal);

    private sealed record PendingPreview(WorkspaceAgentRecallGrantPreview Value, DateTimeOffset ExpiresAtUtc);

    public WorkspaceAgentRecallGrantPreview Preview(
        AuthenticatedContext actor,
        DateTimeOffset expiresAtUtc,
        IWorkspaceContextPackGrantPersistence persistence,
        Func<OwnerScopeGrantState, WorkspaceSnapshot?, IManagedResourceStore, IWorkspacePersistence,
            (WorkspaceAgentRecallScope Scope, CognitiveAnnotation[] Annotations,
                WorkspaceAgentRecallRelationPreview[] Relations, WorkspacePassageIndexStatus? PassageIndex)> resolve)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(persistence);
        ArgumentNullException.ThrowIfNull(resolve);
        EnsureOwner(actor);
        if (expiresAtUtc.Offset != TimeSpan.Zero || expiresAtUtc <= DateTimeOffset.UtcNow)
        {
            throw new ArgumentException("Grant expiry must be a future UTC instant.", nameof(expiresAtUtc));
        }

        lock (_gate)
        {
            return persistence.ReadWorkspaceContextPackGrantSnapshot((state, workspace, store, workspaceStore) =>
            {
                state = OwnerScopeGrantAuthority.ValidateAndMigrateState(state);
                var resolved = resolve(state, workspace, store, workspaceStore);
                var scope = NormalizeScope(resolved.Scope);
                if (!MatchesWorkspaceMetadata(scope, workspace) ||
                    !DetailsMatch(scope, resolved.Annotations, resolved.Relations, resolved.PassageIndex))
                {
                    throw new WorkspaceAgentRecallGrantRefusalException("request_stale");
                }

                return StorePreviewLocked(state, scope, resolved.Annotations, resolved.Relations,
                    resolved.PassageIndex, expiresAtUtc);
            });
        }
    }

    public OwnerScopeGrantIssue Issue(
        AuthenticatedContext actor,
        string? previewId,
        IWorkspaceContextPackGrantPersistence persistence,
        Func<WorkspaceAgentRecallScope, IManagedResourceStore, IWorkspacePersistence, bool> isCurrent)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(persistence);
        ArgumentNullException.ThrowIfNull(isCurrent);
        EnsureOwner(actor);
        if (!Guid.TryParseExact(previewId, "N", out _))
        {
            throw new WorkspaceAgentRecallGrantRefusalException("scope_preview_unavailable");
        }

        lock (_gate)
        {
            if (!_previews.TryGetValue(previewId!, out var pending) ||
                pending.ExpiresAtUtc <= DateTimeOffset.UtcNow ||
                pending.Value.ExpiresAtUtc <= DateTimeOffset.UtcNow)
            {
                _previews.Remove(previewId!);
                throw new WorkspaceAgentRecallGrantRefusalException("scope_preview_unavailable");
            }

            var preview = pending.Value;
            var tokenBytes = RandomNumberGenerator.GetBytes(32);
            var verifierBytes = SHA256.HashData(tokenBytes);
            try
            {
                var issued = persistence.UpdateWorkspaceContextPackGrantState((state, workspace, store, workspaceStore) =>
                {
                    state = OwnerScopeGrantAuthority.ValidateAndMigrateState(state);
                    if (state.PolicyGeneration != preview.PolicyGeneration ||
                        state.WorkspaceScopeGeneration != preview.WorkspaceScopeGeneration ||
                        state.UnlockEpoch != preview.UnlockEpoch ||
                        !MatchesWorkspaceMetadata(preview.Scope, workspace) ||
                        !isCurrent(preview.Scope, store, workspaceStore))
                    {
                        throw new WorkspaceAgentRecallGrantRefusalException("request_stale");
                    }

                    if (state.Grants.Length >= 10_000 || state.WorkspaceScopeGeneration == long.MaxValue)
                    {
                        throw new IOException("The owner scope grant state cannot accept another grant.");
                    }

                    var grant = new PersistedScopeGrant(
                        Guid.NewGuid().ToString("N"),
                        Convert.ToBase64String(verifierBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_'),
                        Array.Empty<ScopeResourceRevision>(),
                        (string[])preview.Scope.Operations.Clone(),
                        Array.Empty<ScopeResourceRevision>(),
                        DateTimeOffset.UtcNow,
                        preview.ExpiresAtUtc,
                        null,
                        state.PolicyGeneration,
                        state.UnlockEpoch,
                        WorkspaceAgentRecallScope: CloneScope(preview.Scope),
                        AgentRecallSemanticsVersion: CurrentScopeSemanticsVersion,
                        AgentRecallSemanticsGeneration: 1);
                    var nextState = state with
                    {
                        SchemaVersion = OwnerScopeGrantState.CurrentSchemaVersion,
                        Grants = state.Grants.Append(grant).ToArray(),
                        WorkspaceScopeGeneration = state.WorkspaceScopeGeneration + 1
                    };
                    return (nextState, new OwnerScopeGrantIssue(
                        "mb1_" + Convert.ToBase64String(tokenBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_'),
                        CloneGrant(grant)));
                });
                _previews.Remove(previewId!);
                return issued;
            }
            catch (WorkspaceAgentRecallGrantRefusalException)
            {
                _previews.Remove(previewId!);
                throw;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(tokenBytes);
                CryptographicOperations.ZeroMemory(verifierBytes);
            }
        }
    }

    public void Reject(AuthenticatedContext actor, string? previewId)
    {
        ArgumentNullException.ThrowIfNull(actor);
        EnsureOwner(actor);
        if (!Guid.TryParseExact(previewId, "N", out _))
        {
            throw new WorkspaceAgentRecallGrantRefusalException("scope_preview_unavailable");
        }

        lock (_gate)
        {
            if (!_previews.TryGetValue(previewId!, out var pending) || pending.ExpiresAtUtc <= DateTimeOffset.UtcNow)
            {
                _previews.Remove(previewId!);
                throw new WorkspaceAgentRecallGrantRefusalException("scope_preview_unavailable");
            }

            _previews.Remove(previewId!);
        }
    }

    public PersistedScopeGrant Revoke(
        AuthenticatedContext actor,
        string? grantId,
        IWorkspaceContextPackGrantPersistence persistence)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(persistence);
        EnsureOwner(actor);
        if (!Guid.TryParseExact(grantId, "N", out _))
        {
            throw new WorkspaceAgentRecallGrantRefusalException("workspace_grant_unavailable");
        }

        return persistence.UpdateWorkspaceContextPackGrantState((state, _, _, _) =>
        {
            state = OwnerScopeGrantAuthority.ValidateAndMigrateState(state);
            var index = Array.FindIndex(state.Grants, grant =>
                grant.GrantId == grantId && grant.WorkspaceAgentRecallScope is not null);
            if (index < 0)
            {
                throw new WorkspaceAgentRecallGrantRefusalException("workspace_grant_unavailable");
            }

            var grant = state.Grants[index];
            if (grant.RevokedAtUtc is not null)
            {
                return (state, CloneGrant(grant));
            }

            if (state.WorkspaceScopeGeneration == long.MaxValue || grant.AgentRecallSemanticsGeneration == long.MaxValue)
            {
                throw new IOException("The workspace scope generation is exhausted.");
            }

            var revoked = grant with
            {
                RevokedAtUtc = DateTimeOffset.UtcNow,
                AgentRecallSemanticsGeneration = grant.AgentRecallSemanticsGeneration + 1
            };
            var grants = (PersistedScopeGrant[])state.Grants.Clone();
            grants[index] = revoked;
            var nextState = state with
            {
                SchemaVersion = OwnerScopeGrantState.CurrentSchemaVersion,
                Grants = grants,
                WorkspaceScopeGeneration = state.WorkspaceScopeGeneration + 1
            };
            return (nextState, CloneGrant(revoked));
        });
    }

    public WorkspaceAgentRecallGrantSummary[] List(
        AuthenticatedContext actor,
        IWorkspaceContextPackGrantPersistence persistence)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(persistence);
        EnsureOwner(actor);
        return persistence.ReadWorkspaceContextPackGrantSnapshot((state, _, _, _) =>
            OwnerScopeGrantAuthority.ValidateAndMigrateState(state).Grants
                .Where(grant => grant.WorkspaceAgentRecallScope is not null)
                .OrderBy(grant => grant.GrantId, StringComparer.Ordinal)
                .Select(grant => new WorkspaceAgentRecallGrantSummary(
                    grant.GrantId,
                    CloneScope(grant.WorkspaceAgentRecallScope!),
                    grant.CreatedAtUtc,
                    grant.ExpiresAtUtc,
                    grant.PolicyGeneration,
                    grant.AgentRecallSemanticsVersion,
                    grant.AgentRecallSemanticsGeneration,
                    grant.RevokedAtUtc))
                .ToArray());
    }

    public void ClearPreviews()
    {
        lock (_gate)
        {
            _previews.Clear();
        }
    }

    public static bool IsActiveGrant(
        OwnerScopeGrantState state,
        PersistedScopeGrant grant,
        int semanticsVersion,
        long semanticsGeneration) =>
        grant.WorkspaceAgentRecallScope is not null && grant.RevokedAtUtc is null &&
        grant.AgentRecallSemanticsVersion == semanticsVersion &&
        grant.AgentRecallSemanticsGeneration == semanticsGeneration &&
        grant.ExpiresAtUtc > DateTimeOffset.UtcNow &&
        grant.PolicyGeneration == state.PolicyGeneration && grant.UnlockEpoch == state.UnlockEpoch;

    public static WorkspaceAgentRecallScope NormalizeScope(WorkspaceAgentRecallScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (!WorkspaceFolderGrantEvaluator.IsNodeId(scope.WorkspaceId) || scope.TreeRevision < 1 ||
            scope.Operations is null || scope.Operations.Length is < 1 or > 3 ||
            scope.Annotations is null || scope.Annotations.Length > MaximumAnnotations ||
            scope.Files is null || scope.Files.Length > MaximumFiles ||
            scope.Relations is null || scope.Relations.Length > MaximumRelations ||
            scope.Operations.Any(operation => operation is null) ||
            scope.Annotations.Any(annotation => annotation is null) ||
            scope.Files.Any(file => file is null) || scope.Relations.Any(relation => relation is null))
        {
            throw new ArgumentException("The workspace agent-recall scope is invalid.", nameof(scope));
        }

        var operations = scope.Operations.Order(StringComparer.Ordinal).ToArray();
        if (operations.Any(operation => !AllowedOperations.Contains(operation)) ||
            operations.Distinct(StringComparer.Ordinal).Count() != operations.Length ||
            HasSelection(scope, CognitiveAnnotationReadOperation) != (scope.Annotations.Length > 0) ||
            HasSelection(scope, HybridSearchOperation) != (scope.Files.Length > 0) ||
            HasSelection(scope, RelationRecallOperation) != (scope.Relations.Length > 0) ||
            HasSelection(scope, HybridSearchOperation) !=
                (scope.IndexSchemaVersion is > 0 && IsSha256(scope.IndexGeneration)) ||
            (!HasSelection(scope, HybridSearchOperation) &&
                (scope.IndexSchemaVersion is not null || scope.IndexGeneration is not null)))
        {
            throw new ArgumentException("The workspace agent-recall scope does not match its selected operations.", nameof(scope));
        }

        var annotations = scope.Annotations.OrderBy(annotation => annotation.AnnotationId, StringComparer.Ordinal).ToArray();
        var annotationIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var annotation in annotations)
        {
            if (!DomainIdentifiers.IsCognitiveAnnotationId(annotation.AnnotationId) ||
                !annotationIds.Add(annotation.AnnotationId) || annotation.Revision < 1 ||
                !WorkspaceFolderGrantEvaluator.IsNodeId(annotation.AnchorId) ||
                !WorkspaceFolderGrantEvaluator.IsNodeId(annotation.FileId) || annotation.ContentRevision < 1 ||
                annotation.Offset < 0 || annotation.Length < 1 || !IsSha256(annotation.Sha256))
            {
                throw new ArgumentException("The workspace agent-recall annotation pins are invalid.", nameof(scope));
            }
        }

        var files = scope.Files.OrderBy(file => file.FileId, StringComparer.Ordinal).ToArray();
        var fileIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            if (!WorkspaceFolderGrantEvaluator.IsNodeId(file.FileId) || !fileIds.Add(file.FileId) ||
                file.ContentRevision < 1 || file.Length < 0 || file.AccessLifecycleGeneration < 0 || !IsSha256(file.Sha256))
            {
                throw new ArgumentException("The workspace agent-recall file pins are invalid.", nameof(scope));
            }
        }

        var relations = scope.Relations.OrderBy(relation => relation.RelationId, StringComparer.Ordinal).ToArray();
        var relationIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var relation in relations)
        {
            if (!DomainIdentifiers.IsRelationId(relation.RelationId) || !relationIds.Add(relation.RelationId) ||
                relation.Revision < 1 || relation.State != RelationState.Accepted.ToString() ||
                !ValidRelationEndpoint(relation.From) || !ValidRelationEndpoint(relation.To))
            {
                throw new ArgumentException("The workspace agent-recall relation pins are invalid.", nameof(scope));
            }
        }

        return scope with
        {
            Operations = operations,
            Annotations = annotations,
            Files = files,
            Relations = relations
        };
    }

    public static WorkspaceAgentRecallScope CloneScope(WorkspaceAgentRecallScope scope) => scope with
    {
        Operations = (string[])scope.Operations.Clone(),
        Annotations = scope.Annotations.Select(annotation => annotation with { }).ToArray(),
        Files = scope.Files.Select(file => file with { }).ToArray(),
        Relations = scope.Relations.Select(relation => relation with
        {
            From = relation.From with { },
            To = relation.To with { }
        }).ToArray()
    };

    public static bool MatchesWorkspaceMetadata(WorkspaceAgentRecallScope scope, WorkspaceSnapshot? workspace) =>
        workspace is not null && workspace.WorkspaceId == scope.WorkspaceId &&
        workspace.Revision == scope.TreeRevision && workspace.Nodes is not null;

    public static bool ScopeMatches(WorkspaceAgentRecallScope left, WorkspaceAgentRecallScope right)
    {
        if (left.WorkspaceId != right.WorkspaceId || left.TreeRevision != right.TreeRevision ||
            left.IndexSchemaVersion != right.IndexSchemaVersion || left.IndexGeneration != right.IndexGeneration ||
            !left.Operations.SequenceEqual(right.Operations, StringComparer.Ordinal) ||
            left.Annotations.Length != right.Annotations.Length || left.Files.Length != right.Files.Length ||
            left.Relations.Length != right.Relations.Length)
        {
            return false;
        }

        return left.Annotations.SequenceEqual(right.Annotations) && left.Files.SequenceEqual(right.Files) &&
            left.Relations.SequenceEqual(right.Relations);
    }

    private WorkspaceAgentRecallGrantPreview StorePreviewLocked(
        OwnerScopeGrantState state,
        WorkspaceAgentRecallScope scope,
        CognitiveAnnotation[] annotations,
        WorkspaceAgentRecallRelationPreview[] relations,
        WorkspacePassageIndexStatus? passageIndex,
        DateTimeOffset expiresAtUtc)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var expiredId in _previews.Where(pair => pair.Value.ExpiresAtUtc <= now)
                     .Select(pair => pair.Key).ToArray())
        {
            _previews.Remove(expiredId);
        }

        if (_previews.Count >= MaximumPendingPreviews)
        {
            throw new IOException("The workspace agent-recall preview capacity is exhausted.");
        }

        var preview = new WorkspaceAgentRecallGrantPreview(
            Guid.NewGuid().ToString("N"),
            CloneScope(scope),
            annotations.Select(CloneAnnotation).ToArray(),
            relations.Select(CloneRelation).ToArray(),
            passageIndex is null ? null : passageIndex with { },
            now,
            expiresAtUtc,
            state.PolicyGeneration,
            state.WorkspaceScopeGeneration,
            state.UnlockEpoch,
            now + PreviewLifetime);
        _previews.Add(preview.PreviewId, new PendingPreview(preview, preview.PreviewExpiresAtUtc));
        return ClonePreview(preview);
    }

    private static bool DetailsMatch(
        WorkspaceAgentRecallScope scope,
        CognitiveAnnotation[] annotations,
        WorkspaceAgentRecallRelationPreview[] relations,
        WorkspacePassageIndexStatus? passageIndex)
    {
        if (annotations.Length != scope.Annotations.Length || relations.Length != scope.Relations.Length ||
            (HasSelection(scope, HybridSearchOperation) &&
                (passageIndex is null || passageIndex.Stale || passageIndex.WorkspaceRevision != scope.TreeRevision ||
                 passageIndex.SchemaVersion != scope.IndexSchemaVersion || passageIndex.WorkspaceDigest != scope.IndexGeneration ||
                 passageIndex.State is not ("ready" or "partial"))))
        {
            return false;
        }

        return true;
    }

    private static bool HasSelection(WorkspaceAgentRecallScope scope, string operation) =>
        scope.Operations.Contains(operation, StringComparer.Ordinal);

    private static bool ValidRelationEndpoint(WorkspaceContextPackEndpointPin? endpoint) =>
        endpoint is not null && endpoint.EndpointKind is nameof(RelationEndpointKind.WorkspaceFile) or nameof(RelationEndpointKind.WorkspacePassage) &&
        WorkspaceFolderGrantEvaluator.IsNodeId(endpoint.ResourceId) && endpoint.Revision > 0 && IsSha256(endpoint.Sha256) &&
        (endpoint.EndpointKind == nameof(RelationEndpointKind.WorkspacePassage)
            ? endpoint.Offset is >= 0 && endpoint.Length is > 0
            : endpoint.Offset is null && endpoint.Length is null);

    private static CognitiveAnnotation CloneAnnotation(CognitiveAnnotation annotation) => annotation with
    {
        Anchor = annotation.Anchor with { },
        Facets = (CognitiveFacet[])annotation.Facets.Clone(),
        History = annotation.History.Select(version => version with
        {
            State = version.State with { Facets = (CognitiveFacet[])version.State.Facets.Clone() }
        }).ToArray()
    };

    private static WorkspaceAgentRecallRelationPreview CloneRelation(WorkspaceAgentRecallRelationPreview value) => value with
    {
        Relation = value.Relation with
        {
            From = value.Relation.From with { },
            To = value.Relation.To with { },
            History = value.Relation.History is null
                ? null
                : (RelationHistoryAct[])value.Relation.History.Clone()
        },
        Review = value.Review with { }
    };

    private static WorkspaceAgentRecallGrantPreview ClonePreview(WorkspaceAgentRecallGrantPreview preview) => preview with
    {
        Scope = CloneScope(preview.Scope),
        Annotations = preview.Annotations.Select(CloneAnnotation).ToArray(),
        Relations = preview.Relations.Select(CloneRelation).ToArray(),
        PassageIndex = preview.PassageIndex is null ? null : preview.PassageIndex with { }
    };

    private static PersistedScopeGrant CloneGrant(PersistedScopeGrant grant) => grant with
    {
        Resources = grant.Resources.Select(resource => resource with { }).ToArray(),
        Operations = (string[])grant.Operations.Clone(),
        DestinationResources = grant.DestinationResources.Select(resource => resource with { }).ToArray(),
        WorkspaceAgentRecallScope = grant.WorkspaceAgentRecallScope is null
            ? null
            : CloneScope(grant.WorkspaceAgentRecallScope)
    };

    private static bool IsSha256(string? value) => value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void EnsureOwner(AuthenticatedContext actor)
    {
        if (actor.Kind != PrincipalKind.Owner)
        {
            throw new UnauthorizedAccessException("The owner control channel is required.");
        }
    }
}

public sealed class WorkspaceAgentRecallGrantRefusalException(string code)
    : InvalidOperationException("The workspace agent-recall grant operation is unavailable.")
{
    public string Code { get; } = code;
}
