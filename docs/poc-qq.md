# qqflow-server 本机 PoC

2026-11-11 复核 P0-04 和 P2-03。项目内的 Windows 发布版 `0.10.0` 校验通过，服务监听 `127.0.0.1:5032`。实际 `GET /health` 返回 `status=starting`、`account=unregistered`，对应应用的数据库未就绪。现有契约测试验证会话与消息映射、故障及 SSE 行为。

真实注册需测试 QQ 号、对应 `nt_msg.db` 路径和已获授权的数据库密钥。服务首次启动会生成 API Token。可用官方 CLI 的 `token` 子命令在本机查看，随后在 `./scripts/Prepare-P2Environment.ps1 -Action RegisterQQ` 的提示中录入。密钥及 Token 在本机输入。服务重启后需要重新注册。 [固定版本启动与注册说明](https://github.com/ren-yc/qqflow-server/blob/d481e2f4a5b8ec5f68fda7bb005a0204743f2fcb/docs/quickstart.md)

实际账号仍待注册。注册达到 `ready` 后，在隔离应用目录保存来源，先选已授权的测试会话，再验收真实 `sessions/messages`、增量和重启补拉。SSE 需要用户在来源配置中明确启用。真实撤回关联、QQ 客户端兼容性与源库只读验证待完成。
