using System.Security.Cryptography;
using System.Text;
using MetaBrain.Core.Domain;

namespace MetaBrain.Core.Security;

/// <summary>Owner-declared selection for one source-folder enrollment.</summary>
public sealed record SourceFolderEnrollmentDraft(
    string RootLocator,
    string ZoneId,
    string Format,
    int FormatVersion,
    bool Recursive,
    string[] IncludePatterns,
    string[] ExcludePatterns,
    DateTimeOffset? RawPreservationRetainUntilUtc = null);

/// <summary>A filesystem entry observed without reading its contents.</summary>
public sealed record SourceFolderEntry(
    string RelativePath,
    string Locator,
    bool Included,
    string? ExclusionReason);

/// <summary>One bounded, deterministic directory enumeration for preview.</summary>
public sealed record SourceFolderEnumeration(string RootLocator, SourceFolderEntry[] Entries);

/// <summary>
/// Filesystem boundary for folder selection. Implementations must not follow
/// reparse points and must not read file contents.
/// </summary>
public interface ISourceFolderEnumerator
{
    SourceFolderEnumeration Enumerate(
        string rootLocator,
        bool recursive,
        string[] includePatterns,
        string[] excludePatterns,
        CancellationToken cancellationToken);
}

/// <summary>
/// Encrypted folder membership index. Member IDs reference the canonical source
/// records and per-file enrollment anchors; this index stores selection rules
/// and member IDs, not another copy of each member locator.
/// </summary>
public sealed record SourceFolderEnrollment(
    string FolderId,
    string RootLocator,
    string ZoneId,
    string Format,
    int FormatVersion,
    bool Recursive,
    string[] IncludePatterns,
    string[] ExcludePatterns,
    string[] MemberSourceIds,
    DateTimeOffset EnrolledAtUtc,
    DateTimeOffset? RawPreservationRetainUntilUtc = null);

/// <summary>Anchor captured for one selected file after explicit confirmation.</summary>
public sealed record SourceFolderMemberAnchor(
    string SourceId,
    string RelativePath,
    string Locator,
    long AnchorLength,
    string AnchorDigest);

public sealed record SourceFolderEnrollmentResult(
    SourceFolderEnrollment Folder,
    EnrolledSource[] Members,
    bool AlreadyEnrolled);

public static class SourceFolderEnrollmentGrammar
{
    public const int MaximumFolders = 32;
    public const int MaximumPatterns = 16;
    public const int MaximumPatternLength = 256;
    public const int MaximumPreviewEntries = 512;

    public static string NormalizeRoot(string rootLocator)
    {
        var fullPath = SourceEnrollmentGrammar.NormalizeLocator(rootLocator);
        return Path.TrimEndingDirectorySeparator(fullPath);
    }

    public static string[] NormalizePatterns(string[]? patterns, bool required)
    {
        if (patterns is null || patterns.Length == 0)
        {
            if (required)
            {
                throw new ArgumentException("At least one include pattern must be explicit.");
            }

            return Array.Empty<string>();
        }

        if (patterns.Length > MaximumPatterns)
        {
            throw new ArgumentException("The folder selection has too many glob patterns.");
        }

        var normalized = new string[patterns.Length];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < patterns.Length; index++)
        {
            var pattern = patterns[index]?.Replace('\\', '/') ?? string.Empty;
            if (!IsPattern(pattern) || !seen.Add(pattern))
            {
                throw new ArgumentException("Folder patterns must be unique relative globs without parent traversal.");
            }

            normalized[index] = pattern;
        }

        Array.Sort(normalized, StringComparer.OrdinalIgnoreCase);
        return normalized;
    }

    public static bool IsPattern(string? pattern)
    {
        if (string.IsNullOrEmpty(pattern) || pattern.Length > MaximumPatternLength ||
            pattern[0] == '/' || pattern.Contains(':') ||
            pattern.Any(char.IsControl))
        {
            return false;
        }

        var segments = pattern.Split('/');
        if (segments.Any(segment => segment.Length == 0 || segment is "." or ".."))
        {
            return false;
        }

        return true;
    }

    public static string FolderIdFor(string normalizedRoot)
    {
        var material = "folder|" + normalizedRoot.ToUpperInvariant();
        return "folder-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
    }

    public static string MemberSourceIdFor(string normalizedRoot, string relativePath)
    {
        var material = "folder-member|" + normalizedRoot.ToUpperInvariant() + "|" + relativePath.Replace('\\', '/').ToUpperInvariant();
        return "folder-source-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
    }

    public static bool IsWithinRoot(string normalizedRoot, string locator)
    {
        var fullPath = Path.GetFullPath(locator);
        if (string.Equals(fullPath, normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var prefix = Path.EndsInDirectorySeparator(normalizedRoot)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    public static string NormalizeRelativePath(string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath) || Path.IsPathRooted(relativePath))
        {
            throw new ArgumentException("A folder member must have a relative path.");
        }

        var normalized = relativePath.Replace('\\', '/');
        if (normalized.Split('/').Any(segment => segment.Length == 0 || segment is "." or "..") ||
            normalized.Any(char.IsControl))
        {
            throw new ArgumentException("A folder member path is invalid.");
        }

        return normalized;
    }

    public static SourceFolderEnrollmentDraft NormalizeDraft(SourceFolderEnrollmentDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var root = NormalizeRoot(draft.RootLocator);
        if (!DomainIdentifiers.IsSafeIdentifier(draft.ZoneId) ||
            !SourceEnrollmentGrammar.IsFormat(draft.Format) || draft.FormatVersion < 1 ||
            draft.RawPreservationRetainUntilUtc is { Offset: var retentionOffset } &&
            retentionOffset != TimeSpan.Zero)
        {
            throw new ArgumentException("The folder source mapping or preservation retention is invalid.");
        }

        return draft with
        {
            RootLocator = root,
            IncludePatterns = NormalizePatterns(draft.IncludePatterns, required: true),
            ExcludePatterns = NormalizePatterns(draft.ExcludePatterns, required: false)
        };
    }
}

/// <summary>
/// Applies confirmed membership to the existing encrypted enrollment authority
/// in one state update. The application coordinator writes canonical member
/// metadata through the existing registry and supplies compensation for any
/// new records if registration or the encrypted index commit fails.
/// </summary>
public static class SourceFolderEnrollmentAuthority
{
    public static SourceFolderEnrollmentResult Enroll(
        AuthenticatedContext actor,
        SourceFolderEnrollmentDraft draft,
        IReadOnlyList<SourceFolderMemberAnchor> members,
        ISourceEnrollmentPersistence persistence,
        Func<Action> registerMemberSources)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(persistence);
        ArgumentNullException.ThrowIfNull(registerMemberSources);
        if (actor.Kind != PrincipalKind.Owner)
        {
            throw new UnauthorizedAccessException("Only the owner may manage source-folder enrollment.");
        }

        var normalizedDraft = SourceFolderEnrollmentGrammar.NormalizeDraft(draft);
        if (normalizedDraft.RawPreservationRetainUntilUtc is { } retention && retention <= DateTimeOffset.UtcNow)
        {
            throw new ArgumentException("Folder raw preservation requires a future UTC retention time.");
        }
        if (members.Count == 0 || members.Count > SourceIngestionLimits.MaximumEnrollments)
        {
            throw new SourceOperationException("source_folder_empty_or_too_large");
        }

        var normalizedMembers = new SourceFolderMemberAnchor[members.Count];
        var sourceIds = new HashSet<string>(StringComparer.Ordinal);
        var locators = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < members.Count; index++)
        {
            var member = members[index] ?? throw new ArgumentException("A folder member is required.");
            var relativePath = SourceFolderEnrollmentGrammar.NormalizeRelativePath(member.RelativePath);
            var locator = SourceEnrollmentGrammar.NormalizeLocator(member.Locator);
            var expectedLocator = Path.GetFullPath(Path.Combine(normalizedDraft.RootLocator, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            var sourceId = SourceFolderEnrollmentGrammar.MemberSourceIdFor(normalizedDraft.RootLocator, relativePath);
            if (!string.Equals(locator, expectedLocator, StringComparison.OrdinalIgnoreCase) ||
                !SourceFolderEnrollmentGrammar.IsWithinRoot(normalizedDraft.RootLocator, locator) ||
                !string.Equals(member.SourceId, sourceId, StringComparison.Ordinal) ||
                !sourceIds.Add(member.SourceId) || !locators.Add(locator) ||
                member.AnchorLength is < 1 or > SourceIngestionLimits.MaximumSourceBytes ||
                !SourceEnrollmentGrammar.IsAnchorDigest(member.AnchorDigest))
            {
                throw new ArgumentException("A folder member anchor is invalid.");
            }

            normalizedMembers[index] = member with
            {
                RelativePath = relativePath,
                Locator = locator
            };
        }

        Array.Sort(normalizedMembers, static (left, right) => StringComparer.Ordinal.Compare(left.SourceId, right.SourceId));
        var folderId = SourceFolderEnrollmentGrammar.FolderIdFor(normalizedDraft.RootLocator);
        var now = DateTimeOffset.UtcNow;

        Action? rollbackMemberSources = null;
        return persistence.UpdateSourceEnrollmentStateWithRollback(state =>
        {
            var validated = SourceEnrollmentAuthority.ValidateAndNormalizeState(state);
            if (validated.PendingFolderSourceIds is { Length: > 0 })
            {
                throw new SourceOperationException("source_folder_recovery_pending");
            }

            var existingFolder = FindFolder(validated, folderId);
            if (existingFolder is not null)
            {
                if (Matches(existingFolder, normalizedDraft, normalizedMembers))
                {
                    var existingMembers = normalizedMembers.Select(member =>
                            SourceEnrollmentAuthority.FindEnrollment(validated, member.SourceId) ??
                            throw new InvalidDataException("The folder membership references a missing enrollment."))
                        .ToArray();
                    return (state, new SourceFolderEnrollmentResult(existingFolder, existingMembers, AlreadyEnrolled: true));
                }

                throw new SourceOperationException("source_folder_membership_changed");
            }

            if (validated.Folders!.Length >= SourceFolderEnrollmentGrammar.MaximumFolders ||
                validated.Enrollments.Length + normalizedMembers.Count(member =>
                    SourceEnrollmentAuthority.FindEnrollment(validated, member.SourceId) is null) > SourceIngestionLimits.MaximumEnrollments)
            {
                throw new SourceOperationException("source_folder_limit");
            }

            var nextEnrollments = validated.Enrollments.ToDictionary(enrollment => enrollment.SourceId, StringComparer.Ordinal);
            foreach (var member in normalizedMembers)
            {
                if (FindFolderForSource(validated, member.SourceId) is not null)
                {
                    throw new SourceOperationException("source_folder_member_conflict");
                }

                var current = SourceEnrollmentAuthority.FindEnrollment(validated, member.SourceId);
                foreach (var other in validated.Enrollments)
                {
                    if (other.RevokedAtUtc is null &&
                        !string.Equals(other.SourceId, member.SourceId, StringComparison.Ordinal) &&
                        string.Equals(other.Locator, member.Locator, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new SourceOperationException("source_locator_in_use");
                    }
                }

                if (current is not null && current.RevokedAtUtc is null)
                {
                    var sameMapping =
                        string.Equals(current.ZoneId, normalizedDraft.ZoneId, StringComparison.Ordinal) &&
                        string.Equals(current.Locator, member.Locator, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(current.Format, normalizedDraft.Format, StringComparison.Ordinal) &&
                        current.FormatVersion == normalizedDraft.FormatVersion;
                    if (!sameMapping || current.AnchorLength != member.AnchorLength ||
                        !string.Equals(current.AnchorDigest, member.AnchorDigest, StringComparison.Ordinal))
                    {
                        throw new SourceOperationException("source_enrollment_conflict");
                    }

                    if (normalizedDraft.RawPreservationRetainUntilUtc is { } retention)
                    {
                        nextEnrollments[member.SourceId] = current with
                        {
                            RawPreservationOptIn = true,
                            RawPreservationRetainUntilUtc = retention
                        };
                    }

                    continue;
                }

                var next = new EnrolledSource(
                    member.SourceId,
                    normalizedDraft.ZoneId,
                    member.Locator,
                    normalizedDraft.Format,
                    normalizedDraft.FormatVersion,
                    current is null ? 1 : current.Generation + 1,
                    member.AnchorLength,
                    member.AnchorDigest,
                    now,
                    null,
                    RawPreservationOptIn: normalizedDraft.RawPreservationRetainUntilUtc is not null,
                    RawPreservationRetainUntilUtc: normalizedDraft.RawPreservationRetainUntilUtc);
                nextEnrollments[member.SourceId] = next;
            }

            var folder = new SourceFolderEnrollment(
                folderId,
                normalizedDraft.RootLocator,
                normalizedDraft.ZoneId,
                normalizedDraft.Format,
                normalizedDraft.FormatVersion,
                normalizedDraft.Recursive,
                normalizedDraft.IncludePatterns,
                normalizedDraft.ExcludePatterns,
                normalizedMembers.Select(member => member.SourceId).ToArray(),
                now,
                normalizedDraft.RawPreservationRetainUntilUtc);
            var nextState = validated with
            {
                Enrollments = nextEnrollments.Values.OrderBy(enrollment => enrollment.SourceId, StringComparer.Ordinal).ToArray(),
                Quarantine = validated.Quarantine.Where(record => !sourceIds.Contains(record.SourceId)).ToArray(),
                Folders = validated.Folders.Append(folder).OrderBy(item => item.FolderId, StringComparer.Ordinal).ToArray(),
                PendingFolderSourceIds = Array.Empty<string>()
            };
            var enrolledMembers = normalizedMembers.Select(member => nextEnrollments[member.SourceId]).ToArray();
            // Retain compensation for new canonical records until the encrypted index commits.
            rollbackMemberSources = registerMemberSources();
            return (nextState, new SourceFolderEnrollmentResult(folder, enrolledMembers, AlreadyEnrolled: false));
        }, () => rollbackMemberSources?.Invoke());
    }

    public static SourceFolderEnrollment? FindFolder(SourceEnrollmentState state, string folderId) =>
        Array.Find(state.Folders ?? Array.Empty<SourceFolderEnrollment>(), folder =>
            string.Equals(folder.FolderId, folderId, StringComparison.Ordinal));

    public static SourceFolderEnrollment? FindFolderForSource(SourceEnrollmentState state, string sourceId) =>
        (state.Folders ?? Array.Empty<SourceFolderEnrollment>())
        .FirstOrDefault(folder => folder.MemberSourceIds.Contains(sourceId, StringComparer.Ordinal));

    public static void ValidateState(SourceEnrollmentState state)
    {
        var folders = state.Folders ?? Array.Empty<SourceFolderEnrollment>();
        if (folders.Length > SourceFolderEnrollmentGrammar.MaximumFolders)
        {
            throw new InvalidDataException("The source-folder enrollment state exceeds its bounds.");
        }

        var folderIds = new HashSet<string>(StringComparer.Ordinal);
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var members = new HashSet<string>(StringComparer.Ordinal);
        foreach (var folder in folders)
        {
            ArgumentNullException.ThrowIfNull(folder);
            SourceFolderEnrollmentDraft draft;
            try
            {
                draft = SourceFolderEnrollmentGrammar.NormalizeDraft(new SourceFolderEnrollmentDraft(
                    folder.RootLocator, folder.ZoneId, folder.Format, folder.FormatVersion,
                    folder.Recursive, folder.IncludePatterns, folder.ExcludePatterns,
                    folder.RawPreservationRetainUntilUtc));
            }
            catch (ArgumentException ex)
            {
                throw new InvalidDataException("The source-folder enrollment state is invalid.", ex);
            }

            if (!string.Equals(folder.FolderId, SourceFolderEnrollmentGrammar.FolderIdFor(draft.RootLocator), StringComparison.Ordinal) ||
                !string.Equals(folder.RootLocator, draft.RootLocator, StringComparison.Ordinal) ||
                !folder.IncludePatterns.SequenceEqual(draft.IncludePatterns, StringComparer.Ordinal) ||
                !folder.ExcludePatterns.SequenceEqual(draft.ExcludePatterns, StringComparer.Ordinal) ||
                !folderIds.Add(folder.FolderId) || !roots.Add(folder.RootLocator) ||
                folder.MemberSourceIds is null || folder.MemberSourceIds.Length is < 1 or > SourceIngestionLimits.MaximumEnrollments ||
                folder.EnrolledAtUtc.Offset != TimeSpan.Zero || folder.EnrolledAtUtc > DateTimeOffset.UtcNow.AddMinutes(5))
            {
                throw new InvalidDataException("The source-folder enrollment state is invalid.");
            }

            foreach (var sourceId in folder.MemberSourceIds)
            {
                var enrollment = SourceEnrollmentAuthority.FindEnrollment(state, sourceId);
                if (!DomainIdentifiers.IsDomainIdentifier(sourceId) || !members.Add(sourceId) || enrollment is null ||
                    !string.Equals(enrollment.ZoneId, folder.ZoneId, StringComparison.Ordinal) ||
                    !string.Equals(enrollment.Format, folder.Format, StringComparison.Ordinal) ||
                    enrollment.FormatVersion != folder.FormatVersion ||
                    !SourceFolderEnrollmentGrammar.IsWithinRoot(folder.RootLocator, enrollment.Locator))
                {
                    throw new InvalidDataException("The source-folder membership index is invalid.");
                }
            }
        }
    }

    private static bool Matches(
        SourceFolderEnrollment existing,
        SourceFolderEnrollmentDraft draft,
        IReadOnlyList<SourceFolderMemberAnchor> members) =>
        string.Equals(existing.RootLocator, draft.RootLocator, StringComparison.Ordinal) &&
        string.Equals(existing.ZoneId, draft.ZoneId, StringComparison.Ordinal) &&
        string.Equals(existing.Format, draft.Format, StringComparison.Ordinal) &&
        existing.FormatVersion == draft.FormatVersion &&
        existing.Recursive == draft.Recursive &&
        existing.IncludePatterns.SequenceEqual(draft.IncludePatterns, StringComparer.Ordinal) &&
        existing.ExcludePatterns.SequenceEqual(draft.ExcludePatterns, StringComparer.Ordinal) &&
        existing.RawPreservationRetainUntilUtc == draft.RawPreservationRetainUntilUtc &&
        existing.MemberSourceIds.SequenceEqual(members.Select(member => member.SourceId), StringComparer.Ordinal);
}
