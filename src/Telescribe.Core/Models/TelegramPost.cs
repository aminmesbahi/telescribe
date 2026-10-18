namespace Telescribe.Core.Models;

public class TelegramPost
{
    public long Id { get; set; }
    public DateTime CreatedAt { get; set; }
    public bool IsEdited { get; set; }
    public DateTime? EditedAt { get; set; }
    public int Views { get; set; }
    public int Reactions { get; set; }
    public int TotalForwards { get; set; }
    public int PublicForwards { get; set; }
    public int PrivateForwards { get; set; }
    public string Content { get; set; } = string.Empty;
    public string Channel { get; set; } = string.Empty;

    /// <summary>Name of the original author or channel when this post was forwarded from elsewhere.</summary>
    public string ForwardedFrom { get; set; } = string.Empty;

    /// <summary>Id of the channel post this one replies to, if any.</summary>
    public long? ReplyToMessageId { get; set; }

    /// <summary>Reactions by emoji (custom emoji and paid stars are grouped under their own labels).</summary>
    public Dictionary<string, int> ReactionBreakdown { get; set; } = [];
    public List<string> MediaFiles { get; set; } = [];

    /// <summary>
    /// Ids of every Telegram message folded into this post. More than one entry means the post is an album
    /// (several media messages sharing a grouped id); <see cref="Id"/> is the oldest of them.
    /// </summary>
    public List<long> MergedMessageIds { get; set; } = [];

    // Post type: "text", "photo", "document", "article", ...
    public string PostType { get; set; } = "text";

    // Telegram article metadata (native "article" posts)
    public string ArticleTitle { get; set; } = string.Empty;
    public string ArticleAuthor { get; set; } = string.Empty;
    public string ArticleUrl { get; set; } = string.Empty;
    public string SiteName { get; set; } = string.Empty;
    // True when the cached Instant View page was delivered in parts (Telegram flag: Page.Flags.part)
    public bool IsPartialArticle { get; set; } = false;

    // LLM-generated fields
    public string GeneratedTitle { get; set; } = string.Empty;

    public List<string> Hashtags { get; set; } = [];
    public bool IsLlmProcessed { get; set; } = false;
    public string LlmProvider { get; set; } = string.Empty;
}
