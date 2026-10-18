using System.Text.Json;
using Telescribe.Core.Models;
using Telescribe.Core.Services;
using Telescribe.Core.Site;

namespace Telescribe.Tests;

public class ReportServiceTests
{
    private static ExportSummary Summary() => new()
    {
        TotalPosts = 2,
        Posts =
        [
            new PostSummary { PostId = 1, CreatedAt = new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc), Views = 1200, Reactions = 3, TotalForwards = 1, ContentPreview = "<script>alert(1)</script>" },
            new PostSummary { PostId = 2, CreatedAt = new DateTime(2024, 2, 3, 0, 0, 0, DateTimeKind.Utc), Views = 50, Reactions = 9, TotalForwards = 4, ContentPreview = "" }
        ]
    };

    [Fact]
    public void Previews_are_escaped_and_totals_are_summed()
    {
        var html = new ReportService(Templates.Path("reports")).Render(Summary(), new DateTime(2025, 6, 7, 8, 9, 10, DateTimeKind.Utc));

        Assert.DoesNotContain("<script>alert", html);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html);
        Assert.Contains("1,250", html); // total views
        Assert.Contains("No preview available", html);
        Assert.Contains("2025-06-07 08:09:10 UTC", html);
        Assert.DoesNotContain("{{", html);
    }

    [Fact]
    public async Task Generating_writes_the_report_next_to_the_summary()
    {
        using var temp = new TempDirectory();
        var json = JsonSerializer.Serialize(Summary(), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        File.WriteAllText(temp.Combine("summary.json"), json);

        var path = await new ReportService(Templates.Path("reports")).GenerateAsync(temp.Path);

        Assert.Equal(Path.Combine(temp.Path, "reports", "analytics_report.html"), path);
        Assert.Contains("Telescribe Analytics Report", File.ReadAllText(path));
    }

    [Fact]
    public async Task Generating_without_a_summary_fails_clearly()
    {
        using var temp = new TempDirectory();

        await Assert.ThrowsAsync<FileNotFoundException>(() => new ReportService(Templates.Path("reports")).GenerateAsync(temp.Path));
    }
}

public class JsonLdTests
{
    [Fact]
    public void Strings_cannot_close_the_surrounding_script_element()
    {
        var literal = JsonLd.String("</script><script>alert(1)</script>");

        Assert.DoesNotContain("<", literal);
        Assert.Equal("</script><script>alert(1)</script>", JsonSerializer.Deserialize<string>(literal));
    }

    [Fact]
    public void Non_ascii_text_stays_readable()
    {
        Assert.Contains("سلام", JsonLd.String("سلام"));
    }

    [Fact]
    public void Javascript_line_separators_are_escaped()
    {
        var literal = JsonLd.String("a" + (char)0x2028 + "b" + (char)0x2029 + "c");

        Assert.DoesNotContain((char)0x2028, literal);
        Assert.DoesNotContain((char)0x2029, literal);
        Assert.Equal("a" + (char)0x2028 + "b" + (char)0x2029 + "c", JsonSerializer.Deserialize<string>(literal));
    }

    [Fact]
    public void Null_becomes_an_empty_string_literal()
    {
        Assert.Equal("\"\"", JsonLd.String(null));
    }

    [Fact]
    public void Dates_are_formatted_in_utc()
    {
        Assert.Equal("2024-10-07T12:00:00Z", JsonLd.FormatDate(new DateTime(2024, 10, 7, 12, 0, 0, DateTimeKind.Utc)));
    }
}

public class LlmServiceHelperTests
{
    [Fact]
    public void Persian_hashtags_are_accepted()
    {
        var tags = LlmService.ParseHashtagsFromResponse("#معرفی_کتاب\nSQL\n- database\nnot valid tag\nbad!tag\n", maxHashtags: 5);

        Assert.Equal(["معرفی_کتاب", "SQL", "database"], tags);
    }

    [Fact]
    public void Hashtags_are_deduplicated_and_limited()
    {
        var tags = LlmService.ParseHashtagsFromResponse("a\nb\na\nc\nd\n", maxHashtags: 3);

        Assert.Equal(["a", "b", "c"], tags);
    }

    [Fact]
    public void Titles_are_cleaned()
    {
        Assert.Equal("My title", LlmService.CleanGeneratedTitle("\"Title: My title #tag\""));
        Assert.Equal(string.Empty, LlmService.CleanGeneratedTitle("  "));
    }

    [Fact]
    public void Prompt_content_is_stripped_of_markdown_and_capped()
    {
        var cleaned = LlmService.CleanContentForPrompt("![img](./media/a.jpg) **bold** [link](https://x.test) " + new string('z', 2000));

        Assert.StartsWith("bold link", cleaned);
        Assert.True(cleaned.Length <= 1003);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(429, true)]
    [InlineData(500, true)]
    [InlineData(503, true)]
    [InlineData(400, false)]
    [InlineData(401, false)]
    [InlineData(404, false)]
    public void Only_transient_http_failures_are_retried(int? status, bool expected)
    {
        var ex = new HttpRequestException("boom", inner: null, status is null ? null : (System.Net.HttpStatusCode)status);

        Assert.Equal(expected, LlmService.IsTransient(ex));
    }

    [Fact]
    public void Timeouts_and_wrapped_transient_errors_are_retried_but_bad_arguments_are_not()
    {
        Assert.True(LlmService.IsTransient(new TaskCanceledException()));
        Assert.True(LlmService.IsTransient(new InvalidOperationException("outer", new HttpRequestException("inner"))));
        Assert.False(LlmService.IsTransient(new ArgumentException("bad")));
    }
}
