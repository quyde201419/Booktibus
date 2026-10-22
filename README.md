 HEAD
# Booktibus — Hệ Thống Quản Lý Thư Viện Hiện Đại (Library Management System)

> **Ứng dụng Desktop C# WPF (.NET 10) | Kiến trúc MVVM | Cơ sở dữ liệu SQL Server**  
> Tuân thủ phong cách giao diện **Premium Library** (Dark Navy & Warm Amber), hỗ trợ song ngữ Tiếng Việt & Tiếng Anh.

---

## 1. Giới thiệu tổng quan

**Booktibus** là giải pháp phần mềm quản lý thư viện toàn diện dành cho máy tính để bàn (Windows Desktop), được xây dựng trên nền tảng .NET 10 mới nhất. Hệ thống số hóa toàn bộ quy trình vận hành thư viện từ khâu tra cứu mục lục, mượn/trả sách, quản lý hồ sơ độc giả, tính toán tiền phạt quá hạn minh bạch cho đến báo cáo thống kê chuyên sâu và phân quyền quản trị chặt chẽ.

### Các phân hệ chính:
- **Xác thực & Bảo mật (Authentication & Security):**
  - Đăng nhập, đăng ký tài khoản độc giả tự phục vụ.
  - Hỗ trợ **nhiều tài khoản độc lập đăng ký bằng cùng một địa chỉ email** (chia sẻ email gia đình/tổ chức, nhưng giữ nguyên mã thẻ độc giả, tên đăng nhập, mật khẩu và lịch sử mượn sách độc lập).
  - Khôi phục mật khẩu qua mã xác thực OTP với cơ chế giới hạn thời gian (Sandbox Mock cho môi trường phát triển).
  - Phân quyền nghiêm ngặt 3 cấp độ: **Độc giả (Reader)**, **Thủ thư (Librarian)** và **Quản trị viên (Administrator)**.
- **Mục lục sách (Library Catalog):**
  - Tìm kiếm, lọc theo thể loại, tác giả, trạng thái khả dụng.
  - Đánh dấu sách yêu thích (Favorites) và xem chi tiết thông tin đầu sách.
- **Không gian Độc giả (Reader Space):**
  - Theo dõi danh sách sách đang mượn, lịch sử mượn trả, hạn trả sách.
  - Xem các khoản tiền phạt quá hạn và thông tin thẻ thư viện cá nhân.
- **Nghiệp vụ Thủ thư (Librarian Workspace):**
  - Quản lý kho sách: Thêm mới, chỉnh sửa thông tin sách, cập nhật số lượng tồn kho.
  - Quản lý mượn/trả sách trực tiếp tại quầy: Mượn sách, xác nhận trả sách, kiểm soát tự động tình trạng sách quá hạn.
  - Quản lý hồ sơ độc giả: Xem danh sách độc giả, cấp thẻ thư viện, khóa/mở khóa tài khoản.
- **Quản lý Tiền phạt & Kiểm toán (Fines Management & Audit):**
  - Tự động phát hiện sách trả trễ và tính tiền phạt chính xác theo số ngày quá hạn (mặc định 5.000 VNĐ/ngày).
  - Hiển thị tiền phạt tạm tính đối với các phiếu mượn chưa trả nhưng đã quá hạn.
  - Nghiệp vụ thu tiền phạt, điều chỉnh hoặc miễn phạt (có bắt buộc nhập lý do và ghi nhận nhật ký kiểm toán `dbo.FineAuditLogs`).
  - Quản trị viên tùy chỉnh mức phạt hàng ngày toàn hệ thống.
- **Báo cáo & Thống kê (Reports & Analytics):**
  - Thống kê tổng số đầu sách, sách đang mượn, sách quá hạn, độc giả đang hoạt động.
  - Biểu đồ và danh sách top sách mượn nhiều nhất, tỷ lệ hoàn trả đúng hạn.
- **Quản trị hệ thống (System Administration):**
  - Tạo và phân quyền tài khoản Thủ thư/Quản trị viên.
  - Đặt lại mật khẩu an toàn cho người dùng mà không cần biết mật khẩu cũ.

---

## 2. Kiến trúc & Công nghệ

- **Nền tảng runtime:** .NET 10 (`net10.0-windows`).
- **Giao diện người dùng:** Windows Presentation Foundation (WPF), XAML.
- **Mô hình kiến trúc:** Model - View - ViewModel (MVVM) thuần chuẩn:
  - `ObservableObject` hỗ trợ `INotifyPropertyChanged`.
  - `RelayCommand` xử lý Command binding.
  - `PasswordBoxAssistant` hỗ trợ binding hai chiều an toàn cho `PasswordBox` mà không vi phạm nguyên tắc MVVM và không để lộ mật khẩu trong code-behind.
- **Cơ sở dữ liệu:** Microsoft SQL Server 2019 / 2022 / Express / LocalDB.
- **Phương thức xác thực DB:** Windows Authentication (`Integrated Security=True`), tự động kiểm tra tính sẵn sàng của kết nối và cấu trúc dữ liệu (`DatabaseConfig.ProbeDatabase`).
- **Bảo mật:**
  - Quy định mật khẩu an toàn cao: Bắt buộc tối thiểu 8 ký tự, có ít nhất 1 chữ in hoa (A-Z), 1 chữ in thường (a-z), 1 chữ số (0-9) và 1 ký tự đặc biệt (!@#$%^&*...).
  - Mật khẩu được băm (hash) bằng giải thuật PBKDF2 với muối ngẫu nhiên (salt) chuẩn công nghiệp (`Rfc2898DeriveBytes`).
  - Chặn hoàn toàn việc sử dụng tài khoản siêu người dùng `sa` hoặc nhúng mật khẩu dạng văn bản thô trong mã nguồn.

---

## 3. Cấu trúc thư mục dự án

```
Booktibus/
│   Booktibus.slnx                   # File Visual Studio Solution XML
│   .gitignore                       # Cấu hình bỏ qua build, secrets, file .bak, .vscode, TestResults
│   README.md                        # Tài liệu hướng dẫn dự án (tệp này)
│
├───Booktibus/                       # Thư mục mã nguồn chính của ứng dụng
│   │   App.xaml / App.xaml.cs       # Điểm khởi động ứng dụng & nạp tài nguyên toàn cục
│   │   MainWindow.xaml / .cs        # Cửa sổ chính điều hướng nội dung theo phiên đăng nhập
│   │   Booktibus.csproj             # File cấu hình dự án .NET 10 WPF
│   │
│   ├───Converters/                  # Bộ chuyển đổi giá trị binding cho XAML
│   │       BooleanToVisibilityConverter.cs
│   │       RoleToVisibilityConverter.cs
│   │       StatusToBrushConverter.cs
│   │       NullToVisibilityConverter.cs
│   │       ...
│   │
│   ├───Data/                        # Truy cập CSDL & các script di chuyển schema
│   │       DatabaseConfig.cs        # Kết nối SQL Server, phát hiện bảng & kiểm tra an toàn
│   │       BooktibusAuthSchema.sql          # [Script 1] Khởi tạo DB, Users, Roles, Otp
│   │       BooktibusStage5ReaderSchema.sql  # [Script 2] Readers, Favorites, BorrowRecords
│   │       BooktibusStage8FinesSchema.sql   # [Script 3] FineSettings, Fines, FineAuditLogs
│   │
│   ├───Helpers/                     # Lớp tiện ích bổ trợ
│   │       AppDiagnosticLogger.cs   # Ghi nhật ký chẩn đoán lỗi an toàn
│   │       PasswordBoxAssistant.cs  # Attached property binding an toàn cho PasswordBox
│   │       RelayCommand.cs          # ICommand implementation cho MVVM
│   │
│   ├───Models/                      # Định nghĩa thực thể dữ liệu (POCO)
│   │       AuthModels.cs            # UserSession, Role, RegistrationRequest, ProbeResult
│   │       LibraryModels.cs         # Book, BorrowRecord, ReaderProfile, DashboardStats
│   │       FineModels.cs            # FineItem, FineSetting, FineAuditLog
│   │
│   ├───Resources/                   # Phong cách giao diện & từ điển song ngữ
│   │       Colors.xaml              # Bảng màu chủ đạo Premium Library (Navy, Amber, Gold)
│   │       Styles.xaml              # Định kiểu Control: Button, Card, TextBox, DataGrid
│   │       Typography.xaml          # Phông chữ và tỷ lệ phân cấp nội dung
│   │       Strings.vi.xaml          # Bản dịch Tiếng Việt toàn hệ thống
│   │       Strings.en.xaml          # Bản dịch Tiếng Anh toàn hệ thống
│   │
│   ├───Services/                    # Tầng nghiệp vụ và dịch vụ hệ thống
│   │       DatabaseConfig.cs        # Trình quản lý kết nối và dò tìm schema
│   │       SqlAuthenticationService.cs # Xác thực, đăng ký độc giả chung email, băm mật khẩu
│   │       LibraryManagementService.cs # Nghiệp vụ mục lục, mượn trả sách, hồ sơ độc giả
│   │       FineManagementService.cs    # Tính phạt, thu phạt, điều chỉnh, miễn và kiểm toán
│   │       PasswordResetService.cs     # Quy trình khôi phục mật khẩu bằng OTP
│   │       SessionService.cs           # Lưu trữ phiên làm việc người dùng hiện tại
│   │       LocalizationService.cs      # Chuyển đổi ngôn ngữ động lúc ứng dụng đang chạy
│   │       EmailMockService.cs         # Sandbox cấp mã OTP cho môi trường phát triển
│   │
│   ├───ViewModels/                  # Quản lý trạng thái và logic tương tác giao diện
│   │       MainViewModel.cs         # Điều hướng trang theo phiên làm việc người dùng
│   │       LoginViewModel.cs        # Xử lý đăng nhập, đăng ký, quên mật khẩu, dò tìm DB
│   │       CatalogViewModel.cs      # Tìm kiếm, lọc và xem chi tiết sách
│   │       ReaderBorrowViewModel.cs # Quản lý sách đang mượn & lịch sử của Độc giả
│   │       LibrarianWorkspaceViewModel.cs # Nghiệp vụ mượn/trả sách của Thủ thư
│   │       ReaderManagementViewModel.cs   # Quản lý độc giả và thẻ thư viện
│   │       ReportsViewModel.cs      # Thống kê và báo cáo hoạt động thư viện
│   │       AdminAccountManagementViewModel.cs # Quản lý tài khoản và phân quyền
│   │       FinesViewModel.cs        # Quản lý và theo dõi tiền phạt cho cả 3 vai trò
│   │
│   └───Views/                       # UserControl giao diện hiển thị cho từng chức năng
│           LoginView.xaml
│           CatalogView.xaml
│           ReaderBorrowView.xaml
│           LibrarianWorkspaceView.xaml
│           ReaderManagementView.xaml
│           ReportsView.xaml
│           AdminAccountManagementView.xaml
│           FinesView.xaml
│
└───Booktibus.VerificationTests/     # Dự án kiểm thử tự động toàn diện (14 kịch bản)
        Booktibus.VerificationTests.csproj
        Program.cs
```

---

## 4. Yêu cầu môi trường phát triển

Để mở, biên dịch và chạy dự án Booktibus, môi trường máy tính của bạn cần đáp ứng:

1. **Hệ điều hành:** Windows 10 (bản 1903 trở lên) hoặc Windows 11.
2. **Visual Studio:** Visual Studio 2022 (phiên bản 17.12 trở lên) hoặc Visual Studio 2025.
   - Khi cài đặt Visual Studio, tích chọn khối chức năng: **.NET Desktop Development** (Phát triển ứng dụng trên máy tính với .NET).
3. **.NET SDK:** .NET 10 SDK (`10.0.x`) trở lên. Kiểm tra bằng lệnh:
   ```powershell
   dotnet --version
   ```
4. **Hệ quản trị cơ sở dữ liệu:** Microsoft SQL Server (bản Developer, Express, Standard hoặc LocalDB) đã kích hoạt Windows Authentication.
5. **Công cụ quản lý SQL:** SQL Server Management Studio (SSMS) hoặc Azure Data Studio.

---

## 5. Hướng dẫn thiết lập Cơ sở dữ liệu (Database Setup)

Trước khi khởi chạy ứng dụng lần đầu, bạn cần khởi tạo cơ sở dữ liệu `BooktibusDB` trên máy chủ SQL Server cục bộ.

### Thứ tự thực thi các script SQL (Bắt buộc theo thứ tự):

Mở **SQL Server Management Studio (SSMS)**, kết nối vào SQL Server cục bộ của bạn bằng **Windows Authentication**, mở và thực thi lần lượt 3 tệp sau:

1. **Bước 1: Chạy `Data/BooktibusAuthSchema.sql`**
   - Tự động tạo database `BooktibusDB` nếu chưa có.
   - Khởi tạo bảng vai trò `dbo.Roles`, bảng người dùng `dbo.Users` (hỗ trợ nhiều tài khoản dùng chung email, Username duy nhất), bảng độc giả `dbo.Readers`, bảng OTP `dbo.PasswordResetOtps`.
   - Nạp sẵn 6 đầu sách mẫu ban đầu vào bảng `dbo.Books`.

2. **Bước 2: Chạy `Data/BooktibusStage5ReaderSchema.sql`**
   - Hoàn thiện mở rộng bảng `dbo.Readers`.
   - Khởi tạo bảng sách yêu thích `dbo.ReaderFavorites`.
   - Khởi tạo bảng lịch sử mượn trả `dbo.BorrowRecords`.
   - Tạo trigger tự động đồng bộ số lượng bản sách khả dụng (`AvailableCopies`) khi mượn và trả sách.

3. **Bước 3: Chạy `Data/BooktibusStage8FinesSchema.sql`**
   - Khởi tạo bảng cài đặt mức phạt `dbo.FineSettings` (mức phạt mặc định: 5.000 VNĐ/ngày).
   - Khởi tạo bảng ghi nhận tiền phạt `dbo.Fines` (trạng thái: Unpaid, Paid, Waived).
   - Khởi tạo bảng nhật ký kiểm toán phạt `dbo.FineAuditLogs` (lưu lại mọi thao tác điều chỉnh, miễn giảm, lý do và người thực hiện).

> [!NOTE]  
> Cả 3 script trên đều được viết theo phong cách **idempotent** (an toàn khi chạy lại nhiều lần): Luôn kiểm tra sự tồn tại của đối tượng bằng `OBJECT_ID(...)` trước khi tạo, tuyệt đối không xóa bảng hoặc làm mất dữ liệu hiện có.

### Cấu hình chuỗi kết nối SQL Server

Theo mặc định, Booktibus tự động kết nối vào SQL Server tại máy chủ `localhost` bằng Windows Authentication (`Integrated Security=True`).

Nếu bạn sử dụng tên máy chủ khác (ví dụ: máy tính của bạn dùng SQL Express `.\SQLEXPRESS` hoặc `YOUR-PC\MSSQLSERVER`), bạn có thể cấu hình mà **hoàn toàn không cần sửa mã nguồn**:

- **Cách 1 (Khuyên dùng cho nhà phát triển):**  
  Tạo tệp `appsettings.local.json` trong thư mục chạy của dự án (`Booktibus/bin/Debug/net10.0-windows/appsettings.local.json`):
  ```json
  {
    "SqlServerName": ".\\SQLEXPRESS"
  }
  ```
  *(Tệp `*.local.json` đã được đưa vào `.gitignore`, sẽ không bị commit lên GitHub).*

- **Cách 2 (Sử dụng biến môi trường):**  
  Thiết lập biến môi trường hệ thống Windows:
  ```powershell
  # Đặt tên server:
  [System.Environment]::SetEnvironmentVariable("BOOKTIBUS_SQL_SERVER", ".\SQLEXPRESS", "User")
  
  # Hoặc đặt chuỗi kết nối tùy biến đầy đủ:
  [System.Environment]::SetEnvironmentVariable("BOOKTIBUS_CONNECTION_STRING", "Server=.\\SQLEXPRESS;Database=BooktibusDB;Integrated Security=True;TrustServerCertificate=True;", "User")
  ```

> [!WARNING]  
> **Chính sách bảo mật:** Hệ thống nghiêm cấm và chủ động từ chối kết nối sử dụng tài khoản `sa` hoặc mật khẩu kết nối nhúng trong chuỗi cấu hình. Hãy luôn sử dụng Windows Authentication.

---

## 6. Hướng dẫn khởi chạy ứng dụng (Run Instructions)

### Cách 1: Chạy từ Visual Studio
1. Mở file giải pháp `Booktibus.slnx` bằng Visual Studio 2022/2025.
2. Chọn chế độ cấu hình: `Debug` hoặc `Release`.
3. Nhấn phím **F5** (hoặc `Ctrl + F5`) để bắt đầu khởi chạy.

### Cách 2: Chạy từ giao diện dòng lệnh (Command Line / PowerShell)
```powershell
# Di chuyển vào thư mục gốc của giải pháp
cd D:\VSCOMMUNITY\Booktibus

# Khởi chạy ứng dụng
dotnet run --project Booktibus/Booktibus.csproj
```

### Khởi tạo tài khoản Quản trị viên (Administrator) lần đầu
Khi khởi chạy ứng dụng trên một database mới chưa có tài khoản Quản trị viên, hệ thống sẽ tự động hiển thị màn hình **Khởi tạo Administrator**. Bạn chỉ cần nhập Tên đầy đủ, Tên đăng nhập và Mật khẩu để tạo tài khoản quản trị tối cao của hệ thống.

### Danh sách tài khoản thử nghiệm có sẵn:

| Vai trò | Tên đăng nhập (Username) | Mật khẩu mặc định | Email đăng ký | Mã thẻ độc giả | Ghi chú |
| :--- | :--- | :--- | :--- | :--- | :--- |
| **Độc giả 1** | `quy123` | `Password123!` | `quy@gmail.com` | `LIB-2026-0001` | Tài khoản độc giả mẫu |
| **Độc giả 2** | `quy456` | `Password123!` | `quy@gmail.com` | `LIB-2026-0002` | Minh họa **dùng chung email** với `quy123` |
| **Độc giả 3** | `user123` | `Password123!` | `user123@gmail.com` | `LIB-2026-0003` | Tài khoản độc giả độc lập |
| **Quản trị viên** | `Admin` | *(Thiết lập khi khởi tạo)* | `admin@booktibus.com` | — | Toàn quyền cấu hình & tài khoản |

---

## 7. Bộ kiểm thử tự động (Verification Test Suite)

Dự án đi kèm dự án kiểm thử tự động độc lập `Booktibus.VerificationTests`, xác thực toàn bộ các quy tắc nghiệp vụ cốt lõi mà không ảnh hưởng tới dữ liệu thực tế đang có.

### Cách thực thi bộ kiểm thử:
```powershell
# Chạy trực tiếp từ thư mục gốc giải pháp:
dotnet run --project Booktibus.VerificationTests/Booktibus.VerificationTests.csproj
```

### Cơ chế hoạt động & Đảm bảo an toàn dữ liệu:
- **Tạo dữ liệu thử nghiệm độc lập:** Bộ kiểm thử tự sinh mã hậu tố ngẫu nhiên (`Guid.NewGuid()`), tạo các tài khoản độc giả kiểm thử (`st8_rd1_...`, `st8_rd2_...`), thủ thư kiểm thử (`st8_lib_...`) và các đầu sách kiểm thử (`BK-S8-...`).
- **Tự động dọn dẹp hoàn toàn (100% Cleanup):** Toàn bộ dữ liệu kiểm thử được xóa sạch trong khối `finally`, không để lại dữ liệu rác trong cơ sở dữ liệu.
- **Bảo toàn dữ liệu gốc:** Kiểm chứng và khẳng định 100% tài khoản cốt lõi (`Admin`, `quy123`, `quy456`, `user123`) và 6 đầu sách mẫu ban đầu được giữ nguyên vẹn.
- **Tùy chọn chạy trên CSDL kiểm thử riêng biệt (Khuyên dùng khi muốn cô lập tuyệt đối):**
  Nếu bạn muốn chạy kiểm thử trên một database trắng hoàn toàn độc lập với `BooktibusDB`:
  1. Mở SSMS, tạo database mới: `CREATE DATABASE BooktibusTestDB;`
  2. Chạy lần lượt 3 script schema (`BooktibusAuthSchema.sql`, `BooktibusStage5ReaderSchema.sql`, `BooktibusStage8FinesSchema.sql`) trên `BooktibusTestDB`.
  3. Đặt biến môi trường trước khi chạy lệnh test:
     ```powershell
     $env:BOOKTIBUS_CONNECTION_STRING = "Server=localhost;Database=BooktibusTestDB;Integrated Security=True;TrustServerCertificate=True;"
     dotnet run --project Booktibus.VerificationTests/Booktibus.VerificationTests.csproj
     ```

### Các kịch bản được tự động kiểm chứng (16/16 Passed):
1. **Khởi tạo môi trường kiểm thử:** Tạo dữ liệu kiểm thử độc lập có tiền tố, cô lập hoàn toàn với tài khoản gốc.
2. **Sách trả đúng hạn:** Không phát sinh tiền phạt (`Fines = 0`).
3. **Sách trả sớm hạn:** Không phát sinh tiền phạt.
4. **Sách trả trễ (Overdue Return):** Tự động tính số ngày quá hạn và phát sinh bản ghi phạt chính xác (4 ngày $\times$ 5.000 = 20.000 VNĐ) với trạng thái `Unpaid`.
5. **Sách chưa trả quá hạn:** Tự động tính tiền phạt dự kiến/tạm tính để độc giả và thủ thư cùng theo dõi.
6. **Sách chưa đến hạn trả:** Không phát sinh khoản phạt nào.
7. **Chống trùng lặp tiền phạt:** Truy vấn hoặc mở lại màn hình nhiều lần không tạo bản ghi phạt trùng lặp.
8. **Chống trùng lặp trả sách:** Ghi nhận trả sách nhiều lần trên cùng một phiếu mượn bị từ chối an toàn và giữ nguyên số lượng tồn kho.
9. **Cô lập dữ liệu độc giả:** Độc giả chỉ xem được dữ liệu mượn và tiền phạt của chính mình, bị chặn ở tầng Service nếu cố truy cập dữ liệu người khác.
10. **Phân quyền chức năng tầng Service:** Độc giả không thể thực hiện các thao tác quản lý tiền phạt của Thủ thư hoặc Quản trị viên (Điều chỉnh, Miễn phạt, Cập nhật mức phạt ngày).
11. **Kiểm toán điều chỉnh & miễn phạt:** Thao tác điều chỉnh và miễn tiền phạt bắt buộc nhập lý do và được ghi nhận đầy đủ vào nhật ký `dbo.FineAuditLogs`.
12. **Cấu hình mức phạt:** Quản trị viên cập nhật thành công mức phạt áp dụng toàn hệ thống.
13. **Xử lý lỗi CSDL & Probe 10 bảng:** Ứng dụng xử lý an toàn khi SQL Server offline (không làm crash ứng dụng); phương thức `DatabaseConfig.ProbeDatabase` kiểm tra đầy đủ 10 bảng dữ liệu cốt lõi.
14. **Bảo toàn dữ liệu:** Xác thực 100% tài khoản cốt lõi và sách gốc giữ nguyên.
15. **Quy tắc mật khẩu nâng cao:** Kiểm tra bắt buộc tối thiểu 8 ký tự, có chữ hoa, chữ thường, chữ số và ký tự đặc biệt.
16. **Từ chối mật khẩu yếu tầng Service:** Ngăn chặn đăng ký tài khoản nếu mật khẩu thiếu ký tự đặc biệt hoặc chữ hoa. Xác thực 100% tài khoản cốt lõi (`Admin`, `quy123`, `quy456`, `user123`) và các đầu sách ban đầu được giữ nguyên vẹn.

---

## 8. Giới hạn kỹ thuật hiện tại & Lưu ý

1. **Khôi phục mật khẩu (Sandbox Email Mode):**  
   Để thuận tiện cho việc phát triển và kiểm thử offline cục bộ, dịch vụ `EmailMockService` hoạt động ở chế độ Sandbox: Khi yêu cầu đặt lại mật khẩu, mã OTP (6 chữ số) sẽ được sinh ngẫu nhiên, lưu vào `dbo.PasswordResetOtps` và hiển thị trực tiếp trên giao diện để sao chép thuận tiện. Khi đưa vào môi trường sản xuất thực tế, chỉ cần thay thế phần gửi tin bằng SMTP/SendGrid mà không cần sửa giao diện người dùng.
2. **Thanh toán tiền phạt:**  
   Chức năng quản lý tiền phạt hiện tại hỗ trợ quy trình thu tiền mặt/chuyển khoản đối soát trực tiếp tại quầy thư viện do Thủ thư xác nhận. Ứng dụng chưa tích hợp cổng thanh toán trực tuyến của bên thứ 3 (như VNPay, MoMo, Stripe).
3. **Trí tuệ nhân tạo (AI Assistant):**  
   Hệ thống hiện tại chưa tích hợp OpenAI API và không yêu cầu bất kỳ API Key nào.

---

## 9. Hướng dẫn bàn giao & Đưa dự án lên GitHub

Dự án đã được cấu hình tệp `.gitignore` chuẩn cho các dự án C# .NET / Visual Studio / WPF:
- Đã loại trừ các thư mục nhị phân và file tạm thời: `bin/`, `obj/`, `.vs/`, `logs/`.
- Đã loại trừ các file cấu hình bí mật cá nhân: `appsettings.local.json`, `*.local.json`, `*.user`, `*.suo`.
- Đã loại trừ các tệp sao lưu database: `*.bak`.

### Các bước khởi tạo kho lưu trữ Git và đẩy lên GitHub:

```powershell
# 1. Di chuyển vào thư mục gốc dự án
cd D:\VSCOMMUNITY\Booktibus

# 2. Khởi tạo Git repository (nếu chưa có)
git init

# 3. Kiểm tra trạng thái các file (bảo đảm không có bin, obj, hay file .bak bị theo dõi)
git status

# 4. Thêm toàn bộ mã nguồn hợp lệ vào staging
git add .

# 5. Tạo commit đầu tiên
git commit -m "feat: complete Booktibus LMS Desktop application - Stages 1 through 9 ready"

# 6. Liên kết với kho lưu trữ từ xa trên GitHub (thay URL bằng repo của bạn)
git remote add origin https://github.com/YourUsername/Booktibus.git

# 7. Đổi tên nhánh chính thành main và đẩy mã nguồn lên
git branch -M main
git push -u origin main
```

---

*Dự án được xây dựng và hoàn thiện bởi nhóm phát triển Booktibus — Sẵn sàng cho giai đoạn phát triển mở rộng và cộng tác nhóm.*


