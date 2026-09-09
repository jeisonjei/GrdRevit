param(
    [switch]$Publish
)

$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot
$SolPath  = Join-Path $RepoRoot 'GrdRevit.sln'
$DistRoot = Join-Path $RepoRoot 'dist'

# GUIDs from addin templates
$Id2024 = '86C8044E-E80E-438B-9C23-239B96D1E94B'
$Id2026 = '8E3F35CA-4985-4D7F-8F9F-FFEF76E7DB9E'

Write-Host "=== Build $SolPath ==="
dotnet build $SolPath -c Release --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }

if (-not $Publish) {
    Write-Host "Built. To publish to Revit folders run: .\build\build.ps1 -Publish"
    exit 0
}

function New-VersionDir([string]$version, [string]$tfm) {
    $verDir = Join-Path $DistRoot "Revit$version"
    $addonsDir = Join-Path $verDir 'Addins'
    New-Item -ItemType Directory -Force -Path $verDir    | Out-Null
    New-Item -ItemType Directory -Force -Path $addonsDir | Out-Null

    $src = Join-Path $RepoRoot "src\Grd.Revit\bin\Release\$tfm"
    Copy-Item (Join-Path $src 'GrdRevit.dll')          $verDir -Force
    Copy-Item (Join-Path $src 'GrdRevit.Core.dll')     $verDir -Force
    # net48 flavour shares System.Text.Json dependencies in the same folder
    Get-ChildItem $src | Where-Object { $_.Name -match 'System\.Text\.Json|System\.Memory|System\.Buffers|Microsoft\.Bcl|System\.Runtime\.CompilerServices' } |
        Copy-Item -Destination $verDir -Force

    $manifestName = "$version.addin"
    $template = Join-Path $RepoRoot "addins\GrdRevit.$version.addin.template"
    $content = [System.IO.File]::ReadAllText($template, [System.Text.Encoding]::UTF8)
    $assemblyPath = Join-Path $verDir 'GrdRevit.dll'
    $content = $content.Replace('{ASSEMBLY_PATH}', $assemblyPath)

    if ($version -like '2024*') { $content = $content.Replace('{ADDIN_ID_2024}', $Id2024) }
    if ($version -like '2026*') { $content = $content.Replace('{ADDIN_ID_2026}', $Id2026) }

    $manifestPath = Join-Path $addonsDir $manifestName
    if ($version -like '2024*') {
        [System.IO.File]::WriteAllText($manifestPath, $content, [System.Text.Encoding]::Unicode)
    } else {
        [System.IO.File]::WriteAllText($manifestPath, $content, [System.Text.Encoding]::UTF8)
    }
    Write-Host "Done: $manifestPath"
}

New-VersionDir '2024' 'net48'
New-VersionDir '2026' 'net8.0-windows'

# Self-contained installer (net48 WinForms, bundles plugin DLLs as embedded resources)
Write-Host ""
Write-Host "=== Publish GrdInstaller.exe ==="
$instOut = Join-Path $RepoRoot 'tools\Grd.Installer\bin\Release\net48'
dotnet publish (Join-Path $RepoRoot 'tools\Grd.Installer\Grd.Installer.csproj') -c Release -o $instOut --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Installer publish failed' }
Copy-Item (Join-Path $instOut 'GrdInstaller.exe') (Join-Path $DistRoot 'GrdInstaller.exe') -Force
Write-Host "Done: $DistRoot\GrdInstaller.exe"

Write-Host ""
Write-Host "=== Publish complete. Folders:"
Get-ChildItem $DistRoot -Directory | ForEach-Object { Write-Host "   $($_.FullName)" }
Write-Host ""
Write-Host "Quick install: run dist\GrdInstaller.exe (single self-contained installer)"
Write-Host "Manual install: put dist\Revit<ver>\Addins\*.addin into %APPDATA%\Autodesk\Revit\Addins\<ver>\"
Write-Host "Keep DLLs in dist\Revit<ver>\ (paths in .addin are absolute)."