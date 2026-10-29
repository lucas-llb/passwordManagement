using System.Globalization;
using System.Text;
using System.Text.Json;
using PasswordManager.Core.Profiles;
using PasswordManager.Core.Vault;

namespace PasswordManager.App.E2E.Tests;

internal static class SeededVault
{
    public const string ProfileName = "Perfil E2E";

    public static readonly JsonElement Manifest = ReadManifest("entries-unicode");

    public static string Password => Manifest.GetProperty("password").GetString()!;

    // Password of the test vector without entries; its manifest is read on demand, only the tests that seed it need it.
    public static string EmptyPassword => ReadManifest("empty").GetProperty("password").GetString()!;

    public static JsonElement[] Entries => [.. Manifest.GetProperty("content").GetProperty("entries").EnumerateArray()];

    // The 2,000-entry vault of the list performance test: built here through Core (no test vector, since spec/test-vectors
    // is shared with Android), with the same cheap Argon2id cost as the vectors. Every value is fictitious.
    public const int LargeCount = 2000;

    public const string LargePassword = "Senha fictícia do cofre grande 2000";

    private static readonly KdfParameters LargeKdf = new(1024, 2);

    public static void Seed(string directory) => SeedVector(directory, "entries-unicode");

    public static void SeedEmpty(string directory) => SeedVector(directory, "empty");

    public static void SeedProfileOnly(string directory) => WriteProfileList(directory);

    // Entry i (1..2000) is "Entrada {i:D4}", in folder "Pasta {i % 5}", and a favorite when i % 7 == 0.
    public static void SeedLarge(string directory)
    {
        var kitText = new char[RecoveryKit.TextLength];
        try
        {
            var file = VaultSession.Create(LargePassword, kitText, LargeKdf);
            var repository = new ProfileRepository(directory);
            var profile = repository.Create(ProfileName, file);

            using var session = new VaultSession(repository.GetVaultPath(profile.Id), LargeKdf);
            session.Unlock(LargePassword);
            session.Save(new VaultContent { Entries = [.. Enumerable.Range(1, LargeCount).Select(LargeEntry)] });
        }
        finally
        {
            Array.Clear(kitText);
        }
    }

    public static string LargeTitle(int index) => string.Create(CultureInfo.InvariantCulture, $"Entrada {index:D4}");

    // The id of the only profile in the folder's profiles.json (as written by a seed or by the app).
    public static string ProfileId(string directory)
    {
        using var list = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "profiles.json")));
        var profile = Assert.Single(list.RootElement.GetProperty("profiles").EnumerateArray());
        return profile.GetProperty("id").GetString()!;
    }

    private static VaultEntry LargeEntry(int index) => new()
    {
        Id = Guid.NewGuid(),
        Title = LargeTitle(index),
        Username = string.Create(CultureInfo.InvariantCulture, $"usuario{index:D4}@example.com"),
        Password = string.Create(CultureInfo.InvariantCulture, $"senha-ficticia-{index:D4}"),
        Url = string.Create(CultureInfo.InvariantCulture, $"https://site{index:D4}.example.com"),
        Notes = string.Create(CultureInfo.InvariantCulture, $"Nota fictícia {index:D4}"),
        Folder = string.Create(CultureInfo.InvariantCulture, $"Pasta {index % 5}"),
        Favorite = index % 7 == 0,
        CreatedAt = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero),
        UpdatedAt = new DateTimeOffset(2026, 1, 2, 12, 0, 0, TimeSpan.Zero),
    };

    private static JsonElement ReadManifest(string vectorName) => JsonDocument.Parse(
        File.ReadAllBytes(Path.Combine(AppSession.Metadata("TestVectorsDir"), $"{vectorName}.json"))).RootElement;

    private static void SeedVector(string directory, string vectorName)
    {
        var id = WriteProfileList(directory);
        File.Copy(
            Path.Combine(AppSession.Metadata("TestVectorsDir"), $"{vectorName}.pmvault"),
            Path.Combine(directory, $"{id}.pmvault"));
    }

    private static Guid WriteProfileList(string directory)
    {
        var id = Guid.NewGuid();
        File.WriteAllText(
            Path.Combine(directory, "profiles.json"),
            $$"""{"profiles":[{"id":"{{id}}","displayName":"{{ProfileName}}","createdAt":"2026-01-01T12:00:00.000Z"}]}""",
            new UTF8Encoding(false));
        return id;
    }
}
