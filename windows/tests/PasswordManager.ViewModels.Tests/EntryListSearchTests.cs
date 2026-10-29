using System.Collections;
using System.Diagnostics;
using System.Globalization;
using PasswordManager.Core.Profiles;
using PasswordManager.Core.Vault;
using PasswordManager.ViewModels.Navigation;

namespace PasswordManager.ViewModels.Tests;

public sealed class EntryListSearchTests : IDisposable
{
    private readonly TestProfiles _profiles = new();
    private readonly NavigationService _navigation = new();

    // Fictitious entries that mirror the shape of the unicode test vector; each one has a distinct trait.
    private readonly VaultEntry _light = TestProfiles.Entry("Conta de luz — São João", "joao.sao@example.com") with
    {
        Url = "https://energia.example.com",
        Folder = "Finanças",
        Favorite = true,
        Password = "Pão-de-açúcar-ficticio",
        Notes = "Vencimento todo dia 10",
    };

    private readonly VaultEntry _family = TestProfiles.Entry("Família 👨‍👩‍👧 🏠", "familia@example.com") with
    {
        Url = "",
        Folder = "Casa 🏡",
        Password = "senha-ficticia-2",
        Notes = "anotacao-ficticia",
    };

    private readonly VaultEntry _tokyo = TestProfiles.Entry("東京アカウント", "") with
    {
        Url = "example.jp",
        Folder = "国际",
        Favorite = true,
        Password = "senha-ficticia-3",
        Notes = "",
    };

    private readonly VaultEntry _note = TestProfiles.Entry("Nota segura", "") with
    {
        Url = "",
        Folder = "",
        Password = "senha-ficticia-4",
        Notes = "texto-secreto-ficticio",
    };

    public void Dispose() => _profiles.Dispose();

    // The list as the app builds it after an unlock: the session is open and the list is the root page.
    private EntryListViewModel OpenList(params VaultEntry[] entries)
    {
        var profile = _profiles.CreateProfile("Alice", TestProfiles.Password, entries);
        var session = _profiles.OpenSession(profile);
        var list = new EntryListViewModel(profile, session, _profiles.Repository, _navigation, _profiles.Throttle);
        _navigation.ResetTo(list);

        return list;
    }

    private EntryListViewModel OpenFourEntries() => OpenList(_light, _family, _tokyo, _note);

    private static string[] Titles(EntryListViewModel list) => [.. list.FilteredEntries.Select(e => e.Title)];

    [Fact]
    public void Defaults_NoQueryNoFilters_FilteredEntriesAreTheEntriesInOrder()
    {
        var list = OpenFourEntries();

        Assert.Equal(string.Empty, list.SearchText);
        Assert.False(list.FavoritesOnly);
        Assert.Null(list.SelectedFolder);
        Assert.Equal(list.Entries, list.FilteredEntries);
        Assert.Equal([_light, _family, _tokyo, _note], list.FilteredEntries);
        Assert.False(list.HasNoMatches);
    }

    [Fact]
    public void FilteredEntries_IsANonGenericIList_BeforeAndAfterAFilter()
    {
        var list = OpenFourEntries();
        Assert.IsAssignableFrom<IList>(list.FilteredEntries);

        list.SearchText = "luz";
        Assert.IsAssignableFrom<IList>(list.FilteredEntries);
        Assert.Single(list.FilteredEntries);

        list.SearchText = "sem-resultado";
        Assert.IsAssignableFrom<IList>(list.FilteredEntries);
        Assert.Empty(list.FilteredEntries);

        list.SearchText = string.Empty;
        Assert.IsAssignableFrom<IList>(list.FilteredEntries);
        Assert.Equal(4, list.FilteredEntries.Count);
    }

    [Fact]
    public void FilteredEntries_EmptyVault_IsAnEmptyIList()
    {
        var list = OpenList();

        Assert.IsAssignableFrom<IList>(list.FilteredEntries);
        Assert.Empty(list.FilteredEntries);
    }

    [Theory]
    [InlineData("luz", "Conta de luz — São João")] // title
    [InlineData("familia@", "Família 👨‍👩‍👧 🏠")] // username
    [InlineData("example.jp", "東京アカウント")] // URL
    [InlineData("国际", "東京アカウント")] // folder
    [InlineData("casa 🏡", "Família 👨‍👩‍👧 🏠")] // folder (a term per word, both found)
    [InlineData("東京", "東京アカウント")]
    public void Search_EachSearchedField_MatchesOnItsOwn(string query, string expectedTitle)
    {
        var list = OpenFourEntries();

        list.SearchText = query;

        Assert.Equal([expectedTitle], Titles(list));
    }

    [Theory]
    [InlineData("SAO JOAO")]
    [InlineData("sao joao")]
    [InlineData("São João")]
    [InlineData("SÃO JOÃO")]
    [InlineData("financas")]
    [InlineData("FINANÇAS")]
    [InlineData("  luz   SAO ")]
    public void Search_IsCaseAndAccentInsensitive(string query)
    {
        var list = OpenFourEntries();

        list.SearchText = query;

        Assert.Equal([_light.Title], Titles(list));
    }

    [Fact]
    public void Search_DecomposedAccentInTheQueryMatchesAPrecomposedField()
    {
        var list = OpenFourEntries();

        list.SearchText = "São João";

        Assert.Equal([_light.Title], Titles(list));
    }

    [Theory]
    [InlineData("Pão-de-açúcar-ficticio")] // the password of an entry
    [InlineData("pao-de-acucar")]
    [InlineData("Vencimento")] // the notes of an entry
    [InlineData("texto-secreto-ficticio")]
    [InlineData("senha-ficticia")]
    public void Search_PasswordsAndNotes_AreNeverMatched(string query)
    {
        var list = OpenFourEntries();

        list.SearchText = query;

        Assert.Empty(list.FilteredEntries);
        Assert.True(list.HasNoMatches);
    }

    [Fact]
    public void Search_TheIdOfAnEntry_IsNeverMatched()
    {
        var list = OpenFourEntries();

        list.SearchText = _light.Id.ToString();

        Assert.Empty(list.FilteredEntries);
    }

    [Fact]
    public void Search_Results_KeepContentOrder()
    {
        var list = OpenFourEntries();

        // "example" is in the username or the URL of the first three entries, but not of the last one.
        list.SearchText = "example";

        Assert.Equal([_light, _family, _tokyo], list.FilteredEntries);
    }

    [Fact]
    public void Search_MultipleTerms_AreAndedAndCanMatchDifferentFields()
    {
        var list = OpenFourEntries();

        list.SearchText = "conta financas energia"; // title + folder + URL of the same entry
        Assert.Equal([_light.Title], Titles(list));

        list.SearchText = "conta familia"; // two terms that never meet in one entry
        Assert.Empty(list.FilteredEntries);

        list.SearchText = "example com"; // term order does not matter
        Assert.Equal([_light, _family], list.FilteredEntries);
    }

    [Fact]
    public void Search_ATermNeverSpansTwoFields()
    {
        var first = TestProfiles.Entry("ab", "cd") with { Url = "", Folder = "" };
        var list = OpenList(first);

        list.SearchText = "bc";

        Assert.Empty(list.FilteredEntries);
        Assert.True(list.HasNoMatches);
    }

    [Fact]
    public void Search_BlankQuery_ListsEverything()
    {
        var list = OpenFourEntries();
        list.SearchText = "luz";

        list.SearchText = "   ";

        Assert.Equal(4, list.FilteredEntries.Count);
        Assert.False(list.HasNoMatches);
    }

    [Fact]
    public void SearchText_Null_BecomesAnEmptyString()
    {
        var list = OpenFourEntries();
        list.SearchText = "luz";

        list.SearchText = null!;

        Assert.Equal(string.Empty, list.SearchText);
        Assert.Equal(4, list.FilteredEntries.Count);
    }

    [Fact]
    public void FavoritesOnly_KeepsOnlyFavorites_InContentOrder()
    {
        var list = OpenFourEntries();

        list.FavoritesOnly = true;
        Assert.Equal([_light, _tokyo], list.FilteredEntries);

        list.FavoritesOnly = false;
        Assert.Equal(4, list.FilteredEntries.Count);
    }

    [Fact]
    public void SelectedFolder_UsesOrdinalEquality_AndExcludesEntriesWithoutAFolder()
    {
        var upper = TestProfiles.Entry("Maiúscula") with { Folder = "Work" };
        var lower = TestProfiles.Entry("Minúscula") with { Folder = "work" };
        var none = TestProfiles.Entry("Sem pasta") with { Folder = "" };
        var list = OpenList(upper, lower, none);

        list.SelectedFolder = "Work";
        Assert.Equal([upper], list.FilteredEntries);

        list.SelectedFolder = "work";
        Assert.Equal([lower], list.FilteredEntries);

        list.SelectedFolder = "WORK";
        Assert.Empty(list.FilteredEntries);

        list.SelectedFolder = null;
        Assert.Equal([upper, lower, none], list.FilteredEntries);
    }

    [Fact]
    public void AllThreeFilters_Combine()
    {
        var list = OpenFourEntries();

        list.FavoritesOnly = true;
        Assert.Equal([_light, _tokyo], list.FilteredEntries);

        list.SelectedFolder = "Finanças";
        Assert.Equal([_light], list.FilteredEntries);

        list.SearchText = "東京";
        Assert.Empty(list.FilteredEntries);
        Assert.True(list.HasNoMatches);

        list.SelectedFolder = null;
        Assert.Equal([_tokyo], list.FilteredEntries);

        list.FavoritesOnly = false;
        list.SearchText = string.Empty;
        Assert.Equal(4, list.FilteredEntries.Count);
        Assert.False(list.HasNoMatches);
    }

    [Fact]
    public void Folders_AreDistinctNonEmptyAndSortedForTheCurrentCulture()
    {
        string[] folders = ["Work", "banco", "", "Casa", "Work", "work", "Árvore", ""];
        var entries = folders
            .Select((folder, i) => TestProfiles.Entry($"Entrada {i}") with { Folder = folder })
            .ToArray();
        var list = OpenList(entries);

        string[] distinct = ["Work", "banco", "Casa", "work", "Árvore"];
        Assert.Equal(distinct.Order(StringComparer.CurrentCulture), list.Folders);
        Assert.Equal(5, list.Folders.Count);
        Assert.DoesNotContain(string.Empty, list.Folders);
        Assert.Equal(list.Folders.Count, list.Folders.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Folders_SeededShapedVault_HasTheThreeFolders()
    {
        var list = OpenFourEntries();

        string[] expected = ["Finanças", "Casa 🏡", "国际"];
        Assert.Equal(expected.Order(StringComparer.CurrentCulture), list.Folders);
    }

    [Fact]
    public void Folders_NoFolderAtAll_IsEmpty()
    {
        var list = OpenList(_note);

        Assert.Empty(list.Folders);
    }

    [Fact]
    public void HasNoMatches_NonEmptyVaultWithZeroResults_IsTrue()
    {
        var list = OpenFourEntries();

        list.SearchText = "sem-resultado";

        Assert.True(list.HasNoMatches);
    }

    [Fact]
    public void HasNoMatches_EmptyVault_IsFalseEvenWithAQuery()
    {
        var list = OpenList();

        list.SearchText = "qualquer coisa";
        list.FavoritesOnly = true;

        Assert.True(list.IsEmpty);
        Assert.Empty(list.FilteredEntries);
        Assert.False(list.HasNoMatches);
    }

    [Fact]
    public void Filtering_RaisesPropertyChangedForFilteredEntriesAndHasNoMatches()
    {
        var list = OpenFourEntries();
        var changed = new List<string?>();
        list.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        list.SearchText = "sem-resultado";

        Assert.Contains(nameof(EntryListViewModel.SearchText), changed);
        Assert.Contains(nameof(EntryListViewModel.FilteredEntries), changed);
        Assert.Contains(nameof(EntryListViewModel.HasNoMatches), changed);

        list.SearchText = string.Empty;
        changed.Clear();
        list.FavoritesOnly = true;
        Assert.Contains(nameof(EntryListViewModel.FavoritesOnly), changed);
        Assert.Contains(nameof(EntryListViewModel.FilteredEntries), changed);

        changed.Clear();
        list.SelectedFolder = "Finanças";
        Assert.Contains(nameof(EntryListViewModel.SelectedFolder), changed);
        Assert.Contains(nameof(EntryListViewModel.FilteredEntries), changed);
        Assert.Contains(nameof(EntryListViewModel.HasNoMatches), changed);
    }

    [Fact]
    public void OpenEntry_ThenGoBack_ReturnsToTheSameListWithTheFilterIntact()
    {
        var list = OpenFourEntries();
        list.SearchText = "東京";
        list.FavoritesOnly = true;
        list.SelectedFolder = "国际";
        var filtered = list.FilteredEntries;

        list.OpenEntryCommand.Execute(_tokyo);
        Assert.IsType<EntryDetailViewModel>(_navigation.Current);
        Assert.True(_navigation.GoBack());

        Assert.Same(list, _navigation.Current);
        Assert.Equal("東京", list.SearchText);
        Assert.True(list.FavoritesOnly);
        Assert.Equal("国际", list.SelectedFolder);
        Assert.Same(filtered, list.FilteredEntries);
        Assert.Equal([_tokyo], list.FilteredEntries);
    }

    [Fact]
    public void Lock_ClearsTheFilterStateAndTheFilteredEntries()
    {
        var list = OpenFourEntries();
        list.SearchText = "東京";
        list.FavoritesOnly = true;
        list.SelectedFolder = "国际";

        list.LockCommand.Execute(null);

        Assert.Empty(list.FilteredEntries);
        Assert.IsAssignableFrom<IList>(list.FilteredEntries);
        Assert.Empty(list.Folders);
        Assert.Empty(list.Entries);
        Assert.Equal(string.Empty, list.SearchText);
        Assert.False(list.FavoritesOnly);
        Assert.Null(list.SelectedFolder);
        Assert.False(list.HasNoMatches);
    }

    [Fact]
    public void Lock_ThenChangingTheFilter_DoesNotThrowAndNeverTouchesTheSession()
    {
        var list = OpenFourEntries();
        list.LockCommand.Execute(null);

        var exception = Record.Exception(() =>
        {
            list.SearchText = "luz";
            list.FavoritesOnly = true;
            list.SelectedFolder = "Finanças";
            list.SearchText = string.Empty;
            list.FavoritesOnly = false;
            list.SelectedFolder = null;
        });

        Assert.Null(exception);
        Assert.Empty(list.FilteredEntries);
        Assert.False(list.HasNoMatches);
    }

    [Fact]
    public void Filter_TwoThousandEntries_StaysWithinBudget()
    {
        const int count = 2000;
        var entries = Enumerable.Range(1, count)
            .Select(i => TestProfiles.Entry($"Entrada {i:D4}", $"usuario{i:D4}@example.com") with
            {
                Url = $"https://site{i:D4}.example.com",
                Folder = $"Pasta {i % 5}",
                Favorite = i % 7 == 0,
                Password = "senha-ficticia-grande",
                Notes = "anotacao-ficticia-grande",
            })
            .ToArray();
        var profile = _profiles.CreateProfile("Alice", TestProfiles.Password, entries);
        var session = _profiles.OpenSession(profile);

        var watch = Stopwatch.StartNew();
        var list = new EntryListViewModel(profile, session, _profiles.Repository, _navigation, _profiles.Throttle);
        watch.Stop();
        _navigation.ResetTo(list);
        Assert.True(
            watch.ElapsedMilliseconds <= 250,
            $"Constructing the list took {watch.ElapsedMilliseconds} ms (budget 250 ms).");
        Assert.Equal(count, list.FilteredEntries.Count);

        // One untimed pass warms up the JIT, so that the timed pass measures the filter and not the compiler.
        Replay(list, entries, timed: false);
        Replay(list, entries, timed: true);
    }

    // Typing "entrada 1234" one character at a time, then favorites, a folder, and clearing everything.
    private static void Replay(EntryListViewModel list, VaultEntry[] entries, bool timed)
    {
        var updates = new List<(string Name, Action Apply)>();
        const string typed = "entrada 1234";
        for (var length = 1; length <= typed.Length; length++)
        {
            var query = typed[..length];
            updates.Add(($"type '{query}'", () => list.SearchText = query));
        }

        updates.Add(("favorites on", () => list.FavoritesOnly = true));
        updates.Add(("favorites off", () => list.FavoritesOnly = false));
        updates.Add(("clear the search", () => list.SearchText = string.Empty));
        updates.Add(("folder Pasta 3", () => list.SelectedFolder = "Pasta 3"));
        updates.Add(("favorites on again", () => list.FavoritesOnly = true));
        updates.Add(("clear the folder", () => list.SelectedFolder = null));
        updates.Add(("clear everything", () => list.FavoritesOnly = false));

        foreach (var (name, apply) in updates)
        {
            var watch = Stopwatch.StartNew();
            apply();
            watch.Stop();

            if (timed)
            {
                Assert.True(
                    watch.ElapsedMilliseconds <= 50,
                    $"Update '{name}' took {watch.ElapsedMilliseconds} ms (budget 50 ms).");
            }

            Assert.Equal(Oracle(entries, list.SearchText, list.FavoritesOnly, list.SelectedFolder), list.FilteredEntries);
        }
    }

    // The brute-force answer: culture-aware, accent- and case-insensitive, with no folded index.
    private static VaultEntry[] Oracle(VaultEntry[] entries, string query, bool favoritesOnly, string? folder)
    {
        var terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var compare = CultureInfo.InvariantCulture.CompareInfo;
        const CompareOptions options = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;

        return
        [
            .. entries.Where(entry =>
            {
                if (favoritesOnly && !entry.Favorite)
                {
                    return false;
                }

                if (folder is not null && !string.Equals(entry.Folder, folder, StringComparison.Ordinal))
                {
                    return false;
                }

                string[] fields = [entry.Title, entry.Username, entry.Url, entry.Folder];

                return terms.All(term => fields.Any(field => compare.IndexOf(field, term, options) >= 0));
            }),
        ];
    }
}
