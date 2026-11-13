using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;
using System.Text.RegularExpressions;

namespace StudentReminder.Core;

public static class ContractJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectNullableAnnotations = true,
            MaxDepth = 32
        };
        options.Converters.Add(new ExplicitOffsetConverter());
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    private sealed class ExplicitOffsetConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String)
                throw new JsonException("时间必须是带时区的 ISO 8601 字符串。");
            var value = reader.GetString();
            if (value is null || !Regex.IsMatch(value, @"T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2})$", RegexOptions.CultureInvariant)
                || !reader.TryGetDateTimeOffset(out var timestamp))
                throw new JsonException("时间必须带 Z 或明确的时区偏移。");
            return timestamp;
        }

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString("O", CultureInfo.InvariantCulture));
    }
}
