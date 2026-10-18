using System.Text;

namespace Telescribe.Core.Export;

/// <summary>Makes file names that come from Telegram safe to use on disk and inside Markdown links.</summary>
public static class MediaFileName
{
    private const int MaxLength = 120;

    /// <summary>
    /// Reduces an uploader-supplied name to a plain file name: no directory parts, no characters that are
    /// invalid on common file systems, and no leading/trailing dots or spaces.
    /// </summary>
    public static string Sanitize(string? name, string fallback = "file")
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return fallback;
        }

        // Both separators, regardless of the OS we happen to run on.
        var lastSeparator = name.LastIndexOfAny(['/', '\\']);
        var leaf = lastSeparator >= 0 ? name[(lastSeparator + 1)..] : name;

        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(leaf.Length);
        foreach (var c in leaf)
        {
            builder.Append(char.IsControl(c) || Array.IndexOf(invalid, c) >= 0 || c is ':' or '*' or '?' or '"' or '<' or '>' or '|'
                ? '_'
                : c);
        }

        var cleaned = builder.ToString().Trim().Trim('.').Trim();
        if (cleaned.Length == 0)
        {
            return fallback;
        }

        if (cleaned.Length > MaxLength)
        {
            var extension = Path.GetExtension(cleaned);
            if (extension.Length > 16)
            {
                extension = string.Empty;
            }

            var stemLength = MaxLength - extension.Length;
            if (char.IsHighSurrogate(cleaned[stemLength - 1]))
            {
                stemLength--;
            }

            cleaned = cleaned[..stemLength] + extension;
        }

        return cleaned;
    }

    /// <summary>
    /// The relative URL used in exported Markdown. Characters that would end a Markdown link destination
    /// early (spaces, parentheses, ...) are percent-encoded; everything else is left readable.
    /// </summary>
    public static string ToMarkdownUrl(string fileName)
    {
        var builder = new StringBuilder("./media/");
        foreach (var c in fileName)
        {
            if (c is ' ' or '(' or ')' or '[' or ']' or '<' or '>' or '%' or '#' or '?' or '\\' or '"' or '\'')
            {
                builder.Append('%').Append(((int)c).ToString("X2"));
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }
}
