# Hướng dẫn sử dụng EAAP (Phase 1)

EAAP là platform phân tích mã nguồn tự động: bạn đăng ký một repository Git, yêu cầu scan, platform sẽ clone code → tạo snapshot → chạy analyzer trong Argo Workflows → thu kết quả SARIF về thành Warnings → chấm quality gate bằng OPA. Toàn bộ thao tác qua REST API (chưa có UI ở Phase 1).

---

## 1. Yêu cầu môi trường

| Công cụ | Bắt buộc | Ghi chú |
| --- | --- | --- |
| Docker Desktop | ✔ | Chạy postgres, rabbitmq, minio, opa |
| .NET SDK ≥ 8 | ✔ | Build/chạy API |
| git | ✔ | Platform dùng git CLI để clone |
| k3d + kubectl | Chỉ khi chạy scan thật | Argo Workflows chạy trên k3d |

> Máy này: k3d cài qua winget nhưng chưa có trong PATH của shell mới. Thêm tạm:
> ```powershell
> $env:Path += ";$env:LOCALAPPDATA\Microsoft\WinGet\Packages\k3d.k3d_Microsoft.Winget.Source_8wekyb3d8bbwe"
> ```

## 2. Khởi động lần đầu (một lần duy nhất)

```powershell
# Hạ tầng local: postgres, rabbitmq, minio (tự tạo bucket "eaap"), opa (tự load policy)
docker compose up -d --wait

# Local tools (dotnet-ef, swagger cli)
dotnet tool restore

# Tạo schema database
$env:ConnectionStrings__Postgres = "Host=localhost;Port=5432;Database=eaap;Username=eaap;Password=eaap-dev"
dotnet ef database update -p src/Eaap.Infrastructure -s src/Eaap.Api

# Tạo k3d cluster + cài Argo + apply WorkflowTemplate
.\deploy\k3d\setup.ps1

# Build adapter và import vào cluster
# - MegaLinter (cần ~25GB đĩa trống):
.\deploy\k3d\build-adapter.ps1
# - HOẶC adapter echo siêu nhẹ để thử pipeline (máy ít đĩa):
docker build -t eaap/adapter-echo:latest adapters/echo
k3d image import eaap/adapter-echo:latest -c eaap
```

## 3. Chạy hằng ngày

Mở 2 terminal:

```powershell
# Terminal 1 — expose Argo API (giữ chạy)
kubectl -n argo port-forward svc/argo-server 2746:2746

# Terminal 2 — chạy API
$env:ASPNETCORE_ENVIRONMENT = "Development"
# Nếu dùng adapter echo, đăng ký nó trước khi chạy:
#   $env:Adapters__echo__Image = "eaap/adapter-echo:latest"
dotnet run --project src/Eaap.Api --urls http://localhost:5080
```

Kiểm tra:
- Swagger UI: <http://localhost:5080/swagger>
- Health: <http://localhost:5080/health> — phải `Healthy` cả postgres/rabbitmq/minio/opa.

## 4. Quy trình scan một repository

### Bước 1 — Đăng ký repository

```powershell
$repo = Invoke-RestMethod -Method Post http://localhost:5080/api/v1/repositories `
  -ContentType application/json `
  -Body '{"provider":"GitHub","cloneUrl":"https://github.com/<org>/<repo>.git","defaultBranch":"main"}'
$repo.id
```

`provider`: `GitHub | GitLab | Bitbucket | AzureDevOps | GenericGit` (đường dẫn local dùng `GenericGit`).

### Bước 2 — Yêu cầu scan

```powershell
$scan = Invoke-RestMethod -Method Post "http://localhost:5080/api/v1/repositories/$($repo.id)/scans" `
  -ContentType application/json `
  -Body '{"analyzers":["megalinter"]}'          # hoặc ["echo"]
$scan.jobId
```

Body tùy chọn: `branch` (mặc định defaultBranch), `commitSha` (scan đúng commit; cùng commit sẽ tái sử dụng snapshot, không clone lại). Trả về `202 { jobId }`.

### Bước 3 — Theo dõi job

```powershell
Invoke-RestMethod "http://localhost:5080/api/v1/jobs/$($scan.jobId)"
```

Trạng thái: `Pending → Running → Succeeded` | `GateFailed` (vi phạm quality gate) | `Failed` (adapter/hạ tầng lỗi). Response gồm danh sách `analyzerRuns` (mỗi analyzer: status, warningCount, đường dẫn SARIF trên MinIO) và `gateEvaluation` (passed, violations).

### Bước 4 — Xem kết quả

```powershell
# Warnings (phân trang + lọc)
Invoke-RestMethod "http://localhost:5080/api/v1/jobs/$($scan.jobId)/warnings"
Invoke-RestMethod "http://localhost:5080/api/v1/jobs/$($scan.jobId)/warnings?level=Error&page=1&pageSize=50"
Invoke-RestMethod "http://localhost:5080/api/v1/jobs/$($scan.jobId)/warnings?ruleId=no-unused-vars"

# SARIF tổng hợp (merge mọi analyzer run) — application/sarif+json
Invoke-RestMethod "http://localhost:5080/api/v1/jobs/$($scan.jobId)/sarif" -OutFile ket-qua.sarif
```

Mỗi warning có: `ruleId`, `level` (None/Note/Warning/Error), `message`, `filePath`, `startLine/endLine`, `fingerprint` (dedup — 2 warning trùng trong cùng job chỉ giữ 1, đếm số lần trong `SarifRaw.properties.duplicateCount`).

## 5. Quality gate

Policy Rego: `policies/quality-gate/default.rego` — job fail gate khi có ≥1 warning mức Error, hoặc tổng warning vượt `maxWarnings`.

- Ngưỡng cấu hình: `Opa:MaxWarnings` trong `appsettings.json` (mặc định 100), hoặc env `Opa__MaxWarnings` khi chạy API. Ví dụ chế độ nghiêm ngặt:
  ```powershell
  $env:Opa__MaxWarnings = "0"   # mọi warning → GateFailed
  ```
- Sửa policy: chỉnh file `.rego` rồi `docker compose restart opa`.
- Kết quả gate nằm trong `GET /jobs/{id}` → `gateEvaluation.violations` ghi rõ lý do (vd `"warningCount=12 > max 0"`).

## 6. Thêm / thay adapter

Adapter là container theo contract (spec Phần 5): mount `/workspace` (ro), ghi `*.sarif` vào `/results`, report gốc vào `/artifacts`; nhận env `EAAP_JOB_ID`, `EAAP_ANALYZER_RUN_ID`, `EAAP_SNAPSHOT_COMMIT`; entrypoint chuẩn `/eaap-entrypoint.sh`; exit 0 kể cả khi có warning.

Đăng ký adapter với platform qua config (Phase 1 dùng registry tĩnh — ADR-005):

```jsonc
// appsettings.json
"Adapters": {
  "megalinter": { "Image": "eaap/adapter-megalinter:latest", "TimeoutSeconds": 1800 }
}
// hoặc env var:  Adapters__<id>__Image=<image>
```

Nhớ `k3d image import <image> -c eaap` sau khi build. Phase 1 chạy **một analyzer mỗi job** (analyzer thừa trong request sẽ bị đánh Failed).

## 7. Lệnh tiện ích

```powershell
dotnet test                                        # 18 unit + 5 integration (cần Docker)
dotnet build src/Eaap.Api /p:ExportOpenApi=true    # export docs/api/openapi.json
kubectl -n argo get workflows                      # xem workflow Argo
kubectl -n argo logs <pod> -c main                 # log từng bước workflow
docker compose down                                # tắt hạ tầng (thêm -v nếu muốn xoá data)
k3d cluster stop eaap / k3d cluster start eaap     # tắt/bật cluster
```

## 8. Sự cố thường gặp

| Triệu chứng | Nguyên nhân / Cách xử lý |
| --- | --- |
| `/health` đỏ | `docker compose up -d --wait` chưa chạy hoặc container chết — `docker compose ps` |
| Job kẹt `Pending` | API không publish/consume được — kiểm tra RabbitMQ (localhost:15672, eaap/eaap-dev) |
| Job `Failed` ngay sau submit | Argo API không tới được — port-forward 2746 đã chạy chưa? Adapter id có trong config `Adapters` chưa? |
| Workflow `ImagePullBackOff` | Quên `k3d image import` image adapter vào cluster |
| Job `Failed`, workflow xanh | SARIF không hợp lệ hoặc thiếu file `/results/<analyzerId>.sarif` — xem log pod `run-analyzer` |
| Xoá workflow bằng tay | Polling sẽ tự đóng job đó là `Failed` (404 → fail job) |
| Integration test chập chờn | Máy đang nặng I/O (pull image lớn) — chạy lại khi máy rảnh |

## 9. Tra cứu thêm

- Spec gốc: `EAAP-AI-Build-Spec-Phase1.md`
- Quyết định kiến trúc: `docs/decisions/ADR-001..006`
- Việc để Phase sau: `docs/backlog.md`
- OpenAPI: `docs/api/openapi.json` (hoặc Swagger UI khi API đang chạy)
