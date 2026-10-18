using System.Security.Cryptography;
using System.Text.Json;
using MetaBrain.Core.Domain;
using MetaBrain.Core.Security;

namespace MetaBrain.Application;

/// <summary>
/// One private evidence pin a declassification stands on: an opaque memory
/// item or registered source at the exact revision the owner reviewed.
/// Snapshots, relations, lifecycle records, archives, journals, feedback and
/// proposals are never lineage pins.
/// </summary>
public sealed record DeclassifyPin(string ResourceId, long Revision);

/// <summary>
/// Owner-facing preview of a sanitized cross-zone publication. Everything the
/// shared zone would receive is shown verbatim (title, body, kind, level,
/// attribution, applicability, occurrence and the empty shared citation set)
/// beside the private pins it would stand on and whether those pins still
/// match live state. Mutates nothing, mints no grant and publishes no catalog
/// entry.
/// </summary>
public sealed record DeclassifyPreview(
    string DestinationResourceId,
    string DestinationZoneId,
    MemoryItemDraft Sanitized,
    DeclassifyPin[] Pins,
    bool PinsCurrent,
    string[] MismatchedResourceIds);

/// <summary>
/// One completed owner declassification: the sanitized shared item as stored,
/// the publication record that now covers it, the encrypted lineage relations
/// that retain the private evidence, and whether this call is what wrote them
/// (false = an earlier identical call already wrote exactly this content,
/// lineage and decision).
/// </summary>
public sealed record DeclassifyResult(
    string DestinationResourceId,
    long DestinationRevision,
    MemoryItem Item,
    long LifecycleRevision,
    string[] LineageRelationIds,
    bool DecisionApplied);

/// <summary>
/// S6-T5 owner declassification and reviewed-derivative publication into an
/// explicit destination zone through the owner channel (R06/R08/R13).
///
/// A sanitized shared copy is written as an ordinary revision-gated canonical
/// memory item in the destination zone with EMPTY citations, so no private
/// identifier, locator, span, title, name or backlink crosses into shared
/// readability. The private evidence stays reachable only through encrypted
/// <c>derived_from</c> relations (one per pinned private source, accepted by
/// this owner act) that a shared-only session cannot open: both endpoints must
/// sit inside its frozen scope, and the private endpoint never does.
///
/// There is no second store, no second key and no second policy here:
/// persistence goes only through <see cref="IManagedResourceStore"/> and the
/// existing memory, relation and lifecycle services. Catalog publication stays
/// a separate owner act that never opens a body, and a new shared item or a
/// new shared revision never enters an already-frozen grant snapshot.
///
/// Two entry points share one write path: explicit pins (any owner-reviewed
/// private items and/or sources, which covers already-accepted synthesis
/// outputs) and a pending create proposal (whose cited sources become the
/// pins, which covers synthesis outputs still awaiting review). Both require
/// the owner's explicit sanitized content with the source attribution intact,
/// both revalidate every pin at its live revision before the commit, and both
/// are crash/retry safe without a journal of their own.
/// </summary>
public sealed class DeclassifyService
{
    internal const string LineageReason =
        "Owner declassification: sanitized shared derivative; private evidence retained encrypted and unreadable from the shared zone.";

    private const string LifecycleReasonPrefix = "Owner declassification into ";

    /// <summary>Generic read-time warning carried on a shared derivative whose private lineage moved.</summary>
    public const string DerivativeStaleWarning = "derivative_source_changed";

    private const int MaximumPins = 32;

    /// <summary>
    /// Previews one explicit-pins declassification. Resolves every pin against
    /// live state and reports whether each still matches; writes nothing.
    /// </summary>
    public DeclassifyPreview Preview(
        AuthenticatedContext context,
        string destinationResourceId,
        string destinationZoneId,
        byte[] requestJson,
        IManagedResourceStore store)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(requestJson);
        EnsureOwner(context);

        var (pins, sanitized) = DecodeExplicitBody(requestJson);
        ValidateDestination(destinationResourceId, destinationZoneId, pins);
        ValidateSanitizedForPins(sanitized, destinationResourceId, destinationZoneId, store, pins);
        var (current, mismatched) = CheckPinsCurrent(pins, store);
        return new DeclassifyPreview(destinationResourceId, destinationZoneId, sanitized, pins, current, mismatched);
    }

    /// <summary>
    /// Publishes one explicit-pins declassification: the sanitized shared item
    /// (create-only), one accepted <c>derived_from</c> relation per pin, and
    /// the published lifecycle record. A retry carrying exactly the stored
    /// content, lineage and decision resumes instead of duplicating; any other
    /// content, a moved pin, or an occupied destination fails closed.
    /// </summary>
    public DeclassifyResult Publish(
        AuthenticatedContext context,
        string destinationResourceId,
        string destinationZoneId,
        byte[] requestJson,
        IManagedResourceStore store)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(requestJson);
        EnsureOwner(context);

        var (pins, sanitized) = DecodeExplicitBody(requestJson);
        ValidateDestination(destinationResourceId, destinationZoneId, pins);
        ValidateSanitizedForPins(sanitized, destinationResourceId, destinationZoneId, store, pins);
        RequirePinsCurrent(pins, store);
        return WriteDeclassified(context, destinationResourceId, destinationZoneId, sanitized, pins, store);
    }

    /// <summary>
    /// Publishes one pending create proposal as a sanitized shared derivative.
    /// The proposal's cited sources become the lineage pins exactly as the
    /// agent recorded them; the owner's sanitized content must keep the
    /// proposal's author, creation method and evidence level. A revise proposal
    /// is refused: only a create carries standalone content to sanitize.
    /// </summary>
    public DeclassifyResult PublishFromProposal(
        AuthenticatedContext context,
        string proposalId,
        long proposalRevision,
        byte[] requestJson,
        IManagedResourceStore store)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(requestJson);
        EnsureOwner(context);
        if (string.IsNullOrEmpty(proposalId) || !DomainIdentifiers.IsProposalId(proposalId))
        {
            throw new ArgumentException("The declassified proposal ID must be an opaque safe proposal identifier.");
        }

        if (proposalRevision < 1)
        {
            throw new ArgumentException("The declassified proposal revision is required.");
        }

        var (destinationResourceId, destinationZoneId, sanitized) = DecodeProposalBody(requestJson);
        Proposal proposal;
        try
        {
            proposal = new ProposalService().Read(proposalId, proposalRevision, store);
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException("The declassified proposal is unavailable at that revision.", ex);
        }

        if (proposal.Kind != ProposalKind.Create)
        {
            throw new ArgumentException("Only a create proposal carries standalone content to declassify; a revise proposal revises its subject instead.");
        }

        var pins = PinsFromProposal(proposal);
        ValidateDestination(destinationResourceId, destinationZoneId, pins);
        RequireProposalAttribution(proposal, sanitized);
        MemoryItemValidator.ValidateDraft(sanitized, destinationResourceId, destinationZoneId);
        RequireSanitizedCitationsEmpty(sanitized);
        RequirePinsCurrent(pins, store);
        return WriteDeclassified(context, destinationResourceId, destinationZoneId, sanitized, pins, store);
    }

    /// <summary>
    /// Read-time staleness of one shared derivative over its encrypted private
    /// lineage: true when any <c>derived_from</c> ancestor is missing, moved,
    /// unreadable at its pinned revision, unavailable or revoked — directly or
    /// transitively through a cited memory item's own pinned citations — or
    /// when the lineage is cyclic or unbounded. Nothing is rewritten and no
    /// ancestor identity is disclosed: the caller only learns whether the
    /// derivative can still be traced to its recorded roots. Shared items with
    /// no declassification lineage are current under this check.
    /// </summary>
    public static bool EvaluateDerivative(string resourceId, IManagedResourceStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (string.IsNullOrEmpty(resourceId))
        {
            return false;
        }

        var pins = LoadLineagePins(resourceId, store);
        if (pins is null)
        {
            // An unreadable lineage record fails closed to stale: the
            // derivation cannot be traced as recorded.
            return true;
        }

        if (pins.Count == 0)
        {
            return false;
        }

        var synthetic = new Proposal(
            "prop-synthetic-derivative-probe",
            ZoneId: string.Empty,
            Revision: 0,
            SchemaVersion: ProposalCodec.CurrentSchemaVersion,
            Kind: ProposalKind.Create,
            DestinationResourceId: string.Empty,
            DestinationRevision: 0,
            SubjectResourceId: null,
            SubjectRevision: null,
            IdempotencyKey: "synthetic",
            PayloadHash: string.Empty,
            Item: new MemoryItemDraft(
                Kind: MemoryKind.Synthesis,
                Author: SpeakerRole.Assistant,
                CreationMethod: CreationMethod.Synthesis,
                Evidence: EvidenceLevel.ReportedSource,
                Level: AbstractionLevel.L2,
                Title: null,
                Body: "Synthetic derivative lineage probe; never stored.",
                Sources: pins.Select(pin => new MemorySourceReference(
                    SourceId: pin.ResourceId,
                    Locator: null,
                    Availability: null,
                    SourceRevision: pin.Revision,
                    Span: null)).ToArray(),
                OccurredAtUtc: null,
                RecordedAtUtc: DateTimeOffset.UtcNow,
                Applicability: null),
            Executor: ExecutorKind.Owner,
            RecordedAtUtc: DateTimeOffset.UtcNow,
            ClaimedUserConfirmed: false);
        return ProposalService.EvaluateReview(synthetic, store).NeedsReview;
    }

    private static DeclassifyResult WriteDeclassified(
        AuthenticatedContext context,
        string destinationResourceId,
        string destinationZoneId,
        MemoryItemDraft sanitized,
        DeclassifyPin[] pins,
        IManagedResourceStore store)
    {
        var item = WriteSharedItem(context, destinationResourceId, destinationZoneId, sanitized, store, out var itemApplied);
        var lineage = EnsureLineage(context, destinationResourceId, destinationZoneId, item.Revision, pins, store, out var lineageApplied);
        var lifecycleRevision = EnsurePublished(context, destinationResourceId, destinationZoneId, pins, store, out var lifecycleApplied);
        return new DeclassifyResult(
            destinationResourceId,
            item.Revision,
            item,
            lifecycleRevision,
            lineage,
            itemApplied && lineageApplied && lifecycleApplied);
    }

    private static MemoryItem WriteSharedItem(
        AuthenticatedContext context,
        string destinationResourceId,
        string destinationZoneId,
        MemoryItemDraft sanitized,
        IManagedResourceStore store,
        out bool applied)
    {
        byte[]? draft = null;
        try
        {
            draft = EncodeDraft(sanitized);
            try
            {
                var created = new MemoryItemService().Create(context, destinationResourceId, destinationZoneId, draft, store);
                applied = true;
                return created;
            }
            catch (ArgumentException)
            {
                var live = ReadLiveItem(store, destinationResourceId);
                if (live is not null && HoldsDeclassifiedContent(store, destinationResourceId, live.Value.Revision, destinationZoneId, sanitized))
                {
                    applied = false;
                    return live.Value.Item;
                }

                throw new ArgumentException("The shared destination already holds other content; nothing was overwritten.");
            }
        }
        finally
        {
            if (draft is { Length: > 0 })
            {
                CryptographicOperations.ZeroMemory(draft);
            }
        }
    }

    private static string[] EnsureLineage(
        AuthenticatedContext context,
        string destinationResourceId,
        string destinationZoneId,
        long destinationRevision,
        DeclassifyPin[] pins,
        IManagedResourceStore store,
        out bool applied)
    {
        applied = true;
        var ids = new List<string>(pins.Length);
        var existing = LoadLineageRelations(destinationResourceId, store);
        foreach (var pin in pins)
        {
            var match = existing.FirstOrDefault(relation =>
                string.Equals(relation.To.ResourceId, pin.ResourceId, StringComparison.Ordinal) &&
                relation.To.Revision == pin.Revision &&
                (relation.State == RelationState.Accepted || relation.State == RelationState.Proposed));
            if (match is not null)
            {
                ids.Add(match.Id);
                continue;
            }

            var relationId = "rel-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            var draft = RelationCodec.EncodeDraft(new RelationDraft(
                Kind: RelationKind.DerivedFrom,
                State: RelationState.Accepted,
                From: new RelationEndpoint(destinationResourceId, RelationEndpointKind.MemoryItem, destinationRevision, Span: null),
                To: new RelationEndpoint(pin.ResourceId, ClassifyPin(store, pin), pin.Revision, Span: null),
                CreationMethod: CreationMethod.DirectWrite,
                Reason: LineageReason,
                StateReason: null,
                OccurredAtUtc: null,
                RecordedAtUtc: DateTimeOffset.UtcNow,
                RequestEvidence: null));
            try
            {
                var written = new RelationService().Create(context, relationId, destinationZoneId, draft, store);
                ids.Add(written.Relation.Id);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(draft);
            }
        }

        if (ids.Count != pins.Length)
        {
            throw new ArgumentException("The declassified lineage could not be recorded for every pinned source.");
        }

        // A resumed publish (item and lineage already stored) applies nothing.
        if (existing.Count >= pins.Length && existing.All(relation =>
                pins.Any(pin => string.Equals(relation.To.ResourceId, pin.ResourceId, StringComparison.Ordinal) &&
                    relation.To.Revision == pin.Revision)))
        {
            applied = false;
        }

        return ids.ToArray();
    }

    private static long EnsurePublished(
        AuthenticatedContext context,
        string destinationResourceId,
        string destinationZoneId,
        DeclassifyPin[] pins,
        IManagedResourceStore store,
        out bool applied)
    {
        var service = new LifecycleService();
        var record = service.Read(context, destinationResourceId, expectedRevision: null, store);
        if (record is null)
        {
            var draft = new LifecycleDraft(
                CreationMethod: CreationMethod.DirectWrite,
                Reason: LifecycleReasonPrefix + destinationZoneId + ": sanitized shared derivative of " +
                    pins.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + " pinned private source(s).",
                Occurred: null,
                Recorded: null,
                ValidFrom: null,
                ValidUntil: null,
                ReviewAfter: null);
            var draftJson = LifecycleCodec.EncodeDraft(draft);
            try
            {
                record = service.Create(context, destinationResourceId, draftJson, store);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(draftJson);
            }
        }

        if (record.Publication == PublicationState.Published)
        {
            applied = false;
            return record.Revision;
        }

        if (record.Publication != PublicationState.Proposal)
        {
            throw new ArgumentException("The shared item's publication record cannot take this decision; re-propose it first.");
        }

        var decided = service.Transition(context, destinationResourceId, LifecycleAct.Publish, record.Revision, transitionJson: null, store);
        applied = true;
        return decided.Revision;
    }

    private static (DeclassifyPin[] Pins, MemoryItemDraft Item) DecodeExplicitBody(byte[] json)
    {
        var preamble = StripUtf8Preamble(json);
        var offset = json.Length - preamble.Length;
        using var document = JsonDocument.Parse(new ReadOnlyMemory<byte>(json, offset, preamble.Length));
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("The declassification request schema is invalid.");
        }

        if (!root.TryGetProperty("pins", out var pinsElement) || pinsElement.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException("The declassification request names at least one pinned private source.");
        }

        var pins = new List<DeclassifyPin>();
        foreach (var element in pinsElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException("A declassification pin is invalid.");
            }

            var id = element.TryGetProperty("resourceId", out var idValue) && idValue.ValueKind == JsonValueKind.String
                ? idValue.GetString()
                : null;
            var revision = element.TryGetProperty("revision", out var revisionValue) && revisionValue.ValueKind == JsonValueKind.Number
                ? revisionValue.GetInt64()
                : 0;
            pins.Add(new DeclassifyPin(id ?? string.Empty, revision));
        }

        if (!root.TryGetProperty("item", out var itemElement) || itemElement.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("The declassification request carries the sanitized shared content.");
        }

        var itemBytes = JsonSerializer.SerializeToUtf8Bytes(itemElement);
        try
        {
            return (pins.ToArray(), MemoryEnvelopeCodec.DecodeDraft(itemBytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(itemBytes);
        }
    }

    private static (string DestinationResourceId, string DestinationZoneId, MemoryItemDraft Item) DecodeProposalBody(byte[] json)
    {
        var preamble = StripUtf8Preamble(json);
        var offset = json.Length - preamble.Length;
        using var document = JsonDocument.Parse(new ReadOnlyMemory<byte>(json, offset, preamble.Length));
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("The declassification request schema is invalid.");
        }

        var destinationId = root.TryGetProperty("destinationResourceId", out var idValue) && idValue.ValueKind == JsonValueKind.String
            ? idValue.GetString()
            : null;
        var destinationZone = root.TryGetProperty("destinationZoneId", out var zoneValue) && zoneValue.ValueKind == JsonValueKind.String
            ? zoneValue.GetString()
            : null;
        if (string.IsNullOrEmpty(destinationId) || string.IsNullOrEmpty(destinationZone))
        {
            throw new ArgumentException("The declassified shared destination ID and zone are required.");
        }

        if (!root.TryGetProperty("item", out var itemElement) || itemElement.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("The declassification request carries the sanitized shared content.");
        }

        var itemBytes = JsonSerializer.SerializeToUtf8Bytes(itemElement);
        try
        {
            var draft = MemoryEnvelopeCodec.DecodeDraft(itemBytes);
            // The proposal path carries a wrapper {destination,item}: the
            // recorded time is the proposal's own recorded instant, not the
            // owner's send time, so require the draft's own timestamp.
            if (draft.RecordedAtUtc is null)
            {
                throw new ArgumentException("The sanitized shared content carries its recorded time.");
            }

            return (destinationId, destinationZone, draft);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(itemBytes);
        }
    }

    private static DeclassifyPin[] PinsFromProposal(Proposal proposal)
    {
        var sources = proposal.Item.Sources ?? Array.Empty<MemorySourceReference>();
        var pins = new List<DeclassifyPin>(sources.Length);
        foreach (var citation in sources)
        {
            if (string.IsNullOrEmpty(citation.SourceId) || citation.SourceRevision is not > 0)
            {
                throw new ArgumentException("The declassified proposal cites a source without a pinned revision; only pinned citations become lineage.");
            }

            pins.Add(new DeclassifyPin(citation.SourceId, citation.SourceRevision.Value));
        }

        if (pins.Count == 0)
        {
            throw new ArgumentException("The declassified proposal cites no pinned source; there is no lineage to retain.");
        }

        return pins.ToArray();
    }

    private static void ValidateDestination(
        string destinationResourceId,
        string destinationZoneId,
        DeclassifyPin[] pins)
    {
        if (string.IsNullOrEmpty(destinationResourceId) || !DomainIdentifiers.IsDomainIdentifier(destinationResourceId))
        {
            throw new ArgumentException("The shared destination ID must be an opaque safe memory identifier.");
        }

        if (string.IsNullOrEmpty(destinationZoneId) || !DomainIdentifiers.IsSafeIdentifier(destinationZoneId))
        {
            throw new ArgumentException("The shared destination zone must be an opaque safe identifier.");
        }

        if (pins is null || pins.Length == 0 || pins.Length > MaximumPins)
        {
            throw new ArgumentException("The declassification names between 1 and 32 pinned private sources.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pin in pins)
        {
            if (string.IsNullOrEmpty(pin.ResourceId) || !DomainIdentifiers.IsDomainIdentifier(pin.ResourceId) ||
                pin.Revision < 1 || !seen.Add(pin.ResourceId))
            {
                throw new ArgumentException("A declassification pin must name a distinct opaque memory or source revision.");
            }

            if (SourceSnapshotIds.IsSnapshotId(pin.ResourceId))
            {
                throw new ArgumentException("A declassification pins a canonical source or item revision, never a transient snapshot.");
            }

            if (string.Equals(pin.ResourceId, destinationResourceId, StringComparison.Ordinal))
            {
                throw new ArgumentException("A shared derivative never derives from itself.");
            }
        }
    }

    private static void ValidateSanitizedForPins(
        MemoryItemDraft sanitized,
        string destinationResourceId,
        string destinationZoneId,
        IManagedResourceStore store,
        DeclassifyPin[] pins)
    {
        MemoryItemValidator.ValidateDraft(sanitized, destinationResourceId, destinationZoneId);
        RequireSanitizedCitationsEmpty(sanitized);

        // The derivative inherits the attribution of its memory-item
        // progenitors so an approval can never relabel AI-written or
        // tool-observed content as user-written or user-confirmed. Pins that
        // are registered sources carry no memory triple; when no pin is a
        // memory item the synthesis triple is required.
        (SpeakerRole Author, CreationMethod CreationMethod, EvidenceLevel Evidence)? required = null;
        foreach (var pin in pins)
        {
            if (TryReadMemoryTriple(store, pin, out var triple))
            {
                if (required is null)
                {
                    required = triple;
                }
                else if (!required.Value.Equals(triple))
                {
                    throw new ArgumentException("The pinned memory items disagree on authorship; a derivative keeps one recorded attribution.");
                }
            }
        }

        var expected = required ?? (SpeakerRole.Assistant, CreationMethod.Synthesis, EvidenceLevel.ReportedSource);
        if (sanitized.Author != expected.Author ||
            sanitized.CreationMethod != expected.CreationMethod ||
            sanitized.Evidence != expected.Evidence)
        {
            throw new ArgumentException("A declassified derivative keeps its recorded author, creation method and evidence level; sanitizing never relabels it.");
        }
    }

    private static void RequireProposalAttribution(Proposal proposal, MemoryItemDraft sanitized)
    {
        if (sanitized.Author != proposal.Item.Author ||
            sanitized.CreationMethod != proposal.Item.CreationMethod ||
            sanitized.Evidence != proposal.Item.Evidence)
        {
            throw new ArgumentException("A declassified proposal keeps its author, creation method and evidence level; sanitizing never relabels an AI-written record as user-written or user-confirmed.");
        }
    }

    private static void RequireSanitizedCitationsEmpty(MemoryItemDraft sanitized)
    {
        if (sanitized.Sources is { Length: > 0 })
        {
            throw new ArgumentException("A sanitized shared copy carries no citations: private lineage travels only through encrypted derived_from relations, never through shared-readable citation anchors.");
        }
    }

    private static (bool Current, string[] Mismatched) CheckPinsCurrent(DeclassifyPin[] pins, IManagedResourceStore store)
    {
        var live = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var resource in store.ListResources())
        {
            live[resource.ResourceId] = resource.Revision;
        }

        var mismatched = new List<string>();
        foreach (var pin in pins)
        {
            if (!live.TryGetValue(pin.ResourceId, out var liveRevision) || liveRevision != pin.Revision)
            {
                mismatched.Add(pin.ResourceId);
            }
        }

        return (mismatched.Count == 0, mismatched.ToArray());
    }

    private static void RequirePinsCurrent(DeclassifyPin[] pins, IManagedResourceStore store)
    {
        var (current, mismatched) = CheckPinsCurrent(pins, store);
        if (!current)
        {
            throw new ArgumentException("A pinned private source moved past the reviewed revision (" +
                string.Join(",", mismatched) + "); re-preview it before publishing.");
        }

        foreach (var pin in pins)
        {
            ValidatePinReadable(store, pin);
        }
    }

    private static void ValidatePinReadable(IManagedResourceStore store, DeclassifyPin pin)
    {
        if (!store.TryGetZone(pin.ResourceId, out var zone) || string.IsNullOrEmpty(zone))
        {
            throw new ArgumentException("A pinned private source is not registered in any zone.");
        }

        byte[]? raw = null;
        try
        {
            raw = store.ReadContent(pin.ResourceId, pin.Revision);
            try
            {
                _ = MemoryEnvelopeCodec.DecodeStored(raw);
                return;
            }
            catch (ArgumentException)
            {
            }

            _ = RegisteredSourceCodec.DecodeStored(raw);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException)
        {
            throw new ArgumentException("A pinned private source is unavailable at its pinned revision.", ex);
        }
        finally
        {
            if (raw is { Length: > 0 })
            {
                CryptographicOperations.ZeroMemory(raw);
            }
        }
    }

    private static bool TryReadMemoryTriple(IManagedResourceStore store, DeclassifyPin pin, out (SpeakerRole, CreationMethod, EvidenceLevel) triple)
    {
        triple = default;
        byte[]? raw = null;
        try
        {
            raw = store.ReadContent(pin.ResourceId, pin.Revision);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException)
        {
            return false;
        }

        try
        {
            var stored = MemoryEnvelopeCodec.DecodeStored(raw);
            if (!string.Equals(stored.Id, pin.ResourceId, StringComparison.Ordinal))
            {
                return false;
            }

            triple = (stored.Author, stored.CreationMethod, stored.Evidence);
            return true;
        }
        catch (ArgumentException)
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

    private static RelationEndpointKind ClassifyPin(IManagedResourceStore store, DeclassifyPin pin)
    {
        byte[]? raw = null;
        try
        {
            raw = store.ReadContent(pin.ResourceId, pin.Revision);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException)
        {
            throw new ArgumentException("A lineage endpoint is unavailable at its pinned revision.", ex);
        }

        try
        {
            try
            {
                _ = MemoryEnvelopeCodec.DecodeStored(raw);
                return RelationEndpointKind.MemoryItem;
            }
            catch (ArgumentException)
            {
            }

            _ = RegisteredSourceCodec.DecodeStored(raw);
            return RelationEndpointKind.RegisteredSource;
        }
        finally
        {
            if (raw is { Length: > 0 })
            {
                CryptographicOperations.ZeroMemory(raw);
            }
        }
    }

    private static List<StoredRelation> LoadLineageRelations(string resourceId, IManagedResourceStore store)
    {
        var matches = new List<StoredRelation>();
        foreach (var resource in store.ListResources())
        {
            if (!DomainIdentifiers.IsRelationId(resource.ResourceId))
            {
                continue;
            }

            byte[]? raw = null;
            try
            {
                raw = store.ReadContent(resource.ResourceId, resource.Revision);
                var stored = RelationCodec.DecodeStored(raw);
                if (stored.Kind == RelationKind.DerivedFrom &&
                    string.Equals(stored.From.ResourceId, resourceId, StringComparison.Ordinal))
                {
                    matches.Add(stored);
                }
            }
            catch (Exception ex) when (ex is ArgumentException or IOException)
            {
                throw new ArgumentException("A recorded derivation lineage is unreadable; it cannot be traced as current.", ex);
            }
            finally
            {
                if (raw is { Length: > 0 })
                {
                    CryptographicOperations.ZeroMemory(raw);
                }
            }
        }

        return matches;
    }

    private static List<(string ResourceId, long Revision)>? LoadLineagePins(string resourceId, IManagedResourceStore store)
    {
        List<(string ResourceId, long Revision)> pins;
        try
        {
            pins = LoadLineageRelations(resourceId, store)
                .Where(relation => relation.State is RelationState.Accepted or RelationState.Proposed)
                .Select(relation => (relation.To.ResourceId, relation.To.Revision))
                .Distinct()
                .ToList();
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }

        return pins;
    }

    private static (MemoryItem Item, long Revision)? ReadLiveItem(IManagedResourceStore store, string resourceId)
    {
        var live = store.ListResources()
            .FirstOrDefault(resource => string.Equals(resource.ResourceId, resourceId, StringComparison.Ordinal));
        if (live is null)
        {
            return null;
        }

        byte[]? raw = null;
        try
        {
            raw = store.ReadContent(resourceId, live.Revision);
            var stored = MemoryEnvelopeCodec.DecodeStored(raw);
            if (!string.Equals(stored.Id, resourceId, StringComparison.Ordinal))
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

    private static bool HoldsDeclassifiedContent(
        IManagedResourceStore store,
        string resourceId,
        long revision,
        string zoneId,
        MemoryItemDraft sanitized)
    {
        byte[]? raw = null;
        try
        {
            raw = store.ReadContent(resourceId, revision);
            var stored = MemoryEnvelopeCodec.DecodeStored(raw);
            return string.Equals(stored.Id, resourceId, StringComparison.Ordinal) &&
                string.Equals(stored.ZoneId, zoneId, StringComparison.Ordinal) &&
                stored.Kind == sanitized.Kind &&
                stored.Author == sanitized.Author &&
                stored.Executor == ExecutorKind.Owner &&
                stored.CreationMethod == sanitized.CreationMethod &&
                stored.Evidence == sanitized.Evidence &&
                stored.Level == sanitized.Level &&
                string.Equals(stored.Title, sanitized.Title, StringComparison.Ordinal) &&
                string.Equals(stored.Body, sanitized.Body, StringComparison.Ordinal) &&
                stored.OccurredAtUtc == sanitized.OccurredAtUtc &&
                stored.Applicability == sanitized.Applicability &&
                (stored.Sources is null || stored.Sources.Length == 0) &&
                (sanitized.Sources is null || sanitized.Sources.Length == 0);
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

    private static void EnsureOwner(AuthenticatedContext actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (actor.Kind != PrincipalKind.Owner)
        {
            throw new UnauthorizedAccessException("Declassification requires the authenticated owner workflow.");
        }
    }
    private static ReadOnlySpan<byte> StripUtf8Preamble(byte[] json) =>
        json.Length >= 3 && json[0] == 0xEF && json[1] == 0xBB && json[2] == 0xBF
            ? new ReadOnlySpan<byte>(json, 3, json.Length - 3)
            : json;

    private static readonly JsonSerializerOptions DraftOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static byte[] EncodeDraft(MemoryItemDraft sanitized)
    {
        // The shared write service owns draft validation; this encodes the
        // sanitized content with the same contract MemoryEnvelopeCodec reads.
        var bytes = JsonSerializer.SerializeToUtf8Bytes(sanitized, DraftOptions);
        if (bytes.Length is <= 0 or > 1024 * 1024)
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw new ArgumentException("The sanitized shared content exceeds its storage limit.");
        }

        return bytes;
    }
}
