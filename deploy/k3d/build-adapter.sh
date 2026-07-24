#!/bin/sh
# Build image adapter cua EAAP va import vao k3d cluster.
#   ./build-adapter.sh                              # megalinter (mac dinh, giu nguyen hanh vi phase 1)
#   ./build-adapter.sh eaap echo test-report        # cluster + nhieu adapter
#   EAAP_SKIP_IMPORT=1 ./build-adapter.sh eaap test-report
set -eu

CLUSTER_NAME="${1:-eaap}"
[ $# -gt 0 ] && shift
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"

ADAPTERS="$*"
[ -n "$ADAPTERS" ] || ADAPTERS="megalinter"

for adapter in $ADAPTERS; do
    image="eaap/adapter-${adapter}:latest"
    dockerfile="$REPO_ROOT/adapters/$adapter/Dockerfile"
    if [ ! -f "$dockerfile" ]; then
        echo "No Dockerfile for adapter '$adapter' at $dockerfile" >&2
        exit 1
    fi

    echo "==> Building $image"
    case "$adapter" in
        # Converter adapters compile .NET sources from src/, so their build context is the
        # repo root instead of the adapter folder (see ADR-007).
        test-report|coverage|prometheus-slo)
            docker build -t "$image" -f "$dockerfile" "$REPO_ROOT"
            ;;
        *)
            docker build -t "$image" "$REPO_ROOT/adapters/$adapter"
            ;;
    esac

    [ "${EAAP_SKIP_IMPORT:-0}" = "1" ] && continue

    echo "==> Importing $image into k3d cluster '$CLUSTER_NAME'"
    k3d image import "$image" -c "$CLUSTER_NAME"
done
