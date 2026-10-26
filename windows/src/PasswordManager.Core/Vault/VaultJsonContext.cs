using System.Text.Json.Serialization;

namespace PasswordManager.Core.Vault;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    RespectNullableAnnotations = true,
    Converters = [typeof(UtcTimestampConverter)])]
[JsonSerializable(typeof(VaultContent))]
internal sealed partial class VaultJsonContext : JsonSerializerContext;
