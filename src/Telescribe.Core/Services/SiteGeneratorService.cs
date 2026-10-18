using System.Globalization;
using System.Net;
using System.Text;
using Telescribe.Core.Models;
using Telescribe.Core.Site;
using Telescribe.Core.Templating;

namespace Telescribe.Core.Services;

/// <summary>Renders the individual pages of a static site (index, post, about, sitemap, robots.txt).</summary>
public class SiteGeneratorService
{
    private readonly TemplateEngine _engine;

    public SiteGeneratorService(string templatesPath)
    {
        _engine = new TemplateEngine(templatesPath);
        Template = TemplateInfo.Load(Path.GetFileName(Path.TrimEndingDirectorySeparator(templatesPath)), templatesPath);
    }

    public TemplateInfo Template { get; }

    public bool HasTemplate(string templateName) => _engine.Exists(templateName);

    public string RenderTemplate(string templateName, IReadOnlyDictionary<string, TemplateValue> variables)
    {
        return _engine.Render(templateName, variables);
    }

    public string RenderIndexPage(StaticSiteConfig config, IReadOnlyList<PostData> listedPosts, DateTime generatedDate, int totalPosts)
    {
        var postsContent = new StringBuilder();
        var canonicalUrl = BuildCanonicalUrl(NormalizeBaseUrl(config.SiteBaseUrl), string.Empty);
        var pageTitle = BuildPageTitle(config.SiteTitle, config.Subtitle);
        var description = NormalizeWhitespace(config.Description);

        foreach (var post in listedPosts)
        {
            postsContent.AppendLine(RenderTemplate("post-card.html", new Dictionary<string, TemplateValue>
            {
                ["filename"] = post.Filename,
                ["title"] = post.Title,
                ["date"] = FormatDate(post.Date),
                ["preview"] = NormalizeWhitespace(post.Preview),
                ["views"] = post.Views.ToString(CultureInfo.InvariantCulture),
                ["reactions"] = post.Reactions.ToString(CultureInfo.InvariantCulture),
                ["forwards"] = post.Forwards.ToString(CultureInfo.InvariantCulture)
            }));
        }

        return RenderTemplate("index.html", new Dictionary<string, TemplateValue>
        {
            ["pageTitle"] = pageTitle,
            ["siteTitle"] = config.SiteTitle,
            ["subtitle"] = config.Subtitle,
            ["headerIcon"] = config.HeaderIcon,
            ["description"] = description,
            ["canonicalUrl"] = canonicalUrl,
            ["canonicalTag"] = TemplateValue.Raw(BuildCanonicalTag(config.SiteBaseUrl, string.Empty)),
            ["socialMetaTags"] = TemplateValue.Raw(BuildSocialMetaTags(config, pageTitle, description, canonicalUrl, "website")),
            ["faviconPath"] = "favicon.svg",
            ["jsonLdScript"] = TemplateValue.Raw(JsonLd.IndexScript(config, Template, canonicalUrl)),
            ["jsonCanonicalUrl"] = TemplateValue.Raw(JsonLd.String(canonicalUrl)),
            ["jsonSiteTitle"] = TemplateValue.Raw(JsonLd.String(config.SiteTitle)),
            ["jsonSubtitle"] = TemplateValue.Raw(JsonLd.String(config.Subtitle)),
            ["jsonDescription"] = TemplateValue.Raw(JsonLd.String(config.Description)),
            ["postsContent"] = TemplateValue.Raw(postsContent.ToString()),
            ["generatedDate"] = generatedDate.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            ["totalPosts"] = totalPosts.ToString(CultureInfo.InvariantCulture)
        });
    }

    public string RenderPostPage(StaticSiteConfig config, PostData post)
    {
        var relativePath = $"posts/{post.Filename}.html";
        var canonicalUrl = BuildCanonicalUrl(NormalizeBaseUrl(config.SiteBaseUrl), relativePath);
        var pageTitle = BuildPageTitle(post.Title, config.SiteTitle);
        var description = NormalizeWhitespace(post.Preview);

        return RenderTemplate("post.html", new Dictionary<string, TemplateValue>
        {
            ["pageTitle"] = pageTitle,
            ["siteTitle"] = config.SiteTitle,
            ["title"] = post.Title,
            ["date"] = post.Date.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            ["preview"] = description,
            ["canonicalUrl"] = canonicalUrl,
            ["canonicalTag"] = TemplateValue.Raw(BuildCanonicalTag(config.SiteBaseUrl, relativePath)),
            ["socialMetaTags"] = TemplateValue.Raw(BuildSocialMetaTags(config, pageTitle, description, canonicalUrl, "article", post.Date)),
            ["faviconPath"] = "../favicon.svg",
            ["jsonLdScript"] = TemplateValue.Raw(JsonLd.PostScript(config, Template, post, canonicalUrl)),
            ["jsonCanonicalUrl"] = TemplateValue.Raw(JsonLd.String(canonicalUrl)),
            ["jsonSiteTitle"] = TemplateValue.Raw(JsonLd.String(config.SiteTitle)),
            ["jsonTitle"] = TemplateValue.Raw(JsonLd.String(post.Title)),
            ["jsonPreview"] = TemplateValue.Raw(JsonLd.String(post.Preview)),
            ["jsonDate"] = TemplateValue.Raw(JsonLd.String(JsonLd.FormatDate(post.Date))),
            // Already sanitised by PostHtmlRenderer.
            ["content"] = TemplateValue.Raw(post.Content),
            ["views"] = post.Views.ToString(CultureInfo.InvariantCulture),
            ["reactions"] = post.Reactions.ToString(CultureInfo.InvariantCulture),
            ["forwards"] = post.Forwards.ToString(CultureInfo.InvariantCulture)
        });
    }

    public string RenderAboutPage(StaticSiteConfig config)
    {
        var canonicalUrl = BuildCanonicalUrl(NormalizeBaseUrl(config.SiteBaseUrl), "about.html");
        var pageTitle = Template.FormatAboutTitle(config.SiteTitle);

        return RenderTemplate("about.html", new Dictionary<string, TemplateValue>
        {
            ["pageTitle"] = pageTitle,
            ["siteTitle"] = config.SiteTitle,
            ["subtitle"] = config.Subtitle,
            ["headerIcon"] = config.HeaderIcon,
            ["description"] = config.Description,
            ["faviconPath"] = "favicon.svg",
            ["canonicalUrl"] = canonicalUrl,
            ["canonicalTag"] = TemplateValue.Raw(BuildCanonicalTag(config.SiteBaseUrl, "about.html")),
            ["socialMetaTags"] = TemplateValue.Raw(BuildSocialMetaTags(config, pageTitle, config.Description, canonicalUrl)),
            ["jsonSiteTitle"] = TemplateValue.Raw(JsonLd.String(config.SiteTitle)),
            ["jsonSubtitle"] = TemplateValue.Raw(JsonLd.String(config.Subtitle)),
            ["jsonDescription"] = TemplateValue.Raw(JsonLd.String(config.Description)),
            ["jsonCanonicalUrl"] = TemplateValue.Raw(JsonLd.String(canonicalUrl))
        });
    }

    /// <summary>Generates a standard XML sitemap for the static site.</summary>
    public string GenerateSitemap(IReadOnlyList<PostData> posts, string baseUrl, bool includeAboutPage = false)
    {
        var normalizedBaseUrl = NormalizeBaseUrl(baseUrl);
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine("<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">");

        AppendUrl(sb, BuildCanonicalUrl(normalizedBaseUrl, string.Empty), today, "daily", "1.0");

        if (includeAboutPage)
        {
            AppendUrl(sb, BuildCanonicalUrl(normalizedBaseUrl, "about.html"), today, "monthly", "0.5");
        }

        foreach (var post in posts)
        {
            var lastMod = post.Date == DateTime.MinValue ? today : FormatDate(post.Date);
            AppendUrl(sb, BuildCanonicalUrl(normalizedBaseUrl, $"posts/{post.Filename}.html"), lastMod, "monthly", "0.8");
        }

        sb.AppendLine("</urlset>");
        return sb.ToString();
    }

    public string GenerateRobotsTxt(string? baseUrl)
    {
        var normalizedBaseUrl = NormalizeBaseUrl(baseUrl);
        if (string.IsNullOrWhiteSpace(normalizedBaseUrl))
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        sb.AppendLine("User-agent: *");
        sb.AppendLine("Allow: /");
        sb.AppendLine();
        sb.AppendLine($"Sitemap: {BuildCanonicalUrl(normalizedBaseUrl, "sitemap.xml")}");
        return sb.ToString();
    }

    public string BuildSocialMetaTags(
        StaticSiteConfig config,
        string pageTitle,
        string description,
        string canonicalUrl,
        string openGraphType = "website",
        DateTime? publishedDate = null)
    {
        var imageUrl = ResolveSocialImageUrl(config);
        var locale = string.IsNullOrWhiteSpace(config.OpenGraphLocale) ? Template.OpenGraphLocale : config.OpenGraphLocale;
        var tags = new StringBuilder();

        tags.AppendLine($"<meta property=\"og:type\" content=\"{EscapeHtmlAttribute(openGraphType)}\">");
        tags.AppendLine($"<meta property=\"og:site_name\" content=\"{EscapeHtmlAttribute(config.SiteTitle)}\">");
        tags.AppendLine($"<meta property=\"og:title\" content=\"{EscapeHtmlAttribute(pageTitle)}\">");
        tags.AppendLine($"<meta property=\"og:description\" content=\"{EscapeHtmlAttribute(description)}\">");
        tags.AppendLine($"<meta property=\"og:url\" content=\"{EscapeHtmlAttribute(canonicalUrl)}\">");
        tags.AppendLine($"<meta property=\"og:locale\" content=\"{EscapeHtmlAttribute(locale)}\">");

        if (!string.IsNullOrWhiteSpace(imageUrl))
        {
            tags.AppendLine($"<meta property=\"og:image\" content=\"{EscapeHtmlAttribute(imageUrl)}\">");
            var imageMimeType = GetImageMimeType(imageUrl);
            if (!string.IsNullOrWhiteSpace(imageMimeType))
                tags.AppendLine($"<meta property=\"og:image:type\" content=\"{imageMimeType}\">");
            if (config.SocialImageWidth > 0)
                tags.AppendLine($"<meta property=\"og:image:width\" content=\"{config.SocialImageWidth}\">");
            if (config.SocialImageHeight > 0)
                tags.AppendLine($"<meta property=\"og:image:height\" content=\"{config.SocialImageHeight}\">");
            tags.AppendLine($"<meta property=\"og:image:alt\" content=\"{EscapeHtmlAttribute(config.SiteTitle)}\">");
        }

        if (openGraphType == "article" && publishedDate.HasValue)
        {
            var timestamp = JsonLd.FormatDate(publishedDate.Value);
            tags.AppendLine($"<meta property=\"article:published_time\" content=\"{EscapeHtmlAttribute(timestamp)}\">");
            tags.AppendLine($"<meta property=\"article:modified_time\" content=\"{EscapeHtmlAttribute(timestamp)}\">");
        }

        tags.AppendLine($"<meta name=\"twitter:card\" content=\"{(string.IsNullOrWhiteSpace(imageUrl) ? "summary" : "summary_large_image")}\">");
        if (!string.IsNullOrWhiteSpace(config.TwitterHandle))
            tags.AppendLine($"<meta name=\"twitter:site\" content=\"{EscapeHtmlAttribute(config.TwitterHandle)}\">");
        tags.AppendLine($"<meta name=\"twitter:title\" content=\"{EscapeHtmlAttribute(pageTitle)}\">");
        tags.AppendLine($"<meta name=\"twitter:description\" content=\"{EscapeHtmlAttribute(description)}\">");
        if (!string.IsNullOrWhiteSpace(imageUrl))
        {
            tags.AppendLine($"<meta name=\"twitter:image\" content=\"{EscapeHtmlAttribute(imageUrl)}\">");
            tags.AppendLine($"<meta name=\"twitter:image:alt\" content=\"{EscapeHtmlAttribute(config.SiteTitle)}\">");
        }

        return tags.ToString().TrimEnd();
    }

    private static void AppendUrl(StringBuilder sb, string loc, string lastMod, string changeFreq, string priority)
    {
        sb.AppendLine("  <url>");
        sb.AppendLine($"    <loc>{EscapeXml(loc)}</loc>");
        sb.AppendLine($"    <lastmod>{lastMod}</lastmod>");
        sb.AppendLine($"    <changefreq>{changeFreq}</changefreq>");
        sb.AppendLine($"    <priority>{priority}</priority>");
        sb.AppendLine("  </url>");
    }

    private static string FormatDate(DateTime date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string ResolveSocialImageUrl(StaticSiteConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.SocialImageUrl))
            return string.Empty;

        if (Uri.TryCreate(config.SocialImageUrl, UriKind.Absolute, out var absolute))
            return absolute.ToString();

        return BuildCanonicalUrl(NormalizeBaseUrl(config.SiteBaseUrl), config.SocialImageUrl);
    }

    private static string GetImageMimeType(string imageUrl)
    {
        var path = Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri) ? uri.AbsolutePath : imageUrl;
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            _ => string.Empty
        };
    }

    private static string EscapeHtmlAttribute(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

    private static string EscapeXml(string text)
    {
        return text
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&apos;");
    }

    private static string BuildCanonicalTag(string? baseUrl, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return string.Empty;
        }

        var canonicalUrl = BuildCanonicalUrl(NormalizeBaseUrl(baseUrl), relativePath);
        return $"<link rel=\"canonical\" href=\"{EscapeHtmlAttribute(canonicalUrl)}\">";
    }

    private static string BuildCanonicalUrl(string normalizedBaseUrl, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(normalizedBaseUrl))
        {
            return string.Empty;
        }

        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return $"{normalizedBaseUrl}/";
        }

        return $"{normalizedBaseUrl}/{relativePath.TrimStart('/')}";
    }

    private static string NormalizeBaseUrl(string? baseUrl)
    {
        return string.IsNullOrWhiteSpace(baseUrl)
            ? string.Empty
            : baseUrl.Trim().TrimEnd('/');
    }

    /// <summary>Collapses line breaks and runs of whitespace so text fits a single-line attribute.</summary>
    private static string NormalizeWhitespace(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return string.Empty;

        return string.Join(' ', input.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static string BuildPageTitle(string primaryText, string secondaryText, int maxLength = 60)
    {
        var combined = $"{primaryText} - {secondaryText}";
        if (combined.Length <= maxLength)
        {
            return combined;
        }

        if (primaryText.Length <= maxLength)
        {
            return primaryText;
        }

        var cut = Math.Max(0, maxLength - 1);
        if (cut > 0 && char.IsHighSurrogate(primaryText[cut - 1]))
        {
            cut--;
        }

        return primaryText[..cut].TrimEnd() + "…";
    }
}

public class PostData
{
    public string Filename { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Preview { get; set; } = string.Empty;
    /// <summary>Sanitised HTML body of the post.</summary>
    public string Content { get; set; } = string.Empty;
    public DateTime Date { get; set; }
    public int Views { get; set; }
    public int Reactions { get; set; }
    public int Forwards { get; set; }
    public bool IsNoContent { get; set; }
}
