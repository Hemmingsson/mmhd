#!/usr/bin/env bash
# Build the ARM64 API image for the local Compose installation.
set -euo pipefail
cd "$(git rev-parse --show-toplevel)"
tag="local/mmhd-api:$(git rev-parse --short=12 HEAD)"
runtime="${MMHD_RUNTIME_IMAGE:-local/mmhd-runtime:arm64}"
if ! docker image inspect "$runtime" >/dev/null 2>&1; then
  docker build --platform linux/arm64 -f docker/mmhd/Dockerfile.runtime -t "$runtime" .
fi
context=$(mktemp -d)
trap 'rm -rf "$context"' EXIT
export MH_VERSION="$(scripts/mmhd-version.sh)"
sdk=$(command -v dotnet || printf "%s/.dotnet/dotnet" "$HOME")
"$sdk" publish MusicHoarder.Api -c Release -r linux-arm64 --self-contained false \
  -p:UseAppHost=false -o "$context/publish"
docker build --platform linux/arm64 --build-arg "RUNTIME_IMAGE=$runtime" \
  -f docker/mmhd/Dockerfile.api -t "$tag" "$context"
