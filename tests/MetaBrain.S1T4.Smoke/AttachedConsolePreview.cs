using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace MetaBrain.S1T4.Smoke;

internal static partial class OwnerServiceSmoke
{
    private sealed record AttachedConsoleResult(int ExitCode, string Transcript);

    private static readonly Regex TerminalEscapeSequence = new(
        "\\x1B\\][^\\x07\\x1B]*(?:\\x07|\\x1B\\\\)|\\x1B\\[[0-?]*[ -/]*[@-~]|\\x1B[()][0-2]",
        RegexOptions.Compiled);

    private const uint WaitObject0 = 0;
    private const uint WaitTimeout = 258;
    private const uint StartfUseStdHandles = 0x00000100;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareReadWrite = 0x00000001 | 0x00000002;
    private const uint ConsoleTextmodeBuffer = 1;
    private const uint OpenExisting = 3;

    private static async Task<AttachedConsoleResult> RunAttachedPreviewAsync(string executable, string controlPipe, string requestId)
    {
        var commandLine = new StringBuilder();
        commandLine.Append('"');
        commandLine.Append(executable);
        commandLine.Append('"');
        foreach (var argument in new[] { "owner", "request-preview", "--control-pipe", controlPipe, "--request-id", requestId })
        {
            commandLine.Append(" \"");
            commandLine.Append(argument);
            commandLine.Append('"');
        }

        var raw = await Task.Run(() => RunIsolatedConsolePreview(commandLine.ToString(), CommandTimeout)).ConfigureAwait(false);
        var transcript = TerminalEscapeSequence.Replace(raw.Output, string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        return new AttachedConsoleResult(raw.ExitCode, transcript);
    }

    private sealed record IsolatedConsoleOutput(int ExitCode, string Output);

    private static IsolatedConsoleOutput RunIsolatedConsolePreview(string commandLine, TimeSpan timeout)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The attached-console preview requires Windows.");
        }

        var allocated = false;
        if (GetConsoleWindow() == IntPtr.Zero)
        {
            if (!AllocConsole())
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            allocated = true;
        }

        var security = new SecurityAttributes
        {
            Length = Marshal.SizeOf<SecurityAttributes>(),
            Descriptor = IntPtr.Zero,
            InheritHandle = true,
        };
        var buffer = CreateConsoleScreenBuffer(
            GenericRead | GenericWrite, FileShareReadWrite, ref security, ConsoleTextmodeBuffer, IntPtr.Zero);
        if (buffer == new IntPtr(-1))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            SetConsoleScreenBufferSize(buffer, new Coord { X = 240, Y = 9000 });
            if (!SetCursorPosition(buffer, new Coord { X = 0, Y = 0 }))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var nul = CreateNulHandle("NUL", GenericRead, FileShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (nul == new IntPtr(-1))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            try
            {
                var startup = new StartupInfo
                {
                    Cb = Marshal.SizeOf<StartupInfo>(),
                    Flags = StartfUseStdHandles,
                    StdInput = nul,
                    StdOutput = buffer,
                    StdError = buffer,
                };
                var mutableCommandLine = new StringBuilder(commandLine);
                if (!CreateIsolatedProcess(
                        null, mutableCommandLine, IntPtr.Zero, IntPtr.Zero, true,
                        0, IntPtr.Zero, null, ref startup, out var processInfo))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                CloseHandle(processInfo.Thread);
                try
                {
                    var waitMilliseconds = checked((uint)Math.Min(timeout.TotalMilliseconds, int.MaxValue));
                    var waited = WaitForSingleObject(processInfo.Process, waitMilliseconds);
                    if (waited == WaitTimeout)
                    {
                        TerminateProcess(processInfo.Process, 1);
                        WaitForSingleObject(processInfo.Process, 5000);
                        throw new InvalidOperationException("The attached-console preview did not exit before its timeout.");
                    }

                    if (waited != WaitObject0)
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    }

                    if (!GetExitCodeProcess(processInfo.Process, out var exitCode))
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    }

                    return new IsolatedConsoleOutput(exitCode, ScrapeConsoleBuffer(buffer));
                }
                finally
                {
                    CloseHandle(processInfo.Process);
                }
            }
            finally
            {
                CloseHandle(nul);
            }
        }
        finally
        {
            CloseHandle(buffer);
            if (allocated)
            {
                FreeConsole();
            }
        }
    }

    private static string ScrapeConsoleBuffer(IntPtr buffer)
    {
        if (!GetConsoleScreenBufferInfo(buffer, out var info))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        var width = Math.Max(info.Size.X, (short)1);
        var rows = Math.Min(info.Cursor.Y + 1, 9000);
        var collected = new StringBuilder();
        for (var row = 0; row < rows; row++)
        {
            var line = new StringBuilder(width);
            if (!ReadConsoleOutputCharacter(buffer, line, (uint)width, new Coord { X = 0, Y = (short)row }, out _))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            collected.Append(line.ToString().TrimEnd());
            collected.Append('\n');
            if (collected.Length > 262144)
            {
                break;
            }
        }

        return collected.ToString();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Coord
    {
        public short X;
        public short Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SmallRect
    {
        public short Left;
        public short Top;
        public short Right;
        public short Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ConsoleScreenBufferInfo
    {
        public Coord Size;
        public Coord Cursor;
        public short Attributes;
        public SmallRect Window;
        public Coord MaximumWindowSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr Descriptor;
        public bool InheritHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Cb;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public uint Flags;
        public short ShowWindow;
        public short Reserved2;
        public IntPtr ReservedData;
        public IntPtr StdInput;
        public IntPtr StdOutput;
        public IntPtr StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public uint ProcessId;
        public uint ThreadId;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateConsoleScreenBuffer(
        uint desiredAccess,
        uint shareMode,
        ref SecurityAttributes securityAttributes,
        uint flags,
        IntPtr screenBufferData);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleScreenBufferSize(IntPtr consoleOutput, Coord size);

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "SetConsoleCursorPosition")]
    private static extern bool SetCursorPosition(IntPtr consoleOutput, Coord position);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleScreenBufferInfo(IntPtr consoleOutput, out ConsoleScreenBufferInfo info);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "ReadConsoleOutputCharacterW")]
    private static extern bool ReadConsoleOutputCharacter(
        IntPtr consoleOutput,
        [Out] StringBuilder character,
        uint length,
        Coord readCoord,
        out uint charsRead);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern IntPtr CreateNulHandle(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateProcessW")]
    private static extern bool CreateIsolatedProcess(
        string? applicationName,
        StringBuilder commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr process, out int exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
