namespace PasswordManager.ViewModels.Tests;

public sealed class EntrySearchTests
{
    [Fact]
    public void Fold_AccentedText_DropsAccentsAndUpperCases()
    {
        Assert.Equal("SAO JOAO", EntrySearch.Fold("São João"));
    }

    [Fact]
    public void Fold_NfcAndNfdFormsOfTheSameText_AreEqual()
    {
        var nfc = "Café";
        var nfd = "Café";

        Assert.NotEqual(nfc, nfd);
        Assert.Equal("CAFE", EntrySearch.Fold(nfc));
        Assert.Equal(EntrySearch.Fold(nfc), EntrySearch.Fold(nfd));
    }

    [Fact]
    public void Fold_CjkText_IsUnchangedApartFromUpperCasing()
    {
        Assert.Equal("東京アカウント", EntrySearch.Fold("東京アカウント"));
        Assert.Equal("国际", EntrySearch.Fold("国际"));
        Assert.Equal("東京 EXAMPLE.JP", EntrySearch.Fold("東京 example.jp"));
    }

    [Fact]
    public void Fold_EmptyText_IsEmpty()
    {
        Assert.Equal(string.Empty, EntrySearch.Fold(string.Empty));
    }

    [Fact]
    public void Fold_LoneSurrogate_DoesNotThrow()
    {
        var folded = EntrySearch.Fold("\uD800");

        Assert.Equal("\uD800", folded);
    }

    [Fact]
    public void Fold_LoneSurrogateInsideText_DoesNotThrowAndKeepsTheRestSearchable()
    {
        var folded = EntrySearch.Fold("ab\uD800cd");

        Assert.Contains("AB", folded, StringComparison.Ordinal);
        Assert.Contains("CD", folded, StringComparison.Ordinal);
    }

    [Fact]
    public void Fold_InvalidUtf16_FallsBackToUpperCasingTheRawText()
    {
        // The text cannot be normalized, so its accent stays.
        Assert.Equal("É\uD800", EntrySearch.Fold("é\uD800"));
    }

    [Fact]
    public void Fold_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => EntrySearch.Fold(null!));
    }

    [Fact]
    public void Terms_SplitsOnWhitespaceAndFoldsEachTerm()
    {
        Assert.Equal(["LUZ", "SAO"], EntrySearch.Terms("  luz   sao "));
    }

    [Fact]
    public void Terms_TabsAndLineBreaksAlsoSeparateTerms()
    {
        Assert.Equal(["LUZ", "SAO", "JOAO"], EntrySearch.Terms("luz\tsão\r\njoão"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Terms_EmptyOrBlankQuery_GivesNoTerms(string query)
    {
        Assert.Empty(EntrySearch.Terms(query));
    }

    [Fact]
    public void Terms_ATermMadeOnlyOfCombiningMarks_IsDropped()
    {
        Assert.Equal(["LUZ"], EntrySearch.Terms("luz ́̂"));
    }

    [Fact]
    public void Terms_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => EntrySearch.Terms(null!));
    }

    [Fact]
    public void Matches_EveryTermMustMatch()
    {
        string[] fields = ["CONTA DE LUZ", "JOAO@EXAMPLE.COM", "", "FINANCAS"];

        Assert.True(EntrySearch.Matches(fields, ["LUZ", "JOAO"]));
        Assert.False(EntrySearch.Matches(fields, ["LUZ", "ZZZ"]));
        Assert.False(EntrySearch.Matches(fields, ["ZZZ"]));
    }

    [Fact]
    public void Matches_EachTermCanMatchADifferentField()
    {
        string[] fields = ["CONTA DE LUZ", "JOAO@EXAMPLE.COM", "HTTPS://LUZ.EXAMPLE.COM", "FINANCAS"];

        Assert.True(EntrySearch.Matches(fields, ["CONTA", "JOAO", "FINANCAS"]));
    }

    [Fact]
    public void Matches_NoTerms_MatchesEverything()
    {
        Assert.True(EntrySearch.Matches(["A", "B"], []));
        Assert.True(EntrySearch.Matches([], []));
    }

    [Fact]
    public void Matches_ATermThatSpansTwoFields_DoesNotMatch()
    {
        // Title "ab" and username "cd": the term "BC" only exists if the fields were glued together.
        Assert.False(EntrySearch.Matches(["AB", "CD"], ["BC"]));
    }

    [Fact]
    public void Matches_UsesAnOrdinalSubstring()
    {
        Assert.True(EntrySearch.Matches(["EXAMPLE.JP"], ["AMPLE."]));
        Assert.False(EntrySearch.Matches(["EXAMPLE.JP"], ["example"]));
    }

    [Fact]
    public void Matches_NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => EntrySearch.Matches(null!, []));
        Assert.Throws<ArgumentNullException>(() => EntrySearch.Matches([], null!));
    }
}
