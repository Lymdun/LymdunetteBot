# Custom Lymdunistan Server Discord Bot

## Requirements
- .NET 10 SDK to build the bot, or the .NET 10 runtime to run a published build.
- libsodium for voice support (libsodium.dll in the bot's working directory on Windows).
- FFmpeg  
  This bot uses FFmpeg to convert stream to Opus in the Ogg format. You can download pre-built FFmpeg binaries [here](https://ffmpeg.org/download.html). You can either put the downloaded binary in the bot's working directory or add `ffmpeg` to PATH.

The Docker image includes the .NET 10 runtime, libsodium, and FFmpeg.

## Installation
Create a `.env` file in parent directory containing your `DISCORD_TOKEN` value.

The leaderboard monitor checks LiveBench and DeepSWE every 30 minutes and stores its deduplication state in `data/leaderboard-monitor-state.json`. The Docker Compose configuration persists this directory across container rebuilds.
