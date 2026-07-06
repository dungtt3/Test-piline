# EAAP — Engineering Analysis Automation Platform (Phase 1)

Platform phân tích mã nguồn: clone repo → snapshot lên MinIO → chạy analyzer (OCI adapter) qua Argo Workflows → ingest SARIF thành Warnings → quality gate qua OPA. Xây theo `EAAP-AI-Build-Spec-Phase1.md`.

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

Policy Rego tại `policies/quality-gate/default.rego` (OPA load tự động qua docker compose). Ngưỡng cấu hình `Opa:MaxWarnings` (mặc định 100). Đặt `$env:Opa__MaxWarnings = "0"` trước khi chạy API để mọi warning làm job `GateFailed`.

## Test

```powershell
dotnet test          # 18 unit + 5 integration (Testcontainers, cần Docker)
```

## Export OpenAPI

```powershell
dotnet build src/Eaap.Api /p:ExportOpenApi=true   # ghi docs/api/openapi.json
```

## Tài liệu

- Spec gốc: `EAAP-AI-Build-Spec-Phase1.md`
- Quyết định kiến trúc (ADR): `docs/decisions/`
- Ngoài phạm vi Phase 1: `docs/backlog.md`
