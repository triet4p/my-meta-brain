using MetaBrain.Core.Domain;
using MetaBrain.Core.Security;

namespace MetaBrain.Application;

/// <summary>One canonical resource a purge preview would delete.</summary>
public sealed record PurgeImpactEntry(
    string ResourceId,
    string Kind,
    long Revision,
    string Reason);

/// <summary>
/// Owner-facing purge impact preview: the exact canonical set a confirm
/// would delete (target first, then every impacted derivative in ID order),
/// the catalog entries that would be withdrawn, and the S8 handoff note.
/// The preview binds the live revisions it computed: a confirm carrying a
/// moved target revision fails closed, exactly like the existing grant and
/// recall preview contracts.
/// </summary>
public sealed record PurgeImpactPreview(
    string PurgeId,
    string TargetResourceId,
    long TargetRevision,
    string TargetZoneId,
    string TargetKind,
    PurgeImpactEntry[] Impacted,
    string[] WithdrawnCatalogIds,
    bool GrantsTouched,
    bool SessionsTouched,
    long LiveGeneration,
    string S8BackupNote);

/// <summary>One completed (or resumed) owner purge.</summary>
public sealed record PurgeResult(
    string PurgeId,
    string TargetResourceId,
    PurgedResourceRef[] Deleted,
    string[] WithdrawnCatalogIds,
    long FinalGeneration,
    bool DecisionApplied);

/// <summary>
/// S7-T5 owner purge authority over the shared encrypted canonical store
/// (R29/R08/R18). The owner previews the exact impacted canonical set,
/// then confirms with a typed PURGE confirmation. The confirm binds the
/// live target revision: a moved target fails closed with
/// <c>purge_target_moved</c> and deletes nothing.
/// <para>
/// Deletion covers the target plus every canonical derivative that names it:
/// the lifecycle record (<c>lc-</c>), every retained archive (<c>arc-</c>),
/// every relation with either endpoint on a deleted ID, every proposal or
/// reviewed item that cites a deleted ID, every feedback record on a
/// deleted ID, every ingestion journal and preserved snapshot of a deleted
/// source enrollment generation, and every source snapshot whose lineage
/// names a deleted source. Originals outside the vault (enrolled source
/// transcripts, exported files, agent-local copies) are never touched: the
/// owner must revoke or delete them through their own separate surface, and
/// the S8 backup contract notes that retained backups keep ciphertext until
/// their own retention expires.
/// </para>
/// <para>
/// The service never touches the recall policy: a demote/pin/archive of a
/// purged ID is refused by its subject check, and restoring purge is not a
/// recall act. Request/grant scopes and live sessions lose the purged IDs
/// through a policy-generation bump (the same invalidation that revokes
/// stale sessions); pending access-request previews that still name a purged
/// catalog are held stale.
/// </para>
/// </summary>
public sealed class PurgeService
{
    public const string S8BackupNoteText =
        "S8 backup contract: purge deletes live canonical bytes and the private index rows that described them; " +
        "it does not rewrite retained encrypted backups, external exports, or copies an agent already read. " +
        "A restore from a retained backup must re-apply this ledger, not resurrect purged IDs.";

    private readonly TimeProvider _timeProvider;

    public PurgeService(TimeProvider? timeProvider = null) =>
        _timeProvider = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// Previews one owner purge: binds the live target revision and computes
    /// the closed impacted set from live manifest state. Writes nothing.
    /// </summary>
    public PurgeImpactPreview Preview(
        AuthenticatedContext context,
        string targetResourceId,
        long targetRevision,
        string reason,
        IVaultOperation operation)
    {
        OwnerPurgeLedgerAuthority.EnsureOwner(context);
        ArgumentNullException.ThrowIfNull(operation);
        if (string.IsNullOrEmpty(targetResourceId) ||
            !DomainIdentifiers.IsDomainIdentifier(targetResourceId) ||
            targetRevision < 1)
        {
            throw new ArgumentException("The purge target and its live revision are required.");
        }

        _ = OwnerPurgeLedgerAuthority.NormalizeReason(reason);
        var ledger = OwnerPurgeLedgerAuthority.ValidateAndNormalizeState(
            operation.LoadPurgeLedgerState());
        if (OwnerPurgeLedgerAuthority.IsPurged(ledger, targetResourceId))
        {
            throw new ArgumentException("The target was already purged; it cannot be purged again.");
        }

        var live = operation.ListResources();
        var target = live.FirstOrDefault(resource =>
            string.Equals(resource.ResourceId, targetResourceId, StringComparison.Ordinal))
            ?? throw new ArgumentException("The purge target is not a managed resource.");
        if (target.Revision != targetRevision)
        {
            throw new ArgumentException("The purge target moved; take a new preview.");
        }

        if (!string.Equals(target.ZoneId, LiveZone(operation, targetResourceId), StringComparison.Ordinal))
        {
            throw new ArgumentException("The purge target zone moved; take a new preview.");
        }

        var impacted = ComputeImpacted(operation, live, target);
        var catalogState = OwnerCatalogAuthority.ValidateAndNormalizeState(operation.LoadCatalogState());
        var withdrawn = catalogState.Entries
            .Where(entry => impacted.Any(item =>
                string.Equals(item.ResourceId, entry.ResourceId, StringComparison.Ordinal)))
            .Select(entry => entry.CatalogId)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        var grantState = OwnerScopeGrantAuthority.ValidateAndMigrateState(operation.LoadScopeGrantState());
        var grantsTouched = grantState.Grants.Any(grant =>
            grant.Resources.Any(resource => impacted.Any(item =>
                string.Equals(item.ResourceId, resource.ResourceId, StringComparison.Ordinal))) ||
            grant.DestinationResources.Any(resource => impacted.Any(item =>
                string.Equals(item.ResourceId, resource.ResourceId, StringComparison.Ordinal))));
        var preview = new PurgeImpactPreview(
            Guid.NewGuid().ToString("N"),
            target.ResourceId,
            target.Revision,
            target.ZoneId,
            Classify(target.ResourceId),
            impacted,
            withdrawn,
            GrantsTouched: grantsTouched,
            SessionsTouched: grantsTouched,
            LiveGeneration: grantState.PolicyGeneration,
            S8BackupNoteText);
        return preview;
    }

    /// <summary>
    /// Confirms one owner purge preview: binds the exact impacted set the
    /// preview computed (fail closed on any change) and the live target
    /// revision, then deletes every canonical member, withdraws the mapped
    /// catalog entries, records the durable ledger entry, strips the purged
    /// IDs from grants/collections/pending requests with a policy-generation
    /// bump, and invalidates the materialized private index.
    /// </summary>
    public PurgeResult Confirm(
        AuthenticatedContext context,
        PurgeImpactPreview preview,
        string confirmation,
        string reason,
        IVaultOperation operation,
        Action? invalidateIndex)
    {
        OwnerPurgeLedgerAuthority.EnsureOwner(context);
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(operation);
        if (!string.Equals(confirmation, "PURGE", StringComparison.Ordinal))
        {
            throw new ArgumentException("Purge requires the typed PURGE confirmation.");
        }

        if (!Guid.TryParseExact(preview.PurgeId, "N", out _) ||
            string.IsNullOrEmpty(preview.TargetResourceId) ||
            !DomainIdentifiers.IsDomainIdentifier(preview.TargetResourceId) ||
            preview.TargetRevision < 1 ||
            preview.Impacted is null || preview.Impacted.Length == 0 ||
            preview.WithdrawnCatalogIds is null)
        {
            throw new ArgumentException("The purge preview is invalid.");
        }

        // Recompute the impact from live state and require byte-equality with
        // the preview the owner reviewed: a moved target or a changed
        // derivative set fails closed and deletes nothing.
        var fresh = Preview(context, preview.TargetResourceId, preview.TargetRevision, "purge-confirm-rebind", operation);
        if (!ImpactSetsEqual(fresh.Impacted, preview.Impacted, out var mismatch) ||
            !fresh.WithdrawnCatalogIds.SequenceEqual(preview.WithdrawnCatalogIds, StringComparer.Ordinal) ||
            fresh.LiveGeneration != preview.LiveGeneration)
        {
            _ = mismatch;
            throw new ArgumentException("The purge target or its impact changed; take a new preview.");
        }

        var now = _timeProvider.GetUtcNow().ToUniversalTime();
        var confirmedReason = OwnerPurgeLedgerAuthority.NormalizeReason(reason);
        var intent = new OwnerPurgeIntent(
            preview.PurgeId,
            preview.TargetResourceId,
            preview.TargetRevision,
            preview.TargetZoneId,
            preview.TargetKind,
            preview.Impacted.Select(item => new PurgedResourceRef(item.ResourceId, item.Revision)).ToArray(),
            preview.WithdrawnCatalogIds,
            confirmedReason,
            context.Kind == PrincipalKind.Owner ? ExecutorKind.Owner : ExecutorKind.Agent,
            now,
            now,
            null,
            PurgeIntentPhase.Confirmed,
            preview.LiveGeneration,
            preview.Impacted.Select(item => new PurgedResourceRef(item.ResourceId, item.Revision)).ToArray());
        intent = OwnerPurgeLedgerAuthority.ValidateIntent(intent);

        // Phase 1 (durable intent): the interruption checkpoint is committed
        // before the first canonical delete, so a crash here resumes with the
        // full remaining set instead of half-purging.
        operation.UpdatePurgeLedgerState(state =>
        {
            var validated = OwnerPurgeLedgerAuthority.ValidateAndNormalizeState(state);
            if (validated.Intents.Any(existing =>
                string.Equals(existing.PurgeId, intent.PurgeId, StringComparison.Ordinal)))
            {
                throw new ArgumentException("The purge preview was already confirmed.");
            }

            if (OwnerPurgeLedgerAuthority.IsPurged(validated, intent.TargetResourceId))
            {
                throw new ArgumentException("The target was already purged; it cannot be purged again.");
            }

            var next = validated with { Intents = validated.Intents.Append(intent).ToArray() };
            return (OwnerPurgeLedgerAuthority.ValidateAndNormalizeState(next), true);
        });

        var deleted = ApplyIntent(context, intent.PurgeId, operation, invalidateIndex);
        return deleted;
    }

    /// <summary>
    /// Resumes one confirmed-but-incomplete intent after an interruption
    /// (crash, lock, process kill): deletes whatever canonical members are
    /// still present with the same idempotent delete, then finalizes. A fully
    /// applied intent returns its stored outcome without deleting twice.
    /// </summary>
    public PurgeResult Resume(
        AuthenticatedContext context,
        string purgeId,
        IVaultOperation operation,
        Action? invalidateIndex)
    {
        OwnerPurgeLedgerAuthority.EnsureOwner(context);
        ArgumentNullException.ThrowIfNull(operation);
        if (!Guid.TryParseExact(purgeId, "N", out _))
        {
            throw new ArgumentException("The purge ID is invalid.");
        }

        return ApplyIntent(context, purgeId, operation, invalidateIndex);
    }

    private PurgeResult ApplyIntent(
        AuthenticatedContext context,
        string purgeId,
        IVaultOperation operation,
        Action? invalidateIndex)
    {
        var ledger = OwnerPurgeLedgerAuthority.ValidateAndNormalizeState(operation.LoadPurgeLedgerState());
        var intent = OwnerPurgeLedgerAuthority.FindIntent(ledger, purgeId)
            ?? throw new ArgumentException("The purge intent does not exist.");
        if (intent.Phase == PurgeIntentPhase.Applied)
        {
            return new PurgeResult(
                intent.PurgeId, intent.TargetResourceId, intent.Impacted,
                intent.WithdrawnCatalogIds, intent.FinalGeneration, DecisionApplied: false);
        }
        // Resume from the durable interruption checkpoint, never from a stale
        // snapshot read at entry. Confirmed (no delete finished) restarts from
        // the full bound set; Applying restarts from Remaining. After the first
        // idempotent delete below, no reader may see that ID again, so the
        // ledger must move to Applying with one fewer Remaining before the
        // next delete (a crash before this commit resumes the same ID with the
        // same idempotent delete; a crash after it never replays the
        // committed ID). Index invalidation runs only after that ledger commit,
        // so a torn index clear can never hide an uncommitted deletion.
        // Empty Remaining is authoritative: the deletes already finished and
        // the run must finalize, never replay the bound Impacted set.
        var seed = intent.Remaining;
        var ordered = seed.OrderBy(item => item.ResourceId, StringComparer.Ordinal).ToArray();
        foreach (var item in ordered)
        {
            operation.DeleteCanonicalResource(item.ResourceId);
            operation.UpdatePurgeLedgerState(state =>
            {
                var validated = OwnerPurgeLedgerAuthority.ValidateAndNormalizeState(state);
                var current = OwnerPurgeLedgerAuthority.FindIntent(validated, purgeId)
                    ?? throw new ArgumentException("The purge intent disappeared mid-apply.");
                var rest = current.Remaining
                    .Where(left => !string.Equals(left.ResourceId, item.ResourceId, StringComparison.Ordinal))
                    .ToArray();
                var moved = current with { Phase = PurgeIntentPhase.Applying, Remaining = rest };
                var intents = validated.Intents
                    .Select(candidate => string.Equals(candidate.PurgeId, purgeId, StringComparison.Ordinal) ? moved : candidate)
                    .ToArray();
                var nextState = OwnerPurgeLedgerAuthority.ValidateAndNormalizeState(
                    validated with { Intents = intents });
                return (nextState, true);
            });
            invalidateIndex?.Invoke();
        }

        // Phase 3 (scope finalization) is idempotent and resumable: catalog
        // withdrawal tolerates unknown entries, and the scope strip below is
        // a set subtraction from live state, so re-running it after a crash
        // in Finalizing converges without double effects. The generation bump
        // below runs only when live state still names a purged ID (or already
        // bumped past the intent's FinalGeneration has not happened yet);
        // once FinalGeneration is recorded, resume reports without rewriting.
        // A durable Finalizing claim keeps concurrent resumes from racing:
        // the winner below finalizes, losers only report the outcome. A crash
        // after the claim resumes here and completes the same idempotent
        // steps instead of wedging in Finalizing.
        var finalizer = operation.UpdatePurgeLedgerState(state =>
        {
            var validated = OwnerPurgeLedgerAuthority.ValidateAndNormalizeState(state);
            var current = OwnerPurgeLedgerAuthority.FindIntent(validated, purgeId)
                ?? throw new ArgumentException("The purge intent disappeared mid-apply.");
            if (current.Phase == PurgeIntentPhase.Applied)
            {
                return (validated, new PurgeResult(
                    current.PurgeId, current.TargetResourceId, current.Impacted,
                    current.WithdrawnCatalogIds, current.FinalGeneration, DecisionApplied: false));
            }

            var claiming = current.Phase == PurgeIntentPhase.Finalizing
                ? current
                : current with { Phase = PurgeIntentPhase.Finalizing };
            if (ReferenceEquals(claiming, current))
            {
                return (validated, (PurgeResult?)null);
            }

            var intents = validated.Intents
                .Select(candidate => string.Equals(candidate.PurgeId, purgeId, StringComparison.Ordinal) ? claiming : candidate)
                .ToArray();
            var nextState = OwnerPurgeLedgerAuthority.ValidateAndNormalizeState(
                validated with { Intents = intents });
            return (nextState, (PurgeResult?)null);
        });
        if (finalizer is not null)
        {
            return finalizer;
        }

        var finalGeneration = operation.UpdateScopeGrantState(state =>
        {
            var current = OwnerScopeGrantAuthority.ValidateAndMigrateState(state);
            var purged = intent.Impacted.Select(item => item.ResourceId).ToHashSet(StringComparer.Ordinal);
            var touchesPurged =
                current.Grants.Any(grant =>
                    grant.Resources.Any(resource => purged.Contains(resource.ResourceId)) ||
                    grant.DestinationResources.Any(resource => purged.Contains(resource.ResourceId))) ||
                current.Collections.Any(collection => collection.ResourceIds.Any(id => purged.Contains(id))) ||
                current.Requests.Any(request => request.ResolvedResources.Any(resource => purged.Contains(resource.ResourceId)));
            // No purged ID left in live scope state: a resumed Finalizing run
            // after a crash past the strip must not bump the generation again.
            if (!touchesPurged)
            {
                return (current, current.PolicyGeneration);
            }

            var grants = current.Grants
                .Select(grant => grant with
                {
                    Resources = grant.Resources.Where(resource => !purged.Contains(resource.ResourceId)).ToArray(),
                    DestinationResources = grant.DestinationResources.Where(resource => !purged.Contains(resource.ResourceId)).ToArray()
                })
                .Where(grant => grant.Resources.Length > 0 || grant.DestinationResources.Length > 0 ||
                    grant.Operations.Length == 0)
                .ToArray();
            var collections = current.Collections
                .Select(collection => collection with
                {
                    ResourceIds = collection.ResourceIds.Where(id => !purged.Contains(id)).ToArray()
                })
                .Where(collection => collection.ResourceIds.Length > 0)
                .ToArray();
            var requests = current.Requests
                .Select(request => request with
                {
                    ResolvedResources = request.ResolvedResources.Where(resource => !purged.Contains(resource.ResourceId)).ToArray()
                })
                .ToArray();
            var nextState = OwnerScopeGrantAuthority.ValidateAndMigrateState(current with
            {
                PolicyGeneration = current.PolicyGeneration == long.MaxValue
                    ? throw new IOException("The owner scope policy generation is exhausted.")
                    : current.PolicyGeneration + 1,
                Grants = grants,
                Collections = collections,
                Requests = requests
            });
            return (nextState, nextState.PolicyGeneration);
        });

        foreach (var catalogId in intent.WithdrawnCatalogIds)
        {
            try
            {
                new OwnerCatalogAuthority().Withdraw(context, catalogId, operation);
            }
            catch (OwnerCatalogValidationException ex) when (string.Equals(ex.Code, "catalog_unknown", StringComparison.Ordinal))
            {
                // Already withdrawn (e.g. a resumed attempt): idempotent.
            }
        }

        // S7-T6 deletion policy over source enrollment: a purged source's
        // enrollment is revoked in the same run, so the deleted source cannot
        // keep being ingested, refreshed or probed afterwards. The step is
        // idempotent and re-runs on resume; the original file outside the
        // vault is untouched, exactly like the rest of the purge.
        RevokePurgedEnrollments(operation, intent.Impacted.Select(item => item.ResourceId).ToArray());

        invalidateIndex?.Invoke();

        var completedAt = _timeProvider.GetUtcNow().ToUniversalTime();
        operation.UpdatePurgeLedgerState(state =>
        {
            var validated = OwnerPurgeLedgerAuthority.ValidateAndNormalizeState(state);
            var current = OwnerPurgeLedgerAuthority.FindIntent(validated, purgeId)
                ?? throw new ArgumentException("The purge intent disappeared mid-apply.");
            var done = OwnerPurgeLedgerAuthority.ValidateIntent(current with
            {
                Phase = PurgeIntentPhase.Applied,
                CompletedAtUtc = completedAt,
                FinalGeneration = finalGeneration,
                Remaining = Array.Empty<PurgedResourceRef>()
            });
            var intents = validated.Intents
                .Select(candidate => string.Equals(candidate.PurgeId, purgeId, StringComparison.Ordinal) ? done : candidate)
                .ToArray();
            return (OwnerPurgeLedgerAuthority.ValidateAndNormalizeState(validated with { Intents = intents }), true);
        });

        return new PurgeResult(
            intent.PurgeId, intent.TargetResourceId, intent.Impacted,
            intent.WithdrawnCatalogIds, finalGeneration, DecisionApplied: true);
    }

    /// <summary>
    /// Revokes the enrollment of every purged source ID. The enrollment is
    /// owner-private state beside the vault, so the deletion policy has to
    /// reach it explicitly: without this, an enrollment of a deleted source
    /// could keep ingesting and probing the original file. The step is
    /// idempotent (an already revoked enrollment is left alone) and it never
    /// touches the original file outside the vault.
    /// </summary>
    private static void RevokePurgedEnrollments(IVaultOperation operation, IReadOnlyCollection<string> purgedIds)
    {
        if (purgedIds.Count == 0)
        {
            return;
        }

        operation.UpdateSourceEnrollmentState(state =>
        {
            var validated = SourceEnrollmentAuthority.ValidateAndNormalizeState(state);
            if (validated.Enrollments.Length == 0)
            {
                return (state, false);
            }

            var revokedAt = DateTimeOffset.UtcNow;
            var changed = false;
            var enrollments = validated.Enrollments.Select(enrollment =>
            {
                if (enrollment.RevokedAtUtc is not null ||
                    !purgedIds.Contains(enrollment.SourceId))
                {
                    return enrollment;
                }

                changed = true;
                return enrollment with { RevokedAtUtc = revokedAt };
            }).ToArray();
            return changed ? (validated with { Enrollments = enrollments }, true) : (state, false);
        });
    }

    private static bool ImpactSetsEqual(PurgeImpactEntry[] left, PurgeImpactEntry[] right, out string mismatch)
    {
        mismatch = string.Empty;
        if (left.Length != right.Length)
        {
            mismatch = "count " + left.Length + " vs " + right.Length;
            return false;
        }

        var orderedLeft = left.OrderBy(item => item.ResourceId, StringComparer.Ordinal).ToArray();
        var orderedRight = right.OrderBy(item => item.ResourceId, StringComparer.Ordinal).ToArray();
        for (var index = 0; index < orderedLeft.Length; index++)
        {
            if (!string.Equals(orderedLeft[index].ResourceId, orderedRight[index].ResourceId, StringComparison.Ordinal) ||
                orderedLeft[index].Revision != orderedRight[index].Revision ||
                !string.Equals(orderedLeft[index].Kind, orderedRight[index].Kind, StringComparison.Ordinal))
            {
                mismatch = orderedLeft[index].ResourceId + "@" + orderedLeft[index].Revision + "/" + orderedLeft[index].Kind +
                    " vs " + orderedRight[index].ResourceId + "@" + orderedRight[index].Revision + "/" + orderedRight[index].Kind;
                return false;
            }
        }

        return true;
    }

    private static string LiveZone(IManagedResourceStore store, string resourceId) =>
        store.TryGetZone(resourceId, out var zone) && !string.IsNullOrEmpty(zone)
            ? zone!
            : throw new ArgumentException("The purge target zone is unavailable.");

    private static string Classify(string resourceId)
    {
        if (!string.IsNullOrEmpty(resourceId) && DomainIdentifiers.IsDomainIdentifier(resourceId))
        {
            return "memory-or-source";
        }

        return "canonical";
    }

    /// <summary>
    /// Computes the closed impacted set from live manifest state: the target
    /// plus every canonical derivative that names it (lifecycle, archives,
    /// relations, citing proposals/reviewed items, feedback, journals,
    /// snapshots and snapshot lineage on a deleted source). The walk closes
    /// transitively: deleting a relation endpoint never deletes another item,
    /// but deleting an item deletes every canonical record that pins it.
    /// Provenance citation anchors (an item citing a source) do NOT pull the
    /// cited source in, and declassification lineage does NOT pull the shared
    /// derivative out: the shared copy stays readable and is only marked
    /// stale by its existing review evaluation.
    /// </summary>
    private PurgeImpactEntry[] ComputeImpacted(
        IManagedResourceStore store,
        IReadOnlyList<ScopeResourceRevision> live,
        ScopeResourceRevision target)
    {
        var byId = live.ToDictionary(resource => resource.ResourceId, resource => resource, StringComparer.Ordinal);
        var impacted = new Dictionary<string, PurgeImpactEntry>(StringComparer.Ordinal);
        void Add(string resourceId, string reason)
        {
            if (!byId.TryGetValue(resourceId, out var entry) || impacted.ContainsKey(resourceId))
            {
                return;
            }

            impacted[resourceId] = new PurgeImpactEntry(
                entry.ResourceId, KindOf(store, entry.ResourceId), entry.Revision, reason);
        }

        Add(target.ResourceId, "purge-target");
        var changed = true;
        while (changed)
        {
            changed = false;
            var snapshot = impacted.Keys.ToArray();
            foreach (var candidate in live)
            {
                if (impacted.ContainsKey(candidate.ResourceId))
                {
                    continue;
                }

                if (IsDerivedOf(candidate.ResourceId, snapshot))
                {
                    Add(candidate.ResourceId, "derived-record");
                    changed = true;
                    continue;
                }

                if (NamesAnyPurgedId(store, candidate, snapshot))
                {
                    Add(candidate.ResourceId, "pins-purged-id");
                    changed = true;
                }
            }
        }

        return impacted.Values.OrderBy(item => item.ResourceId, StringComparer.Ordinal).ToArray();
    }

    private static bool IsDerivedOf(string candidateId, string[] purgedIds)
    {
        foreach (var purged in purgedIds)
        {
            if (string.Equals(candidateId, DomainIdentifiers.LifecycleIdPrefix + purged, StringComparison.Ordinal) ||
                (candidateId.StartsWith(DomainIdentifiers.ArchiveIdPrefix, StringComparison.Ordinal) &&
                    candidateId.Contains(purged, StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when the candidate canonical record pins, cites, or retains a
    /// purged ID at its pinned revision: relations with either endpoint on a
    /// purged ID; proposals (or their reviewed items) whose subject or
    /// citations name a purged ID; feedback on a purged ID; ingestion
    /// journals/snapshots of a purged source generation; source snapshots
    /// whose stored lineage names a purged source.
    /// Citation direction matters: a memory item that merely cites a source
    /// does not pull the cited source in, and a shared declassified copy
    /// whose lineage relation names a purged private pin is kept out (the
    /// copy stays readable; its review already reports stale).
    /// </summary>
    private static bool NamesAnyPurgedId(
        IManagedResourceReader store,
        ScopeResourceRevision candidate,
        string[] purgedIds)
    {
        byte[]? raw = null;
        try
        {
            raw = store.ReadContent(candidate.ResourceId, candidate.Revision);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return false;
        }

        try
        {
            try
            {
                var relation = RelationCodec.DecodeStored(raw);
                if (!string.Equals(relation.Id, candidate.ResourceId, StringComparison.Ordinal))
                {
                    return false;
                }

                // A shared declassification derivative keeps private lineage
                // through accepted derived_from relations: deleting the
                // private pin must NOT delete the shared copy (withdrawal
                // would widen the blast radius past what the owner reviewed).
                if (relation.Kind == RelationKind.DerivedFrom)
                {
                    return false;
                }

                return purgedIds.Any(id =>
                    string.Equals(relation.From.ResourceId, id, StringComparison.Ordinal) ||
                    string.Equals(relation.To.ResourceId, id, StringComparison.Ordinal));
            }
            catch (ArgumentException)
            {
            }

            try
            {
                var proposal = ProposalCodec.DecodeStored(raw);
                if (!string.Equals(proposal.Id, candidate.ResourceId, StringComparison.Ordinal))
                {
                    return false;
                }

                if (proposal.SubjectResourceId is not null &&
                    purgedIds.Contains(proposal.SubjectResourceId, StringComparer.Ordinal))
                {
                    return true;
                }

                return proposal.Item.Sources?.Any(source =>
                    source.SourceId is not null &&
                    purgedIds.Contains(source.SourceId, StringComparer.Ordinal)) == true;
            }
            catch (ArgumentException)
            {
            }

            try
            {
                var feedback = FeedbackCodec.DecodeStored(raw);
                if (!string.Equals(feedback.Id, candidate.ResourceId, StringComparison.Ordinal))
                {
                    return false;
                }

                return purgedIds.Contains(feedback.TargetResourceId, StringComparer.Ordinal);
            }
            catch (ArgumentException)
            {
            }

            try
            {
                var snapshot = SourceSnapshotCodec.DecodeStored(raw, candidate.Revision);
                if (!string.Equals(snapshot.Stored.Id, candidate.ResourceId, StringComparison.Ordinal))
                {
                    return false;
                }

                return purgedIds.Contains(snapshot.Stored.SourceId, StringComparer.Ordinal);
            }
            catch (ArgumentException)
            {
            }

            try
            {
                var journal = SourceIngestionJournalCodec.Decode(raw);
                if (!string.Equals(SourceIngestionIds.JournalIdFor(journal.SourceId, journal.Generation), candidate.ResourceId, StringComparison.Ordinal))
                {
                    return false;
                }

                return purgedIds.Contains(journal.SourceId, StringComparer.Ordinal);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidDataException)
            {
            }

            try
            {
                var lifecycle = LifecycleCodec.DecodeStored(raw);
                if (!string.Equals(lifecycle.Id, candidate.ResourceId, StringComparison.Ordinal))
                {
                    return false;
                }

                if (purgedIds.Contains(lifecycle.SubjectResourceId, StringComparer.Ordinal))
                {
                    return true;
                }

                return lifecycle.RetainedRevisions.Any(retained =>
                    purgedIds.Contains(retained.ArchiveResourceId, StringComparer.Ordinal));
            }
            catch (ArgumentException)
            {
            }

            try
            {
                var memory = MemoryEnvelopeCodec.DecodeStored(raw);
                if (!string.Equals(memory.Id, candidate.ResourceId, StringComparison.Ordinal))
                {
                    return false;
                }

                // Reviewed items are canonical memory items whose ID derives
                // from a pending proposal: a proposal citing a purged ID pulls
                // its reviewed item in.
                if (candidate.ResourceId.StartsWith(DomainIdentifiers.ReviewedItemIdPrefix, StringComparison.Ordinal))
                {
                    if (store is IManagedResourceStore fullStore)
                    {
                        foreach (var proposalId in fullStore.ListResources()
                            .Where(resource => DomainIdentifiers.IsProposalId(resource.ResourceId))
                            .Select(resource => resource.ResourceId))
                        {
                            if (!DomainIdentifiers.ReviewedItemIdFor(proposalId).Equals(candidate.ResourceId, StringComparison.Ordinal))
                            {
                                continue;
                            }

                            byte[]? proposalRaw = null;
                            try
                            {
                                var liveRevision = fullStore.ListResources().FirstOrDefault(resource =>
                                    string.Equals(resource.ResourceId, proposalId, StringComparison.Ordinal))?.Revision;
                                if (liveRevision is null)
                                {
                                    continue;
                                }

                                proposalRaw = fullStore.ReadContent(proposalId, liveRevision.Value);
                                var proposal = ProposalCodec.DecodeStored(proposalRaw);
                                if (proposal.Item.Sources?.Any(source =>
                                    source.SourceId is not null &&
                                    purgedIds.Contains(source.SourceId, StringComparer.Ordinal)) == true)
                                {
                                    return true;
                                }
                            }
                            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
                            {
                            }
                            finally
                            {
                                if (proposalRaw is { Length: > 0 })
                                {
                                    System.Security.Cryptography.CryptographicOperations.ZeroMemory(proposalRaw);
                                }
                            }
                        }
                    }
                }

                return false;
            }
            catch (ArgumentException)
            {
            }

            return false;
        }
        finally
        {
            if (raw is { Length: > 0 })
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(raw);
            }
        }
    }

    private static string KindOf(IManagedResourceReader store, string resourceId)
    {
        byte[]? raw = null;
        try
        {
            var revision = store is IManagedResourceStore full
                ? full.ListResources().FirstOrDefault(resource =>
                    string.Equals(resource.ResourceId, resourceId, StringComparison.Ordinal))?.Revision ?? 0
                : 0;
            if (revision < 1)
            {
                return "canonical";
            }

            raw = store.ReadContent(resourceId, revision);
            try
            {
                _ = RelationCodec.DecodeStored(raw);
                return "relation";
            }
            catch (ArgumentException)
            {
            }

            try
            {
                _ = LifecycleCodec.DecodeStored(raw);
                return "lifecycle";
            }
            catch (ArgumentException)
            {
            }

            try
            {
                _ = ProposalCodec.DecodeStored(raw);
                return "proposal";
            }
            catch (ArgumentException)
            {
            }

            try
            {
                _ = FeedbackCodec.DecodeStored(raw);
                return "feedback";
            }
            catch (ArgumentException)
            {
            }

            try
            {
                _ = SourceSnapshotCodec.DecodeStored(raw, revision);
                return "snapshot";
            }
            catch (ArgumentException)
            {
            }

            try
            {
                _ = SourceIngestionJournalCodec.Decode(raw);
                return "journal";
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidDataException)
            {
            }

            try
            {
                _ = MemoryEnvelopeCodec.DecodeStored(raw);
                return "memory";
            }
            catch (ArgumentException)
            {
            }

            try
            {
                _ = RegisteredSourceCodec.DecodeStored(raw);
                return "source";
            }
            catch (ArgumentException)
            {
            }

            return "canonical";
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return "canonical";
        }
        finally
        {
            if (raw is { Length: > 0 })
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(raw);
            }
        }
    }
}
