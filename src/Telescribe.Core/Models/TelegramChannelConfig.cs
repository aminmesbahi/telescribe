using Telescribe.Core.Export;

namespace Telescribe.Core.Models;

public sealed class TelegramChannelConfig
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>
    /// Relative paths are resolved below <see cref="TelegramConfig.ExportDirectory"/>.
    /// Empty uses the channel's safe slug.
    /// </summary>
    public string ExportDirectory { get; set; } = "";

    /// <summary>Relative paths are resolved below this channel's export directory.</summary>
    public string SiteOutputDirectory { get; set; } = "";

    /// <summary>Relative paths are resolved below this channel's export directory.</summary>
    public string StatsHistoryPath { get; set; } = "";
}

public sealed record TelegramChannelTarget(
    int Index,
    string Id,
    string Name,
    string Slug,
    string ExportDirectory,
    string SiteDirectory,
    string StatsHistoryPath);

public static class TelegramChannels
{
    public static IReadOnlyList<TelegramChannelTarget> Resolve(TelegramConfig config)
    {
        var configured = config.Channels.Where(channel => !string.IsNullOrWhiteSpace(channel.Id)).ToList();
        if (configured.Count == 0 && !string.IsNullOrWhiteSpace(config.ChannelId))
        {
            configured.Add(new TelegramChannelConfig { Id = config.ChannelId });
        }

        var root = string.IsNullOrWhiteSpace(config.ExportDirectory) ? "./exports" : config.ExportDirectory;
        var slugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<TelegramChannelTarget>(configured.Count);

        for (var index = 0; index < configured.Count; index++)
        {
            var channel = configured[index];
            var reference = ChannelReference.Parse(channel.Id);
            var name = string.IsNullOrWhiteSpace(channel.Name)
                ? reference.Username ?? reference.Title ?? channel.Id
                : channel.Name.Trim();
            var slug = SafeSlug(channel.NameOrId(reference, name));
            if (!slugs.Add(slug))
            {
                throw new InvalidOperationException($"Channel '{channel.Id}' uses the duplicate export name '{slug}'. Set a unique Channels:Name.");
            }

            var exportDirectory = Resolve(root, channel.ExportDirectory, slug);
            var siteDirectory = string.IsNullOrWhiteSpace(channel.SiteOutputDirectory)
                ? ResolveDefaultSite(config.StaticSite.OutputDirectory, exportDirectory, slug)
                : ResolveRelative(exportDirectory, channel.SiteOutputDirectory);
            var statsPath = ResolveRelative(
                exportDirectory,
                string.IsNullOrWhiteSpace(channel.StatsHistoryPath) ? "stats-history.jsonl" : channel.StatsHistoryPath);

            result.Add(new TelegramChannelTarget(index + 1, channel.Id.Trim(), name, slug, exportDirectory, siteDirectory, statsPath));
        }

        return result;
    }

    public static TelegramChannelTarget Find(IReadOnlyList<TelegramChannelTarget> channels, string selector)
    {
        var match = channels.FirstOrDefault(channel =>
            string.Equals(channel.Id, selector, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(channel.Name, selector, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(channel.Slug, selector, StringComparison.OrdinalIgnoreCase) ||
            channel.Index.ToString() == selector);

        return match ?? throw new ArgumentException(
            $"Unknown channel '{selector}'. Available: {string.Join(", ", channels.Select(channel => channel.Name))}");
    }

    private static string Resolve(string root, string path, string fallback = "")
    {
        var value = string.IsNullOrWhiteSpace(path) ? fallback : path;
        return string.IsNullOrWhiteSpace(value)
            ? Path.GetFullPath(root)
            : ResolveRelative(root, value);
    }

    private static string ResolveDefaultSite(string configured, string exportDirectory, string slug)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return Path.Combine(exportDirectory, "site");
        }

        return Path.GetFullPath(Path.Combine(configured, slug));
    }

    private static string ResolveRelative(string root, string path) =>
        Path.IsPathRooted(path) ? Path.GetFullPath(path) : Path.GetFullPath(Path.Combine(root, path));

    private static string SafeSlug(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Trim().Select(ch => invalid.Contains(ch) || char.IsWhiteSpace(ch) ? '-' : ch).ToArray();
        var slug = string.Join("", chars).Trim('-', '.').Trim();
        while (slug.Contains("--", StringComparison.Ordinal))
        {
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        }

        return string.IsNullOrWhiteSpace(slug) ? "channel" : slug;
    }
}

file static class TelegramChannelConfigExtensions
{
    public static string NameOrId(this TelegramChannelConfig channel, ChannelReference reference, string name) =>
        string.IsNullOrWhiteSpace(channel.Name) ? reference.Username ?? reference.Id?.ToString() ?? name : channel.Name;
}
