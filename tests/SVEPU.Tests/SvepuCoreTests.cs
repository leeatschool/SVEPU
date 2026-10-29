using System.IO.Compression;
using SVEPU.Core.Models;
using SVEPU.Core.Services;

namespace SVEPU.Tests;

public sealed class SvepuCoreTests
{
    [Fact]
    public void ModScanner_ParsesManifestWithCommentsAndTrailingCommas_AndSkipsBuiltIns()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "SVEPU_Test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            // 1. Valid mod with comments, trailing commas, and object version
            string modA = Path.Combine(tempRoot, "CustomFarming");
            Directory.CreateDirectory(modA);
            File.WriteAllText(Path.Combine(modA, "manifest.json"), """
            {
                // Leading comment
                "Name": "Custom Farming Redux",
                "Author": "Aaron",
                "Version": { "MajorVersion": 2, "MinorVersion": 4, "PatchVersion": 1 },
                "Description": "Adds custom machines.",
                "UniqueID": "Aaron.CustomFarming",
                "ContentPackFor": {
                    "UniqueID": "Pathoschild.ContentPatcher",
                },
            }
            """);
            File.WriteAllText(Path.Combine(modA, "config.json"), """{ "Enabled": true }""");

            // 2. Built-in SMAPI mod that must be skipped
            string consoleCmds = Path.Combine(tempRoot, "ConsoleCommands");
            Directory.CreateDirectory(consoleCmds);
            File.WriteAllText(Path.Combine(consoleCmds, "manifest.json"), """
            {
                "Name": "Console Commands",
                "UniqueID": "SMAPI.ConsoleCommands",
                "Version": "4.0.0"
            }
            """);

            // 3. Hidden cache directory (.smapi) that must be skipped
            string dotSmapi = Path.Combine(tempRoot, ".smapi");
            Directory.CreateDirectory(dotSmapi);
            File.WriteAllText(Path.Combine(dotSmapi, "manifest.json"), """{ "Name": "Hidden" }""");

            using var scanner = new ModScannerService();
            var mods = scanner.ScanPath(tempRoot);

            Assert.Single(mods);
            var scanned = mods[0];
            Assert.Equal("Custom Farming Redux", scanned.Name);
            Assert.Equal("Aaron", scanned.Author);
            Assert.Equal("2.4.1", scanned.Version);
            Assert.Equal("Aaron.CustomFarming", scanned.UniqueID);
            Assert.True(scanned.HasConfigJson);

            // Check dependency warning when ContentPatcher is missing
            ModScannerService.UpdateDependencyWarnings(mods);
            Assert.True(scanned.HasWarning);
            Assert.Contains("Pathoschild.ContentPatcher", scanned.DependencyWarning);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void SteamLaunchOptions_UpdatesAndInserts413150LaunchOptionsCorrectly()
    {
        string sampleVdf = """
        "UserLocalConfigStore"
        {
        	"Software"
        	{
        		"Valve"
        		{
        			"Steam"
        			{
        				"apps"
        				{
        					"413150"
        					{
        						"LastPlayed"		"1700000000"
        					}
        				}
        			}
        		}
        	}
        }
        """;

        string rawLaunchOpt = SteamLaunchOptionsService.BuildWindowsLaunchOption(@"C:\Program Files (x86)\Steam\steamapps\common\Stardew Valley");
        string updated = SteamLaunchOptionsService.UpdateStardewLaunchOptionsInVdf(sampleVdf, rawLaunchOpt);

        Assert.Contains("\"LaunchOptions\"", updated);
        Assert.Contains("StardewModdingAPI.exe", updated);
        Assert.Contains("%command%", updated);

        // Updating an already-present LaunchOptions entry replaces it cleanly
        string updatedAgain = SteamLaunchOptionsService.UpdateStardewLaunchOptionsInVdf(updated, "\"D:\\Games\\Stardew Valley\\StardewModdingAPI.exe\" %command%");
        Assert.Contains("D:\\\\Games\\\\Stardew Valley\\\\StardewModdingAPI.exe", updatedAgain);
    }

    [Fact]
    public async Task PackageBuilderAndExtractor_RoundTripsModsAndHonorsConflictModes()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "SVEPU_PkgTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            string sourceModsDir = Path.Combine(tempRoot, "SourceMods");
            string mod1Dir = Path.Combine(sourceModsDir, "TestModOne");
            Directory.CreateDirectory(mod1Dir);
            File.WriteAllText(Path.Combine(mod1Dir, "manifest.json"), """
            {
                "Name": "Test Mod One",
                "Author": "Tester",
                "Version": "1.2.0",
                "UniqueID": "Tester.TestModOne"
            }
            """);
            File.WriteAllText(Path.Combine(mod1Dir, "config.json"), """{ "Key": "NewValue" }""");
            File.WriteAllText(Path.Combine(mod1Dir, "content.json"), """{ "Format": "2.0.0" }""");

            using var scanner = new ModScannerService();
            var scannedMods = scanner.ScanPath(sourceModsDir);
            Assert.Single(scannedMods);

            string outputDir = Path.Combine(tempRoot, "Output");
            var metadata = new PackageMetadata
            {
                PackName = "My Test Pack",
                Author = "Aaron",
                Version = "1.0.0",
                Description = "Test description",
                IncludeConfigs = false // Verify config.json is stripped when IncludeConfigs is false
            };

            var builder = new PackageBuilderService();
            var buildRes = await builder.BuildPackageAsync(
                metadata,
                scannedMods,
                outputDir,
                keepUnzippedFolder: true);

            Assert.True(buildRes.Success, buildRes.Message);
            Assert.True(File.Exists(buildRes.ZipPath));
            Assert.True(Directory.Exists(buildRes.FolderPath));

            var (loadedMeta, _, payloadDir) = PackageExtractorService.LoadPackagePayload(buildRes.FolderPath);
            Assert.Equal("My Test Pack", loadedMeta.PackName);
            Assert.Single(loadedMeta.Mods);
            Assert.NotNull(payloadDir);

            // Target game folder with an existing non-conflicting mod
            string fakeGameDir = Path.Combine(tempRoot, "FakeStardewValley");
            string existingOtherMod = Path.Combine(fakeGameDir, "Mods", "OtherExistingMod");
            Directory.CreateDirectory(existingOtherMod);
            File.WriteAllText(Path.Combine(existingOtherMod, "manifest.json"), "{}");

            var extractor = new PackageExtractorService();
            var extractRes = await extractor.ExtractModsAsync(
                payloadDir!,
                fakeGameDir,
                loadedMeta.Mods,
                ConflictResolutionMode.OverwriteConflicts);

            Assert.True(extractRes.Success, extractRes.Message);
            Assert.Equal(1, extractRes.InstalledCount);

            string installedModDir = Path.Combine(fakeGameDir, "Mods", "TestModOne");
            Assert.True(Directory.Exists(installedModDir));
            Assert.True(File.Exists(Path.Combine(installedModDir, "content.json")));
            // config.json should have been excluded because IncludeConfigs = false
            Assert.False(File.Exists(Path.Combine(installedModDir, "config.json")));
            // OtherExistingMod should remain intact under OverwriteConflicts
            Assert.True(Directory.Exists(existingOtherMod));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void IconInjector_ConvertsPngToValidIcoHeader()
    {
        var iconPath = AssetLocator.FindAssetFile("app-icon.png");
        Assert.NotNull(iconPath);
        Assert.True(File.Exists(iconPath));

        var icoBytes = IconInjectorService.CreateIcoFromImageFile(iconPath!);
        Assert.True(icoBytes.Length > 22);
        Assert.Equal(0, icoBytes[0]);
        Assert.Equal(0, icoBytes[1]);
        Assert.Equal(1, icoBytes[2]); // Type = 1 (ICO)
        Assert.Equal(0, icoBytes[3]);
        Assert.Equal(1, icoBytes[4]); // Count = 1

        // Also ensure app-icon.ico exists in assets/
        string icoPath = Path.Combine(Path.GetDirectoryName(iconPath!)!, "app-icon.ico");
        if (!File.Exists(icoPath))
        {
            File.WriteAllBytes(icoPath, icoBytes);
        }
        Assert.True(File.Exists(icoPath));
    }

    [Fact]
    public async Task AppSetupService_InstallsAndUninstallsPayloadCleanly()
    {
        string tempInstallDir = Path.Combine(Path.GetTempPath(), "SVEPU_SetupTest_" + Guid.NewGuid().ToString("N"));
        try
        {
            using var ms = new MemoryStream();
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                var entry = zip.CreateEntry("SVEPU.Packager.exe");
                await using var entryStream = entry.Open();
                await entryStream.WriteAsync(new byte[] { 1, 2, 3, 4 });
            }
            ms.Position = 0;

            var setup = new AppSetupService();
            var installRes = await setup.InstallAsync(
                ms,
                tempInstallDir,
                createDesktopShortcut: false,
                createMenuShortcut: false,
                registerSystemUninstaller: false);

            Assert.True(installRes.Success, installRes.Message);
            Assert.True(File.Exists(Path.Combine(tempInstallDir, "SVEPU.Packager.exe")));

            var uninstallRes = await setup.UninstallAsync(tempInstallDir);
            Assert.True(uninstallRes.Success, uninstallRes.Message);
            Assert.False(Directory.Exists(tempInstallDir));
        }
        finally
        {
            if (Directory.Exists(tempInstallDir))
            {
                Directory.Delete(tempInstallDir, recursive: true);
            }
        }
    }
}
