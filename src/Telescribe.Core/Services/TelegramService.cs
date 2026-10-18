using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Telescribe.Core.Export;
using Telescribe.Core.Models;
using TL;
using WTelegram;

namespace Telescribe.Core.Services;

public partial class TelegramService : IDisposable
{
    private const int HistoryBatchSize = 100;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly JsonSerializerOptions SummaryJson = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private Client? _client;
    private readonly TelegramConfig _config;
    private readonly string _exportPath;
    private readonly string _rawExportPath;
    private readonly string _processedExportPath;
    private readonly string _mediaPath;

    public TelegramService(TelegramConfig config, string exportPath = "./exports")
    {
        _config = config;
        _exportPath = exportPath;
        _rawExportPath = Path.Combine(exportPath, "raw");
        _processedExportPath = Path.Combine(exportPath, "processed");
        _mediaPath = Path.Combine(exportPath, "media");
        EnsureDirectoriesExist();

        // WTelegramClient logs to the console by default; Telescribe prints its own progress instead.
        Helpers.Log = (_, _) => { };
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
            _client = new Client(WhatIsYourApiConfig);

            Console.WriteLine("Connecting to Telegram servers...");
            await _client.LoginUserIfNeeded();

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

        if (errors.Count == 0)
        {
            return true;
        }

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
        Console.WriteLine("\n💡 Update the configuration in appsettings.json (or user secrets / environment variables) with your real values.");
        return false;
    }

    private string? WhatIsYourApiConfig(string what)
    {
        switch (what)
        {
            case "api_id": return _config.ApiId.ToString();
            case "api_hash": return _config.ApiHash;
            case "phone_number": return _config.PhoneNumber;
            case "session_pathname": return string.IsNullOrWhiteSpace(_config.SessionPath) ? null : _config.SessionPath;
            case "verification_code":
                Console.Write("Enter verification code: ");
                return Console.ReadLine();
            case "password":
                return ReadSecret("Enter 2FA password: ");
            default: return null;
        }
    }

    /// <summary>Reads a line without echoing it (falls back to a plain read when input is redirected).</summary>
    internal static string ReadSecret(string prompt)
    {
        Console.Write(prompt);
        if (Console.IsInputRedirected)
        {
            return Console.ReadLine() ?? string.Empty;
        }

        var secret = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return secret.ToString();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (secret.Length > 0)
                {
                    secret.Length--;
                }
            }
            else if (!char.IsControl(key.KeyChar))
            {
                secret.Append(key.KeyChar);
            }
        }
    }

    public async Task<ExportSummary> ExportChannelPostsAsync(CancellationToken cancellationToken = default)
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
            var (targetChat, inputPeer) = await ResolveTargetAsync();
            Console.WriteLine($"Found target chat: {GetChatDisplayName(targetChat)}");
            Console.WriteLine("Exporting the full channel history...");

            var merger = new AlbumMerger();
            var exported = 0;

            async Task RecordAsync(TelegramPost post)
            {
                await SavePostAsMarkdownAsync(post);
                exportSummary.Posts.Add(CreatePostSummary(post));
                exportSummary.MediaFilesCount += post.MediaFiles.Count;
                exported++;
                if (exported % 100 == 0)
                {
                    Console.WriteLine($"Exported {exported} posts so far...");
                }
            }

            await foreach (var item in EnumerateHistoryAsync(inputPeer, cancellationToken))
            {
                var post = await ConvertToTelegramPostAsync(item.Message, item.ForwardedFrom);
                foreach (var completed in merger.Add(post, item.Message.grouped_id))
                {
                    await RecordAsync(completed);
                }
            }

            foreach (var completed in merger.Flush())
            {
                await RecordAsync(completed);
            }

            exportSummary.TotalPosts = exportSummary.Posts.Count;
            await SaveSummaryAsync(exportSummary);
            StatsHistory.AppendChanges(StatsHistory.DefaultPath(_exportPath), exportSummary.ExportTime, exportSummary.Posts);

            Console.WriteLine($"Exported {exportSummary.TotalPosts} posts to {_exportPath}");
            return exportSummary;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Export failed: {ex.Message}");
            throw;
        }
    }

    private sealed record HistoryMessage(Message Message, string ForwardedFrom);

    /// <summary>The display name of the original sender of a forwarded message, or empty.</summary>
    private static string ResolveForwardedFrom(Message message, IPeerResolver resolver)
    {
        var header = message.fwd_from;
        if (header is null)
        {
            return string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(header.from_name))
        {
            return header.from_name;
        }

        return header.from_id is null ? string.Empty : resolver.UserOrChat(header.from_id)?.ToString()?.TrimStart('@') ?? string.Empty;
    }

    /// <summary>Walks the channel history from the newest message to the oldest, one page at a time.</summary>
    private async IAsyncEnumerable<HistoryMessage> EnumerateHistoryAsync(
        InputPeer peer,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var offsetId = 0;
        var batch = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var page = await _client!.Messages_GetHistory(peer, offset_id: offsetId, limit: HistoryBatchSize);
            if (page.Messages.Length == 0)
            {
                yield break;
            }

            batch++;
            Console.WriteLine($"Retrieved batch {batch}: {page.Messages.Length} messages");

            var oldestId = page.Messages.Min(m => m.ID);
            if (offsetId != 0 && oldestId >= offsetId)
            {
                yield break; // no progress; avoid looping forever
            }

            foreach (var messageBase in page.Messages)
            {
                if (messageBase is Message message)
                {
                    yield return new HistoryMessage(message, ResolveForwardedFrom(message, page));
                }
            }

            offsetId = oldestId;
            await Task.Delay(100, cancellationToken);
        }
    }

    private async Task<(ChatBase Chat, InputPeer Peer)> ResolveTargetAsync()
    {
        var reference = ChannelReference.Parse(_config.ChannelId);
        ChatBase? chat = null;

        // Public channels can be resolved by username without being in the account's dialog list.
        if (reference.Username is not null)
        {
            try
            {
                var resolved = await _client!.Contacts_ResolveUsername(reference.Username);
                chat = resolved.chats.Values.FirstOrDefault(c => c.IsActive);
            }
            catch (RpcException ex) when (ex.Code == 400)
            {
                // USERNAME_NOT_OCCUPIED / USERNAME_INVALID: fall back to searching the dialogs.
            }
        }

        if (chat is null)
        {
            var dialogs = await _client!.Messages_GetAllDialogs();
            chat = FindTargetChatInDialogs(dialogs, reference);
        }

        if (chat is null)
        {
            throw new InvalidOperationException($"Channel/Chat '{_config.ChannelId}' not found");
        }

        InputPeer peer = chat switch
        {
            Channel channel => new InputPeerChannel(channel.id, channel.access_hash),
            Chat basicChat => new InputPeerChat(basicChat.id),
            _ => throw new InvalidOperationException("Unsupported chat type")
        };

        return (chat, peer);
    }

    private static ChatBase? FindTargetChatInDialogs(Messages_Dialogs dialogs, ChannelReference reference)
    {
        var chats = dialogs.chats.Values;

        if (reference.Id is { } id)
        {
            var byId = chats.FirstOrDefault(c => c.ID == id);
            if (byId is not null) return byId;
        }

        if (reference.Username is not null)
        {
            var byUsername = chats.OfType<Channel>().FirstOrDefault(c =>
                string.Equals(c.MainUsername, reference.Username, StringComparison.OrdinalIgnoreCase));
            if (byUsername is not null) return byUsername;
        }

        var title = reference.Title;
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        return chats.FirstOrDefault(c => string.Equals(c.Title, title, StringComparison.OrdinalIgnoreCase))
               ?? chats.FirstOrDefault(c => c.Title?.Contains(title, StringComparison.OrdinalIgnoreCase) == true);
    }

    private static string GetChatDisplayName(ChatBase chat)
    {
        return chat switch
        {
            Channel channel => channel.title ?? $"Channel {channel.ID}",
            Chat regularChat => regularChat.title ?? $"Chat {regularChat.id}",
            _ => $"Unknown chat {chat.ID}"
        };
    }

    private async Task<TelegramPost> ConvertToTelegramPostAsync(Message message, string forwardedFrom)
    {
        // Never drop a post: always produce at minimum one with its raw content.
        var post = new TelegramPost
        {
            Id = message.id,
            CreatedAt = message.Date,
            Views = message.views,
            IsEdited = message.edit_date != default,
            EditedAt = message.edit_date != default ? message.edit_date : null,
            TotalForwards = message.forwards,
            PublicForwards = 0,
            PrivateForwards = 0,
            ForwardedFrom = forwardedFrom,
            ReplyToMessageId = message.reply_to is MessageReplyHeader { reply_to_peer_id: null } reply && reply.reply_to_msg_id > 0
                ? reply.reply_to_msg_id
                : null
        };

        if (message.reactions?.results != null)
        {
            post.Reactions = message.reactions.results.Sum(r => r.count);
            post.ReactionBreakdown = ReactionLabels.Count(message.reactions.results.Select(r => (r.reaction, r.count)));
        }

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
                case MessageMediaPhoto { photo: Photo photoData }:
                    {
                        var fileName = $"{post.Id}_photo.jpg";
                        if (await DownloadMediaAsync(fileName, 0, stream => _client!.DownloadFileAsync(photoData, stream)))
                        {
                            post.MediaFiles.Add(fileName);
                            post.PostType = "photo";
                        }
                        break;
                    }
                case MessageMediaDocument { document: Document doc }:
                    {
                        var fileName = GetDocumentFileName(doc, post.Id);
                        if (await DownloadMediaAsync(fileName, doc.size, stream => _client!.DownloadFileAsync(doc, stream)))
                        {
                            post.MediaFiles.Add(fileName);
                            post.PostType = "document";
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

    /// <summary>
    /// Makes sure <paramref name="fileName"/> exists in the media folder. Existing files are kept (no
    /// re-download on refresh), oversized documents are skipped, and the download is written to a temporary
    /// file first so a failure never leaves a truncated file behind.
    /// </summary>
    /// <returns>True if the file is available afterwards.</returns>
    private async Task<bool> DownloadMediaAsync(string fileName, long sizeBytes, Func<Stream, Task> download)
    {
        if (_client == null)
        {
            return false;
        }

        var path = Path.Combine(_mediaPath, fileName);
        if (File.Exists(path) && new FileInfo(path).Length > 0)
        {
            return true;
        }

        if (_config.MaxMediaSizeMB > 0 && sizeBytes > _config.MaxMediaSizeMB * 1024L * 1024L)
        {
            Console.WriteLine($"Skipped {fileName}: {sizeBytes / (1024 * 1024)} MB exceeds MaxMediaSizeMB ({_config.MaxMediaSizeMB})");
            return false;
        }

        var partial = path + ".part";
        try
        {
            await using (var stream = File.Create(partial))
            {
                await download(stream);
            }

            File.Move(partial, path, overwrite: true);
            Console.WriteLine($"Downloaded: {fileName}");
            return true;
        }
        catch
        {
            if (File.Exists(partial))
            {
                File.Delete(partial);
            }

            throw;
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
                        var barLength = Math.Clamp((int)Math.Round(percentage / 5), 0, 20);
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

    private static string GetDocumentFileName(Document document, long postId)
    {
        var filenameAttr = document.attributes?.OfType<DocumentAttributeFilename>().FirstOrDefault();
        if (filenameAttr?.file_name != null)
        {
            // The name is chosen by whoever uploaded the file, so it must never be trusted as a path.
            return $"{postId}_{MediaFileName.Sanitize(filenameAttr.file_name)}";
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

    private async Task SavePostAsMarkdownAsync(TelegramPost post)
    {
        var fileName = $"{post.Id}.md";

        await File.WriteAllTextAsync(Path.Combine(_rawExportPath, fileName), PostMarkdownWriter.BuildRaw(post), Utf8NoBom);

        if (post.IsLlmProcessed)
        {
            await File.WriteAllTextAsync(Path.Combine(_processedExportPath, fileName), PostMarkdownWriter.BuildProcessed(post), Utf8NoBom);
        }

        var mainContent = post.IsLlmProcessed ? PostMarkdownWriter.BuildProcessed(post) : PostMarkdownWriter.BuildRaw(post);
        await File.WriteAllTextAsync(Path.Combine(_exportPath, fileName), mainContent, Utf8NoBom);

        DeleteMergedSiblingFiles(post);
    }

    /// <summary>
    /// When an album is merged into one post, earlier exports may already contain its other messages as
    /// separate posts; remove those files so the album does not appear twice.
    /// </summary>
    private void DeleteMergedSiblingFiles(TelegramPost post)
    {
        foreach (var siblingId in post.MergedMessageIds.Where(id => id != post.Id))
        {
            foreach (var directory in new[] { _exportPath, _rawExportPath, _processedExportPath })
            {
                var path = Path.Combine(directory, $"{siblingId}.md");
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }

    /// <summary>Restores the AI-generated title and tags of a previously processed post onto a re-exported one.</summary>
    private async Task ApplyExistingEnrichmentAsync(TelegramPost post)
    {
        var processedPath = Path.Combine(_processedExportPath, $"{post.Id}.md");
        if (!File.Exists(processedPath))
        {
            return;
        }

        var enrichment = PostMarkdown.ReadEnrichment(await File.ReadAllTextAsync(processedPath), post.Id, post.ArticleTitle);
        if (enrichment is null)
        {
            return;
        }

        post.IsLlmProcessed = true;
        post.LlmProvider = enrichment.Provider ?? string.Empty;
        post.GeneratedTitle = enrichment.GeneratedTitle ?? string.Empty;
        post.Hashtags = enrichment.Hashtags.ToList();
    }

    private string GetContentPreview(string content)
    {
        if (string.IsNullOrEmpty(content))
            return string.Empty;

        if (content.Length <= _config.SummaryCharacterCount)
            return content;

        var length = _config.SummaryCharacterCount;
        if (length > 0 && char.IsHighSurrogate(content[length - 1]))
        {
            length--;
        }

        return content[..length] + "...";
    }

    private async Task SaveSummaryAsync(ExportSummary summary)
    {
        var filePath = Path.Combine(_exportPath, "summary.json");
        summary.SummaryFilePath = filePath;
        await File.WriteAllTextAsync(filePath, JsonSerializer.Serialize(summary, SummaryJson));
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
            return JsonSerializer.Deserialize<ExportSummary>(json, SummaryJson);
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

    public async Task<UpdateSummary> UpdateExistingExportsAsync(DateTime lastExportDate, CancellationToken cancellationToken = default)
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
            var existingPostIds = existingSummary?.Posts.Select(p => p.PostId).ToHashSet() ?? [];

            ChatBase targetChat;
            InputPeer inputPeer;
            try
            {
                (targetChat, inputPeer) = await ResolveTargetAsync();
            }
            catch (InvalidOperationException ex)
            {
                updateSummary.Status = "Failed";
                updateSummary.ErrorMessage = ex.Message;
                return updateSummary;
            }

            Console.WriteLine($"Found target chat: {GetChatDisplayName(targetChat)}");

            // Posts older than this have settled statistics and are not re-fetched.
            var cutoff = lastExportDate - TimeSpan.FromDays(Math.Max(0, _config.RefreshWindowDays));
            Console.WriteLine($"Looking for posts newer than {lastExportDate:yyyy-MM-dd HH:mm:ss} UTC (refreshing posts back to {cutoff:yyyy-MM-dd})...");

            var newPosts = new List<TelegramPost>();
            var refreshedPosts = new List<TelegramPost>();
            var removedIds = new HashSet<long>();
            var merger = new AlbumMerger();

            async Task RecordAsync(TelegramPost post)
            {
                var isNew = !existingPostIds.Contains(post.Id);
                if (!isNew)
                {
                    await ApplyExistingEnrichmentAsync(post);
                }

                await SavePostAsMarkdownAsync(post);

                foreach (var siblingId in post.MergedMessageIds.Where(id => id != post.Id))
                {
                    removedIds.Add(siblingId);
                }

                if (isNew)
                {
                    newPosts.Add(post);
                    updateSummary.NewPosts.Add(CreatePostSummary(post));
                    existingPostIds.Add(post.Id);
                }
                else
                {
                    refreshedPosts.Add(post);
                }
            }

            await foreach (var item in EnumerateHistoryAsync(inputPeer, cancellationToken))
            {
                if (item.Message.Date < cutoff)
                {
                    break;
                }

                var post = await ConvertToTelegramPostAsync(item.Message, item.ForwardedFrom);
                foreach (var completed in merger.Add(post, item.Message.grouped_id))
                {
                    await RecordAsync(completed);
                }
            }

            foreach (var completed in merger.Flush())
            {
                await RecordAsync(completed);
            }

            updateSummary.NewPostsCount = newPosts.Count;
            updateSummary.RefreshedPostsCount = refreshedPosts.Count;
            updateSummary.MediaFilesCount = newPosts.Sum(p => p.MediaFiles.Count);
            updateSummary.DateRangeTo = newPosts.Count > 0 ? newPosts.Max(p => p.CreatedAt) : lastExportDate;
            updateSummary.Status = "Completed";

            Console.WriteLine($"Update completed: {newPosts.Count} new posts exported, {refreshedPosts.Count} existing posts refreshed");

            if (newPosts.Count > 0 || refreshedPosts.Count > 0)
            {
                await UpdateExportSummaryFileAsync(updateSummary, refreshedPosts, removedIds);
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
            ContentPreview = GetContentPreview(post.Content),
            ReactionBreakdown = post.ReactionBreakdown
        };
    }

    private async Task UpdateExportSummaryFileAsync(
        UpdateSummary updateSummary,
        IReadOnlyCollection<TelegramPost> refreshedPosts,
        IReadOnlySet<long> removedPostIds)
    {
        try
        {
            var existingSummary = await GetLastExportSummaryAsync();
            if (existingSummary == null)
            {
                return;
            }

            // First update since history tracking existed: record the previous export as the baseline.
            var historyPath = StatsHistory.DefaultPath(_exportPath);
            if (!File.Exists(historyPath))
            {
                StatsHistory.AppendChanges(historyPath, existingSummary.ExportTime, existingSummary.Posts);
            }

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

            existingSummary.Posts.RemoveAll(p => removedPostIds.Contains(p.PostId));
            existingSummary.TotalPosts = existingSummary.Posts.Select(p => p.PostId).Distinct().Count();

            await File.WriteAllTextAsync(Path.Combine(_exportPath, "summary.json"), JsonSerializer.Serialize(existingSummary, SummaryJson));
            StatsHistory.AppendChanges(
                StatsHistory.DefaultPath(_exportPath),
                updateSummary.UpdateTime,
                updateSummary.NewPosts.Concat(refreshedPosts.Select(CreatePostSummary)));
            Console.WriteLine($"Updated export summary: {existingSummary.TotalPosts} total posts");
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
        existingPost.ReactionBreakdown = updatedPost.ReactionBreakdown;
    }

    public void Dispose()
    {
        _client?.Dispose();
        GC.SuppressFinalize(this);
    }
}
