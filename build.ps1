<#
.SYNOPSIS
    The build. One command, two modes.

.DESCRIPTION
    .\build.ps1              Debug build, then run it.
    .\build.ps1 dev          Same.
    .\build.ps1 release      Native AOT single exe -> dist\NexusShot.exe
    .\build.ps1 installer    Release, then the Inno installer -> dist\NexusShot-<version>.exe
    .\build.ps1 test         Unit tests, then a headless render + drag-timing check.

    There is one project and one output directory. The old build had several places an exe could
    appear and several commands that had to be run in the right order; this replaces all of that.

.NOTES
    Native AOT shells out to vswhere.exe to find the MSVC linker. When it cannot, it bakes the
    "not recognized" error text into the linker command line and fails with a message that points
    nowhere near the cause - so the VS Installer directory goes on PATH before publishing.
#>
[CmdletBinding()]
param(
    [ValidateSet('dev', 'release', 'installer', 'test')]
    [string]$Mode = 'dev',

    # Defaults to the csproj's <Version>. Left unset so there is one declared version, not a copy
    # here that silently disagrees with what the exe is stamped with.
    [string]$Version
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = $PSScriptRoot
$project = Join-Path $root 'src\NexusShot\NexusShot.csproj'
$tests = Join-Path $root 'src\NexusShot.Tests\NexusShot.Tests.csproj'
$dist = Join-Path $root 'dist'

if (-not $Version) {
    $Version = ([xml](Get-Content $project)).Project.PropertyGroup.Version |
        Where-Object { $_ } | Select-Object -First 1
    if (-not $Version) { throw "No <Version> found in $project." }
}

function Assert-LastExitCode([string]$what) {
    if ($LASTEXITCODE -ne 0) { throw "$what failed (exit $LASTEXITCODE)." }
}

# Asked of MSBuild rather than hardcoded or searched for: the path embeds the target framework, and
# an older framework's output left beside the current one would otherwise run stale code.
function Get-BuiltExe([string]$configuration) {
    $target = dotnet msbuild $project -getProperty:TargetPath "-p:Configuration=$configuration"
    Assert-LastExitCode 'Output path lookup'

    $exe = [IO.Path]::ChangeExtension($target.Trim(), '.exe')
    if (-not (Test-Path $exe)) { throw "No $exe. Did the build run?" }
    return $exe
}

# Native AOT needs the MSVC toolchain, which it locates through vswhere.
function Add-VsToolsToPath {
    $installer = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
    if (-not (Test-Path (Join-Path $installer 'vswhere.exe'))) {
        throw "vswhere.exe not found. Install the Visual Studio C++ build tools: winget install Microsoft.VisualStudio.2022.BuildTools"
    }
    if ($env:PATH -notlike "*$installer*") { $env:PATH = "$installer;$env:PATH" }
}

switch ($Mode) {

    'dev' {
        dotnet build $project -c Debug
        Assert-LastExitCode 'Build'

        $exe = Get-BuiltExe 'Debug'
        Write-Host "`nRunning $exe`n" -ForegroundColor Cyan
        & $exe
    }

    'test' {
        dotnet build $project -c Release
        Assert-LastExitCode 'Build'

        # Unit tests first: they cover the document, geometry and history without a GPU, so a
        # failure here is faster to read than a bad pixel in the render check below.
        dotnet test --project $tests -c Debug
        Assert-LastExitCode 'Unit tests'

        # A generated test image, so the check needs nothing checked in.
        Add-Type -AssemblyName System.Drawing
        $sample = Join-Path $env:TEMP 'nexusshot-build-test.png'
        $bitmap = New-Object System.Drawing.Bitmap 1400, 900
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        $graphics.Clear([System.Drawing.Color]::White)
        $font = New-Object System.Drawing.Font('Consolas', 13)
        for ($i = 0; $i -lt 20; $i++) {
            $graphics.DrawString(
                "Line $i - the quick brown fox jumps over the lazy dog",
                $font, [System.Drawing.Brushes]::DimGray, 70, (80 + $i * 38))
        }
        $font.Dispose()
        $graphics.Dispose()
        $bitmap.Save($sample, [System.Drawing.Imaging.ImageFormat]::Png)
        $bitmap.Dispose()

        $exe = Get-BuiltExe 'Release'
        $out = Join-Path $env:TEMP 'nexusshot-test-out.txt'
        $err = Join-Path $env:TEMP 'nexusshot-test-err.txt'

        $process = Start-Process $exe -ArgumentList '--render-test', "`"$sample`"" `
            -PassThru -Wait -WindowStyle Hidden `
            -RedirectStandardOutput $out -RedirectStandardError $err

        Get-Content $out -ErrorAction SilentlyContinue
        if ($process.ExitCode -ne 0) {
            Get-Content $err -ErrorAction SilentlyContinue | Select-Object -First 15
            throw "Render test failed (exit $($process.ExitCode))."
        }
        Write-Host "`nRender test passed." -ForegroundColor Green
    }

    { $_ -in 'release', 'installer' } {
        Add-VsToolsToPath
        New-Item -ItemType Directory -Force $dist | Out-Null

        Write-Host "Publishing Native AOT..." -ForegroundColor Cyan
        dotnet publish $project -c Release -r win-x64 `
            -p:Version=$Version -o $dist
        Assert-LastExitCode 'Publish'

        # The PDB is for us, not for users: it is bigger than the exe.
        Remove-Item (Join-Path $dist '*.pdb') -Force -ErrorAction SilentlyContinue

        $exe = Get-Item (Join-Path $dist 'NexusShot.exe')
        Write-Host ("`nNexusShot.exe  {0:N1} MB" -f ($exe.Length / 1MB)) -ForegroundColor Green

        if ($Mode -eq 'installer') {
            $iscc = @(
                "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
                "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
                "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
            ) | Where-Object { Test-Path $_ } | Select-Object -First 1

            if (-not $iscc) {
                throw "Inno Setup 6 not found. Install it: winget install JRSoftware.InnoSetup"
            }

            & $iscc "/DAppVersion=$Version" "/DPublishDir=$dist" (Join-Path $root 'installer\NexusShot.iss')
            Assert-LastExitCode 'Inno Setup'

            $installer = Get-Item (Join-Path $dist "NexusShot-$Version.exe")
            Write-Host ("Installer      {0:N1} MB  ->  {1}" -f ($installer.Length / 1MB), $installer.FullName) `
                -ForegroundColor Green
        }
    }
}
