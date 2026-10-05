using System.ComponentModel;
using System.Collections;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using TinadecCore.Abstractions.Ports;

namespace TinadecCore.Models.Harness;

/// <summary>
/// The <c>Windows</c> terminal host: <see cref="CreatePseudoConsole"/> bridges a pair of anonymous pipes
/// to a console the child believes it owns, and the console is handed to <c>CreateProcess</c> through a
/// <c>PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE</c> entry in a <c>STARTUPINFOEX</c> attribute list.
/// </summary>
/// <remarks>
/// Raw <c>CreateProcess</c> rather than <see cref="System.Diagnostics.Process"/>: a <c>ProcessStartInfo</c>
/// cannot carry an attribute list, and the attribute list is the whole mechanism. Arguments go through a
/// command line built by <see cref="Win32CommandLine"/>, never by joining with spaces.
/// </remarks>
internal sealed class ConPtyTerminalHost : IHarnessTerminalHost
{
    public bool IsSupported => OperatingSystem.IsWindows();

    public IHarnessTerminalSession Start(HarnessTerminalRequest request)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "No terminal host for this platform yet: this build only implements ConPTY (Windows). Refusing to fall back to pipes, because a pipe is not a terminal.");
        }

        return ConPtyTerminalSession.Start(request);
    }
}

internal sealed class ConPtyTerminalSession : IHarnessTerminalSession
{
    private const uint ExtremePresent = 0x0008_0000; // EXTENDED_STARTUPINFO_PRESENT
    private const uint UnicodeEnvironment = 0x0000_0400;
    private const uint UseStdHandles = 0x0000_0100; // STARTF_USESTDHANDLES
    private const uint WaitObject0 = 0x0000_0000;
    private const ulong AttributePseudoConsole = 0x0002_0016; // PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE

    private readonly SafeHandle _process;
    private readonly IntPtr _pseudoConsole;
    private readonly FileStream _output;
    private readonly FileStream _input;
    private readonly int _processId;
    private int _disposed;

    private ConPtyTerminalSession(SafeHandle process, IntPtr pseudoConsole, FileStream output, FileStream input, int processId)
    {
        _process = process;
        _pseudoConsole = pseudoConsole;
        _output = output;
        _input = input;
        _processId = processId;
    }

    public Stream Output => _output;

    public Stream Input => _input;

    public int ProcessId => _processId;

    /// <summary>
    /// Asked of the process handle rather than compared against <c>GetExitCodeProcess</c>, because that
    /// API reports "still running" as exit code 259: a child that exits with 259 on purpose would read
    /// as alive forever.
    /// </summary>
    public bool HasExited => WaitForSingleObject(_process, 0u) == WaitObject0;

    public int ExitCode
    {
        get
        {
            if (!GetExitCodeProcess(_process, out var code))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "reading the terminal child's exit code failed");
            }

            return (int)code;
        }
    }

    public bool WaitForExit(TimeSpan timeout)
        => WaitForSingleObject(_process, (uint)Math.Clamp(timeout.TotalMilliseconds, 0d, uint.MaxValue - 1d)) == WaitObject0;

    public void Resize(int columns, int rows)
    {
        // COORD is two shorts; a width that does not fit would ask the console for a negative screen.
        if (columns is < 1 or > short.MaxValue || rows is < 1 or > short.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(columns), $"a {columns}x{rows} terminal does not fit the coordinate type");
        }

        var result = ResizePseudoConsole(_pseudoConsole, new COORD((short)columns, (short)rows));
        if (result != 0)
        {
            throw new Win32Exception(unchecked((int)result), $"ResizePseudoConsole({columns}x{rows}) failed");
        }
    }

    public void Kill()
    {
        if (!TerminateProcess(_process, 1))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "TerminateProcess on the terminal child failed");
        }
    }

    public static ConPtyTerminalSession Start(HarnessTerminalRequest request)
    {
        if (request.Columns < 2 || request.Rows < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "a terminal smaller than 2x2 cannot hold a prompt");
        }

        // The pseudoconsole reads the child's keystrokes from the read end of one pipe and writes what the
        // child paints into the write end of the other; the opposite ends go to the caller.
        if (!CreatePipe(out var consoleIn, out var clientWrite, IntPtr.Zero, 0)
            || !CreatePipe(out var clientRead, out var consoleOut, IntPtr.Zero, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "creating the terminal pipes failed");
        }

        var attributeList = IntPtr.Zero;
        var environmentBlock = IntPtr.Zero;
        var commandLine = IntPtr.Zero;
        IntPtr pseudoConsole = IntPtr.Zero;
        var handedOff = false;
        var scratchHandles = new List<IntPtr> { consoleIn, clientWrite, clientRead, consoleOut };
        try
        {
            var created = CreatePseudoConsole(new COORD((short)request.Columns, (short)request.Rows), consoleIn, consoleOut, 0, out pseudoConsole);
            if (created != 0)
            {
                throw new Win32Exception(unchecked((int)created), "CreatePseudoConsole failed");
            }

            var size = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            if (size == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "sizing the process attribute list failed");
            }

            attributeList = Marshal.AllocHGlobal(size);
            if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref size))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "InitializeProcThreadAttributeList failed");
            }

            // The value is the console handle itself, read as cbSize bytes — not the address of a slot
            // holding it. With the address, CreateProcess succeeded, the child ran, and the stream carried
            // zero bytes.
            if (!UpdateProcThreadAttribute(attributeList, 0, AttributePseudoConsole, pseudoConsole, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "attaching the pseudoconsole to the startup info failed");
            }

            commandLine = Marshal.StringToHGlobalUni(Win32CommandLine.Build(request.Executable, request.ArgumentList));
            if (request.Environment is { } environment)
            {
                environmentBlock = Marshal.StringToHGlobalUni(BuildEnvironmentBlock(environment));
            }

            var startupInfo = new STARTUPINFOEX
            {
                cb = (uint)Marshal.SizeOf<STARTUPINFOEX>(),
                // "The standard handles are the ones below", and below are three nulls. Without this the
                // child inherits this process's handles instead: measured as IsOutputRedirected=true and
                // Console.WindowWidth throwing IOException while `mode con` described the pseudoconsole we
                // had just built — a console the child could open but was not standing on.
                dwFlags = UseStdHandles,
                lpAttributeList = attributeList
            };

            if (!CreateProcess(IntPtr.Zero, commandLine, IntPtr.Zero, IntPtr.Zero, false, ExtremePresent | UnicodeEnvironment, environmentBlock, request.WorkingDirectory, ref startupInfo, out var processInformation))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"starting '{request.Executable}' on the terminal failed");
            }

            CloseHandle(processInformation.hThread);
            var process = new SafeProcessHandle(processInformation.hProcess, true);
            // Synchronous streams: an anonymous pipe cannot be opened for overlapped I/O, so .NET refuses
            // an async handle. A reader parks until the child writes, which is what a terminal host does
            // with a pty anywhere; ending the child is what releases a parked reader (see Dispose).
            var output = new FileStream(new SafeFileHandle(clientRead, true), FileAccess.Read, 4096);
            var input = new FileStream(new SafeFileHandle(clientWrite, true), FileAccess.Write, 4096);
            var session = new ConPtyTerminalSession(process, pseudoConsole, output, input, processInformation.dwProcessId);
            handedOff = true;

            // The pseudoconsole took these two ends at CreatePseudoConsole and the child is attached, so
            // our copies have no further use, and closing them is what lets the caller's stream learn
            // that the channel is gone. Ordered after the hand-off so the finally cannot close them twice.
            CloseHandle(consoleIn);
            CloseHandle(consoleOut);
            return session;
        }
        finally
        {
            if (attributeList != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(attributeList);
                Marshal.FreeHGlobal(attributeList);
            }
            if (environmentBlock != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(environmentBlock);
            }
            if (commandLine != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(commandLine);
            }

            if (!handedOff)
            {
                foreach (var handle in scratchHandles)
                {
                    CloseHandle(handle);
                }

                if (pseudoConsole != IntPtr.Zero)
                {
                    ClosePseudoConsole(pseudoConsole);
                }
            }
        }
    }

    /// <summary>
    /// Overrides merged onto a copy of this process's environment. Not convenience: <c>CreateProcess</c>
    /// treats <c>lpEnvironment</c> as the child's entire environment, so a block holding only the names a
    /// caller listed starts a child with no <c>PATH</c> and no <c>SYSTEMROOT</c> — measured as node
    /// aborting inside its own startup assertion, visible only as silence. A null override removes a name.
    /// </summary>
    private static string BuildEnvironmentBlock(IReadOnlyDictionary<string, string?> overrides)
    {
        var merged = new SortedDictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            // Hidden "=C:" entries carry the current directory per drive. They sort by the text after the
            // '=', not as if the '=' were a character, so an ordinal sort would place them wrong.
            if (entry.Key is string name && !name.StartsWith('=') && entry.Value is string value)
            {
                merged[name] = value;
            }
        }

        foreach (var (name, value) in overrides)
        {
            merged[name] = value;
        }

        var builder = new StringBuilder();
        foreach (var (name, value) in merged)
        {
            if (value is null)
            {
                continue;
            }

            builder.Append(name).Append('=').Append(value).Append('\0');
        }
        builder.Append('\0');
        return builder.ToString();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // A child that outlives its session is a screen nobody can close, so termination is part of
        // disposal here even though Kill() is the call that reports failure.
        if (!HasExited)
        {
            TerminateProcess(_process, 1);
        }

        _input.Dispose();
        _output.Dispose();
        ClosePseudoConsole(_pseudoConsole);
        _process.Dispose();
    }

    private static void CloseHandle(IntPtr handle)
    {
        if (handle != IntPtr.Zero)
        {
            CloseHandlePrivate(handle);
        }
    }

    private sealed class SafeProcessHandle : SafeHandle
    {
        public SafeProcessHandle(IntPtr handle, bool ownsHandle) : base(handle, ownsHandle)
        {
        }

        public override bool IsInvalid => handle == IntPtr.Zero;

        protected override bool ReleaseHandle() => CloseHandlePrivate(handle);
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct COORD
    {
        public readonly short X;
        public readonly short Y;

        public COORD(short x, short y)
        {
            X = x;
            Y = y;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEX
    {
        public uint cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public uint dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public ushort wShowWindow, cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool CreatePipe(out IntPtr readHandle, out IntPtr writeHandle, IntPtr securityAttributes, int size);

    [DllImport("kernel32", EntryPoint = "CloseHandle", SetLastError = true)]
    private static extern bool CloseHandlePrivate(IntPtr handle);

    [DllImport("kernel32", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeHandle handle, uint milliseconds);

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool GetExitCodeProcess(SafeHandle handle, out uint exitCode);

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool TerminateProcess(SafeHandle handle, uint exitCode);

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcess(
        IntPtr applicationName,
        IntPtr commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string workingDirectory,
        ref STARTUPINFOEX startupInfo,
        out PROCESS_INFORMATION processInformation);

    [DllImport("kernel32")]
    private static extern uint CreatePseudoConsole(COORD size, IntPtr input, IntPtr output, uint flags, out IntPtr pseudoConsole);

    [DllImport("kernel32")]
    private static extern uint ResizePseudoConsole(IntPtr pseudoConsole, COORD size);

    [DllImport("kernel32")]
    private static extern void ClosePseudoConsole(IntPtr pseudoConsole);

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr attributeList, int attributeCount, int flags, ref IntPtr sizeInBytes);

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr attributeList, uint flags, ulong attribute, IntPtr value, IntPtr size, IntPtr previousValue, IntPtr previousSize);

    [DllImport("kernel32")]
    private static extern void DeleteProcThreadAttributeList(IntPtr attributeList);
}
