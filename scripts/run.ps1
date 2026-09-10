param([switch]$Background)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$build = Join-Path $root 'src/QuickCapture/bin/Release/net8.0-windows/QuickCapture.exe'
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
$localDotnet = Join-Path $root '.tools/dotnet/dotnet.exe'
if (Test-Path $localDotnet) { $env:DOTNET_ROOT = Split-Path -Parent $localDotnet }
Write-Host "Starting $executable ($((Get-Item $executable).LastWriteTime))"
$arguments = if ($Background) { @('--background') } else { @() }
if ($arguments.Count) { Start-Process -FilePath $executable -ArgumentList $arguments -WindowStyle Hidden }
else { Start-Process -FilePath $executable -WindowStyle Hidden }
