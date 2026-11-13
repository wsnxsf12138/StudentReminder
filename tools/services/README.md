# 项目内的 Moect 备用导入

Moect 1.2.8 可用于导出已授权的微信会话 JSON。版本、上游地址和哈希固定在 [服务清单](service-manifest.json)。安装目录与下载包由 Git 忽略。

在项目根目录执行：

```powershell
./scripts/Install-ProjectServices.ps1 -Service moect
./scripts/Get-ProjectServices.ps1
./scripts/Start-ProjectService.ps1 -Service moect
```

Moect 界面用于登录、刷新快照和导出。导出后可在「Moect 备用导入」选择获授权的群，也可使用 [手动导入流程](../../docs/moect-watch.md)。

应用的直读来源为 WxLocal 和 QqLocal。0.5.0-alpha 移除了 qqflow-server、WeFlow 桌面版、WeFlow CLI 和 WeMemory 的项目安装清单及本机 HTTP 适配器。历史版本的实验记录和旧版使用步骤保留在对应标签及交接文档。
