using System.Text;
using System.Text.RegularExpressions;
using Telescribe.Core.Models;
using TL;
using WTelegram;

namespace Telescribe.Core.Services;

public partial class TelegramService
{
    private string ProcessMessageEntities(string text, MessageEntity[]? entities)
    {
        if (string.IsNullOrEmpty(text) || entities == null || entities.Length == 0)
            return text;

        var entityRanges = new List<(int offset, int length, string replacement)>();
        var processedRanges = new HashSet<(int, int)>();

        // Phase 1: Process blockquotes first (structural elements that modify line prefixes)
        foreach (var entity in entities.OrderByDescending(e => e.offset))
        {
            if (entity is not MessageEntityBlockquote) continue;
            if (entity.offset < 0 || entity.length <= 0 || entity.offset + entity.length > text.Length) continue;

            var entityText = text.Substring(entity.offset, entity.length);
            var lines = entityText.Split('\n');
            var replacement = string.Join("\n", lines.Select(l => "> " + l));

            processedRanges.Add((entity.offset, entity.length));
            entityRanges.Add((entity.offset, entity.length, replacement));
        }

        // Phase 2: Process remaining inline entities with priority ordering
        var remainingEntities = entities
            .Where(e => e is not MessageEntityBlockquote)
            .OrderBy(e => e switch
            {
                MessageEntityTextUrl => 0,
                MessageEntityUrl => 0,
                MessageEntityPre => 1,
                MessageEntityCode => 2,
                _ => 3
            })
            .ThenBy(e => e.offset)
            .ToList();

        foreach (var entity in remainingEntities)
        {
            if (entity.offset < 0 || entity.length <= 0 || entity.offset + entity.length > text.Length) continue;

            // Skip if this range overlaps with a higher-priority entity or blockquote
            if (processedRanges.Any(range =>
                (entity.offset >= range.Item1 && entity.offset < range.Item1 + range.Item2) ||
                (entity.offset + entity.length > range.Item1 && entity.offset + entity.length <= range.Item1 + range.Item2) ||
                (entity.offset <= range.Item1 && entity.offset + entity.length >= range.Item1 + range.Item2)))
            {
                continue;
            }

            var entityText = text.Substring(entity.offset, entity.length);
            string? replacement = null;

            switch (entity)
            {
                case MessageEntityTextUrl textUrl:
                    replacement = $"[{entityText}]({textUrl.url})";
                    processedRanges.Add((entity.offset, entity.length));
                    break;

                case MessageEntityUrl:
                    replacement = $"[{entityText}]({entityText})";
                    processedRanges.Add((entity.offset, entity.length));
                    break;

                case MessageEntityBold:
                    replacement = $"**{entityText}**";
                    break;

                case MessageEntityItalic:
                    replacement = $"*{entityText}*";
                    break;

                case MessageEntityUnderline:
                    replacement = $"<u>{entityText}</u>";
                    break;

                case MessageEntityStrike:
                    replacement = $"~~{entityText}~~";
                    break;

                case MessageEntitySpoiler:
                    replacement = $"||{entityText}||";
                    break;

                case MessageEntityCode:
                    replacement = $"`{entityText}`";
                    processedRanges.Add((entity.offset, entity.length));
                    break;

                case MessageEntityPre pre:
                    var language = !string.IsNullOrEmpty(pre.language) ? pre.language : "";
                    replacement = $"```{language}\n{entityText}\n```";
                    processedRanges.Add((entity.offset, entity.length));
                    break;

                case MessageEntityEmail:
                    replacement = $"[{entityText}](mailto:{entityText})";
                    break;

                case MessageEntityPhone:
                    replacement = $"[{entityText}](tel:{entityText})";
                    break;

                case MessageEntityMention:
                case MessageEntityHashtag:
                case MessageEntityCashtag:
                case MessageEntityBotCommand:
                case MessageEntityBankCard:
                case MessageEntityMentionName:
                case MessageEntityUnknown:
                    replacement = entityText;
                    break;

                case MessageEntityCustomEmoji:
                    replacement = entityText;
                    break;

                case MessageEntityFormattedDate:
                    replacement = entityText;
                    break;

                case MessageEntityDiffInsert:
                    replacement = $"<ins>{entityText}</ins>";
                    break;

                case MessageEntityDiffReplace diffReplace:
                    replacement = $"<del>{diffReplace.old_text}</del><ins>{entityText}</ins>";
                    processedRanges.Add((entity.offset, entity.length));
                    break;

                case MessageEntityDiffDelete:
                    replacement = $"<del>{entityText}</del>";
                    break;
            }

            if (replacement != null)
            {
                entityRanges.Add((entity.offset, entity.length, replacement));
            }
        }

        // Sort by offset descending to process from end to start (prevents offset shifts)
        entityRanges = entityRanges.OrderByDescending(e => e.offset).ToList();

        foreach (var (offset, length, replacement) in entityRanges)
        {
            text = text.Remove(offset, length);
            text = text.Insert(offset, replacement);
        }

        return text;
    }
}
