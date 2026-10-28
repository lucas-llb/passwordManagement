using System.Runtime.InteropServices;
using System.Security.Cryptography;
using PasswordManager.Core.Passwords;

namespace PasswordManager.Core.Tests.Passwords;

public class PasswordStrengthTests
{
    // Every password here is fictitious.
    private const string Emoji = "\U0001F600";

    [Fact]
    public void Evaluate_Empty_IsNone()
    {
        Assert.Equal(PasswordStrengthLevel.None, PasswordStrength.Evaluate([]));
        Assert.Equal(0, PasswordStrength.EstimateBits([]));
    }

    [Theory]
    [InlineData("aaaaaaaaaaaa")]
    [InlineData("123456789012")]
    [InlineData("qwertyuiopas")]
    [InlineData("abcdefghijkl")]
    [InlineData("abcabcabcabc")]
    [InlineData("Password1234")]
    [InlineData("senha1234567")]
    [InlineData("P@ssw0rd2024")]
    [InlineData(Emoji + Emoji + Emoji + Emoji + Emoji + Emoji + Emoji + Emoji + Emoji + Emoji + Emoji + Emoji)]
    public void Evaluate_CommonPatterns_IsVeryWeak(string password)
    {
        Assert.Equal(PasswordStrengthLevel.VeryWeak, PasswordStrength.Evaluate(password));
    }

    [Fact]
    public void Evaluate_TwelveRandomLowercase_IsFair()
    {
        Assert.Equal(PasswordStrengthLevel.Fair, PasswordStrength.Evaluate("vbqmzkxjrtwp"));
    }

    [Fact]
    public void Evaluate_SixteenMixedRandom_IsVeryStrong()
    {
        Assert.Equal(PasswordStrengthLevel.VeryStrong, PasswordStrength.Evaluate("Tq7#vZ2m!Kx9$pWd"));
    }

    [Fact]
    public void Evaluate_GeneratedPassphrases_AreAtLeastStrong()
    {
        for (int run = 0; run < 200; run++)
        {
            char[] phrase = Passphrase.Generate();
            try
            {
                PasswordStrengthLevel level = PasswordStrength.Evaluate(phrase);

                // The phrase itself is never put in the failure message.
                Assert.True(level >= PasswordStrengthLevel.Strong, $"Run {run} rated {level}.");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(phrase.AsSpan()));
            }
        }
    }

    [Theory]
    [InlineData(29.99, PasswordStrengthLevel.VeryWeak)]
    [InlineData(30, PasswordStrengthLevel.Weak)]
    [InlineData(44.99, PasswordStrengthLevel.Weak)]
    [InlineData(45, PasswordStrengthLevel.Fair)]
    [InlineData(59.99, PasswordStrengthLevel.Fair)]
    [InlineData(60, PasswordStrengthLevel.Strong)]
    [InlineData(79.99, PasswordStrengthLevel.Strong)]
    [InlineData(80, PasswordStrengthLevel.VeryStrong)]
    public void LevelFor_Boundaries(double bits, PasswordStrengthLevel expected)
    {
        Assert.Equal(expected, PasswordStrength.LevelFor(bits));
    }

    [Fact]
    public void EstimateBits_VeryLongInput_DoesNotThrowAndGrows()
    {
        char[] longInput = new char[10_000];
        for (int i = 0; i < longInput.Length; i++)
        {
            longInput[i] = (char)('a' + (i * 7 % 26));
        }

        double analyzedOnly = PasswordStrength.EstimateBits(longInput.AsSpan(0, PasswordStrength.AnalyzedLength));
        double thousand = PasswordStrength.EstimateBits(longInput.AsSpan(0, 1_000));
        double full = PasswordStrength.EstimateBits(longInput);

        Assert.True(double.IsFinite(full));
        Assert.True(thousand > analyzedOnly, $"{thousand} <= {analyzedOnly}");
        Assert.True(full > thousand, $"{full} <= {thousand}");
    }

    [Fact]
    public void EstimateBits_SurrogatePairAtAnalyzedBoundary_CountsAsOneCharacter()
    {
        // 127 letters, then one emoji whose pair straddles the analyzed length: it must not be split.
        string input = new string('a', PasswordStrength.AnalyzedLength - 1) + Emoji;

        double bits = PasswordStrength.EstimateBits(input);

        Assert.True(double.IsFinite(bits));
        Assert.True(bits > PasswordStrength.EstimateBits(new string('a', PasswordStrength.AnalyzedLength - 1)));
    }

    [Fact]
    public void EstimateBits_DoesNotModifyInput()
    {
        char[] input = "P@ssw0rd2024-Tq7#vZ2m!Kx9$pWd".ToCharArray();
        char[] copy = (char[])input.Clone();

        _ = PasswordStrength.EstimateBits(input);
        _ = PasswordStrength.Evaluate(input);

        Assert.Equal(copy, input);
    }
}
