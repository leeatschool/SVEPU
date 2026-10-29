# SVEPU — Stardew Valley Easy Port Utility

**SVEPU** is a cross-platform mod packaging and installation wizard suite for **Stardew Valley** built with C# / .NET 8 and Avalonia UI.

## Features

### 1. SVEPU Packager (`src/SVEPU.Packager`)
- **Flexible Mod Input**:
  - Drag and drop individual mod folders, an entire `Mods` directory, or `.zip` mod archives directly into the window.
  - One-click **Auto-Detect My Stardew Mods Folder** locates your local Steam, GOG Galaxy, or Xbox App / Game Pass `Mods` directory automatically.
  - Parses SMAPI `manifest.json` files (including files with comments and trailing commas), displays a checkable mod list with live missing-dependency warnings, and automatically skips built-in SMAPI folders (`ConsoleCommands`, `SaveBackup`, `.smapi`).
  - Toggle whether to include or strip `config.json` files across packaged mods.
- **Custom Metadata & Icon**:
  - Customize Pack Name, Author, Version, Description/Release Notes, and a Custom Package Icon image (injected into the generated Windows `.exe` icon resource and displayed inside the Installer Wizard).
- **Single Shared Payload, Cross-Platform Output**:
  - Outputs a ready-to-share `.zip` archive (and optional unzipped folder) containing root launchers for Windows (`Install-Windows.exe` / `.bat`), Linux (`Install-Linux`), and macOS (`Install-macOS.command`) sharing a single compressed `payload/mods.svepu` archive.

### 2. SVEPU Installation Wizard (`src/SVEPU.Installer`)
- **Recommended Install vs. Custom Install**:
  - **Recommended Install**: Streamlined setup that detects your Stardew Valley installation, checks for SMAPI, extracts all mods (overwriting outdated conflicts while keeping other mods intact), and configures Steam Launch Options automatically (closing and relaunching Steam so changes to `localconfig.vdf` persist).
  - **Custom Install**: Allows toggling SMAPI installation, Steam Launch Options, automatic Steam restart, Conflict Resolution mode (`Overwrite conflicting mods`, `Backup & Clean existing Mods folder`, or `Skip existing mods`), and individual mods from the package.
- **Automatic SMAPI Download & Silent Install**:
  - Checks if SMAPI is installed in the target Stardew Valley directory. If missing, asks:
    > *"SMAPI wasn't detected, this is a required component for Stardew Valley Modding. Would you like me to download it for you?"*
  - Downloads the latest official release directly from [Pathoschild/SMAPI](https://github.com/Pathoschild/SMAPI/releases/) with a progress bar and installs it silently.

## Assets (`assets/`)
Place custom visual assets in the `assets/` folder:
- `assets/app-icon.png` (and `assets/app-icon.ico`): Main application and default installer icon.
- Optional service icons loaded automatically by the Installer Wizard if placed in `assets/`:
  - `assets/steam.png`
  - `assets/gog.png`
  - `assets/xbox.png`
  - `assets/folder.png`

## Building & Running

### Run in Development
```powershell
dotnet build SVEPU.sln
dotnet test SVEPU.sln
dotnet run --project src/SVEPU.Packager
```

### Build Self-Contained Cross-Platform Release
```powershell
.\build-release.ps1
```
