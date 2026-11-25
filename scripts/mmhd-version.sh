#!/usr/bin/env bash
set -euo pipefail
base=$(git describe --tags --abbrev=0 --match 'v[0-9]*' 2>/dev/null || echo v2.7.0)
printf '%s-mmhd.g%s\n' "${base#v}" "$(git rev-parse --short=12 HEAD)"
