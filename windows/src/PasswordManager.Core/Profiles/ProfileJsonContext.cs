using System.Text.Json.Serialization;
using PasswordManager.Core.Vault;

namespace PasswordManager.Core.Profiles;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    RespectNullableAnnotations = true,
    Converters = [typeof(UtcTimestampConverter)])]
[JsonSerializable(typeof(ProfileList))]
internal sealed partial class ProfileJsonContext : JsonSerializerContext;
