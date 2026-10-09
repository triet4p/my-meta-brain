using MetaBrain.Core.Domain;
using MetaBrain.Core.Security;

namespace MetaBrain.Application;

/// <summary>
/// S2-T3 source registry authority: create-only plus revision-gated revise
/// over the shared encrypted store, exactly like <see cref="MemoryItemService"/>.
/// A registered source is metadata only (title, private locator, version,
/// explicit availability): no source body is persisted here, and originals
/// stay outside vault protection (R16/R18). Identity comes from trusted
/// request params, executor from the trusted context, revision from the
/// vault descriptor. The service never touches grant state, so registering,
/// revising, or availability changes cannot expand an approved snapshot
/// (R11). Reads resolve the live revision from the manifest without
/// decrypting other resources; item citations resolve the same live revision
/// to validate their anchors.
/// </summary>
public sealed class SourceRegistryService
{
    public RegisteredSource Create(
        AuthenticatedContext context,
        string sourceId,
        string zoneId,
        byte[] draftJson,
        IManagedResourceStore store)
    {
        return Write(context, sourceId, zoneId, draftJson, store, expectedRevision: 0);
    }

    public RegisteredSource Revise(
        AuthenticatedContext context,
        string sourceId,
        string zoneId,
        long expectedRevision,
        byte[] draftJson,
        IManagedResourceStore store)
    {
        if (expectedRevision < 1)
        {
            throw new ArgumentException("A revise requires the live revision it replaces.");
        }

        return Write(context, sourceId, zoneId, draftJson, store, expectedRevision);
    }

    public RegisteredSource Read(
        AuthenticatedContext context,
        string sourceId,
        long? expectedRevision,
        IManagedResourceReader store)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(store);
        EnsureTrusted(context);
        if (string.IsNullOrEmpty(sourceId))
        {
            throw new ArgumentException("The source ID is required.");
        }

        byte[] raw;
        try
        {
            raw = store.ReadContent(sourceId, expectedRevision);
        }
        catch (ManagedResourceUnavailableException ex)
        {
            throw new ArgumentException("The registered source is unavailable.", ex);
        }

        try
        {
            var stored = RegisteredSourceCodec.DecodeStored(raw);
            store.ValidateRegistration(sourceId, expectedRevision);
            var liveRevision = expectedRevision ?? ResolveLiveRevision(store, sourceId);
            return ToSource(stored, liveRevision);
        }
        catch (ArgumentException)
        {
            throw;
        }
        catch (ManagedResourceUnavailableException ex)
        {
            throw new ArgumentException("The registered source is unavailable.", ex);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(raw);
        }
    }

    /// <summary>
    /// Live revision of a registered source, resolved from the encrypted
    /// manifest only (no content decrypt, no existence confirmation for
    /// unregistered IDs: null means "not registered", never "exists but
    /// hidden"). Item writes use this to validate citation anchors.
    /// </summary>
    public static long? TryGetLiveRevision(IManagedResourceReader store, string? sourceId)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (string.IsNullOrEmpty(sourceId))
        {
            return null;
        }

        if (store is not IManagedResourceStore full)
        {
            return null;
        }

        try
        {
            return full.ListResources().FirstOrDefault(resource =>
                string.Equals(resource.ResourceId, sourceId, StringComparison.Ordinal))?.Revision;
        }
        catch (ManagedResourceUnavailableException)
        {
            return null;
        }
    }

    private static RegisteredSource Write(
        AuthenticatedContext context,
        string sourceId,
        string zoneId,
        byte[] draftJson,
        IManagedResourceStore store,
        long expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(store);
        EnsureTrusted(context);
        if (string.IsNullOrEmpty(sourceId) || string.IsNullOrEmpty(zoneId))
        {
            throw new ArgumentException("The source ID and zone are required.");
        }

        ArgumentNullException.ThrowIfNull(draftJson);
        RegisteredSourceDraft draft;
        try
        {
            draft = RegisteredSourceCodec.DecodeDraft(draftJson);
        }
        catch (ArgumentException)
        {
            throw;
        }

        RegisteredSourceValidator.ValidateDraft(draft, sourceId, zoneId);
        var recordedAtUtc = draft.RecordedAtUtc ?? DateTimeOffset.UtcNow;
        if (recordedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Recorded time must use UTC.");
        }

        var stored = new StoredRegisteredSource(
            RegisteredSourceCodec.CurrentSchemaVersion,
            sourceId,
            zoneId,
            context.Kind == PrincipalKind.Agent ? ExecutorKind.Agent : ExecutorKind.Owner,
            draft.CreationMethod!.Value,
            draft.Title,
            draft.Locator,
            draft.Version,
            draft.Availability!.Value,
            draft.CapturedAtUtc,
            recordedAtUtc);
        var payload = RegisteredSourceCodec.EncodeStored(stored);
        try
        {
            // Revision-gated exactly like canonical memory writes: 0 creates
            // only when absent, N revises only the live revision N; stale
            // concurrent registrations fail closed and publish nothing.
            var revision = store.WriteContent(sourceId, zoneId, payload, expectedRevision);
            return ToSource(stored, revision);
        }
        catch (ManagedResourceUnavailableException ex)
        {
            throw new ArgumentException("The source registration was rejected by vault storage.", ex);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(payload);
        }
    }

    internal static RegisteredSource ToSource(StoredRegisteredSource stored, long revision) => new(
        stored.Id,
        stored.ZoneId,
        revision,
        stored.SchemaVersion,
        stored.Executor,
        stored.CreationMethod,
        stored.Title,
        stored.Locator,
        stored.Version,
        stored.Availability,
        stored.CapturedAtUtc,
        stored.RecordedAtUtc);

    private static long ResolveLiveRevision(IManagedResourceReader store, string sourceId)
    {
        if (store is IManagedResourceStore full)
        {
            var live = full.ListResources().FirstOrDefault(resource =>
                string.Equals(resource.ResourceId, sourceId, StringComparison.Ordinal));
            if (live is not null)
            {
                return live.Revision;
            }
        }

        throw new ArgumentException("The registered source is unavailable.");
    }

    private static void EnsureTrusted(AuthenticatedContext actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
    }
}
