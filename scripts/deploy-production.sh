#!/usr/bin/env bash
# Installed as /usr/local/sbin/lymdunettebot-cd for the restricted CD SSH key.
set -euo pipefail
umask 077
export PATH=/usr/sbin:/usr/bin:/sbin:/bin
cd /var/lymdunbot
[[ -f .env && -d data && -f docker-compose.yml ]]

if [[ ${SSH_ORIGINAL_COMMAND:-} == check ]]; then
    docker compose version
    docker info >/dev/null
    echo 'LymdunetteBot CD connection and prerequisites OK.'
    exit 0
fi

if [[ ! ${SSH_ORIGINAL_COMMAND:-} =~ ^deploy\ ([0-9a-f]{40})$ ]]; then
    echo 'Allowed commands: check, deploy <commit SHA>.' >&2
    exit 1
fi
image="lymdunettebot-cd:${BASH_REMATCH[1]}"
compose=(docker compose --project-name lymdunbot
    -f docker-compose.yml -f docker-compose.cd.yml)

# Serialize imports and recreation, including any manual deployment.
exec 9>/var/lock/lymdunettebot-cd.lock
flock -w 900 9
docker load --quiet
docker image inspect "$image" >/dev/null
printf 'services:\n  lymdunette-bot:\n    image: %s\n    pull_policy: never\n' "$image" > docker-compose.cd.yml.tmp
mv -f docker-compose.cd.yml.tmp docker-compose.cd.yml
"${compose[@]}" up -d --no-deps --force-recreate --no-build lymdunette-bot

# Compose returning successfully does not prove that the bot has connected.
container=$("${compose[@]}" ps -q lymdunette-bot)
for ((attempt=0; attempt<12; attempt++)); do
    sleep 5
    state=$(docker inspect --format '{{.State.Running}} {{.RestartCount}}' "$container")
    if [[ "$state" != 'true 0' ]]; then
        echo 'Bot stopped or restarted during startup.' >&2
        exit 1
    fi
    if docker logs "$container" 2>&1 | grep -F 'SchedulerService Ready fired!' >/dev/null; then
        echo "Deployed $image; bot is ready."
        exit 0
    fi
done
echo 'Bot did not become ready within 60 seconds; inspect its logs on the server.' >&2
exit 1
