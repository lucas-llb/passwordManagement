using PasswordManager.Core.Storage;
using PasswordManager.Core.Vault;

namespace PasswordManager.Core.Tests.Storage;

public sealed class AtomicFileTests : IDisposable
{
    private static readonly byte[] First = [.. Enumerable.Repeat((byte)0x11, 64)];
    private static readonly byte[] Second = [.. Enumerable.Repeat((byte)0x22, 96)];
    private static readonly byte[] Third = [.. Enumerable.Repeat((byte)0x33, 32)];

    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("pm-atomic-");
    private readonly string _path;

    public AtomicFileTests()
    {
        _path = Path.Combine(_directory.FullName, "target.bin");
    }

    private string TempPath => _path + VaultFile.TempSuffix;

    private string BackupPath => _path + VaultFile.BackupSuffix;

    public void Dispose()
    {
        _directory.Delete(recursive: true);
    }

    [Fact]
    public void Write_WithoutBackupWrittenTwice_KeepsOnlyTheTargetAndNoBackup()
    {
        AtomicFile.Write(_path, First, keepBackup: false);
        AtomicFile.Write(_path, Second, keepBackup: false);

        Assert.Equal(Second, File.ReadAllBytes(_path));
        Assert.False(File.Exists(BackupPath));
        Assert.False(File.Exists(TempPath));
        Assert.Equal([_path], Directory.GetFileSystemEntries(_directory.FullName));
    }

    [Fact]
    public void Write_WithBackupWrittenTwice_KeepsPreviousVersionAsBackup()
    {
        AtomicFile.Write(_path, First, keepBackup: true);
        AtomicFile.Write(_path, Second, keepBackup: true);

        Assert.Equal(Second, File.ReadAllBytes(_path));
        Assert.Equal(First, File.ReadAllBytes(BackupPath));
        Assert.False(File.Exists(TempPath));
    }

    [Fact]
    public void Write_WithoutBackupAndUnrelatedBackupPresent_LeavesBackupUntouched()
    {
        File.WriteAllBytes(BackupPath, Third);
        AtomicFile.Write(_path, First, keepBackup: false);

        AtomicFile.Write(_path, Second, keepBackup: false);

        Assert.Equal(Second, File.ReadAllBytes(_path));
        Assert.Equal(Third, File.ReadAllBytes(BackupPath));
        Assert.False(File.Exists(TempPath));
    }

    [Fact]
    public void Write_StaleTempFile_IsOverwrittenAndRemoved()
    {
        File.WriteAllBytes(TempPath, [.. Enumerable.Repeat((byte)0xAB, 10)]);

        AtomicFile.Write(_path, First, keepBackup: false);

        Assert.Equal(First, File.ReadAllBytes(_path));
        Assert.False(File.Exists(TempPath));
    }

    [Fact]
    public void Write_TempPathBlockedByDirectory_ThrowsAndKeepsTargetUnchanged()
    {
        AtomicFile.Write(_path, First, keepBackup: false);
        Directory.CreateDirectory(TempPath);
        File.WriteAllBytes(Path.Combine(TempPath, "blocker"), [0x00]);

        Assert.Throws<UnauthorizedAccessException>(() => AtomicFile.Write(_path, Second, keepBackup: false));

        Assert.Equal(First, File.ReadAllBytes(_path));
        Assert.False(File.Exists(BackupPath));
    }
}
