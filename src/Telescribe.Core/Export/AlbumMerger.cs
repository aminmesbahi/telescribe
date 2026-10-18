using Telescribe.Core.Models;

namespace Telescribe.Core.Export;

/// <summary>
/// Telegram delivers an album (several photos/videos posted together) as one message per item, all sharing a
/// <c>grouped_id</c>. This merges such runs back into a single post. Messages are expected newest first, as
/// returned by the history API; the merged post takes the id and timestamp of the oldest message, which is
/// what Telegram shows as the album's first item.
/// </summary>
public sealed class AlbumMerger
{
    private TelegramPost? _pending;
    private long _pendingGroupId;

    /// <summary>Feeds the next (older) post. Returns any posts that are now complete.</summary>
    public IEnumerable<TelegramPost> Add(TelegramPost post, long groupedId)
    {
        if (groupedId != 0 && _pending is not null && _pendingGroupId == groupedId)
        {
            Merge(_pending, post);
            return [];
        }

        var completed = Flush();

        if (groupedId != 0)
        {
            _pending = post;
            _pendingGroupId = groupedId;
            _pending.MergedMessageIds.Add(post.Id);
            return completed;
        }

        return completed.Append(post);
    }

    /// <summary>Returns the album still being collected (if any) and resets the merger.</summary>
    public List<TelegramPost> Flush()
    {
        var result = new List<TelegramPost>();
        if (_pending is not null)
        {
            result.Add(_pending);
            _pending = null;
            _pendingGroupId = 0;
        }

        return result;
    }

    private static void Merge(TelegramPost target, TelegramPost older)
    {
        target.MergedMessageIds.Add(older.Id);

        // The older message comes first in the album.
        target.MediaFiles.InsertRange(0, older.MediaFiles.Where(f => !target.MediaFiles.Contains(f)));

        // Only one message of an album carries the caption.
        if (string.IsNullOrWhiteSpace(target.Content))
        {
            target.Content = older.Content;
        }
        else if (!string.IsNullOrWhiteSpace(older.Content) &&
                 !string.Equals(older.Content.Trim(), target.Content.Trim(), StringComparison.Ordinal))
        {
            target.Content = older.Content + "\n\n" + target.Content;
        }

        target.Id = older.Id;
        target.CreatedAt = older.CreatedAt;
        target.Views = Math.Max(target.Views, older.Views);
        target.Reactions = Math.Max(target.Reactions, older.Reactions);
        target.TotalForwards = Math.Max(target.TotalForwards, older.TotalForwards);

        if (older.IsEdited)
        {
            target.IsEdited = true;
            if (older.EditedAt > (target.EditedAt ?? DateTime.MinValue))
            {
                target.EditedAt = older.EditedAt;
            }
        }

        if (string.IsNullOrWhiteSpace(target.PostType) || target.PostType == "text")
        {
            target.PostType = older.PostType;
        }
    }
}
