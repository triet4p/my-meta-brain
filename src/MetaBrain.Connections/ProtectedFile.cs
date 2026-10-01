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
        var file = new FileInfo(path);
        var fileSecurity = FileSystemAclExtensions.GetAccessControl(file, AccessControlSections.Access | AccessControlSections.Owner);
        var fileOwner = fileSecurity.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier
            ?? throw new InvalidDataException("The service settings file has no owner.");
        var trusted = new HashSet<string>(AdditionalTrustedSids, StringComparer.Ordinal)
        {
            new SecurityIdentifier(serviceSid).Value,
            fileOwner.Value
        };
        AssertRestrictedAcl(fileSecurity, trusted);

        var directory = file.Directory ?? throw new InvalidDataException("Invalid service settings directory.");
        var directorySecurity = FileSystemAclExtensions.GetAccessControl(directory, AccessControlSections.Access | AccessControlSections.Owner);
        var directoryOwner = directorySecurity.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier
            ?? throw new InvalidDataException("The service settings directory has no owner.");
        var directoryTrusted = new HashSet<string>(trusted, StringComparer.Ordinal) { directoryOwner.Value };
        AssertRestrictedAcl(directorySecurity, directoryTrusted);

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length <= 0 || stream.Length > maximumLength)
        {
            throw new InvalidDataException("Invalid service settings size.");
        }

        using var reader = new StreamReader(stream, new System.Text.UTF8Encoding(false, true));
        return new PrivateFileContent(reader.ReadToEnd(), fileOwner.Value);
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
