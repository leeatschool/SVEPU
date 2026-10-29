using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace SVEPU.Core.Services;

public sealed class SteamLaunchOptionsService
{
    public const string StardewAppId = "413150";
    private readonly GamePathDetectorService _detector;

    public SteamLaunchOptionsService(GamePathDetectorService? detector = null)
    {
        _detector = detector ?? new GamePathDetectorService();
    }

    public static string BuildWindowsLaunchOption(string gamePath)
    {
        var normalized = GamePathDetectorService.NormalizeGamePath(gamePath);
        var smapiExe = Path.Combine(normalized, "StardewModdingAPI.exe");
        return $"\"{smapiExe}\" %command%";
    }

    public async Task<(bool Success, string Message)> ApplySteamLaunchOptionsAsync(
        string gamePath,
        bool autoCloseAndRelaunchSteam,
        IProgress<string>? statusProgress = null,
        CancellationToken cancellationToken = default)
    {
        var steamRoot = _detector.DetectSteamInstallRoot();
        if (string.IsNullOrWhiteSpace(steamRoot) || !Directory.Exists(steamRoot))
        {
            return (false, "Could not locate the Steam installation directory.");
        }

        var userdataDir = Path.Combine(steamRoot, "userdata");
        if (!Directory.Exists(userdataDir))
        {
            return (false, "Could not find Steam userdata directory.");
        }

        var configFiles = Directory.GetFiles(userdataDir, "localconfig.vdf", SearchOption.AllDirectories);
        if (configFiles.Length == 0)
        {
            return (false, "No Steam user localconfig.vdf files found.");
        }

        bool steamWasRunning = IsSteamRunning();
        string? steamExePath = GetSteamExecutablePath(steamRoot);

        if (steamWasRunning && autoCloseAndRelaunchSteam)
        {
            statusProgress?.Report("Closing Steam briefly to apply Launch Options...");
            await CloseSteamAsync(steamExePath, cancellationToken);
        }

        var launchOption = BuildWindowsLaunchOption(gamePath);
        int updatedCount = 0;

        foreach (var vdfPath in configFiles)
        {
            try
            {
                var original = await File.ReadAllTextAsync(vdfPath, cancellationToken);
                var updated = UpdateStardewLaunchOptionsInVdf(original, launchOption);
                if (!string.Equals(original, updated, StringComparison.Ordinal))
                {
                    File.Copy(vdfPath, vdfPath + ".svepu.bak", overwrite: true);
                    await File.WriteAllTextAsync(vdfPath, updated, cancellationToken);
                }
                updatedCount++;
            }
            catch
            {
                // Continue with remaining user profiles if one fails
            }
        }

        if (steamWasRunning && autoCloseAndRelaunchSteam && !string.IsNullOrWhiteSpace(steamExePath) && File.Exists(steamExePath))
        {
            statusProgress?.Report("Relaunching Steam...");
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = steamExePath,
                    Arguments = "-silent",
                    UseShellExecute = true
                });
            }
            catch
            {
                // Ignore relaunch failure
            }
        }

        return updatedCount > 0
            ? (true, $"Configured Steam Launch Options across {updatedCount} Steam profile(s).")
            : (false, "Failed to update Steam localconfig.vdf.");
    }

    public static string UpdateStardewLaunchOptionsInVdf(string vdfContent, string rawLaunchOption)
    {
        if (string.IsNullOrWhiteSpace(vdfContent))
        {
            return vdfContent;
        }

        var escapedOption = EscapeVdfValue(rawLaunchOption);

        // Locate the "apps" section, then the "413150" block inside it
        int appsIndex = FindAppsSectionAfterSteam(vdfContent);
        int searchStart = appsIndex >= 0 ? appsIndex : 0;

        var appKeyRegex = new Regex(@"(?m)^(\s*)""413150""\s*\r?\n\s*\{");
        var appMatch = appKeyRegex.Match(vdfContent, searchStart);

        if (appMatch.Success)
        {
            string indent = appMatch.Groups[1].Value;
            string innerIndent = indent + "\t";
            int openBraceIndex = vdfContent.IndexOf('{', appMatch.Index);
            int closeBraceIndex = FindMatchingClosingBrace(vdfContent, openBraceIndex);

            if (openBraceIndex >= 0 && closeBraceIndex > openBraceIndex)
            {
                string blockInner = vdfContent.Substring(openBraceIndex + 1, closeBraceIndex - openBraceIndex - 1);
                var launchOptRegex = new Regex(@"(?m)^(\s*""LaunchOptions""\s+"")((?:\\.|[^""\\])*)("")");

                if (launchOptRegex.IsMatch(blockInner))
                {
                    string updatedInner = launchOptRegex.Replace(
                        blockInner,
                        m => $"{m.Groups[1].Value}{escapedOption}{m.Groups[3].Value}",
                        count: 1);

                    return vdfContent[..(openBraceIndex + 1)] + updatedInner + vdfContent[closeBraceIndex..];
                }
                else
                {
                    string newLine = vdfContent.Contains("\r\n") ? "\r\n" : "\n";
                    string insertion = $"{newLine}{innerIndent}\"LaunchOptions\"\t\t\"{escapedOption}\"";
                    return vdfContent[..(openBraceIndex + 1)] + insertion + blockInner + vdfContent[closeBraceIndex..];
                }
            }
        }

        // If "413150" block does not exist yet under "apps", insert it right inside "apps" {
        if (appsIndex >= 0)
        {
            int appsOpenBrace = vdfContent.IndexOf('{', appsIndex);
            if (appsOpenBrace >= 0)
            {
                string newLine = vdfContent.Contains("\r\n") ? "\r\n" : "\n";
                string block = $"{newLine}\t\t\t\t\t\"413150\"{newLine}\t\t\t\t\t{{{newLine}\t\t\t\t\t\t\"LaunchOptions\"\t\t\"{escapedOption}\"{newLine}\t\t\t\t\t}}";
                return vdfContent[..(appsOpenBrace + 1)] + block + vdfContent[(appsOpenBrace + 1)..];
            }
        }

        return vdfContent;
    }

    public static string EscapeVdfValue(string value)
    {
        return value
            .Replace(@"\", @"\\")
            .Replace("\"", "\\\"");
    }

    private static int FindAppsSectionAfterSteam(string vdf)
    {
        var steamMatch = Regex.Match(vdf, @"""Steam""\s*\r?\n\s*\{", RegexOptions.IgnoreCase);
        int start = steamMatch.Success ? steamMatch.Index : 0;
        var appsMatch = Regex.Match(vdf[start..], @"""apps""\s*\r?\n\s*\{", RegexOptions.IgnoreCase);
        return appsMatch.Success ? start + appsMatch.Index : -1;
    }

    private static int FindMatchingClosingBrace(string text, int openBraceIndex)
    {
        int depth = 0;
        bool inQuotes = false;

        for (int i = openBraceIndex; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '"' && (i == 0 || text[i - 1] != '\\'))
            {
                inQuotes = !inQuotes;
            }
            else if (!inQuotes)
            {
                if (c == '{')
                {
                    depth++;
                }
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        return i;
                    }
                }
            }
        }

        return -1;
    }

    private static bool IsSteamRunning()
    {
        try
        {
            return Process.GetProcessesByName("steam").Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static string? GetSteamExecutablePath(string steamRoot)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var exe = Path.Combine(steamRoot, "steam.exe");
            return File.Exists(exe) ? exe : null;
        }
        return null;
    }

    private static async Task CloseSteamAsync(string? steamExePath, CancellationToken cancellationToken)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(steamExePath) && File.Exists(steamExePath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = steamExePath,
                    Arguments = "-shutdown",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
            }

            for (int i = 0; i < 12; i++)
            {
                if (!IsSteamRunning())
                {
                    return;
                }
                await Task.Delay(500, cancellationToken);
            }

            foreach (var proc in Process.GetProcessesByName("steam"))
            {
                try
                {
                    proc.Kill();
                    await proc.WaitForExitAsync(cancellationToken);
                }
                catch
                {
                    // Ignore process access errors
                }
            }
        }
        catch
        {
            // Ignore shutdown errors
        }
    }
}
