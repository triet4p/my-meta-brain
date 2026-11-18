using System.Security.Cryptography;
using System.Text.Json;
using MetaBrain.Core.Domain;
using MetaBrain.Core.Security;

namespace MetaBrain.Application;

/// <summary>
/// S18-T2 the single implementation of the guarded legacy-to-workspace
/// preview and ownership transfer. It runs inside the owner's unlocked lease
/// over the existing encrypted resource store and workspace persistence:
/// exact original bytes move in memory between encrypted stores, no plaintext
/// temp/draft/cache is written, no grant/catalog/session/token is touched, and
/// the encrypted reverse map is the owner's traceability source. Zone-bearing
/// and structured-only records are reported ineligible and are never moved;
/// declassification is never inferred or offered here.
/// </summary>
internal sealed class RecordMigrationService
{
    /// <summary>Write-free preview of every legacy canonical record.</summary>
    public MigrationPreviewInfo Preview(IVaultOperation operation, string vaultId, long manifestRevision)
    {
        var resources = operation.ListResources();
        var tree = operation.ReadWorkspaceTree();
        var treeRevision = tree?.Revision ?? 0;
        var (previewMap, _) = LoadMap(operation, resources, vaultId);
        var migratedIds = previewMap.Entries.Select(entry => entry.LegacyId).ToHashSet(StringComparer.Ordinal);
        var candidates = BuildCandidates(operation, tree, migratedIds);
        var token = MigrationState.ComputeToken(vaultId, manifestRevision, treeRevision, resources, candidates);
        var eligible = candidates.Count(candidate => candidate.Eligible);
        return new MigrationPreviewInfo(
            token, vaultId, manifestRevision, treeRevision, candidates,
            new MigrationBackupRequirement(vaultId, manifestRevision),
            eligible, candidates.Length - eligible);
    }

    /// <summary>
    /// Executes only a still-fresh, explicitly confirmed preview against a
    /// verified consistent encrypted pre-migration backup. Every write is
    /// gated; a mismatch finds nothing to write and refuses closed.
    /// </summary>
    public MigrationExecutionInfo Execute(
        IVaultOperation operation,
        string vaultId,
        long manifestRevision,
        string token,
        VaultBackupVerification backup)
    {
        // Re-derive the exact state the preview bound. Any vault or workspace
        // change since the preview produces a different token and refuses.
        var resources = operation.ListResources();
        var freshTree = operation.ReadWorkspaceTree();
        var freshTreeRevision = freshTree?.Revision ?? 0;
        var (currentMap, _) = LoadMap(operation, resources, vaultId);
        var currentMigratedIds = currentMap.Entries.Select(entry => entry.LegacyId).ToHashSet(StringComparer.Ordinal);
        var candidates = BuildCandidates(operation, freshTree, currentMigratedIds);
        var currentToken = MigrationState.ComputeToken(vaultId, manifestRevision, freshTreeRevision, resources, candidates);
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(currentToken), Convert.FromHexString(NormalizeToken(token))))
        {
            throw new MigrationRefusalException(MigrationCodes.StalePreview,
                "The vault or workspace changed since the preview; nothing was transferred.");
        }

        if (!string.Equals(backup.VaultId, vaultId, StringComparison.Ordinal) ||
            backup.ManifestRevision != manifestRevision)
        {
            throw new MigrationRefusalException(MigrationCodes.BackupMismatch,
                "The verified backup does not match the previewed vault state; nothing was transferred.");
        }

        if (!candidates.Any(candidate => candidate.Eligible) && currentMap.Entries.Length == 0)
        {
            throw new MigrationRefusalException(MigrationCodes.NoEligibleRecords,
                "This preview has no eligible originals; nothing was transferred.");
        }

        var (map, mapRevision) = LoadMap(operation, resources, vaultId);
        var entries = new List<MigrationMapEntry>(map.Entries);
        var unmigrated = candidates.Where(candidate => !candidate.Eligible).ToArray();
        var migrated = new List<MigrationMapEntry>();
        var tree = freshTree;

        foreach (var candidate in candidates.Where(candidate => candidate.Eligible)
                     .OrderBy(candidate => candidate.LegacyId, StringComparer.Ordinal))
        {
            if (entries.Any(entry => string.Equals(entry.LegacyId, candidate.LegacyId, StringComparison.Ordinal)))
            {
                var existingEntry = entries.First(entry =>
                    string.Equals(entry.LegacyId, candidate.LegacyId, StringComparison.Ordinal));
                var prior = operation.ReadWorkspaceFile(existingEntry.NodeId, existingEntry.ContentRevision);
                if (prior.Length != candidate.Length ||
                    !string.Equals(prior.Sha256, candidate.Sha256, StringComparison.Ordinal))
                {
                    throw new MigrationRefusalException(MigrationCodes.InconsistentState,
                        "A previously migrated original no longer matches its encrypted map; nothing was transferred.");
                }

                continue;
            }

            var (entry, _) = TransferCandidate(operation, candidate, ref tree);
            entries.Add(entry);
            migrated.Add(entry);
        }

        var updatedMap = new MigrationMapRecord(MigrationMapRecord.CurrentSchemaVersion, vaultId, entries.ToArray());
        var mapContent = updatedMap.Encode();
        long newMapRevision;
        try
        {
            newMapRevision = operation.WriteContent(
                MigrationState.MapResourceId, MigrationCodes.ZoneIndependent, mapContent, mapRevision);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(mapContent);
        }

        return new MigrationExecutionInfo(
            vaultId, tree?.Revision ?? 0, currentToken, migrated.ToArray(), unmigrated,
            MigrationState.MapResourceId, newMapRevision);
    }

    /// <summary>Read-only view of the encrypted reverse map for the owner.</summary>
    public MigrationReportInfo Report(IVaultOperation operation, string vaultId)
    {
        var (map, revision) = LoadMap(operation, operation.ListResources(), vaultId);
        return new MigrationReportInfo(vaultId, map.Entries, revision);
    }

    private static (MigrationMapRecord Map, long Revision) LoadMap(
        IVaultOperation operation, IReadOnlyList<ScopeResourceRevision> resources, string vaultId)
    {
        var revision = resources
            .Where(resource => string.Equals(resource.ResourceId, MigrationState.MapResourceId, StringComparison.Ordinal))
            .Select(resource => resource.Revision)
            .DefaultIfEmpty(0)
            .Max();
        if (revision == 0)
        {
            return (MigrationMapRecord.Empty(vaultId), 0);
        }

        var bytes = operation.ReadContent(MigrationState.MapResourceId, revision);
        try
        {
            return (MigrationMapRecord.Decode(bytes, vaultId), revision);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    /// <summary>
    /// S18-T3 crash-safe resume. The preview-bound state is re-derived first;
    /// then every persisted transition is reconciled. A node written before an
    /// interruption is adopted into the encrypted reverse map only after its
    /// exact bytes match the legacy original, an occupied destination holding
    /// different bytes is refused and left untouched, and a mapped original
    /// whose node or retained bytes changed is refused rather than repaired.
    /// The pass is idempotent: a completed migration only re-verifies and
    /// writes nothing. No grant, catalog entry, token or session is touched.
    /// </summary>
    public MigrationResumeInfo Resume(
        IVaultOperation operation,
        string vaultId,
        long manifestRevision,
        string token)
    {
        var resources = operation.ListResources();
        var tree = operation.ReadWorkspaceTree();
        var treeRevision = tree?.Revision ?? 0;
        var (map, mapRevision) = LoadMap(operation, resources, vaultId);
        var migratedIds = map.Entries.Select(entry => entry.LegacyId).ToHashSet(StringComparer.Ordinal);
        var candidates = BuildCandidates(operation, tree, migratedIds);
        var currentToken = MigrationState.ComputeToken(vaultId, manifestRevision, treeRevision, resources, candidates);
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(currentToken), Convert.FromHexString(NormalizeToken(token))))
        {
            throw new MigrationRefusalException(MigrationCodes.StalePreview,
                "The vault or workspace changed since the preview; nothing was resumed.");
        }

        if (candidates.Any(candidate =>
                string.Equals(candidate.Reason, MigrationCodes.DestinationConflict, StringComparison.Ordinal) &&
                !migratedIds.Contains(candidate.LegacyId)))
        {
            throw new MigrationRefusalException(MigrationCodes.DestinationConflict,
                "A migration destination is occupied by different bytes; it was not adopted or overwritten.");
        }

        var entries = new List<MigrationMapEntry>(map.Entries);
        var verified = new List<MigrationMapEntry>();
        var adopted = new List<MigrationMapEntry>();
        var created = new List<MigrationMapEntry>();

        foreach (var entry in map.Entries.OrderBy(entry => entry.LegacyId, StringComparer.Ordinal))
        {
            // Identity lookup, not name lookup: an owner rename or move after
            // migration keeps the stable node id and must not break the map.
            var node = tree?.Nodes.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, entry.NodeId, StringComparison.Ordinal));
            if (node is null || node.Kind != WorkspaceNodeKind.File ||
                node.ContentRevision != entry.ContentRevision)
            {
                throw new MigrationRefusalException(MigrationCodes.InconsistentState,
                    "A mapped original no longer has its recorded workspace node; nothing was resumed.");
            }

            var retained = operation.ReadWorkspaceFile(node.Id, entry.ContentRevision);
            if (retained.Length != entry.Length ||
                !string.Equals(retained.Sha256, entry.Sha256, StringComparison.Ordinal))
            {
                throw new MigrationRefusalException(MigrationCodes.InconsistentState,
                    "A mapped original no longer matches its exact retained bytes; nothing was resumed.");
            }

            verified.Add(entry);
        }

        foreach (var candidate in candidates
                     .Where(candidate =>
                         string.Equals(candidate.Reason, MigrationCodes.Resumable, StringComparison.Ordinal) ||
                         string.Equals(candidate.Reason, MigrationCodes.Eligible, StringComparison.Ordinal))
                     .OrderBy(candidate => candidate.LegacyId, StringComparer.Ordinal))
        {
            var (entry, adoptedNode) = TransferCandidate(operation, candidate, ref tree);
            entries.Add(entry);
            (adoptedNode ? adopted : created).Add(entry);
        }

        var unmigrated = candidates
            .Where(candidate => !string.Equals(candidate.Reason, MigrationCodes.Eligible, StringComparison.Ordinal) &&
                                !string.Equals(candidate.Reason, MigrationCodes.Resumable, StringComparison.Ordinal) &&
                                !string.Equals(candidate.Reason, MigrationCodes.AlreadyMigrated, StringComparison.Ordinal))
            .ToArray();

        var newMapRevision = mapRevision;
        if (adopted.Count > 0 || created.Count > 0)
        {
            var updatedMap = new MigrationMapRecord(MigrationMapRecord.CurrentSchemaVersion, vaultId, entries.ToArray());
            var mapContent = updatedMap.Encode();
            try
            {
                newMapRevision = operation.WriteContent(
                    MigrationState.MapResourceId, MigrationCodes.ZoneIndependent, mapContent, mapRevision);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(mapContent);
            }
        }

        return new MigrationResumeInfo(
            vaultId, tree?.Revision ?? 0, currentToken, adopted.ToArray(), created.ToArray(),
            verified.ToArray(), unmigrated, MigrationState.MapResourceId, newMapRevision);
    }

    /// <summary>
    /// One durable transfer. Exact legacy bytes are decoded and checked in the
    /// unlocked process; the deterministic destination is created or adopted
    /// only when its bytes match exactly. An unrelated occupied destination is
    /// refused, never adopted or overwritten, so it cannot become a writable
    /// second truth. Buffers are zeroized on every path.
    /// </summary>
    private static (MigrationMapEntry Entry, bool Adopted) TransferCandidate(
        IVaultOperation operation,
        MigrationCandidate candidate,
        ref WorkspaceSnapshot? tree)
    {
        byte[]? envelope = null;
        SourceSnapshot? snapshot = null;
        WorkspaceFileContent? adoptedContent = null;
        try
        {
            envelope = operation.ReadContent(candidate.LegacyId, candidate.Revision);
            snapshot = SourceSnapshotCodec.DecodeStored(envelope, candidate.Revision);
            var contentDigest = Convert.ToHexString(SHA256.HashData(snapshot.Content)).ToLowerInvariant();
            if (snapshot.Stored.Purged ||
                snapshot.Content.LongLength != candidate.Length ||
                !string.Equals(contentDigest, candidate.Sha256, StringComparison.Ordinal) ||
                !string.Equals(snapshot.Stored.ZoneId, MigrationCodes.ZoneIndependent, StringComparison.Ordinal))
            {
                throw new MigrationRefusalException(MigrationCodes.InconsistentState,
                    "A source snapshot changed after the preview; nothing was transferred.");
            }

            if (snapshot.Content.LongLength > MigrationState.MaximumContentBytes)
            {
                throw new MigrationRefusalException(MigrationCodes.OversizedSnapshot,
                    "A source snapshot exceeds the per-act cap; nothing was transferred.");
            }

            var folderId = EnsureMigratedFolder(operation, tree);
            tree = operation.ReadWorkspaceTree();
            WorkspaceMutationInfo mutation;
            var existingNode = FindMigratedNode(tree, candidate.LegacyId);
            if (existingNode is not null)
            {
                if (existingNode.Kind != WorkspaceNodeKind.File)
                {
                    throw new MigrationRefusalException(MigrationCodes.DestinationConflict,
                        "The migration destination is occupied by a different kind of node; nothing was transferred.");
                }

                // A crash between node creation and map write left a node at
                // the deterministic destination. Bind it only when its bytes
                // match the legacy original exactly; never create a duplicate
                // or adopt an unrelated occupied destination.
                adoptedContent = operation.ReadWorkspaceFile(existingNode.Id, existingNode.ContentRevision);
                if (adoptedContent.Length != candidate.Length ||
                    !string.Equals(adoptedContent.Sha256, candidate.Sha256, StringComparison.Ordinal))
                {
                    throw new MigrationRefusalException(MigrationCodes.DestinationConflict,
                        "The destination already holds different bytes; nothing was transferred.");
                }

                mutation = new WorkspaceMutationInfo(
                    existingNode.Id, tree?.Revision ?? 0, existingNode.Revision, existingNode.ContentRevision,
                    adoptedContent.Length, adoptedContent.Sha256);
            }
            else
            {
                mutation = operation.CreateWorkspaceFile(
                    folderId, candidate.LegacyId, snapshot.Content, tree?.Revision ?? 0);
            }

            var entry = new MigrationMapEntry(
                candidate.Family, candidate.LegacyId, candidate.Revision,
                mutation.NodeId, mutation.ContentRevision, candidate.Length, candidate.Sha256,
                candidate.ZoneId, candidate.SourceId ?? string.Empty, candidate.LegacyId);
            tree = operation.ReadWorkspaceTree();
            return (entry, existingNode is not null);
        }
        finally
        {
            if (adoptedContent?.Content is { Length: > 0 } adoptedBytes)
            {
                CryptographicOperations.ZeroMemory(adoptedBytes);
            }

            if (snapshot?.Content is { Length: > 0 } snapshotBytes)
            {
                CryptographicOperations.ZeroMemory(snapshotBytes);
            }

            if (envelope is { Length: > 0 } envelopeBytes)
            {
                CryptographicOperations.ZeroMemory(envelopeBytes);
            }
        }
    }

    private static MigrationCandidate[] BuildCandidates(
        IVaultOperation operation,
        WorkspaceSnapshot? tree,
        IReadOnlySet<string> migratedIds)
    {
        var candidates = new List<MigrationCandidate>();
        foreach (var resource in operation.ListResources()
                     .OrderBy(resource => resource.ResourceId, StringComparer.Ordinal))
        {
            if (string.Equals(resource.ResourceId, MigrationState.MapResourceId, StringComparison.Ordinal))
            {
                continue;
            }

            if (!SourceSnapshotIds.IsSnapshotId(resource.ResourceId))
            {
                candidates.Add(new MigrationCandidate(
                    FamilyOf(resource.ResourceId), resource.ResourceId, resource.Revision, 0, string.Empty,
                    resource.ZoneId, string.Empty, false, MigrationCodes.StructuredOnly, null));
                continue;
            }

            byte[]? envelope = null;
            SourceSnapshot? snapshot = null;
            try
            {
                envelope = operation.ReadContent(resource.ResourceId, resource.Revision);
                snapshot = SourceSnapshotCodec.DecodeStored(envelope, resource.Revision);
                var stored = snapshot.Stored;
                var destination = DestinationPathOf(resource.ResourceId);
                var contentDigest = Convert.ToHexString(SHA256.HashData(snapshot.Content)).ToLowerInvariant();
                string reason;
                if (stored.Purged)
                {
                    reason = MigrationCodes.Purged;
                }
                else if (snapshot.Content.LongLength > MigrationState.MaximumContentBytes ||
                         stored.SourceLength > MigrationState.MaximumContentBytes)
                {
                    reason = MigrationCodes.OversizedSnapshot;
                }
                else if (string.Equals(stored.ZoneId, MigrationCodes.ZoneIndependent, StringComparison.Ordinal))
                {
                    reason = migratedIds.Contains(resource.ResourceId)
                        ? MigrationCodes.AlreadyMigrated
                        : DestinationState(operation, tree, resource.ResourceId,
                            snapshot.Content.LongLength, contentDigest);
                }
                else if (string.IsNullOrEmpty(stored.ZoneId) ||
                         string.Equals(stored.ZoneId, "unknown", StringComparison.Ordinal))
                {
                    reason = MigrationCodes.UnknownClassification;
                }
                else
                {
                    // Workspace reads/shares authorize by file id and revision,
                    // never by zone: a zoned record's protection cannot be
                    // enforced at the destination, so it stays legacy. No
                    // declassification is offered, bundled or inferred here.
                    reason = MigrationCodes.ZoneProtectionNotEnforced;
                }
                candidates.Add(new MigrationCandidate(
                    "source-snapshot", resource.ResourceId, resource.Revision, snapshot.Content.LongLength,
                    contentDigest, stored.ZoneId, destination,
                    string.Equals(reason, MigrationCodes.Eligible, StringComparison.Ordinal),
                    reason, stored.SourceId));
            }
            catch (Exception ex) when (ex is ArgumentException or JsonException or InvalidOperationException or ManagedResourceUnavailableException)
            {
                candidates.Add(new MigrationCandidate(
                    SourceSnapshotIds.IsSnapshotId(resource.ResourceId) ? "source-snapshot" : FamilyOf(resource.ResourceId),
                    resource.ResourceId, resource.Revision, 0, string.Empty, resource.ZoneId,
                    string.Empty, false, MigrationCodes.UnknownOrCorrupt, null));
            }
            finally
            {
                if (snapshot?.Content is { Length: > 0 } snapshotContent)
                {
                    CryptographicOperations.ZeroMemory(snapshotContent);
                }

                if (envelope is { Length: > 0 } envelopeContent)
                {
                    CryptographicOperations.ZeroMemory(envelopeContent);
                }
            }
        }

        return candidates.ToArray();
    }

    /// <summary>
    /// Distinguishes an untouched destination from a node left by an
    /// interrupted transfer and from an unrelated occupied destination. Only
    /// an exact byte match with the legacy original is resumable; different
    /// bytes or a non-file node are reported as a conflict and are never
    /// adopted, overwritten or silently dropped.
    /// </summary>
    private static string DestinationState(
        IVaultOperation operation,
        WorkspaceSnapshot? tree,
        string legacyId,
        long length,
        string sha256)
    {
        var node = FindMigratedNode(tree, legacyId);
        if (node is null)
        {
            return MigrationCodes.Eligible;
        }

        if (node.Kind != WorkspaceNodeKind.File)
        {
            return MigrationCodes.DestinationConflict;
        }

        byte[]? bytes = null;
        try
        {
            bytes = operation.ReadWorkspaceFile(node.Id, node.ContentRevision).Content;
            return bytes.LongLength == length &&
                   string.Equals(
                       Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                       sha256, StringComparison.Ordinal)
                ? MigrationCodes.Resumable
                : MigrationCodes.DestinationConflict;
        }
        catch (WorkspaceRefusalException)
        {
            return MigrationCodes.DestinationConflict;
        }
        finally
        {
            if (bytes is { Length: > 0 })
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
    }

    private static string EnsureMigratedFolder(IVaultOperation operation, WorkspaceSnapshot? tree)
    {
        var rootId = tree?.RootId ?? "root";
        var migrated = FindChild(tree, rootId, "Migrated");
        string migratedId;
        if (migrated is null)
        {
            migratedId = operation.CreateWorkspaceFolder(rootId, "Migrated", tree?.Revision ?? 0).NodeId;
            tree = operation.ReadWorkspaceTree();
        }
        else if (migrated.Kind == WorkspaceNodeKind.Folder)
        {
            migratedId = migrated.Id;
        }
        else
        {
            throw new MigrationRefusalException(MigrationCodes.DestinationConflict,
                "The migration destination path is occupied by a file; nothing was transferred.");
        }

        var snapshots = FindChild(tree, migratedId, "snap");
        if (snapshots is not null)
        {
            if (snapshots.Kind == WorkspaceNodeKind.Folder)
            {
                return snapshots.Id;
            }

            throw new MigrationRefusalException(MigrationCodes.DestinationConflict,
                "The migration destination path is occupied by a file; nothing was transferred.");
        }

        return operation.CreateWorkspaceFolder(migratedId, "snap", tree?.Revision ?? 0).NodeId;
    }

    private static WorkspaceNodeInfo? FindMigratedNode(WorkspaceSnapshot? tree, string legacyId)
    {
        if (tree is null)
        {
            return null;
        }

        var migrated = FindChild(tree, tree.RootId, "Migrated");
        if (migrated is null || migrated.Kind != WorkspaceNodeKind.Folder)
        {
            return null;
        }

        var snapshots = FindChild(tree, migrated.Id, "snap");
        if (snapshots is null || snapshots.Kind != WorkspaceNodeKind.Folder)
        {
            return null;
        }

        return FindChild(tree, snapshots.Id, legacyId);
    }

    private static WorkspaceNodeInfo? FindChild(WorkspaceSnapshot? tree, string parentId, string name)
    {
        if (tree is null)
        {
            return null;
        }

        return tree.Nodes.FirstOrDefault(node =>
            string.Equals(node.ParentId, parentId, StringComparison.Ordinal) &&
            string.Equals(node.Name, name, StringComparison.Ordinal));
    }

    private static string DestinationPathOf(string legacyId) =>
        "Migrated/snap/" + legacyId;

    private static string FamilyOf(string resourceId)
    {
        var separator = resourceId.IndexOf('-');
        return separator > 0 ? resourceId[..separator] : resourceId;
    }

    private static string NormalizeToken(string token) =>
        token.Length == 64 && token.All(Uri.IsHexDigit) ? token.ToLowerInvariant() : token;
}
