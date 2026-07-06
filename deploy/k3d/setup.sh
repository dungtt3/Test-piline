#!/bin/sh
# Tao k3d cluster va cai Argo Workflows cho EAAP (Linux/macOS).
# Yeu cau: k3d, kubectl, docker.
set -eu

CLUSTER_NAME="${1:-eaap}"
ARGO_VERSION="${2:-v3.5.11}"
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"

echo "==> Creating k3d cluster '$CLUSTER_NAME'"
k3d cluster create "$CLUSTER_NAME" --wait

echo "==> Installing Argo Workflows $ARGO_VERSION"
kubectl create namespace argo --dry-run=client -o yaml | kubectl apply -f -
kubectl apply -n argo -f "https://github.com/argoproj/argo-workflows/releases/download/$ARGO_VERSION/install.yaml"

echo "==> Configuring argo-server for local use (http, no auth)"
kubectl patch deployment argo-server -n argo --type json --patch-file "$SCRIPT_DIR/argo-server-patch.json"

echo "==> Granting workflow permissions to the default service account"
kubectl create rolebinding eaap-argo-admin --clusterrole=admin --serviceaccount=argo:default -n argo --dry-run=client -o yaml | kubectl apply -f -

kubectl -n argo rollout status deployment/argo-server --timeout=300s
kubectl -n argo rollout status deployment/workflow-controller --timeout=300s

echo "==> Applying EAAP WorkflowTemplate"
kubectl apply -n argo -f "$SCRIPT_DIR/../argo/analysis-job.yaml"

echo ""
echo "Done. Next steps:"
echo "  1. Build + import adapter : ./deploy/k3d/build-adapter.sh"
echo "  2. Expose Argo API        : kubectl -n argo port-forward svc/argo-server 2746:2746"
