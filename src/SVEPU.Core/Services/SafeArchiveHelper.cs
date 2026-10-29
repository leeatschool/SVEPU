using System.IO.Compression;

namespace SVEPU.Core.Services;

public static class SafeArchiveHelper
{
    public const long DefaultMaxTotalExtractedBytes = 8L * 1024 * 1024 * 1024; // 8 GB
    public const int DefaultMaxArchiveEntries = 100_000;

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static bool IsSafeSingleFolderOrFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        string trimmed = name.Trim();
        if (trimmed is "." or "..")
        {
            return false;
        }

        if (trimmed.Contains('/') || trimmed.Contains('\\') || Path.IsPathRooted(trimmed))
        {
            return false;
        }

        if (!string.Equals(Path.GetFileName(trimmed), trimmed, StringComparison.Ordinal))
        {
            return false;
        }

        var invalidChars = Path.GetInvalidFileNameChars();
        return trimmed.IndexOfAny(invalidChars) < 0;
    }

    public static bool IsSubPathOf(string candidatePath, string baseDirPath)
    {
        if (string.IsNullOrWhiteSpace(candidatePath) || string.IsNullOrWhiteSpace(baseDirPath))
        {
            return false;
        }

        string normalizedBase = Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDirPath)) + Path.DirectorySeparatorChar;
        string fullCandidate = Path.GetFullPath(candidatePath);

        return fullCandidate.StartsWith(normalizedBase, PathComparison);
    }

    public static bool IsSymlinkEntry(ZipArchiveEntry entry)
    {
        uint unixMode = ((uint)entry.ExternalAttributes >> 16) & 0xF000u;
        return unixMode == 0xA000u;
    }

    public static void ExtractZipFileSafely(
        string zipFilePath,
        string destinationDirectory,
        long maxTotalBytes = DefaultMaxTotalExtractedBytes,
        int maxEntries = DefaultMaxArchiveEntries)
    {
        using var fs = new FileStream(zipFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        ExtractZipStreamSafely(fs, destinationDirectory, maxTotalBytes, maxEntries);
    }

    public static void ExtractZipStreamSafely(
        Stream zipStream,
        string destinationDirectory,
        long maxTotalBytes = DefaultMaxTotalExtractedBytes,
        int maxEntries = DefaultMaxArchiveEntries,
        Action<int, int, string>? onEntryProgress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(destinationDirectory);
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);

        if (archive.Entries.Count > maxEntries)
        {
            throw new InvalidDataException($"Archive contains {archive.Entries.Count} entries, exceeding the maximum allowed ({maxEntries}).");
        }

        long totalBytesWritten = 0;
        int totalEntries = Math.Max(1, archive.Entries.Count);
        int index = 0;

        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            index++;

            if (IsSymlinkEntry(entry))
            {
                continue;
            }

            if (string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            string relativePath = entry.FullName.Replace('\\', '/').TrimStart('/');
            string fullDestFile = Path.GetFullPath(Path.Combine(destinationDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar)));

            if (!IsSubPathOf(fullDestFile, destinationDirectory))
            {
                throw new InvalidDataException($"Blocked archive entry attempting path traversal outside destination: {entry.FullName}");
            }

            ExtractEntryToFileBounded(entry, fullDestFile, ref totalBytesWritten, maxTotalBytes);
            onEntryProgress?.Invoke(index, totalEntries, entry.Name);
        }
    }

    public static void ExtractEntryToFileBounded(
        ZipArchiveEntry entry,
        string fullDestFile,
        ref long totalBytesWritten,
        long maxTotalBytes = DefaultMaxTotalExtractedBytes)
    {
        if (IsSymlinkEntry(entry))
        {
            return;
        }

        string? parentDir = Path.GetDirectoryName(fullDestFile);
        if (!string.IsNullOrWhiteSpace(parentDir))
        {
            Directory.CreateDirectory(parentDir);
        }

        using var inStream = entry.Open();
        using var outStream = new FileStream(fullDestFile, FileMode.Create, FileAccess.Write, FileShare.None);

        byte[] buffer = new byte[81920];
        int read;
        while ((read = inStream.Read(buffer, 0, buffer.Length)) > 0)
        {
            totalBytesWritten += read;
            if (totalBytesWritten > maxTotalBytes)
            {
                throw new InvalidDataException($"Extracted archive size exceeded the safety limit ({maxTotalBytes / (1024 * 1024)} MB).");
            }
            outStream.Write(buffer, 0, read);
        }
    }
}
