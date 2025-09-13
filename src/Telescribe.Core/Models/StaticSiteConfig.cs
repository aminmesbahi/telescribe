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
}