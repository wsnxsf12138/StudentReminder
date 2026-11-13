# WeFlow Rust CLI 本机 PoC

2026-11-11 对 P0-02、P2-01 和 P2-04 使用项目内发布版 `6.0.0`，以独立 `WEFLOW_HOME` 在 `127.0.0.1:18109` 运行 API v2。测试使用生成的加密合成数据库与公开测试 Token。

| 检查 | 实测结果 |
|---|---|
| 版本与鉴权 | `/health` 返回 `6.0.0` 和 `apiVersion=2`；无 Token 读取 OpenAPI 返回 401；空配置读取会话返回 409 |
| 数据库与会话 | `/api/db/test` 返回 `connected=true`；`/api/chat/sessions` 返回 1 个合成群；配置脚本成功验证合成库并保留其他配置项；启动脚本在 `18108` 启动后，鉴权读取通过，无 Token 请求返回 401 |
| 首次同步 | 通过应用同步协调器读取选中群，跨分页入库 201 条 |
| 停机 | 连接故障识别为 `offline`，本地库保留 201 条和已提交的游标 |
| 重启与重放 | 同秒新增 204 条全部补入，累计 405 条；再次同步新增 0 条 |

会话使用 `username` 和 `displayName`，消息使用 `serverId`、`createTime`、`senderUsername`、`localType` 和 `parsedContent`。页内第一条的 `_cursor` 作为下一次 `after`；初次读取使用 `since`，消息和游标一起写入事务。服务实例变化时重试该页，所选会话绑定到账号目录的哈希。 [固定版本 API 文档](https://github.com/334456777/WeFlow/blob/v6.0.0/docs/zh-CN/serve-api.md)

契约测试覆盖未选会话过滤、账号变化、重启、同秒分页和鉴权故障。发布版联调的生成程序与数据位于当前机器的 `artifacts/p2-validation/`，应用和上游合成库均为独立数据目录。可移植的自动检查为 `./scripts/Test-Project.ps1`。

Rust 来源使用轮询。真实微信数据库兼容性、撤回和长期运行仍待验证。桌面版 WeFlow 的真实 `sessions/messages/SSE` 也待验证。可在本机完成 [服务配置](../tools/services/README.md) 后对授权群复测。已通过真实账号验收的离线路径见 [Moect PoC](poc-wechat-alternatives.md)。
