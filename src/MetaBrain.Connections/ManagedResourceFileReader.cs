using System.Security.AccessControl;
using System.Security.Principal;
using MetaBrain.Core.Security;

namespace MetaBrain.Connections;

/// <summary>
/// Service-owned file reader for one configured managed-resource root.
/// The mapping is ID → plain file name; request IDs never contribute path text.
/// Each read opens the file handle first (before consulting the grant
/// authority), then the caller re-checks authorization with the current policy
/// generation and validates the still-registered mapping. No content, handle,
/// or byte cache survives the read call, so revocation is honored by the next
/// request even when an earlier handle or read is still outstanding.
/// </summary>
internal sealed class ManagedResourceFileReader : IManagedResourceReader
{
    internal const long MaximumResourceBytes = 1024 * 1024;

    private readonly object _gate = new();
    private readonly string _root;
    private readonly SecurityIdentifier _serviceSid;
    private readonly Dictionary<string, string> _fileNamesByResource = new(StringComparer.Ordinal);

    public ManagedResourceFileReader(string root, string serviceSid)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Managed resource reads are Windows-only.");
        }

        _root = Path.GetFullPath(root);
        _serviceSid = new SecurityIdentifier(serviceSid);
        var directory = new DirectoryInfo(_root);
        if (!directory.Exists || (directory.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("The managed resource root must be an existing non-reparse directory.");
        }
    }

    /// <summary>
    /// Registers an opaque ID mapping to a plain file name inside the
    /// configured root. Called once at startup from owner service settings;
    /// intentionally not request-reachable.
    /// </summary>
    public void Register(string resourceId, string zoneId, string fileName)
    {
        if (!IsSafeIdentifier(resourceId) || !IsSafeIdentifier(zoneId))
        {
            throw new ArgumentException("Managed resource mappings require valid identifiers.");
        }

        if (string.IsNullOrEmpty(fileName) || fileName.Length > 128 ||
            fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            fileName.Contains('/') || fileName.Contains('\\') ||
            string.Equals(fileName, ".", StringComparison.Ordinal) ||
            string.Equals(fileName, "..", StringComparison.Ordinal))
        {
            throw new ArgumentException("Managed resource files must be plain file names.", nameof(fileName));
        }

        lock (_gate)
        {
            if (_fileNamesByResource.ContainsKey(resourceId))
            {
                throw new ArgumentException("Managed resource IDs must be unique.", nameof(resourceId));
            }

            _fileNamesByResource.Add(resourceId, fileName);
        }
    }

    public byte[] ReadContent(string resourceId, long? expectedRevision = null)
    {
        return ReadSnapshot(resourceId, expectedRevision);
    }

    /// <summary>
    /// Atomically opens the ID-mapped file and snapshots its validated bytes
    /// through the already-open handle. All validation (containment, reparse,
    /// ACL) is bound to the open handle's final NT path, not to a re-queried
    /// path, so a swap between open and check cannot redirect the read. The
    /// handle is opened with a share mode that denies write/delete opens for
    /// the lifetime of the read.
    /// </summary>
    internal byte[] ReadSnapshot(string resourceId, long? expectedRevision = null)
    {
        string fileName;
        lock (_gate)
        {
            if (!_fileNamesByResource.TryGetValue(resourceId, out fileName!))
            {
                throw new ManagedResourceUnavailableException();
            }
        }

        _ = expectedRevision;

        // Rebuild the full path only from the registered plain file name; the
        // caller-supplied ID never contributes path text.
        var fullPath = Path.Combine(_root, fileName);
        FileStream stream;
        try
        {
            stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 8192,
                FileOptions.SequentialScan);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException)
        {
            throw new ManagedResourceUnavailableException();
        }

        using (stream)
        {
            try
            {
                var finalPath = ResolveFinalPath(stream.SafeFileHandle);
                AssertContainedFile(finalPath, fileName);
                AssertRestrictedAcl(finalPath);

                if (stream.Length <= 0 || stream.Length > MaximumResourceBytes)
                {
                    throw new ManagedResourceUnavailableException();
                }

                var buffer = new byte[stream.Length];
                var offset = 0;
                while (offset < buffer.Length)
                {
                    var read = stream.Read(buffer, offset, buffer.Length - offset);
                    if (read == 0)
                    {
                        Array.Clear(buffer);
                        throw new ManagedResourceUnavailableException();
                    }

                    offset += read;
                }
                // Confirm the still-open handle names the registered file after
                // the bytes were read; a mapping change (or rename) in between
                // fails the request even though bytes were already read.
                ValidateMapping(resourceId, fileName);
                if (!string.Equals(ResolveFinalPath(stream.SafeFileHandle), finalPath, StringComparison.OrdinalIgnoreCase))
                {
                    Array.Clear(buffer);
                    throw new ManagedResourceUnavailableException();
                }

                return buffer;
            }
            catch (ManagedResourceUnavailableException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException or ObjectDisposedException)
            {
                throw new ManagedResourceUnavailableException();
            }
        }
    }

    /// <summary>
    /// Confirms the ID is still registered under any file name. Used by the
    /// request path after the post-open authorization re-check: a mapping
    /// change between the two grant checks fails closed.
    /// </summary>
    public void ValidateRegistration(string resourceId, long? expectedRevision = null)
    {
        lock (_gate)
        {
            if (!_fileNamesByResource.TryGetValue(resourceId, out _))
            {
                throw new ManagedResourceUnavailableException();
            }
        }
    }

    private void ValidateMapping(string resourceId, string expectedFileName)
    {
        lock (_gate)
        {
            if (!_fileNamesByResource.TryGetValue(resourceId, out var current) ||
                !string.Equals(current, expectedFileName, StringComparison.Ordinal))
            {
                throw new ManagedResourceUnavailableException();
            }
        }
    }

    private void AssertContainedFile(string finalPath, string fileName)
    {
        var attributes = File.GetAttributes(finalPath);
        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
        {
            throw new ManagedResourceUnavailableException();
        }

        var expectedRoot = EnsureTrailingSeparator(_root);
        if (!finalPath.StartsWith(expectedRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new ManagedResourceUnavailableException();
        }

        var relative = finalPath[expectedRoot.Length..];
        if (!string.Equals(relative, fileName, StringComparison.OrdinalIgnoreCase) ||
            relative.Contains('\\') || relative.Contains('/'))
        {
            throw new ManagedResourceUnavailableException();
        }
    }

    private void AssertRestrictedAcl(string finalPath)
    {
        var fileSecurity = FileSystemAclExtensions.GetAccessControl(
            new FileInfo(finalPath), AccessControlSections.Access | AccessControlSections.Owner);
        if (!fileSecurity.AreAccessRulesProtected)
        {
            throw new ManagedResourceUnavailableException();
        }

        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            _serviceSid.Value,
            "S-1-5-18",
            "S-1-5-32-544",
        };
        var owner = fileSecurity.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (owner is not null)
        {
            allowed.Add(owner.Value);
        }

        foreach (AuthorizationRule rule in fileSecurity.GetAccessRules(includeExplicit: true, includeInherited: true, targetType: typeof(SecurityIdentifier)))
        {
            if (rule is not FileSystemAccessRule accessRule || accessRule.AccessControlType != AccessControlType.Allow)
            {
                continue;
            }

            if (!allowed.Contains(((SecurityIdentifier)accessRule.IdentityReference).Value))
            {
                throw new ManagedResourceUnavailableException();
            }
        }
    }

    private static string ResolveFinalPath(Microsoft.Win32.SafeHandles.SafeFileHandle handle)
    {
        if (handle.IsInvalid)
        {
            throw new ManagedResourceUnavailableException();
        }

        var builder = new System.Text.StringBuilder(1024);
        uint length = NativeMethods.GetFinalPathNameByHandle(
            handle.DangerousGetHandle(), builder, (uint)builder.Capacity, 0);
        if (length == 0 || length >= builder.Capacity)
        {
            throw new ManagedResourceUnavailableException();
        }

        var finalPath = builder.ToString(0, (int)length);
        const string ntPrefix = @"\\?\";
        if (finalPath.StartsWith(ntPrefix, StringComparison.Ordinal))
        {
            finalPath = finalPath[ntPrefix.Length..];
        }

        const string uncPrefix = @"UNC\";
        if (finalPath.StartsWith(uncPrefix, StringComparison.Ordinal))
        {
            finalPath = @"\\" + finalPath[uncPrefix.Length..];
        }

        return Path.GetFullPath(finalPath);
    }

    private static string EnsureTrailingSeparator(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;

    private static bool IsSafeIdentifier(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 128 || !IsAsciiAlphaNumeric(value[0]))
        {
            return false;
        }

        for (var index = 1; index < value.Length; index++)
        {
            var character = value[index];
            if (!IsAsciiAlphaNumeric(character) && character is not '.' and not '_' and not '-')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsAsciiAlphaNumeric(char value) =>
        value is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9';

    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        internal static extern uint GetFinalPathNameByHandle(
            IntPtr handle, System.Text.StringBuilder path, uint length, uint flags);
    }
}
