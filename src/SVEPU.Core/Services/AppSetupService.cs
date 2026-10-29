using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace SVEPU.Core.Services;

public sealed class AppSetupService
{
    public const string AppDisplayName = "SVEPU - Stardew Valley Easy Port Utility";
    public const string AppVersion = "1.0.0";
    public const string AppPublisher = "Aaron";
    private const string WindowsUninstallRegPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\SVEPU";

    public static string GetDefaultInstallDirectory()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, "Programs", "SVEPU");
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, "Applications", "SVEPU");
        }

        string linuxHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(linuxHome, ".local", "share", "SVEPU");
    }

    public static string GetInstalledPackagerExecutablePath(string installDir)
    {
        string exeName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "SVEPU.Packager.exe"
            : "SVEPU.Packager";
        return Path.Combine(installDir, exeName);
    }

    public static bool IsAlreadyInstalled(string installDir)
    {
        return !string.IsNullOrWhiteSpace(installDir) &&
               File.Exists(GetInstalledPackagerExecutablePath(installDir));
    }

    public async Task<(bool Success, string Message)> InstallAsync(
        Stream? payloadZipStream,
        string installDir,
        bool createDesktopShortcut,
        bool createMenuShortcut,
        bool registerSystemUninstaller,
        IProgress<(double Percent, string Status)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(installDir))
        {
            return (false, "Please specify a valid installation directory.");
        }

        try
        {
            Directory.CreateDirectory(installDir);

            if (payloadZipStream != null)
            {
                progress?.Report((10, "Extracting SVEPU application files..."));
                await Task.Run(() =>
                {
                    using var archive = new ZipArchive(payloadZipStream, ZipArchiveMode.Read, leaveOpen: true);
                    int total = Math.Max(1, archive.Entries.Count);
                    int index = 0;

                    foreach (var entry in archive.Entries)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        index++;

                        if (string.IsNullOrEmpty(entry.Name))
                        {
                            continue;
                        }

                        string relativePath = entry.FullName.Replace('\\', '/');
                        string destFile = Path.GetFullPath(Path.Combine(installDir, relativePath.Replace('/', Path.DirectorySeparatorChar)));
                        if (!destFile.StartsWith(Path.GetFullPath(installDir), StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        string? parent = Path.GetDirectoryName(destFile);
                        if (!string.IsNullOrWhiteSpace(parent))
                        {
                            Directory.CreateDirectory(parent);
                        }

                        entry.ExtractToFile(destFile, overwrite: true);

                        double pct = 10.0 + (index / (double)total) * 65.0;
                        progress?.Report((pct, $"Installing: {entry.Name}"));
                    }
                }, cancellationToken);
            }
            else
            {
                // Fallback when running directly from development build without embedded zip
                progress?.Report((15, "Copying local build files into installation directory..."));
                string? devSource = FindLocalDevPackagerBuild();
                if (string.IsNullOrWhiteSpace(devSource) || !Directory.Exists(devSource))
                {
                    return (false, "Could not locate embedded SVEPU payload or local build files.");
                }
                CopyDirectoryRecursive(devSource, installDir);
            }

            // Ensure executable permissions on Linux and macOS
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                progress?.Report((78, "Setting executable permissions..."));
                SetUnixExecutablePermissions(installDir);
            }

            // Copy Setup binary as Uninstaller inside installDir
            string? currentProcessPath = Environment.ProcessPath;
            string uninstallerName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? "Uninstall-SVEPU.exe"
                : "Uninstall-SVEPU";
            string targetUninstallerPath = Path.Combine(installDir, uninstallerName);

            if (!string.IsNullOrWhiteSpace(currentProcessPath) &&
                File.Exists(currentProcessPath) &&
                !Path.GetFileName(currentProcessPath).StartsWith("dotnet", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    if (!string.Equals(Path.GetFullPath(currentProcessPath), Path.GetFullPath(targetUninstallerPath), StringComparison.OrdinalIgnoreCase))
                    {
                        File.Copy(currentProcessPath, targetUninstallerPath, overwrite: true);
                        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                        {
                            TryChmodExecutable(targetUninstallerPath);
                        }
                    }
                }
                catch
                {
                    // Ignore uninstaller self-copy failure if locked
                }
            }

            if (createDesktopShortcut || createMenuShortcut)
            {
                progress?.Report((85, "Creating application shortcuts..."));
                await CreateShortcutsAsync(installDir, createDesktopShortcut, createMenuShortcut);
            }

            if (registerSystemUninstaller && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                progress?.Report((93, "Registering SVEPU in Windows Installed Apps..."));
                RegisterWindowsUninstaller(installDir, targetUninstallerPath);
            }

            progress?.Report((100, "SVEPU installation complete!"));
            return (true, $"Successfully installed SVEPU to {installDir}");
        }
        catch (Exception ex)
        {
            return (false, $"Installation failed: {ex.Message}");
        }
    }

    public async Task<(bool Success, string Message)> UninstallAsync(
        string installDir,
        IProgress<(double Percent, string Status)>? progress = null)
    {
        try
        {
            progress?.Report((20, "Removing shortcuts..."));
            RemoveShortcuts();

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                progress?.Report((45, "Removing Windows Installed Apps registry entry..."));
                UnregisterWindowsUninstaller();
            }

            if (!string.IsNullOrWhiteSpace(installDir) && Directory.Exists(installDir))
            {
                progress?.Report((75, $"Removing files in {installDir}..."));
                await Task.Run(() =>
                {
                    foreach (var file in Directory.GetFiles(installDir, "*", SearchOption.AllDirectories))
                    {
                        try
                        {
                            File.Delete(file);
                        }
                        catch
                        {
                            // Ignore currently running uninstaller binary if locked on Windows
                        }
                    }

                    try
                    {
                        Directory.Delete(installDir, recursive: true);
                    }
                    catch
                    {
                        // Schedule self-deletion after process exit on Windows
                        ScheduleDirectoryCleanupOnExit(installDir);
                    }
                });
            }

            progress?.Report((100, "SVEPU has been uninstalled."));
            return (true, "SVEPU was cleanly uninstalled from this device.");
        }
        catch (Exception ex)
        {
            return (false, $"Uninstall error: {ex.Message}");
        }
    }

    private static async Task CreateShortcutsAsync(string installDir, bool desktop, bool menu)
    {
        string targetExe = GetInstalledPackagerExecutablePath(installDir);
        string iconIco = Path.Combine(installDir, "assets", "app-icon.ico");
        string iconPng = Path.Combine(installDir, "assets", "app-icon.png");

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var shortcutPaths = new List<string>();
            if (desktop)
            {
                string desktopDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                shortcutPaths.Add(Path.Combine(desktopDir, "SVEPU - Stardew Valley Easy Port Utility.lnk"));
            }

            if (menu)
            {
                string startMenuDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                    "Programs",
                    "SVEPU");
                Directory.CreateDirectory(startMenuDir);
                shortcutPaths.Add(Path.Combine(startMenuDir, "SVEPU - Stardew Valley Easy Port Utility.lnk"));
            }

            foreach (var lnkPath in shortcutPaths)
            {
                await CreateWindowsLnkAsync(lnkPath, targetExe, installDir, File.Exists(iconIco) ? iconIco : targetExe);
            }
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            string script =
                "#!/usr/bin/env sh\n" +
                $"exec \"{targetExe}\"\n";

            if (desktop)
            {
                string desktopFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "SVEPU.command");
                await File.WriteAllBytesAsync(desktopFile, Encoding.UTF8.GetBytes(script));
                TryChmodExecutable(desktopFile);
            }

            if (menu)
            {
                string appsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications");
                Directory.CreateDirectory(appsDir);
                string appCmd = Path.Combine(appsDir, "SVEPU.command");
                await File.WriteAllBytesAsync(appCmd, Encoding.UTF8.GetBytes(script));
                TryChmodExecutable(appCmd);
            }
        }
        else
        {
            string desktopEntry =
                "[Desktop Entry]\n" +
                "Type=Application\n" +
                $"Name={AppDisplayName}\n" +
                "Comment=Package and share your Stardew Valley mods across any device.\n" +
                $"Exec=\"{targetExe}\"\n" +
                $"Path={installDir}\n" +
                $"Icon={iconPng}\n" +
                "Terminal=false\n" +
                "Categories=Game;Utility;\n";

            if (desktop)
            {
                string desktopDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                if (Directory.Exists(desktopDir))
                {
                    string desktopFile = Path.Combine(desktopDir, "SVEPU.desktop");
                    await File.WriteAllBytesAsync(desktopFile, Encoding.UTF8.GetBytes(desktopEntry));
                    TryChmodExecutable(desktopFile);
                }
            }

            if (menu)
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string appsDir = Path.Combine(home, ".local", "share", "applications");
                Directory.CreateDirectory(appsDir);
                string menuFile = Path.Combine(appsDir, "svepu.desktop");
                await File.WriteAllBytesAsync(menuFile, Encoding.UTF8.GetBytes(desktopEntry));
                TryChmodExecutable(menuFile);
            }
        }
    }

    private static void RemoveShortcuts()
    {
        try
        {
            string desktopDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string winLnk = Path.Combine(desktopDir, "SVEPU - Stardew Valley Easy Port Utility.lnk");
            if (File.Exists(winLnk)) File.Delete(winLnk);

            string macCmd = Path.Combine(desktopDir, "SVEPU.command");
            if (File.Exists(macCmd)) File.Delete(macCmd);

            string linuxDesktop = Path.Combine(desktopDir, "SVEPU.desktop");
            if (File.Exists(linuxDesktop)) File.Delete(linuxDesktop);

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                string startMenuFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                    "Programs",
                    "SVEPU");
                if (Directory.Exists(startMenuFolder))
                {
                    Directory.Delete(startMenuFolder, recursive: true);
                }
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                string macAppCmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications", "SVEPU.command");
                if (File.Exists(macAppCmd)) File.Delete(macAppCmd);
            }
            else
            {
                string linuxMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "applications", "svepu.desktop");
                if (File.Exists(linuxMenu)) File.Delete(linuxMenu);
            }
        }
        catch
        {
            // Ignore shortcut cleanup errors
        }
    }

    private static async Task CreateWindowsLnkAsync(string lnkPath, string targetExe, string workingDir, string iconPath)
    {
        try
        {
            string escapedLnk = lnkPath.Replace("'", "''");
            string escapedTarget = targetExe.Replace("'", "''");
            string escapedWorkDir = workingDir.Replace("'", "''");
            string escapedIcon = iconPath.Replace("'", "''");

            string psCommand =
                $"$ws = New-Object -ComObject WScript.Shell; " +
                $"$s = $ws.CreateShortcut('{escapedLnk}'); " +
                $"$s.TargetPath = '{escapedTarget}'; " +
                $"$s.WorkingDirectory = '{escapedWorkDir}'; " +
                $"$s.IconLocation = '{escapedIcon}'; " +
                $"$s.Description = 'Package and share your Stardew Valley mods across any device.'; " +
                $"$s.Save()";

            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = $"-NoProfile -NonInteractive -WindowStyle Hidden -Command \"{psCommand}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            });

            if (proc != null)
            {
                await proc.WaitForExitAsync();
            }
        }
        catch
        {
            // Ignore shortcut creation failure
        }
    }

    private static void RegisterWindowsUninstaller(string installDir, string uninstallerExe)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(WindowsUninstallRegPath);
            if (key != null)
            {
                string iconPath = Path.Combine(installDir, "assets", "app-icon.ico");
                key.SetValue("DisplayName", AppDisplayName);
                key.SetValue("DisplayVersion", AppVersion);
                key.SetValue("Publisher", AppPublisher);
                key.SetValue("InstallLocation", installDir);
                key.SetValue("DisplayIcon", File.Exists(iconPath) ? iconPath : GetInstalledPackagerExecutablePath(installDir));
                key.SetValue("UninstallString", $"\"{uninstallerExe}\" --uninstall --install-dir \"{installDir}\"");
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }
        }
        catch
        {
            // Ignore registry errors
        }
    }

    private static void UnregisterWindowsUninstaller()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(WindowsUninstallRegPath, throwOnMissingSubKey: false);
        }
        catch
        {
            // Ignore registry errors
        }
    }

    private static void SetUnixExecutablePermissions(string installDir)
    {
        foreach (var file in Directory.GetFiles(installDir, "*", SearchOption.AllDirectories))
        {
            string name = Path.GetFileName(file);
            if (name.Equals("SVEPU.Packager", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("SVEPU.Installer", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Uninstall-SVEPU", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Install-Linux", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Install-macOS.command", StringComparison.OrdinalIgnoreCase))
            {
                TryChmodExecutable(file);
            }
        }
    }

    private static void TryChmodExecutable(string filePath)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(
                filePath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        catch
        {
            // Ignore chmod errors
        }
    }

    private static void ScheduleDirectoryCleanupOnExit(string installDir)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/C ping 127.0.0.1 -n 3 > nul & rmdir /s /q \"{installDir}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            });
        }
        catch
        {
            // Ignore background cleanup errors
        }
    }

    private static string? FindLocalDevPackagerBuild()
    {
        string baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "SVEPU.Packager", "bin", "Release", "net8.0")),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "SVEPU.Packager", "bin", "Debug", "net8.0"))
        };
        return candidates.FirstOrDefault(Directory.Exists);
    }

    private static void CopyDirectoryRecursive(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            File.Copy(file, Path.Combine(targetDir, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var subDir in Directory.GetDirectories(sourceDir))
        {
            CopyDirectoryRecursive(subDir, Path.Combine(targetDir, Path.GetFileName(subDir)));
        }
    }
}
