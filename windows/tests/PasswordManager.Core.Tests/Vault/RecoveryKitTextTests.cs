using System.Globalization;
using System.Security.Cryptography;
using PasswordManager.Core.Vault;

namespace PasswordManager.Core.Tests.Vault;

public class RecoveryKitTextTests
{
    // Key 0x01..0x14 and its text; the same pair is the fixture of MasterPasswordTests and RecoveryKitTests.
    private const string Canonical = "0410-6105-0R3G-G28A-1C60-T3GF-208H-44RM";
    private const string CanonicalHex = "0102030405060708090a0b0c0d0e0f1011121314";

    private static byte[] Decode(string text)
    {
        var key = new byte[VaultFormat.RecoveryKeySize];
        Assert.True(RecoveryKitText.TryDecode(text, key));
        return key;
    }

    private static byte[] DecodeFailing(string text)
    {
        var key = new byte[VaultFormat.RecoveryKeySize];
        key.AsSpan().Fill(0xAA);
        Assert.False(RecoveryKitText.TryDecode(text, key));
        return key;
    }

    private static string Encode(byte[] key)
    {
        var text = new char[RecoveryKitText.Length];
        RecoveryKitText.Encode(key, text);
        return new string(text);
    }

    // Expected values computed independently with Python base64.b32encode plus a translation from the RFC 4648
    // alphabet to the Crockford alphabet. Reuse them on Android.
    public static TheoryData<string, string> KnownAnswers() => new()
    {
        { new string('0', 40), "0000-0000-0000-0000-0000-0000-0000-0000" },
        { new string('F', 40), "ZZZZ-ZZZZ-ZZZZ-ZZZZ-ZZZZ-ZZZZ-ZZZZ-ZZZZ" },
        { CanonicalHex, Canonical },
        { "80" + new string('0', 38), "G000-0000-0000-0000-0000-0000-0000-0000" },
        { new string('0', 38) + "01", "0000-0000-0000-0000-0000-0000-0000-0001" },
        { "00443214c74254b635cf84653a56d7c675be77df", "0123-4567-89AB-CDEF-GHJK-MNPQ-RSTV-WXYZ" },
    };

    public static TheoryData<string> NormalizedForms() => new()
    {
        "0410-6105-0r3g-g28a-1c60-t3gf-208h-44rm",
        "041061050R3GG28A1C60T3GF208H44RM",
        "0410 6105 0R3G G28A 1C60 T3GF 208H 44RM",
        " 0410  6105--0r3g -G28A 1C60 - T3GF-208H-44RM- ",
        "O41O-61O5-OR3G-G28A-1C6O-T3GF-2O8H-44RM",
        "o41o-61o5-0R3G-G28A-1C6o-T3GF-2o8H-44RM",
        "04I0-6I05-0R3G-G28A-IC60-T3GF-208H-44RM",
        "04i0-6i05-0R3G-G28A-iC60-T3GF-208H-44RM",
        "04L0-6L05-0R3G-G28A-LC60-T3GF-208H-44RM",
        "04l0-6l05-0R3G-G28A-lC60-T3GF-208H-44RM",
    };

    public static TheoryData<char> InvalidChars() => new()
    {
        'U', 'u', '*', '_', '.', '=', '\t', '\n', '\0', 'İ', 'ı', '０',
    };

    // --- Known answers and shape ---

    [Theory]
    [MemberData(nameof(KnownAnswers))]
    public void Encode_KnownKey_GivesExpectedText(string keyHex, string expectedText)
    {
        Assert.Equal(expectedText, Encode(Convert.FromHexString(keyHex)));
    }

    [Theory]
    [MemberData(nameof(KnownAnswers))]
    public void TryDecode_KnownText_GivesExpectedKey(string expectedKeyHex, string text)
    {
        Assert.Equal(Convert.FromHexString(expectedKeyHex), Decode(text));
    }

    [Fact]
    public void Length_IsThirtyNine()
    {
        Assert.Equal(39, RecoveryKitText.Length);
    }

    [Fact]
    public void Encode_RandomKeys_HaveSeparatorsAtFixedPositionsAndNoAmbiguousLetters()
    {
        for (var n = 0; n < 256; n++)
        {
            var text = Encode(RandomNumberGenerator.GetBytes(VaultFormat.RecoveryKeySize));

            Assert.Equal(39, text.Length);
            for (var i = 0; i < text.Length; i++)
            {
                if (i % 5 == 4)
                {
                    Assert.Equal('-', text[i]);
                }
                else
                {
                    Assert.Contains(text[i], VaultFormat.RecoveryKeyAlphabet);
                }
            }

            Assert.DoesNotContain('I', text);
            Assert.DoesNotContain('L', text);
            Assert.DoesNotContain('O', text);
            Assert.DoesNotContain('U', text);
        }
    }

    [Fact]
    public void EncodeThenTryDecode_RandomKeys_RoundTrip()
    {
        for (var n = 0; n < 256; n++)
        {
            var key = RandomNumberGenerator.GetBytes(VaultFormat.RecoveryKeySize);

            Assert.Equal(key, Decode(Encode(key)));
        }
    }

    // --- Normalization ---

    [Theory]
    [MemberData(nameof(NormalizedForms))]
    public void TryDecode_NormalizedForm_GivesSameKeyAsCanonicalText(string text)
    {
        Assert.Equal(Convert.FromHexString(CanonicalHex), Decode(text));
    }

    [Fact]
    public void TryDecode_LowerCaseIWithTurkishCulture_StillReadsAsOne()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");

            Assert.Equal(Convert.FromHexString(CanonicalHex), Decode("04i0-6i05-0r3g-g28a-ic60-t3gf-208h-44rm"));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    // --- Invalid input ---

    [Theory]
    [MemberData(nameof(InvalidChars))]
    public void TryDecode_SymbolReplacedByInvalidChar_ReturnsFalseAndClearsKey(char invalid)
    {
        var text = Canonical.Remove(6, 1).Insert(6, invalid.ToString());

        Assert.All(DecodeFailing(text), b => Assert.Equal(0, b));
    }

    // Inserting (not replacing) keeps 32 valid symbols, so only the char itself can make the text invalid.
    [Theory]
    [MemberData(nameof(InvalidChars))]
    public void TryDecode_InvalidCharInsertedIntoValidText_ReturnsFalseAndClearsKey(char invalid)
    {
        var text = Canonical.Insert(6, invalid.ToString());

        Assert.All(DecodeFailing(text), b => Assert.Equal(0, b));
    }

    [Theory]
    [InlineData("0410-6105-0R3G-G28A-1C60-T3GF-208H-44R")]
    [InlineData("0410-6105-0R3G-G28A-1C60-T3GF-208H-44RM0")]
    [InlineData("0410-6105-0R3G-G28A-1C60-T3GF-208H-44RM-0")]
    [InlineData("")]
    [InlineData("--------")]
    [InlineData("        ")]
    public void TryDecode_WrongSymbolCount_ReturnsFalseAndClearsKey(string text)
    {
        Assert.All(DecodeFailing(text), b => Assert.Equal(0, b));
    }

    // --- Lengths ---

    [Theory]
    [InlineData(19)]
    [InlineData(21)]
    public void Encode_WrongKeyLength_ThrowsArgumentException(int length)
    {
        Assert.Throws<ArgumentException>(() => RecoveryKitText.Encode(new byte[length], new char[RecoveryKitText.Length]));
    }

    [Theory]
    [InlineData(38)]
    [InlineData(40)]
    public void Encode_WrongTextLength_ThrowsArgumentException(int length)
    {
        Assert.Throws<ArgumentException>(
            () => RecoveryKitText.Encode(new byte[VaultFormat.RecoveryKeySize], new char[length]));
    }

    [Theory]
    [InlineData(19)]
    [InlineData(21)]
    public void TryDecode_WrongDestinationLength_ThrowsArgumentException(int length)
    {
        Assert.Throws<ArgumentException>(() => RecoveryKitText.TryDecode(Canonical, new byte[length]));
    }
}
