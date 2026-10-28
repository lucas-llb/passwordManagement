using System.Collections.Frozen;

namespace PasswordManager.Core.Passwords;

/// <summary>
/// Offline, dependency-free estimate of how hard a password is to guess (decision D4, W-16).
/// </summary>
/// <remarks>
/// <para>
/// The estimate is the cheapest way, in bits, to describe the password as a sequence of tokens: single characters
/// guessed by brute force, dictionary words (the EFF short list and a small list of common passwords), repeated blocks,
/// sequences (alphabet, digits, keyboard rows) and years. The cheapest split is found by dynamic programming, so a
/// password made of a known pattern costs as little as that pattern.
/// </para>
/// <para>
/// The meter is advisory only: the hard rule for a master password stays the 12-character minimum. The password is
/// never turned into a string. Only the first <see cref="AnalyzedLength"/> characters are analyzed, in two stack
/// copies that are cleared before returning; each further character adds the brute-force cost. Nothing is logged and
/// no exception carries the input.
/// </para>
/// </remarks>
public static class PasswordStrength
{
    /// <summary>Characters analyzed for patterns; the rest only add brute-force cost.</summary>
    internal const int AnalyzedLength = 128;

    private const int MinTokenLength = 3;
    private const int MaxRepeatBlock = 4;
    private const int EffMaxWordLength = 5;
    private const int YearLength = 4;

    private const int LowercasePool = 26;
    private const int UppercasePool = 26;
    private const int DigitPool = 10;
    private const int SymbolPool = 33;
    private const int NonAsciiPool = 100;

    // Common base words of weak passwords, pt-BR and English, lowercase ASCII. Leet spellings are covered by the
    // leet copy, digits and years by the other token kinds, so only the base word is listed.
    private static readonly string[] CommonWords =
    [
        "senha", "minhasenha", "admin", "administrador", "acesso", "usuario", "segredo", "brasil", "amor", "teamo",
        "flamengo", "corinthians", "palmeiras", "saopaulo", "vasco", "gremio", "santos", "cruzeiro", "jesus", "deus",
        "familia", "mudar", "princesa", "anjo", "felicidade", "vitoria", "gabriel", "maria", "futebol", "mae",
        "password", "passwd", "pass", "qwerty", "letmein", "welcome", "dragon", "monkey", "iloveyou", "master",
        "login", "shadow", "sunshine", "princess", "football", "baseball", "superman", "batman", "trustno",
        "freedom", "whatever", "secret", "hello", "love", "test", "guest", "root", "user", "default", "changeme",
    ];

    private static readonly FrozenSet<string> CommonSet = CommonWords.ToFrozenSet(StringComparer.Ordinal);

    private static readonly int MaxWordLength = Math.Max(EffMaxWordLength, CommonWords.Max(word => word.Length));

    private static readonly double EffWordBits = Math.Log2(Passphrase.WordListSize);

    private static readonly double CommonWordBits = Math.Log2(CommonSet.Count);

    private static readonly double YearBits = Math.Log2(200);

    private static readonly string[] KeyboardRows = ["qwertyuiop", "asdfghjkl", "zxcvbnm", "1234567890"];

    /// <summary>Estimates the bits needed to guess <paramref name="password"/>. Empty input gives 0.</summary>
    public static double EstimateBits(ReadOnlySpan<char> password)
    {
        if (password.IsEmpty)
        {
            return 0;
        }

        double charBits = Math.Log2(PoolSize(password));

        int analyzed = Math.Min(password.Length, AnalyzedLength);
        if (analyzed < password.Length && char.IsHighSurrogate(password[analyzed - 1])
            && char.IsLowSurrogate(password[analyzed]))
        {
            // Never split a surrogate pair between the analyzed part and the tail.
            analyzed--;
        }

        double tailBits = CodePointCount(password[analyzed..]) * charBits;

        Span<char> lowerBuffer = stackalloc char[AnalyzedLength];
        Span<char> leetBuffer = stackalloc char[AnalyzedLength];
        try
        {
            Span<char> lower = lowerBuffer[..analyzed];
            Span<char> leet = leetBuffer[..analyzed];
            for (int i = 0; i < analyzed; i++)
            {
                lower[i] = ToLowerAscii(password[i]);
                leet[i] = FromLeet(lower[i]);
            }

            return Segment(password[..analyzed], lower, leet, charBits) + tailBits;
        }
        finally
        {
            lowerBuffer.Clear();
            leetBuffer.Clear();
        }
    }

    /// <summary>Rates <paramref name="password"/> for the strength meter.</summary>
    public static PasswordStrengthLevel Evaluate(ReadOnlySpan<char> password) =>
        password.IsEmpty ? PasswordStrengthLevel.None : LevelFor(EstimateBits(password));

    /// <summary>Maps an estimate in bits to its level (bands: 30, 45, 60 and 80 bits).</summary>
    internal static PasswordStrengthLevel LevelFor(double bits) => bits switch
    {
        < 30 => PasswordStrengthLevel.VeryWeak,
        < 45 => PasswordStrengthLevel.Weak,
        < 60 => PasswordStrengthLevel.Fair,
        < 80 => PasswordStrengthLevel.Strong,
        _ => PasswordStrengthLevel.VeryStrong,
    };

    // best[j] is the cheapest cost of the first j characters; positions inside a surrogate pair stay unreachable.
    private static double Segment(ReadOnlySpan<char> original, ReadOnlySpan<char> lower, ReadOnlySpan<char> leet, double charBits)
    {
        int n = original.Length;
        Span<double> best = stackalloc double[n + 1];
        best.Fill(double.PositiveInfinity);
        best[0] = 0;

        for (int i = 0; i < n; i++)
        {
            double start = best[i];
            if (double.IsPositiveInfinity(start))
            {
                continue;
            }

            // Brute force: one character, a surrogate pair counting as one.
            Relax(best, i + (IsPairAt(original, i) ? 2 : 1), start + charBits);

            AddWords(best, i, start, original, lower, leet);
            AddRepeats(best, i, start, original, lower, charBits);
            AddAlphabetSequences(best, i, start, lower, charBits);
            AddKeyboardSequences(best, i, start, lower, charBits);
            AddYear(best, i, start, lower);
        }

        return best[n];
    }

    private static void AddWords(
        Span<double> best, int i, double start, ReadOnlySpan<char> original, ReadOnlySpan<char> lower, ReadOnlySpan<char> leet)
    {
        var commonLookup = CommonSet.GetAlternateLookup<ReadOnlySpan<char>>();
        int longest = Math.Min(MaxWordLength, original.Length - i);
        for (int length = MinTokenLength; length <= longest; length++)
        {
            ReadOnlySpan<char> candidate = leet.Slice(i, length);
            double wordBits = double.PositiveInfinity;
            if (length <= EffMaxWordLength && Wordlist.Contains(candidate))
            {
                wordBits = EffWordBits;
            }

            if (commonLookup.Contains(candidate))
            {
                wordBits = Math.Min(wordBits, CommonWordBits);
            }

            if (double.IsPositiveInfinity(wordBits))
            {
                continue;
            }

            if (original.Slice(i, length).ContainsAnyInRange('A', 'Z'))
            {
                wordBits += 1;
            }

            if (!lower.Slice(i, length).SequenceEqual(candidate))
            {
                wordBits += 1;
            }

            Relax(best, i + length, start + wordBits);
        }
    }

    private static void AddRepeats(
        Span<double> best, int i, double start, ReadOnlySpan<char> original, ReadOnlySpan<char> lower, double charBits)
    {
        int n = original.Length;
        for (int blockLength = 1; blockLength <= MaxRepeatBlock && i + (2 * blockLength) <= n; blockLength++)
        {
            if (char.IsHighSurrogate(original[i + blockLength - 1]))
            {
                // The block would end inside a surrogate pair.
                continue;
            }

            ReadOnlySpan<char> block = lower.Slice(i, blockLength);
            double blockBits = CodePointCount(original.Slice(i, blockLength)) * charBits;
            int count = 1;
            while (i + ((count + 1) * blockLength) <= n
                && lower.Slice(i + (count * blockLength), blockLength).SequenceEqual(block))
            {
                count++;
                int end = i + (count * blockLength);
                if (end - i >= MinTokenLength)
                {
                    Relax(best, end, start + blockBits + Math.Log2(count));
                }
            }
        }
    }

    private static void AddAlphabetSequences(Span<double> best, int i, double start, ReadOnlySpan<char> lower, double charBits)
    {
        char first = lower[i];
        bool isLetter = first is >= 'a' and <= 'z';
        bool isDigit = char.IsAsciiDigit(first);
        if (!isLetter && !isDigit)
        {
            return;
        }

        foreach (int step in (ReadOnlySpan<int>)[1, -1])
        {
            int length = 1;
            while (i + length < lower.Length
                && lower[i + length] - lower[i + length - 1] == step
                && (isLetter ? lower[i + length] is >= 'a' and <= 'z' : char.IsAsciiDigit(lower[i + length])))
            {
                length++;
                if (length >= MinTokenLength)
                {
                    Relax(best, i + length, start + SequenceBits(length, charBits));
                }
            }
        }
    }

    private static void AddKeyboardSequences(Span<double> best, int i, double start, ReadOnlySpan<char> lower, double charBits)
    {
        foreach (string row in KeyboardRows)
        {
            int position = row.IndexOf(lower[i], StringComparison.Ordinal);
            if (position < 0)
            {
                continue;
            }

            foreach (int step in (ReadOnlySpan<int>)[1, -1])
            {
                int length = 1;
                while (i + length < lower.Length)
                {
                    int next = position + (step * length);
                    if (next < 0 || next >= row.Length || lower[i + length] != row[next])
                    {
                        break;
                    }

                    length++;
                    if (length >= MinTokenLength)
                    {
                        Relax(best, i + length, start + SequenceBits(length, charBits));
                    }
                }
            }
        }
    }

    private static void AddYear(Span<double> best, int i, double start, ReadOnlySpan<char> lower)
    {
        if (i + YearLength > lower.Length)
        {
            return;
        }

        bool century = (lower[i] == '1' && lower[i + 1] == '9') || (lower[i] == '2' && lower[i + 1] == '0');
        if (century && char.IsAsciiDigit(lower[i + 2]) && char.IsAsciiDigit(lower[i + 3]))
        {
            Relax(best, i + YearLength, start + YearBits);
        }
    }

    private static double SequenceBits(int length, double charBits) => charBits + Math.Log2(length) + 1;

    private static void Relax(Span<double> best, int end, double cost)
    {
        if (cost < best[end])
        {
            best[end] = cost;
        }
    }

    private static int PoolSize(ReadOnlySpan<char> password)
    {
        bool lower = false, upper = false, digit = false, symbol = false, nonAscii = false;
        foreach (char c in password)
        {
            if (c is >= 'a' and <= 'z')
            {
                lower = true;
            }
            else if (c is >= 'A' and <= 'Z')
            {
                upper = true;
            }
            else if (char.IsAsciiDigit(c))
            {
                digit = true;
            }
            else if (char.IsAscii(c))
            {
                symbol = true;
            }
            else
            {
                nonAscii = true;
            }
        }

        return (lower ? LowercasePool : 0) + (upper ? UppercasePool : 0) + (digit ? DigitPool : 0)
            + (symbol ? SymbolPool : 0) + (nonAscii ? NonAsciiPool : 0);
    }

    private static bool IsPairAt(ReadOnlySpan<char> text, int i) =>
        char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]);

    private static int CodePointCount(ReadOnlySpan<char> text)
    {
        int count = 0;
        for (int i = 0; i < text.Length; i += IsPairAt(text, i) ? 2 : 1)
        {
            count++;
        }

        return count;
    }

    private static char ToLowerAscii(char c) => c is >= 'A' and <= 'Z' ? (char)(c + ('a' - 'A')) : c;

    private static char FromLeet(char c) => c switch
    {
        '@' or '4' => 'a',
        '3' => 'e',
        '1' or '!' => 'i',
        '0' => 'o',
        '$' or '5' => 's',
        '7' => 't',
        _ => c,
    };
}
