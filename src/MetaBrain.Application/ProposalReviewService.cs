using System.Security.Cryptography;
using MetaBrain.Core.Domain;
using MetaBrain.Core.Security;

namespace MetaBrain.Application;

/// <summary>
/// One S5-T4 owner review outcome: which pending proposal was reviewed, at
/// which exact revision, the canonical item the review wrote, the publication
/// state the shared lifecycle record of that item carries now, and whether
/// this act is what recorded that decision (false = an earlier attempt already
/// wrote exactly this content and this decision, and nothing was written
/// twice).
/// </summary>
public sealed record ProposalReviewResult(
    string ProposalId,
    long ProposalRevision,
    ProposalKind Kind,
    string ItemId,
    MemoryItem Item,
    PublicationState Publication,
    long LifecycleRevision,
    bool DecisionApplied);

/// <summary>
/// S5-T4 owner review authority over one pending S4-T5 proposal. Publishing
/// writes the reviewed content as canonical memory content through the same
/// S2-T2 revision-gated service and records the owner's decision as the
/// publication state of the resulting item; rejecting records the owner's
/// decision without ever applying the proposal to the item it would have
/// revised.
/// <para>
/// The attribution of the written item — author, creation method and evidence
/// level — is the stored proposal's own claim. A reviewed body may change what
/// the record says, but a reviewed draft that names another author, creation
/// method or evidence is refused, and a revise proposal whose claim disagrees
/// with the item it would revise is refused too, so approving an AI-written or
/// tool-observed proposal can never relabel it as user-written or
/// user-confirmed (R12).
/// </para>
/// <para>
/// The act is crash/retry safe without a journal of its own. The item is
/// written first and the decision second, both revision-gated; every step is
/// re-derived from encrypted state, so an interrupted attempt is either
/// invisible (nothing written) or resumable: a retry that carries exactly the
/// content already stored completes the decision, while a retry that carries
/// other content — or a pinned subject that moved to other content under a
/// concurrent editor — is refused without overwriting anything. A fully
/// completed act retried returns the same outcome and writes nothing twice,
/// and a decision the publication state cannot express (for example
/// re-publishing a withdrawn item) is refused before the content write.
/// </para>
/// <para>
/// Nothing here grants, widens, or copies an approval: the act is owner-only,
/// needs an unlocked vault, and only ever writes the canonical store through
/// the existing services. The proposal record itself is never edited — it is
/// the input of the review and stays exactly as the agent wrote it, so the
/// pending identity, the claimed evidence and the agent authorship remain
/// readable after the review.
/// </para>
/// </summary>
public sealed class ProposalReviewService
{
    /// <summary>
    /// Publishes one pending proposal at <paramref name="expectedProposalRevision"/>
    /// with the owner's reviewed content. A create proposal writes the new
    /// canonical item derived from the proposal; a revise proposal writes the
    /// new revision of the item and exact revision the proposal pinned, so a
    /// concurrent editor that already moved that item fails closed with the
    /// draft still in the owner's hands.
    /// </summary>
    public ProposalReviewResult Publish(
        AuthenticatedContext context,
        string proposalId,
        long expectedProposalRevision,
        byte[] reviewedDraftJson,
        IManagedResourceStore store)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(reviewedDraftJson);
        if (expectedProposalRevision < 1)
        {
            throw new ArgumentException("The reviewed proposal revision is required.");
        }

        var proposal = ReadProposal(proposalId, expectedProposalRevision, store);
        var reviewed = MemoryEnvelopeCodec.DecodeDraft(reviewedDraftJson);
        var itemId = ItemIdFor(proposal);
        MemoryItemValidator.ValidateDraft(reviewed, itemId, proposal.ZoneId);
        RequireProposalAttribution(proposal, reviewed);
        if (!CanRecord(ReadRecord(context, itemId, store), LifecycleAct.Publish))
        {
            throw new ArgumentException(
                "The item's publication state cannot record this decision; re-propose it first.");
        }

        var item = WriteReviewItem(
            context, proposal, itemId, reviewed, reviewedDraftJson,
            reviseSubject: proposal.Kind == ProposalKind.Revise, store);
        var (record, applied) = Decide(context, itemId, proposal.Id, LifecycleAct.Publish, transitionJson: null, store);
        return new ProposalReviewResult(
            proposal.Id, proposal.Revision, proposal.Kind, itemId, item, record.Publication, record.Revision, applied);
    }

    /// <summary>
    /// Rejects one pending proposal at <paramref name="expectedProposalRevision"/>.
    /// The proposal's own content is written as the canonical item derived from
    /// the proposal — an ordinary encrypted memory item in the <c>rejected</c>
    /// publication state — and the owner's rationale is recorded in its
    /// lifecycle history. A revise proposal is never applied: the item it would
    /// have revised keeps its revision, its content and its own publication
    /// state, and the rejection is recorded on the review item instead. A
    /// <c>rejected</c> item is not current in a search and the decision is
    /// reversible through <c>lifecycle.repropose</c>, exactly like every other
    /// publication decision.
    /// </summary>
    public ProposalReviewResult Reject(
        AuthenticatedContext context,
        string proposalId,
        long expectedProposalRevision,
        byte[]? rationaleJson,
        IManagedResourceStore store)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(store);
        if (expectedProposalRevision < 1)
        {
            throw new ArgumentException("The reviewed proposal revision is required.");
        }

        var proposal = ReadProposal(proposalId, expectedProposalRevision, store);
        var itemId = DomainIdentifiers.ReviewedItemIdFor(proposal.Id);

        // The rejected content is the proposal's own draft, but the record is
        // recorded now: the proposal's stamp says when the agent proposed, not
        // when this item was recorded.
        var reviewed = proposal.Item with { RecordedAtUtc = DateTimeOffset.UtcNow };
        MemoryItemValidator.ValidateDraft(reviewed, itemId, proposal.ZoneId);
        if (!CanRecord(ReadRecord(context, itemId, store), LifecycleAct.RejectProposal))
        {
            throw new ArgumentException(
                "The item's publication state cannot record this decision; re-propose it first.");
        }

        var item = WriteReviewItem(context, proposal, itemId, reviewed, reviewedDraftJson: null, reviseSubject: false, store);
        var (record, applied) = Decide(
            context, itemId, proposal.Id, LifecycleAct.RejectProposal, rationaleJson, store);
        return new ProposalReviewResult(
            proposal.Id, proposal.Revision, proposal.Kind, itemId, item, record.Publication, record.Revision, applied);
    }

    private static string ItemIdFor(Proposal proposal) => proposal.Kind == ProposalKind.Revise
        ? proposal.SubjectResourceId!
        : DomainIdentifiers.ReviewedItemIdFor(proposal.Id);

    /// <summary>
    /// Reads the reviewed proposal at exactly the revision the owner looked at.
    /// An unknown proposal, a revision that moved under the review, or a record
    /// that is not a proposal at all are one generic outcome: the review never
    /// acts on bytes the owner did not see.
    /// </summary>
    private static Proposal ReadProposal(string proposalId, long expectedProposalRevision, IManagedResourceStore store)
    {
        if (string.IsNullOrEmpty(proposalId) || !DomainIdentifiers.IsProposalId(proposalId))
        {
            throw new ArgumentException("The reviewed proposal ID must be an opaque safe proposal identifier.");
        }

        try
        {
            return new ProposalService().Read(proposalId, expectedProposalRevision, store);
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException("The reviewed proposal is unavailable at that revision.", ex);
        }
    }

    /// <summary>
    /// R12: the written item's author, creation method and evidence level are
    /// the stored proposal's own claim. The owner is free to change what the
    /// record says — kind, level, title, body, citations, applicability and
    /// occurred time — but an approval never turns an AI-written or
    /// tool-observed proposal into a user-written or user-confirmed record.
    /// </summary>
    private static void RequireProposalAttribution(Proposal proposal, MemoryItemDraft reviewed)
    {
        if (reviewed.Author != proposal.Item.Author ||
            reviewed.CreationMethod != proposal.Item.CreationMethod ||
            reviewed.Evidence != proposal.Item.Evidence)
        {
            throw new ArgumentException(
                "A reviewed proposal keeps its author, creation method and evidence level; approving it never relabels an AI-written or tool-observed record as user-written or user-confirmed.");
        }
    }

    /// <summary>
    /// The revise half of the same rule for a record that already exists: the
    /// reviewed attribution must be the attribution the stored item carries, so
    /// an approval cannot rewrite the authorship, creation method or evidence
    /// of the item being revised either.
    /// </summary>
    private static void RequireSubjectAttribution(MemoryItem subject, MemoryItemDraft reviewed)
    {
        if (reviewed.Author != subject.Author ||
            reviewed.CreationMethod != subject.CreationMethod ||
            reviewed.Evidence != subject.Evidence)
        {
            throw new ArgumentException(
                "The proposal's attribution disagrees with the item it would revise; a revise keeps the stored author, creation method and evidence level.");
        }
    }

    /// <summary>
    /// Writes the item the review acts on, or resolves the item an earlier
    /// attempt of the same act already wrote. The two paths are one contract:
    /// the exact bytes this act would write are derived from the reviewed
    /// content, and an item that already holds those bytes is that attempt's
    /// own write, so the act resumes instead of duplicating or overwriting.
    /// </summary>
    private static MemoryItem WriteReviewItem(
        AuthenticatedContext context,
        Proposal proposal,
        string itemId,
        MemoryItemDraft reviewed,
        byte[]? reviewedDraftJson,
        bool reviseSubject,
        IManagedResourceStore store)
    {
        var expected = ExpectedEnvelope(itemId, proposal.ZoneId, reviewed);
        var live = ReadLiveItem(store, itemId);
        if (reviseSubject)
        {
            var pinned = proposal.SubjectRevision!.Value;
            if (live is null || live.Value.Revision < pinned)
            {
                throw new ArgumentException("The item this proposal would revise is unavailable.");
            }

            if (live.Value.Revision == pinned)
            {
                RequireSubjectAttribution(live.Value.Item, reviewed);
                return new MemoryItemService().Revise(
                    context, itemId, proposal.ZoneId, pinned, reviewedDraftJson!, store);
            }

            if (HoldsReviewedContent(store, itemId, live.Value.Revision, expected))
            {
                return live.Value.Item;
            }

            throw new ArgumentException(
                "The item this proposal would revise moved past the reviewed revision and holds other content: the reviewed revision was not applied and nothing was overwritten.");
        }

        if (live is null)
        {
            return reviewedDraftJson is not null
                ? new MemoryItemService().Create(context, itemId, proposal.ZoneId, reviewedDraftJson, store)
                : ImportEnvelope(context, itemId, proposal.ZoneId, expected, store);
        }

        if (HoldsReviewedContent(store, itemId, live.Value.Revision, expected))
        {
            return live.Value.Item;
        }

        throw new ArgumentException(
            "This proposal's review item already holds other content; nothing was overwritten.");
    }

    /// <summary>
    /// Writes the exact bytes of the reviewed content as a create-only
    /// canonical item through the explicit import boundary: the envelope
    /// identity is checked against the target and every citation anchor against
    /// the live registry, so a second review of the same proposal fails closed
    /// instead of writing a second copy.
    /// </summary>
    private static MemoryItem ImportEnvelope(
        AuthenticatedContext context,
        string itemId,
        string zoneId,
        StoredMemoryEnvelope envelope,
        IManagedResourceStore store)
    {
        var bytes = MemoryEnvelopeCodec.EncodeStored(envelope);
        try
        {
            return new MemoryItemService().Import(context, itemId, zoneId, expectedRevision: 0, bytes, store);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    /// <summary>
    /// The envelope this act writes for the reviewed content: the identity is
    /// the review target, the executor is the owner acting now, and every other
    /// field comes from the reviewed draft, exactly as the shared write service
    /// composes its own envelope.
    /// </summary>
    private static StoredMemoryEnvelope ExpectedEnvelope(string itemId, string zoneId, MemoryItemDraft draft) =>
        new(
            MemoryEnvelopeCodec.CurrentSchemaVersion,
            itemId,
            zoneId,
            draft.Kind!.Value,
            draft.Author!.Value,
            ExecutorKind.Owner,
            draft.CreationMethod!.Value,
            draft.Evidence!.Value,
            draft.Level!.Value,
            draft.Title,
            draft.Body!,
            MemoryItemValidator.NormalizeSources(draft.Sources),
            draft.OccurredAtUtc,
            draft.RecordedAtUtc!.Value,
            draft.Applicability);

    /// <summary>
    /// True when the stored bytes of one revision already hold exactly the
    /// reviewed content, so an interrupted or repeated attempt resumes instead
    /// of writing a second copy. The recorded instant is not compared: it is
    /// the stamp of the write itself, each attempt carries its own, and the
    /// instant the content was first recorded is the truthful one and stays.
    /// </summary>
    private static bool HoldsReviewedContent(
        IManagedResourceStore store,
        string itemId,
        long revision,
        StoredMemoryEnvelope expected)
    {
        byte[]? raw = null;
        try
        {
            raw = store.ReadContent(itemId, revision);
            var stored = MemoryEnvelopeCodec.DecodeStored(raw);
            return string.Equals(stored.Id, expected.Id, StringComparison.Ordinal) &&
                string.Equals(stored.ZoneId, expected.ZoneId, StringComparison.Ordinal) &&
                stored.SchemaVersion == expected.SchemaVersion &&
                stored.Kind == expected.Kind &&
                stored.Author == expected.Author &&
                stored.Executor == expected.Executor &&
                stored.CreationMethod == expected.CreationMethod &&
                stored.Evidence == expected.Evidence &&
                stored.Level == expected.Level &&
                string.Equals(stored.Title, expected.Title, StringComparison.Ordinal) &&
                string.Equals(stored.Body, expected.Body, StringComparison.Ordinal) &&
                stored.OccurredAtUtc == expected.OccurredAtUtc &&
                stored.Applicability == expected.Applicability &&
                stored.Sources.SequenceEqual(expected.Sources);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException)
        {
            return false;
        }
        finally
        {
            if (raw is { Length: > 0 })
            {
                CryptographicOperations.ZeroMemory(raw);
            }
        }
    }

    /// <summary>Reads one canonical item at its live revision, or null when it does not exist.</summary>
    private static (MemoryItem Item, long Revision)? ReadLiveItem(IManagedResourceStore store, string itemId)
    {
        var live = store.ListResources()
            .FirstOrDefault(resource => string.Equals(resource.ResourceId, itemId, StringComparison.Ordinal));
        if (live is null)
        {
            return null;
        }

        byte[]? raw = null;
        try
        {
            raw = store.ReadContent(itemId, live.Revision);
            var stored = MemoryEnvelopeCodec.DecodeStored(raw);
            if (!string.Equals(stored.Id, itemId, StringComparison.Ordinal))
            {
                return null;
            }

            return (MemoryItemService.ToItem(stored, live.Revision), live.Revision);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException)
        {
            return null;
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
    /// The publication state this decision records, or null for an act that is
    /// not a publication decision.
    /// </summary>
    private static PublicationState? DecidedPublication(LifecycleAct act) => act switch
    {
        LifecycleAct.Publish => PublicationState.Published,
        LifecycleAct.RejectProposal => PublicationState.Rejected,
        _ => null
    };

    /// <summary>
    /// Whether the record's current state can take this decision: a record that
    /// does not exist yet is created by the act, a record still in
    /// <c>proposal</c> takes the edge, and a record already sitting on this
    /// decision is the outcome a retry returns. Every other state is refused by
    /// the core's own policy table rather than forced.
    /// </summary>
    private static bool CanRecord(LifecycleRecord? record, LifecycleAct act) =>
        record is null ||
        record.Publication == PublicationState.Proposal ||
        record.Publication == DecidedPublication(act);

    private static LifecycleRecord? ReadRecord(
        AuthenticatedContext context,
        string itemId,
        IManagedResourceStore store) =>
        new LifecycleService().Read(context, itemId, expectedRevision: null, store);

    /// <summary>
    /// Records one owner decision through the shared lifecycle core. The review
    /// item's lifecycle record is created when it does not exist yet — its
    /// initial publication state is <c>proposal</c> by the core's own rule, and
    /// the reason recorded on it names the proposal the review acted on — and
    /// the decision is then one explicit publication act whose executor is the
    /// owner and whose time and rationale stay in the record's history. The
    /// returned flag says whether this call is what wrote the edge.
    /// </summary>
    private static (LifecycleRecord Record, bool Applied) Decide(
        AuthenticatedContext context,
        string itemId,
        string proposalId,
        LifecycleAct act,
        byte[]? transitionJson,
        IManagedResourceStore store)
    {
        var service = new LifecycleService();
        var record = ReadRecord(context, itemId, store);
        if (record is null)
        {
            var draft = new LifecycleDraft(
                CreationMethod.DirectWrite, "review item of proposal " + proposalId, null, null, null, null, null);
            var draftJson = LifecycleCodec.EncodeDraft(draft);
            try
            {
                record = service.Create(context, itemId, draftJson, store);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(draftJson);
            }
        }

        if (record.Publication == PublicationState.Proposal)
        {
            return (service.Transition(context, itemId, act, record.Revision, transitionJson, store), true);
        }

        if (record.Publication == DecidedPublication(act))
        {
            return (record, false);
        }

        throw new ArgumentException(
            "The item's publication state cannot record this decision (" +
            LifecycleTokens.Publication(record.Publication) + "); re-propose it first.");
    }
}
