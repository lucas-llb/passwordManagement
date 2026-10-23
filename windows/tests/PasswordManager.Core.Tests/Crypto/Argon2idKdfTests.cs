using System.Buffers.Binary;
using PasswordManager.Core.Crypto;
using PasswordManager.Core.Vault;

namespace PasswordManager.Core.Tests.Crypto;

public class Argon2idKdfTests
{
    // Computed with OpenSSL Argon2id via Python cryptography 50.0.2 (independent of libsodium); NFC via Python unicodedata.
    private static readonly byte[] K1 = Convert.FromHexString("d17ea6341ca93da6079ea2f64dc4aa31dd1aaf9caa67fb42ac4afd0714706f26");
    private static readonly byte[] K2 = Convert.FromHexString("58782fc96f06a6d7fcec4728099f6a788ee98a04f4cd8fa0a27af6db8b7a46b8");
    private static readonly byte[] K3 = Convert.FromHexString("da1fe23ea05cfd9e9d3d83fe89bfc3418f0e80d8ab8a345b89b43e9d41213433");
    private static readonly byte[] K4 = Convert.FromHexString("b2139fcdb9dac3eaec8ab6e84eed39d012d47455f18da2de398b1a6519105991");
    private static readonly byte[] K5 = Convert.FromHexString("f5da83698016cd37261aea6ea81a90af036c5e135cf9185f4d06775a50b2e3a5");

    private const string AsciiPassword = "correct horse battery staple";
    private const string UnicodePasswordNfd = "Pässwörd-ção-\U0001F511";
    private const string UnicodePasswordNfc = "Pässwörd-ção-\U0001F511";

    private static readonly byte[] S1 = [.. Enumerable.Range(0x00, 16).Select(i => (byte)i)];
    private static readonly byte[] S2 = [.. Enumerable.Range(0xF0, 16).Select(i => (byte)i)];
    private static readonly byte[] Secret = [.. Enumerable.Range(0x01, 20).Select(i => (byte)i)];

    private static readonly KdfParameters LowCost = new(1024, 2);

    [Fact]
    public void DeriveKek_AsciiPasswordMinimumParameters_MatchesKnownAnswer()
    {
        var kek = new byte[32];

        Argon2idKdf.DeriveKek(AsciiPassword.AsSpan(), new KdfParameters(8, 1), S1, kek);

        Assert.Equal(K1, kek);
    }

    [Fact]
    public void DeriveKek_AsciiPasswordLowCost_MatchesKnownAnswer()
    {
        var kek = new byte[32];

        Argon2idKdf.DeriveKek(AsciiPassword.AsSpan(), LowCost, S1, kek);

        Assert.Equal(K2, kek);
    }

    [Fact]
    public void DeriveKek_AsciiPasswordDefaultParameters_MatchesKnownAnswer()
    {
        var kek = new byte[32];

        Argon2idKdf.DeriveKek(AsciiPassword.AsSpan(), KdfParameters.Default, S2, kek);

        Assert.Equal(K3, kek);
    }

    [Theory]
    [InlineData(UnicodePasswordNfd)]
    [InlineData(UnicodePasswordNfc)]
    public void DeriveKek_UnicodePasswordNfdOrNfc_MatchesNfcKnownAnswer(string password)
    {
        var kek = new byte[32];

        Argon2idKdf.DeriveKek(password.AsSpan(), LowCost, S1, kek);

        Assert.Equal(K4, kek);
    }

    [Fact]
    public void DeriveKek_RawSecretBytes_MatchesKnownAnswer()
    {
        var kek = new byte[32];

        Argon2idKdf.DeriveKek(Secret.AsSpan(), LowCost, S1, kek);

        Assert.Equal(K5, kek);
    }

    [Fact]
    public void DeriveKek_ParametersAndSaltReadFromSlot_MatchesKnownAnswer()
    {
        var slot = new byte[101];
        slot[0] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(slot.AsSpan(1), 98);
        slot[3] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(slot.AsSpan(4), 1024);
        BinaryPrimitives.WriteUInt32LittleEndian(slot.AsSpan(8), 2);
        slot[12] = 1;
        S1.CopyTo(slot, 13);
        var kek = new byte[32];

        var parameters = KdfParameters.ReadFromSlot(slot);
        var salt = slot.AsSpan(VaultFormat.SlotSaltOffset, VaultFormat.SaltSize);
        Argon2idKdf.DeriveKek(AsciiPassword.AsSpan(), parameters, salt, kek);

        Assert.Equal(K2, kek);
    }

    [Theory]
    [InlineData(15)]
    [InlineData(17)]
    public void DeriveKek_WrongSaltLength_ThrowsArgumentException(int saltLength)
    {
        var salt = new byte[saltLength];
        var kek = new byte[32];

        Assert.Throws<ArgumentException>(() => Argon2idKdf.DeriveKek(AsciiPassword.AsSpan(), LowCost, salt, kek));
        Assert.Throws<ArgumentException>(() => Argon2idKdf.DeriveKek(Secret.AsSpan(), LowCost, salt, kek));
    }

    [Theory]
    [InlineData(31)]
    [InlineData(33)]
    public void DeriveKek_WrongKekLength_ThrowsArgumentException(int kekLength)
    {
        var kek = new byte[kekLength];

        Assert.Throws<ArgumentException>(() => Argon2idKdf.DeriveKek(AsciiPassword.AsSpan(), LowCost, S1, kek));
        Assert.Throws<ArgumentException>(() => Argon2idKdf.DeriveKek(Secret.AsSpan(), LowCost, S1, kek));
    }

    [Theory]
    [InlineData(0u, 0u)]
    [InlineData(7u, 2u)]
    [InlineData(1024u, 33u)]
    public void DeriveKek_ParametersOutOfRange_ThrowsArgumentOutOfRangeException(uint memoryKiB, uint iterations)
    {
        var parameters = new KdfParameters(memoryKiB, iterations);
        var kek = new byte[32];

        Assert.Throws<ArgumentOutOfRangeException>(() => Argon2idKdf.DeriveKek(AsciiPassword.AsSpan(), parameters, S1, kek));
        Assert.Throws<ArgumentOutOfRangeException>(() => Argon2idKdf.DeriveKek(Secret.AsSpan(), parameters, S1, kek));
    }

    [Fact]
    public void DeriveKek_DefaultParameters_ThrowsArgumentOutOfRangeException()
    {
        var kek = new byte[32];

        Assert.Throws<ArgumentOutOfRangeException>(() => Argon2idKdf.DeriveKek(AsciiPassword.AsSpan(), default, S1, kek));
    }

    [Fact]
    public void DeriveKek_LoneSurrogate_ThrowsArgumentException()
    {
        var kek = new byte[32];

        Assert.Throws<ArgumentException>(() => Argon2idKdf.DeriveKek("a\uD800b".AsSpan(), LowCost, S1, kek));
    }

    [Fact]
    public void DeriveKek_CharArrayPassword_IsNotModified()
    {
        var password = UnicodePasswordNfd.ToCharArray();
        var original = UnicodePasswordNfd.ToCharArray();
        var kek = new byte[32];

        Argon2idKdf.DeriveKek(password.AsSpan(), LowCost, S1, kek);

        Assert.Equal(original, password);
        Assert.Equal(K4, kek);
    }
}
