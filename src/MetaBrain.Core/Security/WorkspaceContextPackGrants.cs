using System.Security.Cryptography;
using MetaBrain.Core.Domain;

namespace MetaBrain.Core.Security;

public sealed record WorkspaceContextPackPassageSelection(long Offset, long Length, string Sha256);

public sealed record WorkspaceContextPackFileSelection(
    string FileId,
    long ContentRevision,
    string FullSha256,
    bool IsSeed,
    WorkspaceContextPackPassageSelection[] Passages);

public sealed record WorkspaceContextPackSelection(
    WorkspaceContextPackFileSelection[] Files,
    string[] RelationIds);

public sealed record WorkspaceContextPackPassagePin(
    string FileId,
    long ContentRevision,
    long Offset,
    long Length,
    string Sha256);

public sealed record WorkspaceContextPackFilePin(
    string FileId,
    long ContentRevision,
    long Length,
    string Sha256,
    long AccessLifecycleGeneration,
    bool IsSeed,
    WorkspaceContextPackPassagePin[] Passages);

public sealed record WorkspaceContextPackEndpointPin(
    string ResourceId,
    string EndpointKind,
    long Revision,
    long? Offset,
    long? Length,
    string Sha256);

public sealed record WorkspaceContextPackRelationPin(
    string RelationId,
    long Revision,
    string Kind,
    string State,
    WorkspaceContextPackEndpointPin From,
    WorkspaceContextPackEndpointPin To);

public sealed record WorkspaceContextPackScope(
    string WorkspaceId,
    long TreeRevision,
    WorkspaceContextPackFilePin[] Files,
    WorkspaceContextPackRelationPin[] Relations);

public sealed record WorkspaceContextPackRelationPreview(Relation Relation, RelationReview Review);

public sealed record WorkspaceContextPackGrantPreview(
    string PreviewId,
    WorkspaceContextPackScope Scope,
    WorkspaceContextPackRelationPreview[] Relations,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    long PolicyGeneration,
    long WorkspaceScopeGeneration,
    long UnlockEpoch,
    DateTimeOffset PreviewExpiresAtUtc);

public sealed record WorkspaceContextPackGrantSummary(
    string GrantId,
    WorkspaceContextPackScope Scope,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    long PolicyGeneration,
    int SemanticsVersion,
    long SemanticsGeneration,
    DateTimeOffset? RevokedAtUtc);

public sealed record WorkspaceContextPackGrantSnapshot(OwnerScopeGrantState State, WorkspaceSnapshot? Workspace);

/// <summary>
/// Encrypted grant operations run their callbacks under the vault commit gate.
/// This lets preview/issue/read compare canonical relations, tree metadata and
/// exact original bytes as one consistent observation.
/// </summary>
public interface IWorkspaceContextPackGrantPersistence : IWorkspaceFolderGrantPersistence
{
    T ReadWorkspaceContextPackGrantSnapshot<T>(
        Func<OwnerScopeGrantState, WorkspaceSnapshot?, IManagedResourceStore, IWorkspacePersistence, T> read);

    T UpdateWorkspaceContextPackGrantState<T>(
        Func<OwnerScopeGrantState, WorkspaceSnapshot?, IManagedResourceStore, IWorkspacePersistence,
            (OwnerScopeGrantState State, T Result)> update);

    T ReadWorkspaceContextPackForGrant<T>(
        string grantId,
        int semanticsVersion,
        long semanticsGeneration,
        Func<OwnerScopeGrantState, WorkspaceContextPackScope, WorkspaceSnapshot,
            IManagedResourceStore, IWorkspacePersistence, T> read);
}

/// <summary>Owner approval for a separate, frozen workspace context-pack scope.</summary>
public sealed class WorkspaceContextPackGrantAuthority
{
    public const int CurrentScopeSemanticsVersion = 1;
    public const string WorkspaceContextPackOperation = "workspace.context.pack";
    public const int MaximumFiles = 100;
    public const int MaximumPassages = 500;
    public const int MaximumRelations = 100;

    private static readonly TimeSpan PreviewLifetime = TimeSpan.FromMinutes(5);
    private const int MaximumPendingPreviews = 1024;
    private readonly object _gate = new();
    private readonly Dictionary<string, PendingPreview> _previews = new(StringComparer.Ordinal);

    private sealed record PendingPreview(WorkspaceContextPackGrantPreview Value, DateTimeOffset ExpiresAtUtc);

    public WorkspaceContextPackGrantPreview Preview(
        AuthenticatedContext actor,
        DateTimeOffset expiresAtUtc,
        IWorkspaceContextPackGrantPersistence persistence,
        Func<OwnerScopeGrantState, WorkspaceSnapshot?, IManagedResourceStore, IWorkspacePersistence,
            (WorkspaceContextPackScope Scope, WorkspaceContextPackRelationPreview[] Relations)> resolve)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(persistence);
        ArgumentNullException.ThrowIfNull(resolve);
        EnsureOwner(actor);
        if (expiresAtUtc.Offset != TimeSpan.Zero || expiresAtUtc <= DateTimeOffset.UtcNow)
        {
            throw new ArgumentException("Grant expiry must be a future UTC instant.", nameof(expiresAtUtc));
        }

        // The authority gate is acquired before the vault gate, matching Issue
        // and avoiding a lock-order inversion between concurrent previews.
        lock (_gate)
        {
            return persistence.ReadWorkspaceContextPackGrantSnapshot((state, workspace, store, workspaceStore) =>
            {
                state = OwnerScopeGrantAuthority.ValidateAndMigrateState(state);
                var (untrustedScope, relations) = resolve(state, workspace, store, workspaceStore);
                var scope = NormalizeScope(untrustedScope);
                if (!MatchesWorkspaceMetadata(scope, workspace) ||
                    !VerifyContentPins(scope, workspaceStore) ||
                    !RelationDetailsMatch(scope, relations))
                {
                    throw new WorkspaceContextPackGrantRefusalException("request_stale");
                }

                return StorePreviewLocked(state, scope, relations, expiresAtUtc);
            });
        }
    }

    public OwnerScopeGrantIssue Issue(
        AuthenticatedContext actor,
        string? previewId,
        IWorkspaceContextPackGrantPersistence persistence,
        Func<IManagedResourceStore, IWorkspacePersistence, WorkspaceContextPackScope, bool> relationsAreCurrent)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(persistence);
        ArgumentNullException.ThrowIfNull(relationsAreCurrent);
        EnsureOwner(actor);
        if (!Guid.TryParseExact(previewId, "N", out _))
        {
            throw new WorkspaceContextPackGrantRefusalException("scope_preview_unavailable");
        }

        lock (_gate)
        {
            if (!_previews.TryGetValue(previewId!, out var pending) ||
                pending.ExpiresAtUtc <= DateTimeOffset.UtcNow ||
                pending.Value.ExpiresAtUtc <= DateTimeOffset.UtcNow)
            {
                _previews.Remove(previewId!);
                throw new WorkspaceContextPackGrantRefusalException("scope_preview_unavailable");
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
                        !VerifyContentPins(preview.Scope, workspaceStore) ||
                        !relationsAreCurrent(store, workspaceStore, preview.Scope))
                    {
                        throw new WorkspaceContextPackGrantRefusalException("request_stale");
                    }

                    if (state.Grants.Length >= 10_000 || state.WorkspaceScopeGeneration == long.MaxValue)
                    {
                        throw new IOException("The owner scope grant state cannot accept another grant.");
                    }

                    var grant = new PersistedScopeGrant(
                        Guid.NewGuid().ToString("N"),
                        EncodeBase64Url(verifierBytes),
                        Array.Empty<ScopeResourceRevision>(),
                        new[] { WorkspaceContextPackOperation },
                        Array.Empty<ScopeResourceRevision>(),
                        DateTimeOffset.UtcNow,
                        preview.ExpiresAtUtc,
                        null,
                        state.PolicyGeneration,
                        state.UnlockEpoch,
                        WorkspaceContextPackScope: CloneScope(preview.Scope),
                        ContextPackSemanticsVersion: CurrentScopeSemanticsVersion,
                        ContextPackSemanticsGeneration: 1);
                    var nextState = state with
                    {
                        SchemaVersion = OwnerScopeGrantState.CurrentSchemaVersion,
                        Grants = state.Grants.Append(grant).ToArray(),
                        WorkspaceScopeGeneration = state.WorkspaceScopeGeneration + 1
                    };
                    return (nextState, new OwnerScopeGrantIssue(
                        "mb1_" + EncodeBase64Url(tokenBytes), CloneGrant(grant)));
                });
                _previews.Remove(previewId!);
                return issued;
            }
            catch (WorkspaceContextPackGrantRefusalException)
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
            throw new WorkspaceContextPackGrantRefusalException("scope_preview_unavailable");
        }

        lock (_gate)
        {
            if (!_previews.TryGetValue(previewId!, out var pending) ||
                pending.ExpiresAtUtc <= DateTimeOffset.UtcNow)
            {
                _previews.Remove(previewId!);
                throw new WorkspaceContextPackGrantRefusalException("scope_preview_unavailable");
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
            throw new WorkspaceContextPackGrantRefusalException("workspace_grant_unavailable");
        }

        return persistence.UpdateWorkspaceContextPackGrantState((state, _, _, _) =>
        {
            state = OwnerScopeGrantAuthority.ValidateAndMigrateState(state);
            var index = Array.FindIndex(state.Grants, grant =>
                string.Equals(grant.GrantId, grantId, StringComparison.Ordinal) &&
                grant.WorkspaceContextPackScope is not null);
            if (index < 0)
            {
                throw new WorkspaceContextPackGrantRefusalException("workspace_grant_unavailable");
            }

            var grant = state.Grants[index];
            if (grant.RevokedAtUtc is not null)
            {
                return (state, CloneGrant(grant));
            }

            if (state.WorkspaceScopeGeneration == long.MaxValue ||
                grant.ContextPackSemanticsGeneration == long.MaxValue)
            {
                throw new IOException("The workspace scope generation is exhausted.");
            }

            var revoked = grant with
            {
                RevokedAtUtc = DateTimeOffset.UtcNow,
                ContextPackSemanticsGeneration = grant.ContextPackSemanticsGeneration + 1
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

    public WorkspaceContextPackGrantSummary[] List(
        AuthenticatedContext actor,
        IWorkspaceContextPackGrantPersistence persistence)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(persistence);
        EnsureOwner(actor);
        return persistence.ReadWorkspaceContextPackGrantSnapshot((state, _, _, _) =>
            OwnerScopeGrantAuthority.ValidateAndMigrateState(state).Grants
                .Where(grant => grant.WorkspaceContextPackScope is not null)
                .OrderBy(grant => grant.GrantId, StringComparer.Ordinal)
                .Select(grant => new WorkspaceContextPackGrantSummary(
                    grant.GrantId,
                    CloneScope(grant.WorkspaceContextPackScope!),
                    grant.CreatedAtUtc,
                    grant.ExpiresAtUtc,
                    grant.PolicyGeneration,
                    grant.ContextPackSemanticsVersion,
                    grant.ContextPackSemanticsGeneration,
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
        grant.WorkspaceContextPackScope is not null && grant.RevokedAtUtc is null &&
        grant.ContextPackSemanticsVersion == semanticsVersion &&
        grant.ContextPackSemanticsGeneration == semanticsGeneration &&
        grant.ExpiresAtUtc > DateTimeOffset.UtcNow &&
        grant.PolicyGeneration == state.PolicyGeneration && grant.UnlockEpoch == state.UnlockEpoch;

    public static WorkspaceContextPackScope NormalizeScope(WorkspaceContextPackScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (!WorkspaceFolderGrantEvaluator.IsNodeId(scope.WorkspaceId) || scope.TreeRevision < 1 ||
            scope.Files is null || scope.Files.Length is 0 or > MaximumFiles ||
            scope.Relations is null || scope.Relations.Length > MaximumRelations ||
            scope.Files.Any(file => file is null) || scope.Relations.Any(relation => relation is null))
        {
            throw new ArgumentException("The workspace context-pack scope is invalid.", nameof(scope));
        }

        var files = scope.Files.OrderBy(file => file.FileId, StringComparer.Ordinal).ToArray();
        var fileIds = new HashSet<string>(StringComparer.Ordinal);
        var totalPassages = 0;
        var seeds = 0;
        for (var index = 0; index < files.Length; index++)
        {
            var file = files[index];
            if (!WorkspaceFolderGrantEvaluator.IsNodeId(file.FileId) || file.ContentRevision < 1 ||
                file.Length is < 0 or > WorkspaceContentRevisionInfo.MaximumContentBytes ||
                !IsSha256(file.Sha256) || file.AccessLifecycleGeneration < 0 ||
                file.Passages is null || file.Passages.Any(passage => passage is null) ||
                !fileIds.Add(file.FileId))
            {
                throw new ArgumentException("The workspace context-pack file selection is invalid.", nameof(scope));
            }

            if (file.IsSeed)
            {
                seeds++;
            }

            var passages = file.Passages.OrderBy(passage => passage.Offset)
                .ThenBy(passage => passage.Length)
                .ThenBy(passage => passage.Sha256, StringComparer.Ordinal)
                .ToArray();
            totalPassages = checked(totalPassages + passages.Length);
            if (totalPassages > MaximumPassages)
            {
                throw new ArgumentException("The workspace context-pack passage selection is too large.", nameof(scope));
            }

            var passageKeys = new HashSet<(long Offset, long Length, string Sha256)>();
            foreach (var passage in passages)
            {
                if (passage is null || passage.FileId != file.FileId ||
                    passage.ContentRevision != file.ContentRevision || passage.Offset < 0 ||
                    passage.Length <= 0 || passage.Offset > file.Length ||
                    passage.Length > file.Length - passage.Offset || !IsSha256(passage.Sha256) ||
                    !passageKeys.Add((passage.Offset, passage.Length, passage.Sha256)))
                {
                    throw new ArgumentException("The workspace context-pack passage selection is invalid.", nameof(scope));
                }
            }

            files[index] = file with { Passages = passages };
        }

        if (seeds == 0)
        {
            throw new ArgumentException("The workspace context-pack scope needs an explicit seed file.", nameof(scope));
        }

        var relations = scope.Relations.OrderBy(relation => relation.RelationId, StringComparer.Ordinal).ToArray();
        var relationIds = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < relations.Length; index++)
        {
            var relation = relations[index];
            if (!DomainIdentifiers.IsRelationId(relation.RelationId) || relation.Revision < 1 ||
                !Enum.TryParse<RelationKind>(relation.Kind, ignoreCase: false, out var kind) || !Enum.IsDefined(kind) ||
                !string.Equals(relation.State, nameof(RelationState.Accepted), StringComparison.Ordinal) ||
                !relationIds.Add(relation.RelationId) || relation.From is null || relation.To is null ||
                !ValidEndpoint(relation.From, files) || !ValidEndpoint(relation.To, files))
            {
                throw new ArgumentException("The workspace context-pack relation selection is invalid.", nameof(scope));
            }

            relations[index] = relation with { From = relation.From with { }, To = relation.To with { } };
        }

        return new WorkspaceContextPackScope(
            scope.WorkspaceId,
            scope.TreeRevision,
            files,
            relations);
    }

    public static WorkspaceContextPackScope CloneScope(WorkspaceContextPackScope scope) => scope with
    {
        Files = scope.Files.Select(file => file with
        {
            Passages = file.Passages.Select(passage => passage with { }).ToArray()
        }).ToArray(),
        Relations = scope.Relations.Select(relation => relation with
        {
            From = relation.From with { },
            To = relation.To with { }
        }).ToArray()
    };

    public static bool MatchesWorkspaceMetadata(WorkspaceContextPackScope scope, WorkspaceSnapshot? workspace)
    {
        if (workspace is null || workspace.WorkspaceId != scope.WorkspaceId ||
            workspace.Revision != scope.TreeRevision || workspace.Nodes is null)
        {
            return false;
        }

        var nodes = WorkspaceFolderGrantEvaluator.BuildNodeIndex(workspace);
        foreach (var file in scope.Files)
        {
            if (!nodes.TryGetValue(file.FileId, out var node) || node.Kind != WorkspaceNodeKind.File ||
                node.AccessLifecycleGeneration != file.AccessLifecycleGeneration || node.Revisions is null)
            {
                return false;
            }

            var revision = node.Revisions.FirstOrDefault(item => item.Revision == file.ContentRevision);
            if (revision is null || revision.Length != file.Length || revision.Sha256 != file.Sha256)
            {
                return false;
            }
        }

        return true;
    }

    public static bool VerifyContentPins(WorkspaceContextPackScope scope, IWorkspacePersistence workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        foreach (var file in scope.Files)
        {
            WorkspaceFileContent? content = null;
            try
            {
                content = workspace.ReadWorkspaceFile(file.FileId, file.ContentRevision);
                if (!ContentMatches(file, content))
                {
                    return false;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WorkspaceRefusalException)
            {
                return false;
            }
            finally
            {
                if (content?.Content is { Length: > 0 } bytes)
                {
                    CryptographicOperations.ZeroMemory(bytes);
                }
            }
        }

        return true;
    }

    public static bool ContentMatches(WorkspaceContextPackFilePin file, WorkspaceFileContent content)
    {
        if (content.Revision != file.ContentRevision || content.Length != file.Length ||
            content.Content.LongLength != file.Length || !string.Equals(content.Sha256, file.Sha256, StringComparison.Ordinal) ||
            !string.Equals(ComputeSha256(content.Content), file.Sha256, StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var passage in file.Passages)
        {
            var digest = SHA256.HashData(content.Content.AsSpan(checked((int)passage.Offset), checked((int)passage.Length)));
            if (!string.Equals(Convert.ToHexString(digest).ToLowerInvariant(), passage.Sha256, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    public static bool ScopeMatches(WorkspaceContextPackScope left, WorkspaceContextPackScope right)
    {
        if (left.WorkspaceId != right.WorkspaceId || left.TreeRevision != right.TreeRevision ||
            left.Files.Length != right.Files.Length || left.Relations.Length != right.Relations.Length)
        {
            return false;
        }

        for (var index = 0; index < left.Files.Length; index++)
        {
            var a = left.Files[index];
            var b = right.Files[index];
            if (a.FileId != b.FileId || a.ContentRevision != b.ContentRevision || a.Length != b.Length ||
                a.Sha256 != b.Sha256 || a.AccessLifecycleGeneration != b.AccessLifecycleGeneration ||
                a.IsSeed != b.IsSeed || a.Passages.Length != b.Passages.Length)
            {
                return false;
            }

            for (var passageIndex = 0; passageIndex < a.Passages.Length; passageIndex++)
            {
                if (a.Passages[passageIndex] != b.Passages[passageIndex])
                {
                    return false;
                }
            }
        }

        for (var index = 0; index < left.Relations.Length; index++)
        {
            var a = left.Relations[index];
            var b = right.Relations[index];
            if (a.RelationId != b.RelationId || a.Revision != b.Revision || a.Kind != b.Kind || a.State != b.State ||
                a.From != b.From || a.To != b.To)
            {
                return false;
            }
        }

        return true;
    }

    private WorkspaceContextPackGrantPreview StorePreviewLocked(
        OwnerScopeGrantState state,
        WorkspaceContextPackScope scope,
        WorkspaceContextPackRelationPreview[] relations,
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
            throw new IOException("The workspace context-pack preview capacity is exhausted.");
        }

        var preview = new WorkspaceContextPackGrantPreview(
            Guid.NewGuid().ToString("N"),
            CloneScope(scope),
            relations.Select(CloneRelationPreview).ToArray(),
            now,
            expiresAtUtc,
            state.PolicyGeneration,
            state.WorkspaceScopeGeneration,
            state.UnlockEpoch,
            now + PreviewLifetime);
        _previews.Add(preview.PreviewId, new PendingPreview(preview, preview.PreviewExpiresAtUtc));
        return ClonePreview(preview);
    }

    public static bool RelationDetailsMatch(
        WorkspaceContextPackScope scope,
        WorkspaceContextPackRelationPreview[] relations)
    {
        if (relations.Length != scope.Relations.Length)
        {
            return false;
        }

        var details = relations.ToDictionary(item => item.Relation.Id, StringComparer.Ordinal);
        foreach (var pin in scope.Relations)
        {
            if (!details.TryGetValue(pin.RelationId, out var item) ||
                item.Relation.Revision != pin.Revision || item.Relation.State.ToString() != pin.State ||
                item.Relation.Kind.ToString() != pin.Kind || item.Review.State != RelationReviewState.Current ||
                !EndpointMatches(pin.From, item.Relation.From) || !EndpointMatches(pin.To, item.Relation.To))
            {
                return false;
            }
        }

        return true;
    }

    private static bool EndpointMatches(WorkspaceContextPackEndpointPin pin, RelationEndpoint endpoint)
    {
        var kind = endpoint.EndpointKind.ToString();
        var offset = endpoint.Span?.Start;
        long? length = endpoint.Span is { } span ? span.End - span.Start : null;
        var digest = endpoint.WorkspacePin?.Sha256 ?? string.Empty;
        return pin.ResourceId == endpoint.ResourceId && pin.EndpointKind == kind && pin.Revision == endpoint.Revision &&
            pin.Offset == offset && pin.Length == length && pin.Sha256 == digest;
    }

    private static bool ValidEndpoint(
        WorkspaceContextPackEndpointPin endpoint,
        WorkspaceContextPackFilePin[] files)
    {
        if (!WorkspaceFolderGrantEvaluator.IsNodeId(endpoint.ResourceId) || endpoint.Revision < 1 ||
            !IsSha256(endpoint.Sha256) || endpoint.EndpointKind is not
                (nameof(RelationEndpointKind.WorkspaceFile) or nameof(RelationEndpointKind.WorkspacePassage)))
        {
            return false;
        }

        var file = files.FirstOrDefault(candidate => candidate.FileId == endpoint.ResourceId);
        if (file is null || endpoint.Revision != file.ContentRevision)
        {
            return false;
        }

        if (endpoint.EndpointKind == nameof(RelationEndpointKind.WorkspaceFile))
        {
            return endpoint.Offset is null && endpoint.Length is null && endpoint.Sha256 == file.Sha256;
        }

        if (endpoint.Offset is not { } offset || endpoint.Length is not { } length ||
            offset < 0 || length <= 0 || offset > file.Length || length > file.Length - offset)
        {
            return false;
        }

        return file.Passages.Any(passage =>
            passage.Offset == offset && passage.Length == length && passage.Sha256 == endpoint.Sha256);
    }

    private static WorkspaceContextPackGrantPreview ClonePreview(WorkspaceContextPackGrantPreview preview) =>
        preview with
        {
            Scope = CloneScope(preview.Scope),
            Relations = preview.Relations.Select(CloneRelationPreview).ToArray()
        };

    private static WorkspaceContextPackRelationPreview CloneRelationPreview(WorkspaceContextPackRelationPreview relation) =>
        relation with
        {
            Relation = relation.Relation with
            {
                From = relation.Relation.From with { },
                To = relation.Relation.To with { },
                RequestEvidence = relation.Relation.RequestEvidence?.Select(item => item with { }).ToArray(),
                History = relation.Relation.History?.Select(item => item with { }).ToArray()
            },
            Review = relation.Review with { StaleResourceIds = (string[])relation.Review.StaleResourceIds.Clone() }
        };

    private static PersistedScopeGrant CloneGrant(PersistedScopeGrant grant) => grant with
    {
        Resources = grant.Resources.Select(resource => resource with { }).ToArray(),
        Operations = (string[])grant.Operations.Clone(),
        DestinationResources = grant.DestinationResources.Select(resource => resource with { }).ToArray(),
        Egress = grant.Egress is null ? null : grant.Egress with { },
        WorkspaceContextPackScope = grant.WorkspaceContextPackScope is null
            ? null
            : CloneScope(grant.WorkspaceContextPackScope)
    };

    private static string ComputeSha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static bool IsSha256(string? value) => value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string EncodeBase64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static void EnsureOwner(AuthenticatedContext actor)
    {
        if (actor.Kind != PrincipalKind.Owner)
        {
            throw new UnauthorizedAccessException("Workspace context-pack grant management requires the owner workflow.");
        }
    }
}

public sealed class WorkspaceContextPackGrantRefusalException : InvalidOperationException
{
    public WorkspaceContextPackGrantRefusalException(string code)
        : base("The workspace context-pack grant operation is unavailable.") => Code = code;

    public string Code { get; }
}
