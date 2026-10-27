using System.Text;
using System.Text.Json;

namespace PasswordManager.App.E2E.Tests;

internal static class SeededVault
{
    public const string ProfileName = "Perfil E2E";

    public static readonly JsonElement Manifest = JsonDocument.Parse(
        File.ReadAllBytes(Path.Combine(AppSession.Metadata("TestVectorsDir"), "entries-unicode.json"))).RootElement;

    public static string Password => Manifest.GetProperty("password").GetString()!;

    public static JsonElement[] Entries => [.. Manifest.GetProperty("content").GetProperty("entries").EnumerateArray()];

    public static void Seed(string directory)
    {
        var id = WriteProfileList(directory);
        File.Copy(
            Path.Combine(AppSession.Metadata("TestVectorsDir"), "entries-unicode.pmvault"),
            Path.Combine(directory, $"{id}.pmvault"));
    }

    public static void SeedProfileOnly(string directory) => WriteProfileList(directory);

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
