using System.Text;
using PasswordManager.Core.Profiles;
using PasswordManager.Core.Vault;

namespace PasswordManager.Core.Tests.Profiles;

public sealed class ProfileRepositoryTests : IDisposable
{
    private const string GoodId = "0b6f7c1e-2f5d-4a6b-9a8e-3c1d2e4f5a6b";
    private const string GoodCreatedAt = "2026-01-01T12:00:00.000Z";
    private const string CorruptMessage = "Profile list is corrupt.";

    // W-03 test file F1 = preamble | slotCount 1 | Slot1 (108 bytes); Vault adds 40 body bytes (nonce 24 + tag 16),
    // the minimum that passes VaultHeader.Parse (148 bytes).
    private static readonly byte[] Aad1 = Convert.FromHexString("504d5646010001620001000400000200000001000102030405060708090a0b0c0d0e0f");
    private static readonly byte[] WrapNonce = [.. Enumerable.Range(0x40, 24).Select(i => (byte)i)];
    private static readonly byte[] W1 = Convert.FromHexString("2e8a17f179bdf5a651b7b489c0ce899222020515738dd5df49db643b04d135cc002785a15a71b4953a359d825d34a848");
    private static readonly byte[] Preamble = Convert.FromHexString("504d56460100");
    private static readonly byte[] Slot1 = [.. Aad1.AsSpan(6), .. WrapNonce, .. W1];
    private static readonly byte[] F1 = [.. Preamble, 0x01, .. Slot1];
    private static readonly byte[] Vault = [.. F1, .. new byte[40]];

    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("pm-profiles-");
    private readonly string _vaults;

    public ProfileRepositoryTests()
    {
        _vaults = Path.Combine(_root.FullName, "vaults");
    }

    private string ProfilesPath => Path.Combine(_vaults, ProfileRepository.ProfilesFileName);

    public void Dispose()
    {
        _root.Delete(recursive: true);
    }

    private ProfileRepository NewRepository() => new(_vaults, new FixedTimeProvider());

    private void WriteProfilesFile(string json)
    {
        Directory.CreateDirectory(_vaults);
        File.WriteAllText(ProfilesPath, json);
    }

    private string[] FolderEntries() =>
        [.. Directory.GetFileSystemEntries(_vaults).Order(StringComparer.Ordinal)];

    private static string[] Expected(params string[] paths) =>
        [.. paths.Order(StringComparer.Ordinal)];

    private static byte[] WithByte(byte[] source, int index, byte value)
    {
        var copy = (byte[])source.Clone();
        copy[index] = value;
        return copy;
    }

    private static string ProfileJson(string id, string? displayNameFragment = null, string? createdAtFragment = null) =>
        $$"""{"profiles":[{"id":"{{id}}"{{displayNameFragment ?? ",\"displayName\":\"Work\""}}{{createdAtFragment ?? ",\"createdAt\":\"" + GoodCreatedAt + "\""}}}]}""";

    public static TheoryData<string, string> CorruptFiles() => new()
    {
        { "empty file", "" },
        { "not json", "not json" },
        { "null root", "null" },
        { "array root", "[]" },
        { "object without profiles", "{}" },
        { "null profiles", """{"profiles":null}""" },
        { "null element", """{"profiles":[null]}""" },
        { "missing displayName", ProfileJson(GoodId, displayNameFragment: "") },
        { "null displayName", ProfileJson(GoodId, displayNameFragment: ",\"displayName\":null") },
        { "whitespace displayName", ProfileJson(GoodId, displayNameFragment: ",\"displayName\":\"   \"") },
        { "missing id", """{"profiles":[{"displayName":"Work","createdAt":"2026-01-01T12:00:00.000Z"}]}""" },
        { "malformed id", ProfileJson("not-a-guid") },
        { "all-zero id", ProfileJson("00000000-0000-0000-0000-000000000000") },
        {
            "ids differing only in case",
            $$"""{"profiles":[{"id":"{{GoodId}}","displayName":"A","createdAt":"{{GoodCreatedAt}}"},{"id":"{{GoodId.ToUpperInvariant()}}","displayName":"B","createdAt":"{{GoodCreatedAt}}"}]}"""
        },
        { "missing createdAt", ProfileJson(GoodId, createdAtFragment: "") },
        { "malformed createdAt", ProfileJson(GoodId, createdAtFragment: ",\"createdAt\":\"yesterday\"") },
    };

    public static TheoryData<string, string, string> InvalidNames() => new()
    {
        { "empty", "", "Profile name is empty." },
        { "whitespace", "   ", "Profile name is empty." },
        { "65 characters", new string('a', 65), "Profile name is too long." },
        { "NUL", "a\u0000b", "Profile name has an invalid character." },
        { "newline", "a\nb", "Profile name has an invalid character." },
        { "C1 control", "a\u0085b", "Profile name has an invalid character." },
        { "lone high surrogate", "\uD800", "Profile name has an invalid character." },
        { "lone low surrogate", "a\uDC00b", "Profile name has an invalid character." },
        { "same name, other case", "work", "Profile name is already in use." },
        { "padded upper case", " WORK ", "Profile name is already in use." },
        { "decomposed duplicate", "Cafe\u0301", "Profile name is already in use." },
    };

    public static TheoryData<string, byte[], string> InvalidVaults() => new()
    {
        { "147 bytes", Vault[..147], "Vault file is truncated." },
        { "bad magic", WithByte(Vault, 0, 0x00), "Vault file has an invalid magic." },
    };

    public static TheoryData<string, string, ProfileNameError> NameChecks() => new()
    {
        { "valid", "Other", ProfileNameError.None },
        { "valid, padded", "  Other  ", ProfileNameError.None },
        { "exactly 64 characters", new string('a', 64), ProfileNameError.None },
        { "empty", "", ProfileNameError.Empty },
        { "whitespace", " \t ".Replace('\t', ' '), ProfileNameError.Empty },
        { "65 characters", new string('a', 65), ProfileNameError.TooLong },
        { "control character", "a\u0000b", ProfileNameError.InvalidCharacter },
        { "control character wins over length", "a\u0000" + new string('a', 70), ProfileNameError.InvalidCharacter },
        { "lone surrogate", "a\uDC00b", ProfileNameError.InvalidCharacter },
        { "duplicate, other case", "WORK", ProfileNameError.Duplicate },
        { "duplicate, decomposed", "Cafe\u0301", ProfileNameError.Duplicate },
    };

    // --- Location ---

    [Fact]
    public void GetDefaultDirectory_Unpackaged_UsesLocalAppData()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PasswordManagement",
            "vaults");

        Assert.Equal(expected, ProfileRepository.GetDefaultDirectory());
    }

    [Fact]
    public void GetDefaultDirectory_Packaged_AppendsVaultsToLocalFolder()
    {
        Assert.Equal(@"C:\Pkg\LocalState\vaults", ProfileRepository.GetDefaultDirectory(@"C:\Pkg\LocalState"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void GetDefaultDirectory_EmptyOrWhitespacePackagedFolder_ThrowsArgument(string folder)
    {
        Assert.Throws<ArgumentException>(() => ProfileRepository.GetDefaultDirectory(folder));
    }

    [Fact]
    public void Constructor_NullDirectory_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() => new ProfileRepository(null!));
    }

    [Fact]
    public void Constructor_EmptyDirectory_ThrowsArgument()
    {
        Assert.Throws<ArgumentException>(() => new ProfileRepository(string.Empty));
    }

    [Fact]
    public void Constructor_RelativeDirectory_ExposesFullPathAndTouchesNothing()
    {
        var repository = new ProfileRepository(Path.Combine("relative", "vaults"));

        Assert.True(Path.IsPathFullyQualified(repository.VaultsDirectory));
        Assert.Equal(Path.GetFullPath(Path.Combine("relative", "vaults")), repository.VaultsDirectory);
        Assert.False(Directory.Exists(repository.VaultsDirectory));
    }

    [Fact]
    public void GetVaultPath_UppercaseParsedGuid_UsesLowercaseFileName()
    {
        var id = Guid.Parse(GoodId.ToUpperInvariant());

        Assert.Equal(Path.Combine(_vaults, GoodId + ".pmvault"), NewRepository().GetVaultPath(id));
    }

    [Fact]
    public void GetVaultPath_EmptyGuid_ThrowsArgument()
    {
        Assert.Throws<ArgumentException>(() => NewRepository().GetVaultPath(Guid.Empty));
    }

    // --- List ---

    [Fact]
    public void List_MissingFolder_ReturnsEmptyAndCreatesNothing()
    {
        Assert.Empty(NewRepository().List());
        Assert.False(Directory.Exists(_vaults));
    }

    [Fact]
    public void List_FolderWithoutFile_ReturnsEmpty()
    {
        Directory.CreateDirectory(_vaults);

        Assert.Empty(NewRepository().List());
        Assert.Empty(FolderEntries());
    }

    [Fact]
    public void List_HandWrittenFile_IgnoresUnknownFieldsAndKeepsTheInstant()
    {
        WriteProfilesFile(
            $$$"""{"version":2,"profiles":[{"id":"{{{GoodId.ToUpperInvariant()}}}","displayName":"Work","createdAt":"2026-01-01T09:00:00.000-03:00","extra":{"a":[1,2]}}]}""");

        var profile = Assert.Single(NewRepository().List());

        Assert.Equal(Guid.Parse(GoodId), profile.Id);
        Assert.Equal("Work", profile.DisplayName);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero), profile.CreatedAt);
    }

    [Theory]
    [MemberData(nameof(CorruptFiles))]
    public void List_CorruptFile_ThrowsStoreExceptionWithoutInnerException(string name, string json)
    {
        Assert.NotNull(name);
        WriteProfilesFile(json);

        var exception = Assert.Throws<ProfileStoreException>(() => NewRepository().List());

        Assert.Equal(CorruptMessage, exception.Message);
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public void List_TwoInstancesOnOneFolder_SecondSeesTheFirstsCreate()
    {
        var first = NewRepository();
        var second = NewRepository();
        Assert.Empty(second.List());

        var created = first.Create("Work", Vault);

        Assert.Equal(created, Assert.Single(second.List()));
    }

    // --- Create ---

    [Fact]
    public void Create_FirstProfile_CreatesFolderVaultAndExactJson()
    {
        var profile = NewRepository().Create("Work", Vault);

        var id = profile.Id.ToString("D");
        Assert.NotEqual(Guid.Empty, profile.Id);
        Assert.Equal("Work", profile.DisplayName);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 12, 0, 0, 123, TimeSpan.Zero), profile.CreatedAt);
        Assert.Equal(TimeSpan.Zero, profile.CreatedAt.Offset);

        var vaultPath = Path.Combine(_vaults, id + ".pmvault");
        Assert.Equal(Expected(ProfilesPath, vaultPath), FolderEntries());
        Assert.Equal(Vault, File.ReadAllBytes(vaultPath));

        var expectedJson = Encoding.UTF8.GetBytes(
            $$"""{"profiles":[{"id":"{{id}}","displayName":"Work","createdAt":"2026-01-01T12:00:00.123Z"}]}""");
        Assert.Equal(expectedJson, File.ReadAllBytes(ProfilesPath));
    }

    [Fact]
    public void Create_TwoProfiles_HaveDistinctIdsInCreationOrderAndNoProfilesBackup()
    {
        var repository = NewRepository();

        var first = repository.Create("Work", Vault);
        var second = repository.Create("Home", Vault);

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal([first, second], repository.List());
        Assert.False(File.Exists(ProfilesPath + ".bak"));
        Assert.False(File.Exists(ProfilesPath + ".tmp"));
    }

    [Fact]
    public void Create_DecomposedPaddedName_IsStoredNormalizedAndTrimmed()
    {
        var repository = NewRepository();

        var profile = repository.Create("  Cafe\u0301  ", Vault);

        Assert.Equal("Caf\u00E9", profile.DisplayName);
        Assert.Equal("Caf\u00E9", Assert.Single(repository.List()).DisplayName);
    }

    [Fact]
    public void Create_NameWithEmoji_RoundTripsThroughList()
    {
        var repository = NewRepository();

        var profile = repository.Create("Caf\u00E9 \U0001F600", Vault);

        Assert.Equal(profile, Assert.Single(repository.List()));
        Assert.Equal("Caf\u00E9 \U0001F600", profile.DisplayName);
    }

    [Fact]
    public void Create_NameOfExactlyMaxLength_IsAccepted()
    {
        var name = new string('a', ProfileRepository.MaxDisplayNameLength);

        var profile = NewRepository().Create(name, Vault);

        Assert.Equal(name, profile.DisplayName);
    }

    [Fact]
    public void Create_VaultBytes_AreNotModified()
    {
        var input = (byte[])Vault.Clone();

        NewRepository().Create("Work", input);

        Assert.Equal(Vault, input);
    }

    [Theory]
    [MemberData(nameof(InvalidNames))]
    public void Create_InvalidName_ThrowsArgumentAndWritesNothing(string name, string displayName, string message)
    {
        Assert.NotNull(name);
        var repository = NewRepository();
        repository.Create("Work", Vault);
        repository.Create("Caf\u00E9", Vault);
        var entriesBefore = FolderEntries();
        var listBefore = File.ReadAllBytes(ProfilesPath);

        var exception = Assert.Throws<ArgumentException>(() => repository.Create(displayName, Vault));

        Assert.Equal("displayName", exception.ParamName);
        Assert.StartsWith(message, exception.Message, StringComparison.Ordinal);
        Assert.Equal(entriesBefore, FolderEntries());
        Assert.Equal(listBefore, File.ReadAllBytes(ProfilesPath));
    }

    [Fact]
    public void Create_InvalidNameOnEmptyRepository_DoesNotCreateTheFolder()
    {
        Assert.Throws<ArgumentException>(() => NewRepository().Create("   ", Vault));

        Assert.False(Directory.Exists(_vaults));
    }

    [Fact]
    public void Create_NullName_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() => NewRepository().Create(null!, Vault));
    }

    [Theory]
    [MemberData(nameof(InvalidVaults))]
    public void Create_InvalidVaultBytes_ThrowsFormatExceptionAndTouchesNothing(string name, byte[] vault, string message)
    {
        Assert.NotNull(name);

        var exception = Assert.Throws<VaultFormatException>(() => NewRepository().Create("Work", vault));

        Assert.Equal(message, exception.Message);
        Assert.False(Directory.Exists(_vaults));
    }

    [Fact]
    public void Create_CorruptList_ThrowsStoreExceptionAndKeepsTheFileUntouched()
    {
        WriteProfilesFile("not json");
        var before = File.ReadAllBytes(ProfilesPath);

        Assert.Throws<ProfileStoreException>(() => NewRepository().Create("Work", Vault));

        Assert.Equal(before, File.ReadAllBytes(ProfilesPath));
        Assert.Equal(Expected(ProfilesPath), FolderEntries());
    }

    [Fact]
    public void Create_ListSaveFails_RemovesTheNewVaultAndKeepsTheList()
    {
        var repository = NewRepository();
        var first = repository.Create("Work", Vault);
        var listBefore = File.ReadAllBytes(ProfilesPath);
        var blockedTemp = ProfilesPath + ".tmp";
        Directory.CreateDirectory(blockedTemp);
        File.WriteAllText(Path.Combine(blockedTemp, "keep.txt"), "x");

        Assert.Throws<UnauthorizedAccessException>(() => repository.Create("Home", Vault));

        Assert.Equal(listBefore, File.ReadAllBytes(ProfilesPath));
        Assert.Equal(
            [repository.GetVaultPath(first.Id)],
            Directory.GetFiles(_vaults, "*.pmvault"));
    }

    // --- ValidateDisplayName ---

    [Theory]
    [MemberData(nameof(NameChecks))]
    public void ValidateDisplayName_Name_ReturnsTheFirstBrokenRule(string name, string displayName, ProfileNameError expected)
    {
        Assert.NotNull(name);
        var repository = NewRepository();
        repository.Create("Work", Vault);
        repository.Create("Caf\u00E9", Vault);

        Assert.Equal(expected, repository.ValidateDisplayName(displayName));
    }

    [Fact]
    public void ValidateDisplayName_RenamingId_SkipsOnlyThatProfile()
    {
        var repository = NewRepository();
        var work = repository.Create("Work", Vault);
        var home = repository.Create("Home", Vault);

        Assert.Equal(ProfileNameError.None, repository.ValidateDisplayName("WORK", work.Id));
        Assert.Equal(ProfileNameError.Duplicate, repository.ValidateDisplayName("WORK", home.Id));
        Assert.Equal(ProfileNameError.Duplicate, repository.ValidateDisplayName("home", work.Id));
    }

    [Fact]
    public void ValidateDisplayName_NullName_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() => NewRepository().ValidateDisplayName(null!));
    }

    [Fact]
    public void ValidateDisplayName_MissingFolder_ChecksAndCreatesNothing()
    {
        Assert.Equal(ProfileNameError.None, NewRepository().ValidateDisplayName("Work"));
        Assert.False(Directory.Exists(_vaults));
    }

    // --- Rename ---

    [Fact]
    public void Rename_ValidName_ChangesOnlyTheNameOfThatProfile()
    {
        var repository = NewRepository();
        var work = repository.Create("Work", Vault);
        var home = repository.Create("Home", Vault);
        var vaultsBefore = new[] { work, home }.Select(p => File.ReadAllBytes(repository.GetVaultPath(p.Id))).ToArray();

        var renamed = repository.Rename(work.Id, "  Office  ");

        Assert.Equal(work with { DisplayName = "Office" }, renamed);
        Assert.Equal([renamed, home], repository.List());
        Assert.Equal(work.Id, renamed.Id);
        Assert.Equal(work.CreatedAt, renamed.CreatedAt);
        Assert.Equal(vaultsBefore[0], File.ReadAllBytes(repository.GetVaultPath(work.Id)));
        Assert.Equal(vaultsBefore[1], File.ReadAllBytes(repository.GetVaultPath(home.Id)));
        Assert.False(File.Exists(ProfilesPath + ".bak"));
        Assert.False(File.Exists(ProfilesPath + ".tmp"));
    }

    [Fact]
    public void Rename_CaseOnlyChangeOfItself_IsAllowed()
    {
        var repository = NewRepository();
        var work = repository.Create("Work", Vault);

        var renamed = repository.Rename(work.Id, "WORK");

        Assert.Equal("WORK", renamed.DisplayName);
        Assert.Equal("WORK", Assert.Single(repository.List()).DisplayName);
    }

    [Fact]
    public void Rename_AnotherProfilesName_ThrowsArgumentAndKeepsTheFile()
    {
        var repository = NewRepository();
        var work = repository.Create("Work", Vault);
        repository.Create("Home", Vault);
        var before = File.ReadAllBytes(ProfilesPath);

        var exception = Assert.Throws<ArgumentException>(() => repository.Rename(work.Id, "home"));

        Assert.Equal("displayName", exception.ParamName);
        Assert.StartsWith("Profile name is already in use.", exception.Message, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(ProfilesPath));
    }

    [Fact]
    public void Rename_UnknownId_ThrowsKeyNotFoundAndKeepsTheFile()
    {
        var repository = NewRepository();
        repository.Create("Work", Vault);
        var before = File.ReadAllBytes(ProfilesPath);

        var exception = Assert.Throws<KeyNotFoundException>(() => repository.Rename(Guid.NewGuid(), "Office"));

        Assert.Equal("Profile not found.", exception.Message);
        Assert.Equal(before, File.ReadAllBytes(ProfilesPath));
    }

    [Fact]
    public void Rename_MissingFolder_ThrowsKeyNotFoundAndCreatesNothing()
    {
        Assert.Throws<KeyNotFoundException>(() => NewRepository().Rename(Guid.NewGuid(), "Office"));

        Assert.False(Directory.Exists(_vaults));
    }

    [Fact]
    public void Rename_NullName_ThrowsArgumentNull()
    {
        var repository = NewRepository();
        var work = repository.Create("Work", Vault);

        Assert.Throws<ArgumentNullException>(() => repository.Rename(work.Id, null!));
    }

    // --- Delete ---

    [Fact]
    public void Delete_ListedProfile_RemovesItFromTheListAndDeletesItsThreeFiles()
    {
        var repository = NewRepository();
        var work = repository.Create("Work", Vault);
        var home = repository.Create("Home", Vault);
        var workPath = repository.GetVaultPath(work.Id);
        var homePath = repository.GetVaultPath(home.Id);
        File.WriteAllBytes(workPath + ".bak", Vault);
        File.WriteAllBytes(workPath + ".tmp", Vault);
        File.WriteAllBytes(homePath + ".bak", Vault);

        var deleted = repository.Delete(work.Id);

        Assert.True(deleted);
        Assert.Equal([home], repository.List());
        Assert.False(File.Exists(workPath));
        Assert.False(File.Exists(workPath + ".bak"));
        Assert.False(File.Exists(workPath + ".tmp"));
        Assert.Equal(Vault, File.ReadAllBytes(homePath));
        Assert.True(File.Exists(homePath + ".bak"));
        Assert.Equal(Expected(ProfilesPath, homePath, homePath + ".bak"), FolderEntries());
    }

    [Fact]
    public void Delete_LastProfile_LeavesAnEmptyList()
    {
        var repository = NewRepository();
        var work = repository.Create("Work", Vault);

        Assert.True(repository.Delete(work.Id));

        Assert.Equal(Encoding.UTF8.GetBytes("""{"profiles":[]}"""), File.ReadAllBytes(ProfilesPath));
        Assert.Empty(repository.List());
        Assert.Equal(Expected(ProfilesPath), FolderEntries());
    }

    [Fact]
    public void Delete_UnlistedIdWithLeftovers_ReturnsFalseAndRemovesTheLeftovers()
    {
        var repository = NewRepository();
        var work = repository.Create("Work", Vault);
        var listBefore = File.ReadAllBytes(ProfilesPath);
        var orphan = Guid.NewGuid();
        var orphanPath = repository.GetVaultPath(orphan);
        File.WriteAllBytes(orphanPath, Vault);
        File.WriteAllBytes(orphanPath + ".bak", Vault);
        File.WriteAllBytes(orphanPath + ".tmp", Vault);

        var deleted = repository.Delete(orphan);

        Assert.False(deleted);
        Assert.Equal(Expected(ProfilesPath, repository.GetVaultPath(work.Id)), FolderEntries());
        Assert.Equal(listBefore, File.ReadAllBytes(ProfilesPath));
    }

    [Fact]
    public void Delete_RetryAfterDeletion_ReturnsFalse()
    {
        var repository = NewRepository();
        var work = repository.Create("Work", Vault);

        Assert.True(repository.Delete(work.Id));
        Assert.False(repository.Delete(work.Id));
    }

    [Fact]
    public void Delete_MissingFolder_ReturnsFalseAndCreatesNothing()
    {
        Assert.False(NewRepository().Delete(Guid.NewGuid()));
        Assert.False(Directory.Exists(_vaults));
    }

    [Fact]
    public void Delete_CorruptList_ThrowsStoreExceptionAndKeepsTheVaultFiles()
    {
        var repository = NewRepository();
        var work = repository.Create("Work", Vault);
        var workPath = repository.GetVaultPath(work.Id);
        File.WriteAllBytes(workPath + ".bak", Vault);
        File.WriteAllText(ProfilesPath, "not json");

        Assert.Throws<ProfileStoreException>(() => repository.Delete(work.Id));

        Assert.Equal(Vault, File.ReadAllBytes(workPath));
        Assert.True(File.Exists(workPath + ".bak"));
        Assert.Equal("not json", File.ReadAllText(ProfilesPath));
    }

    [Fact]
    public void Delete_EmptyId_ThrowsArgument()
    {
        Assert.Throws<ArgumentException>(() => NewRepository().Delete(Guid.Empty));
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        // 2026-01-01T12:00:00.1234567Z: more precision than the file keeps.
        private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero).AddTicks(1234567);

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
