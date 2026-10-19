using System.Text.Json;
using System.Text;
using WtSshManager;

var testRoot = Path.Combine(Path.GetTempPath(), "WtSshManager.StoreTests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(testRoot);

try
{
    const string secret = "test-only-password-42-测试";
    var profilePath = Path.Combine(testRoot, "profiles.json");
    var store = new ProfileStore(profilePath);
    var profile = new SshProfile
    {
        Id = Guid.NewGuid(),
        Name = "Storage verification",
        Host = "example.invalid",
        Group = "Tests",
        Username = "tester",
        Password = secret
    };

    store.Save([profile]);
    var storedJson = File.ReadAllText(profilePath);
    Require(string.IsNullOrEmpty(profile.Password), "save clears the source profile password");
    Require(!storedJson.Contains(secret, StringComparison.Ordinal), "saved JSON contains no plaintext password");
    Require(!storedJson.Contains("\"Password\"", StringComparison.Ordinal), "saved JSON omits the plaintext property");
    Require(!string.IsNullOrEmpty(profile.ProtectedPassword), "save stores a DPAPI ciphertext");
    Pass("password is encrypted on disk and cleared from the source model");

    var listedProfile = store.Load().Single();
    Require(string.IsNullOrEmpty(listedProfile.Password), "list loading does not decrypt the password");
    Require(listedProfile.HasSavedPassword, "list loading retains the saved-password indicator");
    Require(store.Load(profile.Id).Password == secret, "selected-profile loading decrypts the password");
    Pass("profile listing avoids decryption and selected-profile loading round-trips the password");

    var protectedPassword = profile.ProtectedPassword;
    profile.Name = "Storage verification renamed";
    store.Save([profile]);
    Require(profile.ProtectedPassword == protectedPassword, "metadata update preserves existing ciphertext");
    Require(store.Load(profile.Id).Password == secret, "metadata update preserves the password");
    Pass("metadata updates preserve the encrypted password");

    var addedProfile = new SshProfile
    {
        Id = Guid.NewGuid(),
        Name = "Added profile",
        Host = "added.example.invalid",
        Group = "Tests"
    };
    store.Save([profile, addedProfile]);
    Require(store.Load().Count == 2, "saving an additional profile adds it to the list");
    Pass("adding a profile persists it to the store");

    store.Save([profile]);
    Require(store.Load().Single().Id == profile.Id, "saving the remaining profiles removes the deleted profile");
    Pass("removing a profile persists the updated list");

    var queryProfiles = new[]
    {
        new SshProfile
        {
            Name = "api-prod",
            Host = "prod-api.example.invalid",
            Username = "root",
            Group = "Production"
        },
        new SshProfile
        {
            Name = "db-prod",
            Host = "db.example.invalid",
            Username = "postgres",
            Group = "Production",
            IsFavorite = true
        },
        new SshProfile
        {
            Name = "Web dev",
            Host = "dev.example.invalid",
            Username = "alice",
            Group = "Development",
            IsFavorite = true
        }
    };

    Require(
        ProfileQuery.Filter(queryProfiles, " ALICE ", string.Empty, false).Single().Name == "Web dev",
        "search trims and matches usernames without case sensitivity");
    Require(
        ProfileQuery.Filter(queryProfiles, string.Empty, "production", false).Select(item => item.Name).SequenceEqual(["db-prod", "api-prod"]),
        "group filtering is case-insensitive and favorites sort first");
    Require(
        ProfileQuery.Filter(queryProfiles, "prod", "Production", true).Single().Name == "db-prod",
        "search, group, and favorites filters compose");
    Require(
        ProfileQuery.Filter(queryProfiles, "missing", string.Empty, false).Count == 0,
        "an unmatched query returns no profiles");
    Pass("profile search, group, favorite, and sort rules");

    var launchProfile = new SshProfile
    {
        Id = Guid.NewGuid(),
        Name = "Production; blue",
        Host = "example.invalid",
        Username = "tester",
        Password = "launch-password-sentinel",
        ProtectedPassword = "encrypted-launch-sentinel"
    };
    var embeddedProfileJson = ConPtySession.SerializeProfile(launchProfile);
    Require(!embeddedProfileJson.Contains(launchProfile.Password, StringComparison.Ordinal), "the embedded session profile omits the plaintext password");
    Require(embeddedProfileJson.Contains(launchProfile.ProtectedPassword, StringComparison.Ordinal), "the embedded session profile keeps the DPAPI ciphertext");
    Require(launchProfile.Password == "launch-password-sentinel", "profile serialization does not mutate the source password");
    Pass("embedded sessions inherit only the encrypted credential");

    var smokeRunnerPath = Path.GetFullPath(
        "tools/wt-ssh-manager/bin/Debug/net10.0-windows10.0.26100.0/win-x64/askpass/WtSshRunner.exe");
    if (File.Exists(smokeRunnerPath))
    {
        var terminalOutput = new StringBuilder();
        var processExited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var smokeProfile = new SshProfile
        {
            Id = Guid.NewGuid(),
            Name = "ConPTY smoke test",
            Host = "127.0.0.1",
            Username = "tester",
            Port = 1
        };
        using var session = ConPtySession.Start(
            smokeRunnerPath,
            smokeProfile,
            80,
            24,
            text => terminalOutput.Append(text),
            exitCode => processExited.TrySetResult(exitCode));
        session.Resize(100, 30);
        var smokeExitCode = await processExited.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var capturedOutput = terminalOutput.ToString();
        Require(capturedOutput.Contains("ssh", StringComparison.OrdinalIgnoreCase), "the ConPTY output contains the SSH runner result");
        Require(smokeExitCode != 0 && smokeExitCode != -1, "the connection-refused child exits with an SSH failure code");
        Pass("ConPTY launches the runner, forwards terminal output, resizes, and observes exit");
    }

    profile.ProtectedPassword = string.Empty;
    store.Save([profile]);
    Require(!store.Load(profile.Id).HasSavedPassword, "clearing the credential removes saved ciphertext");
    Pass("clearing the saved password removes it from the profile");

    var legacyPath = Path.Combine(testRoot, "legacy.json");
    var legacyProfile = new SshProfile
    {
        Name = "Legacy profile",
        Host = "example.invalid",
        Group = "Tests",
        Username = "tester",
        ProtectedPassword = protectedPassword
    };
    File.WriteAllText(legacyPath, JsonSerializer.Serialize(new[] { legacyProfile }));

    var migratedProfile = new ProfileStore(legacyPath).Load().Single();
    var migratedJson = File.ReadAllText(legacyPath);
    Require(migratedProfile.Id != Guid.Empty, "legacy loading assigns a stable ID");
    Require(migratedProfile.ProtectedPassword == protectedPassword, "migration preserves encrypted password bytes");
    Require(migratedJson.Contains(migratedProfile.Id.ToString(), StringComparison.Ordinal), "migration persists the new ID");
    Require(new ProfileStore(legacyPath).Load(migratedProfile.Id).Password == secret, "migrated profile remains decryptable");
    Pass("legacy profiles migrate IDs without losing their encrypted password");

    var invalidPath = Path.Combine(testRoot, "invalid.json");
    var invalidProfile = new SshProfile
    {
        Id = Guid.NewGuid(),
        Name = "Invalid ciphertext",
        Host = "example.invalid",
        ProtectedPassword = "not-base64"
    };
    File.WriteAllText(invalidPath, JsonSerializer.Serialize(new[] { invalidProfile }));
    try
    {
        _ = new ProfileStore(invalidPath).Load(invalidProfile.Id);
        throw new InvalidOperationException("Invalid ciphertext was accepted.");
    }
    catch (InvalidDataException)
    {
    }

    Pass("malformed encrypted values fail with a profile-data error");
}
finally
{
    if (Directory.Exists(testRoot) && Path.GetFileName(testRoot).Length == 32)
    {
        Directory.Delete(testRoot, recursive: true);
    }
}

static void Require(bool condition, string scenario)
{
    if (!condition)
    {
        throw new InvalidOperationException($"Failed: {scenario}");
    }
}

static void Pass(string scenario) => Console.WriteLine($"PASS: {scenario}");

