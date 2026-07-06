# ADR-001: Đặt cấu trúc monorepo tại root của repo Test-piline

- **Bối cảnh:** Spec mô tả cấu trúc dưới thư mục `eaap/`, nhưng repo Git hiện tại (`Test-piline`) là repo trống dành riêng cho dự án này.
- **Quyết định:** Đặt toàn bộ cấu trúc (`src/`, `tests/`, `docker-compose.yml`, ...) trực tiếp tại root repo thay vì lồng thêm một cấp `eaap/`.
- **Hệ quả:** Đường dẫn ngắn hơn, lệnh `dotnet`/`docker compose` chạy từ root; mọi đường dẫn trong spec hiểu là tương đối từ root repo.
