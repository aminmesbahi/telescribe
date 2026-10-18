using Telescribe.Core.Templating;

namespace Telescribe.Tests;

public class TemplateEngineTests
{
    private static string Render(string template, params (string Key, TemplateValue Value)[] values) =>
        TemplateEngine.Apply(template, values.ToDictionary(v => v.Key, v => v.Value));

    [Fact]
    public void Plain_values_are_html_escaped()
    {
        var html = Render("<h1>{{title}}</h1>", ("title", "<img src=x onerror=alert(1)> & \"q\""));

        Assert.Equal("<h1>&lt;img src=x onerror=alert(1)&gt; &amp; &quot;q&quot;</h1>", html);
    }

    [Fact]
    public void Raw_values_are_inserted_verbatim()
    {
        var html = Render("<div>{{content}}</div>", ("content", TemplateValue.Raw("<p>ok</p>")));

        Assert.Equal("<div><p>ok</p></div>", html);
    }

    [Fact]
    public void Substituted_content_is_not_scanned_for_further_placeholders()
    {
        var html = Render("{{content}}|{{views}}",
            ("content", TemplateValue.Raw("{{views}}")),
            ("views", "42"));

        Assert.Equal("{{views}}|42", html);
    }

    [Fact]
    public void Unknown_placeholders_are_left_untouched()
    {
        Assert.Equal("a {{missing}} b", Render("a {{missing}} b", ("other", "x")));
    }

    [Fact]
    public void Templates_are_loaded_from_disk_and_missing_ones_throw()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(temp.Combine("page.html"), "Hello {{name}}");
        var engine = new TemplateEngine(temp.Path);

        Assert.Equal("Hello World", engine.Render("page.html", new Dictionary<string, TemplateValue> { ["name"] = "World" }));
        Assert.True(engine.Exists("page.html"));
        Assert.False(engine.Exists("nope.html"));
        Assert.Throws<FileNotFoundException>(() => engine.Render("nope.html", new Dictionary<string, TemplateValue>()));
    }

    [Fact]
    public void Template_info_falls_back_on_template_name_and_honours_template_json()
    {
        using var temp = new TempDirectory();

        Assert.Equal("fa", TemplateInfo.Load("fa", temp.Path).Language);
        Assert.Equal("rtl", TemplateInfo.Load("fa", temp.Path).Direction);
        Assert.Equal("en", TemplateInfo.Load("en", temp.Path).Language);

        File.WriteAllText(temp.Combine("template.json"), """{ "language": "fa", "openGraphLocale": "fa_IR", "aboutTitleFormat": "About {0}!" }""");
        var info = TemplateInfo.Load("custom", temp.Path);

        Assert.Equal("fa", info.Language);
        Assert.Equal("About Me!", info.FormatAboutTitle("Me"));
    }
}
