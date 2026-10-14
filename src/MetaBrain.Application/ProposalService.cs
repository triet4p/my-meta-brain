using System.Security.Cryptography;
using MetaBrain.Core.Domain;
using MetaBrain.Core.Security;

namespace MetaBrain.Application;

/// <summary>
/// S4-T5 proposal authority over the shared encrypted canonical store. An
/// agent proposes, inside an owner-approved grant, that a memory item be
/// created or revised at one exact frozen revision; the proposal is a
/// pending owner-review record in its own namespace, never a write to the
/// item itself and never a publication. There is no second store, no second
/// key, and no grant state here: persistence goes only through
/// <see cref="IManagedResourceStore"/>, so recording a proposal cannot
/// expand an approved snapshot (R11), cannot mutate the subject item, and
/// cannot publish anything.
/// <para>
/// Identity is derived server-side from the approved inbox destination plus
/// the caller's idempotency key, so a repeated operation resolves to the
/// same record: an identical replay returns the stored proposal unchanged,
/// and a reuse of the same key with different content fails closed with a
/// conflict instead of overwriting the winner. The payload hash pins the
/// content the first write of the key carried.
/// </para>
/// </summary>
public sealed class ProposalService
{
    /// <summary>
    /// Records one agent proposal inside an owner-approved grant. The
    /// handler has already authorized the <c>proposal.create</c> operation
    /// and the exact inbox destination against the frozen session scope;
    /// this service proves the revise precondition (the subject exists right
    /// now at exactly the named revision), validates the proposed content,
    /// and commits create-only. A create-only miss on the derived ID is
    /// resolved against the stored record: identical content is returned
    /// (idempotent replay), different content raises a conflict. Nothing is
    /// written in either failure case, and no subject byte is ever touched.
    /// </summary>
    public Proposal CreateAgentProposal(
        AuthenticatedContext context,
        string grantId,
        byte[] draftJson,
        IManagedResourceStore store)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(draftJson);
        EnsureTrusted(context);

        ProposalDraft draft;
        try
        {
            draft = ProposalCodec.DecodeDraft(draftJson);
        }
        catch (ArgumentException)
        {
            throw;
        }

        // The recorded time is server-stamped before validation, so a body
        // can neither backdate nor post-date the record it writes, and the
        // proposed item always validates against a trusted recorded time.
        var recordedAtUtc = DateTimeOffset.UtcNow;
        draft = draft with { Item = draft.Item is { } item ? item with { RecordedAtUtc = recordedAtUtc } : null };

        ProposalValidator.ValidateDraft(draft);

        // The owning zone comes from the approved inbox destination's
        // registered zone, exactly like an agent link takes its zone from
        // its subject endpoint: an unregistered destination fails closed
        // before anything is derived or written.
        if (!store.TryGetZone(draft.DestinationResourceId!, out var zone) || string.IsNullOrEmpty(zone))
        {
            throw new ArgumentException("The proposal inbox destination is not registered.");
        }

        if (draft.Kind == ProposalKind.Revise)
        {
            VerifySubjectPrecondition(store, draft.SubjectResourceId!, draft.SubjectRevision!.Value);
        }

        var proposalId = ProposalValidator.DeriveProposalId(grantId, draft.DestinationResourceId!, draft.IdempotencyKey!);
        var stored = new StoredProposal(
            ProposalCodec.CurrentSchemaVersion,
            proposalId,
            zone,
            draft.Kind!.Value,
            draft.DestinationResourceId!,
            draft.DestinationRevision!.Value,
            draft.SubjectResourceId,
            draft.SubjectRevision,
            draft.IdempotencyKey!,
            string.Empty,
            draft.Item!,
            ExecutorOf(context),
            recordedAtUtc,
            draft.UserConfirmed == true);
        stored = stored with { PayloadHash = ProposalValidator.ComputePayloadHash(stored) };

        var payload = ProposalCodec.EncodeStored(stored);
        try
        {
            // Revision-gated exactly like canonical memory writes: 0 creates
            // only when absent. A stale concurrent retry cannot overwrite the
            // winner and nothing is published on mismatch.
            var revision = store.WriteContent(proposalId, zone, payload, expectedRevision: 0);
            return ToProposal(stored, revision);
        }
        catch (ManagedResourceUnavailableException)
        {
            // The derived ID already exists: this is either an identical
            // replay (idempotent success, the stored record is authoritative)
            // or a reuse of the same key for different content (conflict,
            // nothing mutates).
            var existing = Read(proposalId, expectedRevision: null, store);
            if (string.Equals(existing.IdempotencyKey, stored.IdempotencyKey, StringComparison.Ordinal) &&
                string.Equals(existing.PayloadHash, stored.PayloadHash, StringComparison.Ordinal))
            {
                return existing;
            }

            throw new ArgumentException("A proposal with this idempotency key already carries different content.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    /// <summary>
    /// Reads one stored proposal at the pinned revision. Trusted callers
    /// only: the agent surface never reads proposals through this service.
    /// </summary>
    public Proposal Read(
        string proposalId,
        long? expectedRevision,
        IManagedResourceReader store)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (string.IsNullOrEmpty(proposalId) || !DomainIdentifiers.IsProposalId(proposalId))
        {
            throw new ArgumentException("The proposal ID is required.");
        }

        byte[]? raw = null;
        try
        {
            raw = store.ReadContent(proposalId, expectedRevision);
            var stored = ProposalCodec.DecodeStored(raw);
            store.ValidateRegistration(proposalId, expectedRevision);
            var liveRevision = expectedRevision ?? ResolveLiveRevision(store, proposalId);
            return ToProposal(stored, liveRevision);
        }
        catch (ManagedResourceUnavailableException ex)
        {
            throw new ArgumentException("The proposal record is unavailable.", ex);
        }
        finally
        {
            if (raw is { Length: > 0 })
            {
                CryptographicOperations.ZeroMemory(raw);
            }
        }
    }

    /// <summary>
    /// Lean inbox identities from the encrypted manifest: ID, live revision,
    /// and owning zone only, never the proposed body.
    /// </summary>
    public static IReadOnlyList<ProposalIdentity> List(IManagedResourceStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        return store.ListResources()
            .Where(resource => DomainIdentifiers.IsProposalId(resource.ResourceId))
            .Select(resource => new ProposalIdentity(resource.ResourceId, resource.Revision, resource.ZoneId))
            .OrderBy(identity => identity.Id, StringComparer.Ordinal)
            .ToArray();
    }

    internal static Proposal ToProposal(StoredProposal stored, long revision) => new(
        stored.Id,
        stored.ZoneId,
        revision,
        stored.SchemaVersion,
        stored.Kind,
        stored.DestinationResourceId,
        stored.DestinationRevision,
        stored.SubjectResourceId,
        stored.SubjectRevision,
        stored.IdempotencyKey,
        stored.PayloadHash,
        stored.Item,
        stored.Executor,
        stored.RecordedAtUtc,
        stored.ClaimedUserConfirmed);

    /// <summary>
    /// The revise precondition: the subject item must exist right now at
    /// exactly the named revision. A guessed ID or a moved revision fails
    /// closed, so a proposal can never be recorded against a revision the
    /// vault does not hold, and the subject's own bytes are never rewritten.
    /// </summary>
    private static void VerifySubjectPrecondition(
        IManagedResourceStore store,
        string subjectResourceId,
        long subjectRevision)
    {
        byte[]? raw = null;
        try
        {
            raw = store.ReadContent(subjectResourceId, subjectRevision);
            var stored = MemoryEnvelopeCodec.DecodeStored(raw);
            if (!string.Equals(stored.Id, subjectResourceId, StringComparison.Ordinal))
            {
                throw new ArgumentException("The revise subject revision is unavailable.");
            }
        }
        catch (ArgumentException)
        {
            throw;
        }
        catch (IOException ex)
        {
            throw new ArgumentException("The revise subject revision is unavailable.", ex);
        }
        finally
        {
            if (raw is { Length: > 0 })
            {
                CryptographicOperations.ZeroMemory(raw);
            }
        }
    }

    private static long ResolveLiveRevision(IManagedResourceReader store, string proposalId)
    {
        if (store is IManagedResourceStore full)
        {
            var live = full.ListResources().FirstOrDefault(resource =>
                string.Equals(resource.ResourceId, proposalId, StringComparison.Ordinal));
            if (live is not null)
            {
                return live.Revision;
            }
        }

        throw new ArgumentException("The proposal record is unavailable.");
    }

    private static ExecutorKind ExecutorOf(AuthenticatedContext context) =>
        context.Kind == PrincipalKind.Agent ? ExecutorKind.Agent : ExecutorKind.Owner;

    private static void EnsureTrusted(AuthenticatedContext actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
    }
}
