using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using SVEPU.Core.Models;

namespace SVEPU.Core.Services;

public sealed class GamePathDetectorService
{
    private static readonly Regex VdfPathRegex = new(
        "\"path\"\\s+\"([^\"]+)\"",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public string? DetectGamePath(GameServiceType service)
    {
        return service switch
        {
            GameServiceType.Steam => DetectSteamGamePath(),
            GameServiceType.GogGalaxy => DetectGogGamePath(),
            GameServiceType.XboxApp => DetectXboxGamePath(),
            _ => null
        };
    }

    public string? DetectAnyInstalledModsFolder()
    {
        foreach (var service in new[] { GameServiceType.Steam, GameServiceType.GogGalaxy, GameServiceType.XboxApp })
        {
            var gamePath = DetectGamePath(service);
            if (!string.IsNullOrWhiteSpace(gamePath))
            {
                var modsPath = GetModsPath(gamePath);
                if (Directory.Exists(modsPath))
                {
                    return modsPath;
                }
            }
        }
        return null;
    }

    public static string GetModsPath(string gameOrModsPath)
    {
        if (string.IsNullOrWhiteSpace(gameOrModsPath))
        {
            return string.Empty;
        }

        var normalized = NormalizeGamePath(gameOrModsPath);
        return Path.Combine(normalized, "Mods");
    }

    public static string NormalizeGamePath(string inputPath)
    {
        if (string.IsNullOrWhiteSpace(inputPath))
        {
            return string.Empty;
        }

        var trimmed = inputPath.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var dirInfo = new DirectoryInfo(trimmed);

        // If user selected the "Mods" directory directly, step up to the game root
        if (string.Equals(dirInfo.Name, "Mods", StringComparison.OrdinalIgnoreCase) && dirInfo.Parent != null)
        {
            return dirInfo.Parent.FullName;
        }

        // On macOS, if user selected Stardew Valley.app, step into Contents/MacOS
        if (dirInfo.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
        {
            var macOsInner = Path.Combine(dirInfo.FullName, "Contents", "MacOS");
            if (Directory.Exists(macOsInner))
            {
                return macOsInner;
            }
        }

        return dirInfo.FullName;
    }

    public static bool IsSmapiInstalled(string gamePath)
    {
        if (string.IsNullOrWhiteSpace(gamePath))
        {
            return false;
        }

        var normalized = NormalizeGamePath(gamePath);
        if (!Directory.Exists(normalized))
        {
            return false;
        }

        return File.Exists(Path.Combine(normalized, "StardewModdingAPI.exe")) ||
               File.Exists(Path.Combine(normalized, "StardewModdingAPI.dll")) ||
               File.Exists(Path.Combine(normalized, "StardewModdingAPI"));
    }

    public static bool IsValidStardewDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var normalized = NormalizeGamePath(path);
        if (!Directory.Exists(normalized))
        {
            return false;
        }

        return File.Exists(Path.Combine(normalized, "Stardew Valley.dll")) ||
               File.Exists(Path.Combine(normalized, "Stardew Valley.exe")) ||
               File.Exists(Path.Combine(normalized, "StardewValley.exe")) ||
               File.Exists(Path.Combine(normalized, "StardewValley")) ||
               Directory.Exists(Path.Combine(normalized, "Content"));
    }

    public string? DetectSteamInstallRoot()
    {
        var candidates = new List<string>();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            TryAddWindowsRegistryValue(candidates, Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath");
            TryAddWindowsRegistryValue(candidates, Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath");
            TryAddWindowsRegistryValue(candidates, Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath");

            foreach (var drive in GetFixedDriveRoots())
            {
                candidates.Add(Path.Combine(drive, "Program Files (x86)", "Steam"));
                candidates.Add(Path.Combine(drive, "Program Files", "Steam"));
                candidates.Add(Path.Combine(drive, "Steam"));
            }
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            candidates.Add(Path.Combine(home, "Library", "Application Support", "Steam"));
        }
        else
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            candidates.Add(Path.Combine(home, ".steam", "steam"));
            candidates.Add(Path.Combine(home, ".local", "share", "Steam"));
            candidates.Add(Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam"));
        }

        return candidates
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Replace('/', Path.DirectorySeparatorChar))
            .FirstOrDefault(Directory.Exists);
    }

    public string? DetectSteamGamePath()
    {
        var libraryRoots = new List<string>();
        var steamRoot = DetectSteamInstallRoot();

        if (!string.IsNullOrWhiteSpace(steamRoot))
        {
            libraryRoots.Add(steamRoot);
            var vdfPath = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdfPath))
            {
                libraryRoots.AddRange(ParseSteamLibraryFoldersVdf(File.ReadAllText(vdfPath)));
            }
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            foreach (var drive in GetFixedDriveRoots())
            {
                libraryRoots.Add(Path.Combine(drive, "SteamLibrary"));
                libraryRoots.Add(Path.Combine(drive, "Program Files (x86)", "Steam"));
            }
        }

        foreach (var lib in libraryRoots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var commonSdv = Path.Combine(lib, "steamapps", "common", "Stardew Valley");
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                var macPath = Path.Combine(commonSdv, "Contents", "MacOS");
                if (Directory.Exists(macPath))
                {
                    return macPath;
                }
            }

            if (Directory.Exists(commonSdv))
            {
                return commonSdv;
            }
        }

        return null;
    }

    public static List<string> ParseSteamLibraryFoldersVdf(string vdfContent)
    {
        var paths = new List<string>();
        if (string.IsNullOrWhiteSpace(vdfContent))
        {
            return paths;
        }

        foreach (Match match in VdfPathRegex.Matches(vdfContent))
        {
            var rawPath = match.Groups[1].Value.Replace(@"\\", @"\");
            if (!string.IsNullOrWhiteSpace(rawPath))
            {
                paths.Add(rawPath);
            }
        }

        return paths;
    }

    private string? DetectGogGamePath()
    {
        var candidates = new List<string>();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            TryAddWindowsRegistryValue(candidates, Registry.LocalMachine, @"SOFTWARE\WOW6432Node\GOG.com\Games\1453375253", "path");
            TryAddWindowsRegistryValue(candidates, Registry.LocalMachine, @"SOFTWARE\GOG.com\Games\1453375253", "path");

            foreach (var drive in GetFixedDriveRoots())
            {
                candidates.Add(Path.Combine(drive, "Program Files (x86)", "GOG Galaxy", "Games", "Stardew Valley"));
                candidates.Add(Path.Combine(drive, "Program Files", "GOG Galaxy", "Games", "Stardew Valley"));
                candidates.Add(Path.Combine(drive, "GOG Games", "Stardew Valley"));
            }
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            candidates.Add("/Applications/Stardew Valley.app/Contents/MacOS");
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            candidates.Add(Path.Combine(home, "Applications", "Stardew Valley.app", "Contents", "MacOS"));
        }
        else
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            candidates.Add(Path.Combine(home, "GOG Games", "Stardew Valley", "game"));
            candidates.Add(Path.Combine(home, "Games", "Stardew Valley"));
        }

        return candidates.FirstOrDefault(Directory.Exists);
    }

    private string? DetectXboxGamePath()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return null;
        }

        var candidates = new List<string>();
        foreach (var drive in GetFixedDriveRoots())
        {
            // Check .GamingRoot on each drive if present
            var gamingRootFile = Path.Combine(drive, ".GamingRoot");
            if (File.Exists(gamingRootFile))
            {
                try
                {
                    var bytes = File.ReadAllBytes(gamingRootFile);
                    // .GamingRoot format: 4-byte header 'RGBX', 4-byte version, followed by UTF-16LE relative path
                    if (bytes.Length > 8)
                    {
                        var relativeFolder = Encoding.Unicode.GetString(bytes, 8, bytes.Length - 8).TrimEnd('\0', '\\', '/');
                        if (!string.IsNullOrWhiteSpace(relativeFolder))
                        {
                            candidates.Add(Path.Combine(drive, relativeFolder, "Stardew Valley", "Content"));
                        }
                    }
                }
                catch
                {
                    // Ignore malformed .GamingRoot
                }
            }

            candidates.Add(Path.Combine(drive, "XboxGames", "Stardew Valley", "Content"));
            candidates.Add(Path.Combine(drive, "Program Files", "ModifiableWindowsApps", "Stardew Valley"));
        }

        return candidates.FirstOrDefault(Directory.Exists);
    }

    private static IEnumerable<string> GetFixedDriveRoots()
    {
        try
        {
            return DriveInfo.GetDrives()
                .Where(d => d.IsReady && (d.DriveType == DriveType.Fixed || d.DriveType == DriveType.Removable))
                .Select(d => d.RootDirectory.FullName);
        }
        catch
        {
            return new[] { @"C:\" };
        }
    }

    private static void TryAddWindowsRegistryValue(List<string> target, RegistryKey rootKey, string subKeyPath, string valueName)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        try
        {
            using var subKey = rootKey.OpenSubKey(subKeyPath);
            var val = subKey?.GetValue(valueName) as string;
            if (!string.IsNullOrWhiteSpace(val))
            {
                target.Add(val);
            }
        }
        catch
        {
            // Ignore registry permission/missing key errors
        }
    }
}
