using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiffPdf.Client;

/// <summary>
/// String-enum converter that never throws on an unknown member name: a value this client's enum copy
/// does not know (a newer server added a status) deserializes as the enum's default member instead of
/// failing the whole response read — an older client degrades gracefully rather than crashing on every
/// list refresh. Writes enum member names, the same wire shape as <see cref="JsonStringEnumConverter"/>.
/// </summary>
public sealed class TolerantEnumConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum;

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(typeof(TolerantEnumConverter<>).MakeGenericType(typeToConvert))!;

    private sealed class TolerantEnumConverter<T> : JsonConverter<T> where T : struct, Enum
    {
        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
        {
            JsonTokenType.String when Enum.TryParse<T>(reader.GetString(), ignoreCase: true, out var parsed) => parsed,
            JsonTokenType.Number when reader.TryGetInt64(out var number) => (T)Enum.ToObject(typeof(T), number),
            _ => default, // unknown member from a newer server — degrade to the default member, never throw
        };

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString());
    }
}
