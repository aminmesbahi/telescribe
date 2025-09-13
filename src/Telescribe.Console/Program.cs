global using static System.Console;
using Microsoft.Extensions.Configuration;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Telescribe.Core.Models;
using Telescribe.Core.Services;
using PostData = Telescribe.Core.Services.PostData;

namespace Telescribe.Console;

public class Program
{
    public static async Task Main(string[] args)
    {
        WriteLine("🔭 Telescribe - Telegram Channel Exporter");

        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppDomain.CurrentDomain.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables()
            .Build();

        var config = LoadConfiguration(configuration);
        if (config == null)
        {
            WriteLine("❌ Configuration validation failed. Please check your settings.");
            return;
        }

        if (args.Length > 0 && args[0] == "static")
        {
            await GenerateStaticSite(config);
            return;
        }
        if (args.Length > 0 && args[0] == "reports")
        {
            await GenerateHtmlReports(config);
            return;
        }
        if (config == null)
        {
            WriteLine("❌ Configuration validation failed. Please check your settings.");
            return;
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

                // case "3":
                //     await ProcessWithLlm(config);
                //     break;

                // case "4":
                //     await UploadToWordPress(config);
                //     break;

                case "5":
                    await GenerateHtmlReports(config);
                    break;

                case "6":
                    await GenerateStaticSite(config);
                    break;

                case "7":
                case "0":
                case "exit":
                case "quit":
                    WriteLine("👋 Goodbye! Thanks for using Telescribe!");
                    return;

                default:
                    WriteLine("❌ Invalid choice. Please enter a number from 1-7.");
                    break;
            }

            WriteLine("\n⏱️  Press any key to return to main menu...");
            ReadKey();
        }
    }

    static void DisplayMainMenu()
    {
        Clear();
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
        WriteLine("   🚪 7  Exit Application          - Close Telescribe");
        WriteLine();
        WriteLine("═══════════════════════════════════════════════════════════════");
        Write("🎯 Enter your choice (1-7): ");
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
                WriteLine("❌ Missing required Telegram configuration. Please check appsettings.json:");
                WriteLine("   • PhoneNumber");
                WriteLine("   • ChannelId");
                WriteLine("   • ApiId");
                WriteLine("   • ApiHash");
                return null;
            }

            var llmConfig = new LlmConfig();
            configuration.GetSection("TelegramConfig:LLM").Bind(llmConfig);
            config.Llm = llmConfig;

            var wordPressConfig = new WordPressConfig();
            var wpSection = configuration.GetSection("TelegramConfig:WordPress");
            wordPressConfig.SiteUrl = wpSection["BaseUrl"] ?? "";
            wordPressConfig.Username = wpSection["Username"] ?? "";
            wordPressConfig.ApplicationPassword = wpSection["Password"] ?? "";
            wordPressConfig.CategoryName = wpSection["DefaultCategoryId"] ?? "1";
            wordPressConfig.EnableUpload = bool.Parse(wpSection["EnableUploads"] ?? "false");
            config.WordPress = wordPressConfig;

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
            var telegramService = TelegramServiceFactory.CreateService(config);

            WriteLine("🔐 Authenticating with Telegram...");
            var authenticated = await telegramService.AuthenticateAsync();

            if (!authenticated)
            {
                WriteLine("❌ Authentication failed! Please check your configuration.");
                WriteLine("   • Verify your API ID and API Hash from https://my.telegram.org/");
                WriteLine("   • Ensure your phone number is in international format (e.g., +1234567890)");
                WriteLine("   • Check that your channel ID or username is correct");
                return;
            }

            WriteLine("✅ Authentication successful!");
            WriteLine("📡 Fetching posts from Telegram channel...");
            var result = await telegramService.ExportChannelPostsAsync();

            WriteLine($"🎉 Export completed successfully!");
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
                WriteLine($"   🖼️ Media files downloaded: {result.MediaFilesCount}");
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
            var telegramService = TelegramServiceFactory.CreateService(config);

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
                WriteLine("❌ Authentication failed! Please check your configuration.");
                WriteLine("   • Verify your API ID and API Hash from https://my.telegram.org/");
                WriteLine("   • Ensure your phone number is in international format (e.g., +1234567890)");
                WriteLine("   • Check that your channel ID or username is correct");
                return;
            }

            WriteLine("✅ Authentication successful!");
            WriteLine("🔍 Searching for new posts...");
            var updateSummary = await telegramService.UpdateExistingExportsAsync(lastPostDate);

            if (updateSummary.NewPostsCount == 0)
            {
                WriteLine("✅ No new posts found. Export is already up to date!");
                return;
            }

            WriteLine($"🎉 Update completed successfully!");
            WriteLine($"   📊 New posts exported: {updateSummary.NewPostsCount}");
            WriteLine($"   📅 Date range: {updateSummary.DateRangeFrom:yyyy-MM-dd} to {updateSummary.DateRangeTo:yyyy-MM-dd}");
            WriteLine($"   📁 Export path: {updateSummary.ExportPath}");

            if (updateSummary.MediaFilesCount > 0)
            {
                WriteLine($"   🖼️ Media files downloaded: {updateSummary.MediaFilesCount}");
            }

            if (config.Llm.EnableProcessing && updateSummary.NewPostsCount > 0)
            {
                Write("\n🤖 Would you like to process the new posts with AI? (y/n): ");
                var llmChoice = ReadLine()?.Trim().ToLower();

                if (llmChoice == "y" || llmChoice == "yes")
                {
                    WriteLine("🤖 AI processing feature coming soon!");
                    WriteLine("💡 Please check back in future updates for AI-powered content enhancement.");
                }
            }
        }
        catch (Exception ex)
        {
            WriteLine($"❌ Update failed: {ex.Message}");
            WriteLine("Please check your configuration and try again.");
        }
    }

    static async Task GenerateHtmlReports(TelegramConfig config)
    {
        WriteLine("\n📊 Generating Analytics Reports...");
        WriteLine(new string('─', 50));

        var exportDir = "./exports";
        if (!Directory.Exists(exportDir))
        {
            WriteLine("❌ Export directory not found. Please run export first.");
            return;
        }

        var summaryPath = Path.Combine(exportDir, "summary.json");
        if (!File.Exists(summaryPath))
        {
            WriteLine("❌ Export summary not found. Please run export first.");
            return;
        }

        var summaryJson = await File.ReadAllTextAsync(summaryPath);
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var summary = JsonSerializer.Deserialize<ExportSummary>(summaryJson, options);

        if (summary?.Posts == null)
        {
            WriteLine("❌ Invalid summary data.");
            return;
        }

        var reportsDir = Path.Combine(exportDir, "reports");
        Directory.CreateDirectory(reportsDir);

        WriteLine($"🔍 Analyzing {summary.Posts.Count} posts...");

        var popularPosts = summary.Posts
            .OrderByDescending(p => p.Views + p.Reactions + p.TotalForwards)
            .Take(20)
            .ToList();

        var recentPosts = summary.Posts
            .OrderByDescending(p => p.CreatedAt)
            .Take(10)
            .ToList();

        var mostReacted = summary.Posts
            .OrderByDescending(p => p.Reactions)
            .Take(10)
            .ToList();

        var mostForwarded = summary.Posts
            .OrderByDescending(p => p.TotalForwards)
            .Take(10)
            .ToList();

        var baseDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? Directory.GetCurrentDirectory();
        var reportsTemplatesPath = Path.Combine(baseDirectory, "templates", "reports");
        
        if (!Directory.Exists(reportsTemplatesPath))
        {
            reportsTemplatesPath = Path.Combine("templates", "reports");
        }
        
        if (!Directory.Exists(reportsTemplatesPath))
        {
            WriteLine($"❌ Reports template directory not found: {reportsTemplatesPath}");
            WriteLine("Available template directories:");
            var templateBaseDir = Path.Combine(baseDirectory, "templates");
            if (!Directory.Exists(templateBaseDir))
                templateBaseDir = "templates";
            
            if (Directory.Exists(templateBaseDir))
            {
                foreach (var dir in Directory.GetDirectories(templateBaseDir))
                {
                    WriteLine($"   - {Path.GetFileName(dir)}");
                }
            }
            return;
        }

        var reportGenerator = new SiteGeneratorService(reportsTemplatesPath);

        var statsCards = reportGenerator.RenderTemplate("stats-cards.html", new Dictionary<string, string>
        {
            ["totalPosts"] = summary.TotalPosts.ToString("N0"),
            ["totalViews"] = summary.Posts.Sum(p => p.Views).ToString("N0"),
            ["totalReactions"] = summary.Posts.Sum(p => p.Reactions).ToString("N0"),
            ["totalForwards"] = summary.Posts.Sum(p => p.TotalForwards).ToString("N0")
        });

        var popularPostsHtml = string.Join("", popularPosts.Select(p => 
            reportGenerator.RenderTemplate("popular-post-row.html", new Dictionary<string, string>
            {
                ["postId"] = p.PostId.ToString(),
                ["date"] = p.CreatedAt.ToString("yyyy-MM-dd"),
                ["views"] = p.Views.ToString("N0"),
                ["reactions"] = p.Reactions.ToString("N0"),
                ["forwards"] = p.TotalForwards.ToString("N0"),
                ["contentPreview"] = p.ContentPreview ?? "No preview available"
            })));

        var recentPostsHtml = string.Join("", recentPosts.Select(p => 
            reportGenerator.RenderTemplate("recent-post-row.html", new Dictionary<string, string>
            {
                ["postId"] = p.PostId.ToString(),
                ["date"] = p.CreatedAt.ToString("yyyy-MM-dd"),
                ["views"] = p.Views.ToString("N0"),
                ["reactions"] = p.Reactions.ToString("N0"),
                ["forwards"] = p.TotalForwards.ToString("N0"),
                ["contentPreview"] = p.ContentPreview ?? "No preview available"
            })));

        var reactedPostsHtml = string.Join("", mostReacted.Select(p => 
            reportGenerator.RenderTemplate("reacted-post-row.html", new Dictionary<string, string>
            {
                ["postId"] = p.PostId.ToString(),
                ["date"] = p.CreatedAt.ToString("yyyy-MM-dd"),
                ["reactions"] = p.Reactions.ToString("N0"),
                ["views"] = p.Views.ToString("N0"),
                ["forwards"] = p.TotalForwards.ToString("N0"),
                ["contentPreview"] = p.ContentPreview ?? "No preview available"
            })));

        var forwardedPostsHtml = string.Join("", mostForwarded.Select(p => 
            reportGenerator.RenderTemplate("forwarded-post-row.html", new Dictionary<string, string>
            {
                ["postId"] = p.PostId.ToString(),
                ["date"] = p.CreatedAt.ToString("yyyy-MM-dd"),
                ["forwards"] = p.TotalForwards.ToString("N0"),
                ["views"] = p.Views.ToString("N0"),
                ["reactions"] = p.Reactions.ToString("N0"),
                ["contentPreview"] = p.ContentPreview ?? "No preview available"
            })));

        var htmlReport = reportGenerator.RenderTemplate("analytics.html", new Dictionary<string, string>
        {
            ["statsCards"] = statsCards,
            ["popularPosts"] = popularPostsHtml,
            ["recentPosts"] = recentPostsHtml,
            ["reactedPosts"] = reactedPostsHtml,
            ["forwardedPosts"] = forwardedPostsHtml,
            ["generatedDate"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " UTC"
        });

        var reportPath = Path.Combine(reportsDir, "analytics_report.html");
        await File.WriteAllTextAsync(reportPath, htmlReport);

        WriteLine($"✅ Analytics report generated successfully!");
        WriteLine($"   📄 Report saved to: {reportPath}");
        WriteLine($"   🎨 Using template-based generation");
        WriteLine($"   🌐 Open in browser to view detailed insights");
    }

    static async Task GenerateStaticSite(TelegramConfig config)
    {
        WriteLine("\n🏗️ Building Static Website...");
        WriteLine(new string('─', 50));

        var exportDir = "./exports";
        if (!Directory.Exists(exportDir))
        {
            WriteLine("❌ Export directory not found. Please run export first.");
            return;
        }

        var markdownFiles = Directory.GetFiles(exportDir, "*.md")
            .Where(f => !Path.GetFileName(f).Equals("summary.json", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (markdownFiles.Length == 0)
        {
            WriteLine("❌ No markdown files found for site generation.");
            return;
        }

        var siteDir = Path.Combine(exportDir, $"website_{DateTime.Now:yyyyMMdd_HHmmss}");
        Directory.CreateDirectory(siteDir);
        Directory.CreateDirectory(Path.Combine(siteDir, "posts"));
        Directory.CreateDirectory(Path.Combine(siteDir, "assets"));

        WriteLine($"🏗️ Creating website structure in: {siteDir}");
        WriteLine($"📄 Processing {markdownFiles.Length} posts...");
        WriteLine($"🎨 Using template: {config.StaticSite.TemplateName}");

        var baseDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? Directory.GetCurrentDirectory();
        var templatesPath = Path.Combine(baseDirectory, "templates", config.StaticSite.TemplateName);
        
        if (!Directory.Exists(templatesPath))
        {
            templatesPath = Path.Combine("templates", config.StaticSite.TemplateName);
        }
        
        if (!Directory.Exists(templatesPath))
        {
            WriteLine($"❌ Template directory not found: {templatesPath}");
            WriteLine("Available templates:");
            var templateDir = Path.Combine(baseDirectory, "templates");
            if (!Directory.Exists(templateDir))
                templateDir = "templates";
            
            if (Directory.Exists(templateDir))
            {
                foreach (var dir in Directory.GetDirectories(templateDir))
                {
                    WriteLine($"   - {Path.GetFileName(dir)}");
                }
            }
            return;
        }

        var siteGenerator = new SiteGeneratorService(templatesPath);

        var assetsSource = Path.Combine(templatesPath, "style.css");
        if (File.Exists(assetsSource))
        {
            var assetsTarget = Path.Combine(siteDir, "assets", "style.css");
            File.Copy(assetsSource, assetsTarget, true);
            WriteLine("✅ Copied template assets");
        }

        var posts = new List<PostData>();

        foreach (var file in markdownFiles)
        {
            var fileName = Path.GetFileNameWithoutExtension(file);
            var content = await File.ReadAllTextAsync(file);
            var title = ExtractTitle(content) ?? fileName;
            var preview = ExtractPreview(content);
            var dateCreated = ExtractCreationDate(content);
            var (views, reactions, forwards) = ExtractMetadata(content);
            var htmlContent = ConvertMarkdownToHtml(content);

            var postData = new PostData
            {
                Filename = fileName,
                Title = title,
                Preview = preview,
                Content = htmlContent,
                Date = dateCreated,
                Views = views,
                Reactions = reactions,
                Forwards = forwards
            };

            posts.Add(postData);
        }

        var sortedPosts = posts
            .OrderByDescending(p => p.Date)
            .Take(config.StaticSite.MaxPostsInIndex)
            .ToList();

        WriteLine($"📝 Processing {sortedPosts.Count} posts (sorted by date, newest first)...");

        foreach (var post in sortedPosts)
        {
            var postHtml = siteGenerator.RenderPostPage(config.StaticSite, post);
            var postPath = Path.Combine(siteDir, "posts", $"{post.Filename}.html");
            await File.WriteAllTextAsync(postPath, postHtml);
        }

        var indexHtml = siteGenerator.RenderIndexPage(config.StaticSite, sortedPosts, DateTime.Now);
        var indexPath = Path.Combine(siteDir, "index.html");
        await File.WriteAllTextAsync(indexPath, indexHtml);

        WriteLine($"✅ Static website generated successfully!");
        WriteLine($"   🌐 Site saved to: {siteDir}");
        WriteLine($"   � {sortedPosts.Count} posts generated");
        WriteLine($"   � Template: {config.StaticSite.TemplateName}");

        if (config.StaticSite.OpenBrowserAfterGeneration)
        {
            var indexUrl = Path.GetFullPath(indexPath);
            WriteLine($"   🚀 Opening browser: {indexUrl}");
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = indexUrl,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                WriteLine($"❌ Could not open browser: {ex.Message}");
                WriteLine($"   Please open manually: {indexUrl}");
            }
        }
    }

    static (int views, int reactions, int forwards) ExtractMetadata(string content)
    {
        int views = 0, reactions = 0, forwards = 0;

        var lines = content.Split('\n');
        foreach (var line in lines)
        {
            if (line.StartsWith("**Views:**"))
                int.TryParse(line.Replace("**Views:**", "").Trim(), out views);
            else if (line.StartsWith("**Reactions:**"))
                int.TryParse(line.Replace("**Reactions:**", "").Trim(), out reactions);
            else if (line.StartsWith("**Forwards:**"))
                int.TryParse(line.Replace("**Forwards:**", "").Trim(), out forwards);
        }

        return (views, reactions, forwards);
    }

    static string? ExtractTitle(string content)
    {
        var lines = content.Split('\n');

        foreach (var line in lines)
        {
            var trimmedLine = line.Trim();

            if (trimmedLine.StartsWith("**Created:**") ||
                trimmedLine.StartsWith("**Edited:**") ||
                trimmedLine.StartsWith("**Views:**") ||
                trimmedLine.StartsWith("**Reactions:**") ||
                trimmedLine.StartsWith("**Forwards:**") ||
                trimmedLine.StartsWith("**LLM Processed:**") ||
                trimmedLine.StartsWith("![") ||
                string.IsNullOrWhiteSpace(trimmedLine))
            {
                continue;
            }

            return trimmedLine;
        }

        return null;
    }

    static string ExtractPreview(string content, int maxLength = 150)
    {
        var lines = content.Split('\n');
        var contentBuilder = new StringBuilder();
        bool skippedMetadata = false;
        bool skippedTitle = false;

        foreach (var line in lines)
        {
            var trimmedLine = line.Trim();

            if (!skippedMetadata && (trimmedLine.StartsWith("**Created:**") ||
                trimmedLine.StartsWith("**Edited:**") ||
                trimmedLine.StartsWith("**Views:**") ||
                trimmedLine.StartsWith("**Reactions:**") ||
                trimmedLine.StartsWith("**Forwards:**") ||
                trimmedLine.StartsWith("**LLM Processed:**") ||
                trimmedLine.StartsWith("![") ||
                string.IsNullOrWhiteSpace(trimmedLine)))
            {
                continue;
            }

            if (!skippedMetadata)
            {
                skippedMetadata = true;
            }

            if (!skippedTitle)
            {
                skippedTitle = true;
                continue;
            }

            if (!string.IsNullOrWhiteSpace(trimmedLine) && !trimmedLine.StartsWith("#"))
            {
                contentBuilder.AppendLine(trimmedLine);
                if (contentBuilder.Length > maxLength)
                    break;
            }
        }

        var preview = contentBuilder.ToString().Trim();
        if (preview.Length > maxLength)
        {
            preview = preview.Substring(0, maxLength) + "...";
        }

        return string.IsNullOrEmpty(preview) ? "No preview available" : preview;
    }

    static string ConvertMarkdownToHtml(string markdown)
    {
        var lines = markdown.Split('\n');
        var htmlBuilder = new StringBuilder();
        bool skippedMetadata = false;

        foreach (var line in lines)
        {
            var trimmedLine = line.Trim();

            if (!skippedMetadata && (trimmedLine.StartsWith("**Created:**") ||
                trimmedLine.StartsWith("**Edited:**") ||
                trimmedLine.StartsWith("**Views:**") ||
                trimmedLine.StartsWith("**Reactions:**") ||
                trimmedLine.StartsWith("**Forwards:**") ||
                trimmedLine.StartsWith("**LLM Processed:**")))
            {
                continue;
            }

            if (!skippedMetadata && !string.IsNullOrWhiteSpace(trimmedLine))
            {
                skippedMetadata = true;
            }

            if (skippedMetadata)
            {
                if (trimmedLine.StartsWith("!["))
                {
                    var match = System.Text.RegularExpressions.Regex.Match(trimmedLine, @"!\[.*?\]\((.*?)\)");
                    if (match.Success)
                    {
                        var imagePath = match.Groups[1].Value;
                        htmlBuilder.AppendLine($"<img src='../../{imagePath}' alt='Post Image' style='max-width: 100%; height: auto; border-radius: 8px; margin: 15px 0;'>");
                    }
                    continue;
                }

                var htmlLine = trimmedLine;

                htmlLine = System.Text.RegularExpressions.Regex.Replace(htmlLine, @"\*\*(.*?)\*\*", "<strong>$1</strong>");
                htmlLine = System.Text.RegularExpressions.Regex.Replace(htmlLine, @"\*(.*?)\*", "<em>$1</em>");

                if (htmlLine.StartsWith("### "))
                    htmlLine = $"<h3>{htmlLine.Substring(4)}</h3>";
                else if (htmlLine.StartsWith("## "))
                    htmlLine = $"<h2>{htmlLine.Substring(3)}</h2>";
                else if (htmlLine.StartsWith("# "))
                    htmlLine = $"<h1>{htmlLine.Substring(2)}</h1>";

                if (string.IsNullOrWhiteSpace(htmlLine))
                    htmlBuilder.AppendLine("<br>");
                else if (!htmlLine.StartsWith("<h") && !htmlLine.StartsWith("<img"))
                    htmlBuilder.AppendLine($"<p>{htmlLine}</p>");
                else
                    htmlBuilder.AppendLine(htmlLine);
            }
        }

        return htmlBuilder.ToString();
    }

    static DateTime ExtractCreationDate(string content)
    {
        var lines = content.Split('\n');
        foreach (var line in lines)
        {
            if (line.StartsWith("**Created:**"))
            {
                var dateText = line.Replace("**Created:**", "").Trim();
                if (dateText.EndsWith(" UTC"))
                {
                    dateText = dateText.Substring(0, dateText.Length - 4).Trim();
                }

                if (DateTime.TryParseExact(dateText, "yyyy-MM-dd HH:mm:ss", null, DateTimeStyles.None, out var date))
                {
                    return date;
                }

                if (DateTime.TryParse(dateText, out var date2))
                {
                    return date2;
                }
            }
        }
        return DateTime.MinValue;
    }

    static long GetDirectorySize(string dirPath)
    {
        var dir = new DirectoryInfo(dirPath);
        return dir.GetFiles("*", SearchOption.AllDirectories).Sum(file => file.Length);
    }

}
