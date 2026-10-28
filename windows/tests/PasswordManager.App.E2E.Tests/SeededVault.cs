using System.Text;
using System.Text.Json;

namespace PasswordManager.App.E2E.Tests;

internal static class SeededVault
{
    public const string ProfileName = "Perfil E2E";

    public static readonly JsonElement Manifest = ReadManifest("entries-unicode");

    public static string Password => Manifest.GetProperty("password").GetString()!;

    // Password of the test vector without entries; its manifest is read on demand, only the tests that seed it need it.
    public static string EmptyPassword => ReadManifest("empty").GetProperty("password").GetString()!;

    public static JsonElement[] Entries => [.. Manifest.GetProperty("content").GetProperty("entries").EnumerateArray()];

    public static void Seed(string directory) => SeedVector(directory, "entries-unicode");

    public static void SeedEmpty(string directory) => SeedVector(directory, "empty");

    public static void SeedProfileOnly(string directory) => WriteProfileList(directory);

    // The id of the only profile in the folder's profiles.json (as written by a seed or by the app).
    public static string ProfileId(string directory)
    {
        using var list = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "profiles.json")));
        var profile = Assert.Single(list.RootElement.GetProperty("profiles").EnumerateArray());
        return profile.GetProperty("id").GetString()!;
    }

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
