using System.Globalization;

namespace Telescribe.Core.Export;

/// <summary>
/// A user-supplied channel identifier, normalised. Accepts <c>@name</c>, <c>name</c>, <c>t.me/name</c>,
/// <c>https://t.me/name</c>, a numeric id, or a Bot-API style <c>-100&lt;id&gt;</c> id; anything else is
/// treated as a title to search for.
/// </summary>
public sealed record ChannelReference(string? Username, long? Id, string? Title)
{
    public static ChannelReference Parse(string? input)
    {
        var text = (input ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return new ChannelReference(null, null, null);
        }

        foreach (var prefix in new[] { "https://t.me/", "http://t.me/", "https://telegram.me/", "t.me/", "telegram.me/" })
        {
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                text = text[prefix.Length..];
                var end = text.IndexOfAny(['/', '?', '#']);
                if (end >= 0)
                {
                    text = text[..end];
                }

                break;
            }
        }

        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric))
        {
            return new ChannelReference(null, NormalizeId(numeric), null);
        }

        var username = text.TrimStart('@');
        return IsValidUsername(username)
            ? new ChannelReference(username, null, text)
            : new ChannelReference(null, null, text);
    }

    /// <summary>Bot-API ids look like -1001234567890; Telegram's own channel id is 1234567890.</summary>
    private static long NormalizeId(long id)
    {
        var text = id.ToString(CultureInfo.InvariantCulture);
        if (text.StartsWith("-100", StringComparison.Ordinal) && text.Length > 4)
        {
            return long.Parse(text[4..], CultureInfo.InvariantCulture);
        }

        return Math.Abs(id);
    }

    // Telegram usernames: 5-32 characters, letters, digits and underscores, starting with a letter.
    private static bool IsValidUsername(string value) =>
        value.Length is >= 4 and <= 32 &&
        char.IsAsciiLetter(value[0]) &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
}
