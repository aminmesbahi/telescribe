# Automating Telescribe

`telescribe sync` does everything needed to keep a site current in one non-interactive run:

1. fetches new posts from Telegram (a full export the very first time) and refreshes the statistics of recent ones,
2. optionally generates titles and hashtags (`--ai`) and publishes to WordPress (`--wordpress`),
3. rebuilds the static site incrementally,
4. optionally copies it somewhere (`--deploy <folder>`, add `--mirror` to also delete files that are gone).

The process exits with `0` on success and `1` on failure, so schedulers can alert on it.

```bash
dotnet run --project Telescribe.Console -- sync --ai --deploy /var/www/mychannel --mirror
```

## One-time setup: log in interactively

Telegram asks for a verification code (and your 2FA password, if set) the first time. Run `export` once in a terminal.
The login is then stored in a session file (`WTelegram.session`, or `TelegramConfig:SessionPath`), and later runs are
silent. **Treat the session file like a password**: anyone who has it can act as your Telegram account. Never commit it.

Put the other secrets in environment variables or user secrets, not in a committed file:

```bash
export TelegramConfig__ApiId=12345678
export TelegramConfig__ApiHash=...
export TelegramConfig__PhoneNumber=+1234567890
export TelegramConfig__ChannelId=@yourchannel
export TelegramConfig__Llm__ApiKey=...
```

## Linux/macOS: cron

```cron
# every day at 06:15
15 6 * * *  cd /opt/telescribe && ./Telescribe.Console sync --ai --deploy /var/www/mychannel --mirror >> sync.log 2>&1
```

## Windows: Task Scheduler

```text
Action:    Start a program
Program:   C:\tools\telescribe\Telescribe.Console.exe
Arguments: sync --ai --deploy D:\sites\mychannel --mirror
Start in:  C:\tools\telescribe
```

"Start in" matters: the `exports` folder and the session file are relative to the working directory.

## Docker

```bash
docker build -f src/Telescribe.Console/Dockerfile -t telescribe src
docker run -it -v telescribe-data:/app/exports telescribe export      # first login
docker run --rm -v telescribe-data:/app/exports \
    -e TelegramConfig__ApiId=... -e TelegramConfig__ApiHash=... \
    -e TelegramConfig__PhoneNumber=... -e TelegramConfig__ChannelId=@yourchannel \
    -e TelegramConfig__SessionPath=/app/exports/telegram.session \
    telescribe sync
```

Set `TelegramConfig__SessionPath` to a file inside the mounted volume so the login survives between runs.

## GitHub Actions and GitHub Pages

This needs the Telegram session on the runner, which means storing it as a repository secret. That is a real trade-off:
a leaked secret gives access to your Telegram account, so only do this in a private repository and prefer a dedicated
account that is a member of the channel. The workflow below restores the session and the previous export from the
cache, syncs, and publishes `exports/site`:

```yaml
name: Sync channel
on:
  schedule: [{ cron: "15 6 * * *" }]
  workflow_dispatch:

permissions: { contents: read, pages: write, id-token: write }

jobs:
  sync:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v7
      - uses: actions/setup-dotnet@v5
        with: { global-json-file: global.json }

      - uses: actions/cache@v4
        with:
          path: src/Telescribe.Console/exports
          key: telescribe-exports-${{ github.run_id }}
          restore-keys: telescribe-exports-

      - name: Restore Telegram session
        run: |
          mkdir -p src/Telescribe.Console/exports
          echo "$TELEGRAM_SESSION_B64" | base64 -d > src/Telescribe.Console/exports/telegram.session
        env: { TELEGRAM_SESSION_B64: "${{ secrets.TELEGRAM_SESSION_B64 }}" }

      - name: Sync
        working-directory: src/Telescribe.Console
        run: dotnet run -c Release -- sync
        env:
          TelegramConfig__ApiId: ${{ secrets.TELEGRAM_API_ID }}
          TelegramConfig__ApiHash: ${{ secrets.TELEGRAM_API_HASH }}
          TelegramConfig__PhoneNumber: ${{ secrets.TELEGRAM_PHONE }}
          TelegramConfig__ChannelId: "@yourchannel"
          TelegramConfig__SessionPath: exports/telegram.session
          TelegramConfig__StaticSite__SiteBaseUrl: "https://you.github.io/yoursite"

      - uses: actions/upload-pages-artifact@v3
        with: { path: src/Telescribe.Console/exports/site }
      - uses: actions/deploy-pages@v4
```

Create the secret with `base64 -w0 telegram.session` after logging in locally. The cache keeps media between runs so
only new files are downloaded; if the cache is ever evicted the next run simply re-exports everything.
