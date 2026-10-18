# Security Policy

Telescribe is a personal-use console tool, so only the latest release on the `main` branch receives security fixes.

## Reporting a vulnerability

Please **do not** open a public issue for security problems. Use GitHub's private reporting instead:
[Report a vulnerability](https://github.com/aminmesbahi/telescribe/security/advisories/new).

Include what you found, how to reproduce it, and the version/commit you tested. You can expect an acknowledgement
within a week; fixes are released on `main` and credited in the release notes unless you prefer otherwise.

## Things worth knowing

- Telescribe handles **your Telegram API credentials and login session** (`WTelegram.session`). Treat that file like a
  password: it is git-ignored and must never be committed or shared.
- Put secrets in [user secrets](https://learn.microsoft.com/aspnet/core/security/app-secrets) or environment variables
  (`TelegramConfig__ApiHash`, `TelegramConfig__Llm__ApiKey`, ...) rather than in a committed `appsettings.json`.
- Exported channel content is treated as untrusted when a static site is generated: titles and text are HTML-escaped,
  raw HTML in posts is not passed through, and only `http`, `https`, `mailto` and `tel` links are kept.
