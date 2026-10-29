param(
    [string]$OutputDir = "$PSScriptRoot\publish"
)

$ErrorActionPreference = "Stop"

$templatesStaging = Join-Path $OutputDir "staging\installer-templates"
$setupPayloadDir = Join-Path $PSScriptRoot "src\SVEPU.Setup\payload"
$setupPayloadZip = Join-Path $setupPayloadDir "SVEPU-AppPayload.zip"
$installersOutDir = Join-Path $OutputDir "Installers"

New-Item -ItemType Directory -Path $templatesStaging -Force | Out-Null
New-Item -ItemType Directory -Path $setupPayloadDir -Force | Out-Null
New-Item -ItemType Directory -Path $installersOutDir -Force | Out-Null

Write-Host "=== 1. Building Cross-Platform Mod Installer Templates ===" -ForegroundColor Cyan
foreach ($rid in @("win-x64", "linux-x64", "osx-x64", "osx-arm64")) {
    Write-Host "Publishing SVEPU.Installer template for $rid..." -ForegroundColor Yellow
    $ridOut = Join-Path $templatesStaging $rid
    dotnet publish "$PSScriptRoot\src\SVEPU.Installer\SVEPU.Installer.csproj" `
        -c Release `
        -r $rid `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -o $ridOut
}

$targets = @(
    @{ Rid = "win-x64";   SetupBinary = "SVEPU.Setup.exe"; OutName = "SVEPU-Setup-Windows.exe" },
    @{ Rid = "linux-x64"; SetupBinary = "SVEPU.Setup";     OutName = "SVEPU-Setup-Linux" },
    @{ Rid = "osx-x64";   SetupBinary = "SVEPU.Setup";     OutName = "SVEPU-Setup-macOS-x64" },
    @{ Rid = "osx-arm64"; SetupBinary = "SVEPU.Setup";     OutName = "SVEPU-Setup-macOS-arm64" }
)

Write-Host "=== 2. Building Standalone Cross-Platform SVEPU Setup Installers ===" -ForegroundColor Cyan
foreach ($target in $targets) {
    $rid = $target.Rid
    Write-Host "Packaging SVEPU.Packager payload for $rid..." -ForegroundColor Yellow

    $packagerStage = Join-Path $OutputDir "staging\packager-$rid"
    if (Test-Path $packagerStage) { Remove-Item $packagerStage -Recurse -Force }

    dotnet publish "$PSScriptRoot\src\SVEPU.Packager\SVEPU.Packager.csproj" `
        -c Release `
        -r $rid `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -o $packagerStage

    Copy-Item -Path $templatesStaging -Destination (Join-Path $packagerStage "installer-templates") -Recurse -Force

    if (Test-Path $setupPayloadZip) { Remove-Item $setupPayloadZip -Force }
    Compress-Archive -Path "$packagerStage\*" -DestinationPath $setupPayloadZip -Force

    Write-Host "Compiling single-file Setup Wizard ($($target.OutName))..." -ForegroundColor Yellow
    $setupStage = Join-Path $OutputDir "staging\setup-$rid"
    if (Test-Path $setupStage) { Remove-Item $setupStage -Recurse -Force }

    dotnet publish "$PSScriptRoot\src\SVEPU.Setup\SVEPU.Setup.csproj" `
        -c Release `
        -r $rid `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -o $setupStage

    Copy-Item -Path (Join-Path $setupStage $target.SetupBinary) -Destination (Join-Path $installersOutDir $target.OutName) -Force
}

if (Test-Path $setupPayloadZip) { Remove-Item $setupPayloadZip -Force }

Write-Host "=== Build Complete! Standalone Setup Installers created in: $installersOutDir ===" -ForegroundColor Green
Get-ChildItem $installersOutDir | Select-Object Name, Length
