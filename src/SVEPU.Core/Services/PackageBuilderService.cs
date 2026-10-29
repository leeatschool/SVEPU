using System.IO.Compression;
using System.Text;
using System.Text.Json;
using SVEPU.Core.Models;

namespace SVEPU.Core.Services;

public sealed class PackageBuilderService
{
    private static readonly JsonSerializerOptions MetadataJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task<(bool Success, string ZipPath, string FolderPath, string Message)> BuildPackageAsync(
        PackageMetadata metadata,
        IReadOnlyList<ModEntry> selectedMods,
        string outputDirectory,
        string? customIconPath = null,
        string? installerBinariesRoot = null,
        bool keepUnzippedFolder = true,
        IProgress<(double Percent, string Status)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (selectedMods.Count == 0)
        {
            return (false, string.Empty, string.Empty, "Please select at least one mod to package.");
        }

        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            return (false, string.Empty, string.Empty, "Please specify an output directory.");
        }

        Directory.CreateDirectory(outputDirectory);

        string safePackName = SanitizeFileName(string.IsNullOrWhiteSpace(metadata.PackName) ? "StardewModpack" : metadata.PackName);
        string safeVersion = SanitizeFileName(string.IsNullOrWhiteSpace(metadata.Version) ? "1.0.0" : metadata.Version);
        string bundleBaseName = $"{safePackName}-v{safeVersion}";

        string stagingDir = Path.Combine(outputDirectory, bundleBaseName);
        if (Directory.Exists(stagingDir))
        {
            Directory.Delete(stagingDir, recursive: true);
        }
        Directory.CreateDirectory(stagingDir);

        string payloadDir = Path.Combine(stagingDir, "payload");
        Directory.CreateDirectory(payloadDir);

        try
        {
            progress?.Report((5, "Preparing package metadata..."));

            metadata.CreatedAtUtc = DateTime.UtcNow;
            metadata.Mods = new List<PackagedModInfo>();

            var usedFolderNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var modFolderMapping = new List<(ModEntry Mod, string TargetFolderName)>();

            foreach (var mod in selectedMods)
            {
                string folderName = string.IsNullOrWhiteSpace(mod.FolderName)
                    ? SanitizeFileName(mod.Name)
                    : mod.FolderName;

                string uniqueFolderName = folderName;
                int suffix = 2;
                while (!usedFolderNames.Add(uniqueFolderName))
                {
                    uniqueFolderName = $"{folderName}_{suffix++}";
                }

                modFolderMapping.Add((mod, uniqueFolderName));
                metadata.Mods.Add(new PackagedModInfo
                {
                    Name = mod.Name,
                    Author = mod.Author,
                    Version = mod.Version,
                    UniqueID = mod.UniqueID,
                    Description = mod.Description,
                    FolderName = uniqueFolderName,
                    HasConfigJson = mod.HasConfigJson && metadata.IncludeConfigs,
                    IsSelected = true
                });
            }

            // Copy custom icon (or default icon) into payload
            if (!string.IsNullOrWhiteSpace(customIconPath) && File.Exists(customIconPath))
            {
                string ext = Path.GetExtension(customIconPath);
                if (string.IsNullOrWhiteSpace(ext))
                {
                    ext = ".png";
                }
                string iconFileName = "icon" + ext.ToLowerInvariant();
                File.Copy(customIconPath, Path.Combine(payloadDir, iconFileName), overwrite: true);
                metadata.CustomIconFileName = iconFileName;
            }

            // Write metadata.json
            string metadataJson = JsonSerializer.Serialize(metadata, MetadataJsonOptions);
            await File.WriteAllTextAsync(Path.Combine(payloadDir, "metadata.json"), metadataJson, cancellationToken);

            // Build mods.svepu archive
            string modsArchivePath = Path.Combine(payloadDir, "mods.svepu");
            progress?.Report((15, "Compressing selected mods into payload/mods.svepu..."));

            await Task.Run(() =>
            {
                using var fs = new FileStream(modsArchivePath, FileMode.Create, FileAccess.Write, FileShare.None);
                using var archive = new ZipArchive(fs, ZipArchiveMode.Create);

                for (int i = 0; i < modFolderMapping.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var (mod, targetFolderName) = modFolderMapping[i];
                    double pct = 15.0 + ((i + 1) / (double)modFolderMapping.Count) * 55.0;
                    progress?.Report((pct, $"Packaging mod ({i + 1}/{modFolderMapping.Count}): {mod.Name}..."));

                    AddModDirectoryToZip(archive, mod.SourcePath, targetFolderName, metadata.IncludeConfigs);
                }
            }, cancellationToken);

            // Bundle cross-platform launchers & installer runtime files
            progress?.Report((75, "Creating Windows, macOS, and Linux installer launchers..."));
            await CreateCrossPlatformLaunchersAsync(stagingDir, payloadDir, customIconPath, installerBinariesRoot, cancellationToken);

            // Create final .zip archive with Unix executable permissions preserved for Linux/macOS launchers
            string zipOutputPath = Path.Combine(outputDirectory, $"{bundleBaseName}.zip");
            if (File.Exists(zipOutputPath))
            {
                File.Delete(zipOutputPath);
            }

            progress?.Report((88, $"Creating distributable archive { Path.GetFileName(zipOutputPath) }..."));
            await Task.Run(() => CreateDistributableZip(stagingDir, zipOutputPath), cancellationToken);

            if (!keepUnzippedFolder && Directory.Exists(stagingDir))
            {
                Directory.Delete(stagingDir, recursive: true);
            }

            progress?.Report((100, "Package created successfully!"));
            return (true, zipOutputPath, keepUnzippedFolder ? stagingDir : string.Empty, $"Successfully packaged {selectedMods.Count} mod(s) into {zipOutputPath}");
        }
        catch (Exception ex)
        {
            return (false, string.Empty, string.Empty, $"Packaging failed: {ex.Message}");
        }
    }

    private static void AddModDirectoryToZip(ZipArchive archive, string sourceModDir, string targetFolderName, bool includeConfigs)
    {
        var baseDir = new DirectoryInfo(sourceModDir);
        if (!baseDir.Exists)
        {
            return;
        }

        foreach (var file in baseDir.EnumerateFiles("*", SearchOption.AllDirectories))
        {
            var relPath = Path.GetRelativePath(sourceModDir, file.FullName);

            // Skip hidden/dot folders
            if (relPath.StartsWith('.') || relPath.Contains($"{Path.DirectorySeparatorChar}."))
            {
                continue;
            }

            // Optionally skip config.json at the root of the mod folder
            if (!includeConfigs && string.Equals(relPath, "config.json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string zipEntryName = $"{targetFolderName}/{relPath.Replace('\\', '/')}";
            archive.CreateEntryFromFile(file.FullName, zipEntryName, CompressionLevel.Optimal);
        }
    }

    private static async Task CreateCrossPlatformLaunchersAsync(
        string stagingDir,
        string payloadDir,
        string? customIconPath,
        string? installerBinariesRoot,
        CancellationToken cancellationToken)
    {
        string binDir = Path.Combine(payloadDir, "bin");
        Directory.CreateDirectory(binDir);

        // Copy pre-built or local SVEPU.Installer binaries if available
        string? resolvedBinRoot = ResolveInstallerBinariesRoot(installerBinariesRoot);
        bool copiedSingleFileWinExe = false;

        if (!string.IsNullOrWhiteSpace(resolvedBinRoot) && Directory.Exists(resolvedBinRoot))
        {
            // Check if multi-RID subfolders (win-x64, linux-x64, osx-x64) exist, or direct build output
            string winSingleExe = Path.Combine(resolvedBinRoot, "win-x64", "SVEPU.Installer.exe");
            if (File.Exists(winSingleExe))
            {
                string rootWinLauncher = Path.Combine(stagingDir, "Install-Windows.exe");
                File.Copy(winSingleExe, rootWinLauncher, overwrite: true);
                if (!string.IsNullOrWhiteSpace(customIconPath) && File.Exists(customIconPath))
                {
                    IconInjectorService.TryInjectIconIntoWindowsExe(rootWinLauncher, customIconPath);
                }
                copiedSingleFileWinExe = true;

                // Copy linux-x64 and osx-x64 if present
                foreach (var rid in new[] { "linux-x64", "osx-x64", "osx-arm64" })
                {
                    string ridSrc = Path.Combine(resolvedBinRoot, rid);
                    if (Directory.Exists(ridSrc))
                    {
                        CopyDirectoryRecursive(ridSrc, Path.Combine(binDir, rid));
                    }
                }
            }
            else
            {
                // Copy current build output of SVEPU.Installer into payload/bin/win-x64 (or portable)
                string targetWinDir = Path.Combine(binDir, "win-x64");
                CopyDirectoryRecursive(resolvedBinRoot, targetWinDir);

                string builtExe = Path.Combine(targetWinDir, "SVEPU.Installer.exe");
                if (File.Exists(builtExe) && !string.IsNullOrWhiteSpace(customIconPath) && File.Exists(customIconPath))
                {
                    IconInjectorService.TryInjectIconIntoWindowsExe(builtExe, customIconPath);
                }

                // If single-file publish without sibling DLLs, place directly at root as Install-Windows.exe
                var dllCount = Directory.GetFiles(targetWinDir, "*.dll").Length;
                if (File.Exists(builtExe) && dllCount == 0)
                {
                    string rootWinLauncher = Path.Combine(stagingDir, "Install-Windows.exe");
                    File.Move(builtExe, rootWinLauncher, overwrite: true);
                    copiedSingleFileWinExe = true;
                }
            }
        }

        if (!copiedSingleFileWinExe)
        {
            // If payload/bin/win-x64/SVEPU.Installer.exe exists alongside DLLs, copy the apphost/launcher or create a launcher script
            string innerWinExe = Path.Combine(binDir, "win-x64", "SVEPU.Installer.exe");
            if (File.Exists(innerWinExe))
            {
                // Also copy portable files or create root launcher batch/exe
                string batPath = Path.Combine(stagingDir, "Install-Windows.bat");
                await File.WriteAllTextAsync(
                    batPath,
                    "@echo off\r\nstart \"\" \"%~dp0payload\\bin\\win-x64\\SVEPU.Installer.exe\" --package-root \"%~dp0\"\r\n",
                    cancellationToken);
            }
        }

        // Write Linux launcher (Install-Linux) with LF line endings
        string linuxScript =
            "#!/usr/bin/env sh\n" +
            "DIR=\"$(CDPATH= cd -- \"$(dirname -- \"$0\")\" && pwd)\"\n" +
            "if [ -x \"$DIR/payload/bin/linux-x64/SVEPU.Installer\" ]; then\n" +
            "  exec \"$DIR/payload/bin/linux-x64/SVEPU.Installer\" --package-root \"$DIR\"\n" +
            "elif [ -f \"$DIR/payload/bin/win-x64/SVEPU.Installer.dll\" ]; then\n" +
            "  exec dotnet \"$DIR/payload/bin/win-x64/SVEPU.Installer.dll\" --package-root \"$DIR\"\n" +
            "else\n" +
            "  echo \"SVEPU Installer binary not found in payload/bin.\"\n" +
            "  exit 1\n" +
            "fi\n";
        await File.WriteAllBytesAsync(Path.Combine(stagingDir, "Install-Linux"), Encoding.UTF8.GetBytes(linuxScript), cancellationToken);

        // Write macOS launcher (Install-macOS.command) with LF line endings
        string macScript =
            "#!/usr/bin/env sh\n" +
            "DIR=\"$(CDPATH= cd -- \"$(dirname -- \"$0\")\" && pwd)\"\n" +
            "ARCH=\"$(uname -m)\"\n" +
            "if [ \"$ARCH\" = \"arm64\" ] && [ -x \"$DIR/payload/bin/osx-arm64/SVEPU.Installer\" ]; then\n" +
            "  exec \"$DIR/payload/bin/osx-arm64/SVEPU.Installer\" --package-root \"$DIR\"\n" +
            "elif [ -x \"$DIR/payload/bin/osx-x64/SVEPU.Installer\" ]; then\n" +
            "  exec \"$DIR/payload/bin/osx-x64/SVEPU.Installer\" --package-root \"$DIR\"\n" +
            "elif [ -f \"$DIR/payload/bin/win-x64/SVEPU.Installer.dll\" ]; then\n" +
            "  exec dotnet \"$DIR/payload/bin/win-x64/SVEPU.Installer.dll\" --package-root \"$DIR\"\n" +
            "else\n" +
            "  echo \"SVEPU Installer binary not found in payload/bin.\"\n" +
            "  exit 1\n" +
            "fi\n";
        await File.WriteAllBytesAsync(Path.Combine(stagingDir, "Install-macOS.command"), Encoding.UTF8.GetBytes(macScript), cancellationToken);
    }

    private static string? ResolveInstallerBinariesRoot(string? explicitRoot)
    {
        if (!string.IsNullOrWhiteSpace(explicitRoot) && Directory.Exists(explicitRoot))
        {
            return explicitRoot;
        }

        string appBase = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(appBase, "installer-templates"),
            Path.Combine(appBase, "..", "SVEPU.Installer"),
            Path.GetFullPath(Path.Combine(appBase, "..", "..", "..", "..", "SVEPU.Installer", "bin", "Release", "net8.0")),
            Path.GetFullPath(Path.Combine(appBase, "..", "..", "..", "..", "SVEPU.Installer", "bin", "Debug", "net8.0"))
        };

        return candidates.FirstOrDefault(Directory.Exists);
    }

    private static void CopyDirectoryRecursive(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            string dest = Path.Combine(targetDir, Path.GetFileName(file));
            File.Copy(file, dest, overwrite: true);
        }

        foreach (var subDir in Directory.GetDirectories(sourceDir))
        {
            string destSub = Path.Combine(targetDir, Path.GetFileName(subDir));
            CopyDirectoryRecursive(subDir, destSub);
        }
    }

    private static void CreateDistributableZip(string sourceDir, string zipFilePath)
    {
        using var fs = new FileStream(zipFilePath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(fs, ZipArchiveMode.Create);

        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relPath = Path.GetRelativePath(sourceDir, file).Replace('\\', '/');
            var entry = archive.CreateEntryFromFile(file, relPath, CompressionLevel.Optimal);

            // Set Unix 0755 executable permissions on Linux and macOS launcher scripts and binaries
            if (relPath.Equals("Install-Linux", StringComparison.OrdinalIgnoreCase) ||
                relPath.Equals("Install-macOS.command", StringComparison.OrdinalIgnoreCase) ||
                relPath.EndsWith("/SVEPU.Installer", StringComparison.OrdinalIgnoreCase))
            {
                // 0100755 octal in Unix mode bits shifted by 16
                entry.ExternalAttributes = unchecked((int)0x81ED0000);
            }
        }
    }

    public static string SanitizeFileName(string input)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(input.Length);
        foreach (char c in input.Trim())
        {
            sb.Append(invalidChars.Contains(c) ? '_' : c);
        }
        return sb.Length > 0 ? sb.ToString() : "StardewModpack";
    }
}
