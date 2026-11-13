using StudentReminder.Core;
using System.Text;
using System.Text.Json;

namespace StudentReminder.Ingestion;

public sealed class ManualImportService(IMessageRepository repository)
{
    public const int MaxJsonBytes = 16 * 1024 * 1024;

    public async Task<ImportResult> ImportJsonAsync(string json, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (Encoding.UTF8.GetByteCount(json) > MaxJsonBytes)
            throw new ArgumentException("JSON 文件超过 16 MiB 上限。");
        var messages = JsonSerializer.Deserialize<List<NormalizedMessage>>(json, ContractJson.Options)
            ?? throw new ArgumentException("请输入消息 JSON 数组。");
        if (messages.Count > MessageValidation.MaxMessages)
            throw new ArgumentException("每批最多导入 10000 条消息。");
        var normalized = messages.Select(MessageValidation.Normalize).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        await repository.InitializeAsync(cancellationToken);
        return await repository.SaveBatchAsync(normalized, cancellationToken);
    }

    public async Task<ImportResult> ImportFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 8192, useAsync: true);
        if (stream.Length > MaxJsonBytes) throw new ArgumentException("JSON 文件超过 16 MiB 上限。");
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > MaxJsonBytes) throw new ArgumentException("JSON 文件超过 16 MiB 上限。");
            buffer.Write(chunk, 0, read);
        }
        var bytes = buffer.ToArray();
        var offset = bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) ? 3 : 0;
        var json = new UTF8Encoding(false, true).GetString(bytes, offset, bytes.Length - offset);
        return await ImportJsonAsync(json, cancellationToken);
    }

    public Task<ImportResult> ImportTextAsync(string text, string conversationName, DateTimeOffset sentAt,
        CancellationToken cancellationToken = default)
    {
        var message = new NormalizedMessage
        {
            Source = "manual", Adapter = "paste", AccountId = "manual-local",
            ConversationId = conversationName, ConversationName = conversationName,
            ConversationType = "private", SenderId = "manual-sender", SenderName = "手动导入",
            SentAt = sentAt, ReceivedAt = DateTimeOffset.UtcNow, Text = text
        };
        return ImportJsonAsync(JsonSerializer.Serialize(new[] { message }, ContractJson.Options), cancellationToken);
    }
}
