using PasswordManager.Core.Vault;

namespace PasswordManager.Core.Tests.Vault;

public class VaultSessionTests
{
    private const string Password = "correct horse battery staple";
    private const string NewPassword = "new fictitious passphrase";
    private const string ExportPassword = "fictitious export passphrase";

    // Fictitious Vault Key 0x20..0x3F and recovery key 0x01..0x14.
    private static readonly byte[] Vk = [.. Enumerable.Range(0x20, 32).Select(i => (byte)i)];
    private static readonly byte[] RecoveryKey = [.. Enumerable.Range(0x01, 20).Select(i => (byte)i)];

    // Fictitious Vault Key 0x40..0x5F of another profile.
    private static readonly byte[] OtherVk = [.. Enumerable.Range(0x40, 32).Select(i => (byte)i)];

    private static readonly KdfParameters LowCost = new(1024, 2);

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

    private static readonly VaultEntry OtherEntry = SampleEntry with
    {
        Id = Guid.Parse("7d1c2b3a-4e5f-4a6b-8c9d-0e1f2a3b4c5d"),
        Title = "Other example",
        Password = "another-fake-password",
    };

    private static readonly VaultContent Content = new() { Entries = [SampleEntry] };
    private static readonly VaultContent NewContent = new() { Entries = [SampleEntry, OtherEntry] };

    // [type, n LE16, n x 0xEE]
    private static readonly byte[] Unknown3 = [0x03, 0x05, 0x00, .. Enumerable.Repeat((byte)0xEE, 5)];

    // Password slot + kit slot + Unknown(3, 5), sealed with Vk over Content.
    private static readonly byte[] VaultFull = VaultBody.Seal(
        VaultHeader.Build(
        [
            KeySlot.CreatePasswordSlot(Password, LowCost, Vk),
            KeySlot.CreateRecoveryKitSlot(RecoveryKey, LowCost, Vk),
            Unknown3,
        ]),
        Vk,
        Content);

    // Writes VaultFull to a fresh temp folder, runs the test with the vault path and deletes the folder.
    private static void WithVault(Action<string> test)
    {
        var directory = Directory.CreateTempSubdirectory("pm-vaultsession-");
        try
        {
            var path = Path.Combine(directory.FullName, $"{Guid.NewGuid()}.pmvault");
            VaultFile.Write(path, VaultFull);
            test(path);
        }
        finally
        {
            if (Directory.Exists(directory.FullName))
            {
                directory.Delete(recursive: true);
            }
        }
    }

    private static VaultSession OpenSession(string path, string password = Password)
    {
        var session = new VaultSession(path, LowCost);
        session.Unlock(password);
        return session;
    }

    private static byte[] UnlockWithPassword(byte[] file, string password)
    {
        var vaultKey = new byte[VaultFormat.KeySize];
        KeySlot.UnwrapPasswordSlot(file, VaultHeader.Parse(file).PasswordSlotOffset, password, vaultKey);
        return vaultKey;
    }

    private static byte[] UnlockWithKit(byte[] file)
    {
        var vaultKey = new byte[VaultFormat.KeySize];
        KeySlot.UnwrapRecoveryKitSlot(file, VaultHeader.Parse(file).RecoveryKitSlotOffset!.Value, RecoveryKey, vaultKey);
        return vaultKey;
    }

    private static byte[] UnlockWithKitText(byte[] file, char[] kitText)
    {
        var vaultKey = new byte[VaultFormat.KeySize];
        RecoveryKit.Unlock(file, kitText, vaultKey);
        return vaultKey;
    }

    private static void AssertNewKitFails(byte[] file, char[] kitText) =>
        AssertAuthenticationFailure(() => UnlockWithKitText(file, kitText));

    private static IReadOnlyList<VaultEntry> OpenEntries(byte[] file, byte[] vaultKey) =>
        VaultBody.Open(file, VaultHeader.Parse(file).BodyOffset, vaultKey).Entries;

    private static byte[] SlotBytes(byte[] file, byte type)
    {
        var range = Assert.Single(VaultHeader.Parse(file).Slots, slot => slot.Type == type);
        return file[range.Offset..(range.Offset + range.Length)];
    }

    private static byte[] BodyNonce(byte[] file)
    {
        var offset = VaultHeader.Parse(file).BodyOffset;
        return file[offset..(offset + VaultFormat.NonceSize)];
    }

    private static byte[] FlipLowBit(byte[] source, int index)
    {
        var copy = (byte[])source.Clone();
        copy[index] ^= 0x01;
        return copy;
    }

    private static void AssertAuthenticationFailure(Action action)
    {
        var exception = Assert.Throws<VaultAuthenticationException>(action);

        Assert.Equal("Vault authentication failed.", exception.Message);
        Assert.Null(exception.InnerException);
    }

    private static void AssertAllZero(byte[] buffer) => Assert.All(buffer, b => Assert.Equal(0, b));

    // --- Constructor ---

    [Fact]
    public void Constructor_EmptyPath_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => new VaultSession("", LowCost));
    }

    [Fact]
    public void Constructor_ParametersOutOfRange_ThrowsArgumentOutOfRangeException()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => new VaultSession("vault.pmvault", new KdfParameters(7, 1)));

        Assert.Equal("newSlotParameters", exception.ParamName);
    }

    [Fact]
    public void Constructor_MissingFile_DoesNotTouchDiskAndStartsLocked()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pm-vaultsession-missing-{Guid.NewGuid()}", "vault.pmvault");

        using var session = new VaultSession(path);

        Assert.False(session.IsUnlocked);
        Assert.Equal(Path.GetFullPath(path), session.VaultPath);
        Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
    }

    // --- Create ---

    [Fact]
    public void Create_File_HasOnePasswordSlotAndOneKitSlot()
    {
        var kitText = new char[RecoveryKit.TextLength];

        var file = VaultSession.Create(Password, kitText, LowCost);

        byte[] expectedTypes = [VaultFormat.SlotTypePassword, VaultFormat.SlotTypeRecoveryKit];
        Assert.Equal(expectedTypes, VaultHeader.Parse(file).Slots.Select(slot => slot.Type));
        Assert.Equal(LowCost, KdfParameters.ReadFromSlot(SlotBytes(file, VaultFormat.SlotTypePassword)));
        Assert.Equal(LowCost, KdfParameters.ReadFromSlot(SlotBytes(file, VaultFormat.SlotTypeRecoveryKit)));
    }

    [Fact]
    public void Create_Password_UnlocksSessionWithEmptyContent()
    {
        WithVault(path =>
        {
            VaultFile.Write(path, VaultSession.Create(Password, new char[RecoveryKit.TextLength], LowCost));

            using var session = OpenSession(path);

            Assert.True(session.IsUnlocked);
            Assert.Empty(session.Content.Entries);
        });
    }

    [Fact]
    public void Create_KitText_OpensBody()
    {
        var kitText = new char[RecoveryKit.TextLength];

        var file = VaultSession.Create(Password, kitText, LowCost);

        Assert.Contains(kitText, c => c != '\0');
        var vaultKey = new byte[VaultFormat.KeySize];
        RecoveryKit.Unlock(file, kitText, vaultKey);
        Assert.Empty(OpenEntries(file, vaultKey));
        Assert.Equal(vaultKey, UnlockWithPassword(file, Password));
    }

    [Fact]
    public void Create_TwoCalls_UseDifferentVaultKeys()
    {
        var first = VaultSession.Create(Password, new char[RecoveryKit.TextLength], LowCost);
        var second = VaultSession.Create(Password, new char[RecoveryKit.TextLength], LowCost);

        Assert.NotEqual(UnlockWithPassword(first, Password), UnlockWithPassword(second, Password));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(38)]
    [InlineData(40)]
    public void Create_WrongKitTextLength_ThrowsArgumentException(int length)
    {
        var exception = Assert.Throws<ArgumentException>(
            () => VaultSession.Create(Password, new char[length], LowCost));

        Assert.Equal("kitText", exception.ParamName);
    }

    [Fact]
    public void Create_ParametersOutOfRange_ThrowsArgumentOutOfRangeException()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => VaultSession.Create(Password, new char[RecoveryKit.TextLength], new KdfParameters(1024, 33)));

        Assert.Equal("parameters", exception.ParamName);
    }

    // --- Unlock ---

    [Fact]
    public void Unlock_RightPassword_IsUnlockedWithEntries()
    {
        WithVault(path =>
        {
            using var session = new VaultSession(path, LowCost);

            session.Unlock(Password);

            Assert.True(session.IsUnlocked);
            Assert.Equal(Content.Entries, session.Content.Entries);
            Assert.Equal(Vk, session.VaultKeyBufferForTesting);
        });
    }

    [Fact]
    public void Unlock_WrongPassword_ThrowsAndStaysLocked()
    {
        WithVault(path =>
        {
            using var session = new VaultSession(path, LowCost);

            AssertAuthenticationFailure(() => session.Unlock(NewPassword));

            Assert.False(session.IsUnlocked);
            Assert.Throws<InvalidOperationException>(() => session.Content);
            AssertAllZero(session.VaultKeyBufferForTesting);
        });
    }

    [Fact]
    public void Unlock_TamperedBody_ThrowsZeroesKeyAndStaysLocked()
    {
        WithVault(path =>
        {
            File.WriteAllBytes(path, FlipLowBit(VaultFull, VaultHeader.Parse(VaultFull).BodyOffset + 24));
            using var session = new VaultSession(path, LowCost);

            AssertAuthenticationFailure(() => session.Unlock(Password));

            Assert.False(session.IsUnlocked);
            AssertAllZero(session.VaultKeyBufferForTesting);
        });
    }

    [Fact]
    public void Unlock_MissingFile_ThrowsFileNotFoundException()
    {
        WithVault(path =>
        {
            using var session = new VaultSession(path + ".missing", LowCost);

            Assert.Throws<FileNotFoundException>(() => session.Unlock(Password));
            Assert.False(session.IsUnlocked);
        });
    }

    [Fact]
    public void Unlock_CorruptFile_ThrowsVaultFormatException()
    {
        WithVault(path =>
        {
            File.WriteAllBytes(path, [.. Enumerable.Repeat((byte)0x5A, 200)]);
            using var session = new VaultSession(path, LowCost);

            var exception = Assert.Throws<VaultFormatException>(() => session.Unlock(Password));

            Assert.Equal("Vault file has an invalid magic.", exception.Message);
            Assert.False(session.IsUnlocked);
        });
    }

    [Fact]
    public void Unlock_WhileUnlocked_ThrowsInvalidOperationException()
    {
        WithVault(path =>
        {
            using var session = OpenSession(path);

            Assert.Throws<InvalidOperationException>(() => session.Unlock(Password));
            Assert.True(session.IsUnlocked);
            Assert.Equal(Vk, session.VaultKeyBufferForTesting);
        });
    }

    // --- Lock ---

    [Fact]
    public void Lock_ZeroesKeyAndLocks()
    {
        WithVault(path =>
        {
            using var session = OpenSession(path);
            var keyBuffer = session.VaultKeyBufferForTesting;

            session.Lock();

            AssertAllZero(keyBuffer);
            Assert.False(session.IsUnlocked);
        });
    }

    [Fact]
    public void Lock_EveryOperation_ThrowsInvalidOperationException()
    {
        WithVault(path =>
        {
            using var session = OpenSession(path);
            var export = session.Export(ExportPassword);

            session.Lock();

            Assert.Throws<InvalidOperationException>(() => session.Content);
            Assert.Throws<InvalidOperationException>(() => session.Save(NewContent));
            Assert.Throws<InvalidOperationException>(() => session.ChangePassword(Password, NewPassword));
            Assert.Throws<InvalidOperationException>(() => session.Export(ExportPassword));
            Assert.Throws<InvalidOperationException>(() => session.Import(export, ExportPassword));
            Assert.Equal(VaultFull, VaultFile.Read(path));
        });
    }

    [Fact]
    public void Lock_Twice_IsNoOpAndUnlockWorksAgain()
    {
        WithVault(path =>
        {
            using var session = OpenSession(path);

            session.Lock();
            session.Lock();
            session.Unlock(Password);

            Assert.True(session.IsUnlocked);
            Assert.Equal(Content.Entries, session.Content.Entries);
            Assert.Equal(Vk, session.VaultKeyBufferForTesting);
        });
    }

    [Fact]
    public void Lock_BeforeUnlock_IsNoOp()
    {
        using var session = new VaultSession("never-read.pmvault", LowCost);

        session.Lock();

        Assert.False(session.IsUnlocked);
    }

    [Fact]
    public void Dispose_ZeroesKeyAndLaterCallsThrowObjectDisposedException()
    {
        WithVault(path =>
        {
            var session = OpenSession(path);
            var keyBuffer = session.VaultKeyBufferForTesting;

            session.Dispose();

            AssertAllZero(keyBuffer);
            Assert.False(session.IsUnlocked);
            Assert.Throws<ObjectDisposedException>(() => session.Unlock(Password));
            Assert.Throws<ObjectDisposedException>(() => session.Content);
            Assert.Throws<ObjectDisposedException>(() => session.Save(NewContent));
            Assert.Throws<ObjectDisposedException>(() => session.ChangePassword(Password, NewPassword));
            Assert.Throws<ObjectDisposedException>(() => session.Export(ExportPassword));
            Assert.Throws<ObjectDisposedException>(() => session.Import(VaultFull, Password));

            // Lock and Dispose stay safe to call after disposal.
            session.Lock();
            session.Dispose();
        });
    }

    // --- Save ---

    [Fact]
    public void Save_NewSession_ReadsSavedEntries()
    {
        WithVault(path =>
        {
            using (var session = OpenSession(path))
            {
                session.Save(NewContent);

                Assert.Same(NewContent, session.Content);
            }

            using var reopened = OpenSession(path);
            Assert.Equal(NewContent.Entries, reopened.Content.Entries);
        });
    }

    [Fact]
    public void Save_File_HasNewBodyNonceAndSameSlotsByteForByte()
    {
        WithVault(path =>
        {
            using var session = OpenSession(path);

            session.Save(NewContent);

            var after = VaultFile.Read(path);
            Assert.NotEqual(BodyNonce(VaultFull), BodyNonce(after));
            Assert.Equal(VaultHeader.Parse(VaultFull).BodyOffset, VaultHeader.Parse(after).BodyOffset);
            Assert.Equal(VaultFull[..VaultHeader.Parse(VaultFull).BodyOffset], after[..VaultHeader.Parse(after).BodyOffset]);
            Assert.Equal(SlotBytes(VaultFull, VaultFormat.SlotTypeRecoveryKit), SlotBytes(after, VaultFormat.SlotTypeRecoveryKit));
            Assert.Equal(Unknown3, SlotBytes(after, 3));
            Assert.Equal(NewContent.Entries, OpenEntries(after, UnlockWithKit(after)));
        });
    }

    [Fact]
    public void Save_Twice_BackupHoldsPreviousBytes()
    {
        WithVault(path =>
        {
            using var session = OpenSession(path);

            session.Save(NewContent);
            var first = VaultFile.Read(path);
            session.Save(Content);

            Assert.Equal(first, File.ReadAllBytes(path + VaultFile.BackupSuffix));
            Assert.NotEqual(BodyNonce(first), BodyNonce(VaultFile.Read(path)));
            Assert.Equal(Content.Entries, OpenEntries(VaultFile.Read(path), Vk));
        });
    }

    [Fact]
    public void Save_First_BackupHoldsOriginalBytes()
    {
        WithVault(path =>
        {
            using var session = OpenSession(path);

            session.Save(NewContent);

            Assert.Equal(VaultFull, File.ReadAllBytes(path + VaultFile.BackupSuffix));
        });
    }

    [Fact]
    public void Save_FolderDeleted_ThrowsIOExceptionAndKeepsOldContent()
    {
        WithVault(path =>
        {
            using var session = OpenSession(path);
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);

            Assert.ThrowsAny<IOException>(() => session.Save(NewContent));

            Assert.True(session.IsUnlocked);
            Assert.Equal(Content.Entries, session.Content.Entries);
            Assert.Equal(Vk, session.VaultKeyBufferForTesting);
        });
    }

    [Fact]
    public void Save_Null_ThrowsArgumentNullException()
    {
        WithVault(path =>
        {
            using var session = OpenSession(path);

            Assert.Throws<ArgumentNullException>(() => session.Save(null!));
        });
    }

    // --- ChangePassword ---

    [Fact]
    public void ChangePassword_NewPasswordOpensDiskFileOldFailsKitOpens()
    {
        WithVault(path =>
        {
            using var session = OpenSession(path);

            session.ChangePassword(Password, NewPassword);

            var after = VaultFile.Read(path);
            Assert.Equal(Vk, UnlockWithPassword(after, NewPassword));
            AssertAuthenticationFailure(() => UnlockWithPassword(after, Password));
            Assert.Equal(SlotBytes(VaultFull, VaultFormat.SlotTypeRecoveryKit), SlotBytes(after, VaultFormat.SlotTypeRecoveryKit));
            Assert.Equal(Unknown3, SlotBytes(after, 3));
            Assert.Equal(Content.Entries, OpenEntries(after, UnlockWithKit(after)));
            Assert.Equal(LowCost, KdfParameters.ReadFromSlot(SlotBytes(after, VaultFormat.SlotTypePassword)));
            var backup = File.ReadAllBytes(path + VaultFile.BackupSuffix);
            Assert.Equal(after, backup);
            AssertAuthenticationFailure(() => UnlockWithPassword(backup, Password));
        });
    }

    [Fact]
    public void ChangePassword_BackupLocked_StillSucceeds()
    {
        WithVault(path =>
        {
            var backupPath = path + VaultFile.BackupSuffix;
            File.WriteAllBytes(backupPath, VaultFull);
            using var session = OpenSession(path);

            using (new FileStream(backupPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                session.ChangePassword(Password, NewPassword);
            }

            var after = VaultFile.Read(path);
            Assert.Equal(Vk, UnlockWithPassword(after, NewPassword));
            session.VerifyPassword(NewPassword);
            AssertAuthenticationFailure(() => session.VerifyPassword(Password));
            Assert.Equal(VaultFull, File.ReadAllBytes(backupPath));

            session.Save(NewContent);

            Assert.Equal(after, File.ReadAllBytes(backupPath));
        });
    }

    [Fact]
    public void ChangePassword_ThenSave_ReopensWithNewPassword()
    {
        WithVault(path =>
        {
            using (var session = OpenSession(path))
            {
                session.ChangePassword(Password, NewPassword);

                Assert.True(session.IsUnlocked);
                Assert.Equal(Content.Entries, session.Content.Entries);

                session.Save(NewContent);
            }

            using var reopened = new VaultSession(path, LowCost);
            AssertAuthenticationFailure(() => reopened.Unlock(Password));
            reopened.Unlock(NewPassword);
            Assert.Equal(NewContent.Entries, reopened.Content.Entries);
        });
    }

    [Fact]
    public void ChangePassword_WrongCurrentPassword_ThrowsAndLeavesFileAndSession()
    {
        WithVault(path =>
        {
            using var session = OpenSession(path);

            AssertAuthenticationFailure(() => session.ChangePassword(NewPassword, NewPassword));

            Assert.Equal(VaultFull, VaultFile.Read(path));
            Assert.False(File.Exists(path + VaultFile.BackupSuffix));
            Assert.True(session.IsUnlocked);
            Assert.Equal(Vk, session.VaultKeyBufferForTesting);

            session.Save(NewContent);
            Assert.Equal(NewContent.Entries, OpenEntries(VaultFile.Read(path), UnlockWithPassword(VaultFile.Read(path), Password)));
        });
    }

    // --- PrepareRecoveryKit ---

    [Fact]
    public void PrepareRecoveryKit_WritesNothing_OldKitStillOpens()
    {
        WithVault(path =>
        {
            using var session = OpenSession(path);
            var kitText = new char[RecoveryKit.TextLength];

            session.PrepareRecoveryKit(kitText);

            Assert.DoesNotContain('\0', kitText);
            Assert.Equal([path], Directory.GetFileSystemEntries(Path.GetDirectoryName(path)!));
            var disk = VaultFile.Read(path);
            Assert.Equal(VaultFull, disk);
            Assert.Equal(Vk, UnlockWithKit(disk));
            AssertNewKitFails(disk, kitText);
            Assert.True(session.IsUnlocked);
        });
    }

    [Fact]
    public void PrepareRecoveryKit_VaultWithoutKit_AddsSlotOnCommit()
    {
        WithVault(path =>
        {
            var noKit = VaultBody.Seal(VaultHeader.Build([KeySlot.CreatePasswordSlot(Password, LowCost, Vk)]), Vk, Content);
            File.WriteAllBytes(path, noKit);
            using var session = OpenSession(path);
            var kitText = new char[RecoveryKit.TextLength];

            session.CommitRecoveryKit(session.PrepareRecoveryKit(kitText));

            var after = VaultFile.Read(path);
            Assert.NotNull(VaultHeader.Parse(after).RecoveryKitSlotOffset);
            Assert.Equal(SlotBytes(noKit, VaultFormat.SlotTypePassword), SlotBytes(after, VaultFormat.SlotTypePassword));
            Assert.Equal(Content.Entries, OpenEntries(after, UnlockWithKitText(after, kitText)));
        });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(38)]
    [InlineData(40)]
    public void PrepareRecoveryKit_BadBufferLength_Throws(int length)
    {
        WithVault(path =>
        {
            using var session = OpenSession(path);

            var exception = Assert.Throws<ArgumentException>(() => session.PrepareRecoveryKit(new char[length]));

            Assert.Equal("kitText", exception.ParamName);
            Assert.Equal(VaultFull, VaultFile.Read(path));
            Assert.True(session.IsUnlocked);
        });
    }

    [Fact]
    public void PrepareRecoveryKit_Locked_Throws()
    {
        WithVault(path =>
        {
            using var session = new VaultSession(path, LowCost);
            var kitText = new char[RecoveryKit.TextLength];

            Assert.Throws<InvalidOperationException>(() => session.PrepareRecoveryKit(kitText));

            session.Unlock(Password);
            session.Lock();

            Assert.Throws<InvalidOperationException>(() => session.PrepareRecoveryKit(kitText));
            Assert.All(kitText, c => Assert.Equal('\0', c));
        });
    }

    [Fact]
    public void PrepareRecoveryKit_Disposed_Throws()
    {
        WithVault(path =>
        {
            var session = OpenSession(path);
            session.Dispose();

            Assert.Throws<ObjectDisposedException>(() => session.PrepareRecoveryKit(new char[RecoveryKit.TextLength]));
        });
    }

    // --- CommitRecoveryKit ---

    [Fact]
    public void CommitRecoveryKit_NewKitOpensOldFails_OtherSlotsStay()
    {
        WithVault(path =>
        {
            using var session = OpenSession(path);
            var content = session.Content;
            var kitText = new char[RecoveryKit.TextLength];

            session.CommitRecoveryKit(session.PrepareRecoveryKit(kitText));

            var after = VaultFile.Read(path);
            Assert.Equal(Vk, UnlockWithKitText(after, kitText));
            AssertAuthenticationFailure(() => UnlockWithKit(after));
            Assert.Equal(Vk, UnlockWithPassword(after, Password));
            Assert.Equal(SlotBytes(VaultFull, VaultFormat.SlotTypePassword), SlotBytes(after, VaultFormat.SlotTypePassword));
            Assert.Equal(Unknown3, SlotBytes(after, 3));
            Assert.Equal(Content.Entries, OpenEntries(after, Vk));
            Assert.NotEqual(BodyNonce(VaultFull), BodyNonce(after));
            Assert.True(session.IsUnlocked);
            Assert.Same(content, session.Content);
            Assert.Equal(Vk, session.VaultKeyBufferForTesting);
        });
    }

    [Fact]
    public void CommitRecoveryKit_BackupHoldsNewFile()
    {
        WithVault(path =>
        {
            using var session = OpenSession(path);
            var kitText = new char[RecoveryKit.TextLength];

            session.CommitRecoveryKit(session.PrepareRecoveryKit(kitText));

            var backup = File.ReadAllBytes(path + VaultFile.BackupSuffix);
            Assert.Equal(VaultFile.Read(path), backup);
            AssertAuthenticationFailure(() => UnlockWithKit(backup));
            Assert.Equal(Vk, UnlockWithKitText(backup, kitText));
        });
    }

    [Fact]
    public void CommitRecoveryKit_ThenSave_KeepsNewKit()
    {
        WithVault(path =>
        {
            using var session = OpenSession(path);
            var kitText = new char[RecoveryKit.TextLength];
            session.CommitRecoveryKit(session.PrepareRecoveryKit(kitText));

            session.Save(NewContent);

            var after = VaultFile.Read(path);
            Assert.Equal(NewContent.Entries, OpenEntries(after, UnlockWithKitText(after, kitText)));
            AssertAuthenticationFailure(() => UnlockWithKit(after));
            AssertAuthenticationFailure(() => UnlockWithKit(File.ReadAllBytes(path + VaultFile.BackupSuffix)));
        });
    }

    [Fact]
    public void CommitRecoveryKit_AfterSave_ThrowsAndWritesNothing()
    {
        WithVault(path =>
        {
            using var session = OpenSession(path);
            var kitText = new char[RecoveryKit.TextLength];
            var replacement = session.PrepareRecoveryKit(kitText);
            session.Save(NewContent);
            var saved = VaultFile.Read(path);
            var backup = File.ReadAllBytes(path + VaultFile.BackupSuffix);

            Assert.Throws<InvalidOperationException>(() => session.CommitRecoveryKit(replacement));

            Assert.Equal(saved, VaultFile.Read(path));
            Assert.Equal(backup, File.ReadAllBytes(path + VaultFile.BackupSuffix));
            Assert.Equal(NewContent.Entries, OpenEntries(saved, UnlockWithKit(saved)));
            AssertNewKitFails(saved, kitText);
            Assert.True(session.IsUnlocked);
        });
    }

    [Fact]
    public void CommitRecoveryKit_AfterChangePassword_ThrowsAndWritesNothing()
    {
        WithVault(path =>
        {
            using var session = OpenSession(path);
            var kitText = new char[RecoveryKit.TextLength];
            var replacement = session.PrepareRecoveryKit(kitText);
            session.ChangePassword(Password, NewPassword);
            var changed = VaultFile.Read(path);

            Assert.Throws<InvalidOperationException>(() => session.CommitRecoveryKit(replacement));

            Assert.Equal(changed, VaultFile.Read(path));
            Assert.Equal(Vk, UnlockWithPassword(changed, NewPassword));
            Assert.Equal(Vk, UnlockWithKit(changed));
            AssertNewKitFails(changed, kitText);
        });
    }

    [Fact]
    public void CommitRecoveryKit_Twice_SecondThrowsAndWritesNothing()
    {
        WithVault(path =>
        {
            using var session = OpenSession(path);
            var replacement = session.PrepareRecoveryKit(new char[RecoveryKit.TextLength]);
            session.CommitRecoveryKit(replacement);
            var committed = VaultFile.Read(path);

            Assert.Throws<InvalidOperationException>(() => session.CommitRecoveryKit(replacement));

            Assert.Equal(committed, VaultFile.Read(path));
        });
    }

    [Fact]
    public void CommitRecoveryKit_Locked_ThrowsAndWritesNothing()
    {
        WithVault(path =>
        {
            using var session = OpenSession(path);
            var replacement = session.PrepareRecoveryKit(new char[RecoveryKit.TextLength]);

            session.Lock();

            Assert.Throws<InvalidOperationException>(() => session.CommitRecoveryKit(replacement));
            Assert.Equal(VaultFull, VaultFile.Read(path));
            Assert.Equal([path], Directory.GetFileSystemEntries(Path.GetDirectoryName(path)!));

            // Unlocking again reads the file anew, so the old replacement still does not apply.
            session.Unlock(Password);

            Assert.Throws<InvalidOperationException>(() => session.CommitRecoveryKit(replacement));
            Assert.Equal(VaultFull, VaultFile.Read(path));
        });
    }

    [Fact]
    public void CommitRecoveryKit_Null_Throws()
    {
        WithVault(path =>
        {
            using var session = OpenSession(path);

            Assert.Throws<ArgumentNullException>(() => session.CommitRecoveryKit(null!));
        });
    }

    [Fact]
    public void CommitRecoveryKit_WriteFails_KeepsOldFileAndRetrySucceeds()
    {
        WithVault(path =>
        {
            using var session = OpenSession(path);
            var kitText = new char[RecoveryKit.TextLength];
            var replacement = session.PrepareRecoveryKit(kitText);

            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                // Windows reports a sharing violation on the rename as access denied, so either documented exception is accepted.
                var exception = Record.Exception(() => session.CommitRecoveryKit(replacement));

                Assert.True(exception is IOException or UnauthorizedAccessException, $"Unexpected exception: {exception}");
            }

            Assert.Equal(VaultFull, VaultFile.Read(path));
            Assert.Equal([path], Directory.GetFileSystemEntries(Path.GetDirectoryName(path)!));
            Assert.True(session.IsUnlocked);

            session.CommitRecoveryKit(replacement);

            var after = VaultFile.Read(path);
            Assert.Equal(Vk, UnlockWithKitText(after, kitText));
            AssertAuthenticationFailure(() => UnlockWithKit(after));
        });
    }

    // --- VerifyPassword ---

    [Fact]
    public void VerifyPassword_Correct_ChangesNeitherSessionNorDisk()
    {
        WithVault(path =>
        {
            var directory = Path.GetDirectoryName(path)!;
            using var session = OpenSession(path);
            var content = session.Content;

            session.VerifyPassword(Password);

            Assert.True(session.IsUnlocked);
            Assert.Same(content, session.Content);
            Assert.Equal(Vk, session.VaultKeyBufferForTesting);
            Assert.Equal(VaultFull, File.ReadAllBytes(path));
            Assert.Equal([path], Directory.GetFileSystemEntries(directory));
        });
    }

    [Fact]
    public void VerifyPassword_Wrong_ThrowsAndStaysUnlocked()
    {
        WithVault(path =>
        {
            var directory = Path.GetDirectoryName(path)!;
            using var session = OpenSession(path);

            AssertAuthenticationFailure(() => session.VerifyPassword(NewPassword));
            AssertAuthenticationFailure(() => session.VerifyPassword(""));

            Assert.True(session.IsUnlocked);
            Assert.Equal(Content.Entries, session.Content.Entries);
            Assert.Equal(Vk, session.VaultKeyBufferForTesting);
            Assert.Equal(VaultFull, File.ReadAllBytes(path));
            Assert.Equal([path], Directory.GetFileSystemEntries(directory));

            session.VerifyPassword(Password);
        });
    }

    [Fact]
    public void VerifyPassword_AfterChangePassword_AcceptsOnlyNewPassword()
    {
        WithVault(path =>
        {
            using var session = OpenSession(path);

            session.ChangePassword(Password, NewPassword);

            session.VerifyPassword(NewPassword);
            AssertAuthenticationFailure(() => session.VerifyPassword(Password));
            Assert.True(session.IsUnlocked);
        });
    }

    [Fact]
    public void VerifyPassword_Locked_ThrowsInvalidOperation()
    {
        WithVault(path =>
        {
            using var session = new VaultSession(path, LowCost);

            Assert.Throws<InvalidOperationException>(() => session.VerifyPassword(Password));

            session.Unlock(Password);
            session.Lock();

            Assert.Throws<InvalidOperationException>(() => session.VerifyPassword(Password));
            Assert.False(session.IsUnlocked);
        });
    }

    [Fact]
    public void VerifyPassword_Disposed_ThrowsObjectDisposed()
    {
        WithVault(path =>
        {
            var session = OpenSession(path);
            session.Dispose();

            Assert.Throws<ObjectDisposedException>(() => session.VerifyPassword(Password));
        });
    }

    // --- Export ---

    [Fact]
    public void Export_HasOnlyOnePasswordSlot()
    {
        WithVault(path =>
        {
            using var session = OpenSession(path);

            var export = session.Export(ExportPassword);

            var header = VaultHeader.Parse(export);
            var slot = Assert.Single(header.Slots);
            Assert.Equal(VaultFormat.SlotTypePassword, slot.Type);
            Assert.Null(header.RecoveryKitSlotOffset);
            Assert.Equal(LowCost, KdfParameters.ReadFromSlot(SlotBytes(export, VaultFormat.SlotTypePassword)));
        });
    }

    [Fact]
    public void Export_ExportPasswordOpensItAndMasterPasswordFails()
    {
        WithVault(path =>
        {
            using var session = OpenSession(path);

            var export = session.Export(ExportPassword);

            var exportKey = UnlockWithPassword(export, ExportPassword);
            Assert.Equal(Content.Entries, OpenEntries(export, exportKey));
            Assert.NotEqual(Vk, exportKey);
            AssertAuthenticationFailure(() => UnlockWithPassword(export, Password));
        });
    }

    [Fact]
    public void Export_SessionVaultKey_DoesNotOpenIt()
    {
        WithVault(path =>
        {
            using var session = OpenSession(path);

            var export = session.Export(ExportPassword);

            AssertAuthenticationFailure(() => OpenEntries(export, (byte[])session.VaultKeyBufferForTesting.Clone()));
        });
    }

    [Fact]
    public void Export_LeavesFolderAndVaultUnchanged()
    {
        WithVault(path =>
        {
            var directory = Path.GetDirectoryName(path)!;
            using var session = OpenSession(path);
            var listing = Directory.GetFiles(directory);

            var export = session.Export(ExportPassword);

            Assert.NotEmpty(export);
            Assert.Equal(listing, Directory.GetFiles(directory));
            Assert.Equal(VaultFull, VaultFile.Read(path));
            Assert.True(session.IsUnlocked);
            Assert.Equal(Vk, session.VaultKeyBufferForTesting);
        });
    }

    [Fact]
    public void Export_AfterSave_ContainsSavedContent()
    {
        WithVault(path =>
        {
            using var session = OpenSession(path);
            session.Save(NewContent);

            var export = session.Export(ExportPassword);

            Assert.Equal(NewContent.Entries, OpenEntries(export, UnlockWithPassword(export, ExportPassword)));
        });
    }

    // --- Import ---

    [Fact]
    public void Import_ExportRoundTrip_ReturnsEqualEntries()
    {
        WithVault(path =>
        {
            using var session = OpenSession(path);
            var export = session.Export(ExportPassword);

            var imported = session.Import(export, ExportPassword);

            Assert.Equal(Content.Entries, imported.Entries);
        });
    }

    [Fact]
    public void Import_ProfileVaultWithKitSlot_ImportsThroughItsPassword()
    {
        WithVault(path =>
        {
            var otherProfile = VaultBody.Seal(
                VaultHeader.Build(
                [
                    KeySlot.CreatePasswordSlot(NewPassword, LowCost, OtherVk),
                    KeySlot.CreateRecoveryKitSlot(RecoveryKey, LowCost, OtherVk),
                ]),
                OtherVk,
                NewContent);
            using var session = OpenSession(path);

            var imported = session.Import(otherProfile, NewPassword);

            Assert.Equal(NewContent.Entries, imported.Entries);
        });
    }

    [Fact]
    public void Import_WrongPassword_ThrowsVaultAuthenticationException()
    {
        WithVault(path =>
        {
            using var session = OpenSession(path);
            var export = session.Export(ExportPassword);

            AssertAuthenticationFailure(() => session.Import(export, Password));
        });
    }

    [Fact]
    public void Import_FlippedBodyByte_ThrowsVaultAuthenticationException()
    {
        WithVault(path =>
        {
            using var session = OpenSession(path);
            var export = session.Export(ExportPassword);
            var tampered = FlipLowBit(export, VaultHeader.Parse(export).BodyOffset + 24);

            AssertAuthenticationFailure(() => session.Import(tampered, ExportPassword));
        });
    }

    [Fact]
    public void Import_Garbage_ThrowsVaultFormatException()
    {
        WithVault(path =>
        {
            using var session = OpenSession(path);

            Assert.Throws<VaultFormatException>(() => session.Import([.. Enumerable.Repeat((byte)0x5A, 200)], ExportPassword));
            Assert.Throws<VaultFormatException>(() => session.Import([0x50, 0x4D], ExportPassword));
        });
    }

    [Fact]
    public void Import_LeavesSessionAndDiskUnchanged()
    {
        WithVault(path =>
        {
            var directory = Path.GetDirectoryName(path)!;
            using var session = OpenSession(path);
            var export = session.Export(ExportPassword);
            var listing = Directory.GetFiles(directory);

            session.Import(export, ExportPassword);
            AssertAuthenticationFailure(() => session.Import(export, Password));

            Assert.True(session.IsUnlocked);
            Assert.Equal(Content.Entries, session.Content.Entries);
            Assert.Equal(Vk, session.VaultKeyBufferForTesting);
            Assert.Equal(listing, Directory.GetFiles(directory));
            Assert.Equal(VaultFull, VaultFile.Read(path));
        });
    }

    [Fact]
    public async Task Import_LockDuringKdf_DoesNotWait()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var costly = VaultBody.Seal(
            VaultHeader.Build([KeySlot.CreatePasswordSlot(ExportPassword, new KdfParameters(256 * 1024, 16), OtherVk)]),
            OtherVk,
            NewContent);
        var directory = Directory.CreateTempSubdirectory("pm-vaultsession-");
        try
        {
            var path = Path.Combine(directory.FullName, $"{Guid.NewGuid()}.pmvault");
            VaultFile.Write(path, VaultFull);
            using var session = OpenSession(path);

            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var import = Task.Factory.StartNew(
                () =>
                {
                    started.SetResult();
                    return session.Import(costly, ExportPassword);
                },
                cancellationToken,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);

            // Long enough to pass the state check, and far from the end of a KDF at this cost (about a second).
            await started.Task.WaitAsync(cancellationToken);
            await Task.Delay(100, cancellationToken);

            session.Lock();

            // A short wait, because a Lock that did wait returns only just before the import task completes.
            var first = await Task.WhenAny(import, Task.Delay(50, cancellationToken));
            Assert.NotSame(import, first);
            Assert.False(session.IsUnlocked);
            AssertAllZero(session.VaultKeyBufferForTesting);

            var imported = await import;

            Assert.Equal(NewContent.Entries, imported.Entries);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    // --- Concurrency ---

    [Fact]
    public async Task Lock_DuringSaveLoop_FileAlwaysUnlocksAndDecrypts()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("pm-vaultsession-");
        try
        {
            var path = Path.Combine(directory.FullName, $"{Guid.NewGuid()}.pmvault");
            VaultFile.Write(path, VaultFull);

            for (var round = 0; round < 10; round++)
            {
                using var session = OpenSession(path);
                var firstSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var saver = Task.Run(
                    () =>
                    {
                        var saves = 0;
                        while (true)
                        {
                            try
                            {
                                session.Save(saves % 2 == 0 ? NewContent : Content);
                            }
                            catch (InvalidOperationException)
                            {
                                return saves;
                            }

                            saves++;
                            firstSave.TrySetResult();
                        }
                    },
                    cancellationToken);

                await firstSave.Task.WaitAsync(cancellationToken);
                session.Lock();
                var completed = await saver;

                Assert.True(completed > 0);
                AssertAllZero(session.VaultKeyBufferForTesting);

                var file = VaultFile.Read(path);
                var vaultKey = UnlockWithPassword(file, Password);
                Assert.Equal(Vk, vaultKey);
                var expected = completed % 2 == 1 ? NewContent : Content;
                Assert.Equal(expected.Entries, OpenEntries(file, vaultKey));
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
