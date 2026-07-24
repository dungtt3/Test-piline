# EAAP — Engineering Analysis Automation Platform (v1.0.0)

Platform phân tích vòng đời phần mềm theo triết lý **Integrate-first**: clone repo → snapshot lên MinIO → (tùy chọn chạy test) → chạy analyzer (OCI adapter, gồm cả runtime SLO qua Prometheus) → ingest SARIF thành Warnings + Metric (phân loại security severity/CWE/CVE, technical debt) → dedup xuyên job bằng baseline → suppression → **một Quality Gate OPA xuyên suốt source→runtime** (per-repo) → trend trên Grafana → notification + webhook auto-scan. Có auth JWT + RBAC. Xây theo `EAAP-AI-Build-Spec-Phase1.md` … `-Phase4.md`; so sánh: `docs/COMPARISON.md`.

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

## Phase 3 — Security & Dependency

Phase 3 thêm 3 adapter security **native-SARIF** (`trivy` SCA+secret+misconfig, `semgrep` SAST, `gitleaks` secret), phân loại `SecuritySeverity`/CWE/CVE khi ingest, **suppression** theo fingerprint, và **gate security** (mặc định nghiêm: 0 critical/0 high). Xem `EAAP-AI-Build-Spec-Phase3.md`.

Demo quét `vulnerable-app`, xem summary, suppress, re-scan (≤ 12 lệnh nối tiếp hạ tầng ở trên):

```powershell
# 1. Build 3 adapter security (offline: DB/rule đóng băng vào image — ADR-011/012)
.\deploy\k3d\build-adapter.ps1 -Adapters trivy,semgrep,gitleaks -SkipImport

# 2. Đăng ký repo (dùng tests/fixtures/vulnerable-app hoặc repo thật) và scan 4 analyzer
$repo = Invoke-RestMethod -Method Post http://localhost:5080/api/v1/repositories -ContentType application/json -Body '{"provider":"GitHub","cloneUrl":"https://github.com/<org>/<repo>.git","defaultBranch":"main"}'
$scan = Invoke-RestMethod -Method Post "http://localhost:5080/api/v1/repositories/$($repo.id)/scans" -ContentType application/json -Body '{"analyzers":["trivy"]}'

# 3. Job GateFailed vì có finding high/critical; xem phân bố security
Invoke-RestMethod "http://localhost:5080/api/v1/jobs/$($scan.jobId)/security-summary"
Invoke-RestMethod "http://localhost:5080/api/v1/jobs/$($scan.jobId)/warnings?securitySeverity=High,Critical"

# 4. Suppress một fingerprint (lấy từ warnings ở trên) rồi scan lại
$fp = (Invoke-RestMethod "http://localhost:5080/api/v1/jobs/$($scan.jobId)/warnings?securitySeverity=Critical").items[0].fingerprint
Invoke-RestMethod -Method Post "http://localhost:5080/api/v1/repositories/$($repo.id)/suppressions" -ContentType application/json -Body (@{fingerprint=$fp; reason="Accepted risk: reviewed by security team"} | ConvertTo-Json)

# 5. Nới gate per-repo nếu cần (mặc định 0 critical/0 high)
Invoke-RestMethod -Method Put "http://localhost:5080/api/v1/repositories/$($repo.id)/gate" -ContentType application/json -Body '{"thresholds":{"maxSecurityHigh":5}}'
```

Warning suppressed vẫn lưu, ẩn mặc định (`?includeSuppressed=true` để xem), **không** tính vào gate và trend (đếm riêng `WarningSuppressed`). Fixture cố tình lỗi ở `tests/fixtures/vulnerable-app/` (secret là **fake** — key ví dụ công khai của AWS).

## Phase 4 — Runtime & Enterprise (v1.0.0)

Adapter `prometheus-slo` (SLO runtime → warning), technical debt, **gate xuyên suốt** (source→runtime trong một `GateEvaluation`), auth JWT + RBAC 3 role, notification (webhook HMAC/Slack/email), webhook auto-scan GitHub/GitLab. Xem `EAAP-AI-Build-Spec-Phase4.md`.

Kịch bản tổng "one gate to rule them all" (≤ 15 lệnh, nối tiếp hạ tầng ở trên):

```powershell
# 1. Seed admin qua env rồi chạy API (mọi /api/v1/* cần auth; /auth/login, /hooks/*, /health mở)
$env:Auth__JwtSecret = "change-me-32-chars-minimum-secret-000"; $env:Auth__AdminEmail = "admin@eaap.local"; $env:Auth__AdminPassword = "admin-pass-123"
$env:ASPNETCORE_ENVIRONMENT = "Development"; dotnet run --project src/Eaap.Api --urls http://localhost:5080

# 2. Đăng nhập lấy JWT, tạo header
$jwt = (Invoke-RestMethod -Method Post http://localhost:5080/auth/login -ContentType application/json -Body '{"email":"admin@eaap.local","password":"admin-pass-123"}').token
$h = @{ Authorization = "Bearer $jwt" }

# 3. Build 7 adapter (megalinter/test-report/coverage/trivy/semgrep/gitleaks/prometheus-slo)
.\deploy\k3d\build-adapter.ps1 -Adapters test-report,coverage,trivy,semgrep,gitleaks,prometheus-slo -SkipImport

# 4. Đăng ký repo + kênh notification webhook (bắn khi GateFailed)
$repo = Invoke-RestMethod -Method Post http://localhost:5080/api/v1/repositories -Headers $h -ContentType application/json -Body '{"provider":"GitHub","cloneUrl":"https://github.com/<org>/<repo>.git","defaultBranch":"main"}'
Invoke-RestMethod -Method Post "http://localhost:5080/api/v1/repositories/$($repo.id)/notifications" -Headers $h -ContentType application/json -Body '{"type":"Webhook","config":{"url":"https://webhook.site/<id>","secret":"s3cret"},"triggers":["GateFailed"],"enabled":true}'

# 5. Scan → gate đánh giá 7 chiều trong 1 GateEvaluation; xem debt + security + gate
$scan = Invoke-RestMethod -Method Post "http://localhost:5080/api/v1/repositories/$($repo.id)/scans" -Headers $h -ContentType application/json -Body '{"analyzers":["trivy"]}'
Invoke-RestMethod "http://localhost:5080/api/v1/jobs/$($scan.jobId)" -Headers $h
Invoke-RestMethod "http://localhost:5080/api/v1/repositories/$($repo.id)/debt" -Headers $h

# 6. (CI) Cấp API token dùng thay JWT trong pipeline
Invoke-RestMethod -Method Post http://localhost:5080/auth/tokens -Headers $h -ContentType application/json -Body '{"name":"ci"}'
```

`.eaap/config.yaml` có thể khai báo `runtime:` (SLO Prometheus) và `analyzers:` (cho webhook auto-scan). Webhook GitHub: `POST /hooks/github` verify `X-Hub-Signature-256` HMAC với `WebhookSecret` per-repo.

## Test

```powershell
dotnet test          # 122 unit + 46 integration (Testcontainers + WireMock, cần Docker)
```

## Export OpenAPI

```powershell
dotnet build src/Eaap.Api /p:ExportOpenApi=true   # ghi docs/api/openapi.json
```

## Tài liệu

- Spec gốc: `EAAP-AI-Build-Spec-Phase1.md` … `-Phase4.md`
- So sánh với SonarQube/DefectDojo: `docs/COMPARISON.md`
- Quyết định kiến trúc (ADR): `docs/decisions/` (ADR-001…014)
- Ngoài phạm vi hiện tại: `docs/backlog.md`
