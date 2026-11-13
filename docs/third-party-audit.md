# 第三方依赖与待验证事项

## v0.6.2-alpha 桌面运行依赖与分发审查

2026-11-13 根据 [应用锁文件](../src/StudentReminder.App/packages.lock.json)、还原后 NuGet 元数据和 Release 输出核对。测试与探针的独立依赖见各自的锁文件。

| 组件 | 版本、来源与许可 | 打包应带的材料 |
|---|---|---|
| .NET Desktop Runtime、WPF | 当前构建依赖用户机器安装 .NET 10 Desktop Runtime。 [Microsoft 安装说明](https://learn.microsoft.com/en-us/dotnet/core/install/windows) | 如改为自包含发布，按实际 runtime pack 收录许可证与第三方声明。 |
| Microsoft.Data.Sqlite、Microsoft.Data.Sqlite.Core | 10.0.14，MIT。 [源码](https://github.com/dotnet/dotnet/tree/0427b9abbead3fa64c6655eb4290c537397131fa) | MIT 许可证及版权声明。 |
| Microsoft.Bcl.AsyncInterfaces、Microsoft.Bcl.Memory、Microsoft.Extensions.DependencyInjection.Abstractions、Microsoft.Extensions.Logging.Abstractions、System.Numerics.Tensors | 10.0.13，MIT。 [源码](https://github.com/dotnet/dotnet/tree/4099f83977f4d29604a9aeb971fb0a7bc1d5f699) | MIT 许可证、版权声明及包内 `THIRD-PARTY-NOTICES.TXT`。 |
| System.Text.Encoding.CodePages | 11.0.0，MIT。 [源码](https://github.com/dotnet/dotnet/tree/297acf930fb0f5c07fd4e51d78e521b4ad2e5d1f) | MIT 许可证、版权声明及包内第三方声明。 |
| Microsoft.Extensions.AI.Abstractions | 10.11.0，MIT。 [源码](https://github.com/dotnet/extensions/tree/25f07afa51034e37b334210642d7dc5f191c142d) | MIT 许可证及版权声明。 |
| CommunityToolkit.HighPerformance | 8.4.2，MIT。 [源码](https://github.com/CommunityToolkit/dotnet/tree/35dfe7b12da0e64384bfaec7ebc0883196a89f9f) | 包内 `License.md`、`ThirdPartyNotices.txt`。 |
| System.Linq.Async、System.Interactive.Async | 7.0.1，MIT。 [源码](https://github.com/dotnet/reactive/tree/96eed184a84471105d5d701a99e0b98629703d3f) | MIT 许可证及版权声明。 |
| ZstdSharp.Port | 0.8.5，MIT。 [源码](https://github.com/oleg-st/ZstdSharp/tree/033a60cc132a437ff64467fd0a531b45638ce2f2) | MIT 许可证与版权声明，核对上游 Zstandard 附带声明。 |
| SQLitePCLRaw 的 bundle、core、provider、lib | 2.1.13，NuGet 包声明 Apache-2.0。Release 的 Windows x64 `e_sqlite3.dll` 实测报告 SQLite 3.53.3，SQLite 上游代码属公有领域。 [SQLitePCLRaw](https://github.com/ericsink/SQLitePCL.raw) · [SQLite 声明](https://www.sqlite.org/copyright.html) | Apache-2.0 文本及适用的 NOTICE，保留原生 SQLite 来源声明。按最终 RID 核对原生文件清单。 |
| LLamaSharp、LLamaSharp.Backend.Cpu | 0.30.0，MIT。托管库固定源码引用的 llama.cpp 提交为 `815a2a5915f22ce6a760c676389c5dfe8535c08f`。Windows x64 输出包含 AVX、AVX2、AVX512 和无 AVX 的 llama、ggml、mtmd DLL。CPU 包自身未声明二进制源码提交。 [托管源码](https://github.com/SciSharp/LLamaSharp/tree/d03b6cbca3c66357e2ed9ee9d0324f5eaeabfd4f) · [CPU 包](https://www.nuget.org/packages/LLamaSharp.Backend.Cpu/0.30.0) · [llama.cpp 许可证](https://github.com/ggml-org/llama.cpp/blob/815a2a5915f22ce6a760c676389c5dfe8535c08f/LICENSE) | 保留 LLamaSharp 与 llama.cpp、ggml 的许可证和版权声明。核对最终原生二进制的构建来源及随附声明。 |
| 可选 Qwen3.5 GGUF | 2B 和 4B 试用权重来源及 SHA-256 见 [v0.6.0-alpha 交接](handoffs/v0.6.0-alpha.md)。Qwen3.5-2B 上游为 Apache-2.0。 [原模型许可证](https://huggingface.co/Qwen/Qwen3.5-2B/blob/main/LICENSE) · [2B 量化仓库](https://huggingface.co/bartowski/Qwen_Qwen3.5-2B-GGUF) | 若随包分发，固定到最终量化文件与哈希，附对应许可证、版权及适用的 NOTICE，标记量化来源。复核 4B 最终选件后再纳入。 |
| Moect、WxLocal、QqLocal | Moect 1.2.8 是独立安装的备用导入工具，GPL-3.0。WxLocal 与 QqLocal 是桌面应用的进程内依赖。直读模块的代码来源仍需确认，核查结果见下节。 [Moect 清单](../tools/services/service-manifest.json) · [微信来源记录](wx-local-reader.md) · [QQ 来源记录](qq-local-reader.md) | 先确定分发包是否包含两种直读或 Moect。若分发 GPL 覆盖的二进制，按适用条款附许可及对应源码。 [GPL-3.0 条款](https://www.gnu.org/licenses/gpl-3.0.html) |

## P5-04 许可定案

StudentReminder 采用 GPL-3.0 并随二进制附对应源码。下表保留直读模块的来源核查记录。

| 核查对象 | 已取得的证据 | 尚需补齐 |
|---|---|---|
| Moect 与 WxLocal | Moect v1.2.8 标签解析为 `88dffc7d0d06632f8061b0a19aa82b38337d7ee5`，与服务清单一致。仓库内的许可证副本与该标签的 Git blob 一致。项目接入记录称 WxLocal 参考 Moect 并以 C# 重写。 [上游固定版本许可证](https://github.com/mofan6/Moect/blob/v1.2.8/LICENSE) · [项目接入记录](wx-local-reader.md) | 确认开发时实际参考的提交及文件。按文件或功能记录参考内容与实现来源，核对是否复用了受保护的表达。分发独立 Moect 程序与分发含 WxLocal 的主程序须分别判断。 |
| QqLocal 与 QQ 参考项目 | `NapNeko/qq_dump_db` 的原链接在 GitHub API 返回 404，项目文档中的 MIT 说明目前缺少可复核的上游副本。QQBackup 的 `nt_msg_db_util` 可查到 GPL-3.0 文本，`qq-win-db-key` 和 `QQDecrypt` 当前所核对版本使用同一份自定义非商用协议。 [nt_msg_db_util 许可](https://github.com/QQBackup/nt_msg_db_util/blob/317024ce2c5e3482fdf9f310a5a7159ab4927e34/LICENSE) · [qq-win-db-key 许可](https://github.com/QQBackup/qq-win-db-key/blob/5c643c39bdc65a7ce3ecc414052fdfd93d5f9107/LICENSE) · [QQDecrypt 许可](https://github.com/QQBackup/QQDecrypt/blob/4cc0912c92dcb1edcc799127e56178c60a7b7cdc/LICENSE) | 确认开发时使用的仓库、提交及具体内容。这三个 QQBackup 版本是本次找到的候选来源。若确有受保护代码被复用，按实际版本核对兼容性或取得额外授权。 |
| StudentReminder 主程序 | v0.6.2-alpha 快照没有项目根目录 `LICENSE`。桌面工程引用两种直读适配器，适配器再引用各自的直读实现。 [桌面工程](../src/StudentReminder.App/StudentReminder.App.csproj) | 项目权利人决定首包的目标和许可路线，并核对各贡献的授权。若采用 GPL 路线，要确认整个拟分发组合的许可兼容性及对应源码的提供方式。 |

**定案路径**

1. 若首个试用包以验证事项闭环为目标，可从发布构建中移除两种直读及其探针，保留手动导入和 Moect 导出文件导入。Moect 独立取得，GGUF 权重可由使用者自备。用最终安装包文件清单验证发布边界，再为保留的代码和依赖确认许可证及声明。
2. 若首包必须保留两种直读，先按上表补齐来源映射。软件的思想、处理过程和操作方法与具体表达适用不同的著作权判断。若存在 GPL 覆盖的代码并构成组合程序，评估以 GPLv3 兼容方式分发，并让接收者取得与包对应的源码和构建脚本。对适用非商用或其他不兼容条款的内容，须先取得适用授权，或从分发范围移除。 [计算机软件保护条例第六条](https://ipc.court.gov.cn/zh-cn/news/view-6245.html) · [GPLv3 第 5、6、10 节](https://www.gnu.org/licenses/gpl-3.0.html)

**验收材料**包括项目权利人的书面决定、拟分发文件清单、对应组件的版本和来源、许可证及适用的 NOTICE、需要随包提供的源码和获取方式。将这些材料与候选安装包逐项核对。给少量测试者发包也需要按其中组件的条款办理。 [GPLv3 第 0、6 节](https://www.gnu.org/licenses/gpl-3.0.html) · [Apache 2.0 第 4 节](https://www.apache.org/licenses/LICENSE-2.0.html)

## 隐私与出站边界

- 每次启动暂停来源和云端自动分析。模型列表与连接测试由用户触发，前者使用服务商凭据，后者提交固定合成文本。分析使用选定的会话和脱敏片段。手动历史分析、联合分析和摘要润色有提交前预览。云服务使用固定 HTTPS 地址，默认客户端禁用自动重定向。
- API Key 由当前 Windows 用户的 DPAPI 保护，普通 SQLite 配置保存模型设置。应用日志使用操作名、计数和异常类型。探针进度输出可能含本机路径，探针的独立导出与缓存由操作者管理。
- 设置页可清理当前实例的直读缓存。读取会话会建立包含解密数据和密钥的缓存。已入库数据与凭据的离线删除步骤见 [README](../README.md#清除缓存与本地资料)。
- 公开分发前仍须核对项目许可、原生二进制声明、群聊第三方信息处理及 QQ、微信和模型服务商条款。安装包和真实账户链路另行验收。

## 历史记录（2026-11-10）

以下服务选型及协议记录对应已退役的本机 HTTP 来源。

更新日期：2026-11-10。此文记录固定版本、公开协议与文件部署，**不是已完成的 P0 合规或真实兼容性审查结论**。

## 当前代码依赖

使用 .NET 10 SDK、WPF、Microsoft.Data.Sqlite 和 xUnit 测试工具。直接和传递 NuGet 版本及内容哈希已记录在各项目的 `packages.lock.json`，本地和 CI 使用锁定还原。

CI 使用上游 `actions/checkout` v7.0.1、`actions/setup-dotnet` v6.1.0、`actions/upload-artifact` v7.0.2；已通过上游 GitHub API 核对发布标签、提交及 `action.yml` 的 Node 24 运行时，并锁定提交 SHA。

参考：[.NET Windows 安装](https://learn.microsoft.com/en-us/dotnet/core/install/windows)、[Microsoft.Data.Sqlite](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/)、[SQLite 事务](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/transactions)。对外分发前仍须整理传递依赖、原生 SQLite 组件的许可证及必要声明。

## 固定的数据源候选

| 候选 | 上游入口 | 本项目验证状态 |
|---|---|---|
| WeFlow | [固定提交](https://github.com/hicccc77/WeFlow/tree/91adcfdbb24e8e147d8332440b8c6ac899ad2abf) | 5.0.0 源码子模块及独立构建锁；LICENSE 为 CC BY-NC-SA 4.0；完整构建及便携部署成功，EXE / app.asar 哈希已登记；本机构建 EXE 为 NotSigned |
| WeMemory | [官方 API](https://wememory.toolwharf.com/zh/docs/local-api) | 官方 Windows ZIP 1.0.1 已解压；程序内版本 1.0.0.1，Authenticode 为 NotSigned；本地包 SHA-256 已登记，未见厂商公开哈希或签名证明 |
| qqflow-server | [v0.10.0](https://github.com/ren-yc/qqflow-server/tree/d481e2f4a5b8ec5f68fda7bb005a0204743f2fcb) | 官方 Windows 包已解压，包哈希与 Release digest 一致，`--version` 返回 0.10.0；LICENSE 为 MIT |

QQ 公开文档还核对了提交 `8e7f0fd5bfc66893977b31be8130600542d738e9`；与安装包 tag 的差异为文档及测试等调整，适配器使用的 Health / Pull 字段及路径已对照 tag 的 OpenAPI。不能把文档提交写成二进制对应源码提交。

应用适配器仅消费公开 GET 接口；第三方实现通过独立源码子模块或程序包安装。安装源码/程序不等于获得平台授权或分发许可。许可副本、安装来源和校验详见 `tools/services/`。WeMemory 未随 ZIP 提供本项目可采用的开源许可证，后续分发前须按厂商条款确认。

## 协议验证与限制

- WeFlow 与 QQ 使用 ChatLab Pull 的 `sync.nextSince`、`nextOffset` 和固定上界；下一轮使用重叠窗口补拉，避免只依赖 SSE。WeMemory 按其公开文档实现 before/since/until 轮询，组合语义仍需真机 PoC。
- WeFlow 的全局 SSE 会提供未选会话片段，默认不开启；用户明确授权后仅持久化所选会话。
- QQ 原生 SSE 的 rawid 不能直接当成 Pull 的 platformMessageId，当前不猜测撤回目标，显示复核状态。
- WeFlow 固定源码的 WCDB 实现由用户指定外部路径，本项目未配置该原生组件；当前仅验证客户端源码构建与合成协议，实际数据库读取另行验收。
- 本项目没有配置真实数据库凭据、账号或聊天会话。真实授权、客户端版本、切换数据库、服务 License 状态及 48 小时实验均未完成。
