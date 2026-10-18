using System.Xml.Linq;
using Telescribe.Core.Export;
using Telescribe.Core.Models;
using Telescribe.Core.Services;
using Telescribe.Core.Site;
using Telescribe.Core.Templating;

namespace Telescribe.Tests;

public class TagIndexTests
{
    [Fact]
    public void Hashtags_in_text_are_found_including_persian_ones()
    {
        var tags = TagIndex.ExtractTags("Learning #SQL and #معرفی_کتاب today. #dotnet\nSecond line #SQL again");

        Assert.Equal(["SQL", "معرفی_کتاب", "dotnet"], tags);
    }

    [Theory]
    [InlineData("# A heading")]
    [InlineData("Issue #1 and #2024")]
    [InlineData("Link https://example.com/page#section here")]
    [InlineData("Entity &#39; and a#b")]
    [InlineData("Code `#notatag` here")]
    [InlineData("```\n#notatag\n```")]
    [InlineData("A [link](https://example.com/#anchor)")]
    [InlineData("**Tags:** #fromheader")]
    [InlineData("A lone # sign")]
    public void Things_that_only_look_like_hashtags_are_ignored(string markdown)
    {
        Assert.Empty(TagIndex.ExtractTags(markdown));
    }

    [Fact]
    public void Enrichment_tags_come_first_and_duplicates_are_merged_case_insensitively()
    {
        var tags = TagIndex.ExtractTags("Body #sql #New", ["SQL", "#books"]);

        Assert.Equal(["SQL", "books", "New"], tags);
    }

    [Fact]
    public void Single_character_tags_are_ignored()
    {
        Assert.Empty(TagIndex.ExtractTags("#a #b"));
    }

    private static PostData Post(string name, DateTime date, params string[] tags) =>
        new() { Filename = name, Title = name, Date = date, Tags = [.. tags] };

    [Fact]
    public void Tags_are_grouped_filtered_by_use_and_ordered_by_popularity()
    {
        var posts = new[]
        {
            Post("1", new DateTime(2024, 1, 1), "SQL", "rare"),
            Post("2", new DateTime(2024, 1, 2), "sql", "dotnet"),
            Post("3", new DateTime(2024, 1, 3), "SQL", "dotnet")
        };

        var tags = TagIndex.Build(posts, minPosts: 2);

        Assert.Equal(["SQL", "dotnet"], tags.Select(t => t.Display));
        Assert.Equal(["3", "2", "1"], tags[0].Posts.Select(p => p.Filename)); // newest first
        Assert.Equal("tag-sql.html", tags[0].FileName);
    }

    [Fact]
    public void Tag_pages_get_safe_unique_file_names_and_encoded_links()
    {
        var posts = new[]
        {
            Post("1", DateTime.UtcNow, "معرفی", "a/b..c"),
            Post("2", DateTime.UtcNow, "معرفی", "a/b..c")
        };

        var tags = TagIndex.Build(posts, minPosts: 1);

        foreach (var tag in tags)
        {
            Assert.DoesNotContain('/', tag.FileName);
            Assert.DoesNotContain('\\', tag.FileName);
        }

        Assert.Equal("tag-%D9%85%D8%B9%D8%B1%D9%81%DB%8C.html", tags.Single(t => t.Display == "معرفی").Href);
    }

    [Fact]
    public void Tags_that_collide_after_sanitising_still_get_distinct_files()
    {
        var posts = new[]
        {
            Post("1", DateTime.UtcNow, "a?b", "a*b"),
            Post("2", DateTime.UtcNow, "a?b", "a*b")
        };

        var tags = TagIndex.Build(posts, minPosts: 1);

        Assert.Equal(2, tags.Select(t => t.FileName).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}

public class AtomFeedTests
{
    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";

    private static StaticSiteConfig Config() => new() { SiteTitle = "My <Site>", Description = "Desc", SiteBaseUrl = "https://blog.test/", AuthorName = "Jane" };

    private static PostData Post(int id, string title) => new()
    {
        Filename = id.ToString(),
        Title = title,
        Preview = "Preview & more",
        Content = "<p>Hi</p><img src=\"../media/" + id + "_photo.jpg\">",
        Date = new DateTime(2024, 1, id, 12, 0, 0, DateTimeKind.Utc),
        Updated = new DateTime(2024, 1, id, 13, 0, 0, DateTimeKind.Utc)
    };

    [Fact]
    public void The_feed_is_well_formed_atom_with_escaped_text()
    {
        var xml = AtomFeed.Build(Config(), TemplateInfo.English, [Post(2, "Second <b>"), Post(1, "First & last")], 30, DateTime.UtcNow);

        var feed = XDocument.Parse(xml).Root!;
        Assert.Equal(Atom + "feed", feed.Name);
        Assert.Equal("My <Site>", feed.Element(Atom + "title")!.Value);
        Assert.Equal("Jane", feed.Element(Atom + "author")!.Element(Atom + "name")!.Value);
        Assert.Equal("https://blog.test/", feed.Element(Atom + "id")!.Value);

        var entries = feed.Elements(Atom + "entry").ToList();
        Assert.Equal(["Second <b>", "First & last"], entries.Select(e => e.Element(Atom + "title")!.Value));
        Assert.Equal("https://blog.test/posts/2.html", entries[0].Element(Atom + "id")!.Value);
        Assert.Equal("2024-01-02T12:00:00Z", entries[0].Element(Atom + "published")!.Value);
        Assert.Equal("2024-01-02T13:00:00Z", entries[0].Element(Atom + "updated")!.Value);
        Assert.Equal("2024-01-02T13:00:00Z", feed.Element(Atom + "updated")!.Value);
    }

    [Fact]
    public void Media_links_in_entries_are_absolute()
    {
        var xml = AtomFeed.Build(Config(), TemplateInfo.English, [Post(1, "First")], 30, DateTime.UtcNow);

        var content = XDocument.Parse(xml).Root!.Element(Atom + "entry")!.Element(Atom + "content")!.Value;
        Assert.Contains("src=\"https://blog.test/media/1_photo.jpg\"", content);
        Assert.DoesNotContain("../media/", content);
    }

    [Fact]
    public void Only_the_requested_number_of_entries_is_included()
    {
        var xml = AtomFeed.Build(Config(), TemplateInfo.English, [Post(3, "c"), Post(2, "b"), Post(1, "a")], 2, DateTime.UtcNow);

        Assert.Equal(2, XDocument.Parse(xml).Root!.Elements(Atom + "entry").Count());
    }

    [Fact]
    public void An_empty_feed_is_still_valid()
    {
        var xml = AtomFeed.Build(Config(), TemplateInfo.Persian, [], 30, new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var feed = XDocument.Parse(xml).Root!;
        Assert.Empty(feed.Elements(Atom + "entry"));
        Assert.Equal("2025-01-01T00:00:00Z", feed.Element(Atom + "updated")!.Value);
        Assert.Equal("fa", (string?)feed.Attribute(XNamespace.Xml + "lang"));
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>", xml);
    }
}

public class SiteFeatureTests
{
    private static readonly DateTime Day1 = new(2024, 10, 7, 12, 0, 0, DateTimeKind.Utc);

    private static string Md(DateTime created, string body, string extraHeader = "") =>
        $"**Created:** {created:yyyy-MM-dd HH:mm:ss} UTC\n**Post Type:** Text\n**Views:** 5\n**Reactions:** 1\n**Forwards:** 0\n{extraHeader}\n{body}\n";

    private static string Export(TempDirectory temp, params (long Id, string Markdown)[] posts)
    {
        var exports = temp.Combine("exports");
        Directory.CreateDirectory(exports);
        foreach (var (id, markdown) in posts)
        {
            File.WriteAllText(Path.Combine(exports, id + ".md"), markdown);
        }

        return exports;
    }

    private static SiteBuilder Builder(StaticSiteConfig? config = null) =>
        new(config ?? new StaticSiteConfig { TemplateName = "en", SiteTitle = "Test", SiteBaseUrl = "https://blog.test" }, Templates.Path("en"));

    [Fact]
    public async Task Tags_used_by_enough_posts_get_pages_and_links()
    {
        using var temp = new TempDirectory();
        var exports = Export(temp,
            (1, Md(Day1, "Alpha post\n\nAbout #SQL and #rare")),
            (2, Md(Day1.AddDays(1), "Beta post\n\nMore #sql")),
            (3, Md(Day1.AddDays(2), "Gamma post\n\nNothing tagged")));
        var site = temp.Combine("site");

        var result = await Builder().BuildAsync(exports, site);

        Assert.Equal(1, result.TagPages);
        var tagPage = File.ReadAllText(Path.Combine(site, "tag-sql.html"));
        Assert.Contains("Alpha post", tagPage);
        Assert.Contains("Beta post", tagPage);
        Assert.DoesNotContain("Gamma post", tagPage);
        Assert.Contains("#SQL", tagPage);

        var post1 = File.ReadAllText(Path.Combine(site, "posts", "1.html"));
        Assert.Contains("href=\"../tag-sql.html\"", post1);
        Assert.DoesNotContain("tag-rare", post1);
        Assert.DoesNotContain("tag-sql.html", File.ReadAllText(Path.Combine(site, "posts", "3.html")));
        Assert.Contains("https://blog.test/tag-sql.html", File.ReadAllText(Path.Combine(site, "sitemap.xml")));
    }

    [Fact]
    public async Task Persian_tags_produce_working_encoded_links()
    {
        using var temp = new TempDirectory();
        var exports = Export(temp,
            (1, Md(Day1, "اول\n\nمتن #معرفی")),
            (2, Md(Day1.AddDays(1), "دوم\n\nمتن #معرفی")));
        var site = temp.Combine("site");

        await Builder().BuildAsync(exports, site);

        Assert.True(File.Exists(Path.Combine(site, "tag-معرفی.html")));
        Assert.Contains("href=\"../tag-%D9%85%D8%B9%D8%B1%D9%81%DB%8C.html\"", File.ReadAllText(Path.Combine(site, "posts", "1.html")));
        Assert.Contains("https://blog.test/tag-%D9%85%D8%B9%D8%B1%D9%81%DB%8C.html", File.ReadAllText(Path.Combine(site, "sitemap.xml")));
    }

    [Fact]
    public async Task Tag_pages_can_be_turned_off_and_stale_ones_are_removed()
    {
        using var temp = new TempDirectory();
        var exports = Export(temp,
            (1, Md(Day1, "One\n\n#sql")),
            (2, Md(Day1.AddDays(1), "Two\n\n#sql")));
        var site = temp.Combine("site");

        await Builder().BuildAsync(exports, site);
        Assert.True(File.Exists(Path.Combine(site, "tag-sql.html")));

        var off = new StaticSiteConfig { TemplateName = "en", GenerateTagPages = false };
        var result = await Builder(off).BuildAsync(exports, site);

        Assert.Equal(0, result.TagPages);
        Assert.False(File.Exists(Path.Combine(site, "tag-sql.html")));
        Assert.DoesNotContain("post-tags", File.ReadAllText(Path.Combine(site, "posts", "1.html")));
    }

    [Fact]
    public async Task A_feed_is_generated_and_advertised_when_a_base_url_is_set()
    {
        using var temp = new TempDirectory();
        var exports = Export(temp, (1, Md(Day1, "One\n\nBody")), (2, Md(Day1.AddDays(1), "Two\n\nBody")));
        var site = temp.Combine("site");

        var result = await Builder().BuildAsync(exports, site);

        Assert.Equal(Path.Combine(site, "feed.xml"), result.FeedPath);
        Assert.Equal(2, XDocument.Load(result.FeedPath!).Root!.Elements(XName.Get("entry", "http://www.w3.org/2005/Atom")).Count());
        Assert.Contains("type=\"application/atom+xml\"", File.ReadAllText(result.IndexPath));
        Assert.Contains("https://blog.test/feed.xml", File.ReadAllText(Path.Combine(site, "posts", "1.html")));
    }

    [Fact]
    public async Task No_feed_without_a_base_url_or_when_disabled_and_old_feeds_are_removed()
    {
        using var temp = new TempDirectory();
        var exports = Export(temp, (1, Md(Day1, "One\n\nBody")));
        var site = temp.Combine("site");
        await Builder().BuildAsync(exports, site);
        Assert.True(File.Exists(Path.Combine(site, "feed.xml")));

        var disabled = new StaticSiteConfig { TemplateName = "en", SiteBaseUrl = "https://blog.test", FeedItemCount = 0 };
        var result = await Builder(disabled).BuildAsync(exports, site);

        Assert.Null(result.FeedPath);
        Assert.False(File.Exists(Path.Combine(site, "feed.xml")));
        Assert.DoesNotContain("atom+xml", File.ReadAllText(result.IndexPath));
    }

    [Fact]
    public async Task Forward_and_reply_notes_and_media_players_appear_on_post_pages()
    {
        using var temp = new TempDirectory();
        var exports = Export(temp,
            (1, Md(Day1, "🎬 **Video:** [Click to view v.mp4](./media/1_v.mp4)\n\nWatch it\n\n🎵 **Audio:** [Click to play a.mp3](./media/1_a.mp3)", "**Forwarded from:** Some Channel\n**Reply to:** 99\n")));
        var site = temp.Combine("site");

        await Builder().BuildAsync(exports, site);

        var html = File.ReadAllText(Path.Combine(site, "posts", "1.html"));
        Assert.Contains("Forwarded from Some Channel", html);
        Assert.Contains("href=\"99.html\"", html);
        Assert.Contains("<video controls", html);
        Assert.Contains("src=\"../media/1_v.mp4\"", html);
        Assert.Contains("<audio controls", html);
        Assert.Contains("src=\"../media/1_a.mp3\"", html);
    }

    [Fact]
    public void Players_are_only_added_for_local_media()
    {
        var html = Telescribe.Core.Site.PostHtmlRenderer.Render("**Created:** 2024-01-01 00:00:00 UTC\n\n[remote](https://example.com/a.mp4)");

        Assert.DoesNotContain("<video", html);
    }

    [Fact]
    public async Task Cleaning_removes_a_generated_site_but_refuses_foreign_folders()
    {
        using var temp = new TempDirectory();
        var exports = Export(temp, (1, Md(Day1, "One\n\nBody")));
        var site = temp.Combine("site");
        await Builder().BuildAsync(exports, site);
        Assert.True(File.Exists(Path.Combine(site, SiteBuilder.MarkerFileName)));

        SiteBuilder.CleanSiteDirectory(site);
        Assert.False(Directory.Exists(site));

        var foreign = temp.Combine("documents");
        Directory.CreateDirectory(foreign);
        File.WriteAllText(Path.Combine(foreign, "thesis.docx"), "important");
        Assert.Throws<InvalidOperationException>(() => SiteBuilder.CleanSiteDirectory(foreign));
        Assert.True(File.Exists(Path.Combine(foreign, "thesis.docx")));

        // Empty and missing folders are fine.
        var empty = temp.Combine("empty");
        Directory.CreateDirectory(empty);
        SiteBuilder.CleanSiteDirectory(empty);
        SiteBuilder.CleanSiteDirectory(temp.Combine("never-existed"));
    }

    [Fact]
    public void The_output_folder_defaults_to_site_inside_the_exports_folder()
    {
        Assert.Equal(Path.Combine("exports", "site"), SiteBuilder.ResolveSiteDirectory(new StaticSiteConfig(), "exports"));
        Assert.Equal("public", SiteBuilder.ResolveSiteDirectory(new StaticSiteConfig { OutputDirectory = "public" }, "exports"));
    }

    [Fact]
    public async Task Edited_posts_report_their_edit_time_as_updated()
    {
        using var temp = new TempDirectory();
        var exports = Export(temp, (1, Md(Day1, "One\n\nBody", "**Edited:** 2024-11-01 08:00:00 UTC\n")));
        var site = temp.Combine("site");

        await Builder().BuildAsync(exports, site);

        Assert.Contains("2024-11-01T08:00:00Z", File.ReadAllText(Path.Combine(site, "feed.xml")));
    }
}

public class ReactionLabelTests
{
    [Fact]
    public void Reactions_are_grouped_by_emoji_with_special_labels()
    {
        var totals = ReactionLabels.Count(
        [
            (new TL.ReactionEmoji { emoticon = "👍" }, 5),
            (new TL.ReactionEmoji { emoticon = "👍" }, 2),
            (new TL.ReactionEmoji { emoticon = "🔥" }, 1),
            (new TL.ReactionCustomEmoji { document_id = 1 }, 3),
            (new TL.ReactionCustomEmoji { document_id = 2 }, 4),
            (new TL.ReactionPaid(), 9)
        ]);

        Assert.Equal(7, totals["👍"]);
        Assert.Equal(1, totals["🔥"]);
        Assert.Equal(7, totals[ReactionLabels.CustomEmoji]);
        Assert.Equal(9, totals[ReactionLabels.Paid]);
    }
}
