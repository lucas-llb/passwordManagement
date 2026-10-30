using System.Collections;
using System.Diagnostics;
using PasswordManager.Core.Profiles;
using PasswordManager.Core.Vault;
using PasswordManager.ViewModels.Navigation;

namespace PasswordManager.ViewModels.Tests;

/// <summary>How a save or a delete in the detail rebuilds the list: entries, search index, folders and filters.</summary>
public sealed class EntryEditTests : IDisposable
{
    private readonly TestProfiles _profiles = new();
    private readonly NavigationService _navigation = new();

    private readonly VaultEntry _light = TestProfiles.Entry("Conta de luz", "luz@example.com") with
    {
        Folder = "Finanças",
        Password = "senha-ficticia-1",
    };

    private readonly VaultEntry _family = TestProfiles.Entry("Família", "casa@example.com") with
    {
        Folder = "Casa",
        Password = "senha-ficticia-2",
    };

    private readonly VaultEntry _note = TestProfiles.Entry("Nota segura", "") with
    {
        Folder = "",
        Password = "",
    };

    private Profile? _profile;

    public void Dispose() => _profiles.Dispose();

    private EntryListViewModel OpenList(params VaultEntry[] entries)
    {
        _profile = _profiles.CreateProfile("Alice", TestProfiles.Password, entries);
        var session = _profiles.OpenSession(_profile);
        var list = new EntryListViewModel(
            _profile,
            session,
            _profiles.Repository,
            _navigation,
            _profiles.Throttle,
            _profiles.Time);
        _navigation.ResetTo(list);

        return list;
    }

    private EntryDetailViewModel Open(EntryListViewModel list, VaultEntry entry)
    {
        list.OpenEntryCommand.Execute(entry);
        return Assert.IsType<EntryDetailViewModel>(_navigation.Current);
    }

    private static string[] Titles(EntryListViewModel list) => [.. list.FilteredEntries.Select(e => e.Title)];

    [Fact]
    public void Save_NewTitle_IsFoundAndTheOldTitleIsNot()
    {
        var list = OpenList(_light, _family, _note);
        var detail = Open(list, _family);

        detail.Title = "Residência";
        detail.SaveCommand.Execute(null);

        list.SearchText = "residencia";
        Assert.Equal(["Residência"], Titles(list));

        list.SearchText = "familia";
        Assert.Empty(list.FilteredEntries);
        Assert.True(list.HasNoMatches);
    }

    [Fact]
    public void Save_WithAnActiveSearch_AppliesItAgain()
    {
        var list = OpenList(_light, _family, _note);
        list.SearchText = "conta";
        var detail = Open(list, _light);

        detail.Title = "Energia";
        detail.SaveCommand.Execute(null);

        Assert.Equal("conta", list.SearchText);
        Assert.Empty(list.FilteredEntries);
    }

    [Fact]
    public void Save_FolderMoved_FoldersGainTheNewAndDropTheEmptyOne()
    {
        var list = OpenList(_light, _family, _note);
        Assert.Equal(["Casa", "Finanças"], list.Folders);
        var raised = new List<string?>();
        list.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        var detail = Open(list, _light);

        detail.Folder = "Contas";
        detail.SaveCommand.Execute(null);

        Assert.Equal(["Casa", "Contas"], list.Folders);
        Assert.Contains(nameof(EntryListViewModel.Folders), raised);
        Assert.Contains(nameof(EntryListViewModel.Entries), raised);
    }

    [Fact]
    public void Save_SelectedFolderEmptied_SelectedFolderBecomesNull()
    {
        var list = OpenList(_light, _family, _note);
        list.SelectedFolder = "Finanças";
        Assert.Equal(["Conta de luz"], Titles(list));
        var detail = Open(list, _light);

        detail.Folder = string.Empty;
        detail.SaveCommand.Execute(null);

        Assert.Null(list.SelectedFolder);
        Assert.Equal(["Conta de luz", "Família", "Nota segura"], Titles(list));
    }

    [Fact]
    public void Save_SelectedFolderStillUsed_IsKept()
    {
        var list = OpenList(_light, _family, _note);
        list.SelectedFolder = "Casa";
        var detail = Open(list, _light);

        detail.Folder = "Casa";
        detail.SaveCommand.Execute(null);

        Assert.Equal("Casa", list.SelectedFolder);
        Assert.Equal(["Conta de luz", "Família"], Titles(list));
    }

    [Fact]
    public void Save_FilteredEntries_StayANonGenericList()
    {
        var list = OpenList(_light, _family, _note);
        var detail = Open(list, _family);

        detail.Title = "Residência";
        detail.SaveCommand.Execute(null);
        Assert.IsAssignableFrom<IList>(list.FilteredEntries);

        list.SearchText = "resid";
        Assert.IsAssignableFrom<IList>(list.FilteredEntries);
    }

    [Fact]
    public void Save_EntriesKeepTheirOrder()
    {
        var list = OpenList(_light, _family, _note);
        var detail = Open(list, _family);

        detail.Title = "Residência";
        detail.SaveCommand.Execute(null);

        Assert.Equal(["Conta de luz", "Residência", "Nota segura"], list.Entries.Select(e => e.Title));
        Assert.Same(detail.Entry, list.Entries[1]);
    }

    [Fact]
    public void LastOpened_IsTheOpenedEntryThenTheSavedRecord()
    {
        var list = OpenList(_light, _family, _note);
        Assert.Null(list.LastOpened);
        var detail = Open(list, _family);
        Assert.Same(_family, list.LastOpened);

        detail.Title = "Residência";
        detail.SaveCommand.Execute(null);

        Assert.NotSame(_family, list.LastOpened);
        Assert.Same(detail.Entry, list.LastOpened);
        Assert.Contains(list.LastOpened!, list.FilteredEntries);
    }

    [Fact]
    public void LastOpened_NewEntrySaved_IsTheNewRecord()
    {
        var list = OpenList(_light);
        list.NewEntryCommand.Execute(null);
        var detail = Assert.IsType<EntryDetailViewModel>(_navigation.Current);

        detail.Title = "Novo";
        detail.SaveCommand.Execute(null);

        Assert.Same(detail.Entry, list.LastOpened);
        Assert.Same(detail.Entry, list.Entries[^1]);
    }

    [Fact]
    public void LastOpened_AfterDelete_IsNull()
    {
        var list = OpenList(_light, _family);
        var detail = Open(list, _family);

        detail.RequestDeleteCommand.Execute(null);
        detail.ConfirmDeleteCommand.Execute(null);

        Assert.Null(list.LastOpened);
        Assert.Equal([_light], list.Entries);
        Assert.Equal(["Finanças"], list.Folders);
    }

    [Fact]
    public void LastOpened_AfterLock_IsNull()
    {
        var list = OpenList(_light);
        Open(list, _light);

        list.LockCommand.Execute(null);

        Assert.Null(list.LastOpened);
    }

    [Fact]
    public void Save_AfterLock_LeavesTheListLocked()
    {
        var list = OpenList(_light, _family);
        var detail = Open(list, _family);
        detail.Title = "Residência";

        list.LockCommand.Execute(null);
        detail.SaveCommand.Execute(null);

        Assert.False(list.IsOpen);
        Assert.Empty(list.Entries);
        Assert.Empty(list.Folders);
        Assert.Empty(list.FilteredEntries);
        Assert.Null(list.LastOpened);
    }

    [Fact]
    public void Save_TwoThousandEntries_StaysWithinBudget()
    {
        const int count = 2000;
        var entries = Enumerable.Range(1, count)
            .Select(i => TestProfiles.Entry($"Entrada {i:D4}", $"usuario{i:D4}@example.com") with
            {
                Url = $"https://site{i:D4}.example.com",
                Folder = $"Pasta {i % 5}",
                Password = "senha-ficticia-grande",
                Notes = "anotacao-ficticia-grande",
            })
            .ToArray();
        var list = OpenList(entries);

        // One untimed save warms up the JIT, so that the timed save measures the write and the rebuild.
        var warmUp = Open(list, list.Entries[0]);
        warmUp.Title = "Aquecimento";
        warmUp.SaveCommand.Execute(null);
        warmUp.GoBackCommand.Execute(null);

        var detail = Open(list, list.Entries[1234]);
        detail.Title = "Editada sem par";

        var watch = Stopwatch.StartNew();
        detail.SaveCommand.Execute(null);
        watch.Stop();

        Assert.True(watch.ElapsedMilliseconds <= 1000, $"The save took {watch.ElapsedMilliseconds} ms (budget 1000 ms).");
        Assert.False(detail.IsDirty);
        Assert.Equal(count, list.Entries.Count);

        list.SearchText = "editada sem par";
        Assert.Equal(["Editada sem par"], Titles(list));
    }
}
