using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Telescribe.Core.Export;

namespace Telescribe.Core.Site;

/// <summary>
/// Turns an exported post's Markdown into the HTML shown on a post page. Raw HTML in the source is never
/// passed through (only a short allow-list of inline tags that the exporter itself emits), and link
/// schemes are restricted, so untrusted channel content cannot inject markup or scripts.
/// </summary>
public static partial class PostHtmlRenderer
{
    // Deliberately not UseAdvancedExtensions(): its generic-attributes extension would let post text
    // inject arbitrary attributes (e.g. event handlers) into rendered elements.
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseEmphasisExtras()
        .UseAutoLinks()
        .UseSoftlineBreakAsHardlineBreak()
        .Build();

    private static readonly HashSet<string> AllowedLinkSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        Uri.UriSchemeHttp, Uri.UriSchemeHttps, Uri.UriSchemeMailto, "tel"
    };

    /// <param name="markdown">Full exported post, including its metadata header.</param>
    /// <param name="postTitle">If set, headings that merely repeat the title are dropped (the page header already shows it).</param>
    /// <param name="mediaUrl">Maps a local media link ("./media/x.jpg") to the URL to emit. Defaults to the post-page relative path.</param>
    public static string Render(string markdown, string? postTitle = null, Func<string, string>? mediaUrl = null)
    {
        var document = Markdown.Parse(BuildBody(markdown), Pipeline);

        Sanitize(document);
        RemoveDuplicateTitleHeadings(document, postTitle);
        RewriteLinks(document, mediaUrl ?? DefaultMediaUrl);

        return document.ToHtml(Pipeline);
    }

    private static string BuildBody(string markdown)
    {
        var lines = new List<string>();
        var preface = new List<string>();
        foreach (var line in PostMarkdown.SplitLines(markdown))
        {
            var trimmed = line.Trim();

            if (trimmed.StartsWith("**Forwarded from:**", StringComparison.Ordinal))
            {
                preface.Add("*↪ Forwarded from " + trimmed["**Forwarded from:**".Length..].Trim() + "*");
                continue;
            }

            if (trimmed.StartsWith("**Reply to:**", StringComparison.Ordinal) &&
                long.TryParse(trimmed["**Reply to:**".Length..].Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var replyTo))
            {
                preface.Add($"*[↩ In reply to post {replyTo}]({replyTo}.html)*");
                continue;
            }
            if (PostMarkdown.IsHeaderLine(trimmed) ||
                trimmed.Equals(PostMarkdown.ArticlePreviewOnlyMarker, StringComparison.OrdinalIgnoreCase) ||
                trimmed.Equals("## Content", StringComparison.Ordinal))
            {
                continue;
            }

            if (trimmed.Equals(PostMarkdown.NoContentMarker, StringComparison.OrdinalIgnoreCase))
            {
                lines.Add("*⚠️ This post had no text content (e.g. a poll or media-only post).*");
                continue;
            }

            lines.Add(line);
        }

        if (preface.Count > 0)
        {
            lines.InsertRange(0, preface.SelectMany(p => new[] { p, string.Empty }));
        }

        return string.Join('\n', lines).Trim('\n');
    }

    private static void Sanitize(MarkdownDocument document)
    {
        foreach (var inline in document.Descendants<HtmlInline>().ToList())
        {
            if (!AllowedInlineTagRegex().IsMatch(inline.Tag))
            {
                inline.ReplaceBy(new LiteralInline(inline.Tag));
            }
        }

        foreach (var block in document.Descendants<HtmlBlock>().ToList())
        {
            if (block.Parent is not ContainerBlock parent)
            {
                continue;
            }

            var paragraph = new ParagraphBlock
            {
                Inline = new ContainerInline().AppendChild(new LiteralInline(block.Lines.ToString()))
            };

            var index = parent.IndexOf(block);
            parent.RemoveAt(index);
            parent.Insert(index, paragraph);
        }
    }

    private static void RemoveDuplicateTitleHeadings(MarkdownDocument document, string? postTitle)
    {
        if (string.IsNullOrWhiteSpace(postTitle))
        {
            return;
        }

        var normalizedTitle = PostMarkdown.StripMarkdownFormatting(postTitle).Trim();

        foreach (var heading in document.Descendants<HeadingBlock>().Where(h => h.Level <= 2).ToList())
        {
            var text = heading.Inline is null ? string.Empty : PlainText(heading.Inline);
            if (string.Equals(text.Trim(), normalizedTitle, StringComparison.OrdinalIgnoreCase) &&
                heading.Parent is ContainerBlock parent)
            {
                parent.Remove(heading);
            }
        }
    }

    /// <summary>Post pages live in /posts and media in /media.</summary>
    private static string DefaultMediaUrl(string url) => "../" + (url.StartsWith("./", StringComparison.Ordinal) ? url[2..] : url);

    private static void RewriteLinks(MarkdownDocument document, Func<string, string> mediaUrl)
    {
        foreach (var link in document.Descendants<LinkInline>().ToList())
        {
            var url = link.Url ?? string.Empty;

            if (IsLocalMediaPath(url))
            {
                link.Url = mediaUrl(url);
                AddPlayer(link);
            }
            else if (HasDisallowedScheme(url))
            {
                // Keep the visible text, drop the target. A bare ContainerInline renders only its children.
                link.ReplaceBy(new ContainerInline(), copyChildren: !link.IsImage);
                continue;
            }

            if (link.IsImage)
            {
                var attributes = link.GetAttributes();
                attributes.AddPropertyIfNotExist("loading", "lazy");
                attributes.AddPropertyIfNotExist("decoding", "async");
                attributes.AddPropertyIfNotExist("style", "max-width: 100%; height: auto; border-radius: 8px; margin: 15px 0;");
            }
            else if (Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                     (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                var attributes = link.GetAttributes();
                attributes.AddPropertyIfNotExist("target", "_blank");
                attributes.AddPropertyIfNotExist("rel", "noopener noreferrer");
            }
        }
    }

    /// <summary>Puts a native player under a link to a local video or audio file; the link stays as the download fallback.</summary>
    private static void AddPlayer(LinkInline link)
    {
        if (link.IsImage || string.IsNullOrEmpty(link.Url))
        {
            return;
        }

        var extension = Path.GetExtension(link.Url).ToLowerInvariant();
        var source = WebUtility.HtmlEncode(link.Url);
        string? player = extension switch
        {
            ".mp4" or ".webm" or ".mov" or ".m4v" => $"<video controls preload=\"metadata\" style=\"display:block;max-width:100%;margin:10px 0\" src=\"{source}\"></video>",
            ".mp3" or ".ogg" or ".oga" or ".opus" or ".m4a" or ".wav" or ".flac" => $"<audio controls preload=\"none\" style=\"display:block;width:100%;margin:10px 0\" src=\"{source}\"></audio>",
            _ => null
        };

        if (player is not null)
        {
            link.InsertAfter(new HtmlInline(player));
        }
    }

    private static bool IsLocalMediaPath(string url) =>
        url.StartsWith("./media/", StringComparison.Ordinal) || url.StartsWith("media/", StringComparison.Ordinal);

    private static bool HasDisallowedScheme(string url)
    {
        // Browsers ignore tabs/newlines inside a URL and leading control characters, so "java\tscript:" is a
        // valid javascript: URL. Normalise the same way before looking at the scheme.
        var cleaned = new string(url.Where(c => c > ' ').ToArray());
        var colon = cleaned.IndexOf(':');
        if (colon <= 0)
        {
            return false; // relative reference or fragment
        }

        var candidate = cleaned[..colon];
        if (candidate.AsSpan().IndexOfAny('/', '?', '#') >= 0)
        {
            return false; // the colon belongs to a relative path, not a scheme
        }

        if (candidate.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '+' or '-' or '.')))
        {
            return true;
        }

        return !AllowedLinkSchemes.Contains(candidate);
    }

    private static string PlainText(ContainerInline container)
    {
        var builder = new StringBuilder();
        foreach (var literal in container.Descendants<LiteralInline>())
        {
            builder.Append(literal.Content.ToString());
        }

        foreach (var code in container.Descendants<CodeInline>())
        {
            builder.Append(code.Content);
        }

        return WebUtility.HtmlDecode(builder.ToString());
    }

    [GeneratedRegex(@"^</?(u|ins|del|sub|sup|mark|br)\s*/?>$", RegexOptions.IgnoreCase)]
    private static partial Regex AllowedInlineTagRegex();
}
