using System.Text;
using PasswordManager.Core.Profiles;
using PasswordManager.Core.Vault;
using PasswordManager.ViewModels.Navigation;

namespace PasswordManager.ViewModels.Tests;

public sealed class CsvExportViewModelTests : IDisposable
{
    // Fictitious.
    private const string WrongPassword = "a fictitious wrong passphrase";

    private const string CsvName = "senhas.csv";

    private readonly TestProfiles _profiles = new();
    private readonly NavigationService _navigation = new();

    // The folder the user picks. A sibling of the vaults folder, never inside it.
    private readonly DirectoryInfo _exports = Directory.CreateTempSubdirectory("pm-viewmodels-csv-");

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

    private string ExportPath(string name = CsvName) => Path.Combine(_exports.FullName, name);

    // Profiles -> Unlock -> list -> Export -> CSV, with an open vault of two entries, one of them with commas, quotes and
    // line breaks. The vault costs little (LowCost); the page does not choose the parameters, the vault slot does.
    private (Profile Profile, EntryListViewModel List, ExportViewModel Export, CsvExportViewModel Csv) OpenCsv(
        bool acknowledged = true,
        ProfileRepository? repository = null)
    {
        var profile = _profiles.CreateProfile(
            "Alice",
            TestProfiles.Password,
            TestProfiles.Entry("Mail, personal") with { Notes = "line 1\nline \"2\"", Folder = "A,B" },
            TestProfiles.Entry("Bank"));

        var session = _profiles.OpenSession(profile);
        _sessions.Add(session);
        var list = new EntryListViewModel(
            profile,
            session,
            repository ?? _profiles.Repository,
            _navigation,
            _profiles.Throttle,
            _profiles.Clipboard,
            _profiles.Settings);
        _navigation.ResetTo(list);
        list.OpenExportCommand.Execute(null);
        var export = Assert.IsType<ExportViewModel>(_navigation.Current);
        export.OpenCsvExportCommand.Execute(null);
        var csv = Assert.IsType<CsvExportViewModel>(_navigation.Current);
        csv.Acknowledged = acknowledged;

        return (profile, list, export, csv);
    }

    private static CsvExportRequest Request(string password, IExportFilePicker picker) => new(password.ToCharArray(), picker);

    private static void AssertZeroed(CsvExportRequest request)
    {
        Assert.NotEmpty(request.MasterPassword);
        Assert.All(request.MasterPassword, c => Assert.Equal('\0', c));
    }

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
        var (_, list, _, _) = OpenCsv();

        Assert.Throws<ArgumentNullException>(() => new CsvExportViewModel(null!, _navigation));
        Assert.Throws<ArgumentNullException>(() => new CsvExportViewModel(list, null!));
    }

    [Fact]
    public void Request_NullArgument_Throws()
    {
        var picker = new FakeExportFilePicker(null);

        Assert.Throws<ArgumentNullException>(() => new CsvExportRequest(null!, picker));
        Assert.Throws<ArgumentNullException>(() => new CsvExportRequest([], null!));
    }

    [Fact]
    public async Task ExportAsync_NullRequest_Throws()
    {
        var (_, _, _, csv) = OpenCsv();

        await Assert.ThrowsAsync<ArgumentNullException>(() => csv.ExportCommand.ExecuteAsync(null));
    }

    [Fact]
    public void Page_StartsNotAcknowledgedAndCannotExport()
    {
        var (_, _, _, csv) = OpenCsv(acknowledged: false);

        Assert.False(csv.Acknowledged);
        Assert.Equal(0, csv.RetrySeconds);
        Assert.Equal(ExportOutcome.None, csv.Outcome);
        Assert.Null(csv.SavedFileName);
        Assert.False(csv.ExportCommand.CanExecute(null));
        Assert.False(csv.AllowsScreenCapture);

        csv.Acknowledged = true;

        Assert.True(csv.ExportCommand.CanExecute(null));
    }

    [Fact]
    public async Task Export_RightPassword_WritesOnlyTheChosenFile()
    {
        var (_, list, _, csv) = OpenCsv();
        var target = ExportPath();
        var picker = new FakeExportFilePicker(target);
        var request = Request(TestProfiles.Password, picker);
        var before = VaultFolderSnapshot();

        await csv.ExportCommand.ExecuteAsync(request);

        Assert.Equal(ExportOutcome.Saved, csv.Outcome);
        Assert.Equal(CsvName, csv.SavedFileName);
        Assert.Equal(1, picker.Calls);
        Assert.Equal([CsvName], ExportFolderNames());

        var bytes = File.ReadAllBytes(target);
        Assert.Equal(VaultCsv.Serialize(list.Entries), bytes);
        Assert.StartsWith(VaultCsv.HeaderLine + "\r\n", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);

        // The commas, quotes and line breaks of the first entry are quoted: the file is a real CSV.
        var text = Encoding.UTF8.GetString(bytes);
        Assert.Contains("\"Mail, personal\"", text, StringComparison.Ordinal);
        Assert.Contains("\"line 1\nline \"\"2\"\"\"", text, StringComparison.Ordinal);
        Assert.Contains("\"A,B\"", text, StringComparison.Ordinal);

        AssertZeroed(request);
        Assert.True(list.IsOpen);

        // The vault was not written: same files and bytes, so no .bak, no .tmp and no copy of the export.
        AssertVaultFolderUnchanged(before);
        Assert.Same(csv, _navigation.Current);
        Assert.True(csv.ExportCommand.CanExecute(null));
    }

    [Fact]
    public async Task Export_RightPassword_ZeroesThePasswordWhileTheDialogIsOpen()
    {
        var (_, _, _, csv) = OpenCsv();
        var picker = new FakeExportFilePicker(ExportPath());
        picker.Block();
        var request = Request(TestProfiles.Password, picker);

        var running = csv.ExportCommand.ExecuteAsync(request);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (picker.Calls == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.Equal(1, picker.Calls);

        // The dialog is open: the password was checked and is already gone from memory.
        Assert.True(csv.ExportCommand.IsRunning);
        AssertZeroed(request);

        picker.Release();
        await running;

        Assert.Equal(ExportOutcome.Saved, csv.Outcome);
    }

    [Fact]
    public async Task Export_WrongPassword_NeverOpensPickerAndCountsFailure()
    {
        var (profile, list, _, csv) = OpenCsv();
        var picker = new FakeExportFilePicker(ExportPath());
        var request = Request(WrongPassword, picker);

        await csv.ExportCommand.ExecuteAsync(request);

        Assert.Equal(0, picker.Calls);
        Assert.Equal(ExportOutcome.WrongPassword, csv.Outcome);
        Assert.Null(csv.SavedFileName);
        Assert.Equal(1, csv.RetrySeconds);
        Assert.True(_profiles.Throttle.RemainingDelay(profile.Id) > TimeSpan.Zero);
        Assert.False(csv.ExportCommand.CanExecute(null));
        Assert.Empty(ExportFolderNames());
        AssertZeroed(request);
        Assert.True(list.IsOpen);
        Assert.Same(csv, _navigation.Current);
    }

    [Fact]
    public async Task Export_PasswordWithLoneSurrogate_IsAWrongPassword()
    {
        var (profile, _, _, csv) = OpenCsv();
        var picker = new FakeExportFilePicker(ExportPath());
        var request = Request("\uD800" + "abcdefghijk", picker);

        await csv.ExportCommand.ExecuteAsync(request);

        // The key derivation refuses the password: it is counted like any other wrong one, and the dialog never opens.
        Assert.Equal(ExportOutcome.WrongPassword, csv.Outcome);
        Assert.Equal(0, picker.Calls);
        Assert.Equal(TimeSpan.FromSeconds(1), _profiles.Throttle.RemainingDelay(profile.Id));
        Assert.Empty(ExportFolderNames());
        AssertZeroed(request);
    }

    [Fact]
    public async Task Export_WrongPasswordAfterTheDelay_DoublesIt()
    {
        var (profile, _, _, csv) = OpenCsv();
        var picker = new FakeExportFilePicker(ExportPath());

        await csv.ExportCommand.ExecuteAsync(Request(WrongPassword, picker));
        Assert.Equal(TimeSpan.FromSeconds(1), _profiles.Throttle.RemainingDelay(profile.Id));
        _profiles.Time.Advance(TimeSpan.FromSeconds(1));
        csv.RefreshRetryDelay();
        Assert.Equal(0, csv.RetrySeconds);
        Assert.True(csv.ExportCommand.CanExecute(null));

        await csv.ExportCommand.ExecuteAsync(Request(WrongPassword, picker));

        Assert.Equal(ExportOutcome.WrongPassword, csv.Outcome);
        Assert.Equal(2, csv.RetrySeconds);
        Assert.Equal(TimeSpan.FromSeconds(2), _profiles.Throttle.RemainingDelay(profile.Id));
        Assert.Equal(0, picker.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Export_DuringRetryDelay_IsIgnored(bool rightPassword)
    {
        var (profile, _, _, csv) = OpenCsv();

        // A wrong password on the unlock screen of the same profile: the delay is shared.
        _profiles.Throttle.RecordFailure(profile.Id);
        var picker = new FakeExportFilePicker(ExportPath());
        var request = Request(rightPassword ? TestProfiles.Password : WrongPassword, picker);

        await csv.ExportCommand.ExecuteAsync(request);

        // No Argon2id and nothing counted: a right password did not reset the delay, a wrong one did not double it.
        Assert.Equal(TimeSpan.FromSeconds(1), _profiles.Throttle.RemainingDelay(profile.Id));
        Assert.Equal(1, csv.RetrySeconds);
        Assert.Equal(ExportOutcome.None, csv.Outcome);
        Assert.Equal(0, picker.Calls);
        Assert.Empty(ExportFolderNames());
        AssertZeroed(request);
    }

    [Fact]
    public void OnNavigatedTo_PicksUpTheDelayOfTheUnlockScreen()
    {
        var (profile, list, export, csv) = OpenCsv();
        csv.GoBackCommand.Execute(null);
        Assert.Same(export, _navigation.Current);

        _profiles.Throttle.RecordFailure(profile.Id);
        export.OpenCsvExportCommand.Execute(null);

        var again = Assert.IsType<CsvExportViewModel>(_navigation.Current);
        Assert.NotSame(csv, again);
        Assert.Equal(1, again.RetrySeconds);
        again.Acknowledged = true;
        Assert.False(again.ExportCommand.CanExecute(null));
        Assert.True(list.IsOpen);
    }

    [Fact]
    public async Task Export_RightPassword_ResetsSharedDelay()
    {
        var (profile, _, _, csv) = OpenCsv();
        var picker = new FakeExportFilePicker(ExportPath());

        // Two wrong passwords: the delay grows to 2 s, then it runs out.
        await csv.ExportCommand.ExecuteAsync(Request(WrongPassword, picker));
        _profiles.Time.Advance(TimeSpan.FromSeconds(1));
        await csv.ExportCommand.ExecuteAsync(Request(WrongPassword, picker));
        Assert.Equal(TimeSpan.FromSeconds(2), _profiles.Throttle.RemainingDelay(profile.Id));
        _profiles.Time.Advance(TimeSpan.FromSeconds(2));

        await csv.ExportCommand.ExecuteAsync(Request(TestProfiles.Password, picker));

        Assert.Equal(ExportOutcome.Saved, csv.Outcome);
        Assert.Equal(0, csv.RetrySeconds);
        Assert.Equal(TimeSpan.Zero, _profiles.Throttle.RemainingDelay(profile.Id));

        // The failures were forgotten: the next wrong password is the first again (1 s, not 4 s).
        await csv.ExportCommand.ExecuteAsync(Request(WrongPassword, picker));

        Assert.Equal(TimeSpan.FromSeconds(1), _profiles.Throttle.RemainingDelay(profile.Id));
    }

    [Fact]
    public async Task Export_NotAcknowledged_DoesNothing()
    {
        var (profile, _, _, csv) = OpenCsv(acknowledged: false);
        var picker = new FakeExportFilePicker(ExportPath());
        var right = Request(TestProfiles.Password, picker);
        var wrong = Request(WrongPassword, picker);
        Assert.False(csv.ExportCommand.CanExecute(null));

        await csv.ExportCommand.ExecuteAsync(right);
        await csv.ExportCommand.ExecuteAsync(wrong);

        // No dialog, no file, no Argon2id (so even a wrong password counts for nothing), and both arrays are zeroed.
        Assert.Equal(0, picker.Calls);
        Assert.Equal(ExportOutcome.None, csv.Outcome);
        Assert.Equal(0, csv.RetrySeconds);
        Assert.Equal(TimeSpan.Zero, _profiles.Throttle.RemainingDelay(profile.Id));
        Assert.Empty(ExportFolderNames());
        AssertZeroed(right);
        AssertZeroed(wrong);
    }

    [Fact]
    public async Task Export_PickerCanceled_WritesNothing()
    {
        var (profile, list, _, csv) = OpenCsv();
        var before = VaultFolderSnapshot();
        var picker = new FakeExportFilePicker(null);
        var request = Request(TestProfiles.Password, picker);

        await csv.ExportCommand.ExecuteAsync(request);

        Assert.Equal(1, picker.Calls);
        Assert.Equal(ExportOutcome.None, csv.Outcome);
        Assert.Null(csv.SavedFileName);
        Assert.Empty(ExportFolderNames());
        AssertVaultFolderUnchanged(before);
        AssertZeroed(request);
        Assert.Same(csv, _navigation.Current);
        Assert.True(list.IsOpen);
        Assert.True(csv.ExportCommand.CanExecute(null));

        // The password was right: it cleared the delay, and the user can try again at once.
        Assert.Equal(TimeSpan.Zero, _profiles.Throttle.RemainingDelay(profile.Id));
    }

    [Fact]
    public async Task Export_LockedWhilePicking_WritesNothingAndZeroes()
    {
        var (profile, list, _, csv) = OpenCsv();
        var picker = new FakeExportFilePicker(ExportPath()) { BeforeReturn = () => list.LockCommand.Execute(null) };
        var request = Request(TestProfiles.Password, picker);

        await csv.ExportCommand.ExecuteAsync(request);

        // The vault was locked while the dialog was open: no file, and no message on a page that is gone.
        Assert.False(list.IsOpen);
        Assert.Equal(ExportOutcome.None, csv.Outcome);
        Assert.Null(csv.SavedFileName);
        Assert.Empty(ExportFolderNames());
        AssertZeroed(request);
        Assert.Equal(profile, Assert.IsType<UnlockViewModel>(_navigation.Current).Profile);
    }

    [Fact]
    public async Task Export_VaultAlreadyLocked_DoesNothingAndCountsNothing()
    {
        var (profile, list, _, csv) = OpenCsv();
        list.LockCommand.Execute(null);

        // A page that outlived the lock, asked once more: the session is disposed.
        csv.Acknowledged = true;
        var picker = new FakeExportFilePicker(ExportPath());
        var request = Request(TestProfiles.Password, picker);

        await csv.ExportCommand.ExecuteAsync(request);

        Assert.Equal(0, picker.Calls);
        Assert.Equal(ExportOutcome.None, csv.Outcome);
        Assert.Equal(TimeSpan.Zero, _profiles.Throttle.RemainingDelay(profile.Id));
        Assert.Empty(ExportFolderNames());
        AssertZeroed(request);
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
        var (profile, _, _, csv) = OpenCsv(repository: repository);
        var vaults = _profiles.DirectoryPath;
        var target = variant switch
        {
            RefusedPath.UpperCase => Path.Combine(vaults.ToUpperInvariant(), CsvName),
            RefusedPath.DotDotSegment => Path.Combine(vaults, "sub", "..", CsvName),
            RefusedPath.TheVaultItself => _profiles.Repository.GetVaultPath(profile.Id),
            _ => Path.Combine(vaults, CsvName),
        };
        var before = VaultFolderSnapshot();
        var picker = new FakeExportFilePicker(target);
        var request = Request(TestProfiles.Password, picker);

        await csv.ExportCommand.ExecuteAsync(request);

        Assert.Equal(1, picker.Calls);
        Assert.Equal(ExportOutcome.VaultsFolder, csv.Outcome);
        Assert.Null(csv.SavedFileName);
        AssertVaultFolderUnchanged(before);
        Assert.Empty(ExportFolderNames());
        AssertZeroed(request);
    }

    [Fact]
    public async Task Export_IntoSubfolderOfVaultsFolder_IsAllowed()
    {
        var (_, list, _, csv) = OpenCsv();
        var subfolder = Path.Combine(_profiles.DirectoryPath, "exports");
        Directory.CreateDirectory(subfolder);
        var before = VaultFolderSnapshot();
        var target = Path.Combine(subfolder, CsvName);

        await csv.ExportCommand.ExecuteAsync(Request(TestProfiles.Password, new FakeExportFilePicker(target)));

        Assert.Equal(ExportOutcome.Saved, csv.Outcome);
        Assert.Equal([CsvName], Directory.GetFileSystemEntries(subfolder).Select(path => Path.GetFileName(path)).ToArray());
        AssertVaultFolderUnchanged(before);
        Assert.Equal(VaultCsv.Serialize(list.Entries), File.ReadAllBytes(target));
    }

    [Fact]
    public async Task Export_ExistingFile_IsReplacedWithoutBak()
    {
        var (_, list, _, csv) = OpenCsv();
        var target = ExportPath();
        File.WriteAllBytes(target, [1, 2, 3, 4]);

        await csv.ExportCommand.ExecuteAsync(Request(TestProfiles.Password, new FakeExportFilePicker(target)));

        Assert.Equal(ExportOutcome.Saved, csv.Outcome);

        // Replaced in place: no .tmp left behind and no .bak of the old file.
        Assert.Equal([CsvName], ExportFolderNames());
        Assert.Equal(VaultCsv.Serialize(list.Entries), File.ReadAllBytes(target));
    }

    [Fact]
    public async Task Export_WriteFails_IsFailedAndKeepsExistingFile()
    {
        var (_, _, _, csv) = OpenCsv();
        var target = ExportPath();
        byte[] original = [1, 2, 3, 4];
        File.WriteAllBytes(target, original);
        var request = Request(TestProfiles.Password, new FakeExportFilePicker(target));

        using (new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await csv.ExportCommand.ExecuteAsync(request);
        }

        Assert.Equal(ExportOutcome.Failed, csv.Outcome);
        Assert.Null(csv.SavedFileName);
        Assert.Equal(original, File.ReadAllBytes(target));
        Assert.Equal([CsvName], ExportFolderNames());
        AssertZeroed(request);
        Assert.Same(csv, _navigation.Current);
        Assert.True(csv.ExportCommand.CanExecute(null));
    }

    [Fact]
    public async Task Export_MissingFolder_IsFailed()
    {
        var (_, _, _, csv) = OpenCsv();
        var missing = Path.Combine(_exports.FullName, "missing");
        var request = Request(TestProfiles.Password, new FakeExportFilePicker(Path.Combine(missing, CsvName)));

        await csv.ExportCommand.ExecuteAsync(request);

        Assert.Equal(ExportOutcome.Failed, csv.Outcome);
        Assert.Null(csv.SavedFileName);
        Assert.False(Directory.Exists(missing));
        Assert.Empty(ExportFolderNames());
        AssertZeroed(request);
    }

    [Fact]
    public async Task Export_Attempts_ClearTheResultOfThePreviousOne()
    {
        var (_, _, _, csv) = OpenCsv();
        var picker = new FakeExportFilePicker(ExportPath());

        await csv.ExportCommand.ExecuteAsync(Request(TestProfiles.Password, picker));
        Assert.Equal(ExportOutcome.Saved, csv.Outcome);
        Assert.NotNull(csv.SavedFileName);

        // A canceled attempt after a save clears the message and the file name of the save.
        picker.Path = null;
        await csv.ExportCommand.ExecuteAsync(Request(TestProfiles.Password, picker));

        Assert.Equal(ExportOutcome.None, csv.Outcome);
        Assert.Null(csv.SavedFileName);
    }

    [Fact]
    public async Task GoBack_WhileExportRuns_IsBlocked()
    {
        var (_, _, export, csv) = OpenCsv();
        var picker = new FakeExportFilePicker(null);
        picker.Block();

        var running = csv.ExportCommand.ExecuteAsync(Request(TestProfiles.Password, picker));

        Assert.True(csv.ExportCommand.IsRunning);
        Assert.False(csv.GoBackCommand.CanExecute(null));
        csv.GoBackCommand.Execute(null);
        Assert.Same(csv, _navigation.Current);

        picker.Release();
        await running;

        Assert.False(csv.ExportCommand.IsRunning);
        Assert.True(csv.GoBackCommand.CanExecute(null));
        csv.GoBackCommand.Execute(null);

        // Back returns to the export page, not to the list.
        Assert.Same(export, _navigation.Current);
    }

    [Fact]
    public async Task OnNavigatedFrom_ClearsOutcomeAndAcknowledgement()
    {
        var (_, _, export, csv) = OpenCsv();

        await csv.ExportCommand.ExecuteAsync(Request(TestProfiles.Password, new FakeExportFilePicker(ExportPath())));
        Assert.Equal(ExportOutcome.Saved, csv.Outcome);
        Assert.NotNull(csv.SavedFileName);
        Assert.True(csv.Acknowledged);
        csv.GoBackCommand.Execute(null);

        Assert.Same(export, _navigation.Current);
        Assert.Equal(ExportOutcome.None, csv.Outcome);
        Assert.Null(csv.SavedFileName);
        Assert.False(csv.Acknowledged);
        Assert.False(csv.ExportCommand.CanExecute(null));

        // A page that is shown again starts clean, so the user says it again.
        export.OpenCsvExportCommand.Execute(null);
        var again = Assert.IsType<CsvExportViewModel>(_navigation.Current);
        Assert.False(again.Acknowledged);
        Assert.False(again.ExportCommand.CanExecute(null));
    }
}
