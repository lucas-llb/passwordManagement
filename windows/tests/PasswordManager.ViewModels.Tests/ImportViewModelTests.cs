using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using PasswordManager.Core.Profiles;
using PasswordManager.Core.Vault;
using PasswordManager.ViewModels.Navigation;

namespace PasswordManager.ViewModels.Tests;

public sealed class ImportViewModelTests : IDisposable
{
    // Fictitious.
    private const string FilePassword = "a fictitious import passphrase";

    private const string WrongPassword = "a fictitious wrong passphrase";

    private const string CsvHeader = "name,url,username,password,notes,folder";

    // Same key as the seeded "Mail" entry, with another password and no notes.
    private const string DuplicateRow = "Mail,https://example.com,user@example.com,fictitious-new-password,,";

    private const string NewRow = "Forum,https://forum.example.com,me@example.com,fictitious-forum-password,forum notes,Hobbies";

    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(30);

    private readonly TestProfiles _profiles = new();
    private readonly NavigationService _navigation = new();

    // The folder the user picks files from. A sibling of the vaults folder, never inside it.
    private readonly DirectoryInfo _imports = Directory.CreateTempSubdirectory("pm-viewmodels-imports-");

    private readonly List<VaultSession> _sessions = [];

    public enum BadFile
    {
        TooLarge,
        InvalidEncoding,
        InvalidHeader,
        MalformedCsv,
        TooManyEntries,
        Missing,
    }

    public void Dispose()
    {
        foreach (var session in _sessions)
        {
            session.Dispose();
        }

        _profiles.Dispose();
        if (Directory.Exists(_imports.FullName))
        {
            _imports.Delete(recursive: true);
        }
    }

    // List -> Import, over an open vault with two entries (on the clock of TestProfiles), the way the app gets there.
    private (Profile Profile, EntryListViewModel List, ImportViewModel Import) OpenImport()
    {
        var profile = _profiles.CreateProfile(
            "Alice",
            TestProfiles.Password,
            TestProfiles.Entry("Mail"),
            TestProfiles.Entry("Bank"));
        var session = _profiles.OpenSession(profile);
        _sessions.Add(session);
        var list = new EntryListViewModel(
            profile,
            session,
            _profiles.Repository,
            _navigation,
            _profiles.Throttle,
            _profiles.Clipboard,
            _profiles.Time);
        _navigation.ResetTo(list);
        list.OpenImportCommand.Execute(null);

        return (profile, list, Assert.IsType<ImportViewModel>(_navigation.Current));
    }

    private string ImportPath(string name) => Path.Combine(_imports.FullName, name);

    private string WriteCsv(string name, params string[] rows)
    {
        var path = ImportPath(name);
        File.WriteAllText(path, string.Join('\n', rows.Prepend(CsvHeader)) + "\n", new UTF8Encoding(false));
        return path;
    }

    // A .pmvault export of a throwaway vault, protected by FilePassword alone, with the given Argon2id cost.
    private string WritePmvault(string name, KdfParameters parameters, params VaultEntry[] entries)
    {
        var profile = _profiles.CreateProfile("Source " + name, TestProfiles.Password, TestProfiles.LowCost, entries);
        using var session = new VaultSession(_profiles.Repository.GetVaultPath(profile.Id), parameters);
        session.Unlock(TestProfiles.Password);

        var path = ImportPath(name);
        VaultFile.WriteExport(path, session.Export(FilePassword));
        return path;
    }

    private string WritePmvault(string name, params VaultEntry[] entries) =>
        WritePmvault(name, TestProfiles.LowCost, entries);

    private string VaultPath(Profile profile) => _profiles.Repository.GetVaultPath(profile.Id);

    private string BackupPath(Profile profile) => VaultPath(profile) + VaultFile.BackupSuffix;

    // The test profile's own first save left a .bak: this removes it, so a new one can only come from the import.
    private void RemoveBackup(Profile profile) => File.Delete(BackupPath(profile));

    // The name and bytes of every file directly in the vaults folder.
    private Dictionary<string, byte[]> VaultFolderSnapshot() =>
        Directory.GetFiles(_profiles.DirectoryPath)
            .ToDictionary(path => Path.GetFileName(path), path => File.ReadAllBytes(path), StringComparer.Ordinal);

    private void AssertVaultFolderUnchanged(Dictionary<string, byte[]> before)
    {
        var after = VaultFolderSnapshot();

        Assert.Equal(before.Keys.Order(StringComparer.Ordinal), after.Keys.Order(StringComparer.Ordinal));
        foreach (var (name, bytes) in before)
        {
            Assert.Equal(bytes, after[name]);
        }
    }

    // The entries on disk, read with a session of its own.
    private IReadOnlyList<VaultEntry> SavedEntries(Profile profile)
    {
        using var session = _profiles.OpenSession(profile);
        return session.Content.Entries;
    }

    private static Task Choose(ImportViewModel import, string? path) =>
        import.ChooseFileCommand.ExecuteAsync(new FakeExportFilePicker(path));

    // The .pmvault bytes the page holds. The rule that they get zeroed has no other way to be seen from outside.
    private static byte[]? HeldFile(ImportViewModel import) =>
        (byte[]?)typeof(ImportViewModel).GetField("_file", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(import);

    private static void AssertCleared(ImportViewModel import)
    {
        Assert.Equal(ImportStage.ChooseFile, import.Stage);
        Assert.Equal(ImportError.None, import.Error);
        Assert.Equal(0, import.ErrorLine);
        Assert.Null(import.FileName);
        Assert.False(import.IsCsv);
        Assert.Empty(import.PreviewRows);
        Assert.Equal(0, import.NewCount);
        Assert.Equal(0, import.DuplicateCount);
        Assert.Equal(0, import.WriteCount);
        Assert.Empty(import.RejectedLines);
        Assert.Empty(import.IgnoredColumns);
        Assert.Equal(DuplicatePolicy.Skip, import.Policy);
        Assert.Equal(0, import.AddedCount);
        Assert.Equal(0, import.ReplacedCount);
        Assert.Equal(0, import.SkippedCount);
        Assert.False(import.IsBusy);
        Assert.Null(HeldFile(import));
    }

    [Fact]
    public void Constructor_NullArgument_Throws()
    {
        var (_, list, _) = OpenImport();

        Assert.Throws<ArgumentNullException>(() => new ImportViewModel(null!, _navigation, _profiles.Time));
        Assert.Throws<ArgumentNullException>(() => new ImportViewModel(list, null!, _profiles.Time));
        Assert.Throws<ArgumentNullException>(() => new ImportViewModel(list, _navigation, null!));
    }

    [Fact]
    public async Task ChooseFile_Csv_ShowsPreviewAndWritesNothing()
    {
        var (profile, _, import) = OpenImport();
        RemoveBackup(profile);
        var path = WriteCsv("passwords.csv", DuplicateRow, NewRow);
        var source = File.ReadAllBytes(path);
        var before = VaultFolderSnapshot();

        await Choose(import, path);

        Assert.Equal(ImportStage.Preview, import.Stage);
        Assert.Equal(ImportError.None, import.Error);
        Assert.Equal("passwords.csv", import.FileName);
        Assert.True(import.IsCsv);
        Assert.Equal(1, import.NewCount);
        Assert.Equal(1, import.DuplicateCount);
        Assert.Equal(1, import.WriteCount);
        Assert.Equal(DuplicatePolicy.Skip, import.Policy);
        Assert.False(import.IsBusy);

        Assert.Equal(2, import.PreviewRows.Length);
        Assert.Equal("Mail", import.PreviewRows[0].Title);
        Assert.True(import.PreviewRows[0].IsDuplicate);
        Assert.Equal("Forum", import.PreviewRows[1].Title);
        Assert.Equal("me@example.com", import.PreviewRows[1].Username);
        Assert.Equal("https://forum.example.com", import.PreviewRows[1].Url);
        Assert.False(import.PreviewRows[1].IsDuplicate);

        // The page cannot show what the row does not have.
        Assert.Equal(
            ["IsDuplicate", "Title", "Url", "Username"],
            typeof(ImportPreviewRow).GetProperties().Select(property => property.Name).Order(StringComparer.Ordinal));

        AssertVaultFolderUnchanged(before);
        Assert.False(File.Exists(BackupPath(profile)));
        Assert.Equal(source, File.ReadAllBytes(path));
        Assert.True(import.ConfirmCommand.CanExecute(null));
    }

    [Fact]
    public async Task ChooseFile_Csv_ReportsRejectedLinesAndIgnoredColumns()
    {
        var (_, _, import) = OpenImport();
        var path = ImportPath("export.csv");
        File.WriteAllText(
            path,
            "name,password,login_totp\nForum,fictitious-1,fictitious-totp\n   ,fictitious-2,\nBoard,fictitious-3,\n",
            new UTF8Encoding(false));

        await Choose(import, path);

        Assert.Equal(ImportStage.Preview, import.Stage);
        Assert.Equal(2, import.NewCount);
        Assert.Equal([3], import.RejectedLines);
        Assert.Equal(["login_totp"], import.IgnoredColumns);
    }

    [Fact]
    public async Task ChooseFile_Pmvault_AsksPasswordBeforeDecrypting()
    {
        var (_, _, import) = OpenImport();
        var path = WritePmvault("backup.pmvault", TestProfiles.Entry("Forum"));

        await Choose(import, path);

        Assert.Equal(ImportStage.Password, import.Stage);
        Assert.Equal(ImportError.None, import.Error);
        Assert.Equal("backup.pmvault", import.FileName);
        Assert.False(import.IsCsv);
        Assert.Empty(import.PreviewRows);
        Assert.Equal(0, import.NewCount);
        Assert.False(import.ConfirmCommand.CanExecute(null));
        Assert.True(import.OpenVaultFileCommand.CanExecute(Array.Empty<char>()));
    }

    [Fact]
    public async Task ChooseFile_NotAVault_IsInvalidVaultFileWithoutPassword()
    {
        var (_, _, import) = OpenImport();
        var path = ImportPath("notes.pmvault");
        File.WriteAllText(path, "this is not a vault");

        await Choose(import, path);

        Assert.Equal(ImportError.InvalidVaultFile, import.Error);
        Assert.Equal(ImportStage.ChooseFile, import.Stage);
        Assert.Null(import.FileName);
        Assert.Null(HeldFile(import));
        Assert.False(import.OpenVaultFileCommand.CanExecute(Array.Empty<char>()));
    }

    [Fact]
    public async Task ChooseFile_OtherExtension_IsUnsupported()
    {
        var (_, _, import) = OpenImport();

        // The file does not exist: the extension alone refuses it, before any read.
        await Choose(import, ImportPath("passwords.txt"));

        Assert.Equal(ImportError.UnsupportedFile, import.Error);
        Assert.Equal(ImportStage.ChooseFile, import.Stage);
    }

    [Fact]
    public async Task ChooseFile_UpperCaseExtension_IsAccepted()
    {
        var (_, _, import) = OpenImport();

        await Choose(import, WriteCsv("PASSWORDS.CSV", NewRow));

        Assert.Equal(ImportStage.Preview, import.Stage);
        Assert.Equal("PASSWORDS.CSV", import.FileName);

        await Choose(import, WritePmvault("BACKUP.PMVAULT", TestProfiles.Entry("Forum")));

        Assert.Equal(ImportStage.Password, import.Stage);
        Assert.Equal("BACKUP.PMVAULT", import.FileName);
    }

    [Fact]
    public async Task ChooseFile_Canceled_KeepsStage()
    {
        var (_, _, import) = OpenImport();

        // On the first step: nothing changes, and an error from the last attempt goes away.
        await Choose(import, ImportPath("passwords.txt"));
        Assert.Equal(ImportError.UnsupportedFile, import.Error);
        await Choose(import, null);
        Assert.Equal(ImportStage.ChooseFile, import.Stage);
        Assert.Equal(ImportError.None, import.Error);

        // On the password step: the file stays, and its password still opens it.
        await Choose(import, WritePmvault("backup.pmvault", TestProfiles.Entry("Forum")));
        await Choose(import, null);
        Assert.Equal(ImportStage.Password, import.Stage);
        Assert.Equal("backup.pmvault", import.FileName);

        await import.OpenVaultFileCommand.ExecuteAsync(FilePassword.ToCharArray());

        Assert.Equal(ImportStage.Preview, import.Stage);
    }

    [Fact]
    public async Task ChooseFile_LockedWhilePicking_SetsNothing()
    {
        var (profile, list, import) = OpenImport();
        var before = VaultFolderSnapshot();
        var picker = new FakeExportFilePicker(WriteCsv("passwords.csv", NewRow))
        {
            BeforeReturn = () => list.LockCommand.Execute(null),
        };

        await import.ChooseFileCommand.ExecuteAsync(picker);

        Assert.Equal(1, picker.Calls);
        AssertCleared(import);
        Assert.Equal(profile, Assert.IsType<UnlockViewModel>(_navigation.Current).Profile);
        AssertVaultFolderUnchanged(before);
    }

    [Theory]
    [InlineData(BadFile.TooLarge, ImportError.TooLarge, 0)]
    [InlineData(BadFile.InvalidEncoding, ImportError.InvalidEncoding, 0)]
    [InlineData(BadFile.InvalidHeader, ImportError.InvalidHeader, 1)]
    [InlineData(BadFile.MalformedCsv, ImportError.MalformedCsv, 3)]
    [InlineData(BadFile.TooManyEntries, ImportError.TooManyEntries, VaultImport.MaxEntries + 2)]
    [InlineData(BadFile.Missing, ImportError.ReadFailed, 0)]
    public async Task ChooseFile_BadCsv_MapsEveryError(BadFile kind, ImportError expected, int line)
    {
        var (_, _, import) = OpenImport();
        var path = ImportPath("bad.csv");
        var utf8 = new UTF8Encoding(false);
        switch (kind)
        {
            case BadFile.TooLarge:
                using (var stream = File.Create(path))
                {
                    stream.SetLength(VaultImport.MaxFileBytes + 1L);
                }

                break;
            case BadFile.InvalidEncoding:
                File.WriteAllBytes(path, [.. utf8.GetBytes(CsvHeader + "\nForum,"), 0xFF, (byte)'\n']);
                break;
            case BadFile.InvalidHeader:
                File.WriteAllText(path, "col1,col2\nfictitious,fictitious\n", utf8);
                break;
            case BadFile.MalformedCsv:
                File.WriteAllText(path, CsvHeader + "\n" + NewRow + "\nFor\"um,,,fictitious,,\n", utf8);
                break;
            case BadFile.TooManyEntries:
                var rows = Enumerable.Repeat("Forum,,,fictitious,,", VaultImport.MaxEntries + 1);
                File.WriteAllText(path, CsvHeader + "\n" + string.Join('\n', rows) + "\n", utf8);
                break;
            case BadFile.Missing:
                break;
        }

        var before = VaultFolderSnapshot();

        await Choose(import, path);

        Assert.Equal(expected, import.Error);
        Assert.Equal(line, import.ErrorLine);
        Assert.Equal(ImportStage.ChooseFile, import.Stage);
        Assert.Empty(import.PreviewRows);
        Assert.False(import.IsBusy);
        AssertVaultFolderUnchanged(before);
    }

    [Fact]
    public async Task ChooseFile_FailedReadOnPasswordStep_DiscardsTheFirstFile()
    {
        var (_, _, import) = OpenImport();
        await Choose(import, WritePmvault("backup.pmvault", TestProfiles.Entry("Forum")));
        var held = Assert.IsType<byte[]>(HeldFile(import));

        await Choose(import, ImportPath("missing.csv"));

        Assert.Equal(ImportError.ReadFailed, import.Error);
        Assert.Equal(ImportStage.ChooseFile, import.Stage);
        Assert.Null(import.FileName);
        Assert.Null(HeldFile(import));
        Assert.All(held, value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task OpenVaultFile_RightPassword_ShowsPreviewAndZeroes()
    {
        var (_, list, import) = OpenImport();
        var forum = TestProfiles.Entry("Forum");
        var mail = TestProfiles.Entry("Mail") with { Id = list.Entries[0].Id };
        await Choose(import, WritePmvault("backup.pmvault", forum, mail));
        var held = Assert.IsType<byte[]>(HeldFile(import));
        var password = FilePassword.ToCharArray();

        await import.OpenVaultFileCommand.ExecuteAsync(password);

        Assert.Equal(ImportStage.Preview, import.Stage);
        Assert.Equal(ImportError.None, import.Error);
        Assert.Equal("backup.pmvault", import.FileName);
        Assert.False(import.IsCsv);
        Assert.Equal(1, import.NewCount);
        Assert.Equal(1, import.DuplicateCount);
        Assert.Equal(["Forum", "Mail"], import.PreviewRows.Select(row => row.Title));
        Assert.All(password, c => Assert.Equal('\0', c));
        Assert.Null(HeldFile(import));
        Assert.All(held, value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task OpenVaultFile_WrongPassword_StaysAndZeroes()
    {
        var (profile, _, import) = OpenImport();
        await Choose(import, WritePmvault("backup.pmvault", TestProfiles.Entry("Forum")));
        var password = WrongPassword.ToCharArray();

        await import.OpenVaultFileCommand.ExecuteAsync(password);

        Assert.Equal(ImportError.WrongPassword, import.Error);
        Assert.Equal(ImportStage.Password, import.Stage);
        Assert.Empty(import.PreviewRows);
        Assert.False(import.IsBusy);
        Assert.All(password, c => Assert.Equal('\0', c));

        // The file password is not the master password: the delay of the unlock screen is not involved.
        Assert.Equal(TimeSpan.Zero, _profiles.Throttle.RemainingDelay(profile.Id));

        // The file stays for another try, and the error goes away.
        await import.OpenVaultFileCommand.ExecuteAsync(FilePassword.ToCharArray());

        Assert.Equal(ImportStage.Preview, import.Stage);
        Assert.Equal(ImportError.None, import.Error);
    }

    [Fact]
    public async Task OpenVaultFile_TamperedFile_IsWrongPassword()
    {
        var (_, _, import) = OpenImport();
        var path = WritePmvault("backup.pmvault", TestProfiles.Entry("Forum"));
        var bytes = File.ReadAllBytes(path);
        bytes[^1] ^= 0x01;
        File.WriteAllBytes(path, bytes);
        await Choose(import, path);
        Assert.Equal(ImportStage.Password, import.Stage);

        await import.OpenVaultFileCommand.ExecuteAsync(FilePassword.ToCharArray());

        Assert.Equal(ImportError.WrongPassword, import.Error);
        Assert.Equal(ImportStage.Password, import.Stage);
        Assert.Empty(import.PreviewRows);
    }

    [Fact]
    public async Task OpenVaultFile_LockedWhileRunning_SetsNothing()
    {
        var (_, list, import) = OpenImport();

        // The real cost, so the lock comes while Argon2id runs.
        await Choose(import, WritePmvault("backup.pmvault", KdfParameters.Default, TestProfiles.Entry("Forum")));
        var held = Assert.IsType<byte[]>(HeldFile(import));
        var password = FilePassword.ToCharArray();

        var running = import.OpenVaultFileCommand.ExecuteAsync(password);
        Assert.False(running.IsCompleted);
        list.LockCommand.Execute(null);
        await running;

        AssertCleared(import);
        Assert.IsType<UnlockViewModel>(_navigation.Current);
        Assert.Empty(list.Entries);
        Assert.All(password, c => Assert.Equal('\0', c));
        Assert.All(held, value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task OpenVaultFile_TooManyEntries_DiscardsTheFile()
    {
        var (_, _, import) = OpenImport();
        var entries = Enumerable.Range(0, VaultImport.MaxEntries + 1).Select(_ => TestProfiles.Entry("Forum")).ToArray();
        await Choose(import, WritePmvault("backup.pmvault", entries));

        await import.OpenVaultFileCommand.ExecuteAsync(FilePassword.ToCharArray());

        Assert.Equal(ImportError.TooManyEntries, import.Error);
        Assert.Equal(ImportStage.ChooseFile, import.Stage);
        Assert.Null(HeldFile(import));
    }

    [Fact]
    public async Task OpenVaultFile_InvalidEntry_IsListedByPosition()
    {
        var (_, _, import) = OpenImport();
        await Choose(import, WritePmvault("backup.pmvault", TestProfiles.Entry("Forum"), TestProfiles.Entry("   ")));

        await import.OpenVaultFileCommand.ExecuteAsync(FilePassword.ToCharArray());

        Assert.Equal(ImportStage.Preview, import.Stage);
        Assert.Equal(1, import.NewCount);
        Assert.Equal([2], import.RejectedLines);
    }

    [Fact]
    public async Task OpenVaultFile_NoFileChosen_IsIgnoredAndZeroes()
    {
        var (_, _, import) = OpenImport();
        var password = FilePassword.ToCharArray();

        Assert.False(import.OpenVaultFileCommand.CanExecute(password));
        await import.OpenVaultFileCommand.ExecuteAsync(password);

        Assert.Equal(ImportStage.ChooseFile, import.Stage);
        Assert.Equal(ImportError.None, import.Error);
        Assert.All(password, c => Assert.Equal('\0', c));
    }

    [Fact]
    public async Task Confirm_Skip_WritesOnce()
    {
        var (profile, list, import) = OpenImport();
        var mail = list.Entries[0];

        // The entry the user last opened goes away with the list it belonged to.
        import.GoBackCommand.Execute(null);
        list.OpenEntryCommand.Execute(mail);
        Assert.IsType<EntryDetailViewModel>(_navigation.Current).GoBackCommand.Execute(null);
        Assert.Same(mail, list.LastOpened);
        list.OpenImportCommand.Execute(null);
        import = Assert.IsType<ImportViewModel>(_navigation.Current);

        var vaultBefore = File.ReadAllBytes(VaultPath(profile));
        var path = WriteCsv("passwords.csv", DuplicateRow, NewRow);
        var source = File.ReadAllBytes(path);
        await Choose(import, path);

        await import.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal(ImportStage.Done, import.Stage);
        Assert.Equal(ImportError.None, import.Error);
        Assert.Equal(1, import.AddedCount);
        Assert.Equal(0, import.ReplacedCount);
        Assert.Equal(1, import.SkippedCount);
        Assert.True(import.IsCsv);
        Assert.False(import.IsBusy);

        // One save: the .bak holds the version from before the import, not a step in between.
        Assert.Equal(vaultBefore, File.ReadAllBytes(BackupPath(profile)));
        Assert.Equal(source, File.ReadAllBytes(path));

        var saved = SavedEntries(profile);
        Assert.Equal(["Mail", "Bank", "Forum"], saved.Select(entry => entry.Title));
        Assert.Equal(mail, saved[0]);
        Assert.Equal("fictitious-forum-password", saved[2].Password);
        Assert.Equal(saved, list.Entries);

        // The list is rebuilt, search and folders included.
        Assert.Equal(3, list.FilteredEntries.Count);
        Assert.Contains("Hobbies", list.Folders);
        list.SearchText = "forum";
        Assert.Equal("Forum", Assert.Single(list.FilteredEntries).Title);
        Assert.Null(list.LastOpened);
    }

    [Fact]
    public async Task Confirm_Duplicate_AppendsEntries()
    {
        var (profile, list, import) = OpenImport();
        var mail = list.Entries[0];
        await Choose(import, WriteCsv("passwords.csv", DuplicateRow, NewRow));
        import.Policy = DuplicatePolicy.Duplicate;
        Assert.Equal(2, import.WriteCount);

        await import.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal(ImportStage.Done, import.Stage);
        Assert.Equal(2, import.AddedCount);
        Assert.Equal(0, import.ReplacedCount);
        Assert.Equal(0, import.SkippedCount);

        var saved = SavedEntries(profile);
        Assert.Equal(["Mail", "Bank", "Mail", "Forum"], saved.Select(entry => entry.Title));
        Assert.Equal(mail, saved[0]);
        Assert.Equal("fictitious-new-password", saved[2].Password);
        Assert.Equal(saved.Count, saved.Select(entry => entry.Id).Distinct().Count());
    }

    [Fact]
    public async Task Confirm_Replace_UpdatesInPlace()
    {
        var (profile, list, import) = OpenImport();
        var mail = list.Entries[0];
        await Choose(import, WriteCsv("passwords.csv", DuplicateRow, NewRow));
        import.Policy = DuplicatePolicy.Replace;
        Assert.Equal(2, import.WriteCount);

        // A clock with a part below the millisecond, which the saved date must not keep.
        _profiles.Time.Advance(TimeSpan.FromTicks(12_345_678));
        var now = _profiles.Time.GetUtcNow();
        await import.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal(ImportStage.Done, import.Stage);
        Assert.Equal(1, import.AddedCount);
        Assert.Equal(1, import.ReplacedCount);
        Assert.Equal(0, import.SkippedCount);

        var saved = SavedEntries(profile);
        Assert.Equal(["Mail", "Bank", "Forum"], saved.Select(entry => entry.Title));
        Assert.Equal(mail.Id, saved[0].Id);
        Assert.Equal(mail.CreatedAt, saved[0].CreatedAt);
        Assert.Equal("fictitious-new-password", saved[0].Password);
        Assert.Equal(mail.Notes, saved[0].Notes);
        Assert.Equal(mail.Folder, saved[0].Folder);
        Assert.Equal(new DateTimeOffset(now.Ticks - (now.Ticks % TimeSpan.TicksPerMillisecond), now.Offset), saved[0].UpdatedAt);
    }

    [Fact]
    public async Task Confirm_Pmvault_KeepsIdsAndDates()
    {
        var (profile, _, import) = OpenImport();
        var forum = TestProfiles.Entry("Forum") with { Favorite = true };
        await Choose(import, WritePmvault("backup.pmvault", forum));
        await import.OpenVaultFileCommand.ExecuteAsync(FilePassword.ToCharArray());

        await import.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal(ImportStage.Done, import.Stage);
        Assert.False(import.IsCsv);
        Assert.Equal(forum, SavedEntries(profile)[2]);
    }

    [Fact]
    public async Task Confirm_NothingToWrite_CannotExecute()
    {
        var (_, _, import) = OpenImport();
        await Choose(import, WriteCsv("passwords.csv", DuplicateRow));
        var before = VaultFolderSnapshot();

        Assert.Equal(0, import.WriteCount);
        Assert.False(import.ConfirmCommand.CanExecute(null));

        await import.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal(ImportStage.Preview, import.Stage);
        AssertVaultFolderUnchanged(before);

        // Another policy has something to write, and the command says so.
        var changes = 0;
        import.ConfirmCommand.CanExecuteChanged += (_, _) => changes++;
        import.Policy = DuplicatePolicy.Replace;
        Assert.True(import.ConfirmCommand.CanExecute(null));
        Assert.True(changes > 0);
    }

    [Fact]
    public async Task Confirm_WriteFails_IsSaveFailedAndStaysOnPreview()
    {
        var (profile, list, import) = OpenImport();
        var path = VaultPath(profile);
        var vaultBefore = File.ReadAllBytes(path);
        var entriesBefore = list.Entries;
        await Choose(import, WriteCsv("passwords.csv", NewRow));

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await import.ConfirmCommand.ExecuteAsync(null);
        }

        Assert.Equal(ImportError.SaveFailed, import.Error);
        Assert.Equal(ImportStage.Preview, import.Stage);
        Assert.Single(import.PreviewRows);
        Assert.False(import.IsBusy);
        Assert.Equal(vaultBefore, File.ReadAllBytes(path));
        Assert.Same(entriesBefore, list.Entries);

        // The file is free again: the same preview saves.
        Assert.True(import.ConfirmCommand.CanExecute(null));
        await import.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal(ImportStage.Done, import.Stage);
        Assert.Equal(ImportError.None, import.Error);
        Assert.Equal(3, SavedEntries(profile).Count);
        Assert.Equal(3, list.Entries.Count);
    }

    [Fact]
    public async Task Confirm_LockAfterSave_DoesNotRepopulateList()
    {
        var (profile, list, import) = OpenImport();
        await Choose(import, WriteCsv("passwords.csv", NewRow));
        RemoveBackup(profile);

        // Until this context is pumped, Confirm's continuation waits, as if the UI thread were busy with the lock.
        var context = new QueuedSynchronizationContext();
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);
        Task running;
        try
        {
            running = import.ConfirmCommand.ExecuteAsync(null);
            Assert.True(SpinWait.SpinUntil(() => File.Exists(BackupPath(profile)), WaitLimit));

            // It waits on the session gate until the save ends.
            list.LockCommand.Execute(null);

            var ended = running;
            Assert.True(SpinWait.SpinUntil(
                () =>
                {
                    context.Pump();
                    return ended.IsCompleted;
                },
                WaitLimit));
            context.Pump();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        await running;

        Assert.Empty(list.Entries);
        Assert.Empty(list.FilteredEntries);
        Assert.Empty(list.Folders);
        Assert.Null(list.LastOpened);
        AssertCleared(import);
        Assert.IsType<UnlockViewModel>(_navigation.Current);

        // The save itself had already ended, so it stays on disk.
        Assert.Equal(3, SavedEntries(profile).Count);
    }

    [Fact]
    public async Task Confirm_Done_DropsPreviewData()
    {
        var (_, _, import) = OpenImport();
        await Choose(import, WriteCsv("passwords.csv", NewRow, DuplicateRow));

        await import.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal(ImportStage.Done, import.Stage);
        Assert.Empty(import.PreviewRows);
        Assert.Empty(import.RejectedLines);
        Assert.Empty(import.IgnoredColumns);
        Assert.Null(import.FileName);
        Assert.Equal(0, import.NewCount);
        Assert.Equal(0, import.DuplicateCount);
        Assert.Null(typeof(ImportViewModel).GetField("_incoming", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(import));
        Assert.False(import.ConfirmCommand.CanExecute(null));

        // The counts of the done page stay until it is left.
        Assert.Equal(1, import.AddedCount);
        Assert.Equal(1, import.SkippedCount);
        Assert.True(import.GoBackCommand.CanExecute(null));
    }

    [Fact]
    public async Task GoBack_FromPreview_WritesNothingAndClears()
    {
        var (_, list, import) = OpenImport();
        await Choose(import, WriteCsv("passwords.csv", NewRow));
        var before = VaultFolderSnapshot();
        var entries = list.Entries;

        import.GoBackCommand.Execute(null);

        Assert.Same(list, _navigation.Current);
        AssertCleared(import);
        AssertVaultFolderUnchanged(before);
        Assert.Same(entries, list.Entries);
        Assert.True(list.IsOpen);
    }

    [Fact]
    public async Task GoBack_WhileBusy_IsBlocked()
    {
        var (_, _, import) = OpenImport();
        var picker = new FakeExportFilePicker(null);
        picker.Block();

        var running = import.ChooseFileCommand.ExecuteAsync(picker);

        Assert.True(import.IsBusy);
        Assert.False(import.GoBackCommand.CanExecute(null));
        Assert.False(import.ChooseFileCommand.CanExecute(picker));

        // A direct call is ignored too.
        import.GoBackCommand.Execute(null);
        Assert.Same(import, _navigation.Current);

        // And a second dialog does not open.
        await import.ChooseFileCommand.ExecuteAsync(picker);
        Assert.Equal(1, picker.Calls);

        picker.Release();
        await running;

        Assert.False(import.IsBusy);
        Assert.True(import.GoBackCommand.CanExecute(null));
        import.GoBackCommand.Execute(null);
        Assert.NotSame(import, _navigation.Current);
    }

    [Fact]
    public async Task OnNavigatedFrom_ClearsEverything()
    {
        var (_, _, import) = OpenImport();

        // The password step: the bytes are zeroed.
        await Choose(import, WritePmvault("backup.pmvault", TestProfiles.Entry("Forum")));
        await import.OpenVaultFileCommand.ExecuteAsync(WrongPassword.ToCharArray());
        Assert.Equal(ImportError.WrongPassword, import.Error);
        var held = Assert.IsType<byte[]>(HeldFile(import));

        import.OnNavigatedFrom();

        AssertCleared(import);
        Assert.All(held, value => Assert.Equal(0, value));

        // The preview: rows, counts and the policy go.
        var path = ImportPath("export.csv");
        File.WriteAllText(path, "name,password,login_totp\nForum,fictitious-1,fictitious-totp\n   ,fictitious-2,\n", new UTF8Encoding(false));
        await Choose(import, path);
        import.Policy = DuplicatePolicy.Replace;
        Assert.NotEmpty(import.IgnoredColumns);
        Assert.NotEmpty(import.RejectedLines);

        import.OnNavigatedFrom();

        AssertCleared(import);

        // The done page: its counts go.
        await Choose(import, WriteCsv("passwords.csv", NewRow));
        await import.ConfirmCommand.ExecuteAsync(null);
        Assert.Equal(1, import.AddedCount);

        import.OnNavigatedFrom();

        AssertCleared(import);
    }

    // Holds every continuation until the test asks for them, on the test's own thread.
    private sealed class QueuedSynchronizationContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _queue = new();

        public override void Post(SendOrPostCallback d, object? state) => _queue.Enqueue((d, state));

        public void Pump()
        {
            while (_queue.TryDequeue(out var item))
            {
                item.Callback(item.State);
            }
        }
    }
}
