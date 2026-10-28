using System.Security.Cryptography;
using PasswordManager.Core.Passwords;

namespace PasswordManager.Core.Tests.Passwords;

public class WordlistTests
{
    // SHA-256 of eff_short_wordlist_1.txt as published by the EFF (see THIRD-PARTY-NOTICES.md).
    private const string PublishedSha256 = "8f5ca830b8bffb6fe39c9736c024a00a6a6411adb3f83a9be8bfeeb6e067ae69";

    [Fact]
    public void Resource_MatchesPublishedEffShortList()
    {
        using var stream = Wordlist.OpenResource();

        Assert.Equal(PublishedSha256, Convert.ToHexStringLower(SHA256.HashData(stream)));
    }

    [Fact]
    public void Words_Are1296UniqueLowercaseWordsOf3To5Chars()
    {
        Assert.Equal(1296, Wordlist.Words.Count);
        Assert.Equal(1296, Wordlist.Words.Distinct(StringComparer.Ordinal).Count());

        Assert.All(Wordlist.Words, word =>
        {
            Assert.InRange(word.Length, 3, 5);
            Assert.Matches("^[a-z-]+$", word);
        });
    }

    [Fact]
    public void Contains_KnownWordAndNonWord_UsesOrdinalSpanLookup()
    {
        string known = Wordlist.Words[648];

        Assert.True(Wordlist.Contains(known.AsSpan()));
        Assert.False(Wordlist.Contains(known.ToUpperInvariant().AsSpan()));
        Assert.False(Wordlist.Contains("zzzzzzzzz".AsSpan()));
    }
}
