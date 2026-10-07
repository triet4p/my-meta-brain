using MetaBrain.Core.Domain;
using MetaBrain.Core.Security;

namespace MetaBrain.Application;

/// <summary>
/// S2-T1 domain service: one path for typed memory item create/read over the
/// shared encrypted store. Identity (ResourceId/ZoneId) comes from trusted
/// request params, executor from the trusted context, revision from the vault
/// descriptor. The service never touches grant state: persistence goes only
/// through <see cref="IManagedResourceStore"/>, so additions, membership
/// edits, and revisions cannot expand the approved snapshot.
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
            var revision = store.WriteContent(resourceId, zoneId, payload);
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
