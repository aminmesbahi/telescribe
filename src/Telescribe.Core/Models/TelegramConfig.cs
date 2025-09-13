namespace Telescribe.Core.Models;

public class TelegramConfig
{
    public string PhoneNumber { get; set; } = string.Empty;
    public string ChannelId { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public int ApiId { get; set; }
    public string ApiHash { get; set; } = string.Empty;
    public int SummaryCharacterCount { get; set; } = 200;
    public WordPressConfig WordPress { get; set; } = new();
    public LlmConfig Llm { get; set; } = new();
    public StaticSiteConfig StaticSite { get; set; } = new();
}