global using static System.Console;
using Microsoft.Extensions.Configuration;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using Telescribe.Core.Models;
using Telescribe.Core.Services;
using Telescribe.Core.Site;

namespace Telescribe.Console;

public static class Program
{
    private const string ExportDirectory = "./exports";

    public static async Task<int> Main(string[] args)
    {
        try
        {
            OutputEncoding = Encoding.UTF8;
        }
        catch (IOException)
        {
            // No console attached (redirected output); nothing to configure.
        }

        WriteLine("🔭 Telescribe - Telegram Channel Exporter");

        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppDomain.CurrentDomain.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddUserSecrets(Assembly.GetExecutingAssembly(), optional: true)
            .AddEnvironmentVariables()
            .Build();

        var config = LoadConfiguration(configuration);
        if (config == null)
        {
            WriteLine("❌ Configuration validation failed. Please check your settings.");
            return 1;
        }

        if (args.Length > 0)
        {
            return await RunCommandAsync(args[0], config) ? 0 : 1;
        }

        while (true)
        {
            DisplayMainMenu();
            var input = ReadLine()?.Trim();

            switch (input)
            {
                case "1":
                    await ExportTelegramPosts(config);
                    break;

                case "2":
                    await UpdateExistingExports(config);
                    break;

                case "3":
                    WriteLine("🤖 AI content processing is not wired into the menu yet.");
                    break;

                case "4":
                    WriteLine("🌐 WordPress integration is not wired into the menu yet.");
                    break;

                case "5":
                    await GenerateHtmlReports();
                    break;

                case "6":
                    await GenerateStaticSite(config);
                    break;

                case "8":
                    await UpdateStaticSite(config);
                    break;

                case "7":
                case "0":
                case "exit":
                case "quit":
                    WriteLine("👋 Goodbye! Thanks for using Telescribe!");
                    return 0;

                default:
                    WriteLine("❌ Invalid choice. Please enter a number from 1-8.");
                    break;
            }

            WriteLine("\n⏱️  Press any key to return to main menu...");
            if (IsInputRedirected)
            {
                ReadLine();
            }
            else
            {
                ReadKey(intercept: true);
            }
        }
    }

    private static async Task<bool> RunCommandAsync(string command, TelegramConfig config)
    {
        switch (command.ToLowerInvariant())
        {
            case "static":
                return await GenerateStaticSite(config);
            case "update-site":
                return await UpdateStaticSite(config);
            case "reports":
                return await GenerateHtmlReports();
            default:
                WriteLine($"❌ Unknown command '{command}'. Available: static, update-site, reports (or run without arguments for the menu).");
                return false;
        }
    }

    static void DisplayMainMenu()
    {
        if (!IsOutputRedirected)
        {
            Clear();
        }

        WriteLine("╔════════════════════════════════════════════════════════════════╗");
        WriteLine("║                          🔭 Telescribe                         ║");
        WriteLine("║                   Telegram Channel Exporter                    ║");
        WriteLine("╚════════════════════════════════════════════════════════════════╝");
        WriteLine();
        WriteLine("🚀 Choose your action:");
        WriteLine();
        WriteLine("   📥 1  Export Channel Posts      - Download all posts from Telegram");
        WriteLine("   🔄 2  Update Existing Posts     - Sync new posts since last export");
        WriteLine("   🤖 3  AI Content Processing     - Coming Soon! 🚧");
        WriteLine("   🌐 4  WordPress Integration     - Coming Soon! 🚧");
        WriteLine("   📊 5  Analytics Reports         - Generate detailed insights");
        WriteLine("   🏗️  6  Static Website Builder    - Create a beautiful website");
        WriteLine("   🔄 8  Update Static Website      - Refresh existing site with new posts");
        WriteLine("   🚪 7  Exit Application          - Close Telescribe");
        WriteLine();
        WriteLine("═══════════════════════════════════════════════════════════════");
        Write("🎯 Enter your choice (1-8): ");
    }

    static TelegramConfig? LoadConfiguration(IConfiguration configuration)
    {
        try
        {
            var config = new TelegramConfig();
            configuration.GetSection("TelegramConfig").Bind(config);

            if (string.IsNullOrEmpty(config.PhoneNumber) ||
                string.IsNullOrEmpty(config.ChannelId) ||
                config.ApiId == 0 ||
                string.IsNullOrEmpty(config.ApiHash))
            {
                WriteLine("❌ Missing required Telegram configuration. Set these in appsettings.json, user secrets or environment variables (TelegramConfig__ApiId, ...):");
                WriteLine("   • PhoneNumber");
                WriteLine("   • ChannelId");
                WriteLine("   • ApiId");
                WriteLine("   • ApiHash");
                return null;
            }

            return config;
        }
        catch (Exception ex)
        {
            WriteLine($"❌ Configuration error: {ex.Message}");
            return null;
        }
    }

    static async Task ExportTelegramPosts(TelegramConfig config)
    {
        WriteLine("\n📥 Starting Telegram Channel Export...");
        WriteLine(new string('─', 50));

        try
        {
            using var telegramService = TelegramServiceFactory.CreateService(config);

            WriteLine("🔐 Authenticating with Telegram...");
            var authenticated = await telegramService.AuthenticateAsync();

            if (!authenticated)
            {
                PrintAuthenticationHelp();
                return;
            }

            WriteLine("✅ Authentication successful!");
            WriteLine("📡 Fetching posts from Telegram channel...");
            var result = await telegramService.ExportChannelPostsAsync();

            WriteLine("🎉 Export completed successfully!");
            WriteLine($"   📊 Total posts exported: {result.TotalPosts}");
            if (result.Posts.Any())
            {
                var dateFrom = result.Posts.Min(p => p.CreatedAt);
                var dateTo = result.Posts.Max(p => p.CreatedAt);
                WriteLine($"   📅 Date range: {dateFrom:yyyy-MM-dd} to {dateTo:yyyy-MM-dd}");
            }
            WriteLine($"   📁 Export path: {result.ExportPath}");

            if (result.MediaFilesCount > 0)
            {
                WriteLine($"   🖼️ Media files: {result.MediaFilesCount}");
            }
        }
        catch (Exception ex)
        {
            WriteLine($"❌ Export failed: {ex.Message}");
            WriteLine("Please check your configuration and try again.");
        }
    }

    static async Task UpdateExistingExports(TelegramConfig config)
    {
        WriteLine("\n🔄 Updating Existing Channel Export...");
        WriteLine(new string('─', 50));

        try
        {
            using var telegramService = TelegramServiceFactory.CreateService(config);

            WriteLine("📊 Reading last export summary...");
            var lastExportSummary = await telegramService.GetLastExportSummaryAsync();

            if (lastExportSummary == null || !lastExportSummary.Posts.Any())
            {
                WriteLine("❌ No existing exports found. Please run 'Export Channel Posts' first.");
                return;
            }

            var lastPostDate = lastExportSummary.Posts.Max(p => p.CreatedAt);
            WriteLine($"📅 Last export contained posts up to: {lastPostDate:yyyy-MM-dd HH:mm:ss} UTC");

            WriteLine("🔐 Authenticating with Telegram...");
            var authenticated = await telegramService.AuthenticateAsync();

            if (!authenticated)
            {
                PrintAuthenticationHelp();
                return;
            }

            WriteLine("✅ Authentication successful!");
            WriteLine("🔍 Searching for new posts...");
            var updateSummary = await telegramService.UpdateExistingExportsAsync(lastPostDate);

            if (updateSummary.Status != "Completed")
            {
                WriteLine($"❌ Update failed: {updateSummary.ErrorMessage}");
                return;
            }

            if (updateSummary.NewPostsCount == 0)
            {
                WriteLine(updateSummary.RefreshedPostsCount == 0
                    ? "✅ No new posts found. Export is already up to date!"
                    : $"✅ No new posts found. Refreshed metadata/content for {updateSummary.RefreshedPostsCount} existing posts.");
                return;
            }

            WriteLine("🎉 Update completed successfully!");
            WriteLine($"   📊 New posts exported: {updateSummary.NewPostsCount}");
            if (updateSummary.RefreshedPostsCount > 0)
            {
                WriteLine($"   🔄 Existing posts refreshed: {updateSummary.RefreshedPostsCount}");
            }
            WriteLine($"   📅 Date range: {updateSummary.DateRangeFrom:yyyy-MM-dd} to {updateSummary.DateRangeTo:yyyy-MM-dd}");
            WriteLine($"   📁 Export path: {updateSummary.ExportPath}");

            if (updateSummary.MediaFilesCount > 0)
            {
                WriteLine($"   🖼️ Media files: {updateSummary.MediaFilesCount}");
            }

            if (config.Llm.EnableProcessing)
            {
                WriteLine("\n🤖 AI processing of new posts is not available from the menu yet.");
            }
        }
        catch (Exception ex)
        {
            WriteLine($"❌ Update failed: {ex.Message}");
            WriteLine("Please check your configuration and try again.");
        }
    }

    static void PrintAuthenticationHelp()
    {
        WriteLine("❌ Authentication failed! Please check your configuration.");
        WriteLine("   • Verify your API ID and API Hash from https://my.telegram.org/");
        WriteLine("   • Ensure your phone number is in international format (e.g., +1234567890)");
        WriteLine("   • Check that your channel ID or username is correct");
    }

    static async Task<bool> GenerateHtmlReports()
    {
        WriteLine("\n📊 Generating Analytics Reports...");
        WriteLine(new string('─', 50));

        if (!Directory.Exists(ExportDirectory))
        {
            WriteLine("❌ Export directory not found. Please run export first.");
            return false;
        }

        var reportsTemplatesPath = ResolveTemplatesPath("reports");
        if (reportsTemplatesPath == null)
        {
            return false;
        }

        try
        {
            var reportPath = await new ReportService(reportsTemplatesPath).GenerateAsync(ExportDirectory);

            WriteLine("✅ Analytics report generated successfully!");
            WriteLine($"   📄 Report saved to: {reportPath}");
            WriteLine("   🌐 Open in browser to view detailed insights");
            return true;
        }
        catch (FileNotFoundException ex)
        {
            WriteLine($"❌ {ex.Message}");
            return false;
        }
    }

    static async Task<bool> GenerateStaticSite(TelegramConfig config)
    {
        WriteLine("\n🏗️ Building Static Website...");
        WriteLine(new string('─', 50));

        if (!Directory.Exists(ExportDirectory))
        {
            WriteLine("❌ Export directory not found. Please run export first.");
            return false;
        }

        var templatesPath = ResolveTemplatesPath(config.StaticSite.TemplateName);
        if (templatesPath == null)
        {
            return false;
        }

        var siteDir = Path.Combine(ExportDirectory, $"website_{DateTime.Now:yyyyMMdd_HHmmss}");
        WriteLine($"🏗️ Creating website in: {siteDir}");
        WriteLine($"🎨 Using template: {config.StaticSite.TemplateName}");

        return await BuildSiteAsync(config, templatesPath, siteDir, isUpdate: false);
    }

    static async Task<bool> UpdateStaticSite(TelegramConfig config)
    {
        WriteLine("\n🔄 Updating Static Website...");
        WriteLine(new string('─', 50));

        if (!Directory.Exists(ExportDirectory))
        {
            WriteLine("❌ Export directory not found. Please run export first.");
            return false;
        }

        // Folder names embed a sortable timestamp, so the last one is the newest.
        var siteDir = Directory.GetDirectories(ExportDirectory, "website_*")
            .OrderByDescending(d => d, StringComparer.Ordinal)
            .FirstOrDefault();

        if (siteDir == null)
        {
            WriteLine("❌ No generated site found. Run option 6 (Static Website Builder) first.");
            return false;
        }

        var templatesPath = ResolveTemplatesPath(config.StaticSite.TemplateName);
        if (templatesPath == null)
        {
            return false;
        }

        WriteLine($"📂 Updating site: {siteDir}");
        await SyncExportsBeforeUpdateAsync(config);

        return await BuildSiteAsync(config, templatesPath, siteDir, isUpdate: true);
    }

    static async Task SyncExportsBeforeUpdateAsync(TelegramConfig config)
    {
        using var telegramService = TelegramServiceFactory.CreateService(config);

        var lastExportSummary = await telegramService.GetLastExportSummaryAsync();
        if (lastExportSummary?.Posts.Any() != true)
        {
            return;
        }

        var lastPostDate = lastExportSummary.Posts.Max(p => p.CreatedAt);
        WriteLine($"📡 Syncing exports from Telegram before rebuilding site (last export: {lastPostDate:yyyy-MM-dd HH:mm:ss} UTC)...");

        if (!await telegramService.AuthenticateAsync())
        {
            WriteLine("⚠️  Telegram authentication failed. Continuing with existing exported markdown files.");
            return;
        }

        var summary = await telegramService.UpdateExistingExportsAsync(lastPostDate);
        if (summary.Status != "Completed")
        {
            WriteLine($"⚠️  Export sync failed: {summary.ErrorMessage}");
            WriteLine("   Continuing with local exported markdown files.");
            return;
        }

        WriteLine($"   📡 Export sync: {summary.NewPostsCount} new, {summary.RefreshedPostsCount} refreshed");
    }

    static async Task<bool> BuildSiteAsync(TelegramConfig config, string templatesPath, string siteDir, bool isUpdate)
    {
        try
        {
            var result = await new SiteBuilder(config.StaticSite, templatesPath).BuildAsync(ExportDirectory, siteDir);

            foreach (var warning in result.Warnings)
            {
                WriteLine($"⚠️  {warning}");
            }

            WriteLine(isUpdate ? "✅ Site updated successfully!" : "✅ Static website generated successfully!");
            WriteLine($"   🌐 Site folder: {result.SiteDirectory}");
            WriteLine($"   📌 {result.PostPages} post pages ({result.NewPostPages} new, {result.UpdatedPostPages} refreshed, {result.RemovedPostPages} removed); {result.IndexedPosts} listed on the index");
            if (result.MediaFilesCopied > 0)
                WriteLine($"   🖼️  Media files copied: {result.MediaFilesCopied}");
            if (result.SitemapPath is not null)
                WriteLine($"   🗺️  Sitemap: {result.SitemapPath}");
            WriteLine($"   📌 Template: {config.StaticSite.TemplateName}");

            if (config.StaticSite.OpenBrowserAfterGeneration)
            {
                OpenInBrowser(result.IndexPath);
            }

            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or FileNotFoundException)
        {
            WriteLine($"❌ {ex.Message}");
            return false;
        }
    }

    static void OpenInBrowser(string indexPath)
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
    static string? ResolveTemplatesPath(string name)
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
