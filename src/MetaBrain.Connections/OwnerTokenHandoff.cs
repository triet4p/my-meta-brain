using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace MetaBrain.Connections;

internal sealed class OwnerTokenHandoff : IDisposable
{
    private static readonly SecurityIdentifier SystemSid = new("S-1-5-18");
    private const uint CryptProtectUiForbidden = 0x1;
    private static readonly byte[] HandoffPurpose = Encoding.ASCII.GetBytes("MetaBrain|owner-token-handoff|v1");
    private FileStream? _stream;
    private bool _written;

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

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
    private static readonly SecurityIdentifier AdministratorsSid = new("S-1-5-32-544");

    private OwnerTokenHandoff(string path, FileStream stream)
    {
        Path = path;
        _stream = stream;
    }

    public string Path { get; }

    public static OwnerTokenHandoff Create(string requestedPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Owner token handoff files are Windows-only.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(requestedPath);
        var path = System.IO.Path.GetFullPath(requestedPath);
        var directory = new DirectoryInfo(System.IO.Path.GetDirectoryName(path)
            ?? throw new InvalidDataException("The owner handoff directory is invalid."));
        if (!directory.Exists || (directory.Attributes & FileAttributes.ReparsePoint) != 0 ||
            File.Exists(path) || Directory.Exists(path))
        {
            throw new IOException("The owner handoff destination must be a new file in an existing non-reparse directory.");
        }

        var ownerSid = WindowsIdentity.GetCurrent().User
            ?? throw new UnauthorizedAccessException("The owner handoff process has no user SID.");
        var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        try
        {
            var security = new FileSecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.SetOwner(ownerSid);
            security.AddAccessRule(new FileSystemAccessRule(ownerSid, FileSystemRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(SystemSid, FileSystemRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(AdministratorsSid, FileSystemRights.FullControl, AccessControlType.Allow));
            FileSystemAclExtensions.SetAccessControl(new FileInfo(path), security);
            return new OwnerTokenHandoff(path, stream);
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
    }

    public void WriteToken(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        if (_written || token.Length != 47 || !token.StartsWith("mb1_", StringComparison.Ordinal))
        {
            throw new InvalidDataException("The owner handoff token is invalid.");
        }

        var bytes = Encoding.ASCII.GetBytes(token);
        byte[]? protectedBytes = null;
        try
        {
            if (bytes.Length != token.Length)
            {
                throw new InvalidDataException("The owner handoff token is invalid.");
            }

            protectedBytes = ProtectForCurrentUser(bytes);
            var stream = _stream ?? throw new ObjectDisposedException(nameof(OwnerTokenHandoff));
            stream.Write(protectedBytes);
            stream.Flush(flushToDisk: true);
            _written = true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            if (protectedBytes is { Length: > 0 })
            {
                CryptographicOperations.ZeroMemory(protectedBytes);
            }
        }
    }
    private static byte[] ProtectForCurrentUser(byte[] plaintext)
    {
        var inputHandle = GCHandle.Alloc(plaintext, GCHandleType.Pinned);
        var entropyHandle = GCHandle.Alloc(HandoffPurpose, GCHandleType.Pinned);
        var input = new DataBlob { Length = plaintext.Length, Data = inputHandle.AddrOfPinnedObject() };
        var entropy = new DataBlob { Length = HandoffPurpose.Length, Data = entropyHandle.AddrOfPinnedObject() };
        var output = default(DataBlob);
        try
        {
            if (!CryptProtectData(ref input, null, ref entropy, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectUiForbidden, out output))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not protect the owner token handoff.");
            }

            if (output.Length is <= 0 or > 1024 * 1024)
            {
                throw new InvalidDataException("The protected owner token handoff has an invalid size.");
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


    public void Dispose()
    {
        var stream = Interlocked.Exchange(ref _stream, null);
        stream?.Dispose();
        if (!_written && File.Exists(Path) && (File.GetAttributes(Path) & FileAttributes.ReparsePoint) == 0)
        {
            File.Delete(Path);
        }
    }
}
