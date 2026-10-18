using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Telescribe.Core.Models;
using Telescribe.Core.Services;
using Telescribe.Core.Templating;

namespace Telescribe.Core.Site;

/// <summary>Builds the Atom feed (<c>feed.xml</c>) of the newest posts.</summary>
public static class AtomFeed
{
    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";

    public const string FileName = "feed.xml";

    /// <param name="posts">Posts, newest first; only the first <paramref name="count"/> are included.</param>
    public static string Build(StaticSiteConfig config, TemplateInfo template, IEnumerable<PostData> posts, int count, DateTime generatedUtc)
    {
        var baseUrl = config.SiteBaseUrl.Trim().TrimEnd('/');
        var selected = posts.Take(count).ToList();
        var updated = selected.Count > 0 ? selected.Max(p => p.Updated) : generatedUtc;

        var feed = new XElement(Atom + "feed",
            new XAttribute(XNamespace.Xml + "lang", template.Language),
            new XElement(Atom + "title", config.SiteTitle),
            new XElement(Atom + "subtitle", config.Description),
            new XElement(Atom + "id", baseUrl + "/"),
            new XElement(Atom + "link", new XAttribute("rel", "alternate"), new XAttribute("href", baseUrl + "/")),
            new XElement(Atom + "link", new XAttribute("rel", "self"), new XAttribute("href", $"{baseUrl}/{FileName}")),
            new XElement(Atom + "updated", Format(updated)),
            new XElement(Atom + "generator", "Telescribe"),
            new XElement(Atom + "author", new XElement(Atom + "name", string.IsNullOrWhiteSpace(config.AuthorName) ? config.SiteTitle : config.AuthorName)));

        foreach (var post in selected)
        {
            var url = $"{baseUrl}/posts/{Uri.EscapeDataString(post.Filename)}.html";
            feed.Add(new XElement(Atom + "entry",
                new XElement(Atom + "title", post.Title),
                new XElement(Atom + "id", url),
                new XElement(Atom + "link", new XAttribute("rel", "alternate"), new XAttribute("href", url)),
                new XElement(Atom + "published", Format(post.Date)),
                new XElement(Atom + "updated", Format(post.Updated)),
                new XElement(Atom + "summary", post.Preview),
                new XElement(Atom + "content", new XAttribute("type", "html"), MakeMediaUrlsAbsolute(post.Content, baseUrl))));
        }

        var document = new XDocument(new XDeclaration("1.0", "utf-8", null), feed);
        using var writer = new Utf8StringWriter();
        using (var xml = XmlWriter.Create(writer, new XmlWriterSettings { Indent = true, OmitXmlDeclaration = false, Encoding = new System.Text.UTF8Encoding(false) }))
        {
            document.Save(xml);
        }

        return writer.ToString();
    }

    /// <summary>Feed readers resolve relative links against the feed, not the post, so media links must be absolute.</summary>
    internal static string MakeMediaUrlsAbsolute(string html, string baseUrl) =>
        html.Replace("=\"../media/", $"=\"{baseUrl}/media/", StringComparison.Ordinal);

    private static string Format(DateTime value) =>
        (value == DateTime.MinValue ? DateTime.UtcNow : value).ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>XmlWriter writes the encoding named in the declaration; a StringWriter would claim UTF-16.</summary>
    private sealed class Utf8StringWriter : StringWriter
    {
        public override System.Text.Encoding Encoding => new System.Text.UTF8Encoding(false);
    }
}
