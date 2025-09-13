namespace Telescribe.Core.Models;

public class WordPressConfig
{
    public string SiteUrl { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string ApplicationPassword { get; set; } = string.Empty;
    public string CategoryName { get; set; } = "Telegram Posts";
    public bool EnableUpload { get; set; } = false;
    public string PostStatus { get; set; } = "publish"; // publish, draft, private
    public string PostFormat { get; set; } = "standard"; // standard, aside, gallery, link, image, quote, status, video, audio, chat
    public bool UploadMedia { get; set; } = true;
    public string AuthorName { get; set; } = "Telescribe";
}