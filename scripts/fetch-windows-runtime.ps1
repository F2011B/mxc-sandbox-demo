param([string]$ArchivePath)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$manifest = Get-Content "$PSScriptRoot/windows-runtime-hashes.json" -Raw | ConvertFrom-Json
if (-not $ArchivePath) {
    $ArchivePath = Join-Path ([IO.Path]::GetTempPath()) 'mxc-release-binaries-1.0.1.zip'
    if (-not (Test-Path $ArchivePath)) { Invoke-WebRequest 'https://github.com/microsoft/mxc/releases/download/v1.0.1/mxc-release-binaries.zip' -OutFile $ArchivePath }
}
if ((Get-FileHash $ArchivePath -Algorithm SHA256).Hash -ne $manifest.archiveSha256) { throw 'MXC release archive SHA-256 mismatch.' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
try {
    foreach ($item in @(@('x64','win-x64'), @('arm64','win-arm64'))) {
        $folder = Join-Path $root "artifacts/native/$($item[1])"
        New-Item -ItemType Directory -Force $folder | Out-Null
        $target = Join-Path $folder 'wxc-exec.exe'
        $entry = $zip.GetEntry("$($item[0])/wxc-exec.exe")
        if (-not $entry) { throw 'Executor missing in official archive.' }
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $true)
        if ((Get-FileHash $target -Algorithm SHA256).Hash -ne $manifest.executors.($item[1])) { throw 'MXC executor SHA-256 mismatch.' }
    }
} finally { $zip.Dispose() }
