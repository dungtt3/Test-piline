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
## M8 — README + tổng kết ✅
- [x] README bổ sung mục Phase 2: demo test+coverage+gate+trend ≤ 12 lệnh + mẫu `.eaap/config.yaml`; tiêu đề + intro cập nhật
- [x] `docs/api/openapi.json` export lại — có đủ endpoint mới (`/baseline`, `/gate`, `/trend`)
- [x] Checklist Phần 12: regression Phase 1 xanh ✔; adapter `megalinter`/`echo` không sửa 1 dòng (last touch = commit init) ✔; 1 migration duy nhất `Phase2_TestQuality` ✔; Grafana provisioning live OK ✔; ADR đủ (007–010) ✔

---

# EAAP Phase 3 — Security & Dependency (theo EAAP-AI-Build-Spec-Phase3.md)

## M1 — Migration Phase3 + SecurityEnricher ✅
- [x] Domain: enum `SecuritySeverity`; Warning thêm `SecuritySeverity`/`Cve`/`Cwe`/`IsSuppressed`; entity `Suppression` (unique repo+fingerprint, reason ≥10); `TrendPoint.WarningSuppressed`
- [x] `Eaap.Sarif/SecurityEnricher`: (1) CVSS `security-severity` → band None/Low/Med/High/Crit; (2) thiếu CVSS → map từ level CHỈ khi adapter category=security; (3) non-security → None; CWE regex trong tags/taxa/relationships, CVE regex trong ruleId/message
- [x] Ingestion resolve category từ registry (`AdapterEntry.Category`) → set SecuritySeverity/Cve/Cwe khi lưu warning
- [x] manifest 4 adapter cũ thêm `category`; registry appsettings thêm Category + đăng ký trivy/semgrep/gitleaks (security)
- [x] Migration duy nhất `Phase3_Security`; apply sạch trên DB thật
- [x] AC Phần 4 (unit): 13 test — 8 band CVSS + fixture thật Trivy (CVSS 10→Critical, CVE-2021-44228, CWE-502), Semgrep (warning→Medium, CWE-78 từ tag), Gitleaks (error→High), non-security→None, CVSS thắng bất kể category

## M2 — Adapter trivy ✅
- [x] `adapters/trivy/` native-sarif: `trivy fs --scanners vuln,secret,misconfig --format sarif` + JSON gốc vào /artifacts; entrypoint 32 dòng (≤50)
- [x] Offline: DB nhúng vào image lúc build, `TRIVY_SKIP_DB_UPDATE=true` runtime (ADR-011)
- [x] Fixture `tests/fixtures/vulnerable-app/` (dùng chung 3 adapter): package.json lodash CVE, AWS key **fake** (key ví dụ công khai AWS), SQLi Python; README cảnh báo rõ cố tình lỗi + secret fake
- [x] Verify tĩnh: `sh -n` OK, SARIF native Trivy đã có SecurityEnricher (M1); e2e live hoãn như ADR-006 (image lớn)

## M3 — Adapter semgrep ✅
- [x] `adapters/semgrep/` native-sarif: `--config /eaap-rules --sarif`, không `--error`; entrypoint 34 dòng; luôn đảm bảo SARIF hợp lệ (rỗng nếu cần)
- [x] Offline: rule vendored trong image (`p/security-audit`+`p/secrets` best-effort lúc build) + bộ tối thiểu `rules/eaap-python-sqli.yaml` (CWE-89) bắt SQLi vulnerable-app; `SEMGREP_USE_REGISTRY=1` để bật --config auto (ADR-012)

## M4 — Adapter gitleaks ✅
- [x] `adapters/gitleaks/` native-sarif: `gitleaks detect --no-git --report-format sarif --exit-code 0 --redact`; entrypoint 27 dòng
- [x] `--no-git` vì snapshot là tarball không có .git (thiết kế Phase 1); manifest ghi chú chỉ quét trạng thái hiện tại; base image override ENTRYPOINT về `/eaap-entrypoint.sh`
## M5 — API security filter + summary ✅
- [x] `WarningDto` thêm `SecuritySeverity`/`Cve`/`Cwe`/`IsSuppressed`; warnings endpoint filter `securitySeverity=High,Critical` (comma list) + `cwe=` + `includeSuppressed` (default hidden)
- [x] `GET /jobs/{id}/security-summary` → `bySeverity` (đủ 5 mức, mặc định 0), `byCwe`/`byCve` (đếm, sắp giảm dần); loại suppressed
- [x] AC (integration): SARIF 4 finding qua analyzer `trivy` → filter Critical,High=3; cwe=CWE-79=1; summary critical=1/high=2/medium=1; byCwe có 502/79/89; byCve có CVE-2021-44228
## M6 — Suppression ✅
- [x] Ingest đánh dấu `IsSuppressed` khi fingerprint khớp Suppression còn hiệu lực (ExpiresAt null hoặc tương lai)
- [x] Gate summary loại suppressed (error/warning/newWarning đều tính trên non-suppressed); Trend `WarningTotal` loại suppressed, đếm vào `WarningSuppressed`
- [x] CRUD API: `POST/GET/DELETE /repositories/{id}/suppressions` — validate reason ≥10 ký tự + fingerprint phải tồn tại trong warning/baseline của repo; 409 nếu đã có; `?includeExpired`
- [x] AC (integration): job1 error→GateFailed; suppress X; job2 X IsSuppressed, gate Succeeded, trend WarningSuppressed=1, warnings ẩn (includeSuppressed hiện lại); hết hạn → job3 tính lại GateFailed; + test validate reason ngắn/fingerprint lạ → 400
## M7 — Gate security ✅
- [x] `GateSummary` thêm `SecurityCounts` (critical/high/medium/low, non-suppressed); `GateThresholds` thêm `MaxSecurityCritical`/`MaxSecurityHigh`; OpaOptions mặc định 0 (nghiêm)
- [x] rego thêm 2 rule: fail nếu critical > maxSecurityCritical, high > maxSecurityHigh; consumer tính security counts + resolve binding
- [x] AC: direct gate (critical/high fail mặc định, medium/low không fail, nới ngưỡng cho phép) + integration (trivy critical → GateFailed "security.critical=1 > max 0" → suppress → Succeeded)
## M8 — Demo + README Phase 3 ✅
- [x] README mục Phase 3: demo quét vulnerable-app + security-summary + suppress + gate ≤12 lệnh; tiêu đề/intro/Tài liệu cập nhật
- [x] `docs/api/openapi.json` export lại — có `security-summary`, `suppressions` (POST/GET/DELETE)
- [x] Checklist Phần 9: regression Phase 1+2 xanh ✔; 3 adapter native-sarif, entrypoint 32/34/27 ≤50 ✔; vulnerable-app README cảnh báo + secret fake ✔; ADR offline trivy(011)/semgrep(012) ✔; openapi cập nhật ✔

---

# EAAP Phase 4 — Runtime & Enterprise (theo EAAP-AI-Build-Spec-Phase4.md)

## M1 — Migration Phase4 + Technical Debt ✅
- [x] Migration duy nhất `Phase4_Enterprise`: `Warning.DebtMinutes`, `TrendPoint.DebtTotalMinutes`, `Repository.WebhookSecret` + bảng User/UserRole/ApiToken/NotificationChannel/NotificationDeliveryLog (dùng ở M5/M6/M7); apply sạch DB thật
- [x] `DebtCalculator` (Domain, thuần): suppressed=0; explicit `properties.debtMinutes` thắng; security Critical=120/High=60 override; level error=30/warning=10/note=2
- [x] Ingest set `DebtMinutes`; TrendService tính `DebtTotalMinutes`; API `GET /repositories/{id}/debt` (tổng job mới nhất + trend); Grafana panel "Technical debt (hours)"
- [x] AC: 11 unit test bảng quy đổi (level/security/explicit/suppressed/negative); integration tổng debt = 180 (critical 120 + high 60 — analyzer security map error→High) đúng qua pipeline + API

## M2 — fingerprintKey trong Eaap.Sarif ✅
- [x] `WarningFingerprint.ComputeFromKey(ruleId, fingerprintKey)` = SHA256(ruleId|key); ingest dùng khi result có `properties.fingerprintKey`, ngược lại công thức cũ (backward compatible)
- [x] AC: 4 unit test mới (ổn định khi message đổi, khác nhau theo rule/key, không đụng công thức path) + 10 test cũ vẫn pass
## M3 — Adapter prometheus-slo + demo stack ✅
- [x] `src/Eaap.Adapters.PrometheusSlo` (query mode): `SloEvaluator` thuần (operator </<=/>/>=/==/!=, violation = điều kiện healthy sai) + `PrometheusClient` (instant query) + Program đọc `EAAP_RUNTIME_CONFIG`
- [x] Violation → SARIF `slo.<id>` + properties observedValue/threshold/query/**fingerprintKey**; metrics `runtime.slo.<id>.value` cho MỌI SLO; Prometheus down → exit≠0 (analyzer Failed, khác SLO fail)
- [x] `adapters/prometheus-slo/` (mode query, requiresWorkspace false, category runtime); registry + build-adapter; docker-compose profile `runtime-demo` (prometheus + demo-app) + `deploy/prometheus/prometheus.yml`
- [x] AC: 13 unit test SloEvaluator; **chạy adapter thật với Prometheus giả** → 1/2 SLO fail đúng, metrics đủ 2 giá trị, exit 0; Prometheus down → exit 1
## M4 — Gate xuyên suốt ✅
- [x] `GateSummary` thêm `RuntimeInfo(SloViolations)` + `DebtInfo(TotalMinutes, DeltaMinutes)`; `GateThresholds` thêm `MaxSloViolations` (default 0 nghiêm) + `MaxDebtDeltaMinutes` (default int.MaxValue = tắt)
- [x] rego 2 rule: `runtime.sloViolations > max`, `debt.deltaMinutes > max`; OPA input đủ section summary/metrics/runtime/debt/thresholds
- [x] Consumer tính sloViolations (warning `slo.*` non-suppressed) + debt total + delta (so TrendPoint gần nhất); binding override 2 key mới
- [x] AC: direct gate (SLO fail, debt-delta tắt mặc định/bật qua binding, **cross-lifecycle 6 chiều trong 1 result**) + integration "one gate": 1 job 2 analyzer run (coverage 40% + slo violation) → 1 GateEvaluation có cả 2 violation
## M5 — Auth + RBAC ✅
- [x] `AuthTokenService` (BCrypt hash mật khẩu, JWT HS256 8h, API token `eaap_...` + SHA256 hash) — ADR-013; `EaapAuthHandler` custom xử lý cả JWT lẫn API token (phân biệt qua prefix)
- [x] Policy: `RequireMaintainer` (write), `RequireAdmin` (user/token); toàn bộ `/api/v1/*` yêu cầu auth, `/auth/login`+`/health` anonymous; seed Admin từ env khi User rỗng; Swagger nút Authorize
- [x] Endpoints: `/auth/login`, `/auth/tokens` (POST/DELETE), `/users` (GET/POST Admin), `/users/{id}/role` (PUT Admin)
- [x] AC: 7 unit test cơ chế token (JWT roundtrip/hết hạn/sai secret, BCrypt, API token hash/prefix); integration RBAC matrix (Viewer read 200/write 403, Maintainer write 201, users Admin-only), 401 khi anonymous, login thật → JWT dùng được / sai mật khẩu 401
- [x] `TestAuthHandler` (Testing) giữ 33 test hành vi cũ chạy (default Admin) — không phải sửa
## M6 — Notification Center ✅
- [x] Kênh Webhook (HMAC-SHA256 `X-Eaap-Signature`), Slack (Block Kit), Email (MailKit) — ADR-014; suppress advisory MailKit NU1902 (chỉ kết nối SMTP operator cấu hình)
- [x] `NewCriticalSecurityFound` publish từ ingestion (warning IsNew+Critical); trigger consumer nghe GateEvaluated/JobFinished/NewCriticalSecurityFound → fan-out per-channel `NotificationDeliveryRequested`
- [x] Retry MassTransit (5s/25s/125s, cấu hình được); hết retry → `Fault<T>` → `NotificationDeliveryLog`
- [x] API: CRUD `/repositories/{id}/notifications` + `POST /notifications/{id}/test`
- [x] AC: 8 unit (HMAC verify, Slack Block Kit 3 field, email, repo-name); 3 integration WireMock (payload+HMAC verify, disabled không gửi, 500×2 rồi 200 → retry OK không log lỗi)
## M7 — Webhook GitHub/GitLab ⏳
## M8 — Demo tổng + README v4 → tag v1.0.0 ⏳

---

## Review (Phase 3)

- **Kết quả:** 8/8 milestone; `dotnet build -warnaserror` 0 warning; **108 test pass** (80 unit + 28 integration), tăng từ 88 cuối Phase 2; regression Phase 1+2 nguyên vẹn.
- **Kiến trúc thêm:** `SecurityEnricher` (CVSS→severity, CWE/CVE) + cột `SecuritySeverity`/`Cve`/`Cwe`/`IsSuppressed`; 3 adapter security native-sarif (trivy/semgrep/gitleaks) offline; API filter security + `security-summary`; suppression (fingerprint) loại khỏi gate+trend; gate security nghiêm mặc định.
- **Sai lệch/quyết định (ADR):** Trivy DB đóng băng vào image, quét offline (ADR-011); Semgrep rule vendored offline + bộ tối thiểu CWE-89 (ADR-012). E2e live 3 adapter hoãn như ADR-006 (image lớn) — adapter verify tĩnh (sh -n, ≤50 dòng) + SARIF native đã test qua SecurityEnricher.
- **KPI "thêm tool SARIF < 1 ngày":** 3 adapter đều là entrypoint shell mỏng (27–34 dòng) bọc image chính thức, không cần converter — minh chứng chi phí thêm tool có SARIF là rất thấp.
- **Checklist Phần 9:** đủ ✔.

---

## Review (Phase 2)

- **Kết quả:** 8/8 milestone; `dotnet build -warnaserror` 0 warning; **88 test pass** (67 unit + 21 integration Testcontainers), tăng từ 23 test cuối Phase 1; Grafana provisioning nghiệm thu live (datasource grafana_ro kết nối OK, dashboard nạp).
- **Kiến trúc thêm:** Metric tách khỏi Warning (`MetricSet` + `metrics.json`); 2 adapter converter .NET (`test-report`, `coverage`) dùng chung `Eaap.Sarif`; baseline xuyên job (`IsNew`/`Resolved`); gate per-repo (coverage/tests/newWarnings, binding); TrendPoint + Grafana.
- **Sai lệch/đơn giản hoá (đều có ADR):** source adapter .NET trong `src/` build context repo root (ADR-007); YamlDotNet cho `.eaap/config.yaml` (ADR-008); resolve baseline chỉ khi job đủ tập analyzer + baseline chỉ mutate trên default branch (ADR-009); role `grafana_ro` tạo trong migration M1 (ADR-010).
- **Bug tìm được khi làm/test:** (1) `Dictionary<string,double>` cho jsonb làm vỡ InMemory provider → value converter; (2) `TrimStart('.', '/')` nuốt dấu chấm của `.eaap` → config không khớp cả ở production; (3) rego `%.2f` với JSON int `90` → `%!f(int=90)` → dùng `%v`; (4) `.gitignore` pattern `*.coverage` nuốt thư mục `Eaap.Adapters.Coverage` trên Windows (case-insensitive).
- **Backward-compat:** contract adapter Phase 1 bất biến; `megalinter`/`echo` chạy nguyên; job không có `.eaap/config.yaml` submit workflow y hệt Phase 1 (test-enabled=false).
- **Checklist Phần 12:** đủ ✔. Regression Phase 1 (23 test) vẫn xanh trong tổng 88.

---

## Review (Phase 1)

- **Kết quả:** 6/6 milestone hoàn thành; `dotnet build -warnaserror` 0 warning; **23 tests pass** (18 unit + 5 integration Testcontainers); e2e thật trên k3d + Argo + MinIO + OPA chạy trọn pipeline.
- **Sai lệch so spec (đều có ADR):** build bằng SDK 10 target net8.0 (ADR-002); MassTransit 8.2.5 (ADR-003); polling thay webhook (ADR-004); 1 analyzer/job Phase 1 (ADR-005); e2e nghiệm thu bằng echo adapter do giới hạn đĩa, MegaLinter adapter vẫn đầy đủ theo contract (ADR-006).
- **Bug tìm được khi e2e:** minio/mc thiếu tar (tách bước extract); Argo emissary cần command tường minh với image local; imagePullPolicy IfNotPresent cho image import; polling loop bị starve bởi workflow 404 (fix per-job try/catch).
- **Checklist Phần 12:** docker compose + k3d script chạy sạch ✔; acceptance M1–M6 pass (M6c manual theo README) ✔; dotnet test 100% ✔; openapi.json exported ✔; ADR đầy đủ ✔; README ≤10 lệnh ✔; không secret trong source (dev creds chỉ trong appsettings.Development.json mẫu + docker-compose defaults) ✔.
