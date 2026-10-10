namespace MetaBrain.Core.Security;

/// <summary>
/// One approved collection scope point in a permission-filtered agent view:
/// the exact opaque resource ID and revision the session may address. The
/// view deliberately carries no zone, kind, title, body, locator, membership
/// count or collection name, because none of that is part of an approved
/// grant snapshot and none of it is needed to authorize a read.
/// </summary>
public sealed record AgentCollectionScopePoint(string ResourceId, long Revision);

/// <summary>
/// Permission-filtered view of an owner collection for one redeemed agent
/// session. It is projected from the session's frozen approved snapshot, so
/// later membership, title, path or revision changes cannot widen it, and a
/// session granted a subset of a collection never sees the members, counts
/// or titles it was not approved for. The view names no collection: the
/// collection is mutable owner-side selection metadata, while the session's
/// authority is the immutable snapshot itself.
/// </summary>
public sealed record AgentCollectionView(
    string SessionId,
    string GrantId,
    string[] Operations,
    AgentCollectionScopePoint[] Resources);

/// <summary>
/// Single projection contract over owner collection membership for agent
/// browsing (R11, R32). Collections stay an owner-only selector: membership
/// changes advance the policy generation and fail closed on live sessions
/// instead of silently re-scoping them, and this projection only ever
/// re-expresses an already-approved snapshot. The canonical memory items,
/// sources, relations, lifecycle records and retained archives a collection
/// points at are never copied or re-granted here: the grant snapshot remains
/// the only authority.
/// </summary>
public static class CollectionViewAuthority
{
    /// <summary>
    /// Projects the frozen approved snapshot of one live session into the
    /// agent-visible collection view. The snapshot is normalized (ordered by
    /// resource ID, each approved resource listed exactly once, revisions at
    /// least 1) so overlapping memberships or grants can never present a
    /// duplicated or inflated scope. Destination scopes and egress consent
    /// are not borrowable read scope and are intentionally not projected.
    /// </summary>
    public static AgentCollectionView ProjectSession(AgentSessionSnapshot session)
    {
        ArgumentNullException.ThrowIfNull(session);

        var resources = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var resource in session.Resources)
        {
            ArgumentNullException.ThrowIfNull(resource);
            if (resource.Revision < 1 ||
                !OwnerScopeGrantAuthority.IsValidIdentifier(resource.ResourceId))
            {
                throw new InvalidDataException("An approved session snapshot carries an invalid resource revision.");
            }

            resources[resource.ResourceId] = resource.Revision;
        }

        var operations = session.Operations is { Length: > 0 } operationsValue
            ? operationsValue.Distinct(StringComparer.Ordinal).OrderBy(operation => operation, StringComparer.Ordinal).ToArray()
            : throw new InvalidDataException("An approved session snapshot carries no operations.");

        return new AgentCollectionView(
            session.SessionId,
            session.GrantId,
            operations,
            resources.Select(pair => new AgentCollectionScopePoint(pair.Key, pair.Value)).ToArray());
    }
}
