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
                    var fi = new FileInfo(metadataPath);
                    if (fi.Length <= 10 * 1024 * 1024) // Cap metadata.json at 10 MB
                    {
                        var json = File.ReadAllText(metadataPath);
                        var metadata = JsonSerializer.Deserialize<PackageMetadata>(json, MetadataJsonOptions) ?? new PackageMetadata();

                        // Filter out any malformed/unsafe FolderName entries from metadata.json
                        metadata.Mods = metadata.Mods
                            .Where(m => SafeArchiveHelper.IsSafeSingleFolderOrFileName(m.FolderName))
                            .ToList();

                        string? iconPath = null;
                        if (SafeArchiveHelper.IsSafeSingleFolderOrFileName(metadata.CustomIconFileName))
                        {
                            var candidate = Path.GetFullPath(Path.Combine(payloadDir, metadata.CustomIconFileName!));
                            if (SafeArchiveHelper.IsSubPathOf(candidate, payloadDir) && File.Exists(candidate))
                            {
                                iconPath = candidate;
                            }
                        }

                        return (metadata, iconPath, payloadDir);
                    }
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

        targetModsDir = Path.GetFullPath(targetModsDir);
        Directory.CreateDirectory(targetModsDir);

        var selectedModsList = modsToInstall.Where(m => m.IsSelected).ToList();
        if (selectedModsList.Count == 0)
        {
            return (false, 0, 0, null, "No mods were selected for installation.");
        }

        // Strict pre-validation of all FolderName values before any filesystem mutations
        foreach (var modInfo in selectedModsList)
        {
            if (!SafeArchiveHelper.IsSafeSingleFolderOrFileName(modInfo.FolderName))
            {
                return (false, 0, 0, null, $"Blocked unsafe mod folder name in package metadata: '{modInfo.FolderName}'.");
            }

            string candidateModDir = Path.GetFullPath(Path.Combine(targetModsDir, modInfo.FolderName));
            if (!SafeArchiveHelper.IsSubPathOf(candidateModDir, targetModsDir))
            {
                return (false, 0, 0, null, $"Blocked path traversal attempt for mod folder: '{modInfo.FolderName}'.");
            }
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
                        .Where(d => !d.Name.StartsWith('.') &&
                                    !BuiltInSmapiFolders.Contains(d.Name) &&
                                    (d.Attributes & FileAttributes.ReparsePoint) == 0)
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

                if (archive.Entries.Count > SafeArchiveHelper.DefaultMaxArchiveEntries)
                {
                    return (false, 0, 0, backupDirPath, "Package archive exceeds maximum allowed file entry count.");
                }

                int installedCount = 0;
                int skippedCount = 0;
                long totalBytesExtracted = 0;

                for (int i = 0; i < selectedModsList.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var modInfo = selectedModsList[i];
                    string modFolder = modInfo.FolderName.Trim();
                    string destModPath = Path.GetFullPath(Path.Combine(targetModsDir, modFolder));

                    if (!SafeArchiveHelper.IsSubPathOf(destModPath, targetModsDir))
                    {
                        throw new InvalidDataException($"Mod destination path escaped target Mods directory: {modFolder}");
                    }

                    double pct = 10.0 + ((i + 1) / (double)selectedModsList.Count) * 85.0;
                    progress?.Report((pct, $"Installing ({i + 1}/{selectedModsList.Count}): {modInfo.Name}..."));

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
                        if (string.IsNullOrEmpty(entry.Name) || SafeArchiveHelper.IsSymlinkEntry(entry))
                        {
                            continue;
                        }

                        string relativeInsideMod = entry.FullName.Replace('\\', '/')[prefix.Length..];
                        string fullDestFile = Path.GetFullPath(Path.Combine(destModPath, relativeInsideMod.Replace('/', Path.DirectorySeparatorChar)));

                        // Guard against zip slip with strict trailing separator checks on both destModPath and targetModsDir
                        if (!SafeArchiveHelper.IsSubPathOf(fullDestFile, destModPath) ||
                            !SafeArchiveHelper.IsSubPathOf(fullDestFile, targetModsDir))
                        {
                            throw new InvalidDataException($"Blocked Zip-Slip path traversal entry: {entry.FullName}");
                        }

                        SafeArchiveHelper.ExtractEntryToFileBounded(entry, fullDestFile, ref totalBytesExtracted);
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
