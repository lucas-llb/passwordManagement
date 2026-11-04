using PasswordManager.Core.Preferences;

namespace PasswordManager.ViewModels.Tests;

public sealed class AppSettingsTests : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("pm-settings-");

    public void Dispose()
    {
        if (Directory.Exists(_directory.FullName))
        {
            _directory.Delete(recursive: true);
        }
    }

    private string SettingsPath => Path.Combine(_directory.FullName, PreferencesStore.FileName);

    [Fact]
    public void Constructor_NullOrEmptyFolder_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new AppSettings(null!));
        Assert.Throws<ArgumentException>(() => new AppSettings(string.Empty));
    }

    [Fact]
    public void Constructor_LoadsTheStore()
    {
        PreferencesStore.Save(_directory.FullName, new AppPreferences(15, lockOnMinimize: true));

        var settings = new AppSettings(_directory.FullName);

        Assert.Equal(new AppPreferences(15, lockOnMinimize: true), settings.Current);
    }

    [Fact]
    public void Constructor_NoFile_UsesTheDefaultAndWritesNothing()
    {
        var settings = new AppSettings(_directory.FullName);

        Assert.Equal(AppPreferences.Default, settings.Current);
        Assert.False(File.Exists(SettingsPath));
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(5, true)]
    [InlineData(60, false)]
    [InlineData(15, true)]
    public void AutoLockOptions_MapsMinutesAndFlag(int minutes, bool lockOnMinimize)
    {
        PreferencesStore.Save(_directory.FullName, new AppPreferences(minutes, lockOnMinimize));

        var options = new AppSettings(_directory.FullName).AutoLockOptions;

        Assert.Equal(TimeSpan.FromMinutes(minutes), options.IdleTimeout);
        Assert.Equal(lockOnMinimize, options.LockOnMinimize);
    }

    [Fact]
    public void AutoLockOptions_Default_MatchesTheAutoLockDefault()
    {
        var options = new AppSettings(_directory.FullName).AutoLockOptions;

        Assert.Equal(AutoLockOptions.Default, options);
    }

    [Fact]
    public void Update_RaisesChangedAndSaves()
    {
        var settings = new AppSettings(_directory.FullName);
        var raised = new List<AppPreferences>();
        settings.Changed += (sender, _) =>
        {
            Assert.Same(settings, sender);
            raised.Add(settings.Current);
        };
        var changed = new AppPreferences(30, lockOnMinimize: true);

        var saved = settings.Update(changed);

        Assert.True(saved);
        Assert.Equal(changed, settings.Current);
        Assert.Equal(TimeSpan.FromMinutes(30), settings.AutoLockOptions.IdleTimeout);
        Assert.True(settings.AutoLockOptions.LockOnMinimize);
        Assert.Equal([changed], raised);
        Assert.Equal(changed, PreferencesStore.Load(_directory.FullName));
        Assert.Equal(
            """{"idleTimeoutMinutes":30,"lockOnMinimize":true}""",
            File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void Update_SameValue_RaisesNothingAndWritesNothing()
    {
        var settings = new AppSettings(_directory.FullName);
        var raised = 0;
        settings.Changed += (_, _) => raised++;

        var saved = settings.Update(new AppPreferences(AppPreferences.Default.IdleTimeoutMinutes, AppPreferences.Default.LockOnMinimize));

        Assert.True(saved);
        Assert.Equal(0, raised);
        Assert.False(File.Exists(SettingsPath));
    }

    [Fact]
    public void Update_Null_Throws()
    {
        var settings = new AppSettings(_directory.FullName);

        Assert.Throws<ArgumentNullException>(() => settings.Update(null!));
        Assert.Equal(AppPreferences.Default, settings.Current);
    }

    [Fact]
    public void Update_SaveFails_ReturnsFalseAndKeepsTheValueInMemory()
    {
        // The folder does not exist and the store does not create it, so the write fails with an IOException.
        var missing = Path.Combine(_directory.FullName, "missing");
        var settings = new AppSettings(missing);
        var raised = 0;
        settings.Changed += (_, _) => raised++;
        var changed = new AppPreferences(10, lockOnMinimize: true);

        var saved = settings.Update(changed);

        Assert.False(saved);
        Assert.Equal(changed, settings.Current);
        Assert.Equal(TimeSpan.FromMinutes(10), settings.AutoLockOptions.IdleTimeout);
        Assert.True(settings.AutoLockOptions.LockOnMinimize);
        Assert.Equal(1, raised);
        Assert.False(Directory.Exists(missing));
    }

    [Fact]
    public void Update_AfterAFailedSave_SavesTheNextChange()
    {
        var missing = Path.Combine(_directory.FullName, "later");
        var settings = new AppSettings(missing);
        Assert.False(settings.Update(new AppPreferences(10, lockOnMinimize: false)));

        Directory.CreateDirectory(missing);
        var saved = settings.Update(new AppPreferences(10, lockOnMinimize: true));

        Assert.True(saved);
        Assert.Equal(new AppPreferences(10, lockOnMinimize: true), PreferencesStore.Load(missing));
    }
}
