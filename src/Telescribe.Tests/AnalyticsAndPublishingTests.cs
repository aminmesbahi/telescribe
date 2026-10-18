using System.IO.Compression;
using Telescribe.Core.Export;
using Telescribe.Core.Models;
using Telescribe.Core.Site;

namespace Telescribe.Tests;

public class StatsHistoryTests
{
    private static PostSummary Post(long id, int views, int reactions = 0, int forwards = 0) =>
        new() { PostId = id, Views = views, Reactions = reactions, TotalForwards = forwards };

    private static readonly DateTime T1 = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Only_changed_counters_are_recorded()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("history.jsonl");

        Assert.Equal(2, StatsHistory.AppendChanges(path, T1, [Post(1, 10), Post(2, 20)]));
        Assert.Equal(1, StatsHistory.AppendChanges(path, T1.AddDays(1), [Post(1, 10), Post(2, 25)]));
        Assert.Equal(0, StatsHistory.AppendChanges(path, T1.AddDays(2), [Post(1, 10), Post(2, 25)]));

        var history = StatsHistory.Load(path);
        Assert.Equal(3, history.Count);
        Assert.Equal(25, history[^1].Views);
        Assert.Equal(T1.AddDays(1), history[^1].Time);
    }

    [Fact]
    public void Reaction_and_forward_changes_count_too()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("history.jsonl");
        StatsHistory.AppendChanges(path, T1, [Post(1, 10)]);

        Assert.Equal(1, StatsHistory.AppendChanges(path, T1.AddDays(1), [Post(1, 10, reactions: 1)]));
        Assert.Equal(1, StatsHistory.AppendChanges(path, T1.AddDays(2), [Post(1, 10, reactions: 1, forwards: 1)]));
    }

    [Fact]
    public void A_truncated_line_does_not_make_the_log_unreadable()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("history.jsonl");
        StatsHistory.AppendChanges(path, T1, [Post(1, 10)]);
        File.AppendAllText(path, "{\"t\":\"2025-01-0\n\n");
        StatsHistory.AppendChanges(path, T1.AddDays(1), [Post(1, 11)]);

        Assert.Equal(2, StatsHistory.Load(path).Count);
    }

    [Fact]
    public void A_missing_log_is_empty()
    {
        using var temp = new TempDirectory();

        Assert.Empty(StatsHistory.Load(temp.Combine("nope.jsonl")));
    }
}

public class ReportAnalyticsTests
{
    private static PostSummary Post(long id, DateTime created, int views = 0, int reactions = 0, int forwards = 0, Dictionary<string, int>? breakdown = null) =>
        new() { PostId = id, CreatedAt = created, Views = views, Reactions = reactions, TotalForwards = forwards, ReactionBreakdown = breakdown ?? [] };

    [Fact]
    public void Posts_per_month_counts_and_orders_months()
    {
        var posts = new[]
        {
            Post(1, new DateTime(2024, 3, 5)), Post(2, new DateTime(2024, 3, 20)),
            Post(3, new DateTime(2024, 1, 1)), Post(4, default)
        };

        var months = ReportAnalytics.PostsPerMonth(posts);

        Assert.Equal([("2024-01", 1.0), ("2024-03", 2.0)], months);
    }

    [Fact]
    public void Only_the_latest_months_are_kept()
    {
        var posts = Enumerable.Range(1, 30).Select(i => Post(i, new DateTime(2022, 1, 1).AddMonths(i)));

        var months = ReportAnalytics.PostsPerMonth(posts, months: 12);

        Assert.Equal(12, months.Count);
        Assert.Equal("2024-07", months[^1].Label);
    }

    [Fact]
    public void The_heatmap_uses_monday_first_weekdays_and_utc_hours()
    {
        // 2024-10-07 is a Monday, 2024-10-13 a Sunday.
        var grid = ReportAnalytics.PostingHeatmap(
        [
            Post(1, new DateTime(2024, 10, 7, 9, 30, 0)),
            Post(2, new DateTime(2024, 10, 7, 9, 45, 0)),
            Post(3, new DateTime(2024, 10, 13, 23, 0, 0))
        ]);

        Assert.Equal(2, grid[0, 9]);
        Assert.Equal(1, grid[6, 23]);
        Assert.Equal(3, grid.Cast<int>().Sum());
    }

    [Fact]
    public void Total_views_over_time_use_each_posts_latest_known_value()
    {
        var t = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var history = new[]
        {
            new StatsSnapshot(t, 1, 10, 0, 0), new StatsSnapshot(t, 2, 5, 0, 0),
            new StatsSnapshot(t.AddDays(1), 1, 30, 0, 0),
            new StatsSnapshot(t.AddDays(2), 3, 100, 0, 0)
        };

        var points = ReportAnalytics.ViewsOverTime(history);

        Assert.Equal([(t, 15.0), (t.AddDays(1), 35.0), (t.AddDays(2), 135.0)], points);
    }

    [Fact]
    public void Reactions_are_totalled_per_emoji_most_used_first()
    {
        var posts = new[]
        {
            Post(1, default, breakdown: new() { ["👍"] = 3, ["🔥"] = 1 }),
            Post(2, default, breakdown: new() { ["👍"] = 2, ["❤"] = 4 })
        };

        Assert.Equal([("👍", 5.0), ("❤", 4.0), ("🔥", 1.0)], ReportAnalytics.ReactionTotals(posts));
        Assert.Single(ReportAnalytics.ReactionTotals(posts, top: 1));
    }

    [Fact]
    public void Engagement_ignores_small_posts_and_ranks_by_rate()
    {
        var posts = new[]
        {
            Post(1, default, views: 1000, reactions: 10, forwards: 0),   // 1%
            Post(2, default, views: 200, reactions: 10, forwards: 10),   // 10%
            Post(3, default, views: 10, reactions: 10, forwards: 10)     // too few views
        };

        var ranked = ReportAnalytics.Engagement(posts);

        Assert.Equal([2L, 1L], ranked.Select(r => r.Post.PostId));
        Assert.Equal(10.0, ranked[0].Rate);
    }
}

public class ChartTests
{
    private static int[,] Grid()
    {
        var grid = new int[7, 24];
        grid[0, 0] = 1;
        return grid;
    }

    [Fact]
    public void Labels_are_escaped_in_every_chart()
    {
        const string hostile = "<script>alert(1)</script>";
        var t = new DateTime(2025, 1, 1);

        var charts = new[]
        {
            Charts.Bars([(hostile, 3), ("b", 1)], hostile),
            Charts.Line([(t, 1), (t.AddDays(1), 2)], hostile),
            Charts.HorizontalBars([(hostile, 4)]),
            Charts.Heatmap(Grid(), hostile)
        };

        foreach (var svg in charts)
        {
            Assert.DoesNotContain("<script", svg);
            Assert.StartsWith("<svg", svg);
        }
    }

    [Fact]
    public void Charts_without_data_say_so_instead_of_drawing_nothing()
    {
        Assert.Contains("Not enough data", Charts.Bars([], "x"));
        Assert.Contains("Not enough data", Charts.Line([(DateTime.UtcNow, 1)], "x"));
        Assert.Contains("Not enough data", Charts.HorizontalBars([]));
        Assert.Contains("Not enough data", Charts.Heatmap(new int[7, 24], "x"));
    }

    [Fact]
    public void Numbers_use_a_dot_whatever_the_culture()
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("fa-IR");

            var svg = Charts.Bars([("a", 1.5), ("b", 3)], "x");

            Assert.DoesNotContain("٫", svg); // Arabic decimal separator
            Assert.DoesNotContain(",", svg.Split("<title>")[0].Replace("viewBox", "")); // no decimal commas in coordinates
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void The_report_contains_charts_and_explains_missing_history()
    {
        var summary = new ExportSummary
        {
            Posts =
            [
                new PostSummary { PostId = 1, CreatedAt = new DateTime(2024, 1, 2, 9, 0, 0), Views = 500, Reactions = 20, TotalForwards = 5, ContentPreview = "<b>x</b>", ReactionBreakdown = new() { ["👍"] = 20 } }
            ]
        };

        var service = new ReportService(Templates.Path("reports"));
        var withoutHistory = service.Render(summary, DateTime.UtcNow);
        var withHistory = service.Render(summary, DateTime.UtcNow,
        [
            new StatsSnapshot(new DateTime(2025, 1, 1), 1, 100, 0, 0),
            new StatsSnapshot(new DateTime(2025, 1, 8), 1, 500, 0, 0)
        ]);

        Assert.Contains("Posts per Month", withoutHistory);
        Assert.Contains("<svg", withoutHistory);
        Assert.Contains("at least two updates", withoutHistory);
        Assert.Contains("Highest Engagement", withoutHistory);
        Assert.Contains("&lt;b&gt;x&lt;/b&gt;", withoutHistory);
        Assert.DoesNotContain("at least two updates", withHistory);
        Assert.Contains("<polyline", withHistory);
    }
}

public class ArchiveServiceTests
{
    [Fact]
    public async Task Only_exported_content_is_archived()
    {
        using var temp = new TempDirectory();
        var exports = temp.Combine("exports");
        foreach (var (path, content) in new[]
                 {
                     ("1.md", "post"), ("summary.json", "{}"), ("stats-history.jsonl", ""),
                     ("raw/1.md", "raw"), ("processed/1.md", "processed"),
                     ("media/a.jpg", "jpg"), ("media/sub/b.mp4", "mp4"), ("media/c.jpg.part", "partial"),
                     ("site/index.html", "html"), ("website_2025/index.html", "html"), ("reports/r.html", "html"),
                     ("archives/old.zip", "zip"), ("WTelegram.session", "secret"), ("wordpress_state.json", "{}")
                 })
        {
            var full = Path.Combine(exports, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }

        var result = await ArchiveService.CreateAsync(exports, temp.Combine("out", "backup.zip"));

        using var zip = ZipFile.OpenRead(result.Path);
        Assert.Equal(
            ["1.md", "media/a.jpg", "media/sub/b.mp4", "processed/1.md", "raw/1.md", "stats-history.jsonl", "summary.json"],
            zip.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal));
        Assert.Equal(7, result.Files);
        Assert.False(File.Exists(result.Path + ".tmp"));
        using var reader = new StreamReader(zip.GetEntry("1.md")!.Open());
        Assert.Equal("post", reader.ReadToEnd());
    }

    [Fact]
    public async Task Archiving_nothing_fails_clearly()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(temp.Combine("exports"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => ArchiveService.CreateAsync(temp.Combine("exports"), temp.Combine("a.zip")));
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => ArchiveService.CreateAsync(temp.Combine("missing"), temp.Combine("a.zip")));
    }
}

public class SiteDeployerTests
{
    private static string Site(TempDirectory temp)
    {
        var site = temp.Combine("site");
        Directory.CreateDirectory(Path.Combine(site, "posts"));
        File.WriteAllText(Path.Combine(site, "index.html"), "index");
        File.WriteAllText(Path.Combine(site, "posts", "1.html"), "one");
        File.WriteAllText(Path.Combine(site, SiteBuilder.MarkerFileName), "marker");
        return site;
    }

    [Fact]
    public void Files_are_copied_once_and_then_left_alone()
    {
        using var temp = new TempDirectory();
        var site = Site(temp);
        var target = temp.Combine("public");

        var first = SiteDeployer.Deploy(site, target, mirror: false);
        var second = SiteDeployer.Deploy(site, target, mirror: false);

        Assert.Equal(new DeployResult(2, 0, 0), first); // the marker file is not published
        Assert.Equal(new DeployResult(0, 2, 0), second);
        Assert.Equal("one", File.ReadAllText(Path.Combine(target, "posts", "1.html")));
        Assert.False(File.Exists(Path.Combine(target, SiteBuilder.MarkerFileName)));
    }

    [Fact]
    public void Changed_files_are_overwritten()
    {
        using var temp = new TempDirectory();
        var site = Site(temp);
        var target = temp.Combine("public");
        SiteDeployer.Deploy(site, target, mirror: false);

        File.WriteAllText(Path.Combine(site, "index.html"), "INDEX"); // same length, different content

        Assert.Equal(1, SiteDeployer.Deploy(site, target, mirror: false).Copied);
        Assert.Equal("INDEX", File.ReadAllText(Path.Combine(target, "index.html")));
    }

    [Fact]
    public void Extra_files_survive_unless_mirroring_and_even_then_git_and_cname_are_kept()
    {
        using var temp = new TempDirectory();
        var site = Site(temp);
        var target = temp.Combine("public");
        SiteDeployer.Deploy(site, target, mirror: false);

        File.WriteAllText(Path.Combine(target, "stale.html"), "old");
        Directory.CreateDirectory(Path.Combine(target, "old-folder"));
        File.WriteAllText(Path.Combine(target, "old-folder", "x.html"), "old");
        Directory.CreateDirectory(Path.Combine(target, ".git"));
        File.WriteAllText(Path.Combine(target, ".git", "HEAD"), "ref");
        File.WriteAllText(Path.Combine(target, "CNAME"), "example.com");
        File.WriteAllText(Path.Combine(target, ".nojekyll"), "");

        Assert.Equal(0, SiteDeployer.Deploy(site, target, mirror: false).Deleted);
        Assert.True(File.Exists(Path.Combine(target, "stale.html")));

        var mirrored = SiteDeployer.Deploy(site, target, mirror: true);

        Assert.Equal(2, mirrored.Deleted);
        Assert.False(File.Exists(Path.Combine(target, "stale.html")));
        Assert.False(Directory.Exists(Path.Combine(target, "old-folder")));
        Assert.True(File.Exists(Path.Combine(target, ".git", "HEAD")));
        Assert.True(File.Exists(Path.Combine(target, "CNAME")));
        Assert.True(File.Exists(Path.Combine(target, ".nojekyll")));
        Assert.True(File.Exists(Path.Combine(target, "index.html")));
    }

    [Fact]
    public void Targets_that_overlap_the_site_are_refused()
    {
        using var temp = new TempDirectory();
        var site = Site(temp);

        Assert.Throws<InvalidOperationException>(() => SiteDeployer.Deploy(site, site, mirror: true));
        Assert.Throws<InvalidOperationException>(() => SiteDeployer.Deploy(site, Path.Combine(site, "posts"), mirror: true));
        Assert.Throws<InvalidOperationException>(() => SiteDeployer.Deploy(site, temp.Path, mirror: true));
    }

    [Fact]
    public void A_missing_site_fails_clearly()
    {
        using var temp = new TempDirectory();

        Assert.Throws<DirectoryNotFoundException>(() => SiteDeployer.Deploy(temp.Combine("nope"), temp.Combine("public"), mirror: false));
    }
}
