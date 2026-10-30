using System.Globalization;
using System.Text;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;
using PasswordManager.App.E2E.Tests.Localization;
using PasswordManager.Core.Vault;

namespace PasswordManager.App.E2E.Tests;

// The detail page as the entry editor (W-20), on the real app over the seeded vault: the hidden password, editing and
// saving every field, a new entry, the multi-line notes kept with \n, the Back gate on unsaved changes, the delete
// confirmation, a save that cannot be written, and a lock while editing or confirming. Every save is checked on disk by
// reopening the vault through Core. Every step waits on the UI, never on a fixed sleep; every value is fictitious.
public sealed class EntryEditE2ETests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(100);

    private static string[] SeededTitles => [.. SeededVault.Entries.Select(e => e.GetProperty("title").GetString()!)];

    [Fact]
    public void Password_HiddenByDefault_ShownOnToggle()
    {
        using var app = AppSession.Launch(SeededVault.Seed);
        Unlock(app);
        OpenEntry(app, 0);

        // Only the password box is in the tree, and it is never read: the password is nowhere in the window.
        app.WaitFor("DetailPassword");
        Assert.Null(app.Find("DetailPasswordText"));
        Assert.DoesNotContain("password #1", app.VisibleEntryFields());

        app.Toggle("DetailShowPassword");
        app.WaitFor("DetailPasswordText");
        Assert.Equal(Seeded(0, "password"), app.ValueOf("DetailPasswordText"));
        Assert.True(app.IsToggled("DetailShowPassword"));

        app.Toggle("DetailShowPassword");
        app.WaitGone("DetailPasswordText");
        app.WaitFor("DetailPassword");
        Assert.DoesNotContain("password #1", app.VisibleEntryFields());
    }

    [Fact]
    public void Edit_EveryField_SavesAtomicallyAndKeepsCreatedAt()
    {
        using var app = AppSession.Launch(SeededVault.Seed);
        Unlock(app);
        OpenEntry(app, 0);
        var createdText = app.WaitFor("DetailCreatedAt").Name;
        var updatedText = app.WaitFor("DetailUpdatedAt").Name;

        // The app stores UpdatedAt truncated to whole milliseconds, so the window starts at the millisecond.
        var start = TruncateToMilliseconds(DateTimeOffset.UtcNow);
        const string title = "Conta editada fictícia";
        const string username = "usuario.editado@example.com";
        const string password = "Senha-editada-fictícia-123";
        const string url = "https://example.net/editado";
        const string notes = "Nota editada fictícia\nsegunda linha";
        const string folder = "Pasta editada";
        app.SetValue("DetailTitle", title);
        app.SetValue("DetailUsername", username);
        app.SetValue("DetailPassword", password);
        app.SetValue("DetailUrl", url);
        app.SetValue("DetailFolder", folder);
        app.SetValue("DetailNotes", notes);
        Assert.True(app.IsToggled("DetailFavorite"));
        app.Toggle("DetailFavorite");
        Assert.False(app.IsToggled("DetailFavorite"));

        app.WaitEnabled("DetailSave");
        app.Invoke("DetailSave");
        app.WaitEnabled("DetailSave", expected: false);
        var end = DateTimeOffset.UtcNow;

        // On disk: same Id and CreatedAt, a fresh UpdatedAt, every edited field (notes with \n), the rest untouched.
        var entries = SeededVault.ReadContent(app.VaultsDirectory, SeededVault.Password).Entries;
        Assert.Equal(SeededVault.Entries.Length, entries.Count);
        var saved = entries[0];
        Assert.Equal(Guid.Parse(Seeded(0, "id")), saved.Id);
        Assert.Equal(SeededDate(0, "createdAt"), saved.CreatedAt);
        Assert.InRange(saved.UpdatedAt, start, end);
        Assert.Equal(title, saved.Title);
        Assert.Equal(username, saved.Username);
        Assert.Equal(password, saved.Password);
        Assert.Equal(url, saved.Url);
        Assert.Equal(notes, saved.Notes);
        Assert.Equal(folder, saved.Folder);
        Assert.False(saved.Favorite);
        for (var index = 1; index < entries.Count; index++)
        {
            AssertSeeded(index, entries[index]);
        }

        // The previous version is the .bak copy, and it still opens.
        var backup = SeededVault.ReadFile(SeededVault.VaultPath(app.VaultsDirectory) + VaultFile.BackupSuffix, SeededVault.Password);
        Assert.Equal(SeededVault.Entries.Length, backup.Entries.Count);
        AssertSeeded(0, backup.Entries[0]);

        // The page shows the same dates as the disk: CreatedAt unchanged, UpdatedAt the new one.
        Assert.Equal(createdText, app.WaitFor("DetailCreatedAt").Name);
        app.WaitName("DetailUpdatedAt", AppResources.Format("DetailUpdatedAt", saved.UpdatedAt.LocalDateTime));
        Assert.NotEqual(updatedText, app.WaitFor("DetailUpdatedAt").Name);

        // The list is rebuilt: the new title in place, the emptied folder gone, the new one listed, the search updated.
        GoBack(app);
        WaitTitles(app, [title, .. SeededTitles[1..]]);
        var folders = app.OptionNames("EntriesFolder");
        Assert.DoesNotContain(Seeded(0, "folder"), folders);
        Assert.Contains(folder, folders);
        app.SetValue("EntriesSearch", title);
        WaitTitles(app, title);
    }

    [Fact]
    public void NewEntry_SavesAndAppearsAtTheEnd()
    {
        using var app = AppSession.Launch(SeededVault.Seed);
        Unlock(app);
        app.Invoke("EntriesNew");
        app.WaitFor("DetailTitle");
        app.WaitGone("EntriesList");

        // An empty draft: the title is asked for, nothing can be saved, and there are no dates and no delete.
        app.WaitFor("DetailTitleRequired");
        Assert.False(app.IsEnabled("DetailSave"));
        Assert.Null(app.Find("DetailCreatedAt"));
        Assert.Null(app.Find("DetailUpdatedAt"));
        Assert.Null(app.Find("DetailDelete"));

        const string title = "Entrada nova fictícia";
        app.SetValue("DetailTitle", title);
        app.SetValue("DetailUsername", "nova@example.com");
        app.SetValue("DetailPassword", "Senha-nova-fictícia-456");
        app.SetValue("DetailUrl", "https://example.com/nova");
        app.SetValue("DetailFolder", "Pasta nova");
        app.SetValue("DetailNotes", "Nota nova fictícia");
        app.WaitGone("DetailTitleRequired");
        app.WaitEnabled("DetailSave");
        app.Invoke("DetailSave");
        app.WaitEnabled("DetailSave", expected: false);

        // On disk: one more entry, at the end, created and updated at the same instant; the seeded ones untouched.
        var entries = SeededVault.ReadContent(app.VaultsDirectory, SeededVault.Password).Entries;
        Assert.Equal(SeededVault.Entries.Length + 1, entries.Count);
        for (var index = 0; index < SeededVault.Entries.Length; index++)
        {
            AssertSeeded(index, entries[index]);
        }

        var created = entries[^1];
        Assert.Equal(title, created.Title);
        Assert.Equal("nova@example.com", created.Username);
        Assert.Equal("Senha-nova-fictícia-456", created.Password);
        Assert.Equal("https://example.com/nova", created.Url);
        Assert.Equal("Pasta nova", created.Folder);
        Assert.Equal("Nota nova fictícia", created.Notes);
        Assert.False(created.Favorite);
        Assert.NotEqual(Guid.Empty, created.Id);
        Assert.Equal(created.CreatedAt, created.UpdatedAt);

        // The page is now that saved entry: both dates (the same instant) and the delete button.
        app.WaitName("DetailCreatedAt", AppResources.Format("DetailCreatedAt", created.CreatedAt.LocalDateTime));
        app.WaitName("DetailUpdatedAt", AppResources.Format("DetailUpdatedAt", created.UpdatedAt.LocalDateTime));
        app.WaitFor("DetailDelete");

        GoBack(app);
        WaitTitles(app, [.. SeededTitles, title]);
    }

    [Fact]
    public void MultilineNotes_OpenClean_SaveKeepsLf()
    {
        using var app = AppSession.Launch(SeededVault.Seed);
        Unlock(app);
        OpenEntry(app, 3);

        // The notes box holds the seeded notes (as \r, which a text box uses), yet the draft is clean.
        var seededNotes = Seeded(3, "notes");
        Assert.Contains('\n', seededNotes);
        Assert.Equal(seededNotes.ReplaceLineEndings("\n"), app.ValueOf("DetailNotes").ReplaceLineEndings("\n"));
        Assert.False(app.IsEnabled("DetailSave"));
        Assert.True(app.IsEnabled("DetailBack"));

        app.SetValue("DetailTitle", "Nota segura editada");
        app.WaitEnabled("DetailSave");
        app.Invoke("DetailSave");
        app.WaitEnabled("DetailSave", expected: false);

        var saved = SeededVault.ReadContent(app.VaultsDirectory, SeededVault.Password).Entries[3];
        Assert.Equal("Nota segura editada", saved.Title);
        Assert.Equal(Encoding.UTF8.GetBytes(seededNotes), Encoding.UTF8.GetBytes(saved.Notes));
    }

    [Fact]
    public void UnsavedChanges_BlockBack_UntilDiscarded()
    {
        using var app = AppSession.Launch(SeededVault.Seed);
        var seedBytes = File.ReadAllBytes(Path.Combine(AppSession.Metadata("TestVectorsDir"), "entries-unicode.pmvault"));
        Unlock(app);
        OpenEntry(app, 0);
        app.WaitEnabled("DetailBack");

        app.SetValue("DetailTitle", "Título alterado fictício");
        app.WaitEnabled("DetailBack", expected: false);
        app.WaitEnabled("DetailDiscard");

        app.Invoke("DetailDiscard");
        app.WaitEnabled("DetailBack");
        Assert.Equal(SeededTitles[0], app.ValueOf("DetailTitle"));
        Assert.False(app.IsEnabled("DetailSave"));
        Assert.False(app.IsEnabled("DetailDiscard"));

        // Nothing was written: the vault is the seed byte for byte, and no .bak was made.
        var vaultPath = SeededVault.VaultPath(app.VaultsDirectory);
        Assert.Equal(seedBytes, File.ReadAllBytes(vaultPath));
        Assert.False(File.Exists(vaultPath + VaultFile.BackupSuffix));

        GoBack(app);
        WaitTitles(app, SeededTitles);
    }

    [Fact]
    public void Delete_ConfirmsThenRemoves()
    {
        using var app = AppSession.Launch(SeededVault.Seed);
        Unlock(app);
        var id = Guid.Parse(Seeded(1, "id"));
        OpenEntry(app, 1);

        // Cancel: the dialog closes and the entry stays, on the page and on disk.
        app.Invoke("DetailDelete");
        app.WaitDialog("DetailDeleteDialog");
        app.InvokeInDialog("DetailDeleteDialog", "CloseButton");
        app.WaitDialogGone("DetailDeleteDialog");
        Assert.Equal(SeededTitles[1], app.ValueOf("DetailTitle"));
        Assert.Contains(SeededVault.ReadContent(app.VaultsDirectory, SeededVault.Password).Entries, e => e.Id == id);

        // Confirm: back on the list without it, and gone from disk.
        app.Invoke("DetailDelete");
        app.WaitDialog("DetailDeleteDialog");
        app.InvokeInDialog("DetailDeleteDialog", "PrimaryButton");
        app.WaitFor("EntriesList");
        app.WaitGone("DetailTitle");
        WaitTitles(app, SeededTitles[0], SeededTitles[2], SeededTitles[3]);

        var entries = SeededVault.ReadContent(app.VaultsDirectory, SeededVault.Password).Entries;
        Assert.Equal(SeededVault.Entries.Length - 1, entries.Count);
        Assert.DoesNotContain(entries, e => e.Id == id);
        AssertSeeded(0, entries[0]);
        AssertSeeded(2, entries[1]);
        AssertSeeded(3, entries[2]);
        Assert.DoesNotContain(app.VisibleEntryFields(), label => label.EndsWith(" #2", StringComparison.Ordinal));
    }

    [Fact]
    public void SaveFailure_ShowsErrorAndKeepsTheDraft()
    {
        using var app = AppSession.Launch(SeededVault.Seed);
        Unlock(app);
        OpenEntry(app, 0);
        var vaultPath = SeededVault.VaultPath(app.VaultsDirectory);
        var before = File.ReadAllBytes(vaultPath);
        const string title = "Conta não gravada fictícia";

        // While another handle holds the vault with no sharing, the atomic replace cannot go through.
        using (new FileStream(vaultPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            app.SetValue("DetailTitle", title);
            app.WaitEnabled("DetailSave");
            app.Invoke("DetailSave");
            app.WaitFor("DetailSaveError");
            Assert.True(app.IsEnabled("DetailSave"));
            Assert.False(app.IsEnabled("DetailBack"));
            Assert.Equal(title, app.ValueOf("DetailTitle"));
        }

        Assert.Equal(before, File.ReadAllBytes(vaultPath));

        // Once the file is free, the same draft saves and the error goes away.
        app.Invoke("DetailSave");
        app.WaitGone("DetailSaveError");
        app.WaitEnabled("DetailSave", expected: false);
        app.WaitEnabled("DetailBack");
        Assert.Equal(title, SeededVault.ReadContent(app.VaultsDirectory, SeededVault.Password).Entries[0].Title);
    }

    [Fact]
    public void Lock_WhileEditingOrConfirming_DropsEverything()
    {
        using var app = AppSession.Launch(SeededVault.Seed);
        Unlock(app);
        var vaultPath = SeededVault.VaultPath(app.VaultsDirectory);
        var before = File.ReadAllBytes(vaultPath);

        // 1. A lock with unsaved edits drops the draft: nothing of it, or of the vault, is left, and nothing is written.
        const string draftTitle = "Rascunho não salvo fictício";
        OpenEntry(app, 0);
        app.SetValue("DetailTitle", draftTitle);
        app.WaitEnabled("DetailBack", expected: false);
        app.PostSessionChange(NativeMethods.WTS_SESSION_LOCK);
        app.WaitFor("UnlockPassword");
        app.WaitGone("DetailTitle");
        app.AssertNoEntryVisible();
        Assert.DoesNotContain(app.Window.FindAllDescendants(), e => ShowsText(e, draftTitle));
        Assert.Equal(before, File.ReadAllBytes(vaultPath));

        // 2. A lock while the delete confirmation is open closes it, as a cancel: the entry stays.
        Unlock(app);
        WaitTitles(app, SeededTitles);
        OpenEntry(app, 1);
        app.Invoke("DetailDelete");
        app.WaitDialog("DetailDeleteDialog");
        app.PostSessionChange(NativeMethods.WTS_SESSION_LOCK);
        app.WaitDialogGone("DetailDeleteDialog");
        app.WaitFor("UnlockPassword");
        app.WaitGone("DetailTitle");
        app.AssertNoEntryVisible();
        Assert.Equal(before, File.ReadAllBytes(vaultPath));
        Assert.Contains(
            SeededVault.ReadContent(app.VaultsDirectory, SeededVault.Password).Entries,
            e => e.Id == Guid.Parse(Seeded(1, "id")));

        Unlock(app);
        WaitTitles(app, SeededTitles);
    }

    // Opens the only profile (from the profile list, or the Unlock page it leads to) and waits for the entry list.
    private static void Unlock(AppSession app)
    {
        if (app.Find("UnlockPassword") is null)
        {
            Assert.Single(app.Items("ProfilesList")).Patterns.Invoke.Pattern.Invoke();
        }

        app.TypePassword(SeededVault.Password);
        app.Invoke("UnlockButton");
        app.WaitFor("EntriesList");
        app.WaitGone("UnlockPassword");
    }

    // Opens seeded entry `index` (0-based) from the list by its title and waits until the editor shows it.
    private static void OpenEntry(AppSession app, int index)
    {
        var title = SeededTitles[index];
        var item = Retry.WhileNull(
            () => app.Items("EntriesList").FirstOrDefault(i => i.Name == title), Timeout, Poll, ignoreException: true).Result
            ?? throw new InvalidOperationException($"Entry #{index + 1} is not in the list.");
        item.Patterns.Invoke.Pattern.Invoke();
        app.WaitFor("DetailTitle");
        app.WaitGone("EntriesList");
        Assert.Equal(title, app.ValueOf("DetailTitle"));
    }

    private static void GoBack(AppSession app)
    {
        app.WaitEnabled("DetailBack");
        app.Invoke("DetailBack");
        app.WaitFor("EntriesList");
        app.WaitGone("DetailTitle");
    }

    // Waits until the list shows exactly these titles, in this order (each item container is named with its title).
    private static void WaitTitles(AppSession app, params string[] expected)
    {
        Retry.WhileFalse(
            () => app.Items("EntriesList").Select(i => i.Name).SequenceEqual(expected), Timeout, Poll, ignoreException: true);
        Assert.Equal(expected, app.Items("EntriesList").Select(i => i.Name));
    }

    private static string Seeded(int index, string field) => SeededVault.Entries[index].GetProperty(field).GetString()!;

    // The vector stores timestamps as ISO 8601 UTC.
    private static DateTimeOffset SeededDate(int index, string field) =>
        DateTimeOffset.Parse(Seeded(index, field), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    private static DateTimeOffset TruncateToMilliseconds(DateTimeOffset value) =>
        value.AddTicks(-(value.Ticks % TimeSpan.TicksPerMillisecond));

    // The entry on disk is seeded entry `index` (0-based), field by field.
    private static void AssertSeeded(int index, VaultEntry actual)
    {
        JsonElement expected = SeededVault.Entries[index];
        Assert.Equal(Guid.Parse(expected.GetProperty("id").GetString()!), actual.Id);
        Assert.Equal(expected.GetProperty("title").GetString(), actual.Title);
        Assert.Equal(expected.GetProperty("username").GetString(), actual.Username);
        Assert.Equal(expected.GetProperty("password").GetString(), actual.Password);
        Assert.Equal(expected.GetProperty("url").GetString(), actual.Url);
        Assert.Equal(expected.GetProperty("notes").GetString(), actual.Notes);
        Assert.Equal(expected.GetProperty("folder").GetString(), actual.Folder);
        Assert.Equal(expected.GetProperty("favorite").GetBoolean(), actual.Favorite);
        Assert.Equal(SeededDate(index, "createdAt"), actual.CreatedAt);
        Assert.Equal(SeededDate(index, "updatedAt"), actual.UpdatedAt);
    }

    // Whether an element holds this text as its Name or as its Value (a password box is never read).
    private static bool ShowsText(AutomationElement element, string text) =>
        element.Properties.Name.ValueOrDefault == text
        || (!element.Properties.IsPassword.ValueOrDefault
            && element.Patterns.Value.PatternOrDefault?.Value.ValueOrDefault == text);
}
