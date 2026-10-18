using System.Net;

namespace Telescribe.Core.Templating;

/// <summary>
/// A value substituted into a <c>{{placeholder}}</c>. Plain strings are HTML-escaped when rendered;
/// use <see cref="Raw"/> for fragments that are already trusted HTML (or JSON destined for a script block).
/// </summary>
public readonly record struct TemplateValue
{
    private TemplateValue(string value, bool isRaw)
    {
        Value = value ?? string.Empty;
        IsRaw = isRaw;
    }

    public string Value { get; }

    public bool IsRaw { get; }

    public static TemplateValue Text(string? value) => new(value ?? string.Empty, isRaw: false);

    public static TemplateValue Raw(string? value) => new(value ?? string.Empty, isRaw: true);

    public static implicit operator TemplateValue(string? value) => Text(value);

    public string Render() => IsRaw ? Value : WebUtility.HtmlEncode(Value);
}
