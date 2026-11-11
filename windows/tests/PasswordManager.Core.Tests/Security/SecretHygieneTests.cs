using PasswordManager.Core.Vault;

namespace PasswordManager.Core.Tests.Security;

// W-32 guards over two claims of the security review about the Core types: a vault record never prints entry secrets
// and the authentication exception carries no detail. VaultJsonTests.Entry_ToString_DoesNotLeakSecrets and the
// VaultSession Lock tests stay where they are; these tests cover the types those do not.
public class SecretHygieneTests
{
    private const string FixedMessage = "Vault authentication failed.";
    private const string MasterPassword = "fictitious master password";
    private const string WrongPassword = "fictitious wrong password";

    private static readonly KdfParameters LowCost = new(1024, 2);

    // Fictitious Vault Key 0x20..0x3F.
    private static readonly byte[] Vk = [.. Enumerable.Range(0x20, 32).Select(i => (byte)i)];

    private static readonly VaultEntry FullEntry = new()
    {
        Id = Guid.Parse("0b6f7c1e-2f5d-4a6b-9a8e-3c1d2e4f5a6b"),
        Title = "Fictitious title",
        Username = "fictitious-user@example.com",
        Password = "fictitious-password",
        Url = "https://example.com/fictitious",
        Notes = "fictitious notes",
        Folder = "Personal",
        Favorite = true,
        CreatedAt = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero),
        UpdatedAt = new DateTimeOffset(2026, 1, 2, 12, 0, 0, TimeSpan.Zero),
    };

    [Fact]
    public void VaultContent_ToString_DoesNotPrintEntrySecrets()
    {
        // The record prints the list type, not the entries. If that ever changes, the title may print (VaultEntry's
        // documented choice) but no username, password, URL or notes may.
        var content = new VaultContent { Entries = [FullEntry] };

        var text = content.ToString() ?? string.Empty;

        Assert.False(string.IsNullOrWhiteSpace(text), "VaultContent.ToString() printed nothing: the check read no text.");
        Assert.Contains(nameof(VaultContent), text, StringComparison.Ordinal);
        Assert.DoesNotContain(FullEntry.Username, text, StringComparison.Ordinal);
        Assert.DoesNotContain(FullEntry.Password, text, StringComparison.Ordinal);
        Assert.DoesNotContain(FullEntry.Url, text, StringComparison.Ordinal);
        Assert.DoesNotContain(FullEntry.Notes, text, StringComparison.Ordinal);
    }

    [Fact]
    public void VaultAuthenticationException_IsAFixedMessage()
    {
        // The real throw path: a password slot opened with the wrong password. A wrong password, a wrong kit and a
        // tampered file all reach this same parameterless exception, so its message can never tell them apart and its
        // ToString() cannot carry the password that failed.
        var header = VaultHeader.Build([KeySlot.CreatePasswordSlot(MasterPassword, LowCost, Vk)]);
        var file = VaultBody.Seal(header, Vk, new VaultContent());
        var exception = Assert.Throws<VaultAuthenticationException>(
            () => KeySlot.UnwrapPasswordSlot(
                file,
                VaultHeader.Parse(file).PasswordSlotOffset,
                WrongPassword,
                new byte[VaultFormat.KeySize]));

        Assert.Equal(FixedMessage, exception.Message);
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain(WrongPassword, exception.ToString(), StringComparison.Ordinal);
    }
}
