using PasswordManager.Core.Crypto;
using PasswordManager.Core.Vault;

namespace PasswordManager.Core.Tests.Crypto;

public class VaultBodyCipherTests
{
    // Fictitious Vault Key 0x20..0x3F and body nonce 0x60..0x77.
    private static readonly byte[] Vk = [.. Enumerable.Range(0x20, 32).Select(i => (byte)i)];
    private static readonly byte[] Bn = [.. Enumerable.Range(0x60, 24).Select(i => (byte)i)];

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

    // AAD_body = file[0 .. B+24).
    private static readonly byte[] AadF1 = [.. F1, .. Bn];
    private static readonly byte[] AadF2 = [.. F2, .. Bn];

    // P0 = {"entries":[]}; P1 = one entry with a raw-UTF-8 title, a JSON-escaped notes field and an unknown field.
    private static readonly byte[] P0 = Convert.FromHexString("7b22656e7472696573223a5b5d7d");
    private static readonly byte[] P1 = Convert.FromHexString(
        "7b22656e7472696573223a5b7b226964223a2230623666376331652d326635642d346136622d396138652d336331643265346635613662222c227469746c6522" +
        "3a22436166c3a920f09f9491222c22757365726e616d65223a2275736572406578616d706c652e636f6d222c2270617373776f7264223a226e6f742d612d7265" +
        "616c2d70617373776f7264222c2275726c223a2268747470733a2f2f6578616d706c652e636f6d222c226e6f746573223a224361665c7530304539205c754438" +
        "33445c7544443131222c22666f6c646572223a22506572736f6e616c222c226661766f72697465223a747275652c22637265617465644174223a22323032362d" +
        "30312d30315431323a30303a30302e3030305a222c22757064617465644174223a22323032362d30312d30325431323a30303a30302e3030305a227d5d2c2266" +
        "75747572654669656c64223a317d");

    // Computed with HChaCha20 in pure Python plus OpenSSL ChaCha20-Poly1305 (independent of libsodium).
    private static readonly byte[] C0 = Convert.FromHexString("93052548f103f2a4d923af544fdcf72089107a0fb053d81aed759ce00dcb");
    private static readonly byte[] C2 = Convert.FromHexString("93052548f103f2a4d923af544fdc6c3aa10b7fa64c287cbe96a51fa2167a");
    private static readonly byte[] C1 = Convert.FromHexString(
        "93052548f103f2a4d923af54698346fd55d68e250bd3bf5103135cd708f3b7e30ea51c27439d1d437edd0a5da4856260b3da6f1a3ce40fd132629ccc1fb2e5b2" +
        "31807aee6ac56b3adcc39ff20a65209b0615e4c192e03ac794b02ac9a24f875a92ec93cd9fbafcff9d107775a0637426d8b66d091649974137f55c6a5bb3fa7d" +
        "ec9854d75245f5530942f2a7348cffdd4ff3d15a5815c0cff2b24ad6e300a605d0e3bdadf93b65aa3044415a20dd8d2c1f9b148b07cfc1b206ede799c9954963" +
        "b98464d8b2b432dfebea1ccefdeaa07185a7cd90be735d729b07f283ce8d63fc2b4267572b47cbdc2e21d0bdc4bedfeadb9a3241186593b7ac1a60077157463a" +
        "ed365cb120e5a338425b9d98a8c0efe7cee22cfd114bfdddd1b6ddbd424191ea3d014b47e7e6fc4497a96920c43070b6c6444db0f0539d3ee775fa10272e3507" +
        "318b94f2bc7b40855ff53dac9d44ef5c558ef3d49473f8c5c4a6ea6bb7ea");

    private static byte[] Filled(int length) => [.. Enumerable.Repeat((byte)0xAA, length)];

    private static byte[] FlipLowBit(byte[] source, int index)
    {
        var copy = (byte[])source.Clone();
        copy[index] ^= 0x01;
        return copy;
    }

    public static TheoryData<int> AadIndexes() => [.. Enumerable.Range(0, 132)];

    public static TheoryData<int> NonceIndexes() => [.. Enumerable.Range(0, 24)];

    public static TheoryData<int> CiphertextIndexes() => [.. Enumerable.Range(0, 30)];

    private static void AssertAuthenticationFailure(Action decrypt, byte[] plaintext)
    {
        var exception = Assert.Throws<VaultAuthenticationException>(decrypt);

        Assert.Equal("Vault authentication failed.", exception.Message);
        Assert.Null(exception.InnerException);
        Assert.All(plaintext, b => Assert.Equal(0, b));
    }

    [Fact]
    public void KnownInputs_TestFilesHaveExpectedSizes()
    {
        Assert.Equal(108, F1.Length);
        Assert.Equal(209, F2.Length);
        Assert.Equal(132, AadF1.Length);
        Assert.Equal(233, AadF2.Length);
        Assert.Equal(334, P1.Length);
        Assert.Equal(350, C1.Length);
    }

    // --- Known answers ---

    [Fact]
    public void Encrypt_EmptyEntriesWithF1Aad_MatchesKnownAnswerC0()
    {
        var ciphertext = new byte[30];

        VaultBodyCipher.Encrypt(Vk, P0, AadF1, Bn, ciphertext);

        Assert.Equal(C0, ciphertext);
    }

    [Fact]
    public void Encrypt_EmptyEntriesWithF2Aad_MatchesKnownAnswerC2()
    {
        var ciphertext = new byte[30];

        VaultBodyCipher.Encrypt(Vk, P0, AadF2, Bn, ciphertext);

        Assert.Equal(C2, ciphertext);
    }

    [Fact]
    public void Encrypt_OneEntryWithF1Aad_MatchesKnownAnswerC1()
    {
        var ciphertext = new byte[350];

        VaultBodyCipher.Encrypt(Vk, P1, AadF1, Bn, ciphertext);

        Assert.Equal(C1, ciphertext);
    }

    [Fact]
    public void Decrypt_KnownAnswerC0_ReturnsP0()
    {
        var plaintext = Filled(14);

        VaultBodyCipher.Decrypt(Vk, C0, AadF1, Bn, plaintext);

        Assert.Equal(P0, plaintext);
    }

    [Fact]
    public void Decrypt_KnownAnswerC2_ReturnsP0()
    {
        var plaintext = Filled(14);

        VaultBodyCipher.Decrypt(Vk, C2, AadF2, Bn, plaintext);

        Assert.Equal(P0, plaintext);
    }

    [Fact]
    public void Decrypt_KnownAnswerC1_ReturnsP1()
    {
        var plaintext = Filled(334);

        VaultBodyCipher.Decrypt(Vk, C1, AadF1, Bn, plaintext);

        Assert.Equal(P1, plaintext);
    }

    [Fact]
    public void Encrypt_Inputs_AreNotModified()
    {
        var vaultKey = (byte[])Vk.Clone();
        var plaintext = (byte[])P0.Clone();
        var aad = (byte[])AadF1.Clone();
        var nonce = (byte[])Bn.Clone();

        VaultBodyCipher.Encrypt(vaultKey, plaintext, aad, nonce, new byte[30]);

        Assert.Equal(Vk, vaultKey);
        Assert.Equal(P0, plaintext);
        Assert.Equal(AadF1, aad);
        Assert.Equal(Bn, nonce);
    }

    [Fact]
    public void Decrypt_Inputs_AreNotModified()
    {
        var vaultKey = (byte[])Vk.Clone();
        var ciphertext = (byte[])C0.Clone();
        var aad = (byte[])AadF1.Clone();
        var nonce = (byte[])Bn.Clone();

        VaultBodyCipher.Decrypt(vaultKey, ciphertext, aad, nonce, new byte[14]);

        Assert.Equal(Vk, vaultKey);
        Assert.Equal(C0, ciphertext);
        Assert.Equal(AadF1, aad);
        Assert.Equal(Bn, nonce);
    }

    // --- Authentication failures ---

    [Fact]
    public void Decrypt_WrongVaultKey_ThrowsVaultAuthenticationExceptionWithoutReason()
    {
        var plaintext = Filled(14);
        var wrongKey = FlipLowBit(Vk, 0);

        AssertAuthenticationFailure(() => VaultBodyCipher.Decrypt(wrongKey, C0, AadF1, Bn, plaintext), plaintext);
    }

    [Fact]
    public void Decrypt_F2AadForC0_ThrowsVaultAuthenticationException()
    {
        var plaintext = Filled(14);

        AssertAuthenticationFailure(() => VaultBodyCipher.Decrypt(Vk, C0, AadF2, Bn, plaintext), plaintext);
    }

    [Theory]
    [MemberData(nameof(AadIndexes))]
    public void Decrypt_FlippedAadByte_ThrowsVaultAuthenticationException(int index)
    {
        var plaintext = Filled(14);
        var aad = FlipLowBit(AadF1, index);

        AssertAuthenticationFailure(() => VaultBodyCipher.Decrypt(Vk, C0, aad, Bn, plaintext), plaintext);
    }

    [Theory]
    [MemberData(nameof(NonceIndexes))]
    public void Decrypt_FlippedNonceByte_ThrowsVaultAuthenticationException(int index)
    {
        var plaintext = Filled(14);
        var nonce = FlipLowBit(Bn, index);

        AssertAuthenticationFailure(() => VaultBodyCipher.Decrypt(Vk, C0, AadF1, nonce, plaintext), plaintext);
    }

    [Theory]
    [MemberData(nameof(CiphertextIndexes))]
    public void Decrypt_FlippedCiphertextByte_ThrowsVaultAuthenticationException(int index)
    {
        var plaintext = Filled(14);
        var ciphertext = FlipLowBit(C0, index);

        AssertAuthenticationFailure(() => VaultBodyCipher.Decrypt(Vk, ciphertext, AadF1, Bn, plaintext), plaintext);
    }

    // --- Lengths ---

    [Theory]
    [InlineData(31)]
    [InlineData(33)]
    public void EncryptAndDecrypt_WrongVaultKeyLength_ThrowsArgumentException(int length)
    {
        var vaultKey = new byte[length];

        Assert.Throws<ArgumentException>(() => VaultBodyCipher.Encrypt(vaultKey, P0, AadF1, Bn, new byte[30]));
        Assert.Throws<ArgumentException>(() => VaultBodyCipher.Decrypt(vaultKey, C0, AadF1, Bn, new byte[14]));
    }

    [Theory]
    [InlineData(23)]
    [InlineData(25)]
    public void EncryptAndDecrypt_WrongNonceLength_ThrowsArgumentException(int length)
    {
        var nonce = new byte[length];

        Assert.Throws<ArgumentException>(() => VaultBodyCipher.Encrypt(Vk, P0, AadF1, nonce, new byte[30]));
        Assert.Throws<ArgumentException>(() => VaultBodyCipher.Decrypt(Vk, C0, AadF1, nonce, new byte[14]));
    }

    [Theory]
    [InlineData(29)]
    [InlineData(31)]
    public void Encrypt_WrongCiphertextBufferLength_ThrowsArgumentException(int length)
    {
        Assert.Throws<ArgumentException>(() => VaultBodyCipher.Encrypt(Vk, P0, AadF1, Bn, new byte[length]));
    }

    [Fact]
    public void Decrypt_CiphertextShorterThanTag_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => VaultBodyCipher.Decrypt(Vk, new byte[15], AadF1, Bn, []));
    }

    [Theory]
    [InlineData(13)]
    [InlineData(15)]
    public void Decrypt_WrongPlaintextBufferLength_ThrowsArgumentException(int length)
    {
        Assert.Throws<ArgumentException>(() => VaultBodyCipher.Decrypt(Vk, C0, AadF1, Bn, new byte[length]));
    }
}
