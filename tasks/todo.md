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

## M2 — Adapter test-report ✅
- [x] `src/Eaap.Adapters.TestReport` (parser + SARIF builder tách riêng để unit test được) — ADR-007; tái dụng `Eaap.Sarif`, không tự viết model SARIF
- [x] `adapters/test-report/`: Dockerfile (context = repo root), entrypoint.sh → `/eaap-entrypoint.sh`, manifest.yaml (`mode: converter`, `emitsMetrics: true`); đăng ký vào `Adapters` registry
- [x] Parse TRX + JUnit; **đếm từ element chứ không tin attribute tổng kết** — TRX thật của `dotnet test` ghi `notExecuted="0"` dù có 1 test NotExecuted
- [x] Fixtures **thật**: TRX từ `dotnet test --logger trx`, JUnit từ pytest `--junitxml` (đã khử đường dẫn tuyệt đối), + 1 XML rác
- [x] `deploy/k3d/build-adapter.ps1|.sh` tổng quát hoá cho nhiều adapter + build context repo root
- [x] AC: 13 unit test (2 fixture thật đúng passed/failed/skipped; file rác → null + lý do; XML hỏng; `<error>` tính là fail; file/line → location; nhiều testsuite; SARIF ghi ra file có BOM vẫn qua validator)
- [x] Nghiệm thu thật: chạy adapter trên thư mục có cả 3 fixture → exit 0, quét đệ quy, bỏ qua file rác, tổng hợp 6 test; không có report → exit 0 + `tests.total=0`
## M3 — Adapter coverage ✅
- [x] `src/Eaap.Adapters.Coverage` + `adapters/coverage/` (cùng khuôn M2, ADR-007); đăng ký vào registry
- [x] Parse Cobertura (ưu tiên) và lcov; **merge bằng tổng lines-covered/lines-valid, KHÔNG trung bình tỷ lệ**
- [x] Có cả Cobertura lẫn lcov → chỉ dùng Cobertura (tránh đếm đôi cùng một lần chạy)
- [x] `coverage.file.low` cho file dưới `COVERAGE_FILE_THRESHOLD` (default 50), trần 200 result + `coverage.files.truncated`, sắp xếp file tệ nhất trước để phần bị cắt là phần ít quan trọng
- [x] Class partial dùng chung filename → mỗi dòng chỉ đếm 1 lần
- [x] `metrics.json` chỉ ghi chiều nào thực sự đo được (không có branch → không ghi `coverage.branch`)
- [x] Fixtures: Cobertura **thật** từ coverlet (cắt gọn, tính lại root cho nhất quán), lcov, + 2 file merge 9/10 và 1/90
- [x] AC: 16 unit test — merge ra **10%** đúng chứ không phải 45.56% nếu trung bình tỷ lệ; threshold; truncation; lcov đủ line/branch/method
- [x] Nghiệm thu thật: 4 kịch bản chạy adapter (merge / lcov+threshold / ưu tiên Cobertura / không có report) đều exit 0 và đúng số
## M4 — Workflow step run-tests + .eaap/config.yaml ✅
- [x] `EaapRepoConfig`/`RepoTestConfig` + port `IRepoConfigReader` (Application); `RepoConfigReader` đọc thẳng `.eaap/config.yaml` từ tarball snapshot trên MinIO, không cần clone lại — YamlDotNet (ADR-008)
- [x] Chịu lỗi: thiếu file / YAML hỏng / khai báo nửa vời (`enabled: true` nhưng thiếu `command`) → không chạy test, KHÔNG chặn job
- [x] WorkflowTemplate thêm step `run-tests` với `when: test-enabled == true`, mount `/workspace` **ghi được** (khác analyzer mount ro); `continueOn.failed: true` để test fail không làm fail workflow, nhưng lỗi image/timeout (Argo "error") vẫn fail đúng như spec
- [x] Tham số test luôn được gửi, mặc định `test-enabled=false` → job Phase 1 submit workflow y hệt cũ
- [x] AC: 9 unit test parser + 2 test consumer (có config → `ArgoSubmitRequest.Test` runnable; không config → `Test == null`)
- [x] **Bug test bắt được:** `TrimStart('.', '/')` xoá luôn dấu chấm của `.eaap` → config không bao giờ khớp, hỏng cả đường production; đã sửa thành chỉ strip tiền tố `./` và thêm test khoá lại
## M5 — Dedup cross-job + baseline + API ✅
- [x] `BaselineService` chạy trong `CloseJobIfFinishedAsync` **trước gate** (để M6 có newWarningCount); DI scoped
- [x] Mỗi warning: có baseline Active → `IsNew=false`; chưa có → `IsNew=true` + tạo baseline; đã Resolved mà tái xuất → reactivate + `IsNew=true` (tránh vi phạm unique index)
- [x] Resolve chỉ trên default branch, chỉ khi `jobAnalyzers ⊇ everSeenAnalyzers` (ADR-009); feature branch chỉ đánh IsNew, không đụng baseline
- [x] API: `GET /jobs/{id}/warnings?isNew=true|false` (+ cột `IsNew` trong `WarningDto`); `GET /repositories/{id}/baseline?status=` (paged, `BaselineDto`)
- [x] AC: integration 3 job liên tiếp → job1 5 baseline; job2 1 IsNew + 2 Resolved (chỉ ruleF mới); job3 giống job2 → 0 IsNew, 0 resolve mới; + test API filter isNew và baseline status
- [x] 3 unit test BaselineService (feature branch không đụng baseline; reactivate; skip resolve khi thiếu analyzer)
## M6 — Gate per-repository ✅
- [x] `GateSummary` thêm `NewWarningCount`; `IQualityGate.EvaluateAsync(summary, metrics, thresholds)`; `GateThresholds` record
- [x] rego mở rộng: fail `tests.failed > maxTestsFailed`, `newWarningCount > maxNewWarnings` (âm = tắt), `coverage.line < minCoverageLine` (chỉ khi metric tồn tại); thiếu coverage → note "skipped", không fail; `pass = không có failure`, `violations = failures ∪ notes`
- [x] Default thresholds hợp lý: maxWarnings=100, maxNewWarnings=-1 (tắt — scan đầu không fail vì mọi warning đều mới), minCoverageLine=0 (tắt), maxTestsFailed=0 (nghiêm, chỉ áp khi repo chạy test)
- [x] `GatePolicyBinding` override từng key; consumer gom metrics từ MetricSet + tính newWarningCount + resolve thresholds
- [x] API: `PUT/GET /repositories/{id}/gate`
- [x] AC: (a) coverage 82.5% pass mặc định → đặt binding minCoverageLine=90 → GateFailed (chạy thật qua API+pipeline); (b) newWarningCount=1, maxNewWarnings=0 → GateFailed; + tests.failed, skipped-note, binding round-trip
- [x] **rego bug bắt được:** `%.2f` với JSON `90` (int) → `%!f(int=90)`; đổi sang `%v`. Và MetricsIngestionTest lộ đúng: metrics tests.failed=2 giờ làm GateFailed (metrics chảy vào gate)
## M7 — TrendPoint + Grafana ✅
- [x] `TrendService` ghi 1 TrendPoint/job kết thúc (Succeeded/GateFailed) trên default branch; idempotent (unique JobId + check tồn tại); lấy new/resolved từ `BaselineOutcome`
- [x] docker-compose thêm Grafana + provisioning: datasource Postgres qua `grafana_ro` (password từ env), dashboard 4 panel (Warning total+errors, New vs Resolved, Coverage %, Tests failed), biến `$repository` lấy từ TrendPoints (vì grafana_ro không có quyền đọc Repositories)
- [x] API `GET /repositories/{id}/trend?from=&to=`
- [x] AC: integration 2 job → TrendPoint đúng số (WarningTotal/New/Resolved/CoverageLine); API trả đúng thứ tự thời gian; **grafana_ro SELECT TrendPoints được nhưng INSERT/UPDATE bị từ chối (SqlState 42501)**
- [x] Nghiệm thu Grafana thật (live smoke): container start, datasource `grafana_ro` kết nối Postgres OK, dashboard `eaap-trend` provisioned
## M8 — README + tổng kết ⏳

---

## Review (Phase 1)

- **Kết quả:** 6/6 milestone hoàn thành; `dotnet build -warnaserror` 0 warning; **23 tests pass** (18 unit + 5 integration Testcontainers); e2e thật trên k3d + Argo + MinIO + OPA chạy trọn pipeline.
- **Sai lệch so spec (đều có ADR):** build bằng SDK 10 target net8.0 (ADR-002); MassTransit 8.2.5 (ADR-003); polling thay webhook (ADR-004); 1 analyzer/job Phase 1 (ADR-005); e2e nghiệm thu bằng echo adapter do giới hạn đĩa, MegaLinter adapter vẫn đầy đủ theo contract (ADR-006).
- **Bug tìm được khi e2e:** minio/mc thiếu tar (tách bước extract); Argo emissary cần command tường minh với image local; imagePullPolicy IfNotPresent cho image import; polling loop bị starve bởi workflow 404 (fix per-job try/catch).
- **Checklist Phần 12:** docker compose + k3d script chạy sạch ✔; acceptance M1–M6 pass (M6c manual theo README) ✔; dotnet test 100% ✔; openapi.json exported ✔; ADR đầy đủ ✔; README ≤10 lệnh ✔; không secret trong source (dev creds chỉ trong appsettings.Development.json mẫu + docker-compose defaults) ✔.
