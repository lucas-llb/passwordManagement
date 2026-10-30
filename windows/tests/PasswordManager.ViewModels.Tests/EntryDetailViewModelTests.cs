using PasswordManager.Core.Passwords;
using PasswordManager.Core.Profiles;
using PasswordManager.Core.Vault;
using PasswordManager.ViewModels.Navigation;

namespace PasswordManager.ViewModels.Tests;

public sealed class EntryDetailViewModelTests : IDisposable
{
    private readonly TestProfiles _profiles = new();
    private readonly NavigationService _navigation = new();

    private readonly VaultEntry _mail = TestProfiles.Entry("Mail", "mail@example.com") with
    {
        Password = "senha-ficticia-1",
        Url = "https://mail.example.com",
        Notes = "a\nb",
        Folder = "Pessoal",
        Favorite = true,
    };

    private readonly VaultEntry _bank = TestProfiles.Entry("Bank", "bank@example.com") with
    {
        Password = "senha-ficticia-2",
        Folder = "Finanças",
    };

    private readonly VaultEntry _note = TestProfiles.Entry("Note", "") with
    {
        Password = "",
        Url = "",
        Notes = "linha 1\nlinha 2",
        Folder = "",
    };

    private Profile? _profile;

    public void Dispose() => _profiles.Dispose();

    // The list as the app builds it after an unlock, with the test clock, and the list as the root page.
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

    private EntryDetailViewModel OpenNew(EntryListViewModel list)
    {
        list.NewEntryCommand.Execute(null);
        return Assert.IsType<EntryDetailViewModel>(_navigation.Current);
    }

    // The vault as it is on disk, read through a new session.
    private IReadOnlyList<VaultEntry> ReadDisk()
    {
        using var session = _profiles.OpenSession(_profile!);
        return session.Content.Entries;
    }

    private string VaultPath => _profiles.Repository.GetVaultPath(_profile!.Id);

    private static DateTimeOffset TruncateToMilliseconds(DateTimeOffset value) =>
        new(value.Ticks - (value.Ticks % TimeSpan.TicksPerMillisecond), value.Offset);

    // Open and draft

    [Fact]
    public void Open_ExistingEntry_LoadsEveryFieldIntoTheDraft()
    {
        var list = OpenList(_mail, _bank);

        var detail = Open(list, _mail);

        Assert.Same(_mail, detail.Entry);
        Assert.False(detail.IsNew);
        Assert.Equal(_mail.Title, detail.Title);
        Assert.Equal(_mail.Username, detail.Username);
        Assert.Equal(_mail.Password, detail.Password);
        Assert.Equal(_mail.Url, detail.Url);
        Assert.Equal(_mail.Notes, detail.Notes);
        Assert.Equal(_mail.Folder, detail.Folder);
        Assert.True(detail.Favorite);
    }

    [Fact]
    public void Open_ExistingEntry_IsCleanWithThePasswordHiddenAndBackAllowed()
    {
        var list = OpenList(_mail);

        var detail = Open(list, _mail);

        Assert.False(detail.IsDirty);
        Assert.False(detail.IsPasswordVisible);
        Assert.True(detail.IsPasswordHidden);
        Assert.False(detail.IsTitleMissing);
        Assert.False(detail.SaveFailed);
        Assert.False(detail.IsDeletePending);
        Assert.False(detail.SaveCommand.CanExecute(null));
        Assert.False(detail.DiscardCommand.CanExecute(null));
        Assert.True(detail.GoBackCommand.CanExecute(null));
        Assert.True(detail.RequestDeleteCommand.CanExecute(null));
    }

    [Fact]
    public void Open_ExistingEntry_DatesAreTheEntryDates()
    {
        var list = OpenList(_mail);

        var detail = Open(list, _mail);

        Assert.Equal(_mail.CreatedAt, detail.CreatedAt);
        Assert.Equal(_mail.UpdatedAt, detail.UpdatedAt);
    }

    [Fact]
    public void GoBack_CleanDraft_ReturnsToTheList()
    {
        var list = OpenList(_mail);
        var detail = Open(list, _mail);

        detail.GoBackCommand.Execute(null);

        Assert.Same(list, _navigation.Current);
        Assert.False(_navigation.CanGoBack);
    }

    [Fact]
    public void Constructor_NullArguments_Throw()
    {
        var list = OpenList(_mail);

        Assert.Throws<ArgumentNullException>(() => new EntryDetailViewModel(null!, _mail, _navigation, _profiles.Time));
        Assert.Throws<ArgumentNullException>(() => new EntryDetailViewModel(list, _mail, null!, _profiles.Time));
        Assert.Throws<ArgumentNullException>(() => new EntryDetailViewModel(list, _mail, _navigation, null!));
    }

    // Line endings

    [Fact]
    public void Notes_CarriageReturnsFromTheTextBox_AreNotDirty()
    {
        var list = OpenList(_mail);
        var detail = Open(list, _mail);

        detail.Notes = "a\rb";

        Assert.False(detail.IsDirty);
        Assert.True(detail.GoBackCommand.CanExecute(null));
    }

    [Fact]
    public void Save_NotesWithCarriageReturns_AreSavedWithLineFeeds()
    {
        var list = OpenList(_mail);
        var detail = Open(list, _mail);

        detail.Notes = "a\rb";
        detail.Title = "Mail 2";
        detail.SaveCommand.Execute(null);

        Assert.Equal("a\nb", ReadDisk()[0].Notes);
        Assert.Equal("a\nb", detail.Entry!.Notes);
        Assert.False(detail.IsDirty);
    }

    // Each field on its own

    private static readonly string[] FieldNames = ["Title", "Username", "Password", "Url", "Notes", "Folder", "Favorite"];

    public static TheoryData<string> Fields => new(FieldNames);

    [Theory]
    [MemberData(nameof(Fields))]
    public void Draft_OneFieldChangedAndBack_IsDirtyThenClean(string field)
    {
        var list = OpenList(_mail);
        var detail = Open(list, _mail);

        Set(detail, field, changed: true);
        Assert.True(detail.IsDirty);
        Assert.True(detail.SaveCommand.CanExecute(null));
        Assert.True(detail.DiscardCommand.CanExecute(null));
        Assert.False(detail.GoBackCommand.CanExecute(null));

        Set(detail, field, changed: false);
        Assert.False(detail.IsDirty);
        Assert.False(detail.SaveCommand.CanExecute(null));
        Assert.True(detail.GoBackCommand.CanExecute(null));
    }

    private void Set(EntryDetailViewModel detail, string field, bool changed)
    {
        switch (field)
        {
            case "Title":
                detail.Title = changed ? "Mail editado" : _mail.Title;
                break;
            case "Username":
                detail.Username = changed ? "outro@example.com" : _mail.Username;
                break;
            case "Password":
                detail.Password = changed ? "senha-ficticia-nova" : _mail.Password;
                break;
            case "Url":
                detail.Url = changed ? "https://outro.example.com" : _mail.Url;
                break;
            case "Notes":
                detail.Notes = changed ? "outra nota" : _mail.Notes;
                break;
            case "Folder":
                detail.Folder = changed ? "Trabalho" : _mail.Folder;
                break;
            case "Favorite":
                detail.Favorite = changed ? !_mail.Favorite : _mail.Favorite;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(field));
        }
    }

    [Fact]
    public void Draft_NullFromABinding_BecomesEmpty()
    {
        var list = OpenList(_mail);
        var detail = Open(list, _mail);

        detail.Username = null!;

        Assert.Equal(string.Empty, detail.Username);
        Assert.True(detail.IsDirty);
    }

    // Edit and save

    [Fact]
    public void Save_Edit_KeepsIdAndCreatedAtAndStampsUpdatedAt()
    {
        var list = OpenList(_mail, _bank, _note);
        var detail = Open(list, _bank);
        detail.Title = "Banco";
        detail.Username = "banco@example.com";
        detail.Password = "senha-ficticia-editada";
        detail.Url = "https://banco.example.com";
        detail.Notes = "nota editada";
        detail.Folder = "Contas";
        detail.Favorite = true;

        _profiles.Time.Advance(TimeSpan.FromTicks(15_000));
        var expected = TruncateToMilliseconds(_profiles.Time.GetUtcNow());
        detail.SaveCommand.Execute(null);

        var disk = ReadDisk();
        Assert.Equal(3, disk.Count);
        var saved = disk[1];
        Assert.Equal(_bank.Id, saved.Id);
        Assert.Equal(_bank.CreatedAt, saved.CreatedAt);
        Assert.Equal(expected, saved.UpdatedAt);
        Assert.NotEqual(_profiles.Time.GetUtcNow(), saved.UpdatedAt);
        Assert.Equal("Banco", saved.Title);
        Assert.Equal("banco@example.com", saved.Username);
        Assert.Equal("senha-ficticia-editada", saved.Password);
        Assert.Equal("https://banco.example.com", saved.Url);
        Assert.Equal("nota editada", saved.Notes);
        Assert.Equal("Contas", saved.Folder);
        Assert.True(saved.Favorite);
        Assert.Equal(_mail, disk[0]);
        Assert.Equal(_note, disk[2]);
        Assert.True(File.Exists(VaultPath + ".bak"));

        Assert.False(detail.IsDirty);
        Assert.False(detail.SaveFailed);
        Assert.Equal(saved, detail.Entry);
        Assert.Equal(expected, detail.UpdatedAt);
        Assert.Equal(_bank.CreatedAt, detail.CreatedAt);
        Assert.True(detail.GoBackCommand.CanExecute(null));
    }

    [Fact]
    public void Save_Edit_RaisesTheDatesAndTheEntry()
    {
        var list = OpenList(_mail);
        var detail = Open(list, _mail);
        detail.Title = "Mail 2";
        var raised = new List<string?>();
        detail.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        detail.SaveCommand.Execute(null);

        Assert.Contains(nameof(EntryDetailViewModel.Entry), raised);
        Assert.Contains(nameof(EntryDetailViewModel.UpdatedAt), raised);
        Assert.Contains(nameof(EntryDetailViewModel.IsDirty), raised);
    }

    // New entry

    [Fact]
    public void NewEntry_OpensAnEmptyDraftThatCannotBeSaved()
    {
        var list = OpenList(_mail);

        var detail = OpenNew(list);

        Assert.True(detail.IsNew);
        Assert.Null(detail.Entry);
        Assert.Null(detail.CreatedAt);
        Assert.Null(detail.UpdatedAt);
        Assert.Equal(string.Empty, detail.Title);
        Assert.Equal(string.Empty, detail.Password);
        Assert.False(detail.Favorite);
        Assert.True(detail.IsTitleMissing);
        Assert.False(detail.IsDirty);
        Assert.False(detail.SaveCommand.CanExecute(null));
        Assert.True(detail.GoBackCommand.CanExecute(null));
        Assert.True(_navigation.CanGoBack);
    }

    [Fact]
    public void NewEntry_WhitespaceTitle_KeepsSaveDisabled()
    {
        var list = OpenList(_mail);
        var detail = OpenNew(list);

        detail.Title = "   ";
        detail.Username = "novo@example.com";

        Assert.True(detail.IsDirty);
        Assert.True(detail.IsTitleMissing);
        Assert.False(detail.SaveCommand.CanExecute(null));

        detail.SaveCommand.Execute(null);
        Assert.Single(ReadDisk());
    }

    [Fact]
    public void NewEntry_TitleTyped_RaisesSaveCanExecuteChanged()
    {
        var list = OpenList(_mail);
        var detail = OpenNew(list);
        detail.Username = "novo@example.com";
        var raised = 0;
        detail.SaveCommand.CanExecuteChanged += (_, _) => raised++;

        detail.Title = "Novo";

        Assert.True(raised > 0);
        Assert.True(detail.SaveCommand.CanExecute(null));
    }

    [Fact]
    public void NewEntry_Save_AppendsTheEntryAndBecomesIt()
    {
        var list = OpenList(_mail, _bank);
        var detail = OpenNew(list);
        detail.Title = "Novo";
        detail.Username = "novo@example.com";
        detail.Password = "senha-ficticia-nova";
        detail.Notes = "x\r\ny";
        _profiles.Time.Advance(TimeSpan.FromTicks(15_000));

        detail.SaveCommand.Execute(null);

        var disk = ReadDisk();
        Assert.Equal(3, disk.Count);
        var added = disk[2];
        Assert.NotEqual(Guid.Empty, added.Id);
        Assert.Equal("Novo", added.Title);
        Assert.Equal("x\ny", added.Notes);
        Assert.Equal(added.CreatedAt, added.UpdatedAt);
        Assert.Equal(TruncateToMilliseconds(_profiles.Time.GetUtcNow()), added.CreatedAt);
        Assert.False(detail.IsNew);
        Assert.Equal(added, detail.Entry);
        Assert.False(detail.IsDirty);
        Assert.True(detail.RequestDeleteCommand.CanExecute(null));
        Assert.Same(detail, _navigation.Current);
        Assert.Equal(3, list.Entries.Count);
    }

    [Fact]
    public void NewEntry_SavedTwice_EditsTheSameEntry()
    {
        var list = OpenList(_mail);
        var detail = OpenNew(list);
        detail.Title = "Novo";
        detail.SaveCommand.Execute(null);
        var id = detail.Entry!.Id;

        detail.Title = "Novo 2";
        detail.SaveCommand.Execute(null);

        var disk = ReadDisk();
        Assert.Equal(2, disk.Count);
        Assert.Equal(id, disk[1].Id);
        Assert.Equal("Novo 2", disk[1].Title);
    }

    // Back gate

    [Fact]
    public void GoBack_DirtyDraft_CannotRunAndRaisesCanExecuteChanged()
    {
        var list = OpenList(_mail);
        var detail = Open(list, _mail);
        var raised = 0;
        detail.GoBackCommand.CanExecuteChanged += (_, _) => raised++;

        detail.Title = "Mail editado";

        Assert.False(detail.GoBackCommand.CanExecute(null));
        Assert.True(raised > 0);

        detail.GoBackCommand.Execute(null);
        Assert.Same(detail, _navigation.Current);
    }

    [Fact]
    public void Discard_RestoresEveryFieldAndAllowsBack()
    {
        var list = OpenList(_mail);
        var detail = Open(list, _mail);
        foreach (var field in FieldNames)
        {
            Set(detail, field, changed: true);
        }

        detail.DiscardCommand.Execute(null);

        Assert.Equal(_mail.Title, detail.Title);
        Assert.Equal(_mail.Username, detail.Username);
        Assert.Equal(_mail.Password, detail.Password);
        Assert.Equal(_mail.Url, detail.Url);
        Assert.Equal(_mail.Notes, detail.Notes);
        Assert.Equal(_mail.Folder, detail.Folder);
        Assert.Equal(_mail.Favorite, detail.Favorite);
        Assert.False(detail.IsDirty);
        Assert.False(detail.DiscardCommand.CanExecute(null));
        Assert.True(detail.GoBackCommand.CanExecute(null));
        Assert.Equal(_mail, ReadDisk()[0]);

        detail.GoBackCommand.Execute(null);
        Assert.Same(list, _navigation.Current);
    }

    [Fact]
    public void Discard_NewEntry_EmptiesTheDraft()
    {
        var list = OpenList(_mail);
        var detail = OpenNew(list);
        detail.Title = "Novo";
        detail.Favorite = true;

        detail.DiscardCommand.Execute(null);

        Assert.Equal(string.Empty, detail.Title);
        Assert.False(detail.Favorite);
        Assert.False(detail.IsDirty);
        Assert.True(detail.IsNew);
    }

    // Password toggle

    [Fact]
    public void IsPasswordVisible_Toggled_RaisesIsPasswordHidden()
    {
        var list = OpenList(_mail);
        var detail = Open(list, _mail);
        var raised = new List<string?>();
        detail.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        detail.IsPasswordVisible = true;

        Assert.True(detail.IsPasswordVisible);
        Assert.False(detail.IsPasswordHidden);
        Assert.Contains(nameof(EntryDetailViewModel.IsPasswordHidden), raised);

        raised.Clear();
        detail.IsPasswordVisible = false;

        Assert.True(detail.IsPasswordHidden);
        Assert.Contains(nameof(EntryDetailViewModel.IsPasswordHidden), raised);
        Assert.False(detail.IsDirty);
    }

    // Password generator

    private static bool HasEveryClass(string password) =>
        password.Any(c => PasswordGenerator.LowercaseLetters.Contains(c))
        && password.Any(c => PasswordGenerator.UppercaseLetters.Contains(c))
        && password.Any(c => PasswordGenerator.DigitCharacters.Contains(c))
        && password.Any(c => PasswordGenerator.SymbolCharacters.Contains(c));

    [Fact]
    public void Generator_Defaults_AreTwentyWithEveryOptionOnAndNeverDirty()
    {
        var list = OpenList(_mail);
        var detail = Open(list, _mail);

        Assert.Equal(20, detail.GeneratorLength);
        Assert.True(detail.GeneratorUppercase);
        Assert.True(detail.GeneratorDigits);
        Assert.True(detail.GeneratorSymbols);
        Assert.Equal(8, detail.GeneratorMinLength);
        Assert.Equal(128, detail.GeneratorMaxLength);
        Assert.True(detail.GeneratePasswordCommand.CanExecute(null));

        detail.GeneratorLength = 32;
        detail.GeneratorUppercase = false;
        detail.GeneratorDigits = false;
        detail.GeneratorSymbols = false;

        Assert.False(detail.IsDirty);
        Assert.False(detail.SaveCommand.CanExecute(null));
        Assert.True(detail.GoBackCommand.CanExecute(null));
    }

    [Fact]
    public void GeneratePassword_SavedEntry_ReplacesTheDraftPasswordAndKeepsItHidden()
    {
        var list = OpenList(_mail);
        var detail = Open(list, _mail);

        detail.GeneratePasswordCommand.Execute(null);

        Assert.Equal(20, detail.Password.Length);
        Assert.True(HasEveryClass(detail.Password));
        Assert.NotEqual(_mail.Password, detail.Password);
        Assert.True(detail.IsDirty);
        Assert.False(detail.IsPasswordVisible);
        Assert.True(detail.IsPasswordHidden);
        Assert.True(detail.SaveCommand.CanExecute(null));
        Assert.False(detail.GoBackCommand.CanExecute(null));
        Assert.Equal(_mail, ReadDisk()[0]);
    }

    [Fact]
    public void GeneratePassword_ThenDiscard_RestoresTheSavedPassword()
    {
        var list = OpenList(_mail);
        var detail = Open(list, _mail);
        detail.GeneratePasswordCommand.Execute(null);

        detail.DiscardCommand.Execute(null);

        Assert.Equal(_mail.Password, detail.Password);
        Assert.False(detail.IsDirty);
        Assert.True(detail.GoBackCommand.CanExecute(null));
        Assert.Equal(_mail, ReadDisk()[0]);
    }

    [Fact]
    public void GeneratePassword_EveryOptionOff_GivesOnlyLowercaseLetters()
    {
        var list = OpenList(_mail);
        var detail = Open(list, _mail);
        detail.GeneratorLength = 32;
        detail.GeneratorUppercase = false;
        detail.GeneratorDigits = false;
        detail.GeneratorSymbols = false;

        detail.GeneratePasswordCommand.Execute(null);

        Assert.Equal(32, detail.Password.Length);
        Assert.All(detail.Password, c => Assert.Contains(c, PasswordGenerator.LowercaseLetters));
    }

    public static TheoryData<double, int> LengthClamps => new()
    {
        { 3, 8 },
        { 500, 128 },
        { double.NaN, 20 },
        { 20.6, 21 },
        { double.PositiveInfinity, 128 },
        { double.NegativeInfinity, 8 },
    };

    [Theory]
    [MemberData(nameof(LengthClamps))]
    public void GeneratePassword_LengthOutOfRange_IsRoundedClampedAndWrittenBack(double requested, int expected)
    {
        var list = OpenList(_mail);
        var detail = Open(list, _mail);
        detail.GeneratorLength = requested;

        detail.GeneratePasswordCommand.Execute(null);

        Assert.Equal(expected, detail.Password.Length);
        Assert.Equal(expected, detail.GeneratorLength);
    }

    [Fact]
    public void GeneratePassword_NewEntry_IsSavedToDiskAsTheDraft()
    {
        var list = OpenList(_mail);
        var detail = OpenNew(list);

        detail.GeneratePasswordCommand.Execute(null);
        detail.Title = "Novo";
        var generated = detail.Password;
        detail.SaveCommand.Execute(null);

        var disk = ReadDisk();
        Assert.Equal(2, disk.Count);
        Assert.Equal(generated, disk[1].Password);
        Assert.Equal(20, disk[1].Password.Length);
        Assert.True(HasEveryClass(disk[1].Password));
        Assert.False(detail.IsDirty);
    }

    [Fact]
    public void GeneratePassword_NewEntryWithoutTitle_StaysUnsavable()
    {
        var list = OpenList(_mail);
        var detail = OpenNew(list);

        detail.GeneratePasswordCommand.Execute(null);

        Assert.True(detail.IsDirty);
        Assert.False(detail.SaveCommand.CanExecute(null));
    }

    [Fact]
    public void GeneratePassword_Twice_GivesDifferentPasswords()
    {
        var list = OpenList(_mail);
        var detail = Open(list, _mail);

        detail.GeneratePasswordCommand.Execute(null);
        var first = detail.Password;
        detail.GeneratePasswordCommand.Execute(null);

        Assert.NotEqual(first, detail.Password);
    }

    [Fact]
    public void ToString_AfterGenerate_IsOnlyTheTypeName()
    {
        var list = OpenList(_mail);
        var detail = Open(list, _mail);

        detail.GeneratePasswordCommand.Execute(null);

        Assert.Equal(nameof(EntryDetailViewModel), detail.ToString());
        Assert.DoesNotContain(detail.Password, detail.ToString(), StringComparison.Ordinal);
    }

    // Save failure

    [Fact]
    public void Save_VaultFileLocked_FailsKeepsTheDraftThenSucceedsOnceReleased()
    {
        var list = OpenList(_mail, _bank);
        var detail = Open(list, _mail);
        var before = File.ReadAllBytes(VaultPath);
        detail.Title = "Mail editado";

        using (new FileStream(VaultPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            detail.SaveCommand.Execute(null);

            Assert.True(detail.SaveFailed);
            Assert.True(detail.IsDirty);
            Assert.Equal("Mail editado", detail.Title);
            Assert.Same(_mail, detail.Entry);
            Assert.True(detail.SaveCommand.CanExecute(null));
            Assert.False(detail.GoBackCommand.CanExecute(null));
            Assert.Equal([_mail, _bank], list.Entries);
        }

        Assert.Equal(before, File.ReadAllBytes(VaultPath));

        detail.SaveCommand.Execute(null);

        Assert.False(detail.SaveFailed);
        Assert.False(detail.IsDirty);
        Assert.Equal("Mail editado", ReadDisk()[0].Title);
    }

    // Delete

    [Fact]
    public void RequestDelete_ThenCancel_LeavesTheDiskUnchanged()
    {
        var list = OpenList(_mail, _bank);
        var detail = Open(list, _bank);
        var before = File.ReadAllBytes(VaultPath);
        Assert.False(detail.ConfirmDeleteCommand.CanExecute(null));

        detail.RequestDeleteCommand.Execute(null);

        Assert.True(detail.IsDeletePending);
        Assert.True(detail.ConfirmDeleteCommand.CanExecute(null));

        detail.CancelDeleteCommand.Execute(null);

        Assert.False(detail.IsDeletePending);
        Assert.False(detail.ConfirmDeleteCommand.CanExecute(null));
        Assert.Equal(before, File.ReadAllBytes(VaultPath));
        Assert.Same(detail, _navigation.Current);
    }

    [Fact]
    public void ConfirmDelete_RemovesTheEntryAndGoesBackToTheList()
    {
        var list = OpenList(_mail, _bank, _note);
        var detail = Open(list, _bank);
        detail.Title = "Edição não salva";
        detail.RequestDeleteCommand.Execute(null);

        detail.ConfirmDeleteCommand.Execute(null);

        Assert.Equal([_mail, _note], ReadDisk());
        Assert.Equal([_mail, _note], list.Entries);
        Assert.Equal([_mail, _note], list.FilteredEntries);
        Assert.False(detail.IsDeletePending);
        Assert.Same(list, _navigation.Current);
        Assert.False(_navigation.CanGoBack);
    }

    [Fact]
    public void ConfirmDelete_VaultFileLocked_FailsAndStays()
    {
        var list = OpenList(_mail, _bank);
        var detail = Open(list, _bank);
        detail.RequestDeleteCommand.Execute(null);

        using (new FileStream(VaultPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            detail.ConfirmDeleteCommand.Execute(null);
        }

        Assert.True(detail.SaveFailed);
        Assert.False(detail.IsDeletePending);
        Assert.Same(detail, _navigation.Current);
        Assert.Equal([_mail, _bank], ReadDisk());
        Assert.Equal([_mail, _bank], list.Entries);
    }

    [Fact]
    public void RequestDelete_NewEntry_CannotRun()
    {
        var list = OpenList(_mail);
        var detail = OpenNew(list);

        Assert.False(detail.RequestDeleteCommand.CanExecute(null));

        detail.RequestDeleteCommand.Execute(null);
        Assert.False(detail.IsDeletePending);
    }

    // After Lock

    [Fact]
    public void Save_AfterLock_DoesNothingAndDoesNotThrow()
    {
        var list = OpenList(_mail);
        var detail = Open(list, _mail);
        detail.Title = "Mail editado";
        var before = File.ReadAllBytes(VaultPath);

        list.LockCommand.Execute(null);
        detail.SaveCommand.Execute(null);

        Assert.False(detail.SaveFailed);
        Assert.Same(_mail, detail.Entry);
        Assert.Equal(before, File.ReadAllBytes(VaultPath));
        Assert.IsType<UnlockViewModel>(_navigation.Current);
    }

    [Fact]
    public void ConfirmDelete_AfterLock_DoesNothingAndStaysOnTheUnlockPage()
    {
        var list = OpenList(_mail);
        var detail = Open(list, _mail);
        detail.RequestDeleteCommand.Execute(null);
        var before = File.ReadAllBytes(VaultPath);

        list.LockCommand.Execute(null);
        detail.ConfirmDeleteCommand.Execute(null);

        Assert.False(detail.SaveFailed);
        Assert.Equal(before, File.ReadAllBytes(VaultPath));
        Assert.IsType<UnlockViewModel>(_navigation.Current);
        Assert.True(_navigation.GoBack());
        Assert.IsType<ProfilesViewModel>(_navigation.Current);
    }

    [Fact]
    public void NewEntry_SaveAfterLock_WritesNothing()
    {
        var list = OpenList(_mail);
        var detail = OpenNew(list);
        detail.Title = "Novo";
        var before = File.ReadAllBytes(VaultPath);

        list.LockCommand.Execute(null);
        detail.SaveCommand.Execute(null);

        Assert.True(detail.IsNew);
        Assert.Equal(before, File.ReadAllBytes(VaultPath));
    }
}
