namespace StudentReminder.Core;

public static class SourceStatus
{
    public static IReadOnlySet<string> Codes { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "disabled", "pending", "ok", "offline", "unauthorized", "license_invalid", "database_not_ready",
        "missing_token", "protocol_error", "timeout", "backoff", "stopped", "recall_unresolved", "account_changed", "error"
    };

    public static string Describe(string code) => code switch
    {
        "disabled" => "已关闭", "pending" => "等待同步", "ok" => "最近同步成功", "offline" => "服务离线",
        "unauthorized" => "Token 无效或已过期", "license_invalid" => "服务授权无效", "database_not_ready" => "数据库未就绪",
        "missing_token" => "尚未配置 Token", "protocol_error" => "接口与预期协议不符", "timeout" => "连接超时",
        "backoff" => "连接中断，等待重试", "stopped" => "同步已暂停", "recall_unresolved" => "收到未能关联的撤回，需复核",
        "account_changed" => "数据源账号已变化，请暂停旧来源，再为新账号新建别名",
        _ => "同步失败"
    };
}
