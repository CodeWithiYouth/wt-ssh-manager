using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace WtSshManager;

internal sealed class ConPtySession : IDisposable
{
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint CreateSuspended = 0x00000004;
    private const uint Infinite = 0xFFFFFFFF;
    private const uint WaitObject0 = 0;
    private const uint JobObjectExtendedLimitInformationClass = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private static readonly IntPtr PseudoConsoleAttribute = new(0x00020016);
    private static readonly UTF8Encoding Utf8 = new(false);

    private readonly Channel<byte[]> _input = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });
    private readonly object _pseudoConsoleLock = new();

    private IntPtr _pseudoConsole;
    private IntPtr _inputWritePipe;
    private IntPtr _outputReadPipe;
    private IntPtr _processHandle;
    private IntPtr _jobHandle;
    private Task _outputTask = Task.CompletedTask;
    private Task _inputTask = Task.CompletedTask;
    private Task _monitorTask = Task.CompletedTask;
    private int _columns;
    private int _rows;
    private int _disposed;

    private ConPtySession(
        IntPtr pseudoConsole,
        IntPtr inputWritePipe,
        IntPtr outputReadPipe,
        IntPtr processHandle,
        IntPtr jobHandle,
        int columns,
        int rows)
    {
        _pseudoConsole = pseudoConsole;
        _inputWritePipe = inputWritePipe;
        _outputReadPipe = outputReadPipe;
        _processHandle = processHandle;
        _jobHandle = jobHandle;
        _columns = columns;
        _rows = rows;
    }

    public event Action<string>? OutputReceived;
    public event Action<int>? ProcessExited;

    public static string SerializeProfile(SshProfile profile)
    {
        var sessionProfile = profile.Clone();
        sessionProfile.Password = string.Empty;
        return JsonSerializer.Serialize(sessionProfile);
    }

    public static ConPtySession Start(
        string runnerPath,
        SshProfile profile,
        int columns,
        int rows,
        Action<string>? outputReceived = null,
        Action<int>? processExited = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("ConPTY 需要 Windows 10 1809 或更高版本。");
        }

        if (!File.Exists(runnerPath))
        {
            throw new FileNotFoundException("找不到 SSH 会话组件。请重新构建或发布应用。", runnerPath);
        }

        columns = Math.Clamp(columns, 2, short.MaxValue);
        rows = Math.Clamp(rows, 2, short.MaxValue);

        IntPtr inputRead = IntPtr.Zero;
        IntPtr inputWrite = IntPtr.Zero;
        IntPtr outputRead = IntPtr.Zero;
        IntPtr outputWrite = IntPtr.Zero;
        IntPtr pseudoConsole = IntPtr.Zero;
        IntPtr attributeList = IntPtr.Zero;
        IntPtr environmentBlock = IntPtr.Zero;
        IntPtr processHandle = IntPtr.Zero;
        IntPtr threadHandle = IntPtr.Zero;
        IntPtr jobHandle = IntPtr.Zero;
        var attributeListInitialized = false;

        try
        {
            CreatePipePair(out inputRead, out inputWrite);
            CreatePipePair(out outputRead, out outputWrite);

            var result = CreatePseudoConsole(new Coord((short)columns, (short)rows), inputRead, outputWrite, 0, out pseudoConsole);
            if (result < 0)
            {
                Marshal.ThrowExceptionForHR(result);
            }

            CloseHandle(inputRead);
            inputRead = IntPtr.Zero;
            CloseHandle(outputWrite);
            outputWrite = IntPtr.Zero;

            nuint attributeListSize = 0;
            _ = InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeListSize);
            if (attributeListSize == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            attributeList = Marshal.AllocHGlobal(checked((int)attributeListSize));
            if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeListSize))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            attributeListInitialized = true;

            if (!UpdateProcThreadAttribute(
                    attributeList,
                    0,
                    PseudoConsoleAttribute,
                    pseudoConsole,
                    (nuint)IntPtr.Size,
                    IntPtr.Zero,
                    IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            jobHandle = CreateJobObject(IntPtr.Zero, null);
            if (jobHandle == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var jobLimits = new JobObjectExtendedLimitInformation();
            jobLimits.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
            if (!SetInformationJobObject(
                    jobHandle,
                    JobObjectExtendedLimitInformationClass,
                    ref jobLimits,
                    (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            environmentBlock = CreateEnvironmentBlock(SerializeProfile(profile));
            var startupInfo = new StartupInfoEx
            {
                StartupInfo = new StartupInfo { cb = (uint)Marshal.SizeOf<StartupInfoEx>() },
                AttributeList = attributeList
            };
            var commandLine = new StringBuilder(QuoteWindowsArgument(Path.GetFullPath(runnerPath)));
            var workingDirectory = Path.GetDirectoryName(Path.GetFullPath(runnerPath));

            if (!CreateProcess(
                    Path.GetFullPath(runnerPath),
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    false,
                    ExtendedStartupInfoPresent | CreateUnicodeEnvironment | CreateSuspended,
                    environmentBlock,
                    workingDirectory,
                    ref startupInfo,
                    out var processInformation))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            processHandle = processInformation.Process;
            threadHandle = processInformation.Thread;

            if (!AssignProcessToJobObject(jobHandle, processHandle))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            if (ResumeThread(threadHandle) == uint.MaxValue)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            CloseHandle(threadHandle);
            threadHandle = IntPtr.Zero;
            DeleteProcThreadAttributeList(attributeList);
            Marshal.FreeHGlobal(attributeList);
            attributeList = IntPtr.Zero;
            attributeListInitialized = false;
            Marshal.FreeHGlobal(environmentBlock);
            environmentBlock = IntPtr.Zero;

            var session = new ConPtySession(
                pseudoConsole,
                inputWrite,
                outputRead,
                processHandle,
                jobHandle,
                columns,
                rows);
            session.OutputReceived += outputReceived;
            session.ProcessExited += processExited;
            pseudoConsole = IntPtr.Zero;
            inputWrite = IntPtr.Zero;
            outputRead = IntPtr.Zero;
            processHandle = IntPtr.Zero;
            jobHandle = IntPtr.Zero;
            session.StartWorkers();
            return session;
        }
        catch
        {
            if (processHandle != IntPtr.Zero)
            {
                _ = TerminateProcess(processHandle, 1);
                _ = WaitForSingleObject(processHandle, 1000);
            }

            CloseIfOpen(jobHandle);
            ClosePseudoConsoleIfOpen(pseudoConsole);
            CloseIfOpen(inputRead);
            CloseIfOpen(inputWrite);
            CloseIfOpen(outputRead);
            CloseIfOpen(outputWrite);
            CloseIfOpen(threadHandle);
            CloseIfOpen(processHandle);
            if (attributeList != IntPtr.Zero)
            {
                if (attributeListInitialized)
                {
                    DeleteProcThreadAttributeList(attributeList);
                }

                Marshal.FreeHGlobal(attributeList);
            }

            if (environmentBlock != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(environmentBlock);
            }

            throw;
        }
    }

    public async Task WriteInputAsync(string value)
    {
        if (Volatile.Read(ref _disposed) != 0 || value.Length == 0)
        {
            return;
        }

        var bytes = Utf8.GetBytes(value);
        try
        {
            await _input.Writer.WriteAsync(bytes).ConfigureAwait(false);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw;
        }
    }

    public void Resize(int columns, int rows)
    {
        columns = Math.Clamp(columns, 2, short.MaxValue);
        rows = Math.Clamp(rows, 2, short.MaxValue);
        lock (_pseudoConsoleLock)
        {
            if (Volatile.Read(ref _disposed) != 0 || (columns == _columns && rows == _rows))
            {
                return;
            }

            var pseudoConsole = Volatile.Read(ref _pseudoConsole);
            if (pseudoConsole == IntPtr.Zero)
            {
                return;
            }

            var result = ResizePseudoConsole(pseudoConsole, new Coord((short)columns, (short)rows));
            if (result < 0)
            {
                Marshal.ThrowExceptionForHR(result);
            }

            _columns = columns;
            _rows = rows;
        }
    }

    private void StartWorkers()
    {
        _outputTask = Task.Run(ReadOutputLoop);
        _inputTask = Task.Run(WriteInputLoopAsync);
        _monitorTask = Task.Run(MonitorProcessAsync);
    }

    private void ReadOutputLoop()
    {
        var decoder = Utf8.GetDecoder();
        var bytes = new byte[8192];
        var characters = new char[Utf8.GetMaxCharCount(bytes.Length)];
        var outputHandle = Volatile.Read(ref _outputReadPipe);
        if (outputHandle == IntPtr.Zero)
        {
            return;
        }

        while (ReadFile(outputHandle, bytes, (uint)bytes.Length, out var bytesRead, IntPtr.Zero))
        {
            if (bytesRead == 0)
            {
                break;
            }

            var count = decoder.GetChars(bytes, 0, (int)bytesRead, characters, 0, flush: false);
            if (count > 0)
            {
                OutputReceived?.Invoke(new string(characters, 0, count));
            }
        }

        var tailCount = decoder.GetChars(Array.Empty<byte>(), 0, 0, characters, 0, flush: true);
        if (tailCount > 0)
        {
            OutputReceived?.Invoke(new string(characters, 0, tailCount));
        }
    }

    private async Task WriteInputLoopAsync()
    {
        try
        {
            await foreach (var bytes in _input.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                try
                {
                    var inputHandle = Volatile.Read(ref _inputWritePipe);
                    var offset = 0;
                    while (inputHandle != IntPtr.Zero && offset < bytes.Length)
                    {
                        var remaining = bytes.AsSpan(offset).ToArray();
                        if (!WriteFile(
                                inputHandle,
                                remaining,
                                (uint)remaining.Length,
                                out var bytesWritten,
                                IntPtr.Zero))
                        {
                            CryptographicOperations.ZeroMemory(remaining);
                            return;
                        }

                        CryptographicOperations.ZeroMemory(remaining);
                        if (bytesWritten == 0)
                        {
                            return;
                        }

                        offset += (int)bytesWritten;
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(bytes);
                }
            }
        }
        catch (ChannelClosedException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task MonitorProcessAsync()
    {
        var processHandle = Volatile.Read(ref _processHandle);
        if (processHandle == IntPtr.Zero)
        {
            return;
        }

        var waitResult = await Task.Run(() => WaitForSingleObject(processHandle, Infinite)).ConfigureAwait(false);
        if (waitResult != WaitObject0)
        {
            return;
        }

        _ = GetExitCodeProcess(processHandle, out var exitCode);
        _input.Writer.TryComplete();
        ClosePseudoConsoleOnce();
        CloseHandleOnce(ref _jobHandle);
        await Task.WhenAny(_inputTask, Task.Delay(1000)).ConfigureAwait(false);
        await Task.WhenAny(_outputTask, Task.Delay(3000)).ConfigureAwait(false);

        if (Volatile.Read(ref _disposed) == 0)
        {
            ProcessExited?.Invoke(unchecked((int)exitCode));
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _input.Writer.TryComplete();
        CloseHandleOnce(ref _jobHandle);
        ClosePseudoConsoleOnce();

        try
        {
            _ = _monitorTask.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }

        try
        {
            _ = _outputTask.Wait(TimeSpan.FromSeconds(1));
            _ = _inputTask.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
        }

        CloseHandleOnce(ref _inputWritePipe);
        CloseHandleOnce(ref _outputReadPipe);
        CloseHandleOnce(ref _processHandle);
    }

    private void ClosePseudoConsoleOnce()
    {
        lock (_pseudoConsoleLock)
        {
            var pseudoConsole = Interlocked.Exchange(ref _pseudoConsole, IntPtr.Zero);
            if (pseudoConsole != IntPtr.Zero)
            {
                ClosePseudoConsole(pseudoConsole);
            }
        }
    }

    private static IntPtr CreateEnvironmentBlock(string serializedProfile)
    {
        const string variableName = "WTSH_SSH_PROFILE_JSON";
        var environment = GetEnvironmentStrings();
        if (environment == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        var entries = new List<string>();
        try
        {
            var cursor = environment;
            while (true)
            {
                var entry = Marshal.PtrToStringUni(cursor);
                if (string.IsNullOrEmpty(entry))
                {
                    break;
                }

                if (!entry.StartsWith(variableName + "=", StringComparison.OrdinalIgnoreCase))
                {
                    entries.Add(entry);
                }

                cursor = IntPtr.Add(cursor, checked((entry.Length + 1) * sizeof(char)));
            }
        }
        finally
        {
            FreeEnvironmentStrings(environment);
        }

        entries.Add($"{variableName}={serializedProfile}");
        entries.Sort(StringComparer.OrdinalIgnoreCase);
        return Marshal.StringToHGlobalUni(string.Join('\0', entries) + "\0\0");
    }

    private static void CreatePipePair(out IntPtr readPipe, out IntPtr writePipe)
    {
        if (!CreatePipe(out readPipe, out writePipe, IntPtr.Zero, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    private static string QuoteWindowsArgument(string value) => $"\"{value.Replace("\"", "\\\"")}\"";

    private static void CloseIfOpen(IntPtr handle)
    {
        if (handle != IntPtr.Zero)
        {
            CloseHandle(handle);
        }
    }

    private static void ClosePseudoConsoleIfOpen(IntPtr pseudoConsole)
    {
        if (pseudoConsole != IntPtr.Zero)
        {
            ClosePseudoConsole(pseudoConsole);
        }
    }

    private void CloseHandleOnce(ref IntPtr handle)
    {
        var value = Interlocked.Exchange(ref handle, IntPtr.Zero);
        CloseIfOpen(value);
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Coord(short x, short y)
    {
        public readonly short X = x;
        public readonly short Y = y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public uint cb;
        public IntPtr Reserved;
        public IntPtr Desktop;
        public IntPtr Title;
        public uint X;
        public uint Y;
        public uint XSize;
        public uint YSize;
        public uint XCountChars;
        public uint YCountChars;
        public uint FillAttribute;
        public uint Flags;
        public ushort ShowWindow;
        public ushort Reserved2Length;
        public IntPtr Reserved2;
        public IntPtr StandardInput;
        public IntPtr StandardOutput;
        public IntPtr StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public uint ProcessId;
        public uint ThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
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
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out IntPtr readPipe, out IntPtr writePipe, IntPtr securityAttributes, uint size);

    [DllImport("kernel32.dll")]
    private static extern int CreatePseudoConsole(Coord size, IntPtr input, IntPtr output, uint flags, out IntPtr pseudoConsole);

    [DllImport("kernel32.dll")]
    private static extern int ResizePseudoConsole(IntPtr pseudoConsole, Coord size);

    [DllImport("kernel32.dll")]
    private static extern void ClosePseudoConsole(IntPtr pseudoConsole);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(
        IntPtr attributeList,
        int attributeCount,
        uint flags,
        ref nuint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(
        IntPtr attributeList,
        uint flags,
        IntPtr attribute,
        IntPtr value,
        nuint size,
        IntPtr previousValue,
        IntPtr returnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr attributeList);

    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcess(
        string applicationName,
        StringBuilder commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref StartupInfoEx startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", EntryPoint = "GetEnvironmentStringsW", SetLastError = true)]
    private static extern IntPtr GetEnvironmentStrings();

    [DllImport("kernel32.dll", EntryPoint = "FreeEnvironmentStringsW", SetLastError = true)]
    private static extern bool FreeEnvironmentStrings(IntPtr environment);

    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr jobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(
        IntPtr job,
        uint informationClass,
        ref JobObjectExtendedLimitInformation information,
        uint informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(IntPtr file, byte[] buffer, uint bytesToRead, out uint bytesRead, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(IntPtr file, byte[] buffer, uint bytesToWrite, out uint bytesWritten, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}

