using System.Diagnostics;
using Telescribe.Core.Export;
using Telescribe.Core.Models;
using Telescribe.Core.Services;
using Telescribe.Core.Site;

namespace Telescribe.Console;

public sealed record SyncOptions(bool Ai, bool WordPress, string? DeployTarget, bool Mirror, bool Open);

/// <summary>
/// The operations behind both the interactive menu and the command line. Each returns true on success so the
/// command line can exit with a meaningful status code.
/// </summary>
public sealed class Workflows(TelegramConfig config)
{
    public const string ExportDirectory = "./exports";

    /// <summary>Whether the config asks for the site to be opened after a build.</summary>
    public bool OpenAfterBuild => config.StaticSite.OpenBrowserAfterGeneration;

    public async Task<bool> ExportAsync(CancellationToken cancellationToken)
    {
        WriteLine("\n📥 Starting Telegram Channel Export...");
        WriteLine(new string('─', 50));

        return await WithTelegramAsync(async telegram =>
        {
            WriteLine("📡 Fetching posts from Telegram channel...");
            var result = await telegram.ExportChannelPostsAsync(cancellationToken);

            WriteLine("🎉 Export completed successfully!");
            WriteLine($"   📊 Total posts exported: {result.TotalPosts}");
            if (result.Posts.Count > 0)
            {
                WriteLine($"   📅 Date range: {result.Posts.Min(p => p.CreatedAt):yyyy-MM-dd} to {result.Posts.Max(p => p.CreatedAt):yyyy-MM-dd}");
            }

            WriteLine($"   📁 Export path: {result.ExportPath}");
            if (result.MediaFilesCount > 0)
            {
                WriteLine($"   🖼️ Media files: {result.MediaFilesCount}");
            }

            return true;
        });
    }

    public async Task<bool> UpdateAsync(CancellationToken cancellationToken)
    {
        WriteLine("\n🔄 Updating Existing Channel Export...");
        WriteLine(new string('─', 50));

        return await WithTelegramAsync(telegram => UpdateWithAsync(telegram, announceNoExport: true, cancellationToken), requireExistingExport: true);
    }

    private static async Task<bool> UpdateWithAsync(TelegramService telegram, bool announceNoExport, CancellationToken cancellationToken)
    {
        var last = await telegram.GetLastExportSummaryAsync();
        if (last == null || last.Posts.Count == 0)
        {
            if (announceNoExport)
            {
                WriteLine("❌ No existing exports found. Run 'export' first.");
            }

            return false;
        }

        var lastPostDate = last.Posts.Max(p => p.CreatedAt);
        WriteLine($"📅 Last export contained posts up to: {lastPostDate:yyyy-MM-dd HH:mm:ss} UTC");
        WriteLine("🔍 Searching for new posts...");

        var update = await telegram.UpdateExistingExportsAsync(lastPostDate, cancellationToken);
        if (update.Status != "Completed")
        {
            WriteLine($"❌ Update failed: {update.ErrorMessage}");
            return false;
        }

        if (update.NewPostsCount == 0)
        {
            WriteLine(update.RefreshedPostsCount == 0
                ? "✅ No new posts found. Export is already up to date!"
                : $"✅ No new posts found. Refreshed statistics for {update.RefreshedPostsCount} existing posts.");
            return true;
        }

        WriteLine("🎉 Update completed successfully!");
        WriteLine($"   📊 New posts exported: {update.NewPostsCount}");
        if (update.RefreshedPostsCount > 0)
        {
            WriteLine($"   🔄 Existing posts refreshed: {update.RefreshedPostsCount}");
        }

        WriteLine($"   📅 Date range: {update.DateRangeFrom:yyyy-MM-dd} to {update.DateRangeTo:yyyy-MM-dd}");
        if (update.MediaFilesCount > 0)
        {
            WriteLine($"   🖼️ Media files: {update.MediaFilesCount}");
        }

        return true;
    }

    public async Task<bool> EnrichAsync(bool force, int limit, CancellationToken cancellationToken)
    {
        WriteLine("\n🤖 AI Content Processing...");
        WriteLine(new string('─', 50));

        if (!Directory.Exists(Path.Combine(ExportDirectory, "raw")))
        {
            WriteLine("❌ No exported posts found. Run 'export' first.");
            return false;
        }

        using var llm = new LlmService(config.Llm);
        if (!await llm.InitializeAsync())
        {
            return false;
        }

        var summary = await new PostEnrichmentService(llm, config.Llm, ExportDirectory)
            .RunAsync(new EnrichmentOptions(force, limit), cancellationToken);

        WriteLine("✅ AI processing finished.");
        WriteLine($"   📝 Processed: {summary.ProcessedPosts} (titles: {summary.TitlesGenerated}, hashtags: {summary.HashtagsExtracted})");
        WriteLine($"   ⏭️  Skipped: {summary.SkippedPosts}   ❌ Failed: {summary.FailedPosts}");
        foreach (var error in summary.Errors.Take(5))
        {
            WriteLine($"   • {error}");
        }

        return summary.FailedPosts == 0;
    }

    public async Task<bool> BuildSiteAsync(string? output, bool clean, bool open, CancellationToken cancellationToken)
    {
        WriteLine("\n🏗️ Building Static Website...");
        WriteLine(new string('─', 50));

        if (!Directory.Exists(ExportDirectory))
        {
            WriteLine("❌ Export directory not found. Run 'export' first.");
            return false;
        }

        var templatesPath = ResolveTemplatesPath(config.StaticSite.TemplateName);
        if (templatesPath == null)
        {
            return false;
        }

        var siteDir = output ?? SiteBuilder.ResolveSiteDirectory(config.StaticSite, ExportDirectory);
        WriteLine($"🏗️ Site folder: {siteDir}");
        WriteLine($"🎨 Template: {config.StaticSite.TemplateName}");

        try
        {
            if (clean)
            {
                SiteBuilder.CleanSiteDirectory(siteDir);
            }

            var result = await new SiteBuilder(config.StaticSite, templatesPath).BuildAsync(ExportDirectory, siteDir, cancellationToken);

            foreach (var warning in result.Warnings)
            {
                WriteLine($"⚠️  {warning}");
            }

            WriteLine("✅ Static website generated successfully!");
            WriteLine($"   📌 {result.PostPages} post pages ({result.NewPostPages} new, {result.UpdatedPostPages} refreshed, {result.RemovedPostPages} removed); {result.IndexedPosts} listed on the index");
            if (result.TagPages > 0)
                WriteLine($"   🏷️  Tag pages: {result.TagPages}");
            if (result.MediaFilesCopied > 0)
                WriteLine($"   🖼️  Media files copied: {result.MediaFilesCopied}");
            if (result.FeedPath is not null)
                WriteLine($"   📰 Feed: {result.FeedPath}");
            if (result.SitemapPath is not null)
                WriteLine($"   🗺️  Sitemap: {result.SitemapPath}");

            if (open)
            {
                OpenInBrowser(result.IndexPath);
            }

            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            WriteLine($"❌ {ex.Message}");
            return false;
        }
    }

    public async Task<bool> ReportAsync(CancellationToken cancellationToken)
    {
        WriteLine("\n📊 Generating Analytics Reports...");
        WriteLine(new string('─', 50));

        if (!Directory.Exists(ExportDirectory))
        {
            WriteLine("❌ Export directory not found. Run 'export' first.");
            return false;
        }

        var templatesPath = ResolveTemplatesPath("reports");
        if (templatesPath == null)
        {
            return false;
        }

        try
        {
            var path = await new ReportService(templatesPath).GenerateAsync(ExportDirectory, cancellationToken);
            WriteLine("✅ Analytics report generated successfully!");
            WriteLine($"   📄 Report saved to: {path}");
            return true;
        }
        catch (FileNotFoundException ex)
        {
            WriteLine($"❌ {ex.Message}");
            return false;
        }
    }

    public async Task<bool> WordPressAsync(bool force, bool dryRun, CancellationToken cancellationToken)
    {
        WriteLine("\n🌐 WordPress Sync...");
        WriteLine(new string('─', 50));

        // Polls without a question and media-less empty posts are not worth a WordPress post.
        var posts = (await ExportedPost.LoadAllAsync(ExportDirectory, cancellationToken))
            .Where(p => !PostMarkdown.IsNoContent(p.Markdown))
            .ToList();
        if (posts.Count == 0)
        {
            WriteLine("❌ No exported posts found. Run 'export' first.");
            return false;
        }

        if (dryRun)
        {
            var state = WordPressState.Load(
                WordPressState.DefaultPath(ExportDirectory),
                Path.Combine(ExportDirectory, "wordpress_mappings.json"));
            var plan = WordPressSyncPlanner.Plan(posts, state, force);

            WriteLine("🔎 Dry run — nothing will be sent to WordPress.");
            WriteLine($"   ➕ Would create: {plan.Count(p => p.Action == WordPressAction.Create)}");
            WriteLine($"   ✏️  Would update: {plan.Count(p => p.Action == WordPressAction.Update)}");
            WriteLine($"   ⏭️  Unchanged:    {plan.Count(p => p.Action == WordPressAction.Skip)}");
            return true;
        }

        if (string.IsNullOrWhiteSpace(config.WordPress.SiteUrl) || string.IsNullOrWhiteSpace(config.WordPress.Username))
        {
            WriteLine("❌ WordPress is not configured (WordPress:SiteUrl, Username and ApplicationPassword).");
            return false;
        }

        using var wordPress = WordPressServiceFactory.CreateService(config.WordPress, ExportDirectory);
        if (!await wordPress.AuthenticateAsync())
        {
            return false;
        }

        var summary = await wordPress.SyncAsync(posts, force, cancellationToken);

        WriteLine("✅ WordPress sync finished.");
        WriteLine($"   ➕ Created: {summary.NewPosts}   ✏️  Updated: {summary.UpdatedPosts}   ⏭️  Unchanged: {summary.SkippedPosts}   🖼️  Media uploaded: {summary.MediaFilesUploaded}");
        if (summary.FailedPosts > 0)
        {
            WriteLine($"   ❌ Failed: {summary.FailedPosts}");
            foreach (var error in summary.Errors.Take(5))
            {
                WriteLine($"   • {error}");
            }
        }

        return summary.FailedPosts == 0;
    }

    public async Task<bool> ArchiveAsync(string? output, CancellationToken cancellationToken)
    {
        WriteLine("\n📦 Creating archive...");
        WriteLine(new string('─', 50));

        try
        {
            var result = await ArchiveService.CreateAsync(ExportDirectory, output ?? ArchiveService.DefaultPath(ExportDirectory), cancellationToken);
            WriteLine($"✅ Archive created: {result.Path}");
            WriteLine($"   📦 {result.Files} files, {result.Bytes / (1024.0 * 1024.0):0.#} MB before compression");
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            WriteLine($"❌ {ex.Message}");
            return false;
        }
    }

    /// <summary>Update (or first export), optional AI and WordPress steps, build the site, optional deploy.</summary>
    public async Task<bool> SyncAsync(SyncOptions options, CancellationToken cancellationToken)
    {
        WriteLine("\n🔁 Sync");
        WriteLine(new string('─', 50));

        var hasExport = File.Exists(Path.Combine(ExportDirectory, "summary.json"));
        var fetched = await WithTelegramAsync(async telegram => hasExport
            ? await UpdateWithAsync(telegram, announceNoExport: false, cancellationToken)
            : await ExportWithAsync(telegram, cancellationToken));

        if (!fetched)
        {
            WriteLine("⚠️  Could not sync with Telegram. Continuing with the existing local export.");
            if (!hasExport)
            {
                return false;
            }
        }

        if (options.Ai && !await EnrichAsync(force: false, limit: 0, cancellationToken))
        {
            return false;
        }

        if (options.WordPress && !await WordPressAsync(force: false, dryRun: false, cancellationToken))
        {
            return false;
        }

        if (!await BuildSiteAsync(output: null, clean: false, open: options.Open, cancellationToken))
        {
            return false;
        }

        if (options.DeployTarget is { Length: > 0 } target)
        {
            var siteDir = SiteBuilder.ResolveSiteDirectory(config.StaticSite, ExportDirectory);
            var result = SiteDeployer.Deploy(siteDir, target, options.Mirror);
            WriteLine($"🚚 Deployed to {target}: {result.Copied} copied, {result.Unchanged} unchanged, {result.Deleted} deleted");
        }

        return true;
    }

    private static async Task<bool> ExportWithAsync(TelegramService telegram, CancellationToken cancellationToken)
    {
        var result = await telegram.ExportChannelPostsAsync(cancellationToken);
        WriteLine($"🎉 Export completed: {result.TotalPosts} posts");
        return true;
    }

    /// <summary>Creates the Telegram service, signs in, and runs <paramref name="action"/>.</summary>
    private async Task<bool> WithTelegramAsync(Func<TelegramService, Task<bool>> action, bool requireExistingExport = false)
    {
        try
        {
            using var telegram = TelegramServiceFactory.CreateService(config);

            if (requireExistingExport)
            {
                var last = await telegram.GetLastExportSummaryAsync();
                if (last == null || last.Posts.Count == 0)
                {
                    WriteLine("❌ No existing exports found. Run 'export' first.");
                    return false;
                }
            }

            WriteLine("🔐 Authenticating with Telegram...");
            if (!await telegram.AuthenticateAsync())
            {
                WriteLine("❌ Authentication failed! Please check your configuration.");
                WriteLine("   • Verify your API ID and API Hash from https://my.telegram.org/");
                WriteLine("   • Ensure your phone number is in international format (e.g., +1234567890)");
                WriteLine("   • The first login is interactive (verification code); run 'export' once in a terminal before automating");
                return false;
            }

            WriteLine("✅ Authentication successful!");
            return await action(telegram);
        }
        catch (OperationCanceledException)
        {
            WriteLine("⏹️  Cancelled.");
            return false;
        }
        catch (Exception ex)
        {
            WriteLine($"❌ Failed: {ex.Message}");
            return false;
        }
    }

    public static void OpenInBrowser(string indexPath)
    {
        var indexUrl = Path.GetFullPath(indexPath);
        WriteLine($"   🚀 Opening browser: {indexUrl}");
        try
        {
            Process.Start(new ProcessStartInfo { FileName = indexUrl, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            WriteLine($"ℹ️ Could not open browser: {ex.Message}");
            WriteLine($"   Please open manually: {indexUrl}");
        }
    }

    /// <summary>Finds <c>templates/{name}</c> next to the executable, then in the working directory.</summary>
    public static string? ResolveTemplatesPath(string name)
    {
        var baseDirectory = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDirectory, "templates", name),
            Path.Combine("templates", name)
        };

        var found = candidates.FirstOrDefault(Directory.Exists);
        if (found != null)
        {
            return found;
        }

        WriteLine($"❌ Template directory not found: {candidates[0]}");
        var templatesRoot = new[] { Path.Combine(baseDirectory, "templates"), "templates" }.FirstOrDefault(Directory.Exists);
        if (templatesRoot != null)
        {
            WriteLine("Available templates:");
            foreach (var dir in Directory.GetDirectories(templatesRoot))
            {
                WriteLine($"   - {Path.GetFileName(dir)}");
            }
        }

        return null;
    }
}
