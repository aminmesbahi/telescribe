using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Telescribe.Core.Models;
using TL;
using WTelegram;

namespace Telescribe.Core.Services;

public class ConsoleOutputSuppressor : IDisposable
{
    private readonly TextWriter _originalOut;
    private readonly TextWriter _originalError;

    public ConsoleOutputSuppressor()
    {
        _originalOut = Console.Out;
        _originalError = Console.Error;
        Console.SetOut(TextWriter.Null);
        Console.SetError(TextWriter.Null);
    }

    public void Dispose()
    {
        Console.SetOut(_originalOut);
        Console.SetError(_originalError);
        GC.SuppressFinalize(this);
    }
}

public class TelegramService : IDisposable
{
    private Client? _client;
    private readonly TelegramConfig _config;
    private readonly string _exportPath = "./exports";
    private readonly string _rawExportPath = "./exports/raw";
    private readonly string _processedExportPath = "./exports/processed";
    private readonly string _mediaPath = "./exports/media";

    public TelegramService(TelegramConfig config)
    {
        _config = config;
        EnsureDirectoriesExist();
        WTelegram.Helpers.Log = (lvl, str) => { };
    }

    public async Task<bool> AuthenticateAsync()
    {
        try
        {
            if (!ValidateConfiguration())
            {
                return false;
            }

            Console.WriteLine("Initializing Telegram client...");

            using (new ConsoleOutputSuppressor())
            {
                _client = new Client(WhatIsYourApiConfig);
            }

            Console.WriteLine("Connecting to Telegram servers...");

            using (new ConsoleOutputSuppressor())
            {
                await _client.LoginUserIfNeeded();
            }

            if (_client.User != null)
            {
                Console.WriteLine($"✅ Successfully authenticated as: {_client.User.first_name} {_client.User.last_name}");
                return true;
            }

            Console.WriteLine("Authentication failed: No user information received");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Authentication failed: {ex.Message}");

            if (ex.Message.Contains("hex string"))
            {
                Console.WriteLine("\nThis error usually means your API Hash is invalid.");
                Console.WriteLine("Please ensure you have a valid API Hash from https://my.telegram.org/");
            }
            else if (ex.Message.Contains("api_id"))
            {
                Console.WriteLine("\nThis error usually means your API ID is invalid.");
                Console.WriteLine("Please ensure you have a valid API ID from https://my.telegram.org/");
            }
            else if (ex.Message.Contains("phone_number"))
            {
                Console.WriteLine("\nPlease ensure your phone number is in international format (e.g., +1234567890)");
            }

            return false;
        }
    }

    private bool ValidateConfiguration()
    {
        var errors = new List<string>();

        if (_config.ApiId <= 0)
        {
            errors.Add("API ID must be a positive number (get it from https://my.telegram.org/)");
        }

        if (string.IsNullOrWhiteSpace(_config.ApiHash) || _config.ApiHash == "your_api_hash")
        {
            errors.Add("API Hash is required (get it from https://my.telegram.org/)");
        }
        else if (_config.ApiHash.Length != 32)
        {
            errors.Add("API Hash should be 32 characters long");
        }

        if (string.IsNullOrWhiteSpace(_config.PhoneNumber) || _config.PhoneNumber == "+1234567890")
        {
            errors.Add("Phone Number is required in international format (e.g., +1234567890)");
        }

        if (string.IsNullOrWhiteSpace(_config.ChannelId) || _config.ChannelId == "your_channel_id")
        {
            errors.Add("Channel ID or username is required (e.g., @channelname or channel ID)");
        }

        if (errors.Any())
        {
            Console.WriteLine("❌ Configuration validation failed:");
            foreach (var error in errors)
            {
                Console.WriteLine($"   • {error}");
            }
            Console.WriteLine("\n📝 To get Telegram API credentials:");
            Console.WriteLine("   1. Go to https://my.telegram.org/");
            Console.WriteLine("   2. Log in with your phone number");
            Console.WriteLine("   3. Go to 'API Development Tools'");
            Console.WriteLine("   4. Create a new application");
            Console.WriteLine("   5. Copy the api_id and api_hash");
            Console.WriteLine("\n💡 Update the configuration in appsettings.json with your real values.");
            return false;
        }

        return true;
    }

    private string? WhatIsYourApiConfig(string what)
    {
        switch (what)
        {
            case "api_id": return _config.ApiId.ToString();
            case "api_hash": return _config.ApiHash;
            case "phone_number": return _config.PhoneNumber;
            case "verification_code":
                Console.Write("Enter verification code: ");
                return Console.ReadLine();
            case "password":
                Console.Write("Enter 2FA password: ");
                return Console.ReadLine();
            default: return null;
        }
    }

    public async Task<ExportSummary> ExportChannelPostsAsync()
    {
        if (_client == null)
            throw new InvalidOperationException("Client not authenticated");

        var exportSummary = new ExportSummary
        {
            ExportTime = DateTime.UtcNow,
            ExportPath = _exportPath
        };

        try
        {
            Messages_Dialogs dialogs;
            using (new ConsoleOutputSuppressor())
            {
                dialogs = await _client.Messages_GetAllDialogs();
            }

            var targetChat = FindTargetChatInDialogs(dialogs);

            if (targetChat == null)
            {
                throw new InvalidOperationException($"Channel/Chat '{_config.ChannelId}' not found");
            }

            Console.WriteLine($"Found target chat: {GetChatDisplayName(targetChat)}");

            InputPeer inputPeer = targetChat switch
            {
                Channel channel => new InputPeerChannel(channel.id, channel.access_hash),
                Chat chat => new InputPeerChat(chat.id),
                _ => throw new InvalidOperationException("Unsupported chat type")
            };

            var posts = new List<TelegramPost>();
            var offsetId = 0;
            var batchSize = 100;
            var totalRequested = 1000;
            var batchesProcessed = 0;

            Console.WriteLine($"Starting export of up to {totalRequested} messages...");

            while (posts.Count < totalRequested && batchesProcessed < 20)
            {
                Messages_MessagesBase messages;
                using (new ConsoleOutputSuppressor())
                {
                    messages = await _client.Messages_GetHistory(inputPeer, offset_id: offsetId, limit: batchSize);
                }

                if (messages.Messages.Length == 0)
                {
                    Console.WriteLine("No more messages to retrieve");
                    break;
                }

                Console.WriteLine($"Retrieved batch {batchesProcessed + 1}: {messages.Messages.Length} messages");

                var batchPosts = 0;
                foreach (var messageBase in messages.Messages)
                {
                    if (messageBase is Message message)
                    {
                        var post = await ConvertToTelegramPostAsync(message);
                        if (post == null) continue;

                        posts.Add(post);
                        await SavePostAsMarkdownAsync(post);
                        batchPosts++;

                        exportSummary.Posts.Add(new PostSummary
                        {
                            PostId = post.Id,
                            CreatedAt = post.CreatedAt,
                            IsEdited = post.IsEdited,
                            EditedAt = post.EditedAt,
                            Views = post.Views,
                            Reactions = post.Reactions,
                            TotalForwards = post.TotalForwards,
                            PublicForwards = post.PublicForwards,
                            PrivateForwards = post.PrivateForwards,
                            ContentPreview = GetContentPreview(post.Content)
                        });

                        offsetId = message.id;
                    }
                    else
                    {
                        Console.WriteLine($"  Skipped non-Message: {messageBase?.GetType().Name} id={messageBase?.ID}");
                    }
                }

                Console.WriteLine($"Processed {batchPosts} posts from this batch (Total: {posts.Count})");
                batchesProcessed++;

                await Task.Delay(100);
            }

            exportSummary.TotalPosts = posts.Count;
            exportSummary.MediaFilesCount = posts.Sum(p => p.MediaFiles.Count);

            await SaveSummaryAsync(exportSummary);

            Console.WriteLine($"Exported {posts.Count} posts to {_exportPath}");

            return exportSummary;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Export failed: {ex.Message}");
            throw;
        }
    }

    private ChatBase? FindTargetChatInDialogs(Messages_Dialogs dialogs)
    {
        if (long.TryParse(_config.ChannelId, out var channelId))
        {
            var chatById = dialogs.chats.Values.FirstOrDefault(c => c.ID == channelId);
            if (chatById != null) return chatById;
        }

        return dialogs.chats.Values.FirstOrDefault(c =>
            (c is Channel channel &&
             (channel.username?.Equals(_config.ChannelId, StringComparison.OrdinalIgnoreCase) == true ||
              channel.title?.Contains(_config.ChannelId, StringComparison.OrdinalIgnoreCase) == true)) ||
            (c is Chat chat &&
             chat.title?.Contains(_config.ChannelId, StringComparison.OrdinalIgnoreCase) == true));
    }

    private string GetChatDisplayName(ChatBase chat)
    {
        return chat switch
        {
            Channel channel => channel.title ?? $"Channel {channel.ID}",
            Chat regularChat => regularChat.title ?? $"Chat {regularChat.id}",
            _ => $"Unknown chat {chat.ID}"
        };
    }

    private async Task<TelegramPost?> ConvertToTelegramPostAsync(Message message)
    {
        // Never return null: always produce at minimum a post with raw content.
        // This ensures no post is ever silently dropped from the export.
        var post = new TelegramPost
        {
            Id = message.id,
            CreatedAt = message.Date,
            Views = message.views,
            IsEdited = message.edit_date != default,
            EditedAt = message.edit_date != default ? message.edit_date : null,
            TotalForwards = message.forwards,
            PublicForwards = 0,
            PrivateForwards = 0
        };

        if (message.reactions?.results != null)
            post.Reactions = message.reactions.results.Sum(r => r.count);

        try
        {
            post.Content = ProcessMessageEntities(message.message ?? string.Empty, message.entities);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ⚠ Entity processing failed for message {message.id}: {ex.GetType().Name}: {ex.Message}");
            post.Content = message.message ?? string.Empty;
        }

        try
        {
            if (message.media != null)
                await HandleMediaAsync(post, message.media);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ⚠ Media processing failed for message {message.id}: {ex.GetType().Name}: {ex.Message}");
        }

        return post;
    }

    private async Task HandleMediaAsync(TelegramPost post, MessageMedia media)
    {
        try
        {
            switch (media)
            {
                case MessageMediaPhoto photo:
                    {
                        var fileName = $"{post.Id}_photo.jpg";
                        var filePath = Path.Combine(_mediaPath, fileName);

                        if (photo.photo is Photo photoData && _client != null)
                        {
                            using var fileStream = File.Create(filePath);
                            using (new ConsoleOutputSuppressor())
                            {
                                await _client.DownloadFileAsync(photoData, fileStream);
                            }
                            post.MediaFiles.Add(fileName);
                            post.PostType = "photo";
                            Console.WriteLine($"Downloaded photo: {fileName}");
                        }
                        break;
                    }
                case MessageMediaDocument document:
                    {
                        if (document.document is Document doc)
                        {
                            var fileName = GetDocumentFileName(doc, post.Id);
                            var filePath = Path.Combine(_mediaPath, fileName);

                            if (_client != null)
                            {
                                using var fileStream = File.Create(filePath);
                                using (new ConsoleOutputSuppressor())
                                {
                                    await _client.DownloadFileAsync(doc, fileStream);
                                }
                                post.MediaFiles.Add(fileName);
                                post.PostType = "document";
                                Console.WriteLine($"Downloaded document: {fileName}");
                            }
                        }
                        break;
                    }
                case MessageMediaWebPage webPageMedia:
                    {
                        await HandleWebPageMediaAsync(post, webPageMedia.webpage);
                        break;
                    }
                case MessageMediaPoll pollMedia:
                    {
                        HandlePollMedia(post, pollMedia);
                        break;
                    }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to handle media for post {post.Id}: {ex.Message}");
        }
    }

    private void HandlePollMedia(TelegramPost post, MessageMediaPoll pollMedia)
    {
        var poll = pollMedia.poll;
        var results = pollMedia.results;

        var sb = new StringBuilder();

        // Process poll question with entities (if any)
        if (poll.question != null)
        {
            var questionText = ProcessMessageEntities(poll.question.text ?? string.Empty, poll.question.entities);
            if (!string.IsNullOrWhiteSpace(questionText))
            {
                sb.AppendLine($"## {questionText}");
                sb.AppendLine();
            }
        }

        // Determine if we have detailed results
        var pollResults = results;
        var hasResults = pollResults?.results != null && pollResults.results.Length > 0;

        // Build a map from option bytes -> voter count
        var optionVoterMap = new Dictionary<string, int>();
        var totalVoters = 0;
        if (hasResults)
        {
            foreach (var result in pollResults!.results)
            {
                var voterCount = result.voters;
                optionVoterMap[result.option] = voterCount;
                totalVoters += voterCount;
            }
            if (pollResults!.total_voters > totalVoters)
                totalVoters = pollResults.total_voters;
        }

        // Render answers with their vote counts
        if (poll.answers != null)
        {
            foreach (var answerBase in poll.answers)
            {
                if (answerBase is PollAnswer answer)
                {
                    var answerText = answer.text?.text ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(answerText)) continue;

                    if (hasResults && optionVoterMap.TryGetValue(answer.option, out var count))
                    {
                        var percentage = totalVoters > 0 ? (count * 100.0 / totalVoters) : 0;
                        var barLength = (int)Math.Round(percentage / 5);
                        var bar = new string('█', barLength) + new string('░', 20 - barLength);
                        sb.AppendLine($"  - {bar} **{answerText}** — {count} vote{(count == 1 ? "" : "s")} ({percentage:F0}%)");
                    }
                    else
                    {
                        sb.AppendLine($"  - {answerText}");
                    }
                }
            }
            sb.AppendLine();
        }

        if (hasResults && totalVoters > 0)
        {
            sb.AppendLine($"  _Total: {totalVoters} voter{(totalVoters == 1 ? "" : "s")}_");
            sb.AppendLine();
        }

        // Handle quiz solution if present
        if (pollResults?.solution != null)
        {
            var solutionText = ProcessMessageEntities(pollResults.solution, pollResults.solution_entities);
            if (!string.IsNullOrWhiteSpace(solutionText))
            {
                sb.AppendLine("---");
                sb.AppendLine();
                sb.AppendLine(solutionText);
                sb.AppendLine();
            }
        }

        post.PostType = "poll";

        var pollMarkdown = sb.ToString().Trim();
        if (!string.IsNullOrWhiteSpace(pollMarkdown))
        {
            if (!string.IsNullOrWhiteSpace(post.Content))
                post.Content += "\n\n" + pollMarkdown;
            else
                post.Content = pollMarkdown;
        }
    }

    private string GetDocumentFileName(Document document, long postId)
    {
        var filenameAttr = document.attributes?.OfType<DocumentAttributeFilename>().FirstOrDefault();
        if (filenameAttr?.file_name != null)
        {
            return $"{postId}_{filenameAttr.file_name}";
        }

        var extension = document.mime_type switch
        {
            "image/jpeg" => ".jpg",
            "image/png" => ".png",
            "video/mp4" => ".mp4",
            "audio/mpeg" => ".mp3",
            _ => ""
        };

        return $"{postId}_document{extension}";
    }

    private async Task HandleWebPageMediaAsync(TelegramPost post, WebPageBase? webpageBase)
    {
        try
        {
            if (webpageBase is not WebPage webpage)
            {
                return;
            }

            // Telegram does not always set webpage.type == "article" for channel article previews.
            // Some real article posts arrive with a cached_page, title/url metadata, or a page
            // description but no explicit type flag. Treat these as article content if they carry
            // the article metadata that Telegram exposes in the preview object.
            var isArticle = IsArticleWebPage(webpage);

            if (!isArticle)
            {
                // Regular link previews: the message text already contains the post
                // content, so there is nothing else to extract.
                return;
            }

            post.PostType = "article";
            post.ArticleTitle = webpage.title ?? string.Empty;
            post.ArticleAuthor = webpage.author ?? string.Empty;
            post.ArticleUrl = !string.IsNullOrWhiteSpace(webpage.url)
                ? webpage.url
                : webpage.display_url ?? string.Empty;
            post.SiteName = webpage.site_name ?? string.Empty;

            var articleParts = new List<string>();

            // Cover image of the article
            if (webpage.photo is Photo coverPhoto)
            {
                var fileName = $"{post.Id}_article_cover.jpg";
                var filePath = Path.Combine(_mediaPath, fileName);

                if (_client != null)
                {
                    using var fileStream = File.Create(filePath);
                    using (new ConsoleOutputSuppressor())
                    {
                        await _client.DownloadFileAsync(coverPhoto, fileStream);
                    }
                    post.MediaFiles.Add(fileName);
                    Console.WriteLine($"Downloaded article cover: {fileName}");
                }
            }

            // Document directly attached to the webpage (e.g. a video preview)
            if (webpage.document is Document webpageDoc && _client != null)
            {
                var docFileName = GetDocumentFileName(webpageDoc, post.Id);
                var docFilePath = Path.Combine(_mediaPath, docFileName);
                using var docStream = File.Create(docFilePath);
                using (new ConsoleOutputSuppressor())
                {
                    await _client.DownloadFileAsync(webpageDoc, docStream);
                }
                post.MediaFiles.Add(docFileName);
                Console.WriteLine($"Downloaded article document: {docFileName}");
            }

            if (webpage.cached_page != null)
            {
                post.IsPartialArticle = webpage.cached_page.flags.HasFlag(Page.Flags.part);
                var photoMap = await DownloadArticlePhotosAsync(post, webpage.cached_page);
                var documentMap = await DownloadArticleDocumentsAsync(post, webpage.cached_page);
                var body = ConvertPageBlocksToMarkdown(webpage.cached_page, photoMap, documentMap, post);
                if (!string.IsNullOrWhiteSpace(body))
                {
                    articleParts.Add(body);
                }
            }

            // Fallback for articles without a cached page: description, then embed URL
            if (articleParts.Count == 0 && !string.IsNullOrWhiteSpace(webpage.description))
            {
                articleParts.Add(webpage.description);
            }

            if (articleParts.Count == 0 && !string.IsNullOrWhiteSpace(webpage.embed_url))
            {
                articleParts.Add($"[Embedded content]({webpage.embed_url})");
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
                var filePath = Path.Combine(_mediaPath, fileName);

                try
                {
                    using var fileStream = File.Create(filePath);
                    using (new ConsoleOutputSuppressor())
                    {
                        await _client.DownloadFileAsync(photo, fileStream);
                    }
                    post.MediaFiles.Add(fileName);
                    photoMap[photoId] = fileName;
                    Console.WriteLine($"Downloaded article photo: {fileName}");
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
                var filePath = Path.Combine(_mediaPath, fileName);

                try
                {
                    using var fileStream = File.Create(filePath);
                    using (new ConsoleOutputSuppressor())
                    {
                        await _client.DownloadFileAsync(doc, fileStream);
                    }
                    post.MediaFiles.Add(fileName);
                    documentMap[docId] = fileName;
                    Console.WriteLine($"Downloaded article document: {fileName}");
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
                        return $"![{alt}](./media/{fileName})";
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
                        return $"🎬 [{alt}](./media/{videoFile})";
                    }
                    return string.IsNullOrWhiteSpace(caption) ? "🎬 Video" : $"🎬 {caption}";
                }

            case PageBlockAudio audio:
                {
                    var caption = ConvertPageCaptionToMarkdown(audio.caption);
                    if (documentMap.TryGetValue(audio.audio_id, out var audioFile))
                    {
                        var alt = string.IsNullOrWhiteSpace(caption) ? audioFile : caption;
                        return $"🎵 [{alt}](./media/{audioFile})";
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
                            return $"![{label}](./media/{posterFile})\n[{label}]({embed.url})";
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

    private string ProcessMessageEntities(string text, MessageEntity[]? entities)
    {
        if (string.IsNullOrEmpty(text) || entities == null || entities.Length == 0)
            return text;

        var entityRanges = new List<(int offset, int length, string replacement)>();
        var processedRanges = new HashSet<(int, int)>();

        // Phase 1: Process blockquotes first (structural elements that modify line prefixes)
        foreach (var entity in entities.OrderByDescending(e => e.offset))
        {
            if (entity is not MessageEntityBlockquote) continue;
            if (entity.offset < 0 || entity.length <= 0 || entity.offset + entity.length > text.Length) continue;

            var entityText = text.Substring(entity.offset, entity.length);
            var lines = entityText.Split('\n');
            var replacement = string.Join("\n", lines.Select(l => "> " + l));

            processedRanges.Add((entity.offset, entity.length));
            entityRanges.Add((entity.offset, entity.length, replacement));
        }

        // Phase 2: Process remaining inline entities with priority ordering
        var remainingEntities = entities
            .Where(e => e is not MessageEntityBlockquote)
            .OrderBy(e => e switch
            {
                MessageEntityTextUrl => 0,
                MessageEntityUrl => 0,
                MessageEntityPre => 1,
                MessageEntityCode => 2,
                _ => 3
            })
            .ThenBy(e => e.offset)
            .ToList();

        foreach (var entity in remainingEntities)
        {
            if (entity.offset < 0 || entity.length <= 0 || entity.offset + entity.length > text.Length) continue;

            // Skip if this range overlaps with a higher-priority entity or blockquote
            if (processedRanges.Any(range =>
                (entity.offset >= range.Item1 && entity.offset < range.Item1 + range.Item2) ||
                (entity.offset + entity.length > range.Item1 && entity.offset + entity.length <= range.Item1 + range.Item2) ||
                (entity.offset <= range.Item1 && entity.offset + entity.length >= range.Item1 + range.Item2)))
            {
                continue;
            }

            var entityText = text.Substring(entity.offset, entity.length);
            string? replacement = null;

            switch (entity)
            {
                case MessageEntityTextUrl textUrl:
                    replacement = $"[{entityText}]({textUrl.url})";
                    processedRanges.Add((entity.offset, entity.length));
                    break;

                case MessageEntityUrl:
                    replacement = $"[{entityText}]({entityText})";
                    processedRanges.Add((entity.offset, entity.length));
                    break;

                case MessageEntityBold:
                    replacement = $"**{entityText}**";
                    break;

                case MessageEntityItalic:
                    replacement = $"*{entityText}*";
                    break;

                case MessageEntityUnderline:
                    replacement = $"<u>{entityText}</u>";
                    break;

                case MessageEntityStrike:
                    replacement = $"~~{entityText}~~";
                    break;

                case MessageEntitySpoiler:
                    replacement = $"||{entityText}||";
                    break;

                case MessageEntityCode:
                    replacement = $"`{entityText}`";
                    processedRanges.Add((entity.offset, entity.length));
                    break;

                case MessageEntityPre pre:
                    var language = !string.IsNullOrEmpty(pre.language) ? pre.language : "";
                    replacement = $"```{language}\n{entityText}\n```";
                    processedRanges.Add((entity.offset, entity.length));
                    break;

                case MessageEntityEmail:
                    replacement = $"[{entityText}](mailto:{entityText})";
                    break;

                case MessageEntityPhone:
                    replacement = $"[{entityText}](tel:{entityText})";
                    break;

                case MessageEntityMention:
                case MessageEntityHashtag:
                case MessageEntityCashtag:
                case MessageEntityBotCommand:
                case MessageEntityBankCard:
                case MessageEntityMentionName:
                case MessageEntityUnknown:
                    replacement = entityText;
                    break;

                case MessageEntityCustomEmoji:
                    replacement = entityText;
                    break;

                case MessageEntityFormattedDate:
                    replacement = entityText;
                    break;

                case MessageEntityDiffInsert:
                    replacement = $"<ins>{entityText}</ins>";
                    break;

                case MessageEntityDiffReplace diffReplace:
                    replacement = $"<del>{diffReplace.old_text}</del><ins>{entityText}</ins>";
                    processedRanges.Add((entity.offset, entity.length));
                    break;

                case MessageEntityDiffDelete:
                    replacement = $"<del>{entityText}</del>";
                    break;
            }

            if (replacement != null)
            {
                entityRanges.Add((entity.offset, entity.length, replacement));
            }
        }

        // Sort by offset descending to process from end to start (prevents offset shifts)
        entityRanges = entityRanges.OrderByDescending(e => e.offset).ToList();

        foreach (var (offset, length, replacement) in entityRanges)
        {
            text = text.Remove(offset, length);
            text = text.Insert(offset, replacement);
        }

        return text;
    }

    private async Task SavePostAsMarkdownAsync(TelegramPost post)
    {
        var fileName = $"{post.Id}.md";

        var rawContent = GenerateRawMarkdownContent(post);
        var rawFilePath = Path.Combine(_rawExportPath, fileName);
        await File.WriteAllTextAsync(rawFilePath, rawContent, System.Text.Encoding.UTF8);

        if (post.IsLlmProcessed)
        {
            var processedContent = GenerateProcessedMarkdownContent(post);
            var processedFilePath = Path.Combine(_processedExportPath, fileName);
            await File.WriteAllTextAsync(processedFilePath, processedContent, System.Text.Encoding.UTF8);
        }

        var mainContent = post.IsLlmProcessed ? GenerateProcessedMarkdownContent(post) : rawContent;
        var mainFilePath = Path.Combine(_exportPath, fileName);
        await File.WriteAllTextAsync(mainFilePath, mainContent, System.Text.Encoding.UTF8);
    }

    private string GenerateRawMarkdownContent(TelegramPost post)
    {
        var header = $"""
            **Created:** {post.CreatedAt:yyyy-MM-dd HH:mm:ss} UTC
            {(post.IsEdited ? $"**Edited:** {post.EditedAt:yyyy-MM-dd HH:mm:ss} UTC" : "")}
            **Post Type:** {GetPostTypeLabel(post.PostType)}
            **Views:** {post.Views}
            **Reactions:** {post.Reactions}
            **Forwards:** {post.TotalForwards}

            """;

        if (post.PostType == "article")
        {
            if (!string.IsNullOrWhiteSpace(post.ArticleTitle))
            {
                header += $"# {post.ArticleTitle}\n\n";
            }
            if (!string.IsNullOrWhiteSpace(post.ArticleAuthor))
            {
                header += $"**Author:** {post.ArticleAuthor}\n\n";
            }
            if (!string.IsNullOrWhiteSpace(post.SiteName))
            {
                header += $"**Site:** {post.SiteName}\n\n";
            }
            if (!string.IsNullOrWhiteSpace(post.ArticleUrl))
            {
                header += $"**Source:** [{post.ArticleUrl}]({post.ArticleUrl})\n\n";
            }
            if (post.IsPartialArticle)
            {
                header += "> \u26a0\ufe0f Article content is partial (Telegram Instant View delivered in segments).\n\n";
            }
        }

        var contentBody = "";

        if (post.MediaFiles.Any())
        {
            foreach (var mediaFile in post.MediaFiles)
            {
                contentBody += GenerateMediaMarkdown(mediaFile);
            }

            if (!string.IsNullOrWhiteSpace(post.Content))
            {
                contentBody += "\n";
            }
        }

        if (!string.IsNullOrWhiteSpace(post.Content))
        {
            contentBody += post.Content + "\n";
        }

        if (string.IsNullOrWhiteSpace(contentBody) && !post.MediaFiles.Any())
        {
            contentBody = post.PostType == "article"
                ? "This article was shared as a preview without extracted body text.\n"
                : "[No content]\n";
        }

        return header + contentBody;
    }

    private string GenerateProcessedMarkdownContent(TelegramPost post)
    {
        var title = !string.IsNullOrWhiteSpace(post.GeneratedTitle)
            ? post.GeneratedTitle
            : !string.IsNullOrWhiteSpace(post.ArticleTitle)
                ? post.ArticleTitle
                : $"Post {post.Id}";

        var header = $"""
            # {title}

            **Created:** {post.CreatedAt:yyyy-MM-dd HH:mm:ss} UTC
            {(post.IsEdited ? $"**Edited:** {post.EditedAt:yyyy-MM-dd HH:mm:ss} UTC" : "")}
            **Post Type:** {GetPostTypeLabel(post.PostType)}
            **Views:** {post.Views}
            **Reactions:** {post.Reactions}
            **Forwards:** {post.TotalForwards}
            {(post.IsLlmProcessed ? $"**LLM Processed:** Yes ({post.LlmProvider})" : "")}

            """;

        if (post.PostType == "article")
        {
            if (!string.IsNullOrWhiteSpace(post.ArticleAuthor))
            {
                header += $"**Author:** {post.ArticleAuthor}\n\n";
            }
            if (!string.IsNullOrWhiteSpace(post.SiteName))
            {
                header += $"**Site:** {post.SiteName}\n\n";
            }
            if (!string.IsNullOrWhiteSpace(post.ArticleUrl))
            {
                header += $"**Source:** [{post.ArticleUrl}]({post.ArticleUrl})\n\n";
            }
            if (post.IsPartialArticle)
            {
                header += "> ⚠️ Article content is partial (Telegram Instant View delivered in segments).\n\n";
            }
        }

        if (post.Hashtags.Any())
        {
            header += $"**Tags:** {string.Join(" ", post.Hashtags.Select(h => $"#{h}"))}\n\n";
        }

        header += "## Content\n\n";

        var contentBody = "";

        if (post.MediaFiles.Any())
        {
            foreach (var mediaFile in post.MediaFiles)
            {
                contentBody += GenerateMediaMarkdown(mediaFile);
            }

            if (!string.IsNullOrWhiteSpace(post.Content))
            {
                contentBody += "\n";
            }
        }

        if (!string.IsNullOrWhiteSpace(post.Content))
        {
            contentBody += post.Content + "\n";
        }

        if (string.IsNullOrWhiteSpace(contentBody) && !post.MediaFiles.Any())
        {
            contentBody = post.PostType == "article"
                ? "This article was shared as a preview without extracted body text.\n"
                : "[No content]\n";
        }

        return header + contentBody;
    }

    private static string GetPostTypeLabel(string postType)
    {
        return postType switch
        {
            "article" => "Article",
            "poll" => "Poll",
            "photo" => "Photo",
            "document" => "Document",
            "video" => "Video",
            "audio" => "Audio",
            _ => string.IsNullOrWhiteSpace(postType) ? "Text" : char.ToUpperInvariant(postType[0]) + postType.Substring(1)
        };
    }

    private string GenerateMediaMarkdown(string mediaFile)
    {
        var extension = Path.GetExtension(mediaFile).ToLowerInvariant();

        return extension switch
        {
            ".jpg" or ".jpeg" or ".png" or ".gif" =>
                $"![{mediaFile}](./media/{mediaFile})\n\n",
            ".mp4" or ".mov" or ".avi" =>
                $"🎬 **Video:** [Click to view {mediaFile}](./media/{mediaFile})\n\n",
            ".mp3" or ".wav" or ".ogg" =>
                $"🎵 **Audio:** [Click to play {mediaFile}](./media/{mediaFile})\n\n",
            ".pdf" =>
                $"📄 **Document:** [Click to open {mediaFile}](./media/{mediaFile})\n\n",
            _ =>
                $"📎 **File:** [Download {mediaFile}](./media/{mediaFile})\n\n"
        };
    }

    private string GetContentPreview(string content)
    {
        if (string.IsNullOrEmpty(content))
            return string.Empty;

        if (content.Length <= _config.SummaryCharacterCount)
            return content;

        return content.Substring(0, _config.SummaryCharacterCount) + "...";
    }

    private async Task SaveSummaryAsync(ExportSummary summary)
    {
        var json = JsonSerializer.Serialize(summary, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        var filePath = Path.Combine(_exportPath, "summary.json");
        summary.SummaryFilePath = filePath;
        await File.WriteAllTextAsync(filePath, json);
    }

    public async Task<ExportSummary?> GetLastExportSummaryAsync()
    {
        var filePath = Path.Combine(_exportPath, "summary.json");

        if (!File.Exists(filePath))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(filePath);
            return JsonSerializer.Deserialize<ExportSummary>(json, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error reading last export summary: {ex.Message}");
            return null;
        }
    }

    private void EnsureDirectoriesExist()
    {
        Directory.CreateDirectory(_exportPath);
        Directory.CreateDirectory(_rawExportPath);
        Directory.CreateDirectory(_processedExportPath);
        Directory.CreateDirectory(_mediaPath);
    }

    public async Task<UpdateSummary> UpdateExistingExportsAsync(DateTime lastExportDate)
    {
        if (_client == null)
            throw new InvalidOperationException("Client not authenticated");

        var updateSummary = new UpdateSummary
        {
            UpdateTime = DateTime.UtcNow,
            ExportPath = _exportPath,
            DateRangeFrom = lastExportDate,
            Status = "Starting"
        };

        try
        {
            var existingSummary = await GetLastExportSummaryAsync();
            var existingPostIds = existingSummary?.Posts
                .Select(p => p.PostId)
                .ToHashSet() ?? [];

            Messages_Dialogs dialogs;
            using (new ConsoleOutputSuppressor())
            {
                dialogs = await _client.Messages_GetAllDialogs();
            }

            var targetChat = FindTargetChatInDialogs(dialogs);

            if (targetChat == null)
            {
                updateSummary.Status = "Failed";
                updateSummary.ErrorMessage = $"Channel/Chat '{_config.ChannelId}' not found";
                return updateSummary;
            }

            Console.WriteLine($"Found target chat: {GetChatDisplayName(targetChat)}");

            InputPeer inputPeer = targetChat switch
            {
                Channel channel => new InputPeerChannel(channel.id, channel.access_hash),
                Chat chat => new InputPeerChat(chat.id),
                _ => throw new InvalidOperationException("Unsupported chat type")
            };

            var newPosts = new List<TelegramPost>();
            var refreshedPosts = new List<TelegramPost>();
            var offsetId = 0;
            var batchSize = 100;
            var maxBatches = 10;
            var batchesProcessed = 0;

            Console.WriteLine($"Looking for posts newer than {lastExportDate:yyyy-MM-dd HH:mm:ss} UTC...");

            while (batchesProcessed < maxBatches)
            {
                Messages_MessagesBase messages;
                using (new ConsoleOutputSuppressor())
                {
                    messages = await _client.Messages_GetHistory(inputPeer, offset_id: offsetId, limit: batchSize);
                }

                if (messages.Messages.Length == 0)
                {
                    Console.WriteLine("No more messages to retrieve");
                    break;
                }

                Console.WriteLine($"Retrieved batch {batchesProcessed + 1}: {messages.Messages.Length} messages");

                var batchNewPosts = 0;
                var batchRefreshedPosts = 0;

                foreach (var messageBase in messages.Messages)
                {
                    if (messageBase is Message message)
                    {
                        var messageDate = message.Date;

                        var isNewPost = messageDate > lastExportDate && !existingPostIds.Contains(message.id);
                        var isExistingExport = existingPostIds.Contains(message.id);

                        if (!isNewPost && !isExistingExport)
                        {
                            offsetId = message.id;
                            continue;
                        }

                        var post = await ConvertToTelegramPostAsync(message);
                        if (post != null)
                        {
                            await SavePostAsMarkdownAsync(post);

                            if (isNewPost)
                            {
                                newPosts.Add(post);
                                updateSummary.NewPosts.Add(CreatePostSummary(post));
                                batchNewPosts++;
                                existingPostIds.Add(post.Id);
                            }
                            else if (isExistingExport)
                            {
                                refreshedPosts.Add(post);
                                batchRefreshedPosts++;
                            }
                        }

                        offsetId = message.id;
                    }
                }

                Console.WriteLine($"Found {batchNewPosts} new posts and refreshed {batchRefreshedPosts} existing posts in this batch");
                batchesProcessed++;

                await Task.Delay(100);
            }

            updateSummary.NewPostsCount = newPosts.Count;
            updateSummary.RefreshedPostsCount = refreshedPosts.Count;
            updateSummary.DateRangeTo = newPosts.Any() ? newPosts.Max(p => p.CreatedAt) : lastExportDate;
            updateSummary.Status = "Completed";

            Console.WriteLine($"Update completed: {newPosts.Count} new posts exported, {refreshedPosts.Count} existing posts refreshed");

            if (newPosts.Any() || refreshedPosts.Any())
            {
                await UpdateExportSummaryFileAsync(updateSummary, refreshedPosts);
            }

            return updateSummary;
        }
        catch (Exception ex)
        {
            updateSummary.Status = "Failed";
            updateSummary.ErrorMessage = ex.Message;
            Console.WriteLine($"Error during update: {ex.Message}");
            return updateSummary;
        }
    }

    private PostSummary CreatePostSummary(TelegramPost post)
    {
        return new PostSummary
        {
            PostId = post.Id,
            CreatedAt = post.CreatedAt,
            IsEdited = post.IsEdited,
            EditedAt = post.EditedAt,
            Views = post.Views,
            Reactions = post.Reactions,
            TotalForwards = post.TotalForwards,
            PublicForwards = post.PublicForwards,
            PrivateForwards = post.PrivateForwards,
            ContentPreview = GetContentPreview(post.Content)
        };
    }

    private async Task UpdateExportSummaryFileAsync(UpdateSummary updateSummary, IReadOnlyCollection<TelegramPost> refreshedPosts)
    {
        try
        {
            var summaryFilePath = Path.Combine(_exportPath, "summary.json");

            ExportSummary? existingSummary = null;
            if (File.Exists(summaryFilePath))
            {
                var summaryJson = await File.ReadAllTextAsync(summaryFilePath);
                var options = new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    WriteIndented = true
                };
                existingSummary = JsonSerializer.Deserialize<ExportSummary>(summaryJson, options);
            }

            if (existingSummary != null)
            {
                existingSummary.TotalPosts += updateSummary.NewPostsCount;
                existingSummary.ExportTime = updateSummary.UpdateTime;
                existingSummary.MediaFilesCount += updateSummary.MediaFilesCount;
                foreach (var newPostSummary in updateSummary.NewPosts)
                {
                    UpsertPostSummary(existingSummary.Posts, newPostSummary);
                }

                foreach (var refreshedPost in refreshedPosts)
                {
                    UpsertPostSummary(existingSummary.Posts, CreatePostSummary(refreshedPost));
                }

                existingSummary.TotalPosts = existingSummary.Posts
                    .Select(p => p.PostId)
                    .Distinct()
                    .Count();

                var serializeOptions = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                };
                var updatedJson = JsonSerializer.Serialize(existingSummary, serializeOptions);
                await File.WriteAllTextAsync(summaryFilePath, updatedJson);

                Console.WriteLine($"Updated export summary: {existingSummary.TotalPosts} total posts");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: Could not update export summary file: {ex.Message}");
        }
    }

    private static void UpsertPostSummary(List<PostSummary> posts, PostSummary updatedPost)
    {
        var existingPost = posts.FirstOrDefault(p => p.PostId == updatedPost.PostId);
        if (existingPost == null)
        {
            posts.Add(updatedPost);
            return;
        }

        existingPost.CreatedAt = updatedPost.CreatedAt;
        existingPost.IsEdited = updatedPost.IsEdited;
        existingPost.EditedAt = updatedPost.EditedAt;
        existingPost.Views = updatedPost.Views;
        existingPost.Reactions = updatedPost.Reactions;
        existingPost.TotalForwards = updatedPost.TotalForwards;
        existingPost.PublicForwards = updatedPost.PublicForwards;
        existingPost.PrivateForwards = updatedPost.PrivateForwards;
        existingPost.ContentPreview = updatedPost.ContentPreview;
    }

    public void Dispose()
    {
        _client?.Dispose();
        GC.SuppressFinalize(this);
    }
}