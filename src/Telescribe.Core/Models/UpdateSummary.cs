namespace Telescribe.Core.Models;

public class UpdateSummary
{
    public int NewPostsCount { get; set; }
    public DateTime UpdateTime { get; set; }
    public DateTime DateRangeFrom { get; set; }
    public DateTime DateRangeTo { get; set; }
    public string ExportPath { get; set; } = "./exports";
    public int MediaFilesCount { get; set; }
    public List<PostSummary> NewPosts { get; set; } = [];
    public string Status { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }
}