namespace Telescribe.Core.Models;

public class StaticSiteConfig
{
    public string TemplateName { get; set; } = "en";
    public string SiteTitle { get; set; } = "Telescribe";
    public string Subtitle { get; set; } = "Telegram Channel Archive";
    public string HeaderIcon { get; set; } = "📱";
    public string Description { get; set; } = "Archive of Telegram channel posts";
    /// <summary>Absolute URL of the default image used for Open Graph and Twitter previews.</summary>
    public string SocialImageUrl { get; set; } = "";
    public int SocialImageWidth { get; set; }
    public int SocialImageHeight { get; set; }
    /// <summary>Twitter/X handle, including the leading @, used for Twitter Card metadata.</summary>
    public string TwitterHandle { get; set; } = "";
    /// <summary>Open Graph locale such as en_US or fa_IR. Empty uses the template's locale.</summary>
    public string OpenGraphLocale { get; set; } = "";
    public bool OpenBrowserAfterGeneration { get; set; } = true;
    /// <summary>
    /// How many of the newest posts are listed on the index page. Every post still gets its own page and
    /// sitemap entry. Use 0 to list all posts.
    /// </summary>
    public int MaxPostsInIndex { get; set; } = 50;
    /// <summary>Base URL used in sitemap.xml (e.g. https://example.com). Leave empty to skip absolute URLs.</summary>
    public string SiteBaseUrl { get; set; } = "";
    /// <summary>When true, posts with no text content (polls, media-only) are excluded from the generated site.</summary>
    public bool SkipEmptyContentPosts { get; set; } = false;

    /// <summary>Folder the site is written to. Empty uses <c>site</c> inside the exports folder.</summary>
    public string OutputDirectory { get; set; } = "";
    /// <summary>Create a page per tag (hashtags in posts plus AI-generated tags) and link them from posts.</summary>
    public bool GenerateTagPages { get; set; } = true;
    /// <summary>A tag only gets its own page when at least this many posts use it.</summary>
    public int MinPostsPerTag { get; set; } = 2;
    /// <summary>Number of newest posts in feed.xml (needs SiteBaseUrl). 0 disables the feed.</summary>
    public int FeedItemCount { get; set; } = 30;

    /// <summary>Author name used in structured data (JSON-LD). Falls back to <see cref="SiteTitle"/> when empty.</summary>
    public string AuthorName { get; set; } = "";
    /// <summary>Optional author profile URL used in structured data.</summary>
    public string AuthorUrl { get; set; } = "";
    /// <summary>Publisher name used in structured data. Falls back to <see cref="SiteTitle"/> when empty.</summary>
    public string PublisherName { get; set; } = "";
    /// <summary>Optional profile URLs (X, LinkedIn, ...) listed as <c>sameAs</c> in structured data.</summary>
    public List<string> SameAs { get; set; } = [];
}
