using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Windows.UI.Core;
using WindowsTerminalControl.Core;
using WindowsTerminalControl.WinUI;
using NativeConPtySession = WindowsTerminalControl.Core.ConPtySession;

namespace WtSshManager;

public sealed partial class EmbeddedTerminalView : UserControl, IDisposable, IAsyncDisposable
{
    private readonly TaskCompletionSource _viewLoaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _terminalInitialized = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TerminalControl _terminalControl;
    private readonly SemaphoreSlim _pasteGate = new(1, 1);
    private MouseMotionFilteringSession? _session;
    private TerminalSessionConnection? _connection;
    private InputKeyboardSource? _keyboardSource;
    private nint _nativeTerminalWindow;
    private nint _previousNativeWndProc;
    private NativeWndProc? _nativeWndProc;
    private int _disposed;

    public EmbeddedTerminalView()
    {
        InitializeComponent();
        _terminalControl = new TerminalControl(TerminalHostMode.Composition)
        {
            Margin = new Thickness(0),
            MinWidth = 0,
            MinHeight = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        TerminalHost.Children.Add(_terminalControl);
        Loaded += EmbeddedTerminalView_Loaded;
        SizeChanged += EmbeddedTerminalView_SizeChanged;
        _terminalControl.Initialized += TerminalControl_Initialized;
        _terminalControl.InitializationFailed += TerminalControl_InitializationFailed;
        Unloaded += EmbeddedTerminalView_Unloaded;
    }

    public event Action<string>? StatusChanged;

    public void RequestLayout()
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            _terminalControl.UpdateHostBounds();
        }
    }

    public async Task ConnectAsync(SshProfile profile, string runnerPath, string profilePath)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        SetStatus("正在初始化原生终端…");

        await _viewLoaded.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await _terminalInitialized.Task.WaitAsync(TimeSpan.FromSeconds(20));
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var runnerCommand = string.Join(
            " ",
            QuoteWindowsArgument(runnerPath),
            "--connect",
            QuoteWindowsArgument(profile.Id.ToString("D")),
            "--profiles-file",
            QuoteWindowsArgument(profilePath));
        var workingDirectory = Path.GetDirectoryName(Path.GetFullPath(runnerPath)) ?? AppContext.BaseDirectory;
        var processSession = new NativeConPtySession(runnerCommand, workingDirectory);
        var session = new MouseMotionFilteringSession(processSession);
        var connection = new TerminalSessionConnection(session);
        _session = session;
        _connection = connection;

        try
        {
            _terminalControl.Connection = connection;
            _terminalControl.UpdateHostBounds();
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            SetStatus("原生 SSH 终端已就绪");
            _terminalControl.Focus(FocusState.Programmatic);
        }
        catch
        {
            if (ReferenceEquals(_session, session))
            {
                _session = null;
            }

            if (ReferenceEquals(_connection, connection))
            {
                _connection = null;
            }

            connection.Dispose();
            throw;
        }
    }

    private void EmbeddedTerminalView_Loaded(object sender, RoutedEventArgs e)
    {
        _terminalControl.UpdateHostBounds();
        AttachKeyboardSource();
        _viewLoaded.TrySetResult();
    }

    private void EmbeddedTerminalView_Unloaded(object sender, RoutedEventArgs e)
    {
        DetachKeyboardSource();
        UninstallNativePasteHook();
    }

    private void EmbeddedTerminalView_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        _terminalControl.UpdateHostBounds();
    }

    private void TerminalControl_Initialized(object? sender, EventArgs e)
    {
        InstallNativePasteHook();
        _terminalInitialized.TrySetResult();
    }

    private void TerminalControl_InitializationFailed(object? sender, Exception exception)
    {
        _terminalInitialized.TrySetException(exception);
    }

    private void AttachKeyboardSource()
    {
        if (_keyboardSource is not null || XamlRoot is not { } xamlRoot)
        {
            return;
        }

        try
        {
            _keyboardSource = InputKeyboardSource.GetForWindowId(
                xamlRoot.ContentIslandEnvironment.AppWindowId);
            _keyboardSource.KeyDown += InputKeyboardSource_KeyDown;
        }
        catch (Exception exception)
        {
            _keyboardSource = null;
            SetStatus($"终端键盘输入初始化失败：{exception.Message}");
        }
    }

    private void DetachKeyboardSource()
    {
        if (_keyboardSource is not { } source)
        {
            return;
        }

        source.KeyDown -= InputKeyboardSource_KeyDown;
        _keyboardSource = null;
    }

    private void InputKeyboardSource_KeyDown(InputKeyboardSource sender, Microsoft.UI.Input.KeyEventArgs args)
    {
        if (args.VirtualKey != VirtualKey.V ||
            !IsKeyDown(VirtualKey.Control) ||
            _session is null ||
            _nativeTerminalWindow != 0 ||
            !IsActiveTerminal)
        {
            return;
        }

        args.Handled = true;
        _ = PasteClipboardAsync();
    }

    private bool IsActiveTerminal =>
        IsLoaded &&
        Visibility == Visibility.Visible &&
        ActualWidth > 0 &&
        ActualHeight > 0;

    private async Task PasteClipboardAsync()
    {
        if (!await _pasteGate.WaitAsync(0))
        {
            return;
        }

        try
        {
            var content = Clipboard.GetContent();
            if (!content.Contains(StandardDataFormats.Text) || _session is not { } session)
            {
                return;
            }

            var text = await content.GetTextAsync();
            if (text.Length > 0)
            {
                await session.WriteAsync(text, CancellationToken.None);
            }
        }
        catch (Exception exception)
        {
            SetStatus($"粘贴失败：{exception.Message}");
        }
        finally
        {
            _pasteGate.Release();
        }
    }

    private static bool IsKeyDown(VirtualKey key) =>
        (InputKeyboardSource.GetKeyStateForCurrentThread(key) & CoreVirtualKeyStates.Down) != 0;

    private void InstallNativePasteHook()
    {
        if (_nativeTerminalWindow != 0)
        {
            return;
        }

        try
        {
            var field = typeof(TerminalControl).GetField(
                "_terminalWindow",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var terminalWindow = field?.GetValue(_terminalControl) is IntPtr handle ? handle : IntPtr.Zero;
            if (terminalWindow == IntPtr.Zero)
            {
                return;
            }

            _nativeWndProc = NativeTerminalWndProc;
            var previousWndProc = SetWindowLongPtr(
                terminalWindow,
                WindowLongPtrWndProc,
                Marshal.GetFunctionPointerForDelegate(_nativeWndProc));
            if (previousWndProc == IntPtr.Zero)
            {
                _nativeWndProc = null;
                return;
            }

            _nativeTerminalWindow = terminalWindow;
            _previousNativeWndProc = previousWndProc;
        }
        catch (Exception exception)
        {
            _nativeWndProc = null;
            SetStatus($"终端粘贴快捷键初始化失败：{exception.Message}");
        }
    }

    private void UninstallNativePasteHook()
    {
        var terminalWindow = _nativeTerminalWindow;
        var previousWndProc = _previousNativeWndProc;
        if (terminalWindow != IntPtr.Zero && previousWndProc != IntPtr.Zero)
        {
            _ = SetWindowLongPtr(terminalWindow, WindowLongPtrWndProc, previousWndProc);
        }

        _nativeTerminalWindow = IntPtr.Zero;
        _previousNativeWndProc = IntPtr.Zero;
        _nativeWndProc = null;
    }

    private nint NativeTerminalWndProc(nint window, uint message, nint wParam, nint lParam)
    {
        if (_session is not null && IsPasteMessage(message, wParam))
        {
            _ = DispatcherQueue.TryEnqueue(() => _ = PasteClipboardAsync());
            return IntPtr.Zero;
        }

        return CallWindowProc(_previousNativeWndProc, window, message, wParam, lParam);
    }

    private static bool IsPasteMessage(uint message, nint wParam)
    {
        if (message == WmPaste)
        {
            return true;
        }

        return (message == WmKeyDown || message == WmSysKeyDown) &&
               wParam.ToInt64() == VirtualKeyV &&
               IsNativeKeyDown(VirtualKeyControl);
    }

    private static bool IsNativeKeyDown(int key) => (GetKeyState(key) & 0x8000) != 0;

    private static string QuoteWindowsArgument(string value)
    {
        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');
        var backslashes = 0;
        foreach (var character in value)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"')
            {
                builder.Append('\\', backslashes * 2 + 1);
                builder.Append('"');
                backslashes = 0;
                continue;
            }

            builder.Append('\\', backslashes);
            builder.Append(character);
            backslashes = 0;
        }

        builder.Append('\\', backslashes * 2);
        builder.Append('"');
        return builder.ToString();
    }

    private void SetStatus(string message)
    {
        StatusChanged?.Invoke(message);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        DetachKeyboardSource();
        UninstallNativePasteHook();
        var connection = Interlocked.Exchange(ref _connection, null);
        if (connection is not null)
        {
            if (ReferenceEquals(_terminalControl.Connection, connection))
            {
                _terminalControl.Connection = null;
            }

            connection.Dispose();
        }

        Interlocked.Exchange(ref _session, null)?.Dispose();
        _terminalControl.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private sealed class MouseMotionFilteringSession(NativeConPtySession inner) : ITerminalSession
    {
        public event EventHandler<TerminalDataEventArgs>? OutputReceived
        {
            add => inner.OutputReceived += value;
            remove => inner.OutputReceived -= value;
        }

        public event EventHandler<TerminalErrorEventArgs>? Failed
        {
            add => inner.Failed += value;
            remove => inner.Failed -= value;
        }

        public event EventHandler? Exited
        {
            add => inner.Exited += value;
            remove => inner.Exited -= value;
        }

        public Task StartAsync(TerminalSize initialSize, CancellationToken cancellationToken) =>
            inner.StartAsync(initialSize, cancellationToken);

        public ValueTask WriteAsync(string data, CancellationToken cancellationToken) =>
            inner.WriteAsync(MouseMotionFilter.RemoveReports(data), cancellationToken);

        public ValueTask ResizeAsync(TerminalSize size, CancellationToken cancellationToken) =>
            inner.ResizeAsync(size, cancellationToken);

        public void Dispose() => inner.Dispose();
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint NativeWndProc(nint window, uint message, nint wParam, nint lParam);

    private const int WindowLongPtrWndProc = -4;
    private const uint WmKeyDown = 0x0100;
    private const uint WmSysKeyDown = 0x0104;
    private const uint WmPaste = 0x0302;
    private const int VirtualKeyV = 0x56;
    private const int VirtualKeyControl = 0x11;

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint window, int index, nint value);

    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
    private static extern nint CallWindowProc(nint previousWndProc, nint window, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int key);

    private static class MouseMotionFilter
    {
        public static string RemoveReports(string data)
        {
            if (data.Length < 4)
            {
                return data;
            }

            var filtered = new StringBuilder(data.Length);
            for (var index = 0; index < data.Length;)
            {
                if (TryReadSgrOrUrxvtMotion(data, index, out var sequenceLength) ||
                    TryReadLegacyMotion(data, index, out sequenceLength))
                {
                    index += sequenceLength;
                    continue;
                }

                filtered.Append(data[index++]);
            }

            return filtered.ToString();
        }

        private static bool TryReadSgrOrUrxvtMotion(string data, int start, out int length)
        {
            length = 0;
            if (start + 3 >= data.Length || data[start] != '\x1b' || data[start + 1] != '[')
            {
                return false;
            }

            var cursor = start + 2;
            if (data[cursor] == '<')
            {
                cursor++;
            }

            var codeStart = cursor;
            while (cursor < data.Length && char.IsDigit(data[cursor]))
            {
                cursor++;
            }

            if (cursor == codeStart || cursor >= data.Length || data[cursor] != ';' ||
                !int.TryParse(data.AsSpan(codeStart, cursor - codeStart), out var code))
            {
                return false;
            }

            for (var field = 0; field < 2; field++)
            {
                cursor++;
                var fieldStart = cursor;
                while (cursor < data.Length && char.IsDigit(data[cursor]))
                {
                    cursor++;
                }

                if (cursor == fieldStart || cursor >= data.Length || data[cursor] != ';')
                {
                    if (field != 1 || cursor == fieldStart || cursor >= data.Length)
                    {
                        return false;
                    }
                }
            }

            if (cursor >= data.Length || data[cursor] != 'M' || (code & 32) == 0)
            {
                return false;
            }

            length = cursor - start + 1;
            return true;
        }

        private static bool TryReadLegacyMotion(string data, int start, out int length)
        {
            length = 0;
            if (start + 5 >= data.Length || data[start] != '\x1b' || data[start + 1] != '[' ||
                data[start + 2] != 'M')
            {
                return false;
            }

            var button = data[start + 3] - 32;
            if ((button & 32) == 0)
            {
                return false;
            }

            length = 6;
            return true;
        }
    }
}
