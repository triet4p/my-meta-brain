using System.Security.Cryptography;
using MetaBrain.Core.Domain;
using MetaBrain.Core.Security;

namespace MetaBrain.Application;

public sealed record SourceSnapshotDescriptor(string Id, string ZoneId, long Revision, long Generation);

/// <summary>
/// Explicit owner-selected preservation over the existing encrypted canonical
/// resource store. A snapshot is one resource per enrolled source generation;
/// exact bytes, source reference revision, generation anchor, and retention are
/// committed together. Reads use that resource's own ID/revision and never
/// consult or widen source-reference grants.
/// </summary>
public sealed class SourceSnapshotService
{
    public SourceSnapshotMetadata Preserve(
        AuthenticatedContext owner,
        string sourceId,
        long sourceRevision,
        DateTimeOffset retainUntilUtc,
        IVaultOperation store,
        ISourceSnapshotReader reader)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(reader);
        EnsureOwner(owner);
        ValidateRetention(retainUntilUtc);
        if (!DomainIdentifiers.IsDomainIdentifier(sourceId) || sourceRevision < 1)
        {
            throw new ArgumentException("The selected source reference is invalid.");
        }

        var source = new SourceRegistryService().Read(owner, sourceId, sourceRevision, store);
        if (source.Text is not null || source.Locator is null)
        {
            throw new SourceOperationException("source_not_enrolled");
        }

        var enrollmentState = SourceEnrollmentAuthority.ValidateAndNormalizeState(store.LoadSourceEnrollmentState());
        var enrollment = SourceEnrollmentAuthority.FindEnrollment(enrollmentState, sourceId)
            ?? throw new SourceOperationException("source_not_enrolled");
        if (enrollment.RevokedAtUtc is not null)
        {
            throw new SourceOperationException("source_revoked");
        }

        if (SourceEnrollmentAuthority.FindQuarantine(enrollmentState, sourceId) is not null)
        {
            throw new SourceOperationException("source_quarantined");
        }

        string registeredLocator;
        try
        {
            registeredLocator = SourceEnrollmentGrammar.NormalizeLocator(source.Locator);
        }
        catch (ArgumentException)
        {
            throw new SourceOperationException("source_reference_mismatch");
        }

        if (!string.Equals(source.ZoneId, enrollment.ZoneId, StringComparison.Ordinal) ||
            (!string.Equals(registeredLocator, enrollment.Locator, StringComparison.OrdinalIgnoreCase) &&
             enrollment.RelocatedAtUtc is null))
        {
            throw new SourceOperationException("source_reference_mismatch");
        }

        var read = reader.TryReadSnapshot(enrollment.Locator);
        if (read.Status != SourceSnapshotStatus.Snapshot || read.Content is not { Length: > 0 } content)
        {
            throw new SourceOperationException(read.Status switch
            {
                SourceSnapshotStatus.Missing => "source_missing",
                SourceSnapshotStatus.TooLarge => "source_too_large",
                _ => "source_unreadable"
            });
        }

        try
        {
            if (content.Length != enrollment.AnchorLength ||
                !SourceFingerprint.Matches(enrollment.AnchorDigest, content))
            {
                throw new SourceOperationException("source_changed");
            }

            var id = SourceSnapshotIds.For(sourceId, enrollment.Generation);
            var liveRevision = FindLiveRevision(store, id);
            if (liveRevision > 0)
            {
                var existing = ReadStored(id, liveRevision, store);
                try
                {
                    if (!string.Equals(existing.Stored.SourceId, sourceId, StringComparison.Ordinal) ||
                        existing.Stored.SourceGeneration != enrollment.Generation ||
                        !string.Equals(existing.Stored.ZoneId, enrollment.ZoneId, StringComparison.Ordinal))
                    {
                        throw new InvalidDataException("The preserved source snapshot identity does not match its resource.");
                    }

                    if (!existing.Stored.Purged)
                    {
                        if (existing.Stored.SourceRevision == sourceRevision &&
                            existing.Stored.RetainUntilUtc == retainUntilUtc &&
                            existing.Stored.SourceDigest == enrollment.AnchorDigest)
                        {
                            return new SourceSnapshotMetadata(existing.Stored, existing.Revision);
                        }

                        throw new SourceOperationException("source_snapshot_already_preserved");
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(existing.Content);
                }
            }

            var capturedAtUtc = DateTimeOffset.UtcNow;
            var stored = new StoredSourceSnapshot(
                SourceSnapshotCodec.CurrentSchemaVersion,
                id,
                enrollment.ZoneId,
                sourceId,
                sourceRevision,
                enrollment.Generation,
                content.Length,
                enrollment.AnchorDigest,
                capturedAtUtc,
                retainUntilUtc,
                Purged: false);
            byte[]? encoded = null;
            try
            {
                encoded = SourceSnapshotCodec.EncodeStored(stored, content);
                var revision = store.WriteContent(id, stored.ZoneId, encoded, liveRevision);
                return new SourceSnapshotMetadata(stored, revision);
            }
            catch (ManagedResourceUnavailableException)
            {
                throw new SourceOperationException("source_snapshot_revision_conflict");
            }
            finally
            {
                if (encoded is { Length: > 0 })
                {
                    CryptographicOperations.ZeroMemory(encoded);
                }
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(content);
        }
    }

    public IReadOnlyList<SourceSnapshotDescriptor> List(
        AuthenticatedContext owner,
        string sourceId,
        IManagedResourceStore store)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(store);
        EnsureOwner(owner);
        if (!DomainIdentifiers.IsDomainIdentifier(sourceId))
        {
            throw new ArgumentException("The source ID is invalid.");
        }

        return store.ListResources()
            .Where(resource => SourceSnapshotIds.BelongsToSource(resource.ResourceId, sourceId))
            .Select(resource => new SourceSnapshotDescriptor(
                resource.ResourceId,
                resource.ZoneId,
                resource.Revision,
                ParseGeneration(resource.ResourceId)))
            .OrderBy(snapshot => snapshot.Generation)
            .ToArray();
    }

    public SourceSnapshot Read(string snapshotId, long expectedRevision, IManagedResourceReader store)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (!SourceSnapshotIds.IsSnapshotId(snapshotId) || expectedRevision < 1)
        {
            throw new ArgumentException("The source snapshot identity or revision is invalid.");
        }

        var snapshot = ReadStored(snapshotId, expectedRevision, store);
        if (snapshot.Stored.Purged || snapshot.Stored.RetainUntilUtc <= DateTimeOffset.UtcNow)
        {
            CryptographicOperations.ZeroMemory(snapshot.Content);
            throw new SourceOperationException(snapshot.Stored.Purged
                ? "source_snapshot_purged"
                : "source_snapshot_expired");
        }

        return snapshot;
    }

    public SourceSnapshotMetadata Retain(
        AuthenticatedContext owner,
        string snapshotId,
        long expectedRevision,
        DateTimeOffset retainUntilUtc,
        IManagedResourceStore store)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(store);
        EnsureOwner(owner);
        ValidateRetention(retainUntilUtc);
        var existing = ReadStored(snapshotId, expectedRevision, store);
        try
        {
            if (existing.Stored.Purged)
            {
                throw new SourceOperationException("source_snapshot_purged");
            }

            if (existing.Stored.RetainUntilUtc == retainUntilUtc)
            {
                return new SourceSnapshotMetadata(existing.Stored, existing.Revision);
            }

            var stored = existing.Stored with { RetainUntilUtc = retainUntilUtc };
            var encoded = SourceSnapshotCodec.EncodeStored(stored, existing.Content);
            try
            {
                var revision = store.WriteContent(snapshotId, stored.ZoneId, encoded, expectedRevision);
                return new SourceSnapshotMetadata(stored, revision);
            }
            catch (ManagedResourceUnavailableException)
            {
                throw new SourceOperationException("source_snapshot_revision_conflict");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(encoded);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(existing.Content);
        }
    }

    public SourceSnapshotMetadata Purge(
        AuthenticatedContext owner,
        string snapshotId,
        long expectedRevision,
        IManagedResourceStore store)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(store);
        EnsureOwner(owner);
        var existing = ReadStored(snapshotId, expectedRevision, store);
        try
        {
            if (existing.Stored.Purged)
            {
                return new SourceSnapshotMetadata(existing.Stored, existing.Revision);
            }

            var purged = existing.Stored with { Purged = true };
            var encoded = SourceSnapshotCodec.EncodeStored(purged, ReadOnlySpan<byte>.Empty);
            try
            {
                var revision = store.WriteContent(snapshotId, purged.ZoneId, encoded, expectedRevision);
                return new SourceSnapshotMetadata(purged, revision);
            }
            catch (ManagedResourceUnavailableException)
            {
                throw new SourceOperationException("source_snapshot_revision_conflict");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(encoded);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(existing.Content);
        }
    }

    private static SourceSnapshot ReadStored(string snapshotId, long expectedRevision, IManagedResourceReader store)
    {
        if (!SourceSnapshotIds.IsSnapshotId(snapshotId) || expectedRevision < 1)
        {
            throw new ArgumentException("The source snapshot identity or revision is invalid.");
        }

        byte[]? raw = null;
        try
        {
            raw = store.ReadContent(snapshotId, expectedRevision);
            var snapshot = SourceSnapshotCodec.DecodeStored(raw, expectedRevision);
            if (!string.Equals(snapshot.Stored.Id, snapshotId, StringComparison.Ordinal))
            {
                CryptographicOperations.ZeroMemory(snapshot.Content);
                throw new ManagedResourceUnavailableException();
            }

            return snapshot;
        }
        finally
        {
            if (raw is { Length: > 0 })
            {
                CryptographicOperations.ZeroMemory(raw);
            }
        }
    }

    private static long FindLiveRevision(IManagedResourceStore store, string resourceId)
    {
        foreach (var resource in store.ListResources())
        {
            if (string.Equals(resource.ResourceId, resourceId, StringComparison.Ordinal))
            {
                return resource.Revision;
            }
        }

        return 0;
    }

    private static long ParseGeneration(string snapshotId)
    {
        var marker = snapshotId.LastIndexOf("-g", StringComparison.Ordinal);
        return marker >= 0 && long.TryParse(snapshotId.AsSpan(marker + 2),
            System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var generation)
            ? generation
            : throw new InvalidDataException("The source snapshot resource ID is malformed.");
    }

    private static void ValidateRetention(DateTimeOffset retainUntilUtc)
    {
        if (retainUntilUtc.Offset != TimeSpan.Zero || retainUntilUtc <= DateTimeOffset.UtcNow)
        {
            throw new ArgumentException("A source snapshot requires an explicit future UTC retention time.");
        }
    }

    private static void EnsureOwner(AuthenticatedContext owner)
    {
        if (owner.Kind != PrincipalKind.Owner)
        {
            throw new UnauthorizedAccessException("Only the owner may change source preservation.");
        }
    }
}
