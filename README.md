# WinNotch — Dynamic Notch cho Windows 10 & 11

**WinNotch** là ứng dụng Dynamic Notch phong cách **Apple Dynamic Island & macOS Sequoia** viết bằng **C# 13 / .NET 9 WPF + Native Win32/WinRT/NAudio/PDH Interop**, hoạt động **100% Offline** (không gọi mạng, không thu thập dữ liệu, không cần quyền Admin).

Xem kiến trúc & quyết định kỹ thuật tại [`SPEC.md`](./SPEC.md).

## Tính năng chính

- **Dynamic Notch bo góc liên tục Apple $G^2$ (`#050507`) & Kính mờ Liquid Glass:** Khung vỏ đen tuyền OLED `#050507` tiệp hoàn toàn với mép trên màn hình (`Y = 0`), kết hợp bề mặt kính mờ Liquid Glass bên trong, 2 preset kích thước (`Gọn 684 × 272` / `Rộng 744 × 296`) và vật lý lò xo 60–240Hz (`SpringAnimator`, `0% CPU` khi đứng yên).
- **Tab `Agents` — Trung tâm Giám sát AI Agents 1 cột & Co giãn chiều cao động (`≤ 2/3` màn hình):**
  - Tự động dò thời gian thực (`~0.0% CPU`, đọc đuôi byte-offset tăng dần + Named Pipe `WinNotch_AgentActivity_v1`) cho **Antigravity CLI / Teamwork**, **Codex CLI**, **Claude Code** và Lifecycle Hooks.
  - Bố cục **1 cột toàn chiều rộng** dạng cây phân cấp Cha – Con gồm 4 trường trực quan: **`[Tên Dự Án / Vai trò Subagent] — [Công việc đang làm (100% Tiếng Việt)] — [Tiến độ %] — [Thời gian chạy mm:ss]`**, tự động co giãn chiều cao Notch mượt theo số dòng (tối đa `2/3` chiều cao màn hình), hỗ trợ thu gọn nhánh dự án, lọc `Tất cả | Đang chạy` và nhấp mở thẳng cửa sổ IDE/Terminal.
  - Thanh Notch thu gọn `32px` hiển thị trực tiếp **`[Tên Dự Án] · [Tiến độ %]`** (hoặc vàng hổ phách **`Chờ duyệt!`**) kèm HUD đen tuyền `#050507` khi Agent hoàn tất hoặc cần phê duyệt.
- **Tab `Điều khiển` (macOS Sequoia Control Center & Power Suite):**
  - **Thẻ Trái — Trình phát nhạc Apple Music:** Đồng bộ SMTC kèm **Ambient Album Glow**, hiệu ứng chuyển ảnh bìa `180ms` + thu phóng `Play/Pause`, và **5 cột sóng âm đối xứng** theo phổ FFT thời gian thực.
  - **Thẻ Phải — Trung tâm điều khiển nhanh:** Đổi nhanh **Loa / Tai nghe 1 chạm** (`IPolicyConfig` + HUD kiểu AirPods), thanh trượt **Âm lượng & Độ sáng (WMI + DDC/CI)**, **Hẹn giờ cuộn chuột `±1 phút`**, cùng 4 tác vụ nhanh: **Giữ sáng (`Caffeine`)**, **Khóa Micro (`Alt + M`)**, **Chấm mã màu Pixel 11×11 (`Alt + C`)** và **Chụp vùng màn hình**.
  - **Cử chỉ chuột trên thanh Notch thu gọn `32px`:** Lăn chuột chỉnh Âm lượng, `Shift + Lăn chuột` chỉnh Độ sáng, Chuột giữa `Play/Pause`, `Shift + Chuột giữa` qua bài kế tiếp.
- **Tab `Hệ thống` (Giám sát chuyên sâu kiểu iStat Menus & Scrubbing Tương Tác):** Theo dõi 2 tầng siêu nhẹ (`% CPU`, `% GPU Engine`, thanh `RAM` & `Ổ đĩa` nội suy mượt, `Tiến trình ngốn tài nguyên`, nút **`Dọn RAM`** User-Mode, biểu đồ sóng đôi **`↓ Tải xuống / ↑ Tải lên`** 32 giây) kèm **di chuột tra cứu từng mốc giây (`Hover Scrubbing Readout`)** trên biểu đồ CPU và Mạng.
- **Tab `Khay giữ file` bền vững (`shelf.json` & Quick-Look Preview):** Kéo thả hoặc **`+ Dán Clipboard` (`Ctrl + V`)** File, Thư mục, Ảnh chụp, Văn bản/URL lên đỉnh màn hình kèm hiệu ứng nảy thẻ, thẻ **Quick-Look xem trước ảnh & nội dung văn bản** khi rê chuột, và kéo ra nhiều ứng dụng.
- **Cài đặt macOS Sequoia (`720 × 560`) & Menu Khay Hệ Thống Kính Mờ (`276px`):** Cửa sổ Cài đặt không viền chuẩn Sequoia chia 5 chuyên mục với thẻ chọn trực quan, đi kèm menu chuột phải khay hệ thống WPF kính mờ bo góc $G^2$ đồng bộ đa màn hình/DPI.
- **Hiệu ứng Thông báo Dynamic Island (`424 × 80`) & Face ID 1:1 (`LAUIPearlGlyphView`):** Thẻ HUD `#050507` thuần chất iOS (kèm mẫu màu trực tiếp & thanh đo mini), mở khóa Face ID 3D Pearl Rings, `Ghost Mode` giữ `Ctrl` nhấp xuyên `100%` và tự ẩn khi xem Fullscreen.

## Chạy thử & Đóng gói Release

```powershell
# Chạy trực tiếp khi phát triển
dotnet run --project src\WinNotch\WinNotch.csproj

# Đóng gói bản chạy ngay (WinNotch.exe), bộ cài 1 chạm (WinNotch-Setup.exe) & bản nén (WinNotch-Portable-win-x64.zip)
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\build-release.ps1
```
