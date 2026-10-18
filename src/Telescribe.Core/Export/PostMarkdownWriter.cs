using System.Globalization;
using System.Text;
using Telescribe.Core.Models;

namespace Telescribe.Core.Export;

/// <summary>Produces the Markdown files Telescribe exports. The matching reader is <see cref="PostMarkdown"/>.</summary>
public static class PostMarkdownWriter
{
    /// <summary>The file as exported from Telegram, before any AI enrichment.</summary>
    public static string BuildRaw(TelegramPost post)
    {
        var builder = new StringBuilder();
        AppendMetrics(builder, post);
        builder.AppendLine();
        AppendArticleHeader(builder, post, includeTitle: true);
        AppendBody(builder, post);
        return builder.ToString();
    }

    /// <summary>The file with the AI-generated title and tags, as used by the site builder.</summary>
    public static string BuildProcessed(TelegramPost post)
    {
        var title = !string.IsNullOrWhiteSpace(post.GeneratedTitle) ? post.GeneratedTitle
            : !string.IsNullOrWhiteSpace(post.ArticleTitle) ? post.ArticleTitle
            : $"Post {post.Id}";

        var builder = new StringBuilder();
        builder.Append("# ").AppendLine(title).AppendLine();
        AppendMetrics(builder, post);
        if (post.IsLlmProcessed)
        {
            builder.AppendLine($"**LLM Processed:** Yes ({post.LlmProvider})");
        }

        builder.AppendLine();
        AppendArticleHeader(builder, post, includeTitle: false);

        if (post.Hashtags.Count > 0)
        {
            builder.AppendLine($"**Tags:** {string.Join(" ", post.Hashtags.Select(h => "#" + h))}").AppendLine();
        }

        builder.AppendLine("## Content").AppendLine();
        AppendBody(builder, post);
        return builder.ToString();
    }

    public static string BuildMediaMarkdown(string mediaFile)
    {
        var url = MediaFileName.ToMarkdownUrl(mediaFile);
        // Square brackets in the alt/link text would end it early.
        var label = mediaFile.Replace("[", "(").Replace("]", ")");

        return Path.GetExtension(mediaFile).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" => $"![{label}]({url})\n\n",
            ".mp4" or ".mov" or ".avi" or ".webm" => $"🎬 **Video:** [Click to view {label}]({url})\n\n",
            ".mp3" or ".wav" or ".ogg" or ".m4a" or ".flac" => $"🎵 **Audio:** [Click to play {label}]({url})\n\n",
            ".pdf" => $"📄 **Document:** [Click to open {label}]({url})\n\n",
            _ => $"📎 **File:** [Download {label}]({url})\n\n"
        };
    }

    public static string PostTypeLabel(string postType)
    {
        return postType switch
        {
            "article" => "Article",
            "poll" => "Poll",
            "photo" => "Photo",
            "document" => "Document",
            "video" => "Video",
            "audio" => "Audio",
            _ => string.IsNullOrWhiteSpace(postType) ? "Text" : char.ToUpperInvariant(postType[0]) + postType[1..]
        };
    }

    private static void AppendMetrics(StringBuilder builder, TelegramPost post)
    {
        builder.AppendLine($"**Created:** {Utc(post.CreatedAt)}");
        if (post.IsEdited && post.EditedAt is { } editedAt)
        {
            builder.AppendLine($"**Edited:** {Utc(editedAt)}");
        }

        builder.AppendLine($"**Post Type:** {PostTypeLabel(post.PostType)}");
        builder.AppendLine($"**Views:** {post.Views}");
        builder.AppendLine($"**Reactions:** {post.Reactions}");
        builder.AppendLine($"**Forwards:** {post.TotalForwards}");
    }

    private static void AppendArticleHeader(StringBuilder builder, TelegramPost post, bool includeTitle)
    {
        if (post.PostType != "article")
        {
            return;
        }

        if (includeTitle && !string.IsNullOrWhiteSpace(post.ArticleTitle))
        {
            builder.Append("# ").AppendLine(post.ArticleTitle).AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(post.ArticleAuthor))
        {
            builder.AppendLine($"**Author:** {post.ArticleAuthor}").AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(post.SiteName))
        {
            builder.AppendLine($"**Site:** {post.SiteName}").AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(post.ArticleUrl))
        {
            builder.AppendLine($"**Source:** [{post.ArticleUrl}]({post.ArticleUrl})").AppendLine();
        }

        if (post.IsPartialArticle)
        {
            builder.AppendLine("> ⚠️ Article content is partial (Telegram Instant View delivered in segments).").AppendLine();
        }
    }

    private static void AppendBody(StringBuilder builder, TelegramPost post)
    {
        var hasMedia = post.MediaFiles.Count > 0;
        var hasText = !string.IsNullOrWhiteSpace(post.Content);

        foreach (var mediaFile in post.MediaFiles)
        {
            builder.Append(BuildMediaMarkdown(mediaFile));
        }

        if (hasMedia && hasText)
        {
            builder.AppendLine();
        }

        if (hasText)
        {
            builder.AppendLine(post.Content);
        }

        if (!hasMedia && !hasText)
        {
            builder.AppendLine(post.PostType == "article" ? PostMarkdown.ArticlePreviewOnlyMarker : PostMarkdown.NoContentMarker);
        }
    }

    private static string Utc(DateTime value) =>
        value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC";
}
