using System.ComponentModel;
using System.Runtime.InteropServices;

namespace MetaBrain.Connections;

internal static class WindowsServiceControl
{
    private const string ServiceName = "MetaBrain";
    private const uint ServiceWin32OwnProcess = 0x00000010;
    private const uint ServiceStartPending = 0x00000002;
    private const uint ServiceStopPending = 0x00000003;
    private const uint ServiceRunning = 0x00000004;
    private const uint ServiceStopped = 0x00000001;
    private const uint ServiceAcceptStop = 0x00000001;
    private const uint ServiceAcceptShutdown = 0x00000004;
    private const uint ControlStop = 0x00000001;
    private const uint ControlShutdown = 0x00000005;
    private const uint ErrorSuccess = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceTableEntry
    {
        public IntPtr ServiceName;
        public IntPtr ServiceMain;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void ServiceMainDelegate(uint argumentCount, IntPtr arguments);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint HandlerDelegate(uint control, uint eventType, IntPtr eventData, IntPtr context);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "StartServiceCtrlDispatcherW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartServiceControlDispatcher(IntPtr serviceTable);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "RegisterServiceCtrlHandlerExW")]
    private static extern IntPtr RegisterServiceControlHandler(string serviceName, HandlerDelegate handler, IntPtr context);

    [DllImport("advapi32.dll", SetLastError = true, EntryPoint = "SetServiceStatus")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetServiceStatus(IntPtr statusHandle, ref ServiceStatus status);

    private static string _configurationPath = string.Empty;
    private static IntPtr _statusHandle;
    private static ServiceStatus _status;
    private static CancellationTokenSource? _stop;
    private static HandlerDelegate? _handler;

    public static void Run(string configurationPath)
    {
        _configurationPath = configurationPath;
        ServiceMainDelegate serviceMain = ServiceMain;
        _handler = HandleControl;
        var name = Marshal.StringToHGlobalUni(ServiceName);
        var table = Marshal.AllocHGlobal(2 * Marshal.SizeOf<ServiceTableEntry>());
        try
        {
            Marshal.StructureToPtr(new ServiceTableEntry
            {
                ServiceName = name,
                ServiceMain = Marshal.GetFunctionPointerForDelegate(serviceMain)
            }, table, fDeleteOld: false);
            Marshal.StructureToPtr(default(ServiceTableEntry), IntPtr.Add(table, Marshal.SizeOf<ServiceTableEntry>()), fDeleteOld: false);
            if (!StartServiceControlDispatcher(table))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "The Windows Service Control Manager did not start the service.");
            }
        }
        finally
        {
            GC.KeepAlive(serviceMain);
            GC.KeepAlive(_handler);
            Marshal.FreeHGlobal(table);
            Marshal.FreeHGlobal(name);
        }
    }

    private static void ServiceMain(uint argumentCount, IntPtr arguments)
    {
        _statusHandle = RegisterServiceControlHandler(ServiceName, _handler!, IntPtr.Zero);
        if (_statusHandle == IntPtr.Zero)
        {
            return;
        }

        _stop = new CancellationTokenSource();
        PublishStatus(ServiceStartPending, 0, 30000);
        try
        {
            var settings = ServiceSettingsLoader.Load(_configurationPath);
            PublishStatus(ServiceRunning, 0, 0);
            new NamedPipeService(settings).RunAsync(_stop.Token).GetAwaiter().GetResult();
            PublishStatus(ServiceStopped, 0, 0);
        }
        catch
        {
            PublishStatus(ServiceStopped, 1, 0);
        }
        finally
        {
            _stop.Dispose();
            _stop = null;
        }
    }

    private static uint HandleControl(uint control, uint eventType, IntPtr eventData, IntPtr context)
    {
        if (control is ControlStop or ControlShutdown)
        {
            PublishStatus(ServiceStopPending, 0, 30000);
            _stop?.Cancel();
        }

        return ErrorSuccess;
    }

    private static void PublishStatus(uint state, uint exitCode, uint waitHint)
    {
        _status.ServiceType = ServiceWin32OwnProcess;
        _status.CurrentState = state;
        _status.ControlsAccepted = state == ServiceRunning ? ServiceAcceptStop | ServiceAcceptShutdown : 0;
        _status.Win32ExitCode = exitCode;
        _status.ServiceSpecificExitCode = 0;
        _status.CheckPoint = state is ServiceStartPending or ServiceStopPending ? _status.CheckPoint + 1 : 0;
        _status.WaitHint = waitHint;
        if (_statusHandle != IntPtr.Zero)
        {
            _ = SetServiceStatus(_statusHandle, ref _status);
        }
    }
}
