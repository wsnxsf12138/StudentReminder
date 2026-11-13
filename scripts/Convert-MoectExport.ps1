#Requires -Version 7.5

param(
    [Parameter(Mandatory)][string]$InputPath,
    [Parameter(Mandatory)][string]$ExpectedGroup,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9._-]{1,128}$')][string]$AccountAlias
)

$ErrorActionPreference = 'Stop'
$maximumBytes = 16 * 1024 * 1024
$inputFile = Get-Item -LiteralPath $InputPath
if ($inputFile.PSIsContainer -or $inputFile.Length -gt $maximumBytes) { throw '请选择不超过 16 MiB 的 Moect JSON 导出文件。' }
try { $export = Get-Content -LiteralPath $inputFile.FullName -Raw -Encoding utf8 | ConvertFrom-Json -AsHashtable -DateKind String }
catch { throw '无法读取 Moect JSON，请检查导出格式。' }

if ($export -isnot [System.Collections.IDictionary] -or -not $export.Contains('count')) {
    throw '文件不是完整的 Moect 单会话 JSON 导出。'
}
$metadata = $export.metadata
if ($metadata.application -cne 'Moect' -or $export.messages -isnot [array] -or $export.count -ne $export.messages.Count) {
    throw '文件不是完整的 Moect 单会话 JSON 导出。'
}
$conversationId = [string]$metadata.session
if ($metadata.name -cne $ExpectedGroup -or -not $conversationId.EndsWith('@chatroom', [StringComparison]::Ordinal) -or $conversationId.Length -gt 512) {
    throw '导出文件的群名或会话类型与指定目标不符。'
}

$receivedAt = [DateTimeOffset]::UtcNow.ToString('O')
$messages = [System.Collections.Generic.List[object]]::new()
foreach ($row in $export.messages) {
    if ($row.session -cne $conversationId) { throw '文件包含另一个会话的记录。' }
    if ($row.kind -cne 'text') { continue }
    if ($row.text -isnot [string] -or [string]::IsNullOrWhiteSpace($row.text)) { throw '文本记录的正文缺失或格式错误。' }
    if ($row.create_time -isnot [long] -and $row.create_time -isnot [int]) { throw '文本记录缺少有效的 Unix 秒级发送时间。' }
    try { $sentAt = [DateTimeOffset]::FromUnixTimeSeconds($row.create_time).ToString('O') }
    catch { throw '文本记录的发送时间超出范围。' }
    $senderId = [string]$row.sender_wxid
    if ([string]::IsNullOrWhiteSpace($senderId) -or $senderId.Length -gt 512 -or $row.text.Length -gt 65536) {
        throw '文本记录的发送者标识或正文长度不符合项目导入格式。'
    }
    $messageId = [string]$row.server_id
    if ([string]::IsNullOrWhiteSpace($messageId) -or $messageId -eq '0') { $messageId = $null }
    if ($null -ne $messageId -and $messageId.Length -gt 512) { throw '消息标识超过 512 字符。' }
    $messages.Add([ordered]@{
        source = 'wechat'
        adapter = 'moect-json'
        account_id = $AccountAlias
        conversation_id = $conversationId
        conversation_name = $ExpectedGroup
        conversation_type = 'group'
        message_id = $messageId
        sender_id = $senderId
        sender_name = [string]$row.sender
        sent_at = $sentAt
        received_at = $receivedAt
        content_type = 'text'
        text = [string]$row.text
    })
    if ($messages.Count -gt 10000) { throw '文本超过 10000 条，请缩短 Moect 导出的时间范围。' }
}
if ($messages.Count -eq 0) { throw '导出文件没有可导入的文字消息。' }

$json = ConvertTo-Json -InputObject ($messages.ToArray()) -Depth 5
if ([Text.Encoding]::UTF8.GetByteCount($json) -gt $maximumBytes) { throw '转换结果超过 16 MiB，请缩短导出范围。' }
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$directory = Join-Path $projectRoot 'artifacts/moect-import'
New-Item -ItemType Directory -Path $directory -Force | Out-Null
$output = Join-Path $directory ('messages-' + [guid]::NewGuid().ToString('N') + '.json')
[IO.File]::WriteAllText($output, $json, [Text.UTF8Encoding]::new($false))
[pscustomobject]@{ ImportedTextCount = $messages.Count; SkippedCount = $export.messages.Count - $messages.Count; Path = $output }
