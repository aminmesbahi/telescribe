using System.Text.Json;

namespace Telescribe.Core.Templating;

/// <summary>
/// Per-template metadata, read from an optional <c>template.json</c>. Templates without one fall back to
/// a name-based guess (names starting with "fa" are Persian/RTL, everything else English/LTR).
/// </summary>
public sealed record TemplateInfo(
    string Language,
    string Direction,
    string OpenGraphLocale,
    string AboutTitleFormat)
{
    private sealed class Dto
    {
        public string? Language { get; set; }
        public string? Direction { get; set; }
        public string? OpenGraphLocale { get; set; }
        public string? AboutTitleFormat { get; set; }
    }

    public static TemplateInfo English { get; } = new("en", "ltr", "en_US", "About {0}");

    public static TemplateInfo Persian { get; } = new("fa", "rtl", "fa_IR", "درباره {0}");

    public static TemplateInfo Load(string templateName, string templatePath)
    {
        var fallback = templateName.StartsWith("fa", StringComparison.OrdinalIgnoreCase) ? Persian : English;
        var metadataPath = Path.Combine(templatePath, "template.json");
        if (!File.Exists(metadataPath))
        {
            return fallback;
        }

        try
        {
            var dto = JsonSerializer.Deserialize<Dto>(
                File.ReadAllText(metadataPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip });

            if (dto is null)
            {
                return fallback;
            }

            return new TemplateInfo(
                dto.Language ?? fallback.Language,
                dto.Direction ?? fallback.Direction,
                dto.OpenGraphLocale ?? fallback.OpenGraphLocale,
                dto.AboutTitleFormat ?? fallback.AboutTitleFormat);
        }
        catch (JsonException)
        {
            return fallback;
        }
    }

    public string FormatAboutTitle(string siteTitle) => string.Format(AboutTitleFormat, siteTitle);
}
