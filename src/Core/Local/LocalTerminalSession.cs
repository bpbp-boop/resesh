using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Sessions.Core.Backend;
using Sessions.Core.Models;

namespace Sessions.Core.Local;

/// <summary>Launch of a local shell failed (bad executable, missing directory, Win32 error).</summary>
public sealed class LocalSessionException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>
/// One live local shell hosted in a Windows pseudoconsole (ConPTY): the child process,
/// its I/O pipes, and a Job Object that owns the whole descendant tree so closing the
/// tab can never leave orphaned processes. No console window is created — output flows
/// through the pseudoconsole into the pipe reader. All calls are safe from any thread;
/// events fire on background threads.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class LocalTerminalSession : ITerminalBackend
{
    private SafeFileHandle? _inputWrite;   // us -> shell
    private SafeFileHandle? _outputRead;   // shell -> us
    private FileStream? _inputStream;
    private IntPtr _console;               // HPCON
    private IntPtr _job;
    private IntPtr _process;
    private int _processId;
    private Thread? _readerThread;
    private Thread? _exitThread;
    private int _exitRaised;
    private long _lastOutputTicks;
    private volatile bool _disposed;

    public event Action<byte[]>? OutputReceived;

    /// <summary>Raised once when the process ends on its own, with its exit code.
    /// Never raised for a local <see cref="Stop"/>/<see cref="Dispose"/>.</summary>
    public event Action<int>? Exited;

    /// <summary>Diagnostic hook (DEBUG builds wire this to a trace log).</summary>
    public static Action<string>? TraceHook { get; set; }

    public int ProcessId => _processId;

    public bool IsRunning => _process != IntPtr.Zero && _exitRaised == 0 && !_disposed;

    /// <summary>
    /// Starts the shell described by the session's <see cref="LocalTarget"/> at the given
    /// terminal size. Blocking (fast) — throws <see cref="LocalSessionException"/> on failure,
    /// leaving the instance fully cleaned up.
    /// </summary>
    public void Start(Session session, int columns, int rows)
    {
        if (_process != IntPtr.Zero)
            throw new InvalidOperationException("Session already used; create a new instance per launch.");
        var target = session.Local
            ?? throw new ArgumentException("Session has no local target.", nameof(session));
        if (string.IsNullOrWhiteSpace(target.Executable))
            throw new LocalSessionException("No executable is set for this profile.");

        var executable = Environment.ExpandEnvironmentVariables(target.Executable.Trim());
        var directory = Environment.ExpandEnvironmentVariables(target.StartingDirectory.Trim());
        if (directory.Length == 0)
            directory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!Directory.Exists(directory))
            throw new LocalSessionException($"Starting directory not found: {directory}");

        SafeFileHandle? inputRead = null, outputWrite = null;
        var attributes = IntPtr.Zero;
        var environment = IntPtr.Zero;
        try
        {
            if (!CreatePipe(out inputRead, out _inputWrite, IntPtr.Zero, 0)
                || !CreatePipe(out _outputRead, out outputWrite, IntPtr.Zero, 0))
                throw new LocalSessionException("Could not create console pipes.", new Win32Exception());

            var size = new COORD { X = (short)Math.Clamp(columns, 1, short.MaxValue), Y = (short)Math.Clamp(rows, 1, short.MaxValue) };
            var hr = CreatePseudoConsole(size, inputRead, outputWrite, 0, out _console);
            if (hr != 0)
                throw new LocalSessionException($"Could not create the pseudoconsole (HRESULT 0x{hr:x8}).");

            // The console duplicated its ends; ours would only hold the pipe open past exit.
            inputRead.Dispose();
            inputRead = null;
            outputWrite.Dispose();
            outputWrite = null;

            // Attribute list carrying the pseudoconsole into CreateProcess. Per the
            // documented ConPTY usage, the HPCON itself is passed as the value pointer.
            var listSize = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref listSize);
            attributes = Marshal.AllocHGlobal(listSize);
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref listSize)
                || !UpdateProcThreadAttribute(attributes, 0, (IntPtr)PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                    _console, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw new LocalSessionException("Could not prepare the process attributes.", new Win32Exception());

            environment = BuildEnvironmentBlock(target.Environment);

            // STARTF_USESTDHANDLES with null handles is load-bearing (same trick Windows
            // Terminal uses): without it, CreateProcess duplicates any redirected std
            // handles of THIS process into the child, whose output then bypasses the
            // pseudoconsole entirely (observed under test runners and `dotnet run`).
            // With it, the console client opens its console's own in/out on attach.
            var startup = new STARTUPINFOEX
            {
                StartupInfo = { cb = Marshal.SizeOf<STARTUPINFOEX>(), dwFlags = STARTF_USESTDHANDLES },
                lpAttributeList = attributes,
            };
            var commandLine = new StringBuilder(Quote(executable));
            foreach (var argument in target.Arguments)
                commandLine.Append(' ').Append(Quote(Environment.ExpandEnvironmentVariables(argument)));

            // Suspended so the whole tree is inside the kill-on-close job before it runs.
            var flags = EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT | CREATE_SUSPENDED;
            if (!CreateProcessW(null, commandLine, IntPtr.Zero, IntPtr.Zero, false, flags,
                    environment, directory, ref startup, out var process))
            {
                var error = new Win32Exception();
                throw new LocalSessionException($"Could not start {executable}: {error.Message}", error);
            }

            _process = process.hProcess;
            _processId = process.dwProcessId;

            _job = CreateJobObjectW(IntPtr.Zero, null);
            if (_job != IntPtr.Zero)
            {
                var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
                {
                    BasicLimitInformation = { LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE },
                };
                SetInformationJobObject(_job, JobObjectExtendedLimitInformation,
                    ref limits, Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>());
                AssignProcessToJobObject(_job, _process);
            }
            ResumeThread(process.hThread);
            CloseHandle(process.hThread);

            _lastOutputTicks = Environment.TickCount64;
            _inputStream = new FileStream(_inputWrite!, FileAccess.Write);
            _readerThread = new Thread(() => ReadLoop(_outputRead!)) { IsBackground = true, Name = "conpty-read" };
            _readerThread.Start();
            _exitThread = new Thread(WaitForExit) { IsBackground = true, Name = "conpty-wait" };
            _exitThread.Start();
            TraceHook?.Invoke($"local start: pid {_processId} {commandLine}");
        }
        catch
        {
            inputRead?.Dispose();
            outputWrite?.Dispose();
            Cleanup();
            throw;
        }
        finally
        {
            if (attributes != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(attributes);
                Marshal.FreeHGlobal(attributes);
            }
            if (environment != IntPtr.Zero)
                Marshal.FreeHGlobal(environment);
        }
    }

    private void ReadLoop(SafeFileHandle outputRead)
    {
        var buffer = new byte[32 * 1024];
        try
        {
            using var stream = new FileStream(outputRead, FileAccess.Read);
            while (true)
            {
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                    break; // conhost closed its write end: the console is gone
                Interlocked.Exchange(ref _lastOutputTicks, Environment.TickCount64);
                if (!_disposed)
                    OutputReceived?.Invoke(buffer[..read]);
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // Pipe torn down during Stop/Dispose, or broken by console teardown.
        }
        TraceHook?.Invoke($"local read loop exit: pid {_processId}");
    }

    private void WaitForExit()
    {
        WaitForSingleObject(_process, INFINITE);
        GetExitCodeProcess(_process, out var code);
        // conhost renders the client's final output asynchronously; closing the console too
        // early drops it (observed: a fast `cmd /c echo` lost its line). Wait for the output
        // stream to go quiet, then close — which EOFs the reader — and drain it, so the exit
        // notice lands after the process's final output.
        var deadline = Environment.TickCount64 + 2000;
        while (!_disposed && Environment.TickCount64 < deadline
            && Environment.TickCount64 - Interlocked.Read(ref _lastOutputTicks) < 200)
        {
            Thread.Sleep(50);
        }
        CloseConsole();
        try { _readerThread?.Join(TimeSpan.FromSeconds(5)); } catch (ThreadStateException) { }
        if (Interlocked.Exchange(ref _exitRaised, 1) == 0 && !_disposed)
        {
            TraceHook?.Invoke($"local exit: pid {_processId} code {code}");
            Exited?.Invoke(unchecked((int)code));
        }
    }

    /// <summary>Closes the pseudoconsole exactly once (exit path and Stop/Dispose both land here).</summary>
    private void CloseConsole()
    {
        IntPtr console;
        lock (_consoleGate)
        {
            console = _console;
            _console = IntPtr.Zero;
        }
        if (console != IntPtr.Zero)
            ClosePseudoConsole(console);
    }

    private readonly object _consoleGate = new();

    public void Write(byte[] data)
    {
        try
        {
            var stream = _inputStream;
            if (stream is null || _disposed)
                return;
            stream.Write(data, 0, data.Length);
            stream.Flush();
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException) { }
    }

    public void Resize(int columns, int rows)
    {
        lock (_consoleGate)
        {
            if (_disposed || _console == IntPtr.Zero)
                return;
            ResizePseudoConsole(_console, new COORD
            {
                X = (short)Math.Clamp(columns, 1, short.MaxValue),
                Y = (short)Math.Clamp(rows, 1, short.MaxValue),
            });
        }
    }

    /// <summary>
    /// Names of the processes currently inside this shell's job object — the shell itself
    /// plus everything it started. Agent awareness (Phase 6.2) uses this as the local
    /// identity signal: job membership is authoritative in both directions, unlike a
    /// prompt guess, and it costs one kernel call plus a name lookup per process.
    /// Empty when the shell is gone or the query fails; never throws.
    /// </summary>
    public IReadOnlyList<string> GetJobProcessNames()
    {
        var ids = GetJobProcessIds();
        if (ids.Count == 0)
            return [];
        var names = new List<string>(ids.Count);
        foreach (var id in ids)
        {
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(id);
                names.Add(process.ProcessName);
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException)
            {
                // Exited between the job snapshot and the lookup.
            }
        }
        return names;
    }

    private IReadOnlyList<int> GetJobProcessIds()
    {
        lock (_jobGate)
        {
            if (_disposed || _job == IntPtr.Zero)
                return [];
            // The list is variable length; ask with room for a typical shell tree and grow
            // once if the kernel says there are more.
            for (var capacity = 32; capacity <= 512; capacity *= 4)
            {
                var size = (2 * sizeof(uint)) + (capacity * IntPtr.Size);
                var buffer = Marshal.AllocHGlobal(size);
                try
                {
                    if (!QueryInformationJobObject(_job, JobObjectBasicProcessIdList, buffer, size, IntPtr.Zero))
                        return [];
                    var assigned = (int)(uint)Marshal.ReadInt32(buffer);
                    var returned = (int)(uint)Marshal.ReadInt32(buffer, sizeof(uint));
                    if (returned < assigned && assigned <= 512)
                        continue; // buffer was too small for the whole tree
                    var ids = new List<int>(returned);
                    for (var i = 0; i < returned; i++)
                    {
                        var value = Marshal.ReadIntPtr(buffer, (2 * sizeof(uint)) + (i * IntPtr.Size));
                        ids.Add((int)value);
                    }
                    return ids;
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            return [];
        }
    }

    private readonly object _jobGate = new();

    /// <summary>Kills the process tree without raising <see cref="Exited"/> (a user-initiated
    /// stop is reported by the caller). Idempotent; also the Dispose path.</summary>
    public void Stop()
    {
        if (_disposed)
            return;
        _disposed = true;
        Interlocked.Exchange(ref _exitRaised, 1);
        Cleanup();
    }

    private void Cleanup()
    {
        // Order: kill the tree, close the console (conhost exits), then release the pipes.
        if (_job != IntPtr.Zero)
            TerminateJobObject(_job, 1);
        else if (_process != IntPtr.Zero)
            TerminateProcess(_process, 1);
        CloseConsole();
        try { _inputStream?.Dispose(); } catch (IOException) { }
        _inputStream = null;
        _inputWrite?.Dispose();
        _inputWrite = null;
        _outputRead?.Dispose(); // unblocks the reader thread
        _outputRead = null;
        lock (_jobGate)
        {
            if (_job != IntPtr.Zero)
            {
                CloseHandle(_job);
                _job = IntPtr.Zero;
            }
        }
        if (_process != IntPtr.Zero)
        {
            CloseHandle(_process);
            _process = IntPtr.Zero;
        }
    }

    public void Dispose() => Stop();

    // ---- command line and environment ----

    /// <summary>Standard Windows argument quoting (CommandLineToArgvW rules).</summary>
    internal static string Quote(string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '"']) < 0)
            return argument;
        var sb = new StringBuilder(argument.Length + 2).Append('"');
        var backslashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }
            if (c == '"')
            {
                sb.Append('\\', backslashes * 2 + 1).Append('"');
                backslashes = 0;
                continue;
            }
            sb.Append('\\', backslashes).Append(c);
            backslashes = 0;
        }
        return sb.Append('\\', backslashes * 2).Append('"').ToString();
    }

    /// <summary>Inherited environment plus the profile's overrides (empty value = remove),
    /// as a native Unicode block. Zero when there are no overrides (inherit directly).</summary>
    private static IntPtr BuildEnvironmentBlock(IReadOnlyDictionary<string, string>? overrides)
    {
        if (overrides is null || overrides.Count == 0)
            return IntPtr.Zero;

        var merged = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && key.Length > 0)
                merged[key] = entry.Value as string ?? "";
        }
        foreach (var (key, value) in overrides)
        {
            if (string.IsNullOrWhiteSpace(key))
                continue;
            if (string.IsNullOrEmpty(value))
                merged.Remove(key.Trim());
            else
                merged[key.Trim()] = Environment.ExpandEnvironmentVariables(value);
        }

        var block = new StringBuilder();
        foreach (var (key, value) in merged)
            block.Append(key).Append('=').Append(value).Append('\0');
        // StringToHGlobalUni appends the final terminator, completing the double null.
        return Marshal.StringToHGlobalUni(block.ToString());
    }

    // ---- interop ----

    private const uint PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;
    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const uint CREATE_SUSPENDED = 0x00000004;
    private const int STARTF_USESTDHANDLES = 0x00000100;
    private const int JobObjectExtendedLimitInformation = 9;
    private const int JobObjectBasicProcessIdList = 3;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
    private const uint INFINITE = 0xFFFFFFFF;

    [StructLayout(LayoutKind.Sequential)]
    private struct COORD
    {
        public short X;
        public short Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(
        out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe, IntPtr lpPipeAttributes, int nSize);

    [DllImport("kernel32.dll")]
    private static extern int CreatePseudoConsole(
        COORD size, SafeFileHandle hInput, SafeFileHandle hOutput, uint dwFlags, out IntPtr phPC);

    [DllImport("kernel32.dll")]
    private static extern int ResizePseudoConsole(IntPtr hPC, COORD size);

    [DllImport("kernel32.dll")]
    private static extern void ClosePseudoConsole(IntPtr hPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(
        IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(
        IntPtr lpAttributeList, uint dwFlags, IntPtr attribute, IntPtr lpValue,
        IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(
        string? lpApplicationName, StringBuilder lpCommandLine, IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes, bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment,
        string? lpCurrentDirectory, ref STARTUPINFOEX lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(
        IntPtr hJob, int jobObjectInfoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpJobObjectInfo,
        int cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryInformationJobObject(
        IntPtr hJob, int jobObjectInfoClass, IntPtr lpJobObjectInfo, int cbJobObjectInfoLength,
        IntPtr lpReturnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateJobObject(IntPtr hJob, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr hThread);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
