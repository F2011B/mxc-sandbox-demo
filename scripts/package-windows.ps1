param([ValidateSet('win-x64','win-arm64')][string]$Runtime = 'win-x64')
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
& "$PSScriptRoot/fetch-windows-runtime.ps1"
if ($LASTEXITCODE) { throw 'Runtime preparation failed.' }
$out = Join-Path $PWD "artifacts/$Runtime"
dotnet publish src/MxcSandbox.Desktop -c Release --disable-build-servers -r $Runtime --self-contained true "-p:NuGetLockFilePath=obj/publish-$Runtime.lock.json" -o $out
if ($LASTEXITCODE) { throw 'Desktop publish failed.' }
dotnet publish src/MxcSandbox.Console -c Release --disable-build-servers -r $Runtime --self-contained true "-p:NuGetLockFilePath=obj/publish-$Runtime.lock.json" -o "$out/console"
if ($LASTEXITCODE) { throw 'Console publish failed.' }
dotnet publish src/MxcSandbox.WindowsShell -c Release --disable-build-servers -r $Runtime --self-contained true "-p:NuGetLockFilePath=obj/publish-$Runtime.lock.json" -o "$out/guest-console"
if ($LASTEXITCODE) { throw 'Guest shell publish failed.' }
Write-Host "Start: $out/MxcSandbox.Desktop.exe"
