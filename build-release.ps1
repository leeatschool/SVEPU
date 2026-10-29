param(
    [string]$OutputDir = "$PSScriptRoot\publish"
)

$ErrorActionPreference = "Stop"

Write-Host "=== Building SVEPU Cross-Platform Installer Templates ===" -ForegroundColor Cyan
$templatesRoot = Join-Path $OutputDir "SVEPU.Packager\installer-templates"

foreach ($rid in @("win-x64", "linux-x64", "osx-x64", "osx-arm64")) {
    Write-Host "Publishing SVEPU.Installer for $rid..." -ForegroundColor Yellow
    $ridOut = Join-Path $templatesRoot $rid
    dotnet publish "$PSScriptRoot\src\SVEPU.Installer\SVEPU.Installer.csproj" `
        -c Release `
        -r $rid `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -o $ridOut
}

Write-Host "=== Publishing SVEPU.Packager (win-x64) ===" -ForegroundColor Cyan
dotnet publish "$PSScriptRoot\src\SVEPU.Packager\SVEPU.Packager.csproj" `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o (Join-Path $OutputDir "SVEPU.Packager")

Write-Host "=== Build Complete! Output available in: $OutputDir\SVEPU.Packager ===" -ForegroundColor Green
