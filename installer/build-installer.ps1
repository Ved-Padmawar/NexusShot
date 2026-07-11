<#
.SYNOPSIS
Publishes NexusShot self-contained and compiles the Inno Setup installer.

.EXAMPLE
.\installer\build-installer.ps1                # dist\NexusShot-1.0.0.exe
.\installer\build-installer.ps1 -Version 1.2.0
#>
param([string]$Version = "1.0.0")

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path $PSScriptRoot -Parent
$publishDir = Join-Path $repoRoot "build\publish"

# Self-contained publish: the installer must not depend on the user having the .NET runtime.
# (The Windows App SDK runtime is already bundled by WindowsAppSDKSelfContained in the csproj.)
dotnet publish (Join-Path $repoRoot "src\NexusShot.App\NexusShot.App.csproj") `
    -c Release -r win-x64 -p:Platform=x64 --self-contained true `
    -p:Version=$Version -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 not found. Install it: winget install JRSoftware.InnoSetup" }

& $iscc "/DAppVersion=$Version" "/DPublishDir=$publishDir" (Join-Path $PSScriptRoot "NexusShot.iss")
if ($LASTEXITCODE -ne 0) { throw "Inno Setup compilation failed." }

Get-ChildItem (Join-Path $repoRoot "dist") -Filter "NexusShot-$Version.exe" |
    ForEach-Object { "Installer ready: $($_.FullName) ($([math]::Round($_.Length / 1MB, 1)) MB)" }
