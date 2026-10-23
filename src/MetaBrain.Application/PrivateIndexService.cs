using System.Runtime.InteropServices;
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
    private static readonly byte[] DigestDomainBytes = Encoding.UTF8.GetBytes(DigestDomain);

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
        try
        {
            store.Open();
        }
        catch (Exception ex) when (ex is InvalidDataException or CryptographicException)
        {
            // S8-T5: the published generation cannot be read (a tampered or
            // truncated file, or a key that no longer matches it). The
            // generation is a rebuildable derivative of the canonical
            // records: this rebuild replaces it, and the canonical records
            // themselves are never touched.
        }

        for (var attempt = 1; ; attempt++)
        {
            var listed = reader.ListResources();
            // S8-T5: one descriptor snapshot for the whole pass. The rebuild
            // used to ask the store for the resource list again for every
            // memory item, so a rebuild of an n-record vault scanned the
            // descriptor list n times. The manifest snapshot is the canonical
            // bounded source; lifecycle lookup uses binary search over its
            // resource ordering instead of building a second vault-sized map.
            var descriptors = listed;
            store.Clear();
            var canonicalDigest = Digest(listed);
            // S8-T5: the 100k rebuild reads one envelope at a time. A bounded
            // eight-envelope batch was tried once and withdrawn: the guard
            // aborted that run during the rebuild before it published, and
            // host free memory fell while it ran. No per-process attribution
            // was captured, so the batch is correlated with the abort, not
            // proven to cause it. The serial pass is the configuration that
            // completed under the same guard; its duration stays a reported
            // limit.
            const int ReadBatchSize = 1;
            const int ReadBatchDegree = 1;
            // S8-T5: the whole materialization lands in one engine scope, so
            // the postings of a large vault are written once instead of once
            // per token. An abandoned scope is rolled back and never observed
            // as a generation.
            var scope = store.BeginBulkWrite(canonicalDigest);
            var committed = false;
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
                        for (var j = 0; j < batch.Count; j++)
                        {
                            var content = contents[j];
                            try
                            {
                                var resource = listed[start + j];
                                var row = Classify(resource, content);
                                store.Insert(row);
                                // Keep the write working set independent of the
                                // vault size: each row is followed immediately
                                // by its searchable document, so the store
                                // needs no vault-wide memory-item set.
                                if (row.RecordKind == PrivateIndexRecordKind.MemoryItem &&
                                    MemoryItemTryRead(content, out var memory) && memory is not null &&
                                    string.Equals(memory.Id, resource.ResourceId, StringComparison.Ordinal))
                                {
                                    store.InsertSearchDocument(BuildSearchDocument(resource, memory, reader, descriptors));
                                }
                            }
                            finally
                            {
                                CryptographicOperations.ZeroMemory(content);
                            }
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

                // Release the first manifest snapshot before asking for the
                // live one. Its canonical SHA-256 identity is already the
                // generation identity, so the reconcile compares the same
                // exact digest without retaining two vault-wide descriptor
                // arrays at once.
                descriptors = Array.Empty<ScopeResourceRevision>();
                listed = Array.Empty<ScopeResourceRevision>();
                var relistedAfter = reader.ListResources();
                var liveDigest = Digest(relistedAfter);
                relistedAfter = Array.Empty<ScopeResourceRevision>();
                if (!string.Equals(canonicalDigest, liveDigest, StringComparison.Ordinal))
                {
                    if (attempt >= MaximumRebuildAttempts)
                    {
                        scope.Rollback();
                        committed = true;
                        store.Clear();
                        return new PrivateIndexOutcome(true, null);
                    }

                    scope.Rollback();
                    committed = true;
                    continue;
                }

                // S8-T5: the materialized generation is not read back into
                // memory. The snapshot carries the true row count and a
                // bounded sample of the rows, so a large vault cannot turn one
                // generation into an unbounded collection.
                // The commit is deliberately non-cancellable once entered:
                // cancellation before it rolls back; cancellation during the
                // atomic publish can only observe the whole generation.
                cancellationToken.ThrowIfCancellationRequested();
                scope.Commit();
                committed = true;

                // The published generation must be the generation this pass
                // built, not a different generation substituted during the
                // atomic publish.
                if (!string.Equals(store.CanonicalDigest, liveDigest, StringComparison.Ordinal))
                {
                    // The generation the engine holds is not the generation of
                    // the live canonical set: the pass is discarded and
                    // retried, and a set that keeps moving publishes nothing.
                    store.Clear();
                    if (attempt >= MaximumRebuildAttempts)
                    {
                        return new PrivateIndexOutcome(true, null);
                    }

                    continue;
                }

                var rowCount = store.RowCount;
                var sample = store.SelectSample(PrivateIndexFormat.MaximumSnapshotRows);
                return new PrivateIndexOutcome(false, new PrivateIndexSnapshot(
                    store.SchemaVersion,
                    liveDigest,
                    rowCount,
                    sample));
            }
            catch (OperationCanceledException)
            {
                // A lock requested mid-rebuild cancels the batch reads at a
                // batch boundary. The partial rows are dropped right here so a
                // cancelled pass can never be observed as a generation, and the
                // exception propagates; the lock's own drain still waits for
                // this operation to dispose before the key is cleared.
                if (!committed)
                {
                    scope.Rollback();
                    committed = true;
                }

                store.Clear();
                throw;
            }
            catch (ManagedResourceUnavailableException)
            {
                if (!committed)
                {
                    scope.Rollback();
                    committed = true;
                }

                descriptors = Array.Empty<ScopeResourceRevision>();
                listed = Array.Empty<ScopeResourceRevision>();
                var relisted = reader.ListResources();
                var liveDigest = Digest(relisted);
                relisted = Array.Empty<ScopeResourceRevision>();
                if (!string.Equals(canonicalDigest, liveDigest, StringComparison.Ordinal))
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
            finally
            {
                if (!committed)
                {
                    scope.Rollback();
                }

                scope.Dispose();
            }
        }
    }

    /// <summary>
    /// Loads the published generation instead of rebuilding it. The engine
    /// authenticates the sealed header and directory with the borrowed vault
    /// key, and the generation is usable only when its authenticated canonical
    /// digest (and row count) equals the live canonical resource/revision set
    /// the caller passed. A generation that is missing, stale or unreadable is
    /// dropped from resident state and reported, never materialized into a
    /// snapshot: the caller refuses the read and the owner's explicit rebuild
    /// remains the only way to publish a new generation.
    /// <para>
    /// This is the restart/unlock readiness path. It writes nothing: no
    /// generation is staged, published or rewritten, and a read-only reopen
    /// leaves the encrypted file byte-identical. Only authenticated metadata is
    /// used; a revision, kind, state or offset is read through the sealed
    /// records that carry it, so a tampered generation fails authentication
    /// instead of being trusted.
    /// </para>
    /// <para>
    /// S8-T5 delta decision: the generation is one immutable sealed file whose
    /// directory, token buckets and postings describe the whole vault, and the
    /// write scope only appends rows and merges whole-vault postings at commit.
    /// There is no per-row update or delete surface, so reconciling a single
    /// added/revised/deleted record would still require reading the published
    /// generation and rewriting the whole file — the same cost class as the
    /// rebuild, with a new merge format to keep authenticated. Record deltas are
    /// therefore not supported on the current format; a missing or stale
    /// generation goes through the existing explicit owner rebuild.
    /// </para>
    /// </summary>
    public PrivateIndexLoadOutcome Load(IPrivateIndexStore store, IReadOnlyList<ScopeResourceRevision> liveResources)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(liveResources);
        var liveDigest = Digest(liveResources);
        try
        {
            store.Open();
        }
        catch (Exception ex) when (ex is InvalidDataException or CryptographicException or IOException or UnauthorizedAccessException)
        {
            // The generation on disk cannot be authenticated as a generation
            // of this engine and key. It stays on disk (a derivative the owner
            // can replace) but nothing of it becomes resident.
            DiscardUnusableGeneration(store);
            return new PrivateIndexLoadOutcome(PrivateIndexLoadStatus.Unreadable, null);
        }

        if (!store.HasOpenGeneration)
        {
            // No published generation at all: an empty engine, not a failure.
            // The caller decides whether an empty canonical set makes that
            // truthful or whether the owner must rebuild.
            return new PrivateIndexLoadOutcome(PrivateIndexLoadStatus.Missing, null);
        }

        if (store.RowCount != liveResources.Count ||
            !string.Equals(store.CanonicalDigest, liveDigest, StringComparison.Ordinal))
        {
            // The authenticated generation describes another canonical set
            // (or an inconsistent row count): it cannot answer for this one.
            DiscardUnusableGeneration(store);
            return new PrivateIndexLoadOutcome(PrivateIndexLoadStatus.Stale, null);
        }

        try
        {
            var snapshot = new PrivateIndexSnapshot(
                store.SchemaVersion,
                liveDigest,
                store.RowCount,
                store.SelectSample(PrivateIndexFormat.MaximumSnapshotRows));
            return new PrivateIndexLoadOutcome(PrivateIndexLoadStatus.Loaded, snapshot);
        }
        catch (Exception ex) when (ex is InvalidDataException or CryptographicException or IOException or UnauthorizedAccessException)
        {
            DiscardUnusableGeneration(store);
            return new PrivateIndexLoadOutcome(PrivateIndexLoadStatus.Unreadable, null);
        }
    }

    private static void DiscardUnusableGeneration(IPrivateIndexStore store)
    {
        try
        {
            // Drops the resident generation and its decrypted cache without
            // touching the published file.
            store.Clear();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ObjectDisposedException)
        {
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
        return EvaluateSnapshot(reader.ListResources());
    }

    internal string EvaluateSnapshot(IReadOnlyList<ScopeResourceRevision> resources)
    {
        ArgumentNullException.ThrowIfNull(resources);
        return Digest(resources);
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

        if (ProposalTryRead(content, out var proposal) && proposal is not null &&
            string.Equals(proposal.Id, resource.ResourceId, StringComparison.Ordinal))
        {
            // S4-T5 proposals are pending owner-review writes, never
            // searchable content: they classify first, as their own kind, so
            // no search document is built for them and no other record
            // decoder can misread one. The linked IDs name the approved inbox
            // destination and, for a revise proposal, the subject revision
            // it pins.
            var proposalLinked = new List<string>(2) { proposal.DestinationResourceId };
            if (proposal.SubjectResourceId is not null)
            {
                proposalLinked.Add(proposal.SubjectResourceId);
            }

            return new PrivateIndexRow(
                resource.ResourceId,
                resource.ZoneId,
                resource.Revision,
                proposal.SchemaVersion,
                PrivateIndexRecordKind.Proposal,
                "pending",
                LinkedIds(proposalLinked));
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

        if (FeedbackTryRead(content, out var feedback) && feedback is not null &&
            string.Equals(feedback.Id, resource.ResourceId, StringComparison.Ordinal))
        {
            // S4-T4 feedback is canonical usage data, never searchable
            // content: it classifies as its own kind so the row set stays
            // truthful, but no search document is built for it below.
            return new PrivateIndexRow(
                resource.ResourceId,
                resource.ZoneId,
                resource.Revision,
                feedback.SchemaVersion,
                PrivateIndexRecordKind.Feedback,
                StateName(feedback.Kind),
                new[] { feedback.TargetResourceId });
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
        ScopeResourceRevision resource,
        StoredMemoryEnvelope memory,
        IManagedResourceStore reader,
        IReadOnlyList<ScopeResourceRevision> descriptors)
    {
        var tokens = SearchText.TokenizeDocument(
            memory.Title, memory.Body, memory.Applicability?.Project, memory.Applicability?.Environment, memory.Applicability?.Version);
        var publication = "proposal";
        try
        {
            // S8-T5: the lifecycle record of this subject is looked up in the
            // pass's own descriptor snapshot. The old path asked the store for
            // the whole resource list per memory item and then attempted a
            // read for a lifecycle record that most items do not have.
            var lifecycleId = DomainIdentifiers.LifecycleIdFor(memory.Id);
            if (TryFindDescriptor(descriptors, lifecycleId, out var live))
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
            tokens.OrderBy(token => token, StringComparer.Ordinal).ToArray(),
            memory.OccurredAtUtc,
            memory.RecordedAtUtc,
            publication,
            memory.Applicability?.Project ?? string.Empty,
            memory.Applicability?.Environment ?? string.Empty,
            memory.Applicability?.Version ?? string.Empty);
    }
    private static bool TryFindDescriptor(
        IReadOnlyList<ScopeResourceRevision> descriptors,
        string resourceId,
        out ScopeResourceRevision descriptor)
    {
        var low = 0;
        var high = descriptors.Count - 1;
        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            var comparison = string.CompareOrdinal(descriptors[middle].ResourceId, resourceId);
            if (comparison == 0)
            {
                descriptor = descriptors[middle];
                return true;
            }

            if (comparison < 0)
            {
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        descriptor = null!;
        return false;
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

    private static bool FeedbackTryRead(byte[] content, out StoredFeedback? feedback)
    {
        try
        {
            feedback = FeedbackCodec.DecodeStored(content);
            return true;
        }
        catch (ArgumentException)
        {
            feedback = null;
            return false;
        }
    }

    private static bool ProposalTryRead(byte[] content, out StoredProposal? proposal)
    {
        try
        {
            proposal = ProposalCodec.DecodeStored(content);
            return true;
        }
        catch (ArgumentException)
        {
            proposal = null;
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
            .Take(PrivateIndexFormat.MaximumLinkedIds)
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
        ArgumentNullException.ThrowIfNull(resources);
        if (resources.Count > PrivateIndexFormat.MaximumIndexedRows)
        {
            throw new InvalidDataException("The canonical resource snapshot exceeds the private-index row bound.");
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(DigestDomainBytes);
        Span<byte> identifierBytes = stackalloc byte[PrivateIndexFormat.MaximumIdentifierBytes];
        Span<byte> revisionBytes = stackalloc byte[20];
        Span<char> revisionChars = stackalloc char[20];
        Span<byte> separator = stackalloc byte[1];
        var previousResourceId = string.Empty;
        try
        {
            for (var index = 0; index < resources.Count; index++)
            {
                var resource = resources[index];
                if (string.IsNullOrEmpty(resource.ResourceId) ||
                    Encoding.UTF8.GetByteCount(resource.ResourceId) > identifierBytes.Length ||
                    (index > 0 && string.CompareOrdinal(previousResourceId, resource.ResourceId) >= 0))
                {
                    throw new InvalidDataException("The canonical resource snapshot is not in strict resource-ID order.");
                }

                separator[0] = (byte)'|';
                hash.AppendData(separator);
                var identifierLength = Encoding.UTF8.GetBytes(resource.ResourceId.AsSpan(), identifierBytes);
                hash.AppendData(identifierBytes[..identifierLength]);
                separator[0] = (byte)'@';
                hash.AppendData(separator);
                if (!resource.Revision.TryFormat(revisionChars, out var revisionCharCount, provider: CultureInfo.InvariantCulture))
                {
                    throw new InvalidDataException("A canonical resource revision could not be formatted.");
                }

                var revisionByteCount = Encoding.UTF8.GetBytes(revisionChars[..revisionCharCount], revisionBytes);
                hash.AppendData(revisionBytes[..revisionByteCount]);
                previousResourceId = resource.ResourceId;
            }

            Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
            try
            {
                var digestLength = hash.GetHashAndReset(digest);
                if (digestLength != digest.Length)
                {
                    throw new CryptographicException("The canonical digest length changed.");
                }

                return Convert.ToHexString(digest).ToLower(CultureInfo.InvariantCulture);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(digest);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(identifierBytes);
            CryptographicOperations.ZeroMemory(revisionBytes);
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(revisionChars));
            CryptographicOperations.ZeroMemory(separator);
        }
    }

}

/// <summary>
/// One private-index materialization result. A canonical set that moved
/// throughout every rebuild attempt yields no snapshot at all: an index
/// generation that cannot be proven to describe canonical is never published.
/// </summary>
public sealed record PrivateIndexOutcome(bool CanonicalMoved, PrivateIndexSnapshot? Snapshot);

/// <summary>
/// Disposition of one authenticated load of the published generation.
/// </summary>
public enum PrivateIndexLoadStatus
{
    /// <summary>The generation authenticated and describes the live canonical set.</summary>
    Loaded,
    /// <summary>No publication exists, so there is nothing to load.</summary>
    Missing,
    /// <summary>A publication exists but describes another canonical set.</summary>
    Stale,
    /// <summary>A publication exists but cannot be authenticated or decoded.</summary>
    Unreadable
}

/// <summary>
/// Result of loading the published generation without rebuilding it. A
/// snapshot is present only for <see cref="PrivateIndexLoadStatus.Loaded"/>.
/// </summary>
public sealed record PrivateIndexLoadOutcome(PrivateIndexLoadStatus Status, PrivateIndexSnapshot? Snapshot);
