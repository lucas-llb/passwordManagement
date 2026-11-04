using System.Text.Json.Serialization;

namespace PasswordManager.Core.Preferences;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = false)]
[JsonSerializable(typeof(PreferencesFile))]
internal sealed partial class PreferencesJsonContext : JsonSerializerContext;
