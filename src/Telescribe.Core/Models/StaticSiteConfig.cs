namespace Telescribe.Core.Models;

public class StaticSiteConfig
{
    public string TemplateName { get; set; } = "default";
    public string SiteTitle { get; set; } = "Telescribe";
    public string Subtitle { get; set; } = "Telegram Channel Archive";
    public string HeaderIcon { get; set; } = "📱";
    public string Description { get; set; } = "Archive of Telegram channel posts";
    public bool OpenBrowserAfterGeneration { get; set; } = true;
    public int MaxPostsInIndex { get; set; } = 50;
    /// <summary>Base URL used in sitemap.xml (e.g. https://example.com). Leave empty to skip absolute URLs.</summary>
    public string SiteBaseUrl { get; set; } = "";
    /// <summary>When true, posts with no text content (polls, media-only) are excluded from the generated site.</summary>
    public bool SkipEmptyContentPosts { get; set; } = false;
}