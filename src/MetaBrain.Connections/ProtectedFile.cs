using System.Security.AccessControl;
using System.Security.Principal;

namespace MetaBrain.Connections;

internal sealed record PrivateFileContent(string Text, string OwnerSid);

internal static class ProtectedFile
{
    private static readonly HashSet<string> AdditionalTrustedSids = new(StringComparer.Ordinal)
    {
        "S-1-5-18", // LocalSystem
        "S-1-5-32-544" // Local Administrators; owner/admin are outside this boundary's threat model.
    };

    public static PrivateFileContent ReadPrivateFile(string path, string serviceSid, long maximumLength)
    {
        var ownerSid = AssertPrivateFileAccess(path, serviceSid);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length <= 0 || stream.Length > maximumLength)
        {
            throw new InvalidDataException("Invalid service settings size.");
        }

        using var reader = new StreamReader(stream, new System.Text.UTF8Encoding(false, true));
        return new PrivateFileContent(reader.ReadToEnd(), ownerSid);
    }

    /// <summary>
    /// Same restricted-ACL boundary as <see cref="ReadPrivateFile"/>, returning
    /// the raw file bytes instead of a decoded string. Reading a private file as
    /// bytes matters at size: a multi-megabyte manifest read as text first
    /// materializes a UTF-16 string twice the file size (plus the reader's
    /// growth buffer) before the JSON is even parsed, which is allocation the
    /// unlocked process never gets back. The bytes feed the JSON reader
    /// directly, so no intermediate text exists at all.
    /// </summary>
    public static byte[] ReadPrivateBytes(string path, string serviceSid, long maximumLength)
    {
        AssertPrivateFileAccess(path, serviceSid);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length <= 0 || stream.Length > maximumLength)
        {
            throw new InvalidDataException("Invalid service settings size.");
        }

        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    /// <summary>
    /// The one ACL boundary every private read goes through: the file and its
    /// directory must exist as regular (non-reparse) paths, both must carry a
    /// protected ACL, and only the owner/service principals plus the file's own
    /// owner may read. Returns the file owner SID for callers that report it.
    /// </summary>
    private static string AssertPrivateFileAccess(string path, string serviceSid)
    {
        var file = new FileInfo(path);
        if (!file.Exists || (file.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
        {
            throw new InvalidDataException("The service settings file must be a regular file.");
        }

        var directory = file.Directory ?? throw new InvalidDataException("Invalid service settings directory.");
        if (!directory.Exists || (directory.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("The service settings directory must not be a reparse point.");
        }

        var fileSecurity = FileSystemAclExtensions.GetAccessControl(file, AccessControlSections.Access | AccessControlSections.Owner);
        var fileOwner = fileSecurity.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier
            ?? throw new InvalidDataException("The service settings file has no owner.");
        var trusted = new HashSet<string>(AdditionalTrustedSids, StringComparer.Ordinal)
        {
            new SecurityIdentifier(serviceSid).Value,
            fileOwner.Value
        };
        AssertRestrictedAcl(fileSecurity, trusted);

        var directorySecurity = FileSystemAclExtensions.GetAccessControl(directory, AccessControlSections.Access | AccessControlSections.Owner);
        var directoryOwner = directorySecurity.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier
            ?? throw new InvalidDataException("The service settings directory has no owner.");
        var directoryTrusted = new HashSet<string>(trusted, StringComparer.Ordinal) { directoryOwner.Value };
        AssertRestrictedAcl(directorySecurity, directoryTrusted);
        return fileOwner.Value;
    }

    private static void AssertRestrictedAcl(FileSystemSecurity security, HashSet<string> trustedSids)
    {
        if (!security.AreAccessRulesProtected)
        {
            throw new UnauthorizedAccessException("Service credentials are not protected by an explicit ACL.");
        }

        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, targetType: typeof(SecurityIdentifier));
        foreach (AuthorizationRule rule in rules)
        {
            if (rule is not FileSystemAccessRule accessRule || accessRule.AccessControlType != AccessControlType.Allow)
            {
                continue;
            }

            var sid = ((SecurityIdentifier)accessRule.IdentityReference).Value;
            if (!trustedSids.Contains(sid))
            {
                throw new UnauthorizedAccessException("Service settings grant read access outside owner and service principals.");
            }
        }
    }
}
