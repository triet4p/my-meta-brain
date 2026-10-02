using System.Security.Cryptography;

namespace MetaBrain.Core.Security;

public sealed record ScopeResourceRevision(string ResourceId, string ZoneId, long Revision);

public sealed record ScopeGrantEgress(string Provider, string Model, decimal MaximumCostUsd);

public sealed record OwnerScopeGrantPreview(
    string PreviewId,
    ScopeResourceRevision[] Resources,
    string[] Operations,
    ScopeResourceRevision[] DestinationResources,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    ScopeGrantEgress? Egress,
    long PolicyGeneration,
    string? CollectionId = null,
    long UnlockEpoch = 0);

public sealed record PersistedScopeGrant(
    string GrantId,
    string TokenVerifier,
    ScopeResourceRevision[] Resources,
    string[] Operations,
    ScopeResourceRevision[] DestinationResources,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    ScopeGrantEgress? Egress,
    long PolicyGeneration,
    long UnlockEpoch = 0);

public sealed record OwnerScopeCollection(string CollectionId, string[] ResourceIds);

public sealed record OwnerScopeGrantState(
    int SchemaVersion,
    long PolicyGeneration,
    PersistedScopeGrant[] Grants,
    OwnerScopeCollection[] Collections,
    long UnlockEpoch = 0,
    string[] ConsumedTokenVerifiers = null!)
{
    public const int CurrentSchemaVersion = 3;
    public static OwnerScopeGrantState Empty { get; } = new(
        CurrentSchemaVersion, 0, Array.Empty<PersistedScopeGrant>(), Array.Empty<OwnerScopeCollection>(),
        0, Array.Empty<string>());
}

public interface IScopeGrantPersistence
{
    OwnerScopeGrantState LoadScopeGrantState();
    T UpdateScopeGrantState<T>(
        Func<OwnerScopeGrantState, (OwnerScopeGrantState State, T Result)> update);
}

public sealed record OwnerScopeGrantIssue(string Token, PersistedScopeGrant Grant);

/// <summary>
/// Creates owner-confirmed scope previews and persists grants containing only a verifier for the random bearer.
/// Token redemption and session creation are intentionally separate lifecycle operations.
/// </summary>
public sealed class OwnerScopeGrantAuthority
{
    private static readonly HashSet<string> AllowedOperations = new(StringComparer.Ordinal)
    {
        "resource.read",
        "proposal.create",
        "link.create",
        "provider.egress"
    };

    private const int MaximumPendingPreviews = 1024;
    private static readonly TimeSpan PreviewLifetime = TimeSpan.FromMinutes(5);
    private readonly object _gate = new();
    private readonly Dictionary<string, PendingPreview> _previews = new(StringComparer.Ordinal);

    private sealed record PendingPreview(OwnerScopeGrantPreview Value, DateTimeOffset ExpiresAtUtc);

    public OwnerScopeGrantPreview Preview(
        AuthenticatedContext actor,
        IReadOnlyList<ScopeResourceRevision> resources,
        IReadOnlyList<string>? operations,
        IReadOnlyList<ScopeResourceRevision>? destinationResources,
        DateTimeOffset expiresAtUtc,
        ScopeGrantEgress? egress,
        IScopeGrantPersistence persistence)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(persistence);
        EnsureOwner(actor);

        var sourceSnapshot = NormalizeResources(resources, allowEmpty: false);
        var destinationSnapshot = NormalizeResources(destinationResources ?? Array.Empty<ScopeResourceRevision>(), allowEmpty: true);
        var operationSnapshot = NormalizeOperations(operations);
        ValidateOperationScope(operationSnapshot, destinationSnapshot, egress);
        ValidateExpiry(expiresAtUtc);

        lock (_gate)
        {
            var state = ValidateAndMigrateState(persistence.LoadScopeGrantState());
            return StorePreviewLocked(
                state, sourceSnapshot, operationSnapshot, destinationSnapshot, expiresAtUtc, egress, collectionId: null);
        }
    }

    public OwnerScopeGrantPreview PreviewCollection(
        AuthenticatedContext actor,
        string? collectionId,
        IReadOnlyList<ScopeResourceRevision> availableResources,
        IReadOnlyList<string>? operations,
        IReadOnlyList<ScopeResourceRevision>? destinationResources,
        DateTimeOffset expiresAtUtc,
        ScopeGrantEgress? egress,
        IScopeGrantPersistence persistence)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(availableResources);
        ArgumentNullException.ThrowIfNull(persistence);
        EnsureOwner(actor);
        if (!IsValidIdentifier(collectionId))
        {
            throw new ArgumentException("A valid owner collection is required.", nameof(collectionId));
        }

        var available = NormalizeResources(availableResources, allowEmpty: true)
            .ToDictionary(resource => resource.ResourceId, StringComparer.Ordinal);
        var destinationSnapshot = NormalizeResources(destinationResources ?? Array.Empty<ScopeResourceRevision>(), allowEmpty: true);
        var operationSnapshot = NormalizeOperations(operations);
        ValidateOperationScope(operationSnapshot, destinationSnapshot, egress);
        ValidateExpiry(expiresAtUtc);

        lock (_gate)
        {
            var state = ValidateAndMigrateState(persistence.LoadScopeGrantState());
            var collection = state.Collections.FirstOrDefault(value =>
                string.Equals(value.CollectionId, collectionId, StringComparison.Ordinal))
                ?? throw new ArgumentException("The selected owner collection does not exist.", nameof(collectionId));
            var sourceSnapshot = collection.ResourceIds.Select(resourceId =>
                available.TryGetValue(resourceId, out var resource)
                    ? resource
                    : throw new ArgumentException("An owner collection member is no longer managed.", nameof(collectionId)))
                .ToArray();
            return StorePreviewLocked(
                state, sourceSnapshot, operationSnapshot, destinationSnapshot, expiresAtUtc, egress, collectionId);
        }
    }

    public OwnerScopeCollection SetCollection(
        AuthenticatedContext actor,
        string? collectionId,
        IReadOnlyList<ScopeResourceRevision> resources,
        IScopeGrantPersistence persistence)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(persistence);
        EnsureOwner(actor);
        if (!IsValidIdentifier(collectionId))
        {
            throw new ArgumentException("A valid owner collection identifier is required.", nameof(collectionId));
        }

        var resourceIds = NormalizeResources(resources, allowEmpty: false)
            .Select(resource => resource.ResourceId)
            .ToArray();
        lock (_gate)
        {
            return persistence.UpdateScopeGrantState(state =>
            {
                var existing = Array.FindIndex(state.Collections, collection =>
                    string.Equals(collection.CollectionId, collectionId, StringComparison.Ordinal));
                if (existing < 0 && state.Collections.Length >= 1000)
                {
                    throw new IOException("The owner collection limit is exhausted.");
                }

                var changed = existing < 0 ||
                    !state.Collections[existing].ResourceIds.SequenceEqual(resourceIds, StringComparer.Ordinal);
                if (changed && state.PolicyGeneration == long.MaxValue)
                {
                    throw new IOException("The owner scope policy generation is exhausted.");
                }

                var updated = new OwnerScopeCollection(collectionId!, resourceIds);
                if (!changed)
                {
                    return (state, CloneCollection(updated));
                }

                var collections = state.Collections.Where((_, index) => index != existing)
                    .Append(updated)
                    .OrderBy(collection => collection.CollectionId, StringComparer.Ordinal)
                    .ToArray();
                var nextState = state with
                {
                    PolicyGeneration = state.PolicyGeneration + 1,
                    Collections = collections
                };
                return (nextState, CloneCollection(updated));
            });
        }
    }

    public IReadOnlyList<OwnerScopeCollection> ListCollections(
        AuthenticatedContext actor,
        IScopeGrantPersistence persistence)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(persistence);
        EnsureOwner(actor);
        lock (_gate)
        {
            var state = ValidateAndMigrateState(persistence.LoadScopeGrantState());
            return Array.AsReadOnly(state.Collections.Select(CloneCollection).ToArray());
        }
    }

    private OwnerScopeGrantPreview StorePreviewLocked(
        OwnerScopeGrantState state,
        ScopeResourceRevision[] sourceSnapshot,
        string[] operationSnapshot,
        ScopeResourceRevision[] destinationSnapshot,
        DateTimeOffset expiresAtUtc,
        ScopeGrantEgress? egress,
        string? collectionId)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var expiredId in _previews
                     .Where(pair => pair.Value.ExpiresAtUtc <= now)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _previews.Remove(expiredId);
        }

        if (_previews.Count >= MaximumPendingPreviews)
        {
            throw new IOException("The owner scope preview capacity is exhausted.");
        }

        var preview = new OwnerScopeGrantPreview(
            Guid.NewGuid().ToString("N"), sourceSnapshot, operationSnapshot, destinationSnapshot,
            now, expiresAtUtc, egress, state.PolicyGeneration, collectionId, state.UnlockEpoch);
        _previews.Add(preview.PreviewId, new PendingPreview(preview, now + PreviewLifetime));
        return ClonePreview(preview);
    }

    private static void ValidateExpiry(DateTimeOffset expiresAtUtc)
    {
        if (expiresAtUtc.Offset != TimeSpan.Zero || expiresAtUtc <= DateTimeOffset.UtcNow)
        {
            throw new ArgumentException("Grant expiry must be a future UTC instant.", nameof(expiresAtUtc));
        }
    }
    public OwnerScopeGrantIssue Issue(
        AuthenticatedContext actor,
        string? previewId,
        IScopeGrantPersistence persistence)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(persistence);
        EnsureOwner(actor);
        if (!Guid.TryParseExact(previewId, "N", out _))
        {
            throw new ArgumentException("The owner scope preview is unavailable.", nameof(previewId));
        }

        lock (_gate)
        {
            if (!_previews.TryGetValue(previewId!, out var pending) || pending.ExpiresAtUtc <= DateTimeOffset.UtcNow)
            {
                _previews.Remove(previewId!);
                throw new InvalidOperationException("The owner scope preview is unavailable.");
            }

            var preview = pending.Value;
            if (preview.ExpiresAtUtc <= DateTimeOffset.UtcNow)
            {
                _previews.Remove(previewId!);
                throw new InvalidOperationException("The owner scope preview is expired.");
            }

            var tokenBytes = RandomNumberGenerator.GetBytes(32);
            var verifierBytes = SHA256.HashData(tokenBytes);
            try
            {
                var issue = persistence.UpdateScopeGrantState(state =>
                {
                    if (state.PolicyGeneration != preview.PolicyGeneration || state.UnlockEpoch != preview.UnlockEpoch)
                    {
                        throw new InvalidOperationException("The owner scope preview is stale.");
                    }

                    if (state.Grants.Length >= 10_000)
                    {
                        throw new IOException("The owner scope grant state cannot accept another grant.");
                    }

                    var grant = new PersistedScopeGrant(
                        Guid.NewGuid().ToString("N"), EncodeBase64Url(verifierBytes),
                        CloneResources(preview.Resources), (string[])preview.Operations.Clone(),
                        CloneResources(preview.DestinationResources), DateTimeOffset.UtcNow,
                        preview.ExpiresAtUtc, preview.Egress, state.PolicyGeneration, state.UnlockEpoch);
                    var nextState = state with { Grants = state.Grants.Append(grant).ToArray() };
                    var issued = new OwnerScopeGrantIssue("mb1_" + EncodeBase64Url(tokenBytes), CloneGrant(grant));
                    return (nextState, issued);
                });
                _previews.Remove(previewId!);
                return issue;
            }
            catch (InvalidOperationException)
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

    public IReadOnlyList<PersistedScopeGrant> List(AuthenticatedContext actor, IScopeGrantPersistence persistence)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(persistence);
        EnsureOwner(actor);

        lock (_gate)
        {
            var state = ValidateAndMigrateState(persistence.LoadScopeGrantState());
            return Array.AsReadOnly(state.Grants.Select(CloneGrant).ToArray());
        }
    }
    public void ClearPreviews()
    {
        lock (_gate)
        {
            _previews.Clear();
        }
    }


    public static OwnerScopeGrantState ValidateAndMigrateState(OwnerScopeGrantState state)
    {
        if (state is null || state.Grants is null || state.Grants.Length > 10_000 ||
            state.Collections is null || state.Collections.Length > 1000 ||
            state.PolicyGeneration < 0 || state.UnlockEpoch < 0 ||
            (state.SchemaVersion != OwnerScopeGrantState.CurrentSchemaVersion && state.SchemaVersion != 2))
        {
            throw new InvalidDataException("Invalid encrypted owner scope grant state.");
        }

        var consumedVerifiers = state.ConsumedTokenVerifiers;
        if (consumedVerifiers is null && state.SchemaVersion == 2)
        {
            consumedVerifiers = Array.Empty<string>();
        }

        if (consumedVerifiers is null || consumedVerifiers.Length > 10_000)
        {
            throw new InvalidDataException("Invalid encrypted owner scope grant state.");
        }



        var grantIds = new HashSet<string>(StringComparer.Ordinal);
        var verifiers = new HashSet<string>(StringComparer.Ordinal);
        var grants = new PersistedScopeGrant[state.Grants.Length];
        for (var index = 0; index < state.Grants.Length; index++)
        {
            var grant = state.Grants[index];
            if (grant is null || !Guid.TryParseExact(grant.GrantId, "N", out _) || !grantIds.Add(grant.GrantId) ||
                !IsValidVerifier(grant.TokenVerifier) || !verifiers.Add(grant.TokenVerifier) ||
                grant.Resources is null || grant.Operations is null || grant.Operations.Length == 0 ||
                grant.DestinationResources is null ||
                grant.CreatedAtUtc.Offset != TimeSpan.Zero || grant.ExpiresAtUtc.Offset != TimeSpan.Zero ||
                grant.ExpiresAtUtc <= grant.CreatedAtUtc || grant.PolicyGeneration < 0 ||
                grant.PolicyGeneration > state.PolicyGeneration || grant.UnlockEpoch < 0 ||
                grant.UnlockEpoch > state.UnlockEpoch)
            {
                throw new InvalidDataException("Invalid encrypted owner scope grant record.");
            }

            try
            {
                var resources = NormalizeResources(grant.Resources, allowEmpty: false);
                var destinations = NormalizeResources(grant.DestinationResources, allowEmpty: true);
                var operations = NormalizeOperations(grant.Operations);
                ValidateOperationScope(operations, destinations, grant.Egress);
                grants[index] = grant with
                {
                    Resources = resources,
                    Operations = operations,
                    DestinationResources = destinations
                };
            }
            catch (ArgumentException ex)
            {
                throw new InvalidDataException("Invalid encrypted owner scope grant record.", ex);
            }
        }

        OwnerScopeCollection[] collections;
        try
        {
            collections = NormalizeCollections(state.Collections);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidDataException("Invalid encrypted owner scope collection state.", ex);
        }

        var consumed = NormalizeConsumedVerifiers(consumedVerifiers);
        return new OwnerScopeGrantState(OwnerScopeGrantState.CurrentSchemaVersion, state.PolicyGeneration, grants, collections, state.UnlockEpoch, consumed);
    }

    private static OwnerScopeCollection[] NormalizeCollections(OwnerScopeCollection[]? collections)
    {
        if (collections is null || collections.Length > 1000 || collections.Any(collection => collection is null))
        {
            throw new ArgumentException("The encrypted owner collection state is invalid.", nameof(collections));
        }

        var normalized = collections.OrderBy(collection => collection.CollectionId, StringComparer.Ordinal).ToArray();
        var collectionIds = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < normalized.Length; index++)
        {
            var collection = normalized[index];
            if (!IsValidIdentifier(collection.CollectionId) || !collectionIds.Add(collection.CollectionId) ||
                collection.ResourceIds is null || collection.ResourceIds.Length is 0 or > 10_000)
            {
                throw new ArgumentException("The encrypted owner collection record is invalid.", nameof(collections));
            }

            var resourceIds = collection.ResourceIds.Order(StringComparer.Ordinal).ToArray();
            var uniqueResourceIds = new HashSet<string>(StringComparer.Ordinal);
            if (resourceIds.Any(resourceId => !IsValidIdentifier(resourceId) || !uniqueResourceIds.Add(resourceId)))
            {
                throw new ArgumentException("The encrypted owner collection members are invalid.", nameof(collections));
            }

            normalized[index] = collection with { ResourceIds = resourceIds };
        }

        return normalized;
    }

    private static string[] NormalizeConsumedVerifiers(string[]? consumed)
    {
        if (consumed is null || consumed.Length > 10_000 || consumed.Any(value => value is null))
        {
            throw new ArgumentException("The encrypted consumed token state is invalid.", nameof(consumed));
        }

        var normalized = consumed.Order(StringComparer.Ordinal).ToArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var verifier in normalized)
        {
            if (!IsValidVerifier(verifier) || !seen.Add(verifier))
            {
                throw new ArgumentException("The encrypted consumed token record is invalid.", nameof(consumed));
            }
        }

        return normalized;
    }

    private static ScopeResourceRevision[] NormalizeResources(
        IReadOnlyList<ScopeResourceRevision>? resources,
        bool allowEmpty)
    {
        if (resources is null || (!allowEmpty && resources.Count == 0) || resources.Any(resource => resource is null))
        {
            throw new ArgumentException("An owner scope must contain concrete resources.", nameof(resources));
        }

        var normalized = resources.OrderBy(resource => resource.ResourceId, StringComparer.Ordinal).ToArray();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var resource in normalized)
        {
            if (!IsValidIdentifier(resource.ResourceId) || !IsValidIdentifier(resource.ZoneId) ||
                resource.Revision < 1 || !ids.Add(resource.ResourceId))
            {
                throw new ArgumentException("An owner scope contains an invalid or duplicate resource revision.", nameof(resources));
            }
        }

        return normalized;
    }

    private static string[] NormalizeOperations(IReadOnlyList<string>? operations)
    {
        IReadOnlyList<string> requested = operations is { Count: > 0 } ? operations : new[] { "resource.read" };
        var normalized = requested.Order(StringComparer.Ordinal).ToArray();
        if (normalized.Length == 0 || normalized.Any(operation => !AllowedOperations.Contains(operation)) ||
            normalized.Distinct(StringComparer.Ordinal).Count() != normalized.Length)
        {
            throw new ArgumentException("The owner scope contains an unsupported or duplicate operation.", nameof(operations));
        }

        return normalized;
    }

    private static void ValidateOperationScope(
        IReadOnlyCollection<string> operations,
        IReadOnlyCollection<ScopeResourceRevision> destinations,
        ScopeGrantEgress? egress)
    {
        var needsDestination = operations.Contains("proposal.create", StringComparer.Ordinal) ||
            operations.Contains("link.create", StringComparer.Ordinal);
        if (needsDestination != (destinations.Count > 0))
        {
            throw new ArgumentException("Proposal and link operations require a separate concrete destination scope.");
        }

        var hasEgressOperation = operations.Contains("provider.egress", StringComparer.Ordinal);
        if (hasEgressOperation != (egress is not null) ||
            (egress is not null && (!IsValidIdentifier(egress.Provider) || !IsValidIdentifier(egress.Model) || egress.MaximumCostUsd <= 0)))
        {
            throw new ArgumentException("Provider egress requires an explicit valid provider, model, and positive cost limit.");
        }
    }

    private static bool IsValidVerifier(string? verifier)
    {
        if (verifier is null)
        {
            return false;
        }

        try
        {
            var decoded = DecodeBase64Url(verifier);
            try
            {
                return decoded.Length == 32;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(decoded);
            }
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool IsValidIdentifier(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 128 || !IsAsciiAlphaNumeric(value[0]))
        {
            return false;
        }

        for (var index = 1; index < value.Length; index++)
        {
            var character = value[index];
            if (!IsAsciiAlphaNumeric(character) && character is not '.' and not '_' and not '-')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsAsciiAlphaNumeric(char value) =>
        value is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9';

    private static string EncodeBase64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] DecodeBase64Url(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        normalized += new string('=', (4 - normalized.Length % 4) % 4);
        return Convert.FromBase64String(normalized);
    }

    private static OwnerScopeCollection CloneCollection(OwnerScopeCollection collection) =>
        collection with { ResourceIds = (string[])collection.ResourceIds.Clone() };

    private static ScopeResourceRevision[] CloneResources(ScopeResourceRevision[] resources) =>
        resources.Select(resource => resource with { }).ToArray();

    private static OwnerScopeGrantPreview ClonePreview(OwnerScopeGrantPreview preview) => preview with
    {
        Resources = CloneResources(preview.Resources),
        Operations = (string[])preview.Operations.Clone(),
        DestinationResources = CloneResources(preview.DestinationResources),
        Egress = preview.Egress is null ? null : preview.Egress with { }
    };

    private static PersistedScopeGrant CloneGrant(PersistedScopeGrant grant) => grant with
    {
        Resources = CloneResources(grant.Resources),
        Operations = (string[])grant.Operations.Clone(),
        DestinationResources = CloneResources(grant.DestinationResources),
        Egress = grant.Egress is null ? null : grant.Egress with { }
    };

    private static void EnsureOwner(AuthenticatedContext actor)
    {
        if (actor.Kind != PrincipalKind.Owner)
        {
            throw new UnauthorizedAccessException("Scope grant management requires the authenticated owner workflow.");
        }
    }
}
