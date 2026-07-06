# ADR-004: Poll trạng thái Argo thay vì webhook callback

- **Bối cảnh:** Spec Phần 8 cho phép chọn webhook (`POST /internal/argo-callback`) hoặc polling Argo API mỗi 5s, khuyến nghị polling nếu webhook phức tạp.
- **Quyết định:** Dùng `ArgoPollingService` (BackgroundService) poll mỗi 5s (config `Argo:PollIntervalSeconds`) các job Running có `ArgoWorkflowName`; khi workflow kết thúc → publish `AnalyzerRunFinished`. Endpoint `/internal/results/{analyzerRunId}` (optional) không triển khai.
- **Hệ quả:** Không cần expose API cho cluster gọi ngược (đơn giản hoá network local); độ trễ tối đa ~5s. Consumer `AnalyzerRunFinished` có guard idempotent để chịu được event trùng.
