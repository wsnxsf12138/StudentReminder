using System.Security.Cryptography;
using System.Text.Json;
using StudentReminder.Core;

namespace StudentReminder.Ingestion;

public sealed record MoectScanResult(int Files, int Added, int Duplicates, int Conflicts, int Skipped);

public static class MoectInbox
{
    public static async Task<MoectGroup> ReadGroupAsync(string file, CancellationToken cancellationToken = default)
    {
        using var document = JsonDocument.Parse(await ReadFileAsync(file, cancellationToken));
        return ReadGroup(document.RootElement);
    }

    public static async Task<MoectScanResult> ScanAsync(MoectWatchProfile profile,
        Func<string, IReadOnlyList<NormalizedMessage>, CancellationToken, Task<ImportResult?>> import,
        CancellationToken cancellationToken = default)
    {
        profile.Validate();
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
        var files = 0;
        var added = 0;
        var duplicates = 0;
        var conflicts = 0;
        var skipped = 0;
        foreach (var path in Directory.EnumerateFiles(profile.Folder, "*.json", options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] bytes;
            IReadOnlyList<NormalizedMessage> messages;
            try
            {
                bytes = await ReadFileAsync(path, cancellationToken);
                using var document = JsonDocument.Parse(bytes);
                var group = ReadGroup(document.RootElement);
                if (group.Id != profile.Group!.Id) { skipped++; continue; }
                messages = ReadMessages(document.RootElement.GetProperty("messages"), profile, group);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
            {
                skipped++;
                continue;
            }

            var result = await import(Convert.ToHexString(SHA256.HashData(bytes)), messages, cancellationToken);
            files++;
            if (result is null) continue;
            added += result.Added;
            duplicates += result.Duplicates;
            conflicts += result.Conflicts;
        }
        return new(files, added, duplicates, conflicts, skipped);
    }

    private static async Task<byte[]> ReadFileAsync(string path, CancellationToken cancellationToken)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("请选择本地 JSON 文件。");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, useAsync: true);
        if (stream.Length > ManualImportService.MaxJsonBytes) throw new ArgumentException("Moect 文件超过 16 MiB。");
        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        return bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) ? bytes[3..] : bytes;
    }

    private static MoectGroup ReadGroup(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("metadata", out var metadata) ||
            String(metadata, "application") != "Moect" || !root.TryGetProperty("messages", out var rows) ||
            rows.ValueKind != JsonValueKind.Array || !root.TryGetProperty("count", out var count) ||
            count.ValueKind != JsonValueKind.Number || !count.TryGetInt32(out var expected) || rows.GetArrayLength() != expected)
            throw new ArgumentException("请选择完整的 Moect 单群 JSON 导出。");
        var id = String(metadata, "session");
        var name = String(metadata, "name");
        if (id.Length > 512 || !id.EndsWith("@chatroom", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("请选择 Moect 的单群导出文件。");
        return new(id, name);
    }

    private static List<NormalizedMessage> ReadMessages(JsonElement rows, MoectWatchProfile profile, MoectGroup group)
    {
        var now = DateTimeOffset.UtcNow;
        var messages = new List<NormalizedMessage>();
        foreach (var row in rows.EnumerateArray())
        {
            if (String(row, "session") != group.Id) throw new ArgumentException("批次混入其他会话。");
            if (String(row, "kind") != "text") continue;
            if (!row.TryGetProperty("create_time", out var timestamp) || timestamp.ValueKind != JsonValueKind.Number ||
                !timestamp.TryGetInt64(out var seconds)) throw new ArgumentException("消息时间缺失。");
            var messageId = row.TryGetProperty("server_id", out var number) && number.ValueKind == JsonValueKind.Number
                ? number.GetRawText() : String(row, "server_id");
            messages.Add(MessageValidation.Normalize(new NormalizedMessage
            {
                Source = "wechat", Adapter = "moect-json", AccountId = profile.AccountAlias, ConversationId = group.Id,
                ConversationName = group.Name, SenderId = String(row, "sender_wxid"), SenderName = String(row, "sender"),
                MessageId = string.IsNullOrWhiteSpace(messageId) || messageId == "0" ? null : messageId,
                Text = String(row, "text"), SentAt = DateTimeOffset.FromUnixTimeSeconds(seconds), ReceivedAt = now
            }));
            if (messages.Count > MessageValidation.MaxMessages) throw new ArgumentException("Moect 批次超过 10000 条文字。");
        }
        return messages;
    }

    private static string String(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String
            ? field.GetString() ?? "" : "";
}
