# WeMemory 本机 PoC

任务 P0-03、P2-02。检查日期 2026-11-11，程序来自 Windows ZIP 1.0.1，内置版本 1.0.0.1。

| 检查 | 实测结果 |
|---|---|
| 客户端启动 | 主窗口可打开，数据库状态显示未连接 |
| 本地监听 | `127.0.0.1:16789` |
| 无 Token 的健康请求 | HTTP 401 |
| 使用本机配置中的 Token 请求健康接口 | HTTP 403。按接口文档，客户端尚未完成授权或现有授权失效 |
| 数据库就绪、目标群定位、分页与增量轮询 | 待客户端授权并打开数据库后验证 |
| Token 更替 | 待验证 |

本轮的真实数据范围是用户指定的单个微信群。验收使用该群已有记录，交接只记录计数、状态和脱敏字段类型。

## 复测

1. 在 WeMemory 完成已有授权并打开用户指定的本机微信数据库，确认健康接口返回 `hasDb=true`。
2. 在隔离数据目录配置 WeMemory 来源，保存后只选择获授权的群。先核对媒体导出设置，再验证文本读取。
3. 在仓库根目录使用 Release 构建运行探测工具。省略 `--pull-selected` 时只检查连接。加上该选项会检查所选群的消息分页，报告计数和状态。

```powershell
dotnet run --project tools/StudentReminder.SourceProbe --configuration Release --no-build -- artifacts/p0-live wememory p0-wechat --pull-selected
```

4. 在桌面端对同一选择验证单次同步、重复拉取、暂停、服务恢复及旧 Token 的错误显示。记录每项实测结果后更新来源选择 ADR。

[WeMemory 本地 HTTP 接口文档](https://wememory.toolwharf.com/zh/docs/local-api)
