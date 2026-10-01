using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace MetaBrain.Connections;

internal sealed record WindowsClientIdentity(string UserSid);

internal static class WindowsClientIdentityReader
{
    private const uint TokenQuery = 0x0008;
    private const int TokenUser = 1;

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenThreadToken(IntPtr threadHandle, uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool openAsSelf, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass, IntPtr tokenInformation, int tokenInformationLength, out int returnLength);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertSidToStringSid(IntPtr sid, out IntPtr stringSid);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    public static WindowsClientIdentity Read(NamedPipeServerStream pipe)
    {
        WindowsClientIdentity? result = null;
        pipe.RunAsClient(() => result = ReadImpersonationToken());
        return result ?? throw new UnauthorizedAccessException("The named-pipe client identity is unavailable.");
    }

    private static WindowsClientIdentity ReadImpersonationToken()
    {
        if (!OpenThreadToken(GetCurrentThread(), TokenQuery, openAsSelf: true, out var token))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot inspect the named-pipe client token.");
        }

        try
        {
            return new WindowsClientIdentity(ReadTokenSid(token));
        }
        finally
        {
            CloseHandle(token);
        }
    }

    private static string ReadTokenSid(IntPtr token)
    {
        _ = GetTokenInformation(token, TokenUser, IntPtr.Zero, 0, out var length);
        if (length <= 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot inspect the named-pipe client token.");
        }

        var information = Marshal.AllocHGlobal(length);
        try
        {
            if (!GetTokenInformation(token, TokenUser, information, length, out _))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot inspect the named-pipe client token.");
            }

            var sid = Marshal.ReadIntPtr(information);
            if (sid == IntPtr.Zero || !ConvertSidToStringSid(sid, out var stringSid))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot read the named-pipe client SID.");
            }

            try
            {
                return Marshal.PtrToStringUni(stringSid)
                    ?? throw new UnauthorizedAccessException("The named-pipe client SID is invalid.");
            }
            finally
            {
                LocalFree(stringSid);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(information);
        }
    }
}
