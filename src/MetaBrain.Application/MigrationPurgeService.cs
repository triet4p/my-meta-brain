using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MetaBrain.Core.Domain;
using MetaBrain.Core.Security;

namespace MetaBrain.Application;

/// <summary>
/// S18-T4 retention, purge and portability across both representations of a
/// migrated original. The canonical <see cref="PurgeService"/> already owns
/// the one durable purge ledger and the closed canonical impact; this service
/// binds that canonical side to the workspace node, its retained revisions and
/// the encrypted reverse map, and discloses the retention/backup and
/// out-of-control boundaries the act does not cross.
/// <para>
/// Every mutation is owner-only, idempotent and resumable: the canonical purge
/// intent reserves the legacy identities, the workspace purge reserves the
/// node identity, and the map entry is the last thing removed. No original
/// session file, exported copy outside the vault or agent-local copy is ever
/// opened, edited or deleted.
/// </para>
/// </summary>
internal sealed class MigrationPurgeService
{
    internal const string RetainedBackupNote =
        "Retained encrypted backups are not rewritten by this purge. A later backup carries the " +
        "vault's one durable purge ledger and a restore re-applies it, so restoring such a backup " +
        "cannot resurrect a purged identity. Backups taken earlier still hold their own ciphertext " +
        "until their separate retention expires.";

    internal const string OutsideControlNote =
        "Original session files outside the vault (.codex/.omp), plaintext exports taken earlier " +
        "and copies an agent already read are outside Meta Brain's control. This purge never opens, " +
        "edits or deletes them and cannot recall them; the owner handles those through their own " +
        "surfaces.";

    internal const string ExportDisclosure =
        "The selected file is exported as its exact retained original bytes at the pinned revision; " +
        "the sidecar carries migration provenance and explicitly labels annotations, the reverse map, " +
        "the passage index and embeddings as generated or rebuildable representations, never originals. " +
        "This plaintext copy sits outside the vault and cannot be recalled; the vault is unchanged.";

    private readonly RecordMigrationService _migrations;
    private readonly PurgeService _purges;

    public MigrationPurgeService(RecordMigrationService migrations, PurgeService purges)
    {
        _migrations = migrations ?? throw new ArgumentNullException(nameof(migrations));
        _purges = purges ?? throw new ArgumentNullException(nameof(purges));
    }

    /// <summary>Write-free cross-representation impact preview for one migrated original.</summary>
    public MigrationPurgePreview Preview(
        AuthenticatedContext context,
        IVaultOperation operation,
        string vaultId,
        long manifestRevision,
        string legacyId)
    {
        OwnerPurgeLedgerAuthority.EnsureOwner(context);
        if (string.IsNullOrEmpty(legacyId))
        {
            throw new ArgumentException("The migrated legacy id is required.");
        }

        var resources = operation.ListResources();
        var ledger = OwnerPurgeLedgerAuthority.ValidateAndNormalizeState(operation.LoadPurgeLedgerState());
        if (OwnerPurgeLedgerAuthority.IsPurged(ledger, legacyId))
        {
            throw new MigrationRefusalException(MigrationCodes.AlreadyPurged,
                "The original was already purged; nothing was previewed.");
        }

        var map = LoadMap(operation, resources, vaultId, out var mapRevision);
        var entry = map.Entries.FirstOrDefault(item =>
            string.Equals(item.LegacyId, legacyId, StringComparison.Ordinal))
            ?? throw new MigrationRefusalException(MigrationCodes.NotMigrated,
                "The id is not a migrated original; nothing was previewed.");
        if (OwnerPurgeLedgerAuthority.IsPurged(ledger, entry.NodeId))
        {
            throw new MigrationRefusalException(MigrationCodes.AlreadyPurged,
                "The migrated workspace node was already purged; nothing was previewed.");
        }

        var tree = operation.ReadWorkspaceTree();
        var node = tree?.Nodes.FirstOrDefault(item =>
            string.Equals(item.Id, entry.NodeId, StringComparison.Ordinal))
            ?? throw new MigrationRefusalException(MigrationCodes.InconsistentState,
                "The migrated original no longer has its recorded workspace node.");
        if (node.Kind != WorkspaceNodeKind.File)
        {
            throw new MigrationRefusalException(MigrationCodes.InconsistentState,
                "The migrated original's workspace node is not a file.");
        }

        var impact = new List<MigrationPurgeImpact>();
        foreach (var canonical in ComputeCanonicalImpact(
                     operation, resources, legacyId, entry.NodeId, mapRevision))
        {
            impact.Add(canonical);
        }

        // Annotations are generated cognitive layers; the canonical purge walk
        // does not close over them, so name every annotation that pins either
        // face of this original explicitly.
        foreach (var annotation in FindReferencingAnnotations(
                     operation, resources, legacyId, entry.NodeId))
        {
            impact.Add(new MigrationPurgeImpact(
                "annotation", annotation.ResourceId, annotation.Revision,
                "generated annotation naming the purged original"));
        }

        impact.Add(new MigrationPurgeImpact(
            "map-entry", MigrationState.MapResourceId, mapRevision,
            "encrypted legacy-id to node-id binding removed"));
        impact.Add(new MigrationPurgeImpact(
            "workspace-node", node.Id, node.Revision, "migrated original node removed"));
        impact.Add(new MigrationPurgeImpact(
            "workspace-revision", node.Id, node.ContentRevision, "exact-byte original revision erased"));
        foreach (var retained in node.Revisions
                     .Where(retained => retained.Revision != node.ContentRevision)
                     .OrderBy(retained => retained.Revision))
        {
            impact.Add(new MigrationPurgeImpact(
                "retained-revision", node.Id, retained.Revision,
                "retained original-byte revision erased"));
        }

        // Rebuildable derivatives are invalidated, never a hidden second
        // truth: their next generation is derived from the remaining canonical
        // records, so a purged identity cannot return through a rebuild.
        impact.Add(new MigrationPurgeImpact(
            "passage-index", "workspace-passage-index/passages.v1.bin", 0,
            "rebuildable encrypted derivative; invalidated, not a truth copy"));
        impact.Add(new MigrationPurgeImpact(
            "embedding", "workspace-passage-embedding", 0,
            "rebuildable local-model derivative; invalidated, not a truth copy"));

        var ordered = impact
            .OrderBy(item => item.Category, StringComparer.Ordinal)
            .ThenBy(item => item.ResourceId, StringComparer.Ordinal)
            .ThenBy(item => item.Revision)
            .ToArray();
        var token = MigrationState.ComputePurgeToken(
            vaultId, manifestRevision, tree.Revision, mapRevision, legacyId, entry.LegacyRevision,
            node.Id, node.ContentRevision, ordered);
        var disclosure =
            "This purge permanently erases the listed owner-controlled canonical records, workspace " +
            "node, retained revisions and reverse-map entry inside this vault. Retained encrypted " +
            "backups keep ciphertext until their own retention expires, and a restore re-applies the " +
            "durable purge ledger so no purged identity returns. The generated passage index and " +
            "embeddings are rebuildable derivatives, never a second truth. " +
            RetainedBackupNote + " " + OutsideControlNote;

        return new MigrationPurgePreview(
            token, legacyId, entry.LegacyRevision, node.Id, node.Revision, node.ContentRevision,
            tree.Revision, manifestRevision, mapRevision, ordered,
            new[] { RetainedBackupNote }, new[] { OutsideControlNote }, disclosure);
    }

    /// <summary>
    /// Confirms a fresh preview. The canonical purge (durable ledger intent,
    /// closed derivative deletion, catalog/scope finalization) commits first;
    /// then the workspace deletion group is tombstoned and purged into the same
    /// ledger; the reverse-map entry is removed last. Each step tolerates a
    /// partially applied predecessor so <see cref="Resume"/> can finish it.
    /// </summary>
    public MigrationPurgeResult Confirm(
        AuthenticatedContext context,
        IVaultOperation operation,
        string vaultId,
        long manifestRevision,
        MigrationPurgePreview preview,
        string confirmation,
        string reason,
        Action? invalidateIndex)
    {
        OwnerPurgeLedgerAuthority.EnsureOwner(context);
        ArgumentNullException.ThrowIfNull(preview);
        if (!string.Equals(confirmation, "PURGE", StringComparison.Ordinal))
        {
            throw new MigrationRefusalException(MigrationCodes.Confirmation,
                "A migration purge needs the typed PURGE confirmation; nothing was purged.");
        }

        var fresh = Preview(context, operation, vaultId, manifestRevision, preview.LegacyId);
        if (!string.Equals(fresh.Token, NormalizeToken(preview.Token), StringComparison.Ordinal) ||
            !fresh.OwnerControlled.Select(item => item.ResourceId + ":" + item.Revision.ToString(CultureInfo.InvariantCulture))
                .SequenceEqual(preview.OwnerControlled
                    .Select(item => item.ResourceId + ":" + item.Revision.ToString(CultureInfo.InvariantCulture)),
                    StringComparer.Ordinal))
        {
            throw new MigrationRefusalException(MigrationCodes.StalePreview,
                "The vault, workspace or migration map changed since the preview; nothing was purged.");
        }

        var purgeId = CommitCanonicalIntent(
            context, operation, preview, reason);
        var canonical = _purges.Resume(context, purgeId, operation, invalidateIndex);
        var workspaceRemoved = PurgeWorkspaceNode(operation, preview.NodeId, reason);
        var mapRemoved = RemoveMapEntry(operation, vaultId, preview.LegacyId);
        return new MigrationPurgeResult(
            Guid.NewGuid().ToString("N"), preview.LegacyId, preview.NodeId, purgeId,
            canonical.Deleted.Select(item => item.ResourceId).ToArray(),
            workspaceRemoved, mapRemoved, canonical.FinalGeneration, true);
    }

    /// <summary>
    /// Resumes a confirmed-but-incomplete migration purge. The canonical purge
    /// is resumed from its durable intent, the workspace node is purged from
    /// its durable reservation, and the map entry is removed again if needed.
    /// Every step is idempotent; a purged identity is never recreated.
    /// </summary>
    public MigrationPurgeResult Resume(
        AuthenticatedContext context,
        IVaultOperation operation,
        string vaultId,
        string legacyId,
        string canonicalPurgeId,
        string nodeId,
        string reason,
        Action? invalidateIndex)
    {
        OwnerPurgeLedgerAuthority.EnsureOwner(context);
        if (string.IsNullOrEmpty(legacyId) || string.IsNullOrEmpty(nodeId))
        {
            throw new ArgumentException("The legacy id and node id are required.");
        }

        // An empty canonical id means the legacy representation was already
        // purged through its own owner surface: re-check the reservation and
        // finish the workspace and map halves without replaying it.
        var canonicalPurge = string.Empty;
        if (!string.IsNullOrEmpty(canonicalPurgeId))
        {
            var canonical = _purges.Resume(context, canonicalPurgeId, operation, invalidateIndex);
            canonicalPurge = canonical.PurgeId;
        }

        var workspaceRemoved = PurgeWorkspaceNode(operation, nodeId, reason);
        var mapRemoved = RemoveMapEntry(operation, vaultId, legacyId);
        var ledger = OwnerPurgeLedgerAuthority.ValidateAndNormalizeState(operation.LoadPurgeLedgerState());
        if (!OwnerPurgeLedgerAuthority.IsPurged(ledger, legacyId) &&
            !IsLegacyPurged(operation, legacyId))
        {
            throw new MigrationRefusalException(MigrationCodes.InconsistentState,
                "The legacy original is not reserved or purged; nothing was resumed.");
        }

        return new MigrationPurgeResult(
            Guid.NewGuid().ToString("N"), legacyId, nodeId, canonicalPurge,
            Array.Empty<string>(), workspaceRemoved, mapRemoved,
            ledger.Intents.Length, DecisionApplied: true);
    }

    /// <summary>
    /// Exports one migrated original as exact bytes plus a provenance sidecar.
    /// The destination must not already exist; a stale map or a changed
    /// retained revision refuses before anything is written.
    /// </summary>
    public MigrationExportInfo Export(
        AuthenticatedContext context,
        IVaultOperation operation,
        string vaultId,
        string legacyId,
        string outputDirectory,
        string confirmation,
        CancellationToken cancellationToken)
    {
        OwnerPurgeLedgerAuthority.EnsureOwner(context);
        if (!string.Equals(confirmation, "EXPORT", StringComparison.Ordinal))
        {
            throw new MigrationRefusalException(MigrationCodes.Confirmation,
                "An original export needs the typed EXPORT confirmation; nothing was written.");
        }

        if (string.IsNullOrWhiteSpace(legacyId) || string.IsNullOrWhiteSpace(outputDirectory))
        {
            throw new ArgumentException("The legacy id and output directory are required.");
        }

        var map = LoadMap(operation, operation.ListResources(), vaultId, out _);
        var entry = map.Entries.FirstOrDefault(item =>
            string.Equals(item.LegacyId, legacyId, StringComparison.Ordinal))
            ?? throw new MigrationRefusalException(MigrationCodes.NotMigrated,
                "The id is not a migrated original; nothing was exported.");
        var tree = operation.ReadWorkspaceTree();
        var node = tree?.Nodes.FirstOrDefault(item =>
            string.Equals(item.Id, entry.NodeId, StringComparison.Ordinal));
        if (node is null || node.Kind != WorkspaceNodeKind.File ||
            node.ContentRevision != entry.ContentRevision)
        {
            throw new MigrationRefusalException(MigrationCodes.InconsistentState,
                "The migrated original no longer matches its recorded revision; nothing was exported.");
        }

        var content = operation.ReadWorkspaceFile(node.Id, entry.ContentRevision);
        byte[]? sidecarBytes = null;
        try
        {
            if (content.Length != entry.Length ||
                !string.Equals(content.Sha256, entry.Sha256, StringComparison.Ordinal) ||
                !string.Equals(
                    Convert.ToHexString(SHA256.HashData(content.Content)).ToLowerInvariant(),
                    entry.Sha256, StringComparison.Ordinal))
            {
                throw new MigrationRefusalException(MigrationCodes.InconsistentState,
                    "The retained bytes no longer match the encrypted map; nothing was exported.");
            }

            var fullPath = Path.GetFullPath(outputDirectory);
            if (Directory.Exists(fullPath) && Directory.EnumerateFileSystemEntries(fullPath).Any())
            {
                throw new MigrationRefusalException(MigrationCodes.ExportScope,
                    "The export destination already exists and is not empty; nothing was written.");
            }

            var originalPath = Path.Combine(fullPath, "original.bin");
            var sidecarPath = Path.Combine(fullPath, "metadata.json");
            var generatedLabels = new[]
            {
                "migration-map: generated provenance, not an original",
                "cognitive annotations: generated interpretation, not original bytes",
                "passage index: rebuildable encrypted derivative, not an original",
                "passage embeddings: rebuildable local-model derivative, not an original"
            };
            var metadata = new Dictionary<string, object?>
            {
                ["formatVersion"] = 1,
                ["legacyId"] = entry.LegacyId,
                ["legacyRevision"] = entry.LegacyRevision,
                ["family"] = entry.Family,
                ["nodeId"] = entry.NodeId,
                ["contentRevision"] = entry.ContentRevision,
                ["length"] = entry.Length,
                ["sha256"] = entry.Sha256,
                ["zoneId"] = entry.ZoneId,
                ["sourceId"] = entry.SourceId,
                ["generatedRepresentations"] = generatedLabels,
                ["disclosure"] = ExportDisclosure
            };
            sidecarBytes = JsonSerializer.SerializeToUtf8Bytes(metadata);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(fullPath);
            File.WriteAllBytes(originalPath, content.Content);
            File.WriteAllBytes(sidecarPath, sidecarBytes);

            var files = new[]
            {
                new MigrationExportFile(originalPath, content.Length, entry.Sha256),
                new MigrationExportFile(sidecarPath, sidecarBytes.LongLength,
                    Convert.ToHexString(SHA256.HashData(sidecarBytes)).ToLowerInvariant())
            };
            return new MigrationExportInfo(
                legacyId, node.Id, entry.ContentRevision, content.Length, entry.Sha256,
                fullPath, files, generatedLabels, ExportDisclosure);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(content.Content);
            if (sidecarBytes is { Length: > 0 })
            {
                CryptographicOperations.ZeroMemory(sidecarBytes);
            }
        }
    }

    private static bool PurgeWorkspaceNode(IVaultOperation operation, string nodeId, string reason)
    {
        var tree = operation.ReadWorkspaceTree();
        var node = tree?.Nodes.FirstOrDefault(item =>
            string.Equals(item.Id, nodeId, StringComparison.Ordinal));
        if (node is null &&
            !operation.ReadWorkspaceTombstones().Any(tombstone =>
                string.Equals(tombstone.NodeId, nodeId, StringComparison.Ordinal)))
        {
            return true; // already absent: idempotent
        }

        if (node is not null)
        {
            var delete = operation.PreviewWorkspaceDelete(nodeId);
            operation.DeleteWorkspaceNode(
                nodeId, delete.TreeRevision, delete.NodeRevision, delete.Preview);
        }

        var purge = operation.PreviewWorkspacePurge(nodeId);
        operation.PurgeWorkspaceNode(nodeId, reason, purge.TreeRevision, purge.Preview);
        return true;
    }

    private static bool RemoveMapEntry(IVaultOperation operation, string vaultId, string legacyId)
    {
        var map = LoadMap(operation, operation.ListResources(), vaultId, out var revision);
        if (revision == 0 || map.Entries.All(entry =>
                !string.Equals(entry.LegacyId, legacyId, StringComparison.Ordinal)))
        {
            return true; // already removed
        }

        var remaining = map.Entries
            .Where(entry => !string.Equals(entry.LegacyId, legacyId, StringComparison.Ordinal))
            .ToArray();
        if (remaining.Length == 0)
        {
            operation.DeleteCanonicalResource(MigrationState.MapResourceId);
            return true;
        }

        var updated = new MigrationMapRecord(
            MigrationMapRecord.CurrentSchemaVersion, vaultId, remaining);
        var content = updated.Encode();
        try
        {
            operation.WriteContent(
                MigrationState.MapResourceId, MigrationCodes.ZoneIndependent, content, revision);
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(content);
        }
    }

    private static MigrationMapRecord LoadMap(
        IVaultOperation operation,
        IReadOnlyList<ScopeResourceRevision> resources,
        string vaultId,
        out long revision)
    {
        revision = resources
            .Where(resource => string.Equals(resource.ResourceId, MigrationState.MapResourceId, StringComparison.Ordinal))
            .Select(resource => resource.Revision)
            .DefaultIfEmpty(0)
            .Max();
        if (revision == 0)
        {
            return MigrationMapRecord.Empty(vaultId);
        }

        var bytes = operation.ReadContent(MigrationState.MapResourceId, revision);
        try
        {
            return MigrationMapRecord.Decode(bytes, vaultId);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static bool IsLegacyPurged(IVaultOperation operation, string legacyId)
    {
        var resource = operation.ListResources().FirstOrDefault(item =>
            string.Equals(item.ResourceId, legacyId, StringComparison.Ordinal));
        if (resource is null)
        {
            return true; // already deleted from the canonical store
        }

        byte[]? bytes = null;
        try
        {
            bytes = operation.ReadContent(legacyId, resource.Revision);
            return SourceSnapshotCodec.DecodeStored(bytes, resource.Revision).Stored.Purged;
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException or InvalidOperationException or ManagedResourceUnavailableException)
        {
            return false;
        }
        finally
        {
            if (bytes is { Length: > 0 })
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
    }

    /// <summary>
    /// Closes the canonical impact of a preserved snapshot from live state.
    /// A preserved snapshot is a derivative family, so the memory-item purge
    /// target validator does not accept it; the same durable ledger and the
    /// same idempotent delete/resume machinery are still used, with the
    /// canonical set computed here instead.
    /// </summary>
    private static MigrationPurgeImpact[] ComputeCanonicalImpact(
        IVaultOperation operation,
        IReadOnlyList<ScopeResourceRevision> resources,
        string legacyId,
        string nodeId,
        long mapRevision)
    {
        var matches = new List<MigrationPurgeImpact>
        {
            new("snapshot", legacyId,
                resources.First(resource =>
                    string.Equals(resource.ResourceId, legacyId, StringComparison.Ordinal)).Revision,
                "purge-target: legacy preserved snapshot")
        };
        foreach (var resource in resources
                     .Where(resource => !string.Equals(
                         resource.ResourceId, MigrationState.MapResourceId, StringComparison.Ordinal) &&
                         !string.Equals(resource.ResourceId, legacyId, StringComparison.Ordinal))
                     .OrderBy(resource => resource.ResourceId, StringComparer.Ordinal))
        {
            byte[]? bytes = null;
            var references = false;
            try
            {
                bytes = operation.ReadContent(resource.ResourceId, resource.Revision);
                var text = Encoding.UTF8.GetString(bytes);
                references = text.Contains(legacyId, StringComparison.Ordinal) ||
                             text.Contains(nodeId, StringComparison.Ordinal);
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
            {
                // An unreadable derivative is not named by this preview.
            }
            finally
            {
                if (bytes is { Length: > 0 })
                {
                    CryptographicOperations.ZeroMemory(bytes);
                }
            }

            if (references)
            {
                matches.Add(new MigrationPurgeImpact(
                    CategoryOf(resource.ResourceId), resource.ResourceId, resource.Revision,
                    "canonical derivative pinning the purged original"));
            }
        }

        _ = mapRevision;
        return matches.ToArray();
    }

    /// <summary>
    /// Commits the durable canonical intent before any delete, so a crash or a
    /// lock during apply resumes from the bound set rather than half-purging.
    /// </summary>
    private static string CommitCanonicalIntent(
        AuthenticatedContext context,
        IVaultOperation operation,
        MigrationPurgePreview preview,
        string reason)
    {
        var purgeId = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        var refs = preview.OwnerControlled
            .Where(item => item.Category is not ("map-entry" or "workspace-node" or
                "workspace-revision" or "retained-revision" or "passage-index" or "embedding"))
            .Select(item => new PurgedResourceRef(item.ResourceId, item.Revision))
            .OrderBy(item => item.ResourceId, StringComparer.Ordinal)
            .ToArray();
        var catalogState = OwnerCatalogAuthority.ValidateAndNormalizeState(operation.LoadCatalogState());
        var withdrawn = catalogState.Entries
            .Where(entry => refs.Any(item =>
                string.Equals(item.ResourceId, entry.ResourceId, StringComparison.Ordinal)))
            .Select(entry => entry.CatalogId)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        var grantState = OwnerScopeGrantAuthority.ValidateAndMigrateState(operation.LoadScopeGrantState());
        // The ledger target validator intentionally excludes derivative
        // families such as preserved snapshots. The durable reservation of the
        // legacy id lives in Impacted; the synthetic target is only the intent's
        // audit identity.
        var intent = new OwnerPurgeIntent(
            purgeId, "migration-purge-" + purgeId, 1,
            MigrationCodes.ZoneIndependent, "migration-purge",
            refs, withdrawn, OwnerPurgeLedgerAuthority.NormalizeReason(reason),
            context.Kind == PrincipalKind.Owner ? ExecutorKind.Owner : ExecutorKind.Agent,
            now, now, null, PurgeIntentPhase.Confirmed, grantState.PolicyGeneration, refs);
        operation.UpdatePurgeLedgerState(state =>
        {
            var validated = OwnerPurgeLedgerAuthority.ValidateAndNormalizeState(state);
            return (OwnerPurgeLedgerAuthority.Append(validated, intent), true);
        });
        return purgeId;
    }

    private static ScopeResourceRevision[] FindReferencingAnnotations(
        IVaultOperation operation,
        IReadOnlyList<ScopeResourceRevision> resources,
        string legacyId,
        string nodeId)
    {
        var matches = new List<ScopeResourceRevision>();
        foreach (var resource in resources
                     .Where(resource => resource.ResourceId.StartsWith(
                         DomainIdentifiers.CognitiveAnnotationIdPrefix, StringComparison.Ordinal))
                     .OrderBy(resource => resource.ResourceId, StringComparer.Ordinal))
        {
            byte[]? bytes = null;
            var references = false;
            try
            {
                bytes = operation.ReadContent(resource.ResourceId, resource.Revision);
                var text = Encoding.UTF8.GetString(bytes);
                references = text.Contains(legacyId, StringComparison.Ordinal) ||
                             text.Contains(nodeId, StringComparison.Ordinal);
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
            {
                // An unreadable annotation is not named by this preview.
            }
            finally
            {
                if (bytes is { Length: > 0 })
                {
                    CryptographicOperations.ZeroMemory(bytes);
                }
            }

            if (references)
            {
                matches.Add(resource);
            }
        }

        return matches.ToArray();
    }

    private static string CategoryOf(string resourceId)
    {
        if (resourceId.StartsWith(DomainIdentifiers.RelationIdPrefix, StringComparison.Ordinal))
        {
            return "relation";
        }

        if (resourceId.StartsWith(DomainIdentifiers.LifecycleIdPrefix, StringComparison.Ordinal))
        {
            return "lifecycle";
        }

        if (resourceId.StartsWith(DomainIdentifiers.ArchiveIdPrefix, StringComparison.Ordinal))
        {
            return "retained-revision";
        }

        if (SourceSnapshotIds.IsSnapshotId(resourceId))
        {
            return "snapshot";
        }

        return "canonical";
    }

    private static string NormalizeToken(string token) =>
        token.Length == 64 && token.All(Uri.IsHexDigit) ? token.ToLowerInvariant() : token;
}
