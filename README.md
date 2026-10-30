# WinNotch — Dynamic Notch cho Windows 10 & 11

**WinNotch** là ứng dụng Dynamic Notch phong cách **Apple Dynamic Island & macOS Sequoia** viết bằng **C# 13 / .NET 9 WPF + Native Win32/WinRT/NAudio/PDH Interop**, hoạt động **100% Offline** (không gọi mạng, không thu thập dữ liệu, không cần quyền Admin).

Xem kiến trúc & quyết định kỹ thuật tại [`SPEC.md`](./SPEC.md).

## Tính năng chính

- **Dynamic Notch bo góc liên tục Apple $G^2$ (`#050507`):** Tiệp hoàn toàn với mép trên màn hình (`Y = 0`), chuyển động lò xo vật lý 60–240Hz (`SpringAnimator` + chuyển Tab lướt lò xo `ActiveTabPill`, `0% CPU` khi đứng yên).
- **Hiệu ứng Face ID 1:1 iOS Dynamic Island (`LAUIPearlGlyphView`):** Tự động bung khung vuông OLED `156 × 148` ngay khi mở khóa Windows (`WTSRegisterSessionNotification`), tái hiện trọn vẹn 4 pha: gương mặt Face ID → 3 vòng quỹ đạo 3D Pearl Rings xoay chiều sâu Z → khóa khớp vòng tròn → tự vẽ dấu tích xanh `#32D74B`.
- **Tab `Điều khiển` (macOS Sequoia Control Center):**
  - **Thẻ Trái — Trình phát nhạc cố định:** Đồng bộ SMTC (Spotify, Apple Music, YouTube/Chrome/Edge, VLC) kèm **Ambient Album Glow**, **5 cột sóng âm đối xứng** theo phổ FFT thời gian thực và khung chờ 1:1 mở nhanh trình phát nhạc mà không nhảy bố cục.
  - **Thẻ Phải — Trung tâm điều khiển nhanh:** Luôn giữ cố định **Âm lượng + Thiết bị đầu ra**, **Độ sáng màn hình (WMI + DDC/CI)**, **Hẹn giờ nhanh 1 hàng (`5p / 15p / 25p`)** và 3 nút tác vụ nhanh (**`Đầu ra` • `Chụp vùng` • `Khóa máy`**).
- **Tab `Hệ thống` (Giám sát chuyên sâu kiểu iStat Menus):** Theo dõi 2 tầng siêu nhẹ (`% CPU`, `% GPU Engine`, `RAM GB`, `Ổ đĩa & Đọc/Ghi MB/s`, `Tiến trình ngốn tài nguyên nhất`, biểu đồ sóng đôi **`↓ Tải xuống / ↑ Tải lên`** 32 giây, chép nhanh `IPv4`, `Uptime` và mở `Task Mgr`), kèm tùy chọn hiện tốc độ mạng trực tiếp trên thanh Notch thu gọn.
- **Tab `Khay giữ file` bền vững (`shelf.json`):** Kéo thả File, Thư mục, Ảnh hoặc Văn bản lên đỉnh màn hình để giữ tạm kèm Shell Thumbnail gốc và kéo ra nhiều ứng dụng (Zalo, Email, Explorer...).
- **Chống cấn Tab trình duyệt & Tương thích Fullscreen:**
  - Giữ phím **`Ctrl`** (`Ghost Mode`) làm mờ Notch còn `20%` và nhấp xuyên `100%` xuống Tab trình duyệt bên dưới; phím tắt **`Alt + N`** mở/thu gọn nhanh.
  - Nhận diện Fullscreen chuẩn Auto-Hide Taskbar & màn hình `125%–200% DPI`, kèm chấm bảo mật Camera (`#30D158`) / Micro (`#FF9F0A`) và thay thế thanh Volume OSD mặc định của Windows 11.

## Chạy thử & Đóng gói Release

```powershell
# Chạy trực tiếp khi phát triển
dotnet run --project src\WinNotch\WinNotch.csproj

# Đóng gói bản chạy ngay (WinNotch.exe), bộ cài 1 chạm (WinNotch-Setup.exe) & bản nén (WinNotch-Portable-win-x64.zip)
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\build-release.ps1
```
