#!/usr/bin/env bash
# Build the checked-out fork revision and start the configured stack.
set -euo pipefail
cd "$(git rev-parse --show-toplevel)"
compose=(docker compose)
docker compose version >/dev/null 2>&1 || compose=(docker-compose)
export MH_VERSION="$(scripts/mmhd-version.sh)"
if [[ ! -f .env ]] || ! grep -Eq '^COMPOSE_FILE=.*docker-compose\.mmhd\.yml' .env; then
  exec "${compose[@]}" up -d --build --wait
fi

# The existing Mac installation uses native media tools and a pinned frontend bundle.
sha=$(git rev-parse --short=12 HEAD)
scripts/build-mmhd-api.sh
( cd frontend && bun install --frozen-lockfile && bun run build )
bundle=".mmhd-local/releases/$sha/frontend"
if [[ ! -d "$bundle" ]]; then
  mkdir -p "$(dirname "$bundle")"
  tmp=$(mktemp -d "$(dirname "$bundle")/build.XXXXXX")
  cp -R frontend/build/. "$tmp/"
  mv "$tmp" "$bundle"
fi
export MMHD_API_IMAGE="local/mmhd-api:$sha"
export MMHD_FRONTEND_BUILD="./$bundle"
"${compose[@]}" up -d --no-deps --pull never --wait api frontend
# Persist the two successful selections for ordinary Compose restarts.
umask 077
env_file=$(realpath .env)
tmp=$(mktemp "$(dirname "$env_file")/.mmhd-local/env.XXXXXX")
trap 'rm -f "$tmp"' EXIT
awk '!/^(MMHD_API_IMAGE|MMHD_FRONTEND_BUILD)=/' .env > "$tmp"
printf '\nMMHD_API_IMAGE=%s\nMMHD_FRONTEND_BUILD=%s\n' "$MMHD_API_IMAGE" "$MMHD_FRONTEND_BUILD" >> "$tmp"
mv "$tmp" "$env_file"
