using Telescribe.Core.Site;

namespace Telescribe.Tests;

public class PostHtmlRendererTests
{
    private const string Header = "**Created:** 2024-01-01 00:00:00 UTC\n**Views:** 1\n\n";

    private static string Render(string body, string? title = null) => PostHtmlRenderer.Render(Header + body, title);

    [Fact]
    public void Header_lines_are_not_rendered()
    {
        var html = Render("Hello");

        Assert.DoesNotContain("Created", html);
        Assert.DoesNotContain("Views", html);
        Assert.Contains("Hello", html);
    }

    [Fact]
    public void Single_line_breaks_become_line_breaks()
    {
        var html = Render("line one\nline two");

        Assert.Contains("line one<br", html);
        Assert.Contains("line two", html);
    }

    [Fact]
    public void Raw_html_is_escaped()
    {
        var html = Render("<script>alert(1)</script>\n\nText <img src=x onerror=alert(1)> more");

        Assert.DoesNotContain("<script", html);
        Assert.DoesNotContain("<img", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("&lt;img", html);
    }

    [Fact]
    public void The_few_inline_tags_the_exporter_emits_are_kept()
    {
        var html = Render("This is <u>underlined</u> and <del>old</del><ins>new</ins>");

        Assert.Contains("<u>underlined</u>", html);
        Assert.Contains("<del>old</del>", html);
        Assert.Contains("<ins>new</ins>", html);
    }

    [Fact]
    public void Allowed_tags_cannot_carry_attributes()
    {
        var html = Render("<u onclick=alert(1)>x</u>");

        Assert.DoesNotContain("<u onclick", html);
    }

    [Theory]
    [InlineData("[click](javascript:alert(1))")]
    [InlineData("[click](JaVaScRiPt:alert(1))")]
    [InlineData("[click](data:text/html;base64,PHNjcmlwdD4=)")]
    [InlineData("[click](vbscript:msgbox(1))")]
    public void Dangerous_link_schemes_are_dropped_but_the_text_is_kept(string markdown)
    {
        var html = Render(markdown);

        Assert.DoesNotContain("href=", html);
        Assert.DoesNotContain("javascript:", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("click", html);
    }

    [Fact]
    public void Links_obfuscated_with_control_characters_are_dropped()
    {
        var url = "java" + (char)9 + "script:alert(1)";

        var html = Render($"[click](<{url}>)");

        Assert.DoesNotContain("href=", html);
    }

    [Fact]
    public void Regular_links_open_safely_in_a_new_tab()
    {
        var html = Render("[site](https://example.com/a)");

        Assert.Contains("href=\"https://example.com/a\"", html);
        Assert.Contains("target=\"_blank\"", html);
        Assert.Contains("rel=\"noopener noreferrer\"", html);
    }

    [Fact]
    public void Mailto_and_tel_links_are_kept()
    {
        var html = Render("[mail](mailto:a@example.com) [call](tel:+123)");

        Assert.Contains("href=\"mailto:a@example.com\"", html);
        Assert.Contains("href=\"tel:+123\"", html);
    }

    [Fact]
    public void Underscores_inside_urls_are_not_turned_into_emphasis()
    {
        var html = Render("[docs](https://example.com/a_b_c/d_e)");

        Assert.Contains("href=\"https://example.com/a_b_c/d_e\"", html);
        Assert.DoesNotContain("<em>", html);
    }

    [Fact]
    public void Snake_case_words_are_left_alone()
    {
        var html = Render("call my_function_name now");

        Assert.Contains("my_function_name", html);
        Assert.DoesNotContain("<em>", html);
    }

    [Fact]
    public void Images_point_at_the_shared_media_folder_and_load_lazily()
    {
        var html = Render("![102_photo.jpg](./media/102_photo.jpg)\n\nCaption");

        Assert.Contains("src=\"../media/102_photo.jpg\"", html);
        Assert.Contains("loading=\"lazy\"", html);
    }

    [Fact]
    public void Non_image_media_links_are_rewritten_too()
    {
        var html = Render("[Click to view clip.mp4](./media/5_clip.mp4)");

        Assert.Contains("href=\"../media/5_clip.mp4\"", html);
    }

    [Fact]
    public void Leading_images_are_kept()
    {
        var html = Render("![a](./media/a.jpg)\n\nText");

        Assert.Contains("<img", html);
    }

    [Fact]
    public void Percent_encoded_media_names_survive()
    {
        var html = Render("![x](./media/12_my%20file.jpg)");

        Assert.Contains("../media/12_my%20file.jpg", html);
    }

    [Fact]
    public void Headings_that_repeat_the_title_are_removed()
    {
        var html = Render("# My **Title**\n\nBody text\n\n## Other heading", title: "My Title");

        Assert.DoesNotContain("<h1", html);
        Assert.Contains("<h2", html);
        Assert.Contains("Other heading", html);
    }

    [Fact]
    public void Lists_quotes_code_and_tables_render()
    {
        const string body = "- one\n- two\n\n> quoted\n\n```csharp\nvar x = 1 < 2;\n```\n\n| a | b |\n|---|---|\n| 1 | 2 |";

        var html = Render(body);

        Assert.Contains("<ul>", html);
        Assert.Contains("<blockquote>", html);
        Assert.Contains("<code class=\"language-csharp\">", html);
        Assert.Contains("var x = 1 &lt; 2;", html);
        Assert.Contains("<table>", html);
    }

    [Fact]
    public void Generic_attribute_syntax_is_not_interpreted()
    {
        var html = Render("text {onmouseover=\"alert(1)\"}\n\n# heading {onclick=\"x()\"}");

        Assert.DoesNotContain("<p onmouseover", html);
        Assert.DoesNotContain("<h1 ", html);
    }

    [Fact]
    public void The_no_content_marker_becomes_a_notice()
    {
        var html = Render("[No content]");

        Assert.Contains("no text content", html);
    }

    [Fact]
    public void Persian_text_is_preserved()
    {
        var html = Render("**معرفی کتاب**\n\nاین کتاب خوب است");

        Assert.Contains("<strong>معرفی کتاب</strong>", html);
        Assert.Contains("این کتاب خوب است", html);
    }
}
