using System.Text;
using System.Text.Json;
using PasswordManager.Core.Tests.TestVectors;
using PasswordManager.Core.Vault;

namespace PasswordManager.Core.Tests.Vault;

public sealed class VaultCsvTests : IDisposable
{
    private const string Header = "name,url,username,password,notes,folder\r\n";

    private static readonly VaultEntry SampleEntry = new()
    {
        Id = Guid.Parse("0b6f7c1e-2f5d-4a6b-9a8e-3c1d2e4f5a6b"),
        Title = "Example",
        Username = "user@example.com",
        Password = "not-a-real-password",
        Url = "https://example.com",
        Notes = "notes",
        Folder = "Personal",
        Favorite = true,
        CreatedAt = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero),
        UpdatedAt = new DateTimeOffset(2026, 1, 2, 12, 0, 0, TimeSpan.Zero),
    };

    private static readonly VaultEntry EmptyEntry = new()
    {
        Id = Guid.Parse("7d1c2b3a-4e5f-4a6b-8c9d-0e1f2a3b4c5d"),
        Title = "",
        CreatedAt = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero),
        UpdatedAt = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero),
    };

    private static readonly byte[] SampleCsv = Encoding.UTF8.GetBytes("abc");

    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("pm-vaultcsv-");
    private readonly string _path;

    public VaultCsvTests()
    {
        _path = Path.Combine(_directory.FullName, "export.csv");
    }

    public void Dispose()
    {
        _directory.Delete(recursive: true);
    }

    private static string SerializeToString(params VaultEntry[] entries)
    {
        var bytes = VaultCsv.Serialize(entries);

        // Strict decoding, so an invalid sequence fails the test instead of becoming U+FFFD.
        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
    }

    private static string RecordOf(VaultEntry entry) => SerializeToString(entry)[Header.Length..];

    // --- Serialize ---

    [Fact]
    public void Serialize_NoEntries_IsHeaderLineOnly()
    {
        var bytes = VaultCsv.Serialize([]);

        Assert.Equal("name,url,username,password,notes,folder\r\n"u8.ToArray(), bytes);
        Assert.Equal(VaultCsv.HeaderLine + "\r\n", Header);
        Assert.Equal(".csv", VaultCsv.FileExtension);
    }

    [Fact]
    public void Serialize_PlainEntry_WritesColumnsInOrderUnquoted()
    {
        var other = SampleEntry with
        {
            Title = "Other",
            Username = "other-user",
            Password = "another-fake-password",
            Url = "https://example.org",
            Notes = "more notes",
            Folder = "Work",
            Favorite = false,
        };

        var csv = SerializeToString(SampleEntry, other);

        Assert.Equal(
            "name,url,username,password,notes,folder\r\n" +
            "Example,https://example.com,user@example.com,not-a-real-password,notes,Personal\r\n" +
            "Other,https://example.org,other-user,another-fake-password,more notes,Work\r\n",
            csv);
        Assert.DoesNotContain("True", csv, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("2026", csv, StringComparison.Ordinal);
        Assert.DoesNotContain(SampleEntry.Id.ToString(), csv, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Serialize_FieldWithComma_IsQuoted()
    {
        var entry = EmptyEntry with { Title = "Bank, savings", Password = ",", Folder = "a,b,c" };

        Assert.Equal("\"Bank, savings\",,,\",\",,\"a,b,c\"\r\n", RecordOf(entry));
    }

    [Fact]
    public void Serialize_FieldWithQuote_DoublesItAndQuotes()
    {
        var entry = EmptyEntry with { Title = "a\"b", Password = "\"", Notes = "say \"hi\"" };

        Assert.Equal("\"a\"\"b\",,,\"\"\"\",\"say \"\"hi\"\"\",\r\n", RecordOf(entry));
    }

    [Theory]
    [InlineData("line 1\nline 2", "\"line 1\nline 2\"")]
    [InlineData("line 1\r\nline 2", "\"line 1\r\nline 2\"")]
    [InlineData("line 1\rline 2", "\"line 1\rline 2\"")]
    [InlineData("\n", "\"\n\"")]
    public void Serialize_FieldWithLineBreak_IsQuotedAndKeptVerbatim(string notes, string expected)
    {
        var entry = EmptyEntry with { Title = "Note", Notes = notes };

        Assert.Equal($"Note,,,,{expected},\r\n", RecordOf(entry));
    }

    [Fact]
    public void Serialize_EmptyFields_StayEmpty()
    {
        Assert.Equal(",,,,,\r\n", RecordOf(EmptyEntry));
        Assert.Equal("Only title,,,,,\r\n", RecordOf(EmptyEntry with { Title = "Only title" }));
        Assert.Equal(",,,,,Folder\r\n", RecordOf(EmptyEntry with { Folder = "Folder" }));
    }

    [Fact]
    public void Serialize_TabsSpacesAndFormulaPrefixes_AreVerbatim()
    {
        var entry = EmptyEntry with
        {
            Title = "=1+1",
            Url = "+x",
            Username = "-x",
            Password = "@x",
            Notes = " leading and trailing ",
            Folder = "\ttab\t",
        };

        Assert.Equal("=1+1,+x,-x,@x, leading and trailing ,\ttab\t\r\n", RecordOf(entry));
    }

    [Fact]
    public void Serialize_Unicode_IsUtf8WithoutBomAndNotNormalized()
    {
        const string Family = "\U0001F468‍\U0001F469‍\U0001F467";
        const string Nfd = "Café";
        var entry = EmptyEntry with { Title = Family, Notes = Nfd };

        var bytes = VaultCsv.Serialize([entry]);

        Assert.False(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble), "The CSV must not start with a BOM.");
        Assert.Equal(Encoding.UTF8.GetBytes(Header + Family + ",,,," + Nfd + ",\r\n"), bytes);
        Assert.False(Nfd.IsNormalized(NormalizationForm.FormC));
        Assert.True(bytes.AsSpan().IndexOf("Café"u8) >= 0, "NFD must stay NFD.");
        Assert.True(bytes.AsSpan().IndexOf("Café"u8) < 0, "NFD must not be composed to NFC.");
    }

    [Fact]
    public void Serialize_EntriesUnicodeVector_MatchesExpectedCsv()
    {
        var manifest = TestVectorFiles.ReadManifest("entries-unicode");
        var entries = manifest.Content.Deserialize(VaultJsonContext.Default.VaultContent)?.Entries
            ?? throw new JsonException("Manifest entries-unicode.json has no content.");

        var csv = SerializeToString([.. entries]);

        Assert.Equal(
            "name,url,username,password,notes,folder\r\n" +
            "Conta de luz — São João,https://example.com/serviços/ação," +
            "joão.conceição@example.com,Pão-de-açúcar-ÁÉÍÓÚ-ãõ-ç," +
            "Vencimento todo dia 10; é fictício.,Finanças\r\n" +
            "Família \U0001F468‍\U0001F469‍\U0001F467 \U0001F3E0,https://example.org/família," +
            "familia@example.com,senha-\U0001F468‍\U0001F469‍\U0001F467-\U0001F511-fictícia,," +
            "Casa \U0001F3E1\r\n" +
            "東京アカウント,https://example.jp/ログイン,用户名," +
            "密码-סיסמה-كلمة," +
            "שלום עולם — مرحبا " +
            "بالعالم — 你好，世界,国际\r\n" +
            "Nota segura,,,,\"linha 1\nlinha 2\tcom tab\n\"\"aspas\"\" e barra \\ invertida\nCafé em NFD\",\r\n",
            csv);
    }

    [Fact]
    public void Serialize_LoneSurrogate_ThrowsArgumentException()
    {
        // Built in code: attribute arguments are stored as UTF-8 metadata, which cannot hold a lone surrogate.
        string[] passwords = ["\uD83D", "x\uDC68y", "tail\uD83D", "\uDC68\uD83D"];

        Assert.All(passwords, password =>
        {
            var entry = EmptyEntry with { Title = "Lone surrogate", Password = password };

            Assert.ThrowsAny<ArgumentException>(() => VaultCsv.Serialize([entry]));
        });
    }

    [Fact]
    public void Serialize_Null_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => VaultCsv.Serialize(null!));
    }

    // --- WriteFile ---

    [Fact]
    public void WriteFile_NewFile_WritesExactBytesWithoutTmpOrBak()
    {
        var csv = VaultCsv.Serialize([SampleEntry]);

        VaultCsv.WriteFile(_path, csv);

        Assert.Equal(csv, File.ReadAllBytes(_path));
        Assert.Equal([_path], Directory.GetFileSystemEntries(_directory.FullName));
    }

    [Fact]
    public void WriteFile_ExistingFile_IsReplacedWithoutBak()
    {
        File.WriteAllBytes(_path, SampleCsv);
        var csv = VaultCsv.Serialize([SampleEntry]);

        VaultCsv.WriteFile(_path, csv);

        Assert.Equal(csv, File.ReadAllBytes(_path));
        Assert.Equal([_path], Directory.GetFileSystemEntries(_directory.FullName));
    }

    [Fact]
    public void WriteFile_MissingDirectory_ThrowsAndCreatesNothing()
    {
        var missing = Path.Combine(_directory.FullName, "missing");
        var path = Path.Combine(missing, "export.csv");

        Assert.Throws<DirectoryNotFoundException>(() => VaultCsv.WriteFile(path, SampleCsv));

        Assert.False(Directory.Exists(missing));
        Assert.Empty(Directory.GetFileSystemEntries(_directory.FullName));
    }

    [Fact]
    public void WriteFile_TargetLocked_ThrowsAndKeepsOriginal()
    {
        File.WriteAllBytes(_path, SampleCsv);

        using (new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            // Windows reports a sharing violation on the rename as access denied, so either documented exception is accepted.
            var exception = Record.Exception(() => VaultCsv.WriteFile(_path, VaultCsv.Serialize([SampleEntry])));

            Assert.True(exception is IOException or UnauthorizedAccessException, $"Unexpected exception: {exception}");
        }

        Assert.Equal(SampleCsv, File.ReadAllBytes(_path));
        Assert.Equal([_path], Directory.GetFileSystemEntries(_directory.FullName));
    }

    [Fact]
    public void WriteFile_EmptyPath_Throws()
    {
        Assert.Throws<ArgumentException>(() => VaultCsv.WriteFile("", SampleCsv));
        Assert.Throws<ArgumentNullException>(() => VaultCsv.WriteFile(null!, SampleCsv));
        Assert.Empty(Directory.GetFileSystemEntries(_directory.FullName));
    }
}
