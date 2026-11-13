$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$manifest = Get-Content -LiteralPath (Join-Path $projectRoot 'tools/services/service-manifest.json') -Raw | ConvertFrom-Json
foreach ($service in $manifest.services) {
    $executablePath = [IO.Path]::GetFullPath((Join-Path $projectRoot $service.executable))
    if (-not $executablePath.StartsWith($projectRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid executable path.' }
    $exists = Test-Path -LiteralPath $executablePath -PathType Leaf
    $hashMatches = if ($exists -and $service.PSObject.Properties.Name -contains 'executable_sha256') {
        (Get-FileHash -LiteralPath $executablePath -Algorithm SHA256).Hash -eq $service.executable_sha256
    } else { $null }
    [pscustomobject]@{ Service = $service.id; Version = $service.version; Installed = $exists; HashMatches = $hashMatches; Executable = $executablePath }
}
