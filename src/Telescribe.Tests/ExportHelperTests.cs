using Telescribe.Core.Export;
using Telescribe.Core.Models;

namespace Telescribe.Tests;

public class ChannelReferenceTests
{
    [Theory]
    [InlineData("@mychannel", "mychannel")]
    [InlineData("mychannel", "mychannel")]
    [InlineData("t.me/mychannel", "mychannel")]
    [InlineData("https://t.me/mychannel", "mychannel")]
    [InlineData("https://t.me/mychannel/123?single", "mychannel")]
    [InlineData("  @MyChannel  ", "MyChannel")]
    public void Usernames_are_normalised(string input, string expected)
    {
        var reference = ChannelReference.Parse(input);

        Assert.Equal(expected, reference.Username);
        Assert.Null(reference.Id);
    }

    [Theory]
    [InlineData("1234567890", 1234567890L)]
    [InlineData("-1001234567890", 1234567890L)]
    [InlineData("-987654321", 987654321L)]
    public void Numeric_ids_are_normalised(string input, long expected)
    {
        var reference = ChannelReference.Parse(input);

        Assert.Equal(expected, reference.Id);
        Assert.Null(reference.Username);
    }

    [Theory]
    [InlineData("My Channel Title")]
    [InlineData("عنوان کانال")]
    public void Anything_else_is_a_title(string input)
    {
        var reference = ChannelReference.Parse(input);

        Assert.Null(reference.Username);
        Assert.Null(reference.Id);
        Assert.Equal(input, reference.Title);
    }

    [Fact]
    public void Empty_input_yields_an_empty_reference()
    {
        Assert.Equal(new ChannelReference(null, null, null), ChannelReference.Parse("  "));
        Assert.Equal(new ChannelReference(null, null, null), ChannelReference.Parse(null));
    }
}

public class MediaFileNameTests
{
    [Theory]
    [InlineData("..\\..\\evil.dll", "evil.dll")]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("a/b/c.txt", "c.txt")]
    [InlineData("C:\\Windows\\system32\\x.exe", "x.exe")]
    [InlineData("report?.pdf", "report_.pdf")]
    [InlineData("a:b*c.txt", "a_b_c.txt")]
    [InlineData("  spaced name.txt  ", "spaced name.txt")]
    [InlineData("...hidden", "hidden")]
    public void Names_are_reduced_to_a_plain_file_name(string input, string expected)
    {
        Assert.Equal(expected, MediaFileName.Sanitize(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("...")]
    [InlineData("/")]
    public void Unusable_names_use_the_fallback(string? input)
    {
        Assert.Equal("file", MediaFileName.Sanitize(input));
        Assert.Equal("fallback", MediaFileName.Sanitize(input, "fallback"));
    }

    [Fact]
    public void Persian_names_are_kept()
    {
        Assert.Equal("کتاب.pdf", MediaFileName.Sanitize("کتاب.pdf"));
    }

    [Fact]
    public void Very_long_names_are_shortened_but_keep_their_extension()
    {
        var name = MediaFileName.Sanitize(new string('x', 400) + ".pdf");

        Assert.True(name.Length <= 120);
        Assert.EndsWith(".pdf", name);
    }

    [Fact]
    public void A_sanitised_name_cannot_escape_the_media_folder()
    {
        using var temp = new TempDirectory();
        var media = Path.Combine(temp.Path, "exports", "media");
        Directory.CreateDirectory(media);

        foreach (var hostile in new[] { "../../outside.txt", "..\\..\\outside.txt", "sub/../../../outside.txt", "/abs/outside.txt" })
        {
            var fileName = "123_" + MediaFileName.Sanitize(hostile);
            var full = Path.GetFullPath(Path.Combine(media, fileName));

            Assert.Equal(media, Path.GetDirectoryName(full));
        }
    }

    [Theory]
    [InlineData("12_photo.jpg", "./media/12_photo.jpg")]
    [InlineData("12_my file.pdf", "./media/12_my%20file.pdf")]
    [InlineData("12_a(1).png", "./media/12_a%281%29.png")]
    [InlineData("12_100%.txt", "./media/12_100%25.txt")]
    [InlineData("12_کتاب.pdf", "./media/12_کتاب.pdf")]
    public void Markdown_urls_encode_only_what_breaks_links(string fileName, string expected)
    {
        Assert.Equal(expected, MediaFileName.ToMarkdownUrl(fileName));
    }
}

public class AlbumMergerTests
{
    private static TelegramPost Post(long id, string content = "", params string[] media) => new()
    {
        Id = id,
        CreatedAt = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(id),
        Content = content,
        MediaFiles = [.. media],
        Views = (int)id * 10,
        PostType = media.Length > 0 ? "photo" : "text"
    };

    private static List<TelegramPost> Feed(AlbumMerger merger, params (TelegramPost Post, long Group)[] items)
    {
        var completed = new List<TelegramPost>();
        foreach (var (post, group) in items)
        {
            completed.AddRange(merger.Add(post, group));
        }

        completed.AddRange(merger.Flush());
        return completed;
    }

    [Fact]
    public void Ungrouped_posts_pass_straight_through_in_order()
    {
        var result = Feed(new AlbumMerger(), (Post(3), 0), (Post(2), 0), (Post(1), 0));

        Assert.Equal([3L, 2L, 1L], result.Select(p => p.Id));
    }

    [Fact]
    public void An_album_becomes_one_post_with_the_oldest_id_and_all_media_in_order()
    {
        // Newest first, as the history API returns them; the caption sits on the first (oldest) item.
        var result = Feed(new AlbumMerger(),
            (Post(12, "", "12_photo.jpg"), 777),
            (Post(11, "", "11_photo.jpg"), 777),
            (Post(10, "Caption", "10_photo.jpg"), 777));

        var album = Assert.Single(result);
        Assert.Equal(10, album.Id);
        Assert.Equal(["10_photo.jpg", "11_photo.jpg", "12_photo.jpg"], album.MediaFiles);
        Assert.Equal("Caption", album.Content);
        Assert.Equal(Post(10).CreatedAt, album.CreatedAt);
        Assert.Equal(120, album.Views);
        Assert.Equal([12L, 11L, 10L], album.MergedMessageIds);
    }

    [Fact]
    public void Posts_around_an_album_are_not_merged_into_it()
    {
        var result = Feed(new AlbumMerger(),
            (Post(20, "after"), 0),
            (Post(12, "", "12.jpg"), 5),
            (Post(11, "cap", "11.jpg"), 5),
            (Post(10, "before"), 0));

        Assert.Equal([20L, 11L, 10L], result.Select(p => p.Id));
        Assert.Equal(["11.jpg", "12.jpg"], result[1].MediaFiles);
    }

    [Fact]
    public void Different_albums_stay_separate()
    {
        var result = Feed(new AlbumMerger(),
            (Post(6, "", "6.jpg"), 2),
            (Post(5, "b", "5.jpg"), 2),
            (Post(4, "", "4.jpg"), 1),
            (Post(3, "a", "3.jpg"), 1));

        Assert.Equal([5L, 3L], result.Select(p => p.Id));
    }

    [Fact]
    public void A_single_message_with_a_group_id_is_returned_unchanged()
    {
        var result = Feed(new AlbumMerger(), (Post(1, "solo", "1.jpg"), 9));

        var post = Assert.Single(result);
        Assert.Equal(1, post.Id);
        Assert.Equal(["1.jpg"], post.MediaFiles);
    }

    [Fact]
    public void Flush_on_an_empty_merger_returns_nothing()
    {
        Assert.Empty(new AlbumMerger().Flush());
    }

    [Fact]
    public void Edit_information_is_carried_over()
    {
        var newer = Post(2, "", "2.jpg");
        var older = Post(1, "c", "1.jpg");
        older.IsEdited = true;
        older.EditedAt = new DateTime(2025, 5, 5, 0, 0, 0, DateTimeKind.Utc);

        var album = Assert.Single(Feed(new AlbumMerger(), (newer, 3), (older, 3)));

        Assert.True(album.IsEdited);
        Assert.Equal(older.EditedAt, album.EditedAt);
    }
}
