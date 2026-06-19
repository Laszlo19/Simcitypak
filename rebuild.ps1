# Rebuilds SimCityPak.exe. Run from anywhere. See BUILD_NOTES.md for why each step exists.
$ErrorActionPreference = 'Stop'
$base    = Split-Path -Parent $MyInvocation.MyCommand.Path
$msbuild = "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe"
$nuget   = "C:\Projects\simcitypak\simcity sounds\_tools\nuget.exe"   # adjust if you moved it

# 1) Restore NuGet packages (EntityFramework + SQLite)
if (Test-Path $nuget) { & $nuget restore "$base\SimCityPak.sln" -NonInteractive }

# 2) Build the SimCityPak project (Release, x86, retargeted to .NET 4.8)
& $msbuild "$base\SimCityPak\SimCityPak.csproj" `
    /p:Configuration=Release /p:Platform=x86 `
    /p:TargetFrameworkVersion=v4.8 /p:TargetFrameworkProfile= `
    /m /v:minimal /nologo

$exe = "$base\SimCityPak\bin\Release\SimCityPak.exe"
if (Test-Path $exe) { Write-Host "`nBuilt: $exe" -ForegroundColor Green }
else { Write-Host "`nBuild did not produce SimCityPak.exe" -ForegroundColor Red }
