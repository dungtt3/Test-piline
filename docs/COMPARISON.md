# EAAP v1 vs SonarQube vs DefectDojo — 7 chiều phân tích

So sánh theo 7 chiều mà EAAP bao phủ. Số liệu EAAP lấy từ demo tổng Phase 4 (repo `vulnerable-app` + 1 repo .NET có test) chạy trên stack local.

| # | Chiều | EAAP v1 | SonarQube (CE) | DefectDojo |
| - | ----- | ------- | -------------- | ---------- |
| 1 | Lint / static quality | MegaLinter → SARIF, chuẩn hoá về `Warning` | Có (bộ rule riêng) | Không (chỉ import) |
| 2 | Test results | Adapter `test-report` (TRX/JUnit → `test.failed` + metric) | Import qua plugin | Không |
| 3 | Coverage | Adapter `coverage` (Cobertura/lcov → `coverage.*`, merge theo tổng dòng) | Có (import) | Không |
| 4 | Security (SCA + SAST + secret) | Trivy + Semgrep + Gitleaks, `SecuritySeverity`/CWE/CVE | SAST cơ bản; SCA ở bản trả phí | **Trọng tâm**: aggregate nhiều scanner |
| 5 | Baseline / new-vs-resolved | Cross-job baseline theo fingerprint (`IsNew`/`Resolved`) | Có (new code) | Dedup finding |
| 6 | Runtime SLO | Adapter `prometheus-slo` (query Prometheus → `slo.*`) | **Không** | Không |
| 7 | Quality gate + technical debt | **Một OPA gate xuyên suốt** (source→runtime) + debt phút/giờ, per-repo binding | Gate trên new code (không runtime) | Không có gate/CI-blocking gốc |

## Khác biệt chữ ký của EAAP

- **Integrate-first:** orchestrate tool OSS có sẵn, chuẩn hoá **mọi** output về SARIF 2.1.0 + `metrics.json`. Thêm tool có SARIF ≈ một entrypoint shell < 50 dòng (Phase 3 chứng minh: 3 adapter security 27–34 dòng).
- **Một Quality Gate duy nhất** đánh giá 7 chiều trong **một** `GateEvaluation` (Phase 4 M4): errorCount, warning mới, coverage, test fail, security critical/high, SLO runtime, tăng debt — violations phân nhóm theo nguồn. SonarQube gate không chạm runtime; DefectDojo không có gate CI gốc.
- **Xuyên suốt source → runtime:** SLO runtime (Prometheus) cùng một hàng đợi phân tích và cùng một gate với lint/security — điều cả SonarQube lẫn DefectDojo đều không làm.
- **Enterprise tối giản:** auth JWT + API token CI, RBAC 3 role, notification (webhook HMAC/Slack/email), webhook auto-scan GitHub/GitLab.

## Hạn chế đã biết của EAAP v1 (so với đối thủ trưởng thành)

- Chưa có UI (dùng Swagger + Grafana); SonarQube/DefectDojo có UI triage đầy đủ.
- RBAC global 3 role, chưa per-project; chưa SSO/OIDC, chưa multi-tenant (backlog).
- Một analyzer/job ở v1 (ADR-005) — gate xuyên suốt đã chứng minh trên job đa-run seed sẵn; chạy 7 analyzer trong một workflow thật là hạng mục kế tiếp.
- DB tool security (Trivy/Semgrep rule) đóng băng theo ngày build image (ADR-011/012).

> Kết luận: EAAP v1 không thay thế 1-1 SonarQube hay DefectDojo, mà **hợp nhất** cả hai lớp (quality + security) và mở rộng sang **runtime** dưới một quality gate duy nhất — đó là giá trị khác biệt.
