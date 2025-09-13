using Telescribe.Core.Models;

namespace Telescribe.Core.Services;

public interface IWordPressService : IDisposable
{
    Task<bool> AuthenticateAsync();

    Task<int> CreateOrUpdatePostAsync(TelegramPost telegramPost, string markdownContent, Dictionary<string, string>? mediaUrls = null);

    Task<int> EnsureCategoryExistsAsync(string categoryName);

    Task<List<int>> UploadMediaFilesAsync(List<string> mediaFiles, string mediaPath);

    Task<Dictionary<string, string>> UploadMediaFilesAndGetUrlsAsync(List<string> mediaFiles, string mediaPath);

    Task<WordPressUploadSummary> UploadPostsAsync(List<TelegramPost> posts, string exportsPath);
}

public class WordPressUploadSummary
{
    public int TotalPosts { get; set; }
    public int NewPosts { get; set; }
    public int UpdatedPosts { get; set; }
    public int FailedPosts { get; set; }
    public int MediaFilesUploaded { get; set; }
    public DateTime UploadTime { get; set; } = DateTime.UtcNow;
    public List<string> Errors { get; set; } = [];
}