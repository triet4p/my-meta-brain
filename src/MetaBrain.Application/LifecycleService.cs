using System.Security.Cryptography;
using MetaBrain.Core.Domain;
using MetaBrain.Core.Security;

namespace MetaBrain.Application;

/// <summary>
/// One retained historical revision of a subject, read back through the same
/// encrypted store: the archived envelope, the archive resource that holds it,
/// and both revisions so a caller can tell the historical subject revision
/// from the archive resource's own vault revision.
/// </summary>
public sealed record RetainedRevisionReadResult(
    string ArchiveResourceId,
    long ArchiveRevision,
    MemoryItem Subject);

/// <summary>
/// S2-T5 domain lifecycle and temporal validity over the shared encrypted
/// canonical store. A lifecycle record is an ordinary revision-gated canonical
/// resource under a reserved <c>lc-</c> prefix whose ID is derived from the
/// subject memory item, so exactly one record covers one subject and no caller
/// can mint a second, disagreeing state. Identity comes from the trusted
/// subject ID, executor from the trusted
/// <see cref="Security.AuthenticatedContext"/>, revision from the vault
/// descriptor, and the initial states from the subject's kind: nothing is
/// ever client-declared. Every act is an explicit operation with an actor,
/// a UTC time and, where the act is negative, an explicit rationale, appended
/// to a bounded in-record history so transitions stay retrievable after
/// further revisions.
/// <para>
/// The service never touches grant state, so creating, transitioning, or
/// retaining history cannot expand an approved snapshot (R11), and a
/// lifecycle read never grants access to the subject item's content.
/// </para>
/// </summary>
public sealed class LifecycleService
{
    private readonly TimeProvider _timeProvider;

    public LifecycleService(TimeProvider? timeProvider = null) =>
        _timeProvider = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// Creates the lifecycle record for a subject item. The subject must be a
    /// memory item that exists now; the record's zone is the subject's zone, so
    /// a lifecycle record can never disagree with what it describes.
    /// </summary>
    public LifecycleRecord Create(
        AuthenticatedContext context,
        string subjectResourceId,
        byte[] draftJson,
        IManagedResourceStore store)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(store);
        EnsureTrusted(context);
        ArgumentNullException.ThrowIfNull(draftJson);

        LifecycleDraft draft;
        try
        {
            draft = LifecycleCodec.DecodeDraft(draftJson);
        }
        catch (ArgumentException)
        {
            throw;
        }

        LifecycleValidator.ValidateDraft(draft, subjectResourceId);
        var subject = RequireSubject(store, subjectResourceId);
        LifecycleValidator.ValidateAgainstSubject(draft, subject.Envelope);

        var now = _timeProvider.GetUtcNow().ToUniversalTime();
        var stored = new StoredLifecycleRecord(
            LifecycleCodec.CurrentSchemaVersion,
            DomainIdentifiers.LifecycleIdFor(subjectResourceId),
            subject.ZoneId,
            subjectResourceId,
            subject.Envelope.Kind,
            PublicationState.Proposal,
            subject.Envelope.Kind == MemoryKind.Question ? QuestionState.Open : null,
            subject.Envelope.Kind == MemoryKind.Intention ? IntentionState.Pending : null,
            false,
            null,
            false,
            null,
            null,
            draft.Occurred,
            draft.Recorded,
            draft.ValidFrom,
            draft.ValidUntil,
            draft.ReviewAfter,
            ExecutorOf(context),
            draft.CreationMethod!.Value,
            draft.Reason,
            now,
            Array.Empty<LifecycleStateChange>(),
            Array.Empty<LifecycleRetainedRevision>());
        var payload = LifecycleCodec.EncodeStored(stored);
        try
        {
            // Revision-gated exactly like canonical writes: zero creates only
            // when the record is absent, so a subject has at most one record
            // and a duplicate create fails closed.
            var revision = store.WriteContent(stored.Id, stored.ZoneId, payload, expectedRevision: 0);
            return ToRecord(stored, revision);
        }
        catch (ManagedResourceUnavailableException ex)
        {
            throw new ArgumentException("The lifecycle record write was rejected by vault storage.", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    /// <summary>
    /// Reads a lifecycle record by its subject. Unknown, unavailable, tampered,
    /// or wrongly-shaped records are one generic outcome: a read is never an
    /// existence oracle on the owner channel's own data.
    /// </summary>
    public LifecycleRecord? Read(
        AuthenticatedContext context,
        string subjectResourceId,
        long? expectedRevision,
        IManagedResourceStore store)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(store);
        EnsureTrusted(context);
        if (string.IsNullOrEmpty(subjectResourceId))
        {
            return null;
        }

        byte[]? raw = null;
        try
        {
            raw = store.ReadContent(DomainIdentifiers.LifecycleIdFor(subjectResourceId), expectedRevision);
            var stored = LifecycleCodec.DecodeStored(raw);
            var revision = expectedRevision ?? ResolveLiveRevision(store, stored.Id);
            return ToRecord(stored, revision);
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (IOException)
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
    /// Performs one explicit act on a lifecycle record: a state transition, a
    /// notification marker, a validity update, or a revision retention. The
    /// record is re-read at the caller's expected revision, the subject is
    /// re-verified, the act is checked against the single policy table, and the
    /// write stays revision-gated so a stale concurrent act publishes nothing.
    /// </summary>
    public LifecycleRecord Transition(
        AuthenticatedContext context,
        string subjectResourceId,
        LifecycleAct act,
        long expectedRevision,
        byte[]? transitionJson,
        IManagedResourceStore store)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(store);
        EnsureTrusted(context);
        if (string.IsNullOrEmpty(subjectResourceId) || act == LifecycleAct.Unknown || expectedRevision < 1)
        {
            throw new ArgumentException("The lifecycle subject, act, and live revision are required.");
        }

        var transition = LifecycleCodec.DecodeTransitionDraft(transitionJson);

        byte[]? raw = null;
        StoredLifecycleRecord stored;
        try
        {
            raw = store.ReadContent(DomainIdentifiers.LifecycleIdFor(subjectResourceId), expectedRevision);
            stored = LifecycleCodec.DecodeStored(raw);
        }
        catch (ArgumentException)
        {
            throw;
        }
        catch (IOException ex)
        {
            throw new ArgumentException("The lifecycle record is unavailable.", ex);
        }
        finally
        {
            if (raw is { Length: > 0 })
            {
                CryptographicOperations.ZeroMemory(raw);
            }
        }

        // The subject still has to be the item the record describes, and in a
        // zone the record already names; a subject whose kind changed makes
        // the record's question/intention applicability stale, which fails
        // closed instead of silently reinterpreting it.
        var subject = RequireSubject(store, subjectResourceId);
        if (subject.Envelope.Kind != stored.SubjectKind)
        {
            throw new ArgumentException("The lifecycle subject kind changed after the record was created.");
        }

        LifecycleValidator.ValidateTransition(transition, act, stored);

        // A supersession names the replacing item, which must exist as a memory
        // item now; the replaced record is never edited or deleted, and a
        // second supersession of the same subject is refused by the policy.
        string? replacement = null;
        if (LifecycleTransitions.RequiresReplacement(act))
        {
            replacement = transition.ReplacementResourceId!;
            if (string.Equals(replacement, subjectResourceId, StringComparison.Ordinal))
            {
                throw new ArgumentException("An intention cannot supersede itself.");
            }

            _ = RequireSubject(store, replacement);
        }

        LifecycleValidator.ValidateAgainstSubject(transition, subject.Envelope);

        var now = _timeProvider.GetUtcNow().ToUniversalTime();
        var executor = ExecutorOf(context);
        LifecycleRetainedRevision[] retained = stored.RetainedRevisions;
        if (LifecycleTransitions.AllowsRetention(act))
        {
            retained = RetainSubjectRevision(stored, subject, executor, now, store);
        }
        var intentionDueAt = act switch
        {
            LifecycleAct.SetIntentionDue => transition.IntentionDueAt,
            LifecycleAct.ClearIntentionDue => null,
            _ => stored.IntentionDueAt
        };
        var timeTriggerChanged = !Equals(intentionDueAt, stored.IntentionDueAt);


        var next = stored with
        {
            Publication = LifecycleTransitions.ChangesState(act) && LifecycleTransitions.AspectOf(act) == LifecycleAspect.Publication
                ? LifecycleTransitions.TargetPublication(act)
                : stored.Publication,
            Question = LifecycleTransitions.ChangesState(act) && LifecycleTransitions.AspectOf(act) == LifecycleAspect.Question
                ? LifecycleTransitions.TargetQuestion(act)
                : stored.Question,
            Intention = LifecycleTransitions.ChangesState(act) && LifecycleTransitions.AspectOf(act) == LifecycleAspect.Intention
                ? LifecycleTransitions.TargetIntention(act)
                : stored.Intention,
            Notified = timeTriggerChanged ? false : act == LifecycleAct.Notify || stored.Notified,
            NotifiedAtUtc = timeTriggerChanged ? null : act == LifecycleAct.Notify ? now : stored.NotifiedAtUtc,
            Acknowledged = timeTriggerChanged ? false : act == LifecycleAct.Acknowledge || stored.Acknowledged,
            AcknowledgedAtUtc = timeTriggerChanged ? null : act == LifecycleAct.Acknowledge ? now : stored.AcknowledgedAtUtc,
            SupersededBy = LifecycleTransitions.RequiresReplacement(act) ? replacement : stored.SupersededBy,
            Occurred = transition.Occurred ?? stored.Occurred,
            Recorded = transition.Recorded ?? stored.Recorded,
            ValidFrom = transition.ValidFrom ?? stored.ValidFrom,
            ValidUntil = transition.ValidUntil ?? stored.ValidUntil,
            ReviewAfter = transition.ReviewAfter ?? stored.ReviewAfter,
            IntentionDueAt = intentionDueAt,
            RetainedRevisions = retained,
            History = AppendHistory(stored, act, executor, now, transition, replacement)
        };
        var payload = LifecycleCodec.EncodeStored(next);
        try
        {
            var revision = store.WriteContent(next.Id, next.ZoneId, payload, expectedRevision);
            return ToRecord(next, revision);
        }
        catch (ManagedResourceUnavailableException ex)
        {
            throw new ArgumentException("The lifecycle transition was rejected by vault storage.", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    /// <summary>
    /// Reads one retained historical revision of a subject back as the exact
    /// stored envelope it was at that revision. The archive resource is created
    /// only by an explicit retention act, is immutable, and is addressed by the
    /// subject plus the exact revision, so a retained read never serves newer
    /// content and never grants access to the live item.
    /// </summary>
    public RetainedRevisionReadResult? ReadRetainedRevision(
        AuthenticatedContext context,
        string subjectResourceId,
        long revision,
        IManagedResourceStore store)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(store);
        EnsureTrusted(context);
        if (string.IsNullOrEmpty(subjectResourceId) || revision < 1 || !DomainIdentifiers.IsDomainIdentifier(subjectResourceId))
        {
            return null;
        }

        var archiveId = DomainIdentifiers.ArchiveIdFor(subjectResourceId, revision);
        byte[]? raw = null;
        try
        {
            raw = store.ReadContent(archiveId, null);
            var archived = MemoryEnvelopeCodec.DecodeStored(raw);
            if (!string.Equals(archived.Id, subjectResourceId, StringComparison.Ordinal))
            {
                return null;
            }

            var archiveRevision = ResolveLiveRevision(store, archiveId);
            return new RetainedRevisionReadResult(archiveId, archiveRevision, ToSubject(archived, revision));
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (IOException)
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
    /// Writes the subject's current revision into its archive resource. The
    /// bytes are the live stored envelope exactly as they are now, so a
    /// retained read returns the content that was live when it was retained.
    /// A retention whose archive already exists (for example after an
    /// interrupted attempt) is recovered, never silently duplicated, and a
    /// mismatch fails closed.
    /// </summary>
    private static LifecycleRetainedRevision[] RetainSubjectRevision(
        StoredLifecycleRecord stored,
        SubjectState subject,
        ExecutorKind executor,
        DateTimeOffset now,
        IManagedResourceStore store)
    {
        foreach (var existing in stored.RetainedRevisions)
        {
            if (existing.SubjectRevision == subject.Revision)
            {
                throw new ArgumentException("This subject revision is already retained.");
            }
        }

        var archiveId = DomainIdentifiers.ArchiveIdFor(subject.ResourceId, subject.Revision);
        var liveArchive = SourceRegistryService.TryGetLiveRevision(store, archiveId);
        byte[]? payload = null;
        try
        {
            if (liveArchive is null)
            {
                payload = ReadSubjectBytes(store, subject.ResourceId, subject.Revision);
                store.WriteContent(archiveId, subject.ZoneId, payload, expectedRevision: 0);
            }
            else
            {
                // The archive exists but the record does not name it: an
                // interrupted retention. Verify it really holds this subject at
                // this revision before recording it.
                payload = ReadSubjectBytes(store, archiveId, liveArchive.Value);
                var recovered = MemoryEnvelopeCodec.DecodeStored(payload);
                if (!string.Equals(recovered.Id, subject.ResourceId, StringComparison.Ordinal) ||
                    !string.Equals(recovered.ZoneId, subject.ZoneId, StringComparison.Ordinal))
                {
                    throw new ArgumentException("The retained revision archive does not hold this subject.");
                }
            }

            var retained = new LifecycleRetainedRevision[stored.RetainedRevisions.Length + 1];
            Array.Copy(stored.RetainedRevisions, retained, stored.RetainedRevisions.Length);
            retained[^1] = new LifecycleRetainedRevision(subject.Revision, archiveId, executor, now);
            return retained;
        }
        catch (ManagedResourceUnavailableException ex)
        {
            throw new ArgumentException("The retained revision archive write was rejected by vault storage.", ex);
        }
        finally
        {
            if (payload is { Length: > 0 })
            {
                CryptographicOperations.ZeroMemory(payload);
            }
        }
    }

    private static byte[] ReadSubjectBytes(IManagedResourceStore store, string resourceId, long revision)
    {
        byte[] raw;
        try
        {
            raw = store.ReadContent(resourceId, revision);
        }
        catch (IOException ex)
        {
            throw new ArgumentException("The subject bytes are unavailable for retention.", ex);
        }

        if (raw.Length == 0)
        {
            throw new ArgumentException("The subject bytes are unavailable for retention.");
        }

        return raw;
    }

    private static LifecycleStateChange[] AppendHistory(
        StoredLifecycleRecord stored,
        LifecycleAct act,
        ExecutorKind executor,
        DateTimeOffset now,
        LifecycleTransitionDraft transition,
        string? replacement)
    {
        if (stored.History.Length >= LifecycleCodec.MaximumHistoryEntries)
        {
            throw new ArgumentException("The lifecycle history is full; the record cannot record further acts.");
        }

        var aspect = LifecycleTransitions.AspectOf(act);
        string fromState = LifecycleCodec.NoneState;
        string toState = LifecycleCodec.NoneState;
        if (LifecycleTransitions.ChangesState(act))
        {
            fromState = aspect switch
            {
                LifecycleAspect.Publication => LifecycleTokens.Publication(stored.Publication),
                LifecycleAspect.Question => LifecycleTokens.Question(stored.Question!.Value),
                LifecycleAspect.Intention => LifecycleTokens.Intention(stored.Intention!.Value),
                _ => LifecycleCodec.NoneState
            };
            toState = aspect switch
            {
                LifecycleAspect.Publication => LifecycleTokens.Publication(LifecycleTransitions.TargetPublication(act)),
                LifecycleAspect.Question => LifecycleTokens.Question(LifecycleTransitions.TargetQuestion(act)),
                LifecycleAspect.Intention => LifecycleTokens.Intention(LifecycleTransitions.TargetIntention(act)),
                _ => LifecycleCodec.NoneState
            };
        }

        var change = new LifecycleStateChange(
            aspect,
            act,
            fromState,
            toState,
            executor,
            now,
            transition.Rationale,
            LifecycleTransitions.RequiresReplacement(act) ? replacement : null);
        var history = new LifecycleStateChange[stored.History.Length + 1];
        Array.Copy(stored.History, history, stored.History.Length);
        history[^1] = change;
        return history;
    }

    private sealed record SubjectState(string ResourceId, string ZoneId, long Revision, StoredMemoryEnvelope Envelope);

    /// <summary>
    /// Resolves the subject as the memory item it must be: present in the
    /// encrypted manifest, decodable as a stored memory envelope at its live
    /// revision, and never a relation, a lifecycle record, a retained
    /// revision, or a registered source.
    /// </summary>
    private static SubjectState RequireSubject(IManagedResourceStore store, string subjectResourceId)
    {
        if (string.IsNullOrEmpty(subjectResourceId) || !DomainIdentifiers.IsDomainIdentifier(subjectResourceId))
        {
            throw new ArgumentException("The lifecycle subject must be an opaque memory item identifier.");
        }

        if (!store.TryGetZone(subjectResourceId, out var zone) || string.IsNullOrEmpty(zone))
        {
            throw new ArgumentException("The lifecycle subject item does not exist.");
        }

        var revision = ResolveLiveRevision(store, subjectResourceId);
        byte[]? raw = null;
        try
        {
            raw = store.ReadContent(subjectResourceId, revision);
            var envelope = MemoryEnvelopeCodec.DecodeStored(raw);
            return new SubjectState(subjectResourceId, zone, revision, envelope);
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException("The lifecycle subject must be a memory item.", ex);
        }
        catch (IOException ex)
        {
            throw new ArgumentException("The lifecycle subject item is unavailable.", ex);
        }
        finally
        {
            if (raw is { Length: > 0 })
            {
                CryptographicOperations.ZeroMemory(raw);
            }
        }
    }

    private static long ResolveLiveRevision(IManagedResourceStore store, string resourceId)
    {
        var live = store.ListResources().FirstOrDefault(resource =>
            string.Equals(resource.ResourceId, resourceId, StringComparison.Ordinal));
        if (live is null)
        {
            throw new ManagedResourceUnavailableException();
        }

        return live.Revision;
    }

    internal static LifecycleRecord ToRecord(StoredLifecycleRecord stored, long revision) => new(
        stored.Id,
        stored.ZoneId,
        revision,
        stored.SchemaVersion,
        stored.SubjectResourceId,
        stored.SubjectKind,
        stored.Publication,
        stored.Question,
        stored.Intention,
        stored.Notified,
        stored.NotifiedAtUtc,
        stored.Acknowledged,
        stored.AcknowledgedAtUtc,
        stored.SupersededBy,
        stored.Occurred,
        stored.Recorded,
        stored.ValidFrom,
        stored.ValidUntil,
        stored.ReviewAfter,
        stored.Executor,
        stored.CreationMethod,
        stored.Reason,
        stored.RecordedAtUtc,
        stored.History,
        stored.RetainedRevisions,
        stored.IntentionDueAt);

    private static MemoryItem ToSubject(StoredMemoryEnvelope stored, long revision) => new(
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

    private static ExecutorKind ExecutorOf(AuthenticatedContext context) =>
        context.Kind == PrincipalKind.Agent ? ExecutorKind.Agent : ExecutorKind.Owner;

    private static void EnsureTrusted(AuthenticatedContext actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
    }
}
