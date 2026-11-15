using Telescribe.Core.Export;
using System.Text;
using System.Text.RegularExpressions;
using Telescribe.Core.Models;
using TL;
using WTelegram;

namespace Telescribe.Core.Services;

public partial class TelegramService
{
    private async Task HandleWebPageMediaAsync(TelegramPost post, WebPageBase? webpageBase)
    {
        try
        {
            var webpage = webpageBase as WebPage;

            // Some article posts arrive with an empty preview (WebPageEmpty / no usable url), so
            // fall back to the first link in the message text to still resolve them.
            var url = !string.IsNullOrWhiteSpace(webpage?.url)
                ? webpage!.url
                : ExtractFirstUrl(post.Content);

            // messages.getHistory ships only the link-preview metadata: the Instant View page body
            // (cached_page) is normally absent until it is requested explicitly with
            // messages.getWebPage. Fetch it before deciding what the post is so that native Telegram
            // articles don't come through as empty or as plain link text. A cached_page carrying the
            // "part" flag is only a truncated preview and is completed the same way.
            var resolved = webpage;
            if (resolved == null || resolved.cached_page == null ||
                resolved.cached_page.flags.HasFlag(Page.Flags.part))
            {
                var fetched = await FetchInstantViewWebPageAsync(url);
                if (fetched != null)
                {
                    resolved = fetched;
                }
            }

            if (resolved == null || !IsArticleWebPage(resolved))
            {
                // Regular link previews: the message text already contains the post content.
                return;
            }

            post.PostType = "article";
            post.ArticleTitle = resolved.title ?? string.Empty;
            post.ArticleAuthor = resolved.author ?? string.Empty;
            post.ArticleUrl = !string.IsNullOrWhiteSpace(resolved.url)
                ? resolved.url
                : resolved.display_url ?? string.Empty;
            post.SiteName = resolved.site_name ?? string.Empty;

            var articleParts = new List<string>();

            // Cover image of the article
            if (resolved.photo is Photo coverPhoto)
            {
                var fileName = $"{post.Id}_article_cover.jpg";
                if (await DownloadMediaAsync(fileName, 0, stream => _client!.DownloadFileAsync(coverPhoto, stream)))
                {
                    post.MediaFiles.Add(fileName);
                }
            }

            // Document directly attached to the webpage (e.g. a video preview)
            if (resolved.document is Document webpageDoc && _client != null)
            {
                var docFileName = GetDocumentFileName(webpageDoc, post.Id);
                if (await DownloadMediaAsync(docFileName, webpageDoc.size, stream => _client.DownloadFileAsync(webpageDoc, stream)))
                {
                    post.MediaFiles.Add(docFileName);
                }
            }

            if (resolved.cached_page != null)
            {
                post.IsPartialArticle = resolved.cached_page.flags.HasFlag(Page.Flags.part);
                var photoMap = await DownloadArticlePhotosAsync(post, resolved.cached_page);
                var documentMap = await DownloadArticleDocumentsAsync(post, resolved.cached_page);
                var body = ConvertPageBlocksToMarkdown(resolved.cached_page, photoMap, documentMap, post);
                if (!string.IsNullOrWhiteSpace(body))
                {
                    articleParts.Add(body);
                }
            }

            // Fallback for articles without a cached page: description, then embed URL
            if (articleParts.Count == 0 && !string.IsNullOrWhiteSpace(resolved.description))
            {
                articleParts.Add(resolved.description);
            }

            if (articleParts.Count == 0 && !string.IsNullOrWhiteSpace(resolved.embed_url))
            {
                articleParts.Add($"[Embedded content]({resolved.embed_url})");
            }

            if (articleParts.Count == 0)
            {
                return;
            }

            var articleMarkdown = string.Join("\n\n", articleParts).Trim();

            if (!string.IsNullOrWhiteSpace(post.Content))
            {
                post.Content += "\n\n" + articleMarkdown;
            }
            else
            {
                post.Content = articleMarkdown;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to handle article post {post.Id}: {ex.Message}");
        }
    }

    /// <summary>
    /// Extracts a "rich message" post: Telegram's rich-text posts (the successor to Instant View
    /// articles). Their content lives in <see cref="RichMessage.blocks"/> — a set of
    /// <see cref="PageBlock"/> objects — rather than in <c>message.text</c> or <c>media</c>, so they
    /// previously exported as empty "[No content]" posts.
    /// </summary>
    private async Task HandleRichMessageAsync(TelegramPost post, RichMessage richMessage)
    {
        try
        {
            post.PostType = "article";

            // RichMessage carries the same PageBlock content as an Instant View page, so reuse the
            // article conversion pipeline by adapting it into a Page.
            var page = new Page
            {
                blocks = richMessage.blocks,
                photos = richMessage.photos,
                documents = richMessage.documents
            };

            var photoMap = await DownloadArticlePhotosAsync(post, page);
            var documentMap = await DownloadArticleDocumentsAsync(post, page);
            var body = ConvertPageBlocksToMarkdown(page, photoMap, documentMap, post);

            if (string.IsNullOrWhiteSpace(body))
            {
                return;
            }

            post.Content = string.IsNullOrWhiteSpace(post.Content)
                ? body
                : post.Content + "\n\n" + body;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to handle rich message post {post.Id}: {ex.Message}");
        }
    }

    private static bool IsArticleWebPage(WebPage webpage)
    {
        // Explicitly typed as article by Telegram
        if (string.Equals(webpage.type, "article", StringComparison.OrdinalIgnoreCase))
            return true;

        // Has Instant View content (native Telegram article)
        if (webpage.cached_page != null)
            return true;

        return false;
    }

    /// <summary>
    /// Fetches the full <see cref="WebPage"/> (including its Instant View <see cref="Page"/>) for a
    /// link preview. <see cref="Messages_GetHistory"/> delivers the preview metadata but leaves
    /// <see cref="WebPage.cached_page"/> null, so the page body has to be requested explicitly with
    /// <c>messages.getWebPage</c>.
    /// </summary>
    private async Task<WebPage?> FetchInstantViewWebPageAsync(string? url)
    {
        if (_client == null || string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        try
        {
            // Pass an empty hash so Telegram always returns the full page instead of a
            // webPageNotModified result (which carries no cached_page).
            var response = await _client.Messages_GetWebPage(url, hash: 0);
            return response.webpage as WebPage;
        }
        catch (RpcException ex)
        {
            Console.WriteLine($"Could not fetch Instant View page for {url}: {ex.Message}");
            return null;
        }
    }

    /// <summary>First http(s) link found in a message, used when a post has no link preview.</summary>
    private static string? ExtractFirstUrl(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        var match = UrlRegex().Match(content);
        return match.Success ? match.Value.TrimEnd(')', ']', '.', ',', ';', '}', '>') : null;
    }

    [GeneratedRegex(@"https?://[^\s<>""')\]]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    private async Task<Dictionary<long, string>> DownloadArticlePhotosAsync(TelegramPost post, Page page)
    {
        var photoMap = new Dictionary<long, string>();

        try
        {
            if (_client == null || page.photos == null || page.photos.Length == 0)
            {
                return photoMap;
            }

            var photoIds = new HashSet<long>();
            foreach (var block in page.blocks ?? [])
            {
                CollectPhotoIds(block, photoIds);
            }

            if (photoIds.Count == 0)
            {
                return photoMap;
            }

            var photoIndex = 0;
            foreach (var photoId in photoIds)
            {
                if (page.photos.FirstOrDefault(p => p.ID == photoId) is not Photo photo)
                {
                    continue;
                }

                photoIndex++;
                var fileName = $"{post.Id}_article_photo_{photoIndex}.jpg";
                try
                {
                    if (await DownloadMediaAsync(fileName, 0, stream => _client.DownloadFileAsync(photo, stream)))
                    {
                        post.MediaFiles.Add(fileName);
                        photoMap[photoId] = fileName;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to download article photo {photoId} for post {post.Id}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to download article photos for post {post.Id}: {ex.Message}");
        }

        return photoMap;
    }

    private async Task<Dictionary<long, string>> DownloadArticleDocumentsAsync(TelegramPost post, Page page)
    {
        var documentMap = new Dictionary<long, string>();

        try
        {
            if (_client == null || page.documents == null || page.documents.Length == 0)
            {
                return documentMap;
            }

            var documentIds = new HashSet<long>();
            foreach (var block in page.blocks ?? [])
            {
                CollectDocumentIds(block, documentIds);
            }

            if (documentIds.Count == 0)
            {
                return documentMap;
            }

            var docIndex = 0;
            foreach (var docId in documentIds)
            {
                if (page.documents.FirstOrDefault(d => d.ID == docId) is not Document doc)
                {
                    continue;
                }

                docIndex++;
                var fileName = $"{post.Id}_article_doc_{docIndex}{GetDocumentExtension(doc)}";
                try
                {
                    if (await DownloadMediaAsync(fileName, doc.size, stream => _client.DownloadFileAsync(doc, stream)))
                    {
                        post.MediaFiles.Add(fileName);
                        documentMap[docId] = fileName;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to download article document {docId} for post {post.Id}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to download article documents for post {post.Id}: {ex.Message}");
        }

        return documentMap;
    }

    private static string GetDocumentExtension(Document doc)
    {
        return doc.mime_type switch
        {
            "video/mp4" => ".mp4",
            "video/webm" => ".webm",
            "video/quicktime" => ".mov",
            "audio/mpeg" => ".mp3",
            "audio/ogg" => ".ogg",
            "audio/mp4" => ".m4a",
            "audio/flac" => ".flac",
            "audio/wav" => ".wav",
            "image/gif" => ".gif",
            _ => string.Empty
        };
    }

    private void CollectDocumentIds(PageBlock? block, ISet<long> documentIds)
    {
        if (block == null)
        {
            return;
        }

        switch (block)
        {
            case PageBlockVideo video:
                documentIds.Add(video.video_id);
                break;
            case PageBlockAudio audio:
                documentIds.Add(audio.audio_id);
                break;
            case PageBlockCover cover:
                CollectDocumentIds(cover.cover, documentIds);
                break;
            case PageBlockCollage collage:
                foreach (var item in collage.items) CollectDocumentIds(item, documentIds);
                break;
            case PageBlockSlideshow slideshow:
                foreach (var item in slideshow.items) CollectDocumentIds(item, documentIds);
                break;
            case PageBlockEmbedPost embedPost:
                foreach (var item in embedPost.blocks) CollectDocumentIds(item, documentIds);
                break;
            case PageBlockDetails details:
                foreach (var item in details.blocks) CollectDocumentIds(item, documentIds);
                break;
            case PageBlockBlockquoteBlocks quoteBlocks:
                foreach (var item in quoteBlocks.blocks) CollectDocumentIds(item, documentIds);
                break;
            case PageBlockList list:
                foreach (var item in list.items)
                {
                    if (item is PageListItemBlocks listItemBlocks)
                    {
                        foreach (var nested in listItemBlocks.blocks) CollectDocumentIds(nested, documentIds);
                    }
                }
                break;
            case PageBlockOrderedList orderedList:
                foreach (var item in orderedList.items)
                {
                    if (item is PageListOrderedItemBlocks orderedItemBlocks)
                    {
                        foreach (var nested in orderedItemBlocks.blocks) CollectDocumentIds(nested, documentIds);
                    }
                }
                break;
        }
    }

    private void CollectPhotoIds(PageBlock? block, ISet<long> photoIds)
    {
        if (block == null)
        {
            return;
        }

        switch (block)
        {
            case PageBlockPhoto photo:
                photoIds.Add(photo.photo_id);
                break;
            case PageBlockEmbed embed when embed.flags.HasFlag(PageBlockEmbed.Flags.has_poster_photo_id):
                photoIds.Add(embed.poster_photo_id);
                break;
            case PageBlockCover cover:
                CollectPhotoIds(cover.cover, photoIds);
                break;
            case PageBlockCollage collage:
                foreach (var item in collage.items) CollectPhotoIds(item, photoIds);
                break;
            case PageBlockSlideshow slideshow:
                foreach (var item in slideshow.items) CollectPhotoIds(item, photoIds);
                break;
            case PageBlockEmbedPost embedPost:
                foreach (var item in embedPost.blocks) CollectPhotoIds(item, photoIds);
                break;
            case PageBlockDetails details:
                foreach (var item in details.blocks) CollectPhotoIds(item, photoIds);
                break;
            case PageBlockBlockquoteBlocks quoteBlocks:
                foreach (var item in quoteBlocks.blocks) CollectPhotoIds(item, photoIds);
                break;
            case PageBlockList list:
                foreach (var item in list.items)
                {
                    if (item is PageListItemBlocks listItemBlocks)
                    {
                        foreach (var nested in listItemBlocks.blocks) CollectPhotoIds(nested, photoIds);
                    }
                }
                break;
            case PageBlockOrderedList orderedList:
                foreach (var item in orderedList.items)
                {
                    if (item is PageListOrderedItemBlocks orderedItemBlocks)
                    {
                        foreach (var nested in orderedItemBlocks.blocks) CollectPhotoIds(nested, photoIds);
                    }
                }
                break;
        }
    }

    private string ConvertPageBlocksToMarkdown(Page page, Dictionary<long, string> photoMap, Dictionary<long, string> documentMap, TelegramPost post)
    {
        if (page.blocks == null || page.blocks.Length == 0)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        var contentStarted = false;

        foreach (var block in page.blocks)
        {
            // The article cover is already exported separately.
            if (!contentStarted && block is PageBlockCover)
            {
                continue;
            }

            // Skip title/author blocks that merely repeat the post header metadata
            // (Telegram article pages repeat them inside the cached page).
            if (IsDuplicateArticleHeaderBlock(block, post))
            {
                continue;
            }

            var markdown = ConvertPageBlockToMarkdown(block, page, photoMap, documentMap, 0);
            if (!string.IsNullOrWhiteSpace(markdown))
            {
                parts.Add(markdown.Trim());
                contentStarted = true;
            }
        }

        return string.Join("\n\n", parts);
    }

    private bool IsDuplicateArticleHeaderBlock(PageBlock block, TelegramPost post)
    {
        switch (block)
        {
            case PageBlockTitle title when !string.IsNullOrWhiteSpace(post.ArticleTitle):
                return string.Equals(
                    NormalizeForComparison(ConvertRichTextToMarkdown(title.text)),
                    NormalizeForComparison(post.ArticleTitle),
                    StringComparison.OrdinalIgnoreCase);

            case PageBlockAuthorDate authorDate when !string.IsNullOrWhiteSpace(post.ArticleAuthor):
                return string.Equals(
                    NormalizeForComparison(ConvertRichTextToMarkdown(authorDate.author)),
                    NormalizeForComparison(post.ArticleAuthor),
                    StringComparison.OrdinalIgnoreCase);

            default:
                return false;
        }
    }

    private static string NormalizeForComparison(string text)
    {
        return Regex.Replace(text ?? string.Empty, @"[#*_`~]", string.Empty).Trim();
    }

    private string ConvertPageBlockToMarkdown(PageBlock block, Page page, Dictionary<long, string> photoMap, Dictionary<long, string> documentMap, int depth)
    {
        switch (block)
        {
            case PageBlockUnsupported:
            case PageBlockAnchor:
                return string.Empty;

            case PageBlockTitle title:
                return $"# {ConvertRichTextToMarkdown(title.text)}";

            case PageBlockSubtitle subtitle:
                return $"## {ConvertRichTextToMarkdown(subtitle.text)}";

            case PageBlockHeader header:
                return $"### {ConvertRichTextToMarkdown(header.text)}";

            case PageBlockSubheader subheader:
                return $"#### {ConvertRichTextToMarkdown(subheader.text)}";

            case PageBlockHeading1 heading:
                return $"# {ConvertRichTextToMarkdown(heading.text)}";

            case PageBlockHeading2 heading:
                return $"## {ConvertRichTextToMarkdown(heading.text)}";

            case PageBlockHeading3 heading:
                return $"### {ConvertRichTextToMarkdown(heading.text)}";

            case PageBlockHeading4 heading:
                return $"#### {ConvertRichTextToMarkdown(heading.text)}";

            case PageBlockHeading5 heading:
                return $"##### {ConvertRichTextToMarkdown(heading.text)}";

            case PageBlockHeading6 heading:
                return $"###### {ConvertRichTextToMarkdown(heading.text)}";

            case PageBlockKicker kicker:
                return ConvertRichTextToMarkdown(kicker.text);

            case PageBlockParagraph paragraph:
                return ConvertRichTextToMarkdown(paragraph.text);

            case PageBlockPreformatted preformatted:
                {
                    var language = string.IsNullOrWhiteSpace(preformatted.language) ? string.Empty : preformatted.language;
                    return $"```{language}\n{ConvertRichTextToMarkdown(preformatted.text)}\n```";
                }

            case PageBlockFooter footer:
                return ConvertRichTextToMarkdown(footer.text);

            case PageBlockAuthorDate authorDate:
                {
                    var author = ConvertRichTextToMarkdown(authorDate.author);
                    if (string.IsNullOrWhiteSpace(author))
                    {
                        return string.Empty;
                    }
                    return authorDate.published_date != default
                        ? $"**{author}** — {authorDate.published_date:yyyy-MM-dd}"
                        : $"**{author}**";
                }

            case PageBlockDivider:
                return "---";

            case PageBlockList list:
                return ConvertPageListItemsToMarkdown(list.items, page, photoMap, documentMap, depth);

            case PageBlockOrderedList orderedList:
                return ConvertPageOrderedListToMarkdown(orderedList, page, photoMap, documentMap, depth);

            case PageBlockBlockquote blockquote:
                return ConvertBlockquoteToMarkdown(blockquote.text, blockquote.caption);

            case PageBlockPullquote pullquote:
                return ConvertBlockquoteToMarkdown(pullquote.text, pullquote.caption);

            case PageBlockBlockquoteBlocks quoteBlocks:
                {
                    var parts = quoteBlocks.blocks?
                        .Select(b => ConvertPageBlockToMarkdown(b, page, photoMap, documentMap, depth + 1))
                        .Where(m => !string.IsNullOrWhiteSpace(m))
                        .Select(m => PrefixLines(m!, "> "))
                        .ToList() ?? [];
                    var caption = quoteBlocks.caption != null
                        ? ConvertRichTextToMarkdown(quoteBlocks.caption)
                        : string.Empty;
                    if (!string.IsNullOrWhiteSpace(caption))
                    {
                        parts.Add($"> — {caption}");
                    }
                    return string.Join("\n>\n", parts).Trim();
                }

            case PageBlockPhoto photo:
                {
                    var caption = ConvertPageCaptionToMarkdown(photo.caption);
                    if (photoMap.TryGetValue(photo.photo_id, out var fileName))
                    {
                        var alt = string.IsNullOrWhiteSpace(caption) ? fileName : caption;
                        return $"![{alt}]({MediaFileName.ToMarkdownUrl(fileName)})";
                    }
                    if (!string.IsNullOrWhiteSpace(photo.url))
                    {
                        var label = string.IsNullOrWhiteSpace(caption) ? "Photo" : caption;
                        return $"🖼️ [{label}]({photo.url})";
                    }
                    return string.IsNullOrWhiteSpace(caption) ? "🖼️ Photo" : $"🖼️ {caption}";
                }

            case PageBlockVideo video:
                {
                    var caption = ConvertPageCaptionToMarkdown(video.caption);
                    if (documentMap.TryGetValue(video.video_id, out var videoFile))
                    {
                        var alt = string.IsNullOrWhiteSpace(caption) ? videoFile : caption;
                        return $"🎬 [{alt}]({MediaFileName.ToMarkdownUrl(videoFile)})";
                    }
                    return string.IsNullOrWhiteSpace(caption) ? "🎬 Video" : $"🎬 {caption}";
                }

            case PageBlockAudio audio:
                {
                    var caption = ConvertPageCaptionToMarkdown(audio.caption);
                    if (documentMap.TryGetValue(audio.audio_id, out var audioFile))
                    {
                        var alt = string.IsNullOrWhiteSpace(caption) ? audioFile : caption;
                        return $"🎵 [{alt}]({MediaFileName.ToMarkdownUrl(audioFile)})";
                    }
                    return string.IsNullOrWhiteSpace(caption) ? "🎵 Audio" : $"🎵 {caption}";
                }

            case PageBlockCover cover:
                return ConvertPageBlockToMarkdown(cover.cover, page, photoMap, documentMap, depth);

            case PageBlockCollage collage:
                return ConvertPageNestedItemsToMarkdown(collage.items, page, photoMap, documentMap, depth);

            case PageBlockSlideshow slideshow:
                return ConvertPageNestedItemsToMarkdown(slideshow.items, page, photoMap, documentMap, depth);

            case PageBlockEmbed embed:
                {
                    var caption = ConvertPageCaptionToMarkdown(embed.caption);
                    if (!string.IsNullOrWhiteSpace(embed.url))
                    {
                        var label = string.IsNullOrWhiteSpace(caption) ? "Embed" : caption;
                        if (embed.flags.HasFlag(PageBlockEmbed.Flags.has_poster_photo_id) &&
                            photoMap.TryGetValue(embed.poster_photo_id, out var posterFile))
                        {
                            return $"![{label}]({MediaFileName.ToMarkdownUrl(posterFile)})\n[{label}]({embed.url})";
                        }
                        return $"[{label}]({embed.url})";
                    }
                    return string.IsNullOrWhiteSpace(caption) ? "🖼️ [Embed]" : $"🖼️ {caption}";
                }

            case PageBlockEmbedPost embedPost:
                {
                    var parts = new List<string>();

                    var metaParts = new List<string>();
                    if (!string.IsNullOrWhiteSpace(embedPost.author))
                    {
                        metaParts.Add(embedPost.author);
                    }
                    if (embedPost.date != default)
                    {
                        metaParts.Add(embedPost.date.ToString("yyyy-MM-dd"));
                    }
                    if (metaParts.Count > 0)
                    {
                        parts.Add($"**{string.Join(" — ", metaParts)}**");
                    }

                    var blocks = embedPost.blocks?
                        .Select(b => ConvertPageBlockToMarkdown(b, page, photoMap, documentMap, depth + 1))
                        .Where(m => !string.IsNullOrWhiteSpace(m))
                        .ToList() ?? [];
                    parts.AddRange(blocks);

                    var caption = ConvertPageCaptionToMarkdown(embedPost.caption);
                    if (!string.IsNullOrWhiteSpace(caption))
                    {
                        parts.Add(caption);
                    }
                    return string.Join("\n\n", parts).Trim();
                }

            case PageBlockChannel channelBlock:
                {
                    var chat = channelBlock.channel;
                    if (chat == null)
                    {
                        return "📢 Channel";
                    }
                    var chatTitle = !string.IsNullOrWhiteSpace(chat.Title) ? chat.Title : $"Chat {chat.ID}";
                    var username = chat.MainUsername;
                    return string.IsNullOrWhiteSpace(username)
                        ? $"📢 {chatTitle}"
                        : $"📢 [{chatTitle}](https://t.me/{username})";
                }

            case PageBlockTable table:
                return ConvertPageTableToMarkdown(table, page);

            case PageBlockDetails details:
                {
                    var title = ConvertRichTextToMarkdown(details.title);
                    var inner = details.blocks?
                        .Select(b => ConvertPageBlockToMarkdown(b, page, photoMap, documentMap, depth + 1))
                        .Where(m => !string.IsNullOrWhiteSpace(m));
                    var content = inner != null ? string.Join("\n\n", inner) : string.Empty;
                    return string.IsNullOrWhiteSpace(content)
                        ? $"**{title}**"
                        : $"**{title}**\n\n{content}";
                }

            case PageBlockRelatedArticles relatedArticles:
                {
                    var title = ConvertRichTextToMarkdown(relatedArticles.title);
                    var sb = new StringBuilder();
                    if (!string.IsNullOrWhiteSpace(title))
                    {
                        sb.AppendLine($"**{title}**");
                    }
                    foreach (var article in relatedArticles.articles ?? [])
                    {
                        var articleTitle = !string.IsNullOrWhiteSpace(article.title) ? article.title : article.url;
                        if (string.IsNullOrWhiteSpace(articleTitle))
                        {
                            continue;
                        }
                        sb.AppendLine(string.IsNullOrWhiteSpace(article.url)
                            ? $"- {articleTitle}"
                            : $"- [{articleTitle}]({article.url})");
                    }
                    return sb.ToString().Trim();
                }

            case PageBlockMap map:
                {
                    var caption = ConvertPageCaptionToMarkdown(map.caption);
                    return string.IsNullOrWhiteSpace(caption) ? "📍 Map" : $"📍 {caption}";
                }

            case PageBlockMath math:
                return $"$${math.source}$$";

            case PageBlockThinking thinking:
                return ConvertRichTextToMarkdown(thinking.text);

            default:
                return string.Empty;
        }
    }

    private string ConvertPageNestedItemsToMarkdown(PageBlock[]? items, Page page, Dictionary<long, string> photoMap, Dictionary<long, string> documentMap, int depth)
    {
        if (items == null || items.Length == 0)
        {
            return string.Empty;
        }

        return string.Join("\n\n", items
            .Select(b => ConvertPageBlockToMarkdown(b, page, photoMap, documentMap, depth + 1))
            .Where(m => !string.IsNullOrWhiteSpace(m)));
    }

    private string ConvertPageListItemsToMarkdown(PageListItem[]? items, Page page, Dictionary<long, string> photoMap, Dictionary<long, string> documentMap, int depth)
    {
        if (items == null || items.Length == 0)
        {
            return string.Empty;
        }

        var lines = new List<string>();

        foreach (var item in items)
        {
            switch (item)
            {
                case PageListItemText listItemText:
                    lines.Add($"- {ConvertRichTextToMarkdown(listItemText.text)}");
                    break;
                case PageListItemBlocks listItemBlocks:
                    {
                        var nested = ConvertPageNestedItemsToMarkdown(listItemBlocks.blocks, page, photoMap, documentMap, depth);
                        if (!string.IsNullOrWhiteSpace(nested))
                        {
                            lines.Add($"- {nested.Replace("\n", "\n  ")}");
                        }
                        break;
                    }
            }
        }

        return string.Join("\n", lines);
    }

    private string ConvertPageOrderedListToMarkdown(PageBlockOrderedList orderedList, Page page, Dictionary<long, string> photoMap, Dictionary<long, string> documentMap, int depth)
    {
        if (orderedList.items == null || orderedList.items.Length == 0)
        {
            return string.Empty;
        }

        var start = orderedList.flags.HasFlag(PageBlockOrderedList.Flags.has_start) && orderedList.start > 0
            ? orderedList.start
            : 1;

        var lines = new List<string>();

        for (var i = 0; i < orderedList.items.Length; i++)
        {
            var item = orderedList.items[i];
            var number = item switch
            {
                PageListOrderedItemText textItem => textItem.num ?? (start + i).ToString(),
                PageListOrderedItemBlocks blocksItem => blocksItem.num ?? (start + i).ToString(),
                _ => (start + i).ToString()
            };

            switch (item)
            {
                case PageListOrderedItemText orderedItemText:
                    lines.Add($"{number}. {ConvertRichTextToMarkdown(orderedItemText.text)}");
                    break;
                case PageListOrderedItemBlocks orderedItemBlocks:
                    {
                        var nested = ConvertPageNestedItemsToMarkdown(orderedItemBlocks.blocks, page, photoMap, documentMap, depth);
                        if (!string.IsNullOrWhiteSpace(nested))
                        {
                            lines.Add($"{number}. {nested.Replace("\n", "\n   ")}");
                        }
                        break;
                    }
            }
        }

        return string.Join("\n", lines);
    }

    private string ConvertBlockquoteToMarkdown(RichText? text, RichText? caption)
    {
        var lines = new List<string>();

        if (text != null)
        {
            var content = ConvertRichTextToMarkdown(text);
            if (!string.IsNullOrWhiteSpace(content))
            {
                lines.Add(PrefixLines(content, "> "));
            }
        }

        if (caption != null)
        {
            var captionText = ConvertRichTextToMarkdown(caption);
            if (!string.IsNullOrWhiteSpace(captionText))
            {
                lines.Add($"> — {captionText}");
            }
        }

        return string.Join("\n", lines).Trim();
    }

    private string ConvertPageTableToMarkdown(PageBlockTable table, Page page)
    {
        if (table.rows == null || table.rows.Length == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();

        if (table.title != null)
        {
            var title = ConvertRichTextToMarkdown(table.title);
            if (!string.IsNullOrWhiteSpace(title))
            {
                sb.AppendLine($"**{title}**");
                sb.AppendLine();
            }
        }

        var columnCount = table.rows.Max(r => r.cells?.Length ?? 0);
        if (columnCount == 0)
        {
            return sb.ToString().Trim();
        }

        for (var rowIndex = 0; rowIndex < table.rows.Length; rowIndex++)
        {
            var row = table.rows[rowIndex];
            var cells = row.cells ?? [];

            sb.Append("|");
            for (var columnIndex = 0; columnIndex < columnCount; columnIndex++)
            {
                var cellText = columnIndex < cells.Length ? ConvertRichTextToMarkdown(cells[columnIndex].text) : string.Empty;
                sb.Append($" {EscapeTableCell(cellText)} |");
            }
            sb.AppendLine();

            // Header separator after the first row
            if (rowIndex == 0)
            {
                sb.Append("|");
                for (var columnIndex = 0; columnIndex < columnCount; columnIndex++)
                {
                    sb.Append(" --- |");
                }
                sb.AppendLine();
            }
        }

        return sb.ToString().Trim();
    }

    private string ConvertPageCaptionToMarkdown(PageCaption? caption)
    {
        if (caption == null)
        {
            return string.Empty;
        }

        var text = caption.text != null ? ConvertRichTextToMarkdown(caption.text) : string.Empty;
        var credit = caption.credit != null ? ConvertRichTextToMarkdown(caption.credit) : string.Empty;

        if (string.IsNullOrWhiteSpace(credit))
        {
            return text;
        }

        return string.IsNullOrWhiteSpace(text) ? credit : $"{text} — {credit}";
    }

    private string ConvertRichTextToMarkdown(RichText? richText)
    {
        return richText switch
        {
            null => string.Empty,
            TextPlain plain => plain.text ?? string.Empty,
            TextBold bold => $"**{ConvertRichTextToMarkdown(bold.text)}**",
            TextItalic italic => $"*{ConvertRichTextToMarkdown(italic.text)}*",
            TextUnderline underline => $"<u>{ConvertRichTextToMarkdown(underline.text)}</u>",
            TextStrike strike => $"~~{ConvertRichTextToMarkdown(strike.text)}~~",
            TextFixed fixedText => $"`{ConvertRichTextToMarkdown(fixedText.text)}`",
            TextUrl url => $"[{ConvertRichTextToMarkdown(url.text)}]({url.url})",
            TextEmail email => $"[{ConvertRichTextToMarkdown(email.text)}](mailto:{email.email})",
            TextPhone phone => $"[{ConvertRichTextToMarkdown(phone.text)}](tel:{phone.phone})",
            TextSubscript subscript => $"<sub>{ConvertRichTextToMarkdown(subscript.text)}</sub>",
            TextSuperscript superscript => $"<sup>{ConvertRichTextToMarkdown(superscript.text)}</sup>",
            TextMarked marked => $"<mark>{ConvertRichTextToMarkdown(marked.text)}</mark>",
            TextConcat concat => string.Concat(concat.texts?.Select(ConvertRichTextToMarkdown) ?? []),
            TextAnchor anchor => ConvertRichTextToMarkdown(anchor.text),
            TextImage => "🖼️",
            TextCustomEmoji customEmoji => customEmoji.alt ?? string.Empty,
            TextSpoiler spoiler => $"||{ConvertRichTextToMarkdown(spoiler.text)}||",
            TextHashtag hashtag => ConvertRichTextToMarkdown(hashtag.text),
            TextMention mention => ConvertRichTextToMarkdown(mention.text),
            TextCashtag cashtag => ConvertRichTextToMarkdown(cashtag.text),
            TextBotCommand command => ConvertRichTextToMarkdown(command.text),
            TextBankCard bankCard => ConvertRichTextToMarkdown(bankCard.text),
            TextAutoUrl autoUrl => ConvertRichTextToMarkdown(autoUrl.text),
            TextAutoEmail autoEmail => ConvertRichTextToMarkdown(autoEmail.text),
            TextAutoPhone autoPhone => ConvertRichTextToMarkdown(autoPhone.text),
            TextDate date => ConvertRichTextToMarkdown(date.text),
            TextDiff diff => ConvertRichTextToMarkdown(diff.text),
            TextMath math => math.source ?? string.Empty,
            _ => string.Empty
        };
    }

    private static string PrefixLines(string content, string prefix)
    {
        var lines = content.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            lines[i] = prefix + lines[i];
        }
        return string.Join("\n", lines);
    }

    private static string EscapeTableCell(string text)
    {
        return text.Replace("|", "\\|").Replace("\n", "<br>");
    }
}
