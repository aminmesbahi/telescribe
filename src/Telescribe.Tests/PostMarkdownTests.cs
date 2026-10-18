using System.Globalization;
using Telescribe.Core.Export;
using Telescribe.Core.Models;

namespace Telescribe.Tests;

public class PostMarkdownTests
{
    private const string Sample = """
        **Created:** 2024-10-07 15:57:58 UTC
        **Edited:** 2024-10-07 16:02:42 UTC
        **Post Type:** Photo
        **Views:** 1283
        **Reactions:** 10
        **Forwards:** 3

        ![102_photo.jpg](./media/102_photo.jpg)

        **Book intro: T-SQL Fundamentals**

        This book is one of the best.
        See [the site](https://example.com) for more.
        """;

    [Fact]
    public void Metrics_are_read_from_the_header()
    {
        Assert.Equal((1283, 10, 3), PostMarkdown.ExtractMetrics(Sample));
    }

    [Fact]
    public void Unparseable_metrics_default_to_zero()
    {
        Assert.Equal((0, 0, 0), PostMarkdown.ExtractMetrics("**Views:** lots\n**Reactions:**\n"));
    }

    [Fact]
    public void Creation_date_is_utc_and_culture_independent()
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("fa-IR");

            var date = PostMarkdown.ExtractCreationDate(Sample);

            Assert.Equal(new DateTime(2024, 10, 7, 15, 57, 58, DateTimeKind.Utc), date);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Missing_creation_date_is_min_value()
    {
        Assert.Equal(DateTime.MinValue, PostMarkdown.ExtractCreationDate("just text"));
    }

    [Fact]
    public void Title_skips_metadata_and_images_and_strips_formatting()
    {
        Assert.Equal("Book intro: T-SQL Fundamentals", PostMarkdown.ExtractTitle(Sample));
        Assert.Equal("Hello world", PostMarkdown.ExtractTitle("# Hello **world**"));
        Assert.Equal("Link text", PostMarkdown.ExtractTitle("## [Link text](https://example.com)"));
    }

    [Fact]
    public void Title_handles_a_byte_order_mark_before_the_header()
    {
        var withBom = (char)0xFEFF + "**Created:** 2024-01-01 00:00:00 UTC\n\nTitle line";

        Assert.Equal("Title line", PostMarkdown.ExtractTitle(withBom));
        Assert.Equal(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), PostMarkdown.ExtractCreationDate(withBom));
    }

    [Fact]
    public void Long_titles_are_truncated_on_a_word_boundary()
    {
        var title = PostMarkdown.ExtractTitle(string.Join(' ', Enumerable.Repeat("word", 100)), maxLength: 30)!;

        Assert.True(title.Length <= 30, title);
        Assert.EndsWith("word…", title);
    }

    [Fact]
    public void Truncation_never_splits_a_surrogate_pair()
    {
        var emoji = char.ConvertFromUtf32(0x1F600);

        for (var length = 20; length < 40; length++)
        {
            var title = PostMarkdown.ExtractTitle(new string('a', 18) + string.Concat(Enumerable.Repeat(emoji, 20)), maxLength: length)!;

            for (var i = 0; i < title.Length; i++)
            {
                if (char.IsHighSurrogate(title[i]))
                {
                    Assert.True(i + 1 < title.Length && char.IsLowSurrogate(title[i + 1]), $"length {length}: lone high surrogate");
                }
                else
                {
                    Assert.False(char.IsLowSurrogate(title[i]) && (i == 0 || !char.IsHighSurrogate(title[i - 1])), $"length {length}: lone low surrogate");
                }
            }
        }
    }

    [Fact]
    public void Preview_is_the_text_after_the_title()
    {
        var preview = PostMarkdown.ExtractPreview(Sample);

        Assert.StartsWith("This book is one of the best.", preview);
        Assert.Contains("the site", preview);
        Assert.DoesNotContain("](", preview);
    }

    [Fact]
    public void Preview_of_a_single_line_post_is_a_placeholder()
    {
        Assert.Equal("No preview available", PostMarkdown.ExtractPreview("**Created:** 2024-01-01 00:00:00 UTC\n\nOnly a title"));
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("**Views:** 1\n\n[No content]\n", true)]
    [InlineData("**Views:** 1\n\nSome text\n", false)]
    public void No_content_detection(string content, bool expected)
    {
        Assert.Equal(expected, PostMarkdown.IsNoContent(content));
    }

    [Fact]
    public void LLM_header_lines_are_not_mistaken_for_the_title()
    {
        const string content = "**Created:** 2024-01-01 00:00:00 UTC\n**LLM Provider:** openai\n**Generated Title:** Something\n**Hashtags:** #a, #b\n\nReal first line";

        Assert.Equal("Real first line", PostMarkdown.ExtractTitle(content));
    }

    [Fact]
    public void Raw_writer_output_round_trips_through_the_reader()
    {
        var post = new TelegramPost
        {
            Id = 7,
            CreatedAt = new DateTime(2025, 3, 4, 5, 6, 7, DateTimeKind.Utc),
            Views = 100,
            Reactions = 5,
            TotalForwards = 2,
            Content = "First line\nSecond line",
            MediaFiles = ["7_photo.jpg"]
        };

        var markdown = PostMarkdownWriter.BuildRaw(post);

        Assert.Equal(post.CreatedAt, PostMarkdown.ExtractCreationDate(markdown));
        Assert.Equal((100, 5, 2), PostMarkdown.ExtractMetrics(markdown));
        Assert.Equal("First line", PostMarkdown.ExtractTitle(markdown));
        Assert.Contains("![7_photo.jpg](./media/7_photo.jpg)", markdown);
    }

    [Fact]
    public void Empty_posts_are_marked_as_having_no_content()
    {
        var markdown = PostMarkdownWriter.BuildRaw(new TelegramPost { Id = 1, CreatedAt = DateTime.UtcNow });

        Assert.True(PostMarkdown.IsNoContent(markdown));
    }

    [Fact]
    public void Media_links_encode_characters_that_would_break_markdown()
    {
        var markdown = PostMarkdownWriter.BuildMediaMarkdown("12_my file (1).pdf");

        Assert.Contains("(./media/12_my%20file%20%281%29.pdf)", markdown);
    }

    [Fact]
    public void Enrichment_survives_a_processed_file_round_trip()
    {
        var post = new TelegramPost
        {
            Id = 9,
            CreatedAt = DateTime.UtcNow,
            Content = "Body",
            GeneratedTitle = "A Generated Title",
            Hashtags = ["sql", "books"],
            IsLlmProcessed = true,
            LlmProvider = "OpenAI"
        };

        var enrichment = PostMarkdown.ReadEnrichment(PostMarkdownWriter.BuildProcessed(post), 9, articleTitle: null);

        Assert.NotNull(enrichment);
        Assert.Equal("A Generated Title", enrichment.GeneratedTitle);
        Assert.Equal(["sql", "books"], enrichment.Hashtags);
        Assert.Equal("OpenAI", enrichment.Provider);
    }

    [Fact]
    public void Enrichment_is_null_for_unprocessed_files_and_ignores_fallback_titles()
    {
        Assert.Null(PostMarkdown.ReadEnrichment("# Post 9\n\n**Created:** 2024-01-01 00:00:00 UTC\n", 9, null));

        var post = new TelegramPost { Id = 9, CreatedAt = DateTime.UtcNow, Content = "Body", IsLlmProcessed = true, LlmProvider = "x" };
        var enrichment = PostMarkdown.ReadEnrichment(PostMarkdownWriter.BuildProcessed(post), 9, null);

        Assert.NotNull(enrichment);
        Assert.Null(enrichment.GeneratedTitle); // "Post 9" is only the fallback heading
    }
}
