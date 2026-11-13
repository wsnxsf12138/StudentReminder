# 微信本地导出与读取候选

2026-11-11 核对 P0-02、P0-03 和 P2-01～02。WeMemory 带 Token 的本机健康检查返回 403。项目加入 Moect 文字文件导入流程，并核对 WeFlow Rust CLI 的命令 API。

## Moect 1.2.8

Windows x64 便携版的包与 EXE 校验值见服务清单。可使用程序的演示模式或已有解密备份。连接本机微信账号会先整理账号级快照，程序默认将配置和快照保存到 `%LOCALAPPDATA%\Moect`。真实账号连接及快照范围由用户决定。 [使用指南](https://github.com/mofan6/Moect/blob/v1.2.8/docs/usage.md)

在 Moect 中选定获授权的一个群，选择 **JSON**、所需的时间范围和 **快速导出**。将导出位置选为项目内 `artifacts/moect-exports`。在 PowerShell 7.5 或更新版本中执行转换，指定精确群名和稳定的本地账号别名。

```powershell
./scripts/Convert-MoectExport.ps1 -InputPath 'artifacts/moect-exports/导出的文件.json' -ExpectedGroup '获授权的群名' -AccountAlias wechat-local
```

脚本输出文件位于 `artifacts/moect-import/`。通过应用的「导入 JSON」打开它。重复导入同一账号时使用原别名，保留原始发送时间及上游消息编号。脚本校验单群和会话一致性，只转换文字记录。现有无 ID 消息按指纹去重。

实际用 Moect 演示模式导出 9 条记录，转出 8 条文字。项目导入服务写入 8 条，第二次导入识别 8 条重复，隔离数据库保留 8 条。

2026-11-11 用户在本机完成 Moect 连接，使用微信 `4.2.0.13` 的指定群验证。选择 11 月 4 日 00:00 至 11 月 11 日 23:59（北京时间）与 JSON 快速导出，得到 20 条记录。转换脚本保留 12 条文字。项目现有导入服务首次写入 12 条，冲突 0 条，第二次导入识别 12 条重复，最终只有该群的 12 条文字。原始文件从 Moect 默认导出目录复制到 `artifacts/moect-exports/`，转换文件在 `artifacts/moect-import/`，隔离数据库在 `artifacts/p0-live/moect-synthetic/`。这些文件均由 Git 忽略。

该结果验证了目标机器上的 Moect 单群离线导入。后台增量同步仍需单独验证。

### 重叠批次复测

将同一份已获授权的导出按发送时间分为两个批次。首批 6 条全部入库；第二批共 8 条，其中 2 条与首批重叠，实测新增 6 条、重复 2 条；再次导入第二批，新增 0 条、重复 8 条。隔离库最终仅有指定会话的 12 条文字，三个批次均无冲突。复测复用了已有导出，未获取新的群消息。

日常导入可以沿用同一账号别名，并让本次导出时间范围与上次略有重叠。应用会保留原发送时间和消息编号，并去除重复记录。

## WeFlow Rust CLI 6.0.0

Windows x64 便携包通过 GitHub SHA-256 与构建来源证明校验。在独立 `WEFLOW_HOME` 启动纯 API 服务，使用测试 Token。实际 `/health` 返回 `version=6.0.0`、`apiVersion=2`。无 Token 的 OpenAPI 请求返回 401，携带 Token 后返回契约。空配置的会话读取返回 409 `config_error`。

v0.2.2-alpha 增加 API v2 适配器并验证发布版 CLI 读取合成库。字段、断线结果与本机配置见 [WeFlow PoC](poc-weflow.md)。

真实验证需要可读取的本机微信数据库及对应授权配置。首次验证只读取用户指定群的已有文字消息。真实数据、账号快照和转换文件保存在本机，验收记录使用计数及状态。
