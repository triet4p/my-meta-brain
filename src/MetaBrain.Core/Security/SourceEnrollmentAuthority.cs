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
/// generation instead of reviving the old one.
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
    DateTimeOffset? RevokedAtUtc);

public enum SourceQuarantineReason
{
    MixedLocator = 1,
    GenerationTruncated = 2,
    GenerationReplaced = 3
}

/// <summary>
/// Encrypted quarantine record for a source whose identity or mapping became
/// ambiguous (R16): the locator is claimed by another enrollment, or the
/// fixture's bytes no longer match the enrolled generation. Quarantine is
/// private state, it is checked before any source read, and only an explicit
/// owner re-enrollment or revocation clears it.
/// </summary>
public sealed record SourceQuarantineRecord(
    string SourceId,
    SourceQuarantineReason Reason,
    string Detail,
    string Locator,
    DateTimeOffset RecordedAtUtc);

/// <summary>
/// The owner allowlist plus quarantine, persisted as one encrypted private
/// state file beside the scope-grant and catalog states (never as an
/// approved-catalog entry). It carries only enrollment metadata: locators,
/// mappings, generations, anchors and quarantine reasons.
/// </summary>
public sealed record SourceEnrollmentState(
    int SchemaVersion,
    EnrolledSource[] Enrollments,
    SourceQuarantineRecord[] Quarantine)
{
    public const int CurrentSchemaVersion = 1;

    public static SourceEnrollmentState Empty { get; } = new(
        CurrentSchemaVersion,
        Array.Empty<EnrolledSource>(),
        Array.Empty<SourceQuarantineRecord>());
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

    public string Code { get; }
}

/// <summary>
/// Encrypted persistence for the owner enrollment state, implemented by the
/// vault lifecycle so enrollment transitions share the same atomic update
/// and revision semantics as scope grants and the owner catalog.
/// </summary>
public interface ISourceEnrollmentPersistence
{
    SourceEnrollmentState LoadSourceEnrollmentState();

    T UpdateSourceEnrollmentState<T>(
        Func<SourceEnrollmentState, (SourceEnrollmentState State, T Result)> update);
}

public enum SourceSnapshotStatus
{
    Snapshot = 1,
    Unreadable = 2,
    TooLarge = 3
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

        if (anchorLength < 1 || !SourceEnrollmentGrammar.IsAnchorDigest(anchorDigest))
        {
            throw new ArgumentException("The enrolled source anchor is invalid.");
        }

        var now = DateTimeOffset.UtcNow;
        return persistence.UpdateSourceEnrollmentState(state =>
        {
            var validated = ValidateAndNormalizeState(state);
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
        if (state.SchemaVersion != SourceEnrollmentState.CurrentSchemaVersion)
        {
            throw new InvalidDataException("The source enrollment state schema version is unsupported.");
        }

        if (state.Enrollments is null || state.Quarantine is null ||
            state.Enrollments.Length > SourceIngestionLimits.MaximumEnrollments ||
            state.Quarantine.Length > SourceIngestionLimits.MaximumQuarantineRecords)
        {
            throw new InvalidDataException("The source enrollment state exceeds its bounds.");
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

        return state;
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
            enrollment.AnchorLength < 1 ||
            !SourceEnrollmentGrammar.IsAnchorDigest(enrollment.AnchorDigest) ||
            enrollment.EnrolledAtUtc.Offset != TimeSpan.Zero ||
            enrollment.EnrolledAtUtc > DateTimeOffset.UtcNow.AddMinutes(5))
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
