using System.Text.Json;
using StudentReminder.Core;

namespace StudentReminder.AI;

internal static class EventExtractionJson
{
    public const string Schema = """
        {"type":"object","additionalProperties":false,"required":["candidates"],"properties":{"candidates":{
        "type":"array","maxItems":4,"items":{"type":"object","additionalProperties":false,
        "required":["title","category","action","location","start_text","deadline_text","evidence_id","certainty","ambiguities","change_kind"],
        "properties":{"title":{"type":"string","minLength":1,"maxLength":120},"category":{"enum":["assignment","exam","meeting","notice","registration","payment","activity","other"]},
        "action":{"type":"string","maxLength":240},"location":{"type":["string","null"],"maxLength":160},
        "start_text":{"type":["string","null"],"maxLength":100},"deadline_text":{"type":["string","null"],"maxLength":100},
        "evidence_id":{"const":"E1"},"certainty":{"enum":["explicit","uncertain"]},
        "ambiguities":{"type":"array","maxItems":4,"items":{"type":"string","maxLength":120}},
        "change_kind":{"enum":["new","update","cancel"]}}}}}}
        """;

    public const string ContextSchema = """
        {"type":"object","additionalProperties":false,"required":["candidates"],"properties":{"candidates":{
        "type":"array","maxItems":4,"items":{"type":"object","additionalProperties":false,
        "required":["title","category","action","location","start_refs","deadline_refs","evidence_ids","certainty","ambiguities","change_kind"],
        "properties":{"title":{"type":"string"},"category":{"enum":["assignment","exam","meeting","notice","registration","payment","activity","other"]},
        "action":{"type":"string"},"location":{"type":["string","null"]},
        "start_refs":{"type":"array","maxItems":2,"items":{"$ref":"#/$defs/ref"}},
        "deadline_refs":{"type":"array","maxItems":2,"items":{"$ref":"#/$defs/ref"}},
        "evidence_ids":{"type":"array","minItems":1,"maxItems":4,"items":{"type":"string"}},
        "certainty":{"enum":["explicit","uncertain"]},"ambiguities":{"type":"array","maxItems":4,"items":{"type":"string"}},
        "change_kind":{"enum":["new","update","cancel"]}}}}},
        "$defs":{"ref":{"type":"object","additionalProperties":false,"required":["evidence_id","quote"],
        "properties":{"evidence_id":{"type":"string"},"quote":{"type":"string"}}}}}
        """;

    public static IReadOnlyList<ExtractedEvent> Parse(string content, AiInput input, MessageEvidence evidence)
    {
        using var document = JsonDocument.Parse(content, new JsonDocumentOptions { MaxDepth = 12 });
        var root = ReadObject(document.RootElement, "candidates");
        var items = ReadArray(root["candidates"], 4);
        var results = new List<ExtractedEvent>(items.Length);
        var context = input.Context.Count == 0 ? null : input.Context.ToDictionary(row => row.Id, StringComparer.Ordinal);
        foreach (var item in items)
        {
            var fields = context is null
                ? ReadObject(item, "title", "category", "action", "location", "start_text", "deadline_text",
                    "evidence_id", "certainty", "ambiguities", "change_kind")
                : ReadObject(item, "title", "category", "action", "location", "start_refs", "deadline_refs",
                    "evidence_ids", "certainty", "ambiguities", "change_kind");
            var title = ReadText(fields["title"], 120).Trim();
            var category = ReadText(fields["category"], 20);
            var kind = ReadText(fields["change_kind"], 10);
            var certainty = ReadText(fields["certainty"], 10);
            if (title.Length == 0 || !ReviewLabels.IsCategory(category) || kind is not ("new" or "update" or "cancel") ||
                certainty is not ("explicit" or "uncertain")) throw new JsonException();
            var ambiguities = ReadArray(fields["ambiguities"], 4).Select(value => ReadText(value, 120)).ToList();
            ResolvedTime starts;
            ResolvedTime deadline;
            IReadOnlyList<MessageEvidence> links;
            if (context is null)
            {
                if (ReadText(fields["evidence_id"], 8) != "E1") throw new JsonException();
                starts = Resolve(ReadOptional(fields["start_text"], 100), input.Text, input.SentAt, input.TimeZone, ambiguities, "开始");
                deadline = Resolve(ReadOptional(fields["deadline_text"], 100), input.Text, input.SentAt, input.TimeZone, ambiguities, "截止");
                links = [evidence];
            }
            else
            {
                var ids = ReadArray(fields["evidence_ids"], AnalysisContext.MaxMessages).Select(value => ReadText(value, 8)).ToArray();
                if (!ids.Contains("E1", StringComparer.Ordinal) || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length ||
                    ids.Any(id => !context.ContainsKey(id))) throw new JsonException();
                var selected = ids.ToDictionary(id => id, id => context[id], StringComparer.Ordinal);
                starts = ResolveContext(fields["start_refs"], selected, input.TimeZone, ambiguities, "开始");
                deadline = ResolveContext(fields["deadline_refs"], selected, input.TimeZone, ambiguities, "截止");
                links = selected.Values.Select(message => message.Evidence).ToArray();
            }
            var startDay = starts.Date ?? DayOfInstant(starts.Instant, input.TimeZone);
            var deadlineDay = deadline.Date ?? DayOfInstant(deadline.Instant, input.TimeZone);
            if (starts.Instant > deadline.Instant || (starts.Instant is null || deadline.Instant is null) && startDay > deadlineDay)
            {
                ambiguities.Add("开始时间晚于截止时间，请核对。");
                starts = new(null, null, starts.Text);
                deadline = new(null, null, deadline.Text);
            }
            var candidate = new EventCandidate
            {
                Title = title, Category = category, Action = ReadText(fields["action"], 240).Trim(),
                Location = ReadOptional(fields["location"], 160),
                StartsAt = starts.Instant?.ToUniversalTime(), DeadlineAt = deadline.Instant?.ToUniversalTime(),
                StartsOn = starts.Date, DeadlineOn = deadline.Date,
                StartText = starts.Text, DeadlineText = deadline.Text,
                Certainty = ambiguities.Count == 0 ? certainty : "uncertain",
                Priority = category is "exam" or "payment" || deadline.Instant.HasValue || deadline.Date.HasValue ||
                    category == "notice" && AnalysisText.IsImportantNotice(input.Text) ? "high" : "normal",
                Evidence = links, Ambiguities = ambiguities
            };
            results.Add(new(candidate, kind));
        }
        return results;
    }

    private static DateOnly? DayOfInstant(DateTimeOffset? instant, TimeZoneInfo zone) => instant is { } time
        ? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time, zone).DateTime) : null;

    private static string? VerifiedExcerpt(string? excerpt, string text)
    {
        if (string.IsNullOrWhiteSpace(excerpt)) return null;
        var position = 0;
        while (position < text.Length)
        {
            var index = text.IndexOf(excerpt, position, StringComparison.Ordinal);
            if (index < 0) break;
            var prefix = text.AsSpan(0, index).TrimEnd();
            var suffix = text.AsSpan(index + excerpt.Length).TrimStart();
            if (!IsCutoffPrefix(prefix, excerpt) && !IsCutoffSuffix(suffix)) return excerpt;
            position = index + 1;
        }
        return null;
    }

    private static bool IsCutoffPrefix(ReadOnlySpan<char> prefix, string excerpt)
    {
        if (prefix.IsEmpty) return false;
        if (prefix.EndsWith("北京时间", StringComparison.Ordinal)) return true;
        if (excerpt.StartsWith("周", StringComparison.Ordinal) || excerpt.StartsWith("星期", StringComparison.Ordinal))
            return "下本这上每".Contains(prefix[^1]);
        if (excerpt.StartsWith("后天", StringComparison.Ordinal) && prefix[^1] == '大') return true;
        return char.IsDigit(excerpt[0]) && (char.IsDigit(prefix[^1]) || prefix[^1] is '年' or '月' or '-' or '.' or '/');
    }

    private static bool IsCutoffSuffix(ReadOnlySpan<char> suffix) =>
        !suffix.IsEmpty && (char.IsDigit(suffix[0]) || suffix[0] is ':' or '：' or '半' or '分' ||
            (suffix.Length > 1 && suffix[0] is ('+' or '-' or '＋' or '－' or '.' or '/') && char.IsDigit(suffix[1])) ||
            suffix.StartsWith("UTC", StringComparison.OrdinalIgnoreCase) ||
            suffix.StartsWith("Z", StringComparison.OrdinalIgnoreCase) ||
            suffix.StartsWith("北京时间", StringComparison.Ordinal));

    private static ResolvedTime Resolve(string? excerpt, string text, DateTimeOffset sentAt, TimeZoneInfo zone,
        List<string> ambiguities, string label)
    {
        if (excerpt is null) return default;
        if (VerifiedExcerpt(excerpt, text) is null)
        {
            ambiguities.Add($"{label}时间与证据原文不一致。");
            return default;
        }
        var instant = EventTimeParser.Parse(excerpt, sentAt, zone);
        if (instant is not null) return new(instant, null, excerpt);
        var date = EventTimeParser.ParseDate(excerpt, sentAt, zone);
        ambiguities.Add(date.HasValue ? $"{label}时刻待补录（原文：{excerpt}）" : $"{label}时间待补录（原文：{excerpt}）");
        return new(null, date, excerpt);
    }

    private static ResolvedTime ResolveContext(JsonElement raw, IReadOnlyDictionary<string, AiEvidenceText> evidence,
        TimeZoneInfo zone, List<string> ambiguities, string label)
    {
        var references = ReadArray(raw, 2).Select(value =>
        {
            var fields = ReadObject(value, "evidence_id", "quote");
            var id = ReadText(fields["evidence_id"], 8);
            if (!evidence.TryGetValue(id, out var message)) throw new JsonException();
            return (Message: message, Quote: ReadText(fields["quote"], 100));
        }).ToArray();
        if (references.Length == 0) return default;
        var first = references[0];
        if (references.Length == 1) return Resolve(first.Quote, first.Message.Text, first.Message.SentAt, zone, ambiguities, label);
        var second = references[1];
        if (VerifiedExcerpt(first.Quote, first.Message.Text) is null)
        {
            ambiguities.Add($"{label}时间与证据原文不一致。");
            return default;
        }
        var date = EventTimeParser.ParseDate(first.Quote, first.Message.SentAt, zone);
        if (first.Message.Id == second.Message.Id || VerifiedExcerpt(second.Quote, second.Message.Text) is null)
        {
            ambiguities.Add($"{label}时间与证据原文不一致。");
            return new(null, date, first.Quote);
        }
        var instant = date.HasValue
            ? EventTimeParser.Parse(first.Quote.Trim() + second.Quote.Trim(), first.Message.SentAt, zone) : null;
        var excerpt = first.Quote + " / " + second.Quote;
        if (instant is not null && DateOnly.FromDateTime(instant.Value.DateTime) == date) return new(instant, null, excerpt);
        ambiguities.Add($"{label}时间无法从所引消息确定。");
        return new(null, date, excerpt);
    }

    private readonly record struct ResolvedTime(DateTimeOffset? Instant, DateOnly? Date, string? Text);

    private static Dictionary<string, JsonElement> ReadObject(JsonElement element, params string[] required)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new JsonException();
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var field in element.EnumerateObject())
            if (!required.Contains(field.Name, StringComparer.Ordinal) || !result.TryAdd(field.Name, field.Value)) throw new JsonException();
        if (result.Count != required.Length) throw new JsonException();
        return result;
    }

    private static JsonElement[] ReadArray(JsonElement value, int max) =>
        value.ValueKind == JsonValueKind.Array && value.GetArrayLength() <= max
            ? value.EnumerateArray().ToArray() : throw new JsonException();

    private static string ReadText(JsonElement value, int max) =>
        value.ValueKind == JsonValueKind.String && value.GetString() is { } text && text.Length <= max
            ? text : throw new JsonException();

    private static string? ReadOptional(JsonElement value, int max) =>
        value.ValueKind == JsonValueKind.Null ? null : ReadText(value, max);
}
