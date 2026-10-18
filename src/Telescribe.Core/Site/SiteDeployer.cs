namespace Telescribe.Core.Site;

public sealed record DeployResult(int Copied, int Unchanged, int Deleted);

/// <summary>
/// Publishes a generated site to another folder (a web server's document root, a Git checkout for GitHub Pages,
/// a synced drive...). Only files that are new or different are written, so repeated runs are cheap.
/// </summary>
public static class SiteDeployer
{
    // Entries a mirror must never delete from the target: version-control data, Pages configuration, custom domains.
    private static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase) { "CNAME", ".nojekyll" };

    public static DeployResult Deploy(string siteDirectory, string targetDirectory, bool mirror)
    {
        if (!Directory.Exists(siteDirectory))
        {
            throw new DirectoryNotFoundException($"Site folder not found: {siteDirectory}. Build the site first.");
        }

        var source = Path.GetFullPath(siteDirectory);
        var target = Path.GetFullPath(targetDirectory);
        if (string.Equals(source.TrimEnd(Path.DirectorySeparatorChar), target.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) ||
            target.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            source.StartsWith(target + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The deploy target must be a different folder that does not contain, or sit inside, the site folder.");
        }

        Directory.CreateDirectory(target);

        int copied = 0, unchanged = 0;
        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            if (relative == BuilderMarker)
            {
                continue; // internal bookkeeping, not part of the site
            }

            expected.Add(relative);
            var destination = Path.Combine(target, relative);

            if (File.Exists(destination) && new FileInfo(destination).Length == new FileInfo(file).Length &&
                File.ReadAllBytes(destination).AsSpan().SequenceEqual(File.ReadAllBytes(file)))
            {
                unchanged++;
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
            copied++;
        }

        var deleted = mirror ? DeleteExtras(target, expected) : 0;
        return new DeployResult(copied, unchanged, deleted);
    }

    private const string BuilderMarker = SiteBuilder.MarkerFileName;

    private static int DeleteExtras(string target, HashSet<string> expected)
    {
        var deleted = 0;
        foreach (var file in Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories).ToList())
        {
            var relative = Path.GetRelativePath(target, file);
            if (expected.Contains(relative) || IsProtected(relative))
            {
                continue;
            }

            File.Delete(file);
            deleted++;
        }

        // Remove folders left empty (but never protected ones such as .git).
        foreach (var directory in Directory.EnumerateDirectories(target, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length).ToList())
        {
            if (!IsProtected(Path.GetRelativePath(target, directory)) && !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }

        return deleted;
    }

    private static bool IsProtected(string relativePath)
    {
        var first = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        return first.StartsWith('.') || Protected.Contains(first);
    }
}
