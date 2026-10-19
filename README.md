# Wt SSH Manager

Windows SSH connection manager with encrypted profiles and an embedded terminal for interactive TUI applications such as Cline.

## Features

- WinUI 3 desktop interface with a collapsible connection rail.
- Saved SSH profiles with search, groups and favorites.
- Passwords encrypted with Windows DPAPI for the current Windows user.
- Windows ConPTY session hosted inside the app through a native Windows Terminal-based WinUI control.
- Multiple terminal tabs with resize synchronization, clipboard shortcuts and ANSI/VT support.
- Windows 11 Task Manager-inspired layout with a custom terminal/connection icon.

## Requirements

- Windows 10 1809 or later.
- .NET 10 SDK.
- Windows OpenSSH Client.

## Build

```powershell
dotnet restore wt-ssh-manager/WtSshManager.csproj
dotnet build wt-ssh-manager/WtSshManager.csproj -c Release --no-restore
dotnet run --project wt-ssh-manager.tests/WtSshManager.StoreTests.csproj --no-restore
dotnet publish wt-ssh-manager/WtSshManager.csproj -c Release -o artifacts/wt-ssh-manager --no-restore
```

The published executable is placed in `artifacts/wt-ssh-manager/`. The GitHub Actions workflow runs the same build, regression tests and terminal frontend syntax check on `windows-latest`.

Release ZIPs use a compact layout with the complete self-contained runtime under `app/`; start the application with `app/WtSshManager.exe`. The package keeps only the `zh-CN` and `en-us` resource folders and the required WinUI resource directory. Keep the `app/` directory together because its DLLs and resource files are required by the unpackaged WinUI application.

## Release

Push a version tag to build a ZIP and publish a GitHub Release automatically:

```powershell
git tag v0.1.0
git push origin v0.1.0
```

Tags beginning with `v` run the full build and attach `wt-ssh-manager-<tag>.zip` to the release with generated release notes.

## Security

Profiles are stored under `%APPDATA%/WtSshManager/profiles.json`. Saved passwords are protected with DPAPI CurrentUser and are only decrypted when a connection starts. Do not commit a profile file or plaintext credentials.

For passwordless login, prefer SSH public-key authentication and the Windows OpenSSH agent.

