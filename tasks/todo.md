# EAAP Phase 1 — Kế hoạch triển khai (theo EAAP-AI-Build-Spec-Phase1.md)

## M1 — Skeleton & hạ tầng local ✅
- [x] Tạo solution + projects (Domain, Application, Infrastructure, Api, Sarif, UnitTests, IntegrationTests), target net8.0
- [x] docker-compose.yml: postgres 16, rabbitmq, minio, opa
- [x] Domain entities + enums (Phần 3)
- [x] EF Core DbContext + migration đầy đủ schema, enum lưu string, index theo spec
- [x] Minimal API: đủ endpoint Phần 4 (stub 501 cho phần chưa làm), Swagger, /health cho 4 dependency
- [x] AC: dotnet build 0 warning, migration apply được, /health xanh (đã chạy thật, 4 checks Healthy)

## M2 — Repository & Snapshot Service ✅
- [x] POST /repositories, GET /repositories, GET /repositories/{id}
- [x] SnapshotService: clone bằng git CLI → tarball (System.Formats.Tar + gzip) → upload MinIO → Snapshot record; tái sử dụng theo (RepositoryId, CommitSha)
- [x] POST /repositories/{id}/scans: tạo snapshot nếu chưa có, tạo AnalysisJob Pending + AnalyzerRuns, publish JobRequested, 202
- [x] AC: integration test (Testcontainers) clone repo fixture, snapshot trong MinIO+DB, lần 2 cùng commit tái sử dụng; unit test chứng minh không clone lại khi có commitSha

## M3 — Eaap.Sarif ✅
- [x] Wrapper trên Sarif.Sdk (SarifDocument Load/Save/Merge), SarifValidator.Validate(stream) trả list lỗi
- [x] Fingerprint SHA256 theo công thức Phần 6 (+ NormalizePath)
- [x] AC: unit test 3 fixture SARIF, fingerprint deterministic (16 tests pass)

## M4 — Ingestion pipeline ✅
- [x] AnalyzerRunFinishedConsumer: tải SARIF từ MinIO → validate → map Warnings (dedup trong job, duplicateCount) → lưu DB → gate → JobFinished
- [x] GET /jobs/{id}, GET /jobs/{id}/warnings (paged, filter level/ruleId), GET /jobs/{id}/sarif (merged, application/sarif+json)
- [x] AC: integration test bơm fixture vào MinIO, publish event → 3 warnings (dedup từ 4 results), filter đúng, duplicateCount=2, gate GateFailed vì errorCount=1

## M5 — Argo orchestration + MegaLinter adapter ✅
- [x] adapters/megalinter: Dockerfile (ARG BASE_IMAGE cho flavor), entrypoint.sh, manifest.yaml; adapters/echo cho smoke test (ADR-006)
- [x] deploy/k3d: setup.ps1/.sh (cluster + Argo v3.5.11 + patch http/no-auth + rolebinding), build-adapter.ps1/.sh; deploy/argo/analysis-job.yaml (fetch → extract → analyzer → upload)
- [x] ArgoClient (submit qua WorkflowTemplate, get status), JobRequestedConsumer, ArgoPollingService 5s (ADR-004); unit test consumer 2 case
- [x] AC: e2e local trên k3d thật — job Pending→Running→Succeeded, 2 warnings của adapter xuất hiện qua GET /jobs/{id}/warnings (dùng echo adapter vì đĩa còn 12GB — ADR-006)

## M6 — Quality Gate + tổng kết ✅
- [x] policies/quality-gate/default.rego, OpaQualityGate, gọi gate khi job xong, GateEvaluation, status GateFailed
- [x] README end-to-end 10 lệnh, export OpenAPI docs/api/openapi.json (build step /p:ExportOpenApi=true)
- [x] AC: (a) SARIF sạch → Passed=true/Succeeded (integration test + e2e); (b) Opa__MaxWarnings=0 → GateFailed, violations "warningCount=2 > max 0" (e2e thật); (c) README 10 lệnh

---

# EAAP Phase 2 — Kế hoạch triển khai (theo EAAP-AI-Build-Spec-Phase2.md)

## M1 — Migration Phase2 + MetricSet ingestion ✅
- [x] 4 entity mới (MetricSet, WarningBaseline, GatePolicyBinding, TrendPoint) + enum BaselineStatus + cột `Warning.IsNew`
- [x] Migration duy nhất `Phase2_TestQuality` (đủ bảng/index cả phase) + role read-only `grafana_ro` (ADR-010)
- [x] `AnalyzerRunFinished` thêm `MetricsArtifactPath`; ArgoPollingService suy ra `jobs/{jobId}/{runId}/metrics.json` — không cần sửa workflow YAML vì `upload-results` đã copy đệ quy `/work/results/`
- [x] `MetricsIngestionService` parse `{ "metrics": { ... } }`; metrics hỏng/thiếu KHÔNG làm analyzer run Failed (chỉ SARIF mới quyết định trạng thái)
- [x] AC: 6 unit test parse (hợp lệ/thiếu key/không phải object/giá trị không phải số/JSON rác) + 2 integration test (có metrics → MetricSet đúng 7 key; không có metrics → Succeeded, 0 MetricSet)
- [x] Nghiệm thu thật: `dotnet ef database update` sạch trên DB `eaap`; `grafana_ro` SELECT được nhưng INSERT/UPDATE bị từ chối

## M2 — Adapter test-report ⏳
## M3 — Adapter coverage ⏳
## M4 — Workflow step run-tests + .eaap/config.yaml ⏳
## M5 — Dedup cross-job + baseline + API ⏳
## M6 — Gate per-repository ⏳
## M7 — TrendPoint + Grafana ⏳
## M8 — README + tổng kết ⏳

---

## Review (Phase 1)

- **Kết quả:** 6/6 milestone hoàn thành; `dotnet build -warnaserror` 0 warning; **23 tests pass** (18 unit + 5 integration Testcontainers); e2e thật trên k3d + Argo + MinIO + OPA chạy trọn pipeline.
- **Sai lệch so spec (đều có ADR):** build bằng SDK 10 target net8.0 (ADR-002); MassTransit 8.2.5 (ADR-003); polling thay webhook (ADR-004); 1 analyzer/job Phase 1 (ADR-005); e2e nghiệm thu bằng echo adapter do giới hạn đĩa, MegaLinter adapter vẫn đầy đủ theo contract (ADR-006).
- **Bug tìm được khi e2e:** minio/mc thiếu tar (tách bước extract); Argo emissary cần command tường minh với image local; imagePullPolicy IfNotPresent cho image import; polling loop bị starve bởi workflow 404 (fix per-job try/catch).
- **Checklist Phần 12:** docker compose + k3d script chạy sạch ✔; acceptance M1–M6 pass (M6c manual theo README) ✔; dotnet test 100% ✔; openapi.json exported ✔; ADR đầy đủ ✔; README ≤10 lệnh ✔; không secret trong source (dev creds chỉ trong appsettings.Development.json mẫu + docker-compose defaults) ✔.
