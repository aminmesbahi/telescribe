# 🔭 Telescribe

[![Qodana](https://qodana.cloud/projects/NV8ow/reports/latest/badge)](https://qodana.cloud/projects/NV8ow)
[![Security Policy](https://img.shields.io/badge/security-policy-blue?logo=github&logoColor=white)](https://github.com/aminmesbahi/telescribe/blob/main/SECURITY.md)

**Telescribe** is a .NET console application for managing content from your Telegram channel. Export your posts, enrich them with AI, publish a static website, or push everything to WordPress.

It is built for creators who want real control over their content rather than leaving it locked inside Telegram.

## Summary

Telescribe turns your Telegram channel into a content hub. It exports posts with media and engagement metadata, optionally enhances them with AI (titles, hashtags, summaries), and supports several publishing workflows. You can generate a static website, sync posts to WordPress, or just keep a local searchable archive.

## Features

### 📥 **Telegram Export**
- Export the **full history** of any Telegram channel you can access (public channels by `@username`, `t.me/` link or numeric id; no cap on post count)
- Download media files (images, videos, documents, audio); files already on disk are never re-downloaded, an optional `MaxMediaSizeMB` skips huge documents, and downloads are written atomically
- Albums (several photos/videos posted together) are merged into a single post
- Exports polls with vote counts and visual bars
- Rich text formatting: bold, italic, underline, strikethrough, spoiler, code blocks, links, blockquotes
- Article posts partially supported (Instant View content extracted, but native Telegram article format may fail)
- Incremental updates: fetch new posts and refresh statistics for recent ones (`RefreshWindowDays`, default 30)
- Captures views, reactions, and forward counts per post
- Exports to Markdown files for easy processing

### 🤖 **AI Content Processing**
- **Providers**: OpenAI, DeepSeek and Ollama (or any OpenAI-compatible endpoint via `Llm:BaseUrl`), all through one `Microsoft.Extensions.AI` client
- **Titles and hashtags**: generated per post in your language (`Llm:Language`), including Persian hashtags; Telegram articles keep their own title
- **Safe to re-run**: the pristine export stays in `exports/raw`; enriched posts are skipped next time, and a refresh from Telegram keeps the AI title and tags
- **Robust**: timeouts, retries with backoff for rate limits and network errors, reasoning-model aware (`gpt-5`, `o*`), and post text is passed as data, not instructions
- Run it with `ai` (options `--force`, `--limit N`) or `sync --ai`

### 🏗️ **Static Website Generation**
- Generate a responsive static website from your exported posts, built **incrementally** into one stable folder (`exports/site`, or `StaticSite:OutputDirectory`)
- **Multi-Language Templates**: English (`en`) and Persian/Farsi (`fa`) built in, plus custom templates
- **Search, Sort, and Pagination**: All client-side with no external dependencies
- **Safe by default**: post text is rendered with a real Markdown parser (Markdig); raw HTML is escaped, only `http`, `https`, `mailto` and `tel` links are kept, and template values are HTML-escaped
- **Every post gets a page**: `MaxPostsInIndex` only limits how many posts the index page lists (use `0` for all)
- **Tag pages**: hashtags in your posts plus AI tags get a page each (`GenerateTagPages`, `MinPostsPerTag`) and are linked from the posts
- **Atom feed** (`feed.xml`) announced in every page's `<head>` when `SiteBaseUrl` is set
- **Players for media**: local videos and audio files get a native player (the download link stays)
- **Forwarded posts and replies** keep their "forwarded from" / "in reply to" note
- **SEO Ready**: Sitemap.xml, robots.txt, canonical URLs, Open Graph/Twitter tags and schema.org JSON-LD (set `AuthorName`, `PublisherName`, `SameAs`)
- **About Page**: Rendered from template if your template includes one
- **Deploy**: `sync --deploy <folder>` copies only changed files (add `--mirror` to remove files that disappeared)

### 📊 **Analytics and Reporting**
- HTML report with totals, the most popular, recent, reacted and forwarded posts
- **Charts** (inline SVG, no scripts): posts per month, when you post (weekday × hour heatmap), reactions by emoji, and total views over time
- **Growth history**: every export/update appends a snapshot to `stats-history.jsonl`, because Telegram only reports current totals
- **Engagement ranking**: (reactions + forwards) per view

### 🌐 **WordPress Integration**
- Publishes new posts and updates changed ones (statistics changes alone never trigger an update)
- **Media upload** once per file, remembered in `exports/wordpress_state.json`
- **Category and tag management** with tags looked up in one request
- Same safe Markdown rendering as the static site; `wp --dry-run` shows what would happen without contacting WordPress
- State is saved after every post, so an interrupted run never creates duplicates

### ⚙️ **Advanced Features**
- **Template System**: Flexible `{{placeholder}}` engine, easy to extend
- **Media Handling**: Copies Telegram media into the generated site; existing files are never re-downloaded
- **Empty Post Filtering**: Skip polls and media-only posts via config
- **Backups**: `archive` packs posts, media and statistics into one ZIP
- **Automation**: `sync` does everything in one non-interactive run; see [docs/automation.md](docs/automation.md) for cron, Task Scheduler, Docker and GitHub Actions


## Requirements

### System Requirements
- **.NET 11 SDK** (the repository pins a release candidate in [global.json](global.json); newer .NET 11 builds are accepted)

### Telegram API Setup
- **Telegram API credentials** from [https://my.telegram.org/](https://my.telegram.org/)
  - API ID
  - API Hash
  - Phone number (international format)
- **Channel access** (you must be a member of the target channel)

### Optional Dependencies
- **AI Services** (choose one):
  - OpenAI API key for GPT models
  - DeepSeek API key for cost-effective processing
  - Ollama local installation for offline processing
- **WordPress site** with application password for publishing

## Screenshots

### Main Menu
![Menu](docs/screenshots/menu.png)

### Static Site Index Page
![Static Index](/docs/screenshots/static-index.png)

### Static Site Post Page
![Static Post](/docs/screenshots/static-post.png)

### Channel Analytics Report
![Reports](/docs/screenshots/reports.png)

---

## How to Run

### 🔨 Coming soon: Available as a .NET tool!

### 1. Clone, Build and Test
```bash
git clone https://github.com/aminmesbahi/telescribe.git
cd telescribe/src
dotnet build
dotnet test --solution Telescribe.slnx
```
The test runner is [Microsoft.Testing.Platform](https://learn.microsoft.com/dotnet/core/testing/microsoft-testing-platform-intro) (opted in via `global.json`).

### 2. Configuration Setup
Copy `appsettings.example.json` to `appsettings.json` in `Telescribe.Console` and fill in your values (see the example file for every option, including `MaxMediaSizeMB`, `RefreshWindowDays` and the structured-data fields under `StaticSite`).

```json
{
  "TelegramConfig": {
    "PhoneNumber": "+1234567890",
    "ChannelId": "@yourchannel",
    "ApiId": 12345678,
    "ApiHash": "your-api-hash",
    "StaticSite": {
      "SiteTitle": "My Telegram Archive",
      "Subtitle": "Content from my Telegram channel",
      "TemplateName": "en",
      "MaxPostsInIndex": 50,
      "OpenBrowserAfterGeneration": true,
      "SkipEmptyContentPosts": false,
      "SiteBaseUrl": "https://yoursite.com",
      "AuthorName": "Your Name"
    }
  }
}
```

`ChannelId` can be `@name`, `name`, a `t.me/name` link, a numeric id (a Bot-API style `-100…` id is fine), or the channel title.

**Keep secrets out of the file.** Values can also come from [user secrets](https://learn.microsoft.com/aspnet/core/security/app-secrets) or environment variables, which override `appsettings.json`:

```bash
dotnet user-secrets set "TelegramConfig:ApiHash" "your-api-hash" --project Telescribe.Console
# or
export TelegramConfig__ApiHash=your-api-hash
```

The AI (`Llm`) and WordPress options are bound the same way. Their keys are `Llm:*` and `WordPress:SiteUrl`, `Username`, `ApplicationPassword`, `CategoryName`, `EnableUpload`.

### 3. Run the Application

Interactive menu:
```bash
cd Telescribe.Console
dotnet run
```

Or run a command directly (exit code is non-zero on failure; `--help` works on every command):

| Command | What it does |
|---|---|
| `export` | Download the full channel history |
| `update` | Fetch new posts and refresh the statistics of recent ones |
| `ai [--force] [--limit N]` | Generate titles and hashtags |
| `build [-o DIR] [--clean] [--no-open]` | Build or refresh the static site (alias `static`) |
| `report` | Generate the analytics report (alias `reports`) |
| `wp [--dry-run] [--force]` | Publish new and changed posts to WordPress (alias `wordpress`) |
| `archive [-o FILE]` | Pack posts, media and statistics into a ZIP |
| `sync [--ai] [--wordpress] [--deploy DIR [--mirror]] [--open]` | Update from Telegram, optional AI/WordPress, rebuild the site, optional deploy (alias `update-site`) |

```bash
dotnet run --project Telescribe.Console -- sync --ai --deploy /var/www/mychannel
```

The first login asks for the verification code Telegram sends you (and your 2FA password, typed without echo if you use one). The session is then stored in `WTelegram.session` (or `TelegramConfig:SessionPath`), which is git-ignored; keep it private.

`build --clean` deletes the previous site first, but only if Telescribe created it (it checks for a marker file), so a wrongly configured output folder cannot wipe your documents.

### 4. Menu Options
1. **Export Channel Posts** - Download all posts from Telegram
2. **Update Existing Posts** - Sync new posts since the last export
3. **AI Content Processing** - Generate titles and hashtags
4. **WordPress Integration** - Publish new and changed posts
5. **Analytics Reports** - Charts and engagement insights
6. **Static Website Builder** - Build or refresh the site
7. **Exit Application**
8. **Sync** - Update from Telegram, then rebuild the site
9. **Archive** - Pack posts and media into a ZIP

## Templates

Templates live in `src/Telescribe.Console/templates/{name}/` and consist of plain HTML files with `{{placeholder}}` variables. The built-in templates are `en` (English) and `fa` (Persian/RTL). To create a custom template, copy an existing folder and adjust the HTML and CSS files.

- Placeholders are replaced in a single pass, and plain values are **HTML-escaped**. Only values the generator marks as trusted markup (the post body, meta tags, structured data) are inserted as-is.
- An optional `template.json` describes the template (`language`, `direction`, `openGraphLocale`, `aboutTitleFormat`). Without one, names starting with `fa` are treated as Persian and everything else as English.

## Roadmap

### Version 1.0 (Current)
- [x] AI content processing (titles, hashtags)
- [x] WordPress publishing
- [x] Command-line interface and one-shot `sync`
- [x] Tags, Atom feed, charts and growth history
- [ ] Stable release (waiting for the .NET 11 GA)

### Version 2.0 (Planned)
- [ ] Real-time sync (a long-running watcher instead of scheduled `sync`)
- [ ] Full-text search index for the static site (e.g. Pagefind)
- [ ] Export of channel comments (discussion group)
- [ ] Database support for large archives
- [ ] REST API for programmatic access
- [ ] Web dashboard

### Version 3.0 (Ideas)
- [ ] Multi-channel support
- [ ] Content scheduling
- [ ] Plugin system for custom extensions
- [ ] Hosted/cloud deployment

## Known Issues

- **LLM and WordPress**: implemented and unit-tested, but not yet exercised against live services by the maintainers; try `wp --dry-run` and `ai --limit 5` first
- **Stored posts are Markdown files**: statistics and tags are read back from the file header; a per-post JSON store is planned
- **Article Posts**: Native Telegram article format is not fully supported. Some articles may export as empty or only show metadata. The Instant View page parser works for many cases but not all.
- **API Rate Limits**: Telegram API has built-in rate limiting; large channels may take a while (the full history is now fetched, not just the latest 1,000 messages)
- **Existing exports**: albums that an older version exported as separate posts are merged the next time you run an update that covers them; run a full export to rebuild everything

## License

MIT License. See [LICENSE](LICENSE) for details.

## Contributing

Contributions, issues, and feature requests are welcome. Feel free to open a pull request or file an issue on GitHub.

## Support

- **GitHub Issues**: [Create an issue](https://github.com/aminmesbahi/telescribe/issues)
