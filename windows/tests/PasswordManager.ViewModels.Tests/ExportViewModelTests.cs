using PasswordManager.Core.Profiles;
using PasswordManager.Core.Vault;
using PasswordManager.ViewModels.Navigation;

namespace PasswordManager.ViewModels.Tests;

public sealed class ExportViewModelTests : IDisposable
{
    // Fictitious. 30 characters.
    private const string ExportPassword = "a fictitious export passphrase";

    private const string OtherPassword = "another fictitious passphrase";

    private const string BackupName = "backup.pmvault";

    private readonly TestProfiles _profiles = new();
    private readonly NavigationService _navigation = new();

    // The folder the user picks. A sibling of the vaults folder, never inside it.
    private readonly DirectoryInfo _exports = Directory.CreateTempSubdirectory("pm-viewmodels-exports-");

    private readonly List<VaultSession> _sessions = [];

    public enum RefusedPath
    {
        Plain,
        UpperCase,
        TrailingSeparator,
        DotDotSegment,
        TheVaultItself,
    }

    public void Dispose()
    {
        foreach (var session in _sessions)
        {
            session.Dispose();
        }

        _profiles.Dispose();
        if (Directory.Exists(_exports.FullName))
        {
            _exports.Delete(recursive: true);
        }
    }

    private string ExportPath(string name = BackupName) => Path.Combine(_exports.FullName, name);

    // Profiles -> Unlock -> list -> Export, with an open vault of two entries. The session costs little by default; the
    // real cost (64 MiB, 3 passes) is only for the test that checks the parameters the app writes.
    private (Profile Profile, EntryListViewModel List, ExportViewModel Export) OpenExport(
        bool realCost = false,
        ProfileRepository? repository = null)
    {
        var profile = _profiles.CreateProfile(
            "Alice",
            TestProfiles.Password,
            TestProfiles.Entry("Mail"),
            TestProfiles.Entry("Bank"));

        VaultSession session;
        if (realCost)
        {
            session = new VaultSession(_profiles.Repository.GetVaultPath(profile.Id));
            session.Unlock(TestProfiles.Password);
        }
        else
        {
            session = _profiles.OpenSession(profile);
        }

        _sessions.Add(session);
        var list = new EntryListViewModel(
            profile,
            session,
            repository ?? _profiles.Repository,
            _navigation,
            _profiles.Throttle,
            _profiles.Clipboard);
        _navigation.ResetTo(list);
        list.OpenExportCommand.Execute(null);

        return (profile, list, Assert.IsType<ExportViewModel>(_navigation.Current));
    }

    private static ExportRequest Request(string password, IExportFilePicker picker, string? confirmation = null) =>
        new(password.ToCharArray(), (confirmation ?? password).ToCharArray(), picker);

    private static void AssertZeroed(ExportRequest request)
    {
        Assert.NotEmpty(request.Password);
        Assert.NotEmpty(request.Confirmation);
        Assert.All(request.Password, c => Assert.Equal('\0', c));
        Assert.All(request.Confirmation, c => Assert.Equal('\0', c));
    }

    // Opens any vault file with the Core, the way the app that imports it would.
    private static VaultSession OpenFile(string path, string password)
    {
        var session = new VaultSession(path, TestProfiles.LowCost);
        try
        {
            session.Unlock(password);
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    private static byte[] PasswordSlot(byte[] file)
    {
        var range = VaultHeader.Parse(file).Slots.Single(slot => slot.Type == VaultFormat.SlotTypePassword);

        return file.AsSpan(range.Offset, range.Length).ToArray();
    }

    private static byte[] Salt(byte[] slot) =>
        slot[VaultFormat.SlotSaltOffset..(VaultFormat.SlotSaltOffset + VaultFormat.SaltSize)];

    private static byte[] WrapNonce(byte[] slot) => slot[VaultFormat.SlotWrapNonceOffset..VaultFormat.SlotWrappedKeyOffset];

    private static byte[] WrappedKey(byte[] slot) => slot[VaultFormat.SlotWrappedKeyOffset..];

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

    private string[] ExportFolderNames() =>
        [.. Directory.GetFileSystemEntries(_exports.FullName).Select(path => Path.GetFileName(path)).Order(StringComparer.Ordinal)];

    [Fact]
    public void Constructor_NullArgument_Throws()
    {
        var (_, list, _) = OpenExport();

        Assert.Throws<ArgumentNullException>(() => new ExportViewModel(null!, _navigation));
        Assert.Throws<ArgumentNullException>(() => new ExportViewModel(list, null!));
    }

    [Fact]
    public void Request_NullArgument_Throws()
    {
        var picker = new FakeExportFilePicker(null);

        Assert.Throws<ArgumentNullException>(() => new ExportRequest(null!, [], picker));
        Assert.Throws<ArgumentNullException>(() => new ExportRequest([], null!, picker));
        Assert.Throws<ArgumentNullException>(() => new ExportRequest([], [], null!));
    }

    [Fact]
    public async Task Export_Valid_WritesOnlyTheChosenFile()
    {
        // The real cost: the export carries the parameters that the app writes.
        var (_, list, export) = OpenExport(realCost: true);
        var target = ExportPath();
        var picker = new FakeExportFilePicker(target);
        var request = Request(ExportPassword, picker);

        await export.ExportCommand.ExecuteAsync(request);

        Assert.Equal(ExportOutcome.Saved, export.Outcome);
        Assert.Equal(BackupName, export.SavedFileName);
        Assert.Equal(NewPasswordError.None, export.PasswordError);
        Assert.Equal(1, picker.Calls);
        Assert.Equal([BackupName], ExportFolderNames());

        var bytes = File.ReadAllBytes(target);
        var header = VaultHeader.Parse(bytes);
        var slot = Assert.Single(header.Slots);
        Assert.Equal(VaultFormat.SlotTypePassword, slot.Type);
        Assert.Null(header.RecoveryKitSlotOffset);
        Assert.Equal(KdfParameters.Default, KdfParameters.ReadFromSlot(bytes.AsSpan(slot.Offset, slot.Length)));

        using (var opened = OpenFile(target, ExportPassword))
        {
            Assert.Equal(list.Entries, opened.Content.Entries);
        }

        Assert.Throws<VaultAuthenticationException>(() => OpenFile(target, TestProfiles.Password));
        AssertZeroed(request);
    }

    [Fact]
    public async Task Export_Twice_UsesNewSaltAndWrappedKeyEachTime()
    {
        var (profile, _, export) = OpenExport();
        var picker = new FakeExportFilePicker(ExportPath("first.pmvault"));

        await export.ExportCommand.ExecuteAsync(Request(ExportPassword, picker));
        picker.Path = ExportPath("second.pmvault");
        await export.ExportCommand.ExecuteAsync(Request(ExportPassword, picker));

        Assert.Equal(ExportOutcome.Saved, export.Outcome);
        Assert.Equal("second.pmvault", export.SavedFileName);
        var slots = new[]
        {
            PasswordSlot(File.ReadAllBytes(_profiles.Repository.GetVaultPath(profile.Id))),
            PasswordSlot(File.ReadAllBytes(ExportPath("first.pmvault"))),
            PasswordSlot(File.ReadAllBytes(ExportPath("second.pmvault"))),
        };

        // The vault's own slot and the two exports: nothing is shared, so no key, salt or nonce is ever reused.
        foreach (var part in new Func<byte[], byte[]>[] { Salt, WrapNonce, WrappedKey })
        {
            Assert.Equal(slots.Length, slots.Select(slot => Convert.ToHexString(part(slot))).Distinct().Count());
        }
    }

    [Fact]
    public async Task Export_LeavesTheVaultFolderUnchanged()
    {
        var (_, _, export) = OpenExport();
        var before = VaultFolderSnapshot();

        await export.ExportCommand.ExecuteAsync(Request(ExportPassword, new FakeExportFilePicker(ExportPath())));

        Assert.Equal(ExportOutcome.Saved, export.Outcome);

        // Same files with the same bytes: no new .bak (the vault was not written), no .tmp, and no export copy.
        AssertVaultFolderUnchanged(before);
    }

    [Fact]
    public async Task Export_TooShort_NeverOpensPicker()
    {
        var (_, _, export) = OpenExport();
        var picker = new FakeExportFilePicker(ExportPath());
        var request = Request("short", picker);

        await export.ExportCommand.ExecuteAsync(request);

        Assert.Equal(NewPasswordError.TooShort, export.PasswordError);
        Assert.Equal(ExportOutcome.None, export.Outcome);
        Assert.Equal(0, picker.Calls);
        Assert.Empty(ExportFolderNames());
        AssertZeroed(request);
    }

    [Fact]
    public async Task Export_Mismatch_NeverOpensPicker()
    {
        var (_, _, export) = OpenExport();
        var picker = new FakeExportFilePicker(ExportPath());
        var request = Request(ExportPassword, picker, OtherPassword);

        await export.ExportCommand.ExecuteAsync(request);

        Assert.Equal(NewPasswordError.Mismatch, export.PasswordError);
        Assert.Equal(ExportOutcome.None, export.Outcome);
        Assert.Equal(0, picker.Calls);
        Assert.Empty(ExportFolderNames());
        AssertZeroed(request);
    }

    [Fact]
    public async Task Export_ShortAndDifferent_ReportsTooShort()
    {
        var (_, _, export) = OpenExport();

        await export.ExportCommand.ExecuteAsync(Request("short", new FakeExportFilePicker(ExportPath()), "other"));

        Assert.Equal(NewPasswordError.TooShort, export.PasswordError);
    }

    [Fact]
    public async Task Export_ErrorsFromAnAttempt_AreClearedByTheNextOne()
    {
        var (_, _, export) = OpenExport();
        var picker = new FakeExportFilePicker(ExportPath());
        await export.ExportCommand.ExecuteAsync(Request("short", picker));
        Assert.Equal(NewPasswordError.TooShort, export.PasswordError);

        await export.ExportCommand.ExecuteAsync(Request(ExportPassword, picker));

        Assert.Equal(NewPasswordError.None, export.PasswordError);
        Assert.Equal(ExportOutcome.Saved, export.Outcome);

        // A refused attempt after a save also clears the file name of the save.
        await export.ExportCommand.ExecuteAsync(Request("short", picker));

        Assert.Equal(NewPasswordError.TooShort, export.PasswordError);
        Assert.Equal(ExportOutcome.None, export.Outcome);
        Assert.Null(export.SavedFileName);
    }

    [Fact]
    public async Task Export_PickerCanceled_WritesNothing()
    {
        var (_, _, export) = OpenExport();
        var before = VaultFolderSnapshot();
        var picker = new FakeExportFilePicker(null);
        var request = Request(ExportPassword, picker);

        await export.ExportCommand.ExecuteAsync(request);

        Assert.Equal(1, picker.Calls);
        Assert.Equal(ExportOutcome.None, export.Outcome);
        Assert.Null(export.SavedFileName);
        Assert.Equal(NewPasswordError.None, export.PasswordError);
        Assert.Empty(ExportFolderNames());
        AssertVaultFolderUnchanged(before);
        AssertZeroed(request);
        Assert.Same(export, _navigation.Current);
        Assert.True(export.ExportCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(RefusedPath.Plain)]
    [InlineData(RefusedPath.UpperCase)]
    [InlineData(RefusedPath.TrailingSeparator)]
    [InlineData(RefusedPath.DotDotSegment)]
    [InlineData(RefusedPath.TheVaultItself)]
    public async Task Export_IntoVaultsFolder_IsRefused(RefusedPath variant)
    {
        // A repository whose folder is spelled with a trailing separator names the same folder.
        var repository = variant == RefusedPath.TrailingSeparator
            ? new ProfileRepository(_profiles.DirectoryPath + Path.DirectorySeparatorChar)
            : null;
        var (profile, _, export) = OpenExport(repository: repository);
        var vaults = _profiles.DirectoryPath;
        var target = variant switch
        {
            RefusedPath.UpperCase => Path.Combine(vaults.ToUpperInvariant(), BackupName),
            RefusedPath.DotDotSegment => Path.Combine(vaults, "sub", "..", BackupName),
            RefusedPath.TheVaultItself => _profiles.Repository.GetVaultPath(profile.Id),
            _ => Path.Combine(vaults, BackupName),
        };
        var before = VaultFolderSnapshot();
        var picker = new FakeExportFilePicker(target);
        var request = Request(ExportPassword, picker);

        await export.ExportCommand.ExecuteAsync(request);

        Assert.Equal(1, picker.Calls);
        Assert.Equal(ExportOutcome.VaultsFolder, export.Outcome);
        Assert.Null(export.SavedFileName);
        AssertVaultFolderUnchanged(before);
        Assert.Empty(ExportFolderNames());
        AssertZeroed(request);
    }

    [Fact]
    public async Task Export_IntoSubfolderOfVaultsFolder_IsAllowed()
    {
        var (_, list, export) = OpenExport();
        var subfolder = Path.Combine(_profiles.DirectoryPath, "exports");
        Directory.CreateDirectory(subfolder);
        var before = VaultFolderSnapshot();
        var target = Path.Combine(subfolder, BackupName);

        await export.ExportCommand.ExecuteAsync(Request(ExportPassword, new FakeExportFilePicker(target)));

        Assert.Equal(ExportOutcome.Saved, export.Outcome);
        Assert.Equal([BackupName], Directory.GetFileSystemEntries(subfolder).Select(path => Path.GetFileName(path)).ToArray());
        AssertVaultFolderUnchanged(before);
        using var opened = OpenFile(target, ExportPassword);
        Assert.Equal(list.Entries, opened.Content.Entries);
    }

    [Fact]
    public async Task Export_ExistingFile_IsReplaced()
    {
        var (_, list, export) = OpenExport();
        var target = ExportPath();
        File.WriteAllBytes(target, [1, 2, 3, 4]);

        await export.ExportCommand.ExecuteAsync(Request(ExportPassword, new FakeExportFilePicker(target)));

        Assert.Equal(ExportOutcome.Saved, export.Outcome);

        // Replaced in place: no .tmp left behind and no .bak of the old file.
        Assert.Equal([BackupName], ExportFolderNames());
        using var opened = OpenFile(target, ExportPassword);
        Assert.Equal(list.Entries, opened.Content.Entries);
    }

    [Fact]
    public async Task Export_WriteFails_IsFailedAndKeepsExistingFile()
    {
        var (_, _, export) = OpenExport();
        var target = ExportPath();
        byte[] original = [1, 2, 3, 4];
        File.WriteAllBytes(target, original);
        var request = Request(ExportPassword, new FakeExportFilePicker(target));

        using (new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await export.ExportCommand.ExecuteAsync(request);
        }

        Assert.Equal(ExportOutcome.Failed, export.Outcome);
        Assert.Null(export.SavedFileName);
        Assert.Equal(original, File.ReadAllBytes(target));
        Assert.Equal([BackupName], ExportFolderNames());
        AssertZeroed(request);
        Assert.Same(export, _navigation.Current);
    }

    [Fact]
    public async Task Export_MissingFolder_IsFailed()
    {
        var (_, _, export) = OpenExport();
        var missing = Path.Combine(_exports.FullName, "missing");
        var request = Request(ExportPassword, new FakeExportFilePicker(Path.Combine(missing, BackupName)));

        await export.ExportCommand.ExecuteAsync(request);

        Assert.Equal(ExportOutcome.Failed, export.Outcome);
        Assert.Null(export.SavedFileName);
        Assert.False(Directory.Exists(missing));
        Assert.Empty(ExportFolderNames());
        AssertZeroed(request);
    }

    [Fact]
    public async Task Export_PasswordWithLoneSurrogate_IsFailedAndWritesNothing()
    {
        var (_, _, export) = OpenExport();
        var request = Request("\uD800" + "abcdefghijk", new FakeExportFilePicker(ExportPath()));

        await export.ExportCommand.ExecuteAsync(request);

        // The key derivation refuses the password, after the rule accepted its length.
        Assert.Equal(NewPasswordError.None, export.PasswordError);
        Assert.Equal(ExportOutcome.Failed, export.Outcome);
        Assert.Empty(ExportFolderNames());
        AssertZeroed(request);
    }

    [Fact]
    public async Task Export_LockedWhilePicking_WritesNothingAndZeroes()
    {
        var (profile, list, export) = OpenExport();
        var picker = new FakeExportFilePicker(ExportPath()) { BeforeReturn = () => list.LockCommand.Execute(null) };
        var request = Request(ExportPassword, picker);

        await export.ExportCommand.ExecuteAsync(request);

        // The vault was locked while the dialog was open: no Argon2id, no file, and no message on a page that is gone.
        Assert.False(list.IsOpen);
        Assert.Equal(ExportOutcome.None, export.Outcome);
        Assert.Null(export.SavedFileName);
        Assert.Empty(ExportFolderNames());
        AssertZeroed(request);
        Assert.Equal(profile, Assert.IsType<UnlockViewModel>(_navigation.Current).Profile);
    }

    [Fact]
    public async Task GoBack_WhileExportRuns_IsBlocked()
    {
        var (_, list, export) = OpenExport();
        var picker = new FakeExportFilePicker(null);
        picker.Block();

        var running = export.ExportCommand.ExecuteAsync(Request(ExportPassword, picker));

        Assert.True(export.ExportCommand.IsRunning);
        Assert.False(export.GoBackCommand.CanExecute(null));
        export.GoBackCommand.Execute(null);
        Assert.Same(export, _navigation.Current);

        picker.Release();
        await running;

        Assert.False(export.ExportCommand.IsRunning);
        Assert.True(export.GoBackCommand.CanExecute(null));
        export.GoBackCommand.Execute(null);
        Assert.Same(list, _navigation.Current);
    }

    [Fact]
    public async Task OnNavigatedFrom_ClearsOutcomeAndErrors()
    {
        var (_, list, export) = OpenExport();
        var picker = new FakeExportFilePicker(ExportPath());

        await export.ExportCommand.ExecuteAsync(Request("short", picker));
        Assert.Equal(NewPasswordError.TooShort, export.PasswordError);
        export.GoBackCommand.Execute(null);
        Assert.Equal(NewPasswordError.None, export.PasswordError);

        list.OpenExportCommand.Execute(null);
        var again = Assert.IsType<ExportViewModel>(_navigation.Current);
        await again.ExportCommand.ExecuteAsync(Request(ExportPassword, picker));
        Assert.Equal(ExportOutcome.Saved, again.Outcome);
        Assert.NotNull(again.SavedFileName);
        again.GoBackCommand.Execute(null);

        Assert.Equal(ExportOutcome.None, again.Outcome);
        Assert.Null(again.SavedFileName);
        Assert.Equal(NewPasswordError.None, again.PasswordError);
    }
}
