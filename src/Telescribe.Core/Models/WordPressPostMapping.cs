namespace Telescribe.Core.Models;

public class WordPressPostMapping
{
    public long TelegramPostId { get; set; }
    public int WordPressPostId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public string WordPressSlug { get; set; } = string.Empty;
    public List<int> MediaIds { get; set; } = [];
}