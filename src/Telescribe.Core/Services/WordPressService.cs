using Telescribe.Core.Export;
using Telescribe.Core.Models;
using WordPressPCL;
using WordPressPCL.Models;

namespace Telescribe.Core.Services;

public class WordPressService : IWordPressService
{
    private readonly WordPressConfig _config;
    private readonly WordPressClient _client;
    private readonly string _exportDirectory;
    private readonly string _statePath;
    private readonly WordPressState _state;
    private int? _categoryId;
    private Dictionary<string, int>? _tagIds;

    public WordPressService(WordPressConfig config, string exportDirectory = "./exports")
    {
        _config = config;
        _exportDirectory = exportDirectory;
        _client = new WordPressClient(_config.SiteUrl);

        _statePath = WordPressState.DefaultPath(exportDirectory);
        _state = WordPressState.Load(
            _statePath,
            Path.Combine(exportDirectory, "wordpress_mappings.json"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wordpress_mappings.json"));
    }

    public async Task<bool> AuthenticateAsync()
    {
        try
        {
            Console.WriteLine("Authenticating with WordPress...");

            _client.Auth.UseBasicAuth(_config.Username, _config.ApplicationPassword);

            var users = await _client.Users.GetAllAsync();
            var currentUser = users.FirstOrDefault();
            Console.WriteLine(currentUser != null
                ? $"WordPress authentication successful! Connected to site with user: {currentUser.Name}"
                : "WordPress authentication successful!");

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
        var categories = await _client.Categories.GetAllAsync();
        var existing = categories.FirstOrDefault(c => string.Equals(c.Name, categoryName, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            return existing.Id;
        }

        var created = await _client.Categories.CreateAsync(new Category
        {
            Name = categoryName,
            Description = $"Posts imported from Telegram via Telescribe on {DateTime.UtcNow:yyyy-MM-dd}",
            Slug = categoryName.ToLowerInvariant().Replace(' ', '-')
        });

        Console.WriteLine($"Created new WordPress category: {categoryName} (ID: {created.Id})");
        return created.Id;
    }

    public async Task<WordPressUploadSummary> SyncAsync(IReadOnlyList<ExportedPost> posts, bool force = false, CancellationToken cancellationToken = default)
    {
        if (_categoryId is null)
        {
            throw new InvalidOperationException("Call AuthenticateAsync() before syncing.");
        }

        var summary = new WordPressUploadSummary();
        var plan = WordPressSyncPlanner.Plan(posts, _state, force);
        summary.TotalPosts = plan.Count;
        summary.SkippedPosts = plan.Count(p => p.Action == WordPressAction.Skip);

        var work = plan.Where(p => p.Action != WordPressAction.Skip).ToList();
        Console.WriteLine($"WordPress sync: {work.Count(p => p.Action == WordPressAction.Create)} to create, " +
                          $"{work.Count(p => p.Action == WordPressAction.Update)} to update, {summary.SkippedPosts} unchanged.");

        foreach (var item in work)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var uploaded = await UploadMediaAsync(item.Post, summary, cancellationToken);
                var html = WordPressContent.ToHtml(item.Post, uploaded);

                if (item.Action == WordPressAction.Create)
                {
                    await CreatePostAsync(item.Post, html);
                    summary.NewPosts++;
                }
                else
                {
                    await UpdatePostAsync(item.Post, html);
                    summary.UpdatedPosts++;
                }

                // Save after every post so an interrupted run never loses track of what it already published.
                _state.Save(_statePath);
                await Task.Delay(500, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                summary.FailedPosts++;
                summary.Errors.Add($"Post {item.Post.Id}: {ex.Message}");
                Console.WriteLine($"Failed to publish post {item.Post.Id}: {ex.Message}");
            }
        }

        return summary;
    }

    private async Task CreatePostAsync(ExportedPost post, string html)
    {
        var title = WordPressContent.Title(post);
        var slug = WordPressContent.Slug(title, post.Id);

        var created = await _client.Posts.CreateAsync(new Post
        {
            Title = new Title { Raw = title },
            Content = new Content { Raw = html },
            Slug = slug,
            Status = ParseStatus(_config.PostStatus),
            Categories = [_categoryId!.Value],
            Tags = await GetOrCreateTagIdsAsync(post.Tags),
            Date = post.CreatedAt,
            DateGmt = post.CreatedAt,
            Format = _config.PostFormat
        });

        _state.Posts[post.Id] = new WordPressPostMapping
        {
            TelegramPostId = post.Id,
            WordPressPostId = created.Id,
            CreatedAt = DateTime.UtcNow,
            WordPressSlug = slug,
            MediaIds = MediaIdsFor(post),
            ContentHash = post.ContentHash
        };

        Console.WriteLine($"Created WordPress post: {title} (ID: {created.Id}) from {post.CreatedAt:yyyy-MM-dd}");
    }

    private async Task UpdatePostAsync(ExportedPost post, string html)
    {
        var mapping = _state.Posts[post.Id];
        var title = WordPressContent.Title(post);

        var existing = await _client.Posts.GetByIdAsync(mapping.WordPressPostId);
        existing.Title ??= new Title();
        existing.Content ??= new Content();
        existing.Title.Raw = title;
        existing.Content.Raw = html;
        existing.Categories = [_categoryId!.Value];
        existing.Tags = await GetOrCreateTagIdsAsync(post.Tags);

        await _client.Posts.UpdateAsync(existing);

        mapping.UpdatedAt = DateTime.UtcNow;
        mapping.MediaIds = MediaIdsFor(post);
        mapping.ContentHash = post.ContentHash;

        Console.WriteLine($"Updated WordPress post: {title} (ID: {mapping.WordPressPostId})");
    }

    /// <summary>Uploads the post's media that is not in the WordPress library yet; returns file name to URL.</summary>
    private async Task<Dictionary<string, string>> UploadMediaAsync(ExportedPost post, WordPressUploadSummary summary, CancellationToken cancellationToken)
    {
        var urls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!_config.UploadMedia)
        {
            return urls;
        }

        var mediaDirectory = Path.Combine(_exportDirectory, "media");
        foreach (var file in post.MediaFiles)
        {
            if (_state.Media.TryGetValue(file, out var known))
            {
                urls[file] = known.Url;
                continue;
            }

            var path = Path.Combine(mediaDirectory, file);
            if (!File.Exists(path))
            {
                Console.WriteLine($"Warning: media file not found: {path}");
                continue;
            }

            await using var stream = File.OpenRead(path);
            var media = await _client.Media.CreateAsync(stream, file);

            _state.Media[file] = new WordPressMediaItem { Id = media.Id, Url = media.SourceUrl ?? string.Empty };
            urls[file] = media.SourceUrl ?? string.Empty;
            summary.MediaFilesUploaded++;
            Console.WriteLine($"Uploaded media: {file} (ID: {media.Id})");

            _state.Save(_statePath);
            await Task.Delay(500, cancellationToken);
        }

        return urls;
    }

    private List<int> MediaIdsFor(ExportedPost post) =>
        post.MediaFiles.Where(_state.Media.ContainsKey).Select(f => _state.Media[f].Id).ToList();

    /// <summary>Looks tags up in one request (cached for the run) and creates the ones that are missing.</summary>
    private async Task<List<int>> GetOrCreateTagIdsAsync(IReadOnlyList<string> tags)
    {
        var ids = new List<int>();
        if (tags.Count == 0)
        {
            return ids;
        }

        if (_tagIds is null)
        {
            _tagIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var tag in await _client.Tags.GetAllAsync())
            {
                _tagIds[tag.Name ?? string.Empty] = tag.Id;
            }
        }

        foreach (var name in tags.Select(t => t.TrimStart('#').Trim()).Where(t => t.Length > 0))
        {
            if (!_tagIds.TryGetValue(name, out var id))
            {
                var created = await _client.Tags.CreateAsync(new Tag { Name = name, Slug = name.ToLowerInvariant().Replace(' ', '-') });
                id = created.Id;
                _tagIds[name] = id;
            }

            ids.Add(id);
        }

        return ids;
    }

    private static Status ParseStatus(string status) => status.ToLowerInvariant() switch
    {
        "draft" => Status.Draft,
        "private" => Status.Private,
        _ => Status.Publish
    };

    public void Dispose()
    {
        _state.Save(_statePath);
        GC.SuppressFinalize(this);
    }
}
