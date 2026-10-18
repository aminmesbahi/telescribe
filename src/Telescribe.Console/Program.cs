global using static System.Console;
using Microsoft.Extensions.Configuration;
using System.CommandLine;
using System.Reflection;
using System.Text;
using Telescribe.Core.Models;

namespace Telescribe.Console;

public static class Program
{
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

        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppDomain.CurrentDomain.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddUserSecrets(Assembly.GetExecutingAssembly(), optional: true)
            .AddEnvironmentVariables()
            .Build();

        // --help and --version should work before anything is configured.
        if (args.Any(a => a is "-h" or "--help" or "-?" or "/?" or "--version"))
        {
            return await Cli.Create(new Workflows(new TelegramConfig())).Parse(args).InvokeAsync();
        }

        WriteLine("🔭 Telescribe - Telegram Channel Exporter");

        var config = LoadConfiguration(configuration);
        if (config == null)
        {
            WriteLine("❌ Configuration validation failed. Please check your settings.");
            return 1;
        }

        var workflows = new Workflows(config);

        if (args.Length > 0)
        {
            return await Cli.Create(workflows).Parse(args).InvokeAsync();
        }

        return await RunMenuAsync(workflows);
    }

    private static async Task<int> RunMenuAsync(Workflows workflows)
    {
        using var cancel = new CancellationTokenSource();
        CancelKeyPress += (_, e) =>
        {
            // First Ctrl+C stops the running operation; the menu stays open.
            if (!cancel.IsCancellationRequested)
            {
                e.Cancel = true;
                cancel.Cancel();
            }
        };

        while (true)
        {
            DisplayMainMenu();
            var input = ReadLine()?.Trim();
            if (input is null)
            {
                return 0; // input closed
            }

            var token = cancel.Token;
            switch (input.ToLowerInvariant())
            {
                case "1":
                    await workflows.ExportAsync(token);
                    break;
                case "2":
                    await workflows.UpdateAsync(token);
                    break;
                case "3":
                    await workflows.EnrichAsync(force: false, limit: 0, token);
                    break;
                case "4":
                    await workflows.WordPressAsync(force: false, dryRun: false, token);
                    break;
                case "5":
                    await workflows.ReportAsync(token);
                    break;
                case "6":
                    await workflows.BuildSiteAsync(output: null, clean: false, open: workflows.OpenAfterBuild, token);
                    break;
                case "8":
                    await workflows.SyncAsync(new SyncOptions(Ai: false, WordPress: false, DeployTarget: null, Mirror: false, Open: workflows.OpenAfterBuild), token);
                    break;
                case "9":
                    await workflows.ArchiveAsync(output: null, token);
                    break;
                case "7":
                case "0":
                case "exit":
                case "quit":
                    WriteLine("👋 Goodbye! Thanks for using Telescribe!");
                    return 0;
                default:
                    WriteLine("❌ Invalid choice. Please enter a number from 1-9.");
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
        WriteLine("   🤖 3  AI Content Processing     - Titles and hashtags for posts");
        WriteLine("   🌐 4  WordPress Integration     - Publish new and changed posts");
        WriteLine("   📊 5  Analytics Reports         - Charts and engagement insights");
        WriteLine("   🏗️  6  Static Website Builder    - Build or refresh the website");
        WriteLine("   🔁 8  Sync                      - Update from Telegram, then rebuild the site");
        WriteLine("   📦 9  Archive                   - Pack posts and media into a ZIP");
        WriteLine("   🚪 7  Exit Application          - Close Telescribe");
        WriteLine();
        WriteLine("═══════════════════════════════════════════════════════════════");
        Write("🎯 Enter your choice (1-9): ");
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
}
