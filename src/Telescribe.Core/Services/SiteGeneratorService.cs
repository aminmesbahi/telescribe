using System.Text;
using System.Text.Json;
using Telescribe.Core.Models;

namespace Telescribe.Core.Services;

public class SiteGeneratorService
{
    private readonly string _templatesPath;

    public SiteGeneratorService(string templatesPath)
    {
        _templatesPath = templatesPath;
    }

    public string RenderTemplate(string templateName, Dictionary<string, string> variables)
    {
        var templatePath = Path.Combine(_templatesPath, templateName);

        if (!File.Exists(templatePath))
        {
            throw new FileNotFoundException($"Template not found: {templatePath}");
        }

        var template = File.ReadAllText(templatePath);

        foreach (var variable in variables)
        {
            var placeholder = $"{{{{{variable.Key}}}}}";
            template = template.Replace(placeholder, variable.Value);
        }

        return template;
    }

    public string RenderIndexPage(StaticSiteConfig config, List<PostData> posts, DateTime generatedDate)
    {
        var postsContent = new StringBuilder();
        var canonicalUrl = BuildCanonicalUrl(NormalizeBaseUrl(config.SiteBaseUrl), string.Empty);

        foreach (var post in posts)
        {
            var postCardHtml = RenderTemplate("post-card.html", new Dictionary<string, string>
            {
                ["filename"] = post.Filename,
                ["title"] = post.Title,
                ["date"] = post.Date.ToString("yyyy-MM-dd"),
                ["preview"] = post.Preview,
                ["views"] = post.Views.ToString(),
                ["reactions"] = post.Reactions.ToString(),
                ["forwards"] = post.Forwards.ToString()
            });

            postsContent.AppendLine(postCardHtml);
        }

        return RenderTemplate("index.html", new Dictionary<string, string>
        {
            ["pageTitle"] = BuildPageTitle(config.SiteTitle, config.Subtitle),
            ["siteTitle"] = config.SiteTitle,
            ["subtitle"] = config.Subtitle,
            ["headerIcon"] = config.HeaderIcon,
            ["description"] = config.Description,
            ["canonicalUrl"] = canonicalUrl,
            ["canonicalTag"] = BuildCanonicalTag(config.SiteBaseUrl, string.Empty),
            ["faviconPath"] = "favicon.svg",
            ["jsonLdScript"] = BuildJsonLdIndexScript(config, canonicalUrl),
            ["postsContent"] = postsContent.ToString(),
            ["generatedDate"] = generatedDate.ToString("yyyy-MM-dd HH:mm:ss"),
            ["totalPosts"] = posts.Count.ToString()
        });
    }

    public string RenderPostPage(StaticSiteConfig config, PostData post)
    {
        var canonicalUrl = BuildCanonicalUrl(NormalizeBaseUrl(config.SiteBaseUrl), $"posts/{post.Filename}.html");
        return RenderTemplate("post.html", new Dictionary<string, string>
        {
            ["pageTitle"] = BuildPageTitle(post.Title, config.SiteTitle),
            ["siteTitle"] = config.SiteTitle,
            ["title"] = post.Title,
            ["date"] = post.Date.ToString("yyyy-MM-dd HH:mm:ss"),
            ["preview"] = post.Preview,
            ["canonicalUrl"] = canonicalUrl,
            ["canonicalTag"] = BuildCanonicalTag(config.SiteBaseUrl, $"posts/{post.Filename}.html"),
            ["faviconPath"] = "../favicon.svg",
            ["jsonLdScript"] = BuildJsonLdPostScript(config, post, canonicalUrl),
            ["content"] = post.Content,
            ["views"] = post.Views.ToString(),
            ["reactions"] = post.Reactions.ToString(),
            ["forwards"] = post.Forwards.ToString()
        });
    }

    /// <summary>
    /// Generates a standard XML sitemap for the static site.
    /// </summary>
    public string GenerateSitemap(StaticSiteConfig config, List<PostData> posts, string baseUrl, bool includeAboutPage = false)
    {
        var normalizedBaseUrl = NormalizeBaseUrl(baseUrl);
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine("<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">");

        sb.AppendLine("  <url>");
        sb.AppendLine($"    <loc>{EscapeXml(BuildCanonicalUrl(normalizedBaseUrl, string.Empty))}</loc>");
        sb.AppendLine($"    <lastmod>{DateTime.UtcNow:yyyy-MM-dd}</lastmod>");
        sb.AppendLine("    <changefreq>daily</changefreq>");
        sb.AppendLine("    <priority>1.0</priority>");
        sb.AppendLine("  </url>");

        if (includeAboutPage)
        {
            sb.AppendLine("  <url>");
            sb.AppendLine($"    <loc>{EscapeXml(BuildCanonicalUrl(normalizedBaseUrl, "about.html"))}</loc>");
            sb.AppendLine($"    <lastmod>{DateTime.UtcNow:yyyy-MM-dd}</lastmod>");
            sb.AppendLine("    <changefreq>monthly</changefreq>");
            sb.AppendLine("    <priority>0.5</priority>");
            sb.AppendLine("  </url>");
        }

        foreach (var post in posts)
        {
            var lastMod = post.Date == DateTime.MinValue ? DateTime.UtcNow : post.Date;
            sb.AppendLine("  <url>");
            sb.AppendLine($"    <loc>{EscapeXml(BuildCanonicalUrl(normalizedBaseUrl, $"posts/{post.Filename}.html"))}</loc>");
            sb.AppendLine($"    <lastmod>{lastMod:yyyy-MM-dd}</lastmod>");
            sb.AppendLine("    <changefreq>monthly</changefreq>");
            sb.AppendLine("    <priority>0.8</priority>");
            sb.AppendLine("  </url>");
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
        return $"<link rel=\"canonical\" href=\"{canonicalUrl}\">";
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

    private static string JsonString(string? value)
    {
        return JsonSerializer.Serialize(value ?? string.Empty);
    }

    private static string BuildJsonLdPostScript(StaticSiteConfig config, PostData post, string canonicalUrl)
    {
        var lang = config.TemplateName.StartsWith("fa", StringComparison.OrdinalIgnoreCase) ? "fa" : "en";
        return $$"""
            <script type="application/ld+json">
            {
                "@context": "https://schema.org",
                "@graph": [
                    {
                        "@type": "BlogPosting",
                        "headline": {{JsonString(post.Title)}},
                        "description": {{JsonString(post.Preview)}},
                        "datePublished": {{JsonString(post.Date.ToString("yyyy-MM-ddTHH:mm:ssK"))}},
                        "dateModified": {{JsonString(post.Date.ToString("yyyy-MM-ddTHH:mm:ssK"))}},
                        "url": {{JsonString(canonicalUrl)}},
                        "mainEntityOfPage": {
                            "@type": "WebPage",
                            "@id": {{JsonString(canonicalUrl)}}
                        },
                        "author": {
                            "@type": "Person",
                            "name": "REPLACE_WITH_AUTHOR_NAME",
                            "url": "https://example.com"
                        },
                        "publisher": {
                            "@type": "Organization",
                            "name": "REPLACE_WITH_PUBLISHER_NAME",
                            "url": "https://example.com"
                        },
                        "inLanguage": "{{lang}}"
                    }
                ]
            }
            </script>
            """;
    }

    private static string BuildJsonLdIndexScript(StaticSiteConfig config, string canonicalUrl)
    {
        var lang = config.TemplateName.StartsWith("fa", StringComparison.OrdinalIgnoreCase) ? "fa" : "en";
        return $$"""
            <script type="application/ld+json">
            {
                "@context": "https://schema.org",
                "@graph": [
                    {
                        "@type": "WebSite",
                        "url": {{JsonString(canonicalUrl)}},
                        "name": {{JsonString(config.SiteTitle)}},
                        "description": {{JsonString(config.Description)}},
                        "inLanguage": "{{lang}}"
                    },
                    {
                        "@type": "Organization",
                        "name": "REPLACE_WITH_OWNER_OR_ORGANIZATION_NAME",
                        "url": "https://example.com",
                        "sameAs": [
                            "https://example.com/twitter",
                            "https://example.com/linkedin"
                        ]
                    }
                ]
            }
            </script>
            """;
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

        return primaryText.Substring(0, Math.Max(0, maxLength - 1)).TrimEnd() + "…";
    }
}

public class PostData
{
    public string Filename { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Preview { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public DateTime Date { get; set; }
    public int Views { get; set; }
    public int Reactions { get; set; }
    public int Forwards { get; set; }
    public bool IsNoContent { get; set; }
}