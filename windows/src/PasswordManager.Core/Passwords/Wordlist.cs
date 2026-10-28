using System.Collections.Frozen;
using System.Text;

namespace PasswordManager.Core.Passwords;

/// <summary>
/// The EFF Short Wordlist #1 (Creative Commons Attribution, see THIRD-PARTY-NOTICES.md), embedded unmodified from
/// <c>eff_short_wordlist_1.txt</c>. Each line is <c>dddd&lt;TAB&gt;word</c>; only the word is kept.
/// </summary>
/// <remarks>
/// The words are public, so they are not zeroed. Only the generated passphrase is secret. The list is loaded once, on
/// first use, and the load fails loudly if the embedded file is not the 1,296 unique words it must be.
/// </remarks>
internal static class Wordlist
{
    internal const int ExpectedCount = 1296;

    private const string ResourceName = "PasswordManager.Core.Passwords.eff_short_wordlist_1.txt";

    /// <summary>The words in the order of the published list.</summary>
    internal static readonly IReadOnlyList<string> Words = ReadWords();

    /// <summary>The same words as a set, for lookups of a span without allocating a string.</summary>
    internal static readonly FrozenSet<string> Set = new HashSet<string>(Words, StringComparer.Ordinal).ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Returns whether <paramref name="word"/> is one of the words, using an ordinal, case-sensitive match.</summary>
    internal static bool Contains(ReadOnlySpan<char> word) => Set.GetAlternateLookup<ReadOnlySpan<char>>().Contains(word);

    /// <summary>Opens the embedded resource. The caller disposes the stream.</summary>
    /// <exception cref="InvalidOperationException">The resource is missing from the assembly.</exception>
    internal static Stream OpenResource() =>
        typeof(Wordlist).Assembly.GetManifestResourceStream(ResourceName)
        ?? throw new InvalidOperationException("The embedded wordlist resource is missing.");

    private static string[] ReadWords()
    {
        using var stream = OpenResource();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string[] lines = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);

        var words = new string[lines.Length];
        for (int i = 0; i < lines.Length; i++)
        {
            int tab = lines[i].IndexOf('\t');
            if (tab < 0)
            {
                throw new InvalidOperationException("The embedded wordlist has a line without a tab.");
            }

            words[i] = lines[i][(tab + 1)..].TrimEnd('\r');
        }

        if (words.Length != ExpectedCount || new HashSet<string>(words, StringComparer.Ordinal).Count != ExpectedCount)
        {
            throw new InvalidOperationException("The embedded wordlist must have exactly 1,296 unique words.");
        }

        return words;
    }
}
