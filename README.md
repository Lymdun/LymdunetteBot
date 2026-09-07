# Custom Lymdunistan Server Discord Bot

## Requirements (Windows only)
- FFmpeg  
  This bot uses FFmpeg to convert stream to Opus in the Ogg format. You can download pre-built FFmpeg binaries [here](https://ffmpeg.org/download.html). You can either put the downloaded binary in the bot's working directory or add `ffmpeg` to PATH.

## Installation
Create a `.env` file in parent directory containing your `DISCORD_TOKEN` value.

## Fateful Bullet Steam notifications

The bot checks [Fateful Bullet's Steam announcements](https://store.steampowered.com/news/app/3055070?l=french) every five minutes and posts new announcements and patch notes to Discord channel `1372263024638824669`. Messages include the announcement title, a short summary, publication time, and a clickable Steam link. The service starts automatically once Discord is ready.

The first successful check records existing announcements without posting them. Subsequent checks send unseen announcements oldest first, including updates published while the bot was offline. Failed requests and failed Discord sends are retried on the next check. The bot needs **View Channel**, **Send Messages**, and **Embed Links** permissions in the destination channel.

Notification history is saved to `data/steam-news-3055070.json` relative to the working directory. Set `STEAM_NEWS_STATE_PATH` in `.env` to override this path. Keep the file between deployments; Docker Compose mounts a persistent `steam-news-data` volume at `/app/data` for this purpose. Run only one bot instance against this state file. A missing file establishes a new baseline; invalid state is logged and retried without overwriting it. If the process stops after Discord accepts a message but before its state is saved, that announcement can be sent again on restart.

This feature uses Steam's public [GetNewsForApp API](https://partner.steamgames.com/doc/webapi/ISteamNews#GetNewsForApp) and requires no Steam API key. It requests French, but Steam may return the original publication language. It tracks official announcements and patch notes, not edits to store descriptions, prices, release dates, or game files without an accompanying announcement.
