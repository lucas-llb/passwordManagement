using PasswordManager.Core.Vault;

namespace PasswordManager.Core.Tests.Vault;

public class RecoveryKitTests
{
    private const string OldPassword = "correct horse battery staple";
    private const string NewPassword = "new fictitious passphrase";

    // Text of RecoveryKey 0x01..0x14, the kit inside VaultKit.
    private const string KitText = "0410-6105-0R3G-G28A-1C60-T3GF-208H-44RM";
    private const string OtherKitText = "0000-0000-0000-0000-0000-0000-0000-0000";
    private const string KitTextWithU = "0410-6105-0R3G-G28A-1C60-T3GF-208H-44RU";
    private const string KitText31Symbols = "0410-6105-0R3G-G28A-1C60-T3GF-208H-44R";

    private const string CanonicalPattern = "^([0-9A-HJKMNP-TV-Z]{4}-){7}[0-9A-HJKMNP-TV-Z]{4}$";

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

    private static byte[] BodyNonce(byte[] file)
    {
        var offset = VaultHeader.Parse(file).BodyOffset;
        return file[offset..(offset + VaultFormat.NonceSize)];
    }

    private static byte[] UnlockWithPassword(byte[] file, string password)
    {
        var vaultKey = new byte[VaultFormat.KeySize];
        KeySlot.UnwrapPasswordSlot(file, VaultHeader.Parse(file).PasswordSlotOffset, password, vaultKey);
        return vaultKey;
    }

    private static byte[] UnlockWithKit(byte[] file, ReadOnlySpan<char> kitText)
    {
        var vaultKey = new byte[VaultFormat.KeySize];
        RecoveryKit.Unlock(file, kitText, vaultKey);
        return vaultKey;
    }

    private static byte[] CreateKit(byte[] file, KdfParameters parameters, out char[] kitText)
    {
        kitText = new char[RecoveryKit.TextLength];
        return RecoveryKit.Create(file, Vk, parameters, kitText);
    }

    private static IReadOnlyList<VaultEntry> OpenEntries(byte[] file, byte[] vaultKey) =>
        VaultBody.Open(file, VaultHeader.Parse(file).BodyOffset, vaultKey).Entries;

    private static void AssertAuthenticationFailure(Action action)
    {
        var exception = Assert.Throws<VaultAuthenticationException>(action);

        Assert.Equal("Vault authentication failed.", exception.Message);
        Assert.Null(exception.InnerException);
    }

    private static void AssertInvalidKit(Action action)
    {
        var exception = Assert.Throws<FormatException>(action);

        Assert.Equal("Recovery kit is invalid.", exception.Message);
        Assert.Null(exception.InnerException);
    }

    private static void AssertNoKit(Action action)
    {
        var exception = Assert.Throws<InvalidOperationException>(action);

        Assert.Equal("Vault has no recovery kit.", exception.Message);
        Assert.Null(exception.InnerException);
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

    // --- Create ---

    [Fact]
    public void Create_Vault1_AddsKitThatUnlocksAndKeepsPasswordSlot()
    {
        var result = CreateKit(Vault1, LowCost, out var kitText);

        Assert.NotNull(VaultHeader.Parse(result).RecoveryKitSlotOffset);
        Assert.Equal(2, result[VaultFormat.SlotCountOffset]);
        var kitKey = UnlockWithKit(result, kitText);
        Assert.Equal(Vk, kitKey);
        Assert.Equal(Content.Entries, OpenEntries(result, kitKey));
        Assert.Equal(PasswordSlot(Vault1), PasswordSlot(result));
        Assert.Equal(Vk, UnlockWithPassword(result, OldPassword));
        Assert.NotEqual(BodyNonce(Vault1), BodyNonce(result));
        Assert.Matches(CanonicalPattern, new string(kitText));
    }

    [Fact]
    public void Create_KitSlot_IsType2WithLength98AndGivenParameters()
    {
        var parameters = new KdfParameters(2048, 1);

        var result = CreateKit(Vault1, parameters, out _);

        var slot = KitSlot(result);
        Assert.Equal(VaultFormat.SlotTypeRecoveryKit, slot[VaultFormat.SlotTypeOffset]);
        Assert.Equal(98, BitConverter.ToUInt16(slot, VaultFormat.SlotLengthOffset));
        Assert.Equal(parameters, KdfParameters.ReadFromSlot(slot));
    }

    [Fact]
    public void Create_TwoCalls_GiveDifferentTextsAndSalts()
    {
        var first = CreateKit(Vault1, LowCost, out var firstText);
        var second = CreateKit(Vault1, LowCost, out var secondText);

        Assert.NotEqual(firstText, secondText);
        Assert.NotEqual(
            KitSlot(first)[VaultFormat.SlotSaltOffset..VaultFormat.SlotWrapNonceOffset],
            KitSlot(second)[VaultFormat.SlotSaltOffset..VaultFormat.SlotWrapNonceOffset]);
    }

    [Fact]
    public void Create_VaultKit_RevokesOldKitAndNewTextUnlocks()
    {
        var result = CreateKit(VaultKit, LowCost, out var kitText);

        AssertAuthenticationFailure(() => UnlockWithKit(result, KitText));
        Assert.Equal(Vk, UnlockWithKit(result, kitText));
        Assert.Equal(2, result[VaultFormat.SlotCountOffset]);
        Assert.Equal(PasswordSlot(VaultKit), PasswordSlot(result));
    }

    [Fact]
    public void Create_VaultUnknown_KeepsUnknownRecordAndSortsSlots()
    {
        var result = CreateKit(VaultUnknown, LowCost, out var kitText);

        var slots = VaultHeader.Parse(result).Slots;
        int[] expectedTypes = [1, 2, 3];
        Assert.Equal(expectedTypes, slots.Select(slot => (int)slot.Type));
        var range = Assert.Single(slots, slot => slot.Type == 3);
        Assert.Equal(Unknown3, result[range.Offset..(range.Offset + range.Length)]);
        Assert.Equal(Vk, UnlockWithKit(result, kitText));
    }

    [Fact]
    public void Create_WrongVaultKey_ThrowsAndLeavesKitTextUntouched()
    {
        var wrongKey = Enumerable.Repeat((byte)0xAA, 32).ToArray();
        var kitText = Enumerable.Repeat('#', RecoveryKit.TextLength).ToArray();

        AssertAuthenticationFailure(() => RecoveryKit.Create(Vault1, wrongKey, LowCost, kitText));

        Assert.All(kitText, c => Assert.Equal('#', c));
    }

    [Theory]
    [MemberData(nameof(InvalidFiles))]
    public void Create_InvalidFile_ThrowsVaultFormatException(string name, byte[] file, string message)
    {
        Assert.NotNull(name);
        var kitText = new char[RecoveryKit.TextLength];

        var exception = Assert.Throws<VaultFormatException>(() => RecoveryKit.Create(file, Vk, LowCost, kitText));

        Assert.Equal(message, exception.Message);
    }

    [Theory]
    [MemberData(nameof(OutOfRangeParameters))]
    public void Create_ParametersOutOfRange_ThrowsArgumentOutOfRangeException(uint memoryKiB, uint iterations)
    {
        var parameters = new KdfParameters(memoryKiB, iterations);
        var kitText = new char[RecoveryKit.TextLength];

        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => RecoveryKit.Create(Vault1, Vk, parameters, kitText));

        Assert.Equal("parameters", exception.ParamName);
    }

    [Theory]
    [InlineData(38)]
    [InlineData(40)]
    public void Create_WrongKitTextLength_ThrowsArgumentException(int length)
    {
        var kitText = new char[length];

        Assert.Throws<ArgumentException>(() => RecoveryKit.Create(Vault1, Vk, LowCost, kitText));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    [InlineData(33)]
    public void Create_WrongVaultKeyLength_ThrowsArgumentException(int length)
    {
        var vaultKey = new byte[length];
        var kitText = new char[RecoveryKit.TextLength];

        Assert.Throws<ArgumentException>(() => RecoveryKit.Create(Vault1, vaultKey, LowCost, kitText));
    }

    [Fact]
    public void Create_Inputs_AreNotModified()
    {
        var file = (byte[])VaultKit.Clone();
        var vaultKey = (byte[])Vk.Clone();
        var kitText = new char[RecoveryKit.TextLength];

        RecoveryKit.Create(file, vaultKey, LowCost, kitText);

        Assert.Equal(VaultKit, file);
        Assert.Equal(Vk, vaultKey);
    }

    // --- Unlock ---

    [Theory]
    [InlineData(KitText)]
    [InlineData("0410-6105-0r3g-g28a-1c60-t3gf-208h-44rm")]
    [InlineData("041061050R3GG28A1C60T3GF208H44RM")]
    [InlineData("O41O-61O5-OR3G-G28A-IC6O-T3GF-2O8H-44RM")]
    public void Unlock_VaultKit_GivesVaultKey(string kitText)
    {
        Assert.Equal(Vk, UnlockWithKit(VaultKit, kitText));
    }

    [Fact]
    public void Unlock_ValidTextForOtherKey_ThrowsAndZeroesVaultKey()
    {
        var vaultKey = Enumerable.Repeat((byte)0xAA, VaultFormat.KeySize).ToArray();

        AssertAuthenticationFailure(() => RecoveryKit.Unlock(VaultKit, OtherKitText, vaultKey));

        Assert.All(vaultKey, b => Assert.Equal(0, b));
    }

    [Theory]
    [InlineData(KitTextWithU)]
    [InlineData(KitText31Symbols)]
    public void Unlock_InvalidText_ThrowsFormatException(string kitText)
    {
        AssertInvalidKit(() => UnlockWithKit(VaultKit, kitText));
    }

    [Theory]
    [InlineData(KitText)]
    [InlineData("bad")]
    public void Unlock_VaultWithoutKit_ThrowsInvalidOperationException(string kitText)
    {
        AssertNoKit(() => UnlockWithKit(Vault1, kitText));
    }

    [Theory]
    [InlineData(VaultFormat.SlotSaltOffset)]
    [InlineData(VaultFormat.SlotWrapNonceOffset)]
    [InlineData(VaultFormat.SlotWrappedKeyOffset)]
    public void Unlock_FlippedKitSlotByte_ThrowsVaultAuthenticationException(int slotIndex)
    {
        var file = FlipLowBit(VaultKit, VaultHeader.Parse(VaultKit).RecoveryKitSlotOffset!.Value + slotIndex);

        AssertAuthenticationFailure(() => UnlockWithKit(file, KitText));
    }

    [Theory]
    [MemberData(nameof(InvalidFiles))]
    public void Unlock_InvalidFile_ThrowsVaultFormatException(string name, byte[] file, string message)
    {
        Assert.NotNull(name);

        var exception = Assert.Throws<VaultFormatException>(() => UnlockWithKit(file, KitText));

        Assert.Equal(message, exception.Message);
    }

    [Theory]
    [InlineData(31)]
    [InlineData(33)]
    public void Unlock_WrongVaultKeyLength_ThrowsArgumentException(int length)
    {
        var vaultKey = new byte[length];

        Assert.Throws<ArgumentException>(() => RecoveryKit.Unlock(VaultKit, KitText, vaultKey));
    }

    // --- ResetPassword ---

    [Fact]
    public void ResetPassword_VaultKit_NewPasswordOpensAndKitIntact()
    {
        var result = RecoveryKit.ResetPassword(VaultKit, KitText, NewPassword, LowCost);

        Assert.Equal(Vk, UnlockWithPassword(result, NewPassword));
        AssertAuthenticationFailure(() => UnlockWithPassword(result, OldPassword));
        Assert.Equal(Content.Entries, OpenEntries(result, Vk));
        Assert.Equal(KitSlot(VaultKit), KitSlot(result));
        Assert.Equal(Vk, UnlockWithKit(result, KitText));
    }

    [Fact]
    public void ResetPassword_VaultUnknownWithKit_KeepsUnknownRecord()
    {
        var withKit = CreateKit(VaultUnknown, LowCost, out var kitText);

        var result = RecoveryKit.ResetPassword(withKit, kitText, NewPassword, LowCost);

        var range = Assert.Single(VaultHeader.Parse(result).Slots, slot => slot.Type == 3);
        Assert.Equal(Unknown3, result[range.Offset..(range.Offset + range.Length)]);
        Assert.Equal(Vk, UnlockWithPassword(result, NewPassword));
    }

    [Fact]
    public void ResetPassword_WrongKit_ThrowsVaultAuthenticationException()
    {
        AssertAuthenticationFailure(() => RecoveryKit.ResetPassword(VaultKit, OtherKitText, NewPassword, LowCost));
    }

    [Fact]
    public void ResetPassword_InvalidText_ThrowsFormatException()
    {
        AssertInvalidKit(() => RecoveryKit.ResetPassword(VaultKit, KitTextWithU, NewPassword, LowCost));
    }

    [Fact]
    public void ResetPassword_VaultWithoutKit_ThrowsInvalidOperationException()
    {
        AssertNoKit(() => RecoveryKit.ResetPassword(Vault1, KitText, NewPassword, LowCost));
    }

    [Fact]
    public void ResetPassword_ParametersOutOfRange_ThrowsBeforeAnyOtherCheck()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => RecoveryKit.ResetPassword(Vault1, "bad", NewPassword, new KdfParameters(7, 1)));

        Assert.Equal("parameters", exception.ParamName);
    }

    [Fact]
    public void ResetPassword_NewPasswordWithLoneSurrogate_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => RecoveryKit.ResetPassword(VaultKit, KitText, "\uD800", LowCost));
    }

    [Fact]
    public void ResetPassword_Input_IsNotModified()
    {
        var file = (byte[])VaultKit.Clone();

        RecoveryKit.ResetPassword(file, KitText, NewPassword, LowCost);

        Assert.Equal(VaultKit, file);
    }

    // --- End to end ---

    [Fact]
    public void CreateUnlockResetPassword_WrittenVault_ReadsBackAndUnlocks()
    {
        var directory = Directory.CreateTempSubdirectory("pm-recoverykit-");
        var kitText = new char[RecoveryKit.TextLength];
        try
        {
            var path = Path.Combine(directory.FullName, $"{Guid.NewGuid()}.pmvault");
            VaultFile.Write(path, Vault1);

            VaultFile.Write(path, RecoveryKit.Create(VaultFile.Read(path), Vk, LowCost, kitText));

            var kitKey = new byte[VaultFormat.KeySize];
            RecoveryKit.Unlock(VaultFile.Read(path), kitText, kitKey);
            Assert.Equal(Vk, kitKey);

            VaultFile.Write(path, RecoveryKit.ResetPassword(VaultFile.Read(path), kitText, NewPassword, LowCost));

            var bytes = VaultFile.Read(path);
            var vaultKey = UnlockWithPassword(bytes, NewPassword);
            Assert.Equal(Vk, vaultKey);
            Assert.Equal(Content.Entries, OpenEntries(bytes, vaultKey));
        }
        finally
        {
            Array.Clear(kitText);
            directory.Delete(recursive: true);
        }
    }
}
