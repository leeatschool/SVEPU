using System.IO.Compression;
using System.Text.Json;
using SVEPU.Core.Models;

namespace SVEPU.Core.Services;

public sealed class PackageExtractorService
{
    private static readonly JsonSerializerOptions MetadataJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly HashSet<string> BuiltInSmapiFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "ConsoleCommands",
        "SaveBackup",
        "ErrorHandler"
    };

    public static string? ResolvePayloadDirectory(string? explicitPackageRoot = null)
    {
        var searchRoots = new List<string>();
        if (!string.IsNullOrWhiteSpace(explicitPackageRoot))
        {
            searchRoots.Add(explicitPackageRoot);
        }

        searchRoots.Add(AppContext.BaseDirectory);
        searchRoots.Add(Environment.CurrentDirectory);

        foreach (var root in searchRoots.Where(r => !string.IsNullOrWhiteSpace(r)))
        {
            var current = new DirectoryInfo(root);
            for (int depth = 0; depth < 5 && current != null; depth++)
            {
                var candidatePayload = Path.Combine(current.FullName, "payload");
                if (Directory.Exists(candidatePayload) && File.Exists(Path.Combine(candidatePayload, "metadata.json")))
                {
                    return candidatePayload;
                }

                if (File.Exists(Path.Combine(current.FullName, "metadata.json")) &&
                    File.Exists(Path.Combine(current.FullName, "mods.svepu")))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }
        }

        return null;
    }

    public static (PackageMetadata Metadata, string? CustomIconPath, string? PayloadDir) LoadPackagePayload(string? explicitPackageRoot = null)
    {
        var payloadDir = ResolvePayloadDirectory(explicitPackageRoot);
        if (!string.IsNullOrWhiteSpace(payloadDir))
        {
            var metadataPath = Path.Combine(payloadDir, "metadata.json");
            if (File.Exists(metadataPath))
            {
                try
                {
                    var json = File.ReadAllText(metadataPath);
                    var metadata = JsonSerializer.Deserialize<PackageMetadata>(json, MetadataJsonOptions) ?? new PackageMetadata();

                    string? iconPath = null;
                    if (!string.IsNullOrWhiteSpace(metadata.CustomIconFileName))
                    {
                        var candidate = Path.Combine(payloadDir, metadata.CustomIconFileName);
                        if (File.Exists(candidate))
                        {
                            iconPath = candidate;
                        }
                    }

                    return (metadata, iconPath, payloadDir);
                }
                catch
                {
                    // Fall back to default metadata
                }
            }
        }

        return (new PackageMetadata
        {
            PackName = "Stardew Valley Modpack",
            Author = "SVEPU",
            Version = "1.0.0",
            Description = "Ready to install packaged Stardew Valley mods."
        }, null, payloadDir);
    }

    public async Task<(bool Success, int InstalledCount, int SkippedCount, string? BackupDir, string Message)> ExtractModsAsync(
        string payloadDir,
        string gameOrModsPath,
        IReadOnlyCollection<PackagedModInfo> modsToInstall,
        ConflictResolutionMode conflictMode,
        IProgress<(double Percent, string Status)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(payloadDir) || !Directory.Exists(payloadDir))
        {
            return (false, 0, 0, null, "Package payload directory was not found.");
        }

        string modsArchivePath = Path.Combine(payloadDir, "mods.svepu");
        if (!File.Exists(modsArchivePath))
        {
            return (false, 0, 0, null, "Package archive (payload/mods.svepu) was not found.");
        }

        string targetModsDir = GamePathDetectorService.GetModsPath(gameOrModsPath);
        if (string.IsNullOrWhiteSpace(targetModsDir))
        {
            return (false, 0, 0, null, "Invalid target Stardew Valley directory.");
        }

        Directory.CreateDirectory(targetModsDir);

        var selectedFolders = new HashSet<string>(
            modsToInstall.Where(m => m.IsSelected).Select(m => m.FolderName),
            StringComparer.OrdinalIgnoreCase);

        if (selectedFolders.Count == 0)
        {
            return (false, 0, 0, null, "No mods were selected for installation.");
        }

        string? backupDirPath = null;

        try
        {
            return await Task.Run(() =>
            {
                if (conflictMode == ConflictResolutionMode.BackupAndClean)
                {
                    progress?.Report((5, "Backing up existing Mods folder..."));
                    string parentDir = Directory.GetParent(targetModsDir)?.FullName ?? targetModsDir;
                    string backupName = $"Mods_Backup_{DateTime.Now:yyyyMMdd_HHmmss}";
                    backupDirPath = Path.Combine(parentDir, backupName);

                    var existingSubDirs = Directory.GetDirectories(targetModsDir)
                        .Select(d => new DirectoryInfo(d))
                        .Where(d => !d.Name.StartsWith('.') && !BuiltInSmapiFolders.Contains(d.Name))
                        .ToList();

                    if (existingSubDirs.Count > 0)
                    {
                        Directory.CreateDirectory(backupDirPath);
                        foreach (var subDir in existingSubDirs)
                        {
                            string destDir = Path.Combine(backupDirPath, subDir.Name);
                            Directory.Move(subDir.FullName, destDir);
                        }
                    }
                }

                using var fs = new FileStream(modsArchivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var archive = new ZipArchive(fs, ZipArchiveMode.Read);

                int installedCount = 0;
                int skippedCount = 0;
                var modsList = modsToInstall.Where(m => m.IsSelected).ToList();

                for (int i = 0; i < modsList.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var modInfo = modsList[i];
                    string modFolder = modInfo.FolderName;
                    string destModPath = Path.Combine(targetModsDir, modFolder);

                    double pct = 10.0 + ((i + 1) / (double)modsList.Count) * 85.0;
                    progress?.Report((pct, $"Installing ({i + 1}/{modsList.Count}): {modInfo.Name}..."));

                    if (Directory.Exists(destModPath))
                    {
                        if (conflictMode == ConflictResolutionMode.SkipExisting)
                        {
                            skippedCount++;
                            continue;
                        }

                        if (conflictMode == ConflictResolutionMode.OverwriteConflicts)
                        {
                            Directory.Delete(destModPath, recursive: true);
                        }
                    }

                    string prefix = modFolder + "/";
                    var matchingEntries = archive.Entries
                        .Where(e => e.FullName.Replace('\\', '/').StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    foreach (var entry in matchingEntries)
                    {
                        if (string.IsNullOrEmpty(entry.Name))
                        {
                            continue;
                        }

                        string relativeInsideMod = entry.FullName.Replace('\\', '/')[prefix.Length..];
                        string fullDestFile = Path.GetFullPath(Path.Combine(destModPath, relativeInsideMod.Replace('/', Path.DirectorySeparatorChar)));

                        // Guard against zip slip
                        if (!fullDestFile.StartsWith(Path.GetFullPath(destModPath), StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        string? parentFolder = Path.GetDirectoryName(fullDestFile);
                        if (!string.IsNullOrWhiteSpace(parentFolder))
                        {
                            Directory.CreateDirectory(parentFolder);
                        }

                        entry.ExtractToFile(fullDestFile, overwrite: true);
                    }

                    installedCount++;
                }

                progress?.Report((100, $"Installed {installedCount} mod(s) into {targetModsDir}."));
                string msg = skippedCount > 0
                    ? $"Installed {installedCount} mod(s) ({skippedCount} existing mod(s) skipped)."
                    : $"Successfully installed {installedCount} mod(s) into {targetModsDir}.";

                return (true, installedCount, skippedCount, backupDirPath, msg);
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            return (false, 0, 0, backupDirPath, $"Failed to extract mods: {ex.Message}");
        }
    }
}
