namespace Telescribe.Core.Models;

public class ExportSummary
{
    public int TotalPosts { get; set; }
    public DateTime ExportTime { get; set; }
    public string ExportPath { get; set; } = "./exports";
    public string SummaryFilePath { get; set; } = string.Empty;
    public int MediaFilesCount { get; set; }
    public List<PostSummary> Posts { get; set; } = [];
    public LlmProcessingSummary? LlmSummary { get; set; }
}

public class PostSummary
{
    public long PostId { get; set; }
    public DateTime CreatedAt { get; set; }
    public bool IsEdited { get; set; }
    public DateTime? EditedAt { get; set; }
    public int Views { get; set; }
    public int Reactions { get; set; }
    public int TotalForwards { get; set; }
    public int PublicForwards { get; set; }
    public int PrivateForwards { get; set; }
    public string ContentPreview { get; set; } = string.Empty;
}