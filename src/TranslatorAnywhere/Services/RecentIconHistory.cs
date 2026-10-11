using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TranslatorAnywhere.Services;

public static class RecentIconHistory
{
    public static List<string> Normalize(IEnumerable<string>? paths) => (paths ?? Array.Empty<string>())
        .Where(path => !string.IsNullOrWhiteSpace(path) && path.Length <= 32768)
        .Select(path => path.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase).Take(3).ToList();

    public static List<string> Add(IEnumerable<string>? paths, string importedPath) =>
        Normalize(new[] { importedPath }.Concat(paths ?? Array.Empty<string>()));

    // Older versions retained imported copies, but did not save an explicit history.
    public static List<string> Discover(string dataDirectory, string currentPath)
    {
        try
        {
            string directory = Path.Combine(dataDirectory, "icons");
            if (Directory.Exists(directory))
            {
                var files = new DirectoryInfo(directory).EnumerateFiles()
                    .Where(file => Guid.TryParseExact(Path.GetFileNameWithoutExtension(file.Name), "N", out _))
                    .Where(file => file.Extension.ToLowerInvariant() is ".png" or ".svg" or ".jpg" or ".jpeg" or ".ico" or ".bmp" or ".gif")
                    .OrderByDescending(file => file.LastWriteTimeUtc).ThenBy(file => file.Name, StringComparer.OrdinalIgnoreCase);
                var paths = Normalize(files.Select(file => file.FullName));
                if (paths.Count > 0) return paths;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return Normalize(new[] { currentPath });
    }
}
