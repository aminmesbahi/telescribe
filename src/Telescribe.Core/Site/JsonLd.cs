using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Telescribe.Core.Models;
using Telescribe.Core.Services;
using Telescribe.Core.Templating;

namespace Telescribe.Core.Site;

/// <summary>
/// Builds schema.org structured data and JSON string literals that are safe to embed inside a
/// <c>&lt;script&gt;</c> element.
/// </summary>
public static class JsonLd
{
    private static readonly JsonSerializerOptions Options = new()
    {
        // Keeps non-ASCII (e.g. Persian) readable; '<' is escaped separately in Embed().
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true
    };

    /// <summary>A JSON string literal (with quotes) that cannot terminate the surrounding script element.</summary>
    public static string String(string? value) => Embed(JsonSerializer.Serialize(value ?? string.Empty, Options));

    public static string Script(JsonNode graph) =>
        "<script type=\"application/ld+json\">\n" + Embed(graph.ToJsonString(Options)) + "\n</script>";

    public static string IndexScript(StaticSiteConfig config, TemplateInfo template, string canonicalUrl)
    {
        var website = new JsonObject
        {
            ["@type"] = "WebSite",
            ["name"] = config.SiteTitle,
            ["description"] = config.Description,
            ["inLanguage"] = template.Language
        };
        AddIfNotEmpty(website, "url", canonicalUrl);

        var graph = new JsonArray { website, Publisher(config) };
        return Script(new JsonObject { ["@context"] = "https://schema.org", ["@graph"] = graph });
    }

    public static string PostScript(StaticSiteConfig config, TemplateInfo template, PostData post, string canonicalUrl)
    {
        var published = FormatDate(post.Date);
        var article = new JsonObject
        {
            ["@type"] = "BlogPosting",
            ["headline"] = post.Title,
            ["description"] = post.Preview,
            ["datePublished"] = published,
            ["dateModified"] = published,
            ["author"] = Author(config),
            ["publisher"] = Publisher(config),
            ["inLanguage"] = template.Language
        };

        if (!string.IsNullOrEmpty(canonicalUrl))
        {
            article["url"] = canonicalUrl;
            article["mainEntityOfPage"] = new JsonObject { ["@type"] = "WebPage", ["@id"] = canonicalUrl };
        }

        return Script(new JsonObject { ["@context"] = "https://schema.org", ["@graph"] = new JsonArray { article } });
    }

    public static string FormatDate(DateTime date) =>
        date.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);

    private static JsonObject Author(StaticSiteConfig config)
    {
        var hasPerson = !string.IsNullOrWhiteSpace(config.AuthorName);
        var author = new JsonObject
        {
            ["@type"] = hasPerson ? "Person" : "Organization",
            ["name"] = hasPerson ? config.AuthorName : config.SiteTitle
        };
        AddIfNotEmpty(author, "url", config.AuthorUrl);
        return author;
    }

    private static JsonObject Publisher(StaticSiteConfig config)
    {
        var publisher = new JsonObject
        {
            ["@type"] = "Organization",
            ["name"] = string.IsNullOrWhiteSpace(config.PublisherName) ? config.SiteTitle : config.PublisherName
        };
        AddIfNotEmpty(publisher, "url", config.SiteBaseUrl);

        var sameAs = config.SameAs.Where(u => !string.IsNullOrWhiteSpace(u)).ToArray();
        if (sameAs.Length > 0)
        {
            publisher["sameAs"] = new JsonArray(sameAs.Select(u => (JsonNode)JsonValue.Create(u)!).ToArray());
        }

        return publisher;
    }

    private static void AddIfNotEmpty(JsonObject target, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            target[name] = value;
        }
    }
    // Written as a prefix + hex digits so the source never contains a unicode escape that tooling could expand.
    private const string EscapePrefix = "\\" + "u";

    private static string Embed(string json) =>
        json.Replace("<", EscapePrefix + "003C", StringComparison.Ordinal)
            .Replace(((char)0x2028).ToString(), EscapePrefix + "2028", StringComparison.Ordinal)
            .Replace(((char)0x2029).ToString(), EscapePrefix + "2029", StringComparison.Ordinal);
}
