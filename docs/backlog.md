# Backlog — ngoài phạm vi Phase 1

Các mục phát hiện trong lúc build nhưng không làm theo Phần 11 của spec:

- Chạy nhiều analyzer song song trong một job (hiện tại: một workflow/analyzer đầu tiên mỗi job — ADR-005).
- Đọc `adapters/*/manifest.yaml` động thay vì registry tĩnh trong config (ADR-005).
- Endpoint `POST /internal/results/{analyzerRunId}` cho adapter callback (Phase 1 dùng polling MinIO/Argo — ADR-004).
- Dedup warning cross-job theo fingerprint (Phase 2) — fingerprint đã được lưu sẵn.
- UI/frontend, authentication/RBAC.
- Adapter Trivy, Semgrep (Phase 3); runtime/Prometheus adapter.
- Multi-tenant, webhook từ Git provider, scheduled scans, trend/technical debt/Grafana.
- Áp dụng `timeoutSeconds` và `resources` từ manifest vào WorkflowTemplate (hiện template dùng mặc định).

## Phát hiện trong Phase 2

- Xoay vòng mật khẩu role `grafana_ro` (hiện là giá trị dev cố định trong migration — ADR-010) trước khi mở Grafana ra ngoài môi trường lab.
- Nếu tách adapter .NET ra repo riêng: publish `Eaap.Sarif` thành NuGet nội bộ, vì hiện Dockerfile adapter build với context repo root để copy source dùng chung (ADR-007).
- Đăng ký adapter `echo` vào `Adapters` registry trong appsettings (hiện chỉ truyền qua env khi chạy e2e).
