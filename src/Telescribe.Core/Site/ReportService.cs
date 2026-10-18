using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Telescribe.Core.Export;
using Telescribe.Core.Models;
using Telescribe.Core.Services;
using Telescribe.Core.Templating;

namespace Telescribe.Core.Site;

/// <summary>Numbers behind the report's charts. Pure functions of the export summary and stats history.</summary>
public static class ReportAnalytics
{
    /// <summary>Posts per calendar month (UTC), oldest first, limited to the latest <paramref name="months"/> active months.</summary>
    public static List<(string Label, double Value)> PostsPerMonth(IEnumerable<PostSummary> posts, int months = 24)
    {
        return posts
            .Where(p => p.CreatedAt != default)
            .GroupBy(p => new DateTime(p.CreatedAt.Year, p.CreatedAt.Month, 1))
            .OrderBy(g => g.Key)
            .TakeLast(months)
            .Select(g => (g.Key.ToString("yyyy-MM", CultureInfo.InvariantCulture), (double)g.Count()))
            .ToList();
    }

    /// <summary>Posts by weekday (Monday = 0) and hour of day, in UTC.</summary>
    public static int[,] PostingHeatmap(IEnumerable<PostSummary> posts)
    {
        var grid = new int[7, 24];
        foreach (var post in posts.Where(p => p.CreatedAt != default))
        {
            var day = ((int)post.CreatedAt.DayOfWeek + 6) % 7;
            grid[day, post.CreatedAt.Hour]++;
        }

        return grid;
    }

    /// <summary>
    /// Total views across all posts at each time a snapshot was taken, using each post's latest known value so
    /// far. Needs at least two snapshot times to say anything about growth.
    /// </summary>
    public static List<(DateTime Time, double Views)> ViewsOverTime(IEnumerable<StatsSnapshot> history)
    {
        var latest = new Dictionary<long, int>();
        var points = new List<(DateTime, double)>();

        foreach (var group in history.OrderBy(s => s.Time).GroupBy(s => s.Time))
        {
            foreach (var snapshot in group)
            {
                latest[snapshot.PostId] = snapshot.Views;
            }

            points.Add((group.Key, latest.Values.Sum(v => (double)v)));
        }

        return points;
    }

    /// <summary>Reaction totals by emoji, most used first.</summary>
    public static List<(string Label, double Value)> ReactionTotals(IEnumerable<PostSummary> posts, int top = 10)
    {
        return posts
            .SelectMany(p => p.ReactionBreakdown)
            .GroupBy(kv => kv.Key)
            .Select(g => (Label: g.Key, Value: (double)g.Sum(kv => kv.Value)))
            .OrderByDescending(r => r.Value)
            .Take(top)
            .ToList();
    }

    /// <summary>Posts ranked by (reactions + forwards) / views, ignoring posts too small to say anything.</summary>
    public static List<(PostSummary Post, double Rate)> Engagement(IEnumerable<PostSummary> posts, int minViews = 100, int top = 10)
    {
        return posts
            .Where(p => p.Views >= minViews)
            .Select(p => (Post: p, Rate: (p.Reactions + p.TotalForwards) * 100.0 / p.Views))
            .OrderByDescending(x => x.Rate)
            .Take(top)
            .ToList();
    }
}

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

    public string Render(ExportSummary summary, DateTime generatedAtUtc, IReadOnlyList<StatsSnapshot>? history = null)
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
            ["charts"] = TemplateValue.Raw(BuildCharts(posts, history ?? [])),
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

        var history = StatsHistory.Load(StatsHistory.DefaultPath(exportDirectory));
        var reportPath = Path.Combine(reportsDirectory, "analytics_report.html");
        await File.WriteAllTextAsync(reportPath, Render(summary, DateTime.UtcNow, history), cancellationToken);
        return reportPath;
    }

    private static string BuildCharts(IReadOnlyList<PostSummary> posts, IReadOnlyList<StatsSnapshot> history)
    {
        var html = new StringBuilder();

        Section(html, "📅 Posts per Month", Charts.Bars(ReportAnalytics.PostsPerMonth(posts), "posts"));

        var growth = ReportAnalytics.ViewsOverTime(history);
        Section(html, "📈 Total Views Over Time",
            growth.Count >= 2
                ? Charts.Line(growth.Select(p => (p.Time, p.Views)).ToList(), "views")
                : "<p style=\"color:#7f8c8d\">Growth appears here once Telescribe has recorded at least two updates (each update stores a snapshot).</p>");

        Section(html, "🕒 When You Post (UTC)", Charts.Heatmap(ReportAnalytics.PostingHeatmap(posts), "posts"));

        var reactions = ReportAnalytics.ReactionTotals(posts);
        if (reactions.Count > 0)
        {
            Section(html, "😀 Reactions by Emoji", Charts.HorizontalBars(reactions));
        }

        var engagement = ReportAnalytics.Engagement(posts);
        if (engagement.Count > 0)
        {
            var table = new StringBuilder("<table><tr><th>Post ID</th><th>Engagement</th><th>Views</th><th>Reactions</th><th>Forwards</th><th>Content Preview</th></tr>");
            foreach (var (post, rate) in engagement)
            {
                table.Append("<tr><td class='metric'>").Append(post.PostId.ToString(CultureInfo.InvariantCulture)).Append("</td>")
                     .Append("<td class='metric'>").Append(rate.ToString("0.0", CultureInfo.InvariantCulture)).Append("%</td>")
                     .Append("<td>").Append(Number(post.Views)).Append("</td><td>").Append(Number(post.Reactions)).Append("</td><td>").Append(Number(post.TotalForwards)).Append("</td>")
                     .Append("<td class='post-preview'>").Append(WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(post.ContentPreview) ? "No preview available" : post.ContentPreview)).Append("</td></tr>");
            }

            Section(html, "⚡ Highest Engagement (reactions + forwards per view, posts with 100+ views)", table.Append("</table>").ToString());
        }

        return html.ToString();
    }

    private static void Section(StringBuilder html, string heading, string body) =>
        html.Append("<h2>").Append(WebUtility.HtmlEncode(heading)).Append("</h2>\n").Append(body).Append('\n');

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
