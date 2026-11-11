using System.Globalization;
using System.Text;
using System.Text.Json;
using FlaUI.Core.Tools;
using PasswordManager.App.E2E.Tests.Localization;
using PasswordManager.Core.Vault;

namespace PasswordManager.App.E2E.Tests;

// The import (W-25), on the real app over the seeded vault and the real Windows open dialog driven through UI Automation:
// choosing a file only reads it, the preview names what will happen, and the vault is written once, only after Confirm,
// with the .bak holding the version before. A wrong password, a tampered or malformed file, a canceled dialog, Back and a
// session lock write nothing. The fixtures are written to <vaults folder>\imports (removed with the test's folder) as C#
// literals with explicit line ends; every title, address and password is fictitious.
public sealed class ImportE2ETests(ITestOutputHelper output)
{
    private const string WrongFilePassword = "senha-errada-ficticia";
    private const string PmvaultFileName = "backup-importado.pmvault";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(100);

    private static string[] SeededTitles => [.. SeededVault.Entries.Select(e => e.GetProperty("title").GetString()!)];

    [Fact]
    public void Import_Csv_WritesOnlyAfterConfirm()
    {
        using var app = AppSession.Launch(SeededVault.Seed);
        Unlock(app, SeededVault.Password);
        var imports = CreateImportsFolder(app);
        var seeded = SeededVault.ReadContent(app.VaultsDirectory, SeededVault.Password).Entries;
        VaultEntry[] added =
        [
            NewEntry("Servidor novo fictício", "admin-novo", "Senha-nova-ficticia-111", "https://servidor.example.com"),
            NewEntry("Roteador novo fictício", "roteador", "Senha-nova-ficticia-222", "https://roteador.example.com"),
        ];
        var csv = VaultCsv.Serialize([.. seeded, .. added]);
        var csvPath = WriteFile(imports, "senhas.csv", csv);
        var vaultPath = SeededVault.VaultPath(app.VaultsDirectory);
        var vaultBefore = File.ReadAllBytes(vaultPath);
        var vaults = Snapshot(app.VaultsDirectory);

        OpenImport(app);
        app.WaitDisplayAffinity(NativeMethods.WDA_EXCLUDEFROMCAPTURE);
        var start = DateTimeOffset.UtcNow;
        ChooseFile(app, csvPath);

        // The preview: 2 new rows and the 4 entries the vault already has, named so a screen reader says which is which.
        app.WaitName("ImportSummary", AppResources.Format("ImportSummary", added.Length, seeded.Count));
        app.WaitName("ImportFileName", AppResources.Format("ImportFileName", "senhas.csv"));
        Assert.Null(app.Find("ImportError"));
        Assert.Null(app.Find("ImportPassword"));
        string[] rows =
        [
            .. seeded.Select(entry => AppResources.Format("ImportRowDuplicateName", entry.Title)),
            .. added.Select(entry => entry.Title),
        ];
        Assert.Equal(rows, app.WaitItems("ImportPreviewList", rows.Length).Select(item => item.Name));

        // Nothing was written yet, and no password of the file is on screen.
        AssertSnapshot(vaults, app.VaultsDirectory);
        Assert.False(File.Exists(vaultPath + VaultFile.BackupSuffix));
        AssertNotOnScreen(app, [.. seeded.Concat(added).Select(entry => entry.Password)]);

        app.WaitEnabled("ImportConfirm");
        app.Invoke("ImportConfirm");
        app.WaitName("ImportDone", AppResources.Format("ImportDone", added.Length, 0, seeded.Count));
        var end = DateTimeOffset.UtcNow;
        Assert.Equal(AppResources.ExpectedName("ImportDoneCsvReminder"), app.WaitFor("ImportDoneCsvReminder").Name);
        Assert.Null(app.Find("ImportConfirm"));
        Assert.Null(app.Find("ImportPreviewList"));
        Assert.Null(app.Find("ImportError"));

        // On disk: the 4 saved entries untouched, then the 2 new ones in file order with new Ids and the import time.
        var entries = SeededVault.ReadContent(app.VaultsDirectory, SeededVault.Password).Entries;
        Assert.Equal(seeded.Count + added.Length, entries.Count);
        Assert.Equal(seeded, entries.Take(seeded.Count));
        for (var index = 0; index < added.Length; index++)
        {
            var saved = entries[seeded.Count + index];
            var source = added[index];
            Assert.Equal(source.Title, saved.Title);
            Assert.Equal(source.Username, saved.Username);
            Assert.Equal(source.Password, saved.Password);
            Assert.Equal(source.Url, saved.Url);
            Assert.NotEqual(source.Id, saved.Id);
            Assert.DoesNotContain(saved.Id, seeded.Select(entry => entry.Id));
            Assert.Equal(saved.CreatedAt, saved.UpdatedAt);
            Assert.InRange(saved.CreatedAt, start.AddMilliseconds(-1), end);
        }

        // The .bak is the vault as it was before the import, and the source file is as it was.
        var backupPath = vaultPath + VaultFile.BackupSuffix;
        Assert.Equal(vaultBefore, File.ReadAllBytes(backupPath));
        Assert.Equal(seeded, SeededVault.ReadFile(backupPath, SeededVault.Password).Entries);
        Assert.Equal(csv, File.ReadAllBytes(csvPath));

        // Back shows the new titles in the list.
        app.Invoke("ImportBack");
        app.WaitFor("EntriesList");
        app.WaitGone("ImportTitle");
        WaitTitles(app, [.. SeededTitles, .. added.Select(entry => entry.Title)]);
    }

    [Fact]
    public void Import_KeePass1xCsv_KeepsBackslashAndQuote()
    {
        using var app = AppSession.Launch(SeededVault.SeedEmpty);
        Unlock(app, SeededVault.EmptyPassword);
        var imports = CreateImportsFolder(app);

        // KeePass 1.x escapes with a backslash inside the quotes: \" is a quote and \\ is a backslash; any other \x stays
        // as the two characters. Read as RFC 4180, the first password would be corrupted.
        var csv = """
            "Account","Login Name","Password","Web Site","Comments"
            "Conta A","a@example.com","a\"b\\c","https://a.example.com","Nota A"
            "Conta B","b@example.com","x\ny","https://b.example.com","Nota B"
            """.ReplaceLineEndings("\r\n") + "\r\n";
        var csvPath = WriteFile(imports, "keepass1.csv", Encoding.UTF8.GetBytes(csv));

        OpenImport(app);
        ChooseFile(app, csvPath);
        app.WaitName("ImportSummary", AppResources.Format("ImportSummary", 2, 0));
        Assert.Null(app.Find("ImportPolicy"));
        Assert.Null(app.Find("ImportIgnoredColumns"));
        Assert.Null(app.Find("ImportRejected"));
        app.Invoke("ImportConfirm");
        app.WaitName("ImportDone", AppResources.Format("ImportDone", 2, 0, 0));

        var entries = SeededVault.ReadContent(app.VaultsDirectory, SeededVault.EmptyPassword).Entries;
        Assert.Equal(2, entries.Count);
        Assert.Equal("Conta A", entries[0].Title);
        Assert.Equal("a@example.com", entries[0].Username);
        Assert.Equal("a\"b\\c", entries[0].Password);
        Assert.Equal("https://a.example.com", entries[0].Url);
        Assert.Equal("Nota A", entries[0].Notes);
        Assert.Equal("Conta B", entries[1].Title);
        Assert.Equal("x\\ny", entries[1].Password);
    }

    [Fact]
    public void Import_BitwardenCsv_ReportsIgnoredColumns()
    {
        using var app = AppSession.Launch(SeededVault.SeedEmpty);
        Unlock(app, SeededVault.EmptyPassword);
        var imports = CreateImportsFolder(app);
        const string totp = "totp-secreto-ficticio";
        const string password = "Senha-bitwarden-ficticia";

        // The individual export layout. Only login_totp has a value, so only it is named; "fields" is empty in every row.
        // The last record has too few fields: only it is left out, and it starts on line 4.
        var csv = $"""
            folder,favorite,type,name,notes,fields,reprompt,login_uri,login_username,login_password,login_totp
            Trabalho,1,login,Servidor fictício,Nota do servidor,,0,https://servidor.example.com,admin,{password},{totp}
            ,,note,Nota solta fictícia,Só texto,,0,,,,
            Trabalho,0,login,Linha curta
            """.ReplaceLineEndings("\r\n") + "\r\n";
        var csvPath = WriteFile(imports, "bitwarden.csv", Encoding.UTF8.GetBytes(csv));

        OpenImport(app);
        ChooseFile(app, csvPath);
        app.WaitName("ImportSummary", AppResources.Format("ImportSummary", 2, 0));
        var ignored = AppResources.Format("ImportIgnoredColumns", "login_totp");
        app.WaitName("ImportIgnoredColumns", ignored);
        app.WaitName("ImportRejected", AppResources.Format("ImportRejected", 1, 4));
        Assert.DoesNotContain(totp, ignored, StringComparison.Ordinal);
        AssertNotOnScreen(app, totp, password);

        app.Invoke("ImportConfirm");
        app.WaitName("ImportDone", AppResources.Format("ImportDone", 2, 0, 0));
        var entries = SeededVault.ReadContent(app.VaultsDirectory, SeededVault.EmptyPassword).Entries;
        Assert.Equal(2, entries.Count);
        Assert.Equal("Servidor fictício", entries[0].Title);
        Assert.Equal("admin", entries[0].Username);
        Assert.Equal(password, entries[0].Password);
        Assert.Equal("https://servidor.example.com", entries[0].Url);
        Assert.Equal("Nota do servidor", entries[0].Notes);
        Assert.Equal("Trabalho", entries[0].Folder);
        Assert.True(entries[0].Favorite);
        Assert.Equal("Nota solta fictícia", entries[1].Title);
        Assert.Equal(string.Empty, entries[1].Password);
        Assert.Equal("Só texto", entries[1].Notes);
        Assert.False(entries[1].Favorite);
    }

    [Theory]
    [InlineData(DuplicatePolicy.Skip)]
    [InlineData(DuplicatePolicy.Duplicate)]
    [InlineData(DuplicatePolicy.Replace)]
    public void Import_Duplicates_FollowPolicy(DuplicatePolicy policy)
    {
        using var app = AppSession.Launch(SeededVault.Seed);
        Unlock(app, SeededVault.Password);
        var imports = CreateImportsFolder(app);
        var seeded = SeededVault.ReadContent(app.VaultsDirectory, SeededVault.Password).Entries;
        const string otherPassword = "Outra-senha-ficticia-777";

        // The first row has the title, username and URL of a saved entry (a favorite) and another password.
        var duplicate = seeded[0] with { Password = otherPassword };
        var fresh = NewEntry("Entrada nova fictícia", "novo", "Senha-nova-ficticia-333", "https://novo.example.com");
        var csvPath = WriteFile(imports, "duplicadas.csv", VaultCsv.Serialize([duplicate, fresh]));

        OpenImport(app);
        var start = DateTimeOffset.UtcNow;
        ChooseFile(app, csvPath);
        var summary = AppResources.Format("ImportSummary", 1, 1);
        app.WaitName("ImportSummary", summary);
        Assert.Equal(AppResources.Strings["ImportPolicySkip"], app.SelectedOption("ImportPolicy"));
        Assert.Equal(
            [AppResources.Strings["ImportPolicySkip"], AppResources.Strings["ImportPolicyDuplicate"], AppResources.Strings["ImportPolicyReplace"]],
            app.OptionNames("ImportPolicy"));

        // The choice changes what is written, never what is a duplicate.
        app.SelectOption("ImportPolicy", AppResources.Strings[PolicyKey(policy)]);
        Assert.Equal(AppResources.Strings[PolicyKey(policy)], app.SelectedOption("ImportPolicy"));
        Assert.Equal(summary, app.WaitFor("ImportSummary").Name);
        app.WaitEnabled("ImportConfirm");
        app.Invoke("ImportConfirm");

        var (added, replaced, skipped, total) = policy switch
        {
            DuplicatePolicy.Skip => (1, 0, 1, 5),
            DuplicatePolicy.Duplicate => (2, 0, 0, 6),
            _ => (1, 1, 0, 5),
        };
        app.WaitName("ImportDone", AppResources.Format("ImportDone", added, replaced, skipped));
        var end = DateTimeOffset.UtcNow;
        var entries = SeededVault.ReadContent(app.VaultsDirectory, SeededVault.Password).Entries;
        Assert.Equal(total, entries.Count);
        Assert.Equal(fresh.Title, entries[^1].Title);
        Assert.Equal(fresh.Password, entries[^1].Password);

        switch (policy)
        {
            case DuplicatePolicy.Skip:
                Assert.Equal(seeded, entries.Take(seeded.Count));
                break;
            case DuplicatePolicy.Duplicate:
                Assert.Equal(seeded, entries.Take(seeded.Count));
                Assert.Equal(seeded[0].Title, entries[4].Title);
                Assert.Equal(otherPassword, entries[4].Password);
                Assert.NotEqual(seeded[0].Id, entries[4].Id);
                break;
            default:
                // In place: same Id and creation date, the new password, the favorite kept (the CSV has no favorite), a
                // fresh update date; the other saved entries are untouched.
                Assert.Equal(seeded[0].Id, entries[0].Id);
                Assert.Equal(seeded[0].CreatedAt, entries[0].CreatedAt);
                Assert.Equal(otherPassword, entries[0].Password);
                Assert.Equal(seeded[0].Title, entries[0].Title);
                Assert.Equal(seeded[0].Notes, entries[0].Notes);
                Assert.True(entries[0].Favorite);
                Assert.InRange(entries[0].UpdatedAt, start.AddMilliseconds(-1), end);
                Assert.Equal(seeded.Skip(1), entries.Skip(1).Take(seeded.Count - 1));
                break;
        }
    }

    [Fact]
    public void Import_AllDuplicatesWithSkip_CannotConfirm()
    {
        using var app = AppSession.Launch(SeededVault.Seed);
        Unlock(app, SeededVault.Password);
        var imports = CreateImportsFolder(app);
        var seeded = SeededVault.ReadContent(app.VaultsDirectory, SeededVault.Password).Entries;
        var csvPath = WriteFile(
            imports,
            "so-duplicadas.csv",
            VaultCsv.Serialize([seeded[0] with { Password = "Outra-senha-ficticia-1" }, seeded[1] with { Password = "Outra-senha-ficticia-2" }]));
        var vaults = Snapshot(app.VaultsDirectory);

        OpenImport(app);
        ChooseFile(app, csvPath);
        app.WaitName("ImportSummary", AppResources.Format("ImportSummary", 0, 2));

        // Skip, the default, would write nothing: the page says so and Confirm is disabled.
        app.WaitFor("ImportNothing");
        app.WaitEnabled("ImportConfirm", expected: false);

        // Another choice gives it something to write, and back to Skip takes it away again.
        app.SelectOption("ImportPolicy", AppResources.Strings["ImportPolicyReplace"]);
        app.WaitGone("ImportNothing");
        app.WaitEnabled("ImportConfirm");
        app.SelectOption("ImportPolicy", AppResources.Strings["ImportPolicyDuplicate"]);
        app.WaitEnabled("ImportConfirm");
        app.SelectOption("ImportPolicy", AppResources.Strings["ImportPolicySkip"]);
        app.WaitFor("ImportNothing");
        app.WaitEnabled("ImportConfirm", expected: false);

        // Left without confirming: nothing was written.
        app.Invoke("ImportBack");
        app.WaitFor("EntriesList");
        AssertSnapshot(vaults, app.VaultsDirectory);
    }

    [Fact]
    public void Import_Pmvault_WrongThenRightPassword()
    {
        using var app = AppSession.Launch(SeededVault.Seed);
        Unlock(app, SeededVault.Password);
        var imports = CreateImportsFolder(app);
        var filePath = Path.Combine(imports, PmvaultFileName);
        SeededVault.CopyVector("export", filePath);
        var fileBytes = File.ReadAllBytes(filePath);
        var seeded = SeededVault.ReadContent(app.VaultsDirectory, SeededVault.Password).Entries;
        var vaults = Snapshot(app.VaultsDirectory);

        OpenImport(app);
        ChooseFile(app, filePath);

        // The file looks like a vault, so its password is asked for; nothing is decrypted yet.
        app.WaitFor("ImportPassword");
        app.WaitName("ImportFileName", AppResources.Format("ImportFileName", PmvaultFileName));
        Assert.Null(app.Find("ImportSummary"));
        Assert.Null(app.Find("ImportPreviewList"));

        // A wrong password stays on the step with the error: no preview, and the vault is as it was.
        app.SetValue("ImportPassword", WrongFilePassword);
        app.Invoke("ImportOpen");
        app.WaitName("ImportError", AppResources.Strings["ImportErrorWrongPassword"]);
        app.WaitEnabled("ImportOpen");
        Assert.Null(app.Find("ImportSummary"));
        Assert.Null(app.Find("ImportPreviewList"));
        AssertSnapshot(vaults, app.VaultsDirectory);

        // The right one shows the two entries of the file.
        app.SetValue("ImportPassword", SeededVault.ExportPassword);
        app.Invoke("ImportOpen");
        app.WaitName("ImportSummary", AppResources.Format("ImportSummary", 2, 0));
        app.WaitGone("ImportError");
        Assert.Equal(
            SeededVault.ExportEntries.Select(entry => entry.GetProperty("title").GetString()!),
            app.WaitItems("ImportPreviewList", 2).Select(item => item.Name));
        AssertSnapshot(vaults, app.VaultsDirectory);

        app.Invoke("ImportConfirm");
        app.WaitName("ImportDone", AppResources.Format("ImportDone", 2, 0, 0));
        Assert.Null(app.Find("ImportDoneCsvReminder"));

        // The two entries are appended as they were exported: same Ids, dates, flags and texts.
        var entries = SeededVault.ReadContent(app.VaultsDirectory, SeededVault.Password).Entries;
        Assert.Equal(seeded.Count + 2, entries.Count);
        Assert.Equal(seeded, entries.Take(seeded.Count));
        var exported = SeededVault.ExportEntries;
        for (var index = 0; index < exported.Length; index++)
        {
            var source = exported[index];
            var saved = entries[seeded.Count + index];
            Assert.Equal(Guid.Parse(source.GetProperty("id").GetString()!), saved.Id);
            Assert.Equal(Date(source, "createdAt"), saved.CreatedAt);
            Assert.Equal(Date(source, "updatedAt"), saved.UpdatedAt);
            Assert.Equal(source.GetProperty("favorite").GetBoolean(), saved.Favorite);
            Assert.Equal(source.GetProperty("title").GetString(), saved.Title);
            Assert.Equal(source.GetProperty("username").GetString(), saved.Username);
            Assert.Equal(source.GetProperty("password").GetString(), saved.Password);
            Assert.Equal(source.GetProperty("url").GetString(), saved.Url);
            Assert.Equal(source.GetProperty("notes").GetString(), saved.Notes);
            Assert.Equal(source.GetProperty("folder").GetString(), saved.Folder);
        }

        Assert.Equal(fileBytes, File.ReadAllBytes(filePath));
    }

    [Fact]
    public void Import_TamperedPmvault_ShowsSameError()
    {
        using var app = AppSession.Launch(SeededVault.Seed);
        Unlock(app, SeededVault.Password);
        var imports = CreateImportsFolder(app);
        var filePath = Path.Combine(imports, PmvaultFileName);
        SeededVault.CopyVector("export", filePath);

        // One flipped bit in the last byte, which belongs to the body: the header still parses and the slot still opens.
        var bytes = File.ReadAllBytes(filePath);
        bytes[^1] ^= 0x01;
        File.WriteAllBytes(filePath, bytes);
        var vaults = Snapshot(app.VaultsDirectory);

        OpenImport(app);
        ChooseFile(app, filePath);
        app.WaitFor("ImportPassword");

        // The right password: the message is the one of a wrong password, and no entry reaches the preview.
        app.SetValue("ImportPassword", SeededVault.ExportPassword);
        app.Invoke("ImportOpen");
        app.WaitName("ImportError", AppResources.Strings["ImportErrorWrongPassword"]);
        app.WaitEnabled("ImportOpen");
        Assert.Null(app.Find("ImportSummary"));
        Assert.Null(app.Find("ImportPreviewList"));
        AssertSnapshot(vaults, app.VaultsDirectory);
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("header")]
    [InlineData("utf16")]
    [InlineData("notavault")]
    public void Import_BadFiles_ShowErrorAndWriteNothing(string kind)
    {
        using var app = AppSession.Launch(SeededVault.Seed);
        Unlock(app, SeededVault.Password);
        var imports = CreateImportsFolder(app);
        const string header = "name,url,username,password,notes,folder";

        byte[] utf16 =
        [
            .. Encoding.Unicode.GetPreamble(),
            .. Encoding.Unicode.GetBytes(header + "\r\nUm,https://um.example.com,u,p,,\r\n"),
        ];

        // In "malformed", the quote that opens on line 3 never closes.
        var (fileName, bytes, expected) = kind switch
        {
            "malformed" => (
                "malformado.csv",
                Encoding.UTF8.GetBytes(header + "\r\nUm,https://um.example.com,u,p,,\r\nDois,\"sem fim,u,p,,\r\n"),
                AppResources.Format("ImportErrorMalformedCsv", 3)),
            "header" => (
                "cabecalho.csv",
                Encoding.UTF8.GetBytes("coluna1,coluna2,coluna3\r\na,b,c\r\n"),
                AppResources.Strings["ImportErrorInvalidHeader"]),
            "utf16" => (
                "utf16.csv",
                utf16,
                AppResources.Strings["ImportErrorInvalidEncoding"]),
            _ => (
                "texto.pmvault",
                Encoding.UTF8.GetBytes("isto não é um cofre"),
                AppResources.Strings["ImportErrorInvalidVaultFile"]),
        };
        var filePath = WriteFile(imports, fileName, bytes);
        var vaults = Snapshot(app.VaultsDirectory);

        OpenImport(app);
        ChooseFile(app, filePath);

        // The page goes back to choosing a file, with the message and nothing kept of the file.
        app.WaitName("ImportError", expected);
        app.WaitFor("ImportChoose");
        app.WaitEnabled("ImportChoose");
        Assert.Null(app.Find("ImportPassword"));
        Assert.Null(app.Find("ImportFileName"));
        Assert.Null(app.Find("ImportSummary"));
        Assert.Null(app.Find("ImportPreviewList"));
        Assert.Null(app.Find("ImportDone"));
        AssertSnapshot(vaults, app.VaultsDirectory);
        Assert.Equal(bytes, File.ReadAllBytes(filePath));
    }

    [Fact]
    public void Import_DialogCanceled_StaysUsable()
    {
        using var app = AppSession.Launch(SeededVault.Seed);
        Unlock(app, SeededVault.Password);
        CreateImportsFolder(app);
        var vaults = Snapshot(app.VaultsDirectory);
        OpenImport(app);

        // While the dialog is open the import runs: Back and Choose are disabled and the ring is shown.
        app.Invoke("ImportChoose");
        app.WaitFileDialog();
        app.WaitEnabled("ImportBack", expected: false);
        app.WaitEnabled("ImportChoose", expected: false);
        app.WaitFor("ImportProgress");
        app.CancelFileDialog();

        app.WaitEnabled("ImportBack");
        app.WaitEnabled("ImportChoose");
        app.WaitGone("ImportProgress");
        Assert.Null(app.Find("ImportError"));
        Assert.Null(app.Find("ImportSummary"));
        Assert.Null(app.Find("ImportPassword"));
        AssertSnapshot(vaults, app.VaultsDirectory);

        app.Invoke("ImportBack");
        app.WaitFor("EntriesList");
        app.WaitGone("ImportTitle");
    }

    [Fact]
    public void Import_BackFromPreview_WritesNothing()
    {
        using var app = AppSession.Launch(SeededVault.Seed);
        Unlock(app, SeededVault.Password);
        var imports = CreateImportsFolder(app);
        VaultEntry[] added =
        [
            NewEntry("Servidor novo fictício", "admin-novo", "Senha-nova-ficticia-444", "https://servidor.example.com"),
            NewEntry("Roteador novo fictício", "roteador", "Senha-nova-ficticia-555", "https://roteador.example.com"),
        ];
        var csv = VaultCsv.Serialize(added);
        var csvPath = WriteFile(imports, "novas.csv", csv);
        var vaults = Snapshot(app.VaultsDirectory);

        OpenImport(app);
        ChooseFile(app, csvPath);
        app.WaitName("ImportSummary", AppResources.Format("ImportSummary", added.Length, 0));
        app.WaitItems("ImportPreviewList", added.Length);

        app.Invoke("ImportBack");
        app.WaitFor("EntriesList");
        app.WaitGone("ImportTitle");
        WaitTitles(app, SeededTitles);
        AssertSnapshot(vaults, app.VaultsDirectory);
        Assert.False(File.Exists(SeededVault.VaultPath(app.VaultsDirectory) + VaultFile.BackupSuffix));
        Assert.Equal(csv, File.ReadAllBytes(csvPath));

        // A new visit starts empty: nothing of the discarded file is kept, and nothing of it is on screen.
        OpenImport(app);
        app.WaitFor("ImportChoose");
        Assert.Null(app.Find("ImportSummary"));
        Assert.Null(app.Find("ImportFileName"));
        Assert.Null(app.Find("ImportPreviewList"));
        AssertNotOnScreen(app, [.. added.Select(entry => entry.Password)]);
    }

    [Fact]
    public void Import_SessionLock_OnPreview_DiscardsAndWritesNothing()
    {
        using var app = AppSession.Launch(SeededVault.Seed);
        Unlock(app, SeededVault.Password);
        var imports = CreateImportsFolder(app);
        VaultEntry[] added =
        [
            NewEntry("Servidor novo fictício", "admin-novo", "Senha-nova-ficticia-666", "https://servidor.example.com"),
            NewEntry("Roteador novo fictício", "roteador", "Senha-nova-ficticia-777", "https://roteador.example.com"),
        ];
        var csvPath = WriteFile(imports, "novas.csv", VaultCsv.Serialize(added));
        var vaults = Snapshot(app.VaultsDirectory);

        OpenImport(app);
        app.WaitDisplayAffinity(NativeMethods.WDA_EXCLUDEFROMCAPTURE);
        ChooseFile(app, csvPath);
        app.WaitName("ImportSummary", AppResources.Format("ImportSummary", added.Length, 0));

        app.PostSessionChange(NativeMethods.WTS_SESSION_LOCK);
        app.WaitFor("UnlockTitle");
        app.WaitGone("ImportTitle");

        // The unlock page takes the master password, so the lock leaves the window excluded from capture.
        app.WaitDisplayAffinity(NativeMethods.WDA_EXCLUDEFROMCAPTURE);
        AssertNotOnScreen(app, [.. added.Select(entry => entry.Title), .. added.Select(entry => entry.Password)]);
        AssertSnapshot(vaults, app.VaultsDirectory);

        // Unlocking again: the vault has the 4 old entries, and the import page is not behind the list.
        app.TypePassword(SeededVault.Password);
        app.Invoke("UnlockButton");
        app.WaitFor("EntriesList");
        app.WaitGone("UnlockPassword");
        WaitTitles(app, SeededTitles);
        Assert.Null(app.Find("ImportTitle"));
        Assert.Null(app.Find("ImportSummary"));
        AssertSnapshot(vaults, app.VaultsDirectory);
    }

    [Fact]
    public void Import_LargeCsv_ListIsVirtualized()
    {
        const int rows = 5000;
        using var app = AppSession.Launch(SeededVault.Seed);
        Unlock(app, SeededVault.Password);
        var imports = CreateImportsFolder(app);
        var entries = Enumerable.Range(1, rows)
            .Select(index => NewEntry(
                string.Create(CultureInfo.InvariantCulture, $"Importada {index:D4}"),
                string.Create(CultureInfo.InvariantCulture, $"usuario{index:D4}@example.com"),
                string.Create(CultureInfo.InvariantCulture, $"senha-ficticia-{index:D4}"),
                string.Create(CultureInfo.InvariantCulture, $"https://site{index:D4}.example.com")))
            .ToArray();
        var csvPath = WriteFile(imports, "grande.csv", VaultCsv.Serialize(entries));

        OpenImport(app);
        ChooseFile(app, csvPath);
        app.WaitName("ImportSummary", AppResources.Format("ImportSummary", rows, 0));

        // Only a window of the 5,000 rows is realized: the list is virtualized, and nothing wraps it in a scroller.
        var items = app.Items("ImportPreviewList");
        output.WriteLine($"realized items: {items.Length} of {rows}");
        Assert.InRange(items.Length, 1, rows - 1);
        Assert.Equal(entries[0].Title, items[0].Name);
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

    private static void OpenImport(AppSession app)
    {
        app.Invoke("EntriesImport");
        app.WaitFor("ImportTitle");
        app.WaitGone("EntriesList");
    }

    // Choose file, then the path through the real open dialog; the dialog is gone when this returns, so the read has begun.
    private static void ChooseFile(AppSession app, string path)
    {
        app.Invoke("ImportChoose");
        app.OpenInFileDialog(path);
    }

    // The import sources: a subfolder of the test's own folder, removed with it.
    private static string CreateImportsFolder(AppSession app) =>
        Directory.CreateDirectory(Path.Combine(app.VaultsDirectory, "imports")).FullName;

    private static string WriteFile(string folder, string name, byte[] contents)
    {
        var path = Path.Combine(folder, name);
        File.WriteAllBytes(path, contents);
        return path;
    }

    private static VaultEntry NewEntry(string title, string username, string password, string url) => new()
    {
        Id = Guid.NewGuid(),
        Title = title,
        Username = username,
        Password = password,
        Url = url,
        CreatedAt = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero),
        UpdatedAt = new DateTimeOffset(2026, 1, 2, 12, 0, 0, TimeSpan.Zero),
    };

    private static string PolicyKey(DuplicatePolicy policy) => policy switch
    {
        DuplicatePolicy.Skip => "ImportPolicySkip",
        DuplicatePolicy.Duplicate => "ImportPolicyDuplicate",
        _ => "ImportPolicyReplace",
    };

    private static DateTimeOffset Date(JsonElement entry, string field) =>
        DateTimeOffset.Parse(entry.GetProperty(field).GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    // Each item container is named with its entry's title (EntryListPage.OnEntryContainerContentChanging).
    private static string[] Names(AppSession app) => [.. app.Items("EntriesList").Select(item => item.Name)];

    // The list once it shows exactly these titles, in this order; it is rebuilt after an import.
    private static void WaitTitles(AppSession app, params string[] expected)
    {
        Retry.WhileFalse(() => Names(app).SequenceEqual(expected), Timeout, Poll, ignoreException: true);
        Assert.Equal(expected, Names(app));
    }

    // Fails when some element of the window holds one of these values as its Name or its Value (a password box is never
    // read), without printing any of them: only their number in the list. The empty ones are skipped.
    private static void AssertNotOnScreen(AppSession app, params string[] values)
    {
        var found = new SortedSet<int>();
        foreach (var element in app.Window.FindAllDescendants())
        {
            var name = element.Properties.Name.ValueOrDefault;
            var value = element.Properties.IsPassword.ValueOrDefault
                ? null
                : element.Patterns.Value.PatternOrDefault?.Value.ValueOrDefault;
            foreach (var text in new[] { name, value })
            {
                if (string.IsNullOrEmpty(text))
                {
                    continue;
                }

                for (var index = 0; index < values.Length; index++)
                {
                    if (values[index].Length > 0 && text.Contains(values[index], StringComparison.Ordinal))
                    {
                        found.Add(index + 1);
                    }
                }
            }
        }

        Assert.True(found.Count == 0, $"Imported values #{string.Join(", #", found)} are on screen.");
    }

    // Every file directly in the folder with its bytes (and each subfolder by name), to prove nothing was added or changed.
    private static SortedDictionary<string, byte[]?> Snapshot(string directory)
    {
        var snapshot = new SortedDictionary<string, byte[]?>(StringComparer.Ordinal);
        foreach (var entry in Directory.GetFileSystemEntries(directory))
        {
            snapshot.Add(Path.GetFileName(entry), File.Exists(entry) ? File.ReadAllBytes(entry) : null);
        }

        return snapshot;
    }

    // The import step has finished when this runs (its outcome is on screen), so one read is enough.
    private static void AssertSnapshot(SortedDictionary<string, byte[]?> expected, string directory)
    {
        var actual = Snapshot(directory);
        Assert.Equal(expected.Keys, actual.Keys);
        foreach (var (name, bytes) in expected)
        {
            Assert.True(
                bytes is null ? actual[name] is null : actual[name] is { } now && now.AsSpan().SequenceEqual(bytes),
                $"'{name}' in the vaults folder changed.");
        }
    }
}
