# ADR-006: Nghiệm thu e2e Argo bằng adapter echo do giới hạn dung lượng đĩa

- **Bối cảnh:** Image `oxsecurity/megalinter:v8` nặng ~10GB (x2 khi import vào k3d). Máy dev chỉ còn ~13GB trống — pull image full fail liên tục (containerd commit error) và không thể import vào cluster.
- **Quyết định:** (1) Thêm adapter `adapters/echo` (alpine, vài MB) tuân thủ đúng adapter contract Phần 5, dùng để chạy nghiệm thu e2e pipeline Argo → MinIO → ingestion → gate trên máy này. (2) `adapters/megalinter/Dockerfile` thêm `ARG BASE_IMAGE` để build được flavor nhẹ (vd `megalinter-javascript`). Adapter MegaLinter giữ nguyên là adapter chính thức.
- **Hệ quả:** AC M5 phần cơ chế (Pending→Running→Succeeded, warnings qua API) được chứng minh bằng echo adapter; e2e với MegaLinter thật chạy được trên máy có ≥25GB trống theo đúng script sẵn có (`build-adapter.ps1`).
