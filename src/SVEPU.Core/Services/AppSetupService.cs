using System.Diagnostics;
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

    public static bool IsSafeUninstallDirectory(string installDir)
    {
        if (string.IsNullOrWhiteSpace(installDir))
        {
            return false;
        }

        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installDir));
        string? root = Path.GetPathRoot(full);
        if (!string.IsNullOrWhiteSpace(root) &&
            string.Equals(full, Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var protectedFolders = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.System)
        };

        foreach (var pf in protectedFolders.Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            string normalizedProtected = Path.TrimEndingDirectorySeparator(Path.GetFullPath(pf));
            if (string.Equals(full, normalizedProtected, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return IsAlreadyInstalled(full);
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
            string fullInstallDir = Path.GetFullPath(installDir);
            Directory.CreateDirectory(fullInstallDir);

            if (payloadZipStream != null)
            {
                progress?.Report((10, "Extracting SVEPU application files..."));
                await Task.Run(() =>
                {
                    SafeArchiveHelper.ExtractZipStreamSafely(
                        payloadZipStream,
                        fullInstallDir,
                        onEntryProgress: (index, total, entryName) =>
                        {
                            double pct = 10.0 + (index / (double)total) * 65.0;
                            progress?.Report((pct, $"Installing: {entryName}"));
                        },
                        cancellationToken: cancellationToken);
                }, cancellationToken);
            }
            else
            {
                progress?.Report((15, "Copying local build files into installation directory..."));
                string? devSource = FindLocalDevPackagerBuild();
                if (string.IsNullOrWhiteSpace(devSource) || !Directory.Exists(devSource))
                {
                    return (false, "Could not locate embedded SVEPU payload or local build files.");
                }
                CopyDirectoryRecursive(devSource, fullInstallDir);
            }

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                progress?.Report((78, "Setting executable permissions..."));
                SetUnixExecutablePermissions(fullInstallDir);
            }

            string? currentProcessPath = Environment.ProcessPath;
            string uninstallerName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? "Uninstall-SVEPU.exe"
                : "Uninstall-SVEPU";
            string targetUninstallerPath = Path.Combine(fullInstallDir, uninstallerName);

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
                await CreateShortcutsAsync(fullInstallDir, createDesktopShortcut, createMenuShortcut);
            }

            if (registerSystemUninstaller && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                progress?.Report((93, "Registering SVEPU in Windows Installed Apps..."));
                RegisterWindowsUninstaller(fullInstallDir, targetUninstallerPath);
            }

            progress?.Report((100, "SVEPU installation complete!"));
            return (true, $"Successfully installed SVEPU to {fullInstallDir}");
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
            if (!IsSafeUninstallDirectory(installDir))
            {
                return (false, "The specified directory does not appear to be a valid SVEPU installation directory.");
            }

            string fullInstallDir = Path.GetFullPath(installDir);

            progress?.Report((20, "Removing shortcuts..."));
            RemoveShortcuts();

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                progress?.Report((45, "Removing Windows Installed Apps registry entry..."));
                UnregisterWindowsUninstaller();
            }

            if (Directory.Exists(fullInstallDir))
            {
                progress?.Report((75, $"Removing files in {fullInstallDir}..."));
                await Task.Run(() =>
                {
                    foreach (var file in Directory.GetFiles(fullInstallDir, "*", SearchOption.AllDirectories))
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
                        Directory.Delete(fullInstallDir, recursive: true);
                    }
                    catch
                    {
                        ScheduleDirectoryCleanupOnExit(fullInstallDir);
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
            // POSIX single-quote escaping prevents any shell expansion in targetExe
            string posixSingleQuotedExe = "'" + targetExe.Replace("\r", "").Replace("\n", "").Replace("'", "'\\''") + "'";
            string script =
                "#!/usr/bin/env sh\n" +
                $"exec {posixSingleQuotedExe}\n";

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
            string cleanExe = StripNewlinesAndEscapeDesktopExec(targetExe);
            string cleanPath = StripNewlines(installDir);
            string cleanIcon = StripNewlines(iconPng);

            string desktopEntry =
                "[Desktop Entry]\n" +
                "Type=Application\n" +
                $"Name={AppDisplayName}\n" +
                "Comment=Package and share your Stardew Valley mods across any device.\n" +
                $"Exec=\"{cleanExe}\"\n" +
                $"Path={cleanPath}\n" +
                $"Icon={cleanIcon}\n" +
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

    private static string StripNewlines(string value)
    {
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }

    private static string StripNewlinesAndEscapeDesktopExec(string value)
    {
        return StripNewlines(value)
            .Replace(@"\", @"\\")
            .Replace("\"", "\\\"")
            .Replace("$", "\\$")
            .Replace("`", "\\`");
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

            string base64EncodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(psCommand));

            var psi = new ProcessStartInfo
            {
                FileName = "powershell",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-WindowStyle");
            psi.ArgumentList.Add("Hidden");
            psi.ArgumentList.Add("-EncodedCommand");
            psi.ArgumentList.Add(base64EncodedCommand);

            using var proc = Process.Start(psi);
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
            string fullInstallDir = Path.GetFullPath(installDir);
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/C ping 127.0.0.1 -n 3 > nul & rmdir /s /q \"%SVEPU_CLEANUP_DIR%\"",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.Environment["SVEPU_CLEANUP_DIR"] = fullInstallDir;
            Process.Start(psi);
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
