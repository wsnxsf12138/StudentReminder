namespace StudentReminder.Core;

public sealed record AiProfile(string Provider, string Model)
{
    public void Validate()
    {
        if (Provider is not ("deepseek" or "doubao") || string.IsNullOrWhiteSpace(Model) ||
            Model.Length > 128 || Model.Any(char.IsWhiteSpace))
            throw new ArgumentException("请选择 DeepSeek 或豆包，并填写有效的模型 ID。");
    }
}

public sealed record AiUsage(int PromptTokens, int CompletionTokens);
public sealed record AiInput(string Text, DateTimeOffset SentAt, TimeZoneInfo TimeZone)
{
    public IReadOnlyList<AiEvidenceText> Context { get; init; } = [];
}

public sealed record AiEvidenceText(string Id, string Speaker, string Text, DateTimeOffset SentAt, MessageEvidence Evidence);

public sealed record HybridAnalysisSettings(bool Enabled = false, string? ModelPath = null, CloudBatchSettings? Batch = null);

public sealed record CloudBatchSettings(int Count = 4, int WaitSeconds = 120)
{
    public void Validate()
    {
        if (Count is < 1 or > AiLimits.MaxBatchJobs || WaitSeconds is < 1 or > 900)
            throw new ArgumentException("批次条数请输入 1～4，最长等待请输入 1～900 秒。");
    }
}

public sealed record LocalTriageDecision(bool Relevant, IReadOnlyList<int> Related);

public interface ILocalTriage
{
    Task<LocalTriageDecision> EvaluateAsync(string modelPath, AiWorkItem anchor, IReadOnlyList<AiWorkItem> nearby,
        CancellationToken ct);
}
public sealed record AiExtraction(IReadOnlyList<ExtractedEvent> Events, AiUsage? Usage);
public sealed record ExtractedEvent(EventCandidate Candidate, string ChangeKind);

public interface IAiProvider
{
    void ValidateConfig(AiProfile profile, string key);
    Task TestConnectionAsync(AiProfile profile, string key, CancellationToken ct = default);
    Task<AiExtraction> ExtractCandidatesAsync(AiProfile profile, string key, AiInput input,
        MessageEvidence evidence, CancellationToken ct = default);
    Task<AiDigestPolish> GenerateDigestAsync(AiProfile profile, string key, IReadOnlyList<AiDigestItem> items,
        CancellationToken ct = default);
}

public sealed record AiBatchJob(AiWorkItem Anchor, AiInput Input, IReadOnlyList<AiWorkItem> Context);
public sealed record AiBatchAttempt(AiBatchJob Job, int Attempt);
public sealed record AiBatchReservation(long? RequestId, IReadOnlyList<AiBatchAttempt> Attempts,
    DateTimeOffset? AvailableAt = null, bool DailyLimit = false);
public sealed record AiBatchExtraction(IReadOnlyList<IReadOnlyList<ExtractedEvent>> Results, AiUsage? Usage);
public sealed record AiBatchFailure(AiBatchAttempt Attempt, DateTimeOffset? RetryAt);

public interface IAiBatchProvider
{
    Task<AiBatchExtraction> ExtractBatchAsync(AiProfile profile, string key, IReadOnlyList<AiBatchJob> jobs, CancellationToken ct);
}

public sealed record AiDigestItem(int Id, string Title, string Category, DateTimeOffset? StartsAt, DateTimeOffset? DeadlineAt);
public sealed record AiDigestChoice(int Id, string Style);
public sealed record AiDigestPolish(IReadOnlyList<AiDigestChoice> Choices, AiUsage? Usage);

public sealed record AiScope(string Id, string Source, string Adapter, string Account, string Name, string Type)
{
    public override string ToString() => $"{Name} · {Account} · {Adapter}" + (Type == "private" ? " · 私聊" : "");
}

public sealed record AiWorkItem(AiScope Scope, StoredMessage Message, string Status,
    DateTimeOffset? RetryAt = null, string? ErrorCode = null)
{
    public MessageEvidence Evidence => new(Message.Source, Message.Message.ConversationId,
        Message.Message.MessageId ?? Message.Id) { AccountId = Message.Account };
}

public sealed record AiReservation(long? RequestId, int Attempt = 0, DateTimeOffset? AvailableAt = null, bool DailyLimit = false);
public sealed record AiStatistics(int Requests, long PromptTokens, long CompletionTokens,
    int MissingUsage, int Filtered, int Retrying, int Failed);
public sealed record AiHistoryCursor(DateTimeOffset SentAt, string MessageId);
public sealed record AiHistoryPage(IReadOnlyList<AiWorkItem> Items, AiHistoryCursor? Next);

public interface IAiAnalysisStore
{
    Task<IReadOnlyList<AiWorkItem>> ReadAutomaticAsync(IReadOnlyList<string> scopeIds, DateTimeOffset since,
        DateTimeOffset now, CancellationToken ct, IReadOnlyList<string>? queued = null);
    Task<AiWorkItem?> ReadAuthorizedAsync(AiScope scope, string messageId, DateTimeOffset? since, CancellationToken ct);
    Task<IReadOnlyList<AiWorkItem>> ReadNearbyAsync(AiWorkItem anchor, DateTimeOffset since, CancellationToken ct);
    Task MarkFilteredAsync(string messageId, CancellationToken ct, string? reason = null);
    Task<AiReservation> ReserveAsync(AiWorkItem item, AiProfile profile, DateTimeOffset? since,
        DateTimeOffset now, TimeZoneInfo zone, CancellationToken ct, IReadOnlyList<AiWorkItem>? context = null);
    Task RecordFailureAsync(string messageId, long requestId, AiFailureException failure, DateTimeOffset? retryAt,
        DateTimeOffset? providerAvailableAt, CancellationToken ct);
    Task CompleteAsync(AiWorkItem item, long requestId, AiExtraction extraction, DateTimeOffset? since,
        CancellationToken ct, IReadOnlyList<AiWorkItem>? context = null);
    Task<AiBatchReservation> ReserveBatchAsync(IReadOnlyList<AiBatchJob> jobs, AiProfile profile, DateTimeOffset since,
        DateTimeOffset now, TimeZoneInfo zone, CancellationToken ct);
    Task CompleteBatchAsync(AiBatchReservation reservation, AiBatchExtraction extraction, DateTimeOffset since, CancellationToken ct);
    Task RecordBatchFailureAsync(long requestId, IReadOnlyList<AiBatchFailure> failures, AiFailureException error,
        DateTimeOffset? providerAvailableAt, CancellationToken ct);
}

public sealed class AiFailureException(string code, bool retryable, TimeSpan? retryAfter = null, AiUsage? usage = null)
    : Exception(code)
{
    public string Code { get; } = code;
    public bool Retryable { get; } = retryable;
    public TimeSpan? RetryAfter { get; } = retryAfter;
    public AiUsage? Usage { get; } = usage;

    public static string Describe(string code) => code switch
    {
        "auth" => "API Key 无效或无权调用。",
        "model" => "服务拒绝该模型或请求配置，请检查模型 ID。",
        "rate" => "服务限流。",
        "network" => "服务暂时无法连接。",
        "timeout" => "请求超时。",
        "format" => "模型输出为空、被截断或格式不符。",
        "server" => "AI 服务暂不可用。",
        "interrupted" => "上次请求中断，用量可能未知。",
        "local" => "本地模型未能运行。请在模型设置中检查 GGUF 文件并测试本地模型。",
        "local_format" => "本地模型未返回有效的筛选结果。请检查模型配置。",
        _ => "分析未完成。"
    };
}

public static class AiLimits
{
    public const int HybridNearbyCount = 5;
    public const int MaxBatchJobs = 4;
    public const int MaxTextLength = 2000;
    public const int MaxOutputTokens = 800;
    public const int DailyRequests = 100;
    public const int MaxAttempts = 3;
    public static readonly TimeSpan RequestInterval = TimeSpan.FromSeconds(10);
}
