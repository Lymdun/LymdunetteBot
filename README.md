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

## Automatic deployment

GitHub Actions publishes the .NET 8 Linux release and builds its Docker image on
pull requests and pushes to `main` or `master`. Only pushes deploy: the runner
streams `docker save | gzip` over SSH, the VPS imports it with `docker load`, and
Compose recreates only `lymdunette-bot`. The VPS does not compile or build images.
Runs are serialized, and superseded branch revisions are skipped.

The workflow uses these repository Actions secrets:

| Secret | Value |
| --- | --- |
| `LYMDUNETTEBOT_DEPLOY_HOST` | VPS hostname or IP address |
| `LYMDUNETTEBOT_DEPLOY_SSH_KEY` | Dedicated Ed25519 private key for this repository's CD |
| `LYMDUNETTEBOT_DEPLOY_KNOWN_HOSTS` | Verified `<deploy-host> ssh-ed25519 ...` host-key entry matching the deployment host |

The private key stays out of Git. Obtain the public host key through a trusted
administrator connection; the workflow uses strict SSH host verification.

### Server setup

The server needs Bash, Docker with Compose v2, and `flock`. Existing production
configuration stays in `/var/lymdunbot/docker-compose.yml`, with `.env` and
`./data:/app/data` preserved. Images are tagged `lymdunettebot-cd:<commit SHA>` and
selected through `/var/lymdunbot/docker-compose.cd.yml`.

Install `scripts/deploy-production.sh` as `/usr/local/sbin/lymdunettebot-cd`, owned
by root with mode `755`. Keep that installed copy synchronized when changing the
script. The dedicated public key in `/root/.ssh/authorized_keys` must use:

```text
restrict,command="/usr/local/sbin/lymdunettebot-cd" ssh-ed25519 <public-key> lymdunettebot-github-actions
```

The key permits only `check` and `deploy <commit SHA>`, with shell access and
forwarding disabled. Verify it using `ssh -i <private-key> -o IdentitiesOnly=yes
root@<deploy-host> check`. To rotate it, authorize a replacement public key,
update the private-key secret, verify access, then remove the old key entry.

### Startup and recovery

After recreation, the script waits up to 60 seconds for scheduler readiness and
requires the bot to be running with zero restarts. A failed startup fails the
workflow; recovery is manual.

To redeploy a previous image, set `image:` in
`/var/lymdunbot/docker-compose.cd.yml` to its retained tag, then run:

```sh
cd /var/lymdunbot
docker compose --project-name lymdunbot \
  -f docker-compose.yml -f docker-compose.cd.yml \
  up -d --no-deps --force-recreate --no-build lymdunette-bot
```

Check the bot's logs afterward. Keep the Compose override in manual operations,
and periodically remove old images once they are no longer needed for recovery.

