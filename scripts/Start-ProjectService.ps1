param([Parameter(Mandatory)][ValidateSet('moect')][string]$Service)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$manifest = Get-Content -LiteralPath (Join-Path $projectRoot 'tools/services/service-manifest.json') -Raw | ConvertFrom-Json
$item = $manifest.services | Where-Object id -eq $Service | Select-Object -First 1
$executablePath = [IO.Path]::GetFullPath((Join-Path $projectRoot $item.executable))
if (-not $executablePath.StartsWith($projectRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or -not (Test-Path -LiteralPath $executablePath)) { throw 'Service executable is not installed in the project.' }
$process = Start-Process -FilePath $executablePath -WorkingDirectory (Split-Path $executablePath) -WindowStyle Normal -PassThru
Write-Output "Started $Service (PID $($process.Id)); account configuration remains a user action."
