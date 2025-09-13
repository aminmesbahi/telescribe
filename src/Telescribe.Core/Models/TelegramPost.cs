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
    public List<string> MediaFiles { get; set; } = [];

    // LLM-generated fields
    public string GeneratedTitle { get; set; } = string.Empty;

    public List<string> Hashtags { get; set; } = [];
    public bool IsLlmProcessed { get; set; } = false;
    public string LlmProvider { get; set; } = string.Empty;
}