using System.Text.Json;
using System.Text.Json.Serialization;
using Telescribe.Core.Models;

namespace Telescribe.Core.Export;

/// <summary>One post's counters at one point in time.</summary>
public sealed record StatsSnapshot(
    [property: JsonPropertyName("t")] DateTime Time,
    [property: JsonPropertyName("id")] long PostId,
    [property: JsonPropertyName("v")] int Views,
    [property: JsonPropertyName("r")] int Reactions,
    [property: JsonPropertyName("f")] int Forwards);

/// <summary>
/// An append-only log (<c>stats-history.jsonl</c>) of views, reactions and forwards. Telegram only reports current
/// totals, so keeping a snapshot at every export or update is the only way to chart growth over time.
/// </summary>
public static class StatsHistory
{
    public static string DefaultPath(string exportDirectory) => Path.Combine(exportDirectory, "stats-history.jsonl");

    public static IReadOnlyList<StatsSnapshot> Load(string path)
    {
        var snapshots = new List<StatsSnapshot>();
        if (!File.Exists(path))
        {
            return snapshots;
        }

        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                if (JsonSerializer.Deserialize<StatsSnapshot>(line) is { } snapshot)
                {
                    snapshots.Add(snapshot);
                }
            }
            catch (JsonException)
            {
                // A line cut short by an interrupted write; the rest of the log is still usable.
            }
        }

        return snapshots;
    }

    /// <summary>Appends a snapshot for every post whose counters differ from its last recorded values.</summary>
    /// <returns>The number of snapshots written.</returns>
    public static int AppendChanges(string path, DateTime time, IEnumerable<PostSummary> posts)
    {
        var latest = new Dictionary<long, StatsSnapshot>();
        foreach (var snapshot in Load(path))
        {
            latest[snapshot.PostId] = snapshot;
        }

        var lines = new List<string>();
        foreach (var post in posts)
        {
            if (latest.TryGetValue(post.PostId, out var last) &&
                last.Views == post.Views && last.Reactions == post.Reactions && last.Forwards == post.TotalForwards)
            {
                continue;
            }

            var snapshot = new StatsSnapshot(time, post.PostId, post.Views, post.Reactions, post.TotalForwards);
            latest[post.PostId] = snapshot;
            lines.Add(JsonSerializer.Serialize(snapshot));
        }

        if (lines.Count > 0)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.AppendAllLines(path, lines);
        }

        return lines.Count;
    }
}
