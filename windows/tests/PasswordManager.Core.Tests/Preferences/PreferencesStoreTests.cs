using System.Text;
using PasswordManager.Core.Preferences;

namespace PasswordManager.Core.Tests.Preferences;

public sealed class PreferencesStoreTests : IDisposable
{
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("pm-prefs-");

    private string FolderPath => _directory.FullName;

    private string FilePath => Path.Combine(_directory.FullName, PreferencesStore.FileName);

    public void Dispose()
    {
        _directory.Delete(recursive: true);
    }

    private string[] FolderEntries() =>
        [.. Directory.GetFileSystemEntries(_directory.FullName).Order(StringComparer.Ordinal)];

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    public static TheoryData<int, bool> AllowedCombinations()
    {
        var data = new TheoryData<int, bool>();
        foreach (var minutes in AppPreferences.AllowedIdleTimeoutMinutes)
        {
            data.Add(minutes, false);
            data.Add(minutes, true);
        }

        return data;
    }

    public static TheoryData<string, byte[]> CorruptFiles() => new()
    {
        { "empty file", [] },
        { "not json", Utf8("not json") },
        { "open brace", Utf8("{") },
        { "null root", Utf8("null") },
        { "array root", Utf8("[]") },
        { "string root", Utf8("\"5\"") },
        { "number root", Utf8("5.5") },
        { "number too big for int", Utf8("""{"idleTimeoutMinutes":99999999999,"lockOnMinimize":true}""") },
        { "null value", Utf8("""{"idleTimeoutMinutes":null,"lockOnMinimize":true}""") },
        { "idle as string", Utf8("""{"idleTimeoutMinutes":"15","lockOnMinimize":true}""") },
        { "lockOnMinimize as number", Utf8("""{"idleTimeoutMinutes":15,"lockOnMinimize":1}""") },
        { "byte order mark only", Utf8Bom },
        { "binary", [0x00, 0xFF, 0xFE, 0x01, 0x80] },
    };

    [Fact]
    public void Default_IsFiveMinutesAndNoMinimize()
    {
        Assert.Equal(5, AppPreferences.Default.IdleTimeoutMinutes);
        Assert.False(AppPreferences.Default.LockOnMinimize);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(3)]
    [InlineData(61)]
    public void AppPreferences_OutOfSet_Throws(int minutes)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AppPreferences(minutes, false));
    }

    [Fact]
    public void Load_Missing_ReturnsDefaultAndWritesNothing()
    {
        var result = PreferencesStore.Load(FolderPath);

        Assert.Equal(AppPreferences.Default, result);
        Assert.Empty(FolderEntries());
    }

    [Fact]
    public void Load_MissingFolder_ReturnsDefault()
    {
        var missing = Path.Combine(FolderPath, "not-created");

        Assert.Equal(AppPreferences.Default, PreferencesStore.Load(missing));
        Assert.False(Directory.Exists(missing));
    }

    [Theory]
    [MemberData(nameof(AllowedCombinations))]
    public void Save_ThenLoad_RoundTrips(int minutes, bool lockOnMinimize)
    {
        var preferences = new AppPreferences(minutes, lockOnMinimize);

        PreferencesStore.Save(FolderPath, preferences);

        Assert.Equal(preferences, PreferencesStore.Load(FolderPath));
    }

    [Fact]
    public void Save_WritesExactlyTheTwoKeys()
    {
        PreferencesStore.Save(FolderPath, new AppPreferences(15, true));

        Assert.Equal(Utf8("""{"idleTimeoutMinutes":15,"lockOnMinimize":true}"""), File.ReadAllBytes(FilePath));
    }

    [Fact]
    public void Save_LeavesNoBackupOrTemp()
    {
        PreferencesStore.Save(FolderPath, new AppPreferences(5, false));
        PreferencesStore.Save(FolderPath, new AppPreferences(10, true));

        Assert.Equal([FilePath], FolderEntries());
    }

    [Fact]
    public void Save_FolderMissing_ThrowsIOException()
    {
        var missing = Path.Combine(FolderPath, "not-created");

        Assert.ThrowsAny<IOException>(() => PreferencesStore.Save(missing, AppPreferences.Default));
        Assert.False(Directory.Exists(missing));
    }

    [Theory]
    [MemberData(nameof(CorruptFiles))]
    public void Load_Corrupt_ReturnsDefault(string label, byte[] content)
    {
        File.WriteAllBytes(FilePath, content);

        Assert.True(PreferencesStore.Load(FolderPath) == AppPreferences.Default, label);
    }

    [Fact]
    public void Load_Corrupt_DoesNotRewriteTheFile()
    {
        var corrupt = Utf8("""{"idleTimeoutMinutes":3,"lockOnMinimize":"yes"}""");
        File.WriteAllBytes(FilePath, corrupt);
        var entriesBefore = FolderEntries();

        PreferencesStore.Load(FolderPath);

        Assert.Equal(corrupt, File.ReadAllBytes(FilePath));
        Assert.Equal(entriesBefore, FolderEntries());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(3)]
    [InlineData(61)]
    public void Load_IdleOutsideSet_UsesDefaultAndKeepsLockOnMinimize(int minutes)
    {
        File.WriteAllText(FilePath, $$"""{"idleTimeoutMinutes":{{minutes}},"lockOnMinimize":true}""");

        var result = PreferencesStore.Load(FolderPath);

        Assert.Equal(AppPreferences.Default.IdleTimeoutMinutes, result.IdleTimeoutMinutes);
        Assert.True(result.LockOnMinimize);
    }

    [Fact]
    public void Load_MissingKey_UsesItsDefault()
    {
        File.WriteAllText(FilePath, """{"lockOnMinimize":true}""");
        var onlyLock = PreferencesStore.Load(FolderPath);

        File.WriteAllText(FilePath, """{"idleTimeoutMinutes":30}""");
        var onlyIdle = PreferencesStore.Load(FolderPath);

        Assert.Equal(new AppPreferences(AppPreferences.Default.IdleTimeoutMinutes, true), onlyLock);
        Assert.Equal(new AppPreferences(30, AppPreferences.Default.LockOnMinimize), onlyIdle);
    }

    [Fact]
    public void Load_UnknownKeys_AreIgnored()
    {
        File.WriteAllText(
            FilePath,
            """{"idleTimeoutMinutes":15,"lockOnMinimize":true,"profileId":"0b6f7c1e-2f5d-4a6b-9a8e-3c1d2e4f5a6b","lastUsed":"2026-01-01"}""");

        Assert.Equal(new AppPreferences(15, true), PreferencesStore.Load(FolderPath));
    }

    [Fact]
    public void Load_OverMaxSize_ReturnsDefault()
    {
        // Valid JSON padded with spaces past the limit: only the size can make it the default.
        var padded = new byte[PreferencesStore.MaxFileBytes + 1];
        Array.Fill(padded, (byte)' ');
        Utf8("""{"idleTimeoutMinutes":15,"lockOnMinimize":true}""").CopyTo(padded, 0);
        File.WriteAllBytes(FilePath, padded);

        Assert.Equal(AppPreferences.Default, PreferencesStore.Load(FolderPath));
    }

    [Fact]
    public void Load_FileLocked_ReturnsDefault()
    {
        PreferencesStore.Save(FolderPath, new AppPreferences(15, true));

        using (new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Equal(AppPreferences.Default, PreferencesStore.Load(FolderPath));
        }
    }
}
