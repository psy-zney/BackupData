# Zney Backup & Restore

Zney Backup & Restore là ứng dụng Windows giúp chuẩn bị máy tính trước khi cài lại Windows hoặc chuyển sang máy mới. Ứng dụng tạo một gói `.zney` có cấu trúc rõ ràng, cho phép người dùng chọn từng nhóm dữ liệu trước khi khôi phục và không tự chạy bất kỳ script nào từ gói backup.

## Điểm nổi bật

- Chỉ tạo và chỉ đọc định dạng `.zney`.
- Mỗi nhóm dữ liệu có archive nén riêng, metadata JSON và SHA-256 cho từng tệp.
- Phục hồi an toàn: chặn Zip Slip, kiểm tra SHA-256 trước khi ghi đè và chỉ ghi vào vùng dữ liệu người dùng cho phép.
- Phục hồi phần mềm theo luồng có thể kiểm soát: `Winget`, `Steam` hoặc `Manual`.
- Lưu và áp dụng lại một tập cài đặt Windows an toàn, có allow-list rõ ràng.

## Nhóm dữ liệu được gợi ý

| Nhóm | Nội dung | Mặc định |
| --- | --- | --- |
| `ApplicationSettings` | VS Code, Chrome, Edge, Git, Windows Terminal | Chọn |
| `WindowsSettings` | Explorer, theme sáng/tối, transparency, căn lề taskbar | Chọn |
| `Photos` | Thư mục Pictures | Không chọn |
| `Documents` | Thư mục Documents | Không chọn |
| `Videos` | Thư mục Videos | Không chọn |

Ảnh, tài liệu và video thường rất lớn nên được nhận diện nhưng không tự chọn. Người dùng luôn quyết định trước khi tạo backup.

## Luồng khôi phục phần mềm

- **Winget**: cài qua `winget install` với ID đã lưu.
- **Steam**: cài Steam qua `Valve.Steam`, sau đó dừng để người dùng mở Steam và đăng nhập. Zney không lưu thông tin đăng nhập, không lấy thư viện game và không tự tải game.
- **Manual**: không có ID/nguồn cài đặt tin cậy nên chỉ hiển thị thông tin và bỏ qua. Zney không đoán URL tải hay chạy installer không xác minh.

## Cấu trúc gói `.zney`

`.zney` là ZIP container riêng của Zney, không phải định dạng mà ứng dụng khác được phép nhập trực tiếp.

```text
manifest.json
metadata/
  apps.json
  data-groups.json
settings/
  application-settings.json
  windows-settings.json
archives/
  VS_Code_settings.zip
  Personal_documents.zip
  Personal_photos.zip
```

Mỗi archive trong `archives/` nén độc lập. `manifest.json` giữ danh sách SHA-256 của từng tệp. `windows-settings.json` chỉ chứa các khóa Windows được allow-list và được kiểm hash trước khi áp dụng.

## Sử dụng

1. Mở **Zney Backup & Restore** và chọn một trong hai luồng: **Export** hoặc **Import**.
2. Với **Export**, ứng dụng tự quét phần mềm và các nhóm thư mục, hiển thị checklist; chọn mục cần sao lưu rồi bấm tạo backup. Hộp lưu mặc định mở ở `Documents\Zney Backups` và tạo file `.zney` trên máy.
3. Với **Import**, chọn đúng file `.zney`, xem lại từng ứng dụng/nhóm dữ liệu, rồi bấm phục hồi.
4. Với Steam, đăng nhập trong ứng dụng Steam sau khi Zney hoàn tất bước cài Steam.

Nên đóng Chrome, Edge, VS Code và các ứng dụng đang dùng dữ liệu trước khi backup.

## Cài đặt MSI bằng GitHub Actions

Workflow [Build Zney MSI](.github/workflows/build-msi.yml) chạy trên GitHub-hosted Windows runner, không cần build trên máy phát triển.

1. Push source lên GitHub.
2. Vào **Actions** → **Build Zney MSI** → **Run workflow**, hoặc push tag `v*`.
3. Tải artifact `ZneyBackup-msi` từ workflow thành công. Khi push tag `v*`, workflow cũng tạo GitHub Release và đính kèm MSI.

Workflow restore dependency, chạy test, publish ứng dụng Windows x64 self-contained và đóng gói `ZneyBackup.msi` bằng WiX.

## Chính sách dữ liệu khi gỡ cài đặt

MSI chỉ nhắm đến cache nội bộ `%LOCALAPPDATA%\ZneyBackup`. Nó không xóa file `.zney`, thư mục Pictures/Documents/Videos, profile Steam hay dữ liệu của bất kỳ ứng dụng thứ ba nào.

## Kiểm thử

Bộ test bao phủ backup/restore, phát hiện archive bị sửa và Zip Slip. GitHub Actions chạy test trước khi tạo MSI.

## Giới hạn có chủ đích

- Zney không sao lưu mật khẩu, token đăng nhập hoặc khóa riêng.
- Zney không tự điều khiển Steam sau khi cài đặt.
- File `.zney` không có chữ ký số; chỉ mở file bạn tin cậy và luôn kiểm tra danh sách mục trước khi phục hồi.
