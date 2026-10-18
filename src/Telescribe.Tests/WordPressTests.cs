using System.Text.Json;
using Telescribe.Core.Export;
using Telescribe.Core.Models;
using Telescribe.Core.Services;

namespace Telescribe.Tests;

public class ExportedPostTests
{
    private const string Header = "**Created:** 2024-05-06 07:08:09 UTC\n**Post Type:** Text\n**Views:** {0}\n**Reactions:** 1\n**Forwards:** 0\n\n";

    private static string Markdown(int views = 10, string body = "Title line\n\nBody text") => string.Format(Header, views) + body + "\n";

    [Fact]
    public void Fields_are_parsed_from_the_file()
    {
        const string markdown = "# Generated\n\n**Created:** 2024-05-06 07:08:09 UTC\n**Edited:** 2024-05-07 01:02:03 UTC\n**Views:** 1\n**Reactions:** 0\n**Forwards:** 0\n**LLM Processed:** Yes (x)\n**Tags:** #a #b\n\n![p](./media/1_photo.jpg)\n\nBody [clip](./media/2_my%20clip.mp4)\n";

        var post = ExportedPost.Parse(1, markdown);

        Assert.Equal("Generated", post.Title);
        Assert.Equal(new DateTime(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc), post.CreatedAt);
        Assert.Equal(new DateTime(2024, 5, 7, 1, 2, 3, DateTimeKind.Utc), post.EditedAt);
        Assert.Equal(["a", "b"], post.Tags);
        Assert.Equal(["1_photo.jpg", "2_my clip.mp4"], post.MediaFiles);
    }

    [Fact]
    public void The_hash_ignores_statistics_but_not_content()
    {
        var baseline = ExportedPost.Parse(1, Markdown(views: 10)).ContentHash;

        Assert.Equal(baseline, ExportedPost.Parse(1, Markdown(views: 99999)).ContentHash);
        Assert.NotEqual(baseline, ExportedPost.Parse(1, Markdown(body: "Title line\n\nEdited body")).ContentHash);
    }

    [Fact]
    public async Task Loading_reads_numeric_files_oldest_first_and_ignores_the_rest()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(temp.Combine("2.md"), "**Created:** 2024-02-01 00:00:00 UTC\n\nSecond");
        File.WriteAllText(temp.Combine("1.md"), "**Created:** 2024-01-01 00:00:00 UTC\n\nFirst");
        File.WriteAllText(temp.Combine("notes.md"), "not a post");
        File.WriteAllText(temp.Combine("3.txt"), "not markdown");

        var posts = await ExportedPost.LoadAllAsync(temp.Path);

        Assert.Equal([1L, 2L], posts.Select(p => p.Id));
    }
}

public class WordPressSupportTests
{
    private static ExportedPost Post(long id, string body = "Title\n\nBody", int views = 1) =>
        ExportedPost.Parse(id, $"**Created:** 2024-01-01 00:00:00 UTC\n**Views:** {views}\n**Reactions:** 0\n**Forwards:** 0\n\n{body}\n");

    [Fact]
    public void New_posts_are_created_changed_ones_updated_and_unchanged_ones_skipped()
    {
        var unchanged = Post(1);
        var changed = Post(2, "Title\n\nNew body");
        var fresh = Post(3);
        var state = new WordPressState();
        state.Posts[1] = new WordPressPostMapping { TelegramPostId = 1, WordPressPostId = 10, ContentHash = unchanged.ContentHash };
        state.Posts[2] = new WordPressPostMapping { TelegramPostId = 2, WordPressPostId = 20, ContentHash = "old" };

        var plan = WordPressSyncPlanner.Plan([unchanged, changed, fresh], state);

        Assert.Equal([WordPressAction.Skip, WordPressAction.Update, WordPressAction.Create], plan.Select(p => p.Action));
    }

    [Fact]
    public void Statistics_changes_alone_do_not_trigger_an_update()
    {
        var state = new WordPressState();
        state.Posts[1] = new WordPressPostMapping { WordPressPostId = 10, ContentHash = Post(1, views: 1).ContentHash };

        var plan = WordPressSyncPlanner.Plan([Post(1, views: 5000)], state);

        Assert.Equal(WordPressAction.Skip, plan[0].Action);
    }

    [Fact]
    public void Force_updates_everything_that_exists()
    {
        var state = new WordPressState();
        state.Posts[1] = new WordPressPostMapping { WordPressPostId = 10, ContentHash = Post(1).ContentHash };

        var plan = WordPressSyncPlanner.Plan([Post(1), Post(2)], state, force: true);

        Assert.Equal([WordPressAction.Update, WordPressAction.Create], plan.Select(p => p.Action));
    }

    [Theory]
    [InlineData("Hello, World! It's SQL", 7, "hello-world-it-s-sql-tg-7")]
    [InlineData("   ---  ", 9, "telegram-9")]
    [InlineData("معرفی کتاب", 12, "telegram-12")]
    [InlineData("Book: معرفی T-SQL", 3, "book-t-sql-tg-3")]
    public void Slugs_are_ascii_and_unique_per_post(string title, long id, string expected)
    {
        Assert.Equal(expected, WordPressContent.Slug(title, id));
    }

    [Fact]
    public void Long_titles_make_short_slugs_without_trailing_hyphens()
    {
        var slug = WordPressContent.Slug(string.Join(' ', Enumerable.Repeat("word", 30)), 1);

        Assert.EndsWith("-tg-1", slug);
        Assert.DoesNotContain("--", slug);
        Assert.True(slug.Length <= 40 + "-tg-1".Length);
    }

    [Fact]
    public void Very_long_titles_are_shortened()
    {
        var post = Post(1, new string('x', 400));

        Assert.True(WordPressContent.Title(post).Length <= 120);
    }

    [Fact]
    public void Html_uses_uploaded_media_urls_and_drops_the_header()
    {
        var post = Post(1, "Title\n\n![1_photo.jpg](./media/1_photo.jpg)\n\nCaption with [link](https://example.com)");
        var urls = new Dictionary<string, string> { ["1_photo.jpg"] = "https://wp.test/uploads/1_photo.jpg" };

        var html = WordPressContent.ToHtml(post, urls);

        Assert.Contains("src=\"https://wp.test/uploads/1_photo.jpg\"", html);
        Assert.Contains("href=\"https://example.com\"", html);
        Assert.DoesNotContain("Created", html);
        Assert.DoesNotContain("Views", html);
    }

    [Fact]
    public void Html_matches_percent_encoded_media_names_and_leaves_unknown_ones_alone()
    {
        var post = Post(1, "Title\n\n[doc](./media/5_my%20file.pdf) and [other](./media/6_x.pdf)");
        var urls = new Dictionary<string, string> { ["5_my file.pdf"] = "https://wp.test/up/my-file.pdf" };

        var html = WordPressContent.ToHtml(post, urls);

        Assert.Contains("href=\"https://wp.test/up/my-file.pdf\"", html);
        Assert.Contains("href=\"./media/6_x.pdf\"", html);
    }

    [Fact]
    public void Html_is_sanitised_like_the_site()
    {
        var html = WordPressContent.ToHtml(Post(1, "Title\n\n<script>alert(1)</script> [x](javascript:alert(1))"), new Dictionary<string, string>());

        Assert.DoesNotContain("<script", html);
        Assert.DoesNotContain("href=", html); // no link survived
    }

    [Fact]
    public void State_round_trips_and_media_names_ignore_case()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("state.json");
        var state = new WordPressState();
        state.Posts[4] = new WordPressPostMapping { TelegramPostId = 4, WordPressPostId = 44, ContentHash = "h" };
        state.Media["A.jpg"] = new WordPressMediaItem { Id = 7, Url = "https://wp.test/a.jpg" };
        state.Save(path);

        var loaded = WordPressState.Load(path);

        Assert.Equal(44, loaded.Posts[4].WordPressPostId);
        Assert.Equal(7, loaded.Media["a.JPG"].Id);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void Older_mapping_files_are_imported_when_there_is_no_state_yet()
    {
        using var temp = new TempDirectory();
        var legacy = temp.Combine("wordpress_mappings.json");
        File.WriteAllText(legacy, JsonSerializer.Serialize(new Dictionary<long, WordPressPostMapping>
        {
            [5] = new() { TelegramPostId = 5, WordPressPostId = 99 }
        }));

        var state = WordPressState.Load(temp.Combine("state.json"), temp.Combine("missing.json"), legacy);

        Assert.Equal(99, state.Posts[5].WordPressPostId);
    }

    [Fact]
    public void A_corrupt_state_file_is_reported_not_silently_replaced()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("state.json");
        File.WriteAllText(path, "{ not json");

        // Replacing it with an empty state would republish every post as a duplicate.
        Assert.Throws<InvalidOperationException>(() => WordPressState.Load(path));
    }

    [Fact]
    public void Without_any_file_the_state_is_empty()
    {
        using var temp = new TempDirectory();

        var state = WordPressState.Load(temp.Combine("state.json"));

        Assert.Empty(state.Posts);
        Assert.Empty(state.Media);
    }
}
