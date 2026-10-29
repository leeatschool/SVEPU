using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace SVEPU.Core.Services;

public sealed class SmapiInstallerService
{
    private const string LatestReleaseApiUrl = "https://api.github.com/repos/Pathoschild/SMAPI/releases/latest";
    private const long MaxSmapiDownloadBytes = 250L * 1024 * 1024; // 250 MB cap
    private readonly HttpClient _httpClient;

    public SmapiInstallerService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? CreateDefaultHttpClient();
    }

    private static HttpClient CreateDefaultHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(10)
        };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SVEPU-StardewEasyPortUtility", "1.1"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    public static bool IsAllowedSmapiDownloadUrl(string? downloadUrl)
    {
        if (string.IsNullOrWhiteSpace(downloadUrl) ||
            !Uri.TryCreate(downloadUrl, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string host = uri.Host;
        return string.Equals(host, "github.com", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(host, "api.github.com", StringComparison.OrdinalIgnoreCase) ||
               host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<(bool Success, string Version, string Message)> DownloadAndInstallLatestSmapiAsync(
        string gamePath,
        IProgress<(double Percent, string Status)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedGamePath = GamePathDetectorService.NormalizeGamePath(gamePath);
        if (string.IsNullOrWhiteSpace(normalizedGamePath) || !Directory.Exists(normalizedGamePath))
        {
            return (false, string.Empty, "The selected Stardew Valley game directory does not exist.");
        }

        string tempRoot = Path.Combine(Path.GetTempPath(), "SVEPU_SMAPI_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            progress?.Report((5, "Checking GitHub for the latest SMAPI release..."));

            using var releaseResponse = await _httpClient.GetAsync(LatestReleaseApiUrl, cancellationToken);
            releaseResponse.EnsureSuccessStatusCode();

            var releaseJson = await releaseResponse.Content.ReadAsStringAsync(cancellationToken);
            var (tagName, downloadUrl, assetName) = ParseLatestReleaseAsset(releaseJson);

            if (string.IsNullOrWhiteSpace(downloadUrl) || !IsAllowedSmapiDownloadUrl(downloadUrl))
            {
                return (false, string.Empty, "Could not find a valid official GitHub download URL for the SMAPI installer.");
            }

            string safeAssetFileName = PackageBuilderService.SanitizeFileName(Path.GetFileName(assetName ?? "SMAPI-installer.zip"));
            if (!safeAssetFileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                safeAssetFileName += ".zip";
            }

            string zipFilePath = Path.Combine(tempRoot, safeAssetFileName);
            progress?.Report((10, $"Downloading SMAPI {tagName}..."));

            using (var downloadResponse = await _httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
            {
                downloadResponse.EnsureSuccessStatusCode();
                long? totalBytes = downloadResponse.Content.Headers.ContentLength;
                if (totalBytes.HasValue && totalBytes.Value > MaxSmapiDownloadBytes)
                {
                    return (false, tagName, "SMAPI installer archive exceeded the maximum safety size limit.");
                }

                await using var contentStream = await downloadResponse.Content.ReadAsStreamAsync(cancellationToken);
                await using var fileStream = new FileStream(zipFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

                var buffer = new byte[81920];
                long totalRead = 0;
                int bytesRead;

                while ((bytesRead = await contentStream.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    totalRead += bytesRead;
                    if (totalRead > MaxSmapiDownloadBytes)
                    {
                        return (false, tagName, "SMAPI download exceeded the maximum safety size limit.");
                    }

                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);

                    if (totalBytes.HasValue && totalBytes.Value > 0)
                    {
                        double pct = 10.0 + (totalRead / (double)totalBytes.Value) * 60.0;
                        double mbRead = totalRead / (1024.0 * 1024.0);
                        double mbTotal = totalBytes.Value / (1024.0 * 1024.0);
                        progress?.Report((Math.Min(70.0, pct), $"Downloading SMAPI {tagName} ({mbRead:F1} / {mbTotal:F1} MB)..."));
                    }
                }
            }

            progress?.Report((75, $"Extracting SMAPI {tagName} installer..."));
            string extractDir = Path.Combine(tempRoot, "extracted");
            SafeArchiveHelper.ExtractZipFileSafely(zipFilePath, extractDir, maxTotalBytes: 500L * 1024 * 1024);

            progress?.Report((85, $"Installing SMAPI {tagName} into Stardew Valley..."));
            await RunSilentSmapiInstallAsync(extractDir, normalizedGamePath, cancellationToken);

            Directory.CreateDirectory(Path.Combine(normalizedGamePath, "Mods"));

            if (GamePathDetectorService.IsSmapiInstalled(normalizedGamePath))
            {
                progress?.Report((100, $"SMAPI {tagName} installed successfully!"));
                return (true, tagName, $"SMAPI {tagName} has been installed successfully.");
            }

            return (false, tagName, "SMAPI download finished, but SMAPI files could not be verified in the game folder.");
        }
        catch (Exception ex)
        {
            return (false, string.Empty, $"Failed to install SMAPI: {ex.Message}");
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
            }
            catch
            {
                // Ignore temp cleanup failure
            }
        }
    }

    public static (string TagName, string? DownloadUrl, string? AssetName) ParseLatestReleaseAsset(string releaseJson)
    {
        using var doc = JsonDocument.Parse(releaseJson);
        var root = doc.RootElement;

        string tagName = root.TryGetProperty("tag_name", out var tagProp)
            ? (tagProp.GetString() ?? "latest")
            : "latest";

        if (root.TryGetProperty("assets", out var assetsProp) && assetsProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assetsProp.EnumerateArray())
            {
                string? name = asset.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
                string? url = asset.TryGetProperty("browser_download_url", out var urlProp) ? urlProp.GetString() : null;

                if (!string.IsNullOrWhiteSpace(name) &&
                    !string.IsNullOrWhiteSpace(url) &&
                    name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                    !name.Contains("for-developers", StringComparison.OrdinalIgnoreCase) &&
                    IsAllowedSmapiDownloadUrl(url))
                {
                    return (tagName, url, Path.GetFileName(name));
                }
            }
        }

        return (tagName, null, null);
    }

    private static async Task RunSilentSmapiInstallAsync(string extractDir, string gamePath, CancellationToken cancellationToken)
    {
        string osFolderName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "windows"
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                ? "macOS"
                : "linux";

        var internalDirs = Directory.GetDirectories(extractDir, osFolderName, SearchOption.AllDirectories);
        string? osInternalDir = internalDirs.FirstOrDefault();

        if (!string.IsNullOrWhiteSpace(osInternalDir))
        {
            string installerExe = Path.Combine(osInternalDir, RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "SMAPI.Installer.exe" : "SMAPI.Installer");
            string installerDll = Path.Combine(osInternalDir, "SMAPI.Installer.dll");

            try
            {
                ProcessStartInfo? psi = null;
                if (File.Exists(installerExe))
                {
                    psi = new ProcessStartInfo
                    {
                        FileName = installerExe,
                        WorkingDirectory = osInternalDir,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    psi.ArgumentList.Add("--install");
                    psi.ArgumentList.Add("--game-path");
                    psi.ArgumentList.Add(gamePath);
                    psi.ArgumentList.Add("--no-prompt");
                }
                else if (File.Exists(installerDll))
                {
                    psi = new ProcessStartInfo
                    {
                        FileName = "dotnet",
                        WorkingDirectory = osInternalDir,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    psi.ArgumentList.Add(installerDll);
                    psi.ArgumentList.Add("--install");
                    psi.ArgumentList.Add("--game-path");
                    psi.ArgumentList.Add(gamePath);
                    psi.ArgumentList.Add("--no-prompt");
                }

                if (psi != null)
                {
                    using var proc = Process.Start(psi);
                    if (proc != null)
                    {
                        await proc.WaitForExitAsync(cancellationToken);
                    }
                }
            }
            catch
            {
                // Fall back to direct install.dat extraction below
            }

            if (!GamePathDetectorService.IsSmapiInstalled(gamePath))
            {
                string installDat = Path.Combine(osInternalDir, "install.dat");
                if (File.Exists(installDat))
                {
                    SafeArchiveHelper.ExtractZipFileSafely(installDat, gamePath, maxTotalBytes: 500L * 1024 * 1024);

                    string sdvDeps = Path.Combine(gamePath, "Stardew Valley.deps.json");
                    string smapiDeps = Path.Combine(gamePath, "StardewModdingAPI.deps.json");
                    if (File.Exists(sdvDeps))
                    {
                        File.Copy(sdvDeps, smapiDeps, overwrite: true);
                    }
                }
            }
        }
    }
}
