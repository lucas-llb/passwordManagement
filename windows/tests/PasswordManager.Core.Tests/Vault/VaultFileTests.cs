using PasswordManager.Core.Vault;

namespace PasswordManager.Core.Tests.Vault;

public sealed class VaultFileTests : IDisposable
{
    private const string Password = "correct horse battery staple";

    // Fictitious Vault Key 0x20..0x3F and body nonce 0x60..0x77.
    private static readonly byte[] Vk = [.. Enumerable.Range(0x20, 32).Select(i => (byte)i)];
    private static readonly byte[] Bn = [.. Enumerable.Range(0x60, 24).Select(i => (byte)i)];

    private static readonly KdfParameters LowCost = new(1024, 2);

    // W-03 test files: F1 = preamble | slotCount 1 | Slot1 (108 bytes); F2 = preamble | slotCount 2 | Slot1 | Slot2 (209 bytes).
    private static readonly byte[] Aad1 = Convert.FromHexString("504d5646010001620001000400000200000001000102030405060708090a0b0c0d0e0f");
    private static readonly byte[] Aad2 = Convert.FromHexString("504d5646010002620001000400000200000001000102030405060708090a0b0c0d0e0f");
    private static readonly byte[] WrapNonce = [.. Enumerable.Range(0x40, 24).Select(i => (byte)i)];
    private static readonly byte[] W1 = Convert.FromHexString("2e8a17f179bdf5a651b7b489c0ce899222020515738dd5df49db643b04d135cc002785a15a71b4953a359d825d34a848");
    private static readonly byte[] W2 = Convert.FromHexString("fa2d2e816d758690c33354f04cee974acc4fd17d6fcd7f7e914492971f8c52256ac12581922863d5d3e2b9e93c85a1f6");
    private static readonly byte[] Preamble = Convert.FromHexString("504d56460100");
    private static readonly byte[] Slot1 = [.. Aad1.AsSpan(6), .. WrapNonce, .. W1];
    private static readonly byte[] Slot2 = [.. Aad2.AsSpan(6), .. WrapNonce, .. W2];
    private static readonly byte[] F1 = [.. Preamble, 0x01, .. Slot1];
    private static readonly byte[] F2 = [.. Preamble, 0x02, .. Slot1, .. Slot2];

    private static readonly byte[] C0 = Convert.FromHexString("93052548f103f2a4d923af544fdcf72089107a0fb053d81aed759ce00dcb");
    private static readonly byte[] C2 = Convert.FromHexString("93052548f103f2a4d923af544fdc6c3aa10b7fa64c287cbe96a51fa2167a");
    private static readonly byte[] C1 = Convert.FromHexString(
        "93052548f103f2a4d923af54698346fd55d68e250bd3bf5103135cd708f3b7e30ea51c27439d1d437edd0a5da4856260b3da6f1a3ce40fd132629ccc1fb2e5b2" +
        "31807aee6ac56b3adcc39ff20a65209b0615e4c192e03ac794b02ac9a24f875a92ec93cd9fbafcff9d107775a0637426d8b66d091649974137f55c6a5bb3fa7d" +
        "ec9854d75245f5530942f2a7348cffdd4ff3d15a5815c0cff2b24ad6e300a605d0e3bdadf93b65aa3044415a20dd8d2c1f9b148b07cfc1b206ede799c9954963" +
        "b98464d8b2b432dfebea1ccefdeaa07185a7cd90be735d729b07f283ce8d63fc2b4267572b47cbdc2e21d0bdc4bedfeadb9a3241186593b7ac1a60077157463a" +
        "ed365cb120e5a338425b9d98a8c0efe7cee22cfd114bfdddd1b6ddbd424191ea3d014b47e7e6fc4497a96920c43070b6c6444db0f0539d3ee775fa10272e3507" +
        "318b94f2bc7b40855ff53dac9d44ef5c558ef3d49473f8c5c4a6ea6bb7ea");

    // V0 = F1 || BN || C0 (162 bytes); V1 = F1 || BN || C1 (482 bytes); V2 = F2 || BN || C2 (263 bytes).
    private static readonly byte[] V0 = [.. F1, .. Bn, .. C0];
    private static readonly byte[] V1 = [.. F1, .. Bn, .. C1];
    private static readonly byte[] V2 = [.. F2, .. Bn, .. C2];

    private static readonly VaultEntry SampleEntry = new()
    {
        Id = Guid.Parse("0b6f7c1e-2f5d-4a6b-9a8e-3c1d2e4f5a6b"),
        Title = "Example",
        Username = "user@example.com",
        Password = "not-a-real-password",
        Url = "https://example.com",
        Notes = "notes",
        Folder = "Personal",
        Favorite = true,
        CreatedAt = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero),
        UpdatedAt = new DateTimeOffset(2026, 1, 2, 12, 0, 0, TimeSpan.Zero),
    };

    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("pm-vaultfile-");
    private readonly string _path;

    public VaultFileTests()
    {
        _path = Path.Combine(_directory.FullName, $"{Guid.NewGuid()}.pmvault");
    }

    private string TempPath => _path + VaultFile.TempSuffix;

    private string BackupPath => _path + VaultFile.BackupSuffix;

    public void Dispose()
    {
        _directory.Delete(recursive: true);
    }

    private static byte[] With(byte[] source, int index, params byte[] bytes)
    {
        var copy = (byte[])source.Clone();
        bytes.CopyTo(copy, index);
        return copy;
    }

    public static TheoryData<string, byte[], string> InvalidFiles() => new()
    {
        { "147 bytes", V0[..147], "Vault file is truncated." },
        { "bad magic", With(V0, 0, 0x00), "Vault file has an invalid magic." },
        { "version 2", With(V0, 4, 0x02, 0x00), "Vault format version is not supported." },
    };

    // --- Successful writes ---

    [Fact]
    public void Write_FirstWrite_CreatesOnlyTarget()
    {
        VaultFile.Write(_path, V0);

        Assert.Equal(V0, File.ReadAllBytes(_path));
        Assert.False(File.Exists(BackupPath));
        Assert.False(File.Exists(TempPath));
        Assert.Equal([_path], Directory.GetFileSystemEntries(_directory.FullName));
    }

    [Fact]
    public void Write_SecondWrite_KeepsPreviousVersionAsBackup()
    {
        VaultFile.Write(_path, V0);
        VaultFile.Write(_path, V2);

        Assert.Equal(V2, File.ReadAllBytes(_path));
        Assert.Equal(V0, File.ReadAllBytes(BackupPath));
        Assert.False(File.Exists(TempPath));
    }

    [Fact]
    public void Write_ThirdWrite_OverwritesBackup()
    {
        VaultFile.Write(_path, V0);
        VaultFile.Write(_path, V2);
        VaultFile.Write(_path, V1);

        Assert.Equal(V1, File.ReadAllBytes(_path));
        Assert.Equal(V2, File.ReadAllBytes(BackupPath));
        Assert.False(File.Exists(TempPath));
    }

    [Fact]
    public void Write_StaleTempFile_IsOverwrittenAndRemoved()
    {
        File.WriteAllBytes(TempPath, [.. Enumerable.Repeat((byte)0xAB, 10)]);

        VaultFile.Write(_path, V0);

        Assert.Equal(V0, File.ReadAllBytes(_path));
        Assert.False(File.Exists(TempPath));
    }

    // --- Failed writes ---

    [Theory]
    [MemberData(nameof(InvalidFiles))]
    public void Write_InvalidContentsWithoutTarget_ThrowsAndTouchesNothing(string name, byte[] contents, string message)
    {
        Assert.NotNull(name);

        var exception = Assert.Throws<VaultFormatException>(() => VaultFile.Write(_path, contents));

        Assert.Equal(message, exception.Message);
        Assert.Empty(Directory.GetFileSystemEntries(_directory.FullName));
    }

    [Theory]
    [MemberData(nameof(InvalidFiles))]
    public void Write_InvalidContentsWithTarget_ThrowsAndTouchesNothing(string name, byte[] contents, string message)
    {
        Assert.NotNull(name);
        VaultFile.Write(_path, V0);
        VaultFile.Write(_path, V2);

        var exception = Assert.Throws<VaultFormatException>(() => VaultFile.Write(_path, contents));

        Assert.Equal(message, exception.Message);
        Assert.Equal(V2, File.ReadAllBytes(_path));
        Assert.Equal(V0, File.ReadAllBytes(BackupPath));
        Assert.False(File.Exists(TempPath));
    }

    [Fact]
    public void Write_TempPathBlockedByDirectory_ThrowsAndKeepsTargetAndBackup()
    {
        VaultFile.Write(_path, V0);
        VaultFile.Write(_path, V2);
        Directory.CreateDirectory(TempPath);
        File.WriteAllBytes(Path.Combine(TempPath, "blocker"), [0x00]);

        Assert.Throws<UnauthorizedAccessException>(() => VaultFile.Write(_path, V1));

        Assert.Equal(V2, File.ReadAllBytes(_path));
        Assert.Equal(V0, File.ReadAllBytes(BackupPath));
    }

    [Fact]
    public void Write_MissingDirectory_ThrowsAndCreatesNothing()
    {
        var missing = Path.Combine(_directory.FullName, "missing");
        var path = Path.Combine(missing, $"{Guid.NewGuid()}.pmvault");

        Assert.Throws<DirectoryNotFoundException>(() => VaultFile.Write(path, V0));

        Assert.False(Directory.Exists(missing));
        Assert.Empty(Directory.GetFileSystemEntries(_directory.FullName));
    }

    [Fact]
    public void Write_NullPath_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() => VaultFile.Write(null!, V0));
    }

    [Fact]
    public void Write_EmptyPath_ThrowsArgument()
    {
        Assert.Throws<ArgumentException>(() => VaultFile.Write(string.Empty, V0));
    }

    [Fact]
    public void Write_Contents_AreNotModified()
    {
        var contents = (byte[])V2.Clone();

        VaultFile.Write(_path, contents);

        Assert.Equal(V2, contents);
    }

    // --- Read ---

    [Fact]
    public void Read_AfterWrite_ReturnsWrittenBytes()
    {
        VaultFile.Write(_path, V2);

        Assert.Equal(V2, VaultFile.Read(_path));
    }

    [Fact]
    public void Read_MissingFile_ThrowsFileNotFound()
    {
        Assert.Throws<FileNotFoundException>(() => VaultFile.Read(_path));
    }

    [Fact]
    public void Read_NullPath_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() => VaultFile.Read(null!));
    }

    [Fact]
    public void Read_EmptyPath_ThrowsArgument()
    {
        Assert.Throws<ArgumentException>(() => VaultFile.Read(string.Empty));
    }

    [Theory]
    [MemberData(nameof(InvalidFiles))]
    public void Read_InvalidRawFile_ReturnsBytesThatFailToParse(string name, byte[] contents, string message)
    {
        Assert.NotNull(name);
        File.WriteAllBytes(_path, contents);

        var bytes = VaultFile.Read(_path);

        Assert.Equal(contents, bytes);
        var exception = Assert.Throws<VaultFormatException>(() => VaultHeader.Parse(bytes));
        Assert.Equal(message, exception.Message);
    }

    // --- End to end ---

    [Fact]
    public void Write_BuiltAndSealedVault_ReadsBackUnlocksAndKeepsBackup()
    {
        var content = new VaultContent { Entries = [SampleEntry] };
        var header = VaultHeader.Build([KeySlot.CreatePasswordSlot(Password, LowCost, Vk)]);
        var first = VaultBody.Seal(header, Vk, content);
        VaultFile.Write(_path, first);

        var bytes = VaultFile.Read(_path);
        var parsed = VaultHeader.Parse(bytes);
        var vaultKey = new byte[VaultFormat.KeySize];
        KeySlot.UnwrapPasswordSlot(bytes, parsed.PasswordSlotOffset, Password, vaultKey);

        Assert.Equal(Vk, vaultKey);
        Assert.Equal(content.Entries, VaultBody.Open(bytes, parsed.BodyOffset, vaultKey).Entries);

        var second = VaultBody.Seal(bytes.AsSpan(0, parsed.BodyOffset), vaultKey, content);
        VaultFile.Write(_path, second);

        var backup = File.ReadAllBytes(BackupPath);
        Assert.Equal(first, backup);
        Assert.Equal(second, VaultFile.Read(_path));
        Assert.Equal(content.Entries, VaultBody.Open(backup, VaultHeader.Parse(backup).BodyOffset, vaultKey).Entries);
        Assert.Equal(content.Entries, VaultBody.Open(second, VaultHeader.Parse(second).BodyOffset, vaultKey).Entries);
    }
}
