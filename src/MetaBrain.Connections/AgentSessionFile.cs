using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace MetaBrain.Connections;

/// <summary>
/// Local DPAPI-protected handoff for the single-use agent session bearer.
/// Same protection shape as the owner token handoff: CurrentUser DPAPI with an
/// owner/SYSTEM/Administrators protected ACL. The session bearer is never passed
/// as args/env/config/log output or embedded in a URI.
/// </summary>
internal static class AgentSessionFile
{
    private static readonly SecurityIdentifier SystemSid = new("S-1-5-18");
    private static readonly SecurityIdentifier AdministratorsSid = new("S-1-5-32-544");
    private static readonly byte[] SessionPurpose = Encoding.ASCII.GetBytes("MetaBrain|agent-session-handoff|v1");
    private static readonly byte[] HandoffPurpose = Encoding.ASCII.GetBytes("MetaBrain|owner-token-handoff|v1");
    private const uint CryptProtectUiForbidden = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Length;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn,
        string? description,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        uint flags,
        out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        IntPtr description,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        uint flags,
        out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    public static void Write(string sessionToken, string requestedPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedPath);
        if (sessionToken.Length != 48 || !sessionToken.StartsWith("mbs1_", StringComparison.Ordinal))
        {
            throw new InvalidDataException("The agent session token is invalid.");
        }

        var path = Path.GetFullPath(requestedPath);
        var directory = new DirectoryInfo(Path.GetDirectoryName(path)
            ?? throw new InvalidDataException("The agent session directory is invalid."));
        if (!directory.Exists || (directory.Attributes & FileAttributes.ReparsePoint) != 0 ||
            File.Exists(path) || Directory.Exists(path))
        {
            throw new IOException("The agent session destination must be a new file in an existing non-reparse directory.");
        }

        var ownerSid = WindowsIdentity.GetCurrent().User
            ?? throw new UnauthorizedAccessException("The agent session process has no user SID.");
        var plaintext = Encoding.ASCII.GetBytes(sessionToken);
        byte[]? protectedBytes = null;
        try
        {
            protectedBytes = Protect(plaintext, SessionPurpose);
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
            try
            {
                var security = new FileSecurity();
                security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                security.SetOwner(ownerSid);
                security.AddAccessRule(new FileSystemAccessRule(ownerSid, FileSystemRights.FullControl, AccessControlType.Allow));
                security.AddAccessRule(new FileSystemAccessRule(SystemSid, FileSystemRights.FullControl, AccessControlType.Allow));
                security.AddAccessRule(new FileSystemAccessRule(AdministratorsSid, FileSystemRights.FullControl, AccessControlType.Allow));
                FileSystemAclExtensions.SetAccessControl(new FileInfo(path), security);
            }
            catch
            {
                stream.Dispose();
                if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)
                {
                    File.Delete(path);
                }

                throw;
            }

            stream.Write(protectedBytes);
            stream.Flush(flushToDisk: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            if (protectedBytes is { Length: > 0 })
            {
                CryptographicOperations.ZeroMemory(protectedBytes);
            }
        }
    }

    public static string Read(string path)
    {
        var protectedBytes = File.ReadAllBytes(path);
        try
        {
            var plaintext = Unprotect(protectedBytes, SessionPurpose, expectedLength: 48);
            try
            {
                var token = Encoding.ASCII.GetString(plaintext);
                if (token.Length != 48 || !token.StartsWith("mbs1_", StringComparison.Ordinal))
                {
                    throw new InvalidDataException("The agent session file is invalid.");
                }

                return token;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
        }
    }

    public static string UnprotectHandoff(byte[] protectedBytes)
    {
        ArgumentNullException.ThrowIfNull(protectedBytes);
        var plaintext = Unprotect(protectedBytes, HandoffPurpose, expectedLength: 47);
        try
        {
            var token = Encoding.ASCII.GetString(plaintext);
            if (token.Length != 47 || !token.StartsWith("mb1_", StringComparison.Ordinal))
            {
                throw new InvalidDataException("The owner token handoff is invalid.");
            }

            return token;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static byte[] Protect(byte[] plaintext, byte[] purpose)
    {
        var inputHandle = GCHandle.Alloc(plaintext, GCHandleType.Pinned);
        var entropyHandle = GCHandle.Alloc(purpose, GCHandleType.Pinned);
        var input = new DataBlob { Length = plaintext.Length, Data = inputHandle.AddrOfPinnedObject() };
        var entropy = new DataBlob { Length = purpose.Length, Data = entropyHandle.AddrOfPinnedObject() };
        var output = default(DataBlob);
        try
        {
            if (!CryptProtectData(ref input, null, ref entropy, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectUiForbidden, out output))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not protect the agent session handoff.");
            }

            if (output.Length is <= 0 or > 1024 * 1024)
            {
                throw new InvalidDataException("The protected agent session handoff has an invalid size.");
            }

            var protectedBytes = new byte[output.Length];
            Marshal.Copy(output.Data, protectedBytes, 0, protectedBytes.Length);
            return protectedBytes;
        }
        finally
        {
            if (output.Data != IntPtr.Zero)
            {
                LocalFree(output.Data);
            }

            entropyHandle.Free();
            inputHandle.Free();
        }
    }

    private static byte[] Unprotect(byte[] protectedBytes, byte[] purpose, int expectedLength)
    {
        var inputHandle = GCHandle.Alloc(protectedBytes, GCHandleType.Pinned);
        var entropyHandle = GCHandle.Alloc(purpose, GCHandleType.Pinned);
        var input = new DataBlob { Length = protectedBytes.Length, Data = inputHandle.AddrOfPinnedObject() };
        var entropy = new DataBlob { Length = purpose.Length, Data = entropyHandle.AddrOfPinnedObject() };
        var output = default(DataBlob);
        try
        {
            if (!CryptUnprotectData(ref input, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectUiForbidden, out output) || output.Length != expectedLength)
            {
                throw new InvalidDataException("The protected handoff file did not decrypt as a valid token.");
            }

            var plaintext = new byte[output.Length];
            Marshal.Copy(output.Data, plaintext, 0, plaintext.Length);
            return plaintext;
        }
        finally
        {
            if (output.Data != IntPtr.Zero)
            {
                if (output.Length is > 0 and <= 1024 * 1024)
                {
                    for (var index = 0; index < output.Length; index++)
                    {
                        Marshal.WriteByte(output.Data, index, 0);
                    }
                }

                LocalFree(output.Data);
            }

            inputHandle.Free();
            entropyHandle.Free();
        }
    }
}
