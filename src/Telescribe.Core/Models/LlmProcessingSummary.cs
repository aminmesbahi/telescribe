namespace Telescribe.Core.Models;

public class LlmProcessingSummary
{
    public int TotalPosts { get; set; }
    public int ProcessedPosts { get; set; }
    public int TitlesGenerated { get; set; }
    public int HashtagsExtracted { get; set; }
    public int FailedPosts { get; set; }
    public string Provider { get; set; } = string.Empty;
    public List<string> Errors { get; set; } = [];
    public DateTime ProcessingStarted { get; set; }
    public DateTime ProcessingCompleted { get; set; }
    public TimeSpan ProcessingDuration => ProcessingCompleted - ProcessingStarted;
}