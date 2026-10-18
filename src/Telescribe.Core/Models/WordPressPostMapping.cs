namespace Telescribe.Core.Models;

public class WordPressPostMapping
{
    public long TelegramPostId { get; set; }
    public int WordPressPostId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public string WordPressSlug { get; set; } = string.Empty;
    public List<int> MediaIds { get; set; } = [];

    /// <summary>Hash of the post content that was last published (see <c>ExportedPost.ContentHash</c>).</summary>
    public string ContentHash { get; set; } = string.Empty;
}

/// <summary>A file already uploaded to the WordPress media library.</summary>
public class WordPressMediaItem
{
    public int Id { get; set; }
    public string Url { get; set; } = string.Empty;
}
