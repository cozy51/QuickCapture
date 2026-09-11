# -Dotnet starts the app through the shared host (dotnet QuickCapture.dll) instead
# of the exe, which is a way around an exe blocked by Smart App Control. The app
# asks for per-monitor DPI awareness in code, so both ways behave the same.
param([switch]$Background, [switch]$Dotnet)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$build = Join-Path $root 'src/QuickCapture/bin/Release/net8.0-windows/QuickCapture.exe'
$library = [IO.Path]::ChangeExtension($build, '.dll')
$localDotnet = Join-Path $root '.tools/dotnet/dotnet.exe'
if (Test-Path $localDotnet) { $env:DOTNET_ROOT = Split-Path -Parent $localDotnet }
$arguments = if ($Background) { @('--background') } else { @() }
if ($Dotnet) {
    if (!(Test-Path $library)) { & (Join-Path $PSScriptRoot 'build.ps1') }
    $host_ = if (Test-Path $localDotnet) { $localDotnet } else { 'dotnet' }
    Write-Host "Starting $library through $host_ ($((Get-Item $library).LastWriteTime))"
    Start-Process -FilePath $host_ -ArgumentList (@($library) + $arguments) -WindowStyle Hidden
    return
}
# Every exe that exists, newest first: a fresh build must win over an older
# published artifact, or a change is tested against yesterday's exe.
function Candidates {
    $found = @()
    $artifacts = Join-Path $root 'artifacts'
    if (Test-Path $artifacts) { $found += Get-ChildItem -Path $artifacts -Filter 'QuickCapture.exe' -Recurse -File | ForEach-Object { $_.FullName } }
    if (Test-Path $build) { $found += $build }
    return $found | Sort-Object { (Get-Item $_).LastWriteTime } -Descending
}
$executable = Candidates | Select-Object -First 1
if (!$executable) {
    & (Join-Path $PSScriptRoot 'build.ps1')
    $executable = Candidates | Select-Object -First 1
}
if (!$executable) { throw 'QuickCapture.exe not found' }
Write-Host "Starting $executable ($((Get-Item $executable).LastWriteTime))"
if ($arguments.Count) { Start-Process -FilePath $executable -ArgumentList $arguments -WindowStyle Hidden }
else { Start-Process -FilePath $executable -WindowStyle Hidden }
