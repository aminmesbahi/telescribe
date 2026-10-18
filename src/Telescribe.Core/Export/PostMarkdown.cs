using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Telescribe.Core.Export;

public sealed record PostEnrichment(string? GeneratedTitle, IReadOnlyList<string> Hashtags, string? Provider);

/// <summary>
/// Reads the metadata header that Telescribe writes at the top of every exported post. All writers and
/// readers of that header live here so the format cannot drift between them.
/// </summary>
public static partial class PostMarkdown
{
    public const string NoContentMarker = "[No content]";
    public const string ArticlePreviewOnlyMarker = "This article was shared as a preview without extracted body text.";

    private static readonly string[] HeaderPrefixes =
    [
        "**Created:**",
        "**Edited:**",
        "**Post Type:**",
        "**Views:**",
        "**Reactions:**",
        "**Forwards:**",
        "**Forwarded from:**",
        "**Reply to:**",
        "**LLM Processed:**",
        "**LLM Provider:**",
        "**Generated Title:**",
        "**Hashtags:**",
        "**Tags:**"
    ];

    private static readonly string[] AttributionPrefixes =
    [
        "**Author:**",
        "**Site:**",
        "**Source:**"
    ];

    public static bool IsHeaderLine(string trimmedLine) => StartsWithAny(trimmedLine, HeaderPrefixes);

    public static bool IsAttributionLine(string trimmedLine) => StartsWithAny(trimmedLine, AttributionPrefixes);

    public static bool IsMetadataLine(string trimmedLine) => IsHeaderLine(trimmedLine) || IsAttributionLine(trimmedLine);

    public static string[] SplitLines(string content) =>
        content.TrimStart('﻿').Replace("\r\n", "\n").Split('\n');

    public static (int Views, int Reactions, int Forwards) ExtractMetrics(string content)
    {
        int views = 0, reactions = 0, forwards = 0;

        foreach (var line in SplitLines(content))
        {
            if (TryReadInt(line, "**Views:**", out var v)) views = v;
            else if (TryReadInt(line, "**Reactions:**", out var r)) reactions = r;
            else if (TryReadInt(line, "**Forwards:**", out var f)) forwards = f;
        }

        return (views, reactions, forwards);
    }

    /// <summary>The post's creation time as written by the exporter (always UTC). <see cref="DateTime.MinValue"/> if absent.</summary>
    public static DateTime ExtractCreationDate(string content)
    {
        foreach (var line in SplitLines(content))
        {
            if (!line.StartsWith("**Created:**", StringComparison.Ordinal))
            {
                continue;
            }

            var text = line["**Created:**".Length..].Trim();
            if (text.EndsWith(" UTC", StringComparison.Ordinal))
            {
                text = text[..^4].Trim();
            }

            if (DateTime.TryParseExact(text, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var exact) ||
                DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out exact))
            {
                return DateTime.SpecifyKind(exact, DateTimeKind.Utc);
            }
        }

        return DateTime.MinValue;
    }

    /// <summary>First meaningful line of the post (heading markers and inline formatting removed), or null.</summary>
    public static string? ExtractTitle(string content, int maxLength = 120)
    {
        foreach (var line in SplitLines(content))
        {
            var trimmed = line.Trim();
            if (IsSkippableForTitle(trimmed))
            {
                continue;
            }

            var title = StripMarkdownFormatting(StripHeadingMarkers(trimmed));
            return Truncate(title, maxLength);
        }

        return null;
    }

    /// <summary>A short plain-text preview: the lines after the title, up to <paramref name="maxLength"/> characters.</summary>
    public static string ExtractPreview(string content, int maxLength = 130)
    {
        var builder = new StringBuilder();
        var skippedTitle = false;

        foreach (var line in SplitLines(content))
        {
            var trimmed = line.Trim();
            if (IsSkippableForTitle(trimmed))
            {
                continue;
            }

            if (!skippedTitle)
            {
                skippedTitle = true;
                continue;
            }

            if (trimmed.StartsWith('#'))
            {
                continue;
            }

            builder.AppendLine(trimmed);
            if (builder.Length > maxLength)
            {
                break;
            }
        }

        var preview = StripMarkdownFormatting(builder.ToString().Trim());
        if (preview.Length > maxLength)
        {
            preview = Truncate(preview, maxLength, "...");
        }

        return string.IsNullOrEmpty(preview) ? "No preview available" : preview;
    }

    /// <summary>True for posts that carry no text (polls without a question, media-only posts).</summary>
    public static bool IsNoContent(string content)
    {
        return string.IsNullOrWhiteSpace(content) ||
               content.Contains(NoContentMarker, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Adds an AI-generated title (as a leading heading) and tags to a raw post file. The result is what the
    /// exporter stores as the "processed" version of the post; <see cref="ReadEnrichment"/> reads it back.
    /// </summary>
    public static string ApplyEnrichment(string rawMarkdown, string? title, IReadOnlyCollection<string>? tags, string? provider)
    {
        var hasTitle = !string.IsNullOrWhiteSpace(title);
        var hasTags = tags is { Count: > 0 };
        if (!hasTitle && !hasTags && string.IsNullOrWhiteSpace(provider))
        {
            return rawMarkdown;
        }

        var result = new StringBuilder();
        if (hasTitle)
        {
            result.Append("# ").Append(title!.Trim()).Append("\n\n");
        }

        var inserted = false;
        foreach (var line in rawMarkdown.TrimStart('﻿').Replace("\r\n", "\n").Split('\n'))
        {
            result.Append(line).Append('\n');

            if (!inserted && line.StartsWith("**Forwards:**", StringComparison.Ordinal))
            {
                inserted = true;
                result.Append("**LLM Processed:** Yes (").Append(provider).Append(")\n");
                if (hasTags)
                {
                    result.Append("**Tags:** ").Append(string.Join(' ', tags!.Select(t => "#" + t.TrimStart('#')))).Append('\n');
                }
            }
        }

        return result.ToString().TrimEnd('\n') + "\n";
    }

    /// <summary>
    /// Reads the AI-generated title, tags and provider back from a processed post file, so that re-exporting a
    /// post (to refresh its statistics) does not discard them. Returns null if the file was not AI-processed.
    /// </summary>
    public static PostEnrichment? ReadEnrichment(string processedContent, long postId, string? articleTitle)
    {
        string? provider = null;
        string? title = null;
        var tags = new List<string>();

        foreach (var line in SplitLines(processedContent))
        {
            var trimmed = line.Trim();

            if (trimmed.StartsWith("**LLM Processed:**", StringComparison.Ordinal))
            {
                var open = trimmed.IndexOf('(');
                var close = trimmed.LastIndexOf(')');
                provider = open >= 0 && close > open ? trimmed[(open + 1)..close] : string.Empty;
            }
            else if (trimmed.StartsWith("**Tags:**", StringComparison.Ordinal))
            {
                tags.AddRange(trimmed["**Tags:**".Length..]
                    .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                    .Select(t => t.TrimStart('#'))
                    .Where(t => t.Length > 0));
            }
            else if (title is null && trimmed.StartsWith("# ", StringComparison.Ordinal))
            {
                title = trimmed[2..].Trim();
            }
        }

        if (provider is null)
        {
            return null;
        }

        if (title is not null &&
            (string.Equals(title, articleTitle, StringComparison.Ordinal) ||
             string.Equals(title, $"Post {postId}", StringComparison.Ordinal)))
        {
            title = null;
        }

        return new PostEnrichment(title, tags, provider);
    }

    public static string StripHeadingMarkers(string text) => HeadingMarkerRegex().Replace(text, string.Empty);

    /// <summary>Removes links, bold, italic and inline-code markers, leaving the visible text.</summary>
    public static string StripMarkdownFormatting(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        text = LinkRegex().Replace(text, "$1");
        text = BoldRegex().Replace(text, "$1");
        text = ItalicRegex().Replace(text, "$1");
        text = InlineCodeRegex().Replace(text, "$1");
        return text;
    }

    private static bool IsSkippableForTitle(string trimmed)
    {
        return trimmed.Length == 0 ||
               IsMetadataLine(trimmed) ||
               trimmed.StartsWith("![", StringComparison.Ordinal) ||
               trimmed.Equals(NoContentMarker, StringComparison.OrdinalIgnoreCase) ||
               trimmed.Equals(ArticlePreviewOnlyMarker, StringComparison.OrdinalIgnoreCase) ||
               trimmed.Equals("## Content", StringComparison.Ordinal);
    }

    private static bool StartsWithAny(string line, string[] prefixes)
    {
        foreach (var prefix in prefixes)
        {
            if (line.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryReadInt(string line, string prefix, out int value)
    {
        value = 0;
        return line.StartsWith(prefix, StringComparison.Ordinal) &&
               int.TryParse(line[prefix.Length..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static string Truncate(string text, int maxLength, string ellipsis = "…")
    {
        if (text.Length <= maxLength)
        {
            return text;
        }

        var cut = Math.Max(0, maxLength - ellipsis.Length);
        if (cut > 0 && char.IsHighSurrogate(text[cut - 1]))
        {
            cut--;
        }

        var lastSpace = text.LastIndexOf(' ', cut);
        if (lastSpace > cut / 2)
        {
            cut = lastSpace;
        }

        return text[..cut].TrimEnd() + ellipsis;
    }

    /// <summary>The time of the last edit, or null if the post was never edited.</summary>
    public static DateTime? ExtractEditedDate(string content)
    {
        foreach (var line in SplitLines(content))
        {
            if (line.StartsWith("**Edited:**", StringComparison.Ordinal) &&
                TryParseUtc(line["**Edited:**".Length..], out var edited))
            {
                return edited;
            }
        }

        return null;
    }

    /// <summary>File names of the exported media a post links to (decoded), in order of appearance.</summary>
    public static IReadOnlyList<string> ExtractMediaFiles(string content)
    {
        var files = new List<string>();
        foreach (Match match in MediaLinkRegex().Matches(content))
        {
            var name = Uri.UnescapeDataString(match.Groups[1].Value);
            if (!files.Contains(name))
            {
                files.Add(name);
            }
        }

        return files;
    }

    private static bool TryParseUtc(string text, out DateTime value)
    {
        text = text.Trim();
        if (text.EndsWith(" UTC", StringComparison.Ordinal))
        {
            text = text[..^4].Trim();
        }

        if (DateTime.TryParseExact(text, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            value = DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
            return true;
        }

        value = default;
        return false;
    }

    [GeneratedRegex(@"\]\(\.?/?media/([^)\s]+)\)")]
    private static partial Regex MediaLinkRegex();

    [GeneratedRegex(@"^#{1,6}\s+")]
    private static partial Regex HeadingMarkerRegex();

    [GeneratedRegex(@"\[([^\]]+)\]\([^\)]+\)")]
    private static partial Regex LinkRegex();

    [GeneratedRegex(@"\*\*(.*?)\*\*")]
    private static partial Regex BoldRegex();

    [GeneratedRegex(@"\*(.*?)\*")]
    private static partial Regex ItalicRegex();

    [GeneratedRegex("`([^`]+)`")]
    private static partial Regex InlineCodeRegex();
}
