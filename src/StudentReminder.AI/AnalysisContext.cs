using System.Text.Json;
using System.Text.Encodings.Web;
using StudentReminder.Core;

namespace StudentReminder.AI;

public static class AnalysisContext
{
    public const int MaxMessages = 4;
    internal static readonly JsonSerializerOptions ReadableJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static AiInput Create(AiWorkItem anchor, IReadOnlyList<AiWorkItem> related, TimeZoneInfo zone)
    {
        if (related.Count >= MaxMessages || related.Any(row => row.Scope != anchor.Scope) ||
            related.Select(row => row.Message.Id).Append(anchor.Message.Id).Distinct(StringComparer.Ordinal).Count() != related.Count + 1)
            throw new ArgumentException("联合分析最多支持同一会话的 4 条不同消息。");
        var speakers = new Dictionary<string, string>(StringComparer.Ordinal);
        var items = related.Prepend(anchor).ToArray();
        var privateNames = items.SelectMany(row => new[] { row.Message.Message.SenderName, row.Message.Message.SenderId })
            .Where(value => !string.IsNullOrWhiteSpace(value) && value != "unknown")
            .Distinct(StringComparer.Ordinal).OrderByDescending(value => value!.Length).ToArray();
        var parts = items.Select((row, index) =>
        {
            var message = row.Message.Message;
            var identity = message.SenderId == "unknown" ? $"unknown:{index}" : message.SenderId;
            if (!speakers.TryGetValue(identity, out var speaker))
            {
                speaker = FormattableString.Invariant($"S{speakers.Count + 1}");
                speakers.Add(identity, speaker);
            }
            var text = message.Text;
            foreach (var name in privateNames)
                text = text.Replace(name!, "[发送者]", StringComparison.Ordinal);
            return new AiEvidenceText(FormattableString.Invariant($"E{index + 1}"), speaker,
                Truncate(AnalysisText.Prepare(text), index == 0 ? AiLimits.MaxTextLength : 800), message.SentAt, row.Evidence);
        }).ToArray();
        return new(parts[0].Text, parts[0].SentAt, zone) { Context = parts };
    }

    internal static string Truncate(string text, int limit)
    {
        if (text.Length <= limit) return text;
        return text[..(char.IsHighSurrogate(text[limit - 1]) ? limit - 1 : limit)];
    }
}
