using System.Buffers.Binary;
using PasswordManager.Core.Vault;

namespace PasswordManager.Core.Tests.Vault;

public class KeySlotTests
{
    private const string Password = "correct horse battery staple";

    // Fictitious recovery key 0x01..0x14, Vault Key 0x20..0x3F and wrap nonce 0x40..0x57.
    private static readonly byte[] RecoveryKey = [.. Enumerable.Range(0x01, 20).Select(i => (byte)i)];
    private static readonly byte[] Vk = [.. Enumerable.Range(0x20, 32).Select(i => (byte)i)];
    private static readonly byte[] N = [.. Enumerable.Range(0x40, 24).Select(i => (byte)i)];

    private static readonly KdfParameters LowCost = new(1024, 2);

    // AAD_wrap = file[0..6) || slot[0..29) with salt 0x00..0x0F at 1024 KiB / 2: type 1 (password) and type 2 (recovery kit).
    private static readonly byte[] Aad1 = Convert.FromHexString("504d5646010001620001000400000200000001000102030405060708090a0b0c0d0e0f");
    private static readonly byte[] Aad2 = Convert.FromHexString("504d5646010002620001000400000200000001000102030405060708090a0b0c0d0e0f");

    // Computed with HChaCha20 in pure Python plus OpenSSL ChaCha20-Poly1305 (independent of libsodium).
    private static readonly byte[] W1 = Convert.FromHexString("2e8a17f179bdf5a651b7b489c0ce899222020515738dd5df49db643b04d135cc002785a15a71b4953a359d825d34a848");
    private static readonly byte[] W2 = Convert.FromHexString("fa2d2e816d758690c33354f04cee974acc4fd17d6fcd7f7e914492971f8c52256ac12581922863d5d3e2b9e93c85a1f6");

    private static readonly byte[] Preamble = Convert.FromHexString("504d56460100");

    // Slot k (101 bytes) = AADk[6..35) || N || Wk.
    private static readonly byte[] Slot1 = [.. Aad1.AsSpan(6), .. N, .. W1];
    private static readonly byte[] Slot2 = [.. Aad2.AsSpan(6), .. N, .. W2];

    // F1 (108 bytes) = preamble | slotCount 1 | Slot1; F2 (209 bytes) = preamble | slotCount 2 | Slot1 | Slot2.
    private static readonly byte[] F1 = [.. Preamble, 0x01, .. Slot1];
    private static readonly byte[] F2 = [.. Preamble, 0x02, .. Slot1, .. Slot2];

    private const int Slot1Offset = 7;
    private const int Slot2Offset = 108;

    private static byte[] Filled(int length) => [.. Enumerable.Repeat((byte)0xAA, length)];

    private static byte[] FlipLowBit(byte[] source, int index)
    {
        var copy = (byte[])source.Clone();
        copy[index] ^= 0x01;
        return copy;
    }

    private static VaultAuthenticationException AssertAuthenticationFailure(Action unwrap, byte[] vaultKey)
    {
        var exception = Assert.Throws<VaultAuthenticationException>(unwrap);

        Assert.Equal("Vault authentication failed.", exception.Message);
        Assert.Null(exception.InnerException);
        Assert.All(vaultKey, b => Assert.Equal(0, b));
        return exception;
    }

    // Offsets in F1 that keep the slot format-valid: magic/version, memory low byte, iterations low byte,
    // salt, nonce and wrapped key.
    public static TheoryData<int> F1TamperOffsets() =>
    [
        .. Enumerable.Range(0, 6),
        11,
        15,
        .. Enumerable.Range(20, 16),
        .. Enumerable.Range(36, 24),
        .. Enumerable.Range(60, 48),
    ];

    public static TheoryData<int> SlotIndexes() => [.. Enumerable.Range(0, VaultFormat.KdfSlotSize)];

    // --- Known answers and layout ---

    [Fact]
    public void BuildWrapAad_PreambleAndSlot1_MatchesKnownAad1()
    {
        var aad = new byte[35];

        KeySlot.BuildWrapAad(Preamble, Slot1, aad);

        Assert.Equal(Aad1, aad);
    }

    [Fact]
    public void BuildWrapAad_PreambleAndSlot2_MatchesKnownAad2()
    {
        var aad = new byte[35];

        KeySlot.BuildWrapAad(Preamble, Slot2, aad);

        Assert.Equal(Aad2, aad);
    }

    [Fact]
    public void KnownAnswerFiles_HaveExpectedLayout()
    {
        Assert.Equal(101, Slot1.Length);
        Assert.Equal(108, F1.Length);
        Assert.Equal(209, F2.Length);
        Assert.Equal(Slot2, F2.AsSpan(Slot2Offset).ToArray());
    }

    [Fact]
    public void UnwrapPasswordSlot_KnownAnswerF1_ReturnsVaultKey()
    {
        var vaultKey = Filled(32);

        KeySlot.UnwrapPasswordSlot(F1, Slot1Offset, Password, vaultKey);

        Assert.Equal(Vk, vaultKey);
    }

    [Fact]
    public void UnwrapPasswordSlot_KnownAnswerF2Slot1_ReturnsVaultKey()
    {
        var vaultKey = Filled(32);

        KeySlot.UnwrapPasswordSlot(F2, Slot1Offset, Password, vaultKey);

        Assert.Equal(Vk, vaultKey);
    }

    [Fact]
    public void UnwrapRecoveryKitSlot_KnownAnswerF2AtOffset108_ReturnsVaultKey()
    {
        var vaultKey = Filled(32);

        KeySlot.UnwrapRecoveryKitSlot(F2, Slot2Offset, RecoveryKey, vaultKey);

        Assert.Equal(Vk, vaultKey);
    }

    [Fact]
    public void Unwrap_Inputs_AreNotModified()
    {
        var file = (byte[])F2.Clone();
        var recoveryKey = (byte[])RecoveryKey.Clone();

        KeySlot.UnwrapPasswordSlot(file, Slot1Offset, Password, new byte[32]);
        KeySlot.UnwrapRecoveryKitSlot(file, Slot2Offset, recoveryKey, new byte[32]);

        Assert.Equal(F2, file);
        Assert.Equal(RecoveryKey, recoveryKey);
    }

    // --- Wrong secret ---

    [Fact]
    public void UnwrapPasswordSlot_WrongPassword_ThrowsSameExceptionAsTamperedSalt()
    {
        var wrongPasswordKey = Filled(32);
        var tamperedSaltKey = Filled(32);
        var tampered = FlipLowBit(F1, 20);

        var wrongPassword = AssertAuthenticationFailure(
            () => KeySlot.UnwrapPasswordSlot(F1, Slot1Offset, "correct horse battery stapla", wrongPasswordKey),
            wrongPasswordKey);
        var tamperedSalt = AssertAuthenticationFailure(
            () => KeySlot.UnwrapPasswordSlot(tampered, Slot1Offset, Password, tamperedSaltKey),
            tamperedSaltKey);

        Assert.Equal(tamperedSalt.GetType(), wrongPassword.GetType());
        Assert.Equal(tamperedSalt.Message, wrongPassword.Message);
    }

    [Fact]
    public void UnwrapRecoveryKitSlot_WrongRecoveryKey_ThrowsSameExceptionAsTamperedSalt()
    {
        var wrongKitKey = Filled(32);
        var tamperedSaltKey = Filled(32);
        var wrongKit = FlipLowBit(RecoveryKey, 19);
        var tampered = FlipLowBit(F2, Slot2Offset + VaultFormat.SlotSaltOffset);

        var wrongKitException = AssertAuthenticationFailure(
            () => KeySlot.UnwrapRecoveryKitSlot(F2, Slot2Offset, wrongKit, wrongKitKey),
            wrongKitKey);
        var tamperedSalt = AssertAuthenticationFailure(
            () => KeySlot.UnwrapRecoveryKitSlot(tampered, Slot2Offset, RecoveryKey, tamperedSaltKey),
            tamperedSaltKey);

        Assert.Equal(tamperedSalt.GetType(), wrongKitException.GetType());
        Assert.Equal(tamperedSalt.Message, wrongKitException.Message);
    }

    // --- Tamper ---

    [Theory]
    [MemberData(nameof(F1TamperOffsets))]
    public void UnwrapPasswordSlot_FlippedBoundByte_ThrowsVaultAuthenticationException(int offset)
    {
        var vaultKey = Filled(32);
        var file = FlipLowBit(F1, offset);

        AssertAuthenticationFailure(() => KeySlot.UnwrapPasswordSlot(file, Slot1Offset, Password, vaultKey), vaultKey);
    }

    // --- Excluded from the AAD ---

    [Fact]
    public void UnwrapPasswordSlot_ChangedSlotCount_StillReturnsVaultKey()
    {
        var vaultKey = Filled(32);
        var file = (byte[])F1.Clone();
        file[VaultFormat.SlotCountOffset] = 0x05;

        KeySlot.UnwrapPasswordSlot(file, Slot1Offset, Password, vaultKey);

        Assert.Equal(Vk, vaultKey);
    }

    [Theory]
    [MemberData(nameof(SlotIndexes))]
    public void UnwrapPasswordSlot_ChangedSlot2Byte_StillReturnsVaultKey(int index)
    {
        var vaultKey = Filled(32);
        var file = (byte[])F2.Clone();
        file[Slot2Offset + index] ^= 0xFF;

        KeySlot.UnwrapPasswordSlot(file, Slot1Offset, Password, vaultKey);

        Assert.Equal(Vk, vaultKey);
    }

    [Theory]
    [MemberData(nameof(SlotIndexes))]
    public void UnwrapRecoveryKitSlot_ChangedSlot1Byte_StillReturnsVaultKey(int index)
    {
        var vaultKey = Filled(32);
        var file = (byte[])F2.Clone();
        file[Slot1Offset + index] ^= 0xFF;

        KeySlot.UnwrapRecoveryKitSlot(file, Slot2Offset, RecoveryKey, vaultKey);

        Assert.Equal(Vk, vaultKey);
    }

    // --- Round trip ---

    [Fact]
    public void CreatePasswordSlot_LowCost_WritesExpectedLayout()
    {
        var slot = KeySlot.CreatePasswordSlot(Password, LowCost, Vk);

        AssertSlotLayout(slot, slotType: 1);
    }

    [Fact]
    public void CreateRecoveryKitSlot_LowCost_WritesExpectedLayout()
    {
        var slot = KeySlot.CreateRecoveryKitSlot(RecoveryKey, LowCost, Vk);

        AssertSlotLayout(slot, slotType: 2);
    }

    [Fact]
    public void CreatePasswordSlot_EmbeddedAtOffset7_UnwrapsToVaultKey()
    {
        var slot = KeySlot.CreatePasswordSlot(Password, LowCost, Vk);
        byte[] file = [.. Preamble, 0x01, .. slot];
        var vaultKey = Filled(32);

        KeySlot.UnwrapPasswordSlot(file, Slot1Offset, Password, vaultKey);

        Assert.Equal(Vk, vaultKey);
    }

    [Fact]
    public void CreateRecoveryKitSlot_EmbeddedAtOffset108_UnwrapsToVaultKey()
    {
        var slot = KeySlot.CreateRecoveryKitSlot(RecoveryKey, LowCost, Vk);
        byte[] file = [.. Preamble, 0x02, .. Slot1, .. slot];
        var vaultKey = Filled(32);

        KeySlot.UnwrapRecoveryKitSlot(file, Slot2Offset, RecoveryKey, vaultKey);

        Assert.Equal(Vk, vaultKey);
    }

    [Fact]
    public void CreatePasswordSlot_TwoCalls_UseFreshSaltNonceAndWrappedKey()
    {
        var first = KeySlot.CreatePasswordSlot(Password, LowCost, Vk);
        var second = KeySlot.CreatePasswordSlot(Password, LowCost, Vk);

        AssertFreshRandomFields(first, second);
    }

    [Fact]
    public void CreateRecoveryKitSlot_TwoCalls_UseFreshSaltNonceAndWrappedKey()
    {
        var first = KeySlot.CreateRecoveryKitSlot(RecoveryKey, LowCost, Vk);
        var second = KeySlot.CreateRecoveryKitSlot(RecoveryKey, LowCost, Vk);

        AssertFreshRandomFields(first, second);
    }

    [Fact]
    public void Create_Inputs_AreNotModified()
    {
        var recoveryKey = (byte[])RecoveryKey.Clone();
        var vaultKey = (byte[])Vk.Clone();

        KeySlot.CreatePasswordSlot(Password, LowCost, vaultKey);
        KeySlot.CreateRecoveryKitSlot(recoveryKey, LowCost, vaultKey);

        Assert.Equal(RecoveryKey, recoveryKey);
        Assert.Equal(Vk, vaultKey);
    }

    // --- Arguments and format ---

    [Theory]
    [InlineData(31)]
    [InlineData(33)]
    public void CreateAndUnwrap_WrongVaultKeyLength_ThrowsArgumentException(int length)
    {
        var vaultKey = new byte[length];

        Assert.Throws<ArgumentException>(() => KeySlot.CreatePasswordSlot(Password, LowCost, vaultKey));
        Assert.Throws<ArgumentException>(() => KeySlot.CreateRecoveryKitSlot(RecoveryKey, LowCost, vaultKey));
        Assert.Throws<ArgumentException>(() => KeySlot.UnwrapPasswordSlot(F2, Slot1Offset, Password, vaultKey));
        Assert.Throws<ArgumentException>(() => KeySlot.UnwrapRecoveryKitSlot(F2, Slot2Offset, RecoveryKey, vaultKey));
    }

    [Theory]
    [InlineData(19)]
    [InlineData(21)]
    public void CreateAndUnwrapRecoveryKit_WrongRecoveryKeyLength_ThrowsArgumentException(int length)
    {
        var recoveryKey = new byte[length];

        Assert.Throws<ArgumentException>(() => KeySlot.CreateRecoveryKitSlot(recoveryKey, LowCost, Vk));
        Assert.Throws<ArgumentException>(() => KeySlot.UnwrapRecoveryKitSlot(F2, Slot2Offset, recoveryKey, new byte[32]));
    }

    [Theory]
    [InlineData(7u, 2u)]
    [InlineData(1024u, 33u)]
    public void Create_ParametersOutOfRange_ThrowsArgumentOutOfRangeException(uint memoryKiB, uint iterations)
    {
        var parameters = new KdfParameters(memoryKiB, iterations);

        Assert.Throws<ArgumentOutOfRangeException>(() => KeySlot.CreatePasswordSlot(Password, parameters, Vk));
        Assert.Throws<ArgumentOutOfRangeException>(() => KeySlot.CreateRecoveryKitSlot(RecoveryKey, parameters, Vk));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(6)]
    public void Unwrap_SlotOffsetBeforeFirstSlot_ThrowsArgumentOutOfRangeException(int slotOffset)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => KeySlot.UnwrapPasswordSlot(F2, slotOffset, Password, new byte[32]));
        Assert.Throws<ArgumentOutOfRangeException>(() => KeySlot.UnwrapRecoveryKitSlot(F2, slotOffset, RecoveryKey, new byte[32]));
    }

    [Fact]
    public void UnwrapPasswordSlot_RecoveryKitSlot_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => KeySlot.UnwrapPasswordSlot(F2, Slot2Offset, Password, new byte[32]));
    }

    [Fact]
    public void UnwrapRecoveryKitSlot_PasswordSlot_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => KeySlot.UnwrapRecoveryKitSlot(F2, Slot1Offset, RecoveryKey, new byte[32]));
    }

    [Fact]
    public void Unwrap_FileCutTo107Bytes_ThrowsVaultFormatException()
    {
        var file = F1[..107];

        Assert.Throws<VaultFormatException>(() => KeySlot.UnwrapPasswordSlot(file, Slot1Offset, Password, new byte[32]));
        Assert.Throws<VaultFormatException>(() => KeySlot.UnwrapRecoveryKitSlot(file, Slot1Offset, RecoveryKey, new byte[32]));
    }

    [Theory]
    [InlineData(109)]
    [InlineData(int.MaxValue)]
    public void Unwrap_SlotOffsetPastEndOfFile_ThrowsVaultFormatException(int slotOffset)
    {
        Assert.Throws<VaultFormatException>(() => KeySlot.UnwrapPasswordSlot(F1, slotOffset, Password, new byte[32]));
        Assert.Throws<VaultFormatException>(() => KeySlot.UnwrapRecoveryKitSlot(F1, slotOffset, RecoveryKey, new byte[32]));
    }

    [Fact]
    public void Unwrap_TypeZeroSlot_ThrowsVaultFormatException()
    {
        var file = (byte[])F1.Clone();
        file[Slot1Offset + VaultFormat.SlotTypeOffset] = 0x00;

        Assert.Throws<VaultFormatException>(() => KeySlot.UnwrapPasswordSlot(file, Slot1Offset, Password, new byte[32]));
        Assert.Throws<VaultFormatException>(() => KeySlot.UnwrapRecoveryKitSlot(file, Slot1Offset, RecoveryKey, new byte[32]));
    }

    private static void AssertSlotLayout(byte[] slot, byte slotType)
    {
        Assert.Equal(101, slot.Length);
        Assert.Equal(slotType, slot[0]);
        Assert.Equal(new byte[] { 0x62, 0x00 }, slot[1..3]);
        Assert.Equal(0x01, slot[3]);
        Assert.Equal(new byte[] { 0x00, 0x04, 0x00, 0x00 }, slot[4..8]);
        Assert.Equal(new byte[] { 0x02, 0x00, 0x00, 0x00 }, slot[8..12]);
        Assert.Equal(0x01, slot[12]);
        Assert.Equal(98, BinaryPrimitives.ReadUInt16LittleEndian(slot.AsSpan(1)));
        Assert.Equal(LowCost, KdfParameters.ReadFromSlot(slot));
    }

    private static void AssertFreshRandomFields(byte[] first, byte[] second)
    {
        Assert.NotEqual(first[13..29], second[13..29]);
        Assert.NotEqual(first[29..53], second[29..53]);
        Assert.NotEqual(first[53..101], second[53..101]);
    }
}
