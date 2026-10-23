using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PasswordManager.Core.Vault;

/// <summary>Writes timestamps as ISO-8601 UTC with milliseconds (2026-01-01T12:00:00.000Z), as Android does.</summary>
internal sealed class UtcTimestampConverter : JsonConverter<DateTimeOffset>
{
    private const string Format = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.GetDateTimeOffset();

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.UtcDateTime.ToString(Format, CultureInfo.InvariantCulture));
}
