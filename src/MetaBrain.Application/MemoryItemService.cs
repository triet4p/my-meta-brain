using MetaBrain.Core.Domain;
using MetaBrain.Core.Security;

namespace MetaBrain.Application;

/// <summary>
/// S2-T2 durable canonical service: create-only plus revision-gated revise
/// over the shared encrypted store. Identity (ResourceId/ZoneId) comes from
/// trusted request params, executor from the trusted context, revision from
/// the vault descriptor. The service never touches grant state: persistence
/// goes only through <see cref="IManagedResourceStore"/>, so additions,
/// membership edits, and revisions cannot expand the approved snapshot.
/// Stale concurrent revisions fail closed without overwriting the winner.
/// </summary>
public sealed class MemoryItemService
{
    public MemoryItem Create(
        AuthenticatedContext context,
        string resourceId,
        string zoneId,
        byte[] draftJson,
        IManagedResourceStore store)
    {
        return Write(context, resourceId, zoneId, draftJson, store, expectedRevision: 0);
    }

    public MemoryItem Revise(
        AuthenticatedContext context,
        string resourceId,
        string zoneId,
        long expectedRevision,
        byte[] draftJson,
        IManagedResourceStore store)
    {
        if (expectedRevision < 1)
        {
            throw new ArgumentException("A revise requires the live revision it replaces.");
        }

        return Write(context, resourceId, zoneId, draftJson, store, expectedRevision);
    }

    /// <summary>
    /// Explicit import boundary: the owner supplies exact stored-envelope
    /// bytes (e.g. a pre-S2-T2 S2-T1 envelope or an external export) plus the
    /// live revision they replace (0 for create-only). The bytes must decode
    /// as a valid stored envelope with matching ID/zone; the commit stays
    /// revision-gated so stale imports fail closed without overwriting.
    /// </summary>
    public MemoryItem Import(
        AuthenticatedContext context,
        string resourceId,
        string zoneId,
        long expectedRevision,
        byte[] envelopeJson,
        IManagedResourceStore store)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(store);
        EnsureTrusted(context);
        if (string.IsNullOrEmpty(resourceId) || string.IsNullOrEmpty(zoneId) || expectedRevision < 0)
        {
            throw new ArgumentException("The memory ID, zone, and expected revision are required.");
        }

        ArgumentNullException.ThrowIfNull(envelopeJson);
        StoredMemoryEnvelope stored;
        try
        {
            stored = MemoryEnvelopeCodec.DecodeStored(envelopeJson);
        }
        catch (ArgumentException)
        {
            throw;
        }

        if (!string.Equals(stored.Id, resourceId, StringComparison.Ordinal) ||
            !string.Equals(stored.ZoneId, zoneId, StringComparison.Ordinal))
        {
            throw new ArgumentException("The imported envelope identity does not match the target.");
        }

        try
        {
            var revision = store.WriteContent(resourceId, zoneId, envelopeJson, expectedRevision);
            return ToItem(stored, revision);
        }
        catch (ManagedResourceUnavailableException ex)
        {
            throw new ArgumentException("The memory import was rejected by vault storage.", ex);
        }
    }

    private static MemoryItem Write(
        AuthenticatedContext context,
        string resourceId,
        string zoneId,
        byte[] draftJson,
        IManagedResourceStore store,
        long expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(store);
        EnsureTrusted(context);
        if (string.IsNullOrEmpty(resourceId) || string.IsNullOrEmpty(zoneId))
        {
            throw new ArgumentException("The memory ID and zone are required.");
        }

        ArgumentNullException.ThrowIfNull(draftJson);
        MemoryItemDraft draft;
        try
        {
            draft = MemoryEnvelopeCodec.DecodeDraft(draftJson);
        }
        catch (ArgumentException)
        {
            throw;
        }

        MemoryItemValidator.ValidateDraft(draft, resourceId, zoneId);
        var now = DateTimeOffset.UtcNow;
        var recordedAtUtc = draft.RecordedAtUtc ?? now;
        if (recordedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Recorded time must use UTC.");
        }

        var stored = new StoredMemoryEnvelope(
            MemoryEnvelopeCodec.CurrentSchemaVersion,
            resourceId,
            zoneId,
            draft.Kind!.Value,
            draft.Author!.Value,
            context.Kind == PrincipalKind.Agent ? ExecutorKind.Agent : ExecutorKind.Owner,
            draft.CreationMethod!.Value,
            draft.Evidence!.Value,
            draft.Level!.Value,
            draft.Title,
            draft.Body!,
            MemoryItemValidator.NormalizeSources(draft.Sources),
            draft.OccurredAtUtc,
            recordedAtUtc,
            draft.Applicability);
        var payload = MemoryEnvelopeCodec.EncodeStored(stored);
        try
        {
            // Revision-gated: 0 creates only when absent, N revises only the
            // live revision N. Stale concurrent writers fail closed without
            // overwriting the winner; nothing is published on mismatch.
            var revision = store.WriteContent(resourceId, zoneId, payload, expectedRevision);
            return ToItem(stored, revision);
        }
        catch (ManagedResourceUnavailableException ex)
        {
            throw new ArgumentException("The memory write was rejected by vault storage.", ex);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(payload);
        }
    }

    public MemoryItem Read(
        AuthenticatedContext context,
        string resourceId,
        long? expectedRevision,
        IManagedResourceReader store)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(store);
        EnsureTrusted(context);
        if (string.IsNullOrEmpty(resourceId))
        {
            throw new ArgumentException("The memory ID is required.");
        }

        byte[] raw;
        try
        {
            raw = store.ReadContent(resourceId, expectedRevision);
        }
        catch (ManagedResourceUnavailableException ex)
        {
            throw new ArgumentException("The memory item is unavailable.", ex);
        }

        try
        {
            var stored = MemoryEnvelopeCodec.DecodeStored(raw);
            store.ValidateRegistration(resourceId, expectedRevision);
            var liveRevision = expectedRevision ?? ResolveLiveRevision(store, resourceId);
            return ToItem(stored, liveRevision);
        }
        catch (ArgumentException)
        {
            throw;
        }
        catch (ManagedResourceUnavailableException ex)
        {
            throw new ArgumentException("The memory item is unavailable.", ex);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(raw);
        }
    }

    internal static MemoryItem ToItem(StoredMemoryEnvelope stored, long revision) => new(
        stored.Id,
        stored.ZoneId,
        revision,
        stored.SchemaVersion,
        stored.Kind,
        stored.Author,
        stored.Executor,
        stored.CreationMethod,
        stored.Evidence,
        stored.Level,
        stored.Title,
        stored.Body,
        stored.Sources,
        stored.OccurredAtUtc,
        stored.RecordedAtUtc,
        stored.Applicability);

    private static long ResolveLiveRevision(IManagedResourceReader store, string resourceId)
    {
        if (store is IManagedResourceStore full)
        {
            var live = full.ListResources().FirstOrDefault(resource =>
                string.Equals(resource.ResourceId, resourceId, StringComparison.Ordinal));
            if (live is not null)
            {
                return live.Revision;
            }
        }

        throw new ArgumentException("The memory item is unavailable.");
    }
    private static void EnsureTrusted(AuthenticatedContext actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
    }

}
