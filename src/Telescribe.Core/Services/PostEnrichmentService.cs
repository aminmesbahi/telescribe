using System.Globalization;
using System.Text;
using System.Text.Json;
using Telescribe.Core.Export;
using Telescribe.Core.Models;

namespace Telescribe.Core.Services;

public sealed record EnrichmentOptions(bool Force = false, int Limit = 0);

/// <summary>
/// Adds AI-generated titles and hashtags to exported posts. The pristine export in <c>raw/</c> is the input;
/// the enriched copy is written to <c>processed/</c> and to the main export folder that the site builder reads.
/// Posts that were already enriched are skipped, so the command can be re-run (or interrupted) safely.
/// </summary>
public sealed class PostEnrichmentService
{
    private readonly ILlmService _llm;
    private readonly LlmConfig _config;
    private readonly string _exportDirectory;
    private readonly string? _baseHashtagsPath;
    private readonly Action<string> _log;

    public PostEnrichmentService(
        ILlmService llm,
        LlmConfig config,
        string exportDirectory,
        string? baseHashtagsPath = null,
        Action<string>? log = null)
    {
        _llm = llm;
        _config = config;
        _exportDirectory = exportDirectory;
        _baseHashtagsPath = baseHashtagsPath;
        _log = log ?? Console.WriteLine;
    }

    public async Task<LlmProcessingSummary> RunAsync(EnrichmentOptions options, CancellationToken cancellationToken = default)
    {
        var rawDirectory = Path.Combine(_exportDirectory, "raw");
        var processedDirectory = Path.Combine(_exportDirectory, "processed");
        Directory.CreateDirectory(processedDirectory);

        var rawFiles = (Directory.Exists(rawDirectory) ? Directory.GetFiles(rawDirectory, "*.md") : [])
            .Select(path => (Path: path, Id: long.TryParse(Path.GetFileNameWithoutExtension(path), NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : -1))
            .Where(file => file.Id >= 0)
            .OrderByDescending(file => file.Id)
            .ToList();

        var summary = new LlmProcessingSummary
        {
            TotalPosts = rawFiles.Count,
            Provider = _llm.Provider,
            ProcessingStarted = DateTime.UtcNow
        };

        var baseHashtags = await LoadBaseHashtagsAsync(cancellationToken);
        var processedThisRun = 0;

        foreach (var (rawPath, id) in rawFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (options.Limit > 0 && processedThisRun >= options.Limit)
            {
                summary.SkippedPosts++;
                continue;
            }

            var processedPath = Path.Combine(processedDirectory, id + ".md");
            if (!options.Force && File.Exists(processedPath) &&
                PostMarkdown.ReadEnrichment(await File.ReadAllTextAsync(processedPath, cancellationToken), id, null) is not null)
            {
                summary.SkippedPosts++;
                continue;
            }

            var raw = await File.ReadAllTextAsync(rawPath, cancellationToken);
            var body = ExtractBodyForPrompt(raw);
            if (string.IsNullOrWhiteSpace(body))
            {
                summary.SkippedPosts++;
                continue;
            }

            try
            {
                _log($"   📝 Processing post {id}...");

                // Telegram articles already have a title of their own.
                var isArticle = raw.Contains("**Post Type:** Article", StringComparison.Ordinal);
                var title = _config.GenerateTitle && !isArticle ? await _llm.GenerateTitleAsync(body, cancellationToken) : string.Empty;
                var tags = _config.ExtractHashtags
                    ? await _llm.ExtractHashtagsAsync(body, _config.MaxHashtags, baseHashtags, cancellationToken)
                    : [];

                if (string.IsNullOrWhiteSpace(title) && tags.Count == 0)
                {
                    summary.FailedPosts++;
                    summary.Errors.Add($"Post {id}: the model returned neither a title nor hashtags");
                    continue;
                }

                var enriched = PostMarkdown.ApplyEnrichment(raw, title, tags, _llm.Provider);
                await File.WriteAllTextAsync(processedPath, enriched, Utf8NoBom, cancellationToken);
                await File.WriteAllTextAsync(Path.Combine(_exportDirectory, id + ".md"), enriched, Utf8NoBom, cancellationToken);

                summary.ProcessedPosts++;
                processedThisRun++;
                if (!string.IsNullOrWhiteSpace(title)) summary.TitlesGenerated++;
                if (tags.Count > 0) summary.HashtagsExtracted++;

                if (_config.ProcessingDelayMs > 0)
                {
                    await Task.Delay(_config.ProcessingDelayMs, cancellationToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                summary.FailedPosts++;
                summary.Errors.Add($"Post {id}: {ex.Message}");
                _log($"   ❌ Post {id}: {ex.Message}");
            }
        }

        summary.ProcessingCompleted = DateTime.UtcNow;
        await File.WriteAllTextAsync(
            Path.Combine(processedDirectory, "llm_processing_summary.json"),
            JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken);

        return summary;
    }

    /// <summary>The post's own text: no metadata header, media links or placeholder lines.</summary>
    internal static string ExtractBodyForPrompt(string markdown)
    {
        var builder = new StringBuilder();
        foreach (var line in PostMarkdown.SplitLines(markdown))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 ||
                PostMarkdown.IsMetadataLine(trimmed) ||
                trimmed.StartsWith("![", StringComparison.Ordinal) ||
                trimmed.StartsWith("🎬", StringComparison.Ordinal) ||
                trimmed.StartsWith("🎵", StringComparison.Ordinal) ||
                trimmed.StartsWith("📄", StringComparison.Ordinal) ||
                trimmed.StartsWith("📎", StringComparison.Ordinal) ||
                trimmed.Equals(PostMarkdown.NoContentMarker, StringComparison.OrdinalIgnoreCase) ||
                trimmed.Equals(PostMarkdown.ArticlePreviewOnlyMarker, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            builder.AppendLine(trimmed);
        }

        return builder.ToString().Trim();
    }

    private async Task<List<string>> LoadBaseHashtagsAsync(CancellationToken cancellationToken)
    {
        var path = _baseHashtagsPath ?? Path.Combine(AppContext.BaseDirectory, "basehashtags.json");
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            var data = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(await File.ReadAllTextAsync(path, cancellationToken));
            if (data is not null && data.TryGetValue("baseHashtags", out var tags))
            {
                _log($"📚 Loaded {tags.Count} base hashtags");
                return tags;
            }
        }
        catch (JsonException ex)
        {
            _log($"⚠️ Could not read base hashtags: {ex.Message}");
        }

        return [];
    }

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
}
