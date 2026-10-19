using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;

namespace WtSshManager;

public sealed partial class MainWindow : Window
{
    private readonly ProfileStore _store;
    private readonly ObservableCollection<SshProfile> _profiles = [];
    private readonly ObservableCollection<SshProfile> _visibleProfiles = [];
    private bool _initialized;
    private bool _refreshingGroups;
    private bool _sidebarCollapsed;

    public MainWindow(string? profilePath = null)
    {
        InitializeComponent();
        _store = new ProfileStore(profilePath);
        AppWindow.Title = "SSH 连接管理器";
        var appIconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "WtSshManager.ico");
        if (File.Exists(appIconPath))
        {
            try
            {
                AppWindow.SetIcon(appIconPath);
            }
            catch (ArgumentException)
            {
                // The in-app vector mark remains available if the window host rejects the icon file.
            }
        }
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1120, 740));
        Closed += MainWindow_Closed;
        TerminalTabs.SizeChanged += TerminalTabs_SizeChanged;
        ProfilesList.ItemsSource = _visibleProfiles;
        _initialized = true;

        LoadProfiles();
        RefreshGroupFilter();
        RefreshProfiles(_profiles.FirstOrDefault());
    }

    private SshProfile? SelectedProfile => ProfilesList.SelectedItem as SshProfile;

    private void LoadProfiles()
    {
        try
        {
            foreach (var profile in _store.Load())
            {
                _profiles.Add(profile);
            }

            StatusText.Text = "档案保存在本机；密码使用当前 Windows 用户的 DPAPI 加密。";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"读取连接档案失败：{exception.Message}";
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_initialized)
        {
            RefreshProfiles();
        }
    }

    private void GroupFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initialized && !_refreshingGroups)
        {
            RefreshProfiles();
        }
    }

    private void ProfileFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (_initialized)
        {
            RefreshProfiles();
        }
    }

    private void ProfilesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateDetails(SelectedProfile);
    }

    private async void AddButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ProfileDialog(this) { XamlRoot = ProfilesList.XamlRoot };
        var dialogResult = await dialog.ShowAsync();
        if (dialog.Result is not { } profile)
        {
            return;
        }

        if (dialogResult != ContentDialogResult.Primary)
        {
            profile.Password = string.Empty;
            return;
        }

        if (NameAlreadyExists(profile.Name))
        {
            profile.Password = string.Empty;
            await ShowMessageAsync("名称重复", "连接名称已存在，请换一个名称。");
            return;
        }

        _profiles.Add(profile);
        if (!SaveProfiles())
        {
            _profiles.Remove(profile);
            profile.Password = string.Empty;
            return;
        }

        RefreshGroupFilter();
        RefreshProfiles(profile);
    }

    private async void EditButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedProfile;
        if (selected is null)
        {
            return;
        }

        SshProfile editableProfile;
        try
        {
            editableProfile = _store.Load(selected.Id);
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("读取档案失败", exception.Message);
            return;
        }

        var dialog = new ProfileDialog(this, editableProfile) { XamlRoot = ProfilesList.XamlRoot };
        var dialogResult = await dialog.ShowAsync();
        editableProfile.Password = string.Empty;
        if (dialog.Result is not { } updatedProfile)
        {
            return;
        }

        if (dialogResult != ContentDialogResult.Primary)
        {
            updatedProfile.Password = string.Empty;
            return;
        }

        if (NameAlreadyExists(updatedProfile.Name, selected))
        {
            updatedProfile.Password = string.Empty;
            await ShowMessageAsync("名称重复", "连接名称已存在，请换一个名称。");
            return;
        }

        var index = _profiles.IndexOf(selected);
        if (index < 0)
        {
            return;
        }

        _profiles[index] = updatedProfile;
        if (!SaveProfiles())
        {
            _profiles[index] = selected;
            updatedProfile.Password = string.Empty;
            RefreshProfiles(selected);
            return;
        }

        RefreshGroupFilter();
        RefreshProfiles(updatedProfile);
    }

    private async void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedProfile;
        if (selected is null)
        {
            return;
        }

        var confirmation = new ContentDialog
        {
            Title = "删除连接",
            Content = $"确定删除连接档案“{selected.Name}”吗？",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = ProfilesList.XamlRoot
        };

        if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var index = _profiles.IndexOf(selected);
        _profiles.Remove(selected);
        if (!SaveProfiles())
        {
            _profiles.Insert(Math.Max(index, 0), selected);
            RefreshGroupFilter();
            RefreshProfiles(selected);
            return;
        }

        RefreshGroupFilter();
        RefreshProfiles();
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProfile is { } profile)
        {
            await ConnectAsync(profile);
        }
    }

    private void SidebarToggleButton_Click(object sender, RoutedEventArgs e)
    {
        _sidebarCollapsed = !_sidebarCollapsed;
        ApplySidebarState();
    }

    private void ApplySidebarState()
    {
        SidebarColumn.Width = new GridLength(_sidebarCollapsed ? 48 : 320);
        SidebarBorder.Padding = _sidebarCollapsed
            ? new Thickness(0, 8, 0, 8)
            : new Thickness(14);
        SidebarBorder.Margin = _sidebarCollapsed
            ? new Thickness(0, 0, 8, 0)
            : new Thickness(20, 14, 8, 14);
        SidebarExpandedLayout.Visibility = _sidebarCollapsed ? Visibility.Collapsed : Visibility.Visible;
        CollapsedRail.Visibility = _sidebarCollapsed ? Visibility.Visible : Visibility.Collapsed;
        SidebarToggleButton.Content = _sidebarCollapsed ? "→" : "☰";
        ToolTipService.SetToolTip(
            SidebarToggleButton,
            _sidebarCollapsed ? "展开连接栏" : "折叠连接栏");
    }

    private async void TerminalTabs_TabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        if (args.Tab.Content is EmbeddedTerminalView terminal)
        {
            await terminal.DisposeAsync();
        }

        TerminalTabs.TabItems.Remove(args.Tab);
        FitTerminalViews();
        UpdateDetails(SelectedProfile);
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        foreach (var tab in TerminalTabs.TabItems.OfType<TabViewItem>())
        {
            if (tab.Content is EmbeddedTerminalView terminal)
            {
                terminal.Dispose();
            }
        }
    }

    private async void ProfilesList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (SelectedProfile is { } profile)
        {
            await ConnectAsync(profile);
        }
    }

    private void CopyPasswordButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProfile is not { HasSavedPassword: true } profile)
        {
            return;
        }

        try
        {
            var password = _store.Load(profile.Id).Password;
            if (password.Length == 0)
            {
                StatusText.Text = "此连接没有保存密码。";
                return;
            }

            var data = new DataPackage();
            data.SetText(password);
            Clipboard.SetContent(data);
            StatusText.Text = "密码已复制到剪贴板。";
        }
        catch (Exception exception) when (exception is COMException or InvalidDataException or CryptographicException)
        {
            StatusText.Text = $"无法复制密码：{exception.Message}";
        }
    }

    private bool NameAlreadyExists(string name, SshProfile? except = null) =>
        _profiles.Any(profile =>
            !ReferenceEquals(profile, except) &&
            string.Equals(profile.Name, name, StringComparison.OrdinalIgnoreCase));

    private bool SaveProfiles()
    {
        try
        {
            _store.Save(_profiles);
            StatusText.Text = "连接档案已加密保存到本机。";
            return true;
        }
        catch (Exception exception)
        {
            StatusText.Text = $"保存连接档案失败：{exception.Message}";
            return false;
        }
    }

    private async Task ConnectAsync(SshProfile profile)
    {
        var runner = Path.Combine(AppContext.BaseDirectory, "askpass", "WtSshRunner.exe");
        if (!File.Exists(runner))
        {
            await ShowMessageAsync("SSH 连接组件缺失", "找不到 askpass 连接组件。请重新构建或发布应用。");
            return;
        }

        if (FindExecutable("ssh.exe") is null)
        {
            await ShowMessageAsync(
                "OpenSSH 不可用",
                "找不到 OpenSSH 客户端 ssh.exe。请在 Windows 可选功能中安装 OpenSSH Client。");
            return;
        }

        var previousLastConnected = profile.LastConnectedUtc;
        var terminal = new EmbeddedTerminalView();
        var tab = new TabViewItem
        {
            Header = profile.Name,
            Content = terminal,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch
        };
        terminal.StatusChanged += message => StatusText.Text = message;
        TerminalTabs.TabItems.Add(tab);
        TerminalTabs.SelectedItem = tab;
        StatusText.Text = $"正在打开内嵌终端：{profile.Name}";
        UpdateDetails(profile);
        FitTerminalViews();

        try
        {
            await terminal.ConnectAsync(profile, runner);
            profile.LastConnectedUtc = DateTime.UtcNow;
            if (!SaveProfiles())
            {
                profile.LastConnectedUtc = previousLastConnected;
                await terminal.DisposeAsync();
                TerminalTabs.TabItems.Remove(tab);
                FitTerminalViews();
                UpdateDetails(profile);
                return;
            }

            StatusText.Text = $"已在应用内启动 SSH 会话：{profile.Name}";
            UpdateDetails(profile);
        }
        catch (Exception exception)
        {
            await terminal.DisposeAsync();
            TerminalTabs.TabItems.Remove(tab);
            profile.LastConnectedUtc = previousLastConnected;
            FitTerminalViews();
            UpdateDetails(profile);
            await ShowMessageAsync("连接失败", exception.Message);
        }
    }

    private void TerminalTabs_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        FitTerminalViews();
    }

    private void LayoutRoot_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        FitTerminalViews();
    }

    private void FitTerminalViews()
    {
        var availableWidth = TerminalTabs.ActualWidth;
        var availableHeight = TerminalTabs.ActualHeight;
        if (!double.IsFinite(availableWidth) || availableWidth <= 0 ||
            !double.IsFinite(availableHeight) || availableHeight <= 0)
        {
            return;
        }

        var viewWidth = Math.Max(0, availableWidth);
        var viewHeight = Math.Max(160, availableHeight - 44);
        foreach (var tab in TerminalTabs.TabItems.OfType<TabViewItem>())
        {
            if (tab.Content is EmbeddedTerminalView terminal)
            {
                terminal.Width = viewWidth;
                terminal.Height = viewHeight;
                terminal.HorizontalAlignment = HorizontalAlignment.Stretch;
                terminal.VerticalAlignment = VerticalAlignment.Stretch;
                terminal.RequestLayout();
            }
        }
    }

    private void RefreshGroupFilter()
    {
        var selectedGroup = (GroupFilter.SelectedItem as ComboBoxItem)?.Tag as string ?? string.Empty;
        _refreshingGroups = true;
        try
        {
            GroupFilter.Items.Clear();
            GroupFilter.Items.Add(new ComboBoxItem { Content = "全部分组", Tag = string.Empty });
            foreach (var group in _profiles
                         .Select(profile => string.IsNullOrWhiteSpace(profile.Group) ? "默认" : profile.Group)
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .OrderBy(group => group, StringComparer.CurrentCultureIgnoreCase))
            {
                GroupFilter.Items.Add(new ComboBoxItem { Content = group, Tag = group });
            }

            GroupFilter.SelectedItem = GroupFilter.Items
                .Cast<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag as string, selectedGroup, StringComparison.OrdinalIgnoreCase))
                ?? GroupFilter.Items[0];
        }
        finally
        {
            _refreshingGroups = false;
        }
    }

    private void RefreshProfiles(SshProfile? preferredSelection = null)
    {
        var selected = preferredSelection ?? SelectedProfile;
        var selectedGroup = (GroupFilter.SelectedItem as ComboBoxItem)?.Tag as string ?? string.Empty;
        var favoritesOnly = FavoritesOnlyCheckBox.IsChecked == true;
        var filtered = ProfileQuery.Filter(_profiles, SearchBox.Text, selectedGroup, favoritesOnly);

        _visibleProfiles.Clear();
        foreach (var profile in filtered)
        {
            _visibleProfiles.Add(profile);
        }

        ProfileCountText.Text = _profiles.Count.ToString();

        ProfilesList.SelectedItem = selected is not null && _visibleProfiles.Contains(selected)
            ? selected
            : null;
        UpdateDetails(SelectedProfile);
    }

    private void UpdateDetails(SshProfile? profile)
    {
        var hasSelection = profile is not null;
        var hasTerminalTabs = TerminalTabs.TabItems.Count > 0;
        TerminalTabs.Visibility = hasTerminalTabs ? Visibility.Visible : Visibility.Collapsed;
        StatusBar.Visibility = hasTerminalTabs ? Visibility.Collapsed : Visibility.Visible;
        StatusText.Visibility = Visibility.Visible;
        EmptyState.Visibility = hasTerminalTabs || hasSelection ? Visibility.Collapsed : Visibility.Visible;
        DetailsPanel.Visibility = !hasTerminalTabs && hasSelection ? Visibility.Visible : Visibility.Collapsed;
        ConnectButton.IsEnabled = hasSelection;
        CopyPasswordButton.IsEnabled = profile?.HasSavedPassword == true;
        EditButton.IsEnabled = hasSelection;
        DeleteButton.IsEnabled = hasSelection;

        if (profile is null)
        {
            return;
        }

        DetailNameText.Text = profile.Name;
        DetailHostText.Text = profile.Host;
        DetailGroupText.Text = $"分组：{profile.Group}";
        DetailUserText.Text = $"用户：{(string.IsNullOrWhiteSpace(profile.Username) ? "SSH 配置默认值" : profile.Username)}";
        DetailPortText.Text = $"端口：{profile.Port?.ToString() ?? "SSH 配置默认值"}";
        DetailAuthenticationText.Text = $"认证：{profile.AuthenticationLabel}";
        DetailLastConnectedText.Text = profile.LastConnectedLabel;
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = "确定",
            XamlRoot = ProfilesList.XamlRoot
        };

        await dialog.ShowAsync();
    }

    private static string? FindExecutable(string executableName)
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            return null;
        }

        foreach (var entry in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = entry.Trim().Trim('"');
            if (directory.Length == 0)
            {
                continue;
            }

            var candidate = Path.Combine(directory, executableName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}

