param([switch]$Publish, [switch]$Test, [switch]$IntegrationTest, [string]$PublishDirectory = 'artifacts/win-x64')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    $env:DOTNET_CLI_HOME = Join-Path $root '.tools/cli'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_NOLOGO = '1'
    $env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = '0'
    $env:NUGET_PACKAGES = Join-Path $root '.tools/nuget'
    $localDotnet = Join-Path $root '.tools/dotnet/dotnet.exe'
    $dotnet = if (Test-Path $localDotnet) { $localDotnet } else { 'dotnet' }
    & $dotnet build src/QuickCapture/QuickCapture.csproj -c Release --nologo "-p:RestoreConfigFile=$root/NuGet.Config"
    if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
    if ($Test -or $IntegrationTest) {
        & $dotnet build tests/QuickCapture.Tests/QuickCapture.Tests.csproj -c Release "-p:RestoreConfigFile=$root/NuGet.Config"
        if ($LASTEXITCODE -ne 0) { throw 'Test build failed' }
        if (Test-Path $localDotnet) { $env:DOTNET_ROOT = Split-Path -Parent $localDotnet }
        if ($IntegrationTest) { & ./tests/QuickCapture.Tests/bin/Release/net8.0-windows/QuickCapture.Tests.exe --integration }
        else { & ./tests/QuickCapture.Tests/bin/Release/net8.0-windows/QuickCapture.Tests.exe }
        if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
    }
    if ($Publish) {
        & $dotnet publish src/QuickCapture/QuickCapture.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false "-p:RestoreConfigFile=$root/NuGet.Config" -o $PublishDirectory
        if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
    }
} finally { Pop-Location }
