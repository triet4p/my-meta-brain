using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace MetaBrain.Connections;

internal static class WindowsPipeServer
{
    private const uint PipeAccessDuplex = 0x00000003;
    private const uint FileFlagFirstPipeInstance = 0x00080000;
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint PipeRejectRemoteClients = 0x00000008;
    private const uint PipeUnlimitedInstances = 255;
    private const uint BufferSize = 4096;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        public int InheritHandle;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateNamedPipeW")]
    private static extern IntPtr CreateNamedPipe(
        string name,
        uint openMode,
        uint pipeMode,
        uint maximumInstances,
        uint outputBufferSize,
        uint inputBufferSize,
        uint defaultTimeout,
        ref SecurityAttributes securityAttributes);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertSddlToSecurityDescriptor(string sddl, uint revision, out IntPtr descriptor, out uint descriptorSize);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    public static NamedPipeServerStream Create(ServiceSettings settings, bool firstInstance)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var serviceSid = identity.User?.Value ?? throw new InvalidDataException("The service process has no user SID.");
        var sddl = BuildOwnerPipeSddl(settings, serviceSid);
        if (!ConvertSddlToSecurityDescriptor(sddl, 1, out var descriptor, out _))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot secure the owner named-pipe endpoint.");
        }

        try
        {
            var attributes = new SecurityAttributes
            {
                Length = Marshal.SizeOf<SecurityAttributes>(),
                SecurityDescriptor = descriptor,
                InheritHandle = 0
            };
            var openMode = PipeAccessDuplex | FileFlagOverlapped;
            if (firstInstance)
            {
                openMode |= FileFlagFirstPipeInstance;
            }

            var handle = CreateNamedPipe(
                $"\\\\.\\pipe\\{settings.ControlPipe}",
                openMode,
                PipeRejectRemoteClients,
                PipeUnlimitedInstances,
                BufferSize,
                BufferSize,
                0,
                ref attributes);
            if (handle == InvalidHandleValue || handle == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot claim the owner named-pipe endpoint.");
            }

            return new NamedPipeServerStream(PipeDirection.InOut, isAsync: true, isConnected: false, new SafePipeHandle(handle, ownsHandle: true));
        }
        finally
        {
            LocalFree(descriptor);
        }
    }

    private static string BuildOwnerPipeSddl(ServiceSettings settings, string serviceSid)
    {
        var allowedSids = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            new SecurityIdentifier(settings.OwnerSid).Value,
            new SecurityIdentifier(serviceSid).Value
        };
        return "D:P" + string.Concat(allowedSids.Select(sid => $"(A;;GA;;;{sid})"));
    }
}
