#!/usr/bin/env bash
# Installed by an administrator as /usr/local/sbin/lymdunettebot-cd.
# The dedicated SSH key may run only this forced command.
set -Eeuo pipefail
umask 077
export PATH=/usr/sbin:/usr/bin:/sbin:/bin

app=/var/lymdunbot
state=/var/lib/lymdunettebot-cd
service=lymdunette-bot
override="$app/docker-compose.cd.yml"
compose=(docker compose --project-name lymdunbot --project-directory "$app"
    -f "$app/docker-compose.yml" -f "$override")

die() { echo "$*" >&2; exit 1; }

preflight() {
    [[ $EUID == 0 ]] || die 'The deployment command must run as root.'
    for tool in docker python3 flock sha256sum timeout; do
        command -v "$tool" >/dev/null || die "Missing prerequisite: $tool"
    done
    docker compose version >/dev/null
    docker info >/dev/null
    [[ -f "$app/.env" && -d "$app/data" && -f "$app/docker-compose.yml" ]] ||
        die 'Production configuration or persistent data is missing.'
    docker inspect lymdunbot-lymdunette-bot-1 >/dev/null
}

case "${SSH_ORIGINAL_COMMAND:-}" in
    check)
        preflight
        echo 'LymdunetteBot CD connection and prerequisites OK.'
        exit 0
        ;;
    *)
        if [[ ${SSH_ORIGINAL_COMMAND:-} =~ ^deploy\ ([0-9a-f]{40})\ ([0-9a-f]{64})$ ]]; then
            revision=${BASH_REMATCH[1]}
            checksum=${BASH_REMATCH[2]}
        else
            die 'Allowed commands: check, deploy <commit SHA> <archive SHA256>.'
        fi
        ;;
esac

preflight
install -d -m 700 "$state"
exec 9>"$state/deploy.lock"
flock -w 900 9 || die 'Another deployment is still running.'
work=$(mktemp -d "$state/release.XXXXXX")
backup=
activating=false

write_override() {
    printf 'services:\n  %s:\n    image: %s\n    pull_policy: never\n' "$service" "$1" > "$override.tmp"
    mv -f "$override.tmp" "$override"
}

wait_ready() {
    local expected_image=$1 container running restarts actual streak=0
    for ((attempt=0; attempt<30; attempt++)); do
        container=$("${compose[@]}" ps -q "$service") || return 1
        if [[ -n "$container" ]]; then
            read -r running restarts actual < <(
                docker inspect --format '{{.State.Running}} {{.RestartCount}} {{.Image}}' "$container")
            [[ "$restarts" == 0 && "$actual" == "$expected_image" ]] || return 1
            if [[ "$running" == true ]] &&
                docker logs "$container" > "$work/startup.log" 2>&1 &&
                grep -Fq 'SchedulerService Ready fired!' "$work/startup.log"; then
                streak=$((streak + 1))
                # Require readiness to survive three consecutive checks.
                if (( streak >= 3 )); then return 0; fi
            else
                streak=0
            fi
        fi
        sleep 5
    done
    return 1
}

cleanup() {
    local result=$?
    trap - EXIT HUP INT TERM
    if [[ "$activating" == true ]]; then
        echo "Deployment failed; restoring previous image from $backup." >&2
        set +e
        write_override "$rollback_image"
        "${compose[@]}" up -d --no-deps --force-recreate --no-build "$service"
        if wait_ready "$old_image_id"; then
            echo 'Previous release restored and ready.' >&2
        else
            echo "ROLLBACK NEEDS ATTENTION. Recovery files: $backup" >&2
        fi
        result=1
    fi
    rm -rf -- "$work"
    exit "$result"
}
trap cleanup EXIT
trap 'exit 130' HUP INT TERM

# Bound the upload and reject truncated/oversized input by its checksum.
timeout 180 head -c 536870913 > "$work/release.tar.gz"
[[ $(stat -c %s "$work/release.tar.gz") -le 536870912 ]] || die 'Archive exceeds 512 MiB.'
printf '%s  %s\n' "$checksum" "$work/release.tar.gz" | sha256sum --check --status ||
    die 'Release archive checksum mismatch.'
mkdir "$work/publish"

# Only regular files/directories may be extracted. Never accept traversal,
# links, devices, secrets or a persistent data directory into a root build.
python3 - "$work/release.tar.gz" "$work/publish" <<'PY'
import pathlib
import sys
import tarfile

with tarfile.open(sys.argv[1], "r:gz") as archive:
    members = archive.getmembers()
    if len(members) > 10000 or sum(m.size for m in members) > 2 * 1024**3:
        raise SystemExit("Expanded release exceeds limits")
    for member in members:
        path = pathlib.PurePosixPath(member.name)
        if (path.is_absolute() or ".." in path.parts
                or not (member.isfile() or member.isdir())
                or any(p in ("data", ".git") or p.startswith(".env") for p in path.parts)):
            raise SystemExit("Unsafe release archive entry")
    archive.extractall(sys.argv[2], members=members)
PY

[[ -f "$work/publish/LymdunetteBot.dll" && -f "$work/publish/Dockerfile" &&
   -f "$work/publish/.dockerignore" ]] || die 'Incomplete release archive.'
release_id="$(date -u +%Y%m%dT%H%M%S%N)-${revision:0:12}"
image="lymdunettebot-cd:$release_id"
timeout 900 docker build --pull --label "org.opencontainers.image.revision=$revision" \
    --tag "$image" "$work/publish"
image_id=$(docker image inspect --format '{{.Id}}' "$image")
expected_dll=$(sha256sum "$work/publish/LymdunetteBot.dll" | cut -d ' ' -f 1)
# Inspect the built image without starting the bot or giving it credentials.
actual_dll=$(docker run --rm --network none --entrypoint sha256sum "$image" /app/LymdunetteBot.dll | cut -d ' ' -f 1)
[[ "$actual_dll" == "$expected_dll" ]] || die 'Built DLL checksum mismatch.'
docker run --rm --network none --entrypoint sh "$image" -c 'test ! -e /app/.env && test ! -e /app/data'

old_container=$(docker compose --project-name lymdunbot --project-directory "$app" \
    -f "$app/docker-compose.yml" ps -q "$service")
[[ -n "$old_container" ]] || die 'Existing bot container is missing.'
old_image_id=$(docker inspect --format '{{.Image}}' "$old_container")
rollback_image="lymdunettebot-rollback:$release_id"
docker tag "$old_image_id" "$rollback_image"
backup="/var/backups/lymdunbot/$release_id"
install -d -m 700 "$backup"
cp -p "$app/.env" "$app/docker-compose.yml" "$backup/"
if [[ -f "$override" ]]; then cp -p "$override" "$backup/previous-cd.yml"; fi
printf 'services:\n  %s:\n    image: %s\n    pull_policy: never\n' "$service" "$rollback_image" > "$backup/rollback.yml"
printf '%s\n' "$revision" > "$backup/attempted-revision"
env_checksum=$(sha256sum "$app/.env" | cut -d ' ' -f 1)

activating=true
write_override "$image"
"${compose[@]}" up -d --no-deps --force-recreate --no-build "$service"
wait_ready "$image_id" || die 'New bot did not become ready with zero restarts.'
container=$("${compose[@]}" ps -q "$service")
data_mount=$(docker inspect --format '{{range .Mounts}}{{if eq .Destination "/app/data"}}{{.Source}}{{end}}{{end}}' "$container")
[[ "$data_mount" == "$app/data" ]] || die 'Persistent data mount changed.'
[[ $(sha256sum "$app/.env" | cut -d ' ' -f 1) == "$env_checksum" ]] || die 'Production environment file changed.'
printf '%s\n' "$revision" > "$state/current-revision"
activating=false
echo "Deployed $revision; bot ready with zero restarts. Rollback: $backup"
