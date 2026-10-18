using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Telescribe.Core.Export;

/// <summary>An exported post as stored on disk (<c>{id}.md</c>), with the fields other tools need already parsed.</summary>
public sealed record ExportedPost(
    long Id,
    string Markdown,
    DateTime CreatedAt,
    DateTime? EditedAt,
    string Title,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> MediaFiles)
{
    public static ExportedPost Parse(long id, string markdown)
    {
        var enrichment = PostMarkdown.ReadEnrichment(markdown, id, articleTitle: null);

        return new ExportedPost(
            id,
            markdown,
            PostMarkdown.ExtractCreationDate(markdown),
            PostMarkdown.ExtractEditedDate(markdown),
            PostMarkdown.ExtractTitle(markdown) ?? $"Telegram post {id}",
            enrichment?.Hashtags ?? [],
            PostMarkdown.ExtractMediaFiles(markdown));
    }

    /// <summary>Loads every numeric <c>{id}.md</c> in the export folder, oldest first.</summary>
    public static async Task<List<ExportedPost>> LoadAllAsync(string exportDirectory, CancellationToken cancellationToken = default)
    {
        var posts = new List<ExportedPost>();
        if (!Directory.Exists(exportDirectory))
        {
            return posts;
        }

        foreach (var path in Directory.EnumerateFiles(exportDirectory, "*.md", SearchOption.TopDirectoryOnly))
        {
            if (!long.TryParse(Path.GetFileNameWithoutExtension(path), NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            {
                continue;
            }

            posts.Add(Parse(id, await File.ReadAllTextAsync(path, cancellationToken)));
        }

        return posts.OrderBy(p => p.CreatedAt).ThenBy(p => p.Id).ToList();
    }

    /// <summary>
    /// A hash of what a reader sees. View/reaction/forward counters change constantly and are ignored, so a
    /// statistics refresh does not make the post look modified.
    /// </summary>
    public string ContentHash
    {
        get
        {
            var visible = string.Join('\n', PostMarkdown.SplitLines(Markdown)
                .Where(line => !line.StartsWith("**Views:**", StringComparison.Ordinal) &&
                               !line.StartsWith("**Reactions:**", StringComparison.Ordinal) &&
                               !line.StartsWith("**Forwards:**", StringComparison.Ordinal))
                .Select(line => line.TrimEnd()));

            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(visible)));
        }
    }
}
