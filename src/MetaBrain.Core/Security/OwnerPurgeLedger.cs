using MetaBrain.Core.Domain;

namespace MetaBrain.Core.Security;

/// <summary>
/// every owner-confirmed purge. The ledger records WHAT was purged (target
/// and impacted IDs with the revisions they were deleted at) and never the
/// removed payload: no body, title, locator, citation, note or key material
/// is recorded here. Purged IDs stay reserved forever so a later create,
/// import, revise, catalog publication, grant or reindex can never resurrect
/// them; the ledger is what makes that refusal durable across restarts.
/// <para>
/// The ledger also carries the interruption checkpoint: a confirmed intent
/// names every canonical deletion still outstanding, so a crash, lock or
/// process kill between the confirm and the last delete resumes honestly
/// instead of pretending the purge finished.
/// </para>
/// </summary>
public enum PurgeIntentPhase
{
    Unknown = 0,
    Confirmed = 1,
    Applying = 2,
    Finalizing = 3,
    Applied = 4
}

/// <summary>One canonical resource a purge deleted (or must still delete).</summary>
public sealed record PurgedResourceRef(string ResourceId, long Revision);

/// <summary>
/// One owner-confirmed purge intent: the target, the exact impacted canonical
/// set bound at confirm time, the catalog entries withdrawn, the owner's
/// reason, and the phase the interruption checkpoint reached.
/// </summary>
public sealed record OwnerPurgeIntent(
    string PurgeId,
    string TargetResourceId,
    long TargetRevision,
    string TargetZoneId,
    string TargetKind,
    PurgedResourceRef[] Impacted,
    string[] WithdrawnCatalogIds,
    string Reason,
    ExecutorKind Executor,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? ConfirmedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    PurgeIntentPhase Phase,
    long FinalGeneration,
    PurgedResourceRef[] Remaining);

/// <summary>Durable encrypted purge ledger state: every confirmed intent.</summary>
public sealed record OwnerPurgeLedgerState(int SchemaVersion, OwnerPurgeIntent[] Intents)
{
    public const int CurrentSchemaVersion = 1;

    public static OwnerPurgeLedgerState Empty { get; } = new(
        CurrentSchemaVersion, Array.Empty<OwnerPurgeIntent>());
}

/// <summary>
/// Encrypted persistence for the owner purge ledger, implemented by the vault
/// lifecycle so a purge intent shares the same atomic update and revision
/// semantics as scope grants, the owner catalog and source enrollment.
/// </summary>
public interface IPurgeLedgerPersistence
{
    OwnerPurgeLedgerState LoadPurgeLedgerState();

    T UpdatePurgeLedgerState<T>(
        Func<OwnerPurgeLedgerState, (OwnerPurgeLedgerState State, T Result)> update);
}

/// <summary>
/// S7-T5 owner purge ledger authority: validates the durable ledger, reserves
/// purged IDs forever, and moves intents through their interruption phases.
/// The authority never reads canonical content and never deletes anything;
/// deletion and scope finalization live in the application purge service.
/// </summary>
public sealed class OwnerPurgeLedgerAuthority
{
    public const int MaximumIntents = 1000;
    public const int MaximumImpactedPerPurge = 1024;
    public const int MaximumReasonLength = 1000;

    /// <summary>
    /// The zone an owner-confirmed workspace purge is recorded under. The
    /// workspace keeps the same ledger as canonical records and sessions, so
    /// one durable deletion bookkeeping — and one backup/restore rule — covers
    /// every purged identity in the vault.
    /// </summary>
    public const string WorkspaceZoneId = "workspace";

    /// <summary>The target kind a workspace purge records.</summary>
    public const string WorkspaceTargetKind = "workspace-node";

    /// <summary>
    /// True when the ID was purged by any applied (or in-flight confirmed)
    /// intent. Callers refuse canonical creates, imports, revises and catalog
    /// mappings for such IDs so nothing resurrects them.
    /// </summary>
    public static bool IsPurged(OwnerPurgeLedgerState state, string? resourceId)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (string.IsNullOrEmpty(resourceId))
        {
            return false;
        }

        var validated = ValidateAndNormalizeState(state);
        foreach (var intent in validated.Intents)
        {
            if (string.Equals(intent.TargetResourceId, resourceId, StringComparison.Ordinal))
            {
                return true;
            }

            foreach (var impacted in intent.Impacted)
            {
                if (string.Equals(impacted.ResourceId, resourceId, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public static OwnerPurgeIntent? FindIntent(OwnerPurgeLedgerState state, string purgeId)
    {
        ArgumentNullException.ThrowIfNull(state);
        return ValidateAndNormalizeState(state).Intents.FirstOrDefault(intent =>
            string.Equals(intent.PurgeId, purgeId, StringComparison.Ordinal));
    }

    /// <summary>
    /// Every identity any confirmed purge reserved, across all zones. A single
    /// validated pass gives callers a set to filter restored records, catalog
    /// mappings and workspace nodes without re-validating the ledger per item.
    /// </summary>
    public static HashSet<string> ReservedIdentifiers(OwnerPurgeLedgerState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var validated = ValidateAndNormalizeState(state);
        var reserved = new HashSet<string>(StringComparer.Ordinal);
        foreach (var intent in validated.Intents)
        {
            reserved.Add(intent.TargetResourceId);
            foreach (var impacted in intent.Impacted)
            {
                reserved.Add(impacted.ResourceId);
            }
        }

        return reserved;
    }

    /// <summary>
    /// The workspace node identities a purge reserved forever. Only intents
    /// recorded under <see cref="WorkspaceZoneId"/> contribute, so a canonical
    /// record purge can never erase an unrelated workspace node that happens to
    /// share an opaque identifier. The set is what the workspace store and a
    /// restore re-apply before serving or materializing nodes.
    /// </summary>
    public static HashSet<string> ReservedWorkspaceIdentifiers(OwnerPurgeLedgerState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var validated = ValidateAndNormalizeState(state);
        var reserved = new HashSet<string>(StringComparer.Ordinal);
        foreach (var intent in validated.Intents)
        {
            if (!string.Equals(intent.TargetZoneId, WorkspaceZoneId, StringComparison.Ordinal))
            {
                continue;
            }

            reserved.Add(intent.TargetResourceId);
            foreach (var impacted in intent.Impacted)
            {
                reserved.Add(impacted.ResourceId);
            }
        }

        return reserved;
    }

    /// <summary>
    /// Builds the one durable ledger entry an owner-confirmed workspace purge
    /// writes before it removes the canonical nodes. It names the selected
    /// tombstone, the whole deletion group it belonged to and the owner's
    /// reason, and carries no name, path, byte or key: the ledger records what
    /// was purged, never the removed payload.
    /// </summary>
    public static OwnerPurgeIntent CreateWorkspaceIntent(
        string purgeId,
        string targetNodeId,
        long targetRevision,
        string targetKind,
        IEnumerable<PurgedResourceRef> impacted,
        string reason,
        DateTimeOffset now,
        long treeRevision)
    {
        ArgumentNullException.ThrowIfNull(impacted);
        var refs = impacted.ToArray();
        if (refs.Length == 0 || refs.Length > MaximumImpactedPerPurge)
        {
            throw new InvalidDataException("A workspace purge names between one and the ledger bound of identities.");
        }

        var utc = now.ToUniversalTime();
        return ValidateIntent(new OwnerPurgeIntent(
            purgeId,
            targetNodeId,
            targetRevision,
            WorkspaceZoneId,
            targetKind,
            refs,
            Array.Empty<string>(),
            NormalizeReason(reason),
            ExecutorKind.Owner,
            utc,
            utc,
            utc,
            PurgeIntentPhase.Applied,
            treeRevision,
            Array.Empty<PurgedResourceRef>()));
    }

    /// <summary>
    /// Appends one already-validated intent to a validated ledger. A repeated
    /// purge ID, a repeated identity or an exceeded bound refuses instead of
    /// silently rewriting the deletion record.
    /// </summary>
    public static OwnerPurgeLedgerState Append(OwnerPurgeLedgerState state, OwnerPurgeIntent intent)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(intent);
        var validated = ValidateAndNormalizeState(state);
        if (validated.Intents.Any(existing =>
            string.Equals(existing.PurgeId, intent.PurgeId, StringComparison.Ordinal)))
        {
            throw new ArgumentException("The purge intent already exists.");
        }

        if (validated.Intents.Length >= MaximumIntents)
        {
            throw new InvalidDataException("The purge ledger exceeds its bounds.");
        }

        return ValidateAndNormalizeState(validated with
        {
            Intents = validated.Intents.Append(ValidateIntent(intent)).ToArray()
        });
    }

    /// <summary>
    /// Fail-closed structural validation of the persisted ledger. An unknown
    /// schema version, a malformed intent or an exceeded bound is an error,
    /// never a silent repair: a corrupt deletion record must stop writes
    /// rather than forget what was purged.
    /// </summary>
    public static OwnerPurgeLedgerState ValidateAndNormalizeState(OwnerPurgeLedgerState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.SchemaVersion != OwnerPurgeLedgerState.CurrentSchemaVersion)
        {
            throw new InvalidDataException("The purge ledger schema version is unsupported.");
        }

        if (state.Intents is null || state.Intents.Length > MaximumIntents ||
            state.Intents.Any(intent => intent is null))
        {
            throw new InvalidDataException("The purge ledger exceeds its bounds.");
        }

        var normalized = state.Intents.OrderBy(intent => intent.PurgeId, StringComparer.Ordinal).ToArray();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < normalized.Length; index++)
        {
            var intent = ValidateIntent(normalized[index]);
            if (!ids.Add(intent.PurgeId))
            {
                throw new InvalidDataException("The purge ledger repeats a purge ID.");
            }

            normalized[index] = intent;
        }

        return new OwnerPurgeLedgerState(OwnerPurgeLedgerState.CurrentSchemaVersion, normalized);
    }

    internal static OwnerPurgeIntent ValidateIntent(OwnerPurgeIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        if (!Guid.TryParseExact(intent.PurgeId, "N", out _) ||
            string.IsNullOrEmpty(intent.TargetResourceId) ||
            !Domain.DomainIdentifiers.IsDomainIdentifier(intent.TargetResourceId) ||
            intent.TargetRevision < 1 ||
            string.IsNullOrEmpty(intent.TargetZoneId) ||
            !Domain.DomainIdentifiers.IsSafeIdentifier(intent.TargetZoneId) ||
            string.IsNullOrEmpty(intent.TargetKind) ||
            intent.Impacted is null || intent.Impacted.Length > MaximumImpactedPerPurge ||
            intent.Impacted.Any(item => item is null) ||
            intent.WithdrawnCatalogIds is null ||
            intent.Remaining is null || intent.Remaining.Length > MaximumImpactedPerPurge ||
            intent.Remaining.Any(item => item is null) ||
            !Enum.IsDefined(intent.Phase) || intent.Phase == PurgeIntentPhase.Unknown ||
            intent.FinalGeneration < 0 ||
            intent.CreatedAtUtc.Offset != TimeSpan.Zero ||
            (intent.ConfirmedAtUtc is { } confirmed &&
                (confirmed.Offset != TimeSpan.Zero || confirmed < intent.CreatedAtUtc)) ||
            (intent.CompletedAtUtc is { } completed &&
                (completed.Offset != TimeSpan.Zero || completed < intent.CreatedAtUtc)))
        {
            throw new InvalidDataException("The purge ledger intent is invalid.");
        }

        var reason = NormalizeReason(intent.Reason);
        var impacted = NormalizeRefs(intent.Impacted);
        var remaining = NormalizeRefs(intent.Remaining);
        var catalogIds = intent.WithdrawnCatalogIds.OrderBy(id => id, StringComparer.Ordinal).ToArray();
        if (catalogIds.Any(id => string.IsNullOrEmpty(id) || id.Length > 128) ||
            catalogIds.Distinct(StringComparer.Ordinal).Count() != catalogIds.Length)
        {
            throw new InvalidDataException("The purge ledger catalog record is invalid.");
        }

        if (intent.Phase == PurgeIntentPhase.Applied && intent.CompletedAtUtc is null)
        {
            throw new InvalidDataException("An applied purge intent records its completion time.");
        }

        return intent with
        {
            Impacted = impacted,
            Remaining = remaining,
            WithdrawnCatalogIds = catalogIds,
            Reason = reason
        };
    }

    internal static string NormalizeReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new InvalidDataException("A purge intent records the owner's reason.");
        }

        var trimmed = reason.Trim();
        if (trimmed.Length == 0 || trimmed.Length > MaximumReasonLength || trimmed.Any(char.IsControl))
        {
            throw new InvalidDataException("The purge reason is invalid.");
        }

        return trimmed;
    }

    private static PurgedResourceRef[] NormalizeRefs(PurgedResourceRef[] refs)
    {
        var normalized = refs
            .Select(item =>
            {
                if (string.IsNullOrEmpty(item.ResourceId) ||
                    !Domain.DomainIdentifiers.IsSafeIdentifier(item.ResourceId) ||
                    item.Revision < 1)
                {
                    throw new InvalidDataException("The purge ledger resource record is invalid.");
                }

                return item;
            })
            .OrderBy(item => item.ResourceId, StringComparer.Ordinal)
            .ToArray();
        if (normalized.Select(item => item.ResourceId).Distinct(StringComparer.Ordinal).Count() != normalized.Length)
        {
            throw new InvalidDataException("The purge ledger repeats a resource ID.");
        }

        return normalized;
    }

    internal static void EnsureOwner(AuthenticatedContext actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (actor.Kind != PrincipalKind.Owner)
        {
            throw new UnauthorizedAccessException("Only the owner may purge.");
        }
    }
}
