# 版本交接索引

每个完成版本保留一份独立交接文档，代码和文档共同纳入 Git。以版本标签定位完整快照，正文可另外注明代码验收提交；标签不能替代实际测试记录。

| 版本 | 交接文档 | 范围 |
|---|---|---|
| 0.1.0-alpha | [v0.1.0-alpha](v0.1.0-alpha.md) | P1-01～P1-05，本地手动导入基础版 |
| 0.2.0-alpha | [v0.2.0-alpha](v0.2.0-alpha.md) | 来源选择与同步协议基础、合成验收、项目内候选程序文件安装；真实采集待验收 |
| 0.2.1-alpha | [v0.2.1-alpha](v0.2.1-alpha.md) | Moect 单群文字导入、来源探测和 Rust CLI 本机接口验证 |
| 0.2.2-alpha | [v0.2.2-alpha](v0.2.2-alpha.md) | Rust CLI 合成库增量同步、schema v3、本机数据源配置 |
| 0.2.3-alpha | [v0.2.3-alpha](v0.2.3-alpha.md) | QQMessageTest 独立控制台及 OneBot 正向事件验证 |
| 0.2.4-alpha | [v0.2.4-alpha](v0.2.4-alpha.md) | Moect 定时导入、schema v4、DeepSeek 和豆包分析 |
| 0.3.0-alpha | [v0.3.0-alpha](v0.3.0-alpha.md) | 微信进程内直读适配器 + WAL 增量实时同步（WxLocal 接入调度器） |
| 0.4.0-alpha | [v0.4.0-alpha](v0.4.0-alpha.md) | P3-01～P3-06，WxLocal 新消息分析、待确认事项与事件版本，schema v5 |
| 0.4.1-alpha | [v0.4.1-alpha](v0.4.1-alpha.md) | WxLocal 桌面会话发现、账号绑定与同步入口修复 |
| 0.4.2-alpha | [v0.4.2-alpha](v0.4.2-alpha.md) | QQ NT 本地直读适配器接入与 AI 分析泛化 |
| 0.5.0-alpha | [v0.5.0-alpha](v0.5.0-alpha.md) | P4-01～P4-05，提醒、恢复补报、每日摘要、托盘，schema v7，退役四个本机 HTTP 来源 |
| 0.5.1-alpha | [v0.5.1-alpha](v0.5.1-alpha.md) | AI 模型设置页，DeepSeek／豆包模型列表、搜索及手填 |
| 0.6.0-alpha | [v0.6.0-alpha](v0.6.0-alpha.md) | 内置本地推理、云端精提取、多消息证据及日期精度，schema v8 |
| 0.6.1-alpha | [v0.6.1-alpha](v0.6.1-alpha.md) | 同会话定量定时批提取、问卷与重要通知、讲座预约，schema v8 |
| 0.6.2-alpha | [v0.6.2-alpha](v0.6.2-alpha.md) | P5-01、P5-04 回归验证、直读缓存清理与分发依赖审查，schema v8 |
| 0.6.3-alpha | [v0.6.3-alpha](v0.6.3-alpha.md) | QQ 群会话名称显示修复 |
| 0.6.4-alpha | [v0.6.4-alpha](v0.6.4-alpha.md) | QQ 私聊名称修复与无效会话过滤 |
| 0.6.5-alpha | [v0.6.5-alpha](v0.6.5-alpha.md) | QQ 消息发送者名称及本人、系统消息识别 |
| 0.6.6-alpha | [v0.6.6-alpha](v0.6.6-alpha.md) | 清理 QqLocal 残留「0」伪群聊会话 |
| 0.6.7-alpha | [v0.6.7-alpha](v0.6.7-alpha.md) | QqLocal 群系统消息识别与发送者昵称快照兜底 |
| 0.6.8-alpha | [v0.6.8-alpha](v0.6.8-alpha.md) | QqLocal 私聊名称与 QQ 公众号识别（ISSUE-02） |
| 0.6.9-alpha | [v0.6.9-alpha](v0.6.9-alpha.md) | Windows 应用、系统托盘和界面状态图标（P4-02、P4-04、P4-05） |
| 0.6.10-alpha | [v0.6.10-alpha](v0.6.10-alpha.md) | 首页事项日历、当天安排与详情跳转（P4-04） |

## 遗留问题

| 问题 | 状态 | 概要 |
|---|---|---|
| direction 语义复核 | 待解决 | `direction`（`40013`）语义基于归纳结论（`0` 收到、`3` 系统、`1/2/4/5` 为我发出），QQ 更新后若新增取值需复核 |

后续版本复制 [模板](TEMPLATE.md)，按实际结果填写，完成后更新此索引、版本号和 CHANGELOG。未执行、受阻或未通过的项目应明确标注，不能写成已完成。
