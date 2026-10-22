using System.Data;
using System.Text.RegularExpressions;
using Booktibus.Data;
using Booktibus.Helpers;
using Booktibus.Models;
using Microsoft.Data.SqlClient;

namespace Booktibus.Services
{
    /// <summary>
    /// Business and data access service for Stage 6 Librarian and Administrator operations:
    /// Book Catalog CRUD, Circulation (Checkout &amp; Return), Reader Directory Management,
    /// Administrator Account &amp; Role Management, and Live SQL Server Statistics.
    /// Enforces role permissions at the service layer before every database operation.
    /// </summary>
    public class LibraryManagementService
    {
        private static readonly Regex UsernamePattern = new(@"^[a-zA-Z0-9._-]{4,50}$", RegexOptions.Compiled);
        private static readonly Regex EmailPattern = new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled);

        private readonly SessionService _sessionService;
        private readonly AuthorizationService _authorizationService;

        public event EventHandler? CatalogChanged;

        public event EventHandler? CirculationChanged;

        public event EventHandler? ReadersChanged;

        public event EventHandler? AccountsChanged;

        public LibraryManagementService(
            SessionService sessionService,
            AuthorizationService authorizationService)
        {
            ArgumentNullException.ThrowIfNull(sessionService);
            ArgumentNullException.ThrowIfNull(authorizationService);

            _sessionService = sessionService;
            _authorizationService = authorizationService;
        }

        // =====================================================================
        // 3.1. LIBRARIAN BOOK CATALOG MANAGEMENT (ManageCatalog)
        // =====================================================================

        public ServiceOperationResult CreateBook(BookUpsertRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);

            if (!_authorizationService.TryAuthorizeOperation(AppPermission.ManageCatalog, "CreateBook()", out string authMsg))
            {
                return ServiceOperationResult.Fail(authMsg);
            }

            if (!TryValidateBookInput(request, out string bookCode, out string title, out string author,
                out string category, out int? publishYear, out int totalCopies, out string validationError))
            {
                return ServiceOperationResult.Fail(validationError);
            }

            try
            {
                using SqlConnection conn = new(DatabaseConfig.GetConnectionString());
                conn.Open();

                const string checkCodeSql = "SELECT COUNT(1) FROM [dbo].[Books] WHERE [BookCode] = @BookCode;";
                using (SqlCommand checkCmd = new(checkCodeSql, conn))
                {
                    checkCmd.Parameters.Add("@BookCode", SqlDbType.NVarChar, 30).Value = bookCode;
                    int exists = Convert.ToInt32(checkCmd.ExecuteScalar());
                    if (exists > 0)
                    {
                        return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                            ? $"Mã sách '{bookCode}' đã tồn tại trong cơ sở dữ liệu BooktibusDB. Vui lòng nhập mã sách khác."
                            : $"Book Code '{bookCode}' already exists in BooktibusDB. Please specify a unique code.");
                    }
                }

                const string insertSql = @"
                    INSERT INTO [dbo].[Books]
                        ([BookCode], [Title], [Author], [Category], [Publisher], [PublishYear], [Isbn],
                         [TotalCopies], [AvailableCopies], [ShelfLocation], [Description], [IsSampleData])
                    VALUES
                        (@BookCode, @Title, @Author, @Category, @Publisher, @PublishYear, @Isbn,
                         @TotalCopies, @AvailableCopies, @ShelfLocation, @Description, 0);";

                using SqlCommand cmd = new(insertSql, conn);
                cmd.Parameters.Add("@BookCode", SqlDbType.NVarChar, 30).Value = bookCode;
                cmd.Parameters.Add("@Title", SqlDbType.NVarChar, 200).Value = title;
                cmd.Parameters.Add("@Author", SqlDbType.NVarChar, 150).Value = author;
                cmd.Parameters.Add("@Category", SqlDbType.NVarChar, 80).Value = category;
                cmd.Parameters.Add("@Publisher", SqlDbType.NVarChar, 120).Value =
                    string.IsNullOrWhiteSpace(request.Publisher) ? DBNull.Value : request.Publisher.Trim();
                cmd.Parameters.Add("@PublishYear", SqlDbType.Int).Value =
                    publishYear.HasValue ? publishYear.Value : DBNull.Value;
                cmd.Parameters.Add("@Isbn", SqlDbType.NVarChar, 30).Value =
                    string.IsNullOrWhiteSpace(request.Isbn) ? DBNull.Value : request.Isbn.Trim();
                cmd.Parameters.Add("@TotalCopies", SqlDbType.Int).Value = totalCopies;
                cmd.Parameters.Add("@AvailableCopies", SqlDbType.Int).Value = totalCopies;
                cmd.Parameters.Add("@ShelfLocation", SqlDbType.NVarChar, 50).Value =
                    string.IsNullOrWhiteSpace(request.ShelfLocation) ? DBNull.Value : request.ShelfLocation.Trim();
                cmd.Parameters.Add("@Description", SqlDbType.NVarChar, 1000).Value =
                    string.IsNullOrWhiteSpace(request.Description) ? DBNull.Value : request.Description.Trim();

                cmd.ExecuteNonQuery();
                CatalogChanged?.Invoke(this, EventArgs.Empty);

                return ServiceOperationResult.Ok(LocalizationService.IsVietnamese
                    ? $"[Quản lý Sách] Đã thêm đầu sách mới '{title}' ({bookCode}) với {totalCopies} bản sao vào BooktibusDB."
                    : $"[Book Management] Added new book '{title}' ({bookCode}) with {totalCopies} copies to BooktibusDB.");
            }
            catch (SqlException ex)
            {
                AppDiagnosticLogger.LogDatabaseException(nameof(LibraryManagementService), nameof(CreateBook), ex, $"BookCode={bookCode}");
                return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                    ? $"Lỗi SQL Server khi thêm sách mới: {ex.Message}"
                    : $"SQL Server error while adding new book: {ex.Message}");
            }
        }

        public ServiceOperationResult UpdateBook(BookUpsertRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);

            if (!_authorizationService.TryAuthorizeOperation(AppPermission.ManageCatalog, "UpdateBook()", out string authMsg))
            {
                return ServiceOperationResult.Fail(authMsg);
            }

            if (!request.BookId.HasValue || request.BookId.Value <= 0)
            {
                return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                    ? "Vui lòng chọn một đầu sách hợp lệ để cập nhật."
                    : "Please select a valid book to update.");
            }

            if (!TryValidateBookInput(request, out string bookCode, out string title, out string author,
                out string category, out int? publishYear, out int newTotalCopies, out string validationError))
            {
                return ServiceOperationResult.Fail(validationError);
            }

            int bookId = request.BookId.Value;

            try
            {
                using SqlConnection conn = new(DatabaseConfig.GetConnectionString());
                conn.Open();
                using SqlTransaction tx = conn.BeginTransaction();

                // 1. Check duplicate BookCode on other books
                const string checkDupCodeSql = @"
                    SELECT COUNT(1)
                    FROM [dbo].[Books]
                    WHERE [BookCode] = @BookCode AND [BookId] <> @BookId;";

                using (SqlCommand dupCmd = new(checkDupCodeSql, conn, tx))
                {
                    dupCmd.Parameters.Add("@BookCode", SqlDbType.NVarChar, 30).Value = bookCode;
                    dupCmd.Parameters.Add("@BookId", SqlDbType.Int).Value = bookId;
                    if (Convert.ToInt32(dupCmd.ExecuteScalar()) > 0)
                    {
                        tx.Rollback();
                        return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                            ? $"Mã sách '{bookCode}' đã thuộc về một đầu sách khác."
                            : $"Book Code '{bookCode}' is already assigned to another book.");
                    }
                }

                // 2. Count active unreturned loans for this book to enforce copy integrity
                const string activeLoansSql = @"
                    SELECT COUNT(1)
                    FROM [dbo].[BorrowRecords] WITH (UPDLOCK)
                    WHERE [BookId] = @BookId AND [ReturnedAtUtc] IS NULL;";

                int activeLoansCount;
                using (SqlCommand loanCmd = new(activeLoansSql, conn, tx))
                {
                    loanCmd.Parameters.Add("@BookId", SqlDbType.Int).Value = bookId;
                    activeLoansCount = Convert.ToInt32(loanCmd.ExecuteScalar());
                }

                if (newTotalCopies < activeLoansCount)
                {
                    tx.Rollback();
                    return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                        ? $"[Bảo toàn dữ liệu mượn trả] Không thể giảm Tổng số bản sao xuống {newTotalCopies} vì hiện đang có {activeLoansCount} bản sao đang được độc giả mượn chưa trả."
                        : $"[Circulation Integrity] Cannot reduce Total Copies to {newTotalCopies} because {activeLoansCount} copy(ies) are currently borrowed and unreturned.");
                }

                int newAvailableCopies = newTotalCopies - activeLoansCount;

                const string updateSql = @"
                    UPDATE [dbo].[Books]
                    SET [BookCode]        = @BookCode,
                        [Title]           = @Title,
                        [Author]          = @Author,
                        [Category]        = @Category,
                        [Publisher]       = @Publisher,
                        [PublishYear]     = @PublishYear,
                        [Isbn]            = @Isbn,
                        [TotalCopies]     = @TotalCopies,
                        [AvailableCopies] = @AvailableCopies,
                        [ShelfLocation]   = @ShelfLocation,
                        [Description]     = @Description
                    WHERE [BookId] = @BookId;";

                using (SqlCommand updateCmd = new(updateSql, conn, tx))
                {
                    updateCmd.Parameters.Add("@BookId", SqlDbType.Int).Value = bookId;
                    updateCmd.Parameters.Add("@BookCode", SqlDbType.NVarChar, 30).Value = bookCode;
                    updateCmd.Parameters.Add("@Title", SqlDbType.NVarChar, 200).Value = title;
                    updateCmd.Parameters.Add("@Author", SqlDbType.NVarChar, 150).Value = author;
                    updateCmd.Parameters.Add("@Category", SqlDbType.NVarChar, 80).Value = category;
                    updateCmd.Parameters.Add("@Publisher", SqlDbType.NVarChar, 120).Value =
                        string.IsNullOrWhiteSpace(request.Publisher) ? DBNull.Value : request.Publisher.Trim();
                    updateCmd.Parameters.Add("@PublishYear", SqlDbType.Int).Value =
                        publishYear.HasValue ? publishYear.Value : DBNull.Value;
                    updateCmd.Parameters.Add("@Isbn", SqlDbType.NVarChar, 30).Value =
                        string.IsNullOrWhiteSpace(request.Isbn) ? DBNull.Value : request.Isbn.Trim();
                    updateCmd.Parameters.Add("@TotalCopies", SqlDbType.Int).Value = newTotalCopies;
                    updateCmd.Parameters.Add("@AvailableCopies", SqlDbType.Int).Value = newAvailableCopies;
                    updateCmd.Parameters.Add("@ShelfLocation", SqlDbType.NVarChar, 50).Value =
                        string.IsNullOrWhiteSpace(request.ShelfLocation) ? DBNull.Value : request.ShelfLocation.Trim();
                    updateCmd.Parameters.Add("@Description", SqlDbType.NVarChar, 1000).Value =
                        string.IsNullOrWhiteSpace(request.Description) ? DBNull.Value : request.Description.Trim();

                    int rows = updateCmd.ExecuteNonQuery();
                    if (rows == 0)
                    {
                        tx.Rollback();
                        return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                            ? "Không tìm thấy đầu sách cần cập nhật trong cơ sở dữ liệu."
                            : "Target book was not found in the database.");
                    }
                }

                tx.Commit();
                CatalogChanged?.Invoke(this, EventArgs.Empty);

                return ServiceOperationResult.Ok(LocalizationService.IsVietnamese
                    ? $"[Quản lý Sách] Đã cập nhật sách '{title}' ({bookCode}). Bản sao còn lại: {newAvailableCopies}/{newTotalCopies}."
                    : $"[Book Management] Updated book '{title}' ({bookCode}). Available copies: {newAvailableCopies}/{newTotalCopies}.");
            }
            catch (SqlException ex)
            {
                AppDiagnosticLogger.LogDatabaseException(nameof(LibraryManagementService), nameof(UpdateBook), ex, $"BookId={bookId}; BookCode={bookCode}");
                return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                    ? $"Lỗi SQL Server khi cập nhật sách: {ex.Message}"
                    : $"SQL Server error while updating book: {ex.Message}");
            }
        }

        public ServiceOperationResult DeleteBook(int bookId)
        {
            if (!_authorizationService.TryAuthorizeOperation(AppPermission.ManageCatalog, "DeleteBook()", out string authMsg))
            {
                return ServiceOperationResult.Fail(authMsg);
            }

            if (bookId <= 0)
            {
                return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                    ? "Vui lòng chọn đầu sách cần xóa."
                    : "Please select a book to delete.");
            }

            try
            {
                using SqlConnection conn = new(DatabaseConfig.GetConnectionString());
                conn.Open();
                using SqlTransaction tx = conn.BeginTransaction();

                // Integrity check: never delete a book that has any borrowing history in dbo.BorrowRecords
                const string checkLoansSql = @"
                    SELECT COUNT(1)
                    FROM [dbo].[BorrowRecords]
                    WHERE [BookId] = @BookId;";

                using (SqlCommand checkCmd = new(checkLoansSql, conn, tx))
                {
                    checkCmd.Parameters.Add("@BookId", SqlDbType.Int).Value = bookId;
                    int loanCount = Convert.ToInt32(checkCmd.ExecuteScalar());
                    if (loanCount > 0)
                    {
                        tx.Rollback();
                        return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                            ? $"[Bảo toàn giao dịch mượn trả] Không được phép xóa đầu sách này vì đang có {loanCount} giao dịch mượn/trả tham chiếu tới sách trong bảng dbo.BorrowRecords."
                            : $"[Transaction Integrity] Cannot delete this book because {loanCount} borrowing record(s) in dbo.BorrowRecords reference it.");
                    }
                }

                // Remove any ReaderFavorites pointing to this book before deleting the book
                const string deleteFavsSql = "DELETE FROM [dbo].[ReaderFavorites] WHERE [BookId] = @BookId;";
                using (SqlCommand favCmd = new(deleteFavsSql, conn, tx))
                {
                    favCmd.Parameters.Add("@BookId", SqlDbType.Int).Value = bookId;
                    favCmd.ExecuteNonQuery();
                }

                const string deleteBookSql = "DELETE FROM [dbo].[Books] WHERE [BookId] = @BookId;";
                using (SqlCommand delCmd = new(deleteBookSql, conn, tx))
                {
                    delCmd.Parameters.Add("@BookId", SqlDbType.Int).Value = bookId;
                    int deleted = delCmd.ExecuteNonQuery();
                    if (deleted == 0)
                    {
                        tx.Rollback();
                        return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                            ? "Không tìm thấy đầu sách cần xóa."
                            : "Book to delete was not found.");
                    }
                }

                tx.Commit();
                CatalogChanged?.Invoke(this, EventArgs.Empty);

                return ServiceOperationResult.Ok(LocalizationService.IsVietnamese
                    ? "[Quản lý Sách] Đã xóa đầu sách chưa phát sinh giao dịch mượn trả khỏi BooktibusDB."
                    : "[Book Management] Deleted book (with zero circulation history) from BooktibusDB.");
            }
            catch (SqlException ex)
            {
                AppDiagnosticLogger.LogDatabaseException(nameof(LibraryManagementService), nameof(DeleteBook), ex, $"BookId={bookId}");
                return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                    ? $"Lỗi SQL Server khi xóa sách: {ex.Message}"
                    : $"SQL Server error while deleting book: {ex.Message}");
            }
        }

        // =====================================================================
        // 3.2. LIBRARIAN BORROWING & RETURN MANAGEMENT (ProcessCirculation)
        // =====================================================================

        /// <summary>
        /// Automatically detects and updates overdue status for unreturned loans whose DueAtUtc has passed.
        /// </summary>
        public int SyncOverdueBorrowRecords()
        {
            try
            {
                using SqlConnection conn = new(DatabaseConfig.GetConnectionString());
                conn.Open();

                const string sql = @"
                    UPDATE [dbo].[BorrowRecords]
                    SET [BorrowStatus] = N'Overdue'
                    WHERE [ReturnedAtUtc] IS NULL
                      AND [BorrowStatus] = N'Borrowed'
                      AND [DueAtUtc] < SYSUTCDATETIME();";

                using SqlCommand cmd = new(sql, conn);
                return cmd.ExecuteNonQuery();
            }
            catch
            {
                return 0;
            }
        }

        public IReadOnlyList<StaffBorrowRecordItemModel> GetAllBorrowRecordsForStaff(
            string? searchKeyword,
            string? statusFilter,
            out string? errorMessage)
        {
            errorMessage = null;

            if (!_authorizationService.TryAuthorizeOperation(AppPermission.ProcessCirculation, "GetAllBorrowRecordsForStaff()", out string authMsg))
            {
                errorMessage = authMsg;
                return [];
            }

            SyncOverdueBorrowRecords();

            string keyword = searchKeyword?.Trim() ?? string.Empty;
            string status = statusFilter?.Trim() ?? "ALL";
            List<StaffBorrowRecordItemModel> records = new();

            try
            {
                using SqlConnection conn = new(DatabaseConfig.GetConnectionString());
                conn.Open();

                const string sql = @"
                    SELECT
                        br.[BorrowRecordId],
                        br.[RecordCode],
                        br.[UserId],
                        u.[Username],
                        u.[FullName],
                        br.[ReaderCardId],
                        br.[BookId],
                        b.[BookCode],
                        b.[Title],
                        b.[Author],
                        br.[BorrowedAtUtc],
                        br.[DueAtUtc],
                        br.[ReturnedAtUtc],
                        br.[BorrowStatus],
                        ISNULL(br.[Notes], N'')
                    FROM [dbo].[BorrowRecords] br
                    INNER JOIN [dbo].[Books] b ON b.[BookId] = br.[BookId]
                    INNER JOIN [dbo].[Users] u ON u.[UserId] = br.[UserId]
                    WHERE
                        (@Status = N'ALL' OR br.[BorrowStatus] = @Status)
                        AND (
                            @Keyword = N''
                            OR br.[RecordCode] LIKE N'%' + @Keyword + N'%'
                            OR br.[ReaderCardId] LIKE N'%' + @Keyword + N'%'
                            OR u.[Username] LIKE N'%' + @Keyword + N'%'
                            OR u.[FullName] LIKE N'%' + @Keyword + N'%'
                            OR b.[BookCode] LIKE N'%' + @Keyword + N'%'
                            OR b.[Title] LIKE N'%' + @Keyword + N'%'
                        )
                    ORDER BY br.[BorrowedAtUtc] DESC, br.[BorrowRecordId] DESC;";

                using SqlCommand cmd = new(sql, conn);
                cmd.Parameters.Add("@Status", SqlDbType.NVarChar, 30).Value = status;
                cmd.Parameters.Add("@Keyword", SqlDbType.NVarChar, 200).Value = keyword;

                using SqlDataReader reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    records.Add(MapStaffBorrowRecord(reader));
                }
            }
            catch (SqlException ex)
            {
                AppDiagnosticLogger.LogDatabaseException(nameof(LibraryManagementService), nameof(GetAllBorrowRecordsForStaff), ex);
                errorMessage = LocalizationService.IsVietnamese
                    ? $"Lỗi tải danh sách giao dịch mượn trả: {ex.Message}"
                    : $"Error loading circulation records: {ex.Message}";
            }

            return records;
        }

        public ServiceOperationResult CreateBorrowTransaction(CreateBorrowLoanRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);

            if (!_authorizationService.TryAuthorizeOperation(AppPermission.ProcessCirculation, "CreateBorrowTransaction()", out string authMsg))
            {
                return ServiceOperationResult.Fail(authMsg);
            }

            if (request.ReaderUserId <= 0)
            {
                return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                    ? "Vui lòng chọn độc giả mượn sách."
                    : "Please select a reader for the loan.");
            }

            if (request.BookId <= 0)
            {
                return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                    ? "Vui lòng chọn đầu sách cần cho mượn."
                    : "Please select a book to lend.");
            }

            if (request.LoanDays < 1 || request.LoanDays > 60)
            {
                return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                    ? "Số ngày mượn phải nằm trong khoảng từ 1 đến 60 ngày."
                    : "Loan duration must be between 1 and 60 days.");
            }

            try
            {
                using SqlConnection conn = new(DatabaseConfig.GetConnectionString());
                conn.Open();
                using SqlTransaction tx = conn.BeginTransaction();

                // 1. Verify Reader account & card status
                const string readerSql = @"
                    SELECT r.[ReaderCardId], r.[MembershipStatus], u.[IsActive], u.[FullName]
                    FROM [dbo].[Readers] r
                    INNER JOIN [dbo].[Users] u ON u.[UserId] = r.[UserId]
                    WHERE r.[UserId] = @UserId;";

                string readerCardId;
                string membershipStatus;
                bool isUserActive;
                string readerName;

                using (SqlCommand readerCmd = new(readerSql, conn, tx))
                {
                    readerCmd.Parameters.Add("@UserId", SqlDbType.Int).Value = request.ReaderUserId;
                    using SqlDataReader rdr = readerCmd.ExecuteReader();
                    if (!rdr.Read())
                    {
                        rdr.Close();
                        tx.Rollback();
                        return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                            ? "Không tìm thấy hồ sơ thẻ độc giả hợp lệ cho tài khoản đã chọn."
                            : "No valid reader card record found for the selected user.");
                    }

                    readerCardId = rdr.GetString(0);
                    membershipStatus = rdr.GetString(1);
                    isUserActive = rdr.GetBoolean(2);
                    readerName = rdr.GetString(3);
                }

                if (!isUserActive || !string.Equals(membershipStatus, "Active", StringComparison.OrdinalIgnoreCase))
                {
                    tx.Rollback();
                    return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                        ? $"Độc giả '{readerName}' ({readerCardId}) hiện không ở trạng thái hoạt động hợp lệ để mượn sách."
                        : $"Reader '{readerName}' ({readerCardId}) does not have an active membership/account status for borrowing.");
                }

                // 2. Prevent duplicate unreturned checkout of the exact same book by the same reader
                const string dupLoanSql = @"
                    SELECT COUNT(1)
                    FROM [dbo].[BorrowRecords] WITH (UPDLOCK)
                    WHERE [UserId] = @UserId
                      AND [BookId] = @BookId
                      AND [ReturnedAtUtc] IS NULL;";

                using (SqlCommand dupCmd = new(dupLoanSql, conn, tx))
                {
                    dupCmd.Parameters.Add("@UserId", SqlDbType.Int).Value = request.ReaderUserId;
                    dupCmd.Parameters.Add("@BookId", SqlDbType.Int).Value = request.BookId;
                    if (Convert.ToInt32(dupCmd.ExecuteScalar()) > 0)
                    {
                        tx.Rollback();
                        return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                            ? $"[Chặn giao dịch trùng lặp] Độc giả '{readerName}' ({readerCardId}) đang mượn cuốn sách này và chưa trả."
                            : $"[Duplicate Loan Blocked] Reader '{readerName}' ({readerCardId}) already has an active unreturned loan for this book.");
                    }
                }

                // 3. Lock book row and verify AvailableCopies > 0
                const string bookLockSql = @"
                    SELECT [AvailableCopies], [Title], [BookCode]
                    FROM [dbo].[Books] WITH (UPDLOCK)
                    WHERE [BookId] = @BookId;";

                int availableCopies;
                string bookTitle;
                string bookCode;

                using (SqlCommand bookCmd = new(bookLockSql, conn, tx))
                {
                    bookCmd.Parameters.Add("@BookId", SqlDbType.Int).Value = request.BookId;
                    using SqlDataReader bRdr = bookCmd.ExecuteReader();
                    if (!bRdr.Read())
                    {
                        bRdr.Close();
                        tx.Rollback();
                        return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                            ? "Không tìm thấy đầu sách trong cơ sở dữ liệu."
                            : "Book was not found in the database.");
                    }

                    availableCopies = bRdr.GetInt32(0);
                    bookTitle = bRdr.GetString(1);
                    bookCode = bRdr.GetString(2);
                }

                if (availableCopies <= 0)
                {
                    tx.Rollback();
                    return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                        ? $"Đầu sách '{bookTitle}' ({bookCode}) hiện đã hết bản sao có sẵn (AvailableCopies = 0)."
                        : $"Book '{bookTitle}' ({bookCode}) currently has no available copies (AvailableCopies = 0).");
                }

                // 4. Generate unique RecordCode and insert BorrowRecord + decrement AvailableCopies
                string recordCode = GenerateNextBorrowRecordCode(conn, tx);
                DateTime nowUtc = DateTime.UtcNow;
                DateTime dueUtc = nowUtc.AddDays(request.LoanDays);

                const string insertLoanSql = @"
                    INSERT INTO [dbo].[BorrowRecords]
                        ([RecordCode], [UserId], [ReaderCardId], [BookId], [BorrowedAtUtc], [DueAtUtc], [ReturnedAtUtc], [BorrowStatus], [Notes])
                    VALUES
                        (@RecordCode, @UserId, @ReaderCardId, @BookId, @BorrowedAtUtc, @DueAtUtc, NULL, N'Borrowed', @Notes);";

                using (SqlCommand insCmd = new(insertLoanSql, conn, tx))
                {
                    insCmd.Parameters.Add("@RecordCode", SqlDbType.NVarChar, 30).Value = recordCode;
                    insCmd.Parameters.Add("@UserId", SqlDbType.Int).Value = request.ReaderUserId;
                    insCmd.Parameters.Add("@ReaderCardId", SqlDbType.NVarChar, 30).Value = readerCardId;
                    insCmd.Parameters.Add("@BookId", SqlDbType.Int).Value = request.BookId;
                    insCmd.Parameters.Add("@BorrowedAtUtc", SqlDbType.DateTime2).Value = nowUtc;
                    insCmd.Parameters.Add("@DueAtUtc", SqlDbType.DateTime2).Value = dueUtc;
                    insCmd.Parameters.Add("@Notes", SqlDbType.NVarChar, 400).Value =
                        string.IsNullOrWhiteSpace(request.Notes) ? DBNull.Value : request.Notes.Trim();

                    insCmd.ExecuteNonQuery();
                }

                const string decCopiesSql = @"
                    UPDATE [dbo].[Books]
                    SET [AvailableCopies] = [AvailableCopies] - 1
                    WHERE [BookId] = @BookId;";

                using (SqlCommand decCmd = new(decCopiesSql, conn, tx))
                {
                    decCmd.Parameters.Add("@BookId", SqlDbType.Int).Value = request.BookId;
                    decCmd.ExecuteNonQuery();
                }

                tx.Commit();
                CirculationChanged?.Invoke(this, EventArgs.Empty);
                CatalogChanged?.Invoke(this, EventArgs.Empty);
                ReadersChanged?.Invoke(this, EventArgs.Empty);

                return ServiceOperationResult.Ok(LocalizationService.IsVietnamese
                    ? $"[Lập phiếu mượn thành công] Mã phiếu {recordCode}: Độc giả '{readerName}' ({readerCardId}) mượn '{bookTitle}'. Hạn trả: {dueUtc.ToLocalTime():dd/MM/yyyy}."
                    : $"[Loan Created] Record {recordCode}: Reader '{readerName}' ({readerCardId}) borrowed '{bookTitle}'. Due: {dueUtc.ToLocalTime():dd/MM/yyyy}.");
            }
            catch (SqlException ex)
            {
                AppDiagnosticLogger.LogDatabaseException(nameof(LibraryManagementService), nameof(CreateBorrowTransaction), ex, $"ReaderUserId={request.ReaderUserId}; BookId={request.BookId}");
                return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                    ? $"Lỗi SQL Server khi lập phiếu mượn: {ex.Message}"
                    : $"SQL Server error while creating loan: {ex.Message}");
            }
        }

        public ServiceOperationResult ProcessBookReturn(int borrowRecordId, string? returnNotes = null)
        {
            if (!_authorizationService.TryAuthorizeOperation(AppPermission.ProcessCirculation, "ProcessBookReturn()", out string authMsg))
            {
                return ServiceOperationResult.Fail(authMsg);
            }

            if (borrowRecordId <= 0)
            {
                return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                    ? "Vui lòng chọn một phiếu mượn cần ghi nhận trả sách."
                    : "Please select a loan transaction to record as returned.");
            }

            try
            {
                using SqlConnection conn = new(DatabaseConfig.GetConnectionString());
                conn.Open();
                using SqlTransaction tx = conn.BeginTransaction();

                const string lockLoanSql = @"
                    SELECT [RecordCode], [BookId], [BorrowStatus], [ReturnedAtUtc], ISNULL([Notes], N''), [UserId], [DueAtUtc]
                    FROM [dbo].[BorrowRecords] WITH (UPDLOCK)
                    WHERE [BorrowRecordId] = @BorrowRecordId;";

                string recordCode;
                int bookId;
                string currentStatus;
                bool alreadyReturned;
                string existingNotes;
                int loanUserId;
                DateTime dueUtc;

                using (SqlCommand lockCmd = new(lockLoanSql, conn, tx))
                {
                    lockCmd.Parameters.Add("@BorrowRecordId", SqlDbType.Int).Value = borrowRecordId;
                    using SqlDataReader rdr = lockCmd.ExecuteReader();
                    if (!rdr.Read())
                    {
                        rdr.Close();
                        tx.Rollback();
                        return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                            ? "Không tìm thấy phiếu mượn trong cơ sở dữ liệu."
                            : "Loan transaction was not found in the database.");
                    }

                    recordCode = rdr.GetString(0);
                    bookId = rdr.GetInt32(1);
                    currentStatus = rdr.GetString(2);
                    alreadyReturned = !rdr.IsDBNull(3) || string.Equals(currentStatus, "Returned", StringComparison.OrdinalIgnoreCase);
                    existingNotes = rdr.GetString(4);
                    loanUserId = rdr.GetInt32(5);
                    dueUtc = DateTime.SpecifyKind(rdr.GetDateTime(6), DateTimeKind.Utc);
                }

                // Prevent duplicate return processing & copy count corruption
                if (alreadyReturned)
                {
                    tx.Rollback();
                    return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                        ? $"[Chặn xử lý trùng lặp] Phiếu mượn '{recordCode}' đã được ghi nhận trả sách trước đó. Hệ thống giữ nguyên số lượng bản sao."
                        : $"[Duplicate Return Blocked] Loan '{recordCode}' has already been returned. Book copy counts remain unchanged.");
                }

                string updatedNotes = existingNotes;
                if (!string.IsNullOrWhiteSpace(returnNotes))
                {
                    updatedNotes = string.IsNullOrWhiteSpace(existingNotes)
                        ? returnNotes.Trim()
                        : $"{existingNotes} | {returnNotes.Trim()}";
                }

                DateTime returnTimeUtc = DateTime.UtcNow;

                const string updateLoanSql = @"
                    UPDATE [dbo].[BorrowRecords]
                    SET [ReturnedAtUtc] = @ReturnedAtUtc,
                        [BorrowStatus]  = N'Returned',
                        [Notes]         = @Notes
                    WHERE [BorrowRecordId] = @BorrowRecordId;";

                using (SqlCommand updCmd = new(updateLoanSql, conn, tx))
                {
                    updCmd.Parameters.Add("@ReturnedAtUtc", SqlDbType.DateTime2).Value = returnTimeUtc;
                    updCmd.Parameters.Add("@BorrowRecordId", SqlDbType.Int).Value = borrowRecordId;
                    updCmd.Parameters.Add("@Notes", SqlDbType.NVarChar, 400).Value =
                        string.IsNullOrWhiteSpace(updatedNotes) ? DBNull.Value : updatedNotes;
                    updCmd.ExecuteNonQuery();
                }

                const string incCopiesSql = @"
                    UPDATE [dbo].[Books]
                    SET [AvailableCopies] = CASE
                        WHEN [AvailableCopies] + 1 <= [TotalCopies] THEN [AvailableCopies] + 1
                        ELSE [TotalCopies]
                    END
                    WHERE [BookId] = @BookId;";

                using (SqlCommand incCmd = new(incCopiesSql, conn, tx))
                {
                    incCmd.Parameters.Add("@BookId", SqlDbType.Int).Value = bookId;
                    incCmd.ExecuteNonQuery();
                }

                int staffUserId = _sessionService.CurrentSession?.UserId ?? 1;
                string staffUsername = _sessionService.CurrentSession?.Username ?? "Staff";

                FineManagementService.UpsertOverdueFineOnReturnInTransaction(
                    conn,
                    tx,
                    borrowRecordId,
                    loanUserId,
                    dueUtc,
                    returnTimeUtc,
                    staffUserId,
                    staffUsername);

                tx.Commit();
                CirculationChanged?.Invoke(this, EventArgs.Empty);
                CatalogChanged?.Invoke(this, EventArgs.Empty);
                ReadersChanged?.Invoke(this, EventArgs.Empty);

                return ServiceOperationResult.Ok(LocalizationService.IsVietnamese
                    ? $"[Ghi nhận trả sách thành công] Phiếu mượn '{recordCode}' đã chuyển sang trạng thái Đã trả và cộng lại 1 bản sao vào kho."
                    : $"[Book Return Recorded] Loan '{recordCode}' marked as Returned and 1 copy restored to available inventory.");
            }
            catch (SqlException ex)
            {
                AppDiagnosticLogger.LogDatabaseException(nameof(LibraryManagementService), nameof(ProcessBookReturn), ex, $"BorrowRecordId={borrowRecordId}");
                return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                    ? $"Lỗi SQL Server khi ghi nhận trả sách: {ex.Message}"
                    : $"SQL Server error while recording book return: {ex.Message}");
            }
        }

        // =====================================================================
        // 3.3. LIBRARIAN READER DIRECTORY MANAGEMENT (ManageReaders)
        // =====================================================================

        public IReadOnlyList<ReaderDirectoryItemModel> GetReadersDirectory(
            string? searchKeyword,
            string? statusFilter,
            out string? errorMessage)
        {
            errorMessage = null;

            if (!_authorizationService.TryAuthorizeOperation(AppPermission.ManageReaders, "GetReadersDirectory()", out string authMsg))
            {
                errorMessage = authMsg;
                return [];
            }

            SyncOverdueBorrowRecords();

            string keyword = searchKeyword?.Trim() ?? string.Empty;
            string status = statusFilter?.Trim() ?? "ALL";
            List<ReaderDirectoryItemModel> readers = new();

            try
            {
                using SqlConnection conn = new(DatabaseConfig.GetConnectionString());
                conn.Open();

                const string sql = @"
                    SELECT
                        r.[ReaderId],
                        r.[UserId],
                        r.[ReaderCardId],
                        u.[Username],
                        r.[FullName],
                        r.[Email],
                        ISNULL(r.[PhoneNumber], N''),
                        r.[MembershipStatus],
                        u.[IsActive],
                        r.[RegisteredAtUtc],
                        (SELECT COUNT(1) FROM [dbo].[BorrowRecords] br WHERE br.[UserId] = r.[UserId] AND br.[ReturnedAtUtc] IS NULL) AS [ActiveLoans],
                        (SELECT COUNT(1) FROM [dbo].[BorrowRecords] br WHERE br.[UserId] = r.[UserId] AND br.[ReturnedAtUtc] IS NULL AND br.[BorrowStatus] = N'Overdue') AS [OverdueLoans],
                        (SELECT COUNT(1) FROM [dbo].[BorrowRecords] br WHERE br.[UserId] = r.[UserId]) AS [TotalLoans]
                    FROM [dbo].[Readers] r
                    INNER JOIN [dbo].[Users] u ON u.[UserId] = r.[UserId]
                    WHERE
                        (@Status = N'ALL' OR r.[MembershipStatus] = @Status)
                        AND (
                            @Keyword = N''
                            OR r.[ReaderCardId] LIKE N'%' + @Keyword + N'%'
                            OR u.[Username] LIKE N'%' + @Keyword + N'%'
                            OR r.[FullName] LIKE N'%' + @Keyword + N'%'
                            OR r.[Email] LIKE N'%' + @Keyword + N'%'
                        )
                    ORDER BY r.[ReaderCardId] ASC;";

                using SqlCommand cmd = new(sql, conn);
                cmd.Parameters.Add("@Status", SqlDbType.NVarChar, 30).Value = status;
                cmd.Parameters.Add("@Keyword", SqlDbType.NVarChar, 200).Value = keyword;

                using SqlDataReader reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    DateTime regUtc = DateTime.SpecifyKind(reader.GetDateTime(9), DateTimeKind.Utc);
                    readers.Add(new ReaderDirectoryItemModel
                    {
                        ReaderId = reader.GetInt32(0),
                        UserId = reader.GetInt32(1),
                        ReaderCardId = reader.GetString(2),
                        Username = reader.GetString(3),
                        FullName = reader.GetString(4),
                        Email = reader.GetString(5),
                        PhoneNumber = reader.GetString(6),
                        MembershipStatus = reader.GetString(7),
                        IsAccountActive = reader.GetBoolean(8),
                        RegisteredAtLocal = regUtc.ToLocalTime(),
                        ActiveLoansCount = reader.GetInt32(10),
                        OverdueLoansCount = reader.GetInt32(11),
                        TotalLoansCount = reader.GetInt32(12)
                    });
                }
            }
            catch (SqlException ex)
            {
                AppDiagnosticLogger.LogDatabaseException(nameof(LibraryManagementService), nameof(GetReadersDirectory), ex);
                errorMessage = LocalizationService.IsVietnamese
                    ? $"Lỗi tải danh sách độc giả: {ex.Message}"
                    : $"Error loading readers directory: {ex.Message}";
            }

            return readers;
        }

        public ServiceOperationResult UpdateReaderByStaff(UpdateReaderByStaffRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);

            if (!_authorizationService.TryAuthorizeOperation(AppPermission.ManageReaders, "UpdateReaderByStaff()", out string authMsg))
            {
                return ServiceOperationResult.Fail(authMsg);
            }

            string fullName = request.FullName?.Trim() ?? string.Empty;
            string email = request.Email?.Trim() ?? string.Empty;
            string phone = request.PhoneNumber?.Trim() ?? string.Empty;
            string membershipStatus = request.MembershipStatus?.Trim() ?? "Active";

            if (string.IsNullOrWhiteSpace(fullName) || string.IsNullOrWhiteSpace(email))
            {
                return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                    ? "Họ tên và Địa chỉ Email của độc giả không được để trống."
                    : "Reader Full Name and Email Address are required.");
            }

            if (!EmailPattern.IsMatch(email))
            {
                return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                    ? "Định dạng địa chỉ Email không hợp lệ."
                    : "Invalid email address format.");
            }

            if (membershipStatus is not ("Active" or "Suspended"))
            {
                return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                    ? "Trạng thái thẻ độc giả chỉ nhận giá trị 'Active' hoặc 'Suspended'."
                    : "Membership status must be either 'Active' or 'Suspended'.");
            }

            try
            {
                using SqlConnection conn = new(DatabaseConfig.GetConnectionString());
                conn.Open();
                using SqlTransaction tx = conn.BeginTransaction();

                // Verify target account role: Librarian must never modify an Administrator account
                const string roleCheckSql = "SELECT [Role] FROM [dbo].[Users] WHERE [UserId] = @TargetUserId;";
                using (SqlCommand roleCmd = new(roleCheckSql, conn, tx))
                {
                    roleCmd.Parameters.Add("@TargetUserId", SqlDbType.Int).Value = request.TargetUserId;
                    object? roleObj = roleCmd.ExecuteScalar();
                    if (roleObj is null)
                    {
                        tx.Rollback();
                        return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                            ? "Không tìm thấy tài khoản độc giả cần cập nhật."
                            : "Target reader account was not found.");
                    }

                    string targetRole = Convert.ToString(roleObj) ?? string.Empty;
                    if (string.Equals(targetRole, "Administrator", StringComparison.OrdinalIgnoreCase) &&
                        _sessionService.CurrentSession?.Role != UserRole.Administrator)
                    {
                        tx.Rollback();
                        return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                            ? "[Chặn ở tầng Service] Thủ thư (Librarian) không được phép chỉnh sửa tài khoản Quản trị viên (Administrator)."
                            : "[Blocked by Service Layer] Librarians are not permitted to modify Administrator accounts.");
                    }
                }

                const string updateUsersSql = @"
                    UPDATE [dbo].[Users]
                    SET [FullName] = @FullName,
                        [Email]    = @Email
                    WHERE [UserId] = @TargetUserId;";

                using (SqlCommand uCmd = new(updateUsersSql, conn, tx))
                {
                    uCmd.Parameters.Add("@FullName", SqlDbType.NVarChar, 120).Value = fullName;
                    uCmd.Parameters.Add("@Email", SqlDbType.NVarChar, 150).Value = email;
                    uCmd.Parameters.Add("@TargetUserId", SqlDbType.Int).Value = request.TargetUserId;
                    uCmd.ExecuteNonQuery();
                }

                const string updateReadersSql = @"
                    UPDATE [dbo].[Readers]
                    SET [FullName]         = @FullName,
                        [Email]            = @Email,
                        [PhoneNumber]      = @Phone,
                        [MembershipStatus] = @Status
                    WHERE [UserId] = @TargetUserId;";

                using (SqlCommand rCmd = new(updateReadersSql, conn, tx))
                {
                    rCmd.Parameters.Add("@FullName", SqlDbType.NVarChar, 120).Value = fullName;
                    rCmd.Parameters.Add("@Email", SqlDbType.NVarChar, 150).Value = email;
                    rCmd.Parameters.Add("@Phone", SqlDbType.NVarChar, 25).Value =
                        string.IsNullOrWhiteSpace(phone) ? DBNull.Value : phone;
                    rCmd.Parameters.Add("@Status", SqlDbType.NVarChar, 30).Value = membershipStatus;
                    rCmd.Parameters.Add("@TargetUserId", SqlDbType.Int).Value = request.TargetUserId;
                    rCmd.ExecuteNonQuery();
                }

                tx.Commit();
                ReadersChanged?.Invoke(this, EventArgs.Empty);

                return ServiceOperationResult.Ok(LocalizationService.IsVietnamese
                    ? $"[Quản lý Độc giả] Đã cập nhật thông tin hồ sơ độc giả '{fullName}' (Trạng thái thẻ: {membershipStatus})."
                    : $"[Reader Management] Updated profile for reader '{fullName}' (Card status: {membershipStatus}).");
            }
            catch (SqlException ex)
            {
                AppDiagnosticLogger.LogDatabaseException(nameof(LibraryManagementService), nameof(UpdateReaderByStaff), ex, $"TargetUserId={request.TargetUserId}");
                return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                    ? $"Lỗi SQL Server khi cập nhật độc giả: {ex.Message}"
                    : $"SQL Server error while updating reader: {ex.Message}");
            }
        }

        // =====================================================================
        // 4.1. ADMINISTRATOR ACCOUNT & ROLE MANAGEMENT (ManageSystemSettings)
        // =====================================================================

        public IReadOnlyList<ManagedUserAccountModel> GetManagedUsers(
            string? searchKeyword,
            string? roleFilter,
            out string? errorMessage)
        {
            errorMessage = null;

            if (!_authorizationService.TryAuthorizeOperation(AppPermission.ManageSystemSettings, "GetManagedUsers()", out string authMsg))
            {
                errorMessage = authMsg;
                return [];
            }

            string keyword = searchKeyword?.Trim() ?? string.Empty;
            string role = roleFilter?.Trim() ?? "ALL";
            List<ManagedUserAccountModel> users = new();

            try
            {
                using SqlConnection conn = new(DatabaseConfig.GetConnectionString());
                conn.Open();

                const string sql = @"
                    SELECT
                        u.[UserId],
                        u.[Username],
                        u.[FullName],
                        u.[Email],
                        u.[Role],
                        ISNULL(r.[ReaderCardId], N'—'),
                        u.[IsActive],
                        u.[FailedLoginAttempts],
                        u.[LockoutEndUtc],
                        u.[CreatedAtUtc]
                    FROM [dbo].[Users] u
                    LEFT JOIN [dbo].[Readers] r ON r.[UserId] = u.[UserId]
                    WHERE
                        (@Role = N'ALL' OR u.[Role] = @Role)
                        AND (
                            @Keyword = N''
                            OR u.[Username] LIKE N'%' + @Keyword + N'%'
                            OR u.[FullName] LIKE N'%' + @Keyword + N'%'
                            OR u.[Email] LIKE N'%' + @Keyword + N'%'
                            OR r.[ReaderCardId] LIKE N'%' + @Keyword + N'%'
                        )
                    ORDER BY u.[UserId] ASC;";

                using SqlCommand cmd = new(sql, conn);
                cmd.Parameters.Add("@Role", SqlDbType.NVarChar, 30).Value = role;
                cmd.Parameters.Add("@Keyword", SqlDbType.NVarChar, 200).Value = keyword;

                using SqlDataReader reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    DateTime? lockoutLocal = null;
                    if (!reader.IsDBNull(8))
                    {
                        DateTime lockoutUtc = DateTime.SpecifyKind(reader.GetDateTime(8), DateTimeKind.Utc);
                        lockoutLocal = lockoutUtc.ToLocalTime();
                    }

                    DateTime createdUtc = DateTime.SpecifyKind(reader.GetDateTime(9), DateTimeKind.Utc);

                    users.Add(new ManagedUserAccountModel
                    {
                        UserId = reader.GetInt32(0),
                        Username = reader.GetString(1),
                        FullName = reader.GetString(2),
                        Email = reader.GetString(3),
                        Role = reader.GetString(4),
                        ReaderCardId = reader.GetString(5),
                        IsActive = reader.GetBoolean(6),
                        FailedLoginAttempts = reader.GetInt32(7),
                        LockoutEndLocal = lockoutLocal,
                        CreatedAtLocal = createdUtc.ToLocalTime()
                    });
                }
            }
            catch (SqlException ex)
            {
                AppDiagnosticLogger.LogDatabaseException(nameof(LibraryManagementService), nameof(GetManagedUsers), ex);
                errorMessage = LocalizationService.IsVietnamese
                    ? $"Lỗi tải danh sách tài khoản hệ thống: {ex.Message}"
                    : $"Error loading system accounts: {ex.Message}";
            }

            return users;
        }

        public ServiceOperationResult CreateManagedUserAccount(CreateManagedUserRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);

            if (!_authorizationService.TryAuthorizeOperation(AppPermission.ManageSystemSettings, "CreateManagedUserAccount()", out string authMsg))
            {
                return ServiceOperationResult.Fail(authMsg);
            }

            string username = request.Username?.Trim() ?? string.Empty;
            string fullName = request.FullName?.Trim() ?? string.Empty;
            string email = request.Email?.Trim() ?? string.Empty;
            string phone = request.PhoneNumber?.Trim() ?? string.Empty;
            string role = request.Role?.Trim() ?? "Librarian";

            if (string.IsNullOrWhiteSpace(username) ||
                string.IsNullOrWhiteSpace(fullName) ||
                string.IsNullOrWhiteSpace(email) ||
                string.IsNullOrWhiteSpace(request.Password))
            {
                return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                    ? "Vui lòng nhập đầy đủ Tên đăng nhập, Họ tên, Email và Mật khẩu khởi tạo."
                    : "Please provide Username, Full Name, Email, and Initial Password.");
            }

            if (!UsernamePattern.IsMatch(username))
            {
                return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                    ? "Tên đăng nhập phải từ 4–50 ký tự chữ/số (a-z, 0-9, ., _, -)."
                    : "Username must be 4–50 alphanumeric characters (a-z, 0-9, ., _, -).");
            }

            if (!EmailPattern.IsMatch(email))
            {
                return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                    ? "Định dạng địa chỉ Email không hợp lệ."
                    : "Invalid Email Address format.");
            }

            if (role is not ("Reader" or "Librarian" or "Administrator"))
            {
                return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                    ? "Vai trò tài khoản chỉ nhận giá trị Reader, Librarian hoặc Administrator."
                    : "Role must be Reader, Librarian, or Administrator.");
            }

            if (!IsStrongPassword(request.Password))
            {
                return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                    ? "Mật khẩu phải có tối thiểu 8 ký tự, bao gồm chữ hoa, chữ thường, chữ số và ký tự đặc biệt."
                    : "Password must be at least 8 characters and include uppercase, lowercase, digits, and special characters.");
            }

            if (!string.Equals(request.Password, request.ConfirmPassword, StringComparison.Ordinal))
            {
                return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                    ? "Mật khẩu xác nhận không khớp."
                    : "Password confirmation does not match.");
            }

            try
            {
                using SqlConnection conn = new(DatabaseConfig.GetConnectionString());
                conn.Open();
                using SqlTransaction tx = conn.BeginTransaction();

                const string checkUserSql = "SELECT COUNT(1) FROM [dbo].[Users] WHERE [Username] = @Username;";
                using (SqlCommand chkCmd = new(checkUserSql, conn, tx))
                {
                    chkCmd.Parameters.Add("@Username", SqlDbType.NVarChar, 50).Value = username;
                    if (Convert.ToInt32(chkCmd.ExecuteScalar()) > 0)
                    {
                        tx.Rollback();
                        return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                            ? $"Tên đăng nhập '{username}' đã tồn tại trong hệ thống."
                            : $"Username '{username}' is already taken.");
                    }
                }

                (string hash, string salt) = PasswordHasher.HashPassword(request.Password);

                const string insertUserSql = @"
                    INSERT INTO [dbo].[Users]
                        ([Username], [FullName], [Email], [Role], [PasswordHash], [PasswordSalt], [IsActive], [FailedLoginAttempts], [LockoutEndUtc])
                    OUTPUT INSERTED.[UserId]
                    VALUES
                        (@Username, @FullName, @Email, @Role, @Hash, @Salt, 1, 0, NULL);";

                int newUserId;
                using (SqlCommand insCmd = new(insertUserSql, conn, tx))
                {
                    insCmd.Parameters.Add("@Username", SqlDbType.NVarChar, 50).Value = username;
                    insCmd.Parameters.Add("@FullName", SqlDbType.NVarChar, 120).Value = fullName;
                    insCmd.Parameters.Add("@Email", SqlDbType.NVarChar, 150).Value = email;
                    insCmd.Parameters.Add("@Role", SqlDbType.NVarChar, 30).Value = role;
                    insCmd.Parameters.Add("@Hash", SqlDbType.NVarChar, 128).Value = hash;
                    insCmd.Parameters.Add("@Salt", SqlDbType.NVarChar, 128).Value = salt;
                    newUserId = Convert.ToInt32(insCmd.ExecuteScalar());
                }

                string cardNotice = string.Empty;
                if (string.Equals(role, "Reader", StringComparison.OrdinalIgnoreCase))
                {
                    string cardId = EnsureReaderCardExists(conn, tx, newUserId, fullName, email, phone);
                    cardNotice = $" (Card: {cardId})";
                }

                tx.Commit();
                AccountsChanged?.Invoke(this, EventArgs.Empty);
                ReadersChanged?.Invoke(this, EventArgs.Empty);

                return ServiceOperationResult.Ok(LocalizationService.IsVietnamese
                    ? $"[Quản trị Tài khoản] Đã tạo tài khoản '{username}' với vai trò '{role}'{cardNotice}."
                    : $"[Account Administration] Created account '{username}' with role '{role}'{cardNotice}.");
            }
            catch (SqlException ex)
            {
                AppDiagnosticLogger.LogDatabaseException(nameof(LibraryManagementService), nameof(CreateManagedUserAccount), ex, $"Username={username}; Role={role}");
                return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                    ? $"Lỗi SQL Server khi tạo tài khoản: {ex.Message}"
                    : $"SQL Server error while creating account: {ex.Message}");
            }
        }

        public ServiceOperationResult UpdateUserRole(int targetUserId, string newRole)
        {
            if (!_authorizationService.TryAuthorizeOperation(AppPermission.ManageSystemSettings, "UpdateUserRole()", out string authMsg))
            {
                return ServiceOperationResult.Fail(authMsg);
            }

            string normalizedRole = newRole?.Trim() ?? string.Empty;
            if (normalizedRole is not ("Reader" or "Librarian" or "Administrator"))
            {
                return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                    ? "Vai trò mới không hợp lệ (Chỉ chấp nhận Reader, Librarian, Administrator)."
                    : "Invalid target role (Must be Reader, Librarian, or Administrator).");
            }

            int currentAdminUserId = _sessionService.CurrentSession!.UserId;
            if (targetUserId == currentAdminUserId && !string.Equals(normalizedRole, "Administrator", StringComparison.OrdinalIgnoreCase))
            {
                return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                    ? "[Bảo vệ Quản trị viên] Bạn không thể tự hạ quyền tài khoản Quản trị viên đang đăng nhập của chính mình."
                    : "[Admin Safeguard] You cannot demote your own currently signed-in Administrator account.");
            }

            try
            {
                using SqlConnection conn = new(DatabaseConfig.GetConnectionString());
                conn.Open();
                using SqlTransaction tx = conn.BeginTransaction();

                const string getTargetSql = @"
                    SELECT [Username], [FullName], [Email], [Role], [IsActive]
                    FROM [dbo].[Users] WITH (UPDLOCK)
                    WHERE [UserId] = @TargetUserId;";

                string username;
                string fullName;
                string email;
                string currentRole;
                bool isActive;

                using (SqlCommand getCmd = new(getTargetSql, conn, tx))
                {
                    getCmd.Parameters.Add("@TargetUserId", SqlDbType.Int).Value = targetUserId;
                    using SqlDataReader rdr = getCmd.ExecuteReader();
                    if (!rdr.Read())
                    {
                        rdr.Close();
                        tx.Rollback();
                        return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                            ? "Không tìm thấy tài khoản cần phân quyền."
                            : "Target user account was not found.");
                    }

                    username = rdr.GetString(0);
                    fullName = rdr.GetString(1);
                    email = rdr.GetString(2);
                    currentRole = rdr.GetString(3);
                    isActive = rdr.GetBoolean(4);
                }

                // Last Active Administrator Protection
                if (string.Equals(currentRole, "Administrator", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(normalizedRole, "Administrator", StringComparison.OrdinalIgnoreCase))
                {
                    int otherActiveAdmins = CountOtherActiveAdministrators(conn, tx, targetUserId);
                    if (otherActiveAdmins == 0)
                    {
                        tx.Rollback();
                        return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                            ? $"[Bảo vệ Quản trị viên Cuối cùng] Không thể chuyển vai trò của '{username}' vì đây là tài khoản Administrator đang hoạt động cuối cùng trong hệ thống."
                            : $"[Last Administrator Safeguard] Cannot demote '{username}' because it is the last active Administrator account in the system.");
                    }
                }

                const string updateRoleSql = "UPDATE [dbo].[Users] SET [Role] = @NewRole WHERE [UserId] = @TargetUserId;";
                using (SqlCommand updCmd = new(updateRoleSql, conn, tx))
                {
                    updCmd.Parameters.Add("@NewRole", SqlDbType.NVarChar, 30).Value = normalizedRole;
                    updCmd.Parameters.Add("@TargetUserId", SqlDbType.Int).Value = targetUserId;
                    updCmd.ExecuteNonQuery();
                }

                if (string.Equals(normalizedRole, "Reader", StringComparison.OrdinalIgnoreCase))
                {
                    EnsureReaderCardExists(conn, tx, targetUserId, fullName, email, null);
                }

                tx.Commit();
                AccountsChanged?.Invoke(this, EventArgs.Empty);
                ReadersChanged?.Invoke(this, EventArgs.Empty);

                return ServiceOperationResult.Ok(LocalizationService.IsVietnamese
                    ? $"[Phân quyền Tài khoản] Đã cập nhật vai trò của '{username}' từ '{currentRole}' sang '{normalizedRole}'."
                    : $"[Role Updated] Changed role of '{username}' from '{currentRole}' to '{normalizedRole}'.");
            }
            catch (SqlException ex)
            {
                AppDiagnosticLogger.LogDatabaseException(nameof(LibraryManagementService), nameof(UpdateUserRole), ex, $"TargetUserId={targetUserId}; NewRole={normalizedRole}");
                return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                    ? $"Lỗi SQL Server khi đổi vai trò: {ex.Message}"
                    : $"SQL Server error while updating role: {ex.Message}");
            }
        }

        public ServiceOperationResult SetUserActiveStatus(int targetUserId, bool newActiveStatus)
        {
            if (!_authorizationService.TryAuthorizeOperation(AppPermission.ManageSystemSettings, "SetUserActiveStatus()", out string authMsg))
            {
                return ServiceOperationResult.Fail(authMsg);
            }

            int currentAdminUserId = _sessionService.CurrentSession!.UserId;
            if (!newActiveStatus && targetUserId == currentAdminUserId)
            {
                return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                    ? "[Bảo vệ Quản trị viên] Bạn không thể tự vô hiệu hóa tài khoản đang đăng nhập của chính mình."
                    : "[Admin Safeguard] You cannot deactivate your own currently signed-in account.");
            }

            try
            {
                using SqlConnection conn = new(DatabaseConfig.GetConnectionString());
                conn.Open();
                using SqlTransaction tx = conn.BeginTransaction();

                const string getTargetSql = @"
                    SELECT [Username], [Role]
                    FROM [dbo].[Users] WITH (UPDLOCK)
                    WHERE [UserId] = @TargetUserId;";

                string username;
                string role;

                using (SqlCommand getCmd = new(getTargetSql, conn, tx))
                {
                    getCmd.Parameters.Add("@TargetUserId", SqlDbType.Int).Value = targetUserId;
                    using SqlDataReader rdr = getCmd.ExecuteReader();
                    if (!rdr.Read())
                    {
                        rdr.Close();
                        tx.Rollback();
                        return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                            ? "Không tìm thấy tài khoản."
                            : "User account not found.");
                    }

                    username = rdr.GetString(0);
                    role = rdr.GetString(1);
                }

                // Prevent deactivating the last active Administrator
                if (!newActiveStatus && string.Equals(role, "Administrator", StringComparison.OrdinalIgnoreCase))
                {
                    int otherActiveAdmins = CountOtherActiveAdministrators(conn, tx, targetUserId);
                    if (otherActiveAdmins == 0)
                    {
                        tx.Rollback();
                        return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                            ? $"[Bảo vệ Quản trị viên Cuối cùng] Không thể vô hiệu hóa '{username}' vì đây là tài khoản Administrator đang hoạt động cuối cùng."
                            : $"[Last Administrator Safeguard] Cannot deactivate '{username}' because it is the last active Administrator.");
                    }
                }

                const string updSql = "UPDATE [dbo].[Users] SET [IsActive] = @IsActive WHERE [UserId] = @TargetUserId;";
                using (SqlCommand updCmd = new(updSql, conn, tx))
                {
                    updCmd.Parameters.Add("@IsActive", SqlDbType.Bit).Value = newActiveStatus;
                    updCmd.Parameters.Add("@TargetUserId", SqlDbType.Int).Value = targetUserId;
                    updCmd.ExecuteNonQuery();
                }

                tx.Commit();
                AccountsChanged?.Invoke(this, EventArgs.Empty);
                ReadersChanged?.Invoke(this, EventArgs.Empty);

                string stateTextVi = newActiveStatus ? "Kích hoạt lại" : "Vô hiệu hóa";
                string stateTextEn = newActiveStatus ? "Activated" : "Deactivated";

                return ServiceOperationResult.Ok(LocalizationService.IsVietnamese
                    ? $"[Quản trị Tài khoản] Đã {stateTextVi.ToLowerInvariant()} tài khoản '{username}'."
                    : $"[Account Administration] {stateTextEn} account '{username}'.");
            }
            catch (SqlException ex)
            {
                AppDiagnosticLogger.LogDatabaseException(nameof(LibraryManagementService), nameof(SetUserActiveStatus), ex, $"TargetUserId={targetUserId}; IsActive={newActiveStatus}");
                return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                    ? $"Lỗi SQL Server khi cập nhật trạng thái tài khoản: {ex.Message}"
                    : $"SQL Server error while updating account status: {ex.Message}");
            }
        }

        public ServiceOperationResult UnlockUserAccount(int targetUserId)
        {
            if (!_authorizationService.TryAuthorizeOperation(AppPermission.ManageSystemSettings, "UnlockUserAccount()", out string authMsg))
            {
                return ServiceOperationResult.Fail(authMsg);
            }

            try
            {
                using SqlConnection conn = new(DatabaseConfig.GetConnectionString());
                conn.Open();

                const string sql = @"
                    UPDATE [dbo].[Users]
                    SET [FailedLoginAttempts] = 0,
                        [LockoutEndUtc]       = NULL
                    WHERE [UserId] = @TargetUserId;";

                using SqlCommand cmd = new(sql, conn);
                cmd.Parameters.Add("@TargetUserId", SqlDbType.Int).Value = targetUserId;
                int rows = cmd.ExecuteNonQuery();
                if (rows == 0)
                {
                    return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                        ? "Không tìm thấy tài khoản cần mở khóa."
                        : "Target user account not found.");
                }

                AccountsChanged?.Invoke(this, EventArgs.Empty);

                return ServiceOperationResult.Ok(LocalizationService.IsVietnamese
                    ? $"[Quản trị Tài khoản] Đã đặt lại bộ đếm đăng nhập sai và gỡ khóa 15 phút cho UserId={targetUserId}."
                    : $"[Account Administration] Reset failed login attempts and cleared 15-minute lockout for UserId={targetUserId}.");
            }
            catch (SqlException ex)
            {
                AppDiagnosticLogger.LogDatabaseException(nameof(LibraryManagementService), nameof(UnlockUserAccount), ex, $"TargetUserId={targetUserId}");
                return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                    ? $"Lỗi SQL Server khi mở khóa tài khoản: {ex.Message}"
                    : $"SQL Server error while unlocking account: {ex.Message}");
            }
        }

        public ServiceOperationResult ResetUserPasswordByAdmin(int targetUserId, string newPassword, string confirmPassword)
        {
            if (!_authorizationService.TryAuthorizeOperation(AppPermission.ManageSystemSettings, "ResetUserPasswordByAdmin()", out string authMsg))
            {
                return ServiceOperationResult.Fail(authMsg);
            }

            if (targetUserId <= 0)
            {
                return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                    ? "Vui lòng chọn tài khoản cần đặt lại mật khẩu."
                    : "Please select an account to reset password.");
            }

            if (!IsStrongPassword(newPassword))
            {
                return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                    ? "Mật khẩu mới phải có tối thiểu 8 ký tự, bao gồm chữ hoa, chữ thường, chữ số và ký tự đặc biệt (Không dùng mật khẩu mặc định cố định)."
                    : "New password must be at least 8 characters and include uppercase, lowercase, digits, and special characters (No hardcoded default passwords).");
            }

            if (!string.Equals(newPassword, confirmPassword, StringComparison.Ordinal))
            {
                return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                    ? "Mật khẩu xác nhận không khớp."
                    : "Password confirmation does not match.");
            }

            try
            {
                (string newHash, string newSalt) = PasswordHasher.HashPassword(newPassword);

                using SqlConnection conn = new(DatabaseConfig.GetConnectionString());
                conn.Open();

                const string sql = @"
                    UPDATE [dbo].[Users]
                    SET [PasswordHash]        = @Hash,
                        [PasswordSalt]        = @Salt,
                        [FailedLoginAttempts] = 0,
                        [LockoutEndUtc]       = NULL
                    WHERE [UserId] = @TargetUserId;";

                using SqlCommand cmd = new(sql, conn);
                cmd.Parameters.Add("@Hash", SqlDbType.NVarChar, 128).Value = newHash;
                cmd.Parameters.Add("@Salt", SqlDbType.NVarChar, 128).Value = newSalt;
                cmd.Parameters.Add("@TargetUserId", SqlDbType.Int).Value = targetUserId;

                int rows = cmd.ExecuteNonQuery();
                if (rows == 0)
                {
                    return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                        ? "Không tìm thấy tài khoản cần đặt lại mật khẩu."
                        : "Target account was not found.");
                }

                AccountsChanged?.Invoke(this, EventArgs.Empty);

                return ServiceOperationResult.Ok(LocalizationService.IsVietnamese
                    ? $"[Bảo mật PBKDF2-SHA256] Đã đặt lại mật khẩu mới (băm kèm Salt 16-byte ngẫu nhiên) và mở khóa cho UserId={targetUserId}."
                    : $"[PBKDF2-SHA256 Security] Successfully hashed and updated password with a fresh 16-byte random salt for UserId={targetUserId}.");
            }
            catch (SqlException ex)
            {
                AppDiagnosticLogger.LogDatabaseException(nameof(LibraryManagementService), nameof(ResetUserPasswordByAdmin), ex, $"TargetUserId={targetUserId}");
                return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                    ? $"Lỗi SQL Server khi đặt lại mật khẩu: {ex.Message}"
                    : $"SQL Server error while resetting password: {ex.Message}");
            }
        }

        // =====================================================================
        // 4.2. LIVE SQL SERVER DASHBOARD & STATISTICS
        // =====================================================================

        public LibraryDatabaseStatisticsModel GetLiveDatabaseStatistics()
        {
            if (!_authorizationService.HasPermission(AppPermission.ViewDashboard))
            {
                return new LibraryDatabaseStatisticsModel { IsConnectedToSql = false };
            }

            SyncOverdueBorrowRecords();

            try
            {
                using SqlConnection conn = new(DatabaseConfig.GetConnectionString());
                conn.Open();

                const string summarySql = @"
                    SELECT
                        (SELECT COUNT(1) FROM [dbo].[Books]) AS [TotalTitles],
                        (SELECT ISNULL(SUM([TotalCopies]), 0) FROM [dbo].[Books]) AS [TotalCopies],
                        (SELECT ISNULL(SUM([AvailableCopies]), 0) FROM [dbo].[Books]) AS [AvailableCopies],
                        (SELECT COUNT(1) FROM [dbo].[Readers]) AS [TotalReaders],
                        (SELECT COUNT(1) FROM [dbo].[Users]) AS [TotalUsers],
                        (SELECT COUNT(1) FROM [dbo].[BorrowRecords] WHERE [ReturnedAtUtc] IS NULL) AS [ActiveBorrowed],
                        (SELECT COUNT(1) FROM [dbo].[BorrowRecords] WHERE [ReturnedAtUtc] IS NULL AND ([BorrowStatus] = N'Overdue' OR [DueAtUtc] < SYSUTCDATETIME())) AS [OverdueCount],
                        (SELECT COUNT(1) FROM [dbo].[BorrowRecords] WHERE [ReturnedAtUtc] IS NOT NULL OR [BorrowStatus] = N'Returned') AS [ReturnedCount];";

                int totalTitles = 0, totalCopies = 0, availCopies = 0, totalReaders = 0, totalUsers = 0, activeBorrowed = 0, overdueCount = 0, returnedCount = 0;

                using (SqlCommand sumCmd = new(summarySql, conn))
                using (SqlDataReader rdr = sumCmd.ExecuteReader())
                {
                    if (rdr.Read())
                    {
                        totalTitles = rdr.GetInt32(0);
                        totalCopies = rdr.GetInt32(1);
                        availCopies = rdr.GetInt32(2);
                        totalReaders = rdr.GetInt32(3);
                        totalUsers = rdr.GetInt32(4);
                        activeBorrowed = rdr.GetInt32(5);
                        overdueCount = rdr.GetInt32(6);
                        returnedCount = rdr.GetInt32(7);
                    }
                }

                List<CategoryStatRowModel> categories = new();
                const string catSql = @"
                    SELECT
                        [Category],
                        COUNT(1) AS [TitleCount],
                        ISNULL(SUM([TotalCopies]), 0) AS [TotalCopiesSum],
                        ISNULL(SUM([AvailableCopies]), 0) AS [AvailCopiesSum]
                    FROM [dbo].[Books]
                    GROUP BY [Category]
                    ORDER BY [TitleCount] DESC, [Category] ASC;";

                using (SqlCommand catCmd = new(catSql, conn))
                using (SqlDataReader catRdr = catCmd.ExecuteReader())
                {
                    while (catRdr.Read())
                    {
                        categories.Add(new CategoryStatRowModel
                        {
                            CategoryName = catRdr.GetString(0),
                            TitleCount = catRdr.GetInt32(1),
                            TotalCopiesSum = catRdr.GetInt32(2),
                            AvailableCopiesSum = catRdr.GetInt32(3)
                        });
                    }
                }

                List<PeriodLoanStatRowModel> monthlyStats = new();
                const string monthSql = @"
                    SELECT
                        FORMAT([BorrowedAtUtc], 'yyyy-MM') AS [PeriodMonth],
                        COUNT(1) AS [TotalLoans],
                        SUM(CASE WHEN [ReturnedAtUtc] IS NULL THEN 1 ELSE 0 END) AS [ActiveOrOverdue],
                        SUM(CASE WHEN [ReturnedAtUtc] IS NOT NULL OR [BorrowStatus] = N'Returned' THEN 1 ELSE 0 END) AS [ReturnedLoans]
                    FROM [dbo].[BorrowRecords]
                    GROUP BY FORMAT([BorrowedAtUtc], 'yyyy-MM')
                    ORDER BY [PeriodMonth] DESC;";

                using (SqlCommand mCmd = new(monthSql, conn))
                using (SqlDataReader mRdr = mCmd.ExecuteReader())
                {
                    while (mRdr.Read())
                    {
                        monthlyStats.Add(new PeriodLoanStatRowModel
                        {
                            PeriodLabel = mRdr.GetString(0),
                            TotalLoansInPeriod = mRdr.GetInt32(1),
                            ActiveOrOverdueInPeriod = mRdr.GetInt32(2),
                            ReturnedInPeriod = mRdr.GetInt32(3)
                        });
                    }
                }

                List<StaffBorrowRecordItemModel> recentLoans = new();
                const string recentSql = @"
                    SELECT TOP (10)
                        br.[BorrowRecordId],
                        br.[RecordCode],
                        br.[UserId],
                        u.[Username],
                        u.[FullName],
                        br.[ReaderCardId],
                        br.[BookId],
                        b.[BookCode],
                        b.[Title],
                        b.[Author],
                        br.[BorrowedAtUtc],
                        br.[DueAtUtc],
                        br.[ReturnedAtUtc],
                        br.[BorrowStatus],
                        ISNULL(br.[Notes], N'')
                    FROM [dbo].[BorrowRecords] br
                    INNER JOIN [dbo].[Books] b ON b.[BookId] = br.[BookId]
                    INNER JOIN [dbo].[Users] u ON u.[UserId] = br.[UserId]
                    ORDER BY br.[BorrowedAtUtc] DESC, br.[BorrowRecordId] DESC;";

                using (SqlCommand recCmd = new(recentSql, conn))
                using (SqlDataReader recRdr = recCmd.ExecuteReader())
                {
                    while (recRdr.Read())
                    {
                        recentLoans.Add(MapStaffBorrowRecord(recRdr));
                    }
                }

                return new LibraryDatabaseStatisticsModel
                {
                    IsConnectedToSql = true,
                    TotalBookTitles = totalTitles,
                    TotalBookCopies = totalCopies,
                    AvailableBookCopies = availCopies,
                    TotalRegisteredReaders = totalReaders,
                    TotalUsersCount = totalUsers,
                    ActiveBorrowedLoans = activeBorrowed,
                    OverdueLoans = overdueCount,
                    ReturnedLoans = returnedCount,
                    CategoryBreakdown = categories,
                    MonthlyLoanBreakdown = monthlyStats,
                    RecentLoanTransactions = recentLoans
                };
            }
            catch
            {
                return new LibraryDatabaseStatisticsModel { IsConnectedToSql = false };
            }
        }

        // =====================================================================
        // PRIVATE HELPERS
        // =====================================================================

        private static bool TryValidateBookInput(
            BookUpsertRequest request,
            out string bookCode,
            out string title,
            out string author,
            out string category,
            out int? publishYear,
            out int totalCopies,
            out string validationError)
        {
            bookCode = request.BookCode?.Trim().ToUpperInvariant() ?? string.Empty;
            title = request.Title?.Trim() ?? string.Empty;
            author = request.Author?.Trim() ?? string.Empty;
            category = request.Category?.Trim() ?? string.Empty;
            publishYear = null;
            totalCopies = 0;
            validationError = string.Empty;

            if (string.IsNullOrWhiteSpace(bookCode) ||
                string.IsNullOrWhiteSpace(title) ||
                string.IsNullOrWhiteSpace(author) ||
                string.IsNullOrWhiteSpace(category))
            {
                validationError = LocalizationService.IsVietnamese
                    ? "Vui lòng nhập đầy đủ Mã sách, Tên sách, Tác giả và Thể loại."
                    : "Book Code, Title, Author, and Category are required.";
                return false;
            }

            if (bookCode.Length < 3 || bookCode.Length > 30)
            {
                validationError = LocalizationService.IsVietnamese
                    ? "Mã sách phải có độ dài từ 3 đến 30 ký tự."
                    : "Book Code must be between 3 and 30 characters.";
                return false;
            }

            if (!int.TryParse(request.TotalCopiesText?.Trim(), out totalCopies) || totalCopies < 0 || totalCopies > 1000)
            {
                validationError = LocalizationService.IsVietnamese
                    ? "Tổng số bản sao (Total Copies) phải là số nguyên từ 0 đến 1000."
                    : "Total Copies must be an integer between 0 and 1000.";
                return false;
            }

            if (!string.IsNullOrWhiteSpace(request.PublishYearText))
            {
                if (!int.TryParse(request.PublishYearText.Trim(), out int parsedYear) || parsedYear < 1450 || parsedYear > 2100)
                {
                    validationError = LocalizationService.IsVietnamese
                        ? "Năm xuất bản không hợp lệ (Phải nằm trong khoảng 1450 – 2100)."
                        : "Publish Year is invalid (Must be between 1450 and 2100).";
                    return false;
                }
                publishYear = parsedYear;
            }

            return true;
        }

        private static string GenerateNextBorrowRecordCode(SqlConnection conn, SqlTransaction tx)
        {
            const string sql = "SELECT ISNULL(MAX([BorrowRecordId]), 0) + 1 FROM [dbo].[BorrowRecords] WITH (TABLOCKX);";
            using SqlCommand cmd = new(sql, conn, tx);
            int nextSeq = Convert.ToInt32(cmd.ExecuteScalar());
            return $"BR-{DateTime.UtcNow.Year}-{nextSeq:D4}";
        }

        private static int CountOtherActiveAdministrators(SqlConnection conn, SqlTransaction tx, int excludedUserId)
        {
            const string sql = @"
                SELECT COUNT(1)
                FROM [dbo].[Users]
                WHERE [Role] = N'Administrator'
                  AND [IsActive] = 1
                  AND [UserId] <> @ExcludedUserId;";

            using SqlCommand cmd = new(sql, conn, tx);
            cmd.Parameters.Add("@ExcludedUserId", SqlDbType.Int).Value = excludedUserId;
            return Convert.ToInt32(cmd.ExecuteScalar());
        }

        private static string EnsureReaderCardExists(
            SqlConnection conn,
            SqlTransaction tx,
            int userId,
            string fullName,
            string email,
            string? phone)
        {
            const string existingSql = "SELECT [ReaderCardId] FROM [dbo].[Readers] WHERE [UserId] = @UserId;";
            using (SqlCommand checkCmd = new(existingSql, conn, tx))
            {
                checkCmd.Parameters.Add("@UserId", SqlDbType.Int).Value = userId;
                object? existingCard = checkCmd.ExecuteScalar();
                if (existingCard is not null && existingCard != DBNull.Value)
                {
                    return Convert.ToString(existingCard)!;
                }
            }

            string cardId = $"RD-{DateTime.UtcNow.Year}-{userId:D4}";
            const string insertCardSql = @"
                INSERT INTO [dbo].[Readers]
                    ([UserId], [ReaderCardId], [FullName], [Email], [PhoneNumber], [MembershipStatus])
                VALUES
                    (@UserId, @CardId, @FullName, @Email, @Phone, N'Active');";

            using SqlCommand insCmd = new(insertCardSql, conn, tx);
            insCmd.Parameters.Add("@UserId", SqlDbType.Int).Value = userId;
            insCmd.Parameters.Add("@CardId", SqlDbType.NVarChar, 30).Value = cardId;
            insCmd.Parameters.Add("@FullName", SqlDbType.NVarChar, 120).Value = fullName;
            insCmd.Parameters.Add("@Email", SqlDbType.NVarChar, 150).Value = email;
            insCmd.Parameters.Add("@Phone", SqlDbType.NVarChar, 25).Value =
                string.IsNullOrWhiteSpace(phone) ? DBNull.Value : phone.Trim();
            insCmd.ExecuteNonQuery();

            return cardId;
        }

        private static bool IsStrongPassword(string password)
        {
            return PasswordHasher.IsStrongPassword(password);
        }

        private static StaffBorrowRecordItemModel MapStaffBorrowRecord(SqlDataReader reader)
        {
            DateTime borrowedUtc = DateTime.SpecifyKind(reader.GetDateTime(10), DateTimeKind.Utc);
            DateTime dueUtc = DateTime.SpecifyKind(reader.GetDateTime(11), DateTimeKind.Utc);
            DateTime? returnedLocal = null;

            if (!reader.IsDBNull(12))
            {
                DateTime returnedUtc = DateTime.SpecifyKind(reader.GetDateTime(12), DateTimeKind.Utc);
                returnedLocal = returnedUtc.ToLocalTime();
            }

            return new StaffBorrowRecordItemModel
            {
                BorrowRecordId = reader.GetInt32(0),
                RecordCode = reader.GetString(1),
                UserId = reader.GetInt32(2),
                Username = reader.GetString(3),
                ReaderFullName = reader.GetString(4),
                ReaderCardId = reader.GetString(5),
                BookId = reader.GetInt32(6),
                BookCode = reader.GetString(7),
                BookTitle = reader.GetString(8),
                BookAuthor = reader.GetString(9),
                BorrowedAtLocal = borrowedUtc.ToLocalTime(),
                DueAtLocal = dueUtc.ToLocalTime(),
                ReturnedAtLocal = returnedLocal,
                BorrowStatus = reader.GetString(13),
                Notes = reader.GetString(14)
            };
        }
    }
}

