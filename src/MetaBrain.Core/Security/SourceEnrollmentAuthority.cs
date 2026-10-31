using MetaBrain.Core.Domain;

namespace MetaBrain.Core.Security;

/// <summary>
/// Grammar for owner-declared enrollment fields. A locator is one exact,
/// owner-enrolled absolute path: no glob, no relative path and no cwd
/// fallback, so enrollment can never degrade into scanning a profile
/// directory (R16).
/// </summary>
public static class SourceEnrollmentGrammar
{
    public static bool IsFormat(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > SourceIngestionLimits.MaximumFormatLength)
        {
            return false;
        }

        if (!DomainIdentifiers.IsAsciiAlphaNumeric(value[0]))
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!DomainIdentifiers.IsAsciiAlphaNumeric(character) &&
                character is not ('.' or '_' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    public static bool IsLocator(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > SourceIngestionLimits.MaximumLocatorLength)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (char.IsControl(character) || character is '*' or '?')
            {
                return false;
            }
        }

        return Path.IsPathFullyQualified(value);
    }

    public static string NormalizeLocator(string locator)
    {
        ArgumentNullException.ThrowIfNull(locator);
        if (!IsLocator(locator))
        {
            throw new ArgumentException("The enrolled source locator must be an absolute path without wildcards.");
        }

        try
        {
            return Path.GetFullPath(locator);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException("The enrolled source locator must be an absolute path without wildcards.", ex);
        }
    }

    public static bool IsDiagnosticCode(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > SourceIngestionLimits.MaximumDiagnosticCodeLength)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!DomainIdentifiers.IsAsciiAlphaNumeric(character) &&
                character is not ('.' or '_' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    public static bool IsAnchorDigest(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length != 44)
        {
            return false;
        }

        try
        {
            return Convert.FromBase64String(value).Length == 32;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

/// <summary>
/// One owner-enrolled source fixture: the exact locator, its zone mapping,
/// the declared format/version used for dispatch, the current generation, and
/// the generation anchor (length + digest) captured when the owner enrolled
/// or explicitly re-enrolled the fixture. Revocation is recorded, never
/// erased, so a revoked source stays auditable and re-enrollment starts a new
/// generation. Standalone refresh opt-in remains separate from the default
/// false state, and an explicit durable refresh opt-out remains a veto when
/// the parent folder is opted in.
/// </summary>
public sealed record EnrolledSource(
    string SourceId,
    string ZoneId,
    string Locator,
    string Format,
    int FormatVersion,
    long Generation,
    long AnchorLength,
    string AnchorDigest,
    DateTimeOffset EnrolledAtUtc,
    DateTimeOffset? RevokedAtUtc,
    DateTimeOffset? RelocatedAtUtc = null,
    DateTimeOffset? PausedAtUtc = null,
    bool RefreshOptIn = false,
    bool RawPreservationOptIn = false,
    DateTimeOffset? RawPreservationRetainUntilUtc = null,
    bool RefreshOptOut = false);

public enum SourceQuarantineReason
{
    MixedLocator = 1,
    GenerationTruncated = 2,
    GenerationReplaced = 3,
    MappingUnknown = 4
}

/// <summary>
/// Encrypted quarantine record for a source whose identity or mapping became
/// ambiguous (R16): the locator is claimed by another enrollment, the
/// fixture's bytes no longer match the enrolled generation, or the owner
/// submitted a source whose zone/format mapping is not yet resolved
/// (<see cref="SourceQuarantineReason.MappingUnknown"/>). A mapping-unknown
/// record holds the submitted locator with no zone default and no transcript
/// read: only an explicit owner resolution (confirm the mapping or revoke the
/// submission) clears it. Quarantine is private state, it is checked before
/// any source read, and only an explicit owner re-enrollment, resolution or
/// revocation clears it.
/// </summary>
public sealed record SourceQuarantineRecord(
    string SourceId,
    SourceQuarantineReason Reason,
    string Detail,
    string Locator,
    DateTimeOffset RecordedAtUtc);

/// <summary>
/// The owner allowlist plus quarantine and folder-recovery journal, persisted as one encrypted private
/// state file beside the scope-grant and catalog states (never as an approved-catalog entry). It carries
/// only enrollment metadata and pending canonical source IDs, never source bytes.
/// </summary>
public sealed record SourceEnrollmentState(
    int SchemaVersion,
    EnrolledSource[] Enrollments,
    SourceQuarantineRecord[] Quarantine,
    SourceFolderEnrollment[]? Folders = null,
    string[]? PendingFolderSourceIds = null)
{
    public const int CurrentSchemaVersion = 2;
    public const int PreviousSchemaVersion = 1;

    public static SourceEnrollmentState Empty { get; } = new(
        CurrentSchemaVersion,
        Array.Empty<EnrolledSource>(),
        Array.Empty<SourceQuarantineRecord>(),
        Array.Empty<SourceFolderEnrollment>(),
        Array.Empty<string>());
}

/// <summary>
/// Owner-visible refusal carrying a stable wire code. Explicit diagnostics
/// are a contract of this stage (R17): a refusal names the exact condition
/// instead of collapsing into a generic invalid request, so unknown,
/// revoked, quarantined and conflicting states are never confused.
/// </summary>
public sealed class SourceOperationException : Exception
{
    public SourceOperationException(string code)
        : base(code)
    {
        Code = code;
    }

    public SourceOperationException(string code, Exception innerException)
        : base(code, innerException)
    {
        Code = code;
    }

    public string Code { get; }
}

/// <summary>
/// Encrypted persistence for the owner enrollment state, implemented by the
/// vault lifecycle so enrollment transitions share the same atomic update
/// and revision semantics as scope grants and the owner catalog. Pending
/// canonical folder IDs are committed in this state before source creation;
/// unlock recovery removes any uncommitted registrations durably.
/// </summary>
public interface ISourceEnrollmentPersistence
{
    SourceEnrollmentState LoadSourceEnrollmentState();

    T UpdateSourceEnrollmentState<T>(
        Func<SourceEnrollmentState, (SourceEnrollmentState State, T Result)> update);

    T UpdateSourceEnrollmentStateWithRollback<T>(
        Func<SourceEnrollmentState, (SourceEnrollmentState State, T Result)> update,
        Action rollback) =>
        throw new NotSupportedException("Atomic source enrollment rollback is unavailable.");

    void BeginFolderRegistrationRecovery(IReadOnlyList<string> sourceIds) =>
        throw new NotSupportedException("Durable folder registration recovery is unavailable.");

    void CompleteFolderRegistrationRecovery() =>
        throw new NotSupportedException("Durable folder registration recovery is unavailable.");

    void RequireFolderRegistrationRecovery() =>
        throw new NotSupportedException("Durable folder registration recovery is unavailable.");
}

public enum SourceSnapshotStatus
{
    Snapshot = 1,
    Unreadable = 2,
    TooLarge = 3,
    Missing = 4
}

/// <summary>
/// Result of one bounded snapshot attempt for an enrolled locator. The
/// reader never returns partial content: <see cref="SourceSnapshotStatus.Unreadable"/>
/// and <see cref="SourceSnapshotStatus.TooLarge"/> carry no bytes at all, so
/// a truncated or unstable read can never be mistaken for a complete source.
/// </summary>
public sealed record SourceSnapshotResult(SourceSnapshotStatus Status, byte[]? Content = null);

/// <summary>
/// The single source-byte boundary: an exact enrolled locator in, a bounded
/// validated snapshot out. Implementations live in the Connections adapter
/// and follow the same safe-open rules as the managed-resource reader
/// (handle-bound path check, reparse refusal, write-share denial for the
/// read's lifetime, post-read revalidation). No directory is ever listed and
/// no path is ever derived from cwd or a request body.
/// </summary>
public interface ISourceSnapshotReader
{
    SourceSnapshotResult TryReadSnapshot(string locator);
    /// <summary>
    /// S3-T6 cancellable variant of the same exact-locator bounded read. The default
    /// implementation runs the synchronous read and then observes the token, which
    /// preserves the existing boundary for readers that cannot interrupt the OS open;
    /// the shipped file reader overrides this to poll the token while the snapshot
    /// bytes stream, so a requested lock/pause stops an actually active read instead
    /// of only refusing the next call. No directory is ever listed and no path is
    /// ever derived from cwd or a request body on either path.
    /// </summary>
    SourceSnapshotResult TryReadSnapshot(string locator, CancellationToken cancellationToken)
    {
        var result = TryReadSnapshot(locator);
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }
}

/// <summary>
/// S3-T1 owner allowlist authority. Enrollment is explicit and exact:
/// enroll/revoke one fixture at a time with an owner-chosen consistent
/// mapping, never a scan. The authority never touches grant state, so an
/// enrollment transition cannot widen or alter an approved snapshot (R11),
/// and it never reads source bytes — the coordinator supplies the anchor.
/// </summary>
public sealed class SourceEnrollmentAuthority
{
    public EnrolledSource Enroll(
        AuthenticatedContext actor,
        string sourceId,
        string zoneId,
        string locator,
        string format,
        int formatVersion,
        long anchorLength,
        string anchorDigest,
        ISourceEnrollmentPersistence persistence)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(persistence);
        EnsureOwner(actor);
        if (!DomainIdentifiers.IsDomainIdentifier(sourceId) ||
            sourceId.Length > SourceIngestionLimits.MaximumSourceIdLength)
        {
            throw new ArgumentException("The source ID must be an opaque safe identifier.");
        }

        if (!DomainIdentifiers.IsSafeIdentifier(zoneId))
        {
            throw new ArgumentException("The source zone must be an opaque safe identifier.");
        }

        var normalizedLocator = SourceEnrollmentGrammar.NormalizeLocator(locator);
        if (!SourceEnrollmentGrammar.IsFormat(format) || formatVersion < 1)
        {
            throw new ArgumentException("The source format and version must be declared explicitly.");
        }

        if (anchorLength < 0 || !SourceEnrollmentGrammar.IsAnchorDigest(anchorDigest))
        {
            throw new ArgumentException("The enrolled source anchor is invalid.");
        }

        var now = DateTimeOffset.UtcNow;
        return persistence.UpdateSourceEnrollmentState(state =>
        {
            var validated = ValidateAndNormalizeState(state);
            var folder = SourceFolderEnrollmentAuthority.FindFolderForSource(validated, sourceId);
            if (folder is not null && !SourceFolderEnrollmentGrammar.IsWithinRoot(folder.RootLocator, normalizedLocator))
            {
                throw new SourceOperationException("source_folder_path_outside_root");
            }
            foreach (var other in validated.Enrollments)
            {
                if (other.RevokedAtUtc is null &&
                    !string.Equals(other.SourceId, sourceId, StringComparison.Ordinal) &&
                    string.Equals(other.Locator, normalizedLocator, StringComparison.OrdinalIgnoreCase))
                {
                    throw new SourceOperationException("source_locator_in_use");
                }
            }

            var existing = Array.FindIndex(validated.Enrollments, candidate =>
                string.Equals(candidate.SourceId, sourceId, StringComparison.Ordinal));
            EnrolledSource next;
            if (existing < 0)
            {
                if (validated.Enrollments.Length >= SourceIngestionLimits.MaximumEnrollments)
                {
                    throw new IOException("The owner source enrollment limit is exhausted.");
                }

                next = new EnrolledSource(sourceId, zoneId, normalizedLocator, format, formatVersion,
                    1, anchorLength, anchorDigest, now, null);
            }
            else
            {
                var current = validated.Enrollments[existing];
                if (current.RevokedAtUtc is null)
                {
                    var sameMapping =
                        string.Equals(current.ZoneId, zoneId, StringComparison.Ordinal) &&
                        string.Equals(current.Locator, normalizedLocator, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(current.Format, format, StringComparison.Ordinal) &&
                        current.FormatVersion == formatVersion;
                    if (!sameMapping)
                    {
                        throw new SourceOperationException("source_enrollment_conflict");
                    }

                    if (current.AnchorLength == anchorLength &&
                        string.Equals(current.AnchorDigest, anchorDigest, StringComparison.Ordinal))
                    {
                        // Idempotent re-affirmation of the same fixture: no
                        // generation change, no state write.
                        return (state, current);
                    }

                    next = current with
                    {
                        Generation = current.Generation + 1,
                        AnchorLength = anchorLength,
                        AnchorDigest = anchorDigest,
                        EnrolledAtUtc = now,
                        RevokedAtUtc = null
                    };
                }
                else
                {
                    next = current with
                    {
                        ZoneId = zoneId,
                        Locator = normalizedLocator,
                        Format = format,
                        FormatVersion = formatVersion,
                        Generation = current.Generation + 1,
                        AnchorLength = anchorLength,
                        AnchorDigest = anchorDigest,
                        EnrolledAtUtc = now,
                        RevokedAtUtc = null
                    };
                }
            }

            var enrollments = validated.Enrollments
                .Where((_, index) => index != existing)
                .Append(next)
                .OrderBy(candidate => candidate.SourceId, StringComparer.Ordinal)
                .ToArray();
            // An explicit owner re-enrollment resolves the ambiguity the
            // quarantine recorded: the prior generation stays in its own
            // journal, and this generation starts on a fresh checkpoint.
            var quarantine = validated.Quarantine
                .Where(record => !string.Equals(record.SourceId, sourceId, StringComparison.Ordinal))
                .ToArray();
            return (state with { Enrollments = enrollments, Quarantine = quarantine }, next);
        });
    }
    /// <summary>
    /// S5-T7 owner mapping review: submits one source whose zone/format mapping
    /// the owner cannot yet resolve into the private quarantine without any
    /// transcript read and without guessing a zone. The submitted locator is
    /// grammar-checked only; no anchor is computed, no journal is created, and
    /// no enrollment is recorded, so nothing can be ingested from the held
    /// mapping. The held detail names the unresolved field (zone, format, or
    /// both) as a bounded diagnostic token. Only an explicit owner resolution
    /// (confirm the mapping via the normal enroll path, or revoke the held
    /// submission) clears the held record.
    /// </summary>
    public SourceQuarantineRecord SubmitMappingUnknown(
        AuthenticatedContext actor,
        string sourceId,
        string locator,
        string? zoneId,
        string? format,
        int? formatVersion,
        string detail,
        ISourceEnrollmentPersistence persistence)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(persistence);
        EnsureOwner(actor);
        if (!DomainIdentifiers.IsDomainIdentifier(sourceId) ||
            sourceId.Length > SourceIngestionLimits.MaximumSourceIdLength)
        {
            throw new ArgumentException("The source ID must be an opaque safe identifier.");
        }

        var normalizedLocator = SourceEnrollmentGrammar.NormalizeLocator(locator);
        if (!string.Equals(detail, "zone_unresolved", StringComparison.Ordinal) &&
            !string.Equals(detail, "format_unresolved", StringComparison.Ordinal) &&
            !string.Equals(detail, "mapping_unresolved", StringComparison.Ordinal))
        {
            throw new ArgumentException("The unresolved mapping detail must name the unresolved field.");
        }

        if (zoneId is not null && !DomainIdentifiers.IsSafeIdentifier(zoneId))
        {
            throw new ArgumentException("The submitted zone must be an opaque safe identifier when given.");
        }

        if (format is not null && !SourceEnrollmentGrammar.IsFormat(format))
        {
            throw new ArgumentException("The submitted format must be a valid format token when given.");
        }

        if (formatVersion is not null && formatVersion < 1)
        {
            throw new ArgumentException("The submitted format version must be at least 1 when given.");
        }

        // No default is ever applied: an unresolved zone stays unresolved, an
        // unresolved format stays unresolved, and the transcript is never
        // opened to guess either of them.
        var now = DateTimeOffset.UtcNow;
        return persistence.UpdateSourceEnrollmentState(state =>
        {
            var validated = ValidateAndNormalizeState(state);
            if (FindEnrollment(validated, sourceId) is not null)
            {
                throw new SourceOperationException("source_already_enrolled");
            }

            foreach (var other in validated.Enrollments)
            {
                if (other.RevokedAtUtc is null &&
                    string.Equals(other.Locator, normalizedLocator, StringComparison.OrdinalIgnoreCase))
                {
                    throw new SourceOperationException("source_locator_in_use");
                }
            }

            foreach (var other in validated.Quarantine)
            {
                if (!string.Equals(other.SourceId, sourceId, StringComparison.Ordinal) &&
                    string.Equals(other.Locator, normalizedLocator, StringComparison.OrdinalIgnoreCase))
                {
                    throw new SourceOperationException("source_locator_in_use");
                }
            }

            var existing = Array.FindIndex(validated.Quarantine, record =>
                string.Equals(record.SourceId, sourceId, StringComparison.Ordinal));
            if (existing >= 0 && validated.Quarantine[existing].Reason != SourceQuarantineReason.MappingUnknown)
            {
                throw new SourceOperationException("source_quarantined");
            }

            if (validated.Quarantine.Length >= SourceIngestionLimits.MaximumQuarantineRecords && existing < 0)
            {
                throw new IOException("The source quarantine limit is exhausted.");
            }

            var record = new SourceQuarantineRecord(sourceId, SourceQuarantineReason.MappingUnknown, detail, normalizedLocator, now);
            var quarantine = validated.Quarantine
                .Where((_, index) => index != existing)
                .Append(record)
                .OrderBy(candidate => candidate.SourceId, StringComparer.Ordinal)
                .ToArray();
            return (state with { Quarantine = quarantine }, record);
        });
    }

    /// <summary>
    /// S5-T7 owner mapping resolution: revokes one held mapping-unknown
    /// submission without enrolling it. The held locator is never opened and
    /// no zone is ever defaulted; the record is simply cleared so the source
    /// ID may be reused once the owner knows its mapping.
    /// </summary>
    public void RevokeMappingUnknown(
        AuthenticatedContext actor,
        string sourceId,
        ISourceEnrollmentPersistence persistence)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(persistence);
        EnsureOwner(actor);
        if (!DomainIdentifiers.IsDomainIdentifier(sourceId))
        {
            throw new ArgumentException("The source ID must be an opaque safe identifier.");
        }

        persistence.UpdateSourceEnrollmentState(state =>
        {
            var validated = ValidateAndNormalizeState(state);
            var index = Array.FindIndex(validated.Quarantine, record =>
                string.Equals(record.SourceId, sourceId, StringComparison.Ordinal));
            if (index < 0 || validated.Quarantine[index].Reason != SourceQuarantineReason.MappingUnknown)
            {
                throw new SourceOperationException("source_not_mapping_unknown");
            }

            var quarantine = validated.Quarantine
                .Where((_, position) => position != index)
                .ToArray();
            return (state with { Quarantine = quarantine }, true);
        });
    }

    /// <summary>
    /// Owner-confirmed relocation of the same enrolled generation. The
    /// coordinator has already read the exact caller-supplied locator and
    /// matched its full length and digest to the existing anchor; this method
    /// only commits that verified mapping and never searches for a candidate.
    /// </summary>
    public EnrolledSource Relocate(
        AuthenticatedContext actor,
        string sourceId,
        string expectedLocator,
        string locator,
        long expectedGeneration,
        ISourceEnrollmentPersistence persistence)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(persistence);
        EnsureOwner(actor);
        if (!DomainIdentifiers.IsDomainIdentifier(sourceId) ||
            expectedGeneration < 1 || !SourceEnrollmentGrammar.IsLocator(expectedLocator))
        {
            throw new ArgumentException("The source relocation identity is invalid.");
        }

        var normalizedLocator = SourceEnrollmentGrammar.NormalizeLocator(locator);
        var now = DateTimeOffset.UtcNow;
        return persistence.UpdateSourceEnrollmentState(state =>
        {
            var validated = ValidateAndNormalizeState(state);
            var folder = SourceFolderEnrollmentAuthority.FindFolderForSource(validated, sourceId);
            if (folder is not null && !SourceFolderEnrollmentGrammar.IsWithinRoot(folder.RootLocator, normalizedLocator))
            {
                throw new SourceOperationException("source_folder_path_outside_root");
            }
            var current = FindEnrollment(validated, sourceId)
                ?? throw new SourceOperationException("source_not_enrolled");
            if (current.RevokedAtUtc is not null)
            {
                throw new SourceOperationException("source_revoked");
            }

            if (current.Generation != expectedGeneration ||
                !string.Equals(current.Locator, expectedLocator, StringComparison.OrdinalIgnoreCase) ||
                FindQuarantine(validated, sourceId) is not null)
            {
                throw new SourceOperationException("source_relocation_conflict");
            }

            foreach (var other in validated.Enrollments)
            {
                if (other.RevokedAtUtc is null &&
                    !string.Equals(other.SourceId, sourceId, StringComparison.Ordinal) &&
                    string.Equals(other.Locator, normalizedLocator, StringComparison.OrdinalIgnoreCase))
                {
                    throw new SourceOperationException("source_locator_in_use");
                }
            }

            if (string.Equals(current.Locator, normalizedLocator, StringComparison.OrdinalIgnoreCase))
            {
                return (state, current);
            }

            var relocated = current with { Locator = normalizedLocator, RelocatedAtUtc = now };
            var enrollments = validated.Enrollments
                .Select(candidate => string.Equals(candidate.SourceId, sourceId, StringComparison.Ordinal)
                    ? relocated
                    : candidate)
                .OrderBy(candidate => candidate.SourceId, StringComparer.Ordinal)
                .ToArray();
            return (state with { Enrollments = enrollments }, relocated);
        });
    }

    public EnrolledSource Revoke(
        AuthenticatedContext actor,
        string sourceId,
        ISourceEnrollmentPersistence persistence)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(persistence);
        EnsureOwner(actor);
        if (!DomainIdentifiers.IsDomainIdentifier(sourceId))
        {
            throw new ArgumentException("The source ID must be an opaque safe identifier.");
        }

        return persistence.UpdateSourceEnrollmentState(state =>
        {
            var validated = ValidateAndNormalizeState(state);
            var index = Array.FindIndex(validated.Enrollments, candidate =>
                string.Equals(candidate.SourceId, sourceId, StringComparison.Ordinal));
            if (index < 0)
            {
                throw new SourceOperationException("source_not_enrolled");
            }

            var current = validated.Enrollments[index];
            if (current.RevokedAtUtc is not null)
            {
                throw new SourceOperationException("source_already_revoked");
            }

            var revoked = current with { RevokedAtUtc = DateTimeOffset.UtcNow };
            var enrollments = validated.Enrollments
                .Select((candidate, position) => position == index ? revoked : candidate)
                .ToArray();
            return (state with { Enrollments = enrollments }, revoked);
        });
    }
    /// <summary>
    /// S3-T6 owner ingestion pause: an explicit per-source stop that keeps the
    /// enrollment and its checkpoint but refuses status/ingest/refresh reads
    /// until the owner resumes. Pause never deletes state and never widens
    /// any grant; it only adds a fail-closed life gate checked before any
    /// source read.
    /// </summary>
    public EnrolledSource Pause(
        AuthenticatedContext actor,
        string sourceId,
        ISourceEnrollmentPersistence persistence)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(persistence);
        EnsureOwner(actor);
        if (!DomainIdentifiers.IsDomainIdentifier(sourceId))
        {
            throw new ArgumentException("The source ID must be an opaque safe identifier.");
        }

        return persistence.UpdateSourceEnrollmentState(state =>
        {
            var validated = ValidateAndNormalizeState(state);
            var index = Array.FindIndex(validated.Enrollments, candidate =>
                string.Equals(candidate.SourceId, sourceId, StringComparison.Ordinal));
            if (index < 0)
            {
                throw new SourceOperationException("source_not_enrolled");
            }

            var current = validated.Enrollments[index];
            if (current.RevokedAtUtc is not null)
            {
                throw new SourceOperationException("source_revoked");
            }

            if (FindQuarantine(validated, sourceId) is not null)
            {
                throw new SourceOperationException("source_quarantined");
            }

            if (current.PausedAtUtc is not null)
            {
                throw new SourceOperationException("source_already_paused");
            }

            var paused = current with { PausedAtUtc = DateTimeOffset.UtcNow };
            var enrollments = validated.Enrollments
                .Select((candidate, position) => position == index ? paused : candidate)
                .ToArray();
            return (state with { Enrollments = enrollments }, paused);
        });
    }

    /// <summary>
    /// S3-T6 owner ingestion resume: clears an explicit pause without
    /// touching the generation, anchor, checkpoint or refresh opt-in. Resume
    /// after unlock continues from the persisted checkpoint, so no duplicate
    /// events are produced.
    /// </summary>
    public EnrolledSource Resume(
        AuthenticatedContext actor,
        string sourceId,
        ISourceEnrollmentPersistence persistence)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(persistence);
        EnsureOwner(actor);
        if (!DomainIdentifiers.IsDomainIdentifier(sourceId))
        {
            throw new ArgumentException("The source ID must be an opaque safe identifier.");
        }

        return persistence.UpdateSourceEnrollmentState(state =>
        {
            var validated = ValidateAndNormalizeState(state);
            var index = Array.FindIndex(validated.Enrollments, candidate =>
                string.Equals(candidate.SourceId, sourceId, StringComparison.Ordinal));
            if (index < 0)
            {
                throw new SourceOperationException("source_not_enrolled");
            }

            var current = validated.Enrollments[index];
            if (current.RevokedAtUtc is not null)
            {
                throw new SourceOperationException("source_revoked");
            }

            if (current.PausedAtUtc is null)
            {
                throw new SourceOperationException("source_not_paused");
            }

            var resumed = current with { PausedAtUtc = null };
            var enrollments = validated.Enrollments
                .Select((candidate, position) => position == index ? resumed : candidate)
                .ToArray();
            return (state with { Enrollments = enrollments }, resumed);
        });
    }

    /// <summary>
    /// S3-T6 safe opt-in background refresh consent: incremental refresh runs
    /// only for sources the owner explicitly opted in, only while unlocked,
    /// and only from the exact enrolled locator. Opt-out is an explicit owner
    /// act with the same revision-safe encrypted state write.
    /// </summary>
    public EnrolledSource SetRefreshOptIn(
        AuthenticatedContext actor,
        string sourceId,
        bool optIn,
        ISourceEnrollmentPersistence persistence)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(persistence);
        EnsureOwner(actor);
        if (!DomainIdentifiers.IsDomainIdentifier(sourceId))
        {
            throw new ArgumentException("The source ID must be an opaque safe identifier.");
        }

        return persistence.UpdateSourceEnrollmentState(state =>
        {
            var validated = ValidateAndNormalizeState(state);
            var index = Array.FindIndex(validated.Enrollments, candidate =>
                string.Equals(candidate.SourceId, sourceId, StringComparison.Ordinal));
            if (index < 0)
            {
                throw new SourceOperationException("source_not_enrolled");
            }

            var current = validated.Enrollments[index];
            if (current.RevokedAtUtc is not null)
            {
                throw new SourceOperationException("source_revoked");
            }
            var updatedRefreshOptOut = !optIn;
            if (current.RefreshOptIn == optIn && current.RefreshOptOut == updatedRefreshOptOut)
            {
                return (state, current);
            }

            var updated = current with
            {
                RefreshOptIn = optIn,
                RefreshOptOut = updatedRefreshOptOut
            };
            var enrollments = validated.Enrollments
                .Select((candidate, position) => position == index ? updated : candidate)
                .ToArray();
            return (state with { Enrollments = enrollments }, updated);
        });
    }
    /// <summary>
    /// Raw preservation is a separate owner consent from parsing and automatic
    /// refresh. The retention deadline is persisted with the encrypted source
    /// enrollment and copied to each preserved record.
    /// </summary>
    public EnrolledSource SetRawPreservationOptIn(
        AuthenticatedContext actor,
        string sourceId,
        bool optIn,
        DateTimeOffset? retainUntilUtc,
        ISourceEnrollmentPersistence persistence)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(persistence);
        EnsureOwner(actor);
        if (!DomainIdentifiers.IsDomainIdentifier(sourceId))
        {
            throw new ArgumentException("The source ID must be an opaque safe identifier.");
        }

        if (optIn &&
            (retainUntilUtc is not { } retention ||
             retention.Offset != TimeSpan.Zero ||
             retention <= DateTimeOffset.UtcNow) ||
            !optIn && retainUntilUtc is not null)
        {
            throw new ArgumentException("Raw preservation requires an explicit future UTC retention time.");
        }

        return persistence.UpdateSourceEnrollmentState(state =>
        {
            var validated = ValidateAndNormalizeState(state);
            var index = Array.FindIndex(validated.Enrollments, candidate =>
                string.Equals(candidate.SourceId, sourceId, StringComparison.Ordinal));
            if (index < 0)
            {
                throw new SourceOperationException("source_not_enrolled");
            }

            var current = validated.Enrollments[index];
            if (current.RevokedAtUtc is not null)
            {
                throw new SourceOperationException("source_revoked");
            }

            if (current.RawPreservationOptIn == optIn &&
                current.RawPreservationRetainUntilUtc == retainUntilUtc)
            {
                return (state, current);
            }

            var updated = current with
            {
                RawPreservationOptIn = optIn,
                RawPreservationRetainUntilUtc = retainUntilUtc
            };
            var enrollments = validated.Enrollments
                .Select((candidate, position) => position == index ? updated : candidate)
                .ToArray();
            return (state with { Enrollments = enrollments }, updated);
        });
    }


    public SourceQuarantineRecord Quarantine(
        AuthenticatedContext actor,
        string sourceId,
        SourceQuarantineReason reason,
        string detail,
        string locator,
        ISourceEnrollmentPersistence persistence)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(persistence);
        EnsureOwner(actor);
        if (!DomainIdentifiers.IsDomainIdentifier(sourceId) || !Enum.IsDefined(reason) ||
            !SourceEnrollmentGrammar.IsDiagnosticCode(detail))
        {
            throw new ArgumentException("The source quarantine record is invalid.");
        }

        var normalizedLocator = SourceEnrollmentGrammar.NormalizeLocator(locator);
        var now = DateTimeOffset.UtcNow;
        return persistence.UpdateSourceEnrollmentState(state =>
        {
            var validated = ValidateAndNormalizeState(state);
            var existing = Array.FindIndex(validated.Quarantine, record =>
                string.Equals(record.SourceId, sourceId, StringComparison.Ordinal));
            if (validated.Quarantine.Length >= SourceIngestionLimits.MaximumQuarantineRecords)
            {
                throw new IOException("The source quarantine limit is exhausted.");
            }

            var record = new SourceQuarantineRecord(sourceId, reason, detail, normalizedLocator, now);
            var quarantine = validated.Quarantine
                .Where((_, index) => index != existing)
                .Append(record)
                .OrderBy(candidate => candidate.SourceId, StringComparer.Ordinal)
                .ToArray();
            return (state with { Quarantine = quarantine }, record);
        });
    }

    public IReadOnlyList<EnrolledSource> List(
        AuthenticatedContext actor,
        ISourceEnrollmentPersistence persistence)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(persistence);
        EnsureOwner(actor);
        var state = ValidateAndNormalizeState(persistence.LoadSourceEnrollmentState());
        return Array.AsReadOnly(state.Enrollments.Select(Clone).ToArray());
    }

    /// <summary>
    /// Fail-closed structural validation of the persisted enrollment state.
    /// An unknown schema version, a malformed record or an exceeded bound is
    /// an error, never a silent repair: a corrupt allowlist must stop
    /// ingestion rather than widen it.
    /// </summary>
    public static SourceEnrollmentState ValidateAndNormalizeState(SourceEnrollmentState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.SchemaVersion is not (SourceEnrollmentState.CurrentSchemaVersion or SourceEnrollmentState.PreviousSchemaVersion))
        {
            throw new InvalidDataException("The source enrollment state schema version is unsupported.");
        }

        if (state.Enrollments is null || state.Quarantine is null ||
            state.Enrollments.Length > SourceIngestionLimits.MaximumEnrollments ||
            state.Quarantine.Length > SourceIngestionLimits.MaximumQuarantineRecords ||
            state.PendingFolderSourceIds is { Length: > SourceIngestionLimits.MaximumEnrollments })
        {
            throw new InvalidDataException("The source enrollment state exceeds its bounds.");
        }

        if (state.SchemaVersion == SourceEnrollmentState.PreviousSchemaVersion &&
            state.PendingFolderSourceIds is { Length: > 0 })
        {
            throw new InvalidDataException("The legacy source enrollment state cannot contain a folder recovery journal.");
        }

        var sourceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var enrollment in state.Enrollments)
        {
            ValidateEnrollment(enrollment);
            if (!sourceIds.Add(enrollment.SourceId))
            {
                throw new InvalidDataException("The source enrollment state repeats a source ID.");
            }
        }

        var quarantined = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in state.Quarantine)
        {
            ArgumentNullException.ThrowIfNull(record);
            if (!DomainIdentifiers.IsDomainIdentifier(record.SourceId) || !Enum.IsDefined(record.Reason) ||
                !SourceEnrollmentGrammar.IsDiagnosticCode(record.Detail) ||
                !SourceEnrollmentGrammar.IsLocator(record.Locator) ||
                record.RecordedAtUtc.Offset != TimeSpan.Zero ||
                record.RecordedAtUtc > DateTimeOffset.UtcNow.AddMinutes(5) ||
                !quarantined.Add(record.SourceId))
            {
                throw new InvalidDataException("The source quarantine state is invalid.");
            }
        }

        var pending = state.PendingFolderSourceIds ?? Array.Empty<string>();
        if (pending.Length > 0)
        {
            var pendingIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var sourceId in pending)
            {
                if (!DomainIdentifiers.IsDomainIdentifier(sourceId) ||
                    sourceId.Length > SourceIngestionLimits.MaximumSourceIdLength ||
                    !pendingIds.Add(sourceId))
                {
                    throw new InvalidDataException("The folder registration recovery journal is invalid.");
                }
            }
        }

        var normalized = state;
        if (state.SchemaVersion != SourceEnrollmentState.CurrentSchemaVersion ||
            state.Folders is null ||
            state.PendingFolderSourceIds is null)
        {
            normalized = state with
            {
                SchemaVersion = SourceEnrollmentState.CurrentSchemaVersion,
                Folders = state.Folders ?? Array.Empty<SourceFolderEnrollment>(),
                PendingFolderSourceIds = pending
            };
        }

        SourceFolderEnrollmentAuthority.ValidateState(normalized);
        return normalized;
    }

    public static EnrolledSource? FindEnrollment(SourceEnrollmentState state, string sourceId) =>
        Array.Find(state.Enrollments, candidate =>
            string.Equals(candidate.SourceId, sourceId, StringComparison.Ordinal));

    public static SourceQuarantineRecord? FindQuarantine(SourceEnrollmentState state, string sourceId) =>
        Array.Find(state.Quarantine, record =>
            string.Equals(record.SourceId, sourceId, StringComparison.Ordinal));

    private static void ValidateEnrollment(EnrolledSource enrollment)
    {
        ArgumentNullException.ThrowIfNull(enrollment);
        if (!DomainIdentifiers.IsDomainIdentifier(enrollment.SourceId) ||
            enrollment.SourceId.Length > SourceIngestionLimits.MaximumSourceIdLength ||
            !DomainIdentifiers.IsSafeIdentifier(enrollment.ZoneId) ||
            !SourceEnrollmentGrammar.IsLocator(enrollment.Locator) ||
            !SourceEnrollmentGrammar.IsFormat(enrollment.Format) ||
            enrollment.FormatVersion < 1 ||
            enrollment.Generation < 1 ||
            enrollment.AnchorLength < 0 ||
            !SourceEnrollmentGrammar.IsAnchorDigest(enrollment.AnchorDigest) ||
            enrollment.EnrolledAtUtc.Offset != TimeSpan.Zero ||
            enrollment.EnrolledAtUtc > DateTimeOffset.UtcNow.AddMinutes(5) ||
            enrollment.RawPreservationOptIn != (enrollment.RawPreservationRetainUntilUtc is not null) ||
            enrollment.RawPreservationRetainUntilUtc is { Offset: var retentionOffset } &&
            retentionOffset != TimeSpan.Zero)
        {
            throw new InvalidDataException("The source enrollment state is invalid.");
        }

        if (enrollment.RevokedAtUtc is { } revokedAtUtc)
        {
            if (revokedAtUtc.Offset != TimeSpan.Zero || revokedAtUtc < enrollment.EnrolledAtUtc)
            {
                throw new InvalidDataException("The source enrollment revocation time is invalid.");
            }
        }
        if (enrollment.RelocatedAtUtc is { } relocatedAtUtc &&
            (relocatedAtUtc.Offset != TimeSpan.Zero || relocatedAtUtc < enrollment.EnrolledAtUtc))
        {
            throw new InvalidDataException("The source enrollment relocation time is invalid.");
        }
        if (enrollment.PausedAtUtc is { } pausedAtUtc &&
            (pausedAtUtc.Offset != TimeSpan.Zero || pausedAtUtc < enrollment.EnrolledAtUtc))
        {
            throw new InvalidDataException("The source enrollment pause time is invalid.");
        }
    }

    private static EnrolledSource Clone(EnrolledSource enrollment) => enrollment with { };

    private static void EnsureOwner(AuthenticatedContext actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (actor.Kind != PrincipalKind.Owner)
        {
            throw new UnauthorizedAccessException("Only the owner may manage source enrollment.");
        }
    }
}
