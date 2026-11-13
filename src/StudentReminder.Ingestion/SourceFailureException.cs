namespace StudentReminder.Ingestion;

public sealed class SourceFailureException(string code) : Exception("数据源请求失败。")
{
    public string Code { get; } = code;
}
