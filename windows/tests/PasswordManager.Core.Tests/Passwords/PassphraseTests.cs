using System.Runtime.InteropServices;
using System.Security.Cryptography;
using PasswordManager.Core.Passwords;

namespace PasswordManager.Core.Tests.Passwords;

public class PassphraseTests
{
    // Copies the phrase to a string and zeroes the array, as the caller of Generate must.
    private static string TakeAndZero(char[] phrase)
    {
        string text = new(phrase);
        CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(phrase.AsSpan()));
        return text;
    }

    [Fact]
    public void Generate_PickedIndices_JoinsThoseWordsWithHyphens()
    {
        // First, middle, last and one more: the chosen indices are used in order.
        var picks = new Queue<int>([0, 648, 1295, 1]);

        string text = TakeAndZero(Passphrase.Generate(4, _ => picks.Dequeue()));

        string expected = string.Join(
            '-',
            Wordlist.Words[0],
            Wordlist.Words[648],
            Wordlist.Words[1295],
            Wordlist.Words[1]);
        Assert.Equal(expected, text);
    }

    [Fact]
    public void Generate_ShortestWords_HasAtLeast23Chars()
    {
        int shortest = Enumerable.Range(0, Wordlist.Words.Count).First(i => Wordlist.Words[i].Length == 3);

        string text = TakeAndZero(Passphrase.Generate(Passphrase.DefaultWordCount, _ => shortest));

        Assert.True(text.Length >= 23, $"Length was {text.Length}.");
    }

    [Theory]
    [InlineData(3)]
    [InlineData(13)]
    public void Generate_WordCountOutOfRange_Throws(int wordCount)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Passphrase.Generate(wordCount));
    }

    [Fact]
    public void Generate_Default_IsSixListWords()
    {
        for (int run = 0; run < 50; run++)
        {
            string text = TakeAndZero(Passphrase.Generate());

            // "yo-yo" is the only list word with a hyphen, so two adjacent "yo" parts are one word.
            var words = new List<string>();
            string[] parts = text.Split(Passphrase.Separator);
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i] == "yo" && i + 1 < parts.Length && parts[i + 1] == "yo")
                {
                    words.Add("yo-yo");
                    i++;
                }
                else
                {
                    words.Add(parts[i]);
                }
            }

            Assert.Equal(Passphrase.DefaultWordCount, words.Count);
            Assert.All(words, word => Assert.Contains(word, Wordlist.Words));
        }
    }

    [Fact]
    public void Generate_FiftyRuns_AreDistinct()
    {
        var phrases = new HashSet<string>(StringComparer.Ordinal);

        for (int run = 0; run < 50; run++)
        {
            phrases.Add(TakeAndZero(Passphrase.Generate()));
        }

        Assert.Equal(50, phrases.Count);
    }

    [Fact]
    public void EntropyBits_SixWords_IsAbout62()
    {
        Assert.InRange(Passphrase.EntropyBits(6), 62.0, 62.1);
    }
}
