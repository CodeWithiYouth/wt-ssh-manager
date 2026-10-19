using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace WtSshManager;

public sealed partial class ProfileDialog : ContentDialog
{
    private readonly Window _owner;
    private readonly SshProfile? _profile;

    public SshProfile? Result { get; private set; }

    public ProfileDialog(Window owner, SshProfile? profile = null)
    {
        InitializeComponent();
        _owner = owner;
        _profile = profile;
        Closed += (_, _) => PasswordBox.Password = string.Empty;

        if (profile is not null)
        {
            Title = "编辑 SSH 连接";
            NameBox.Text = profile.Name;
            HostBox.Text = profile.Host;
            GroupBox.Text = profile.Group;
            UsernameBox.Text = profile.Username;
            StartupCommandBox.Text = profile.StartupCommand;
            PortBox.Text = profile.Port?.ToString() ?? string.Empty;
            IdentityFileBox.Text = profile.IdentityFile;
            PasswordBox.Password = profile.Password;
            FavoriteBox.IsChecked = profile.IsFavorite;
        }
    }

    private async void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(_owner));

        try
        {
            var file = await picker.PickSingleFileAsync();
            if (file is not null)
            {
                IdentityFileBox.Text = file.Path;
            }
        }
        catch (Exception exception)
        {
            ShowValidationError($"无法打开文件选择器：{exception.Message}", IdentityFileBox);
        }
    }

    private void SaveButton_Click(ContentDialog sender, ContentDialogButtonClickEventArgs e)
    {
        var name = NameBox.Text.Trim();
        var host = HostBox.Text.Trim();
        var group = GroupBox.Text.Trim();
        var username = UsernameBox.Text.Trim();
        var startupCommand = StartupCommandBox.Text.Trim();
        var portText = PortBox.Text.Trim();
        var identityFile = IdentityFileBox.Text.Trim();

        if (name.Length == 0)
        {
            ShowValidationError("请输入显示名称。", NameBox, e);
            return;
        }

        if (host.Length == 0 ||
            host.StartsWith("-", StringComparison.Ordinal) ||
            host.Any(char.IsWhiteSpace) ||
            host.Contains(';'))
        {
            ShowValidationError("请输入有效的主机地址或 SSH 别名。", HostBox, e);
            return;
        }

        if (username.StartsWith("-", StringComparison.Ordinal) ||
            username.Any(char.IsWhiteSpace) ||
            username.Contains(';'))
        {
            ShowValidationError("用户名不能包含空格、分号或以连字符开头。", UsernameBox, e);
            return;
        }

        int? port = null;
        if (portText.Length > 0)
        {
            if (!int.TryParse(portText, out var parsedPort) || parsedPort is < 1 or > 65535)
            {
                ShowValidationError("端口必须是 1 到 65535 之间的整数。", PortBox, e);
                return;
            }

            port = parsedPort;
        }

        if (identityFile.Length > 0)
        {
            if (identityFile.Contains(';'))
            {
                ShowValidationError("私钥路径不能包含分号。", IdentityFileBox, e);
                return;
            }

            identityFile = Environment.ExpandEnvironmentVariables(identityFile);
            if (identityFile.StartsWith("~", StringComparison.Ordinal))
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                identityFile = Path.Combine(
                    home,
                    identityFile[1..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            }

            try
            {
                identityFile = Path.GetFullPath(identityFile);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                ShowValidationError("私钥路径无效。", IdentityFileBox, e);
                return;
            }

            if (!File.Exists(identityFile))
            {
                ShowValidationError("找不到指定的私钥文件。", IdentityFileBox, e);
                return;
            }
        }

        Result = new SshProfile
        {
            Id = _profile?.Id ?? Guid.NewGuid(),
            Name = name,
            Host = host,
            Group = group.Length == 0 ? "默认" : group,
            Username = username,
            StartupCommand = startupCommand,
            Port = port,
            IdentityFile = identityFile,
            Password = PasswordBox.Password,
            IsFavorite = FavoriteBox.IsChecked == true,
            LastConnectedUtc = _profile?.LastConnectedUtc
        };
    }

    private void ShowValidationError(
        string message,
        Control control,
        ContentDialogButtonClickEventArgs? args = null)
    {
        if (args is not null)
        {
            args.Cancel = true;
        }

        ValidationText.Text = message;
        ValidationText.Visibility = Visibility.Visible;
        control.Focus(FocusState.Programmatic);
    }
}

