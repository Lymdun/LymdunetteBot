# Custom Lymdunistan Server Discord Bot

## Requirements (Windows only)
- FFmpeg  
  This bot uses FFmpeg to convert stream to Opus in the Ogg format. You can download pre-built FFmpeg binaries [here](https://ffmpeg.org/download.html). You can either put the downloaded binary in the bot's working directory or add `ffmpeg` to PATH.

## Installation
Create a `.env` file in parent directory containing your `DISCORD_TOKEN` value.

The leaderboard monitor checks LiveBench and DeepSWE every 30 minutes and stores its deduplication state in `data/leaderboard-monitor-state.json`. The Docker Compose configuration persists this directory across container rebuilds.

## Automatic deployment

`.github/workflows/deploy.yml` builds a Linux x64 .NET 8 release on pull requests
and pushes to `main` or `master`. Only pushes deploy to production. Runs are
serialized, and an outdated branch revision is skipped if a newer push arrived
while it was queued or building.

The workflow sends a checksummed release archive to `root@193.168.147.68` using
these repository Actions secrets:

| Secret | Value |
| --- | --- |
| `LYMDUNETTEBOT_DEPLOY_SSH_KEY` | Dedicated, unencrypted Ed25519 private key for this repository's CD |
| `LYMDUNETTEBOT_DEPLOY_KNOWN_HOSTS` | The server's verified `193.168.147.68 ssh-ed25519 ...` public host-key entry |

The private key must never be committed. Obtain the host key through an existing
trusted administrator connection; do not trust an unverified runtime key scan.
GitHub Actions uses strict host verification and read-only repository permissions.

### Server setup and key rotation

The server needs Bash, Docker with Compose v2, Python 3, `flock`, and GNU coreutils.
Production must already exist at `/var/lymdunbot`, with its `.env`, `data/`,
`docker-compose.yml`, and the `lymdunette-bot` service running under project
`lymdunbot`.

An administrator installs the reviewed `scripts/deploy-production.sh` as
`/usr/local/sbin/lymdunettebot-cd`, owned by root with mode `755`. Install future
changes to this script using an administrator connection as well. The workflow
does not replace the server's deployment command.

Create a separate key with `ssh-keygen -t ed25519`, leaving its passphrase empty
for unattended use. Add its public key to `/root/.ssh/authorized_keys` with:

```text
restrict,command="/usr/local/sbin/lymdunettebot-cd" ssh-ed25519 <public-key> lymdunettebot-github-actions
```

Store the private key and verified host-key entry in the two Actions secrets
above. Test the new identity using `ssh -i <private-key> -o IdentitiesOnly=yes
root@193.168.147.68 check`. The forced command permits only this read-only check
and `deploy <40-character commit SHA> <64-character archive SHA256>`; interactive
shells, forwarding, SCP and arbitrary SSH commands are disabled. This remains a
privileged production credential: code accepted into `main` or `master` can build
and run the production bot. To rotate it, provision and verify a replacement,
update the secret, then remove only the old public-key entry.

### Deployment and recovery

The server verifies the archive and builds an image from the published release,
excluding `.env` and `data`. It verifies the image's DLL checksum before replacing
only `lymdunette-bot`. The existing `.env` and `./data:/app/data` mount are preserved.
The image is selected through `/var/lymdunbot/docker-compose.cd.yml`; include that
override in subsequent manual Compose operations.

A release succeeds only after Discord readiness appears in the scheduler logs,
the container remains running with zero restarts for three consecutive checks,
and the data mount and environment-file checksum are verified. Failed activation
restores the previous image and checks its readiness. Failure still marks the
GitHub Actions run as failed, even if rollback succeeds.

Each activation retains the previous image and owner-only recovery files under
`/var/backups/lymdunbot/<release-id>/`. The last successful commit is recorded in
`/var/lib/lymdunettebot-cd/current-revision`. Recovery does not rewind persistent
application data. Inspect retained images/backups periodically and remove old
ones only after deciding which rollback releases to keep.

To restore a retained release manually, replace `<release-id>` below and run as
an administrator on the server:

```sh
cp /var/backups/lymdunbot/<release-id>/rollback.yml /var/lymdunbot/docker-compose.cd.yml
docker compose --project-name lymdunbot --project-directory /var/lymdunbot \
  -f /var/lymdunbot/docker-compose.yml -f /var/lymdunbot/docker-compose.cd.yml \
  up -d --no-deps --force-recreate --no-build lymdunette-bot
```

Then check the bot's logs, running state, and restart count. Automatic recovery
requires the deployment process to stay alive; a server power loss or forced kill
may require this manual procedure.
