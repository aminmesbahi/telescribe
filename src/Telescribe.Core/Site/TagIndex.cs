using System.Text.RegularExpressions;
using Telescribe.Core.Export;
using Telescribe.Core.Services;

namespace Telescribe.Core.Site;

/// <summary>A tag that gets its own page.</summary>
public sealed record TagInfo(string Key, string Display, string FileName, IReadOnlyList<PostData> Posts)
{
    /// <summary>The page's address as written in a link (percent-encoded, relative to the site root).</summary>
    public string Href => Uri.EscapeDataString(FileName);
}

/// <summary>Finds the hashtags of a post and groups posts by tag.</summary>
public static partial class TagIndex
{
    /// <summary>
    /// Hashtags written in the post text plus AI-generated tags, de-duplicated case-insensitively
    /// (the first spelling wins). Code blocks, inline code and link targets are ignored.
    /// </summary>
    public static List<string> ExtractTags(string markdown, IEnumerable<string>? enrichmentTags = null)
    {
        var tags = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string tag)
        {
            tag = tag.Trim().TrimStart('#');
            if (tag.Length >= 2 && seen.Add(tag))
            {
                tags.Add(tag);
            }
        }

        foreach (var tag in enrichmentTags ?? [])
        {
            Add(tag);
        }

        var text = FencedCodeRegex().Replace(markdown, " ");
        text = InlineCodeRegex().Replace(text, " ");
        text = LinkTargetRegex().Replace(text, "]");

        foreach (var line in PostMarkdown.SplitLines(text))
        {
            if (PostMarkdown.IsMetadataLine(line.Trim()))
            {
                continue;
            }

            foreach (Match match in HashtagRegex().Matches(line))
            {
                Add(match.Groups[1].Value);
            }
        }

        return tags;
    }

    /// <summary>Tags used by at least <paramref name="minPosts"/> posts, most used first.</summary>
    public static List<TagInfo> Build(IEnumerable<PostData> posts, int minPosts)
    {
        var groups = new Dictionary<string, (string Display, List<PostData> Posts)>(StringComparer.OrdinalIgnoreCase);
        foreach (var post in posts)
        {
            foreach (var tag in post.Tags)
            {
                if (!groups.TryGetValue(tag, out var group))
                {
                    group = (tag, []);
                    groups[tag] = group;
                }

                group.Posts.Add(post);
            }
        }

        var usedFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<TagInfo>();
        foreach (var (key, group) in groups
                     .Where(g => g.Value.Posts.Count >= Math.Max(1, minPosts))
                     .OrderByDescending(g => g.Value.Posts.Count)
                     .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var fileName = "tag-" + MediaFileName.Sanitize(key.ToLowerInvariant(), "tag") + ".html";
            for (var suffix = 2; !usedFileNames.Add(fileName); suffix++)
            {
                fileName = $"tag-{MediaFileName.Sanitize(key.ToLowerInvariant(), "tag")}-{suffix}.html";
            }

            result.Add(new TagInfo(key, group.Display, fileName, group.Posts.OrderByDescending(p => p.Date).ToList()));
        }

        return result;
    }

    // '#' must start a word (not follow a letter, digit, '/', '&' or another '#'), be followed directly by the tag
    // (so "# Heading" is not a tag), and contain at least one letter (so "#1" is not a tag).
    [GeneratedRegex(@"(?<![\p{L}\p{N}_/&#])#(?=[\p{L}\p{N}_]*\p{L})([\p{L}\p{N}_]+)")]
    private static partial Regex HashtagRegex();

    [GeneratedRegex(@"```.*?```", RegexOptions.Singleline)]
    private static partial Regex FencedCodeRegex();

    [GeneratedRegex(@"`[^`\n]*`")]
    private static partial Regex InlineCodeRegex();

    [GeneratedRegex(@"\]\([^)\n]*\)")]
    private static partial Regex LinkTargetRegex();
}
