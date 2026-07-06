# ADR-005: Phase 1 chạy một analyzer mỗi job, adapter registry tĩnh trong config

- **Bối cảnh:** Schema chỉ có `AnalysisJob.ArgoWorkflowName` (một workflow mỗi job); Phase 1 chỉ có adapter MegaLinter. Spec Phần 8 nói "song song nếu nhiều adapter" nhưng chưa cần cho Phase 1.
- **Quyết định:** `JobRequestedConsumer` submit một workflow cho analyzer đầu tiên của job; các analyzer thừa bị đánh dấu Failed kèm log. Adapter registry Phase 1 là section `Adapters` trong config (mirror `manifest.yaml`), chưa đọc manifest động.
- **Hệ quả:** Đơn giản, đủ cho AC M5/M6. Multi-analyzer song song + đọc manifest động ghi vào `docs/backlog.md` cho Phase sau.
