# Custom Lymdunistan Server Discord Bot

## Requirements (Windows only)
- FFmpeg  
  This bot uses FFmpeg to convert stream to Opus in the Ogg format. You can download pre-built FFmpeg binaries [here](https://ffmpeg.org/download.html). You can either put the downloaded binary in the bot's working directory or add `ffmpeg` to PATH.

## Installation
Create a `.env` file in parent directory containing your `DISCORD_TOKEN` value.

The leaderboard monitor checks LiveBench and DeepSWE every 10 minutes and stores its deduplication state in `data/leaderboard-monitor-state.json`. The Docker Compose configuration persists this directory across container rebuilds. Set `LEADERBOARD_POLL_INTERVAL_MINUTES` or `LEADERBOARD_STATE_PATH` to override these defaults.
