# EAAP — AI BUILD SPECIFICATION

**Mục đích tài liệu:** Đây là tài liệu đầu vào cho AI coding agent (Claude Code hoặc tương đương) để **triển khai trực tiếp** EAAP Phase 1 (Foundation + MegaLinter Adapter). Tài liệu được viết theo nguyên tắc: mọi quyết định đã được chốt sẵn, mọi task có acceptance criteria kiểm chứng được, AI không cần hỏi lại về kiến trúc.

**Nguồn:** Chuyển đổi từ PRD-001 v2.0 (Integrate-first, Build-second).

---

## PHẦN 0 — QUY TẮC CHO AI AGENT

Đọc kỹ trước khi viết bất kỳ dòng code nào:

1. **Không phát minh lại:** Warning model = SARIF 2.1.0. Job engine = Argo Workflows. Policy = OPA/Rego. Storage = MinIO + PostgreSQL. Event bus = RabbitMQ (MassTransit). Không tự viết scheduler, rule engine, hay format output mới.
2. **Thứ tự triển khai:** làm đúng theo thứ tự Milestone M1 → M6. Không nhảy cóc. Mỗi milestone kết thúc bằng việc chạy được acceptance test của milestone đó.
3. **Definition of Done cho mọi task:** (a) code compile không warning, (b) unit test pass, (c) acceptance criteria của task được chứng minh bằng test hoặc lệnh chạy được, (d) không có TODO không giải thích.
4. **Khi mơ hồ:** ưu tiên giải pháp đơn giản nhất thỏa mãn acceptance criteria, ghi chú quyết định vào `docs/decisions/` dưới dạng ADR ngắn (5-10 dòng), rồi tiếp tục. Không dừng lại chờ hỏi.
5. **Không hard-code secret.** Mọi config qua environment variable + `appsettings.json`, có `appsettings.Development.json` mẫu.
6. **Ngôn ngữ code:** C# / .NET 8, comment và tên biến bằng tiếng Anh. Tài liệu sinh ra bằng tiếng Việt.

---

## PHẦN 1 — TECH STACK (ĐÃ CHỐT, KHÔNG THAY ĐỔI)

| Thành phần | Công nghệ | Ghi chú |
| --- | --- | --- |
| Ngôn ngữ platform | .NET 8 (C#), ASP.NET Core Minimal API | Clean Architecture |
| Database | PostgreSQL 16 + EF Core 8 | Migrations code-first |
| Object storage | MinIO (S3 API) | SDK: AWSSDK.S3 |
| Event bus | RabbitMQ + MassTransit | Topology mặc định của MassTransit |
| Job engine | Argo Workflows (chạy trên k3d/kind local) | Submit qua Argo REST API |
| Policy engine | OPA (chạy dạng sidecar container, REST API) | Policy viết bằng Rego |
| Adapter đầu tiên | MegaLinter (image `oxsecurity/megalinter`) | Output SARIF native |
| Frontend Phase 1 | Không có UI. Chỉ REST API + Swagger | UI để Phase sau |
| Container | Docker + docker-compose cho hạ tầng local | k3d cho Argo |
| Test | xUnit + Testcontainers (Postgres, RabbitMQ, MinIO) | Integration test bắt buộc |

---

## PHẦN 2 — CẤU TRÚC REPOSITORY

Tạo monorepo với cấu trúc sau:

```
eaap/
├── docker-compose.yml              # postgres, rabbitmq, minio, opa
├── docs/
│   ├── decisions/                  # ADR do AI tự ghi khi ra quyết định
│   └── api/                        # export OpenAPI spec
├── deploy/
│   ├── k3d/                        # script tạo cluster + cài Argo
│   └── argo/                       # WorkflowTemplate mẫu
├── policies/
│   └── quality-gate/
│       └── default.rego            # gate mặc định
├── adapters/
│   └── megalinter/
│       ├── Dockerfile              # FROM oxsecurity/megalinter + entrypoint
│       ├── entrypoint.sh
│       └── manifest.yaml           # theo schema Phần 5
├── src/
│   ├── Eaap.Domain/                # entities, value objects, domain events
│   ├── Eaap.Application/           # use cases, interfaces (ports)
│   ├── Eaap.Infrastructure/        # EF Core, MinIO, RabbitMQ, Argo client, OPA client
│   ├── Eaap.Api/                   # Minimal API, endpoints, Swagger
│   └── Eaap.Sarif/                 # SARIF 2.1.0 model + validator + fingerprint
├── tests/
│   ├── Eaap.UnitTests/
│   └── Eaap.IntegrationTests/      # Testcontainers
└── README.md                       # hướng dẫn chạy end-to-end
```

---

## PHẦN 3 — DOMAIN MODEL & DATABASE SCHEMA

### Entities (Eaap.Domain)

```
Repository
  Id (guid), Provider (enum: GitHub|GitLab|Bitbucket|AzureDevOps|GenericGit),
  CloneUrl, DefaultBranch, CreatedAt

Snapshot
  Id (guid), RepositoryId (fk), Branch, CommitSha (40 hex),
  StoragePath (đường dẫn tarball trên MinIO), SizeBytes, CreatedAt
  Unique constraint: (RepositoryId, CommitSha)

AnalysisJob
  Id (guid), SnapshotId (fk), Status (enum: Pending|Running|Succeeded|Failed|GateFailed),
  ArgoWorkflowName, RequestedAnalyzers (jsonb array of analyzer ids),
  CreatedAt, StartedAt?, FinishedAt?

AnalyzerRun
  Id (guid), JobId (fk), AnalyzerId (string, vd "megalinter"),
  Status (enum: Pending|Running|Succeeded|Failed),
  SarifArtifactPath?, RawArtifactPath?, WarningCount, StartedAt?, FinishedAt?

Warning
  Id (guid), JobId (fk), AnalyzerRunId (fk),
  RuleId, Level (enum: None|Note|Warning|Error),
  Message, FilePath?, StartLine?, EndLine?,
  Fingerprint (string, indexed),        -- xem thuật toán Phần 6
  SarifRaw (jsonb, result gốc),
  CreatedAt

GateEvaluation
  Id (guid), JobId (fk), PolicyName, Passed (bool),
  Violations (jsonb), EvaluatedAt
```

### Quy tắc

- Mọi enum lưu dạng string trong DB.
- `Warning.SarifRaw` giữ nguyên `result` object gốc của SARIF — nguồn sự thật; các cột còn lại là denormalized để query.
- Index: `Warning(JobId)`, `Warning(Fingerprint)`, `AnalysisJob(SnapshotId)`.

---

## PHẦN 4 — REST API SPECIFICATION (Eaap.Api)

Tất cả endpoint prefix `/api/v1`. Trả về ProblemDetails chuẩn khi lỗi.

```
POST   /repositories
       Body: { provider, cloneUrl, defaultBranch? }
       201 → Repository

GET    /repositories
GET    /repositories/{id}

POST   /repositories/{id}/scans
       Body: { branch?, commitSha?, analyzers: ["megalinter"] }
       Hành vi: nếu chưa có Snapshot cho commit → clone + tạo snapshot;
                tạo AnalysisJob (Pending) → publish event → 202
       202 → { jobId }

GET    /jobs/{id}
       → AnalysisJob + danh sách AnalyzerRun + GateEvaluation (nếu có)

GET    /jobs/{id}/warnings?level=&ruleId=&page=&pageSize=
       → paged list Warning

GET    /jobs/{id}/sarif
       → SARIF log tổng hợp (merge tất cả AnalyzerRun) — content-type application/sarif+json

POST   /internal/results/{analyzerRunId}
       (endpoint cho adapter callback/ingestion — Phase 1 dùng polling MinIO,
        endpoint này optional, xem M4)
```

Swagger UI bật ở `/swagger`, export OpenAPI JSON vào `docs/api/openapi.json` bằng build step.

---

## PHẦN 5 — ADAPTER CONTRACT (BẤT BIẾN)

Mọi adapter là OCI container tuân theo:

```
Mount:
  /workspace   (read-only)  — source code của Snapshot
  /results     (read-write) — adapter ghi *.sarif vào đây
  /artifacts   (read-write) — report gốc, log tùy ý

Env bắt buộc platform truyền vào:
  EAAP_JOB_ID, EAAP_ANALYZER_RUN_ID, EAAP_SNAPSHOT_COMMIT

Exit code:
  0  = adapter chạy xong (dù có warning hay không)
  ≠0 = adapter lỗi → AnalyzerRun.Status = Failed
```

### manifest.yaml schema

```yaml
id: megalinter                # unique, lowercase
name: MegaLinter
version: "1.0.0"
image: eaap/adapter-megalinter:latest
mode: native-sarif            # native-sarif | converter | query
timeoutSeconds: 1800
resources:
  cpu: "2"
  memory: 4Gi
```

### Adapter MegaLinter (adapters/megalinter)

- `Dockerfile`: FROM `oxsecurity/megalinter:latest`, copy entrypoint.
- `entrypoint.sh`: chạy MegaLinter với `SARIF_REPORTER=true`, `REPORT_OUTPUT_FOLDER=/artifacts/megalinter`, sau đó copy file SARIF tổng hợp (`megalinter-report.sarif`) sang `/results/megalinter.sarif`. Exit 0 kể cả khi linter tìm thấy lỗi (dùng `DISABLE_ERRORS=true` hoặc bỏ qua exit code lint, chỉ fail khi MegaLinter crash).

---

## PHẦN 6 — Eaap.Sarif: MODEL, VALIDATOR, FINGERPRINT

1. **Model:** dùng package `Sarif.Sdk` (Microsoft.CodeAnalysis.Sarif) để parse/serialize SARIF 2.1.0. Không tự viết model.
2. **Validator:** hàm `SarifValidator.Validate(stream)` → kiểm tra: version == "2.1.0", có ≥1 run, mỗi run có tool.driver.name. Trả về danh sách lỗi thay vì throw.
3. **Fingerprint (dedup):**

```
fingerprint = SHA256(lowercase(
    ruleId + "|" +
    normalizedPath + "|" +          # path tương đối từ /workspace, chuẩn hóa "/"
    (startLine hoặc "0") + "|" +
    first80Chars(message.text)
))
```

Ghi vào `Warning.Fingerprint`. Phase 1 chỉ lưu, chưa dedup cross-job (để Phase 2). Nếu 2 warning trong **cùng một job** trùng fingerprint → giữ 1, tăng counter trong `SarifRaw.properties.duplicateCount`.

---

## PHẦN 7 — EVENT CATALOG (MassTransit)

Contract đặt trong `Eaap.Domain/Events`, tên cố định:

```
SnapshotCreated      { SnapshotId, RepositoryId, CommitSha }
JobRequested         { JobId, SnapshotId, Analyzers[] }
JobStarted           { JobId, ArgoWorkflowName }
AnalyzerRunFinished  { AnalyzerRunId, JobId, Status, SarifArtifactPath? }
JobFinished          { JobId, Status }
GateEvaluated        { JobId, Passed, PolicyName }
```

Consumer Phase 1:
- `JobRequestedConsumer` → sinh Argo Workflow spec, submit, cập nhật Status=Running, publish JobStarted.
- `AnalyzerRunFinishedConsumer` → tải SARIF từ MinIO, validate, ingest thành Warnings, nếu tất cả run xong → gọi OPA gate → publish GateEvaluated + JobFinished.

---

## PHẦN 8 — TÍCH HỢP ARGO WORKFLOWS

- Orchestrator sinh Workflow từ template `deploy/argo/analysis-job.yaml` với các step:
  1. `fetch-snapshot`: init container tải tarball snapshot từ MinIO, giải nén vào volume `/workspace`.
  2. `analyzer-<id>` (song song nếu nhiều adapter): chạy image adapter theo contract Phần 5, mount volume.
  3. `upload-results`: đẩy `/results` và `/artifacts` lên MinIO theo path `jobs/{jobId}/{analyzerRunId}/`.
  4. Exit handler: gọi webhook `POST /internal/argo-callback` của Eaap.Api báo trạng thái (hoặc Phase 1 đơn giản hơn: Eaap poll Argo API mỗi 5s — chọn polling nếu webhook phức tạp, ghi ADR).
- Client Argo trong `Eaap.Infrastructure/Argo/ArgoClient.cs`: submit workflow, get status. Dùng HttpClient + token, base URL từ config.

---

## PHẦN 9 — QUALITY GATE (OPA)

`policies/quality-gate/default.rego`:

```rego
package eaap.gate

default pass := false

pass if {
    count(errors) == 0
}

errors contains msg if {
    input.summary.errorCount > 0
    msg := sprintf("errorCount=%d, expected 0", [input.summary.errorCount])
}

errors contains msg if {
    input.summary.warningCount > input.thresholds.maxWarnings
    msg := sprintf("warningCount=%d > max %d",
        [input.summary.warningCount, input.thresholds.maxWarnings])
}
```

Input EAAP gửi cho OPA (`POST /v1/data/eaap/gate`):

```json
{
  "input": {
    "summary": { "errorCount": 0, "warningCount": 12, "byRule": {...} },
    "thresholds": { "maxWarnings": 100 }
  }
}
```

Thresholds Phase 1 lấy từ config; Phase sau mới per-repository.

---

## PHẦN 10 — MILESTONES & ACCEPTANCE CRITERIA

### M1 — Skeleton & hạ tầng local
- Tạo solution, projects, docker-compose (postgres, rabbitmq, minio, opa).
- EF Core migrations cho toàn bộ schema Phần 3.
- ✅ AC: `docker compose up` + `dotnet run` → Swagger hiện đủ endpoint (trả 501 tạm được), `dotnet ef database update` thành công, healthcheck `/health` xanh cho cả 4 dependency.

### M2 — Repository & Snapshot Service
- `POST /repositories`, clone bằng `git` CLI (LibGit2Sharp nếu gặp vấn đề, ghi ADR), tạo tarball, upload MinIO, tạo Snapshot record.
- ✅ AC: integration test clone một repo công khai nhỏ (dùng repo fixture trong tests), snapshot xuất hiện trong MinIO và DB; gọi lần 2 cùng commit → tái sử dụng snapshot (không clone lại).

### M3 — Eaap.Sarif
- Model wrapper trên Sarif.Sdk, validator, fingerprint theo Phần 6.
- ✅ AC: unit test với 3 file SARIF fixture (1 hợp lệ từ MegaLinter thật, 1 sai version, 1 thiếu tool.driver.name); fingerprint deterministic (test chạy 2 lần ra cùng hash).

### M4 — Ingestion pipeline
- Consumer `AnalyzerRunFinished`: tải SARIF từ MinIO → validate → map thành Warnings (kèm dedup trong job) → lưu DB.
- `GET /jobs/{id}/warnings` và `GET /jobs/{id}/sarif` hoạt động.
- ✅ AC: integration test bơm file SARIF fixture vào MinIO, publish event giả → warnings query được qua API, count đúng với fixture.

### M5 — Argo orchestration + MegaLinter adapter
- Build image adapter MegaLinter, script k3d cài Argo, WorkflowTemplate, ArgoClient, JobRequestedConsumer, polling status.
- ✅ AC: chạy end-to-end trên máy local: `POST /repositories` (repo mẫu chứa vài file JS/Python có lỗi lint) → `POST /scans` → job chuyển Pending→Running→Succeeded, warnings của MegaLinter xuất hiện trong `GET /jobs/{id}/warnings`.

### M6 — Quality Gate + tổng kết
- OPA client, gọi gate khi job xong, lưu GateEvaluation, status GateFailed khi không pass.
- README end-to-end: từ `git clone` repo eaap đến chạy scan đầu tiên trong ≤ 10 lệnh.
- ✅ AC: (a) scan repo sạch → Passed=true; (b) hạ `maxWarnings=0` trong config, scan repo có lỗi lint → job status = GateFailed, violations ghi rõ lý do; (c) một người mới làm theo README chạy được toàn bộ flow.

---

## PHẦN 11 — NGOÀI PHẠM VI PHASE 1 (KHÔNG LÀM)

- UI/frontend, authentication/RBAC (API mở, chỉ chạy local)
- Dedup cross-job, trend, technical debt, Grafana
- Adapter khác ngoài MegaLinter (Trivy, Semgrep... là Phase 3)
- Multi-tenant, webhook từ Git provider, scheduled scans
- Runtime/Prometheus adapter

Nếu trong lúc build thấy "tiện tay làm luôn" các mục trên — **không làm**, ghi vào `docs/backlog.md`.

---

## PHẦN 12 — CHECKLIST NGHIỆM THU CUỐI CÙNG

```
[ ] docker compose up + k3d script chạy sạch trên máy mới
[ ] 6 milestone đều pass acceptance test tự động (trừ M6c là manual)
[ ] dotnet test: 100% pass, có ≥ 1 integration test cho mỗi service chính
[ ] OpenAPI spec export tại docs/api/openapi.json
[ ] Mọi quyết định ngoài spec có ADR trong docs/decisions/
[ ] README hướng dẫn end-to-end ≤ 10 lệnh
[ ] Không secret nào nằm trong source
```
