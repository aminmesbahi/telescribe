using System.IO.Compression;

namespace Telescribe.Core.Export;

public sealed record ArchiveResult(string Path, int Files, long Bytes);

/// <summary>
/// Packs the exported content (posts, media and statistics) into one ZIP so it can be backed up or moved.
/// Generated output (sites, reports, archives) and credentials such as the Telegram session are never included.
/// </summary>
public static class ArchiveService
{
    private static readonly HashSet<string> AlreadyCompressed = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".mp4", ".mov", ".webm", ".mp3", ".ogg", ".m4a", ".zip", ".pdf"
    };

    public static string DefaultPath(string exportDirectory) =>
        Path.Combine(exportDirectory, "archives", $"telescribe-archive_{DateTime.Now:yyyyMMdd_HHmmss}.zip");

    public static async Task<ArchiveResult> CreateAsync(string exportDirectory, string archivePath, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(exportDirectory))
        {
            throw new DirectoryNotFoundException($"Export directory not found: {exportDirectory}");
        }

        var files = EnumerateContent(exportDirectory).ToList();
        if (files.Count == 0)
        {
            throw new InvalidOperationException("Nothing to archive: no exported posts found.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(archivePath))!);
        var temp = archivePath + ".tmp";

        long total = 0;
        await using (var stream = File.Create(temp))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            foreach (var (fullPath, entryName) in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var level = AlreadyCompressed.Contains(Path.GetExtension(fullPath)) ? CompressionLevel.NoCompression : CompressionLevel.Optimal;
                var entry = zip.CreateEntry(entryName, level);
                entry.LastWriteTime = File.GetLastWriteTime(fullPath);

                await using var source = File.OpenRead(fullPath);
                await using var target = entry.Open();
                await source.CopyToAsync(target, cancellationToken);
                total += source.Length;
            }
        }

        File.Move(temp, archivePath, overwrite: true);
        return new ArchiveResult(archivePath, files.Count, total);
    }

    /// <summary>Top-level posts, raw/processed copies, media, and the summary/statistics files.</summary>
    internal static IEnumerable<(string FullPath, string EntryName)> EnumerateContent(string exportDirectory)
    {
        foreach (var file in Directory.EnumerateFiles(exportDirectory, "*.md", SearchOption.TopDirectoryOnly))
        {
            yield return (file, Path.GetFileName(file));
        }

        foreach (var name in new[] { "summary.json", "stats-history.jsonl" })
        {
            var path = Path.Combine(exportDirectory, name);
            if (File.Exists(path))
            {
                yield return (path, name);
            }
        }

        foreach (var folder in new[] { "raw", "processed", "media" })
        {
            var directory = Path.Combine(exportDirectory, folder);
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                if (file.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
                {
                    continue; // an interrupted download
                }

                yield return (file, folder + "/" + Path.GetRelativePath(directory, file).Replace('\\', '/'));
            }
        }
    }
}
