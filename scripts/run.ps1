param([switch]$Background)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$published = Join-Path $root 'artifacts/win-x64/QuickCapture.exe'
foreach ($version in @('1.1', '1.2', '1.3', '1.4', '1.5', '1.6', '1.7')) {
    $updated = Join-Path $root "artifacts/win-x64-v$version/QuickCapture.exe"
    if ((Test-Path $updated) -and (!(Test-Path $published) -or (Get-Item $updated).LastWriteTime -gt (Get-Item $published).LastWriteTime)) { $published = $updated }
}
$arguments = if ($Background) { @('--background') } else { @() }
if (Test-Path $published) {
    if ($arguments.Count) { Start-Process -FilePath $published -ArgumentList $arguments -WindowStyle Hidden }
    else { Start-Process -FilePath $published -WindowStyle Hidden }
} else {
    $executable = Join-Path $root 'src/QuickCapture/bin/Release/net8.0-windows/QuickCapture.exe'
    if (!(Test-Path $executable)) { & (Join-Path $PSScriptRoot 'build.ps1') }
    $localDotnet = Join-Path $root '.tools/dotnet/dotnet.exe'
    if (Test-Path $localDotnet) { $env:DOTNET_ROOT = Split-Path -Parent $localDotnet }
    if ($arguments.Count) { Start-Process -FilePath $executable -ArgumentList $arguments -WindowStyle Hidden }
    else { Start-Process -FilePath $executable -WindowStyle Hidden }
}
