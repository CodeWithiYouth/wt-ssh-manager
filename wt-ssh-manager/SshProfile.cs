using System.Text.Json.Serialization;

namespace WtSshManager;

public sealed class SshProfile
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public string Group { get; set; } = "默认";
    public string Username { get; set; } = string.Empty;
    public string StartupCommand { get; set; } = string.Empty;
    public int? Port { get; set; }
    public string IdentityFile { get; set; } = string.Empty;
    public string ProtectedPassword { get; set; } = string.Empty;
    public bool IsFavorite { get; set; }
    public DateTime? LastConnectedUtc { get; set; }

    [JsonIgnore]
    public string Password { get; set; } = string.Empty;

    public bool HasSavedPassword =>
        !string.IsNullOrEmpty(Password) || !string.IsNullOrEmpty(ProtectedPassword);

    public string PortLabel => Port?.ToString() ?? "SSH 配置";
    public string LastConnectedLabel => LastConnectedUtc is DateTime lastConnected
        ? $"最近打开：{lastConnected.ToLocalTime():yyyy-MM-dd HH:mm}"
        : "尚未打开";
    public string FavoriteLabel => IsFavorite ? "★ 收藏" : string.Empty;
    public string AuthenticationLabel => HasSavedPassword
        ? "密码自动登录（DPAPI）"
        : string.IsNullOrWhiteSpace(IdentityFile) ? "OpenSSH 默认认证" : "指定密钥";

    public SshProfile Clone() => new()
    {
        Id = Id,
        Name = Name,
        Host = Host,
        Group = Group,
        Username = Username,
        StartupCommand = StartupCommand,
        Port = Port,
        IdentityFile = IdentityFile,
        ProtectedPassword = ProtectedPassword,
        IsFavorite = IsFavorite,
        LastConnectedUtc = LastConnectedUtc,
        Password = Password
    };
}

