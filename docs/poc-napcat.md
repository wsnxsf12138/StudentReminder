# QQMessageTest 环境验证

第一阶段按 QQMessageCollector 计划建立独立控制台。程序通过 `ClientWebSocket` 连接本机 `ws://127.0.0.1:<端口>/`，用 OneBot Token 鉴权。只接收指定登录账号下的指定群或好友私聊，过滤自己的消息和其他会话。连接断开后按 1～30 秒退避重连。 [OneBot 连接方式](https://napneko.github.io/NapCatQQ-Desktop/docs/connections/) · [消息事件结构](https://napneko.github.io/onebot/basic_event)

## 运行

在兼容的 QQ 与 NapCat Framework 环境中启用 OneBot **WebSocket 服务器**，绑定 `127.0.0.1`，填写 Token，消息格式选 `array`。确认登录的是已获授权的测试账号。 [Framework 环境说明](https://napneko.github.io/guide/boot/Framework)

运行 `QQMessageTest.exe`，按提示填入正向 WebSocket 端口、测试账号、授权群号或好友号。跳过其中一类会话时填 `-`。Token 仅在本机隐藏输入。默认输出消息类型和时间，选择显示正文时仅在当前终端打印，测试结束按 `Ctrl+C`。

也可从源码运行：

```powershell
dotnet run --project tools/StudentReminder.NapCatReceiver --configuration Release
```

Windows x64 单文件产物在 `artifacts/qq-message-test/win-x64/QQMessageTest.exe`。发布命令如下。

```powershell
dotnet publish tools/StudentReminder.NapCatReceiver --configuration Release --runtime win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o artifacts/qq-message-test/win-x64
```

## 本机结果

普通 QQ 安装目录含 `9.9.31-49738` 和 `9.9.37-54395` 两份资源，两个清单都指向原始 QQ 入口。未检测到 LiteLoaderQQNT 或 NapCat Framework。LiteLoaderQQNT 已归档，新版 Windows QQ 的安装依赖完整性校验绕过，本机真实采集在此处受阻。 [归档状态](https://github.com/LiteLoaderQQNT/LiteLoaderQQNT)

合成端到端测试启动本机正向 WebSocket，校验 Bearer Token，发送分片、错误账号、未授权会话、自己发送的消息及异常 JSON，再送出一个授权群消息。连接断开后，第二次连接收到授权好友私聊。真实账号登录、群聊和私聊收包等待兼容环境。第一阶段只验证实时文字，入库、富消息和桌面界面属于后续阶段。

2026-11-11 复核普通 QQ 后，直接用发布目录中的 Windows x64 单文件连接合成 WebSocket。鉴权成功，首次收到授权群事件 1 条，重连后收到授权好友事件 1 条；错误账号被过滤，默认输出只含会话类型和时间。普通 QQ 的 Framework 安装和真实收包仍未验收。
