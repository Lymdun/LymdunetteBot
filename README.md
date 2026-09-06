# Custom Lymdunistan Server Discord Bot

## Requirements (Windows only)
- FFmpeg  
  This bot uses FFmpeg to convert stream to Opus in the Ogg format. You can download pre-built FFmpeg binaries [here](https://ffmpeg.org/download.html). You can either put the downloaded binary in the bot's working directory or add `ffmpeg` to PATH.

## Installation
Create a `.env` file in parent directory containing your `DISCORD_TOKEN` value.

## X link previews

The bot replaces new server messages containing `x.com` links with webhook messages using the sender's display name and avatar. It preserves the surrounding text and attachments, changes the links to `fixupx.com`, and deletes the original only after Discord confirms the repost. Mentions are not pinged again. Discord still labels webhook messages as bot/app messages. Replies include a link to the referenced message, and long messages are split into multiple posts. Messages with stickers, polls, or components are left intact.

Each post's language is checked using the public FxTwitter API; `/en` is appended for languages other than French or English. Unknown languages or failed lookups still produce a fixed link without translation. No X API key is required. Translation is provided by FixupX via its language suffix, so its availability and provider depend on FixupX.

Enable the **Message Content Intent** in the Discord developer portal and grant the bot View Channel, Read Message History, Send Messages, Manage Webhooks, Manage Messages, Attach Files, and Embed Links permissions. Threads also require Send Messages in Threads. The bot creates/reuses its own webhook in each channel (the parent channel for threads). If reposting fails, the original is kept; if deletion fails or the original was edited during reposting, both copies may remain.

Run the offline regression checks with `dotnet run --project Tests/XLinkTests.csproj`.
