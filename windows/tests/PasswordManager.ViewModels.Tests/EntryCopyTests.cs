using PasswordManager.Core.Vault;
using PasswordManager.ViewModels.Navigation;

namespace PasswordManager.ViewModels.Tests;

/// <summary>
/// Copying the password of an entry detail through the <see cref="ClipboardGuard"/>: the copy command and its state,
/// the guard following the page only while it is shown, and the lock taking the copy off the clipboard. The guard
/// itself (deadline, retries, interleaving) is covered by <see cref="ClipboardGuardTests"/>.
/// </summary>
public sealed class EntryCopyTests : IDisposable
{
    private const string MailPassword = "senha-ficticia-1";

    private readonly TestProfiles _profiles = new();
    private readonly NavigationService _navigation = new();

    private readonly VaultEntry _mail = TestProfiles.Entry("Mail", "mail@example.com") with
    {
        Password = MailPassword,
    };

    private readonly VaultEntry _bank = TestProfiles.Entry("Bank", "bank@example.com") with
    {
        Password = "senha-ficticia-2",
    };

    public void Dispose() => _profiles.Dispose();

    private FakeClipboard Fake => _profiles.ClipboardHost;

    private ClipboardGuard Guard => _profiles.Clipboard;

    // The list as the app builds it after an unlock, with the shared test clock and guard, and the list as the root page.
    private EntryListViewModel OpenList(params VaultEntry[] entries)
    {
        var profile = _profiles.CreateProfile("Alice", TestProfiles.Password, entries);
        var session = _profiles.OpenSession(profile);
        var list = new EntryListViewModel(
            profile,
            session,
            _profiles.Repository,
            _navigation,
            _profiles.Throttle,
            _profiles.Clipboard,
            _profiles.Time);
        _navigation.ResetTo(list);

        return list;
    }

    // Through the command, so the navigation hooks run.
    private EntryDetailViewModel Open(EntryListViewModel list, VaultEntry entry)
    {
        list.OpenEntryCommand.Execute(entry);
        return Assert.IsType<EntryDetailViewModel>(_navigation.Current);
    }

    private EntryDetailViewModel OpenNew(EntryListViewModel list)
    {
        list.NewEntryCommand.Execute(null);
        return Assert.IsType<EntryDetailViewModel>(_navigation.Current);
    }

    private static List<string?> Record(EntryDetailViewModel detail)
    {
        var raised = new List<string?>();
        detail.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        return raised;
    }

    // Copy

    [Fact]
    public void CopyPassword_SavedEntry_PutsThePasswordOnTheClipboardMarkedPrivate()
    {
        var detail = Open(OpenList(_mail), _mail);

        detail.CopyPasswordCommand.Execute(null);

        Assert.Equal(MailPassword, Fake.Text);
        Assert.True(Fake.IsMarkedPrivate);
        Assert.True(detail.IsPasswordCopied);
        Assert.False(detail.CopyFailed);
        Assert.NotEqual(0, Guard.PendingCopy);
    }

    [Fact]
    public void CopyPassword_PasswordHidden_LeavesTheDraftAndTheVisibilityAlone()
    {
        var detail = Open(OpenList(_mail), _mail);
        Assert.False(detail.IsPasswordVisible);

        detail.CopyPasswordCommand.Execute(null);

        Assert.False(detail.IsDirty);
        Assert.False(detail.IsPasswordVisible);
        Assert.Equal(MailPassword, detail.Password);
    }

    [Fact]
    public void CopyPassword_PasswordShown_StaysShown()
    {
        var detail = Open(OpenList(_mail), _mail);
        detail.IsPasswordVisible = true;

        detail.CopyPasswordCommand.Execute(null);

        Assert.True(detail.IsPasswordVisible);
        Assert.False(detail.IsDirty);
    }

    [Fact]
    public void CopyPassword_EditedPassword_CopiesTheDraftAsItIs()
    {
        var detail = Open(OpenList(_mail), _mail);
        detail.Password = "rascunho-ficticio";

        detail.CopyPasswordCommand.Execute(null);

        Assert.Equal("rascunho-ficticio", Fake.Text);
        Assert.True(detail.IsDirty);
        Assert.Equal(_mail.Password, detail.Entry?.Password);
    }

    [Fact]
    public void CopyPassword_Selection_CopiesOnlyThatText()
    {
        var detail = Open(OpenList(_mail), _mail);

        detail.CopyPasswordCommand.Execute("ficticia");

        Assert.Equal("ficticia", Fake.Text);
        Assert.True(Fake.IsMarkedPrivate);
        Assert.True(detail.IsPasswordCopied);
        Assert.False(detail.IsDirty);
    }

    [Fact]
    public void CopyPassword_Twice_FollowsTheLatestCopy()
    {
        var detail = Open(OpenList(_mail), _mail);
        detail.CopyPasswordCommand.Execute(null);

        detail.CopyPasswordCommand.Execute("ficticia");

        Assert.Equal("ficticia", Fake.Text);
        Assert.True(detail.IsPasswordCopied);
        Assert.Equal(2, Fake.SetCalls);
    }

    // CanExecute

    [Fact]
    public void CopyPassword_NewEntryWithoutPassword_CannotExecuteUntilThePasswordIsSet()
    {
        var detail = OpenNew(OpenList(_mail));
        var changed = 0;
        detail.CopyPasswordCommand.CanExecuteChanged += (_, _) => changed++;
        Assert.False(detail.CopyPasswordCommand.CanExecute(null));

        detail.Password = "senha-ficticia-3";

        Assert.True(detail.CopyPasswordCommand.CanExecute(null));
        Assert.Equal(1, changed);
    }

    [Fact]
    public void CopyPassword_EmptyPassword_DoesNothingEvenThroughExecute()
    {
        var detail = OpenNew(OpenList(_mail));

        detail.CopyPasswordCommand.Execute(null);

        Assert.Equal(0, Fake.SetCalls);
        Assert.False(detail.IsPasswordCopied);
        Assert.False(detail.CopyFailed);
        Assert.Equal(0, Guard.PendingCopy);
    }

    [Fact]
    public void CopyPassword_SelectionParameter_DecidesCanExecute()
    {
        var detail = OpenNew(OpenList(_mail));

        // The draft is empty, but there is a selection to copy; an empty selection is nothing to copy even when the
        // draft has a password (null is what asks for the whole password).
        Assert.True(detail.CopyPasswordCommand.CanExecute("abc"));
        Assert.False(detail.CopyPasswordCommand.CanExecute(string.Empty));

        detail.Password = "senha-ficticia-3";
        Assert.False(detail.CopyPasswordCommand.CanExecute(string.Empty));

        detail.CopyPasswordCommand.Execute(string.Empty);
        Assert.Equal(0, Fake.SetCalls);
    }

    [Fact]
    public void CopyPassword_PasswordCleared_CannotExecuteAnymore()
    {
        var detail = Open(OpenList(_mail), _mail);
        Assert.True(detail.CopyPasswordCommand.CanExecute(null));

        detail.Password = string.Empty;

        Assert.False(detail.CopyPasswordCommand.CanExecute(null));
    }

    // Refused

    [Fact]
    public void CopyPassword_ClipboardRefuses_SetsCopyFailedAndNotCopied()
    {
        var detail = Open(OpenList(_mail), _mail);
        Fake.RefuseSet = true;

        detail.CopyPasswordCommand.Execute(null);

        Assert.True(detail.CopyFailed);
        Assert.False(detail.IsPasswordCopied);
        Assert.Null(Fake.Text);
        Assert.Equal(0, Guard.PendingCopy);
    }

    [Fact]
    public void CopyPassword_AfterARefusal_TheNextSuccessClearsCopyFailed()
    {
        var detail = Open(OpenList(_mail), _mail);
        Fake.RefuseSet = true;
        detail.CopyPasswordCommand.Execute(null);
        Assert.True(detail.CopyFailed);

        Fake.RefuseSet = false;
        detail.CopyPasswordCommand.Execute(null);

        Assert.False(detail.CopyFailed);
        Assert.True(detail.IsPasswordCopied);
        Assert.Equal(MailPassword, Fake.Text);
    }

    // Cleared

    [Fact]
    public async Task IsPasswordCopied_AfterTheDelay_TurnsFalseWhenTheGuardClearsTheClipboard()
    {
        var detail = Open(OpenList(_mail), _mail);
        var raised = Record(detail);
        detail.CopyPasswordCommand.Execute(null);

        _profiles.Time.Advance(ClipboardGuard.DefaultClearAfter - TimeSpan.FromMilliseconds(1));
        await Guard.CheckAsync();
        Assert.True(detail.IsPasswordCopied);
        Assert.Equal(MailPassword, Fake.Text);

        raised.Clear();
        _profiles.Time.Advance(TimeSpan.FromMilliseconds(1));
        await Guard.CheckAsync();

        Assert.Null(Fake.Text);
        Assert.False(detail.IsPasswordCopied);
        Assert.Contains(nameof(EntryDetailViewModel.IsPasswordCopied), raised);
    }

    // Other pages and leaving

    [Fact]
    public void IsPasswordCopied_CopyOnAnotherEntry_IsFalseOnThisPage()
    {
        var list = OpenList(_mail, _bank);
        var first = Open(list, _mail);
        first.CopyPasswordCommand.Execute(null);
        first.GoBackCommand.Execute(null);
        Assert.Same(list, _navigation.Current);

        var second = Open(list, _bank);

        Assert.False(second.IsPasswordCopied);
        Assert.Equal(MailPassword, Fake.Text);
    }

    [Fact]
    public async Task IsPasswordCopied_CopyOnAnotherEntryClears_RaisesNothingOnThisPage()
    {
        var list = OpenList(_mail, _bank);
        var first = Open(list, _mail);
        first.CopyPasswordCommand.Execute(null);
        first.GoBackCommand.Execute(null);
        var second = Open(list, _bank);
        var raised = Record(second);

        _profiles.Time.Advance(ClipboardGuard.DefaultClearAfter);
        await Guard.CheckAsync();

        Assert.Null(Fake.Text);
        Assert.Empty(raised);
        Assert.False(second.IsPasswordCopied);
    }

    [Fact]
    public void IsPasswordCopied_OwnCopyAfterAnotherEntrysCopy_ReplacesIt()
    {
        var list = OpenList(_mail, _bank);
        var first = Open(list, _mail);
        first.CopyPasswordCommand.Execute(null);
        first.GoBackCommand.Execute(null);
        var second = Open(list, _bank);

        second.CopyPasswordCommand.Execute(null);

        Assert.True(second.IsPasswordCopied);
        Assert.Equal("senha-ficticia-2", Fake.Text);
    }

    [Fact]
    public async Task IsPasswordCopied_AfterLeavingThePage_RaisesNothingOnTheOldViewModel()
    {
        var list = OpenList(_mail);
        var detail = Open(list, _mail);
        detail.CopyPasswordCommand.Execute(null);
        detail.GoBackCommand.Execute(null);
        Assert.Same(list, _navigation.Current);
        var raised = Record(detail);

        _profiles.Time.Advance(ClipboardGuard.DefaultClearAfter);
        await Guard.CheckAsync();

        // The guard did change (the clipboard is clear and nothing is pending), and the left page did not hear of it.
        Assert.Null(Fake.Text);
        Assert.Equal(0, Guard.PendingCopy);
        Assert.Empty(raised);
    }

    // Lock

    [Fact]
    public async Task Lock_WithAPendingCopy_ClearsTheClipboardOnTheNextCheckWithoutWaiting()
    {
        var list = OpenList(_mail);
        var detail = Open(list, _mail);
        detail.CopyPasswordCommand.Execute(null);

        list.LockCommand.Execute(null);
        Assert.IsType<UnlockViewModel>(_navigation.Current);
        await Guard.CheckAsync();

        Assert.Null(Fake.Text);
        Assert.Equal(0, Guard.PendingCopy);
    }

    [Fact]
    public async Task Lock_CopyMadeBeforeLeavingTheDetail_AlsoClearsOnLock()
    {
        var list = OpenList(_mail);
        var detail = Open(list, _mail);
        detail.CopyPasswordCommand.Execute(null);
        detail.GoBackCommand.Execute(null);

        list.LockCommand.Execute(null);
        await Guard.CheckAsync();

        Assert.Null(Fake.Text);
        Assert.Equal(0, Guard.PendingCopy);
    }

    [Fact]
    public async Task AutoLock_SessionLocked_ClearsTheClipboardOnTheNextCheckWithoutWaiting()
    {
        var autoLock = new AutoLock(_navigation, _profiles.Time, AutoLockOptions.Default);
        var list = OpenList(_mail);
        var detail = Open(list, _mail);
        detail.CopyPasswordCommand.Execute(null);

        autoLock.OnSessionLocked();
        Assert.False(list.IsOpen);
        await Guard.CheckAsync();

        Assert.Null(Fake.Text);
        Assert.Equal(0, Guard.PendingCopy);
    }

    [Fact]
    public async Task Lock_TheUserCopiedSomethingElse_LeavesItAlone()
    {
        var list = OpenList(_mail);
        var detail = Open(list, _mail);
        detail.CopyPasswordCommand.Execute(null);
        Fake.SetByUser("outra-coisa-ficticia");

        list.LockCommand.Execute(null);
        await Guard.CheckAsync();

        Assert.Equal("outra-coisa-ficticia", Fake.Text);
        Assert.Equal(0, Guard.PendingCopy);
    }

    [Fact]
    public async Task Lock_NothingPending_TouchesNoClipboard()
    {
        var list = OpenList(_mail);
        Fake.SetByUser("do-usuario-ficticio");

        list.LockCommand.Execute(null);
        await Guard.CheckAsync();

        Assert.Equal("do-usuario-ficticio", Fake.Text);
        Assert.Equal(0, Fake.MatchCalls);
        Assert.Equal(0, Fake.ClearCalls);
    }
}
