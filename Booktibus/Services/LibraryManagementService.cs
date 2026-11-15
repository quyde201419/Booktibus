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
        public const int MaxRenewalsPerLoan = 2;

        private static readonly Regex UsernamePattern = new(@"^[a-zA-Z0-9._-]{4,50}$", RegexOptions.Compiled);
        private static readonly Regex EmailPattern = new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled);

        private readonly SessionService _sessionService;
        private readonly AuthorizationService _authorizationService;

        public event EventHandler? CatalogChanged;

        public event EventHandler? CirculationChanged;

        public event EventHandler? ReadersChanged;

        public event EventHandler? AccountsChanged;

        public static void EnsureCirculationSchema(SqlConnection conn)
        {
            const string sql = @"
                IF COL_LENGTH(N'dbo.BorrowRecords', N'RenewalCount') IS NULL
                BEGIN
                    ALTER TABLE [dbo].[BorrowRecords]
                    ADD [RenewalCount] INT NOT NULL CONSTRAINT [DF_BorrowRecords_RenewalCount] DEFAULT (0);
                END;

                IF OBJECT_ID(N'dbo.BookReservations', N'U') IS NULL
                BEGIN
                    CREATE TABLE [dbo].[BookReservations]
                    (
                        [ReservationId]           INT           IDENTITY(1,1) NOT NULL,
                        [ReservationCode]         NVARCHAR(30)  NOT NULL,
                        [BookId]                  INT           NOT NULL,
                        [UserId]                  INT           NOT NULL,
                        [ReaderCardId]            NVARCHAR(30)  NOT NULL,
                        [ReservedAtUtc]           DATETIME2(0)  NOT NULL CONSTRAINT [DF_BookReservations_ReservedAtUtc] DEFAULT (SYSUTCDATETIME()),
                        [ReservationStatus]       NVARCHAR(30)  NOT NULL CONSTRAINT [DF_BookReservations_Status] DEFAULT (N'Pending'),
                        [ReadyAtUtc]              DATETIME2(0)  NULL,
                        [ExpiresAtUtc]            DATETIME2(0)  NULL,
                        [FulfilledBorrowRecordId] INT           NULL,
                        [Notes]                   NVARCHAR(400) NULL,

                        CONSTRAINT [PK_BookReservations] PRIMARY KEY CLUSTERED ([ReservationId] ASC),
                        CONSTRAINT [UQ_BookReservations_Code] UNIQUE NONCLUSTERED ([ReservationCode] ASC),
                        CONSTRAINT [FK_BookReservations_Books] FOREIGN KEY ([BookId]) REFERENCES [dbo].[Books] ([BookId]),
                        CONSTRAINT [FK_BookReservations_Users] FOREIGN KEY ([UserId]) REFERENCES [dbo].[Users] ([UserId]),
                        CONSTRAINT [CK_BookReservations_StatusValid] CHECK ([ReservationStatus] IN (N'Pending', N'Ready', N'Fulfilled', N'Cancelled'))
                    );
                END;";

            using SqlCommand cmd = new(sql, conn);
            cmd.ExecuteNonQuery();
        }

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
                EnsureCirculationSchema(conn);
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
                EnsureCirculationSchema(conn);
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

                // Remove any ReaderFavorites or BookReservations pointing to this book before deleting the book
                EnsureCirculationSchema(conn);

                const string deleteResSql = "DELETE FROM [dbo].[BookReservations] WHERE [BookId] = @BookId;";
                using (SqlCommand resCmd = new(deleteResSql, conn, tx))
                {
                    resCmd.Parameters.Add("@BookId", SqlDbType.Int).Value = bookId;
                    resCmd.ExecuteNonQuery();
                }

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
                EnsureCirculationSchema(conn);

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
                        ISNULL(br.[Notes], N''),
                        br.[RenewalCount],
                        CASE WHEN EXISTS (
                            SELECT 1 FROM [dbo].[BookReservations] rsv
                            WHERE rsv.[BookId] = br.[BookId]
                              AND rsv.[ReservationStatus] IN (N'Pending', N'Ready')
                        ) THEN CAST(1 AS BIT) ELSE CAST(0 AS BIT) END AS [HasActiveReservation]
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
                EnsureCirculationSchema(conn);
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

                // 3.5. Check if this reader has a 'Ready' (held) reservation for this book
                const string checkReadyHoldSql = @"
                    SELECT TOP (1) [ReservationId], [ReservationCode]
                    FROM [dbo].[BookReservations] WITH (UPDLOCK)
                    WHERE [BookId] = @BookId
                      AND [UserId] = @UserId
                      AND [ReservationStatus] = N'Ready'
                    ORDER BY [ReservedAtUtc] ASC;";

                int readyReservationId = 0;
                string readyReservationCode = string.Empty;

                using (SqlCommand holdCmd = new(checkReadyHoldSql, conn, tx))
                {
                    holdCmd.Parameters.Add("@BookId", SqlDbType.Int).Value = request.BookId;
                    holdCmd.Parameters.Add("@UserId", SqlDbType.Int).Value = request.ReaderUserId;
                    using SqlDataReader hRdr = holdCmd.ExecuteReader();
                    if (hRdr.Read())
                    {
                        readyReservationId = hRdr.GetInt32(0);
                        readyReservationCode = hRdr.GetString(1);
                    }
                }

                if (availableCopies <= 0 && readyReservationId == 0)
                {
                    tx.Rollback();
                    return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                        ? $"Đầu sách '{bookTitle}' ({bookCode}) hiện đã hết bản sao có sẵn (AvailableCopies = 0). Độc giả có thể bấm 'Đặt trước (Reserve)' để được giữ sách khi có người trả."
                        : $"Book '{bookTitle}' ({bookCode}) currently has no available copies (AvailableCopies = 0). Readers can click 'Reserve' to hold the next returned copy.");
                }

                // 4. Generate unique RecordCode and insert BorrowRecord
                string recordCode = GenerateNextBorrowRecordCode(conn, tx);
                DateTime nowUtc = DateTime.UtcNow;
                DateTime dueUtc = nowUtc.AddDays(request.LoanDays);

                const string insertLoanSql = @"
                    INSERT INTO [dbo].[BorrowRecords]
                        ([RecordCode], [UserId], [ReaderCardId], [BookId], [BorrowedAtUtc], [DueAtUtc], [ReturnedAtUtc], [BorrowStatus], [Notes], [RenewalCount])
                    OUTPUT INSERTED.[BorrowRecordId]
                    VALUES
                        (@RecordCode, @UserId, @ReaderCardId, @BookId, @BorrowedAtUtc, @DueAtUtc, NULL, N'Borrowed', @Notes, 0);";

                int newBorrowRecordId;
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

                    newBorrowRecordId = Convert.ToInt32(insCmd.ExecuteScalar());
                }

                if (readyReservationId > 0)
                {
                    // Copy was already held on return (AvailableCopies was kept at 0 for this reservation), mark reservation Fulfilled
                    const string fulfillSql = @"
                        UPDATE [dbo].[BookReservations]
                        SET [ReservationStatus]       = N'Fulfilled',
                            [FulfilledBorrowRecordId] = @BorrowRecordId,
                            [Notes]                   = ISNULL([Notes] + N' | ', N'') + N'Đã nhận sách theo phiếu mượn ' + @RecordCode
                        WHERE [ReservationId] = @ReservationId;";

                    using SqlCommand fulCmd = new(fulfillSql, conn, tx);
                    fulCmd.Parameters.Add("@BorrowRecordId", SqlDbType.Int).Value = newBorrowRecordId;
                    fulCmd.Parameters.Add("@RecordCode", SqlDbType.NVarChar, 30).Value = recordCode;
                    fulCmd.Parameters.Add("@ReservationId", SqlDbType.Int).Value = readyReservationId;
                    fulCmd.ExecuteNonQuery();
                }
                else
                {
                    // Also mark any Pending reservation of this same reader for this book as Fulfilled
                    const string fulfillPendingSql = @"
                        UPDATE [dbo].[BookReservations]
                        SET [ReservationStatus]       = N'Fulfilled',
                            [FulfilledBorrowRecordId] = @BorrowRecordId
                        WHERE [BookId] = @BookId
                          AND [UserId] = @UserId
                          AND [ReservationStatus] = N'Pending';";

                    using (SqlCommand fpCmd = new(fulfillPendingSql, conn, tx))
                    {
                        fpCmd.Parameters.Add("@BorrowRecordId", SqlDbType.Int).Value = newBorrowRecordId;
                        fpCmd.Parameters.Add("@BookId", SqlDbType.Int).Value = request.BookId;
                        fpCmd.Parameters.Add("@UserId", SqlDbType.Int).Value = request.ReaderUserId;
                        fpCmd.ExecuteNonQuery();
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
                EnsureCirculationSchema(conn);
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

                // Check if there is a reader waiting in the reservation queue (Pending) for this book
                const string checkNextReservationSql = @"
                    SELECT TOP (1)
                        rsv.[ReservationId],
                        rsv.[ReservationCode],
                        rsv.[UserId],
                        rsv.[ReaderCardId],
                        u.[FullName]
                    FROM [dbo].[BookReservations] rsv WITH (UPDLOCK)
                    INNER JOIN [dbo].[Users] u ON u.[UserId] = rsv.[UserId]
                    WHERE rsv.[BookId] = @BookId
                      AND rsv.[ReservationStatus] = N'Pending'
                    ORDER BY rsv.[ReservedAtUtc] ASC, rsv.[ReservationId] ASC;";

                int heldReservationId = 0;
                string heldReservationCode = string.Empty;
                string heldReaderCardId = string.Empty;
                string heldReaderName = string.Empty;

                using (SqlCommand rsvCmd = new(checkNextReservationSql, conn, tx))
                {
                    rsvCmd.Parameters.Add("@BookId", SqlDbType.Int).Value = bookId;
                    using SqlDataReader rsvRdr = rsvCmd.ExecuteReader();
                    if (rsvRdr.Read())
                    {
                        heldReservationId = rsvRdr.GetInt32(0);
                        heldReservationCode = rsvRdr.GetString(1);
                        heldReaderCardId = rsvRdr.GetString(3);
                        heldReaderName = rsvRdr.GetString(4);
                    }
                }

                if (heldReservationId > 0)
                {
                    // Hold the returned copy for the reader who reserved it (do not release into general AvailableCopies)
                    const string holdForReaderSql = @"
                        UPDATE [dbo].[BookReservations]
                        SET [ReservationStatus] = N'Ready',
                            [ReadyAtUtc]        = SYSUTCDATETIME(),
                            [ExpiresAtUtc]      = DATEADD(DAY, 3, SYSUTCDATETIME()),
                            [Notes]             = ISNULL([Notes] + N' | ', N'') + N'Sách vừa được trả (phiếu ' + @ReturnRecordCode + N') và đang được hệ thống giữ ưu tiên cho bạn.'
                        WHERE [ReservationId] = @ReservationId;";

                    using SqlCommand holdUpdCmd = new(holdForReaderSql, conn, tx);
                    holdUpdCmd.Parameters.Add("@ReturnRecordCode", SqlDbType.NVarChar, 30).Value = recordCode;
                    holdUpdCmd.Parameters.Add("@ReservationId", SqlDbType.Int).Value = heldReservationId;
                    holdUpdCmd.ExecuteNonQuery();
                }
                else
                {
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

                if (heldReservationId > 0)
                {
                    return ServiceOperationResult.Ok(LocalizationService.IsVietnamese
                        ? $"[Ghi nhận trả sách & Giữ sách Đặt trước] Phiếu mượn '{recordCode}' đã trả. Bản sao sách này đã được hệ thống tự động GIỮ SÁCH (Hold: {heldReservationCode}) cho độc giả đã đặt trước: '{heldReaderName}' ({heldReaderCardId})."
                        : $"[Book Returned & Held for Reservation] Loan '{recordCode}' returned. This copy is now automatically HELD ({heldReservationCode}) for reserving reader '{heldReaderName}' ({heldReaderCardId}).");
                }

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
        // 3.2B. LOAN RENEWAL (GIA HẠN MƯỢN SÁCH) & BOOK RESERVATION (ĐẶT TRƯỚC)
        // =====================================================================

        /// <summary>
        /// Renews an active loan by adding 7 or 14 days to DueAtUtc.
        /// Constraints:
        /// 1. Loan is active and NOT overdue.
        /// 2. Book is NOT reserved by anyone (Pending or Ready in dbo.BookReservations).
        /// 3. Each loan can be renewed at most MaxRenewalsPerLoan (2) times.
        /// Authorized for the Reader who owns the loan OR Staff (Librarian/Administrator).
        /// </summary>
        public ServiceOperationResult RenewBorrowLoan(int borrowRecordId, int extensionDays = 7)
        {
            if (_sessionService.CurrentSession is not { } session)
            {
                return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                    ? "Vui lòng đăng nhập để thực hiện gia hạn sách."
                    : "Please sign in to renew a book loan.");
            }

            if (borrowRecordId <= 0)
            {
                return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                    ? "Vui lòng chọn một phiếu mượn hợp lệ để gia hạn."
                    : "Please select a valid loan record to renew.");
            }

            if (extensionDays != 7 && extensionDays != 14)
            {
                return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                    ? "Số ngày gia hạn chỉ được phép chọn 7 ngày hoặc 14 ngày."
                    : "Renewal duration must be either 7 days or 14 days.");
            }

            SyncOverdueBorrowRecords();

            try
            {
                using SqlConnection conn = new(DatabaseConfig.GetConnectionString());
                conn.Open();
                EnsureCirculationSchema(conn);
                using SqlTransaction tx = conn.BeginTransaction();

                const string lockLoanSql = @"
                    SELECT
                        br.[RecordCode],
                        br.[UserId],
                        br.[BookId],
                        b.[Title],
                        b.[BookCode],
                        br.[DueAtUtc],
                        br.[ReturnedAtUtc],
                        br.[BorrowStatus],
                        br.[RenewalCount],
                        ISNULL(br.[Notes], N'')
                    FROM [dbo].[BorrowRecords] br WITH (UPDLOCK)
                    INNER JOIN [dbo].[Books] b ON b.[BookId] = br.[BookId]
                    WHERE br.[BorrowRecordId] = @BorrowRecordId;";

                string recordCode;
                int loanUserId;
                int bookId;
                string bookTitle;
                string bookCode;
                DateTime currentDueUtc;
                bool isReturned;
                string currentStatus;
                int currentRenewalCount;
                string existingNotes;

                using (SqlCommand cmd = new(lockLoanSql, conn, tx))
                {
                    cmd.Parameters.Add("@BorrowRecordId", SqlDbType.Int).Value = borrowRecordId;
                    using SqlDataReader rdr = cmd.ExecuteReader();
                    if (!rdr.Read())
                    {
                        rdr.Close();
                        tx.Rollback();
                        return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                            ? "Không tìm thấy phiếu mượn trong cơ sở dữ liệu."
                            : "Loan record was not found.");
                    }

                    recordCode = rdr.GetString(0);
                    loanUserId = rdr.GetInt32(1);
                    bookId = rdr.GetInt32(2);
                    bookTitle = rdr.GetString(3);
                    bookCode = rdr.GetString(4);
                    currentDueUtc = DateTime.SpecifyKind(rdr.GetDateTime(5), DateTimeKind.Utc);
                    isReturned = !rdr.IsDBNull(6) || string.Equals(rdr.GetString(7), "Returned", StringComparison.OrdinalIgnoreCase);
                    currentStatus = rdr.GetString(7);
                    currentRenewalCount = rdr.GetInt32(8);
                    existingNotes = rdr.GetString(9);
                }

                // Authorization check: Reader can only renew their own loan; Staff can renew any loan
                bool isStaff = _authorizationService.HasPermission(AppPermission.ProcessCirculation);
                if (!isStaff && session.UserId != loanUserId)
                {
                    tx.Rollback();
                    return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                        ? "[Chặn ở tầng Service] Bạn chỉ được phép gia hạn phiếu mượn của chính mình."
                        : "[Blocked by Service Layer] You can only renew your own loan records.");
                }

                // Constraint 0: Cannot renew an already returned loan
                if (isReturned)
                {
                    tx.Rollback();
                    return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                        ? $"Phiếu mượn '{recordCode}' đã được trả hoàn tất nên không thể gia hạn."
                        : $"Loan '{recordCode}' has already been returned and cannot be renewed.");
                }

                // Constraint 1: Book must NOT be overdue
                bool isOverdue = string.Equals(currentStatus, "Overdue", StringComparison.OrdinalIgnoreCase)
                                 || currentDueUtc.Date < DateTime.UtcNow.Date;
                if (isOverdue)
                {
                    tx.Rollback();
                    return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                        ? $"[Từ chối gia hạn] Phiếu mượn '{recordCode}' đã quá hạn trả ({currentDueUtc.ToLocalTime():dd/MM/yyyy}). Sách đã quá hạn không được phép gia hạn."
                        : $"[Renewal Denied] Loan '{recordCode}' is already overdue (Due: {currentDueUtc.ToLocalTime():dd/MM/yyyy}). Overdue loans cannot be renewed.");
                }

                // Constraint 2: Maximum renewals per loan (1-2 times, max = 2)
                if (currentRenewalCount >= MaxRenewalsPerLoan)
                {
                    tx.Rollback();
                    return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                        ? $"[Từ chối gia hạn] Phiếu mượn '{recordCode}' đã gia hạn đủ tối đa {currentRenewalCount}/{MaxRenewalsPerLoan} lần cho phép."
                        : $"[Renewal Denied] Loan '{recordCode}' has already reached the maximum limit of {currentRenewalCount}/{MaxRenewalsPerLoan} renewals.");
                }

                // Constraint 3: Book must NOT be reserved by another reader
                const string checkReservedSql = @"
                    SELECT COUNT(1)
                    FROM [dbo].[BookReservations] WITH (UPDLOCK)
                    WHERE [BookId] = @BookId
                      AND [ReservationStatus] IN (N'Pending', N'Ready');";

                using (SqlCommand rsvCmd = new(checkReservedSql, conn, tx))
                {
                    rsvCmd.Parameters.Add("@BookId", SqlDbType.Int).Value = bookId;
                    int activeHolds = Convert.ToInt32(rsvCmd.ExecuteScalar());
                    if (activeHolds > 0)
                    {
                        tx.Rollback();
                        return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                            ? $"[Từ chối gia hạn] Đầu sách '{bookTitle}' ({bookCode}) hiện đang có {activeHolds} độc giả đặt trước (Reserve/Hold), nên không được phép gia hạn."
                            : $"[Renewal Denied] Book '{bookTitle}' ({bookCode}) currently has {activeHolds} active reservation(s) by other readers and cannot be renewed.");
                    }
                }

                DateTime newDueUtc = currentDueUtc.AddDays(extensionDays);
                int newRenewalCount = currentRenewalCount + 1;
                string actorLabel = isStaff ? $"Thủ thư {session.Username}" : $"Độc giả {session.Username}";
                string renewalNote = $"Gia hạn lần {newRenewalCount}/{MaxRenewalsPerLoan} (+{extensionDays} ngày -> hạn mới {newDueUtc.ToLocalTime():dd/MM/yyyy} bởi {actorLabel})";
                string updatedNotes = string.IsNullOrWhiteSpace(existingNotes)
                    ? renewalNote
                    : $"{existingNotes} | {renewalNote}";

                if (updatedNotes.Length > 400)
                {
                    updatedNotes = updatedNotes[^400..];
                }

                const string updateRenewalSql = @"
                    UPDATE [dbo].[BorrowRecords]
                    SET [DueAtUtc]     = @NewDueAtUtc,
                        [RenewalCount] = @NewRenewalCount,
                        [Notes]        = @Notes
                    WHERE [BorrowRecordId] = @BorrowRecordId;";

                using (SqlCommand updCmd = new(updateRenewalSql, conn, tx))
                {
                    updCmd.Parameters.Add("@NewDueAtUtc", SqlDbType.DateTime2).Value = newDueUtc;
                    updCmd.Parameters.Add("@NewRenewalCount", SqlDbType.Int).Value = newRenewalCount;
                    updCmd.Parameters.Add("@Notes", SqlDbType.NVarChar, 400).Value = updatedNotes;
                    updCmd.Parameters.Add("@BorrowRecordId", SqlDbType.Int).Value = borrowRecordId;
                    updCmd.ExecuteNonQuery();
                }

                tx.Commit();
                CirculationChanged?.Invoke(this, EventArgs.Empty);

                return ServiceOperationResult.Ok(LocalizationService.IsVietnamese
                    ? $"[Gia hạn thành công] Phiếu mượn '{recordCode}' ('{bookTitle}') đã được cộng thêm {extensionDays} ngày. Hạn trả mới: {newDueUtc.ToLocalTime():dd/MM/yyyy} (Lần gia hạn: {newRenewalCount}/{MaxRenewalsPerLoan})."
                    : $"[Loan Renewed] Loan '{recordCode}' ('{bookTitle}') extended by {extensionDays} days. New due date: {newDueUtc.ToLocalTime():dd/MM/yyyy} (Renewal {newRenewalCount}/{MaxRenewalsPerLoan}).");
            }
            catch (SqlException ex)
            {
                AppDiagnosticLogger.LogDatabaseException(nameof(LibraryManagementService), nameof(RenewBorrowLoan), ex, $"BorrowRecordId={borrowRecordId}");
                return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                    ? $"Lỗi SQL Server khi gia hạn phiếu mượn: {ex.Message}"
                    : $"SQL Server error while renewing loan: {ex.Message}");
            }
        }

        /// <summary>
        /// Places a Book Reservation (Hold) when AvailableCopies == 0.
        /// When another reader returns that book, ProcessBookReturn automatically holds the copy for the reserver.
        /// </summary>
        public ServiceOperationResult ReserveBook(int bookId, int? targetReaderUserId = null, string? notes = null)
        {
            if (_sessionService.CurrentSession is not { } session)
            {
                return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                    ? "Vui lòng đăng nhập để đặt trước sách."
                    : "Please sign in to reserve a book.");
            }

            if (bookId <= 0)
            {
                return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                    ? "Vui lòng chọn một đầu sách hợp lệ để đặt trước."
                    : "Please select a valid book to reserve.");
            }

            bool isStaff = _authorizationService.HasPermission(AppPermission.ProcessCirculation);
            int readerUserId = isStaff && targetReaderUserId.HasValue && targetReaderUserId.Value > 0
                ? targetReaderUserId.Value
                : session.UserId;

            if (!isStaff && targetReaderUserId.HasValue && targetReaderUserId.Value != session.UserId)
            {
                return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                    ? "[Chặn ở tầng Service] Độc giả chỉ được đặt trước sách cho chính tài khoản của mình."
                    : "[Blocked by Service Layer] Readers can only reserve books for their own account.");
            }

            try
            {
                using SqlConnection conn = new(DatabaseConfig.GetConnectionString());
                conn.Open();
                EnsureCirculationSchema(conn);
                using SqlTransaction tx = conn.BeginTransaction();

                // 1. Verify Reader card & active status
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
                    readerCmd.Parameters.Add("@UserId", SqlDbType.Int).Value = readerUserId;
                    using SqlDataReader rdr = readerCmd.ExecuteReader();
                    if (!rdr.Read())
                    {
                        rdr.Close();
                        tx.Rollback();
                        return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                            ? "Tài khoản chưa có thẻ độc giả hợp lệ để đặt trước sách."
                            : "No valid reader card found for this account to place a reservation.");
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
                        ? $"Thẻ độc giả '{readerCardId}' hiện không ở trạng thái hoạt động hợp lệ."
                        : $"Reader card '{readerCardId}' is not currently active.");
                }

                // 2. Lock book row and verify AvailableCopies == 0
                const string bookSql = @"
                    SELECT [BookCode], [Title], [AvailableCopies], [TotalCopies]
                    FROM [dbo].[Books] WITH (UPDLOCK)
                    WHERE [BookId] = @BookId;";

                string bookCode;
                string bookTitle;
                int availableCopies;

                using (SqlCommand bookCmd = new(bookSql, conn, tx))
                {
                    bookCmd.Parameters.Add("@BookId", SqlDbType.Int).Value = bookId;
                    using SqlDataReader bRdr = bookCmd.ExecuteReader();
                    if (!bRdr.Read())
                    {
                        bRdr.Close();
                        tx.Rollback();
                        return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                            ? "Không tìm thấy đầu sách trong cơ sở dữ liệu."
                            : "Book was not found in the database.");
                    }

                    bookCode = bRdr.GetString(0);
                    bookTitle = bRdr.GetString(1);
                    availableCopies = bRdr.GetInt32(2);
                }

                if (availableCopies > 0)
                {
                    tx.Rollback();
                    return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                        ? $"[Không cần đặt trước] Đầu sách '{bookTitle}' ({bookCode}) hiện vẫn còn {availableCopies} bản sao có sẵn trên kệ. Chỉ đặt trước khi AvailableCopies = 0."
                        : $"[Reservation Not Needed] Book '{bookTitle}' ({bookCode}) currently has {availableCopies} available copy(ies). Reservations are only for out-of-stock books (AvailableCopies = 0).");
                }

                // 3. Check if reader is already borrowing this book
                const string activeLoanSql = @"
                    SELECT COUNT(1)
                    FROM [dbo].[BorrowRecords] WITH (UPDLOCK)
                    WHERE [UserId] = @UserId
                      AND [BookId] = @BookId
                      AND [ReturnedAtUtc] IS NULL;";

                using (SqlCommand loanCmd = new(activeLoanSql, conn, tx))
                {
                    loanCmd.Parameters.Add("@UserId", SqlDbType.Int).Value = readerUserId;
                    loanCmd.Parameters.Add("@BookId", SqlDbType.Int).Value = bookId;
                    if (Convert.ToInt32(loanCmd.ExecuteScalar()) > 0)
                    {
                        tx.Rollback();
                        return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                            ? $"Độc giả '{readerName}' ({readerCardId}) hiện đang mượn cuốn '{bookTitle}' nên không thể đặt trước chính cuốn sách đang giữ."
                            : $"Reader '{readerName}' ({readerCardId}) is already borrowing '{bookTitle}' and cannot reserve it.");
                    }
                }

                // 4. Check duplicate active reservation by the same reader for the same book
                const string dupRsvSql = @"
                    SELECT [ReservationCode], [ReservationStatus]
                    FROM [dbo].[BookReservations] WITH (UPDLOCK)
                    WHERE [UserId] = @UserId
                      AND [BookId] = @BookId
                      AND [ReservationStatus] IN (N'Pending', N'Ready');";

                using (SqlCommand dupCmd = new(dupRsvSql, conn, tx))
                {
                    dupCmd.Parameters.Add("@UserId", SqlDbType.Int).Value = readerUserId;
                    dupCmd.Parameters.Add("@BookId", SqlDbType.Int).Value = bookId;
                    using SqlDataReader dupRdr = dupCmd.ExecuteReader();
                    if (dupRdr.Read())
                    {
                        string existingRsvCode = dupRdr.GetString(0);
                        dupRdr.Close();
                        tx.Rollback();
                        return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                            ? $"[Chặn đặt trước trùng lặp] Độc giả '{readerName}' đã có phiếu đặt trước '{existingRsvCode}' đang hiệu lực cho đầu sách này."
                            : $"[Duplicate Reservation Blocked] Reader '{readerName}' already has an active reservation '{existingRsvCode}' for this book.");
                    }
                }

                // 5. Generate ReservationCode and insert into dbo.BookReservations
                const string nextSeqSql = "SELECT ISNULL(MAX([ReservationId]), 0) + 1 FROM [dbo].[BookReservations] WITH (TABLOCKX);";
                int nextSeq;
                using (SqlCommand seqCmd = new(nextSeqSql, conn, tx))
                {
                    nextSeq = Convert.ToInt32(seqCmd.ExecuteScalar());
                }

                string reservationCode = $"RSV-{DateTime.UtcNow.Year}-{nextSeq:D4}";
                string defaultNote = string.IsNullOrWhiteSpace(notes)
                    ? "Đặt trước khi hết bản sao (AvailableCopies = 0). Hệ thống sẽ tự động giữ sách khi có người trả."
                    : notes.Trim();

                const string insertRsvSql = @"
                    INSERT INTO [dbo].[BookReservations]
                        ([ReservationCode], [BookId], [UserId], [ReaderCardId], [ReservedAtUtc], [ReservationStatus], [Notes])
                    VALUES
                        (@ReservationCode, @BookId, @UserId, @ReaderCardId, SYSUTCDATETIME(), N'Pending', @Notes);";

                using (SqlCommand insCmd = new(insertRsvSql, conn, tx))
                {
                    insCmd.Parameters.Add("@ReservationCode", SqlDbType.NVarChar, 30).Value = reservationCode;
                    insCmd.Parameters.Add("@BookId", SqlDbType.Int).Value = bookId;
                    insCmd.Parameters.Add("@UserId", SqlDbType.Int).Value = readerUserId;
                    insCmd.Parameters.Add("@ReaderCardId", SqlDbType.NVarChar, 30).Value = readerCardId;
                    insCmd.Parameters.Add("@Notes", SqlDbType.NVarChar, 400).Value = defaultNote;
                    insCmd.ExecuteNonQuery();
                }

                tx.Commit();
                CirculationChanged?.Invoke(this, EventArgs.Empty);
                CatalogChanged?.Invoke(this, EventArgs.Empty);

                return ServiceOperationResult.Ok(LocalizationService.IsVietnamese
                    ? $"[Đặt trước thành công] Đã lập phiếu đặt trước {reservationCode} cho sách '{bookTitle}' ({bookCode}) — Độc giả: {readerName} ({readerCardId}). Khi có người trả sách, hệ thống sẽ tự động giữ bản sao cho bạn."
                    : $"[Book Reserved] Created reservation {reservationCode} for '{bookTitle}' ({bookCode}) — Reader: {readerName} ({readerCardId}). The next returned copy will be automatically held for you.");
            }
            catch (SqlException ex)
            {
                AppDiagnosticLogger.LogDatabaseException(nameof(LibraryManagementService), nameof(ReserveBook), ex, $"BookId={bookId}");
                return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                    ? $"Lỗi SQL Server khi đặt trước sách: {ex.Message}"
                    : $"SQL Server error while reserving book: {ex.Message}");
            }
        }

        /// <summary>
        /// Cancels a Pending or Ready reservation. If the reservation was in 'Ready' (held) status,
        /// promotes the next Pending reservation in queue or restores 1 copy to AvailableCopies.
        /// </summary>
        public ServiceOperationResult CancelReservation(int reservationId)
        {
            if (_sessionService.CurrentSession is not { } session)
            {
                return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                    ? "Vui lòng đăng nhập để hủy đặt trước."
                    : "Please sign in to cancel a reservation.");
            }

            if (reservationId <= 0)
            {
                return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                    ? "Vui lòng chọn phiếu đặt trước cần hủy."
                    : "Please select a reservation to cancel.");
            }

            try
            {
                using SqlConnection conn = new(DatabaseConfig.GetConnectionString());
                conn.Open();
                EnsureCirculationSchema(conn);
                using SqlTransaction tx = conn.BeginTransaction();

                const string getRsvSql = @"
                    SELECT [ReservationCode], [BookId], [UserId], [ReservationStatus]
                    FROM [dbo].[BookReservations] WITH (UPDLOCK)
                    WHERE [ReservationId] = @ReservationId;";

                string reservationCode;
                int bookId;
                int ownerUserId;
                string status;

                using (SqlCommand getCmd = new(getRsvSql, conn, tx))
                {
                    getCmd.Parameters.Add("@ReservationId", SqlDbType.Int).Value = reservationId;
                    using SqlDataReader rdr = getCmd.ExecuteReader();
                    if (!rdr.Read())
                    {
                        rdr.Close();
                        tx.Rollback();
                        return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                            ? "Không tìm thấy phiếu đặt trước."
                            : "Reservation record was not found.");
                    }

                    reservationCode = rdr.GetString(0);
                    bookId = rdr.GetInt32(1);
                    ownerUserId = rdr.GetInt32(2);
                    status = rdr.GetString(3);
                }

                bool isStaff = _authorizationService.HasPermission(AppPermission.ProcessCirculation);
                if (!isStaff && session.UserId != ownerUserId)
                {
                    tx.Rollback();
                    return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                        ? "[Chặn ở tầng Service] Bạn chỉ có thể hủy phiếu đặt trước của chính mình."
                        : "[Blocked by Service Layer] You can only cancel your own reservations.");
                }

                if (!string.Equals(status, "Pending", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(status, "Ready", StringComparison.OrdinalIgnoreCase))
                {
                    tx.Rollback();
                    return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                        ? $"Phiếu đặt trước {reservationCode} hiện ở trạng thái '{status}' nên không thể hủy."
                        : $"Reservation {reservationCode} is currently '{status}' and cannot be cancelled.");
                }

                const string cancelSql = @"
                    UPDATE [dbo].[BookReservations]
                    SET [ReservationStatus] = N'Cancelled',
                        [Notes]             = ISNULL([Notes] + N' | ', N'') + N'Đã hủy đặt trước.'
                    WHERE [ReservationId] = @ReservationId;";

                using (SqlCommand cancelCmd = new(cancelSql, conn, tx))
                {
                    cancelCmd.Parameters.Add("@ReservationId", SqlDbType.Int).Value = reservationId;
                    cancelCmd.ExecuteNonQuery();
                }

                // If this reservation was holding a returned copy ('Ready'), pass it to the next Pending reader or restore AvailableCopies
                if (string.Equals(status, "Ready", StringComparison.OrdinalIgnoreCase))
                {
                    const string nextPendingSql = @"
                        SELECT TOP (1) [ReservationId]
                        FROM [dbo].[BookReservations] WITH (UPDLOCK)
                        WHERE [BookId] = @BookId AND [ReservationStatus] = N'Pending'
                        ORDER BY [ReservedAtUtc] ASC, [ReservationId] ASC;";

                    int nextPendingId = 0;
                    using (SqlCommand nextCmd = new(nextPendingSql, conn, tx))
                    {
                        nextCmd.Parameters.Add("@BookId", SqlDbType.Int).Value = bookId;
                        object? nextObj = nextCmd.ExecuteScalar();
                        if (nextObj is not null && nextObj != DBNull.Value)
                        {
                            nextPendingId = Convert.ToInt32(nextObj);
                        }
                    }

                    if (nextPendingId > 0)
                    {
                        const string promoteSql = @"
                            UPDATE [dbo].[BookReservations]
                            SET [ReservationStatus] = N'Ready',
                                [ReadyAtUtc]        = SYSUTCDATETIME(),
                                [ExpiresAtUtc]      = DATEADD(DAY, 3, SYSUTCDATETIME())
                            WHERE [ReservationId] = @NextId;";

                        using SqlCommand promCmd = new(promoteSql, conn, tx);
                        promCmd.Parameters.Add("@NextId", SqlDbType.Int).Value = nextPendingId;
                        promCmd.ExecuteNonQuery();
                    }
                    else
                    {
                        const string restoreCopySql = @"
                            UPDATE [dbo].[Books]
                            SET [AvailableCopies] = CASE
                                WHEN [AvailableCopies] + 1 <= [TotalCopies] THEN [AvailableCopies] + 1
                                ELSE [TotalCopies]
                            END
                            WHERE [BookId] = @BookId;";

                        using SqlCommand restCmd = new(restoreCopySql, conn, tx);
                        restCmd.Parameters.Add("@BookId", SqlDbType.Int).Value = bookId;
                        restCmd.ExecuteNonQuery();
                    }
                }

                tx.Commit();
                CirculationChanged?.Invoke(this, EventArgs.Empty);
                CatalogChanged?.Invoke(this, EventArgs.Empty);

                return ServiceOperationResult.Ok(LocalizationService.IsVietnamese
                    ? $"[Hủy đặt trước] Đã hủy phiếu đặt trước {reservationCode} thành công."
                    : $"[Reservation Cancelled] Successfully cancelled reservation {reservationCode}.");
            }
            catch (SqlException ex)
            {
                AppDiagnosticLogger.LogDatabaseException(nameof(LibraryManagementService), nameof(CancelReservation), ex, $"ReservationId={reservationId}");
                return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                    ? $"Lỗi SQL Server khi hủy đặt trước: {ex.Message}"
                    : $"SQL Server error while cancelling reservation: {ex.Message}");
            }
        }

        /// <summary>
        /// Allows Staff (Librarian/Administrator) to issue a loan directly from a 'Ready' (held) reservation.
        /// </summary>
        public ServiceOperationResult FulfillReservation(int reservationId, int loanDays = 14)
        {
            if (!_authorizationService.TryAuthorizeOperation(AppPermission.ProcessCirculation, "FulfillReservation()", out string authMsg))
            {
                return ServiceOperationResult.Fail(authMsg);
            }

            if (reservationId <= 0)
            {
                return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                    ? "Vui lòng chọn một phiếu đặt trước đang giữ sách (Ready) để giao sách."
                    : "Please select a Ready reservation to fulfill.");
            }

            try
            {
                int readerUserId;
                int bookId;
                string reservationCode;
                string status;

                using (SqlConnection conn = new(DatabaseConfig.GetConnectionString()))
                {
                    conn.Open();
                    EnsureCirculationSchema(conn);

                    const string sql = @"
                        SELECT [UserId], [BookId], [ReservationCode], [ReservationStatus]
                        FROM [dbo].[BookReservations]
                        WHERE [ReservationId] = @ReservationId;";

                    using SqlCommand cmd = new(sql, conn);
                    cmd.Parameters.Add("@ReservationId", SqlDbType.Int).Value = reservationId;
                    using SqlDataReader rdr = cmd.ExecuteReader();
                    if (!rdr.Read())
                    {
                        return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                            ? "Không tìm thấy phiếu đặt trước."
                            : "Reservation record was not found.");
                    }

                    readerUserId = rdr.GetInt32(0);
                    bookId = rdr.GetInt32(1);
                    reservationCode = rdr.GetString(2);
                    status = rdr.GetString(3);
                }

                if (!string.Equals(status, "Ready", StringComparison.OrdinalIgnoreCase))
                {
                    return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                        ? $"Phiếu đặt trước {reservationCode} hiện ở trạng thái '{status}' (Chưa có bản sao trả về để giữ sách)."
                        : $"Reservation {reservationCode} is currently '{status}' (Book copy has not been returned/held yet).");
                }

                return CreateBorrowTransaction(new CreateBorrowLoanRequest
                {
                    ReaderUserId = readerUserId,
                    BookId = bookId,
                    LoanDays = Math.Clamp(loanDays, 1, 60),
                    Notes = $"Nhận sách từ phiếu đặt trước {reservationCode}"
                });
            }
            catch (SqlException ex)
            {
                AppDiagnosticLogger.LogDatabaseException(nameof(LibraryManagementService), nameof(FulfillReservation), ex, $"ReservationId={reservationId}");
                return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                    ? $"Lỗi SQL Server khi giao sách đặt trước: {ex.Message}"
                    : $"SQL Server error while fulfilling reservation: {ex.Message}");
            }
        }

        public IReadOnlyList<BookReservationItemModel> GetReservationsForCurrentSession(out string? errorMessage)
        {
            errorMessage = null;
            if (_sessionService.CurrentSession is not { } session)
            {
                return [];
            }

            bool isStaff = _authorizationService.HasPermission(AppPermission.ProcessCirculation);
            List<BookReservationItemModel> items = new();

            try
            {
                using SqlConnection conn = new(DatabaseConfig.GetConnectionString());
                conn.Open();
                EnsureCirculationSchema(conn);

                const string sql = @"
                    SELECT
                        rsv.[ReservationId],
                        rsv.[ReservationCode],
                        rsv.[BookId],
                        b.[BookCode],
                        b.[Title],
                        b.[Author],
                        rsv.[UserId],
                        u.[Username],
                        u.[FullName],
                        rsv.[ReaderCardId],
                        rsv.[ReservedAtUtc],
                        rsv.[ReadyAtUtc],
                        rsv.[ExpiresAtUtc],
                        rsv.[ReservationStatus],
                        ISNULL(rsv.[Notes], N'')
                    FROM [dbo].[BookReservations] rsv
                    INNER JOIN [dbo].[Books] b ON b.[BookId] = rsv.[BookId]
                    INNER JOIN [dbo].[Users] u ON u.[UserId] = rsv.[UserId]
                    WHERE (@IsStaff = 1 OR rsv.[UserId] = @CurrentUserId)
                    ORDER BY
                        CASE rsv.[ReservationStatus]
                            WHEN N'Ready' THEN 0
                            WHEN N'Pending' THEN 1
                            ELSE 2
                        END ASC,
                        rsv.[ReservedAtUtc] DESC;";

                using SqlCommand cmd = new(sql, conn);
                cmd.Parameters.Add("@IsStaff", SqlDbType.Bit).Value = isStaff;
                cmd.Parameters.Add("@CurrentUserId", SqlDbType.Int).Value = session.UserId;

                using SqlDataReader rdr = cmd.ExecuteReader();
                while (rdr.Read())
                {
                    DateTime reservedUtc = DateTime.SpecifyKind(rdr.GetDateTime(10), DateTimeKind.Utc);
                    DateTime? readyLocal = rdr.IsDBNull(11) ? null : DateTime.SpecifyKind(rdr.GetDateTime(11), DateTimeKind.Utc).ToLocalTime();
                    DateTime? expiresLocal = rdr.IsDBNull(12) ? null : DateTime.SpecifyKind(rdr.GetDateTime(12), DateTimeKind.Utc).ToLocalTime();

                    items.Add(new BookReservationItemModel
                    {
                        ReservationId = rdr.GetInt32(0),
                        ReservationCode = rdr.GetString(1),
                        BookId = rdr.GetInt32(2),
                        BookCode = rdr.GetString(3),
                        BookTitle = rdr.GetString(4),
                        BookAuthor = rdr.GetString(5),
                        UserId = rdr.GetInt32(6),
                        Username = rdr.GetString(7),
                        ReaderFullName = rdr.GetString(8),
                        ReaderCardId = rdr.GetString(9),
                        ReservedAtLocal = reservedUtc.ToLocalTime(),
                        ReadyAtLocal = readyLocal,
                        ExpiresAtLocal = expiresLocal,
                        ReservationStatus = rdr.GetString(13),
                        Notes = rdr.GetString(14)
                    });
                }
            }
            catch (SqlException ex)
            {
                AppDiagnosticLogger.LogDatabaseException(nameof(LibraryManagementService), nameof(GetReservationsForCurrentSession), ex);
                errorMessage = ex.Message;
            }

            return items;
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
                EnsureCirculationSchema(conn);
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
                EnsureCirculationSchema(conn);
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
                EnsureCirculationSchema(conn);
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
                EnsureCirculationSchema(conn);
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

            int renewalCount = reader.FieldCount > 15 && !reader.IsDBNull(15) ? reader.GetInt32(15) : 0;
            bool hasActiveReservation = reader.FieldCount > 16 && !reader.IsDBNull(16) && reader.GetBoolean(16);

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
                Notes = reader.GetString(14),
                RenewalCount = renewalCount,
                HasActiveReservationOnBook = hasActiveReservation
            };
        }
    }
}

