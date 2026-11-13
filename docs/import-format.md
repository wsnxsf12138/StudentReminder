# 手动导入协议与存储规则

## 粘贴

在应用中填写会话名称、消息发送时间和正文，点击“保存粘贴内容”。整段文字作为一条消息保存，保留换行。发送时间必须使用 `2026-11-10T09:00:00+08:00` 格式；该时间供未来相对日期解析使用，本轮不解析“明天”等事件时间。

相同会话、发送时间和正文再次粘贴会去重；再次粘贴发生在不同发送时间时，会视为不同消息。粘贴使用 `manual` 来源、本地账号别名 `manual-local`，不会访问任何聊天软件。

## JSON 文件

使用 UTF-8 编码（可带 BOM），顶层必须是数组。最大 16 MiB、10000 条消息，单条正文最多 65536 字符。全部消息通过校验后才开始写入；未知字段、缺少必需字段、空文本消息、无时区时间或错误日期使整批失败。JSON 不能使用注释或末尾逗号。

```json
[
  {
    "source": "manual",
    "adapter": "json",
    "account_id": "local-account-01",
    "conversation_id": "course-group-001",
    "conversation_name": "合成课程群",
    "conversation_type": "group",
    "message_id": "sample-001",
    "sender_id": "synthetic-teacher",
    "sender_name": "合成老师",
    "sent_at": "2026-11-10T09:00:00+08:00",
    "received_at": "2026-11-10T09:00:02+08:00",
    "content_type": "text",
    "text": "本周五18点前提交实验报告",
    "is_recalled": false,
    "origin_event": "message.new",
    "metadata": {"synthetic": true}
  }
]
```

必需字段：`source`、`adapter`、`account_id`、`conversation_id`、`sender_id`、`sent_at`、`received_at`、`text`。标识字段为 1～512 个字符；账号必须使用本地别名。`source` 支持 `manual`、`wechat`、`qq`，这只表示导入文件中的来源标签，不会启用实时连接。

`message_id` 可缺省或为 null；会话类型支持 `group` / `private`，默认 `group`；内容类型默认 `text`，非文本可保留空正文及类型占位，本轮不做 OCR 或语音转写。`origin_event` 支持 `message.new` / `message.revoke`，撤回仅标记本应用内证据状态。

## 持久化与去重

除主样本外，`tests/Fixtures/messages.conflicts.synthetic.json` 提供两条同编号异文的合成证据。导入空库时应新增 1、冲突 1，原文不被覆盖，消息状态为“待复核”；该文件也随应用复制到输出目录的 `Samples/`，可通过“导入 JSON”选择。

- 所有消息时间转为 UTC 存储，界面按系统本地时区展示。
- 有上游 ID 时按平台、账号别名、会话和消息 ID 去重；更换适配器不改变相同账号的消息身份。
- 无上游 ID 时使用发送者、UTC 发送时间、内容类型与规范化正文的 SHA-256 复合指纹；只规范化 Unicode NFC 和 CRLF 换行。上游缺少 ID 时，两条完全相同的同时发送消息无法区分，这是该降级方案的限制。
- 同编号的不同正文不覆盖旧证据，标记“待复核”，冲突输入保存在本地 `message_conflicts` 表。冲突解决界面留待后续事件管理阶段。
- 撤回状态只会从未撤回变为已撤回；重放旧消息不会恢复未撤回状态。
- 批次使用 SQLite 事务；校验失败、取消或写入失败不会留下部分消息。首次初始化可能留下空数据库文件。
- schema v1 包含基础表；002 加入来源配置、选择和同步状态；003 加入 Rust CLI 来源与上游账号绑定；004 加入 Moect 导出指纹和 AI 分析队列。升级保留原消息、选择和游标。回退应用时使用对应 schema 的升级前备份或独立数据目录。

## 数据位置与隐私

默认目录是 `%LOCALAPPDATA%\StudentReminder`，包含 `messages.db` 和 `logs/`。数据库保存消息正文与证据，当前未做数据库加密；按个人文件妥善保管。日志只记操作类型、计数、异常类型，不记正文、账号或异常原文。手动导入流程不发网络请求；来源同步另通过 loopback HTTP 读取用户授权的本地服务。

可使用 `--data-dir <目录>` 为本地测试指定独立数据目录。清理测试数据前先关闭应用，再删除自己选定的数据目录；本轮尚无应用内保留期限或数据清除界面。
