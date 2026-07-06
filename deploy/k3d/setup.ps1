# Tao k3d cluster va cai Argo Workflows cho EAAP (Windows PowerShell).
# Yeu cau: k3d, kubectl, docker.
param(
    [string]$ClusterName = "eaap",
    [string]$ArgoVersion = "v3.5.11"
)

$ErrorActionPreference = "Stop"

Write-Host "==> Creating k3d cluster '$ClusterName'"
k3d cluster create $ClusterName --wait

Write-Host "==> Installing Argo Workflows $ArgoVersion"
kubectl create namespace argo --dry-run=client -o yaml | kubectl apply -f -
kubectl apply -n argo -f "https://github.com/argoproj/argo-workflows/releases/download/$ArgoVersion/install.yaml"

Write-Host "==> Configuring argo-server for local use (http, no auth)"
kubectl patch deployment argo-server -n argo --type json --patch-file "$PSScriptRoot\argo-server-patch.json"

Write-Host "==> Granting workflow permissions to the default service account"
kubectl create rolebinding eaap-argo-admin --clusterrole=admin --serviceaccount=argo:default -n argo --dry-run=client -o yaml | kubectl apply -f -

kubectl -n argo rollout status deployment/argo-server --timeout=300s
kubectl -n argo rollout status deployment/workflow-controller --timeout=300s

Write-Host "==> Applying EAAP WorkflowTemplate"
kubectl apply -n argo -f "$PSScriptRoot\..\argo\analysis-job.yaml"

Write-Host ""
Write-Host "Done. Next steps:"
Write-Host "  1. Build + import adapter : .\deploy\k3d\build-adapter.ps1"
Write-Host "  2. Expose Argo API        : kubectl -n argo port-forward svc/argo-server 2746:2746"
