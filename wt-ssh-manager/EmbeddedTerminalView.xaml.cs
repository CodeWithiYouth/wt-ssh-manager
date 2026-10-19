using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Microsoft.Web.WebView2.Core;

namespace WtSshManager;

public sealed partial class EmbeddedTerminalView : UserControl, IDisposable, IAsyncDisposable
{
    private const string VirtualHostName = "wtsshmanager.local";
    private readonly TaskCompletionSource _viewLoaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _terminalReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _initializeTask;
    private ConPtySession? _session;
    private int _columns = 120;
    private int _rows = 32;
    private int _disposed;

    public EmbeddedTerminalView()
    {
        InitializeComponent();
        Loaded += EmbeddedTerminalView_Loaded;
        SizeChanged += (_, _) => RequestLayout();
    }

    public event Action<string>? StatusChanged;

    public void RequestLayout()
    {
        if (Volatile.Read(ref _disposed) == 0 && TerminalWebView.CoreWebView2 is not null)
        {
            PostMessage(new { type = "fit" });
        }
    }

    public async Task ConnectAsync(SshProfile profile, string runnerPath)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        SetStatus("正在初始化内嵌终端…");

        await _viewLoaded.Task.WaitAsync(TimeSpan.FromSeconds(20));
        _initializeTask ??= InitializeTerminalAsync();
        await _initializeTask;
        await _terminalReady.Task.WaitAsync(TimeSpan.FromSeconds(20));
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        SetStatus("内嵌终端已就绪");

        _session = ConPtySession.Start(
            runnerPath,
            profile,
            _columns,
            _rows,
            Session_OutputReceived,
            Session_ProcessExited);
        SetStatus("SSH 会话已启动");
        PostMessage(new { type = "focus" });
    }

    private void EmbeddedTerminalView_Loaded(object sender, RoutedEventArgs e)
    {
        _initializeTask ??= InitializeTerminalAsync();
        _viewLoaded.TrySetResult();
    }

    private async Task InitializeTerminalAsync()
    {
        await TerminalWebView.EnsureCoreWebView2Async();

        var assetsFolder = Path.Combine(AppContext.BaseDirectory, "TerminalUi");
        if (!Directory.Exists(assetsFolder))
        {
            throw new DirectoryNotFoundException($"找不到内嵌终端资源目录：{assetsFolder}");
        }

        var core = TerminalWebView.CoreWebView2
            ?? throw new InvalidOperationException("WebView2 核心初始化失败。");
        core.SetVirtualHostNameToFolderMapping(
            VirtualHostName,
            assetsFolder,
            CoreWebView2HostResourceAccessKind.DenyCors);
        core.WebMessageReceived += CoreWebView2_WebMessageReceived;
        core.NavigationCompleted += (_, args) =>
        {
            if (!args.IsSuccess)
            {
                SetStatus($"终端页面加载失败：{args.WebErrorStatus}");
            }
        };
        core.WebResourceResponseReceived += (sender, args) =>
        {
            if (args.Response.StatusCode >= 400 &&
                args.Request.Uri.StartsWith($"https://{VirtualHostName}/", StringComparison.OrdinalIgnoreCase))
            {
                var message = $"终端资源加载失败：{new Uri(args.Request.Uri).AbsolutePath} ({args.Response.StatusCode})";
                _ = DispatcherQueue.TryEnqueue(() => SetStatus(message));
            }
        };
        core.Navigate($"https://{VirtualHostName}/index.html");
    }

    private void CoreWebView2_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!e.Source.StartsWith($"https://{VirtualHostName}/", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            using var message = JsonDocument.Parse(e.WebMessageAsJson);
            var root = message.RootElement;
            var type = root.GetProperty("type").GetString();
            switch (type)
            {
                case "ready":
                    _terminalReady.TrySetResult();
                    break;
                case "renderer-error":
                    var error = root.TryGetProperty("message", out var errorMessage)
                        ? errorMessage.GetString()
                        : null;
                    SetStatus($"终端前端错误：{error ?? "未知错误"}");
                    break;
                case "input":
                    if (root.TryGetProperty("data", out var input) && input.GetString() is { } text)
                    {
                        _ = WriteInputAsync(text);
                    }

                    break;
                case "resize":
                    if (root.TryGetProperty("cols", out var columns) &&
                        root.TryGetProperty("rows", out var rows))
                    {
                        _columns = Math.Clamp(columns.GetInt32(), 2, short.MaxValue);
                        _rows = Math.Clamp(rows.GetInt32(), 2, short.MaxValue);
                        _session?.Resize(_columns, _rows);
                    }

                    break;
                case "copy":
                    PostMessage(new { type = "selection" });
                    break;
                case "selection":
                    if (root.TryGetProperty("data", out var selection) &&
                        !string.IsNullOrEmpty(selection.GetString()))
                    {
                        var clipboardData = new DataPackage();
                        clipboardData.SetText(selection.GetString());
                        Clipboard.SetContent(clipboardData);
                        SetStatus("已复制终端选区");
                    }

                    break;
                case "paste":
                    _ = PasteClipboardAsync();
                    break;
            }
        }
        catch (JsonException)
        {
            SetStatus("终端消息格式错误");
        }
    }

    private async Task WriteInputAsync(string text)
    {
        try
        {
            if (_session is { } session)
            {
                await session.WriteInputAsync(text);
            }
        }
        catch (Exception exception) when (exception is ChannelClosedException or ObjectDisposedException)
        {
            _ = DispatcherQueue.TryEnqueue(() => SetStatus("SSH 会话已关闭"));
        }
    }

    private async Task PasteClipboardAsync()
    {
        try
        {
            var content = Clipboard.GetContent();
            if (content.Contains(StandardDataFormats.Text) && _session is { } session)
            {
                await session.WriteInputAsync(await content.GetTextAsync());
            }
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException)
        {
            SetStatus($"读取剪贴板失败：{exception.Message}");
        }
    }

    private void Session_OutputReceived(string text)
    {
        _ = DispatcherQueue.TryEnqueue(() => PostMessage(new { type = "output", data = text }));
    }

    private void Session_ProcessExited(int exitCode)
    {
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            PostMessage(new { type = "output", data = $"\r\n[SSH 进程已退出，代码 {exitCode}]\r\n" });
            SetStatus($"SSH 进程已退出（代码 {exitCode}）");
        });
    }

    private void PostMessage(object message)
    {
        var core = TerminalWebView.CoreWebView2;
        if (core is not null && Volatile.Read(ref _disposed) == 0)
        {
            core.PostWebMessageAsJson(JsonSerializer.Serialize(message));
        }
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

        var session = Interlocked.Exchange(ref _session, null);
        session?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        var session = Interlocked.Exchange(ref _session, null);
        if (session is not null)
        {
            await Task.Run(session.Dispose);
        }
    }

}

