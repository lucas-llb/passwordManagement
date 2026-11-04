using System.Text;
using System.Text.Json;
using PasswordManager.Core.Tests.TestVectors;
using PasswordManager.Core.Vault;

namespace PasswordManager.Core.Tests.Vault;

public sealed class VaultCsvImportTests
{
    private const string Header = "name,password\n";
    private const string FakeValue = "fictitious-field-value";

    private const string KeePass1xHeader = "\"Account\",\"Login Name\",\"Password\",\"Web Site\",\"Comments\"\r\n";

    private static readonly DateTimeOffset Truncated = new(2026, 3, 4, 5, 6, 7, 123, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = Truncated.AddTicks(4567);

    private static ImportFile Parse(string csv) => VaultCsv.ParseAndZero(Encoding.UTF8.GetBytes(csv), Now);

    private static VaultImportException AssertRefused(string csv, ImportFileError error)
    {
        var exception = Assert.Throws<VaultImportException>(() => Parse(csv));

        Assert.Equal(error, exception.Error);
        return exception;
    }

    private static string Rows(int count, string row)
    {
        var builder = new StringBuilder(Header);
        for (var i = 0; i < count; i++)
        {
            builder.Append(row).Append('\n');
        }

        return builder.ToString();
    }

    // --- Layouts ---

    [Fact]
    public void ParseAndZero_OwnExport_RoundTripsSixFields()
    {
        var manifest = TestVectorFiles.ReadManifest("entries-unicode");
        var entries = manifest.Content.Deserialize(VaultJsonContext.Default.VaultContent)?.Entries
            ?? throw new JsonException("Manifest entries-unicode.json has no content.");

        var parsed = VaultCsv.ParseAndZero(VaultCsv.Serialize(entries), Now);

        Assert.Equal(entries.Count, parsed.Entries.Count);
        Assert.Contains(entries, entry => entry.Notes.Contains('\n', StringComparison.Ordinal) && entry.Notes.Contains('\\', StringComparison.Ordinal));
        for (var i = 0; i < entries.Count; i++)
        {
            Assert.Equal(entries[i].Title, parsed.Entries[i].Title);
            Assert.Equal(entries[i].Url, parsed.Entries[i].Url);
            Assert.Equal(entries[i].Username, parsed.Entries[i].Username);
            Assert.Equal(entries[i].Password, parsed.Entries[i].Password);
            Assert.Equal(entries[i].Notes, parsed.Entries[i].Notes);
            Assert.Equal(entries[i].Folder, parsed.Entries[i].Folder);
            Assert.NotEqual(entries[i].Id, parsed.Entries[i].Id);
            Assert.False(parsed.Entries[i].Favorite);
            Assert.Equal(Truncated, parsed.Entries[i].CreatedAt);
            Assert.Equal(Truncated, parsed.Entries[i].UpdatedAt);
        }

        Assert.Empty(parsed.RejectedLines);
        Assert.Empty(parsed.IgnoredColumns);
    }

    [Fact]
    public void ParseAndZero_BitwardenIndividual_MapsColumns()
    {
        const string BitwardenHeader =
            "folder,favorite,type,name,notes,fields,reprompt,login_uri,login_username,login_password,login_totp\n";

        var parsed = Parse(
            BitwardenHeader +
            "Social,1,login,Example,some notes,,0,https://example.com,user@example.com,not-a-real-password,\n" +
            ",,note,Secure note,only text,,0,,,,\n");

        Assert.Equal(2, parsed.Entries.Count);
        var login = parsed.Entries[0];
        Assert.Equal("Social", login.Folder);
        Assert.True(login.Favorite);
        Assert.Equal("Example", login.Title);
        Assert.Equal("some notes", login.Notes);
        Assert.Equal("https://example.com", login.Url);
        Assert.Equal("user@example.com", login.Username);
        Assert.Equal("not-a-real-password", login.Password);
        var note = parsed.Entries[1];
        Assert.Equal("Secure note", note.Title);
        Assert.Equal("", note.Password);
        Assert.False(note.Favorite);
        Assert.Empty(parsed.RejectedLines);
        Assert.Empty(parsed.IgnoredColumns);

        var withExtras = Parse(
            BitwardenHeader +
            $",,login,Example,,custom: {FakeValue},0,,,not-a-real-password,{FakeValue}\n");

        Assert.Equal(["fields", "login_totp"], withExtras.IgnoredColumns);
    }

    [Fact]
    public void ParseAndZero_BitwardenOrganization_MapsCollectionsToFolder()
    {
        var parsed = Parse(
            "collections,type,name,notes,fields,reprompt,login_uri,login_username,login_password,login_totp\n" +
            "\"Team A,Team B\",login,Example,,,0,https://example.com,user,not-a-real-password,\n");

        var entry = Assert.Single(parsed.Entries);
        Assert.Equal("Team A,Team B", entry.Folder);
        Assert.Equal("Example", entry.Title);
        Assert.Equal("not-a-real-password", entry.Password);
    }

    [Fact]
    public void ParseAndZero_KeePassXc_MapsColumnsWithLf()
    {
        var parsed = Parse(
            "\"Group\",\"Title\",\"Username\",\"Password\",\"URL\",\"Notes\",\"TOTP\",\"Icon\",\"Last Modified\",\"Created\"\n" +
            "\"Root/Web\",\"Example\",\"user\",\"not\"\"a\\real\",\"https://example.com\",\"line 1\nline 2\"," +
            $"\"{FakeValue}\",\"3\",\"2020-01-01T00:00:00Z\",\"2020-01-01T00:00:00Z\"\n");

        var entry = Assert.Single(parsed.Entries);
        Assert.Equal("Root/Web", entry.Folder);
        Assert.Equal("Example", entry.Title);
        Assert.Equal("user", entry.Username);
        Assert.Equal("not\"a\\real", entry.Password);
        Assert.Equal("https://example.com", entry.Url);
        Assert.Equal("line 1\nline 2", entry.Notes);
        Assert.Equal(Truncated, entry.CreatedAt);
        Assert.Equal(Truncated, entry.UpdatedAt);
        Assert.Equal(["TOTP"], parsed.IgnoredColumns);
    }

    [Fact]
    public void ParseAndZero_KeePass1x_UnescapesBackslashes()
    {
        var parsed = Parse(
            KeePass1xHeader +
            "\"Example\",\"user\",\"a\\\"b\\\\c\",\"https://example.com\",\"not\\na break\r\na break\"\r\n");

        var entry = Assert.Single(parsed.Entries);
        Assert.Equal("Example", entry.Title);
        Assert.Equal("user", entry.Username);
        Assert.Equal("a\"b\\c", entry.Password);
        Assert.Equal("https://example.com", entry.Url);
        Assert.Equal("not\\na break\r\na break", entry.Notes);
    }

    [Fact]
    public void ParseAndZero_KeePass1xDoubledQuote_IsMalformed()
    {
        var exception = AssertRefused(
            KeePass1xHeader + "\"Example\",\"user\",\"a\"\"b\",\"\",\"\"\r\n",
            ImportFileError.MalformedCsv);

        Assert.Equal(2, exception.Line);
    }

    [Fact]
    public void ParseAndZero_KeePass1xNamesInAnotherOrder_IsReadAsRfc()
    {
        var parsed = Parse("Login Name,Account,Password,Web Site,Comments\nuser,Example,\"a\"\"b\\\\c\",,\n");

        Assert.Equal("a\"b\\\\c", Assert.Single(parsed.Entries).Password);
    }

    [Fact]
    public void ParseAndZero_RfcHeader_KeepsBackslashesVerbatim()
    {
        var parsed = Parse(Header + "\"a\\b\",\"c\\\"\"d\\\\\"\n");

        var entry = Assert.Single(parsed.Entries);
        Assert.Equal("a\\b", entry.Title);
        Assert.Equal("c\\\"d\\\\", entry.Password);
    }

    [Fact]
    public void ParseAndZero_HeaderCaseSpacesAndOrder_AreAccepted()
    {
        var parsed = Parse(" PASSWORD ,\tNaMe, Url\r\nnot-a-real-password,Example,https://example.com\r\n");

        var entry = Assert.Single(parsed.Entries);
        Assert.Equal("Example", entry.Title);
        Assert.Equal("not-a-real-password", entry.Password);
        Assert.Equal("https://example.com", entry.Url);
        Assert.Equal("", entry.Username);
        Assert.Equal("", entry.Notes);
        Assert.Equal("", entry.Folder);
    }

    [Fact]
    public void ParseAndZero_Bom_IsDropped()
    {
        byte[] file = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(Header + "Example,not-a-real-password\n")];

        var parsed = VaultCsv.ParseAndZero(file, Now);

        Assert.Equal("Example", Assert.Single(parsed.Entries).Title);
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("0", false)]
    [InlineData("", false)]
    [InlineData(" 1", false)]
    [InlineData("yes", false)]
    public void ParseAndZero_FavoriteColumn_AcceptsOneAndTrue(string value, bool expected)
    {
        var parsed = Parse($"name,password,favorite\nExample,not-a-real-password,{value}\n");

        Assert.Equal(expected, Assert.Single(parsed.Entries).Favorite);
    }

    // --- Refused files ---

    [Fact]
    public void ParseAndZero_InvalidUtf8_IsInvalidEncodingWithoutInnerException()
    {
        byte[] file = [.. Encoding.UTF8.GetBytes(Header + "Example,"), 0xC3, 0x28, (byte)'\n'];

        var exception = Assert.Throws<VaultImportException>(() => VaultCsv.ParseAndZero(file, Now));

        Assert.Equal(ImportFileError.InvalidEncoding, exception.Error);
        Assert.Null(exception.InnerException);
    }

    [Theory]
    [InlineData((byte)0xFF, (byte)0xFE)]
    [InlineData((byte)0xFE, (byte)0xFF)]
    public void ParseAndZero_Utf16Bom_IsInvalidEncoding(byte first, byte second)
    {
        byte[] file = [first, second, .. Encoding.Unicode.GetBytes(Header + "Example,not-a-real-password\n")];

        var exception = Assert.Throws<VaultImportException>(() => VaultCsv.ParseAndZero(file, Now));

        Assert.Equal(ImportFileError.InvalidEncoding, exception.Error);
    }

    public static TheoryData<string> BadHeaders =>
    [
        "",
        "\r\n\n,,\n",
        "Example,not-a-real-password\n",
        "alpha,beta,gamma\n1,2,3\n",
        "name,url\nExample,https://example.com\n",
        "password,url\nnot-a-real-password,https://example.com\n",
        "name,title,password\nExample,Other,not-a-real-password\n",
        "name,password,login_password\nExample,not-a-real,not-a-real\n",
        "name;password\nExample;not-a-real-password\n",
        Header[..^1] + "," + string.Join(',', Enumerable.Range(0, 63).Select(i => $"extra{i}")) + "\n",
    ];

    [Theory]
    [MemberData(nameof(BadHeaders))]
    public void ParseAndZero_BadHeader_IsInvalidHeader(string csv)
    {
        AssertRefused(csv, ImportFileError.InvalidHeader);
    }

    [Fact]
    public void ParseAndZero_SixtyFourColumns_IsAccepted()
    {
        var csv = Header[..^1] + "," + string.Join(',', Enumerable.Range(0, 62).Select(i => $"extra{i}")) + "\n";

        Assert.Empty(Parse(csv).Entries);
    }

    public static TheoryData<string, int> BadStructures =>
        new()
        {
            { Header + "Example,ok\nOther,\"" + FakeValue + "\nnext line\n", 3 },
            { Header + "Example,\"" + FakeValue + "\"x\n", 2 },
            { Header + "Example,\"" + FakeValue + "\" \n", 2 },
            { Header + "Example," + FakeValue + "\"x\n", 2 },
            { Header + "Example, \"" + FakeValue + "\"\n", 2 },
            { Header + "Example,ok\nOther," + FakeValue + "\0\n", 3 },
            { Header + "\"" + FakeValue + "\0\",ok\n", 2 },
            { "name,notes,password\n\"a\nb\",n,p\nOther,\"" + FakeValue + "\"y,p\n", 4 },
        };

    [Theory]
    [MemberData(nameof(BadStructures))]
    public void ParseAndZero_BadStructure_IsMalformedWithLine(string csv, int line)
    {
        var exception = AssertRefused(csv, ImportFileError.MalformedCsv);

        Assert.Equal(line, exception.Line);
        Assert.DoesNotContain(FakeValue, exception.ToString(), StringComparison.Ordinal);
    }

    // --- Records ---

    [Fact]
    public void ParseAndZero_LineEnds_AllEndRecords()
    {
        var parsed = Parse("name,password\r\na,1\nb,2\rc,3\r\nnot a record\nd,4");

        Assert.Equal(["a", "b", "c", "d"], parsed.Entries.Select(entry => entry.Title));
        Assert.Equal(["1", "2", "3", "4"], parsed.Entries.Select(entry => entry.Password));
        Assert.Equal([5], parsed.RejectedLines);
    }

    [Fact]
    public void ParseAndZero_LineBreakInQuotes_IsVerbatimAndCounted()
    {
        var parsed = Parse("name,notes,password\nExample,\"l1\r\nl2\nl3\rl4\",p\nnot a record\n");

        Assert.Equal("l1\r\nl2\nl3\rl4", Assert.Single(parsed.Entries).Notes);
        Assert.Equal([6], parsed.RejectedLines);
    }

    [Fact]
    public void ParseAndZero_BlankRecords_AreSkippedSilently()
    {
        var parsed = Parse("\r\n\n,\nname,password\n\n,\n\"\",\"\"\nExample,p\n\n,,,,,\n");

        Assert.Equal("Example", Assert.Single(parsed.Entries).Title);
        Assert.Empty(parsed.RejectedLines);
    }

    [Fact]
    public void ParseAndZero_WrongFieldCount_RejectsOnlyThatRecord()
    {
        var parsed = Parse(
            "name,url,password\n" +
            "Too few\n" +
            "Too many,u,p,extra\n" +
            "Far too many" + new string(',', 80) + "\n" +
            "Good,u,p\n");

        Assert.Equal("Good", Assert.Single(parsed.Entries).Title);
        Assert.Equal([2, 3, 4], parsed.RejectedLines);
    }

    [Fact]
    public void ParseAndZero_MissingTitle_RejectsRecord()
    {
        var parsed = Parse(Header + ",p\n   ,p\n\t,p\nGood,p\n\"  \",p\n");

        Assert.Equal("Good", Assert.Single(parsed.Entries).Title);
        Assert.Equal([2, 3, 4, 6], parsed.RejectedLines);
    }

    [Fact]
    public void ParseAndZero_FieldAtLimit_IsAcceptedAndOverLimitRejected()
    {
        var atLimit = new string('x', VaultImport.MaxFieldChars);
        var quotes = new string('"', 2 * VaultImport.MaxFieldChars);

        var parsed = Parse(
            Header +
            $"At limit,{atLimit}\n" +
            $"Over limit,{atLimit}x\n" +
            $"Quotes at limit,\"{quotes}\"\n" +
            $"Quotes over limit,\"{quotes}\"\"\"\n");

        Assert.Equal(["At limit", "Quotes at limit"], parsed.Entries.Select(entry => entry.Title));
        Assert.Equal(atLimit, parsed.Entries[0].Password);
        Assert.Equal(VaultImport.MaxFieldChars, parsed.Entries[1].Password.Length);
        Assert.Equal([3, 5], parsed.RejectedLines);
    }

    [Fact]
    public void ParseAndZero_TooManyRecords_Throws()
    {
        Assert.Equal(VaultImport.MaxEntries, Parse(Rows(VaultImport.MaxEntries, "Example,p")).Entries.Count);

        var exception = AssertRefused(Rows(VaultImport.MaxEntries + 1, "Example,p"), ImportFileError.TooManyEntries);
        Assert.Equal(VaultImport.MaxEntries + 2, exception.Line);

        // Rejected records count too, so a hostile file cannot grow the rejected list without limit.
        AssertRefused(Rows(VaultImport.MaxEntries + 1, "not a record"), ImportFileError.TooManyEntries);
    }

    [Fact]
    public void ParseAndZero_SpacesAndFormulaPrefixes_AreVerbatim()
    {
        var parsed = Parse("name,password,notes,folder\n  Bank  ,=1+1, +x ,'-2\n@home,-3,\t,\n");

        Assert.Equal("  Bank  ", parsed.Entries[0].Title);
        Assert.Equal("=1+1", parsed.Entries[0].Password);
        Assert.Equal(" +x ", parsed.Entries[0].Notes);
        Assert.Equal("'-2", parsed.Entries[0].Folder);
        Assert.Equal("@home", parsed.Entries[1].Title);
        Assert.Equal("-3", parsed.Entries[1].Password);
        Assert.Equal("\t", parsed.Entries[1].Notes);
    }

    [Fact]
    public void ParseAndZero_UnknownColumn_IsReportedOnlyFromAcceptedRecords()
    {
        var rejectedOnly = Parse($"name,password,extra\n   ,p,{FakeValue}\nGood,p,\n");
        Assert.Empty(rejectedOnly.IgnoredColumns);

        var silent = Parse($"name,password,type,reprompt,icon,last modified,created\nGood,p,{FakeValue},1,2,3,4\n");
        Assert.Empty(silent.IgnoredColumns);

        var reported = Parse($"name,password, Extra ,EXTRA,\nGood,p,{FakeValue},{FakeValue},{FakeValue}\n");
        Assert.Equal(["Extra", "#5"], reported.IgnoredColumns);
    }

    // --- Zeroing ---

    [Fact]
    public void ParseAndZero_Success_ZeroesInput()
    {
        var file = Encoding.UTF8.GetBytes(Header + "Example,not-a-real-password\n");

        Assert.Single(VaultCsv.ParseAndZero(file, Now).Entries);

        Assert.All(file, b => Assert.Equal(0, b));
    }

    [Theory]
    [InlineData("alpha,beta\n1,2\n")]
    [InlineData("name,password\nExample,\"not-a-real-password\n")]
    public void ParseAndZero_Failure_ZeroesInput(string csv)
    {
        var file = Encoding.UTF8.GetBytes(csv);

        Assert.Throws<VaultImportException>(() => VaultCsv.ParseAndZero(file, Now));

        Assert.All(file, b => Assert.Equal(0, b));
    }

    [Fact]
    public void ParseAndZero_InvalidEncoding_ZeroesInput()
    {
        byte[] file = [.. Encoding.UTF8.GetBytes(Header + "Example,"), 0xFF];

        Assert.Throws<VaultImportException>(() => VaultCsv.ParseAndZero(file, Now));

        Assert.All(file, b => Assert.Equal(0, b));
    }

    [Fact]
    public void ParseAndZero_Null_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => VaultCsv.ParseAndZero(null!, Now));
    }
}
