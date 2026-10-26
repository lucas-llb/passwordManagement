using System.Text;
using System.Text.Json;
using PasswordManager.Core.Crypto;
using PasswordManager.Core.Vault;

namespace PasswordManager.Core.Tests.Vault;

public class VaultBodyTests
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

    // V0 = F1 || BN || C0 (162 bytes); V1 = F1 || BN || C1 (482 bytes); V2 = F2 || BN || C2 (263 bytes).
    private static readonly byte[] V0 = [.. F1, .. Bn, .. C0];
    private static readonly byte[] V1 = [.. F1, .. Bn, .. C1];
    private static readonly byte[] V2 = [.. F2, .. Bn, .. C2];

    private const int B1 = 108;
    private const int B2 = 209;

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

    private const string J6 =
        """{"entries":[{"id":"0b6f7c1e-2f5d-4a6b-9a8e-3c1d2e4f5a6b","createdAt":"2026-01-01T12:00:00.000Z","updatedAt":"2026-01-01T12:00:00.000Z"}]}""";

    private const string IdField = "\"id\":\"0b6f7c1e-2f5d-4a6b-9a8e-3c1d2e4f5a6b\",";

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    private static byte[] InsertAfterId(string json, byte[] insertion)
    {
        var bytes = Utf8(json);
        var at = json.IndexOf(IdField, StringComparison.Ordinal) + IdField.Length;
        return [.. bytes.AsSpan(0, at), .. insertion, .. bytes.AsSpan(at)];
    }

    // Seals any plaintext behind a valid tag (F1 || BN, VK) by calling the cipher directly.
    private static byte[] SealRaw(byte[] plaintext)
    {
        byte[] aad = [.. F1, .. Bn];
        var ciphertext = new byte[plaintext.Length + VaultFormat.TagSize];
        VaultBodyCipher.Encrypt(Vk, plaintext, aad, Bn, ciphertext);
        return [.. aad, .. ciphertext];
    }

    private static byte[] FlipLowBit(byte[] source, int index)
    {
        var copy = (byte[])source.Clone();
        copy[index] ^= 0x01;
        return copy;
    }

    private static void AssertAuthenticationFailure(Action open)
    {
        var exception = Assert.Throws<VaultAuthenticationException>(open);

        Assert.Equal("Vault authentication failed.", exception.Message);
        Assert.Null(exception.InnerException);
    }

    private static void AssertInvalidJson(byte[] file)
    {
        var exception = Assert.Throws<VaultFormatException>(() => VaultBody.Open(file, B1, Vk));

        Assert.Equal("Vault body is not valid JSON.", exception.Message);
        Assert.Null(exception.InnerException);
    }

    // Regions of V0 (B = 108) and V2 (B = 209), as [start, end).
    public static TheoryData<string, int, int> V0Regions() => new()
    {
        { "magic", 0, 4 },
        { "version", 4, 6 },
        { "slotCount", 6, 7 },
        { "slot1", 7, 108 },
        { "bodyNonce", 108, 132 },
        { "ciphertext", 132, 146 },
        { "tag", 146, 162 },
    };

    public static TheoryData<string, int, int> V2Regions() => new()
    {
        { "slot1", 7, 108 },
        { "slot2", 108, 209 },
        { "bodyNonce", 209, 233 },
        { "body", 233, 263 },
    };

    public static TheoryData<string, byte[]> MalformedJson() => new()
    {
        { "J1 not json", Utf8("not json") },
        { "J2 empty", [] },
        { "J3 null", Utf8("null") },
        { "J4 null entries", Utf8("""{"entries":null}""") },
        { "J5 null entry", Utf8("""{"entries":[null]}""") },
        { "J6 missing title", Utf8(J6) },
        { "J7 null title", InsertAfterId(J6, Utf8("\"title\":null,")) },
        { "J8 trailing data", Utf8("""{"entries":[]}x""") },
        { "J9 invalid UTF-8", InsertAfterId(J6, [.. Utf8("\"title\":\""), 0xFF, .. Utf8("\",")]) },
        { "J10 bad date", InsertAfterId(J6.Replace("\"createdAt\":\"2026-01-01T12:00:00.000Z\"", "\"createdAt\":\"not-a-date\"", StringComparison.Ordinal), Utf8("\"title\":\"t\",")) },
    };

    // --- Known answers ---

    [Fact]
    public void KnownFiles_HaveExpectedSizes()
    {
        Assert.Equal(162, V0.Length);
        Assert.Equal(482, V1.Length);
        Assert.Equal(263, V2.Length);
    }

    [Fact]
    public void Open_KnownAnswerV0_ReturnsNoEntries()
    {
        var content = VaultBody.Open(V0, B1, Vk);

        Assert.Empty(content.Entries);
    }

    [Fact]
    public void Open_KnownAnswerV2_ReturnsNoEntries()
    {
        var content = VaultBody.Open(V2, B2, Vk);

        Assert.Empty(content.Entries);
    }

    [Fact]
    public void Open_KnownAnswerV1_ReturnsEntryAndIgnoresUnknownField()
    {
        var content = VaultBody.Open(V1, B1, Vk);

        var entry = Assert.Single(content.Entries);
        Assert.Equal(Guid.Parse("0b6f7c1e-2f5d-4a6b-9a8e-3c1d2e4f5a6b"), entry.Id);
        Assert.Equal("Café \U0001F511", entry.Title);
        Assert.Equal("Café \U0001F511", entry.Notes);
        Assert.Equal("user@example.com", entry.Username);
        Assert.Equal("not-a-real-password", entry.Password);
        Assert.Equal("https://example.com", entry.Url);
        Assert.Equal("Personal", entry.Folder);
        Assert.True(entry.Favorite);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero), entry.CreatedAt);
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 12, 0, 0, TimeSpan.Zero), entry.UpdatedAt);
    }

    // --- Round trip and fresh nonce ---

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Seal_SampleContent_ProducesExpectedLayoutAndRoundTrips(int slotCount)
    {
        var header = slotCount == 1 ? F1 : F2;
        var bodyOffset = header.Length;
        var content = new VaultContent { Entries = [SampleEntry] };
        var json = JsonSerializer.SerializeToUtf8Bytes(content, VaultJsonContext.Default.VaultContent);

        var file = VaultBody.Seal(header, Vk, content);

        Assert.Equal(header, file[..bodyOffset]);
        Assert.Equal(bodyOffset + 24 + json.Length + 16, file.Length);

        var plaintext = new byte[json.Length];
        VaultBodyCipher.Decrypt(
            Vk,
            file.AsSpan(bodyOffset + 24),
            file.AsSpan(0, bodyOffset + 24),
            file.AsSpan(bodyOffset, 24),
            plaintext);
        Assert.Equal(json, plaintext);

        Assert.Equal(content.Entries, VaultBody.Open(file, bodyOffset, Vk).Entries);
    }

    [Fact]
    public void Seal_TwiceWithSameInputs_UsesFreshNonceAndBothOpen()
    {
        var content = new VaultContent { Entries = [SampleEntry] };

        var first = VaultBody.Seal(F1, Vk, content);
        var second = VaultBody.Seal(F1, Vk, content);

        Assert.NotEqual(first[B1..(B1 + 24)], second[B1..(B1 + 24)]);
        Assert.NotEqual(first[(B1 + 24)..], second[(B1 + 24)..]);
        Assert.Equal(content.Entries, VaultBody.Open(first, B1, Vk).Entries);
        Assert.Equal(content.Entries, VaultBody.Open(second, B1, Vk).Entries);
    }

    [Fact]
    public void Seal_Inputs_AreNotModified()
    {
        var header = (byte[])F1.Clone();
        var vaultKey = (byte[])Vk.Clone();

        VaultBody.Seal(header, vaultKey, new VaultContent { Entries = [SampleEntry] });

        Assert.Equal(F1, header);
        Assert.Equal(Vk, vaultKey);
    }

    [Fact]
    public void Open_Inputs_AreNotModified()
    {
        var file = (byte[])V1.Clone();
        var vaultKey = (byte[])Vk.Clone();

        VaultBody.Open(file, B1, vaultKey);

        Assert.Equal(V1, file);
        Assert.Equal(Vk, vaultKey);
    }

    // --- Tamper ---

    [Theory]
    [MemberData(nameof(V0Regions))]
    public void Open_FlippedByteInV0Region_ThrowsVaultAuthenticationException(string region, int start, int end)
    {
        Assert.NotNull(region);
        for (var index = start; index < end; index++)
        {
            var file = FlipLowBit(V0, index);

            AssertAuthenticationFailure(() => VaultBody.Open(file, B1, Vk));
        }
    }

    [Theory]
    [MemberData(nameof(V2Regions))]
    public void Open_FlippedByteInV2Region_ThrowsVaultAuthenticationException(string region, int start, int end)
    {
        Assert.NotNull(region);
        for (var index = start; index < end; index++)
        {
            var file = FlipLowBit(V2, index);

            AssertAuthenticationFailure(() => VaultBody.Open(file, B2, Vk));
        }
    }

    [Fact]
    public void Open_V0WithoutLastByte_ThrowsVaultAuthenticationException()
    {
        var file = V0[..^1];

        AssertAuthenticationFailure(() => VaultBody.Open(file, B1, Vk));
    }

    [Fact]
    public void Open_V0WithAppendedByte_ThrowsVaultAuthenticationException()
    {
        byte[] file = [.. V0, 0x00];

        AssertAuthenticationFailure(() => VaultBody.Open(file, B1, Vk));
    }

    [Theory]
    [InlineData(107)]
    [InlineData(109)]
    public void Open_WrongBodyOffset_ThrowsVaultAuthenticationException(int bodyOffset)
    {
        AssertAuthenticationFailure(() => VaultBody.Open(V0, bodyOffset, Vk));
    }

    [Fact]
    public void Open_WrongVaultKey_ThrowsVaultAuthenticationException()
    {
        var wrongKey = FlipLowBit(Vk, 0);

        AssertAuthenticationFailure(() => VaultBody.Open(V0, B1, wrongKey));
    }

    // --- Format and arguments ---

    [Fact]
    public void Open_BodyShorterThanNonceAndTag_ThrowsVaultFormatException()
    {
        byte[] file = [.. F1, .. new byte[39]];

        var exception = Assert.Throws<VaultFormatException>(() => VaultBody.Open(file, B1, Vk));

        Assert.Equal("Vault body is truncated.", exception.Message);
    }

    [Fact]
    public void Open_BodyOfExactlyNonceAndTag_ThrowsVaultAuthenticationException()
    {
        byte[] file = [.. F1, .. Bn, .. new byte[16]];

        AssertAuthenticationFailure(() => VaultBody.Open(file, B1, Vk));
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(int.MaxValue)]
    public void Open_BodyOffsetPastEnd_ThrowsVaultFormatException(int bodyOffset)
    {
        var exception = Assert.Throws<VaultFormatException>(() => VaultBody.Open(V0, bodyOffset, Vk));

        Assert.Equal("Vault body is truncated.", exception.Message);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(6)]
    public void Open_BodyOffsetBeforeFirstSlot_ThrowsArgumentOutOfRangeException(int bodyOffset)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => VaultBody.Open(V0, bodyOffset, Vk));
    }

    [Theory]
    [InlineData(31)]
    [InlineData(33)]
    public void SealAndOpen_WrongVaultKeyLength_ThrowsArgumentException(int length)
    {
        var vaultKey = new byte[length];

        Assert.Throws<ArgumentException>(() => VaultBody.Seal(F1, vaultKey, new VaultContent()));
        Assert.Throws<ArgumentException>(() => VaultBody.Open(V0, B1, vaultKey));
    }

    [Fact]
    public void Seal_HeaderShorterThanPreambleAndSlotCount_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => VaultBody.Seal(new byte[6], Vk, new VaultContent()));
    }

    [Fact]
    public void Seal_NullContent_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => VaultBody.Seal(F1, Vk, null!));
    }

    // --- Malformed JSON behind a valid tag ---

    [Theory]
    [MemberData(nameof(MalformedJson))]
    public void Open_MalformedJsonBehindValidTag_ThrowsVaultFormatExceptionWithoutInner(string name, byte[] plaintext)
    {
        Assert.NotNull(name);
        var file = SealRaw(plaintext);

        AssertInvalidJson(file);
    }

    [Fact]
    public void Open_EmptyPlaintextBehindValidTag_IsMinimumFileSize()
    {
        Assert.Equal(VaultFormat.MinFileSize, SealRaw([]).Length);
    }

    // --- Reseal ---

    private static byte[] DecryptBody(byte[] file, int bodyOffset)
    {
        var plaintext = new byte[file.Length - bodyOffset - 24 - 16];
        VaultBodyCipher.Decrypt(
            Vk,
            file.AsSpan(bodyOffset + 24),
            file.AsSpan(0, bodyOffset + 24),
            file.AsSpan(bodyOffset, 24),
            plaintext);
        return plaintext;
    }

    [Fact]
    public void Reseal_V1OntoF2_ProducesNewHeaderFreshNonceAndSameEntries()
    {
        var result = VaultBody.Reseal(V1, B1, F2, Vk);

        Assert.Equal(F2, result[..B2]);
        Assert.Equal(VaultBody.Open(V1, B1, Vk).Entries, VaultBody.Open(result, B2, Vk).Entries);
        Assert.NotEqual(Bn, result[B2..(B2 + 24)]);
        Assert.Equal(B2 + 24 + 350, result.Length);
    }

    [Fact]
    public void Reseal_UnknownJsonField_KeepsPlaintextBytes()
    {
        var plaintext = Utf8("""{"entries":[],"futureField":1}""");
        var file = SealRaw(plaintext);

        var result = VaultBody.Reseal(file, B1, F1, Vk);

        Assert.Equal(F1, result[..B1]);
        Assert.Equal(plaintext, DecryptBody(result, B1));
    }

    [Fact]
    public void Reseal_TwiceWithSameInputs_UsesFreshNonceAndCiphertext()
    {
        var first = VaultBody.Reseal(V1, B1, F1, Vk);
        var second = VaultBody.Reseal(V1, B1, F1, Vk);

        Assert.NotEqual(first[B1..(B1 + 24)], second[B1..(B1 + 24)]);
        Assert.NotEqual(first[(B1 + 24)..], second[(B1 + 24)..]);
        Assert.Equal(DecryptBody(V1, B1), DecryptBody(first, B1));
        Assert.Equal(DecryptBody(V1, B1), DecryptBody(second, B1));
    }

    [Theory]
    [MemberData(nameof(V0Regions))]
    public void Reseal_FlippedByteInV0Region_ThrowsVaultAuthenticationException(string region, int start, int end)
    {
        Assert.NotNull(region);
        for (var index = start; index < end; index++)
        {
            var file = FlipLowBit(V0, index);

            AssertAuthenticationFailure(() => VaultBody.Reseal(file, B1, F1, Vk));
        }
    }

    [Fact]
    public void Reseal_WrongVaultKey_ThrowsVaultAuthenticationException()
    {
        var wrongKey = FlipLowBit(Vk, 0);

        AssertAuthenticationFailure(() => VaultBody.Reseal(V0, B1, F1, wrongKey));
    }

    [Fact]
    public void Reseal_BodyOffsetBeforeFirstSlot_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => VaultBody.Reseal(V0, 6, F1, Vk));
    }

    [Fact]
    public void Reseal_BodyShorterThanNonceAndTag_ThrowsVaultFormatException()
    {
        var file = V0[..147];

        var exception = Assert.Throws<VaultFormatException>(() => VaultBody.Reseal(file, B1, F1, Vk));

        Assert.Equal("Vault body is truncated.", exception.Message);
    }

    [Fact]
    public void Reseal_HeaderShorterThanPreambleAndSlotCount_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => VaultBody.Reseal(V0, B1, new byte[6], Vk));
    }

    [Theory]
    [InlineData(31)]
    [InlineData(33)]
    public void Reseal_WrongVaultKeyLength_ThrowsArgumentException(int length)
    {
        var vaultKey = new byte[length];

        Assert.Throws<ArgumentException>(() => VaultBody.Reseal(V0, B1, F1, vaultKey));
    }

    [Fact]
    public void Reseal_Inputs_AreNotModified()
    {
        var file = (byte[])V1.Clone();
        var header = (byte[])F2.Clone();
        var vaultKey = (byte[])Vk.Clone();

        VaultBody.Reseal(file, B1, header, vaultKey);

        Assert.Equal(V1, file);
        Assert.Equal(F2, header);
        Assert.Equal(Vk, vaultKey);
    }
}
