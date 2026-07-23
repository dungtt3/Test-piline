# Build image adapter cua EAAP va import vao k3d cluster.
#   .\build-adapter.ps1                                  # megalinter (mac dinh, giu nguyen hanh vi phase 1)
#   .\build-adapter.ps1 -Adapters echo,test-report       # nhieu adapter
#   .\build-adapter.ps1 -Adapters test-report -SkipImport # chi build, khong can cluster
param(
    [string[]]$Adapters = @("megalinter"),
    [string]$ClusterName = "eaap",
    [switch]$SkipImport
)

$ErrorActionPreference = "Stop"
$repoRoot = Resolve-Path "$PSScriptRoot\..\.."

# Converter adapters compile .NET sources from src/, so their build context is the repo root
# instead of the adapter folder (see ADR-007).
$rootContextAdapters = @("test-report", "coverage")

foreach ($adapter in $Adapters) {
    $image = "eaap/adapter-${adapter}:latest"
    $dockerfile = Join-Path $repoRoot "adapters\$adapter\Dockerfile"
    if (-not (Test-Path $dockerfile)) {
        throw "No Dockerfile for adapter '$adapter' at $dockerfile"
    }

    Write-Host "==> Building $image"
    if ($rootContextAdapters -contains $adapter) {
        docker build -t $image -f $dockerfile $repoRoot
    }
    else {
        docker build -t $image (Join-Path $repoRoot "adapters\$adapter")
    }
    if ($LASTEXITCODE -ne 0) {
        throw "docker build failed for adapter '$adapter'"
    }

    if ($SkipImport) {
        continue
    }

    Write-Host "==> Importing $image into k3d cluster '$ClusterName'"
    k3d image import $image -c $ClusterName
    if ($LASTEXITCODE -ne 0) {
        throw "k3d image import failed for adapter '$adapter'"
    }
}
