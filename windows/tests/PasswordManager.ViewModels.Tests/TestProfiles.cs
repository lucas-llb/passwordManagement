using PasswordManager.Core.Profiles;
using PasswordManager.Core.Vault;
using PasswordManager.ViewModels;

namespace PasswordManager.ViewModels.Tests;

/// <summary>
/// A throwaway vaults folder with a <see cref="ProfileRepository"/> on it. Every profile it creates has a real vault
/// file (cheap Argon2id parameters) with fictitious entries. Deleted on <see cref="Dispose"/>.
/// </summary>
internal sealed class TestProfiles : IDisposable
{
    public const string Password = "correct horse battery staple";

    public static readonly KdfParameters LowCost = new(1024, 2);

    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("pm-viewmodels-");

    public TestProfiles()
    {
        Repository = new ProfileRepository(_directory.FullName);
    }

    public ProfileRepository Repository { get; }

    public string DirectoryPath => _directory.FullName;

    /// <summary>Creates a profile with a vault protected by <paramref name="password"/> and holding <paramref name="entries"/>.</summary>
    public Profile CreateProfile(string name, string password, params VaultEntry[] entries)
    {
        var kitText = new char[RecoveryKit.TextLength];
        try
        {
            var file = VaultSession.Create(password, kitText, LowCost);
            var profile = Repository.Create(name, file);

            using var session = new VaultSession(Repository.GetVaultPath(profile.Id), LowCost);
            session.Unlock(password);
            session.Save(new VaultContent { Entries = entries });

            return profile;
        }
        finally
        {
            Array.Clear(kitText);
        }
    }

    /// <summary>Unlocks the vault of a profile created by <see cref="CreateProfile"/>.</summary>
    public VaultSession OpenSession(Profile profile, string password = Password)
    {
        var session = new VaultSession(Repository.GetVaultPath(profile.Id), LowCost);
        session.Unlock(password);
        return session;
    }

    public static VaultEntry Entry(string title, string username = "user@example.com") => new()
    {
        Id = Guid.NewGuid(),
        Title = title,
        Username = username,
        Password = "not-a-real-password",
        Url = "https://example.com",
        Notes = "notes",
        Folder = "Personal",
        CreatedAt = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero),
        UpdatedAt = new DateTimeOffset(2026, 1, 2, 12, 0, 0, TimeSpan.Zero),
    };

    public void Dispose()
    {
        if (Directory.Exists(_directory.FullName))
        {
            _directory.Delete(recursive: true);
        }
    }
}

/// <summary>A page that only records the navigation hooks, in order, into a shared log.</summary>
internal sealed class RecordingPage(string name, List<string> log) : PageViewModel
{
    public override void OnNavigatedTo() => log.Add($"to:{name}");

    public override void OnNavigatedFrom() => log.Add($"from:{name}");
}
