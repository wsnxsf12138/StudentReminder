using System.Globalization;
using System.Text;
using System.Text.Json;
using StudentReminder.Core;

namespace StudentReminder.NapCatReceiver;

internal sealed class NapCatEvent(string account, string group, string friend)
{
    private readonly string accountAlias = "napcat-" + SourceKeys.Hash("qq-account", account);

    public static bool IsQqId(string? value) => value is { Length: >= 5 and <= 20 } &&
        value[0] != '0' && value.All(char.IsAsciiDigit);

    public NormalizedMessage? Read(ReadOnlyMemory<byte> json, DateTimeOffset receivedAt)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var row = document.RootElement;
            var sender = Field(row, "user_id");
            var kind = Field(row, "message_type");
            var conversation = kind switch
            {
                "group" when group != "-" && Field(row, "group_id") == group => group,
                "private" when friend != "-" && sender == friend && Field(row, "sub_type") == "friend" => friend,
                _ => null
            };
            if (Field(row, "post_type") != "message" || conversation is null ||
                Field(row, "self_id") != account || !IsQqId(sender) || sender == account ||
                !long.TryParse(Field(row, "message_id"), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var id) || id == 0 ||
                !long.TryParse(Field(row, "time"), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) || seconds <= 0 ||
                !row.TryGetProperty("message", out var segments) || segments.ValueKind != JsonValueKind.Array)
                return null;

            var text = new StringBuilder();
            foreach (var segment in segments.EnumerateArray())
            {
                if (Field(segment, "type") == "text" && segment.TryGetProperty("data", out var data) &&
                    data.ValueKind == JsonValueKind.Object && data.TryGetProperty("text", out var value) && value.ValueKind == JsonValueKind.String)
                    text.Append(value.GetString());
            }
            if (text.Length > MessageValidation.MaxTextLength) return null;
            var content = text.ToString();
            if (string.IsNullOrWhiteSpace(content)) return null;

            var senderName = row.TryGetProperty("sender", out var profile) ? Field(profile, "nickname") : null;
            return new NormalizedMessage
            {
                Source = "qq", Adapter = "napcat-onebot", AccountId = accountAlias,
                ConversationId = conversation, ConversationType = kind!,
                MessageId = id.ToString(CultureInfo.InvariantCulture), SenderId = sender!, SenderName = senderName ?? "",
                SentAt = DateTimeOffset.FromUnixTimeSeconds(seconds), ReceivedAt = receivedAt, Text = content
            };
        }
        catch (JsonException) { return null; }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private static string? Field(JsonElement row, string key) =>
        row.ValueKind == JsonValueKind.Object && row.TryGetProperty(key, out var value) ? value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        } : null;
}
