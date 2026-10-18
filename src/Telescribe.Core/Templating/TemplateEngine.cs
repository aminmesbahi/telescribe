using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Telescribe.Core.Templating;

/// <summary>
/// Minimal <c>{{placeholder}}</c> engine. Substitution is single-pass, so substituted content is never
/// re-scanned for placeholders, and plain values are HTML-escaped by default.
/// </summary>
public sealed partial class TemplateEngine
{
    private readonly string _templatesPath;
    private readonly ConcurrentDictionary<string, string> _cache = new(StringComparer.OrdinalIgnoreCase);

    public TemplateEngine(string templatesPath)
    {
        _templatesPath = templatesPath ?? throw new ArgumentNullException(nameof(templatesPath));
    }

    public string TemplatesPath => _templatesPath;

    public bool Exists(string templateName) => File.Exists(Path.Combine(_templatesPath, templateName));

    public string Render(string templateName, IReadOnlyDictionary<string, TemplateValue> values)
    {
        return Apply(Load(templateName), values);
    }

    public static string Apply(string template, IReadOnlyDictionary<string, TemplateValue> values)
    {
        return PlaceholderRegex().Replace(template, match =>
            values.TryGetValue(match.Groups[1].Value, out var value) ? value.Render() : match.Value);
    }

    private string Load(string templateName)
    {
        return _cache.GetOrAdd(templateName, name =>
        {
            var path = Path.Combine(_templatesPath, name);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"Template not found: {path}", path);
            }

            return File.ReadAllText(path);
        });
    }

    [GeneratedRegex(@"\{\{(\w+)\}\}")]
    private static partial Regex PlaceholderRegex();
}
