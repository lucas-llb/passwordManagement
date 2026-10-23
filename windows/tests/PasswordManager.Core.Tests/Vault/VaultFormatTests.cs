using NSec.Cryptography;
using PasswordManager.Core.Vault;

namespace PasswordManager.Core.Tests.Vault;

public class VaultFormatTests
{
    private static readonly byte[] ExpectedMagic = [0x50, 0x4D, 0x56, 0x46];

    [Fact]
    public void Magic_IsAsciiPmvf()
    {
        Assert.Equal(ExpectedMagic, VaultFormat.Magic.ToArray());
        Assert.Equal(VaultFormat.MagicSize, VaultFormat.Magic.Length);
        Assert.Equal(4, VaultFormat.MagicSize);
    }

    [Fact]
    public void Header_Constants_MatchSpec()
    {
        Assert.Equal((ushort)1, VaultFormat.Version);
        Assert.Equal(4, VaultFormat.VersionOffset);
        Assert.Equal(6, VaultFormat.SlotCountOffset);
        Assert.Equal(7, VaultFormat.FirstSlotOffset);
        Assert.Equal(6, VaultFormat.PreambleSize);
        Assert.Equal(1, VaultFormat.MinSlotCount);
        Assert.Equal(8, VaultFormat.MaxSlotCount);
    }

    [Fact]
    public void Header_Offsets_AreContiguous()
    {
        Assert.Equal(VaultFormat.MagicSize, VaultFormat.VersionOffset);
        Assert.Equal(VaultFormat.VersionOffset + 2, VaultFormat.SlotCountOffset);
        Assert.Equal(VaultFormat.SlotCountOffset + 1, VaultFormat.FirstSlotOffset);
        Assert.Equal(VaultFormat.SlotCountOffset, VaultFormat.PreambleSize);
    }

    [Fact]
    public void Slot_Constants_MatchSpec()
    {
        Assert.Equal((byte)1, VaultFormat.SlotTypePassword);
        Assert.Equal((byte)2, VaultFormat.SlotTypeRecoveryKit);
        Assert.Equal(3, VaultFormat.SlotHeaderSize);
        Assert.Equal(0, VaultFormat.SlotTypeOffset);
        Assert.Equal(1, VaultFormat.SlotLengthOffset);
        Assert.Equal(3, VaultFormat.SlotKdfAlgorithmOffset);
        Assert.Equal(4, VaultFormat.SlotKdfMemoryOffset);
        Assert.Equal(8, VaultFormat.SlotKdfIterationsOffset);
        Assert.Equal(12, VaultFormat.SlotKdfParallelismOffset);
        Assert.Equal(13, VaultFormat.SlotSaltOffset);
        Assert.Equal(29, VaultFormat.SlotWrapNonceOffset);
        Assert.Equal(53, VaultFormat.SlotWrappedKeyOffset);
        Assert.Equal(98, VaultFormat.KdfSlotBodySize);
        Assert.Equal(101, VaultFormat.KdfSlotSize);
        Assert.Equal(35, VaultFormat.WrapAadSize);
    }

    [Fact]
    public void KdfSlot_FieldsAreContiguous_AndEndAtSlotSize()
    {
        var offset = VaultFormat.SlotTypeOffset;
        offset = AssertNextField(offset, VaultFormat.SlotLengthOffset, 1);
        offset = AssertNextField(offset, VaultFormat.SlotKdfAlgorithmOffset, 2);
        offset = AssertNextField(offset, VaultFormat.SlotKdfMemoryOffset, 1);
        offset = AssertNextField(offset, VaultFormat.SlotKdfIterationsOffset, 4);
        offset = AssertNextField(offset, VaultFormat.SlotKdfParallelismOffset, 4);
        offset = AssertNextField(offset, VaultFormat.SlotSaltOffset, 1);
        offset = AssertNextField(offset, VaultFormat.SlotWrapNonceOffset, VaultFormat.SaltSize);
        offset = AssertNextField(offset, VaultFormat.SlotWrappedKeyOffset, VaultFormat.NonceSize);
        Assert.Equal(VaultFormat.KdfSlotSize, offset + VaultFormat.WrappedKeySize);

        Assert.Equal(VaultFormat.KdfSlotSize - VaultFormat.SlotHeaderSize, VaultFormat.KdfSlotBodySize);
        Assert.Equal(VaultFormat.PreambleSize + VaultFormat.SlotWrapNonceOffset, VaultFormat.WrapAadSize);
    }

    [Fact]
    public void Sizes_MatchSpec()
    {
        Assert.Equal(16, VaultFormat.SaltSize);
        Assert.Equal(32, VaultFormat.KeySize);
        Assert.Equal(24, VaultFormat.NonceSize);
        Assert.Equal(16, VaultFormat.TagSize);
        Assert.Equal(48, VaultFormat.WrappedKeySize);
    }

    [Fact]
    public void Sizes_MatchNSec()
    {
        Assert.Equal(VaultFormat.KeySize, AeadAlgorithm.XChaCha20Poly1305.KeySize);
        Assert.Equal(VaultFormat.NonceSize, AeadAlgorithm.XChaCha20Poly1305.NonceSize);
        Assert.Equal(VaultFormat.TagSize, AeadAlgorithm.XChaCha20Poly1305.TagSize);

        var argon2id = PasswordBasedKeyDerivationAlgorithm.Argon2id(new Argon2Parameters
        {
            MemorySize = (int)VaultFormat.DefaultKdfMemoryKiB,
            NumberOfPasses = (int)VaultFormat.DefaultKdfIterations,
            DegreeOfParallelism = VaultFormat.DefaultKdfParallelism,
        });
        Assert.Equal(VaultFormat.SaltSize, argon2id.MinSaltSize);
        Assert.Equal(VaultFormat.SaltSize, argon2id.MaxSaltSize);

        Assert.Equal(VaultFormat.WrappedKeySize, VaultFormat.KeySize + VaultFormat.TagSize);
    }

    [Fact]
    public void KdfBounds_ContainDefaults_AndAreAcceptedByNSec()
    {
        Assert.Equal((uint)8, VaultFormat.MinKdfMemoryKiB);
        Assert.Equal((uint)1048576, VaultFormat.MaxKdfMemoryKiB);
        Assert.Equal((uint)1, VaultFormat.MinKdfIterations);
        Assert.Equal((uint)32, VaultFormat.MaxKdfIterations);
        Assert.Equal((byte)1, VaultFormat.DefaultKdfParallelism);
        Assert.Equal((byte)1, VaultFormat.KdfArgon2id);

        Assert.True(VaultFormat.MinKdfMemoryKiB <= VaultFormat.DefaultKdfMemoryKiB);
        Assert.True(VaultFormat.DefaultKdfMemoryKiB <= VaultFormat.MaxKdfMemoryKiB);
        Assert.True(VaultFormat.MinKdfIterations <= VaultFormat.DefaultKdfIterations);
        Assert.True(VaultFormat.DefaultKdfIterations <= VaultFormat.MaxKdfIterations);

        AssertArgon2idAccepts(VaultFormat.MinKdfMemoryKiB, VaultFormat.MinKdfIterations);
        AssertArgon2idAccepts(VaultFormat.DefaultKdfMemoryKiB, VaultFormat.DefaultKdfIterations);
        AssertArgon2idAccepts(VaultFormat.MaxKdfMemoryKiB, VaultFormat.MaxKdfIterations);
    }

    [Fact]
    public void MinFileSize_IsOneSlotPlusBodyNonceAndTag()
    {
        Assert.Equal(148, VaultFormat.MinFileSize);
        Assert.Equal(
            VaultFormat.MinFileSize,
            VaultFormat.FirstSlotOffset + VaultFormat.KdfSlotSize + VaultFormat.NonceSize + VaultFormat.TagSize);
    }

    [Fact]
    public void RecoveryKey_Constants_MatchSpec()
    {
        Assert.Equal(20, VaultFormat.RecoveryKeySize);
        Assert.Equal("0123456789ABCDEFGHJKMNPQRSTVWXYZ", VaultFormat.RecoveryKeyAlphabet);
        Assert.Equal(4, VaultFormat.RecoveryKeyGroupLength);
        Assert.Equal(8, VaultFormat.RecoveryKeyGroupCount);
        Assert.Equal('-', VaultFormat.RecoveryKeySeparator);
    }

    [Fact]
    public void RecoveryKey_EncodesExactlyIntoGroups()
    {
        var bits = VaultFormat.RecoveryKeySize * 8;

        Assert.Equal(160, bits);
        Assert.Equal(VaultFormat.RecoveryKeyGroupLength * VaultFormat.RecoveryKeyGroupCount * 5, bits);
        Assert.True(bits >= 128);
    }

    [Fact]
    public void RecoveryKeyAlphabet_HasThirtyTwoDistinctSymbols_WithoutAmbiguousLetters()
    {
        var alphabet = VaultFormat.RecoveryKeyAlphabet;

        Assert.Equal(32, alphabet.Length);
        Assert.Equal(32, new HashSet<char>(alphabet).Count);
        Assert.All(alphabet, c => Assert.True(char.IsAsciiDigit(c) || char.IsAsciiLetterUpper(c)));
        Assert.DoesNotContain('I', alphabet);
        Assert.DoesNotContain('L', alphabet);
        Assert.DoesNotContain('O', alphabet);
        Assert.DoesNotContain('U', alphabet);
    }

    [Fact]
    public void Version_Kdf_Extension_MatchSpec()
    {
        Assert.Equal((ushort)1, VaultFormat.Version);
        Assert.Equal((byte)1, VaultFormat.KdfArgon2id);
        Assert.Equal(".pmvault", VaultFormat.FileExtension);
    }

    private static int AssertNextField(int offset, int expectedOffset, int size)
    {
        Assert.Equal(offset + size, expectedOffset);
        return expectedOffset;
    }

    private static void AssertArgon2idAccepts(uint memoryKiB, uint iterations)
    {
        var algorithm = PasswordBasedKeyDerivationAlgorithm.Argon2id(new Argon2Parameters
        {
            MemorySize = (int)memoryKiB,
            NumberOfPasses = (int)iterations,
            DegreeOfParallelism = VaultFormat.DefaultKdfParallelism,
        });

        Assert.NotNull(algorithm);
    }
}
