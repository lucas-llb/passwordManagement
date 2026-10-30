using System.Diagnostics;
using System.Globalization;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using PasswordManager.App.E2E.Tests.Localization;

namespace PasswordManager.App.E2E.Tests;

// The entry list's live search (title, username, URL and folder; case- and accent-insensitive) and its two filters
// (favorites only, folder) on the real app (W-19): on the seeded vault, and on a 2,000-entry vault built through Core that
// proves the list stays virtualized and responsive. Every step waits on the UI, never on a fixed sleep; every value is
// fictitious.
public sealed class EntryListSearchE2ETests(ITestOutputHelper output)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(100);

    // The budgets of the 2,000-entry test, polled fast so the measured time is close to the real one.
    private static readonly TimeSpan UnlockBudget = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StepBudget = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan FastPoll = TimeSpan.FromMilliseconds(20);

    private static string[] SeededTitles => [.. SeededVault.Entries.Select(e => e.GetProperty("title").GetString()!)];

    [Fact]
    public void Search_MatchesTitleUsernameUrlAndFolderOnly()
    {
        using var app = AppSession.Launch(SeededVault.Seed);
        Unlock(app, SeededVault.Password);
        var titles = SeededTitles;

        // The control: every entry is listed before the first query.
        WaitTitles(app, titles);

        (string Query, string[] Expected)[] cases =
        [
            ("luz", [titles[0]]), // title
            ("FAMILIA@", [titles[1]]), // username, folded
            ("example.jp", [titles[2]]), // URL
            ("国际", [titles[2]]), // folder
            ("sao joao", [titles[0]]), // two terms, accents folded
            ("example", [titles[0], titles[1], titles[2]]), // content order
        ];
        foreach (var (query, expected) in cases)
        {
            Search(app, query);
            WaitTitles(app, expected);
            Assert.Null(app.Find("EntriesNoMatches"));
        }

        // A password and a note are never searched.
        foreach (var query in new[] { "Pão-de-açúcar", "Vencimento" })
        {
            Search(app, query);
            WaitTitles(app);
            app.WaitFor("EntriesNoMatches");
            Assert.Null(app.Find("EntriesEmpty"));
        }

        Search(app, string.Empty);
        WaitTitles(app, titles);
        app.WaitGone("EntriesNoMatches");
    }

    [Fact]
    public void Filters_FavoritesAndFolder_CombineWithSearch()
    {
        using var app = AppSession.Launch(SeededVault.Seed);
        Unlock(app, SeededVault.Password);
        var titles = SeededTitles;
        WaitTitles(app, titles);

        string[] folders =
        [
            .. SeededVault.Entries
                .Select(e => e.GetProperty("folder").GetString()!)
                .Where(f => f.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.CurrentCulture),
        ];
        var allFolders = AppResources.Strings["EntriesFolderAll"];
        Assert.Equal([allFolders, .. folders], app.OptionNames("EntriesFolder"));

        app.Toggle("EntriesFavoritesOnly");
        WaitTitles(app, titles[0], titles[2]);

        app.SelectOption("EntriesFolder", SeededVault.Entries[0].GetProperty("folder").GetString()!);
        WaitTitles(app, titles[0]);

        Search(app, "東京");
        WaitTitles(app);
        app.WaitFor("EntriesNoMatches");

        app.SelectOption("EntriesFolder", allFolders);
        WaitTitles(app, titles[2]);
        app.WaitGone("EntriesNoMatches");

        app.Toggle("EntriesFavoritesOnly");
        Search(app, string.Empty);
        WaitTitles(app, titles);
    }

    [Fact]
    public void Filter_SurvivesTheDetailPage_AndLockClearsIt()
    {
        using var app = AppSession.Launch(SeededVault.Seed);
        Unlock(app, SeededVault.Password);
        var titles = SeededTitles;

        var folder = SeededVault.Entries[2].GetProperty("folder").GetString()!;
        Search(app, "東京");
        app.Toggle("EntriesFavoritesOnly");
        app.SelectOption("EntriesFolder", folder);
        var item = Assert.Single(WaitTitlesItems(app, titles[2]));
        item.Patterns.Invoke.Pattern.Invoke();
        app.WaitFor("DetailTitle");
        Assert.Equal(titles[2], app.ValueOf("DetailTitle"));

        app.Invoke("DetailBack");
        app.WaitGone("DetailTitle");
        WaitTitles(app, titles[2]);
        Assert.Equal("東京", app.ValueOf("EntriesSearch"));
        Assert.True(app.IsToggled("EntriesFavoritesOnly"));
        Assert.Equal(folder, app.SelectedOption("EntriesFolder"));

        app.Invoke("EntriesLock");
        app.WaitFor("UnlockPassword");
        app.WaitGone("EntriesList");
        Unlock(app, SeededVault.Password);

        WaitTitles(app, titles);
        Assert.Equal(string.Empty, app.ValueOf("EntriesSearch"));
        Assert.False(app.IsToggled("EntriesFavoritesOnly"));
        Assert.Equal(AppResources.Strings["EntriesFolderAll"], app.SelectedOption("EntriesFolder"));
    }

    [Fact]
    public void Search_RealKeystrokes_FilterAsYouType()
    {
        using var app = AppSession.Launch(SeededVault.Seed);
        Unlock(app, SeededVault.Password);
        var titles = SeededTitles;
        app.WaitFocus($"EntriesList[{titles[0]}]");

        app.PressKey(VirtualKeyShort.TAB);
        app.WaitFocus("EntriesNew");
        app.PressKey(VirtualKeyShort.TAB);
        app.WaitFocus("EntriesLock");
        app.PressKey(VirtualKeyShort.TAB);
        app.WaitFocus("EntriesSearch");

        foreach (var key in new[] { VirtualKeyShort.KEY_L, VirtualKeyShort.KEY_U, VirtualKeyShort.KEY_Z })
        {
            app.PressKey(key);
        }

        WaitTitles(app, titles[0]);

        for (var i = 0; i < 3; i++)
        {
            app.PressKey(VirtualKeyShort.BACK);
        }

        WaitTitles(app, titles);
        Assert.Equal(string.Empty, app.ValueOf("EntriesSearch"));

        app.PressKey(VirtualKeyShort.TAB);
        app.WaitFocus("EntriesFavoritesOnly");
        app.PressKey(VirtualKeyShort.SPACE);
        WaitTitles(app, titles[0], titles[2]);
        app.PressKey(VirtualKeyShort.SPACE);
        WaitTitles(app, titles);
    }

    [Fact]
    public void LargeVault_TwoThousandEntries_StaysResponsive()
    {
        using var app = AppSession.Launch(SeededVault.SeedLarge);
        Assert.Single(app.Items("ProfilesList")).Patterns.Invoke.Pattern.Invoke();
        app.TypePassword(SeededVault.LargePassword);
        var first = SeededVault.LargeTitle(1);

        // 1. Unlock to the first realized item.
        Measure(
            "unlock to first item",
            UnlockBudget,
            () => app.Invoke("UnlockButton"),
            () => FirstItemName(app) == first);

        // 2. Only a window of the 2,000 items is realized: the list is virtualized.
        var realized = app.Items("EntriesList").Length;
        output.WriteLine($"realized items: {realized} of {SeededVault.LargeCount}");
        Assert.InRange(realized, 1, SeededVault.LargeCount - 1);

        // 3. Keyboard jumps to both ends.
        app.WaitFocus(Item(first));
        var last = Item(SeededVault.LargeTitle(SeededVault.LargeCount));
        Measure("END", StepBudget, () => app.PressKey(VirtualKeyShort.END), () => FocusKey(app) == last);
        app.PressKey(VirtualKeyShort.HOME);
        app.WaitFocus(Item(first));

        // 4. A search narrows 2,000 entries to one.
        var target = SeededVault.LargeTitle(1234);
        Measure(
            "search \"Entrada 1234\"",
            StepBudget,
            () => app.SetValue("EntriesSearch", target),
            () => Names(app).SequenceEqual([target]));

        // 5. Clearing it lists everything again.
        Measure(
            "clear search",
            StepBudget,
            () => app.SetValue("EntriesSearch", string.Empty),
            () => FirstItemName(app) == first);

        // 6. Favorites only: i % 7 == 0.
        Measure(
            "favorites only",
            StepBudget,
            () => app.Toggle("EntriesFavoritesOnly"),
            () => FirstItemName(app) == SeededVault.LargeTitle(7));
        AssertEveryIndex(app, i => i % 7 == 0);

        // 7. And folder "Pasta 3": i % 7 == 0 and i % 5 == 3, so i % 35 == 28.
        Measure(
            "folder \"Pasta 3\"",
            StepBudget,
            () => app.SelectOption("EntriesFolder", "Pasta 3"),
            () => FirstItemName(app) == SeededVault.LargeTitle(28));
        AssertEveryIndex(app, i => i % 35 == 28);

        // 8. Entry 1234 is neither a favorite nor in that folder.
        Measure(
            "search with filters, no match",
            StepBudget,
            () => app.SetValue("EntriesSearch", target),
            () => app.Find("EntriesNoMatches") is not null);
        Assert.Empty(app.Items("EntriesList"));
    }

    // Runs the action and polls the condition fast; the elapsed time (written to the test output) must fit the budget.
    private void Measure(string step, TimeSpan budget, Action action, Func<bool> done)
    {
        var watch = Stopwatch.StartNew();
        action();
        var reached = Retry.WhileFalse(done, Timeout, FastPoll, ignoreException: true).Success;
        watch.Stop();
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{step}: {watch.Elapsed.TotalMilliseconds:F0} ms"));
        Assert.True(reached, $"Step '{step}' did not finish within {Timeout}.");
        Assert.True(watch.Elapsed <= budget, $"Step '{step}' took {watch.Elapsed}, over the budget of {budget}.");
    }

    // Opens the only profile (from the profile list, or the Unlock page it leads to) and waits for the entry list.
    private static void Unlock(AppSession app, string password)
    {
        if (app.Find("UnlockPassword") is null)
        {
            Assert.Single(app.Items("ProfilesList")).Patterns.Invoke.Pattern.Invoke();
        }

        app.TypePassword(password);
        app.Invoke("UnlockButton");
        app.WaitFor("EntriesList");
        app.WaitGone("UnlockPassword");
    }

    // Sets the query through the text box's ValuePattern and waits until the box shows it; the filter runs as the text
    // changes, so the list that follows belongs to this query.
    private static void Search(AppSession app, string query)
    {
        app.SetValue("EntriesSearch", query);
        var shown = Retry.WhileFalse(() => app.ValueOf("EntriesSearch") == query, Timeout, Poll).Success;
        Assert.True(shown, "The search box does not show the query.");
    }

    // Each item container is named with its entry's title (EntryListPage.OnEntryContainerContentChanging).
    private static string[] Names(AppSession app) => [.. app.Items("EntriesList").Select(i => i.Name)];

    private static string? FirstItemName(AppSession app) =>
        app.Find("EntriesList")?.FindFirstChild(cf => cf.ByControlType(ControlType.ListItem))?.Name;

    private static void WaitTitles(AppSession app, params string[] expected) => WaitTitlesItems(app, expected);

    // The items once the list shows exactly these titles, in this order.
    private static AutomationElement[] WaitTitlesItems(AppSession app, params string[] expected)
    {
        Retry.WhileFalse(() => Names(app).SequenceEqual(expected), Timeout, Poll, ignoreException: true);
        var items = app.Items("EntriesList");
        Assert.Equal(expected, items.Select(i => i.Name));
        return items;
    }

    private static string Item(string title) => $"EntriesList[{title}]";

    private static string FocusKey(AppSession app) => AppSession.FocusKey(app.Focused());

    // Every realized item of the large vault ("Entrada NNNN") satisfies the filter.
    private static void AssertEveryIndex(AppSession app, Func<int, bool> expected)
    {
        var names = Names(app);
        Assert.NotEmpty(names);
        Assert.All(names, name =>
        {
            Assert.StartsWith("Entrada ", name, StringComparison.Ordinal);
            Assert.True(expected(int.Parse(name["Entrada ".Length..], NumberStyles.None, CultureInfo.InvariantCulture)), name);
        });
    }
}
