namespace StudentReminder.Core;

public sealed record ReviewCandidate(string Id, EventCandidate Draft, string Status, string ChangeKind,
    string? EventId, DateTimeOffset CreatedAt);
public sealed record ReviewedEvent(string Id, EventCandidate Details, string Status, int Version, DateTimeOffset UpdatedAt)
{
    public override string ToString() => $"{Details.Title} · v{Version} · {ReviewLabels.Status(Status)}";
}

public sealed record EventRevision(int Version, EventCandidate Details, string Status, DateTimeOffset ChangedAt);

public static class ReviewLabels
{
    public static string Status(string value) => value switch
    {
        "pending" => "待确认", "confirmed" => "已确认", "ignored" => "已忽略", "active" => "进行中",
        "completed" => "已完成", "canceled" => "已取消", "deleted" => "已删除", _ => value
    };

    public static string Category(string value) => value switch
    {
        "assignment" => "作业", "exam" => "考试", "meeting" => "会议", "notice" => "通知",
        "registration" => "报名／预约", "payment" => "缴费", "activity" => "活动", "other" => "其他", _ => value
    };

    public static string Change(string value) => value switch
    {
        "new" => "新事项", "update" => "变更", "cancel" => "取消", _ => value
    };

    public static bool IsCategory(string value) => value is
        "assignment" or "exam" or "meeting" or "notice" or "registration" or "payment" or "activity" or "other";
}
