AI 学生事务助理 — 随包源码

与此二进制对应的项目源码位于本目录的 StudentReminder-source.zip。
源码 ZIP 包含项目源码、资源、锁文件、迁移、合成样本和构建脚本。
发布快照中的 QQ 适配器映射测试使用合成账号与群号。
使用 Windows x64 和 global.json 指定的 .NET 10 SDK。解压源码后，在源码根目录运行：

    dotnet restore StudentReminder.sln --locked-mode
    dotnet publish src/StudentReminder.App -c Release -r win-x64 --self-contained true -p:RuntimeFrameworkVersion=10.0.14 "-p:PathMap=$($PWD.Path)=/_/StudentReminder" -p:DefineConstants=STUDENTREMINDER_TRIAL -o publish

NuGet 还原需要网络或预先准备的 NuGet 缓存。发布时 NuGet 会为 win-x64 补充运行时锁定信息。
源码内的 packaging/New-ReleasePackage.ps1 用于从 Git 工作区生成完整便携包。
项目和第三方许可见解压后根目录的 LICENSE 和 THIRD-PARTY-NOTICES.txt。

主要第三方组件的上游源码
LLamaSharp 0.30.0 https://github.com/SciSharp/LLamaSharp/tree/d03b6cbca3c66357e2ed9ee9d0324f5eaeabfd4f
llama.cpp / ggml https://github.com/ggml-org/llama.cpp/tree/815a2a5915f22ce6a760c676389c5dfe8535c08f
SQLitePCLRaw 2.1.13 https://github.com/ericsink/SQLitePCL.raw/tree/0873d14f6dcb3a8537444679468499ab9f5a782a
SQLite 3.53.3 https://github.com/sqlite/sqlite/tree/version-3.53.3
ZstdSharp.Port 0.8.5 https://github.com/oleg-st/ZstdSharp/tree/033a60cc132a437ff64467fd0a531b45638ce2f2
.NET 10.0.14 https://github.com/dotnet/dotnet/tree/0427b9abbead3fa64c6655eb4290c537397131fa

FILELIST.txt 列出包内载荷文件，SHA256SUMS.txt 同时覆盖 FILELIST.txt。
可以在二进制解压目录用 certutil -hashfile "StudentReminder.App.exe" SHA256 校验单个文件。
