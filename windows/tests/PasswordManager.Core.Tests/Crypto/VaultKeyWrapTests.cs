using PasswordManager.Core.Crypto;
using PasswordManager.Core.Vault;

namespace PasswordManager.Core.Tests.Crypto;

public class VaultKeyWrapTests
{
    // KEKs from Argon2idKdfTests (OpenSSL Argon2id): K2 = password "correct horse battery staple", K5 = secret 0x01..0x14,
    // both with salt 0x00..0x0F at 1024 KiB / 2 iterations.
    private static readonly byte[] K2 = Convert.FromHexString("58782fc96f06a6d7fcec4728099f6a788ee98a04f4cd8fa0a27af6db8b7a46b8");
    private static readonly byte[] K5 = Convert.FromHexString("f5da83698016cd37261aea6ea81a90af036c5e135cf9185f4d06775a50b2e3a5");

    // Fictitious Vault Key 0x20..0x3F and wrap nonce 0x40..0x57.
    private static readonly byte[] Vk = [.. Enumerable.Range(0x20, 32).Select(i => (byte)i)];
    private static readonly byte[] N = [.. Enumerable.Range(0x40, 24).Select(i => (byte)i)];

    // AAD_wrap = file[0..6) || slot[0..29): type 1 (password) and type 2 (recovery kit).
    private static readonly byte[] Aad1 = Convert.FromHexString("504d5646010001620001000400000200000001000102030405060708090a0b0c0d0e0f");
    private static readonly byte[] Aad2 = Convert.FromHexString("504d5646010002620001000400000200000001000102030405060708090a0b0c0d0e0f");

    // Computed with HChaCha20 in pure Python plus OpenSSL ChaCha20-Poly1305 (independent of libsodium).
    private static readonly byte[] W1 = Convert.FromHexString("2e8a17f179bdf5a651b7b489c0ce899222020515738dd5df49db643b04d135cc002785a15a71b4953a359d825d34a848");
    private static readonly byte[] W2 = Convert.FromHexString("fa2d2e816d758690c33354f04cee974acc4fd17d6fcd7f7e914492971f8c52256ac12581922863d5d3e2b9e93c85a1f6");

    private static byte[] Filled(int length) => [.. Enumerable.Repeat((byte)0xAA, length)];

    private static byte[] FlipLowBit(byte[] source, int index)
    {
        var copy = (byte[])source.Clone();
        copy[index] ^= 0x01;
        return copy;
    }

    public static TheoryData<int> AadIndexes() => [.. Enumerable.Range(0, 35)];

    public static TheoryData<int> NonceIndexes() => [.. Enumerable.Range(0, 24)];

    public static TheoryData<int> WrappedIndexes() => [.. Enumerable.Range(0, 48)];

    private static void AssertAuthenticationFailure(Action unwrap, byte[] vaultKey)
    {
        var exception = Assert.Throws<VaultAuthenticationException>(unwrap);

        Assert.Equal("Vault authentication failed.", exception.Message);
        Assert.Null(exception.InnerException);
        Assert.All(vaultKey, b => Assert.Equal(0, b));
    }

    [Fact]
    public void Wrap_PasswordSlotInputs_MatchesKnownAnswerW1()
    {
        var wrapped = new byte[48];

        VaultKeyWrap.Wrap(K2, Vk, Aad1, N, wrapped);

        Assert.Equal(W1, wrapped);
    }

    [Fact]
    public void Wrap_RecoveryKitSlotInputs_MatchesKnownAnswerW2()
    {
        var wrapped = new byte[48];

        VaultKeyWrap.Wrap(K5, Vk, Aad2, N, wrapped);

        Assert.Equal(W2, wrapped);
    }

    [Fact]
    public void Wrap_Inputs_AreNotModified()
    {
        var kek = (byte[])K2.Clone();
        var vaultKey = (byte[])Vk.Clone();
        var aad = (byte[])Aad1.Clone();
        var nonce = (byte[])N.Clone();

        VaultKeyWrap.Wrap(kek, vaultKey, aad, nonce, new byte[48]);

        Assert.Equal(K2, kek);
        Assert.Equal(Vk, vaultKey);
        Assert.Equal(Aad1, aad);
        Assert.Equal(N, nonce);
    }

    [Fact]
    public void Unwrap_KnownAnswerW1_ReturnsVaultKey()
    {
        var vaultKey = Filled(32);

        VaultKeyWrap.Unwrap(K2, W1, Aad1, N, vaultKey);

        Assert.Equal(Vk, vaultKey);
    }

    [Fact]
    public void Unwrap_KnownAnswerW2_ReturnsVaultKey()
    {
        var vaultKey = Filled(32);

        VaultKeyWrap.Unwrap(K5, W2, Aad2, N, vaultKey);

        Assert.Equal(Vk, vaultKey);
    }

    [Fact]
    public void Unwrap_WrongKek_ThrowsVaultAuthenticationExceptionWithoutReason()
    {
        var vaultKey = Filled(32);

        AssertAuthenticationFailure(() => VaultKeyWrap.Unwrap(K5, W1, Aad1, N, vaultKey), vaultKey);
    }

    [Theory]
    [MemberData(nameof(AadIndexes))]
    public void Unwrap_FlippedAadByte_ThrowsVaultAuthenticationException(int index)
    {
        var vaultKey = Filled(32);
        var aad = FlipLowBit(Aad1, index);

        AssertAuthenticationFailure(() => VaultKeyWrap.Unwrap(K2, W1, aad, N, vaultKey), vaultKey);
    }

    [Theory]
    [MemberData(nameof(NonceIndexes))]
    public void Unwrap_FlippedNonceByte_ThrowsVaultAuthenticationException(int index)
    {
        var vaultKey = Filled(32);
        var nonce = FlipLowBit(N, index);

        AssertAuthenticationFailure(() => VaultKeyWrap.Unwrap(K2, W1, Aad1, nonce, vaultKey), vaultKey);
    }

    [Theory]
    [MemberData(nameof(WrappedIndexes))]
    public void Unwrap_FlippedWrappedByte_ThrowsVaultAuthenticationException(int index)
    {
        var vaultKey = Filled(32);
        var wrapped = FlipLowBit(W1, index);

        AssertAuthenticationFailure(() => VaultKeyWrap.Unwrap(K2, wrapped, Aad1, N, vaultKey), vaultKey);
    }

    [Fact]
    public void Unwrap_WrongSlotTypeInAad_ThrowsVaultAuthenticationException()
    {
        var vaultKey = Filled(32);

        AssertAuthenticationFailure(() => VaultKeyWrap.Unwrap(K2, W1, Aad2, N, vaultKey), vaultKey);
    }

    [Theory]
    [InlineData(31)]
    [InlineData(33)]
    public void WrapAndUnwrap_WrongKekLength_ThrowsArgumentException(int length)
    {
        var kek = new byte[length];

        Assert.Throws<ArgumentException>(() => VaultKeyWrap.Wrap(kek, Vk, Aad1, N, new byte[48]));
        Assert.Throws<ArgumentException>(() => VaultKeyWrap.Unwrap(kek, W1, Aad1, N, new byte[32]));
    }

    [Theory]
    [InlineData(31)]
    [InlineData(33)]
    public void WrapAndUnwrap_WrongVaultKeyLength_ThrowsArgumentException(int length)
    {
        var vaultKey = new byte[length];

        Assert.Throws<ArgumentException>(() => VaultKeyWrap.Wrap(K2, vaultKey, Aad1, N, new byte[48]));
        Assert.Throws<ArgumentException>(() => VaultKeyWrap.Unwrap(K2, W1, Aad1, N, vaultKey));
    }

    [Theory]
    [InlineData(34)]
    [InlineData(36)]
    public void WrapAndUnwrap_WrongAadLength_ThrowsArgumentException(int length)
    {
        var aad = new byte[length];

        Assert.Throws<ArgumentException>(() => VaultKeyWrap.Wrap(K2, Vk, aad, N, new byte[48]));
        Assert.Throws<ArgumentException>(() => VaultKeyWrap.Unwrap(K2, W1, aad, N, new byte[32]));
    }

    [Theory]
    [InlineData(23)]
    [InlineData(25)]
    public void WrapAndUnwrap_WrongNonceLength_ThrowsArgumentException(int length)
    {
        var nonce = new byte[length];

        Assert.Throws<ArgumentException>(() => VaultKeyWrap.Wrap(K2, Vk, Aad1, nonce, new byte[48]));
        Assert.Throws<ArgumentException>(() => VaultKeyWrap.Unwrap(K2, W1, Aad1, nonce, new byte[32]));
    }

    [Theory]
    [InlineData(47)]
    [InlineData(49)]
    public void WrapAndUnwrap_WrongWrappedLength_ThrowsArgumentException(int length)
    {
        var wrapped = new byte[length];

        Assert.Throws<ArgumentException>(() => VaultKeyWrap.Wrap(K2, Vk, Aad1, N, wrapped));
        Assert.Throws<ArgumentException>(() => VaultKeyWrap.Unwrap(K2, wrapped, Aad1, N, new byte[32]));
    }
}
