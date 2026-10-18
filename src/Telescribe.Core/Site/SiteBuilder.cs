using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Telescribe.Core.Export;
using Telescribe.Core.Models;
using Telescribe.Core.Services;

namespace Telescribe.Core.Site;

public sealed class SiteBuildResult
{
    public required string SiteDirectory { get; init; }
    public required string IndexPath { get; init; }
    public int PostPages { get; init; }
    public int NewPostPages { get; init; }
    public int UpdatedPostPages { get; init; }
    public int RemovedPostPages { get; init; }
    public int IndexedPosts { get; init; }
    public int MediaFilesCopied { get; init; }
    public bool HasAboutPage { get; init; }
    public string? SitemapPath { get; init; }
    public string? RobotsPath { get; init; }
    public string? FeedPath { get; init; }
    public int TagPages { get; init; }
    public List<string> Warnings { get; init; } = [];
}

/// <summary>
/// Builds (or refreshes) a static site from the exported Markdown posts. A first build and an
/// incremental update use the same code path; the only difference is whether the target already exists.
/// </summary>
public sealed partial class SiteBuilder
{
    /// <summary>Written into every generated site so that "clean" knows it is safe to delete the folder.</summary>
    public const string MarkerFileName = ".telescribe-site";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly StaticSiteConfig _config;
    private readonly string _templatesPath;
    private readonly SiteGeneratorService _generator;

    public SiteBuilder(StaticSiteConfig config, string templatesPath)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _templatesPath = templatesPath ?? throw new ArgumentNullException(nameof(templatesPath));
        _generator = new SiteGeneratorService(templatesPath);
    }

    public static string[] FindPostFiles(string exportDirectory) =>
        Directory.Exists(exportDirectory)
            ? Directory.GetFiles(exportDirectory, "*.md", SearchOption.TopDirectoryOnly)
            : [];

    /// <summary>The configured output folder, or <c>site</c> inside the exports folder.</summary>
    public static string ResolveSiteDirectory(StaticSiteConfig config, string exportDirectory) =>
        string.IsNullOrWhiteSpace(config.OutputDirectory) ? Path.Combine(exportDirectory, "site") : config.OutputDirectory;

    /// <summary>
    /// Deletes a previously generated site so the next build starts from scratch. Refuses to touch a non-empty
    /// folder that Telescribe did not create.
    /// </summary>
    public static void CleanSiteDirectory(string siteDirectory)
    {
        if (!Directory.Exists(siteDirectory))
        {
            return;
        }

        if (!File.Exists(Path.Combine(siteDirectory, MarkerFileName)) && Directory.EnumerateFileSystemEntries(siteDirectory).Any())
        {
            throw new InvalidOperationException($"Refusing to clean '{siteDirectory}': it was not created by Telescribe (no {MarkerFileName} file).");
        }

        Directory.Delete(siteDirectory, recursive: true);
    }

    public async Task<SiteBuildResult> BuildAsync(string exportDirectory, string siteDirectory, CancellationToken cancellationToken = default)
    {
        var postFiles = FindPostFiles(exportDirectory);
        if (postFiles.Length == 0)
        {
            throw new InvalidOperationException($"No exported posts (*.md) found in '{exportDirectory}'.");
        }

        var warnings = new List<string>();
        var postsDirectory = Path.Combine(siteDirectory, "posts");
        Directory.CreateDirectory(postsDirectory);
        Directory.CreateDirectory(Path.Combine(siteDirectory, "assets"));

        var css = await CopyTemplateAssetsAsync(siteDirectory, cancellationToken);
        var mediaCopied = CopyMedia(Path.Combine(exportDirectory, "media"), Path.Combine(siteDirectory, "media"));

        var allPosts = new List<PostData>(postFiles.Length);
        foreach (var file in postFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            allPosts.Add(await LoadPostAsync(file, cancellationToken));
        }

        var sitePosts = allPosts
            .Where(p => !_config.SkipEmptyContentPosts || !p.IsNoContent)
            .OrderByDescending(p => p.Date)
            .ToList();

        var generatedAt = DateTime.Now;

        // Tag pages, and a tag list at the end of each post that belongs to one.
        var tags = _config.GenerateTagPages ? TagIndex.Build(sitePosts, _config.MinPostsPerTag) : [];
        AppendTagLinks(sitePosts, tags);

        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int newPages = 0, updatedPages = 0;
        foreach (var post in sitePosts)
        {
            var path = Path.Combine(postsDirectory, post.Filename + ".html");
            if (File.Exists(path)) updatedPages++; else newPages++;

            await WriteHtmlAsync(path, InlineCss(_generator.RenderPostPage(_config, post), css), cancellationToken);
            written.Add(Path.GetFileName(path));
        }

        var removed = RemoveStalePages(postsDirectory, "*.html", written);

        var listedCount = _config.MaxPostsInIndex > 0 ? Math.Min(_config.MaxPostsInIndex, sitePosts.Count) : sitePosts.Count;
        var listed = sitePosts.Take(listedCount).ToList();
        var indexPath = Path.Combine(siteDirectory, "index.html");
        await WriteHtmlAsync(indexPath, InlineCss(_generator.RenderIndexPage(_config, listed, generatedAt, sitePosts.Count), css), cancellationToken);

        var tagFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tag in tags)
        {
            var page = _generator.RenderIndexPage(_config, tag.Posts, generatedAt, tag.Posts.Count, new ListingPage("#" + tag.Display, tag.FileName));
            await WriteHtmlAsync(Path.Combine(siteDirectory, tag.FileName), InlineCss(page, css), cancellationToken);
            tagFiles.Add(tag.FileName);
        }

        RemoveStalePages(siteDirectory, "tag-*.html", tagFiles);

        var hasAbout = _generator.HasTemplate("about.html");
        if (hasAbout)
        {
            await WriteHtmlAsync(Path.Combine(siteDirectory, "about.html"), InlineCss(_generator.RenderAboutPage(_config), css), cancellationToken);
        }

        string? sitemapPath = null, robotsPath = null, feedPath = null;
        var feedFile = Path.Combine(siteDirectory, AtomFeed.FileName);
        if (!string.IsNullOrWhiteSpace(_config.SiteBaseUrl))
        {
            sitemapPath = Path.Combine(siteDirectory, "sitemap.xml");
            await File.WriteAllTextAsync(sitemapPath, _generator.GenerateSitemap(sitePosts, _config.SiteBaseUrl, hasAbout, tags.Select(t => t.FileName)), Utf8NoBom, cancellationToken);

            robotsPath = Path.Combine(siteDirectory, "robots.txt");
            await File.WriteAllTextAsync(robotsPath, _generator.GenerateRobotsTxt(_config.SiteBaseUrl), Utf8NoBom, cancellationToken);

            if (_config.FeedItemCount > 0)
            {
                feedPath = feedFile;
                await File.WriteAllTextAsync(feedPath, AtomFeed.Build(_config, _generator.Template, sitePosts, _config.FeedItemCount, DateTime.UtcNow), Utf8NoBom, cancellationToken);
            }
        }
        else
        {
            warnings.Add("StaticSite:SiteBaseUrl is not configured; canonical tags, sitemap.xml, robots.txt and feed.xml were skipped.");
        }

        if (feedPath is null && File.Exists(feedFile))
        {
            File.Delete(feedFile);
        }

        await File.WriteAllTextAsync(Path.Combine(siteDirectory, MarkerFileName), "Generated by Telescribe. Safe to delete with 'build --clean'.\n", cancellationToken);

        return new SiteBuildResult
        {
            SiteDirectory = siteDirectory,
            IndexPath = indexPath,
            PostPages = sitePosts.Count,
            NewPostPages = newPages,
            UpdatedPostPages = updatedPages,
            RemovedPostPages = removed,
            IndexedPosts = listed.Count,
            MediaFilesCopied = mediaCopied,
            HasAboutPage = hasAbout,
            SitemapPath = sitemapPath,
            RobotsPath = robotsPath,
            FeedPath = feedPath,
            TagPages = tags.Count,
            Warnings = warnings
        };
    }

    /// <summary>Adds a "#tag" list under each post for the tags that have a page of their own.</summary>
    private static void AppendTagLinks(IReadOnlyList<PostData> posts, IReadOnlyList<TagInfo> tags)
    {
        if (tags.Count == 0)
        {
            return;
        }

        var byKey = tags.ToDictionary(t => t.Key, StringComparer.OrdinalIgnoreCase);
        foreach (var post in posts)
        {
            var links = post.Tags
                .Where(byKey.ContainsKey)
                .Select(tag => byKey[tag])
                .Select(tag => $"<a href=\"../{WebUtility.HtmlEncode(tag.Href)}\">#{WebUtility.HtmlEncode(tag.Display)}</a>")
                .ToList();

            if (links.Count > 0)
            {
                post.Content += "\n<p class=\"post-tags\" style=\"display:flex;flex-wrap:wrap;gap:.6rem;margin-top:1.5rem;font-size:.9em\">" +
                                string.Join(' ', links) + "</p>\n";
            }
        }
    }
    /// <summary>Parses one exported Markdown file into the data a post page and card need.</summary>
    public static async Task<PostData> LoadPostAsync(string markdownPath, CancellationToken cancellationToken = default)
    {
        var content = await File.ReadAllTextAsync(markdownPath, cancellationToken);
        return CreatePostData(Path.GetFileNameWithoutExtension(markdownPath), content);
    }

    public static PostData CreatePostData(string fileName, string content)
    {
        var title = PostMarkdown.ExtractTitle(content) ?? fileName;
        var (views, reactions, forwards) = PostMarkdown.ExtractMetrics(content);
        var isNoContent = PostMarkdown.IsNoContent(content);
        var created = PostMarkdown.ExtractCreationDate(content);

        return new PostData
        {
            Filename = fileName,
            Title = title,
            Preview = isNoContent ? "📊 Poll or media-only post" : PostMarkdown.ExtractPreview(content),
            Content = PostHtmlRenderer.Render(content, title),
            Date = created,
            Updated = PostMarkdown.ExtractEditedDate(content) ?? created,
            Tags = TagIndex.ExtractTags(content, PostMarkdown.ReadEnrichment(content, 0, null)?.Hashtags),
            Views = views,
            Reactions = reactions,
            Forwards = forwards,
            IsNoContent = isNoContent
        };
    }

    private async Task<string> CopyTemplateAssetsAsync(string siteDirectory, CancellationToken cancellationToken)
    {
        var css = string.Empty;
        var stylePath = Path.Combine(_templatesPath, "style.css");
        if (File.Exists(stylePath))
        {
            css = await File.ReadAllTextAsync(stylePath, cancellationToken);
            File.Copy(stylePath, Path.Combine(siteDirectory, "assets", "style.css"), overwrite: true);
        }

        foreach (var asset in new[] { "favicon.svg", "social-preview.png" })
        {
            var source = Path.Combine(_templatesPath, asset);
            if (File.Exists(source))
            {
                File.Copy(source, Path.Combine(siteDirectory, asset), overwrite: true);
            }
        }

        return css;
    }

    /// <summary>Copies media that is missing from, or differs in size from, the site's copy.</summary>
    private static int CopyMedia(string source, string target)
    {
        if (!Directory.Exists(source))
        {
            return 0;
        }

        Directory.CreateDirectory(target);
        var copied = 0;
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            var info = new FileInfo(file);
            if (File.Exists(destination) && new FileInfo(destination).Length == info.Length)
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
            copied++;
        }

        return copied;
    }

    private static int RemoveStalePages(string directory, string pattern, HashSet<string> keep)
    {
        var removed = 0;
        foreach (var page in Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly))
        {
            if (!keep.Contains(Path.GetFileName(page)))
            {
                File.Delete(page);
                removed++;
            }
        }

        return removed;
    }

    /// <summary>Replaces the stylesheet link with the CSS itself so each page is self-contained.</summary>
    public static string InlineCss(string html, string css)
    {
        if (string.IsNullOrEmpty(css))
        {
            return html;
        }

        // Evaluator rather than a replacement string: CSS may contain '$', which would be read as a group reference.
        return StylesheetLinkRegex().Replace(html, _ => "<style>" + css + "</style>");
    }

    private static Task WriteHtmlAsync(string path, string html, CancellationToken cancellationToken) =>
        File.WriteAllTextAsync(path, SanitizeUnicode(html), Utf8NoBom, cancellationToken);

    /// <summary>Replaces unpaired surrogates (which cannot be encoded as UTF-8) with U+FFFD.</summary>
    public static string SanitizeUnicode(string content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return content;
        }

        var builder = new StringBuilder(content.Length);
        for (var i = 0; i < content.Length; i++)
        {
            var current = content[i];
            if (char.IsHighSurrogate(current))
            {
                if (i + 1 < content.Length && char.IsLowSurrogate(content[i + 1]))
                {
                    builder.Append(current).Append(content[i + 1]);
                    i++;
                }
                else
                {
                    builder.Append('�');
                }
            }
            else if (char.IsLowSurrogate(current))
            {
                builder.Append('�');
            }
            else
            {
                builder.Append(current);
            }
        }

        return builder.ToString();
    }

    [GeneratedRegex(@"<link\s+rel=['""]stylesheet['""]\s+href=['""][./]*assets/style\.css['""]\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex StylesheetLinkRegex();
}
