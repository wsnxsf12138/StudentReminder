#requires -Version 5.1
[CmdletBinding()]
param(
    [ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?$')]
    [string]$Version = '0.6.8-alpha',
    [switch]$IncludeModels,
    [string]$OutputRoot = 'artifacts/release'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (Test-Path Variable:PSNativeCommandUseErrorActionPreference) {
    $PSNativeCommandUseErrorActionPreference = $false
}
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$utf8 = [Text.UTF8Encoding]::new($false)
$utf8Bom = [Text.UTF8Encoding]::new($true)
# .NET Framework 用标准编码实例识别 ZIP 文件名的 UTF-8 标记。
$zipEncoding = [Text.Encoding]::UTF8
$comparison = [StringComparison]::OrdinalIgnoreCase
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$releaseRoot = [IO.Path]::GetFullPath((Join-Path $repo 'artifacts/release'))
$runtimeVersion = '10.0.14'
$zipName = "StudentReminder-$Version-win-x64.zip"

if (![IO.Path]::IsPathRooted($OutputRoot)) {
    $OutputRoot = Join-Path $repo $OutputRoot
}
$pathSegments = ($OutputRoot -replace '^[A-Za-z]:', '') -split '[\\/]'
foreach ($segment in $pathSegments) {
    if ($segment -in @('', '.', '..')) { continue }
    if ($segment -match '[<>:"|?*\x00-\x1f]|[ .]$|^(CON|NUL|PRN|AUX|COM[1-9¹²³]|LPT[1-9¹²³])(?:\.|$)') {
        throw '输出路径包含 Windows 保留名称或不安全字符。'
    }
}
$output = [IO.Path]::GetFullPath($OutputRoot).TrimEnd('\')
if (!$output.Equals($releaseRoot, $comparison) -and !$output.StartsWith("$releaseRoot\", $comparison)) {
    throw '-OutputRoot 必须位于仓库的 artifacts/release 目录内。'
}

function Assert-PlainItem($Item) {
    if ($Item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw "路径包含重解析点，已中止：$($Item.FullName)"
    }
}

function Get-DistFiles([string]$Directory) {
    Assert-PlainItem (Get-Item -LiteralPath $Directory -Force)
    foreach ($item in Get-ChildItem -LiteralPath $Directory -Force) {
        Assert-PlainItem $item
        if ($item.PSIsContainer) {
            Get-DistFiles $item.FullName
        } else {
            $item
        }
    }
}

function Remove-DistItem($Item) {
    $resolved = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $Item.FullName).ProviderPath)
    if (!$resolved.StartsWith("$dist\", $comparison)) {
        throw "清理目标超出本次 dist：$resolved"
    }
    Assert-PlainItem $Item
    if ($Item.PSIsContainer) {
        # 检查所有子项，避免递归删除时跟随目录联接或符号链接。
        Get-DistFiles $resolved | Out-Null
        Remove-Item -LiteralPath $resolved -Recurse -Force
    } else {
        Remove-Item -LiteralPath $resolved -Force
    }
}

function Get-JsonProperty($Object, [string]$Name) {
    if ($null -ne $Object) {
        $property = $Object.PSObject.Properties[$Name]
        if ($null -ne $property) { $property.Value }
    }
}

function Assert-NoPrivatePayload([string]$RelativePath, [IO.Stream]$Stream) {
    if ($RelativePath -match '(^|/)(?:data|local-data|wxlocal|qqlocal|logs?|secrets|credentials|exports|chat-exports|backups)(/|$)|(?:^|/)(?:\.env(?:\.[^/]*)?|keys?\.json|credentials?\.json|secrets?\.json|wal-cursor\.json)$|\.(?:db|sqlite3?|db3)(?:-(?:wal|shm|journal))?$|\.(?:key|pem|pfx|p12|dpapi|cred|log)$') {
        throw '发现数据库、凭据、日志或导出路径，已停止出包。'
    }
    if ($null -eq $Stream) { return }
    $header = [byte[]]::new(16)
    $reader = [IO.BinaryReader]::new($Stream, $utf8, $true)
    try { $header = $reader.ReadBytes($header.Length) } finally { $reader.Dispose() }
    if ([Text.Encoding]::ASCII.GetString($header) -eq ('SQLite format 3' + [char]0)) {
        throw '发现 SQLite 数据文件，已停止出包。'
    }
}

function Get-LockedPackages([string]$Root) {
    $lockFiles = Get-ChildItem -LiteralPath (Join-Path $Root 'src') -Filter 'packages.lock.json' -Recurse -File
    foreach ($file in $lockFiles) {
        $relative = $file.FullName.Substring($Root.Length + 1)
        $document = Get-Content -LiteralPath $file.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
        foreach ($framework in $document.dependencies.PSObject.Properties) {
            if ($framework.Name -like '*/*') { continue }
            foreach ($package in $framework.Value.PSObject.Properties) {
                if ($package.Value.type -ne 'Project') {
                    "$relative|$($framework.Name)|$($package.Name)|$($package.Value.resolved)|$($package.Value.contentHash)"
                }
            }
        }
    }
}

$directory = $repo
Assert-PlainItem (Get-Item -LiteralPath $directory -Force)
foreach ($segment in $output.Substring($repo.Length + 1).Split('\')) {
    $directory = Join-Path $directory $segment
    if (!(Test-Path -LiteralPath $directory)) {
        $null = [IO.Directory]::CreateDirectory($directory)
    }
    $item = Get-Item -LiteralPath $directory -Force
    Assert-PlainItem $item
    if (!$item.PSIsContainer) { throw "输出路径不是目录：$directory" }
}
$zip = Join-Path $output $zipName
if (Test-Path -LiteralPath $zip) {
    throw "产物已存在，请用 -OutputRoot 选择另一个子目录：$zip"
}

$modelName = 'Qwen_Qwen3.5-2B-Q4_K_M.gguf'
$modelHash = '57A1085840F497D764A7FC5D346922DBDE961EFB54CC792EA81D694FD846A1D8'
$model = $null
if ($IncludeModels) {
    $modelsDirectory = Join-Path $repo 'Models'
    if (Test-Path -LiteralPath $modelsDirectory) {
        $models = @(Get-ChildItem -LiteralPath $modelsDirectory -File -Filter '*.gguf' -Force)
        if ($models.Count -ne 1 -or $models[0].Name -ne $modelName) {
            throw "Models 目录只能包含本次已核实的 GGUF：$modelName"
        }
        $model = $models[0].FullName
    } else {
        $model = Join-Path $repo "artifacts/local-models/$modelName"
    }
    if (!(Test-Path -LiteralPath $model -PathType Leaf) -or
        (Get-FileHash -LiteralPath $model -Algorithm SHA256).Hash -ne $modelHash) {
        throw "模型缺失或哈希不符。请核对模型许可与文件后放入 Models/$modelName。"
    }
}

$dist = Join-Path $output ('dist-' + [Guid]::NewGuid().ToString('N').Substring(0, 12))
$payload = Join-Path $dist 'package'
$build = Join-Path $dist 'build'
$sourceDir = Join-Path $payload 'source'
$null = [IO.Directory]::CreateDirectory($sourceDir)
$sourceZip = Join-Path $sourceDir 'StudentReminder-source.zip'
$temporaryZip = Join-Path $dist $zipName

# 从已提交的同一快照归档源码和许可，并用该归档构建二进制。
$commit = git -C $repo rev-parse --verify HEAD
if ($LASTEXITCODE -ne 0) { throw '无法确定当前 Git 提交。' }
$sourcePaths = @('src', 'tests', 'scripts', 'tools', 'docs', 'Directory.Build.props',
    'global.json', 'StudentReminder.sln', 'README.md', 'CHANGELOG.md', 'AGENTS.md', '.gitattributes', '.gitignore',
    'LICENSE', 'THIRD-PARTY-NOTICES.txt', 'notices', 'packaging')
$tree = @(git -C $repo ls-tree -r $commit -- $sourcePaths)
if ($LASTEXITCODE -ne 0 -or @($tree -match '^(120000|160000) ').Count -gt 0) {
    throw '无法读取源码树，或源码树包含需要单独处理的符号链接或子模块。'
}
$sourceFiles = @(git -C $repo -c core.quotepath=false ls-tree -r --name-only $commit -- $sourcePaths)
if ($LASTEXITCODE -ne 0) { throw '无法读取源码文件清单。' }
$sampleHashes = @{
    'messages.synthetic.json' = 'DA82477F0FB25375CF0016E5F7619429C875C394F81358497E3BF127EBD8E654'
    'messages.conflicts.synthetic.json' = '089F6BA2FFB61008D3D5BF042F0E5F8B4B9657D35B200304A2CCA33AAFEAF663'
}
foreach ($relative in $sourceFiles) {
    Assert-NoPrivatePayload $relative $null
    if ($relative -match '\.json$' -and
        $relative -notmatch '(^|/)packages\.lock\.json$' -and
        $relative -notin @('global.json', 'tools/services/service-manifest.json') -and
        !($relative.StartsWith('tests/Fixtures/') -and $sampleHashes.ContainsKey([IO.Path]::GetFileName($relative)))) {
        throw '源码中存在未经核对的 JSON 文件，已停止出包。'
    }
    if ($relative -notmatch '\.(?:cs|csproj|json|md|ps1|sql|xaml|props|sln)$' -and
        $relative -notin @('.gitattributes', '.gitignore', 'tools/services/licenses/Moect-GPL-3.0.txt', 'LICENSE', 'THIRD-PARTY-NOTICES.txt',
            'packaging/使用说明.txt', 'packaging/SOURCE-README.txt') -and $relative -notmatch '^notices/[A-Za-z0-9.-]+\.txt$') {
        throw '源码中存在未经核对的文件类型，已停止出包。'
    }
}
git -C $repo archive --format=zip "--output=$sourceZip" $commit -- $sourcePaths
if ($LASTEXITCODE -ne 0) { throw '生成对应源码 ZIP 失败。' }

$archive = [IO.Compression.ZipFile]::Open($sourceZip, [IO.Compression.ZipArchiveMode]::Read, $zipEncoding)
try {
    foreach ($entry in $archive.Entries) {
        if (!$entry.Name) { continue }
        $stream = $entry.Open()
        try {
            Assert-NoPrivatePayload $entry.FullName $stream
        } finally {
            $stream.Dispose()
        }
    }
    foreach ($sampleName in $sampleHashes.Keys) {
        $sampleEntry = $archive.GetEntry("tests/Fixtures/$sampleName")
        if ($null -eq $sampleEntry) { throw '缺少已核对的合成消息样本。' }
        $stream = $sampleEntry.Open()
        try {
            if ((Get-FileHash -InputStream $stream -Algorithm SHA256).Hash -ne $sampleHashes[$sampleName]) {
                throw '合成消息样本哈希不符，已停止出包。'
            }
        } finally {
            $stream.Dispose()
        }
    }
} finally {
    $archive.Dispose()
}

[IO.Compression.ZipFile]::ExtractToDirectory($sourceZip, $build, $zipEncoding)
$props = [xml](Get-Content -LiteralPath (Join-Path $build 'Directory.Build.props') -Raw -Encoding UTF8)
if ($props.Project.PropertyGroup.Version -ne $Version) {
    throw "-Version $Version 与源码快照中的版本 $($props.Project.PropertyGroup.Version) 不一致。"
}
$locked = @(Get-LockedPackages $build)
Push-Location -LiteralPath $build
try {
    dotnet publish src/StudentReminder.App -c Release -r win-x64 --self-contained true "-p:RuntimeFrameworkVersion=$runtimeVersion" "-p:PathMap=$build=/_/StudentReminder" '-p:DefineConstants=STUDENTREMINDER_TRIAL' -o $payload
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败，退出码 $LASTEXITCODE。原始错误见上方。" }
} finally {
    Pop-Location
}
$afterRestore = @(Get-LockedPackages $build)
if (@(Compare-Object -ReferenceObject $locked -DifferenceObject $afterRestore -CaseSensitive).Count -ne 0) {
    throw '发布还原改变了项目锁定的包版本或内容哈希。'
}

# 由发布清单确认托管程序集和原生资产的实际来源。显式 NuGet 资产覆盖运行时同名文件。
$depsName = 'StudentReminder.App.deps.json'
$deps = Get-Content -LiteralPath (Join-Path $payload $depsName) -Raw -Encoding UTF8 | ConvertFrom-Json
$target = Get-JsonProperty $deps.targets $deps.runtimeTarget.name
$origins = @{
    'StudentReminder.App.exe' = @('桌面程序入口', '本项目 / .NET Windows x64 apphost')
    'StudentReminder.App.runtimeconfig.json' = @('运行时配置', '本项目')
    $depsName = @('运行时依赖清单', '本项目')
}
$desktopPack = "Microsoft.WindowsDesktop.App.Runtime.win-x64/$runtimeVersion"
foreach ($library in $target.PSObject.Properties) {
    if ($library.Name -notlike 'runtimepack.*') { continue }
    $origin = $library.Name.Substring('runtimepack.'.Length).Replace('/', ' ')
    foreach ($kind in @('runtime', 'native')) {
        $assets = Get-JsonProperty $library.Value $kind
        if ($null -eq $assets) { continue }
        foreach ($asset in $assets.PSObject.Properties) {
            $purpose = if ($kind -eq 'native') { '原生运行时库' } else { '.NET 自包含运行时' }
            $origins[[IO.Path]::GetFileName($asset.Name)] = @($purpose, "NuGet $origin")
        }
    }
}
if ($null -eq (Get-JsonProperty $target "runtimepack.$desktopPack") -or
    $null -eq (Get-JsonProperty $target "runtimepack.Microsoft.NETCore.App.Runtime.win-x64/$runtimeVersion")) {
    throw '发布清单中的自包含运行时版本与第三方声明不一致。'
}
foreach ($library in $target.PSObject.Properties) {
    if ($library.Name -like 'runtimepack.*') { continue }
    $origin = if ($library.Name -like 'StudentReminder.*/*') { '本项目' } else { 'NuGet ' + $library.Name.Replace('/', ' ') }
    foreach ($kind in @('runtime', 'native', 'resources')) {
        $assets = Get-JsonProperty $library.Value $kind
        if ($null -eq $assets) { continue }
        foreach ($asset in $assets.PSObject.Properties) {
            $name = [IO.Path]::GetFileName($asset.Name)
            $purpose = '托管程序集'
            if ($kind -eq 'native') { $purpose = '原生库' }
            if ($kind -eq 'resources') {
                $name = "$($asset.Value.locale)/$name"
                $purpose = '本地化资源'
            }
            $origins[$name] = @($purpose, $origin)
        }
    }
}
$desktopOrigin = 'NuGet ' + $desktopPack.Replace('/', ' ')
$backend = 'LLamaSharp.Backend.Cpu/0.30.0'
if (!($locked -match '\|LLamaSharp\.Backend\.Cpu\|0\.30\.0\|')) {
    throw 'CPU 后端的锁定版本与第三方声明不一致。'
}
foreach ($isa in @('noavx', 'avx', 'avx2', 'avx512')) {
    foreach ($dll in @('llama.dll', 'ggml.dll', 'ggml-base.dll', 'ggml-cpu.dll', 'mtmd.dll')) {
        $relative = "runtimes/win-x64/native/$isa/$dll"
        if (!(Test-Path -LiteralPath (Join-Path $payload $relative) -PathType Leaf)) {
            throw "CPU 后端缺少文件：$relative"
        }
        $origins[$relative] = @('原生 CPU 推理后端', ('NuGet ' + $backend.Replace('/', ' ')))
    }
}

$runtimes = Join-Path $payload 'runtimes'
foreach ($item in Get-ChildItem -LiteralPath $runtimes -Force) {
    if ($item.Name -ne 'win-x64') { Remove-DistItem $item }
}
foreach ($file in @(Get-DistFiles $payload)) {
    $relative = $file.FullName.Substring($payload.Length + 1).Replace('\', '/')
    if ($origins.ContainsKey($relative) -or $relative -eq 'source/StudentReminder-source.zip') { continue }
    if ($relative -match '^[A-Za-z]{2,3}(?:-[A-Za-z0-9]{2,8})?/([^/]+)\.resources\.dll$') {
        $neutral = $Matches[1] + '.dll'
        if ($origins.ContainsKey($neutral) -and $origins[$neutral][1] -eq $desktopOrigin) {
            $origins[$relative] = @('桌面运行时本地化资源', $desktopOrigin)
            continue
        }
    }
    Remove-DistItem $file
}
$exe = Get-Item -LiteralPath (Join-Path $payload 'StudentReminder.App.exe')
if ($exe.VersionInfo.FileVersion -ne $props.Project.PropertyGroup.FileVersion) {
    throw 'EXE 文件版本与源码快照不一致。'
}

Copy-Item -LiteralPath (Join-Path $build 'LICENSE'), (Join-Path $build 'THIRD-PARTY-NOTICES.txt') -Destination $payload
Copy-Item -LiteralPath (Join-Path $build 'packaging/使用说明.txt') -Destination $payload
Copy-Item -LiteralPath (Join-Path $build 'notices') -Destination $payload -Recurse
$sourceReadme = Join-Path $sourceDir 'SOURCE-README.txt'
Copy-Item -LiteralPath (Join-Path $build 'packaging/SOURCE-README.txt') -Destination $sourceReadme
[IO.File]::AppendAllText($sourceReadme, [Environment]::NewLine + "本包构建基线：$commit" + [Environment]::NewLine, $utf8)
$sampleDir = Join-Path $payload 'Samples'
$null = [IO.Directory]::CreateDirectory($sampleDir)
foreach ($sampleName in $sampleHashes.Keys) {
    Copy-Item -LiteralPath (Join-Path $build "tests/Fixtures/$sampleName") -Destination $sampleDir
    $origins["Samples/$sampleName"] = @('合成导入样本', '本项目')
}
$origins['source/StudentReminder-source.zip'] = @('对应源码归档', '本项目')
if ($IncludeModels) {
    $modelDir = Join-Path $payload 'Models'
    $null = [IO.Directory]::CreateDirectory($modelDir)
    Copy-Item -LiteralPath $model -Destination $modelDir
    $origins["Models/$modelName"] = @('本地初筛权重', '模型 / bartowski Qwen3.5-2B Q4_K_M')
}

$files = @(Get-DistFiles $payload | Sort-Object FullName)
$inventory = [Collections.Generic.List[string]]::new()
$checksums = [Collections.Generic.List[string]]::new()
$inventory.Add('相对路径 | 大小（字节） | 用途 | 来源 | SHA-256')
foreach ($file in $files) {
    $relative = $file.FullName.Substring($payload.Length + 1).Replace('\', '/')
    $stream = $file.OpenRead()
    try { Assert-NoPrivatePayload $relative $stream } finally { $stream.Dispose() }
    $purpose, $origin = if ($origins.ContainsKey($relative)) { $origins[$relative] } else { @('许可或使用说明', '文档') }
    $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($relative -eq "Models/$modelName" -and $hash -ne $modelHash) {
        throw '归档中的模型与已核实文件不一致。'
    }
    $inventory.Add("$relative | $($file.Length) | $purpose | $origin | $hash")
    $checksums.Add("$hash *$relative")
}
$filelist = Join-Path $payload 'FILELIST.txt'
[IO.File]::WriteAllLines($filelist, $inventory, $utf8Bom)
$listHash = (Get-FileHash -LiteralPath $filelist -Algorithm SHA256).Hash.ToLowerInvariant()
$checksums.Add("$listHash *FILELIST.txt")
[IO.File]::WriteAllLines((Join-Path $payload 'SHA256SUMS.txt'), $checksums, $utf8)

$archive = [IO.Compression.ZipFile]::Open($temporaryZip, [IO.Compression.ZipArchiveMode]::Create, $zipEncoding)
try {
    foreach ($file in @(Get-DistFiles $payload | Sort-Object FullName)) {
        $relative = $file.FullName.Substring($payload.Length + 1).Replace('\', '/')
        $null = [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $relative)
    }
} finally {
    $archive.Dispose()
}
[IO.File]::Move($temporaryZip, $zip)
Remove-DistItem (Get-Item -LiteralPath $build)
$bytes = (Get-Item -LiteralPath $zip).Length
$modelStatus = if ($IncludeModels) { '是' } else { '否' }
Write-Host "产物路径：$zip"
Write-Host "文件数：$($files.Count + 2)"
Write-Host "总大小：$bytes 字节"
Write-Host "是否含模型：$modelStatus"
