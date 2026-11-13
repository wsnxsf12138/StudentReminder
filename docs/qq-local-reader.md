# QQ 本地读取模块（StudentReminder.QqLocal）接入说明书

**版本：** v0.1（首版，文字优先）
**日期：** 2026-11-12
**状态：** 已通过合成测试（140/140）与本机真实 QQ NT 端到端验证

---

## 1. 这是什么

`StudentReminder.QqLocal` 是把 **QQ NT（新版 QQ）本地聊天记录读取链路**用 C# 原生实现的功能模块。它不依赖外部 Python / 第三方服务，直接在本机完成：

```
定位 Tencent Files 账号目录 → 从 QQ.exe 进程内存提取 SQLCipher 密钥
→ 解密数据库（含 WAL 增量）→ 读取会话与消息（Protobuf 文本段）→ 输出结构化数据
```

方案与微信模块（`StudentReminder.WxLocal`）同构，但 QQ NT 的加密格式与微信差异很大，本模块独立实现：

| 差异点 | 微信 4.x | QQ NT |
|---|---|---|
| 数据库文件头 | 无额外头 | 前 **1024 字节**为腾讯私有头（`SQLite header 3\x00` + `QQ_NT DB` 标记） |
| 页保留区 | 80 字节（IV16+HMAC-SHA512 64） | **48 字节**（IV16 + HMAC-SHA1 20 + 12 填充） |
| 密钥在内存的形态 | `Config.Cipher` 对象 + XOR 掩码 | `x'<64hex key><32hex salt>'` 字面量 |
| KDF / MAC | PBKDF2-SHA512(256000) / HMAC-SHA512 | PBKDF2-SHA512(4000) / 页 HMAC-SHA1 |
| 消息体 | ZSTD + XML | Protobuf（文本段字段 45101） |

> 开发记录描述为参考 qq_dump_db 和 QQBackup 的行为后独立实现。原 qq_dump_db 链接目前无法复核，实际参考版本及许可状态见 [P5-04 审查](third-party-audit.md#p5-04-许可定案)。

## 2. 新增内容

| 位置 | 说明 |
|---|---|
| `src/StudentReminder.QqLocal/` | 模块本体（`net10.0-windows` 类库，无 UI 依赖） |
| `tools/StudentReminder.QqLocalProbe/` | 命令行探针（info / refresh / decrypt-only / sessions / export / tail） |
| `tests/StudentReminder.Tests/IntegrationTests/QqLocalTests.cs` | 合成加密库夹具测试（字节级断言 + 明文读取 + Protobuf 解析） |
| `docs/qq-local-reader.md` | 本文档 |

依赖新增：`Microsoft.Data.Sqlite 10.0.14`、`System.Text.Encoding.CodePages 11.0.0`。

## 3. 模块分层

| 文件 | 职责 |
|---|---|
| `QqConstants.cs` | QQ 专用常量：`HeaderSz=1024`、`PageSz=4096`、`ReserveSz=48`、`KdfIter=4000`、`MacKdfIter=2` 等 |
| `QqPaths.cs` | 定位 `Tencent Files\<数字账号>\nt_qq\nt_db`，`ListAccounts()` / `FindAccount(qq?)`（OneDrive 重定向兼容） |
| `QqProcessMemory.cs` | Win32 P/Invoke：OpenProcess / ReadProcessMemory / VirtualQueryEx、分块读内存、SeDebugPrivilege |
| `QqKeyExtractor.cs` | 密钥提取：先按已知 salt 十六进制串搜内存，向前取 64 hex 作为 key，用库第 1 页 HMAC-SHA1 校验；含固定预登录密钥回退 |
| `QqCrypto.cs` | SQLCipher-4 原语：PBKDF2-HMAC-SHA512(4000)、页 1 HMAC-SHA1 校验、AES-256-CBC 逐页解密 |
| `QqDbDecryptor.cs` | 快照 → 剥离 1024 头 → 逐页解密 → 合并加密 WAL 已提交帧 → `quick_check` 抽检；`ApplyWalDelta` 增量 |
| `QqWalCursor.cs` | WAL 增量游标状态（salt 世代、`EndOffset`、`NeedsFullRefresh`） |
| `ProtoReader.cs` | 最小 Protobuf wire 解析，递归提取文本段字段 45101 |
| `QqMessageReader.cs` | 读解密库：`nt_msg.db` 的 `c2c_msg_table` / `group_msg_table`，`profile_info.db`/`group_info.db` 取昵称 |
| `QqLocalReader.cs` | 门面：`Describe()` / `Refresh()` / `DecryptOnly()` / `Open()` |

## 4. 快速使用（探针工具）

```powershell
# 1. 环境检查（只读，不动任何数据）
dotnet run --project tools/StudentReminder.QqLocalProbe -- info

# 2. 提密钥 + 解密全部库（必须指定「当前登录中」的账号）
dotnet run --project tools/StudentReminder.QqLocalProbe -- refresh --qq 1000000001

# 3. 列出会话
dotnet run --project tools/StudentReminder.QqLocalProbe -- sessions --qq 1000000001

# 4. 导出某群近 7 天消息为 JSON
dotnet run --project tools/StudentReminder.QqLocalProbe -- export 1000000002 --group --days 7 --qq 1000000001

# 5. WAL 增量同步后读取新消息（实时增量验证）
dotnet run --project tools/StudentReminder.QqLocalProbe -- tail 1000000002 --group --qq 1000000001
```

> ⚠️ **只能提取「当前登录中」账号的密钥。** QQ NT 只会把已登录账号的密钥载入进程内存；未登录账号的数据库虽然在磁盘上，但进程内存里没有它的密钥，`refresh` 会报 0 命中。多账号环境下，请先 `info` 确认目录，再对**已登录的那个 QQ 号**执行 `refresh`。

默认缓存目录 `%LOCALAPPDATA%\StudentReminder\qqlocal`（解密库 + keys.json + exports/ + tail-wal.json）。全部操作对 QQ 原始数据库**只读**（先复制快照再解密）。

## 5. 代码接入

### 5.1 一次性刷新 + 读取

```csharp
using StudentReminder.QqLocal;

// 环境探测（不碰数据）
var env = QqLocalReader.Describe();
foreach (var account in env.Accounts)
    Console.WriteLine($"{account.Qq} -> {account.NtDbDir}");

// 提钥 + 解密到缓存目录（该账号必须已登录）
var account = QqPaths.FindAccount("1000000001")
    ?? throw new InvalidOperationException("未找到 QQ 账号目录");
var stats = QqLocalReader.Refresh(account, cacheDir, progress: Console.Out);

// 打开读取
using var reader = QqLocalReader.Open(cacheDir);
foreach (var conversation in reader.ListConversations())
    Console.WriteLine($"[{conversation.Type}] {conversation.Name} {conversation.Id} 共 {conversation.Count} 条");

var group = reader.IsGroup("1000000002") == true;
var messages = reader.ReadMessages("1000000002", group,
    startUnix: DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 7 * 86400);
foreach (var m in messages.Where(m => m.Kind == "text"))
    Console.WriteLine($"{m.Sender}: {m.Text}");
```

### 5.2 数据模型

```csharp
record QqConversation(string Id, string Name, string Type /* group|private */,
    long LastTime /*unix秒*/, long Count);

record QqMessage(string Conversation, long RowId, long MsgId, long CreateTime /*unix秒*/,
    string SenderUid, string SenderQq, string Sender, bool SentByMe,
    string Kind /* text|other */, string Text);
```

### 5.3 WAL 增量实时同步（已实现）

首版提供 `tail` 探针 + `QqDbDecryptor.ApplyWalDelta` 增量原语，对齐微信模块的 `WxWalCursor` 模式：

- **全量**：`Refresh` 提钥 + 整库解密 + 合并 WAL，生成明文影子库 `nt_msg.db`。
- **增量**：之后每次同步只调用 `ApplyWalDelta(shadowDb, srcWal, encKey, state)`，用已持有的 `enc_key` 增量解密 `-wal` 里新提交的加密帧并覆写影子库对应页。
- **游标**：`QqWalState(salt1, salt2, EndOffset)` 持久化在 `tail-wal.json`；当 QQ checkpoint 重置 WAL（salt 世代变化）时返回 `NeedsFullRefresh`，调用方整库重刷兜底。
- **消息游标**：`ReadMessagesAfter(conversationId, group, lastRowid)` 用单调递增的 `rowid` 读取新增消息。

`QqMessageReader` / `QqLocalReader` 已按 `IDataSourceAdapter` 契约封装，对应的 `QqLocalSourceAdapter`（`StudentReminder.QqLocal.Adapter` 项目）已接入 `SyncCoordinator`，与 `WxLocalSourceAdapter` 同构；接入方式见 `docs/handoffs/v0.4.2-alpha.md`。

## 6. 已验证情况

命令和代码示例中的账号及会话编号为合成占位值。

- **合成测试**：140/140 通过（含 QQ 夹具：1024 头 + salt + AES-256-CBC + HMAC-SHA1 + 48 保留区的字节级往返、WAL 合并、明文读取、ProtoReader 文本提取）。
- **真实环境（2026-11-12 本机，QQ NT 已登录测试账号）**：
  - 19 个加密库中解析 **17 个 salt** 的密钥（`nt_msg.db`、`profile_info.db`、`group_info.db` 等全部命中），解密 **0 失败**；剩余 2 个为 `guild1.db`（QQ 频道）与 `bc_09.db`，属非文字优先范围。
  - `nt_msg.db`（535 MB）整库解密 + WAL 合并通过 `quick_check`。
  - 会话列表 176 个（群聊/私聊、群号、消息数、最后活跃时间全部正确）。
  - 某群近 3 天 1159 条消息中 886 条文字全部正确解出（中文、发送者 uid/qq、时间戳），273 条图片/语音/表情归为 `[非文本消息]`。
  - `tail` 在 `refresh` 后成功增量应用 528 帧 WAL，并推进 rowid 游标到最新消息。

## 7. 运维与故障排查

| 症状 | 原因与处理 |
|---|---|
| `没有提取到任何密钥`（0 命中） | **最常见：目标账号未登录。** QQ NT 只在内存中保留已登录账号的密钥；确认该 QQ 号正在登录，或换已登录账号重试。若确实已登录，再尝试以**管理员身份**运行（读进程内存需 SeDebugPrivilege） |
| `未检测到 QQ 进程` | 先启动并登录 QQ NT |
| `OpenProcess 失败` | 以管理员身份运行探针 |
| 密钥部分命中（如 17/19） | 未命中的多为 `guild*.db`（QQ 频道）或特殊库；文字优先版无需处理 |
| 解密失败 / WAL 快照不完整 | QQ 正在写页，模块自动重试 3 次；持续失败重新 `refresh` |
| 某会话无消息 | 消息可能在 `-wal` 未合并；先 `refresh` 或 `tail` 触发 WAL 增量 |
| 缓存目录膨胀 | `%LOCALAPPDATA%\StudentReminder\qqlocal` 可整目录删除，下次 `refresh` 重建 |

## 8. 安全与合规提醒（必读）

1. **只读边界**：模块对 QQ 进程内存和原始数据库只做读取，解密副本写入独立缓存目录；不修改、不删除、不拦截任何 QQ 数据。
2. **缓存目录含明文聊天记录**：`qqlocal\` 下是**未加密**的数据库副本，等同于本地聊天记录明文。请勿放入同步盘/共享目录；集成时应提供一键清除与保留期限设置。
3. **keys.json 含数据库密钥**：与聊天明文同级敏感，同上处理。
4. **许可**：发布前核对实际参考的仓库、提交和使用内容。 [P5-04 许可审查](third-party-audit.md#p5-04-许可定案)
5. **账号风险**：此类工具与 QQ 客户端保护机制存在冲突，理论上存在账号被风控的可能，风险由使用者自担；建议仅在本机、本人账号、备份用途下使用。
6. **版本偏移风险**：密钥的内存形态（`x'<key><salt>'` 字面量）与 1024 头/48 保留区随 QQ NT 版本可能变化；升级后若提钥/解密失败，需同步更新 `QqConstants` 与 `QqKeyExtractor` 的搜索逻辑。
