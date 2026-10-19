using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WtSshManager;

namespace WtSshRunner;

internal static class Program
{
    private const string AskPassModeVariable = "WTSH_SSH_ASKPASS_MODE";
    private const string ProtectedPasswordVariable = "WTSH_SSH_PASSWORD_PROTECTED";
    private const string ProfileJsonVariable = "WTSH_SSH_PROFILE_JSON";
    private const string TraceVariable = "WTSH_SSH_TRACE";
    private const uint MessageBoxYesNo = 0x00000004;
    private const uint MessageBoxIconWarning = 0x00000030;
    private const uint MessageBoxDefaultButton2 = 0x00000100;
    private const uint MessageBoxSetForeground = 0x00010000;
    private const uint MessageBoxTopmost = 0x00040000;
    private const int MessageBoxYes = 6;
    private static readonly bool TraceEnabled =
        string.Equals(Environment.GetEnvironmentVariable(TraceVariable), "1", StringComparison.Ordinal);

    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
    private static extern int ShowMessageBox(IntPtr window, string text, string caption, uint type);

    private static async Task<int> Main(string[] args)
    {
        if (string.Equals(
                Environment.GetEnvironmentVariable(AskPassModeVariable),
                "1",
                StringComparison.Ordinal))
        {
            return RunAskPass(args.FirstOrDefault() ?? string.Empty);
        }

        var serializedProfile = Environment.GetEnvironmentVariable(ProfileJsonVariable);
        if (!string.IsNullOrWhiteSpace(serializedProfile))
        {
            Environment.SetEnvironmentVariable(ProfileJsonVariable, null);
            try
            {
                var profile = JsonSerializer.Deserialize<SshProfile>(serializedProfile);
                if (profile is null || profile.Id == Guid.Empty || string.IsNullOrWhiteSpace(profile.Host))
                {
                    throw new InvalidDataException("SSH 会话中的连接信息无效。");
                }

                Trace($"Loaded encrypted session profile {profile.Id:D} from the inherited environment.");
                return await RunConnectionAsync(profile);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"SSH 启动失败：{exception.Message}");
                return 1;
            }
        }

        if (args.Length == 4 &&
            string.Equals(args[0], "--connect", StringComparison.Ordinal) &&
            Guid.TryParse(args[1], out var profileId) &&
            string.Equals(args[2], "--profiles-file", StringComparison.Ordinal))
        {
            return await RunConnectionAsync(profileId, args[3]);
        }

        Console.Error.WriteLine("用法：WtSshRunner.exe --connect <profile-id> --profiles-file <path>");
        return 2;
    }

    private static async Task<int> RunConnectionAsync(Guid profileId, string profilePath)
    {
        try
        {
            var normalizedProfilePath = Path.GetFullPath(profilePath);
            Trace($"Loading SSH profile {profileId:D} from '{normalizedProfilePath}'.");
            var profile = new ProfileStore(normalizedProfilePath).Load(profileId, decryptPassword: false);
            return await RunConnectionAsync(profile);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"SSH 启动失败：{exception.Message}");
            return 1;
        }
    }

    private static async Task<int> RunConnectionAsync(SshProfile profile)
    {
        try
        {
            var ssh = FindExecutable("ssh.exe");
            if (ssh is null)
            {
                Console.Error.WriteLine("找不到 OpenSSH 客户端 ssh.exe。");
                return 1;
            }

            var protectedPassword = profile.ProtectedPassword;
            var hasSavedPassword = !string.IsNullOrEmpty(protectedPassword);
            var startInfo = new ProcessStartInfo
            {
                FileName = ssh,
                UseShellExecute = false
            };
            startInfo.Environment.Remove(ProfileJsonVariable);

            startInfo.ArgumentList.Add("-tt");

            if (hasSavedPassword)
            {
                var askPassExecutable = Environment.ProcessPath
                    ?? throw new InvalidOperationException("无法定位 SSH 密码处理组件。");

                startInfo.ArgumentList.Add("-o");
                startInfo.ArgumentList.Add("PreferredAuthentications=password");
                startInfo.ArgumentList.Add("-o");
                startInfo.ArgumentList.Add("PubkeyAuthentication=no");
                startInfo.ArgumentList.Add("-o");
                startInfo.ArgumentList.Add("NumberOfPasswordPrompts=1");

                startInfo.Environment["SSH_ASKPASS"] = askPassExecutable;
                startInfo.Environment["SSH_ASKPASS_REQUIRE"] = "force";
                startInfo.Environment[AskPassModeVariable] = "1";
                startInfo.Environment[ProtectedPasswordVariable] = protectedPassword;
                if (TraceEnabled)
                {
                    startInfo.Environment[TraceVariable] = "1";
                }

                Trace("Launching OpenSSH with DPAPI-protected password and forced askpass.");
            }

            if (TraceEnabled)
            {
                startInfo.ArgumentList.Add("-vvv");
            }

            if (profile.Port is int port)
            {
                startInfo.ArgumentList.Add("-p");
                startInfo.ArgumentList.Add(port.ToString());
            }

            if (!hasSavedPassword && !string.IsNullOrWhiteSpace(profile.IdentityFile))
            {
                startInfo.ArgumentList.Add("-i");
                startInfo.ArgumentList.Add(profile.IdentityFile);
            }

            if (!string.IsNullOrWhiteSpace(profile.Username))
            {
                startInfo.ArgumentList.Add("-l");
                startInfo.ArgumentList.Add(profile.Username);
            }

            startInfo.ArgumentList.Add(profile.Host);
            if (!string.IsNullOrWhiteSpace(profile.StartupCommand))
            {
                startInfo.ArgumentList.Add(profile.StartupCommand);
            }

            using var sshProcess = Process.Start(startInfo);
            if (sshProcess is null)
            {
                Console.Error.WriteLine("无法启动 OpenSSH 客户端。");
                return 1;
            }

            await sshProcess.WaitForExitAsync();
            return sshProcess.ExitCode;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"SSH 启动失败：{exception.Message}");
            return 1;
        }
    }

    private static int RunAskPass(string prompt)
    {
        try
        {
            if (IsHostKeyPrompt(prompt))
            {
                Trace("Displaying the SSH host key confirmation dialog.");
                var accepted = ShowMessageBox(
                    IntPtr.Zero,
                    prompt,
                    "确认 SSH 主机密钥",
                    MessageBoxYesNo | MessageBoxIconWarning | MessageBoxDefaultButton2 |
                    MessageBoxSetForeground | MessageBoxTopmost) == MessageBoxYes;
                return WriteAskPassResponse(accepted ? "yes" : "no");
            }

            if (!IsPasswordPrompt(prompt))
            {
                Trace("Ignoring a non-password askpass prompt.");
                return 1;
            }

            var protectedPassword = Environment.GetEnvironmentVariable(ProtectedPasswordVariable);
            if (string.IsNullOrEmpty(protectedPassword))
            {
                Trace("No protected password was present for this askpass request.");
                return 1;
            }

            var password = SecretProtector.UnprotectBytes(protectedPassword);
            try
            {
                Trace("Decrypted the stored password for one SSH password prompt.");
                return WriteAskPassResponse(password);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(password);
            }
        }
        catch (Exception exception) when (
            exception is InvalidDataException or CryptographicException or IOException or UnauthorizedAccessException)
        {
            Trace($"Askpass could not provide a response: {exception.GetType().Name}.");
            return 1;
        }
    }

    private static int WriteAskPassResponse(string response)
    {
        var responseBytes = Encoding.UTF8.GetBytes(response);
        try
        {
            return WriteAskPassResponse(responseBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(responseBytes);
        }
    }

    private static int WriteAskPassResponse(ReadOnlySpan<byte> response)
    {
        var output = new byte[response.Length + 1];
        response.CopyTo(output);
        output[^1] = (byte)'\n';

        try
        {
            using var standardOutput = Console.OpenStandardOutput();
            standardOutput.Write(output, 0, output.Length);
            standardOutput.Flush();
            return 0;
        }
        catch (IOException)
        {
            return 1;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(output);
        }
    }

    private static bool IsPasswordPrompt(string prompt) =>
        prompt.Contains("password", StringComparison.OrdinalIgnoreCase);

    private static bool IsHostKeyPrompt(string prompt) =>
        prompt.Contains("authenticity of host", StringComparison.OrdinalIgnoreCase) ||
        prompt.Contains("continue connecting", StringComparison.OrdinalIgnoreCase);

    private static void Trace(string message)
    {
        if (TraceEnabled)
        {
            Console.Error.WriteLine($"[WtSshRunner] {message}");
        }
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

