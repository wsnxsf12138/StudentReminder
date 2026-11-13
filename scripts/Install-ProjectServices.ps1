param([ValidateSet('all','moect')][string]$Service = 'all')
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$manifest = Get-Content -LiteralPath (Join-Path $projectRoot 'tools/services/service-manifest.json') -Raw | ConvertFrom-Json

function ProjectPath([string]$Relative) {
    $path = [IO.Path]::GetFullPath((Join-Path $projectRoot $Relative))
    if (-not $path.StartsWith($projectRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Path escapes project root.' }
    return $path
}

function ExpandValidatedArchive([string]$Archive, [string]$Destination, [string]$Sha256) {
    if ((Get-FileHash -LiteralPath $Archive -Algorithm SHA256).Hash -ne $Sha256) { throw 'Package SHA-256 does not match the pinned manifest.' }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($Archive)
    try {
        foreach ($entry in $zip.Entries) {
            $entryPath = [IO.Path]::GetFullPath((Join-Path $Destination $entry.FullName))
            if (-not $entryPath.StartsWith($Destination + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Archive path traversal rejected.' }
        }
    } finally { $zip.Dispose() }
    Expand-Archive -LiteralPath $Archive -DestinationPath $Destination -Force
}

foreach ($item in $manifest.services | Where-Object { $Service -eq 'all' -or $_.id -eq $Service }) {
    $destination = ProjectPath $item.install_directory
    $archivePath = ProjectPath $item.package_path
    New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($archivePath)) | Out-Null
    if (-not (Test-Path -LiteralPath $archivePath)) {
        Invoke-WebRequest -Uri $item.package_url -OutFile $archivePath
    }
    ExpandValidatedArchive $archivePath $destination $item.package_sha256
    Write-Output "Installed $($item.id) $($item.version) in $destination"
}
