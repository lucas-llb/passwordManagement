using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace PasswordManager.Core.Passwords;

/// <summary>
/// Generates a passphrase: random words from the EFF Short Wordlist #1, joined by <see cref="Separator"/>, in lowercase.
/// </summary>
/// <remarks>
/// Each word index comes from <see cref="RandomNumberGenerator.GetInt32(int)"/>, a CSPRNG with no modulo bias. Six words
/// give about 62 bits (AGENTS §3.4 and the W-16 plan). The result is written straight into a <c>char[]</c>, so the
/// passphrase never becomes a managed string here. The caller owns the array and zeroes it after use.
/// </remarks>
public static class Passphrase
{
    /// <summary>Words in a suggested passphrase.</summary>
    public const int DefaultWordCount = 6;

    /// <summary>Fewest words accepted by <see cref="Generate(int)"/>.</summary>
    public const int MinWordCount = 4;

    /// <summary>Most words accepted by <see cref="Generate(int)"/>.</summary>
    public const int MaxWordCount = 12;

    /// <summary>Character between two words.</summary>
    public const char Separator = '-';

    /// <summary>Number of words the generator chooses from.</summary>
    public const int WordListSize = Wordlist.ExpectedCount;

    /// <summary>Entropy of a passphrase with <paramref name="wordCount"/> words, in bits.</summary>
    public static double EntropyBits(int wordCount) => wordCount * Math.Log2(WordListSize);

    /// <summary>
    /// Generates a passphrase of <paramref name="wordCount"/> words.
    /// </summary>
    /// <param name="wordCount">Number of words, from <see cref="MinWordCount"/> to <see cref="MaxWordCount"/>.</param>
    /// <returns>The passphrase in a new array of the exact length. The caller zeroes it with
    /// <see cref="System.Security.Cryptography.CryptographicOperations.ZeroMemory(Span{byte})"/> after use.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="wordCount"/> is outside the allowed range.</exception>
    public static char[] Generate(int wordCount = DefaultWordCount) =>
        Generate(wordCount, static exclusiveMax => RandomNumberGenerator.GetInt32(exclusiveMax));

    /// <summary>
    /// Builds a passphrase from the indices that <paramref name="pickIndex"/> returns. This is the seam the tests use;
    /// the public overload passes the CSPRNG.
    /// </summary>
    internal static char[] Generate(int wordCount, Func<int, int> pickIndex)
    {
        if (wordCount < MinWordCount || wordCount > MaxWordCount)
        {
            throw new ArgumentOutOfRangeException(nameof(wordCount));
        }

        Span<int> indices = stackalloc int[MaxWordCount];
        indices = indices[..wordCount];
        try
        {
            int length = wordCount - 1;
            for (int i = 0; i < wordCount; i++)
            {
                indices[i] = pickIndex(WordListSize);
                length += Wordlist.Words[indices[i]].Length;
            }

            char[] phrase = new char[length];
            int position = 0;
            for (int i = 0; i < wordCount; i++)
            {
                if (i > 0)
                {
                    phrase[position++] = Separator;
                }

                string word = Wordlist.Words[indices[i]];
                word.CopyTo(phrase.AsSpan(position));
                position += word.Length;
            }

            return phrase;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(indices));
        }
    }
}
