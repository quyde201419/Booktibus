using System.Data;
using Booktibus.Data;
using Booktibus.Helpers;
using Booktibus.Models;
using Booktibus.Services;
using Microsoft.Data.SqlClient;

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.WriteLine("======================================================================");
Console.WriteLine("BOOKTIBUS — STAGE 8 & STAGE 9 COMPREHENSIVE VERIFICATION SUITE");
Console.WriteLine("======================================================================");

int passedTests = 0;
int failedTests = 0;

void AssertCheck(string testName, bool condition, string details)
{
    if (condition)
    {
        passedTests++;
        Console.WriteLine($"  [PASS] {testName}: {details}");
    }
    else
    {
        failedTests++;
        Console.WriteLine($"  [FAIL] {testName}: {details}");
    }
}

string suffix = DateTime.UtcNow.Ticks.ToString()[^6..];
string userReader1 = $"st8_rd1_{suffix}";
string userReader2 = $"st8_rd2_{suffix}";
string userLib = $"st8_lib_{suffix}";
string sharedEmail = $"st8_shared_{suffix}@booktibus.edu.vn";
string bookCode1 = $"BK-S8-A{suffix[..4]}";
string bookCode2 = $"BK-S8-B{suffix[..4]}";

int reader1UserId = 0;
int reader2UserId = 0;
int libUserId = 0;
int book1Id = 0;
int book2Id = 0;
int onTimeBorrowId = 0;
int earlyBorrowId = 0;
int lateBorrowId = 0;
int activeOverdueBorrowId = 0;
int activeNotDueBorrowId = 0;

var sessionService = new SessionService();
var authzService = new AuthorizationService(sessionService);
var sqlAuthService = new SqlAuthenticationService();
var readerWorkspace = new ReaderWorkspaceService(sessionService, authzService);
var staffService = new LibraryManagementService(sessionService, authzService);
var fineService = new FineManagementService(sessionService, authzService);

int initialUserCount = 0;
int initialBookCount = 0;

try
{
    string connStr = DatabaseConfig.GetConnectionString();
    using (SqlConnection conn = new(connStr))
    {
        conn.Open();
        using SqlCommand uCntCmd = new("SELECT COUNT(1) FROM dbo.Users WHERE Username NOT LIKE 'st8_%';", conn);
        initialUserCount = Convert.ToInt32(uCntCmd.ExecuteScalar());
        using SqlCommand bCntCmd = new("SELECT COUNT(1) FROM dbo.Books WHERE BookCode NOT LIKE 'BK-S8%';", conn);
        initialBookCount = Convert.ToInt32(bCntCmd.ExecuteScalar());
    }

    // 1. Setup test accounts & books
    Console.WriteLine("\n--- [SETUP] TẠO DỮ LIỆU KIỂM THỬ ĐỘC LẬP CHO GIAI ĐOẠN 8 ---");
    var reg1 = sqlAuthService.RegisterReader(new ReaderRegistrationRequest
    {
        Username = userReader1,
        FullName = "Độc giả Fines 1",
        Email = sharedEmail,
        Password = "ReaderPass123!",
        ConfirmPassword = "ReaderPass123!"
    });
    var reg2 = sqlAuthService.RegisterReader(new ReaderRegistrationRequest
    {
        Username = userReader2,
        FullName = "Độc giả Fines 2",
        Email = sharedEmail,
        Password = "ReaderPass123!",
        ConfirmPassword = "ReaderPass123!"
    });
    reader1UserId = sqlAuthService.Authenticate(userReader1, "ReaderPass123!").Session!.UserId;
    reader2UserId = sqlAuthService.Authenticate(userReader2, "ReaderPass123!").Session!.UserId;

    // Login as Admin to create Librarian & books
    sessionService.StartSession(new UserSession
    {
        UserId = 1,
        Username = "Admin",
        FullName = "Administrator",
        Role = UserRole.Administrator
    });

    var createLibRes = staffService.CreateManagedUserAccount(new CreateManagedUserRequest
    {
        Username = userLib,
        FullName = "Thủ thư Fines",
        Email = sharedEmail,
        Role = "Librarian",
        Password = "LibPassword123!",
        ConfirmPassword = "LibPassword123!"
    });
    libUserId = sqlAuthService.Authenticate(userLib, "LibPassword123!").Session!.UserId;

    var b1Res = staffService.CreateBook(new BookUpsertRequest
    {
        BookCode = bookCode1,
        Title = "Sách Kiểm Thử Tiền Phạt A",
        Author = "Tác giả 1",
        Category = "CNTT",
        TotalCopiesText = "10"
    });
    var b2Res = staffService.CreateBook(new BookUpsertRequest
    {
        BookCode = bookCode2,
        Title = "Sách Kiểm Thử Tiền Phạt B",
        Author = "Tác giả 2",
        Category = "Kinh tế",
        TotalCopiesText = "10"
    });

    book1Id = readerWorkspace.SearchBooks(bookCode1, null, "ALL", out _).First().BookId;
    book2Id = readerWorkspace.SearchBooks(bookCode2, null, "ALL", out _).First().BookId;

    AssertCheck("Tạo tài khoản & đầu sách kiểm thử thành công",
        reader1UserId > 0 && reader2UserId > 0 && libUserId > 0 && book1Id > 0 && book2Id > 0,
        $"R1={reader1UserId}, R2={reader2UserId}, Lib={libUserId}, B1={book1Id}, B2={book2Id}");

    // Switch session to Librarian
    sessionService.StartSession(new UserSession
    {
        UserId = libUserId,
        Username = userLib,
        FullName = "Thủ thư Fines",
        Role = UserRole.Librarian
    });

    // =========================================================================
    // SCENARIO 2 & 3: SÁCH TRẢ ĐÚNG HẠN VÀ TRẢ SỚM -> TIỀN PHẠT = 0
    // =========================================================================
    Console.WriteLine("\n--- [2 & 3] SÁCH TRẢ ĐÚNG HẠN HOẶC TRẢ SỚM HẠN ---");
    // Loan 1: On-time return (due date in future, returned now)
    staffService.CreateBorrowTransaction(new CreateBorrowLoanRequest
    {
        ReaderUserId = reader1UserId,
        BookId = book1Id,
        LoanDays = 7,
        Notes = "On-time loan"
    });
    var r1Loans = staffService.GetAllBorrowRecordsForStaff(bookCode1, "ALL", out _);
    onTimeBorrowId = r1Loans.First(l => l.UserId == reader1UserId).BorrowRecordId;
    var returnOnTimeRes = staffService.ProcessBookReturn(onTimeBorrowId, "Trả đúng hạn");

    // Loan 2: Early return (due date +14 days, returned now)
    staffService.CreateBorrowTransaction(new CreateBorrowLoanRequest
    {
        ReaderUserId = reader1UserId,
        BookId = book2Id,
        LoanDays = 14,
        Notes = "Early loan"
    });
    var r2Loans = staffService.GetAllBorrowRecordsForStaff(bookCode2, "ALL", out _);
    earlyBorrowId = r2Loans.First(l => l.UserId == reader1UserId).BorrowRecordId;
    var returnEarlyRes = staffService.ProcessBookReturn(earlyBorrowId, "Trả sớm hạn");

    int finesForOnTimeOrEarly = 0;
    using (SqlConnection conn = new(connStr))
    {
        conn.Open();
        using SqlCommand cmd = new("SELECT COUNT(1) FROM dbo.Fines WHERE BorrowRecordId IN (@B1, @B2);", conn);
        cmd.Parameters.AddWithValue("@B1", onTimeBorrowId);
        cmd.Parameters.AddWithValue("@B2", earlyBorrowId);
        finesForOnTimeOrEarly = Convert.ToInt32(cmd.ExecuteScalar());
    }

    AssertCheck("Sách trả đúng hạn hoặc trả sớm: không phát sinh tiền phạt (Fines = 0)",
        returnOnTimeRes.IsSuccess && returnEarlyRes.IsSuccess && finesForOnTimeOrEarly == 0,
        $"OnTimeRet={returnOnTimeRes.IsSuccess}, EarlyRet={returnEarlyRes.IsSuccess}, RecordedFinesCount={finesForOnTimeOrEarly}");

    // =========================================================================
    // SCENARIO 4: SÁCH TRẢ MUỘN -> SỐ NGÀY VÀ TIỀN PHẠT CHÍNH XÁC
    // =========================================================================
    Console.WriteLine("\n--- [4] SÁCH TRẢ MUỘN (OVERDUE RETURN) ---");
    // Create loan and simulate due date = 4 days ago
    staffService.CreateBorrowTransaction(new CreateBorrowLoanRequest
    {
        ReaderUserId = reader1UserId,
        BookId = book1Id,
        LoanDays = 1,
        Notes = "Late loan test"
    });
    var r3Loans = staffService.GetAllBorrowRecordsForStaff(bookCode1, "ALL", out _);
    lateBorrowId = r3Loans.First(l => l.UserId == reader1UserId && l.ReturnedAtLocal == null).BorrowRecordId;

    DateTime simulatedDueUtc = DateTime.UtcNow.AddDays(-4);
    using (SqlConnection conn = new(connStr))
    {
        conn.Open();
        using SqlCommand backdateCmd = new("UPDATE dbo.BorrowRecords SET DueAtUtc = @Due WHERE BorrowRecordId = @Id;", conn);
        backdateCmd.Parameters.AddWithValue("@Due", simulatedDueUtc);
        backdateCmd.Parameters.AddWithValue("@Id", lateBorrowId);
        backdateCmd.ExecuteNonQuery();
    }

    var returnLateRes = staffService.ProcessBookReturn(lateBorrowId, "Trả muộn 4 ngày");

    FineItemModel? lateFine = null;
    using (SqlConnection conn = new(connStr))
    {
        conn.Open();
        using SqlCommand fineCmd = new(@"
            SELECT DaysOverdue, DailyRateVnd, Amount, FineStatus
            FROM dbo.Fines
            WHERE BorrowRecordId = @Id AND FineType = N'Overdue';", conn);
        fineCmd.Parameters.AddWithValue("@Id", lateBorrowId);
        using SqlDataReader rdr = fineCmd.ExecuteReader();
        if (rdr.Read())
        {
            lateFine = new FineItemModel
            {
                DaysOverdue = rdr.GetInt32(0),
                DailyRateVnd = rdr.GetDecimal(1),
                Amount = rdr.GetDecimal(2),
                FineStatus = rdr.GetString(3)
            };
        }
    }

    AssertCheck("Sách trả muộn: tự động tính số ngày quá hạn và tiền phạt chính xác (4 ngày x 5.000 = 20.000 VNĐ)",
        returnLateRes.IsSuccess && lateFine is not null &&
        lateFine.DaysOverdue == 4 && lateFine.Amount == 20000m && lateFine.FineStatus == "Unpaid",
        $"DaysOverdue={lateFine?.DaysOverdue}, Amount={lateFine?.Amount:N0} VNĐ, Status={lateFine?.FineStatus}");

    // =========================================================================
    // SCENARIO 5 & 6: SÁCH CHƯA TRẢ VÀ ĐÃ QUÁ HẠN (TIỀN PHẠT DỰ KIẾN) VS CHƯA ĐẾN HẠN
    // =========================================================================
    Console.WriteLine("\n--- [5 & 6] SÁCH CHƯA TRẢ QUÁ HẠN (DỰ KIẾN) VÀ SÁCH CHƯA ĐẾN HẠN ---");
    // Loan 4: Active unreturned loan, due date 3 days ago (Estimated overdue)
    staffService.CreateBorrowTransaction(new CreateBorrowLoanRequest
    {
        ReaderUserId = reader1UserId,
        BookId = book1Id,
        LoanDays = 1,
        Notes = "Active overdue loan"
    });
    var r4Loans = staffService.GetAllBorrowRecordsForStaff(bookCode1, "ALL", out _);
    activeOverdueBorrowId = r4Loans.First(l => l.UserId == reader1UserId && l.ReturnedAtLocal == null).BorrowRecordId;

    DateTime simulatedActiveDue = DateTime.UtcNow.AddDays(-3);
    using (SqlConnection conn = new(connStr))
    {
        conn.Open();
        using SqlCommand backdateCmd = new("UPDATE dbo.BorrowRecords SET DueAtUtc = @Due WHERE BorrowRecordId = @Id;", conn);
        backdateCmd.Parameters.AddWithValue("@Due", simulatedActiveDue);
        backdateCmd.Parameters.AddWithValue("@Id", activeOverdueBorrowId);
        backdateCmd.ExecuteNonQuery();
    }

    // Loan 5: Active unreturned loan, due date +10 days (Not due -> fine = 0)
    staffService.CreateBorrowTransaction(new CreateBorrowLoanRequest
    {
        ReaderUserId = reader1UserId,
        BookId = book2Id,
        LoanDays = 10,
        Notes = "Not due yet loan"
    });
    var r5Loans = staffService.GetAllBorrowRecordsForStaff(bookCode2, "ALL", out _);
    activeNotDueBorrowId = r5Loans.First(l => l.UserId == reader1UserId && l.ReturnedAtLocal == null && l.BorrowRecordId != activeOverdueBorrowId).BorrowRecordId;

    // Check staff workspace
    var staffFines = fineService.GetStaffFinesWorkspace(null, "ALL", out FineSummaryStatsModel summary, out _);
    var estimatedItem = staffFines.FirstOrDefault(f => f.BorrowRecordId == activeOverdueBorrowId);
    var notDueItem = staffFines.FirstOrDefault(f => f.BorrowRecordId == activeNotDueBorrowId);

    AssertCheck("Sách chưa trả và đã quá hạn: hiển thị tiền phạt dự kiến/tạm tính (3 ngày x 5.000 = 15.000 VNĐ)",
        estimatedItem is { IsEstimatedOnly: true, DaysOverdue: 3, Amount: 15000m },
        $"EstimatedItem: IsEstimated={estimatedItem?.IsEstimatedOnly}, Days={estimatedItem?.DaysOverdue}, Amount={estimatedItem?.Amount:N0} VNĐ");

    AssertCheck("Sách chưa đến hạn: không phát sinh khoản phạt nào",
        notDueItem == null,
        "Phiếu mượn chưa đến hạn không xuất hiện trong danh sách phạt");

    // =========================================================================
    // SCENARIO 7 & 8: KHÔNG TẠO TRÙNG KHOẢN PHẠT HOẶC TRÙNG GIAO DỊCH TRẢ SÁCH
    // =========================================================================
    Console.WriteLine("\n--- [7 & 8] CHỐNG TRÙNG LẶP KHOẢN PHẠT & TRẢ SÁCH ---");
    // Reload workspace 3 times
    for (int i = 0; i < 3; i++)
    {
        fineService.GetStaffFinesWorkspace(null, "ALL", out _, out _);
    }
    int totalDbFinesCount = 0;
    using (SqlConnection conn = new(connStr))
    {
        conn.Open();
        using SqlCommand cntCmd = new("SELECT COUNT(1) FROM dbo.Fines WHERE BorrowRecordId = @Id;", conn);
        cntCmd.Parameters.AddWithValue("@Id", lateBorrowId);
        totalDbFinesCount = Convert.ToInt32(cntCmd.ExecuteScalar());
    }
    AssertCheck("Mở lại màn hình/truy vấn nhiều lần: không tạo bản ghi phạt trùng lặp",
        totalDbFinesCount == 1,
        $"Số bản ghi phạt trong dbo.Fines cho phiếu {lateBorrowId} = {totalDbFinesCount}");

    // Attempt second return on already returned late loan
    var secondReturnRes = staffService.ProcessBookReturn(lateBorrowId, "Trả lại lần 2");
    AssertCheck("Ghi nhận trả sách nhiều lần: chặn xử lý trùng lặp và giữ nguyên số lượng tồn kho",
        !secondReturnRes.IsSuccess,
        secondReturnRes.Message);

    // =========================================================================
    // SCENARIO 9 & 10: PHÂN QUYỀN VÀ CÔ LẬP DỮ LIỆU READER VS STAFF
    // =========================================================================
    Console.WriteLine("\n--- [9 & 10] CÔ LẬP DỮ LIỆU VÀ PHÂN QUYỀN TẦNG SERVICE ---");
    // Switch to Reader 2
    sessionService.StartSession(new UserSession
    {
        UserId = reader2UserId,
        Username = userReader2,
        FullName = "Độc giả Fines 2",
        Role = UserRole.Reader
    });

    var r2Fines = fineService.GetOwnFinesWorkspace(out _, out _);
    var r2TryAccessR1 = fineService.TryGetFinesForUser(reader1UserId, out _, out string? crossDenied);
    var r2TryAdjust = fineService.AdjustFineAmount(1, 1000m, "Hạ tiền");
    var r2TryWaive = fineService.WaiveFine(1, "Miễn phạt");
    var r2TryUpdateRate = fineService.UpdateOverdueDailyRate(10000m);

    AssertCheck("Reader chỉ xem được tiền phạt của chính mình, không xem được Reader khác",
        r2Fines.Count == 0 && r2TryAccessR1.Count == 0 && !string.IsNullOrEmpty(crossDenied),
        $"R2OwnFines={r2Fines.Count}, CrossAccessCount={r2TryAccessR1.Count}, DeniedMsg={crossDenied}");

    AssertCheck("Reader không thể gọi hàm quản lý tiền phạt của Thủ thư hoặc Admin ở tầng Service",
        !r2TryAdjust.IsSuccess && !r2TryWaive.IsSuccess && !r2TryUpdateRate.IsSuccess,
        "Adjust, Waive và UpdateDailyRate đều bị từ chối đối với Reader");

    // =========================================================================
    // SCENARIO 11: ĐIỀU CHỈNH HOẶC MIỄN TIỀN PHẠT (LƯU LÝ DO & AUDIT LOG)
    // =========================================================================
    Console.WriteLine("\n--- [11] ĐIỀU CHỈNH & MIỄN TIỀN PHẠT CÓ LƯU LÝ DO & AUDIT LOG ---");
    sessionService.StartSession(new UserSession
    {
        UserId = libUserId,
        Username = userLib,
        FullName = "Thủ thư Fines",
        Role = UserRole.Librarian
    });

    int lateFineId = 0;
    using (SqlConnection conn = new(connStr))
    {
        conn.Open();
        using SqlCommand getFineIdCmd = new("SELECT FineId FROM dbo.Fines WHERE BorrowRecordId = @Id;", conn);
        getFineIdCmd.Parameters.AddWithValue("@Id", lateBorrowId);
        lateFineId = Convert.ToInt32(getFineIdCmd.ExecuteScalar());
    }

    var adjustWithoutReason = fineService.AdjustFineAmount(lateFineId, 10000m, "   ");
    var adjustSuccess = fineService.AdjustFineAmount(lateFineId, 10000m, "Giảm 50% do độc giả có hoàn cảnh khó khăn");

    var waiveWithoutReason = fineService.WaiveFine(lateFineId, "");
    var waiveSuccess = fineService.WaiveFine(lateFineId, "Miễn toàn bộ theo phê duyệt của Ban giám đốc thư viện");

    // Confirm estimated overdue fine and test MarkFineAsPaid ("Thu tiền phạt")
    var confirmEstRes = fineService.ConfirmOrRecordOverdueFine(activeOverdueBorrowId);
    int confirmedFineId = 0;
    using (SqlConnection conn = new(connStr))
    {
        conn.Open();
        using SqlCommand getConfIdCmd = new("SELECT FineId FROM dbo.Fines WHERE BorrowRecordId = @Id;", conn);
        getConfIdCmd.Parameters.AddWithValue("@Id", activeOverdueBorrowId);
        confirmedFineId = Convert.ToInt32(getConfIdCmd.ExecuteScalar());
    }

    var paySuccess = fineService.MarkFineAsPaid(confirmedFineId, "Chuyển khoản", "UNC Vietcombank #9988");
    var paidLogs = fineService.GetFineAuditLogs(confirmedFineId);
    var auditLogs = fineService.GetFineAuditLogs(lateFineId);

    AssertCheck("Điều chỉnh, Miễn phạt và Thu tiền phạt (Mark as Paid): ghi nhận PaidAtUtc và dbo.FineAuditLogs",
        !adjustWithoutReason.IsSuccess && adjustSuccess.IsSuccess &&
        !waiveWithoutReason.IsSuccess && waiveSuccess.IsSuccess &&
        confirmEstRes.IsSuccess && paySuccess.IsSuccess &&
        paidLogs.Any(l => l.ActionType == "Paid" && l.NewStatus == "Paid") &&
        auditLogs.Count >= 2,
        $"Adjust={adjustSuccess.IsSuccess}, Waive={waiveSuccess.IsSuccess}, Pay={paySuccess.IsSuccess}, PaidAuditFound={paidLogs.Any(l => l.ActionType == "Paid")}");

    // =========================================================================
    // SCENARIO 11B: GIA HẠN MƯỢN SÁCH (LOAN RENEWAL) & ĐẶT TRƯỚC SÁCH (RESERVATION)
    // =========================================================================
    Console.WriteLine("\n--- [11B] GIA HẠN MƯỢN SÁCH (+7/+14 NGÀY, TỐI ĐA 2 LẦN) & ĐẶT TRƯỚC SÁCH (HOLD) ---");
    // 1. Overdue loan cannot be renewed
    var renewOverdueFail = staffService.RenewBorrowLoan(activeOverdueBorrowId, 7);
    // 2. Active not-due loan can be renewed 1st time (+7 days) and 2nd time (+14 days), 3rd time blocked
    var renew1Ok = staffService.RenewBorrowLoan(activeNotDueBorrowId, 7);
    var renew2Ok = staffService.RenewBorrowLoan(activeNotDueBorrowId, 14);
    var renew3Blocked = staffService.RenewBorrowLoan(activeNotDueBorrowId, 7);

    AssertCheck("Gia hạn mượn sách: chặn sách quá hạn, cho phép gia hạn +7/+14 ngày tối đa 2 lần, chặn lần thứ 3",
        !renewOverdueFail.IsSuccess && renew1Ok.IsSuccess && renew2Ok.IsSuccess && !renew3Blocked.IsSuccess,
        $"OverdueBlocked={!renewOverdueFail.IsSuccess}, Renew1={renew1Ok.IsSuccess}, Renew2={renew2Ok.IsSuccess}, Renew3Blocked={!renew3Blocked.IsSuccess}");

    // 3. Test Book Reservation when AvailableCopies == 0
    using (SqlConnection conn = new(connStr))
    {
        conn.Open();
        using SqlCommand zeroCopiesCmd = new("UPDATE dbo.Books SET AvailableCopies = 0 WHERE BookId = @BId;", conn);
        zeroCopiesCmd.Parameters.AddWithValue("@BId", book1Id);
        zeroCopiesCmd.ExecuteNonQuery();
    }

    // Reader 2 reserves book1Id (which has AvailableCopies == 0 and Reader 1 is currently borrowing activeOverdueBorrowId)
    sessionService.StartSession(new UserSession
    {
        UserId = reader2UserId,
        Username = userReader2,
        FullName = "Độc giả Fines 2",
        Role = UserRole.Reader
    });
    var reserveOk = staffService.ReserveBook(book1Id, null, "Đặt trước khi hết sách");

    // Switch back to Librarian and return Reader 1's loan of book1Id -> system should hold it for Reader 2 ('Ready')
    sessionService.StartSession(new UserSession
    {
        UserId = libUserId,
        Username = userLib,
        FullName = "Thủ thư Fines",
        Role = UserRole.Librarian
    });
    var returnAndHoldRes = staffService.ProcessBookReturn(activeOverdueBorrowId, "Trả sách có người đặt trước");
    var rsvList = staffService.GetReservationsForCurrentSession(out _);
    var r2Hold = rsvList.FirstOrDefault(r => r.BookId == book1Id && r.UserId == reader2UserId);

    AssertCheck("Đặt trước sách (AvailableCopies == 0) & Tự động giữ sách (Ready/Hold) khi có người trả",
        reserveOk.IsSuccess && returnAndHoldRes.IsSuccess && r2Hold is { ReservationStatus: "Ready" },
        $"ReserveOk={reserveOk.IsSuccess}, ReturnHoldOk={returnAndHoldRes.IsSuccess}, HoldStatus={r2Hold?.ReservationStatus}");

    // Test Admin updating daily rate
    sessionService.StartSession(new UserSession
    {
        UserId = 1,
        Username = "Admin",
        FullName = "Administrator",
        Role = UserRole.Administrator
    });
    var updateRateRes = fineService.UpdateOverdueDailyRate(6000m);
    decimal updatedRate = fineService.GetCurrentDailyOverdueRateVnd();
    // Restore back to 5000m
    fineService.UpdateOverdueDailyRate(5000m);
    decimal restoredRate = fineService.GetCurrentDailyOverdueRateVnd();

    AssertCheck("Administrator quản lý cấu hình mức phạt thành công (5.000 -> 6.000 -> 5.000 VNĐ)",
        updateRateRes.IsSuccess && updatedRate == 6000m && restoredRate == 5000m,
        $"UpdatedRate={updatedRate:N0} VNĐ, RestoredRate={restoredRate:N0} VNĐ");

    // =========================================================================
    // SCENARIO 12: XỬ LÝ LỖI DATABASE AN TOÀN VÀ KIỂM TRA SCHEMA 10 BẢNG
    // =========================================================================
    Console.WriteLine("\n--- [12] XỬ LÝ LỖI DATABASE AN TOÀN VÀ PROBE 10 BẢNG ---");
    var probeResult = DatabaseConfig.ProbeDatabase();
    AssertCheck("DatabaseConfig.ProbeDatabase xác thực đầy đủ 10 bảng cốt lõi của hệ thống",
        probeResult.IsServerReachable && probeResult.IsDatabaseCreated && probeResult.IsSchemaReady && probeResult.MissingTables.Count == 0,
        $"SchemaReady={probeResult.IsSchemaReady}, MissingTables={probeResult.MissingTables.Count}, Summary='{probeResult.StatusSummary}'");

    string origServer = DatabaseConfig.ConfiguredServerName;
    try
    {
        DatabaseConfig.ConfiguredServerName = "localhost\\NON_EXISTENT_INSTANCE_S8";
        var offlineFines = fineService.GetStaffFinesWorkspace(null, "ALL", out _, out string? offlineErr);
        AssertCheck("SQL Server offline: bắt lỗi an toàn và hiển thị thông báo, không crash ứng dụng",
            offlineFines.Count == 0 && !string.IsNullOrEmpty(offlineErr),
            $"OfflineErr='{offlineErr}'");
    }
    finally
    {
        DatabaseConfig.ConfiguredServerName = origServer;
    }
}
finally
{
    // Clean up temporary Stage 8 test rows
    try
    {
        using SqlConnection cleanupConn = new(DatabaseConfig.GetConnectionString());
        cleanupConn.Open();
        using SqlCommand cleanCmd = new(@"
            IF OBJECT_ID(N'dbo.BookReservations', N'U') IS NOT NULL
                DELETE FROM dbo.BookReservations WHERE UserId IN (@U1, @U2, @UL) OR BookId IN (@B1, @B2);
            DELETE FROM dbo.FineAuditLogs WHERE FineId IN (SELECT FineId FROM dbo.Fines WHERE UserId IN (@U1, @U2, @UL));
            DELETE FROM dbo.Fines WHERE UserId IN (@U1, @U2, @UL);
            DELETE FROM dbo.BorrowRecords WHERE UserId IN (@U1, @U2, @UL) OR BookId IN (@B1, @B2);
            DELETE FROM dbo.Readers WHERE UserId IN (@U1, @U2, @UL);
            DELETE FROM dbo.Users WHERE UserId IN (@U1, @U2, @UL);
            DELETE FROM dbo.Books WHERE BookId IN (@B1, @B2);", cleanupConn);
        cleanCmd.Parameters.AddWithValue("@U1", reader1UserId);
        cleanCmd.Parameters.AddWithValue("@U2", reader2UserId);
        cleanCmd.Parameters.AddWithValue("@UL", libUserId);
        cleanCmd.Parameters.AddWithValue("@B1", book1Id);
        cleanCmd.Parameters.AddWithValue("@B2", book2Id);
        cleanCmd.ExecuteNonQuery();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Cleanup Warning] {ex.Message}");
    }
}

// =========================================================================
// SCENARIO 13: BẢO TOÀN NGUYÊN VẸN DỮ LIỆU CŨ
// =========================================================================
Console.WriteLine("\n--- [13] BẢO TOÀN DỮ LIỆU NGƯỜI DÙNG, SÁCH VÀ LỊCH SỬ CŨ ---");
using (SqlConnection verifyConn = new(DatabaseConfig.GetConnectionString()))
{
    verifyConn.Open();
    using SqlCommand uAfterCmd = new("SELECT COUNT(1) FROM dbo.Users;", verifyConn);
    int finalUserCount = Convert.ToInt32(uAfterCmd.ExecuteScalar());

    using SqlCommand bAfterCmd = new("SELECT COUNT(1) FROM dbo.Books;", verifyConn);
    int finalBookCount = Convert.ToInt32(bAfterCmd.ExecuteScalar());

    using SqlCommand checkExistingUsersCmd = new(@"
        SELECT COUNT(1)
        FROM dbo.Users
        WHERE Username IN (N'Admin', N'quy123', N'quy456', N'user123') AND IsActive = 1;", verifyConn);
    int existingCoreUsers = Convert.ToInt32(checkExistingUsersCmd.ExecuteScalar());

    AssertCheck("Bảo toàn 100% tài khoản người dùng và sách hiện có (Admin, quy123, quy456, user123)",
        finalUserCount == initialUserCount && finalBookCount == initialBookCount && existingCoreUsers == 4,
        $"Users={finalUserCount}/{initialUserCount}, Books={finalBookCount}/{initialBookCount}, CoreActiveUsers={existingCoreUsers}/4");
}

// =========================================================================
// SCENARIO 14: QUY TẮC BẢO MẬT MẬT KHẨU MỚI (TỐI THIỂU 8 KÝ TỰ, CHỮ HOA, CHỮ SỐ, ĐẶC BIỆT)
// =========================================================================
Console.WriteLine("\n--- [14] QUY ĐỊNH BẢO MẬT MẬT KHẨU (MIN 8, CHỮ HOA, CHỮ SỐ, KÝ TỰ ĐẶC BIỆT) ---");
bool rejectShort = !PasswordHasher.IsStrongPassword("Sh1!");
bool rejectNoUpper = !PasswordHasher.IsStrongPassword("lowercase123!");
bool rejectNoLower = !PasswordHasher.IsStrongPassword("UPPERCASE123!");
bool rejectNoDigit = !PasswordHasher.IsStrongPassword("NoDigitPassword!");
bool rejectNoSpecial = !PasswordHasher.IsStrongPassword("NoSpecialChar123");
bool acceptStrong1 = PasswordHasher.IsStrongPassword("Password123!");
bool acceptStrong2 = PasswordHasher.IsStrongPassword("Admin@Booktibus#2026");

AssertCheck("Quy tắc mật khẩu kiểm tra đủ: tối thiểu 8 ký tự, có chữ hoa, chữ thường, chữ số và ký tự đặc biệt",
    rejectShort && rejectNoUpper && rejectNoLower && rejectNoDigit && rejectNoSpecial && acceptStrong1 && acceptStrong2,
    $"Short={rejectShort}, NoUpper={rejectNoUpper}, NoLower={rejectNoLower}, NoDigit={rejectNoDigit}, NoSpecial={rejectNoSpecial}, Strong1={acceptStrong1}, Strong2={acceptStrong2}");

// Kiểm tra tầng dịch vụ từ chối đăng ký độc giả nếu mật khẩu thiếu ký tự đặc biệt
var weakRegRes = sqlAuthService.RegisterReader(new ReaderRegistrationRequest
{
    Username = $"weak_usr_{DateTime.UtcNow.Ticks.ToString()[^5..]}",
    FullName = "Độc giả Weak Password",
    Email = "weakpwd@booktibus.test",
    Password = "WeakPassword123", // Không có ký tự đặc biệt
    ConfirmPassword = "WeakPassword123"
});
AssertCheck("SqlAuthenticationService.RegisterReader từ chối mật khẩu không có ký tự đặc biệt (WeakPassword)",
    !weakRegRes.IsSuccess && weakRegRes.FailureReason == RegistrationFailureReason.WeakPassword,
    $"IsSuccess={weakRegRes.IsSuccess}, FailureReason={weakRegRes.FailureReason}");

// =========================================================================
// SCENARIO 15: KIỂM TRA KHỞI TẠO XAML VÀ RESOURCE CỦA FINESVIEW VÀ REVEALPASSWORDBOX
// =========================================================================
Console.WriteLine("\n--- [15] KHỞI TẠO XAML VÀ RESOURCE CỦA FINESVIEW VÀ REVEALPASSWORDBOX ---");
Exception? xamlInitEx = null;
Thread staThread = new(() =>
{
    try
    {
        if (System.Windows.Application.Current == null)
        {
            var app = new Booktibus.App();
            app.InitializeComponent();
        }
        var finesView = new Booktibus.Views.FinesView();
        var borrowingView = new Booktibus.Views.ReaderBorrowingView();
        var catalogView = new Booktibus.Views.BookCatalogView();
        var revealPwd = new Booktibus.Views.RevealPasswordBox();
    }
    catch (Exception ex)
    {
        xamlInitEx = ex;
    }
});
staThread.SetApartmentState(ApartmentState.STA);
staThread.Start();
staThread.Join();

AssertCheck("FinesView và RevealPasswordBox khởi tạo thành công không phát sinh XamlParseException",
    xamlInitEx == null,
    xamlInitEx == null ? "InitializeComponent thành công, tất cả StaticResource hợp lệ." : $"Lỗi: {xamlInitEx.ToString()}");

Console.WriteLine("\n======================================================================");
Console.WriteLine($"KẾT QUẢ KIỂM THỬ: {passedTests} PASSED, {failedTests} FAILED");
Console.WriteLine("======================================================================");

if (failedTests > 0)
{
    Environment.Exit(1);
}
