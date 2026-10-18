namespace Telescribe.Core.Models;

public class TelegramConfig
{
    public string PhoneNumber { get; set; } = string.Empty;
    public string ChannelId { get; set; } = string.Empty;
    public int ApiId { get; set; }
    public string ApiHash { get; set; } = string.Empty;
    public int SummaryCharacterCount { get; set; } = 200;

    /// <summary>
    /// Where the Telegram login session is stored. Empty uses <c>WTelegram.session</c> in the working directory.
    /// Keep it private: anyone with this file can act as your Telegram account.
    /// </summary>
    public string SessionPath { get; set; } = string.Empty;

    /// <summary>
    /// Documents larger than this many megabytes are not downloaded (their posts still export). 0 means no limit.
    /// </summary>
    public int MaxMediaSizeMB { get; set; } = 0;

    /// <summary>
    /// When updating, already-exported posts newer than this many days (counted back from the last export) are
    /// re-fetched so views and reactions stay current. Older posts are left untouched.
    /// </summary>
    public int RefreshWindowDays { get; set; } = 30;

    public WordPressConfig WordPress { get; set; } = new();
    public LlmConfig Llm { get; set; } = new();
    public StaticSiteConfig StaticSite { get; set; } = new();
}
