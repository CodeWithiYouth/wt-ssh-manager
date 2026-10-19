using Microsoft.UI.Xaml;

namespace WtSshManager;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        UnhandledException += (_, args) => RecordStartupError(args.Exception);

        try
        {
            InitializeComponent();
        }
        catch (Exception exception)
        {
            RecordStartupError(exception);
            throw;
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            _window = new MainWindow(GetProfilesFilePath());
            _window.Activate();
        }
        catch (Exception exception)
        {
            RecordStartupError(exception);
            throw;
        }
    }

    private static void RecordStartupError(Exception exception)
    {
        try
        {
            var logPath = Path.Combine(Path.GetTempPath(), "WtSshManager-startup.log");
            var exceptionType = exception.GetType();
            var line = exceptionType.GetProperty("LineNumber")?.GetValue(exception);
            var position = exceptionType.GetProperty("LinePosition")?.GetValue(exception);
            File.AppendAllText(
                logPath,
                $"{DateTimeOffset.Now:O}{Environment.NewLine}" +
                $"Type: {exceptionType.FullName}{Environment.NewLine}" +
                $"HRESULT: 0x{exception.HResult:X8}{Environment.NewLine}" +
                $"Message: {exception.Message}{Environment.NewLine}" +
                $"XAML position: {line ?? "?"}:{position ?? "?"}{Environment.NewLine}" +
                $"Inner exception: {exception.InnerException}{Environment.NewLine}" +
                $"Stack: {exception.StackTrace}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
        }
    }

    private static string? GetProfilesFilePath()
    {
        var arguments = Environment.GetCommandLineArgs();
        for (var index = 1; index + 1 < arguments.Length; index++)
        {
            if (string.Equals(arguments[index], "--profiles-file", StringComparison.Ordinal))
            {
                return arguments[index + 1];
            }
        }

        return Environment.GetEnvironmentVariable("WTSH_PROFILES_FILE");
    }
}

