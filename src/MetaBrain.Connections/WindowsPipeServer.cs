using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MetaBrain.Connections;

/// <summary>
/// The local-only named-pipe listener. The pipe is created with the default
/// security descriptor of the service process (the ordinary permissions of the
/// owner account): the product never builds a custom ACL/SDDL and never
/// changes the permissions or owner of a path. Remote clients are rejected by
/// the pipe itself, and whether a connected client may act as owner is decided
/// by the impersonated client check in the service, not by pipe permissions.
/// </summary>

internal static class WindowsPipeServer
{
    private const uint PipeAccessDuplex = 0x00000003;
    private const uint FileFlagFirstPipeInstance = 0x00080000;
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint PipeRejectRemoteClients = 0x00000008;
    private const uint PipeUnlimitedInstances = 255;
    private const uint BufferSize = 4096;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateNamedPipeW")]
    private static extern IntPtr CreateNamedPipe(
        string name,
        uint openMode,
        uint pipeMode,
        uint maximumInstances,
        uint outputBufferSize,
        uint inputBufferSize,
        uint defaultTimeout,
        IntPtr securityAttributes);

    public static NamedPipeServerStream Create(ServiceSettings settings, bool firstInstance) =>
        Create(settings.ControlPipe, firstInstance);

    public static NamedPipeServerStream CreateAgent(ServiceSettings settings, bool firstInstance) =>
        Create(settings.AgentPipe, firstInstance);

    private static NamedPipeServerStream Create(string pipeName, bool firstInstance)
    {
        var openMode = PipeAccessDuplex | FileFlagOverlapped;
        if (firstInstance)
        {
            openMode |= FileFlagFirstPipeInstance;
        }

        // A null security descriptor keeps the process default: the pipe is
        // owned by the service account with ordinary permissions and no custom
        // access rules. Local-only delivery still comes from the
        // reject-remote-clients pipe mode, not from an ACL.
        var handle = CreateNamedPipe(
            $"\\\\.\\pipe\\{pipeName}",
            openMode,
            PipeRejectRemoteClients,
            PipeUnlimitedInstances,
            BufferSize,
            BufferSize,
            0,
            IntPtr.Zero);
        if (handle == InvalidHandleValue || handle == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot claim the owner named-pipe endpoint.");
        }

        return new NamedPipeServerStream(PipeDirection.InOut, isAsync: true, isConnected: false, new SafePipeHandle(handle, ownsHandle: true));
    }

}
