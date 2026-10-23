using System.Buffers.Binary;
using PasswordManager.Core.Vault;

namespace PasswordManager.Core.Tests.Vault;

public class KdfParametersTests
{
    [Fact]
    public void ReadFromSlot_LiteralLittleEndianBytes_ReturnsMemoryAndIterations()
    {
        var slot = new byte[101];
        slot[0] = 0x01;
        slot[1] = 0x62;
        slot[2] = 0x00;
        slot[3] = 0x01;
        new byte[] { 0x00, 0x00, 0x01, 0x00 }.CopyTo(slot, 4);
        new byte[] { 0x03, 0x00, 0x00, 0x00 }.CopyTo(slot, 8);
        slot[12] = 0x01;

        var parameters = KdfParameters.ReadFromSlot(slot);

        Assert.Equal(65536u, parameters.MemoryKiB);
        Assert.Equal(3u, parameters.Iterations);
    }

    [Theory]
    [InlineData((byte)1)]
    [InlineData((byte)2)]
    public void ReadFromSlot_PasswordAndRecoveryKitSlotTypes_AreAccepted(byte slotType)
    {
        var slot = BuildSlot(slotType: slotType);

        var parameters = KdfParameters.ReadFromSlot(slot);

        Assert.Equal(new KdfParameters(1024, 2), parameters);
    }

    [Theory]
    [InlineData(8u, 1u)]
    [InlineData(1048576u, 32u)]
    [InlineData(8u, 32u)]
    [InlineData(1048576u, 1u)]
    public void ReadFromSlot_EdgeValues_AreAccepted(uint memoryKiB, uint iterations)
    {
        var slot = BuildSlot(memoryKiB: memoryKiB, iterations: iterations);

        var parameters = KdfParameters.ReadFromSlot(slot);

        Assert.Equal(new KdfParameters(memoryKiB, iterations), parameters);
        Assert.True(parameters.IsInRange);
    }

    [Fact]
    public void ReadFromSlot_SlotLongerThanRecord_ReadsFromTheStart()
    {
        var slot = new byte[VaultFormat.KdfSlotSize + 40];
        BuildSlot().CopyTo(slot, 0);
        Array.Fill(slot, (byte)0xFF, VaultFormat.KdfSlotSize, 40);

        var parameters = KdfParameters.ReadFromSlot(slot);

        Assert.Equal(new KdfParameters(1024, 2), parameters);
    }

    [Fact]
    public void ReadFromSlot_SaltSlicedAtSlotSaltOffset_IsTheSaltBytes()
    {
        var salt = new byte[VaultFormat.SaltSize];
        for (var i = 0; i < salt.Length; i++)
        {
            salt[i] = (byte)(0xA0 + i);
        }

        var slot = BuildSlot(salt: salt);

        Assert.Equal(salt, slot.AsSpan(VaultFormat.SlotSaltOffset, VaultFormat.SaltSize).ToArray());
    }

    [Fact]
    public void Default_MatchesVaultFormatDefaults()
    {
        Assert.Equal(VaultFormat.DefaultKdfMemoryKiB, KdfParameters.Default.MemoryKiB);
        Assert.Equal(VaultFormat.DefaultKdfIterations, KdfParameters.Default.Iterations);
        Assert.Equal(65536u, KdfParameters.Default.MemoryKiB);
        Assert.Equal(3u, KdfParameters.Default.Iterations);
        Assert.True(KdfParameters.Default.IsInRange);
    }

    [Theory]
    [InlineData(0u, 0u, false)]
    [InlineData(7u, 3u, false)]
    [InlineData(8u, 1u, true)]
    [InlineData(1048576u, 32u, true)]
    [InlineData(1048577u, 3u, false)]
    [InlineData(65536u, 0u, false)]
    [InlineData(65536u, 33u, false)]
    public void IsInRange_Values_FollowFormatBounds(uint memoryKiB, uint iterations, bool expected)
    {
        Assert.Equal(expected, new KdfParameters(memoryKiB, iterations).IsInRange);
    }

    [Fact]
    public void IsInRange_DefaultStruct_IsFalse()
    {
        Assert.False(default(KdfParameters).IsInRange);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    public void ReadFromSlot_TruncatedSlot_ThrowsVaultFormatException(int length)
    {
        var slot = BuildSlot().AsSpan(0, length).ToArray();

        Assert.Throws<VaultFormatException>(() => KdfParameters.ReadFromSlot(slot));
    }

    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)3)]
    [InlineData((byte)255)]
    public void ReadFromSlot_InvalidSlotType_ThrowsVaultFormatException(byte slotType)
    {
        var slot = BuildSlot(slotType: slotType);

        Assert.Throws<VaultFormatException>(() => KdfParameters.ReadFromSlot(slot));
    }

    [Theory]
    [InlineData((ushort)97)]
    [InlineData((ushort)99)]
    public void ReadFromSlot_InvalidSlotLength_ThrowsVaultFormatException(ushort slotLength)
    {
        var slot = BuildSlot(slotLength: slotLength);

        Assert.Throws<VaultFormatException>(() => KdfParameters.ReadFromSlot(slot));
    }

    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)2)]
    public void ReadFromSlot_InvalidKdfAlgorithm_ThrowsVaultFormatException(byte algorithm)
    {
        var slot = BuildSlot(algorithm: algorithm);

        Assert.Throws<VaultFormatException>(() => KdfParameters.ReadFromSlot(slot));
    }

    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)2)]
    public void ReadFromSlot_InvalidParallelism_ThrowsVaultFormatException(byte parallelism)
    {
        var slot = BuildSlot(parallelism: parallelism);

        Assert.Throws<VaultFormatException>(() => KdfParameters.ReadFromSlot(slot));
    }

    [Theory]
    [InlineData(7u)]
    [InlineData(1048577u)]
    [InlineData(uint.MaxValue)]
    public void ReadFromSlot_MemoryOutOfRange_ThrowsVaultFormatException(uint memoryKiB)
    {
        var slot = BuildSlot(memoryKiB: memoryKiB);

        Assert.Throws<VaultFormatException>(() => KdfParameters.ReadFromSlot(slot));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(33u)]
    [InlineData(uint.MaxValue)]
    public void ReadFromSlot_IterationsOutOfRange_ThrowsVaultFormatException(uint iterations)
    {
        var slot = BuildSlot(iterations: iterations);

        Assert.Throws<VaultFormatException>(() => KdfParameters.ReadFromSlot(slot));
    }

    [Fact]
    public void VaultFormatException_StandardConstructors_KeepMessageAndInnerException()
    {
        var inner = new InvalidOperationException("inner");

        Assert.NotNull(new VaultFormatException().Message);
        Assert.Equal("boom", new VaultFormatException("boom").Message);

        var wrapped = new VaultFormatException("boom", inner);
        Assert.Equal("boom", wrapped.Message);
        Assert.Same(inner, wrapped.InnerException);
    }

    // Builds a 101-byte slot by hand (independent of the code under test).
    private static byte[] BuildSlot(
        byte slotType = 1,
        ushort slotLength = 98,
        byte algorithm = 1,
        uint memoryKiB = 1024,
        uint iterations = 2,
        byte parallelism = 1,
        byte[]? salt = null)
    {
        var slot = new byte[101];
        slot[0] = slotType;
        BinaryPrimitives.WriteUInt16LittleEndian(slot.AsSpan(1, 2), slotLength);
        slot[3] = algorithm;
        BinaryPrimitives.WriteUInt32LittleEndian(slot.AsSpan(4, 4), memoryKiB);
        BinaryPrimitives.WriteUInt32LittleEndian(slot.AsSpan(8, 4), iterations);
        slot[12] = parallelism;
        (salt ?? new byte[16]).CopyTo(slot, 13);
        return slot;
    }
}
