namespace SVEPU.Core.Services;

public static class AssetLocator
{
    public static string? FindAssetFile(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        var searchDirs = new[]
        {
            AppContext.BaseDirectory,
            Environment.CurrentDirectory
        };

        foreach (var startDir in searchDirs.Where(d => !string.IsNullOrWhiteSpace(d)))
        {
            var current = new DirectoryInfo(startDir);
            for (int depth = 0; depth < 6 && current != null; depth++)
            {
                var candidate = Path.Combine(current.FullName, "assets", fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                var directCandidate = Path.Combine(current.FullName, fileName);
                if (File.Exists(directCandidate))
                {
                    return directCandidate;
                }

                current = current.Parent;
            }
        }

        return null;
    }
}
