using Telescribe.Core.Export;

namespace Telescribe.Core.Services;

public interface IWordPressService : IDisposable
{
    Task<bool> AuthenticateAsync();

    Task<int> EnsureCategoryExistsAsync(string categoryName);

    /// <summary>Publishes new posts and updates changed ones. Posts whose content is unchanged are skipped.</summary>
    Task<WordPressUploadSummary> SyncAsync(IReadOnlyList<ExportedPost> posts, bool force = false, CancellationToken cancellationToken = default);
}

public class WordPressUploadSummary
{
    public int TotalPosts { get; set; }
    public int NewPosts { get; set; }
    public int UpdatedPosts { get; set; }
    public int SkippedPosts { get; set; }
    public int FailedPosts { get; set; }
    public int MediaFilesUploaded { get; set; }
    public DateTime UploadTime { get; set; } = DateTime.UtcNow;
    public List<string> Errors { get; set; } = [];
}
