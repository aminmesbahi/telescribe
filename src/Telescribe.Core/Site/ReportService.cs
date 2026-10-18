using System.Globalization;
using System.Text.Json;
using Telescribe.Core.Models;
using Telescribe.Core.Services;
using Telescribe.Core.Templating;

namespace Telescribe.Core.Site;

/// <summary>Renders the HTML analytics report from the export summary.</summary>
public sealed class ReportService
{
    private static readonly JsonSerializerOptions SummaryJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly SiteGeneratorService _templates;

    public ReportService(string reportTemplatesPath)
    {
        _templates = new SiteGeneratorService(reportTemplatesPath);
    }

    public static async Task<ExportSummary?> LoadSummaryAsync(string exportDirectory, CancellationToken cancellationToken = default)
    {
        var summaryPath = Path.Combine(exportDirectory, "summary.json");
        if (!File.Exists(summaryPath))
        {
            return null;
        }

        await using var stream = File.OpenRead(summaryPath);
        return await JsonSerializer.DeserializeAsync<ExportSummary>(stream, SummaryJson, cancellationToken);
    }

    public string Render(ExportSummary summary, DateTime generatedAtUtc)
    {
        var posts = summary.Posts;

        var stats = _templates.RenderTemplate("stats-cards.html", new Dictionary<string, TemplateValue>
        {
            ["totalPosts"] = Number(posts.Count),
            ["totalViews"] = Number(posts.Sum(p => (long)p.Views)),
            ["totalReactions"] = Number(posts.Sum(p => (long)p.Reactions)),
            ["totalForwards"] = Number(posts.Sum(p => (long)p.TotalForwards))
        });

        return _templates.RenderTemplate("analytics.html", new Dictionary<string, TemplateValue>
        {
            ["statsCards"] = TemplateValue.Raw(stats),
            ["popularPosts"] = TemplateValue.Raw(Rows("popular-post-row.html", posts.OrderByDescending(p => (long)p.Views + p.Reactions + p.TotalForwards).Take(20))),
            ["recentPosts"] = TemplateValue.Raw(Rows("recent-post-row.html", posts.OrderByDescending(p => p.CreatedAt).Take(10))),
            ["reactedPosts"] = TemplateValue.Raw(Rows("reacted-post-row.html", posts.OrderByDescending(p => p.Reactions).Take(10))),
            ["forwardedPosts"] = TemplateValue.Raw(Rows("forwarded-post-row.html", posts.OrderByDescending(p => p.TotalForwards).Take(10))),
            ["generatedDate"] = generatedAtUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC"
        });
    }

    public async Task<string> GenerateAsync(string exportDirectory, CancellationToken cancellationToken = default)
    {
        var summary = await LoadSummaryAsync(exportDirectory, cancellationToken)
            ?? throw new FileNotFoundException("Export summary not found. Run an export first.", Path.Combine(exportDirectory, "summary.json"));

        var reportsDirectory = Path.Combine(exportDirectory, "reports");
        Directory.CreateDirectory(reportsDirectory);

        var reportPath = Path.Combine(reportsDirectory, "analytics_report.html");
        await File.WriteAllTextAsync(reportPath, Render(summary, DateTime.UtcNow), cancellationToken);
        return reportPath;
    }

    private string Rows(string template, IEnumerable<PostSummary> posts)
    {
        return string.Concat(posts.Select(p => _templates.RenderTemplate(template, new Dictionary<string, TemplateValue>
        {
            ["postId"] = p.PostId.ToString(CultureInfo.InvariantCulture),
            ["date"] = p.CreatedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["views"] = Number(p.Views),
            ["reactions"] = Number(p.Reactions),
            ["forwards"] = Number(p.TotalForwards),
            ["contentPreview"] = string.IsNullOrWhiteSpace(p.ContentPreview) ? "No preview available" : p.ContentPreview
        })));
    }

    private static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);
}
