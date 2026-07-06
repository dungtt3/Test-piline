# ADR-002: Build bằng .NET SDK 10, target net8.0

- **Bối cảnh:** Spec yêu cầu .NET 8. Máy dev không có SDK 8 (chỉ có runtime 8.0.28), SDK mới nhất là 10.0.102.
- **Quyết định:** Giữ `TargetFramework=net8.0` cho mọi project, build bằng SDK 10 (SDK mới build target cũ được). Không thêm `global.json` pin SDK 8.
- **Hệ quả:** Code và package đều là .NET 8 đúng spec; máy khác chỉ cần SDK ≥ 8. Solution file dạng `.slnx` (format mặc định của SDK 10).
