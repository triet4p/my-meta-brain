using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MetaBrain.Core.Domain;
using MetaBrain.Core.Index;
using MetaBrain.Core.Security;

namespace MetaBrain.Application;

/// <summary>
/// S2-T7 private index authority: materializes the disposable index from the
/// canonical encrypted records only. The canonical file stays the single truth:
/// nothing here writes canonical state, the engine holds no key, and a row is
/// never served without being reconciled against the live canonical
/// resource/revision set.
///
/// Reconciliation contract: a rebuild snapshots the manifest, materializes the
/// rows, then re-reads the manifest. If the canonical set moved in between the
/// materialization is discarded and retried; if it keeps moving the rebuild
/// fails closed with <see cref="PrivateIndexOutcome.CanonicalMoved"/> instead
/// of publishing a stale projection. <see cref="Evaluate"/> fingerprints the
/// live canonical set so a caller can tell whether a previously materialized
/// index still describes canonical.
/// </summary>
public sealed class PrivateIndexAuthority
{
    private const int MaximumRebuildAttempts = 3;
    private const string DigestDomain = "MetaBrain|private-index|v1";

    /// <summary>
    /// Materializes one full index generation from canonical. The caller owns
    /// the unlocked vault operation for the whole call, so no private read can
    /// outlive the lease that authorized it.
    ///
    /// Concurrent canonical movement makes a revision-gated read fail, because
    /// the descriptor the loop started from no longer matches the committed
    /// revision. That is a movement signal, not a storage failure, so the pass
    /// restarts from a fresh manifest snapshot. A read that still fails against
    /// an unchanged resource set is a genuine unavailability (tampered,
    /// truncated, or missing ciphertext) and is rethrown after the partial rows
    /// are cleared: no generation is published over a body that cannot be
    /// proven. A set that keeps moving is dropped instead of guessed at.
    /// </summary>
    public PrivateIndexOutcome Rebuild(IPrivateIndexStore store, IManagedResourceStore reader, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(reader);
        store.Open();
        for (var attempt = 1; ; attempt++)
        {
            var listed = reader.ListResources();
            store.Clear();
            const int ReadBatchSize = 128;
            const int ReadBatchDegree = 8;
            try
            {
                for (var start = 0; start < listed.Count; start += ReadBatchSize)
                {
                    var end = Math.Min(start + ReadBatchSize, listed.Count);
                    var batch = new List<ScopeResourceRevision>(end - start);
                    for (var i = start; i < end; i++)
                    {
                        batch.Add(listed[i]);
                    }

                    var contents = reader.ReadBatch(batch, ReadBatchDegree, cancellationToken);
                    try
                    {
                        var searchPlans = new List<(ScopeResourceRevision Resource, StoredMemoryEnvelope Envelope)>();
                        for (var j = 0; j < batch.Count; j++)
                        {
                            var content = contents[j];
                            try
                            {
                                var row = Classify(listed[start + j], content);
                                store.Insert(row);
                                // S4-T2: only live memory items become search
                                // documents; every plan below re-validates the
                                // decoded identity against its own descriptor.
                                if (row.RecordKind == PrivateIndexRecordKind.MemoryItem &&
                                    MemoryItemTryRead(content, out var memory) && memory is not null &&
                                    string.Equals(memory.Id, listed[start + j].ResourceId, StringComparison.Ordinal))
                                {
                                    searchPlans.Add((listed[start + j], memory));
                                }
                            }
                            finally
                            {
                                CryptographicOperations.ZeroMemory(content);
                            }
                        }

                        // Lifecycle join for publication/currentness: one live
                        // record per subject when present, read at its live
                        // revision after the batch rows are stored.
                        foreach (var plan in searchPlans)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            store.InsertSearchDocument(BuildSearchDocument(plan.Resource, plan.Envelope, reader));
                        }
                    }
                    finally
                    {
                        foreach (var content in contents)
                        {
                            if (content is not null)
                            {
                                CryptographicOperations.ZeroMemory(content);
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // A lock requested mid-rebuild cancels the batch reads at a
                // batch boundary. The partial rows are dropped right here so a
                // cancelled pass can never be observed as a generation, and the
                // exception propagates; the lock's own drain still waits for
                // this operation to dispose before the key is cleared.
                store.Clear();
                throw;
            }
            catch (ManagedResourceUnavailableException)
            {
                var relisted = reader.ListResources();
                if (!SameResourceSet(listed, relisted))
                {
                    if (attempt >= MaximumRebuildAttempts)
                    {
                        store.Clear();
                        return new PrivateIndexOutcome(true, null);
                    }

                    continue;
                }

                // Same canonical set, unreadable body: fail closed rather than
                // publishing a partial generation or retrying forever.
                store.Clear();
                throw;
            }

            var relistedAfter = reader.ListResources();
            if (!SameResourceSet(listed, relistedAfter))
            {
                if (attempt >= MaximumRebuildAttempts)
                {
                    store.Clear();
                    return new PrivateIndexOutcome(true, null);
                }

                continue;
            }

            var rows = store.SelectAll();
            return new PrivateIndexOutcome(false, new PrivateIndexSnapshot(
                store.SchemaVersion,
                Digest(relistedAfter),
                rows.Length,
                rows));
        }
    }

    /// <summary>
    /// Fingerprints the live canonical resource/revision set. Reading only the
    /// manifest descriptors keeps this cheap enough to run on every status
    /// request; it never decrypts a body.
    /// </summary>
    public string Evaluate(IManagedResourceStore reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return Digest(reader.ListResources());
    }

    private static PrivateIndexRow Classify(ScopeResourceRevision resource, byte[] content)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(content);

        if (SourceSnapshotIds.IsSnapshotId(resource.ResourceId))
        {
            var snapshot = SourceSnapshotCodec.DecodeStored(content, resource.Revision);
            try
            {
                if (!string.Equals(snapshot.Stored.Id, resource.ResourceId, StringComparison.Ordinal) ||
                    !string.Equals(snapshot.Stored.ZoneId, resource.ZoneId, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("The preserved source snapshot identity does not match its manifest.");
                }

                var state = snapshot.Stored.Purged
                    ? "purged"
                    : snapshot.Stored.RetainUntilUtc <= DateTimeOffset.UtcNow
                        ? "expired"
                        : "retained";
                return new PrivateIndexRow(
                    resource.ResourceId,
                    resource.ZoneId,
                    resource.Revision,
                    snapshot.Stored.SchemaVersion,
                    PrivateIndexRecordKind.PreservedSourceSnapshot,
                    state,
                    new[] { snapshot.Stored.SourceId });
            }
            finally
            {
                CryptographicOperations.ZeroMemory(snapshot.Content);
            }
        }

        if (MemoryItemTryRead(content, out var memory) && memory is not null &&
            string.Equals(memory.Id, resource.ResourceId, StringComparison.Ordinal))
        {
            return new PrivateIndexRow(
                resource.ResourceId,
                resource.ZoneId,
                resource.Revision,
                memory.SchemaVersion,
                PrivateIndexRecordKind.MemoryItem,
                StateName(memory.Kind),
                LinkedIds(memory.Sources.Select(source => source.SourceId)));
        }

        if (MemoryItemTryRead(content, out var archived) && archived is not null)
        {
            // A retained revision archive holds the exact stored bytes of one
            // superseded subject revision under its own opaque resource ID, so
            // the decoded identity is the subject, never this resource.
            return new PrivateIndexRow(
                resource.ResourceId,
                resource.ZoneId,
                resource.Revision,
                archived.SchemaVersion,
                PrivateIndexRecordKind.RetainedRevision,
                "retained",
                new[] { archived.Id });
        }

        if (RegisteredSourceTryRead(content, out var source) && source is not null &&
            string.Equals(source.Id, resource.ResourceId, StringComparison.Ordinal))
        {
            return new PrivateIndexRow(
                resource.ResourceId,
                resource.ZoneId,
                resource.Revision,
                source.SchemaVersion,
                PrivateIndexRecordKind.RegisteredSource,
                StateName(source.Availability),
                Array.Empty<string>());
        }

        if (RelationTryRead(content, out var relation) && relation is not null &&
            string.Equals(relation.Id, resource.ResourceId, StringComparison.Ordinal))
        {
            return new PrivateIndexRow(
                resource.ResourceId,
                resource.ZoneId,
                resource.Revision,
                relation.SchemaVersion,
                PrivateIndexRecordKind.Relation,
                StateName(relation.State),
                LinkedIds(new[] { relation.From.ResourceId, relation.To.ResourceId }));
        }

        if (LifecycleTryRead(content, out var lifecycle) && lifecycle is not null &&
            string.Equals(lifecycle.Id, resource.ResourceId, StringComparison.Ordinal))
        {
            var linked = new List<string>(lifecycle.RetainedRevisions.Length + 2) { lifecycle.SubjectResourceId };
            if (lifecycle.SupersededBy is not null)
            {
                linked.Add(lifecycle.SupersededBy);
            }

            foreach (var retained in lifecycle.RetainedRevisions)
            {
                linked.Add(retained.ArchiveResourceId);
            }

            return new PrivateIndexRow(
                resource.ResourceId,
                resource.ZoneId,
                resource.Revision,
                lifecycle.SchemaVersion,
                PrivateIndexRecordKind.Lifecycle,
                StateName(lifecycle.Publication),
                LinkedIds(linked));
        }

        return new PrivateIndexRow(
            resource.ResourceId,
            resource.ZoneId,
            resource.Revision,
            0,
            PrivateIndexRecordKind.Unknown,
            "unknown",
            Array.Empty<string>());
    }
    /// <summary>
    /// S4-T2 search document for one live memory item: normalized tokens from
    /// the title, body and applicability plus the item's own occurred/recorded
    /// instants, joined with the live lifecycle publication state (proposal by
    /// default when no lifecycle record exists). Withdrawn and rejected items
    /// stay indexed with their state so ranking can demote them; serving them
    /// is the search handler's decision, never the index's.
    /// </summary>
    private static SearchIndexDocument BuildSearchDocument(
        ScopeResourceRevision resource, StoredMemoryEnvelope memory, IManagedResourceStore reader)
    {
        var tokens = SearchText.TokenizeDocument(
            memory.Title, memory.Body, memory.Applicability?.Project, memory.Applicability?.Environment, memory.Applicability?.Version);
        var publication = "proposal";
        try
        {
            var lifecycleId = "lc-" + memory.Id;
            var live = reader.ListResources().FirstOrDefault(candidate =>
                string.Equals(candidate.ResourceId, lifecycleId, StringComparison.Ordinal));
            if (live is not null)
            {
                var raw = reader.ReadContent(lifecycleId, live.Revision);
                try
                {
                    if (LifecycleTryRead(raw, out var lifecycle) && lifecycle is not null &&
                        string.Equals(lifecycle.Id, lifecycleId, StringComparison.Ordinal) &&
                        string.Equals(lifecycle.SubjectResourceId, memory.Id, StringComparison.Ordinal))
                    {
                        publication = StateName(lifecycle.Publication).ToLowerInvariant();
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(raw);
                }
            }
        }
        catch (ManagedResourceUnavailableException)
        {
            // A lifecycle record that cannot be proven keeps the default: the
            // memory row itself was already proven above.
        }

        const int MaximumTitleChars = 512;
        var title = memory.Title ?? string.Empty;
        return new SearchIndexDocument(
            memory.Id,
            resource.Revision,
            StateName(memory.Kind),
            title.Length <= MaximumTitleChars ? title : title[..MaximumTitleChars],
            tokens.Take(PrivateIndexSchema.MaximumSearchTokens).OrderBy(token => token, StringComparer.Ordinal).ToArray(),
            memory.OccurredAtUtc,
            memory.RecordedAtUtc,
            publication,
            memory.Applicability?.Project ?? string.Empty,
            memory.Applicability?.Environment ?? string.Empty,
            memory.Applicability?.Version ?? string.Empty);
    }


    private static bool MemoryItemTryRead(byte[] content, out StoredMemoryEnvelope? envelope)
    {
        try
        {
            envelope = MemoryEnvelopeCodec.DecodeStored(content);
            return true;
        }
        catch (ArgumentException)
        {
            envelope = null;
            return false;
        }
    }

    private static bool RegisteredSourceTryRead(byte[] content, out StoredRegisteredSource? source)
    {
        try
        {
            source = RegisteredSourceCodec.DecodeStored(content);
            return true;
        }
        catch (ArgumentException)
        {
            source = null;
            return false;
        }
    }

    private static bool RelationTryRead(byte[] content, out StoredRelation? relation)
    {
        try
        {
            relation = RelationCodec.DecodeStored(content);
            return true;
        }
        catch (ArgumentException)
        {
            relation = null;
            return false;
        }
    }

    private static bool LifecycleTryRead(byte[] content, out StoredLifecycleRecord? record)
    {
        try
        {
            record = LifecycleCodec.DecodeStored(content);
            return true;
        }
        catch (ArgumentException)
        {
            record = null;
            return false;
        }
    }

    private static string[] LinkedIds(IEnumerable<string?> ids)
    {
        var distinct = ids
            .Where(id => !string.IsNullOrEmpty(id))
            .Select(id => id!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .Take(PrivateIndexSchema.MaximumLinkedIds)
            .ToArray();
        return distinct;
    }

    private static string StateName(Enum value)
    {
        var name = JsonNamingPolicy.CamelCase.ConvertName(value.ToString());
        return name.Length > 0 ? name : "unknown";
    }

    private static string Digest(IReadOnlyList<ScopeResourceRevision> resources)
    {
        var builder = new StringBuilder(DigestDomain);
        foreach (var resource in resources.OrderBy(entry => entry.ResourceId, StringComparer.Ordinal))
        {
            builder.Append('|').Append(resource.ResourceId).Append('@').Append(resource.Revision.ToString(CultureInfo.InvariantCulture));
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLower(CultureInfo.InvariantCulture);
    }

    private static bool SameResourceSet(IReadOnlyList<ScopeResourceRevision> left, IReadOnlyList<ScopeResourceRevision> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            if (!string.Equals(left[index].ResourceId, right[index].ResourceId, StringComparison.Ordinal) ||
                left[index].Revision != right[index].Revision)
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// One private-index materialization result. A canonical set that moved
/// throughout every rebuild attempt yields no snapshot at all: an index
/// generation that cannot be proven to describe canonical is never published.
/// </summary>
public sealed record PrivateIndexOutcome(bool CanonicalMoved, PrivateIndexSnapshot? Snapshot);
