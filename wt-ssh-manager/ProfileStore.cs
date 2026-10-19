using System.IO;
using System.Text.Json;

namespace WtSshManager;

public sealed class ProfileStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _profilePath;

    public ProfileStore(string? profilePath = null)
    {
        if (!string.IsNullOrWhiteSpace(profilePath))
        {
            _profilePath = Path.GetFullPath(profilePath);
            return;
        }

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(appData))
        {
            appData = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        _profilePath = Path.Combine(appData, "WtSshManager", "profiles.json");
    }

    public string ProfilePath => _profilePath;

    public List<SshProfile> Load()
    {
        var profiles = ReadStoredProfiles();
        var needsIdMigration = profiles.Any(profile => profile.Id == Guid.Empty);
        if (needsIdMigration)
        {
            foreach (var profile in profiles.Where(profile => profile.Id == Guid.Empty))
            {
                profile.Id = Guid.NewGuid();
            }

            Save(profiles);
        }

        return profiles;
    }

    public SshProfile Load(Guid profileId, bool decryptPassword = true)
    {
        var profile = ReadStoredProfiles().FirstOrDefault(item => item.Id == profileId)
            ?? throw new InvalidDataException("找不到对应的 SSH 连接档案。");

        if (decryptPassword && !string.IsNullOrEmpty(profile.ProtectedPassword))
        {
            profile.Password = SecretProtector.Unprotect(profile.ProtectedPassword);
        }

        return profile;
    }

    private List<SshProfile> ReadStoredProfiles()
    {
        if (!File.Exists(_profilePath))
        {
            return [];
        }

        try
        {
            var profiles = JsonSerializer.Deserialize<List<SshProfile>>(
                File.ReadAllText(_profilePath),
                SerializerOptions);

            var validProfiles = profiles?
                .Where(profile =>
                    profile is not null &&
                    !string.IsNullOrWhiteSpace(profile.Name) &&
                    !string.IsNullOrWhiteSpace(profile.Host))
                .ToList() ?? [];

            foreach (var profile in validProfiles)
            {
                profile.Group = string.IsNullOrWhiteSpace(profile.Group) ? "默认" : profile.Group;
                profile.Username ??= string.Empty;
                profile.IdentityFile ??= string.Empty;
                profile.ProtectedPassword ??= string.Empty;
            }

            return validProfiles;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("连接档案文件不是有效的 JSON。", exception);
        }
    }

    public void Save(IEnumerable<SshProfile> profiles)
    {
        var directory = Path.GetDirectoryName(_profilePath)
            ?? throw new InvalidOperationException("无法确定档案目录。");

        Directory.CreateDirectory(directory);
        var temporaryPath = _profilePath + ".tmp";

        try
        {
            var preparedProfiles = profiles.Select(profile =>
            {
                if (profile.Id == Guid.Empty)
                {
                    profile.Id = Guid.NewGuid();
                }

                var storedProfile = profile.Clone();
                storedProfile.Password = string.Empty;
                storedProfile.ProtectedPassword = !string.IsNullOrEmpty(profile.Password)
                    ? SecretProtector.Protect(profile.Password)
                    : profile.ProtectedPassword;
                return (Source: profile, Stored: storedProfile);
            }).ToList();

            var json = JsonSerializer.Serialize(preparedProfiles.Select(item => item.Stored), SerializerOptions);
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, _profilePath, overwrite: true);

            foreach (var item in preparedProfiles)
            {
                item.Source.ProtectedPassword = item.Stored.ProtectedPassword;
                item.Source.Password = string.Empty;
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}

