using System.Text.Json;
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
                        if (post != null)
                        {
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
                        }

                        offsetId = message.id;
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
        try
        {
            var post = new TelegramPost
            {
                Id = message.id,
                CreatedAt = message.Date,
                Content = message.message ?? string.Empty,
                Views = message.views,
                IsEdited = message.edit_date != default,
                EditedAt = message.edit_date != default ? message.edit_date : null
            };

            post.TotalForwards = message.forwards;

            if (message.reactions != null)
            {
                post.Reactions = message.reactions.results?.Sum(r => r.count) ?? 0;
            }

            post.PublicForwards = 0;
            post.PrivateForwards = 0;

            if (message.media != null)
            {
                await HandleMediaAsync(post, message.media);
            }

            return post;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to convert message {message.id}: {ex.Message}");
            return null;
        }
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
                                Console.WriteLine($"Downloaded document: {fileName}");
                            }
                        }
                        break;
                    }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to handle media for post {post.Id}: {ex.Message}");
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
            **Views:** {post.Views}
            **Reactions:** {post.Reactions}
            **Forwards:** {post.TotalForwards}

            """;

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
            contentBody = "[No content]\n";
        }

        return header + contentBody;
    }

    private string GenerateProcessedMarkdownContent(TelegramPost post)
    {
        var header = $"""
            # {(!string.IsNullOrWhiteSpace(post.GeneratedTitle) ? post.GeneratedTitle : $"Post {post.Id}")}

            **Created:** {post.CreatedAt:yyyy-MM-dd HH:mm:ss} UTC
            {(post.IsEdited ? $"**Edited:** {post.EditedAt:yyyy-MM-dd HH:mm:ss} UTC" : "")}
            **Views:** {post.Views}
            **Reactions:** {post.Reactions}
            **Forwards:** {post.TotalForwards}
            {(post.IsLlmProcessed ? $"**LLM Processed:** Yes ({post.LlmProvider})" : "")}

            """;

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
            contentBody = "[No content]\n";
        }

        return header + contentBody;
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
                var foundOldPost = false;

                foreach (var messageBase in messages.Messages)
                {
                    if (messageBase is Message message)
                    {
                        var messageDate = message.Date;

                        if (messageDate <= lastExportDate)
                        {
                            foundOldPost = true;
                            break;
                        }

                        var post = await ConvertToTelegramPostAsync(message);
                        if (post != null)
                        {
                            newPosts.Add(post);
                            await SavePostAsMarkdownAsync(post);
                            batchNewPosts++;

                            updateSummary.NewPosts.Add(new PostSummary
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
                        }

                        offsetId = message.id;
                    }
                }

                Console.WriteLine($"Found {batchNewPosts} new posts in this batch (Total new: {newPosts.Count})");
                batchesProcessed++;

                if (foundOldPost)
                {
                    Console.WriteLine("Reached previously exported content. Stopping...");
                    break;
                }

                await Task.Delay(100);
            }

            updateSummary.NewPostsCount = newPosts.Count;
            updateSummary.DateRangeTo = newPosts.Any() ? newPosts.Max(p => p.CreatedAt) : lastExportDate;
            updateSummary.Status = "Completed";

            Console.WriteLine($"Update completed: {newPosts.Count} new posts exported");

            if (newPosts.Any())
            {
                await UpdateExportSummaryFileAsync(updateSummary);
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

    private async Task UpdateExportSummaryFileAsync(UpdateSummary updateSummary)
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
                existingSummary.Posts.AddRange(updateSummary.NewPosts);

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

    public void Dispose()
    {
        _client?.Dispose();
    }
}