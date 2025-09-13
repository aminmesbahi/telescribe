using System.Text.Json;
using Telescribe.Core.Models;
using WordPressPCL;
using WordPressPCL.Models;

namespace Telescribe.Core.Services;

public class WordPressService : IWordPressService
{
    private readonly WordPressConfig _config;
    private readonly WordPressClient _client;
    private readonly string _mappingFilePath;
    private readonly Dictionary<long, WordPressPostMapping> _postMappings;
    private int? _categoryId;

    public WordPressService(WordPressConfig config)
    {
        _config = config;
        _client = new WordPressClient(_config.SiteUrl);
        _mappingFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wordpress_mappings.json");
        _postMappings = LoadPostMappings();
    }

    public async Task<bool> AuthenticateAsync()
    {
        try
        {
            Console.WriteLine("Authenticating with WordPress...");

            _client.Auth.UseBasicAuth(_config.Username, _config.ApplicationPassword);

            var users = await _client.Users.GetAllAsync();
            var currentUser = users.FirstOrDefault();
            if (currentUser != null)
            {
                Console.WriteLine($"WordPress authentication successful! Connected to site with user: {currentUser.Name}");
            }
            else
            {
                Console.WriteLine("WordPress authentication successful!");
            }

            _categoryId = await EnsureCategoryExistsAsync(_config.CategoryName);
            Console.WriteLine($"Using WordPress category: {_config.CategoryName} (ID: {_categoryId})");

            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"WordPress authentication failed: {ex.Message}");
            return false;
        }
    }

    public async Task<int> EnsureCategoryExistsAsync(string categoryName)
    {
        try
        {
            var categories = await _client.Categories.GetAllAsync();
            var existingCategory = categories.FirstOrDefault(c =>
                string.Equals(c.Name, categoryName, StringComparison.OrdinalIgnoreCase));

            if (existingCategory != null)
            {
                return existingCategory.Id;
            }

            var newCategory = new Category
            {
                Name = categoryName,
                Description = $"Posts imported from Telegram via Telescribe on {DateTime.UtcNow:yyyy-MM-dd}",
                Slug = categoryName.ToLowerInvariant().Replace(" ", "-")
            };

            var createdCategory = await _client.Categories.CreateAsync(newCategory);
            Console.WriteLine($"Created new WordPress category: {categoryName} (ID: {createdCategory.Id})");
            return createdCategory.Id;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error ensuring category exists: {ex.Message}");
            throw;
        }
    }

    public async Task<Dictionary<string, string>> UploadMediaFilesAndGetUrlsAsync(List<string> mediaFiles, string mediaPath)
    {
        var mediaUrls = new Dictionary<string, string>();

        if (!_config.UploadMedia || !mediaFiles.Any())
        {
            return mediaUrls;
        }

        foreach (var mediaFile in mediaFiles)
        {
            try
            {
                var filePath = Path.Combine(mediaPath, mediaFile);
                if (!File.Exists(filePath))
                {
                    Console.WriteLine($"Warning: Media file not found: {filePath}");
                    continue;
                }

                var fileBytes = await File.ReadAllBytesAsync(filePath);
                var fileName = Path.GetFileName(mediaFile);

                using var stream = new MemoryStream(fileBytes);

                var uploadedMedia = await _client.Media.CreateAsync(stream, fileName);
                mediaUrls[mediaFile] = uploadedMedia.SourceUrl;

                Console.WriteLine($"Uploaded media: {fileName} (ID: {uploadedMedia.Id}) -> {uploadedMedia.SourceUrl}");

                await Task.Delay(500);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to upload media {mediaFile}: {ex.Message}");
            }
        }

        return mediaUrls;
    }

    public async Task<List<int>> UploadMediaFilesAsync(List<string> mediaFiles, string mediaPath)
    {
        var uploadedIds = new List<int>();

        if (!_config.UploadMedia || !mediaFiles.Any())
        {
            return uploadedIds;
        }

        foreach (var mediaFile in mediaFiles)
        {
            try
            {
                var filePath = Path.Combine(mediaPath, mediaFile);
                if (!File.Exists(filePath))
                {
                    Console.WriteLine($"Warning: Media file not found: {filePath}");
                    continue;
                }

                var fileBytes = await File.ReadAllBytesAsync(filePath);
                var fileName = Path.GetFileName(mediaFile);

                using var stream = new MemoryStream(fileBytes);

                var uploadedMedia = await _client.Media.CreateAsync(stream, fileName);
                uploadedIds.Add(uploadedMedia.Id);

                Console.WriteLine($"Uploaded media: {fileName} (ID: {uploadedMedia.Id})");

                await Task.Delay(500);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to upload media {mediaFile}: {ex.Message}");
            }
        }

        return uploadedIds;
    }

    public async Task<int> CreateOrUpdatePostAsync(TelegramPost telegramPost, string markdownContent, Dictionary<string, string>? mediaUrls = null)
    {
        try
        {
            var isUpdate = _postMappings.ContainsKey(telegramPost.Id);
            var existingMapping = isUpdate ? _postMappings[telegramPost.Id] : null;

            var htmlContent = ConvertMarkdownToHtml(markdownContent, mediaUrls);

            var title = GeneratePostTitle(telegramPost);

            var slug = GenerateSlug(title, telegramPost.Id);

            var tagIds = await GetOrCreateTagsAsync(telegramPost.Hashtags);

            if (isUpdate)
            {
                var existingPost = await _client.Posts.GetByIDAsync(existingMapping!.WordPressPostId);
                existingPost.Title.Raw = title;
                existingPost.Content.Raw = htmlContent;
                existingPost.Modified = DateTime.UtcNow;
                existingPost.Categories = new List<int> { _categoryId ?? 1 };
                existingPost.Tags = tagIds;

                var updatedPost = await _client.Posts.UpdateAsync(existingPost);

                existingMapping.UpdatedAt = DateTime.UtcNow;
                SavePostMappings();

                Console.WriteLine($"Updated WordPress post: {title} (ID: {updatedPost.Id}) with {tagIds.Count.ToString()} tags");
                return updatedPost.Id;
            }
            else
            {
                var newPost = new Post
                {
                    Title = new Title { Raw = title },
                    Content = new Content { Raw = htmlContent },
                    Slug = slug,
                    Status = _config.PostStatus.ToLowerInvariant() switch
                    {
                        "publish" => Status.Publish,
                        "draft" => Status.Draft,
                        "private" => Status.Private,
                        _ => Status.Publish
                    },
                    Categories = new List<int> { _categoryId ?? 1 },
                    Tags = tagIds,
                    Date = telegramPost.CreatedAt,
                    Modified = telegramPost.IsEdited ? telegramPost.EditedAt ?? telegramPost.CreatedAt : telegramPost.CreatedAt,
                    Format = _config.PostFormat
                };

                var createdPost = await _client.Posts.CreateAsync(newPost);

                _postMappings[telegramPost.Id] = new WordPressPostMapping
                {
                    TelegramPostId = telegramPost.Id,
                    WordPressPostId = createdPost.Id,
                    CreatedAt = DateTime.UtcNow,
                    WordPressSlug = slug
                };
                SavePostMappings();

                Console.WriteLine($"Created WordPress post: {title} (ID: {createdPost.Id}) with {tagIds.Count.ToString()} tags on {telegramPost.CreatedAt:yyyy-MM-dd}");
                return createdPost.Id;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to create/update WordPress post for Telegram post {telegramPost.Id}: {ex.Message}");
            throw;
        }
    }

    public async Task<WordPressUploadSummary> UploadPostsAsync(List<TelegramPost> posts, string exportsPath)
    {
        var summary = new WordPressUploadSummary();
        var mediaPath = Path.Combine(exportsPath, "media");

        Console.WriteLine($"Starting WordPress upload of {posts.Count} posts...");
        Console.WriteLine($"Media path: {mediaPath}");
        Console.WriteLine($"Target category: {_config.CategoryName}");

        foreach (var post in posts)
        {
            try
            {
                var markdownFilePath = Path.Combine(exportsPath, $"{post.Id}.md");
                if (!File.Exists(markdownFilePath))
                {
                    Console.WriteLine($"Warning: Markdown file not found for post {post.Id}");
                    summary.FailedPosts++;
                    continue;
                }

                var markdownContent = await File.ReadAllTextAsync(markdownFilePath);

                var mediaUrls = await UploadMediaFilesAndGetUrlsAsync(post.MediaFiles, mediaPath);
                summary.MediaFilesUploaded += mediaUrls.Count;

                var isUpdate = _postMappings.ContainsKey(post.Id);
                await CreateOrUpdatePostAsync(post, markdownContent, mediaUrls);

                if (isUpdate)
                {
                    summary.UpdatedPosts++;
                }
                else
                {
                    summary.NewPosts++;
                }

                summary.TotalPosts++;

                await Task.Delay(1000);
            }
            catch (Exception ex)
            {
                summary.FailedPosts++;
                summary.Errors.Add($"Post {post.Id}: {ex.Message}");
                Console.WriteLine($"Failed to upload post {post.Id}: {ex.Message}");
            }
        }

        return summary;
    }

    private string GeneratePostTitle(TelegramPost post)
    {
        if (!string.IsNullOrWhiteSpace(post.Content))
        {
            var lines = post.Content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var firstLine = lines.FirstOrDefault()?.Trim();

            if (!string.IsNullOrWhiteSpace(firstLine))
            {
                var title = firstLine.Replace("#", "").Replace("*", "").Replace("_", "").Trim();
                if (title.Length > 60)
                {
                    title = title[..57] + "...";
                }
                return title;
            }
        }

        return $"Telegram Post {post.Id} - {post.CreatedAt:yyyy-MM-dd}";
    }

    private string GenerateSlug(string title, long postId)
    {
        var slug = title.ToLowerInvariant()
            .Replace(" ", "-")
            .Replace("'", "")
            .Replace("\"", "")
            .Replace(".", "")
            .Replace(",", "")
            .Replace("!", "")
            .Replace("?", "")
            .Replace(":", "")
            .Replace(";", "");

        slug = string.Join("", slug.Where(c => char.IsLetterOrDigit(c) || c == '-'));

        if (slug.Length > 40)
        {
            slug = slug[..40];
        }

        return $"{slug}-tg-{postId}";
    }

    private string ConvertMarkdownToHtml(string markdownContent, Dictionary<string, string>? mediaUrls = null)
    {
        var html = markdownContent;

        html = System.Text.RegularExpressions.Regex.Replace(html, @"^# (.+)$", "<h1>$1</h1>", System.Text.RegularExpressions.RegexOptions.Multiline);
        html = System.Text.RegularExpressions.Regex.Replace(html, @"^## (.+)$", "<h2>$1</h2>", System.Text.RegularExpressions.RegexOptions.Multiline);
        html = System.Text.RegularExpressions.Regex.Replace(html, @"^### (.+)$", "<h3>$1</h3>", System.Text.RegularExpressions.RegexOptions.Multiline);

        html = System.Text.RegularExpressions.Regex.Replace(html, @"\*\*(.+?)\*\*", "<strong>$1</strong>");
        html = System.Text.RegularExpressions.Regex.Replace(html, @"\*(.+?)\*", "<em>$1</em>");

        if (mediaUrls != null && mediaUrls.Any())
        {
            html = System.Text.RegularExpressions.Regex.Replace(html, @"!\[([^\]]*)\]\(([^)]+)\)", match =>
            {
                var altText = match.Groups[1].Value;
                var imagePath = match.Groups[2].Value;

                var fileName = Path.GetFileName(imagePath);

                if (mediaUrls.ContainsKey(fileName))
                {
                    return $"<img src=\"{mediaUrls[fileName]}\" alt=\"{altText}\" />";
                }

                return $"<img src=\"{imagePath}\" alt=\"{altText}\" />";
            });
        }
        else
        {
            html = System.Text.RegularExpressions.Regex.Replace(html, @"!\[([^\]]*)\]\(([^)]+)\)", "<img src=\"$2\" alt=\"$1\" />");
        }

        html = System.Text.RegularExpressions.Regex.Replace(html, @"\[([^\]]+)\]\(([^)]+)\)", "<a href=\"$2\">$1</a>");

        html = html.Replace("\n", "<br>\n");

        return html;
    }

    private Dictionary<long, WordPressPostMapping> LoadPostMappings()
    {
        try
        {
            if (File.Exists(_mappingFilePath))
            {
                var json = File.ReadAllText(_mappingFilePath);
                var mappings = JsonSerializer.Deserialize<Dictionary<long, WordPressPostMapping>>(json);
                return mappings ?? new Dictionary<long, WordPressPostMapping>();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: Could not load WordPress mappings: {ex.Message}");
        }

        return new Dictionary<long, WordPressPostMapping>();
    }

    private async Task<List<int>> GetOrCreateTagsAsync(List<string>? hashtags)
    {
        var tagIds = new List<int>();

        if (hashtags == null || !hashtags.Any())
            return tagIds;

        try
        {
            var existingTags = await _client.Tags.GetAllAsync();

            foreach (var hashtag in hashtags)
            {
                if (string.IsNullOrWhiteSpace(hashtag))
                    continue;

                var cleanTag = hashtag.TrimStart('#').Trim();
                if (string.IsNullOrWhiteSpace(cleanTag))
                    continue;

                var existingTag = existingTags.FirstOrDefault(t =>
                    string.Equals(t.Name, cleanTag, StringComparison.OrdinalIgnoreCase));

                if (existingTag != null)
                {
                    tagIds.Add(existingTag.Id);
                    Console.WriteLine($"Using existing tag: {cleanTag} (ID: {existingTag.Id})");
                }
                else
                {
                    try
                    {
                        var newTag = new Tag
                        {
                            Name = cleanTag,
                            Slug = cleanTag.ToLowerInvariant().Replace(" ", "-")
                        };

                        var createdTag = await _client.Tags.CreateAsync(newTag);
                        tagIds.Add(createdTag.Id);
                        Console.WriteLine($"Created new tag: {cleanTag} (ID: {createdTag.Id})");
                    }
                    catch (Exception tagEx)
                    {
                        Console.WriteLine($"Warning: Could not create tag '{cleanTag}': {tagEx.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: Error processing tags: {ex.Message}");
        }

        return tagIds;
    }

    private void SavePostMappings()
    {
        try
        {
            var json = JsonSerializer.Serialize(_postMappings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_mappingFilePath, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: Could not save WordPress mappings: {ex.Message}");
        }
    }

    public void Dispose()
    {
        SavePostMappings();
    }
}