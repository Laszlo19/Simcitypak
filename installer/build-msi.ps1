# Builds SimCityPak.msi from the Release output.
# Requires WiX v5 (free; v6+ needs the paid OSMF EULA):
#   dotnet tool install --global wix --version 5.0.2
#
# Usage:  .\build-msi.ps1            (uses an existing Release build)
#         .\build-msi.ps1 -Rebuild   (rebuilds the app first)
param([switch]$Rebuild)

$ErrorActionPreference = 'Stop'
$here    = Split-Path -Parent $MyInvocation.MyCommand.Path
$proj    = Join-Path $here '..\SimCityPak\SimCityPak.csproj'
$pub     = Join-Path $here '..\SimCityPak\bin\Release'
$wxs     = Join-Path $here 'SimCityPak.wxs'
$msi     = Join-Path $here 'SimCityPak.msi'
$wix     = Join-Path $env:USERPROFILE '.dotnet\tools\wix.exe'
$msbuild = 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe'

if ($Rebuild) {
    & $msbuild $proj /p:Configuration=Release /p:Platform=x86 `
        /p:TargetFrameworkVersion=v4.8 /p:TargetFrameworkProfile= /m /v:minimal /nologo
}

if (-not (Test-Path "$pub\SimCityPak.exe")) { throw "Release build not found at $pub. Run with -Rebuild." }
if (-not (Test-Path $wix)) { throw "wix.exe not found. Install: dotnet tool install --global wix --version 5.0.2" }

& $wix build $wxs -d "PublishDir=$pub" -o $msi
if (Test-Path $msi) { Write-Host "Built: $msi ($([math]::Round((Get-Item $msi).Length/1MB,1)) MB)" -ForegroundColor Green }
