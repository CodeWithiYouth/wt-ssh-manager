# WinUI 3 SSH 管理器

一个本机 WinUI 3 小工具，用于管理 SSH 连接档案，并在应用窗口内使用终端。

## 功能

- 新增、编辑和删除 SSH 连接档案。
- 主机字段支持 IP、域名或 SSH 配置中的 Host 别名。
- 支持分组、收藏和按名称、主机、用户名搜索。
- 可填写用户名、端口、私钥文件路径和连接后执行命令。
- 可保存 SSH 密码；密码使用当前 Windows 用户的 DPAPI 加密，并自动用于 OpenSSH 密码认证。
- 左侧连接栏可以折叠，收起后保留展开按钮，方便把空间让给终端。
- 管理器界面采用 Windows 11 任务管理器风格的顶部标题栏、导航侧栏、档案卡片和蓝色主操作色。
- 顶部标题栏、折叠导航栏和操作按钮使用 Segoe Fluent Icons；应用带有终端与连接组合图标。
- 点击“连接”或双击档案，在应用内打开终端标签页；多个 SSH 会话可以同时保留。
- 每个终端标签页的内容就是完整终端区域；复制和粘贴使用终端快捷键 Ctrl+Shift+C/V，标签页关闭按钮负责结束会话。
- 内嵌终端使用 xterm.js 和 Windows ConPTY，支持 ANSI/VT 画面、交互输入、剪贴板快捷键和窗口尺寸同步，可运行 Cline 等 TUI。
- 关闭终端标签页或主窗口时会结束对应 SSH 进程树；首次连接新主机时会显示主机密钥确认框。
- 档案保存在当前 Windows 用户的 %APPDATA%/WtSshManager/profiles.json。

## 运行

需要 Windows 10 1809 或更高版本、.NET 10 SDK、WebView2 Runtime 和 OpenSSH Client。项目使用 Windows App SDK 2.5.1，并以 self-contained 模式包含 Windows App SDK 运行时。终端前端资源已随应用打包，运行时不需要访问网络。

在仓库根目录运行：

    dotnet run --project wt-ssh-manager/WtSshManager.csproj

默认档案位于 `%APPDATA%/WtSshManager/profiles.json`；也可使用 `--profiles-file <path>` 指定档案文件。

构建：

    dotnet build wt-ssh-manager/WtSshManager.csproj

SSH 档案和连接参数回归检查：

    dotnet run --project wt-ssh-manager.tests/WtSshManager.StoreTests.csproj

发布后的可运行目录位于 `publish/`。

## 密码与 SSH 密钥

密码以 DPAPI CurrentUser 方式加密后保存在 `profiles.json` 中，只有当前 Windows 用户能够解密。连接由 Windows OpenSSH 执行；保存密码时，工具通过 OpenSSH askpass 接口自动提供一次密码，不把明文放进命令行或连接档案环境变量。SSH 会话在 Windows ConPTY 中运行，终端字节流通过本地 WebView2 交给 xterm.js 显示。首次连接新主机时会显示主机密钥确认框。

若要免去每次连接输入密码，建议配置 SSH 公钥认证，并将私钥加入 Windows OpenSSH agent。工具的私钥字段是可选的；留空时会使用 SSH 配置或 agent 中的默认设置。工具不会修改服务器的 authorized_keys。

DPAPI 保护的是磁盘上的档案内容；连接期间密码会短暂存在于当前用户的连接进程内存中。它不替代 Windows 账户保护。

SSH 配置别名示例：

    Host llm-server
        HostName example.com
        User demo-user
        IdentityFile ~/.ssh/id_ed25519

保存后，可在工具中将主机填写为 llm-server，端口、用户名和私钥路径留空，让 OpenSSH 使用上述配置。

