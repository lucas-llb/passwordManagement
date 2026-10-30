using System.Runtime.InteropServices;
using System.Security.Cryptography;
using PasswordManager.Core.Passwords;

namespace PasswordManager.Core.Tests.Passwords;

public class PasswordGeneratorTests
{
    // Copies the password to a string and zeroes the array, as the caller of Generate must.
    private static string TakeAndZero(char[] password)
    {
        string text = new(password);
        CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(password.AsSpan()));
        return text;
    }

    // A seam that returns the queued picks in order and records the exclusiveMax of every call.
    private static Func<int, int> Seam(Queue<int> picks, List<int> maxes) =>
        exclusiveMax =>
        {
            maxes.Add(exclusiveMax);
            return picks.Dequeue();
        };

    private static string AlphabetOf(bool uppercase, bool digits, bool symbols) =>
        PasswordGenerator.LowercaseLetters
        + (uppercase ? PasswordGenerator.UppercaseLetters : string.Empty)
        + (digits ? PasswordGenerator.DigitCharacters : string.Empty)
        + (symbols ? PasswordGenerator.SymbolCharacters : string.Empty);

    [Fact]
    public void Constants_SetsHaveTheDocumentedShape()
    {
        Assert.Equal(26, PasswordGenerator.LowercaseLetters.Length);
        Assert.Equal(26, PasswordGenerator.UppercaseLetters.Length);
        Assert.Equal(10, PasswordGenerator.DigitCharacters.Length);
        Assert.Equal(28, PasswordGenerator.SymbolCharacters.Length);

        string all = AlphabetOf(uppercase: true, digits: true, symbols: true);
        Assert.Equal(90, all.Length);
        Assert.Equal(all.Length, all.Distinct().Count());
        Assert.All(all, c => Assert.InRange(c, '!', '~'));
        Assert.DoesNotContain(PasswordGenerator.SymbolCharacters, c => c is '"' or '\'' or '`' or '\\' or ' ');
        Assert.Equal(20, PasswordGenerator.DefaultLength);
        Assert.Equal(8, PasswordGenerator.MinLength);
        Assert.Equal(128, PasswordGenerator.MaxLength);
    }

    [Fact]
    public void Generate_PickedIndices_MapToAlphabetInOrder()
    {
        // The last index of each set: lowercase 25, uppercase 51, digits 61, symbols 89.
        var picks = new Queue<int>([25, 51, 61, 89, 25, 51, 61, 89]);
        var maxes = new List<int>();

        string text = TakeAndZero(PasswordGenerator.Generate(8, true, true, true, Seam(picks, maxes)));

        Assert.Equal("zZ9~zZ9~", text);
        Assert.Equal(8, maxes.Count);
        Assert.All(maxes, max => Assert.Equal(90, max));
    }

    [Theory]
    [InlineData(false, true, true, 64)]
    [InlineData(true, false, true, 80)]
    [InlineData(true, true, false, 62)]
    [InlineData(false, false, false, 26)]
    public void Generate_SetsOff_AlphabetShrinks(bool uppercase, bool digits, bool symbols, int expectedMax)
    {
        // Cycle through the first character of every enabled set so the draw is accepted at once.
        var firsts = new List<int> { 0 };
        int offset = PasswordGenerator.LowercaseLetters.Length;
        if (uppercase)
        {
            firsts.Add(offset);
            offset += PasswordGenerator.UppercaseLetters.Length;
        }

        if (digits)
        {
            firsts.Add(offset);
            offset += PasswordGenerator.DigitCharacters.Length;
        }

        if (symbols)
        {
            firsts.Add(offset);
        }

        var maxes = new List<int>();
        int call = 0;

        string text = TakeAndZero(PasswordGenerator.Generate(
            8,
            uppercase,
            digits,
            symbols,
            exclusiveMax =>
            {
                maxes.Add(exclusiveMax);
                return firsts[call++ % firsts.Count];
            }));

        Assert.Equal(8, maxes.Count);
        Assert.All(maxes, max => Assert.Equal(expectedMax, max));
        string alphabet = AlphabetOf(uppercase, digits, symbols);
        Assert.Equal(expectedMax, alphabet.Length);
        Assert.All(text, c => Assert.Contains(c, alphabet));
    }

    [Fact]
    public void Generate_DrawMissingAClass_DrawsAgain()
    {
        // First draw: lowercase, uppercase and symbols but no digit. Second draw: every class.
        int[] first = [0, 26, 62, 0, 0, 0, 0, 0];
        int[] second = [0, 26, 52, 62, 0, 0, 0, 0];
        var picks = new Queue<int>([.. first, .. second]);
        var maxes = new List<int>();

        string text = TakeAndZero(PasswordGenerator.Generate(8, true, true, true, Seam(picks, maxes)));

        Assert.Equal("aA0!aaaa", text);
        Assert.Equal(2 * 8, maxes.Count);
        Assert.Empty(picks);
    }

    [Fact]
    public void Generate_SeamNeverSatisfies_ThrowsAfterCap()
    {
        int calls = 0;

        Assert.Throws<InvalidOperationException>(() => PasswordGenerator.Generate(
            8,
            true,
            true,
            true,
            _ =>
            {
                calls++;
                return 0;
            }));

        // 1,000 rejected draws of 8 characters each.
        Assert.Equal(1000 * 8, calls);
    }

    [Fact]
    public void Generate_Default_TwentyCharsWithEveryClass()
    {
        string alphabet = AlphabetOf(uppercase: true, digits: true, symbols: true);

        for (int run = 0; run < 200; run++)
        {
            string text = TakeAndZero(PasswordGenerator.Generate());

            Assert.Equal(20, text.Length);
            Assert.All(text, c => Assert.Contains(c, alphabet));
            Assert.Contains(text, c => PasswordGenerator.LowercaseLetters.Contains(c));
            Assert.Contains(text, c => PasswordGenerator.UppercaseLetters.Contains(c));
            Assert.Contains(text, c => PasswordGenerator.DigitCharacters.Contains(c));
            Assert.Contains(text, c => PasswordGenerator.SymbolCharacters.Contains(c));
        }
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void Generate_OptionsOff_OnlyEnabledClasses(bool uppercase, bool digits, bool symbols)
    {
        string alphabet = AlphabetOf(uppercase, digits, symbols);

        for (int run = 0; run < 100; run++)
        {
            string text = TakeAndZero(PasswordGenerator.Generate(20, uppercase, digits, symbols));

            Assert.Equal(20, text.Length);
            Assert.All(text, c => Assert.Contains(c, alphabet));
            Assert.Contains(text, c => PasswordGenerator.LowercaseLetters.Contains(c));
            Assert.Equal(uppercase, text.Any(c => PasswordGenerator.UppercaseLetters.Contains(c)));
            Assert.Equal(digits, text.Any(c => PasswordGenerator.DigitCharacters.Contains(c)));
            Assert.Equal(symbols, text.Any(c => PasswordGenerator.SymbolCharacters.Contains(c)));
        }
    }

    [Fact]
    public void Generate_LengthBounds()
    {
        Assert.Equal(8, TakeAndZero(PasswordGenerator.Generate(8)).Length);
        Assert.Equal(128, TakeAndZero(PasswordGenerator.Generate(128)).Length);

        Assert.Throws<ArgumentOutOfRangeException>(() => PasswordGenerator.Generate(7));
        Assert.Throws<ArgumentOutOfRangeException>(() => PasswordGenerator.Generate(129));
        Assert.Throws<ArgumentOutOfRangeException>(() => PasswordGenerator.Generate(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => PasswordGenerator.Generate(-1));
    }

    [Fact]
    public void Generate_LowercaseOnly_IsUniform()
    {
        // No class condition applies with lowercase only, so every character is a plain uniform draw over 26 letters
        // and the counts are binomial: mean n/26, sigma = sqrt(n * p * (1 - p)).
        const int runs = 2000;
        const int length = 128;
        const int total = runs * length;
        int[] counts = new int[26];

        for (int run = 0; run < runs; run++)
        {
            string text = TakeAndZero(PasswordGenerator.Generate(length, false, false, false));
            foreach (char c in text)
            {
                counts[c - 'a']++;
            }
        }

        double p = 1.0 / 26;
        double mean = total * p;
        double sigma = Math.Sqrt(total * p * (1 - p));
        Assert.Equal(total, counts.Sum());
        for (int letter = 0; letter < counts.Length; letter++)
        {
            Assert.InRange(counts[letter], mean - (6 * sigma), mean + (6 * sigma));
        }
    }

    [Fact]
    public void Generate_FiftyRuns_AreDistinct()
    {
        var passwords = new HashSet<string>(StringComparer.Ordinal);

        for (int run = 0; run < 50; run++)
        {
            passwords.Add(TakeAndZero(PasswordGenerator.Generate()));
        }

        Assert.Equal(50, passwords.Count);
    }
}
