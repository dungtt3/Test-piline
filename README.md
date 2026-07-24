# EAAP — Engineering Analysis Automation Platform (Phase 1 + 2)

Platform phân tích mã nguồn: clone repo → snapshot lên MinIO → (tùy chọn chạy test) → chạy analyzer (OCI adapter) qua Argo Workflows → ingest SARIF thành Warnings + Metric → dedup xuyên job bằng baseline → quality gate per-repo qua OPA → trend trên Grafana. Xây theo `EAAP-AI-Build-Spec-Phase1.md` và `EAAP-AI-Build-Spec-Phase2.md`.

## Kiến trúc

- **.NET 8** Clean Architecture: `Eaap.Domain` → `Eaap.Application` → `Eaap.Infrastructure` → `Eaap.Api` (Minimal API + Swagger), `Eaap.Sarif` (SARIF 2.1.0 model/validator/fingerprint trên Sarif.Sdk).
- **Hạ tầng local:** PostgreSQL 16, RabbitMQ (MassTransit), MinIO, OPA — qua `docker-compose.yml`; Argo Workflows trên k3d.
- **Adapter contract** (spec Phần 5): container mount `/workspace` (ro), ghi `*.sarif` vào `/results`, report gốc vào `/artifacts`; entrypoint chuẩn `/eaap-entrypoint.sh`. Adapter chính: `adapters/megalinter`; adapter smoke-test: `adapters/echo`.

## Yêu cầu

Docker Desktop, .NET SDK ≥ 8, git, k3d + kubectl (cho phần Argo).

## Chạy end-to-end (10 lệnh)

```powershell
# 1. Clone và vào repo
git clone <repo-url> eaap; cd eaap

# 2. Hạ tầng local (postgres, rabbitmq, minio + bucket, opa)
docker compose up -d --wait

# 3. Restore local tools (dotnet-ef, swagger cli)
dotnet tool restore

# 4. Tạo schema database
$env:ConnectionStrings__Postgres = "Host=localhost;Port=5432;Database=eaap;Username=eaap;Password=eaap-dev"; dotnet ef database update -p src/Eaap.Infrastructure -s src/Eaap.Api

# 5. Tạo k3d cluster + cài Argo + apply WorkflowTemplate
.\deploy\k3d\setup.ps1

# 6. Build image adapter MegaLinter và import vào cluster (cần ~25GB trống;
#    máy yếu dùng adapter echo: docker build -t eaap/adapter-echo:latest adapters/echo; k3d image import eaap/adapter-echo:latest -c eaap)
.\deploy\k3d\build-adapter.ps1

# 7. Expose Argo API (giữ terminal riêng)
kubectl -n argo port-forward svc/argo-server 2746:2746

# 8. Chạy API (Swagger: http://localhost:5080/swagger, health: /health)
$env:ASPNETCORE_ENVIRONMENT = "Development"; dotnet run --project src/Eaap.Api --urls http://localhost:5080

# 9. Đăng ký repository và yêu cầu scan (terminal khác)
$repo = Invoke-RestMethod -Method Post http://localhost:5080/api/v1/repositories -ContentType application/json -Body '{"provider":"GitHub","cloneUrl":"https://github.com/<org>/<repo>.git","defaultBranch":"main"}'; $scan = Invoke-RestMethod -Method Post "http://localhost:5080/api/v1/repositories/$($repo.id)/scans" -ContentType application/json -Body '{"analyzers":["megalinter"]}'

# 10. Theo dõi job và xem warnings
Invoke-RestMethod "http://localhost:5080/api/v1/jobs/$($scan.jobId)"; Invoke-RestMethod "http://localhost:5080/api/v1/jobs/$($scan.jobId)/warnings"
```

Job chuyển `Pending → Running → Succeeded` (hoặc `GateFailed` nếu vi phạm quality gate, `Failed` nếu adapter lỗi). SARIF tổng hợp: `GET /api/v1/jobs/{id}/sarif`.

## Quality gate

Policy Rego tại `policies/quality-gate/default.rego` (OPA load tự động qua docker compose). Ngưỡng nền cấu hình trong `Opa:*` (`MaxWarnings=100`, `MaxNewWarnings=-1` tắt, `MinCoverageLine=0` tắt, `MaxTestsFailed=0`). Từ Phase 2, gate còn xét coverage, số test fail và **warning mới** (so baseline), và cấu hình được **theo từng repo** qua `PUT /api/v1/repositories/{id}/gate`.

## Phase 2 — Test, Coverage, Baseline, Gate per-repo, Trend

Phase 2 thêm khái niệm **Metric** (tách khỏi Warning), 2 adapter converter (`test-report`, `coverage`), dedup **xuyên job** bằng baseline (`IsNew`/`Resolved`), gate per-repository và trend trên Grafana. Xem `EAAP-AI-Build-Spec-Phase2.md`.

Demo test + coverage + gate + trend (tiếp nối 8 lệnh hạ tầng ở trên; ≤ 12 lệnh):

```powershell
# 1. Hạ tầng + Grafana (đã gồm postgres/rabbitmq/minio/opa; grafana ở :3000)
docker compose up -d --wait

# 2. Build 2 adapter converter Phase 2 (context = repo root, không cần k3d)
.\deploy\k3d\build-adapter.ps1 -Adapters test-report,coverage -SkipImport

# 3. Đăng ký repo và siết gate: coverage tối thiểu 80%, không cho warning mới
$repo = Invoke-RestMethod -Method Post http://localhost:5080/api/v1/repositories -ContentType application/json -Body '{"provider":"GitHub","cloneUrl":"https://github.com/<org>/<repo>.git","defaultBranch":"main"}'
Invoke-RestMethod -Method Put "http://localhost:5080/api/v1/repositories/$($repo.id)/gate" -ContentType application/json -Body '{"thresholds":{"minCoverageLine":80,"maxNewWarnings":0}}'

# 4. Repo cần có .eaap/config.yaml khai báo bước test (xem mẫu bên dưới) rồi scan
$scan = Invoke-RestMethod -Method Post "http://localhost:5080/api/v1/repositories/$($repo.id)/scans" -ContentType application/json -Body '{"analyzers":["megalinter"]}'

# 5. Xem warning mới, baseline, và trend
Invoke-RestMethod "http://localhost:5080/api/v1/jobs/$($scan.jobId)/warnings?isNew=true"
Invoke-RestMethod "http://localhost:5080/api/v1/repositories/$($repo.id)/baseline?status=Active"
Invoke-RestMethod "http://localhost:5080/api/v1/repositories/$($repo.id)/trend"

# 6. Mở Grafana http://localhost:3000 (anonymous Viewer) → dashboard "EAAP — Repository Trend",
#    chọn RepositoryId để xem Warning total / New vs Resolved / Coverage % / Tests failed.
```

Mẫu `.eaap/config.yaml` đặt trong repo được quét (bước `run-tests` tùy chọn, sinh report vào `.eaap/test-results` + `.eaap/coverage`):

```yaml
test:
  enabled: true
  image: mcr.microsoft.com/dotnet/sdk:8.0
  command: >
    dotnet test --logger trx --results-directory /workspace/.eaap/test-results
    /p:CollectCoverage=true /p:CoverletOutputFormat=cobertura
    /p:CoverletOutput=/workspace/.eaap/coverage/
```

Coverage và số test là **Metric** (`/results/metrics.json`), không phải Warning; adapter `test-report` biến test FAILED thành SARIF `test.failed`.

## Test

```powershell
dotnet test          # 67 unit + 21 integration (Testcontainers, cần Docker)
```

## Export OpenAPI

```powershell
dotnet build src/Eaap.Api /p:ExportOpenApi=true   # ghi docs/api/openapi.json
```

## Tài liệu

- Spec gốc: `EAAP-AI-Build-Spec-Phase1.md`, `EAAP-AI-Build-Spec-Phase2.md`
- Quyết định kiến trúc (ADR): `docs/decisions/` (ADR-001…010)
- Ngoài phạm vi hiện tại: `docs/backlog.md`
