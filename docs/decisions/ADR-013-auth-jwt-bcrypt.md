# ADR-013: Auth tối giản bằng JWT + BCrypt, custom bearer handler

- **Bối cảnh:** Spec Phase 4 Phần 5 yêu cầu auth tối giản: JWT HS256, mật khẩu BCrypt, API token `eaap_...` cho CI, RBAC 3 role (Admin/Maintainer/Viewer), **không** dùng ASP.NET Core Identity. Cần thêm NuGet ngoài stack đã chốt.
- **Quyết định:**
  1. Thêm `BCrypt.Net-Next` (hash mật khẩu) và `System.IdentityModel.Tokens.Jwt` (tạo/validate JWT) — hai thư viện chuẩn, MIT, không kéo Identity. Ghi ADR này thay cho phê duyệt.
  2. **Một** custom `AuthenticationHandler` (`EaapAuthHandler`) xử lý cả JWT lẫn API token: header `Authorization: Bearer <x>` — nếu `<x>` bắt đầu `eaap_` thì tra `ApiToken` theo SHA256 hash trong DB (kiểm hạn, cập nhật `LastUsedAt`), ngược lại validate JWT. Tránh phức tạp dual-scheme.
  3. Policy-based authorization: `RequireMaintainer` (Maintainer|Admin) cho write (scan/gate/suppression/repository), `RequireAdmin` cho quản lý user/token; đọc chỉ cần authenticated. Toàn bộ `/api/v1/*` yêu cầu auth; `/auth/login`, `/health`, `/hooks/*` (Phase 4 M7) anonymous.
  4. Seed Admin khi bảng User rỗng lúc khởi động và có `Auth:AdminEmail`/`AdminPassword` (từ env). JWT secret từ `Auth:JwtSecret` (env ở production).
- **Nghiệm thu (tách hai tầng, có chủ đích):** cơ chế token thật (JWT roundtrip, hết hạn → invalid, sai secret, API token hash/prefix, BCrypt) test bằng **unit test `AuthTokenService`**. Ma trận RBAC (200/403 theo role) và `/auth/login` thật test bằng **integration**: một `TestAuthHandler` chỉ dùng ở môi trường Testing bơm role qua header `X-Test-Role` (mặc định Admin để test hành vi cũ không cần đăng nhập), `X-Test-Anonymous` để kiểm 401. `/auth/login` chạy logic thật (BCrypt verify + phát JWT) độc lập với handler.
- **Hệ quả:** Không refresh token (đăng nhập lại — backlog). Per-repo role, OIDC/SSO, multi-tenant vẫn là backlog (Phần 9). Token cơ chế được kiểm ở unit, RBAC ở integration — bao phủ đủ AC Phần 5 mà không phải sửa ~30 test hành vi sẵn có.
