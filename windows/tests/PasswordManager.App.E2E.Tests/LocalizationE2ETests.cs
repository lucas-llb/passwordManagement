using System.Globalization;
using System.Text;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using PasswordManager.App.E2E.Tests.Localization;
using PasswordManager.Core.Passwords;
using PasswordManager.Core.Vault;

namespace PasswordManager.App.E2E.Tests;

// Every visible text of every screen must come from Strings/pt-BR/Resources.resw (or be data the user or a test vector owns).
public sealed class LocalizationE2ETests
{
    private static readonly ControlType[] TextControlTypes =
        [ControlType.Text, ControlType.Button, ControlType.Edit, ControlType.List, ControlType.ListItem];

    // The number beside the generator's length slider is the slider's value, data rather than text: the default length.
    private static readonly string GeneratorLengthText =
        PasswordGenerator.DefaultLength.ToString(CultureInfo.InvariantCulture);

    [Fact]
    public void EmptyFolder_ProfileTextsComeFromResources()
    {
        using var app = AppSession.Launch();

        Assert.Equal(AppResources.Strings["AppTitle"], app.Window.Title);
        AssertNameFromResources(app, "ProfilesTitle");
        AssertNameFromResources(app, "ProfilesEmptyTitle");
        AssertNameFromResources(app, "ProfilesEmptyHint");
        AssertEveryTextFromResourcesOrData(app);
    }

    [Fact]
    public void CorruptProfileList_LoadFailedTextComesFromResources()
    {
        using var app = AppSession.Launch(dir => File.WriteAllText(Path.Combine(dir, "profiles.json"), "{"));

        AssertNameFromResources(app, "ProfilesLoadFailed");
        AssertEveryTextFromResourcesOrData(app);
    }

    [Fact]
    public void MissingVaultFile_UnreadableTextComesFromResources()
    {
        using var app = AppSession.Launch(SeededVault.SeedProfileOnly);
        Assert.Single(app.Items("ProfilesList")).Patterns.Invoke.Pattern.Invoke();
        app.TypePassword("qualquer-senha-ficticia");
        app.Invoke("UnlockButton");

        AssertNameFromResources(app, "UnlockVaultUnreadable");
        AssertEveryTextFromResourcesOrData(app, SeededVault.ProfileName);
    }

    [Fact]
    public void SeededVault_EveryScreenTextComesFromResources()
    {
        using var app = AppSession.Launch(SeededVault.Seed);

        // Profiles
        var profile = Assert.Single(app.Items("ProfilesList"));
        AssertNameFromResources(app, "ProfilesTitle");
        AssertNameFromResources(app, "ProfilesList");
        AssertEveryTextFromResourcesOrData(app, SeededVault.ProfileName);
        profile.Patterns.Invoke.Pattern.Invoke();

        // Unlock
        app.WaitFor("UnlockPassword");
        app.WaitGone("ProfilesList");
        AssertNameFromResources(app, "UnlockTitle");
        AssertNameFromResources(app, "UnlockPassword");
        AssertNameFromResources(app, "UnlockButton");
        AssertNameFromResources(app, "UnlockBack", "NavBack");
        AssertEveryTextFromResourcesOrData(app, SeededVault.ProfileName);

        // Wrong password: the countdown sentence is the resw format with the number of seconds; it may vanish while the
        // sweep runs, which is fine.
        app.TypePassword("senha-errada-ficticia");
        app.Invoke("UnlockButton");
        AssertNameFromResources(app, "UnlockWrongPassword");
        var countdown = AppResources.Format("UnlockRetryCountdown", 1);
        app.WaitName("UnlockRetryCountdown", countdown);
        AssertEveryTextFromResourcesOrData(app, SeededVault.ProfileName, countdown);

        // Entries, once the delay is over
        app.WaitEnabled("UnlockButton");
        app.TypePassword(SeededVault.Password);
        app.Invoke("UnlockButton");
        app.WaitFor("EntriesList");
        app.WaitGone("UnlockPassword");
        var items = app.Items("EntriesList");
        Assert.Equal(SeededVault.Entries.Length, items.Length);
        AssertNameFromResources(app, "EntriesTitle");
        AssertNameFromResources(app, "EntriesNew");
        AssertNameFromResources(app, "EntriesExport");
        AssertNameFromResources(app, "EntriesImport");
        AssertNameFromResources(app, "EntriesLock");
        AssertNameFromResources(app, "EntriesList");
        AssertNameFromResources(app, "EntriesSearch");
        AssertNameFromResources(app, "EntriesFavoritesOnly");
        AssertNameFromResources(app, "EntriesFolder");
        AssertEveryTextFromResourcesOrData(app, EntryTexts());

        // A search that matches nothing shows the "no matches" text instead of the list items.
        app.SetValue("EntriesSearch", "consulta-sem-resultado");
        AssertNameFromResources(app, "EntriesNoMatches");
        AssertEveryTextFromResourcesOrData(app, EntryTexts());

        // Detail of the first entry; the query is cleared first so the item is there to open.
        app.SetValue("EntriesSearch", string.Empty);
        app.WaitGone("EntriesNoMatches");
        items = app.WaitItems("EntriesList", SeededVault.Entries.Length);
        items[0].Patterns.Invoke.Pattern.Invoke();
        app.WaitFor("DetailTitle");
        app.WaitGone("EntriesList");
        AssertNameFromResources(app, "DetailBack", "NavBack");
        AssertNameFromResources(app, "DetailTitle");
        AssertNameFromResources(app, "DetailUsername");
        AssertNameFromResources(app, "DetailUrl");
        AssertNameFromResources(app, "DetailFolder");
        AssertNameFromResources(app, "DetailNotes");
        AssertNameFromResources(app, "DetailPassword");
        AssertNameFromResources(app, "DetailShowPassword");
        AssertNameFromResources(app, "DetailCopyPassword");
        AssertNameFromResources(app, "DetailGenerate");
        AssertNameFromResources(app, "DetailGeneratorLength");
        AssertNameFromResources(app, "DetailGeneratorUppercase");
        AssertNameFromResources(app, "DetailGeneratorDigits");
        AssertNameFromResources(app, "DetailGeneratorSymbols");
        AssertNameFromResources(app, "DetailFavorite");
        AssertNameFromResources(app, "DetailSave");
        AssertNameFromResources(app, "DetailDiscard");
        AssertNameFromResources(app, "DetailDelete");

        // The two dates are the resw formats filled with the entry's timestamps in local time: data of the vault, not
        // fixed texts. A saved entry shows both, and no message.
        var created = AppResources.Format("DetailCreatedAt", DateOf(0, "createdAt").LocalDateTime);
        var updated = AppResources.Format("DetailUpdatedAt", DateOf(0, "updatedAt").LocalDateTime);
        Assert.Equal(created, app.WaitFor("DetailCreatedAt").Name);
        Assert.Equal(updated, app.WaitFor("DetailUpdatedAt").Name);
        Assert.Null(app.Find("DetailTitleRequired"));
        Assert.Null(app.Find("DetailSaveError"));
        Assert.Equal(GeneratorLengthText, app.WaitFor("DetailGeneratorLengthValue").Name);
        AssertEveryTextFromResourcesOrData(app, created, updated, GeneratorLengthText);

        // The password box is swapped for a text box while the password is shown; both are named "Senha".
        Assert.Null(app.Find("DetailPasswordText"));
        app.Toggle("DetailShowPassword");
        app.WaitGone("DetailPassword");
        AssertNameFromResources(app, "DetailPasswordText");
        AssertEveryTextFromResourcesOrData(app, created, updated, GeneratorLengthText);
        app.Toggle("DetailShowPassword");
        app.WaitGone("DetailPasswordText");
        app.WaitFor("DetailPassword");

        // Copying shows the confirmation (no error). The value is the seed's fictitious password; the app takes it off
        // the clipboard when it closes.
        Assert.Null(app.Find("DetailPasswordCopied"));
        Assert.Null(app.Find("DetailCopyFailed"));
        app.Invoke("DetailCopyPassword");
        AssertNameFromResources(app, "DetailPasswordCopied");
        Assert.Null(app.Find("DetailCopyFailed"));
        AssertEveryTextFromResourcesOrData(app, created, updated, GeneratorLengthText);

        // The delete confirmation, then cancelled.
        app.Invoke("DetailDelete");
        app.WaitDialog("DetailDeleteDialog");
        AssertNameFromResources(app, "DetailDeleteDialog");
        AssertNameFromResources(app, "DetailDeleteWarning");
        AssertEveryTextFromResourcesOrData(app, created, updated, GeneratorLengthText);
        app.InvokeInDialog("DetailDeleteDialog", "CloseButton");
        app.WaitDialogGone("DetailDeleteDialog");

        // A new entry has no dates and no delete, and asks for the title.
        app.Invoke("DetailBack");
        app.WaitFor("EntriesList");
        app.WaitGone("DetailTitle");
        app.Invoke("EntriesNew");
        app.WaitFor("DetailTitle");
        app.WaitGone("EntriesList");
        AssertNameFromResources(app, "DetailTitleRequired");
        Assert.Null(app.Find("DetailCreatedAt"));
        Assert.Null(app.Find("DetailUpdatedAt"));
        Assert.Null(app.Find("DetailDelete"));
        AssertEveryTextFromResourcesOrData(app, GeneratorLengthText);
    }

    // A seeded entry's timestamp (the vector stores them as ISO 8601 UTC).
    private static DateTimeOffset DateOf(int entry, string field) =>
        DateTimeOffset.Parse(
            SeededVault.Entries[entry].GetProperty(field).GetString()!,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal);

    [Fact]
    public void CreatePage_TextsComeFromResources()
    {
        using var app = AppSession.Launch();
        app.Invoke("ProfilesCreate");

        // Both boxes empty, so the PasswordBox reveal buttons, named by WinUI, are not shown.
        app.WaitFor("CreateName");
        app.WaitGone("ProfilesList");
        AssertNameFromResources(app, "CreateTitle");
        AssertNameFromResources(app, "CreateName");
        AssertNameFromResources(app, "CreatePassword");
        AssertNameFromResources(app, "CreateConfirmPassword");
        AssertNameFromResources(app, "CreateNoRecoveryWarning");
        AssertNameFromResources(app, "CreateSuggest");
        AssertNameFromResources(app, "CreateButton");
        AssertNameFromResources(app, "CreateBack", "NavBack");
        AssertEveryTextFromResourcesOrData(app);

        // The meter text; the box is not empty, so the sweep waits until the attempt below empties it.
        app.SetValue("CreatePassword", "aaaaaaaaaaaa");
        app.WaitName("CreateStrength", AppResources.Strings["PasswordStrengthVeryWeak"]);

        // An empty name and a short fictitious password; the view empties the boxes after the attempt.
        app.SetValue("CreatePassword", "curta-ficti");
        app.SetValue("CreateConfirmPassword", "curta-ficti");
        app.Invoke("CreateButton");
        Assert.Equal(AppResources.Strings["ProfileNameEmpty"], app.WaitFor("CreateNameError").Name);
        AssertNameFromResources(app, "CreatePasswordTooShort");
        app.WaitGone("CreateStrength");
        AssertEveryTextFromResourcesOrData(app);

        // The suggestion fills both boxes, so only its hint is checked, not swept.
        app.Invoke("CreateSuggest");
        app.WaitFor("CreateSuggestion");
        AssertNameFromResources(app, "CreateSuggestionHint");
    }

    [Fact]
    public void RecoveryKitPage_TextsComeFromResources()
    {
        using var app = AppSession.Launch();
        app.Invoke("ProfilesCreate");
        app.WaitFor("CreateName");
        app.SetValue("CreateName", "Perfil Kit");
        app.SetValue("CreatePassword", "frase-ficticia-do-kit");
        app.SetValue("CreateConfirmPassword", "frase-ficticia-do-kit");
        app.Invoke("CreateButton");

        // Argon2id runs with the default cost here, about a second.
        var kitText = app.WaitFor("KitText").Name;
        app.WaitGone("CreateName");
        AssertNameFromResources(app, "KitTitle");
        AssertNameFromResources(app, "KitExplanation");
        AssertNameFromResources(app, "KitInstructions");
        AssertNameFromResources(app, "KitSavedCheck");
        AssertNameFromResources(app, "KitDone");
        AssertEveryTextFromResourcesOrData(app, kitText);
    }

    [Fact]
    public void ProfileDialogs_TextsComeFromResources()
    {
        using var app = AppSession.Launch(SeededVault.Seed);
        Assert.Single(app.WaitItems("ProfilesList", 1));

        app.Invoke("ProfileRename");
        app.WaitFor("ProfileRenameName");
        AssertNameFromResources(app, "ProfileRenameDialog");
        AssertNameFromResources(app, "ProfileRenameName");
        AssertEveryTextFromResourcesOrData(app, SeededVault.ProfileName);
        app.InvokeInDialog("ProfileRenameDialog", "CloseButton");
        app.WaitDialogGone("ProfileRenameDialog");

        app.Invoke("ProfileDelete");
        app.WaitFor("ProfileDeleteName");
        AssertNameFromResources(app, "ProfileDeleteDialog");
        AssertNameFromResources(app, "ProfileDeleteWarning");
        AssertEveryTextFromResourcesOrData(app, SeededVault.ProfileName);
        app.InvokeInDialog("ProfileDeleteDialog", "CloseButton");
        app.WaitDialogGone("ProfileDeleteDialog");
    }

    [Fact]
    public void ExportPage_TextsComeFromResources()
    {
        const string exportPassword = "Senha de exportação fictícia 2026";
        using var app = AppSession.Launch(SeededVault.Seed);
        Assert.Single(app.Items("ProfilesList")).Patterns.Invoke.Pattern.Invoke();
        app.TypePassword(SeededVault.Password);
        app.Invoke("UnlockButton");
        app.WaitFor("EntriesList");
        app.Invoke("EntriesExport");

        // Both boxes empty, so the PasswordBox reveal buttons, named by WinUI, are not shown.
        app.WaitFor("ExportTitle");
        app.WaitGone("EntriesList");
        AssertNameFromResources(app, "ExportTitle");
        AssertNameFromResources(app, "ExportIntro");
        AssertNameFromResources(app, "ExportPassword");
        AssertNameFromResources(app, "ExportConfirm");
        AssertNameFromResources(app, "ExportSave");
        AssertNameFromResources(app, "ExportBack", "NavBack");
        AssertNameFromResources(app, "ExportOpenCsv");
        AssertEveryTextFromResourcesOrData(app);

        // The two refusals; the page empties both boxes after every attempt.
        app.SetValue("ExportPassword", "curta-ficti");
        app.SetValue("ExportConfirm", "curta-ficti");
        app.Invoke("ExportSave");
        AssertNameFromResources(app, "ExportPasswordTooShort");
        AssertEveryTextFromResourcesOrData(app);

        app.SetValue("ExportPassword", "senha-ficticia-um-123");
        app.SetValue("ExportConfirm", "senha-ficticia-dois-456");
        app.Invoke("ExportSave");
        AssertNameFromResources(app, "ExportPasswordMismatch");
        app.WaitGone("ExportPasswordTooShort");
        AssertEveryTextFromResourcesOrData(app);

        // The ring is shown while the save dialog is open. While it spins, WinUI puts its own busy state, in the system
        // language, before the name, so the name must end with the resw value. The dialog itself is Windows', also in the
        // system language, so the sweeps run only once it is closed.
        var exports = Directory.CreateDirectory(Path.Combine(app.VaultsDirectory, "exports")).FullName;
        app.SetValue("ExportPassword", exportPassword);
        app.SetValue("ExportConfirm", exportPassword);
        app.Invoke("ExportSave");
        app.WaitFileDialog();
        Assert.EndsWith(" " + AppResources.ExpectedName("ExportProgress"), app.WaitFor("ExportProgress").Name, StringComparison.Ordinal);
        app.SaveInFileDialog(Path.Combine(app.VaultsDirectory, "recusado.pmvault"));
        AssertNameFromResources(app, "ExportVaultsFolder");
        app.WaitGone("ExportPasswordMismatch");
        AssertEveryTextFromResourcesOrData(app);

        // A folder in the way of the temp file (<name>.tmp) makes the write fail.
        Directory.CreateDirectory(Path.Combine(exports, "falha.pmvault.tmp"));
        app.SetValue("ExportPassword", exportPassword);
        app.SetValue("ExportConfirm", exportPassword);
        app.Invoke("ExportSave");
        app.SaveInFileDialog(Path.Combine(exports, "falha.pmvault"));
        AssertNameFromResources(app, "ExportFailed");
        app.WaitGone("ExportVaultsFolder");
        AssertEveryTextFromResourcesOrData(app);

        // The confirmation is the resw format with the file name.
        app.SetValue("ExportPassword", exportPassword);
        app.SetValue("ExportConfirm", exportPassword);
        app.Invoke("ExportSave");
        app.SaveInFileDialog(Path.Combine(exports, "backup.pmvault"));
        var done = AppResources.Format("ExportDone", "backup.pmvault");
        app.WaitName("ExportDone", done);
        app.WaitGone("ExportFailed");
        AssertEveryTextFromResourcesOrData(app, done);
    }

    [Fact]
    public void CsvExportPage_TextsComeFromResources()
    {
        using var app = AppSession.Launch(SeededVault.Seed);
        Assert.Single(app.Items("ProfilesList")).Patterns.Invoke.Pattern.Invoke();
        app.TypePassword(SeededVault.Password);
        app.Invoke("UnlockButton");
        app.WaitFor("EntriesList");
        app.Invoke("EntriesExport");
        app.WaitFor("ExportTitle");
        app.Invoke("ExportOpenCsv");

        // The box is empty, so the PasswordBox reveal button, named by WinUI, is not shown.
        app.WaitFor("CsvExportTitle");
        app.WaitGone("ExportTitle");
        AssertNameFromResources(app, "CsvExportTitle");
        AssertNameFromResources(app, "CsvExportWarning");
        AssertNameFromResources(app, "CsvExportAcknowledge");
        AssertNameFromResources(app, "CsvExportPassword");
        AssertNameFromResources(app, "CsvExportSave");
        AssertNameFromResources(app, "CsvExportBack", "NavBack");
        AssertEveryTextFromResourcesOrData(app);

        // A wrong master password: the error and the countdown of the delay it starts (the text of the unlock screen).
        app.Toggle("CsvExportAcknowledge");
        app.SetValue("CsvExportPassword", "senha-errada-ficticia");
        app.Invoke("CsvExportSave");
        AssertNameFromResources(app, "CsvExportWrongPassword");
        var countdown = AppResources.Format("UnlockRetryCountdown", 1);
        app.WaitName("CsvExportRetryCountdown", countdown);
        AssertEveryTextFromResourcesOrData(app, countdown);
        app.WaitGone("CsvExportRetryCountdown");
        app.WaitEnabled("CsvExportSave");

        // The ring is shown while the save dialog is open. While it spins, WinUI puts its own busy state, in the system
        // language, before the name, so the name must end with the resw value. The dialog itself is Windows', also in the
        // system language, so the sweeps run only once it is closed.
        var exports = Directory.CreateDirectory(Path.Combine(app.VaultsDirectory, "exports")).FullName;
        app.SetValue("CsvExportPassword", SeededVault.Password);
        app.Invoke("CsvExportSave");
        app.WaitFileDialog();
        Assert.EndsWith(" " + AppResources.ExpectedName("CsvExportProgress"), app.WaitFor("CsvExportProgress").Name, StringComparison.Ordinal);
        app.SaveInFileDialog(Path.Combine(app.VaultsDirectory, "recusado.csv"));
        AssertNameFromResources(app, "CsvExportVaultsFolder");
        app.WaitGone("CsvExportWrongPassword");
        AssertEveryTextFromResourcesOrData(app);

        // A folder in the way of the temp file (<name>.tmp) makes the write fail.
        Directory.CreateDirectory(Path.Combine(exports, "falha.csv.tmp"));
        app.SetValue("CsvExportPassword", SeededVault.Password);
        app.Invoke("CsvExportSave");
        app.SaveInFileDialog(Path.Combine(exports, "falha.csv"));
        AssertNameFromResources(app, "CsvExportFailed");
        app.WaitGone("CsvExportVaultsFolder");
        AssertEveryTextFromResourcesOrData(app);

        // The confirmation is the resw format with the file name.
        app.SetValue("CsvExportPassword", SeededVault.Password);
        app.Invoke("CsvExportSave");
        app.SaveInFileDialog(Path.Combine(exports, "senhas.csv"));
        var done = AppResources.Format("CsvExportDone", "senhas.csv");
        app.WaitName("CsvExportDone", done);
        app.WaitGone("CsvExportFailed");
        AssertEveryTextFromResourcesOrData(app, done);
    }

    [Fact]
    public void ImportPage_TextsComeFromResources()
    {
        using var app = AppSession.Launch(SeededVault.Seed);
        Assert.Single(app.Items("ProfilesList")).Patterns.Invoke.Pattern.Invoke();
        app.TypePassword(SeededVault.Password);
        app.Invoke("UnlockButton");
        app.WaitFor("EntriesList");
        var imports = Directory.CreateDirectory(Path.Combine(app.VaultsDirectory, "imports")).FullName;
        app.Invoke("EntriesImport");

        // Choosing a file.
        app.WaitFor("ImportTitle");
        app.WaitGone("EntriesList");
        AssertNameFromResources(app, "ImportTitle");
        AssertNameFromResources(app, "ImportIntro");
        AssertNameFromResources(app, "ImportChoose");
        AssertNameFromResources(app, "ImportBack", "NavBack");
        AssertEveryTextFromResourcesOrData(app);

        // The ring is shown while the open dialog is up. While it spins, WinUI puts its own busy state, in the system
        // language, before the name, so the name must end with the resw value. The dialog itself is Windows', also in the
        // system language, so the sweeps run only once it is closed.
        app.Invoke("ImportChoose");
        app.WaitFileDialog();
        Assert.EndsWith(" " + AppResources.ExpectedName("ImportProgress"), app.WaitFor("ImportProgress").Name, StringComparison.Ordinal);
        app.CancelFileDialog();
        app.WaitGone("ImportProgress");

        // Every refusal of a whole file.
        var tooLarge = Path.Combine(imports, "grande.csv");
        using (var big = File.Create(tooLarge))
        {
            big.SetLength(VaultImport.MaxFileBytes + 1L);
        }

        var locked = Path.Combine(imports, "bloqueado.csv");
        File.WriteAllText(locked, "name,password\r\na,b\r\n");
        var tooMany = Encoding.UTF8.GetBytes("name,password\r\n" + string.Concat(Enumerable.Repeat("a,b\r\n", VaultImport.MaxEntries + 1)));
        byte[] utf16 = [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes("name,password\r\n")];
        (string FileName, byte[] Contents, string Message)[] refusals =
        [
            ("nota.txt", Encoding.UTF8.GetBytes("texto"), AppResources.Strings["ImportErrorUnsupportedFile"]),
            ("texto.pmvault", Encoding.UTF8.GetBytes("isto não é um cofre"), AppResources.Strings["ImportErrorInvalidVaultFile"]),
            ("utf16.csv", utf16, AppResources.Strings["ImportErrorInvalidEncoding"]),
            ("cabecalho.csv", Encoding.UTF8.GetBytes("a,b,c\r\n1,2,3\r\n"), AppResources.Strings["ImportErrorInvalidHeader"]),
            ("malformado.csv", Encoding.UTF8.GetBytes("name,password\r\na,\"sem fim\r\n"), AppResources.Format("ImportErrorMalformedCsv", 2)),
            ("muitos.csv", tooMany, AppResources.Strings["ImportErrorTooManyEntries"]),
        ];
        foreach (var (fileName, contents, message) in refusals)
        {
            var path = Path.Combine(imports, fileName);
            File.WriteAllBytes(path, contents);
            ChooseAndExpectError(app, path, message);
        }

        ChooseAndExpectError(app, tooLarge, AppResources.Strings["ImportErrorTooLarge"]);

        // A file another program is writing cannot be read (the app shuts writers out while it reads). The dialog still
        // lists it, which a file opened without sharing would not be.
        using (new FileStream(locked, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            ChooseAndExpectError(app, locked, AppResources.Strings["ImportErrorReadFailed"]);
        }

        // A .pmvault asks for its password. The box is empty, so the PasswordBox reveal button, named by WinUI, is not shown.
        const string vaultFileName = "backup.pmvault";
        var vaultFile = Path.Combine(imports, vaultFileName);
        SeededVault.CopyVector("export", vaultFile);
        app.Invoke("ImportChoose");
        app.OpenInFileDialog(vaultFile);
        app.WaitFor("ImportPassword");
        var fileNameText = AppResources.Format("ImportFileName", vaultFileName);
        app.WaitName("ImportFileName", fileNameText);
        AssertNameFromResources(app, "ImportPassword");
        AssertNameFromResources(app, "ImportOpen");
        AssertNameFromResources(app, "ImportChoose");
        AssertNameFromResources(app, "ImportBack", "NavBack");
        AssertEveryTextFromResourcesOrData(app, fileNameText);

        app.SetValue("ImportPassword", "senha-errada-ficticia");
        app.Invoke("ImportOpen");
        app.WaitName("ImportError", AppResources.Strings["ImportErrorWrongPassword"]);
        app.WaitEnabled("ImportOpen");
        AssertEveryTextFromResourcesOrData(app, fileNameText, AppResources.Strings["ImportErrorWrongPassword"]);

        // A CSV: three records with a column the app does not keep, the last one with too few fields.
        var rows = new[]
        {
            (Title: "Conta importada A", Username: "usuario-a", Url: "https://a.example.com"),
            (Title: "Conta importada B", Username: "usuario-b", Url: "https://b.example.com"),
        };
        const string csvFileName = "contas.csv";
        var csvFile = Path.Combine(imports, csvFileName);
        File.WriteAllText(
            csvFile,
            "name,url,username,password,notes,folder,login_totp\r\n"
            + "Conta importada A,https://a.example.com,usuario-a,Senha-ficticia-A,Nota A,Pasta A,totp-ficticio-a\r\n"
            + "Conta importada B,https://b.example.com,usuario-b,Senha-ficticia-B,,,totp-ficticio-b\r\n"
            + "Linha curta\r\n",
            new UTF8Encoding(false));
        app.Invoke("ImportChoose");
        app.OpenInFileDialog(csvFile);
        var summary = AppResources.Format("ImportSummary", rows.Length, 0);
        app.WaitName("ImportSummary", summary);
        var csvText = AppResources.Format("ImportFileName", csvFileName);
        var rejected = AppResources.Format("ImportRejected", 1, 4);
        var ignored = AppResources.Format("ImportIgnoredColumns", "login_totp");
        string[] rowTexts = [.. rows.SelectMany(row => new[] { row.Title, row.Username, row.Url })];
        AssertNameFromResources(app, "ImportPreviewList");
        AssertNameFromResources(app, "ImportConfirm");
        AssertNameFromResources(app, "ImportBack", "NavBack");
        Assert.Equal(csvText, app.WaitFor("ImportFileName").Name);
        Assert.Equal(rejected, app.WaitFor("ImportRejected").Name);
        Assert.Equal(ignored, app.WaitFor("ImportIgnoredColumns").Name);
        Assert.Null(app.Find("ImportPolicy"));
        Assert.Null(app.Find("ImportNothing"));
        AssertEveryTextFromResourcesOrData(app, [csvText, summary, rejected, ignored, .. rowTexts]);

        // A save that fails (a folder where the temp file goes) keeps the preview with its message.
        var blocker = Directory.CreateDirectory(SeededVault.VaultPath(app.VaultsDirectory) + ".tmp").FullName;
        app.Invoke("ImportConfirm");
        app.WaitName("ImportError", AppResources.Strings["ImportErrorSaveFailed"]);
        app.WaitEnabled("ImportConfirm");
        AssertEveryTextFromResourcesOrData(app, [csvText, summary, rejected, ignored, .. rowTexts]);

        // The confirmation is the resw format with the three counts, and a CSV adds the reminder to delete it.
        Directory.Delete(blocker);
        app.Invoke("ImportConfirm");
        var done = AppResources.Format("ImportDone", rows.Length, 0, 0);
        app.WaitName("ImportDone", done);
        AssertNameFromResources(app, "ImportDoneCsvReminder");
        AssertNameFromResources(app, "ImportBack", "NavBack");
        app.WaitGone("ImportError");
        AssertEveryTextFromResourcesOrData(app, done);

        // The same file again: both rows are duplicates now, and Skip would write nothing. The duplicate rows say so in their
        // item names (a format, so data to the sweep), and the choice of what to do with them appears with its help.
        app.Invoke("ImportBack");
        app.WaitFor("EntriesList");
        app.WaitGone("ImportTitle");
        app.Invoke("EntriesImport");
        app.WaitFor("ImportTitle");
        app.Invoke("ImportChoose");
        app.OpenInFileDialog(csvFile);
        var duplicates = AppResources.Format("ImportSummary", 0, rows.Length);
        app.WaitName("ImportSummary", duplicates);
        string[] duplicateNames = [.. rows.Select(row => AppResources.Format("ImportRowDuplicateName", row.Title))];
        AssertNameFromResources(app, "ImportPolicy");
        AssertNameFromResources(app, "ImportPolicyHelp");
        AssertNameFromResources(app, "ImportNothing");
        Assert.Equal(
            [AppResources.Strings["ImportPolicySkip"], AppResources.Strings["ImportPolicyDuplicate"], AppResources.Strings["ImportPolicyReplace"]],
            app.OptionNames("ImportPolicy"));
        Assert.Equal(duplicateNames, app.WaitItems("ImportPreviewList", rows.Length).Select(item => item.Name));
        AssertEveryTextFromResourcesOrData(app, [csvText, duplicates, rejected, ignored, .. rowTexts, .. duplicateNames]);

        // Replace gives it something to write, and the note that nothing is left to import goes away.
        app.SelectOption("ImportPolicy", AppResources.Strings["ImportPolicyReplace"]);
        app.WaitGone("ImportNothing");
        AssertEveryTextFromResourcesOrData(app, [csvText, duplicates, rejected, ignored, .. rowTexts, .. duplicateNames]);
    }

    [Fact]
    public void EmptyVault_EntriesEmptyTextComesFromResources()
    {
        using var app = AppSession.Launch(SeededVault.SeedEmpty);
        Assert.Single(app.Items("ProfilesList")).Patterns.Invoke.Pattern.Invoke();
        app.TypePassword(SeededVault.EmptyPassword);
        app.Invoke("UnlockButton");

        AssertNameFromResources(app, "EntriesEmpty");
        Assert.Empty(app.Items("EntriesList"));
        AssertEveryTextFromResourcesOrData(app);
    }

    // Choose a file through the open dialog and expect the page to refuse it with this message, back at the first step.
    private static void ChooseAndExpectError(AppSession app, string path, string message)
    {
        app.Invoke("ImportChoose");
        app.OpenInFileDialog(path);
        app.WaitName("ImportError", message);
        app.WaitEnabled("ImportChoose");
        AssertEveryTextFromResourcesOrData(app, message);
    }

    // The AutomationId of an element is its x:Uid, except the shared back button, whose uid is NavBack.
    private static void AssertNameFromResources(AppSession app, string automationId, string? uid = null) =>
        Assert.Equal(AppResources.ExpectedName(uid ?? automationId), app.WaitFor(automationId).Name);

    // Sweeps the window content for the Text, Button, Edit, List and ListItem elements and fails on any name that is
    // neither a resw value nor data; list items are named from their data (the profile name, the entry title). Two
    // subtrees are skipped because the OS or WinUI names them in the system language: the title bar and the scroll bars
    // (their line and page buttons). Call it right after the screen's anchor element is shown, and on Unlock only while
    // the PasswordBox is empty, because its reveal button is named by WinUI, not by the resw. An element without a Name
    // (for example the TextBlock of an empty username) has no text, so it is skipped; reading Name directly would throw
    // there.
    private static void AssertEveryTextFromResourcesOrData(AppSession app, params IEnumerable<string> data)
    {
        var allowed = new HashSet<string>(AppResources.Strings.Values, StringComparer.Ordinal);
        allowed.UnionWith(data);

        var foreign = TextElements(app.Window)
            .Select(element => (Element: element, Name: element.Properties.Name.ValueOrDefault))
            .Where(named => !string.IsNullOrEmpty(named.Name) && !allowed.Contains(named.Name))
            .Select(named => $"{named.Element.ControlType} id '{named.Element.Properties.AutomationId.ValueOrDefault}' '{named.Name}'")
            .ToArray();

        Assert.Empty(foreign);
    }

    private static IEnumerable<AutomationElement> TextElements(AutomationElement parent)
    {
        foreach (var child in parent.FindAllChildren())
        {
            var type = child.ControlType;
            if (type is ControlType.TitleBar or ControlType.ScrollBar)
            {
                continue;
            }

            if (TextControlTypes.Contains(type))
            {
                yield return child;
            }

            foreach (var descendant in TextElements(child))
            {
                yield return descendant;
            }
        }
    }

    // The data of the list page: the titles and usernames of the items, and the folder names of the folder filter.
    private static IEnumerable<string> EntryTexts() =>
        SeededVault.Entries.SelectMany(entry => new[]
        {
            entry.GetProperty("title").GetString()!,
            entry.GetProperty("username").GetString()!,
            entry.GetProperty("folder").GetString()!,
        });
}
