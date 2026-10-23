using System.Text.Json;
using PasswordManager.Core.Vault;

namespace PasswordManager.Core.Tests.Vault;

public class VaultJsonTests
{
    private static readonly VaultEntry SampleEntry = new()
    {
        Id = Guid.Parse("0b6f7c1e-2f5d-4a6b-9a8e-3c1d2e4f5a6b"),
        Title = "Example",
        Username = "user@example.com",
        Password = "not-a-real-password",
        Url = "https://example.com",
        Notes = "notes",
        Folder = "Personal",
        Favorite = true,
        CreatedAt = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero),
        UpdatedAt = new DateTimeOffset(2026, 1, 2, 12, 0, 0, TimeSpan.Zero),
    };

    [Fact]
    public void Content_RoundTrips()
    {
        var content = new VaultContent { Entries = [SampleEntry] };

        var json = JsonSerializer.Serialize(content, VaultJsonContext.Default.VaultContent);
        var restored = JsonSerializer.Deserialize(json, VaultJsonContext.Default.VaultContent);

        Assert.NotNull(restored);
        Assert.Equal(content.Entries, restored.Entries);
    }

    [Fact]
    public void Content_UsesCamelCaseFieldNames()
    {
        var json = JsonSerializer.Serialize(
            new VaultContent { Entries = [SampleEntry] },
            VaultJsonContext.Default.VaultContent);

        Assert.Contains("\"createdAt\"", json, StringComparison.Ordinal);
        Assert.Contains("\"favorite\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Content_WritesTimestampsAsUtcWithMilliseconds()
    {
        var entry = SampleEntry with { CreatedAt = new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.FromHours(-3)) };

        var json = JsonSerializer.Serialize(new VaultContent { Entries = [entry] }, VaultJsonContext.Default.VaultContent);

        Assert.Contains("\"createdAt\":\"2026-01-01T12:00:00.000Z\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Entry_ToString_DoesNotLeakSecrets()
    {
        var text = SampleEntry.ToString();

        Assert.DoesNotContain(SampleEntry.Password, text, StringComparison.Ordinal);
        Assert.DoesNotContain(SampleEntry.Username, text, StringComparison.Ordinal);
    }
}
