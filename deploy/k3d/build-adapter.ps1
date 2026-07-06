# Build image adapter MegaLinter va import vao k3d cluster.
param([string]$ClusterName = "eaap")

$ErrorActionPreference = "Stop"
$repoRoot = Resolve-Path "$PSScriptRoot\..\.."

Write-Host "==> Building eaap/adapter-megalinter:latest"
docker build -t eaap/adapter-megalinter:latest "$repoRoot\adapters\megalinter"

Write-Host "==> Importing image into k3d cluster '$ClusterName'"
k3d image import eaap/adapter-megalinter:latest -c $ClusterName
