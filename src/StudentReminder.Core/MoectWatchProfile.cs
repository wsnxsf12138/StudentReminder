using System.Text.Json.Serialization;

namespace StudentReminder.Core;

public sealed record MoectGroup(string Id, string Name);

public sealed record MoectWatchProfile
{
    public string Folder { get; init; } = "";
    public MoectGroup? Group { get; init; }
    public string AccountAlias { get; init; } = "wechat-local";
    public int IntervalMinutes { get; init; } = 30;
    public string Provider { get; init; } = "";
    public string Model { get; init; } = "";

    public void Validate()
    {
        if (Group is null || !Group.Id.EndsWith("@chatroom", StringComparison.Ordinal) ||
            Group.Id.Length > 512 || string.IsNullOrWhiteSpace(Group.Name))
            throw new ArgumentException("请先选择一份授权群的 Moect JSON 样本。");
        if (!Path.IsPathFullyQualified(Folder) || !Directory.Exists(Folder))
            throw new ArgumentException("请选择现有的 Moect 导出目录。");
        if (IntervalMinutes is < 1 or > 1440) throw new ArgumentException("刷新间隔请输入 1～1440 分钟。");
        if (AccountAlias.Length is < 1 or > 128 || AccountAlias.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-')))
            throw new ArgumentException("账号别名可使用字母、数字、点、横线和下划线。");
        if (Provider is not ("" or "deepseek" or "doubao") ||
            (Provider.Length != 0 && (string.IsNullOrWhiteSpace(Model) || Model.Length > 128 || Model.Any(char.IsWhiteSpace))))
            throw new ArgumentException("请选择分析服务并填写有效的模型 ID。");
    }
}

public sealed record MoectAnalysisInput(string MessageId, string Text, DateTimeOffset SentAt);
public sealed record MoectAnalysisResult(string Category, string Title, string TimeText)
{
    [JsonIgnore]
    public string DisplayCategory => Category switch
    {
        "assignment" => "作业", "exam" => "考试", "meeting" => "会议", "notice" => "通知", _ => "无待办"
    };
}

public sealed record MoectAnalysisEntry(DateTimeOffset SentAt, MoectAnalysisResult Result);
