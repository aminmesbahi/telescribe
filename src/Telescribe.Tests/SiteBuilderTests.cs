using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Telescribe.Core.Models;
using Telescribe.Core.Site;

namespace Telescribe.Tests;

public class SiteBuilderTests
{
    private static string PostMarkdown(DateTime created, string body, int views = 10) =>
        $"**Created:** {created:yyyy-MM-dd HH:mm:ss} UTC\n**Post Type:** Text\n**Views:** {views}\n**Reactions:** 1\n**Forwards:** 0\n\n{body}\n";

    private static string WriteExport(TempDirectory temp, params (long Id, string Markdown)[] posts)
    {
        var exports = temp.Combine("exports");
        Directory.CreateDirectory(exports);
        foreach (var (id, markdown) in posts)
        {
            File.WriteAllText(Path.Combine(exports, id + ".md"), markdown);
        }

        return exports;
    }

    private static SiteBuilder NewBuilder(StaticSiteConfig? config = null) =>
        new(config ?? new StaticSiteConfig { TemplateName = "en", SiteTitle = "Test Site", OpenBrowserAfterGeneration = false }, Templates.Path("en"));

    private static readonly DateTime Day1 = new(2024, 10, 7, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task A_basic_site_has_index_post_pages_and_styles()
    {
        using var temp = new TempDirectory();
        var exports = WriteExport(temp,
            (1, PostMarkdown(Day1, "First post title\n\nBody of the first post.")),
            (2, PostMarkdown(Day1.AddDays(1), "Second post title\n\nBody of the second post.")));
        var site = temp.Combine("site");

        var result = await NewBuilder().BuildAsync(exports, site);

        Assert.Equal(2, result.PostPages);
        Assert.True(File.Exists(Path.Combine(site, "index.html")));
        Assert.True(File.Exists(Path.Combine(site, "posts", "1.html")));
        Assert.True(File.Exists(Path.Combine(site, "posts", "2.html")));
        Assert.True(File.Exists(Path.Combine(site, "assets", "style.css")));

        var index = File.ReadAllText(result.IndexPath);
        Assert.Contains("First post title", index);
        Assert.Contains("Second post title", index);
        Assert.Contains("<style>", index); // stylesheet inlined
        Assert.DoesNotContain("{{", index);
    }

    [Fact]
    public async Task Hostile_titles_cannot_inject_markup_into_any_page()
    {
        using var temp = new TempDirectory();
        var exports = WriteExport(temp,
            (1, PostMarkdown(Day1, "<img src=x onerror=alert(1)>\n\nBody")),
            (2, PostMarkdown(Day1.AddDays(1), "</script><script>alert(2)</script>\n\nBody")));
        var site = temp.Combine("site");

        await NewBuilder().BuildAsync(exports, site);

        foreach (var page in new[] { "index.html", "posts/1.html", "posts/2.html" })
        {
            var html = File.ReadAllText(Path.Combine(site, page));
            Assert.DoesNotContain("<img src=x", html);
            Assert.DoesNotContain("<script>alert", html);
        }

        Assert.Contains("&lt;img src=x onerror=alert(1)&gt;", File.ReadAllText(Path.Combine(site, "posts", "1.html")));
    }

    [Fact]
    public async Task Structured_data_stays_valid_json_even_for_hostile_titles()
    {
        using var temp = new TempDirectory();
        const string title = "</script><script>alert(2)</script> \"quoted\" & سلام";
        var exports = WriteExport(temp, (1, PostMarkdown(Day1, title + "\n\nBody")));
        var site = temp.Combine("site");

        await NewBuilder().BuildAsync(exports, site);

        var html = File.ReadAllText(Path.Combine(site, "posts", "1.html"));
        var match = Regex.Match(html, "<script type=\"application/ld\\+json\">(.*?)</script>", RegexOptions.Singleline);
        Assert.True(match.Success);

        using var json = JsonDocument.Parse(match.Groups[1].Value);
        var headline = json.RootElement.GetProperty("@graph")[0].GetProperty("headline").GetString();
        Assert.Equal(title, headline);
    }

    [Fact]
    public async Task Structured_data_has_no_placeholder_values()
    {
        using var temp = new TempDirectory();
        var exports = WriteExport(temp, (1, PostMarkdown(Day1, "Title\n\nBody")));
        var site = temp.Combine("site");

        await NewBuilder().BuildAsync(exports, site);

        foreach (var page in new[] { "index.html", "posts/1.html" })
        {
            var html = File.ReadAllText(Path.Combine(site, page));
            Assert.DoesNotContain("REPLACE_WITH", html);
            Assert.DoesNotContain("example.com", html);
        }
    }

    [Fact]
    public async Task Configured_author_and_publisher_appear_in_structured_data()
    {
        using var temp = new TempDirectory();
        var exports = WriteExport(temp, (1, PostMarkdown(Day1, "Title\n\nBody")));
        var site = temp.Combine("site");
        var config = new StaticSiteConfig
        {
            TemplateName = "en",
            SiteTitle = "Test Site",
            AuthorName = "Jane Doe",
            AuthorUrl = "https://jane.test",
            PublisherName = "Jane Inc",
            SiteBaseUrl = "https://blog.test",
            SameAs = ["https://x.test/jane"]
        };

        await NewBuilder(config).BuildAsync(exports, site);

        var html = File.ReadAllText(Path.Combine(site, "posts", "1.html"));
        Assert.Contains("\"Jane Doe\"", html);
        Assert.Contains("https://jane.test", html);
        Assert.Contains("\"Jane Inc\"", html);
        Assert.Contains("https://x.test/jane", html);
        Assert.Contains("https://blog.test/posts/1.html", html);
    }

    [Fact]
    public async Task Every_post_gets_a_page_even_when_the_index_lists_fewer()
    {
        using var temp = new TempDirectory();
        var exports = WriteExport(temp,
            (1, PostMarkdown(Day1, "Alpha post\n\nBody")),
            (2, PostMarkdown(Day1.AddDays(1), "Beta post\n\nBody")),
            (3, PostMarkdown(Day1.AddDays(2), "Gamma post\n\nBody")));
        var site = temp.Combine("site");
        var config = new StaticSiteConfig { TemplateName = "en", MaxPostsInIndex = 1, SiteBaseUrl = "https://blog.test" };

        var result = await NewBuilder(config).BuildAsync(exports, site);

        Assert.Equal(3, result.PostPages);
        Assert.Equal(1, result.IndexedPosts);
        Assert.Equal(3, Directory.GetFiles(Path.Combine(site, "posts")).Length);

        var index = File.ReadAllText(result.IndexPath);
        Assert.Equal(1, Regex.Count(index, "class='post-card'"));
        Assert.Contains("Gamma post", index);
        Assert.DoesNotContain("Alpha post", index);

        var sitemap = File.ReadAllText(Path.Combine(site, "sitemap.xml"));
        Assert.Contains("https://blog.test/posts/1.html", sitemap);
        Assert.Contains("https://blog.test/posts/3.html", sitemap);
    }

    [Fact]
    public async Task A_max_of_zero_lists_every_post()
    {
        using var temp = new TempDirectory();
        var exports = WriteExport(temp,
            (1, PostMarkdown(Day1, "A\n\nBody")),
            (2, PostMarkdown(Day1.AddDays(1), "B\n\nBody")));

        var result = await NewBuilder(new StaticSiteConfig { TemplateName = "en", MaxPostsInIndex = 0 })
            .BuildAsync(exports, temp.Combine("site"));

        Assert.Equal(2, result.IndexedPosts);
    }

    [Fact]
    public async Task Empty_posts_can_be_skipped()
    {
        using var temp = new TempDirectory();
        var exports = WriteExport(temp,
            (1, PostMarkdown(Day1, "Real post\n\nBody")),
            (2, PostMarkdown(Day1.AddDays(1), "[No content]")));

        var result = await NewBuilder(new StaticSiteConfig { TemplateName = "en", SkipEmptyContentPosts = true })
            .BuildAsync(exports, temp.Combine("site"));

        Assert.Equal(1, result.PostPages);
        Assert.False(File.Exists(Path.Combine(temp.Path, "site", "posts", "2.html")));
    }

    [Fact]
    public async Task Rebuilding_into_an_existing_site_refreshes_it_and_removes_stale_pages()
    {
        using var temp = new TempDirectory();
        var exports = WriteExport(temp,
            (1, PostMarkdown(Day1, "One\n\nBody", views: 5)),
            (2, PostMarkdown(Day1.AddDays(1), "Two\n\nBody")));
        var site = temp.Combine("site");
        var builder = NewBuilder();

        var first = await builder.BuildAsync(exports, site);
        Assert.Equal(2, first.NewPostPages);

        File.WriteAllText(Path.Combine(exports, "1.md"), PostMarkdown(Day1, "One\n\nBody", views: 999));
        File.Delete(Path.Combine(exports, "2.md"));
        File.WriteAllText(Path.Combine(exports, "3.md"), PostMarkdown(Day1.AddDays(2), "Three\n\nBody"));

        var second = await builder.BuildAsync(exports, site);

        Assert.Equal(1, second.NewPostPages);
        Assert.Equal(1, second.UpdatedPostPages);
        Assert.Equal(1, second.RemovedPostPages);
        Assert.False(File.Exists(Path.Combine(site, "posts", "2.html")));
        Assert.Contains("999", File.ReadAllText(Path.Combine(site, "posts", "1.html")));
    }

    [Fact]
    public async Task Media_is_copied_once_and_images_resolve_from_post_pages()
    {
        using var temp = new TempDirectory();
        var exports = WriteExport(temp, (1, PostMarkdown(Day1, "![1_photo.jpg](./media/1_photo.jpg)\n\nCaption line\n\nMore")));
        Directory.CreateDirectory(Path.Combine(exports, "media"));
        File.WriteAllBytes(Path.Combine(exports, "media", "1_photo.jpg"), [1, 2, 3, 4]);
        var site = temp.Combine("site");
        var builder = NewBuilder();

        var first = await builder.BuildAsync(exports, site);
        var second = await builder.BuildAsync(exports, site);

        Assert.Equal(1, first.MediaFilesCopied);
        Assert.Equal(0, second.MediaFilesCopied);
        Assert.Equal([1, 2, 3, 4], File.ReadAllBytes(Path.Combine(site, "media", "1_photo.jpg")));

        var html = File.ReadAllText(Path.Combine(site, "posts", "1.html"));
        Assert.Contains("src=\"../media/1_photo.jpg\"", html);
    }

    [Fact]
    public async Task Dates_are_gregorian_whatever_the_current_culture()
    {
        using var temp = new TempDirectory();
        var exports = WriteExport(temp, (1, PostMarkdown(Day1, "Title\n\nBody")));
        var site = temp.Combine("site");

        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("fa-IR");
            await NewBuilder().BuildAsync(exports, site);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }

        var html = File.ReadAllText(Path.Combine(site, "index.html"));
        Assert.Contains("2024-10-07", html);
        Assert.DoesNotContain("1403", html);
    }

    [Fact]
    public async Task Without_a_base_url_the_sitemap_is_skipped_with_a_warning()
    {
        using var temp = new TempDirectory();
        var exports = WriteExport(temp, (1, PostMarkdown(Day1, "Title\n\nBody")));

        var result = await NewBuilder().BuildAsync(exports, temp.Combine("site"));

        Assert.Null(result.SitemapPath);
        Assert.Single(result.Warnings);
        Assert.False(File.Exists(Path.Combine(temp.Path, "site", "sitemap.xml")));
    }

    [Fact]
    public async Task The_persian_template_builds_too()
    {
        using var temp = new TempDirectory();
        var exports = WriteExport(temp, (1, PostMarkdown(Day1, "**عنوان پست**\n\nمتن پست")));
        var site = temp.Combine("site");

        var builder = new SiteBuilder(new StaticSiteConfig { TemplateName = "fa", SiteTitle = "آرشیو" }, Templates.Path("fa"));
        await builder.BuildAsync(exports, site);

        Assert.Contains("عنوان پست", File.ReadAllText(Path.Combine(site, "index.html")));
    }

    [Fact]
    public async Task Building_without_posts_fails_clearly()
    {
        using var temp = new TempDirectory();
        var exports = temp.Combine("exports");
        Directory.CreateDirectory(exports);

        await Assert.ThrowsAsync<InvalidOperationException>(() => NewBuilder().BuildAsync(exports, temp.Combine("site")));
    }

    [Fact]
    public void Inlining_css_leaves_dollar_signs_alone()
    {
        const string css = "a::after{content:'$1 $& $$'}";

        var html = SiteBuilder.InlineCss("<head><link rel='stylesheet' href='../assets/style.css'></head>", css);

        Assert.Equal("<head><style>" + css + "</style></head>", html);
    }

    [Fact]
    public void Unpaired_surrogates_are_replaced()
    {
        var emoji = char.ConvertFromUtf32(0x1F600);

        Assert.Equal(emoji, SiteBuilder.SanitizeUnicode(emoji));
        Assert.Equal("a" + (char)0xFFFD + "b", SiteBuilder.SanitizeUnicode("a" + (char)0xD83D + "b"));
        Assert.Equal("a" + (char)0xFFFD + "b", SiteBuilder.SanitizeUnicode("a" + (char)0xDE00 + "b"));
    }
}
