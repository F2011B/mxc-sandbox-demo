param(
    [string]$AppDirectory = "$PSScriptRoot/../artifacts/win-x64",
    [string]$OmpDirectory = "$env:LOCALAPPDATA/Programs/omp",
    [string]$Report = "$PSScriptRoot/../artifacts/windows-native.json",
    [switch]$StandardUser
)
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
$directory = [IO.Path]::GetFullPath($AppDirectory)
$reportPath = [IO.Path]::GetFullPath($Report)
if (-not (Test-Path (Join-Path $directory 'MxcSandbox.Desktop.exe'))) { throw 'Build the Windows package first.' }
if (-not (Test-Path (Join-Path $OmpDirectory 'omp.exe'))) { throw 'Specify -OmpDirectory containing the Windows OMP executable.' }
# Test harness only. Reuse the packaged runtime; do not install an SDK on the target.
$harness = Join-Path $directory 'MxcSandbox.WindowsChecks.exe'
if (-not (Test-Path $harness)) {
    $runtime = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'win-arm64' } else { 'win-x64' }
    $output = Join-Path $PWD "artifacts/windows-checks-$runtime"
    dotnet publish tests/MxcSandbox.WindowsChecks -c Release --disable-build-servers -r $runtime --self-contained true "-p:NuGetLockFilePath=obj/publish-$runtime.lock.json" -o $output
    if ($LASTEXITCODE) { throw 'Windows checks publish failed.' }
    foreach ($file in @('MxcSandbox.WindowsChecks.exe','MxcSandbox.WindowsChecks.dll','MxcSandbox.WindowsChecks.deps.json','MxcSandbox.WindowsChecks.runtimeconfig.json')) {
        Copy-Item (Join-Path $output $file) $directory
    }
}
New-Item -ItemType Directory -Force (Split-Path $reportPath -Parent) | Out-Null
$previous = $env:MXC_CHECK_OMP_DIRECTORY
try {
    $env:MXC_CHECK_OMP_DIRECTORY = [IO.Path]::GetFullPath($OmpDirectory)
    $arguments = @($reportPath)
    # SSH/service Session 0: same-account filtered Medium token and a NEW private
    # test desktop. No existing desktop ACL, policy, registry or firewall changes.
    if ($StandardUser) { $arguments += '--standard-user' }
    & $harness @arguments
    if ($LASTEXITCODE) { throw "Native Windows checks failed: $reportPath" }
    $result = Get-Content -Raw -Encoding UTF8 $reportPath | ConvertFrom-Json
    if (-not $result.passed) { throw "Native Windows checks incomplete: $reportPath" }
    $result.checks | Select-Object name, passed, elapsedMs | Format-Table
    Write-Host "Evidence: $reportPath"
}
finally { $env:MXC_CHECK_OMP_DIRECTORY = $previous }
