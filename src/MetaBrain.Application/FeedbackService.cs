using System.Security.Cryptography;
using MetaBrain.Core.Domain;
using MetaBrain.Core.Security;

namespace MetaBrain.Application;

/// <summary>
/// S4-T4 feedback authority over the shared encrypted canonical store.
/// Feedback records how one seen memory/source revision behaved in use
/// (useful, wrong, stale) and is always stored as its own canonical record:
/// it never edits the target item or source, never upgrades evidence or
/// verification, and never rewrites owner beliefs or canonical content.
/// Identity comes from trusted request params for owner writes (or is minted
/// server-side for agent writes), executor from the trusted context, revision
/// from the vault descriptor. The service never touches grant state, so adding
/// or reading feedback cannot expand an approved snapshot.
/// </summary>
public sealed class FeedbackService
{
    public Feedback Create(
        AuthenticatedContext context,
        string feedbackId,
        string zoneId,
        byte[] draftJson,
        IManagedResourceStore store)
    {
        return Write(context, feedbackId, zoneId, draftJson, store, expectedRevision: 0);
    }

    /// <summary>
    /// Agent feedback inside an owner-approved grant. The feedback ID is minted
    /// server-side (an agent never names the identity it writes) and the owning
    /// zone comes from the seen target's registered zone.
    /// </summary>
    public Feedback CreateAgentFeedback(
        AuthenticatedContext context,
        byte[] draftJson,
        IManagedResourceStore store)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(draftJson);

        FeedbackDraft parsed;
        try
        {
            parsed = FeedbackCodec.DecodeDraft(draftJson);
        }
        catch (ArgumentException)
        {
            throw;
        }

        if (string.IsNullOrEmpty(parsed.TargetResourceId))
        {
            throw new ArgumentException("Agent feedback requires the seen target resource.");
        }

        var zoneId = store.TryGetZone(parsed.TargetResourceId, out var zone) && !string.IsNullOrEmpty(zone)
            ? zone
            : throw new ArgumentException("The feedback target has no registered zone.");
        return Write(context, MintFeedbackId(), zoneId, draftJson, store, expectedRevision: 0);
    }

    public Feedback Read(
        AuthenticatedContext context,
        string feedbackId,
        long? expectedRevision,
        IManagedResourceReader store)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(store);
        EnsureTrusted(context);
        if (string.IsNullOrEmpty(feedbackId))
        {
            throw new ArgumentException("The feedback ID is required.");
        }

        byte[] raw;
        try
        {
            raw = store.ReadContent(feedbackId, expectedRevision);
        }
        catch (ManagedResourceUnavailableException ex)
        {
            throw new ArgumentException("The feedback record is unavailable.", ex);
        }

        try
        {
            var stored = FeedbackCodec.DecodeStored(raw);
            store.ValidateRegistration(feedbackId, expectedRevision);
            var liveRevision = expectedRevision ?? ResolveLiveRevision(store, feedbackId);
            return ToFeedback(stored, liveRevision);
        }
        catch (ArgumentException)
        {
            throw;
        }
        catch (ManagedResourceUnavailableException ex)
        {
            throw new ArgumentException("The feedback record is unavailable.", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(raw);
        }
    }

    private static Feedback Write(
        AuthenticatedContext context,
        string feedbackId,
        string zoneId,
        byte[] draftJson,
        IManagedResourceStore store,
        long expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(store);
        EnsureTrusted(context);
        ArgumentNullException.ThrowIfNull(draftJson);
        if (string.IsNullOrEmpty(feedbackId) || string.IsNullOrEmpty(zoneId))
        {
            throw new ArgumentException("The feedback ID and zone are required.");
        }

        FeedbackDraft draft;
        try
        {
            draft = FeedbackCodec.DecodeDraft(draftJson);
        }
        catch (ArgumentException)
        {
            throw;
        }

        FeedbackValidator.ValidateDraft(draft, feedbackId, zoneId);

        // The seen target must exist right now at exactly the seen revision:
        // feedback anchors to evidence the giver could actually see, never to a
        // guessed ID or a moved revision. The record keeps the seen revision as
        // a truthful historical anchor; nothing here rewrites the target.
        var targetZone = store.TryGetZone(draft.TargetResourceId!, out var zone) ? zone : null;
        if (string.IsNullOrEmpty(targetZone))
        {
            throw new ArgumentException("The feedback target is not registered.");
        }

        byte[]? targetRaw = null;
        try
        {
            targetRaw = store.ReadContent(draft.TargetResourceId!, draft.TargetRevision);
        }
        catch (ManagedResourceUnavailableException ex)
        {
            throw new ArgumentException("The feedback target revision is unavailable.", ex);
        }
        finally
        {
            if (targetRaw is { Length: > 0 })
            {
                CryptographicOperations.ZeroMemory(targetRaw);
            }
        }

        var now = DateTimeOffset.UtcNow;
        var stored = new StoredFeedback(
            FeedbackCodec.CurrentSchemaVersion,
            feedbackId,
            zoneId,
            draft.TargetResourceId!,
            draft.TargetRevision!.Value,
            draft.Kind!.Value,
            draft.Note,
            context.Kind == PrincipalKind.Agent ? ExecutorKind.Agent : ExecutorKind.Owner,
            draft.CreationMethod!.Value,
            now);
        var payload = FeedbackCodec.EncodeStored(stored);
        try
        {
            // Revision-gated exactly like canonical memory writes: 0 creates
            // only when absent. Feedback records are append-only; no revise
            // path exists, so a verdict can never be silently edited.
            var revision = store.WriteContent(feedbackId, zoneId, payload, expectedRevision);
            return ToFeedback(stored, revision);
        }
        catch (ManagedResourceUnavailableException ex)
        {
            throw new ArgumentException("The feedback write was rejected by vault storage.", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    internal static string MintFeedbackId() =>
        DomainIdentifiers.FeedbackIdPrefix + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    internal static Feedback ToFeedback(StoredFeedback stored, long revision) => new(
        stored.Id,
        stored.ZoneId,
        revision,
        stored.SchemaVersion,
        stored.TargetResourceId,
        stored.TargetRevision,
        stored.Kind,
        stored.Note,
        stored.Executor,
        stored.CreationMethod,
        stored.RecordedAtUtc);

    private static long ResolveLiveRevision(IManagedResourceReader store, string feedbackId)
    {
        if (store is IManagedResourceStore full)
        {
            var live = full.ListResources().FirstOrDefault(resource =>
                string.Equals(resource.ResourceId, feedbackId, StringComparison.Ordinal));
            if (live is not null)
            {
                return live.Revision;
            }
        }

        throw new ArgumentException("The feedback record is unavailable.");
    }

    private static void EnsureTrusted(AuthenticatedContext actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
    }
}
