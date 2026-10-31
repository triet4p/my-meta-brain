using System.Buffers;
using System.Security.Cryptography;
using System.Text;
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
    SourceMissing = 14,
    Paused = 15,
    Empty = 16
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

/// <summary>
/// One entry of an S3-T6 safe incremental refresh pass over the owner
/// allowlist. Opted-in active sources carry their ingest outcome; every
/// skipped source names its exact skip reason with zero checkpoint movement
/// and no source read. No transcript content, locator or secret crosses this
/// record (R36).
/// </summary>
public sealed record SourceRefreshResult(
    string SourceId,
    string Status,
    long ConsumedOffset,
    int EventsAdded,
    int DiagnosticsAdded,
    string? DiagnosticCode = null);

public enum SourceEnrollmentLife
{
    Enrolled = 1,
    Revoked = 2,
    Quarantined = 3,
    Paused = 4
}

/// <summary>
/// Owner-visible enrollment status: the mapping, enrollment/quarantine/pause
/// life state, persisted checkpoint summary, exact-locator availability
/// observation and the live registered-source metadata revision after any
/// availability sync. S3-T6 adds the owner pause state and the safe
/// background-refresh opt-in; both are owner-encrypted metadata only.
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
    long? SourceReferenceRevision,
    DateTimeOffset? PausedAtUtc = null,
    bool RefreshOptIn = false,
    long JournalCiphertextBytes = 0,
    long SnapshotCiphertextBytes = 0,
    long SnapshotRevision = 0,
    bool RawPreservationOptIn = false,
    DateTimeOffset? RawPreservationRetainUntilUtc = null,
    SourceIngestionSummary? Ingestion = null,
    bool RefreshOptOut = false,
    string? FolderId = null,
    bool? FolderRefreshOptIn = null);
public sealed record SourceFolderPreviewResult(
    string PreviewId,
    SourceFolderEnrollmentDraft Selection,
    SourceFolderEnumeration Enumeration,
    DateTimeOffset ExpiresAtUtc);

internal sealed record PendingSourceFolderPreview(
    SourceFolderEnrollmentDraft Selection,
    SourceFolderEnumeration Enumeration,
    DateTimeOffset ExpiresAtUtc);



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
    private static readonly TimeSpan FolderPreviewLifetime = TimeSpan.FromMinutes(5);
    private const int MaximumPendingFolderPreviews = 32;
    private readonly SourceEnrollmentAuthority _enrollments;
    private readonly SourceNormalizerRegistry _normalizers;
    private readonly ISourceSnapshotReader _reader;
    private readonly ISourceFolderEnumerator? _folderEnumerator;
    private readonly object _refreshStopGate = new();
    private readonly Dictionary<string, CancellationTokenSource> _activeFolderRefreshes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CancellationTokenSource> _activeSourceRefreshes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _stoppedFolderRefreshes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _stoppedSourceRefreshes = new(StringComparer.Ordinal);
    private readonly object _folderPreviewGate = new();
    private readonly Dictionary<string, PendingSourceFolderPreview> _folderPreviews = new(StringComparer.Ordinal);

    public SourceIngestionCoordinator(
        SourceEnrollmentAuthority enrollments,
        SourceNormalizerRegistry normalizers,
        ISourceSnapshotReader reader,
        ISourceFolderEnumerator? folderEnumerator = null)
    {
        _enrollments = enrollments ?? throw new ArgumentNullException(nameof(enrollments));
        _normalizers = normalizers ?? throw new ArgumentNullException(nameof(normalizers));
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _folderEnumerator = folderEnumerator;
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
        if (snapshot.Status != SourceSnapshotStatus.Snapshot || snapshot.Content is not { } content)
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
            var enrolled = _enrollments.Enroll(owner, sourceId, zoneId, normalizedLocator, format, formatVersion,
                snapshot.Content.Length, digest, persistence);
            ClearSourceRefreshStop(sourceId);
            return enrolled;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(snapshot.Content);
        }
    }

    /// <summary>
    /// Enumerates a single owner-selected folder without reading file contents.
    /// The short-lived preview is process memory only; nothing is enrolled
    /// until the caller submits the one explicit confirmation.
    /// </summary>
    public SourceFolderPreviewResult PreviewFolder(
        AuthenticatedContext owner,
        SourceFolderEnrollmentDraft selection,
        ISourceEnrollmentPersistence persistence,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(persistence);
        if (owner.Kind != PrincipalKind.Owner)
        {
            throw new UnauthorizedAccessException("Only the owner may preview source-folder enrollment.");
        }

        var enumerator = _folderEnumerator ?? throw new SourceOperationException("source_folder_unavailable");
        var normalizedSelection = SourceFolderEnrollmentGrammar.NormalizeDraft(selection);
        if (normalizedSelection.RawPreservationRetainUntilUtc is { } retention && retention <= DateTimeOffset.UtcNow)
        {
            throw new ArgumentException("Folder raw preservation requires a future UTC retention time.");
        }
        // Fail closed on corrupt enrollment metadata before showing a preview.
        _ = _enrollments.List(owner, persistence);
        var enumeration = enumerator.Enumerate(
            normalizedSelection.RootLocator,
            normalizedSelection.Recursive,
            normalizedSelection.IncludePatterns,
            normalizedSelection.ExcludePatterns,
            cancellationToken);
        var previewId = Guid.NewGuid().ToString("N");
        var expiresAtUtc = DateTimeOffset.UtcNow.Add(FolderPreviewLifetime);
        lock (_folderPreviewGate)
        {
            RemoveExpiredFolderPreviews(DateTimeOffset.UtcNow);
            if (_folderPreviews.Count >= MaximumPendingFolderPreviews)
            {
                throw new SourceOperationException("source_folder_preview_limit");
            }

            _folderPreviews.Add(previewId, new PendingSourceFolderPreview(
                normalizedSelection, enumeration, expiresAtUtc));
        }

        return new SourceFolderPreviewResult(previewId, normalizedSelection, enumeration, expiresAtUtc);
    }

    /// <summary>
    /// Re-enumerates and verifies the preview, anchors every selected file
    /// through the existing bounded source reader, and creates canonical
    /// registry metadata. A write-ahead encrypted ID journal makes every new
    /// registration recoverable across process interruption; live compensation
    /// is still attempted when registration or membership persistence fails.
    /// </summary>
    public SourceFolderEnrollmentResult EnrollFolder(
        AuthenticatedContext owner,
        string previewId,
        string confirmation,
        IVaultOperation operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(operation);
        if (owner.Kind != PrincipalKind.Owner)
        {
            throw new UnauthorizedAccessException("Only the owner may enroll a source folder.");
        }

        if (!string.Equals(confirmation, "ENROLL", StringComparison.Ordinal) ||
            !Guid.TryParseExact(previewId, "N", out _))
        {
            throw new SourceOperationException("source_folder_confirmation_invalid");
        }

        PendingSourceFolderPreview pending;
        lock (_folderPreviewGate)
        {
            RemoveExpiredFolderPreviews(DateTimeOffset.UtcNow);
            if (!_folderPreviews.Remove(previewId, out pending!))
            {
                throw new SourceOperationException("source_folder_preview_expired");
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (pending.Selection.RawPreservationRetainUntilUtc is { } retention && retention <= DateTimeOffset.UtcNow)
        {
            throw new ArgumentException("Folder raw preservation requires a future UTC retention time.");
        }
        var enumerator = _folderEnumerator ?? throw new SourceOperationException("source_folder_unavailable");
        var currentEnumeration = enumerator.Enumerate(
            pending.Selection.RootLocator,
            pending.Selection.Recursive,
            pending.Selection.IncludePatterns,
            pending.Selection.ExcludePatterns,
            cancellationToken);
        if (!SameEnumeration(pending.Enumeration, currentEnumeration))
        {
            throw new SourceOperationException("source_folder_preview_stale");
        }

        var included = currentEnumeration.Entries.Where(entry => entry.Included).ToArray();
        if (included.Length == 0)
        {
            throw new SourceOperationException("source_folder_empty");
        }

        if (included.Length > SourceIngestionLimits.MaximumEnrollments)
        {
            throw new SourceOperationException("source_folder_limit");
        }

        var sourceIds = included.Select(entry =>
            SourceFolderEnrollmentGrammar.MemberSourceIdFor(pending.Selection.RootLocator, entry.RelativePath)).ToArray();
        var orderedSourceIds = sourceIds.OrderBy(sourceId => sourceId, StringComparer.Ordinal).ToArray();
        var validatedState = SourceEnrollmentAuthority.ValidateAndNormalizeState(operation.LoadSourceEnrollmentState());
        var folderId = SourceFolderEnrollmentGrammar.FolderIdFor(pending.Selection.RootLocator);
        var existingFolder = SourceFolderEnrollmentAuthority.FindFolder(validatedState, folderId);
        if (existingFolder is not null)
        {
            if (!SameSelection(existingFolder, pending.Selection))
            {
                throw new SourceOperationException("source_folder_conflict");
            }

            if (!existingFolder.MemberSourceIds.SequenceEqual(orderedSourceIds, StringComparer.Ordinal))
            {
                throw new SourceOperationException("source_folder_membership_changed");
            }

            var existingMembers = orderedSourceIds.Select(sourceId =>
                    SourceEnrollmentAuthority.FindEnrollment(validatedState, sourceId) ??
                    throw new InvalidDataException("The folder membership references a missing enrollment."))
                .ToArray();
            return new SourceFolderEnrollmentResult(existingFolder, existingMembers, AlreadyEnrolled: true);
        }

        var purgeState = OwnerPurgeLedgerAuthority.ValidateAndNormalizeState(operation.LoadPurgeLedgerState());
        var purgedResourceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var intent in purgeState.Intents)
        {
            purgedResourceIds.Add(intent.TargetResourceId);
            foreach (var impacted in intent.Impacted)
            {
                purgedResourceIds.Add(impacted.ResourceId);
            }
        }

        if (sourceIds.Any(purgedResourceIds.Contains))
        {
            throw new SourceOperationException("resource_purged");
        }

        var additionalEnrollments = sourceIds.Count(sourceId =>
            SourceEnrollmentAuthority.FindEnrollment(validatedState, sourceId) is null);
        if (validatedState.Enrollments.Length + additionalEnrollments > SourceIngestionLimits.MaximumEnrollments)
        {
            throw new SourceOperationException("source_folder_limit");
        }

        var anchors = new SourceFolderMemberAnchor[included.Length];
        for (var index = 0; index < included.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = included[index];
            var snapshot = _reader.TryReadSnapshot(entry.Locator, cancellationToken);
            if (snapshot.Status != SourceSnapshotStatus.Snapshot || snapshot.Content is not { Length: > 0 } content)
            {
                throw new SourceOperationException(
                    snapshot.Status switch
                    {
                        SourceSnapshotStatus.TooLarge => "source_too_large",
                        SourceSnapshotStatus.Missing => "source_missing",
                        SourceSnapshotStatus.Snapshot => "source_empty",
                        _ => "source_locator_unreadable"
                    });
            }

            try
            {
                anchors[index] = new SourceFolderMemberAnchor(
                    sourceIds[index],
                    entry.RelativePath,
                    entry.Locator,
                    content.Length,
                    SourceFingerprint.Compute(content));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(content);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return SourceFolderEnrollmentAuthority.Enroll(owner, pending.Selection, anchors, operation,
            () => RegisterFolderSources(owner, pending.Selection, anchors, operation, cancellationToken));
    }

    private static Action RegisterFolderSources(
        AuthenticatedContext owner,
        SourceFolderEnrollmentDraft selection,
        IReadOnlyList<SourceFolderMemberAnchor> members,
        IVaultOperation operation,
        CancellationToken cancellationToken)
    {
        if (members.Any(member => member.Locator.Length > RegisteredSourceCodec.MaximumLocatorLength))
        {
            throw new SourceOperationException("source_folder_registry_locator_too_long");
        }

        var registry = new SourceRegistryService();
        var missing = new List<SourceFolderMemberAnchor>();
        foreach (var member in members)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var revision = SourceRegistryService.TryGetLiveRevision(operation, member.SourceId);
            if (revision is null)
            {
                missing.Add(member);
                continue;
            }

            RegisteredSource source;
            try
            {
                source = registry.Read(owner, member.SourceId, revision, operation);
            }
            catch (ArgumentException ex)
            {
                throw new SourceOperationException("source_folder_registry_conflict", ex);
            }

            string registeredLocator;
            try
            {
                registeredLocator = SourceEnrollmentGrammar.NormalizeLocator(source.Locator ?? string.Empty);
            }
            catch (ArgumentException ex)
            {
                throw new SourceOperationException("source_folder_registry_conflict", ex);
            }

            if (!string.Equals(source.Id, member.SourceId, StringComparison.Ordinal) ||
                source.Text is not null ||
                !string.Equals(source.ZoneId, selection.ZoneId, StringComparison.Ordinal) ||
                !string.Equals(registeredLocator, member.Locator, StringComparison.OrdinalIgnoreCase))
            {
                throw new SourceOperationException("source_folder_registry_conflict");
            }
        }

        if (missing.Count == 0)
        {
            return static () => { };
        }

        var pendingIds = missing.Select(member => member.SourceId).ToArray();
        operation.BeginFolderRegistrationRecovery(pendingIds);
        try
        {
            foreach (var member in missing)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var draft = RegisteredSourceCodec.EncodeDraft(new RegisteredSourceDraft(
                    Title: null,
                    Locator: member.Locator,
                    Version: null,
                    Availability: SourceAvailability.Available,
                    CreationMethod: CreationMethod.DirectWrite,
                    CapturedAtUtc: null,
                    RecordedAtUtc: DateTimeOffset.UtcNow));
                try
                {
                    _ = registry.Create(owner, member.SourceId, selection.ZoneId, draft, operation);
                    cancellationToken.ThrowIfCancellationRequested();
                }
                catch (ArgumentException ex)
                {
                    throw new SourceOperationException("source_folder_registry_conflict", ex);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(draft);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
        }
        catch
        {
            var rollbackFailure = RollbackFolderSourceRegistrations(operation, pendingIds);
            if (rollbackFailure is not null)
            {
                throw new SourceOperationException("source_folder_recovery_pending", rollbackFailure);
            }

            throw;
        }

        return () =>
        {
            var rollbackFailure = RollbackFolderSourceRegistrations(operation, pendingIds);
            if (rollbackFailure is not null)
            {
                throw new SourceOperationException("source_folder_recovery_pending", rollbackFailure);
            }
        };
    }

    private static Exception? RollbackFolderSourceRegistrations(
        IVaultOperation operation,
        IReadOnlyList<string> pendingIds)
    {
        Exception? firstFailure = null;
        for (var index = pendingIds.Count - 1; index >= 0; index--)
        {
            try
            {
                _ = operation.DeleteCanonicalResource(pendingIds[index]);
            }
            catch (Exception ex)
            {
                firstFailure ??= ex;
            }
        }

        if (firstFailure is null)
        {
            try
            {
                operation.CompleteFolderRegistrationRecovery();
            }
            catch (Exception ex)
            {
                firstFailure = ex;
            }
        }

        if (firstFailure is not null)
        {
            operation.RequireFolderRegistrationRecovery();
        }

        return firstFailure;
    }

    private void RemoveExpiredFolderPreviews(DateTimeOffset now)
    {
        foreach (var previewId in _folderPreviews
                     .Where(pair => pair.Value.ExpiresAtUtc <= now)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _folderPreviews.Remove(previewId);
        }
    }

    private static bool SameEnumeration(
        SourceFolderEnumeration expected,
        SourceFolderEnumeration actual) =>
        string.Equals(expected.RootLocator, actual.RootLocator, StringComparison.OrdinalIgnoreCase) &&
        expected.Entries.Length == actual.Entries.Length &&
        expected.Entries.Zip(actual.Entries).All(pair =>
            string.Equals(pair.First.RelativePath, pair.Second.RelativePath, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(pair.First.Locator, pair.Second.Locator, StringComparison.OrdinalIgnoreCase) &&
            pair.First.Included == pair.Second.Included &&
            string.Equals(pair.First.ExclusionReason, pair.Second.ExclusionReason, StringComparison.Ordinal));

    private static bool SameSelection(SourceFolderEnrollment folder, SourceFolderEnrollmentDraft selection) =>
        string.Equals(folder.RootLocator, selection.RootLocator, StringComparison.Ordinal) &&
        string.Equals(folder.ZoneId, selection.ZoneId, StringComparison.Ordinal) &&
        string.Equals(folder.Format, selection.Format, StringComparison.Ordinal) &&
        folder.FormatVersion == selection.FormatVersion &&
        folder.Recursive == selection.Recursive &&
        folder.IncludePatterns.SequenceEqual(selection.IncludePatterns, StringComparer.Ordinal) &&
        folder.ExcludePatterns.SequenceEqual(selection.ExcludePatterns, StringComparer.Ordinal) &&
        folder.RawPreservationRetainUntilUtc == selection.RawPreservationRetainUntilUtc;
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
        ISourceEnrollmentPersistence persistence)
    {
        EnsureOwner(owner);
        ArgumentNullException.ThrowIfNull(persistence);
        RequestSourceRefreshStop(sourceId);
        return _enrollments.Revoke(owner, sourceId, persistence);
    }

    /// <summary>
    /// Probes only the exact enrolled locator for an active, unpaused
    /// mapping. A changed observation updates a matching registered
    /// reference's availability under its live revision; a paused, revoked
    /// or quarantined source reports its life state with no read and no
    /// availability probe. No source search or autonomous background refresh
    /// is performed here.
    /// </summary>
    public SourceEnrollmentStatus Status(
        AuthenticatedContext owner,
        string sourceId,
        IVaultOperation operation,
        bool probeAvailability = true)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(operation);
        var fullState = SourceEnrollmentAuthority.ValidateAndNormalizeState(operation.LoadSourceEnrollmentState());
        var held = SourceEnrollmentAuthority.FindQuarantine(fullState, sourceId);
        if (held is not null && held.Reason == SourceQuarantineReason.MappingUnknown &&
            SourceEnrollmentAuthority.FindEnrollment(fullState, sourceId) is null)
        {
            return new SourceEnrollmentStatus(
                held.SourceId,
                "(unresolved)",
                "(unresolved)",
                0,
                0,
                SourceEnrollmentLife.Quarantined,
                0,
                held.RecordedAtUtc,
                null,
                held,
                0,
                0,
                0,
                0,
                null,
                SourceAvailabilityObservation.NotChecked,
                null,
                null,
                null,
                null,
                false,
                0,
                0,
                0);
        }

        var enrollment = LoadActiveEnrollment(owner, sourceId, operation);
        var sourceFolder = SourceFolderEnrollmentAuthority.FindFolderForSource(fullState, enrollment.SourceId);
        var quarantine = SourceEnrollmentAuthority.FindQuarantine(
            SourceEnrollmentAuthority.ValidateAndNormalizeState(operation.LoadSourceEnrollmentState()), sourceId);
        var (revision, journal) = ReadJournal(operation, enrollment);
        try
        {
            var life = enrollment.RevokedAtUtc is not null
                ? SourceEnrollmentLife.Revoked
                : quarantine is not null
                    ? SourceEnrollmentLife.Quarantined
                    : enrollment.PausedAtUtc is not null
                        ? SourceEnrollmentLife.Paused
                        : SourceEnrollmentLife.Enrolled;
            var observedAt = probeAvailability && life == SourceEnrollmentLife.Enrolled
                ? DateTimeOffset.UtcNow
                : (DateTimeOffset?)null;
            var availability = observedAt is null
                ? SourceAvailabilityObservation.NotChecked
                : ObserveAvailability(enrollment);
            var sourceReferenceRevision = observedAt is null
                ? (long?)null
                : UpdateRegisteredAvailability(owner, enrollment, availability, operation);
            var (journalBytes, snapshotBytes, snapshotRevision) = ReadCacheScope(operation, enrollment);
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
                sourceReferenceRevision,
                enrollment.PausedAtUtc,
                enrollment.RefreshOptIn,
                journalBytes,
                snapshotBytes,
                snapshotRevision,
                enrollment.RawPreservationOptIn,
                enrollment.RawPreservationRetainUntilUtc,
                CreateSummary(enrollment, journal),
                enrollment.RefreshOptOut,
                sourceFolder?.FolderId,
                sourceFolder?.RefreshOptIn);
        }
        finally
        {
            ClearJournalPreservedBytes(journal);
        }
    }

    private SourceAvailabilityObservation ObserveAvailability(EnrolledSource enrollment)
    {
        var snapshot = _reader.TryReadSnapshot(enrollment.Locator);
        if (snapshot.Status != SourceSnapshotStatus.Snapshot || snapshot.Content is not { } content)
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
            if (content.Length == 0)
            {
                return enrollment.AnchorLength == 0
                    ? SourceAvailabilityObservation.Empty
                    : SourceAvailabilityObservation.Changed;
            }

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
            SourceAvailabilityObservation.Available or SourceAvailabilityObservation.Empty => SourceAvailability.Available,
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

        if (enrollment.PausedAtUtc is not null)
        {
            return new SourceIngestionOutcome(SourceIngestionOutcomeKind.Paused, 0, 0, 0);
        }

        if (SourceEnrollmentAuthority.FindQuarantine(state, sourceId) is not null)
        {
            return new SourceIngestionOutcome(SourceIngestionOutcomeKind.MixedQuarantined, 0, 0, 0);
        }

        if (!_normalizers.TryResolve(enrollment.Format, enrollment.FormatVersion, out var normalizer) ||
            normalizer is null)
        {
            return new SourceIngestionOutcome(SourceIngestionOutcomeKind.UnsupportedFormat, 0, 0, 0);
        }

        var journalId = SourceIngestionIds.JournalIdFor(sourceId, enrollment.Generation);
        var (liveRevision, journal) = ReadJournal(operation, enrollment);
        var offset = journal?.ConsumedOffset ?? 0;
        try
        {
            if ((journal?.Events.Length ?? 0) >= SourceIngestionLimits.MaximumJournalEvents)
            {
                return new SourceIngestionOutcome(SourceIngestionOutcomeKind.JournalLimit, offset, 0, 0);
            }

            var snapshot = _reader.TryReadSnapshot(enrollment.Locator, cancellationToken);
            if (snapshot.Status != SourceSnapshotStatus.Snapshot || snapshot.Content is not { } content)
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

            List<PreservedSourceRecord>? preservedRecords = null;
            try
            {
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

                if (content.Length == 0)
                {
                    return new SourceIngestionOutcome(SourceIngestionOutcomeKind.Empty, 0, 0, 0);
                }

                var preserveUntil = enrollment.RawPreservationRetainUntilUtc;
                var preservationEnabled = enrollment.RawPreservationOptIn &&
                    preserveUntil is { } retainUntilUtc &&
                    retainUntilUtc > DateTimeOffset.UtcNow;
                var existingBarrier = journal?.Version2?.VersionBarrierOffset;
                if (existingBarrier is { } priorBarrier &&
                    content.Length == journal!.AnchorLength &&
                    SourceFingerprint.Matches(journal.AnchorDigest, content))
                {
                    var diagnostic = journal.Diagnostics.FirstOrDefault(item => item.Offset == priorBarrier);
                    return new SourceIngestionOutcome(
                        SourceIngestionOutcomeKind.UnsupportedRecord,
                        priorBarrier,
                        0,
                        0,
                        diagnostic?.Code ?? "version_barrier");
                }

                if (existingBarrier is { } pendingBarrier &&
                    content.Length > journal!.AnchorLength &&
                    !preservationEnabled)
                {
                    var diagnostic = journal.Diagnostics.FirstOrDefault(item => item.Offset == pendingBarrier);
                    return new SourceIngestionOutcome(
                        SourceIngestionOutcomeKind.UnsupportedRecord,
                        pendingBarrier,
                        0,
                        0,
                        diagnostic?.Code ?? "version_barrier");
                }

                var events = journal is null
                    ? new List<NormalizedSourceEvent>()
                    : journal.Events.ToList();
                var diagnostics = journal is null
                    ? new List<SourceIngestionDiagnostic>()
                    : journal.Diagnostics.ToList();
                preservedRecords = journal?.Version2?.PreservedRecords.ToList() ?? new List<PreservedSourceRecord>();
                var versionBarrierOffset = existingBarrier;
                var knownChainIds = BuildOmpChainIdentities(sourceId, normalizer, events, preservedRecords);
                var buffer = content.AsSpan((int)offset);
                var position = 0;
                var eventsAdded = 0;
                var diagnosticsAdded = 0;
                var diagnosticsChanged = false;
                var preservedChanged = false;
                string? stopDiagnostic = null;

                void RecordDiagnostic(long recordOffset, string code)
                {
                    var existingIndex = diagnostics.FindIndex(item => item.Offset == recordOffset);
                    if (existingIndex >= 0 &&
                        string.Equals(diagnostics[existingIndex].Code, code, StringComparison.Ordinal))
                    {
                        return;
                    }

                    if (existingIndex >= 0)
                    {
                        diagnostics.RemoveAt(existingIndex);
                    }

                    diagnostics.Add(new SourceIngestionDiagnostic(recordOffset, code, DateTimeOffset.UtcNow));
                    diagnosticsAdded++;
                    diagnosticsChanged = true;
                }

                void PreserveBytes(int length, long absoluteOffset, string? chainIdentity)
                {
                    var bytes = content.AsSpan(checked((int)absoluteOffset), length).ToArray();
                    var record = new PreservedSourceRecord(
                        absoluteOffset,
                        length,
                        SourceFingerprint.Compute(bytes),
                        preserveUntil!.Value,
                        bytes,
                        chainIdentity);
                    preservedRecords.RemoveAll(existing => existing.Offset == absoluteOffset);
                    preservedRecords.Add(record);
                    preservedChanged = true;
                }

                while (position < buffer.Length)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var remaining = buffer[position..];
                    var newline = remaining.IndexOf((byte)'\n');
                    if (newline < 0)
                    {
                        break;
                    }

                    var recordLength = newline + 1;
                    var line = remaining[..newline];
                    SourceRecordDecode decode;
                    if (HasInvalidRecordEncoding(line))
                    {
                        decode = new SourceRecordDecode(
                            SourceRecordDecodeKind.Unsupported,
                            recordLength,
                            null,
                            "source_record_encoding_invalid");
                    }
                    else
                    {
                        decode = normalizer.Decode(
                            remaining,
                            new SourceRecordContext(sourceId, enrollment.Generation, offset + position));
                    }

                    if (decode.Kind == SourceRecordDecodeKind.NeedMoreBytes)
                    {
                        decode = decode with
                        {
                            Kind = SourceRecordDecodeKind.Unsupported,
                            ConsumedBytes = recordLength,
                            Event = null,
                            Diagnostic = "normalizer_incomplete_record",
                            ChainIdentity = null,
                            ChainParentId = null,
                            IsVersionBarrier = false
                        };
                    }
                    else if (decode.Kind == SourceRecordDecodeKind.Consumed &&
                             (decode.ConsumedBytes != recordLength || decode.Event is null))
                    {
                        decode = decode with
                        {
                            Kind = SourceRecordDecodeKind.Unsupported,
                            ConsumedBytes = recordLength,
                            Event = null,
                            Diagnostic = "normalizer_no_progress"
                        };
                    }
                    else if (decode.Kind == SourceRecordDecodeKind.Unsupported &&
                             decode.ConsumedBytes != recordLength)
                    {
                        decode = decode with
                        {
                            ConsumedBytes = recordLength,
                            ChainIdentity = null,
                            ChainParentId = null,
                            IsVersionBarrier = false
                        };
                    }

                    if (string.Equals(normalizer.Format, OmpSessionNormalizer.FormatName, StringComparison.Ordinal) &&
                        decode.ChainIdentity is { } chainIdentity &&
                        decode.ChainParentId is { } parentId &&
                        !knownChainIds.Contains(parentId))
                    {
                        // D7 refuses this event; retain its independently validated envelope identity for D5 admission after preservation.
                        decode = decode with
                        {
                            Kind = SourceRecordDecodeKind.Unsupported,
                            Event = null,
                            Diagnostic = "omp_chain_reference_unresolved"
                        };
                    }

                    if (decode.Kind == SourceRecordDecodeKind.Consumed && decode.Event is { } draft)
                    {
                        var span = new SourceSpan(offset + position, offset + position + recordLength);
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

                        if (valid)
                        {
                            events.Add(normalized);
                            eventsAdded++;
                            if (decode.ChainIdentity is { } acceptedIdentity)
                            {
                                knownChainIds.Add(acceptedIdentity);
                            }

                            position += recordLength;
                            continue;
                        }

                        decode = decode with
                        {
                            Kind = SourceRecordDecodeKind.Unsupported,
                            ConsumedBytes = recordLength,
                            Event = null,
                            Diagnostic = "normalizer_invalid_event"
                        };
                    }

                    if (decode.Kind != SourceRecordDecodeKind.Unsupported)
                    {
                        stopDiagnostic = "normalizer_unknown_decode";
                        break;
                    }

                    var diagnosticCode = decode.Diagnostic is { Length: > 0 } candidate && IsDiagnostic(candidate)
                        ? candidate
                        : "unsupported_record";
                    var absoluteOffset = offset + position;
                    RecordDiagnostic(absoluteOffset, diagnosticCode);

                    if (decode.IsVersionBarrier)
                    {
                        stopDiagnostic = diagnosticCode;
                        if (preservationEnabled)
                        {
                            versionBarrierOffset = absoluteOffset;
                            PreserveBytes(buffer.Length - position, absoluteOffset, null);
                        }

                        break;
                    }

                    if (!preservationEnabled)
                    {
                        stopDiagnostic = diagnosticCode;
                        break;
                    }

                    PreserveBytes(recordLength, absoluteOffset, decode.ChainIdentity);
                    if (decode.ChainIdentity is { } preservedIdentity)
                    {
                        knownChainIds.Add(preservedIdentity);
                    }

                    position += recordLength;
                }

                var consumed = offset + position;
                if (events.Count > SourceIngestionLimits.MaximumJournalEvents ||
                    diagnostics.Count > SourceIngestionLimits.MaximumJournalDiagnostics)
                {
                    return new SourceIngestionOutcome(SourceIngestionOutcomeKind.JournalLimit, offset, 0, 0);
                }

                if (eventsAdded == 0 && !preservedChanged && stopDiagnostic is null &&
                    journal is not null && content.Length == journal.AnchorLength &&
                    SourceFingerprint.Matches(journal.AnchorDigest, content))
                {
                    return new SourceIngestionOutcome(
                        position < buffer.Length
                            ? SourceIngestionOutcomeKind.PartialPending
                            : SourceIngestionOutcomeKind.Idle,
                        consumed,
                        0,
                        0);
                }

                if (stopDiagnostic is not null && !diagnosticsChanged && !preservedChanged &&
                    eventsAdded == 0 && content.Length == anchorLength &&
                    SourceFingerprint.Matches(anchorDigest, content))
                {
                    return new SourceIngestionOutcome(
                        SourceIngestionOutcomeKind.UnsupportedRecord,
                        consumed,
                        0,
                        0,
                        stopDiagnostic);
                }

                var journalVersion2 = preservedRecords.Count > 0 || versionBarrierOffset is not null
                    ? new SourceIngestionJournalV2(preservedRecords.ToArray(), versionBarrierOffset)
                    : null;
                var schemaVersion = journalVersion2 is not null
                    ? SourceIngestionJournal.PreservationSchemaVersion
                    : journal?.SchemaVersion ?? SourceIngestionJournal.CurrentSchemaVersion;
                var nextJournal = new SourceIngestionJournal(
                    schemaVersion,
                    sourceId,
                    enrollment.ZoneId,
                    enrollment.Generation,
                    content.Length,
                    SourceFingerprint.Compute(content),
                    consumed,
                    events.ToArray(),
                    diagnostics.ToArray(),
                    DateTimeOffset.UtcNow,
                    journalVersion2);
                byte[]? encoded = null;
                try
                {
                    encoded = SourceIngestionJournalCodec.Encode(nextJournal);
                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        operation.WriteContent(journalId, enrollment.ZoneId, encoded, liveRevision);
                    }
                    catch (ManagedResourceUnavailableException)
                    {
                        return new SourceIngestionOutcome(SourceIngestionOutcomeKind.JournalConflict, offset, 0, 0);
                    }
                }
                catch (ArgumentException)
                {
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
                    diagnosticsAdded,
                    stopDiagnostic);
            }
            finally
            {
                if (preservedRecords is not null)
                {
                    ClearPreservedSourceRecords(preservedRecords);
                }

                CryptographicOperations.ZeroMemory(content);
            }
        }
        finally
        {
            ClearJournalPreservedBytes(journal);
        }
    }

    public EnrolledSource Pause(
        AuthenticatedContext owner,
        string sourceId,
        ISourceEnrollmentPersistence persistence)
    {
        EnsureOwner(owner);
        ArgumentNullException.ThrowIfNull(persistence);
        RequestSourceRefreshStop(sourceId);
        return _enrollments.Pause(owner, sourceId, persistence);
    }

    public EnrolledSource Resume(
        AuthenticatedContext owner,
        string sourceId,
        ISourceEnrollmentPersistence persistence)
    {
        var resumed = _enrollments.Resume(owner, sourceId, persistence);
        ClearSourceRefreshStop(sourceId);
        return resumed;
    }

    public EnrolledSource SetRefreshOptIn(
        AuthenticatedContext owner,
        string sourceId,
        bool optIn,
        ISourceEnrollmentPersistence persistence)
    {
        EnsureOwner(owner);
        ArgumentNullException.ThrowIfNull(persistence);
        if (!optIn)
        {
            RequestSourceRefreshStop(sourceId);
        }

        var updated = _enrollments.SetRefreshOptIn(owner, sourceId, optIn, persistence);
        if (optIn)
        {
            ClearSourceRefreshStop(sourceId);
        }

        return updated;
    }

    public SourceFolderEnrollment SetFolderRefreshOptIn(
        AuthenticatedContext owner,
        string folderId,
        bool optIn,
        IVaultOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        EnsureOwner(owner);
        if (!optIn)
        {
            RequestFolderRefreshStop(folderId);
        }
        IReadOnlySet<string>? legacyRefreshOptOuts = null;
        if (optIn)
        {
            legacyRefreshOptOuts = ReadLegacySourceRefreshOptOuts(operation);
        }

        var updated = SourceFolderEnrollmentAuthority.SetRefreshOptIn(
            owner, folderId, optIn, operation, legacyRefreshOptOuts);
        if (optIn)
        {
            ClearFolderRefreshStop(folderId);
        }

        return updated;
    }
    public EnrolledSource SetRawPreservationOptIn(
        AuthenticatedContext owner,
        string sourceId,
        bool optIn,
        DateTimeOffset? retainUntilUtc,
        ISourceEnrollmentPersistence persistence) =>
        _enrollments.SetRawPreservationOptIn(owner, sourceId, optIn, retainUntilUtc, persistence);
    
    /// <summary>
    /// Reads one owner-preserved record by its absolute byte offset. This path
    /// consults only the encrypted journal; it never reopens the source.
    /// </summary>
    public SourcePreservedRecordRead ReadPreservedRecord(
        AuthenticatedContext owner,
        string sourceId,
        long offset,
        IVaultOperation operation)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(operation);
        if (owner.Kind != PrincipalKind.Owner)
        {
            throw new UnauthorizedAccessException("Only the owner may read preserved source records.");
        }

        if (!DomainIdentifiers.IsDomainIdentifier(sourceId) || offset < 0)
        {
            throw new ArgumentException("The preserved source record identity is invalid.");
        }

        var state = SourceEnrollmentAuthority.ValidateAndNormalizeState(operation.LoadSourceEnrollmentState());
        var enrollment = SourceEnrollmentAuthority.FindEnrollment(state, sourceId)
            ?? throw new SourceOperationException("source_not_enrolled");
        var (_, journal) = ReadJournal(operation, enrollment);
        try
        {
            var record = journal?.Version2?.PreservedRecords.FirstOrDefault(candidate => candidate.Offset == offset)
                ?? throw new SourceOperationException("source_record_not_preserved");
            if (record.RetainUntilUtc <= DateTimeOffset.UtcNow)
            {
                throw new SourceOperationException("source_preserved_record_expired");
            }

            return new SourcePreservedRecordRead(
                new SourcePreservedRecordDescriptor(record.Offset, record.Length, record.Digest, record.RetainUntilUtc),
                record.Bytes.ToArray());
        }
        finally
        {
            ClearJournalPreservedBytes(journal);
        }
    }

    /// <summary>
    /// S5-T7 owner mapping review submission: holds one source whose
    /// zone/format mapping is unresolved in the private quarantine with no
    /// transcript read and no guessed zone. The coordinator performs no
    /// anchor read here — the authority grammar-checks the locator only —
    /// so an uncertain mapping can never be ingested into a default zone.
    /// </summary>
    public SourceQuarantineRecord SubmitMappingUnknown(
        AuthenticatedContext owner,
        string sourceId,
        string locator,
        string? zoneId,
        string? format,
        int? formatVersion,
        string detail,
        ISourceEnrollmentPersistence persistence) =>
        _enrollments.SubmitMappingUnknown(owner, sourceId, locator, zoneId, format, formatVersion, detail, persistence);

    /// <summary>
    /// S5-T7 owner mapping resolution withdrawal: clears one held
    /// mapping-unknown submission without enrolling it. The held locator is
    /// never opened.
    /// </summary>
    public void RevokeMappingUnknown(
        AuthenticatedContext owner,
        string sourceId,
        ISourceEnrollmentPersistence persistence) =>
        _enrollments.RevokeMappingUnknown(owner, sourceId, persistence);


    public IReadOnlyList<SourceEnrollmentStatus> List(
        AuthenticatedContext owner,
        IVaultOperation operation)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(operation);
        var state = SourceEnrollmentAuthority.ValidateAndNormalizeState(operation.LoadSourceEnrollmentState());
        var result = new List<SourceEnrollmentStatus>(state.Enrollments.Length + state.Quarantine.Length);
        foreach (var enrollment in state.Enrollments.OrderBy(candidate => candidate.SourceId, StringComparer.Ordinal))
        {
            var quarantine = SourceEnrollmentAuthority.FindQuarantine(state, enrollment.SourceId);
            var life = enrollment.RevokedAtUtc is not null
                ? SourceEnrollmentLife.Revoked
                : quarantine is not null
                    ? SourceEnrollmentLife.Quarantined
                    : enrollment.PausedAtUtc is not null
                        ? SourceEnrollmentLife.Paused
                        : SourceEnrollmentLife.Enrolled;
            var sourceFolder = SourceFolderEnrollmentAuthority.FindFolderForSource(state, enrollment.SourceId);
            var (revision, journal) = ReadJournal(operation, enrollment);
            try
            {
                var (journalBytes, snapshotBytes, snapshotRevision) = ReadCacheScope(operation, enrollment);
                result.Add(new SourceEnrollmentStatus(
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
                    SourceAvailabilityObservation.NotChecked,
                    null,
                    enrollment.RelocatedAtUtc,
                    null,
                    enrollment.PausedAtUtc,
                    enrollment.RefreshOptIn,
                    journalBytes,
                    snapshotBytes,
                    snapshotRevision,
                    enrollment.RawPreservationOptIn,
                    enrollment.RawPreservationRetainUntilUtc,
                    CreateSummary(enrollment, journal),
                    enrollment.RefreshOptOut,
                    sourceFolder?.FolderId,
                    sourceFolder?.RefreshOptIn));
            }
            finally
            {
                ClearJournalPreservedBytes(journal);
            }
        }

        // S5-T7 mapping review: held mapping-unknown submissions are reported
        // beside the allowlist with their quarantine record, no mapping, no
        // checkpoint and no availability probe. The held locator is never
        // echoed: only the quarantine reason, the unresolved-field detail and
        // the recorded time cross this record.
        foreach (var held in state.Quarantine
            .Where(record => record.Reason == SourceQuarantineReason.MappingUnknown)
            .OrderBy(record => record.SourceId, StringComparer.Ordinal))
        {
            result.Add(new SourceEnrollmentStatus(
                held.SourceId,
                "(unresolved)",
                "(unresolved)",
                0,
                0,
                SourceEnrollmentLife.Quarantined,
                0,
                held.RecordedAtUtc,
                null,
                held,
                0,
                0,
                0,
                0,
                null,
                SourceAvailabilityObservation.NotChecked,
                null,
                null,
                null,
                null,
                false,
                0,
                0,
                0));
        }

        return result;
    }

    /// <summary>
    /// Runs one shared refresh pass: explicit folder consent first discovers
    /// matching members, then the same per-source checkpoint path processes
    /// opted-in standalone sources and members of opted-in folders.
    /// </summary>
    public IReadOnlyList<SourceRefreshResult> Refresh(
        AuthenticatedContext owner,
        IVaultOperation operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(operation);
        var results = new List<SourceRefreshResult>();
        var activeFolders = new Dictionary<string, CancellationTokenSource>(StringComparer.Ordinal);
        try
        {
            var initialState = SourceEnrollmentAuthority.ValidateAndNormalizeState(operation.LoadSourceEnrollmentState());
            IReadOnlySet<string>? legacyRefreshOptOuts = null;
            foreach (var folder in (initialState.Folders ?? Array.Empty<SourceFolderEnrollment>())
                         .OrderBy(candidate => candidate.FolderId, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!folder.RefreshOptIn || IsFolderRefreshStopped(folder.FolderId))
                {
                    continue;
                }

                var folderRefresh = BeginFolderRefresh(folder.FolderId, cancellationToken);
                activeFolders.Add(folder.FolderId, folderRefresh);
                if (folderRefresh.IsCancellationRequested)
                {
                    continue;
                }

                try
                {
                    var legacyOptOuts = legacyRefreshOptOuts ??= ReadLegacySourceRefreshOptOuts(operation);
                    results.AddRange(RefreshFolderMembership(owner, folder, operation, legacyOptOuts, folderRefresh.Token));
                }
                catch (OperationCanceledException) when (
                    !cancellationToken.IsCancellationRequested && folderRefresh.IsCancellationRequested)
                {
                    // A pending folder opt-out cancels discovery/admission before its state write.
                }
            }

            foreach (var enrollment in ListEnrolledInOrder(operation))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var liveState = SourceEnrollmentAuthority.ValidateAndNormalizeState(operation.LoadSourceEnrollmentState());
                var live = SourceEnrollmentAuthority.FindEnrollment(liveState, enrollment.SourceId);
                if (live is null)
                {
                    results.Add(new SourceRefreshResult(enrollment.SourceId, "source_not_enrolled", 0, 0, 0, null));
                    continue;
                }

                if (live.RevokedAtUtc is not null)
                {
                    results.Add(new SourceRefreshResult(live.SourceId, "skipped_revoked", 0, 0, 0, null));
                    continue;
                }

                if (live.PausedAtUtc is not null)
                {
                    results.Add(new SourceRefreshResult(live.SourceId, "skipped_paused", 0, 0, 0, null));
                    continue;
                }

                if (SourceEnrollmentAuthority.FindQuarantine(liveState, live.SourceId) is not null)
                {
                    results.Add(new SourceRefreshResult(live.SourceId, "skipped_quarantined", 0, 0, 0, null));
                    continue;
                }

                if (live.RefreshOptOut)
                {
                    results.Add(new SourceRefreshResult(live.SourceId, "skipped_source_opted_out", 0, 0, 0, null));
                    continue;
                }

                if (IsSourceRefreshStopped(live.SourceId))
                {
                    results.Add(new SourceRefreshResult(live.SourceId, "skipped_source_stop_requested", 0, 0, 0, null));
                    continue;
                }

                var folder = SourceFolderEnrollmentAuthority.FindFolderForSource(liveState, live.SourceId);
                CancellationTokenSource? folderRefresh = null;
                if (folder is not null)
                {
                    if (!folder.RefreshOptIn)
                    {
                        results.Add(new SourceRefreshResult(live.SourceId, "skipped_folder_opted_out", 0, 0, 0, null));
                        continue;
                    }

                    if (IsFolderRefreshStopped(folder.FolderId) ||
                        !activeFolders.TryGetValue(folder.FolderId, out folderRefresh) ||
                        folderRefresh.IsCancellationRequested)
                    {
                        results.Add(new SourceRefreshResult(live.SourceId, "skipped_folder_stop_requested", 0, 0, 0, null));
                        continue;
                    }
                }
                else if (!live.RefreshOptIn)
                {
                    results.Add(new SourceRefreshResult(live.SourceId, "skipped_not_opted_in", 0, 0, 0, null));
                    continue;
                }

                var sourceRefresh = BeginSourceRefresh(
                    live.SourceId, folderRefresh?.Token ?? cancellationToken);
                try
                {
                    if (sourceRefresh.IsCancellationRequested)
                    {
                        var stopStatus = folderRefresh?.IsCancellationRequested == true
                            ? "skipped_folder_stop_requested"
                            : "skipped_source_stop_requested";
                        results.Add(new SourceRefreshResult(live.SourceId, stopStatus, 0, 0, 0, null));
                        continue;
                    }

                    SourceIngestionOutcome outcome;
                    try
                    {
                        outcome = Ingest(owner, live.SourceId, operation, sourceRefresh.Token);
                    }
                    catch (OperationCanceledException) when (
                        !cancellationToken.IsCancellationRequested && sourceRefresh.IsCancellationRequested)
                    {
                        var stopStatus = folderRefresh?.IsCancellationRequested == true
                            ? "skipped_folder_stop_requested"
                            : "skipped_source_stop_requested";
                        results.Add(new SourceRefreshResult(live.SourceId, stopStatus, 0, 0, 0, null));
                        continue;
                    }

                    var status = outcome.Kind switch
                    {
                        SourceIngestionOutcomeKind.Advanced => "ingest_advanced",
                        SourceIngestionOutcomeKind.Empty => "ingest_empty",
                        SourceIngestionOutcomeKind.Idle => "ingest_idle",
                        SourceIngestionOutcomeKind.PartialPending => "ingest_partial_pending",
                        SourceIngestionOutcomeKind.UnsupportedFormat => "ingest_unsupported_format",
                        SourceIngestionOutcomeKind.UnsupportedRecord => "ingest_unsupported_record",
                        SourceIngestionOutcomeKind.NotEnrolled => "source_not_enrolled",
                        SourceIngestionOutcomeKind.Revoked => "source_revoked",
                        SourceIngestionOutcomeKind.MixedQuarantined => "source_mixed_quarantined",
                        SourceIngestionOutcomeKind.GenerationChanged => "source_generation_changed",
                        SourceIngestionOutcomeKind.SourceMissing => "source_missing",
                        SourceIngestionOutcomeKind.SourceUnreadable => "source_unreadable",
                        SourceIngestionOutcomeKind.SourceTooLarge => "source_too_large",
                        SourceIngestionOutcomeKind.JournalLimit => "source_journal_limit",
                        SourceIngestionOutcomeKind.JournalConflict => "source_journal_conflict",
                        SourceIngestionOutcomeKind.Paused => "source_paused",
                        _ => "source_ingest_rejected"
                    };
                    results.Add(new SourceRefreshResult(
                        live.SourceId, status, outcome.ConsumedOffset, outcome.EventsAdded,
                        outcome.DiagnosticsAdded, outcome.DiagnosticCode));
                }
                finally
                {
                    EndSourceRefresh(live.SourceId, sourceRefresh);
                    sourceRefresh.Dispose();
                }
            }

            return results;
        }
        finally
        {
            foreach (var (folderId, refresh) in activeFolders)
            {
                EndFolderRefresh(folderId, refresh);
                refresh.Dispose();
            }
        }
    }

    private IReadOnlyList<SourceRefreshResult> RefreshFolderMembership(
        AuthenticatedContext owner,
        SourceFolderEnrollment folder,
        IVaultOperation operation,
        IReadOnlySet<string> legacyRefreshOptOuts,
        CancellationToken cancellationToken)
    {
        var enumerator = _folderEnumerator ?? throw new SourceOperationException("source_folder_unavailable");
        SourceFolderEnumeration enumeration;
        try
        {
            enumeration = enumerator.Enumerate(
                folder.RootLocator,
                folder.Recursive,
                folder.IncludePatterns,
                folder.ExcludePatterns,
                cancellationToken);
        }
        catch (SourceOperationException exception) when (exception.Code == "source_folder_missing")
        {
            // Existing exact members remain enrolled; the source pass reports each missing path below.
            return Array.Empty<SourceRefreshResult>();
        }

        var state = SourceEnrollmentAuthority.ValidateAndNormalizeState(operation.LoadSourceEnrollmentState());
        var liveFolder = SourceFolderEnrollmentAuthority.FindFolder(state, folder.FolderId);
        if (liveFolder is null || !liveFolder.RefreshOptIn || IsFolderRefreshStopped(folder.FolderId))
        {
            return Array.Empty<SourceRefreshResult>();
        }

        var purgedState = OwnerPurgeLedgerAuthority.ValidateAndNormalizeState(operation.LoadPurgeLedgerState());
        var purgedResourceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var intent in purgedState.Intents)
        {
            purgedResourceIds.Add(intent.TargetResourceId);
            foreach (var impacted in intent.Impacted)
            {
                purgedResourceIds.Add(impacted.ResourceId);
            }
        }

        var existingMemberIds = new HashSet<string>(liveFolder.MemberSourceIds, StringComparer.Ordinal);
        var anchors = new List<SourceFolderMemberAnchor>();
        var results = new List<SourceRefreshResult>();
        foreach (var entry in enumeration.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!entry.Included)
            {
                continue;
            }

            var sourceId = SourceFolderEnrollmentGrammar.MemberSourceIdFor(
                liveFolder.RootLocator, entry.RelativePath);
            if (existingMemberIds.Contains(sourceId))
            {
                continue;
            }

            if (purgedResourceIds.Contains(sourceId))
            {
                throw new SourceOperationException("resource_purged");
            }
            if (legacyRefreshOptOuts.Contains(sourceId))
            {
                results.Add(new SourceRefreshResult(sourceId, "skipped_source_opted_out", 0, 0, 0, null));
                continue;
            }

            if (IsSourceRefreshStopped(sourceId))
            {
                results.Add(new SourceRefreshResult(sourceId, "skipped_source_stop_requested", 0, 0, 0, null));
                continue;
            }

            var current = SourceEnrollmentAuthority.FindEnrollment(state, sourceId);
            var quarantine = SourceEnrollmentAuthority.FindQuarantine(state, sourceId);
            if (quarantine is not null)
            {
                if (current is null)
                {
                    results.Add(new SourceRefreshResult(sourceId, "skipped_quarantined", 0, 0, 0, null));
                }

                continue;
            }

            if (current is not null)
            {
                if (current.RevokedAtUtc is not null ||
                    current.PausedAtUtc is not null ||
                    current.RefreshOptOut ||
                    SourceFolderEnrollmentAuthority.FindFolderForSource(state, sourceId) is not null)
                {
                    continue;
                }

                if (!string.Equals(current.ZoneId, liveFolder.ZoneId, StringComparison.Ordinal) ||
                    !string.Equals(current.Locator, entry.Locator, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(current.Format, liveFolder.Format, StringComparison.Ordinal) ||
                    current.FormatVersion != liveFolder.FormatVersion)
                {
                    throw new SourceOperationException("source_enrollment_conflict");
                }
            }

            var sourceRefresh = BeginSourceRefresh(sourceId, cancellationToken);
            try
            {
                if (sourceRefresh.IsCancellationRequested)
                {
                    var stopStatus = cancellationToken.IsCancellationRequested
                        ? "skipped_folder_stop_requested"
                        : "skipped_source_stop_requested";
                    results.Add(new SourceRefreshResult(sourceId, stopStatus, 0, 0, 0, null));
                    continue;
                }

                SourceSnapshotResult snapshot;
                try
                {
                    snapshot = _reader.TryReadSnapshot(entry.Locator, sourceRefresh.Token);
                }
                catch (OperationCanceledException) when (
                    !cancellationToken.IsCancellationRequested && sourceRefresh.IsCancellationRequested)
                {
                    results.Add(new SourceRefreshResult(sourceId, "skipped_source_stop_requested", 0, 0, 0, null));
                    continue;
                }

                if (snapshot.Status != SourceSnapshotStatus.Snapshot || snapshot.Content is not { } content)
                {
                    var status = snapshot.Status switch
                    {
                        SourceSnapshotStatus.Missing => "source_missing",
                        SourceSnapshotStatus.TooLarge => "source_too_large",
                        _ => "source_unreadable"
                    };
                    results.Add(new SourceRefreshResult(sourceId, status, 0, 0, 0, null));
                    continue;
                }

                try
                {
                    sourceRefresh.Token.ThrowIfCancellationRequested();
                    var digest = SourceFingerprint.Compute(content);
                    sourceRefresh.Token.ThrowIfCancellationRequested();
                    anchors.Add(new SourceFolderMemberAnchor(
                        sourceId, entry.RelativePath, entry.Locator, content.Length, digest));
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(content);
                }
            }
            catch (OperationCanceledException) when (
                !cancellationToken.IsCancellationRequested && sourceRefresh.IsCancellationRequested)
            {
                results.Add(new SourceRefreshResult(sourceId, "skipped_source_stop_requested", 0, 0, 0, null));
            }
            finally
            {
                EndSourceRefresh(sourceId, sourceRefresh);
                sourceRefresh.Dispose();
            }
        }

        if (anchors.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var selection = new SourceFolderEnrollmentDraft(
                liveFolder.RootLocator,
                liveFolder.ZoneId,
                liveFolder.Format,
                liveFolder.FormatVersion,
                liveFolder.Recursive,
                liveFolder.IncludePatterns,
                liveFolder.ExcludePatterns,
                liveFolder.RawPreservationRetainUntilUtc);
            _ = SourceFolderEnrollmentAuthority.AddMembers(
                owner,
                liveFolder.FolderId,
                anchors,
                operation,
                () => RegisterFolderSources(owner, selection, anchors, operation, cancellationToken),
                cancellationToken);
        }

        return results;
    }

    internal void RequestFolderRefreshStop(string? folderId)
    {
        if (folderId is null || !DomainIdentifiers.IsDomainIdentifier(folderId))
        {
            return;
        }

        lock (_refreshStopGate)
        {
            _stoppedFolderRefreshes.Add(folderId);
            if (_activeFolderRefreshes.TryGetValue(folderId, out var refresh))
            {
                refresh.Cancel();
            }
        }
    }

    internal void RequestSourceRefreshStop(string? sourceId)
    {
        if (sourceId is null || !DomainIdentifiers.IsDomainIdentifier(sourceId))
        {
            return;
        }

        lock (_refreshStopGate)
        {
            _stoppedSourceRefreshes.Add(sourceId);
            if (_activeSourceRefreshes.TryGetValue(sourceId, out var refresh))
            {
                refresh.Cancel();
            }
        }
    }

    private CancellationTokenSource BeginFolderRefresh(string folderId, CancellationToken cancellationToken)
    {
        var refresh = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_refreshStopGate)
        {
            if (_stoppedFolderRefreshes.Contains(folderId))
            {
                refresh.Cancel();
            }

            if (_activeFolderRefreshes.TryGetValue(folderId, out var previous))
            {
                previous.Cancel();
            }

            _activeFolderRefreshes[folderId] = refresh;
        }

        return refresh;
    }

    private void EndFolderRefresh(string folderId, CancellationTokenSource refresh)
    {
        lock (_refreshStopGate)
        {
            if (_activeFolderRefreshes.TryGetValue(folderId, out var active) &&
                ReferenceEquals(active, refresh))
            {
                _activeFolderRefreshes.Remove(folderId);
            }
        }
    }

    private CancellationTokenSource BeginSourceRefresh(string sourceId, CancellationToken cancellationToken)
    {
        var refresh = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_refreshStopGate)
        {
            if (_stoppedSourceRefreshes.Contains(sourceId))
            {
                refresh.Cancel();
            }

            if (_activeSourceRefreshes.TryGetValue(sourceId, out var previous))
            {
                previous.Cancel();
            }

            _activeSourceRefreshes[sourceId] = refresh;
        }

        return refresh;
    }

    private void EndSourceRefresh(string sourceId, CancellationTokenSource refresh)
    {
        lock (_refreshStopGate)
        {
            if (_activeSourceRefreshes.TryGetValue(sourceId, out var active) &&
                ReferenceEquals(active, refresh))
            {
                _activeSourceRefreshes.Remove(sourceId);
            }
        }
    }

    private bool IsFolderRefreshStopped(string folderId)
    {
        lock (_refreshStopGate)
        {
            return _stoppedFolderRefreshes.Contains(folderId);
        }
    }

    private bool IsSourceRefreshStopped(string sourceId)
    {
        lock (_refreshStopGate)
        {
            return _stoppedSourceRefreshes.Contains(sourceId);
        }
    }

    private void ClearFolderRefreshStop(string folderId)
    {
        lock (_refreshStopGate)
        {
            _stoppedFolderRefreshes.Remove(folderId);
        }
    }

    private void ClearSourceRefreshStop(string sourceId)
    {
        lock (_refreshStopGate)
        {
            _stoppedSourceRefreshes.Remove(sourceId);
        }
    }

    private static void EnsureOwner(AuthenticatedContext owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (owner.Kind != PrincipalKind.Owner)
        {
            throw new UnauthorizedAccessException("Only the owner may change source enrollment or refresh consent.");
        }
    }

    private static IReadOnlySet<string> ReadLegacySourceRefreshOptOuts(IVaultOperation operation)
    {
        var audit = OperationsAuditAuthority.ValidateAndNormalizeState(operation.LoadOperationsAuditState());
        var optedOutSources = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in audit.Audit)
        {
            if (entry.ResourceId is not { } sourceId)
            {
                continue;
            }

            if (string.Equals(entry.Action, ServiceRequestHandler.SourceRefreshOptOutOperation, StringComparison.Ordinal))
            {
                optedOutSources.Add(sourceId);
            }
            else if (string.Equals(entry.Action, ServiceRequestHandler.SourceRefreshOptInOperation, StringComparison.Ordinal))
            {
                optedOutSources.Remove(sourceId);
            }
        }

        return optedOutSources;
    }
    private static bool IsDiagnostic(string value) =>
        SourceEnrollmentGrammar.IsDiagnosticCode(value);
    private static IReadOnlyList<EnrolledSource> ListEnrolledInOrder(IVaultOperation operation)
    {
        var state = SourceEnrollmentAuthority.ValidateAndNormalizeState(operation.LoadSourceEnrollmentState());
        return state.Enrollments.OrderBy(candidate => candidate.SourceId, StringComparer.Ordinal).ToArray();
    }


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
    /// S3-T6-A2 owner cache scope: the actual encrypted cache/snapshot resources
    /// backing one enrolled source generation. The journal ciphertext revision and
    /// the selected snapshot ciphertext revision (when the owner preserved one)
    /// are resolved from the live encrypted manifest — never from enrollment
    /// metadata, never from a locator, and never from plaintext sizes. Sizes are
    /// the encrypted ciphertext lengths observed through the authorized operation,
    /// so the owner sees the real vault footprint without any private content,
    /// locator or key crossing the boundary.
    /// </summary>
    private static (long JournalCiphertextBytes, long SnapshotCiphertextBytes, long SnapshotRevision) ReadCacheScope(
        IVaultOperation operation,
        EnrolledSource enrollment)
    {
        var journalId = SourceIngestionIds.JournalIdFor(enrollment.SourceId, enrollment.Generation);
        var snapshotId = SourceSnapshotIds.For(enrollment.SourceId, enrollment.Generation);
        long journalBytes = 0;
        long snapshotBytes = 0;
        long snapshotRevision = 0;
        foreach (var resource in operation.ListResources())
        {
            if (string.Equals(resource.ResourceId, journalId, StringComparison.Ordinal))
            {
                journalBytes = ReadCiphertextLength(operation, journalId, resource.Revision);
            }
            else if (string.Equals(resource.ResourceId, snapshotId, StringComparison.Ordinal))
            {
                snapshotRevision = resource.Revision;
                snapshotBytes = ReadCiphertextLength(operation, snapshotId, resource.Revision);
            }
        }

        return (journalBytes, snapshotBytes, snapshotRevision);
    }

    private static long ReadCiphertextLength(IVaultOperation operation, string resourceId, long revision)
    {
        byte[]? raw = null;
        try
        {
            raw = operation.ReadContent(resourceId, revision);
            return raw.LongLength;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return 0;
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
    private static SourceIngestionSummary CreateSummary(EnrolledSource enrollment, SourceIngestionJournal? journal)
    {
        var records = journal?.Version2?.PreservedRecords ?? Array.Empty<PreservedSourceRecord>();
        var diagnostics = journal?.Diagnostics ?? Array.Empty<SourceIngestionDiagnostic>();
        var anchorLength = journal?.AnchorLength ?? enrollment.AnchorLength;
        var consumed = journal?.ConsumedOffset ?? 0;
        var suffixBytes = Math.Max(0, anchorLength - consumed);
        var barrierOffset = journal?.Version2?.VersionBarrierOffset;
        var preservedBytes = records.Sum(record => (long)record.Length);
        var preservedPendingBytes = barrierOffset is null
            ? 0
            : records.Sum(record => Math.Max(0, record.End - Math.Max(record.Offset, consumed)));
        var stoppedAtCheckpoint = diagnostics.Any(diagnostic => diagnostic.Offset == consumed);
        var pendingTailBytes = journal is not null && barrierOffset is null && !stoppedAtCheckpoint
            ? suffixBytes
            : 0;
        var unprocessedBytes = journal is null
            ? anchorLength
            : barrierOffset is not null
                ? Math.Max(0, suffixBytes - preservedPendingBytes)
                : stoppedAtCheckpoint
                    ? suffixBytes
                    : 0;

        var interpretation = anchorLength == 0
            ? "empty"
            : journal is null || journal.Events.Length == 0 && consumed == 0
                ? "uninterpreted"
                : diagnostics.Length > 0 || records.Length > 0 || unprocessedBytes > 0
                    ? "partially_interpreted"
                    : "fully_supported";
        var lastDiagnostic = diagnostics
            .OrderByDescending(diagnostic => diagnostic.Offset)
            .FirstOrDefault();
        var descriptors = records
            .OrderBy(record => record.Offset)
            .Select(record => new SourcePreservedRecordDescriptor(
                record.Offset, record.Length, record.Digest, record.RetainUntilUtc))
            .ToArray();

        return new SourceIngestionSummary(
            records.Length == 0 ? "reference_only" : "preserved",
            interpretation,
            "not_indexed",
            pendingTailBytes,
            preservedBytes,
            diagnostics.Length,
            unprocessedBytes,
            preservedPendingBytes,
            lastDiagnostic is null ? null : new SourceRecordDiagnostic(lastDiagnostic.Code, lastDiagnostic.Offset),
            descriptors);
    }

    private static HashSet<string> BuildOmpChainIdentities(
        string sourceId,
        ISourceRecordNormalizer normalizer,
        IReadOnlyList<NormalizedSourceEvent> events,
        IReadOnlyList<PreservedSourceRecord> preservedRecords)
    {
        var identities = new HashSet<string>(StringComparer.Ordinal);
        if (!string.Equals(normalizer.Format, OmpSessionNormalizer.FormatName, StringComparison.Ordinal))
        {
            return identities;
        }

        var prefix = "omp-" + sourceId + "-";
        foreach (var normalized in events)
        {
            if (!normalized.PayloadKind.StartsWith("omp.", StringComparison.Ordinal) ||
                normalized.PayloadKind is "omp.session" or "omp.title" ||
                !normalized.EventId.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var identity = normalized.EventId[prefix.Length..];
            if (identity.Length > 0)
            {
                identities.Add(identity);
            }
        }

        foreach (var preserved in preservedRecords)
        {
            if (preserved.ChainIdentity is { } identity)
            {
                identities.Add(identity);
            }
        }

        return identities;
    }

    private static bool HasInvalidRecordEncoding(ReadOnlySpan<byte> line)
    {
        if (line.Length >= 3 && line[0] == 0xEF && line[1] == 0xBB && line[2] == 0xBF)
        {
            return true;
        }

        var offset = 0;
        while (offset < line.Length)
        {
            if (Rune.DecodeFromUtf8(line[offset..], out _, out var bytesConsumed) != OperationStatus.Done)
            {
                return true;
            }

            offset += bytesConsumed;
        }

        return false;
    }

    private static void ClearJournalPreservedBytes(SourceIngestionJournal? journal)
    {
        if (journal?.Version2?.PreservedRecords is not { } records)
        {
            return;
        }

        ClearPreservedSourceRecords(records);
    }

    private static void ClearPreservedSourceRecords(IEnumerable<PreservedSourceRecord> records)
    {
        foreach (var record in records)
        {
            if (record.Bytes.Length > 0)
            {
                CryptographicOperations.ZeroMemory(record.Bytes);
            }
        }
    }
}
