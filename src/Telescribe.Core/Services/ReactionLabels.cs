using TL;

namespace Telescribe.Core.Services;

internal static class ReactionLabels
{
    public const string CustomEmoji = "custom emoji";
    public const string Paid = "⭐ paid";

    /// <summary>Sums reaction counts per emoji. Custom emoji and paid stars have no stable glyph, so they are grouped.</summary>
    public static Dictionary<string, int> Count(IEnumerable<(Reaction Reaction, int Count)> reactions)
    {
        var totals = new Dictionary<string, int>();
        foreach (var (reaction, count) in reactions)
        {
            var label = reaction switch
            {
                ReactionEmoji emoji => emoji.emoticon,
                ReactionCustomEmoji => CustomEmoji,
                ReactionPaid => Paid,
                _ => "other"
            };

            totals[label] = totals.GetValueOrDefault(label) + count;
        }

        return totals;
    }
}
