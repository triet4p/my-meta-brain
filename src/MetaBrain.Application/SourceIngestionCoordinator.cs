using System.Security.Cryptography;
using MetaBrain.Core.Domain;
using MetaBrain.Core.Security;

namespace MetaBrain.Application;

public enum SourceIngestionOutcomeKind
{
    Advanced = 1,
    Idle = 2,
    PartialPending = 3,
    UnsupportedFormat = 4,
    UnsupportedRecord = 5,
    NotEnrolled = 6,
    Revoked = 7,
    MixedQuarantined = 8,
    GenerationChanged = 9,
    SourceUnreadable = 10,
    SourceTooLarge = 11,
    JournalLimit = 12,
    JournalConflict = 13,
    SourceMissing = 14
}

/// <summary>
/// One bounded ingestion run's observable result. Every terminal state is
/// explicit: nothing here reports success while data was dropped, skipped or
/// spliced, and the checkpoint carried back is the one persisted for the
/// generation the run acted on.
/// </summary>
public sealed record SourceIngestionOutcome(
    SourceIngestionOutcomeKind Kind,
    long ConsumedOffset,
    int EventsAdded,
    int DiagnosticsAdded,
    string? DiagnosticCode = null);

public enum SourceEnrollmentLife
{
    Enrolled = 1,
    Revoked = 2,
    Quarantined = 3
}

/// <summary>
/// Owner-visible enrollment status: the mapping, enrollment/quarantine life
/// state, persisted checkpoint summary, exact-locator availability observation
/// and the live registered-source metadata revision after any availability sync.
/// </summary>
public sealed record SourceEnrollmentStatus(
    string SourceId,
    string ZoneId,
    string Format,
    int FormatVersion,
    long Generation,
    SourceEnrollmentLife Life,
    long AnchorLength,
    DateTimeOffset EnrolledAtUtc,
    DateTimeOffset? RevokedAtUtc,
    SourceQuarantineRecord? Quarantine,
    long JournalRevision,
    long ConsumedOffset,
    int EventCount,
    int DiagnosticCount,
    DateTimeOffset? LastIngestAtUtc,
    SourceAvailabilityObservation Availability,
    DateTimeOffset? AvailabilityObservedAtUtc,
    DateTimeOffset? RelocatedAtUtc,
    long? SourceReferenceRevision);

/// <summary>
/// S3-T1 source enrollment and incremental ingestion coordinator.
/// <para>
/// The coordinator owns the ingest lifecycle end to end: explicit owner
/// enrollment with a generation anchor, encrypted checkpoint/quarantine
/// state, version dispatch, idempotent bounded incremental processing with
/// generation handling, and canonical journal writes through the one vault
/// store. It never parses a Codex/OMP schema — parsing consumers attach
/// through <see cref="ISourceRecordNormalizer"/> (S3-T2/S3-T3) — and it ships
/// with no normalizer of its own, so an unregistered format is an explicit
/// unsupported diagnostic rather than a silent best effort.
/// </para>
/// <para>
/// Ordering is a security property, not a convenience: enrollment, revocation
/// and quarantine state, then format dispatch, all resolve from encrypted
/// state before the enrolled locator is opened; only then is a bounded
/// snapshot read, the generation verified against the consumed prefix, and
/// complete records framed and normalized. An unknown, revoked, unmapped,
/// quarantined or unsupported source is therefore rejected without opening
/// the transcript, and a replaced or truncated fixture is never spliced into
/// the prior generation.
/// </para>
/// </summary>
public sealed class SourceIngestionCoordinator
{
    private readonly SourceEnrollmentAuthority _enrollments;
    private readonly SourceNormalizerRegistry _normalizers;
    private readonly ISourceSnapshotReader _reader;

    public SourceIngestionCoordinator(
        SourceEnrollmentAuthority enrollments,
        SourceNormalizerRegistry normalizers,
        ISourceSnapshotReader reader)
    {
        _enrollments = enrollments ?? throw new ArgumentNullException(nameof(enrollments));
        _normalizers = normalizers ?? throw new ArgumentNullException(nameof(normalizers));
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
    }

    public EnrolledSource Enroll(
        AuthenticatedContext owner,
        string sourceId,
        string zoneId,
        string locator,
        string format,
        int formatVersion,
        ISourceEnrollmentPersistence persistence)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(persistence);
        // Grammar first: an invalid locator never reaches the reader.
        var normalizedLocator = SourceEnrollmentGrammar.NormalizeLocator(locator);
        // Bounded identity read for the generation anchor. This is not
        // transcript interpretation: no record is framed or normalized here,
        // and the bytes are zeroed as soon as the anchor is computed.
        var snapshot = _reader.TryReadSnapshot(normalizedLocator);
        if (snapshot.Status != SourceSnapshotStatus.Snapshot || snapshot.Content is not { Length: > 0 })
        {
            throw new SourceOperationException(
                snapshot.Status switch
                {
                    SourceSnapshotStatus.TooLarge => "source_too_large",
                    SourceSnapshotStatus.Missing => "source_missing",
                    _ => "source_locator_unreadable"
                });

        }

        try
        {
            var digest = SourceFingerprint.Compute(snapshot.Content);
            return _enrollments.Enroll(owner, sourceId, zoneId, normalizedLocator, format, formatVersion,
                snapshot.Content.Length, digest, persistence);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(snapshot.Content);
        }
    }
    public EnrolledSource Relocate(
        AuthenticatedContext owner,
        string sourceId,
        string locator,
        long expectedGeneration,
        IVaultOperation operation)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(operation);
        var state = SourceEnrollmentAuthority.ValidateAndNormalizeState(operation.LoadSourceEnrollmentState());
        var enrollment = SourceEnrollmentAuthority.FindEnrollment(state, sourceId)
            ?? throw new SourceOperationException("source_not_enrolled");
        if (enrollment.RevokedAtUtc is not null)
        {
            throw new SourceOperationException("source_revoked");
        }

        if (SourceEnrollmentAuthority.FindQuarantine(state, sourceId) is not null)
        {
            throw new SourceOperationException("source_quarantined");
        }

        if (enrollment.Generation != expectedGeneration)
        {
            throw new SourceOperationException("source_relocation_conflict");
        }

        var normalizedLocator = SourceEnrollmentGrammar.NormalizeLocator(locator);
        var candidate = _reader.TryReadSnapshot(normalizedLocator);
        if (candidate.Status != SourceSnapshotStatus.Snapshot || candidate.Content is not { Length: > 0 } content)
        {
            throw new SourceOperationException(candidate.Status switch
            {
                SourceSnapshotStatus.Missing => "source_relocation_missing",
                SourceSnapshotStatus.TooLarge => "source_too_large",
                _ => "source_locator_unreadable"
            });
        }

        try
        {
            if (content.Length != enrollment.AnchorLength ||
                !SourceFingerprint.Matches(enrollment.AnchorDigest, content))
            {
                throw new SourceOperationException("source_relocation_changed");
            }

            return _enrollments.Relocate(owner, sourceId, enrollment.Locator, normalizedLocator,
                expectedGeneration, operation);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(content);
        }
    }


    public EnrolledSource Revoke(
        AuthenticatedContext owner,
        string sourceId,
        ISourceEnrollmentPersistence persistence) =>
        _enrollments.Revoke(owner, sourceId, persistence);

    /// <summary>
    /// Probes only the exact enrolled locator for an active mapping. A changed
    /// observation updates a matching registered reference's availability
    /// under its live revision; no source search or background refresh is performed.
    /// </summary>
    public SourceEnrollmentStatus Status(
        AuthenticatedContext owner,
        string sourceId,
        IVaultOperation operation)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(operation);
        var enrollment = LoadActiveEnrollment(owner, sourceId, operation);
        var quarantine = SourceEnrollmentAuthority.FindQuarantine(
            SourceEnrollmentAuthority.ValidateAndNormalizeState(operation.LoadSourceEnrollmentState()), sourceId);
        var (revision, journal) = ReadJournal(operation, enrollment);
        var life = enrollment.RevokedAtUtc is not null
            ? SourceEnrollmentLife.Revoked
            : quarantine is not null
                ? SourceEnrollmentLife.Quarantined
                : SourceEnrollmentLife.Enrolled;
        var observedAt = life == SourceEnrollmentLife.Enrolled ? DateTimeOffset.UtcNow : (DateTimeOffset?)null;
        var availability = observedAt is null
            ? SourceAvailabilityObservation.NotChecked
            : ObserveAvailability(enrollment);
        var sourceReferenceRevision = observedAt is null
            ? (long?)null
            : UpdateRegisteredAvailability(owner, enrollment, availability, operation);
        return new SourceEnrollmentStatus(
            enrollment.SourceId,
            enrollment.ZoneId,
            enrollment.Format,
            enrollment.FormatVersion,
            enrollment.Generation,
            life,
            enrollment.AnchorLength,
            enrollment.EnrolledAtUtc,
            enrollment.RevokedAtUtc,
            quarantine,
            revision,
            journal?.ConsumedOffset ?? 0,
            journal?.Events.Length ?? 0,
            journal?.Diagnostics.Length ?? 0,
            journal?.LastIngestAtUtc,
            availability,
            observedAt,
            enrollment.RelocatedAtUtc,
            sourceReferenceRevision);
    }

    private SourceAvailabilityObservation ObserveAvailability(EnrolledSource enrollment)
    {
        var snapshot = _reader.TryReadSnapshot(enrollment.Locator);
        if (snapshot.Status != SourceSnapshotStatus.Snapshot || snapshot.Content is not { Length: > 0 } content)
        {
            return snapshot.Status switch
            {
                SourceSnapshotStatus.Missing => SourceAvailabilityObservation.Missing,
                SourceSnapshotStatus.TooLarge => SourceAvailabilityObservation.TooLarge,
                _ => SourceAvailabilityObservation.Unreadable
            };
        }

        try
        {
            return content.Length == enrollment.AnchorLength &&
                SourceFingerprint.Matches(enrollment.AnchorDigest, content)
                ? SourceAvailabilityObservation.Available
                : SourceAvailabilityObservation.Changed;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(content);
        }
    }
    private static long? UpdateRegisteredAvailability(
        AuthenticatedContext owner,
        EnrolledSource enrollment,
        SourceAvailabilityObservation observation,
        IVaultOperation operation)
    {
        var liveRevision = SourceRegistryService.TryGetLiveRevision(operation, enrollment.SourceId);
        if (liveRevision is null)
        {
            return null;
        }

        RegisteredSource source;
        try
        {
            source = new SourceRegistryService().Read(owner, enrollment.SourceId, liveRevision, operation);
        }
        catch (ArgumentException)
        {
            // The enrollment can exist without a registered source reference.
            return null;
        }

        if (source.Text is not null || source.Locator is null ||
            !string.Equals(source.ZoneId, enrollment.ZoneId, StringComparison.Ordinal))
        {
            return source.Revision;
        }

        string sourceLocator;
        try
        {
            sourceLocator = SourceEnrollmentGrammar.NormalizeLocator(source.Locator);
        }
        catch (ArgumentException)
        {
            return source.Revision;
        }

        var locatorChanged = !string.Equals(sourceLocator, enrollment.Locator, StringComparison.OrdinalIgnoreCase);
        if (locatorChanged && enrollment.RelocatedAtUtc is null)
        {
            return source.Revision;
        }

        var availability = observation switch
        {
            SourceAvailabilityObservation.Available => SourceAvailability.Available,
            SourceAvailabilityObservation.Changed or SourceAvailabilityObservation.Missing => SourceAvailability.Unavailable,
            SourceAvailabilityObservation.Unreadable or SourceAvailabilityObservation.TooLarge => SourceAvailability.Unknown,
            _ => source.Availability
        };
        if (source.Availability == availability && !locatorChanged)
        {
            return source.Revision;
        }

        var updatedAt = DateTimeOffset.UtcNow;
        var registeredLocator = locatorChanged ? enrollment.Locator : source.Locator;
        var draft = RegisteredSourceCodec.EncodeDraft(new RegisteredSourceDraft(
            source.Title,
            registeredLocator,
            source.Version,
            availability,
            source.CreationMethod,
            source.CapturedAtUtc,
            updatedAt,
            source.Author,
            source.Text));
        try
        {
            return new SourceRegistryService().Revise(
                owner, source.Id, source.ZoneId, source.Revision, draft, operation).Revision;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(draft);
        }
    }


    public SourceIngestionOutcome Ingest(
        AuthenticatedContext owner,
        string sourceId,
        IVaultOperation operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(operation);

        // State gates resolve from encrypted state only; the locator is not
        // touched for an unknown, revoked or quarantined source.
        var state = SourceEnrollmentAuthority.ValidateAndNormalizeState(operation.LoadSourceEnrollmentState());
        var enrollment = SourceEnrollmentAuthority.FindEnrollment(state, sourceId);
        if (enrollment is null)
        {
            return new SourceIngestionOutcome(SourceIngestionOutcomeKind.NotEnrolled, 0, 0, 0);
        }

        if (enrollment.RevokedAtUtc is not null)
        {
            return new SourceIngestionOutcome(SourceIngestionOutcomeKind.Revoked, 0, 0, 0);
        }

        if (SourceEnrollmentAuthority.FindQuarantine(state, sourceId) is not null)
        {
            return new SourceIngestionOutcome(SourceIngestionOutcomeKind.MixedQuarantined, 0, 0, 0);
        }

        // Version dispatch happens before the transcript is opened: an
        // unsupported format or version is an explicit diagnostic, never a
        // best-effort read of bytes nobody can interpret.
        if (!_normalizers.TryResolve(enrollment.Format, enrollment.FormatVersion, out var normalizer) ||
            normalizer is null)
        {
            return new SourceIngestionOutcome(SourceIngestionOutcomeKind.UnsupportedFormat, 0, 0, 0);
        }

        var journalId = SourceIngestionIds.JournalIdFor(sourceId, enrollment.Generation);
        var (liveRevision, journal) = ReadJournal(operation, enrollment);
        var offset = journal?.ConsumedOffset ?? 0;
        if ((journal?.Events.Length ?? 0) >= SourceIngestionLimits.MaximumJournalEvents)
        {
            return new SourceIngestionOutcome(SourceIngestionOutcomeKind.JournalLimit, offset, 0, 0);
        }

        var events = journal is null ? new List<NormalizedSourceEvent>() : journal.Events.ToList();
        var diagnostics = journal is null
            ? new List<SourceIngestionDiagnostic>()
            : journal.Diagnostics.ToList();

        var snapshot = _reader.TryReadSnapshot(enrollment.Locator);
        if (snapshot.Status != SourceSnapshotStatus.Snapshot || snapshot.Content is not { Length: > 0 } content)
        {
            return new SourceIngestionOutcome(
                snapshot.Status switch
                {
                    SourceSnapshotStatus.Missing => SourceIngestionOutcomeKind.SourceMissing,
                    SourceSnapshotStatus.TooLarge => SourceIngestionOutcomeKind.SourceTooLarge,
                    _ => SourceIngestionOutcomeKind.SourceUnreadable
                },
                offset, 0, 0);
        }

        try
        {
            // Generation gate: the consumed prefix must still be exactly the
            // bytes the checkpoint was taken over, and the live source must
            // not be shorter than it. Truncation and replacement are refused
            // instead of spliced into the prior generation.
            var anchorLength = journal?.AnchorLength ?? enrollment.AnchorLength;
            var anchorDigest = journal?.AnchorDigest ?? enrollment.AnchorDigest;
            if (content.Length < anchorLength)
            {
                RecordQuarantine(owner, enrollment, SourceQuarantineReason.GenerationTruncated, "generation_truncated", operation);
                return new SourceIngestionOutcome(SourceIngestionOutcomeKind.GenerationChanged, offset, 0, 0,
                    "generation_truncated");
            }

            if (!SourceFingerprint.Matches(anchorDigest, content.AsSpan(0, (int)anchorLength)))
            {
                RecordQuarantine(owner, enrollment, SourceQuarantineReason.GenerationReplaced, "generation_replaced", operation);
                return new SourceIngestionOutcome(SourceIngestionOutcomeKind.GenerationChanged, offset, 0, 0,
                    "generation_replaced");
            }

            // Bounded incremental framing and normalization of the unconsumed
            // tail. Only complete records are consumed; a trailing partial
            // record stays unconsumed so it can never be committed as done,
            // and an unsupported record stops the run instead of being
            // skipped over.
            var buffer = content.AsSpan((int)offset);
            var position = 0;
            var eventsAdded = 0;
            string? stopDiagnostic = null;
            while (position < buffer.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var decode = normalizer.Decode(
                    buffer.Slice(position),
                    new SourceRecordContext(sourceId, enrollment.Generation, offset + position));
                switch (decode.Kind)
                {
                    case SourceRecordDecodeKind.NeedMoreBytes:
                        // The trailing bytes are an incomplete record: they
                        // stay unconsumed so the checkpoint cannot advance
                        // past them.
                        goto FramingDone;
                    case SourceRecordDecodeKind.Unsupported:
                        stopDiagnostic = decode.Diagnostic is { Length: > 0 } diagnostic && IsDiagnostic(diagnostic)
                            ? diagnostic
                            : "unsupported_record";
                        goto FramingDone;
                    case SourceRecordDecodeKind.Consumed:
                        if (decode.ConsumedBytes <= 0 || decode.Event is not { } draft)
                        {
                            stopDiagnostic = "normalizer_no_progress";
                            goto FramingDone;
                        }

                        var span = new SourceSpan(offset + position, offset + position + decode.ConsumedBytes);
                        var normalized = new NormalizedSourceEvent(
                            draft.EventId, draft.Author, draft.OccurredAtUtc, draft.PayloadKind, draft.PayloadDigest, span);
                        bool valid;
                        try
                        {
                            SourceIngestionJournalValidator.ValidateEvent(normalized, content.Length);
                            valid = true;
                        }
                        catch (ArgumentException)
                        {
                            valid = false;
                        }

                        if (!valid)
                        {
                            stopDiagnostic = "normalizer_invalid_event";
                            goto FramingDone;
                        }

                        events.Add(normalized);
                        eventsAdded++;
                        position += decode.ConsumedBytes;
                        break;
                    default:
                        stopDiagnostic = "normalizer_unknown_decode";
                        goto FramingDone;
                }
            }

            FramingDone:
            var consumed = offset + position;

            if (eventsAdded == 0 && stopDiagnostic is null)
            {
                // Nothing new completed: a fully consumed generation reports
                // idle, and an incomplete tail reports partial pending. Neither
                // rewrites the checkpoint, so re-import is a no-op.
                return new SourceIngestionOutcome(
                    position < buffer.Length ? SourceIngestionOutcomeKind.PartialPending : SourceIngestionOutcomeKind.Idle,
                    consumed, 0, 0);
            }

            if (events.Count > SourceIngestionLimits.MaximumJournalEvents)
            {
                return new SourceIngestionOutcome(SourceIngestionOutcomeKind.JournalLimit, offset, 0, 0);
            }

            if (stopDiagnostic is not null)
            {
                var stopOffset = consumed;
                diagnostics.RemoveAll(existing => existing.Offset == stopOffset);
                diagnostics.Add(new SourceIngestionDiagnostic(stopOffset, stopDiagnostic, DateTimeOffset.UtcNow));
                if (diagnostics.Count > SourceIngestionLimits.MaximumJournalDiagnostics)
                {
                    return new SourceIngestionOutcome(SourceIngestionOutcomeKind.JournalLimit, offset, 0, 0);
                }
            }

            // Canonical accepted output and checkpoint are one encrypted
            // resource write: the journal commits atomically with the
            // advanced offset, so a crash or cancelled retry can neither lose
            // committed events nor duplicate them (the source prefix and the
            // checkpoint move together, and a retry re-derives only the
            // unconsumed tail).
            var nextJournal = new SourceIngestionJournal(
                SourceIngestionJournal.CurrentSchemaVersion,
                sourceId,
                enrollment.ZoneId,
                enrollment.Generation,
                content.Length,
                SourceFingerprint.Compute(content),
                consumed,
                events.ToArray(),
                diagnostics.ToArray(),
                DateTimeOffset.UtcNow);
            byte[]? encoded = null;
            try
            {
                encoded = SourceIngestionJournalCodec.Encode(nextJournal);
                try
                {
                    operation.WriteContent(journalId, enrollment.ZoneId, encoded, liveRevision);
                }
                catch (ManagedResourceUnavailableException)
                {
                    // A concurrent writer published this generation's journal
                    // first: the run commits nothing and reports the conflict
                    // instead of overwriting the winner.
                    return new SourceIngestionOutcome(SourceIngestionOutcomeKind.JournalConflict, offset, 0, 0);
                }
            }
            catch (ArgumentException)
            {
                // The encoded journal exceeds the storage bound; the
                // checkpoint stays where it was so no event is lost.
                return new SourceIngestionOutcome(SourceIngestionOutcomeKind.JournalLimit, offset, 0, 0);
            }
            finally
            {
                if (encoded is { Length: > 0 })
                {
                    CryptographicOperations.ZeroMemory(encoded);
                }
            }

            return new SourceIngestionOutcome(
                stopDiagnostic is not null
                    ? SourceIngestionOutcomeKind.UnsupportedRecord
                    : position < buffer.Length
                        ? SourceIngestionOutcomeKind.PartialPending
                        : SourceIngestionOutcomeKind.Advanced,
                consumed,
                eventsAdded,
                stopDiagnostic is not null ? 1 : 0,
                stopDiagnostic);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(content);
        }
    }

    private static bool IsDiagnostic(string value) =>
        SourceEnrollmentGrammar.IsDiagnosticCode(value);

    private void RecordQuarantine(
        AuthenticatedContext owner,
        EnrolledSource enrollment,
        SourceQuarantineReason reason,
        string detail,
        IVaultOperation operation) =>
        _enrollments.Quarantine(owner, enrollment.SourceId, reason, detail, enrollment.Locator, operation);

    private static EnrolledSource LoadActiveEnrollment(
        AuthenticatedContext owner,
        string sourceId,
        IVaultOperation operation)
    {
        var state = SourceEnrollmentAuthority.ValidateAndNormalizeState(operation.LoadSourceEnrollmentState());
        return SourceEnrollmentAuthority.FindEnrollment(state, sourceId)
               ?? throw new SourceOperationException("source_not_enrolled");
    }

    /// <summary>
    /// Reads this generation's journal through the authorized operation. An
    /// unreadable, malformed or identity-mismatched journal fails closed: a
    /// checkpoint that cannot be trusted must never be replaced by a restart
    /// from zero, because that would duplicate already accepted events.
    /// </summary>
    private static (long Revision, SourceIngestionJournal? Journal) ReadJournal(
        IVaultOperation operation,
        EnrolledSource enrollment)
    {
        var journalId = SourceIngestionIds.JournalIdFor(enrollment.SourceId, enrollment.Generation);
        var revision = 0L;
        foreach (var resource in operation.ListResources())
        {
            if (string.Equals(resource.ResourceId, journalId, StringComparison.Ordinal))
            {
                revision = resource.Revision;
                break;
            }
        }

        if (revision <= 0)
        {
            return (0, null);
        }

        byte[]? raw = null;
        try
        {
            raw = operation.ReadContent(journalId, revision);
            var journal = SourceIngestionJournalCodec.Decode(raw);
            if (!string.Equals(journal.SourceId, enrollment.SourceId, StringComparison.Ordinal) ||
                !string.Equals(journal.ZoneId, enrollment.ZoneId, StringComparison.Ordinal) ||
                journal.Generation != enrollment.Generation)
            {
                throw new SourceOperationException("journal_identity_mismatch");
            }

            return (revision, journal);
        }
        finally
        {
            if (raw is { Length: > 0 })
            {
                CryptographicOperations.ZeroMemory(raw);
            }
        }
    }
}
