#!/bin/sh
# Build image adapter MegaLinter va import vao k3d cluster.
set -eu

CLUSTER_NAME="${1:-eaap}"
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$SCRIPT_DIR/../.."

echo "==> Building eaap/adapter-megalinter:latest"
docker build -t eaap/adapter-megalinter:latest "$REPO_ROOT/adapters/megalinter"

echo "==> Importing image into k3d cluster '$CLUSTER_NAME'"
k3d image import eaap/adapter-megalinter:latest -c "$CLUSTER_NAME"
