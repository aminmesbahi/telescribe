using System.Text;
using System.Text.Json;
using Telescribe.Core.Export;
using Telescribe.Core.Models;
using Telescribe.Core.Site;

namespace Telescribe.Core.Services;

/// <summary>What has already been published: Telegram post to WordPress post, and uploaded media by file name.</summary>
public sealed class WordPressState
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public Dictionary<long, WordPressPostMapping> Posts { get; set; } = [];

    public Dictionary<string, WordPressMediaItem> Media { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static string DefaultPath(string exportDirectory) => Path.Combine(exportDirectory, "wordpress_state.json");

    /// <summary>
    /// Loads the state file. If it does not exist yet, the older mapping-only files (next to the exports, or next to
    /// the executable in the earliest versions) are imported so already-published posts are not duplicated.
    /// </summary>
    public static WordPressState Load(string path, params string[] legacyMappingPaths)
    {
        try
        {
            if (File.Exists(path))
            {
                var state = JsonSerializer.Deserialize<WordPressState>(File.ReadAllText(path)) ?? new WordPressState();
                state.Media = new Dictionary<string, WordPressMediaItem>(state.Media, StringComparer.OrdinalIgnoreCase);
                return state;
            }

            foreach (var legacy in legacyMappingPaths.Where(File.Exists))
            {
                var mappings = JsonSerializer.Deserialize<Dictionary<long, WordPressPostMapping>>(File.ReadAllText(legacy));
                if (mappings is not null)
                {
                    Console.WriteLine($"Imported {mappings.Count} existing WordPress mappings from {legacy}");
                    return new WordPressState { Posts = mappings };
                }
            }
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Could not read WordPress state file '{path}': {ex.Message}", ex);
        }

        return new WordPressState();
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        // Write-then-move so an interrupted run cannot leave a truncated file (which would cause duplicate posts).
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(temp, path, overwrite: true);
    }
}

public enum WordPressAction
{
    Create,
    Update,
    Skip
}

public sealed record WordPressPlanItem(ExportedPost Post, WordPressAction Action);

public static class WordPressSyncPlanner
{
    /// <summary>Decides, per post, whether to create it, update it, or leave it alone.</summary>
    public static List<WordPressPlanItem> Plan(IEnumerable<ExportedPost> posts, WordPressState state, bool force = false)
    {
        return posts.Select(post =>
        {
            if (!state.Posts.TryGetValue(post.Id, out var mapping))
            {
                return new WordPressPlanItem(post, WordPressAction.Create);
            }

            var changed = force || !string.Equals(mapping.ContentHash, post.ContentHash, StringComparison.Ordinal);
            return new WordPressPlanItem(post, changed ? WordPressAction.Update : WordPressAction.Skip);
        }).ToList();
    }
}

public static class WordPressContent
{
    private const int MaxTitleLength = 120;

    public static string Title(ExportedPost post)
    {
        var title = post.Title;
        return title.Length <= MaxTitleLength ? title : title[..(MaxTitleLength - 1)].TrimEnd() + "…";
    }

    /// <summary>
    /// Latin-letter slug plus the Telegram id, so it stays stable and unique. Titles without Latin letters
    /// (e.g. Persian) still get a valid slug.
    /// </summary>
    public static string Slug(string title, long postId)
    {
        var builder = new StringBuilder();
        foreach (var c in title.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                builder.Append(c);
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        var slug = builder.ToString().Trim('-');
        if (slug.Length > 40)
        {
            slug = slug[..40].TrimEnd('-');
        }

        return slug.Length == 0 ? $"telegram-{postId}" : $"{slug}-tg-{postId}";
    }

    /// <summary>
    /// HTML for the WordPress post body. Uses the same sanitising renderer as the static site, minus the metadata
    /// header, and points media links at the files uploaded to the WordPress media library.
    /// </summary>
    public static string ToHtml(ExportedPost post, IReadOnlyDictionary<string, string> uploadedMediaUrls)
    {
        return PostHtmlRenderer.Render(post.Markdown, post.Title, local =>
        {
            var name = Uri.UnescapeDataString(local[(local.LastIndexOf('/') + 1)..]);
            return uploadedMediaUrls.TryGetValue(name, out var uploaded) ? uploaded : local;
        });
    }
}
