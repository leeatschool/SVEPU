using System.IO.Compression;
using System.Text.Json;
using SVEPU.Core.Models;

namespace SVEPU.Core.Services;

public sealed class ModScannerService : IDisposable
{
    private static readonly HashSet<string> BuiltInModFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "ConsoleCommands",
        "SaveBackup",
        "ErrorHandler"
    };

    private static readonly HashSet<string> BuiltInModUniqueIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "SMAPI.ConsoleCommands",
        "SMAPI.SaveBackup",
        "SMAPI.ErrorHandler"
    };

    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly List<string> _tempExtractedDirs = new();

    public List<ModEntry> ScanPath(string path)
    {
        var results = new List<ModEntry>();
        if (string.IsNullOrWhiteSpace(path))
        {
            return results;
        }

        if (File.Exists(path) && path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "SVEPU_Staging_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            _tempExtractedDirs.Add(tempDir);

            ZipFile.ExtractToDirectory(path, tempDir, overwriteFiles: true);
            ScanDirectoryRecursive(tempDir, results, isFromArchive: true, originalArchivePath: path);
            return results;
        }

        if (Directory.Exists(path))
        {
            ScanDirectoryRecursive(path, results, isFromArchive: false, originalArchivePath: null);
        }

        return results;
    }

    private static void ScanDirectoryRecursive(
        string directoryPath,
        List<ModEntry> results,
        bool isFromArchive,
        string? originalArchivePath)
    {
        var dirInfo = new DirectoryInfo(directoryPath);
        if (!dirInfo.Exists)
        {
            return;
        }

        // Skip hidden/dot directories like .smapi
        if (dirInfo.Name.StartsWith('.'))
        {
            return;
        }

        // Skip built-in SMAPI mod folders
        if (BuiltInModFolderNames.Contains(dirInfo.Name))
        {
            return;
        }

        var manifestPath = Path.Combine(directoryPath, "manifest.json");
        if (File.Exists(manifestPath))
        {
            var entry = TryReadModDirectory(directoryPath, manifestPath, isFromArchive, originalArchivePath);
            if (entry != null && !BuiltInModUniqueIds.Contains(entry.UniqueID))
            {
                results.Add(entry);
            }
            // Once a folder has manifest.json, its subfolders are part of this mod (assets, i18n, etc.)
            return;
        }

        foreach (var subDir in dirInfo.EnumerateDirectories())
        {
            ScanDirectoryRecursive(subDir.FullName, results, isFromArchive, originalArchivePath);
        }
    }

    private static ModEntry? TryReadModDirectory(
        string modDirPath,
        string manifestPath,
        bool isFromArchive,
        string? originalArchivePath)
    {
        var dirName = new DirectoryInfo(modDirPath).Name;
        SmapiManifest? manifest = null;

        try
        {
            var rawJson = File.ReadAllText(manifestPath);
            // Strip BOM or weird leading characters if present
            int firstBrace = rawJson.IndexOf('{');
            if (firstBrace > 0)
            {
                rawJson = rawJson[firstBrace..];
            }
            manifest = JsonSerializer.Deserialize<SmapiManifest>(rawJson, ManifestJsonOptions);
        }
        catch
        {
            // Fallback to folder-based metadata if manifest has non-standard syntax
        }

        var requiredDeps = new List<string>();
        if (manifest?.ContentPackFor != null && !string.IsNullOrWhiteSpace(manifest.ContentPackFor.UniqueID))
        {
            requiredDeps.Add(manifest.ContentPackFor.UniqueID.Trim());
        }

        if (manifest?.Dependencies != null)
        {
            foreach (var dep in manifest.Dependencies)
            {
                if (dep.IsRequired && !string.IsNullOrWhiteSpace(dep.UniqueID))
                {
                    if (!requiredDeps.Contains(dep.UniqueID.Trim(), StringComparer.OrdinalIgnoreCase))
                    {
                        requiredDeps.Add(dep.UniqueID.Trim());
                    }
                }
            }
        }

        return new ModEntry
        {
            Name = !string.IsNullOrWhiteSpace(manifest?.Name) ? manifest.Name.Trim() : dirName,
            Author = !string.IsNullOrWhiteSpace(manifest?.Author) ? manifest.Author.Trim() : "Unknown",
            Version = !string.IsNullOrWhiteSpace(manifest?.Version) ? manifest.Version.Trim() : "1.0.0",
            UniqueID = !string.IsNullOrWhiteSpace(manifest?.UniqueID) ? manifest.UniqueID.Trim() : dirName,
            Description = manifest?.Description?.Trim() ?? string.Empty,
            SourcePath = modDirPath,
            FolderName = dirName,
            HasConfigJson = File.Exists(Path.Combine(modDirPath, "config.json")),
            IsFromArchive = isFromArchive,
            OriginalArchivePath = originalArchivePath,
            RequiredDependencies = requiredDeps,
            IsSelected = true
        };
    }

    public static void UpdateDependencyWarnings(IEnumerable<ModEntry> allMods)
    {
        var modList = allMods.ToList();
        var activeUniqueIds = new HashSet<string>(
            modList.Where(m => m.IsSelected && !string.IsNullOrWhiteSpace(m.UniqueID))
                   .Select(m => m.UniqueID),
            StringComparer.OrdinalIgnoreCase);

        foreach (var mod in modList)
        {
            if (!mod.IsSelected || mod.RequiredDependencies.Count == 0)
            {
                mod.DependencyWarning = string.Empty;
                continue;
            }

            var missing = mod.RequiredDependencies
                .Where(depId => !activeUniqueIds.Contains(depId))
                .ToList();

            mod.DependencyWarning = missing.Count > 0
                ? $"Missing required mod(s): {string.Join(", ", missing)}"
                : string.Empty;
        }
    }

    public void Dispose()
    {
        foreach (var dir in _tempExtractedDirs)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
            catch
            {
                // Ignore temp cleanup errors
            }
        }
        _tempExtractedDirs.Clear();
    }
}
