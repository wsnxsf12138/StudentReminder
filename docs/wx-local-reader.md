# 微信本地读取模块（StudentReminder.WxLocal）接入说明书

**版本：** 0.2.4-alpha 配套文档
**日期：** 2026-11-11
**状态：** 已通过合成测试（79/79）与本机真实微信 4.x 端到端验证

---

## 1. 这是什么

`StudentReminder.WxLocal` 是把 **Moect**（github.com/mofan6/Moect，GPL-3.0）的微信 4.x 本地读取链路**用 C# 原生复现**的进程内功能模块。它不依赖外部 Python 环境或第三方服务，直接在本机完成：

```
定位微信数据目录 → 读取 Weixin.exe 进程内存提取数据库密钥
→ 解密 SQLCipher 数据库（含 WAL 增量）→ 读取会话与消息 → 输出结构化数据
```

> ⚠️ **红线变更说明**：本项目计划书 §1.3 / §7.3 / §12.2 原本禁止实现“密钥提取、进程内存读取、解密数据库”。本模块经项目所有者于 2026-11-11 明确决策后越过该红线**直接复现**。这意味着：
> - 模块对微信进程的内存读取为**只读**，但属于计划书原先排除的能力；
> - 技术方案参照 Moect 的公开实现（GPL-3.0）独立重写，未复制其源代码，但**算法逻辑同源**，对外分发前应重新做许可审查；
> - 密钥提取依赖微信特定版本的内存布局（`Config.Cipher` 对象 `+0x88` 偏移、固定 XOR 掩码），**微信每次更新都可能失效**，失效时需要更新偏移量。

## 2. 新增内容

| 位置 | 说明 |
|---|---|
| `src/StudentReminder.WxLocal/` | 模块本体（net10.0-windows 类库，无 UI 依赖） |
| `tools/StudentReminder.WxLocalProbe/` | 命令行探针（info / refresh / decrypt-only / sessions / export） |
| `tests/StudentReminder.Tests/IntegrationTests/WxLocalTests.cs` | 12 个合成测试（加密库往返、WAL 合并、密钥候选、读取管线等） |
| `docs/wx-local-reader.md` | 本文档 |

依赖新增：`Microsoft.Data.Sqlite 10.0.14`、`ZstdSharp.Port 0.8.5`、`System.Text.Encoding.CodePages 11.0.0`（均已写入 packages.lock.json）。

## 3. 模块分层

| 文件 | 职责 |
|---|---|
| `WxPaths.cs` | 定位微信：ini 配置 → 注册表 → 全盘兜底扫描；账号目录 `xwechat_files\<wxid>_<4位hex>\db_storage` |
| `WxProcessMemory.cs` | Win32 P/Invoke：OpenProcess / ReadProcessMemory / VirtualQueryEx、分块读内存、SeDebugPrivilege |
| `WxKeyExtractor.cs` | 密钥提取：在进程内存中定位 `com.Tencent.WCDB.Config.Cipher` 对象 → 追指针 → 读 XOR 掩码 blob → 还原 `x'<key><salt>'` 字面量 → 用库第一页 HMAC-SHA512 校验；含 4.0.x 明文回退和 passphrase 派生回退 |
| `WxCrypto.cs` | SQLCipher-4 原语：PBKDF2-HMAC-SHA512（256000 轮）、页面 HMAC 校验、AES-256-CBC 逐页解密 |
| `WxDbDecryptor.cs` | 快照（微信独占 .db）→ 解密 → 合并加密 WAL 已提交帧 → `PRAGMA quick_check` 抽检 |
| `WxMessageReader.cs` | 读解密后的库：`session.db` 会话列表、`contact.db` 联系人/群成员、`message_N.db` 的 `Msg_<md5(会话)>` 表（跨分片归并）、ZSTD 正文解压、群消息发送者前缀剥离、local_type 复合值归一化 |
| `WxLocalReader.cs` | 门面：`Describe()` / `Refresh()` / `DecryptOnly()` / `Open()` |

## 4. 快速使用（探针工具）

```powershell
# 1. 环境检查（只读，不动任何数据）
dotnet run --project tools/StudentReminder.WxLocalProbe -- info

# 2. 提密钥 + 解密全部库（需微信已登录；建议管理员身份运行）
dotnet run --project tools/StudentReminder.WxLocalProbe -- refresh

# 3. 列出会话
dotnet run --project tools/StudentReminder.WxLocalProbe -- sessions

# 4. 导出某个群的近 7 天消息为 JSON
dotnet run --project tools/StudentReminder.WxLocalProbe -- export 1000000003@chatroom --days 7
```

默认缓存目录 `%LOCALAPPDATA%\StudentReminder\wxlocal`（解密库 + keys.json + exports/）。全部操作对微信原始数据库**只读**（先复制快照再解密）。

## 5. 代码接入

### 5.1 一次性刷新 + 读取

```csharp
using StudentReminder.WxLocal;

// 环境探测（不碰数据）
var env = WxLocalReader.Describe();
foreach (var account in env.Accounts)
    Console.WriteLine($"{account.DirName} -> {account.DbStorage}");

// 提钥 + 解密到缓存目录（微信须在运行）
var account = WxPaths.FindAccount() ?? throw new InvalidOperationException("未找到微信账号目录");
var stats = WxLocalReader.Refresh(account, cacheDir, Console.Out);

// 打开读取
using var reader = WxLocalReader.Open(cacheDir, account.Wxid);
foreach (var session in reader.ListSessions())
    Console.WriteLine($"[{session.Category}] {session.Display} {session.Username}");

var messages = reader.ReadMessages("1000000003@chatroom",
    startUnix: DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 7 * 86400);
foreach (var m in messages.Where(m => m.Kind == "text"))
    Console.WriteLine($"{m.Sender}: {m.Text}");
```

### 5.2 数据模型

```csharp
record WxSessionInfo(string Username, string Display, string Category /* group|friend|official|other */,
    bool IsGroup, long LastTime /*unix秒*/, string Summary, long Unread);

record WxMessage(string Session, long LocalId, string ServerId, int Shard, int Type, string TypeName,
    long CreateTime /*unix秒*/, string SenderWxid, string Sender, bool SentByMe,
    string Content /*原始正文(含XML)*/, string Kind /*text|image|voice|video|...*/, string Text /*可读文本*/);
```

### 5.3 接入现有应用导入管线

探针 `export` 输出的 JSON 与应用现有 **Moect 单群导入格式完全兼容**（`metadata.application == "Moect"`，仅新增 `generator` 字段标注真实来源）。导出文件可直接：

- 放入“消息来源 → Moect 监听目录”，由 `MoectInbox.ScanAsync` 周期扫描入库；
- 或走“手动导入”通道选择该文件。

消息入库后会经过与 Moect 导出完全相同的幂等去重、撤回标记和证据链路。

### 5.4 进程内直读与实时增量同步（已实现）

`StudentReminder.WxLocal.Adapter`（`src/StudentReminder.WxLocal.Adapter`，`net10.0-windows`）已把 `WxLocalReader` / `WxMessageReader` 封装为 `IDataSourceAdapter`（`WxLocalSourceAdapter`），直接接入现有 `SyncCoordinator`，不再走「探针 export JSON → MoectInbox 扫描」中转：

- `HealthAsync` = `WxLocalReader.Describe()` + 账号/进程检查；
- `ListConversationsAsync` = `ListSessions()` 映射；
- `FetchMessagesAsync` = 增量刷新 + 游标读取（`ReadMessagesAfter`，游标为 `(sort_seq, local_id)`），文本消息映射为 `NormalizedMessage`；
- `SubscribeAsync` = 用 `FileSystemWatcher` 监听 `db_storage` 下 `-wal` 变化即时唤醒同步，`SyncCoordinator` 的轮询作为兜底。

**实时增量原理**：首次运行做一次全量 `Refresh`（提钥 + 整库解密 + 合并 WAL，生成明文影子库）；之后每次同步只调用 `WxDbDecryptor.ApplyWalDelta`，用已持有的 `enc_key` 增量解密 `-wal` 里新提交的加密帧并覆写影子库对应页，新消息即可秒级入库。增量游标 `(salt1, salt2, 帧偏移)` 持久化在缓存目录旁的 `wal-cursor.json`；当微信 checkpoint 重置 WAL（salt 变化）时，该库自动整库重刷兜底。

> 影子库与 `wal-cursor.json` 同属明文缓存，位于 `%LOCALAPPDATA%\StudentReminder\wxlocal\<wxid>\`，与 `keys.json` 一并纳入一键清除范围（见 §8）。

## 6. 已验证情况

命令和代码示例中的账号及会话编号为合成占位值。

- **合成测试**：79/79 通过。覆盖密钥 HMAC 校验正反例、逐页解密字节级往返、WAL 已提交帧合并、过期 WAL 世代跳过、XOR 掩码 blob 候选还原、读取管线（会话/联系人/群成员/跨列缺失容错/ZSTD/群前缀剥离/local_type 复合值）。
- **真实环境（2026-11-11 本机）**：微信 4.x（数据目录 `<微信数据目录>\xwechat_files`，5 个进程运行中）→ 20 个加密库全部提钥成功、解密 0 失败；234 个会话正确列出；某课程群近 7 天 830 条消息中 422 条文字全部正确解出（发送者、时间戳、server_id、正文）。
- **测试夹具说明**：合成库无 SQLCipher 的“每页 80 字节保留区”，解密后 quick_check 必然报尾页损坏（真实微信库有保留区，不会出现），因此解密测试用**字节级比对**断言，读取测试直接跑明文库。这是夹具限制，不是模块缺陷。

## 7. 运维与故障排查

| 症状 | 原因与处理 |
|---|---|
| `未检测到微信进程 Weixin.exe` | 先启动并登录微信再运行 refresh |
| `OpenProcess 失败` / 密钥 0 命中 | 以**管理员身份**运行探针（读取其他进程内存需要 SeDebugPrivilege） |
| 密钥 0 命中（管理员也不行） | 微信版本更新导致内存布局变化 → 更新 `WxConstants.CipherXorMask` 与 `WxKeyExtractor` 中的偏移（`+0x88` 等），参照 Moect 上游同步 |
| 解密失败 / WAL 快照不完整 | 微信正在写页，模块自动重试 3 次；持续失败则重新 refresh |
| 会话很多但某群无消息 | 该群消息在 `biz_message_N.db` 或旧分片中，读取器已自动跨分片归并；确认该群在微信中确实有本地消息 |
| 缓存目录膨胀 | `%LOCALAPPDATA%\StudentReminder\wxlocal` 可整目录删除，下次 refresh 重建 |

## 8. 安全与合规提醒（必读）

1. **只读边界**：模块对微信进程内存和原始数据库只做读取，解密副本写入独立缓存目录；不修改、不删除、不拦截任何微信数据。
2. **缓存目录含明文聊天记录**：`wxlocal\` 下是**未加密**的数据库副本，等同于本地聊天记录明文。请勿放入同步盘/共享目录；应用集成时应提供一键清除（删目录即可）与保留期限设置。
3. **keys.json 含数据库密钥**：与聊天明文同级敏感，同上处理。
4. **计划书红线**：本模块的实现范围（密钥提取、进程内存读取、解密）与计划书 §1.3/§7.3/§12.2 的承诺相抵触，相关验收条款（P5-04 许可检查、隐私审计）需在对外分发前重新评估并更新计划书。
5. **许可**：算法参照 GPL-3.0 项目 Moect 独立重写；若后续判定为衍生作品，本项目需遵循 GPL-3.0 开源。商用或对外分发前请咨询专业意见。
6. **账号风险**：此类工具与微信客户端保护机制存在冲突，理论上存在账号被风控的可能，风险由使用者自担；建议仅在本机、本人账号、备份用途下使用。
