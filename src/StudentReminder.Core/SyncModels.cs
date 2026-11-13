using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StudentReminder.Core;

public sealed record SourceDefinition
{
    public required string Adapter { get; init; }
    public required string AccountAlias { get; init; }
    public int Port { get; init; }
    public int PollSeconds { get; init; } = 60;
    public int LookbackDays { get; init; } = 7;
    public bool Enabled { get; init; }
    public bool UsePush { get; init; }
    public long Revision { get; init; }
    public string Health { get; init; } = "disabled";
    public DateTimeOffset? LastOkAt { get; init; }
    public string? ProviderAccount { get; init; }
    [JsonIgnore] public string Platform => Adapter == "qqlocal" ? "qq" : "wechat";
    [JsonIgnore] public string Id => SourceKeys.Hash(Platform, AccountAlias);

    public void Validate()
    {
        if (Adapter is not ("wxlocal" or "qqlocal")) throw new ArgumentException("不支持的数据源。");
        if (string.IsNullOrWhiteSpace(AccountAlias) || AccountAlias.Length > 128) throw new ArgumentException("请填写本地账号别名。");
        if (Port != 0) throw new ArgumentException("本地直读来源不使用端口。");
        if (PollSeconds is < 60 or > 300) throw new ArgumentException("轮询间隔范围为 60～300 秒。");
        if (LookbackDays is < 1 or > 30) throw new ArgumentException("首次回溯范围为 1～30 天。");
    }
}

public static class SourceKeys
{
    public static string Hash(params string[] values) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(values))));
}

public sealed record SelectedConversation(string Id, string Name, string Type, bool Selected);
public sealed record SourceNotification(string Kind, string? ConversationId, string? MessageId,
    string? EventId = null, bool ReliableMessageId = false);

public interface ISyncStore
{
    Task SaveSourceAsync(SourceDefinition source, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SourceDefinition>> ReadSourcesAsync(CancellationToken cancellationToken = default);
    Task SetSourceEnabledAsync(string sourceId, bool enabled, CancellationToken cancellationToken = default);
    Task MergeConversationsAsync(string sourceId, IReadOnlyList<SourceConversation> conversations,
        CancellationToken cancellationToken = default, string? providerAccount = null);
    Task<IReadOnlyList<SelectedConversation>> ReadConversationsAsync(string sourceId, CancellationToken cancellationToken = default);
    Task SelectConversationsAsync(string sourceId, IReadOnlyList<string> conversationIds, CancellationToken cancellationToken = default);
    Task<string?> ReadCursorAsync(string sourceId, string conversationId, CancellationToken cancellationToken = default);
    Task<ImportResult> CommitPageAsync(SourceDefinition source, string conversationId, MessagePage page, CancellationToken cancellationToken = default);
    Task RecallAsync(SourceDefinition source, string conversationId, string messageId, CancellationToken cancellationToken = default);
    Task SetHealthAsync(string sourceId, string code, bool successful, CancellationToken cancellationToken = default);
}

public interface ISourceTokenStore
{
    Task<string?> ReadAsync(string sourceId, CancellationToken cancellationToken = default);
    Task WriteAsync(string sourceId, string token, CancellationToken cancellationToken = default);
}
