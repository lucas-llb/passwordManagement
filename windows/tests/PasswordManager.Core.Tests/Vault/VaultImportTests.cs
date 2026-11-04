using System.Text;
using PasswordManager.Core.Vault;

namespace PasswordManager.Core.Tests.Vault;

public sealed class VaultImportTests : IDisposable
{
    private static readonly DateTimeOffset Created = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Updated = new(2026, 1, 2, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ImportedAt = new(2026, 3, 4, 5, 6, 7, 123, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = ImportedAt.AddTicks(4567);

    private static readonly VaultEntry Saved = new()
    {
        Id = Guid.Parse("0b6f7c1e-2f5d-4a6b-9a8e-3c1d2e4f5a6b"),
        Title = "Example",
        Username = "user@example.com",
        Password = "not-a-real-password",
        Url = "https://example.com",
        Notes = "saved notes",
        Folder = "Personal",
        Favorite = false,
        CreatedAt = Created,
        UpdatedAt = Updated,
    };

    // The same key as Saved under another id, as a CSV row would be.
    private static readonly VaultEntry SameKey = Saved with
    {
        Id = Guid.Parse("7d1c2b3a-4e5f-4a6b-8c9d-0e1f2a3b4c5d"),
        Password = "another-fake-password",
        Notes = "imported notes",
        Folder = "Imported",
        CreatedAt = ImportedAt,
        UpdatedAt = ImportedAt,
    };

    private static readonly VaultEntry NewEntry = SameKey with
    {
        Id = Guid.Parse("11111111-2222-4333-8444-555555555555"),
        Title = "New one",
    };

    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("pm-vaultimport-");
    private readonly string _path;

    public VaultImportTests()
    {
        _path = Path.Combine(_directory.FullName, "import.csv");
    }

    public void Dispose()
    {
        _directory.Delete(recursive: true);
    }

    private static VaultEntry WithNewId(VaultEntry entry) => entry with { Id = Guid.NewGuid() };

    private void CreateSparseFile(long length)
    {
        using var stream = new FileStream(_path, FileMode.Create, FileAccess.Write);
        stream.Write("marker"u8);
        stream.SetLength(length);
    }

    // --- ReadFile ---

    [Fact]
    public void ReadFile_AtLimit_Reads()
    {
        CreateSparseFile(VaultImport.MaxFileBytes);

        var bytes = VaultImport.ReadFile(_path);

        Assert.Equal(VaultImport.MaxFileBytes, bytes.Length);
        Assert.Equal("marker"u8.ToArray(), bytes[..6]);
    }

    [Fact]
    public void ReadFile_OverLimit_IsTooLarge()
    {
        CreateSparseFile(VaultImport.MaxFileBytes + 1L);

        var exception = Assert.Throws<VaultImportException>(() => VaultImport.ReadFile(_path));

        Assert.Equal(ImportFileError.TooLarge, exception.Error);
    }

    [Fact]
    public void ReadFile_Missing_ThrowsIOException()
    {
        Assert.ThrowsAny<IOException>(() => VaultImport.ReadFile(_path));
    }

    [Fact]
    public void ReadFile_OtherWriterHoldsFile_ThrowsIOException()
    {
        using var writer = new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);

        Assert.ThrowsAny<IOException>(() => VaultImport.ReadFile(_path));
    }

    [Fact]
    public void ReadFile_EmptyPath_Throws()
    {
        Assert.Throws<ArgumentException>(() => VaultImport.ReadFile(""));
        Assert.Throws<ArgumentNullException>(() => VaultImport.ReadFile(null!));
    }

    // --- FromContent ---

    [Fact]
    public void FromContent_KeepsIdDatesFavorite()
    {
        var entry = Saved with { Favorite = true };

        var file = VaultImport.FromContent(new VaultContent { Entries = [entry] });

        Assert.Equal(entry, Assert.Single(file.Entries));
        Assert.Empty(file.RejectedLines);
        Assert.Empty(file.IgnoredColumns);
    }

    [Fact]
    public void FromContent_MissingTitleOrLongField_RejectsByPosition()
    {
        var tooLong = new string('x', VaultImport.MaxFieldChars + 1);
        var atLimit = new string('x', VaultImport.MaxFieldChars);

        var file = VaultImport.FromContent(new VaultContent
        {
            Entries =
            [
                Saved with { Title = "" },
                Saved with { Title = "   " },
                Saved with { Password = tooLong },
                Saved with { Title = tooLong },
                Saved with { Username = tooLong },
                Saved with { Url = tooLong },
                Saved with { Notes = tooLong },
                Saved with { Folder = tooLong },
                Saved with { Notes = atLimit },
            ],
        });

        Assert.Equal(atLimit, Assert.Single(file.Entries).Notes);
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8], file.RejectedLines);
    }

    [Fact]
    public void FromContent_EntryWithEveryFieldEmpty_IsSkippedSilently()
    {
        var blank = new VaultEntry { Id = Guid.NewGuid(), Title = "", CreatedAt = Created, UpdatedAt = Updated };

        var file = VaultImport.FromContent(new VaultContent { Entries = [blank, Saved] });

        Assert.Equal(Saved, Assert.Single(file.Entries));
        Assert.Empty(file.RejectedLines);
    }

    [Fact]
    public void FromContent_TooMany_Throws()
    {
        var atLimit = Enumerable.Repeat(Saved, VaultImport.MaxEntries).ToArray();

        Assert.Equal(VaultImport.MaxEntries, VaultImport.FromContent(new VaultContent { Entries = atLimit }).Entries.Count);

        var exception = Assert.Throws<VaultImportException>(
            () => VaultImport.FromContent(new VaultContent { Entries = [.. atLimit, Saved] }));
        Assert.Equal(ImportFileError.TooManyEntries, exception.Error);
    }

    // --- Plan ---

    [Fact]
    public void Plan_SameKey_IsDuplicate()
    {
        var plan = VaultImport.Plan([Saved], [SameKey with { Password = Saved.Password }, NewEntry]);

        Assert.Equal([true, false], plan.IsDuplicate);
        Assert.Equal(1, plan.DuplicateCount);
        Assert.Equal(1, plan.NewCount);
    }

    [Fact]
    public void Plan_DifferentPasswordSameKey_IsDuplicate()
    {
        Assert.Equal([true], VaultImport.Plan([Saved], [SameKey]).IsDuplicate);
    }

    [Theory]
    [InlineData("EXAMPLE", "user@example.com", "https://example.com")]
    [InlineData("Example", "USER@example.com", "https://example.com")]
    [InlineData("Example", "user@example.com", "HTTPS://example.com")]
    [InlineData(" Example", "user@example.com", "https://example.com")]
    [InlineData("Example", "user@example.com ", "https://example.com")]
    public void Plan_DifferentCase_IsNew(string title, string username, string url)
    {
        var incoming = SameKey with { Title = title, Username = username, Url = url };

        var plan = VaultImport.Plan([Saved], [incoming]);

        Assert.Equal([false], plan.IsDuplicate);
        Assert.Equal(1, plan.NewCount);
        Assert.Equal(0, plan.DuplicateCount);
    }

    [Fact]
    public void Plan_NfdAndNfc_IsDuplicate()
    {
        var nfc = "Café";
        var nfd = nfc.Normalize(NormalizationForm.FormD);
        Assert.NotEqual(nfc, nfd);

        var plan = VaultImport.Plan([Saved with { Title = nfc }], [SameKey with { Title = nfd }]);

        Assert.Equal([true], plan.IsDuplicate);
    }

    [Fact]
    public void Plan_LoneSurrogate_ComparesRaw()
    {
        var lone = Saved with { Title = "x\uD83D" };

        Assert.Equal([true], VaultImport.Plan([lone], [SameKey with { Title = "x\uD83D" }]).IsDuplicate);
        Assert.Equal([false], VaultImport.Plan([lone], [SameKey with { Title = "y\uD83D" }]).IsDuplicate);
    }

    [Fact]
    public void Plan_SameIdOtherTitle_IsDuplicate()
    {
        var plan = VaultImport.Plan([Saved], [Saved with { Title = "Renamed", Username = "other", Url = "" }]);

        Assert.Equal([true], plan.IsDuplicate);
    }

    [Fact]
    public void Plan_TwoRowsOneVaultEntry_SecondIsNew()
    {
        var plan = VaultImport.Plan([Saved], [SameKey, WithNewId(SameKey)]);

        Assert.Equal([true, false], plan.IsDuplicate);
        Assert.Equal(1, plan.DuplicateCount);
        Assert.Equal(1, plan.NewCount);
    }

    [Fact]
    public void Plan_TwoVaultEntriesSameKey_EachMatchesOneRow()
    {
        var other = WithNewId(Saved);

        var plan = VaultImport.Plan([Saved, other], [SameKey, WithNewId(SameKey), WithNewId(SameKey)]);

        Assert.Equal([true, true, false], plan.IsDuplicate);
    }

    [Fact]
    public void Plan_IdInLaterRow_WinsOverKeyInEarlierRow()
    {
        var byKeyFirst = WithNewId(SameKey);
        var byIdLater = Saved with { Title = "Renamed" };

        var plan = VaultImport.Plan([Saved], [byKeyFirst, byIdLater]);

        Assert.Equal([false, true], plan.IsDuplicate);
    }

    // --- Merge ---

    [Fact]
    public void Merge_Skip_KeepsVaultEntryAndAddsNew()
    {
        var result = VaultImport.Merge([Saved], [SameKey, NewEntry], DuplicatePolicy.Skip, Now);

        Assert.Equal([Saved, NewEntry], result.Entries);
    }

    [Fact]
    public void Merge_Duplicate_AppendsWithUniqueIds()
    {
        var sameId = Saved with { Title = "Renamed" };

        var result = VaultImport.Merge([Saved], [sameId, SameKey, SameKey], DuplicatePolicy.Duplicate, Now);

        Assert.Equal(4, result.Entries.Length);
        Assert.Equal(Saved, result.Entries[0]);
        Assert.Equal(4, result.Entries.Select(entry => entry.Id).Distinct().Count());
        Assert.Equal(sameId with { Id = result.Entries[1].Id }, result.Entries[1]);
        Assert.NotEqual(Saved.Id, result.Entries[1].Id);
        Assert.Equal(SameKey, result.Entries[2]);
        Assert.Equal(SameKey with { Id = result.Entries[3].Id }, result.Entries[3]);
        Assert.NotEqual(SameKey.Id, result.Entries[3].Id);
    }

    [Fact]
    public void Merge_Replace_KeepsIdAndCreatedAtInPlace()
    {
        var first = NewEntry with { Id = Guid.NewGuid(), Title = "First" };
        var last = NewEntry with { Id = Guid.NewGuid(), Title = "Last" };

        var result = VaultImport.Merge([first, Saved, last], [SameKey], DuplicatePolicy.Replace, Now);

        Assert.Equal(3, result.Entries.Length);
        Assert.Equal(first, result.Entries[0]);
        Assert.Equal(last, result.Entries[2]);
        var replaced = result.Entries[1];
        Assert.Equal(Saved.Id, replaced.Id);
        Assert.Equal(Saved.CreatedAt, replaced.CreatedAt);
        Assert.Equal(SameKey.Password, replaced.Password);
        Assert.Equal(SameKey.Notes, replaced.Notes);
        Assert.Equal(SameKey.Folder, replaced.Folder);
    }

    [Fact]
    public void Merge_Replace_EmptyFieldDoesNotErase()
    {
        var sparse = Saved with { Username = "", Password = "", Url = "", Notes = "", Folder = "", Title = "Renamed" };

        var result = VaultImport.Merge([Saved], [sparse], DuplicatePolicy.Replace, Now);

        var replaced = Assert.Single(result.Entries);
        Assert.Equal("Renamed", replaced.Title);
        Assert.Equal(Saved.Username, replaced.Username);
        Assert.Equal(Saved.Password, replaced.Password);
        Assert.Equal(Saved.Url, replaced.Url);
        Assert.Equal(Saved.Notes, replaced.Notes);
        Assert.Equal(Saved.Folder, replaced.Folder);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void Merge_Replace_FavoriteIsOred(bool saved, bool imported, bool expected)
    {
        var result = VaultImport.Merge(
            [Saved with { Favorite = saved }],
            [SameKey with { Favorite = imported }],
            DuplicatePolicy.Replace,
            Now);

        Assert.Equal(expected, Assert.Single(result.Entries).Favorite);
    }

    [Fact]
    public void Merge_Replace_SetsUpdatedAtTruncated()
    {
        var result = VaultImport.Merge([Saved], [SameKey], DuplicatePolicy.Replace, Now);

        Assert.Equal(ImportedAt, Assert.Single(result.Entries).UpdatedAt);
    }

    [Theory]
    [InlineData(DuplicatePolicy.Skip)]
    [InlineData(DuplicatePolicy.Duplicate)]
    [InlineData(DuplicatePolicy.Replace)]
    public void Merge_NewEntries_AppendInFileOrder(DuplicatePolicy policy)
    {
        var second = NewEntry with { Id = Guid.NewGuid(), Title = "Second" };
        var third = NewEntry with { Id = Guid.NewGuid(), Title = "Third" };

        var result = VaultImport.Merge([Saved], [NewEntry, second, third], policy, Now);

        Assert.Equal([Saved, NewEntry, second, third], result.Entries);
    }

    [Theory]
    [InlineData(DuplicatePolicy.Skip, 1, 0, 2)]
    [InlineData(DuplicatePolicy.Duplicate, 3, 0, 0)]
    [InlineData(DuplicatePolicy.Replace, 1, 2, 0)]
    public void Merge_ReportsCounts(DuplicatePolicy policy, int added, int replaced, int skipped)
    {
        var other = NewEntry with { Id = Guid.NewGuid(), Title = "Other", Username = "other" };

        var result = VaultImport.Merge(
            [Saved, other],
            [SameKey, NewEntry, other with { Id = Guid.NewGuid() }],
            policy,
            Now);

        Assert.Equal(added, result.Added);
        Assert.Equal(replaced, result.Replaced);
        Assert.Equal(skipped, result.Skipped);
        Assert.Equal(2 + added, result.Entries.Length);
    }

    [Fact]
    public void Merge_UndefinedPolicy_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => VaultImport.Merge([Saved], [SameKey], (DuplicatePolicy)99, Now));
    }

    [Theory]
    [InlineData(DuplicatePolicy.Skip)]
    [InlineData(DuplicatePolicy.Replace)]
    public void Merge_ImportTwice_IsStable(DuplicatePolicy policy)
    {
        VaultEntry[] Rows() => [WithNewId(SameKey), WithNewId(SameKey), WithNewId(NewEntry)];

        var first = VaultImport.Merge([Saved], Rows(), policy, Now);
        var second = VaultImport.Merge(first.Entries, Rows(), policy, Now);

        Assert.Equal(2, first.Added);
        Assert.Equal(0, second.Added);
        Assert.Equal(first.Entries.Length, second.Entries.Length);
        Assert.Equal(first.Entries.Select(entry => entry.Id), second.Entries.Select(entry => entry.Id));
    }

    [Fact]
    public void Merge_DoesNotChangeInputLists()
    {
        VaultEntry[] existing = [Saved];
        VaultEntry[] incoming = [SameKey];

        VaultImport.Merge(existing, incoming, DuplicatePolicy.Replace, Now);

        Assert.Equal(Saved, existing[0]);
        Assert.Equal(SameKey, incoming[0]);
    }
}
