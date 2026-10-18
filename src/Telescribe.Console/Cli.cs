using System.CommandLine;

namespace Telescribe.Console;

/// <summary>The command-line interface. Running with no arguments starts the interactive menu instead.</summary>
internal static class Cli
{
    public static RootCommand Create(Workflows workflows)
    {
        var root = new RootCommand("Telescribe - export a Telegram channel, enrich it with AI, publish a static site or WordPress. Run without arguments for the interactive menu.");

        root.Subcommands.Add(Simple("export", "Download the full channel history.", workflows.ExportAsync));
        root.Subcommands.Add(Simple("update", "Fetch new posts and refresh statistics of recent ones.", workflows.UpdateAsync));
        root.Subcommands.Add(Simple("report", "Generate the HTML analytics report.", workflows.ReportAsync, "reports"));

        var force = new Option<bool>("--force") { Description = "Process posts again even if they already have a title and hashtags." };
        var limit = new Option<int>("--limit") { Description = "Process at most this many posts in this run (0 = all).", DefaultValueFactory = _ => 0 };
        var ai = new Command("ai", "Generate titles and hashtags with the configured LLM.");
        ai.Options.Add(force);
        ai.Options.Add(limit);
        ai.SetAction((parse, ct) => Run(workflows.EnrichAsync(parse.GetValue(force), parse.GetValue(limit), ct)));
        root.Subcommands.Add(ai);

        var output = new Option<string?>("--output", "-o") { Description = "Folder to write the site to (default: StaticSite:OutputDirectory, or exports/site)." };
        var clean = new Option<bool>("--clean") { Description = "Delete the previously generated site first and rebuild from scratch." };
        var noOpen = new Option<bool>("--no-open") { Description = "Do not open the site in a browser afterwards." };
        var build = new Command("build", "Build (or refresh) the static website.");
        build.Aliases.Add("static");
        build.Options.Add(output);
        build.Options.Add(clean);
        build.Options.Add(noOpen);
        build.SetAction((parse, ct) => Run(workflows.BuildSiteAsync(parse.GetValue(output), parse.GetValue(clean), !parse.GetValue(noOpen) && workflows.OpenAfterBuild, ct)));
        root.Subcommands.Add(build);

        var wpForce = new Option<bool>("--force") { Description = "Update every post, even if its content is unchanged." };
        var dryRun = new Option<bool>("--dry-run") { Description = "Show what would be created or updated without contacting WordPress." };
        var wp = new Command("wp", "Publish posts to WordPress (creates new posts, updates changed ones).");
        wp.Aliases.Add("wordpress");
        wp.Options.Add(wpForce);
        wp.Options.Add(dryRun);
        wp.SetAction((parse, ct) => Run(workflows.WordPressAsync(parse.GetValue(wpForce), parse.GetValue(dryRun), ct)));
        root.Subcommands.Add(wp);

        var archiveOutput = new Option<string?>("--output", "-o") { Description = "ZIP file to create (default: exports/archives/telescribe-archive_<time>.zip)." };
        var archive = new Command("archive", "Pack posts, media and statistics into one ZIP for backup.");
        archive.Options.Add(archiveOutput);
        archive.SetAction((parse, ct) => Run(workflows.ArchiveAsync(parse.GetValue(archiveOutput), ct)));
        root.Subcommands.Add(archive);

        var syncAi = new Option<bool>("--ai") { Description = "Also generate titles and hashtags for new posts." };
        var syncWp = new Option<bool>("--wordpress") { Description = "Also publish new and changed posts to WordPress." };
        var deploy = new Option<string?>("--deploy") { Description = "Copy the built site to this folder (web root, Git checkout, ...)." };
        var mirror = new Option<bool>("--mirror") { Description = "With --deploy: also delete files in the target that are not in the site (dot-files and CNAME are kept)." };
        var open = new Option<bool>("--open") { Description = "Open the site in a browser afterwards." };
        var sync = new Command("sync", "One-shot refresh: update from Telegram (or export the first time), optional AI/WordPress, rebuild the site, optional deploy.");
        sync.Aliases.Add("update-site");
        sync.Options.Add(syncAi);
        sync.Options.Add(syncWp);
        sync.Options.Add(deploy);
        sync.Options.Add(mirror);
        sync.Options.Add(open);
        sync.SetAction((parse, ct) => Run(workflows.SyncAsync(
            new SyncOptions(parse.GetValue(syncAi), parse.GetValue(syncWp), parse.GetValue(deploy), parse.GetValue(mirror), parse.GetValue(open)), ct)));
        root.Subcommands.Add(sync);

        return root;
    }

    private static Command Simple(string name, string description, Func<CancellationToken, Task<bool>> run, params string[] aliases)
    {
        var command = new Command(name, description);
        foreach (var alias in aliases)
        {
            command.Aliases.Add(alias);
        }

        command.SetAction((_, ct) => Run(run(ct)));
        return command;
    }

    private static async Task<int> Run(Task<bool> operation) => await operation ? 0 : 1;
}
