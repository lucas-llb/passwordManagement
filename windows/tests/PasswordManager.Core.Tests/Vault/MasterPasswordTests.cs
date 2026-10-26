using PasswordManager.Core.Vault;

namespace PasswordManager.Core.Tests.Vault;

public class MasterPasswordTests
{
    private const string OldPassword = "correct horse battery staple";
    private const string NewPassword = "new fictitious passphrase";

    // Fictitious Vault Key 0x20..0x3F and recovery key 0x01..0x14.
    private static readonly byte[] Vk = [.. Enumerable.Range(0x20, 32).Select(i => (byte)i)];
    private static readonly byte[] RecoveryKey = [.. Enumerable.Range(0x01, 20).Select(i => (byte)i)];

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

    private static readonly VaultContent Content = new() { Entries = [SampleEntry] };

    // [type, n LE16, n x 0xEE]
    private static readonly byte[] Unknown3 = [0x03, 0x05, 0x00, .. Enumerable.Repeat((byte)0xEE, 5)];

    // Vault1 = slot 1 only; VaultKit = slot 1 + kit slot; VaultUnknown = slot 1 + Unknown(3, 5).
    private static readonly byte[] Vault1 = Seal(KeySlot.CreatePasswordSlot(OldPassword, LowCost, Vk));
    private static readonly byte[] VaultKit = Seal(
        KeySlot.CreatePasswordSlot(OldPassword, LowCost, Vk),
        KeySlot.CreateRecoveryKitSlot(RecoveryKey, LowCost, Vk));
    private static readonly byte[] VaultUnknown = Seal(KeySlot.CreatePasswordSlot(OldPassword, LowCost, Vk), Unknown3);

    private static byte[] Seal(params byte[][] slots) => VaultBody.Seal(VaultHeader.Build(slots), Vk, Content);

    private static byte[] With(byte[] source, int index, params byte[] bytes)
    {
        var copy = (byte[])source.Clone();
        bytes.CopyTo(copy, index);
        return copy;
    }

    private static byte[] FlipLowBit(byte[] source, int index)
    {
        var copy = (byte[])source.Clone();
        copy[index] ^= 0x01;
        return copy;
    }

    private static byte[] PasswordSlot(byte[] file)
    {
        var offset = VaultHeader.Parse(file).PasswordSlotOffset;
        return file[offset..(offset + VaultFormat.KdfSlotSize)];
    }

    private static byte[] KitSlot(byte[] file)
    {
        var offset = VaultHeader.Parse(file).RecoveryKitSlotOffset!.Value;
        return file[offset..(offset + VaultFormat.KdfSlotSize)];
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

    private static IReadOnlyList<VaultEntry> OpenEntries(byte[] file, byte[] vaultKey) =>
        VaultBody.Open(file, VaultHeader.Parse(file).BodyOffset, vaultKey).Entries;

    private static void AssertAuthenticationFailure(Action action)
    {
        var exception = Assert.Throws<VaultAuthenticationException>(action);

        Assert.Equal("Vault authentication failed.", exception.Message);
        Assert.Null(exception.InnerException);
    }

    // Asserts the new password opens the vault, the old one fails and the kit slot is byte-identical and still opens it.
    private static void AssertPasswordChangedAndKitIntact(byte[] before, byte[] after)
    {
        Assert.Equal(Vk, UnlockWithPassword(after, NewPassword));
        AssertAuthenticationFailure(() => UnlockWithPassword(after, OldPassword));
        Assert.Equal(Content.Entries, OpenEntries(after, Vk));

        Assert.Equal(KitSlot(before), KitSlot(after));
        var kitKey = UnlockWithKit(after);
        Assert.Equal(Vk, kitKey);
        Assert.Equal(Content.Entries, OpenEntries(after, kitKey));
    }

    public static TheoryData<string, byte[], string> InvalidFiles() => new()
    {
        { "147 bytes", VaultKit[..147], "Vault file is truncated." },
        { "bad magic", With(VaultKit, 0, 0x00), "Vault file has an invalid magic." },
        { "version 2", With(VaultKit, 4, 0x02, 0x00), "Vault format version is not supported." },
    };

    public static TheoryData<uint, uint> OutOfRangeParameters() => new()
    {
        { 7, 1 },
        { 1024, 0 },
        { 1024, 33 },
        { 1048577, 1 },
    };

    // --- Change ---

    [Fact]
    public void Change_VaultKit_NewPasswordOpensAndOldFails()
    {
        var result = MasterPassword.Change(VaultKit, OldPassword, NewPassword, LowCost);

        Assert.Equal(Vk, UnlockWithPassword(result, NewPassword));
        AssertAuthenticationFailure(() => UnlockWithPassword(result, OldPassword));
        Assert.Equal(Content.Entries, OpenEntries(result, Vk));
    }

    [Fact]
    public void Change_NewPasswordSlot_HasFreshSaltNonceWrappedKeyAndGivenParameters()
    {
        var parameters = new KdfParameters(2048, 1);

        var result = MasterPassword.Change(VaultKit, OldPassword, NewPassword, parameters);

        var oldSlot = PasswordSlot(VaultKit);
        var newSlot = PasswordSlot(result);
        Assert.NotEqual(oldSlot[13..29], newSlot[13..29]);
        Assert.NotEqual(oldSlot[29..53], newSlot[29..53]);
        Assert.NotEqual(oldSlot[53..101], newSlot[53..101]);
        Assert.Equal(parameters, KdfParameters.ReadFromSlot(newSlot));
        Assert.Equal(LowCost, KdfParameters.ReadFromSlot(oldSlot));
    }

    [Fact]
    public void Change_VaultKit_KeepsKitSlotAndLayout()
    {
        var result = MasterPassword.Change(VaultKit, OldPassword, NewPassword, LowCost);

        AssertPasswordChangedAndKitIntact(VaultKit, result);
        var before = VaultHeader.Parse(VaultKit);
        var after = VaultHeader.Parse(result);
        Assert.Equal(VaultKit[6], result[6]);
        Assert.Equal(before.BodyOffset, after.BodyOffset);
        Assert.Equal(before.RecoveryKitSlotOffset, after.RecoveryKitSlotOffset);
    }

    [Fact]
    public void Change_VaultUnknown_KeepsUnknownRecordByteForByte()
    {
        var result = MasterPassword.Change(VaultUnknown, OldPassword, NewPassword, LowCost);

        var range = Assert.Single(VaultHeader.Parse(result).Slots, slot => slot.Type == 3);
        Assert.Equal(Unknown3, result[range.Offset..(range.Offset + range.Length)]);
        Assert.Equal(Vk, UnlockWithPassword(result, NewPassword));
        Assert.Equal(Content.Entries, OpenEntries(result, Vk));
    }

    [Fact]
    public void Change_VaultKit_ResealsBodyUnderNewHeader()
    {
        var result = MasterPassword.Change(VaultKit, OldPassword, NewPassword, LowCost);

        var b = VaultHeader.Parse(VaultKit).BodyOffset;
        Assert.Equal(b, VaultHeader.Parse(result).BodyOffset);
        Assert.NotEqual(VaultKit[b..(b + 24)], result[b..(b + 24)]);

        byte[] newHeaderOldBody = [.. result.AsSpan(0, b), .. VaultKit.AsSpan(b)];
        byte[] oldHeaderNewBody = [.. VaultKit.AsSpan(0, b), .. result.AsSpan(b)];
        AssertAuthenticationFailure(() => VaultBody.Open(newHeaderOldBody, b, Vk));
        AssertAuthenticationFailure(() => VaultBody.Open(oldHeaderNewBody, b, Vk));
    }

    [Fact]
    public void Change_SamePassword_UsesFreshSaltWrapNonceAndBodyNonce()
    {
        var result = MasterPassword.Change(Vault1, OldPassword, OldPassword, LowCost);

        var oldSlot = PasswordSlot(Vault1);
        var newSlot = PasswordSlot(result);
        var b = VaultHeader.Parse(Vault1).BodyOffset;
        Assert.NotEqual(oldSlot[13..29], newSlot[13..29]);
        Assert.NotEqual(oldSlot[29..53], newSlot[29..53]);
        Assert.NotEqual(Vault1[b..(b + 24)], result[b..(b + 24)]);
        Assert.Equal(Vk, UnlockWithPassword(result, OldPassword));
    }

    [Fact]
    public void Change_Chained_OnlyLastPasswordOpensAndKitStillOpens()
    {
        const string PasswordA = "fictitious passphrase alpha";
        const string PasswordB = "fictitious passphrase bravo";

        var afterA = MasterPassword.Change(VaultKit, OldPassword, PasswordA, LowCost);
        var afterB = MasterPassword.Change(afterA, PasswordA, PasswordB, LowCost);

        Assert.Equal(Vk, UnlockWithPassword(afterB, PasswordB));
        AssertAuthenticationFailure(() => UnlockWithPassword(afterB, OldPassword));
        AssertAuthenticationFailure(() => UnlockWithPassword(afterB, PasswordA));
        Assert.Equal(KitSlot(VaultKit), KitSlot(afterB));
        Assert.Equal(Content.Entries, OpenEntries(afterB, UnlockWithKit(afterB)));
    }

    [Fact]
    public void Change_NfdNewPassword_UnlocksWithNfc()
    {
        const string Nfd = "new fictitious passphrase café";
        const string Nfc = "new fictitious passphrase café";

        var result = MasterPassword.Change(Vault1, OldPassword, Nfd, LowCost);

        Assert.Equal(Vk, UnlockWithPassword(result, Nfc));
    }

    [Fact]
    public void Change_Input_IsNotModified()
    {
        var file = (byte[])VaultKit.Clone();

        MasterPassword.Change(file, OldPassword, NewPassword, LowCost);

        Assert.Equal(VaultKit, file);
    }

    // --- Change errors ---

    [Fact]
    public void Change_WrongCurrentPassword_ThrowsVaultAuthenticationException()
    {
        AssertAuthenticationFailure(() => MasterPassword.Change(VaultKit, NewPassword, NewPassword, LowCost));
    }

    [Fact]
    public void Change_FlippedBodyCiphertextByte_ThrowsVaultAuthenticationException()
    {
        var file = FlipLowBit(VaultKit, VaultHeader.Parse(VaultKit).BodyOffset + 24);

        AssertAuthenticationFailure(() => MasterPassword.Change(file, OldPassword, NewPassword, LowCost));
    }

    [Fact]
    public void Change_FlippedKitWrappedKeyByte_ThrowsVaultAuthenticationException()
    {
        var file = FlipLowBit(VaultKit, VaultHeader.Parse(VaultKit).RecoveryKitSlotOffset!.Value + 53);

        AssertAuthenticationFailure(() => MasterPassword.Change(file, OldPassword, NewPassword, LowCost));
    }

    [Fact]
    public void Change_FlippedUnknownSlotByte_ThrowsVaultAuthenticationException()
    {
        var range = Assert.Single(VaultHeader.Parse(VaultUnknown).Slots, slot => slot.Type == 3);
        var file = FlipLowBit(VaultUnknown, range.Offset + 3);

        AssertAuthenticationFailure(() => MasterPassword.Change(file, OldPassword, NewPassword, LowCost));
    }

    [Theory]
    [MemberData(nameof(InvalidFiles))]
    public void Change_InvalidFile_ThrowsVaultFormatException(string name, byte[] file, string message)
    {
        Assert.NotNull(name);

        var exception = Assert.Throws<VaultFormatException>(() => MasterPassword.Change(file, OldPassword, NewPassword, LowCost));

        Assert.Equal(message, exception.Message);
    }

    [Theory]
    [MemberData(nameof(OutOfRangeParameters))]
    public void Change_ParametersOutOfRange_ThrowsArgumentOutOfRangeException(uint memoryKiB, uint iterations)
    {
        var parameters = new KdfParameters(memoryKiB, iterations);

        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => MasterPassword.Change(VaultKit, OldPassword, NewPassword, parameters));

        Assert.Equal("parameters", exception.ParamName);
    }

    [Fact]
    public void Change_NewPasswordWithLoneSurrogate_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => MasterPassword.Change(Vault1, OldPassword, "\uD800", LowCost));
    }

    // --- Reset ---

    [Fact]
    public void Reset_WithVaultKey_MatchesChange()
    {
        var result = MasterPassword.Reset(VaultKit, Vk, NewPassword, LowCost);

        AssertPasswordChangedAndKitIntact(VaultKit, result);
        Assert.Equal(VaultHeader.Parse(VaultKit).BodyOffset, VaultHeader.Parse(result).BodyOffset);
    }

    [Fact]
    public void Reset_AfterKitUnwrap_NewPasswordOpens()
    {
        var kitKey = UnlockWithKit(VaultKit);

        var result = MasterPassword.Reset(VaultKit, kitKey, NewPassword, LowCost);

        AssertPasswordChangedAndKitIntact(VaultKit, result);
    }

    [Fact]
    public void Reset_WrongVaultKey_ThrowsVaultAuthenticationException()
    {
        var wrongKey = Enumerable.Repeat((byte)0xAA, 32).ToArray();

        AssertAuthenticationFailure(() => MasterPassword.Reset(VaultKit, wrongKey, NewPassword, LowCost));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    [InlineData(33)]
    public void Reset_WrongVaultKeyLength_ThrowsArgumentException(int length)
    {
        var vaultKey = new byte[length];

        Assert.Throws<ArgumentException>(() => MasterPassword.Reset(VaultKit, vaultKey, NewPassword, LowCost));
    }

    [Theory]
    [MemberData(nameof(InvalidFiles))]
    public void Reset_InvalidFile_ThrowsVaultFormatException(string name, byte[] file, string message)
    {
        Assert.NotNull(name);

        var exception = Assert.Throws<VaultFormatException>(() => MasterPassword.Reset(file, Vk, NewPassword, LowCost));

        Assert.Equal(message, exception.Message);
    }

    [Theory]
    [MemberData(nameof(OutOfRangeParameters))]
    public void Reset_ParametersOutOfRange_ThrowsArgumentOutOfRangeException(uint memoryKiB, uint iterations)
    {
        var parameters = new KdfParameters(memoryKiB, iterations);

        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => MasterPassword.Reset(VaultKit, Vk, NewPassword, parameters));

        Assert.Equal("parameters", exception.ParamName);
    }

    [Fact]
    public void Reset_Inputs_AreNotModified()
    {
        var file = (byte[])VaultKit.Clone();
        var vaultKey = (byte[])Vk.Clone();

        MasterPassword.Reset(file, vaultKey, NewPassword, LowCost);

        Assert.Equal(VaultKit, file);
        Assert.Equal(Vk, vaultKey);
    }

    // --- End to end ---

    [Fact]
    public void Change_WrittenVault_ReadsBackUnlocksAndKeepsBackup()
    {
        var directory = Directory.CreateTempSubdirectory("pm-masterpassword-");
        try
        {
            var path = Path.Combine(directory.FullName, $"{Guid.NewGuid()}.pmvault");
            VaultFile.Write(path, VaultKit);

            var changed = MasterPassword.Change(VaultFile.Read(path), OldPassword, NewPassword, LowCost);
            VaultFile.Write(path, changed);

            var bytes = VaultFile.Read(path);
            var parsed = VaultHeader.Parse(bytes);
            var vaultKey = new byte[VaultFormat.KeySize];
            KeySlot.UnwrapPasswordSlot(bytes, parsed.PasswordSlotOffset, NewPassword, vaultKey);

            Assert.Equal(Vk, vaultKey);
            Assert.Equal(changed, bytes);
            Assert.Equal(Content.Entries, VaultBody.Open(bytes, parsed.BodyOffset, vaultKey).Entries);
            Assert.Equal(VaultKit, File.ReadAllBytes(path + VaultFile.BackupSuffix));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
