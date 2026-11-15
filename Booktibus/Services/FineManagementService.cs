using System.Data;
using Booktibus.Data;
using Booktibus.Helpers;
using Booktibus.Models;
using Microsoft.Data.SqlClient;

namespace Booktibus.Services
{
    /// <summary>
    /// Business and data access service for Stage 8 Fines Management:
    /// Calculation, Overdue detection, Estimated vs Recorded Fines, Fine Adjustments,
    /// Waivers with audit logging, and Configurable daily rate policy.
    /// </summary>
    public class FineManagementService
    {
        public const decimal DefaultDailyOverdueRateVnd = 5000m;
        public const string SettingKeyOverdueRate = "OverdueDailyRateVnd";

        private readonly SessionService _sessionService;
        private readonly AuthorizationService _authorizationService;

        public event EventHandler? FinesChanged;

        public FineManagementService(
            SessionService sessionService,
            AuthorizationService authorizationService)
        {
            ArgumentNullException.ThrowIfNull(sessionService);
            ArgumentNullException.ThrowIfNull(authorizationService);

            _sessionService = sessionService;
            _authorizationService = authorizationService;
        }

        // =====================================================================
        // 1. BUSINESS CALCULATION RULES
        // =====================================================================

        /// <summary>
        /// Calculates the number of overdue days.
        /// If returned on time or early, or if due date has not passed, returns 0 (never negative).
        /// </summary>
        public static int CalculateOverdueDays(DateTime dueAtUtc, DateTime? returnedAtUtc = null)
        {
            DateTime effectiveUtc = returnedAtUtc ?? DateTime.UtcNow;

            // Returned on or before due timestamp, or on the same calendar date in UTC
            if (effectiveUtc <= dueAtUtc || effectiveUtc.Date <= dueAtUtc.Date)
            {
                return 0;
            }

            int days = (effectiveUtc.Date - dueAtUtc.Date).Days;
            return Math.Max(0, days);
        }

        /// <summary>
        /// Retrieves the current daily overdue rate in VND from dbo.FineSettings (defaults to 5,000 VND).
        /// </summary>
        public decimal GetCurrentDailyOverdueRateVnd()
        {
            try
            {
                using SqlConnection conn = new(DatabaseConfig.GetConnectionString());
                conn.Open();

                const string sql = "SELECT [SettingValueDecimal] FROM [dbo].[FineSettings] WHERE [SettingKey] = @Key;";
                using SqlCommand cmd = new(sql, conn);
                cmd.Parameters.Add("@Key", SqlDbType.NVarChar, 60).Value = SettingKeyOverdueRate;
                object? scalar = cmd.ExecuteScalar();

                if (scalar is not null && scalar != DBNull.Value && decimal.TryParse(scalar.ToString(), out decimal rate))
                {
                    return rate;
                }
            }
            catch (Exception ex)
            {
                AppDiagnosticLogger.LogException(nameof(FineManagementService), nameof(GetCurrentDailyOverdueRateVnd), ex);
            }

            return DefaultDailyOverdueRateVnd;
        }

        /// <summary>
        /// Updates the daily overdue rate policy in dbo.FineSettings.
        /// Restricted strictly to Administrator (enforced via AppPermission.ManageSystemSettings).
        /// </summary>
        public ServiceOperationResult UpdateOverdueDailyRate(decimal newRateVnd)
        {
            if (!_authorizationService.TryAuthorizeOperation(AppPermission.ManageSystemSettings, "UpdateOverdueDailyRate()", out string authMsg))
            {
                return ServiceOperationResult.Fail(authMsg);
            }

            if (newRateVnd < 0 || newRateVnd > 500_000m)
            {
                return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                    ? "Mức phạt theo ngày phải là số không âm và không vượt quá 500.000 VNĐ/ngày."
                    : "Daily fine rate must be a non-negative number up to 500,000 VND/day.");
            }

            int staffUserId = _sessionService.CurrentSession!.UserId;

            try
            {
                using SqlConnection conn = new(DatabaseConfig.GetConnectionString());
                conn.Open();

                const string sql = @"
                    IF EXISTS (SELECT 1 FROM [dbo].[FineSettings] WHERE [SettingKey] = @Key)
                    BEGIN
                        UPDATE [dbo].[FineSettings]
                        SET [SettingValueDecimal] = @Value,
                            [UpdatedAtUtc]        = SYSUTCDATETIME(),
                            [UpdatedByUserId]     = @UserId
                        WHERE [SettingKey] = @Key;
                    END
                    ELSE
                    BEGIN
                        INSERT INTO [dbo].[FineSettings]
                            ([SettingKey], [SettingValueDecimal], [Description], [UpdatedAtUtc], [UpdatedByUserId])
                        VALUES
                            (@Key, @Value, N'Mức phạt quá hạn mặc định (VNĐ / cuốn sách / ngày quá hạn).', SYSUTCDATETIME(), @UserId);
                    END;";

                using SqlCommand cmd = new(sql, conn);
                cmd.Parameters.Add("@Key", SqlDbType.NVarChar, 60).Value = SettingKeyOverdueRate;
                cmd.Parameters.Add("@Value", SqlDbType.Decimal).Value = newRateVnd;
                cmd.Parameters.Add("@UserId", SqlDbType.Int).Value = staffUserId;
                cmd.ExecuteNonQuery();

                FinesChanged?.Invoke(this, EventArgs.Empty);

                return ServiceOperationResult.Ok(LocalizationService.IsVietnamese
                    ? $"[Cấu hình Tiền phạt] Đã cập nhật mức phạt quá hạn thành {newRateVnd:N0} VNĐ/cuốn/ngày."
                    : $"[Fine Policy] Successfully updated overdue rate to {newRateVnd:N0} VND/book/day.");
            }
            catch (SqlException ex)
            {
                AppDiagnosticLogger.LogDatabaseException(nameof(FineManagementService), nameof(UpdateOverdueDailyRate), ex);
                return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                    ? $"Lỗi SQL Server khi cập nhật mức phạt: {ex.Message}"
                    : $"SQL Server error while updating fine rate: {ex.Message}");
            }
        }

        // =====================================================================
        // 2. READER FINE WORKSPACE (Strictly Scoped to Session UserId)
        // =====================================================================

        public IReadOnlyList<FineItemModel> GetOwnFinesWorkspace(
            out FineSummaryStatsModel summaryStats,
            out string? errorMessage)
        {
            errorMessage = null;
            summaryStats = new FineSummaryStatsModel();

            if (_sessionService.CurrentSession is not { } session)
            {
                errorMessage = LocalizationService.IsVietnamese
                    ? "Vui lòng đăng nhập để xem thông tin tiền phạt."
                    : "Please sign in to view your fine records.";
                return [];
            }

            return TryGetFinesForUser(session.UserId, out summaryStats, out errorMessage);
        }

        public IReadOnlyList<FineItemModel> TryGetFinesForUser(
            int targetUserId,
            out FineSummaryStatsModel summaryStats,
            out string? errorMessage)
        {
            errorMessage = null;
            decimal currentRate = GetCurrentDailyOverdueRateVnd();
            summaryStats = new FineSummaryStatsModel { CurrentDailyOverdueRateVnd = currentRate };

            if (!_authorizationService.CanAccessUserAccountData(targetUserId, out string denialMessage))
            {
                errorMessage = denialMessage;
                return [];
            }

            List<FineItemModel> items = new();

            try
            {
                using SqlConnection conn = new(DatabaseConfig.GetConnectionString());
                conn.Open();

                // 1. Fetch officially recorded fines in dbo.Fines
                const string recordedSql = @"
                    SELECT
                        f.[FineId],
                        f.[FineCode],
                        f.[BorrowRecordId],
                        br.[RecordCode],
                        f.[UserId],
                        u.[Username],
                        u.[FullName],
                        r.[ReaderCardId],
                        br.[BookId],
                        b.[BookCode],
                        b.[Title],
                        br.[BorrowedAtUtc],
                        br.[DueAtUtc],
                        br.[ReturnedAtUtc],
                        f.[FineType],
                        f.[DaysOverdue],
                        f.[DailyRateVnd],
                        f.[Amount],
                        f.[FineStatus],
                        f.[CreatedAtUtc],
                        f.[UpdatedAtUtc],
                        f.[PaidAtUtc],
                        f.[WaivedAtUtc],
                        ISNULL(res.[Username], N''),
                        ISNULL(f.[Notes], N'')
                    FROM [dbo].[Fines] f
                    INNER JOIN [dbo].[BorrowRecords] br ON br.[BorrowRecordId] = f.[BorrowRecordId]
                    INNER JOIN [dbo].[Books] b ON b.[BookId] = br.[BookId]
                    INNER JOIN [dbo].[Users] u ON u.[UserId] = f.[UserId]
                    LEFT JOIN [dbo].[Readers] r ON r.[UserId] = f.[UserId]
                    LEFT JOIN [dbo].[Users] res ON res.[UserId] = f.[ResolvedByUserId]
                    WHERE f.[UserId] = @UserId
                    ORDER BY f.[CreatedAtUtc] DESC;";

                HashSet<int> recordedBorrowRecordIds = new();

                using (SqlCommand recCmd = new(recordedSql, conn))
                {
                    recCmd.Parameters.Add("@UserId", SqlDbType.Int).Value = targetUserId;
                    using SqlDataReader reader = recCmd.ExecuteReader();
                    while (reader.Read())
                    {
                        FineItemModel item = MapRecordedFine(reader);
                        items.Add(item);
                        recordedBorrowRecordIds.Add(item.BorrowRecordId);
                    }
                }

                // 2. Fetch active overdue loans that have NOT been officially recorded yet (Provisional / Estimated)
                const string activeOverdueSql = @"
                    SELECT
                        br.[BorrowRecordId],
                        br.[RecordCode],
                        br.[UserId],
                        u.[Username],
                        u.[FullName],
                        r.[ReaderCardId],
                        br.[BookId],
                        b.[BookCode],
                        b.[Title],
                        br.[BorrowedAtUtc],
                        br.[DueAtUtc]
                    FROM [dbo].[BorrowRecords] br
                    INNER JOIN [dbo].[Books] b ON b.[BookId] = br.[BookId]
                    INNER JOIN [dbo].[Users] u ON u.[UserId] = br.[UserId]
                    LEFT JOIN [dbo].[Readers] r ON r.[UserId] = br.[UserId]
                    WHERE br.[UserId] = @UserId
                      AND br.[ReturnedAtUtc] IS NULL
                      AND br.[DueAtUtc] < SYSUTCDATETIME()
                    ORDER BY br.[DueAtUtc] ASC;";

                using (SqlCommand estCmd = new(activeOverdueSql, conn))
                {
                    estCmd.Parameters.Add("@UserId", SqlDbType.Int).Value = targetUserId;
                    using SqlDataReader reader = estCmd.ExecuteReader();
                    while (reader.Read())
                    {
                        int borrowRecordId = reader.GetInt32(0);
                        if (recordedBorrowRecordIds.Contains(borrowRecordId))
                        {
                            continue; // Skip if already officially recorded in dbo.Fines
                        }

                        DateTime dueUtc = DateTime.SpecifyKind(reader.GetDateTime(10), DateTimeKind.Utc);
                        int daysOverdue = CalculateOverdueDays(dueUtc);
                        if (daysOverdue <= 0)
                        {
                            continue;
                        }

                        decimal estimatedAmount = daysOverdue * currentRate;
                        DateTime borrowedUtc = DateTime.SpecifyKind(reader.GetDateTime(9), DateTimeKind.Utc);

                        items.Add(new FineItemModel
                        {
                            FineId = null,
                            FineCode = $"EST-{reader.GetString(1)}",
                            BorrowRecordId = borrowRecordId,
                            BorrowRecordCode = reader.GetString(1),
                            UserId = reader.GetInt32(2),
                            Username = reader.GetString(3),
                            ReaderFullName = reader.GetString(4),
                            ReaderCardId = reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
                            BookId = reader.GetInt32(6),
                            BookCode = reader.GetString(7),
                            BookTitle = reader.GetString(8),
                            BorrowedAtLocal = borrowedUtc.ToLocalTime(),
                            DueAtLocal = dueUtc.ToLocalTime(),
                            ReturnedAtLocal = null,
                            FineType = "Overdue",
                            DaysOverdue = daysOverdue,
                            DailyRateVnd = currentRate,
                            Amount = estimatedAmount,
                            FineStatus = "Estimated",
                            IsEstimatedOnly = true,
                            CreatedAtLocal = DateTime.Now,
                            Notes = LocalizationService.IsVietnamese
                                ? "Tiền phạt dự kiến tính đến hôm nay (sách chưa trả)."
                                : "Provisional fine calculated to date (unreturned book)."
                        });
                    }
                }

                summaryStats = AggregateStats(items, currentRate);
            }
            catch (SqlException ex)
            {
                AppDiagnosticLogger.LogDatabaseException(nameof(FineManagementService), nameof(TryGetFinesForUser), ex, $"UserId={targetUserId}");
                errorMessage = LocalizationService.IsVietnamese
                    ? $"Lỗi đọc dữ liệu tiền phạt từ SQL Server: {ex.Message}"
                    : $"Error loading fines from SQL Server: {ex.Message}";
            }

            return items;
        }

        // =====================================================================
        // 3. STAFF FINE WORKSPACE (Librarian & Administrator)
        // =====================================================================

        public IReadOnlyList<FineItemModel> GetStaffFinesWorkspace(
            string? searchKeyword,
            string? statusFilter,
            out FineSummaryStatsModel summaryStats,
            out string? errorMessage)
        {
            errorMessage = null;
            decimal currentRate = GetCurrentDailyOverdueRateVnd();
            summaryStats = new FineSummaryStatsModel { CurrentDailyOverdueRateVnd = currentRate };

            if (!_authorizationService.TryAuthorizeOperation(AppPermission.ManageAllFines, "GetStaffFinesWorkspace()", out string authMsg))
            {
                errorMessage = authMsg;
                return [];
            }

            string keyword = searchKeyword?.Trim() ?? string.Empty;
            string status = statusFilter?.Trim() ?? "ALL";
            List<FineItemModel> items = new();

            try
            {
                using SqlConnection conn = new(DatabaseConfig.GetConnectionString());
                conn.Open();

                // 1. Fetch officially recorded fines
                const string recordedSql = @"
                    SELECT
                        f.[FineId],
                        f.[FineCode],
                        f.[BorrowRecordId],
                        br.[RecordCode],
                        f.[UserId],
                        u.[Username],
                        u.[FullName],
                        r.[ReaderCardId],
                        br.[BookId],
                        b.[BookCode],
                        b.[Title],
                        br.[BorrowedAtUtc],
                        br.[DueAtUtc],
                        br.[ReturnedAtUtc],
                        f.[FineType],
                        f.[DaysOverdue],
                        f.[DailyRateVnd],
                        f.[Amount],
                        f.[FineStatus],
                        f.[CreatedAtUtc],
                        f.[UpdatedAtUtc],
                        f.[PaidAtUtc],
                        f.[WaivedAtUtc],
                        ISNULL(res.[Username], N''),
                        ISNULL(f.[Notes], N'')
                    FROM [dbo].[Fines] f
                    INNER JOIN [dbo].[BorrowRecords] br ON br.[BorrowRecordId] = f.[BorrowRecordId]
                    INNER JOIN [dbo].[Books] b ON b.[BookId] = br.[BookId]
                    INNER JOIN [dbo].[Users] u ON u.[UserId] = f.[UserId]
                    LEFT JOIN [dbo].[Readers] r ON r.[UserId] = f.[UserId]
                    LEFT JOIN [dbo].[Users] res ON res.[UserId] = f.[ResolvedByUserId]
                    ORDER BY f.[CreatedAtUtc] DESC;";

                HashSet<int> recordedBorrowRecordIds = new();

                using (SqlCommand recCmd = new(recordedSql, conn))
                using (SqlDataReader reader = recCmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        FineItemModel item = MapRecordedFine(reader);
                        items.Add(item);
                        recordedBorrowRecordIds.Add(item.BorrowRecordId);
                    }
                }

                // 2. Fetch active overdue loans that have not been recorded yet (Estimated)
                const string activeOverdueSql = @"
                    SELECT
                        br.[BorrowRecordId],
                        br.[RecordCode],
                        br.[UserId],
                        u.[Username],
                        u.[FullName],
                        r.[ReaderCardId],
                        br.[BookId],
                        b.[BookCode],
                        b.[Title],
                        br.[BorrowedAtUtc],
                        br.[DueAtUtc]
                    FROM [dbo].[BorrowRecords] br
                    INNER JOIN [dbo].[Books] b ON b.[BookId] = br.[BookId]
                    INNER JOIN [dbo].[Users] u ON u.[UserId] = br.[UserId]
                    LEFT JOIN [dbo].[Readers] r ON r.[UserId] = br.[UserId]
                    WHERE br.[ReturnedAtUtc] IS NULL
                      AND br.[DueAtUtc] < SYSUTCDATETIME()
                    ORDER BY br.[DueAtUtc] ASC;";

                using (SqlCommand estCmd = new(activeOverdueSql, conn))
                using (SqlDataReader reader = estCmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        int borrowRecordId = reader.GetInt32(0);
                        if (recordedBorrowRecordIds.Contains(borrowRecordId))
                        {
                            continue;
                        }

                        DateTime dueUtc = DateTime.SpecifyKind(reader.GetDateTime(10), DateTimeKind.Utc);
                        int daysOverdue = CalculateOverdueDays(dueUtc);
                        if (daysOverdue <= 0)
                        {
                            continue;
                        }

                        decimal estimatedAmount = daysOverdue * currentRate;
                        DateTime borrowedUtc = DateTime.SpecifyKind(reader.GetDateTime(9), DateTimeKind.Utc);

                        items.Add(new FineItemModel
                        {
                            FineId = null,
                            FineCode = $"EST-{reader.GetString(1)}",
                            BorrowRecordId = borrowRecordId,
                            BorrowRecordCode = reader.GetString(1),
                            UserId = reader.GetInt32(2),
                            Username = reader.GetString(3),
                            ReaderFullName = reader.GetString(4),
                            ReaderCardId = reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
                            BookId = reader.GetInt32(6),
                            BookCode = reader.GetString(7),
                            BookTitle = reader.GetString(8),
                            BorrowedAtLocal = borrowedUtc.ToLocalTime(),
                            DueAtLocal = dueUtc.ToLocalTime(),
                            ReturnedAtLocal = null,
                            FineType = "Overdue",
                            DaysOverdue = daysOverdue,
                            DailyRateVnd = currentRate,
                            Amount = estimatedAmount,
                            FineStatus = "Estimated",
                            IsEstimatedOnly = true,
                            CreatedAtLocal = DateTime.Now,
                            Notes = LocalizationService.IsVietnamese
                                ? "Tiền phạt dự kiến tính đến hôm nay (sách chưa trả)."
                                : "Provisional fine calculated to date (unreturned book)."
                        });
                    }
                }

                summaryStats = AggregateStats(items, currentRate);

                // Apply in-memory filtering for search keyword & status
                IEnumerable<FineItemModel> filtered = items;

                if (!string.IsNullOrWhiteSpace(keyword))
                {
                    filtered = filtered.Where(f =>
                        f.BorrowRecordCode.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                        f.FineCode.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                        f.Username.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                        f.ReaderFullName.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                        f.ReaderCardId.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                        f.BookTitle.Contains(keyword, StringComparison.OrdinalIgnoreCase));
                }

                if (!string.Equals(status, "ALL", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.Equals(status, "ESTIMATED", StringComparison.OrdinalIgnoreCase))
                    {
                        filtered = filtered.Where(f => f.IsEstimatedOnly);
                    }
                    else
                    {
                        filtered = filtered.Where(f => !f.IsEstimatedOnly && string.Equals(f.FineStatus, status, StringComparison.OrdinalIgnoreCase));
                    }
                }

                return filtered.ToList();
            }
            catch (SqlException ex)
            {
                AppDiagnosticLogger.LogDatabaseException(nameof(FineManagementService), nameof(GetStaffFinesWorkspace), ex);
                errorMessage = LocalizationService.IsVietnamese
                    ? $"Lỗi tải dữ liệu tiền phạt: {ex.Message}"
                    : $"Error loading fines: {ex.Message}";
                return [];
            }
        }

        public IReadOnlyList<FineAuditLogItemModel> GetFineAuditLogs(int fineId)
        {
            if (!_authorizationService.HasPermission(AppPermission.ManageAllFines) || fineId <= 0)
            {
                return [];
            }

            List<FineAuditLogItemModel> logs = new();

            try
            {
                using SqlConnection conn = new(DatabaseConfig.GetConnectionString());
                conn.Open();

                const string sql = @"
                    SELECT
                        [AuditLogId],
                        [FineId],
                        [BorrowRecordId],
                        [ActionType],
                        [PreviousAmount],
                        [NewAmount],
                        [PreviousStatus],
                        [NewStatus],
                        [Reason],
                        [PerformedByUsername],
                        [CreatedAtUtc]
                    FROM [dbo].[FineAuditLogs]
                    WHERE [FineId] = @FineId
                    ORDER BY [CreatedAtUtc] DESC, [AuditLogId] DESC;";

                using SqlCommand cmd = new(sql, conn);
                cmd.Parameters.Add("@FineId", SqlDbType.Int).Value = fineId;

                using SqlDataReader reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    DateTime createdUtc = DateTime.SpecifyKind(reader.GetDateTime(10), DateTimeKind.Utc);
                    logs.Add(new FineAuditLogItemModel
                    {
                        AuditLogId = reader.GetInt32(0),
                        FineId = reader.GetInt32(1),
                        BorrowRecordId = reader.GetInt32(2),
                        ActionType = reader.GetString(3),
                        PreviousAmount = reader.IsDBNull(4) ? null : reader.GetDecimal(4),
                        NewAmount = reader.GetDecimal(5),
                        PreviousStatus = reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
                        NewStatus = reader.GetString(7),
                        Reason = reader.GetString(8),
                        PerformedByUsername = reader.GetString(9),
                        CreatedAtLocal = createdUtc.ToLocalTime()
                    });
                }
            }
            catch (SqlException ex)
            {
                AppDiagnosticLogger.LogDatabaseException(nameof(FineManagementService), nameof(GetFineAuditLogs), ex, $"FineId={fineId}");
            }

            return logs;
        }

        // =====================================================================
        // 4. FINE ACTIONS (Confirm, Adjust, Waive)
        // =====================================================================

        /// <summary>
        /// Explicitly confirms/records an overdue loan into dbo.Fines with FineStatus = 'Unpaid'.
        /// Enforces unique constraint UQ_Fines_BorrowRecord_FineType so duplicate records are never created.
        /// </summary>
        public ServiceOperationResult ConfirmOrRecordOverdueFine(int borrowRecordId)
        {
            if (!_authorizationService.TryAuthorizeOperation(AppPermission.ManageAllFines, "ConfirmOrRecordOverdueFine()", out string authMsg))
            {
                return ServiceOperationResult.Fail(authMsg);
            }

            if (borrowRecordId <= 0)
            {
                return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                    ? "Vui lòng chọn một phiếu mượn hợp lệ."
                    : "Please select a valid loan record.");
            }

            int staffUserId = _sessionService.CurrentSession!.UserId;
            string staffUsername = _sessionService.CurrentSession!.Username;
            decimal dailyRate = GetCurrentDailyOverdueRateVnd();

            try
            {
                using SqlConnection conn = new(DatabaseConfig.GetConnectionString());
                conn.Open();
                using SqlTransaction tx = conn.BeginTransaction();

                // 1. Check existing fine
                const string checkExistingSql = @"
                    SELECT [FineId], [FineCode], [FineStatus]
                    FROM [dbo].[Fines] WITH (UPDLOCK)
                    WHERE [BorrowRecordId] = @BorrowRecordId AND [FineType] = N'Overdue';";

                using (SqlCommand checkCmd = new(checkExistingSql, conn, tx))
                {
                    checkCmd.Parameters.Add("@BorrowRecordId", SqlDbType.Int).Value = borrowRecordId;
                    using SqlDataReader rdr = checkCmd.ExecuteReader();
                    if (rdr.Read())
                    {
                        string existingCode = rdr.GetString(1);
                        string existingStatus = rdr.GetString(2);
                        rdr.Close();
                        tx.Rollback();
                        return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                            ? $"[Chặn trùng lặp] Phiếu mượn này đã có khoản phạt '{existingCode}' (Trạng thái: {existingStatus}) trong cơ sở dữ liệu."
                            : $"[Duplicate Blocked] A fine record '{existingCode}' ({existingStatus}) already exists for this loan.");
                    }
                }

                // 2. Read borrow record details
                const string getBorrowSql = @"
                    SELECT [UserId], [DueAtUtc], [ReturnedAtUtc], [RecordCode]
                    FROM [dbo].[BorrowRecords] WITH (UPDLOCK)
                    WHERE [BorrowRecordId] = @BorrowRecordId;";

                int readerUserId;
                DateTime dueUtc;
                DateTime? returnedUtc = null;
                string recordCode;

                using (SqlCommand bCmd = new(getBorrowSql, conn, tx))
                {
                    bCmd.Parameters.Add("@BorrowRecordId", SqlDbType.Int).Value = borrowRecordId;
                    using SqlDataReader bRdr = bCmd.ExecuteReader();
                    if (!bRdr.Read())
                    {
                        bRdr.Close();
                        tx.Rollback();
                        return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                            ? "Không tìm thấy phiếu mượn trong cơ sở dữ liệu."
                            : "Borrow record was not found.");
                    }

                    readerUserId = bRdr.GetInt32(0);
                    dueUtc = DateTime.SpecifyKind(bRdr.GetDateTime(1), DateTimeKind.Utc);
                    if (!bRdr.IsDBNull(2))
                    {
                        returnedUtc = DateTime.SpecifyKind(bRdr.GetDateTime(2), DateTimeKind.Utc);
                    }
                    recordCode = bRdr.GetString(3);
                }

                int daysOverdue = CalculateOverdueDays(dueUtc, returnedUtc);
                if (daysOverdue <= 0)
                {
                    tx.Rollback();
                    return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                        ? "Phiếu mượn chưa đến hạn hoặc đã được trả đúng hạn; số ngày quá hạn bằng 0 nên không phát sinh tiền phạt."
                        : "Loan is not overdue; days overdue is 0 so no fine is generated.");
                }

                decimal amount = daysOverdue * dailyRate;
                string fineCode = GenerateNextFineCode(conn, tx);

                const string insertFineSql = @"
                    INSERT INTO [dbo].[Fines]
                        ([FineCode], [BorrowRecordId], [UserId], [FineType], [DaysOverdue], [DailyRateVnd], [Amount], [FineStatus], [CreatedAtUtc], [Notes])
                    OUTPUT INSERTED.[FineId]
                    VALUES
                        (@FineCode, @BorrowRecordId, @UserId, N'Overdue', @DaysOverdue, @DailyRateVnd, @Amount, N'Unpaid', SYSUTCDATETIME(), @Notes);";

                int newFineId;
                using (SqlCommand insCmd = new(insertFineSql, conn, tx))
                {
                    insCmd.Parameters.Add("@FineCode", SqlDbType.NVarChar, 30).Value = fineCode;
                    insCmd.Parameters.Add("@BorrowRecordId", SqlDbType.Int).Value = borrowRecordId;
                    insCmd.Parameters.Add("@UserId", SqlDbType.Int).Value = readerUserId;
                    insCmd.Parameters.Add("@DaysOverdue", SqlDbType.Int).Value = daysOverdue;
                    insCmd.Parameters.Add("@DailyRateVnd", SqlDbType.Decimal).Value = dailyRate;
                    insCmd.Parameters.Add("@Amount", SqlDbType.Decimal).Value = amount;
                    insCmd.Parameters.Add("@Notes", SqlDbType.NVarChar, 500).Value = $"Xác nhận ghi nhận phạt quá hạn cho phiếu {recordCode} ({daysOverdue} ngày).";

                    newFineId = Convert.ToInt32(insCmd.ExecuteScalar());
                }

                InsertAuditLog(conn, tx, newFineId, borrowRecordId, "Recorded", null, amount, null, "Unpaid",
                    $"Thủ thư {staffUsername} xác nhận ghi nhận khoản phạt quá hạn {amount:N0} VNĐ ({daysOverdue} ngày).",
                    staffUserId, staffUsername);

                tx.Commit();
                FinesChanged?.Invoke(this, EventArgs.Empty);

                return ServiceOperationResult.Ok(LocalizationService.IsVietnamese
                    ? $"[Ghi nhận thành công] Đã lập khoản phạt chính thức {fineCode} cho phiếu {recordCode} ({amount:N0} VNĐ • {daysOverdue} ngày quá hạn)."
                    : $"[Fine Recorded] Created official fine {fineCode} for loan {recordCode} ({amount:N0} VND • {daysOverdue} day(s) overdue).");
            }
            catch (SqlException ex)
            {
                AppDiagnosticLogger.LogDatabaseException(nameof(FineManagementService), nameof(ConfirmOrRecordOverdueFine), ex, $"BorrowRecordId={borrowRecordId}");
                return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                    ? $"Lỗi SQL Server khi ghi nhận khoản phạt: {ex.Message}"
                    : $"SQL Server error while recording fine: {ex.Message}");
            }
        }

        /// <summary>
        /// Adjusts an existing unpaid fine amount with a mandatory recorded reason.
        /// </summary>
        public ServiceOperationResult AdjustFineAmount(int fineId, decimal newAmountVnd, string reason)
        {
            if (!_authorizationService.TryAuthorizeOperation(AppPermission.ManageAllFines, "AdjustFineAmount()", out string authMsg))
            {
                return ServiceOperationResult.Fail(authMsg);
            }

            string trimmedReason = reason?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(trimmedReason))
            {
                return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                    ? "Vui lòng nhập lý do cụ thể cho việc điều chỉnh số tiền phạt (Bắt buộc)."
                    : "Please specify a mandatory reason for adjusting the fine amount.");
            }

            if (newAmountVnd < 0 || newAmountVnd > 50_000_000m)
            {
                return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                    ? "Số tiền phạt điều chỉnh phải là số không âm và không vượt quá 50.000.000 VNĐ."
                    : "Adjusted fine amount must be a non-negative number up to 50,000,000 VND.");
            }

            int staffUserId = _sessionService.CurrentSession!.UserId;
            string staffUsername = _sessionService.CurrentSession!.Username;

            try
            {
                using SqlConnection conn = new(DatabaseConfig.GetConnectionString());
                conn.Open();
                using SqlTransaction tx = conn.BeginTransaction();

                const string lockFineSql = @"
                    SELECT [FineCode], [BorrowRecordId], [Amount], [FineStatus]
                    FROM [dbo].[Fines] WITH (UPDLOCK)
                    WHERE [FineId] = @FineId;";

                string fineCode;
                int borrowRecordId;
                decimal currentAmount;
                string currentStatus;

                using (SqlCommand getCmd = new(lockFineSql, conn, tx))
                {
                    getCmd.Parameters.Add("@FineId", SqlDbType.Int).Value = fineId;
                    using SqlDataReader rdr = getCmd.ExecuteReader();
                    if (!rdr.Read())
                    {
                        rdr.Close();
                        tx.Rollback();
                        return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                            ? "Không tìm thấy khoản phạt trong cơ sở dữ liệu."
                            : "Fine record was not found.");
                    }

                    fineCode = rdr.GetString(0);
                    borrowRecordId = rdr.GetInt32(1);
                    currentAmount = rdr.GetDecimal(2);
                    currentStatus = rdr.GetString(3);
                }

                if (!string.Equals(currentStatus, "Unpaid", StringComparison.OrdinalIgnoreCase))
                {
                    tx.Rollback();
                    return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                        ? $"Chỉ được điều chỉnh các khoản phạt đang ở trạng thái Chưa thanh toán (Khoản phạt {fineCode} hiện ở trạng thái: {currentStatus})."
                        : $"Only Unpaid fines can be adjusted (Fine {fineCode} is currently: {currentStatus}).");
                }

                const string updateSql = @"
                    UPDATE [dbo].[Fines]
                    SET [Amount]           = @NewAmount,
                        [UpdatedAtUtc]     = SYSUTCDATETIME(),
                        [ResolvedByUserId] = @StaffUserId,
                        [Notes]            = @Notes
                    WHERE [FineId] = @FineId;";

                using (SqlCommand updCmd = new(updateSql, conn, tx))
                {
                    updCmd.Parameters.Add("@NewAmount", SqlDbType.Decimal).Value = newAmountVnd;
                    updCmd.Parameters.Add("@StaffUserId", SqlDbType.Int).Value = staffUserId;
                    updCmd.Parameters.Add("@Notes", SqlDbType.NVarChar, 500).Value = $"Điều chỉnh số tiền: {currentAmount:N0} VNĐ -> {newAmountVnd:N0} VNĐ. Lý do: {trimmedReason}";
                    updCmd.Parameters.Add("@FineId", SqlDbType.Int).Value = fineId;
                    updCmd.ExecuteNonQuery();
                }

                InsertAuditLog(conn, tx, fineId, borrowRecordId, "Adjusted", currentAmount, newAmountVnd,
                    currentStatus, currentStatus, trimmedReason, staffUserId, staffUsername);

                tx.Commit();
                FinesChanged?.Invoke(this, EventArgs.Empty);

                return ServiceOperationResult.Ok(LocalizationService.IsVietnamese
                    ? $"[Điều chỉnh thành công] Đã cập nhật số tiền phạt của {fineCode} từ {currentAmount:N0} VNĐ thành {newAmountVnd:N0} VNĐ."
                    : $"[Fine Adjusted] Updated amount for {fineCode} from {currentAmount:N0} VND to {newAmountVnd:N0} VND.");
            }
            catch (SqlException ex)
            {
                AppDiagnosticLogger.LogDatabaseException(nameof(FineManagementService), nameof(AdjustFineAmount), ex, $"FineId={fineId}");
                return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                    ? $"Lỗi SQL Server khi điều chỉnh khoản phạt: {ex.Message}"
                    : $"SQL Server error while adjusting fine: {ex.Message}");
            }
        }

        /// <summary>
        /// Waives an existing unpaid fine with a mandatory recorded reason.
        /// </summary>
        public ServiceOperationResult WaiveFine(int fineId, string reason)
        {
            if (!_authorizationService.TryAuthorizeOperation(AppPermission.ManageAllFines, "WaiveFine()", out string authMsg))
            {
                return ServiceOperationResult.Fail(authMsg);
            }

            string trimmedReason = reason?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(trimmedReason))
            {
                return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                    ? "Vui lòng nhập lý do cụ thể cho việc miễn khoản phạt (Bắt buộc)."
                    : "Please specify a mandatory reason for waiving the fine.");
            }

            int staffUserId = _sessionService.CurrentSession!.UserId;
            string staffUsername = _sessionService.CurrentSession!.Username;

            try
            {
                using SqlConnection conn = new(DatabaseConfig.GetConnectionString());
                conn.Open();
                using SqlTransaction tx = conn.BeginTransaction();

                const string lockFineSql = @"
                    SELECT [FineCode], [BorrowRecordId], [Amount], [FineStatus]
                    FROM [dbo].[Fines] WITH (UPDLOCK)
                    WHERE [FineId] = @FineId;";

                string fineCode;
                int borrowRecordId;
                decimal currentAmount;
                string currentStatus;

                using (SqlCommand getCmd = new(lockFineSql, conn, tx))
                {
                    getCmd.Parameters.Add("@FineId", SqlDbType.Int).Value = fineId;
                    using SqlDataReader rdr = getCmd.ExecuteReader();
                    if (!rdr.Read())
                    {
                        rdr.Close();
                        tx.Rollback();
                        return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                            ? "Không tìm thấy khoản phạt trong cơ sở dữ liệu."
                            : "Fine record was not found.");
                    }

                    fineCode = rdr.GetString(0);
                    borrowRecordId = rdr.GetInt32(1);
                    currentAmount = rdr.GetDecimal(2);
                    currentStatus = rdr.GetString(3);
                }

                if (!string.Equals(currentStatus, "Unpaid", StringComparison.OrdinalIgnoreCase))
                {
                    tx.Rollback();
                    return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                        ? $"Khoản phạt {fineCode} hiện ở trạng thái '{currentStatus}', không thể thực hiện miễn phạt."
                        : $"Fine {fineCode} is currently '{currentStatus}' and cannot be waived.");
                }

                const string updateSql = @"
                    UPDATE [dbo].[Fines]
                    SET [FineStatus]       = N'Waived',
                        [WaivedAtUtc]      = SYSUTCDATETIME(),
                        [UpdatedAtUtc]     = SYSUTCDATETIME(),
                        [ResolvedByUserId] = @StaffUserId,
                        [Notes]            = @Notes
                    WHERE [FineId] = @FineId;";

                using (SqlCommand updCmd = new(updateSql, conn, tx))
                {
                    updCmd.Parameters.Add("@StaffUserId", SqlDbType.Int).Value = staffUserId;
                    updCmd.Parameters.Add("@Notes", SqlDbType.NVarChar, 500).Value = $"Miễn phạt {currentAmount:N0} VNĐ. Lý do: {trimmedReason}";
                    updCmd.Parameters.Add("@FineId", SqlDbType.Int).Value = fineId;
                    updCmd.ExecuteNonQuery();
                }

                InsertAuditLog(conn, tx, fineId, borrowRecordId, "Waived", currentAmount, currentAmount,
                    currentStatus, "Waived", trimmedReason, staffUserId, staffUsername);

                tx.Commit();
                FinesChanged?.Invoke(this, EventArgs.Empty);

                return ServiceOperationResult.Ok(LocalizationService.IsVietnamese
                    ? $"[Miễn phạt thành công] Đã chuyển khoản phạt {fineCode} ({currentAmount:N0} VNĐ) sang trạng thái Được miễn phạt."
                    : $"[Fine Waived] Fine {fineCode} ({currentAmount:N0} VND) has been marked as Waived.");
            }
            catch (SqlException ex)
            {
                AppDiagnosticLogger.LogDatabaseException(nameof(FineManagementService), nameof(WaiveFine), ex, $"FineId={fineId}");
                return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                    ? $"Lỗi SQL Server khi miễn khoản phạt: {ex.Message}"
                    : $"SQL Server error while waiving fine: {ex.Message}");
            }
        }

        /// <summary>
        /// Confirms payment collection for an existing unpaid fine (Mark as Paid).
        /// Records the payment method (Tiền mặt / Chuyển khoản), sets FineStatus = 'Paid',
        /// updates PaidAtUtc, and inserts an audit trail record into dbo.FineAuditLogs.
        /// </summary>
        public ServiceOperationResult MarkFineAsPaid(int fineId, string paymentMethod, string? notes = null)
        {
            if (!_authorizationService.TryAuthorizeOperation(AppPermission.ManageAllFines, "MarkFineAsPaid()", out string authMsg))
            {
                return ServiceOperationResult.Fail(authMsg);
            }

            if (fineId <= 0)
            {
                return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                    ? "Vui lòng chọn khoản phạt hợp lệ cần thu tiền."
                    : "Please select a valid fine record to mark as paid.");
            }

            string trimmedMethod = paymentMethod?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(trimmedMethod))
            {
                return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                    ? "Vui lòng chọn hình thức thanh toán (Tiền mặt hoặc Chuyển khoản)."
                    : "Please select a payment method (Cash or Bank Transfer).");
            }

            string normalizedMethod = trimmedMethod switch
            {
                var m when m.Contains("Chuyển khoản", StringComparison.OrdinalIgnoreCase) ||
                           m.Contains("Transfer", StringComparison.OrdinalIgnoreCase) ||
                           m.Contains("Bank", StringComparison.OrdinalIgnoreCase) => "Chuyển khoản",
                _ => "Tiền mặt"
            };

            string trimmedNotes = notes?.Trim() ?? string.Empty;
            int staffUserId = _sessionService.CurrentSession!.UserId;
            string staffUsername = _sessionService.CurrentSession!.Username;

            try
            {
                using SqlConnection conn = new(DatabaseConfig.GetConnectionString());
                conn.Open();
                using SqlTransaction tx = conn.BeginTransaction();

                const string lockFineSql = @"
                    SELECT [FineCode], [BorrowRecordId], [Amount], [FineStatus]
                    FROM [dbo].[Fines] WITH (UPDLOCK)
                    WHERE [FineId] = @FineId;";

                string fineCode;
                int borrowRecordId;
                decimal currentAmount;
                string currentStatus;

                using (SqlCommand getCmd = new(lockFineSql, conn, tx))
                {
                    getCmd.Parameters.Add("@FineId", SqlDbType.Int).Value = fineId;
                    using SqlDataReader rdr = getCmd.ExecuteReader();
                    if (!rdr.Read())
                    {
                        rdr.Close();
                        tx.Rollback();
                        return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                            ? "Không tìm thấy khoản phạt trong cơ sở dữ liệu."
                            : "Fine record was not found.");
                    }

                    fineCode = rdr.GetString(0);
                    borrowRecordId = rdr.GetInt32(1);
                    currentAmount = rdr.GetDecimal(2);
                    currentStatus = rdr.GetString(3);
                }

                if (!string.Equals(currentStatus, "Unpaid", StringComparison.OrdinalIgnoreCase))
                {
                    tx.Rollback();
                    return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                        ? $"Chỉ có thể thu tiền đối với khoản phạt đang ở trạng thái Chưa thanh toán (Khoản phạt {fineCode} hiện ở trạng thái: {currentStatus})."
                        : $"Only Unpaid fines can be marked as Paid (Fine {fineCode} is currently: {currentStatus}).");
                }

                string paymentNote = string.IsNullOrWhiteSpace(trimmedNotes)
                    ? $"Đã thu tiền phạt {currentAmount:N0} VNĐ ({normalizedMethod})."
                    : $"Đã thu tiền phạt {currentAmount:N0} VNĐ ({normalizedMethod}). Ghi chú: {trimmedNotes}";

                string auditReason = string.IsNullOrWhiteSpace(trimmedNotes)
                    ? $"Xác nhận đã thu tiền phạt ({normalizedMethod}) số tiền {currentAmount:N0} VNĐ."
                    : $"Xác nhận đã thu tiền phạt ({normalizedMethod}) số tiền {currentAmount:N0} VNĐ — {trimmedNotes}";

                const string updateSql = @"
                    UPDATE [dbo].[Fines]
                    SET [FineStatus]       = N'Paid',
                        [PaidAtUtc]        = SYSUTCDATETIME(),
                        [UpdatedAtUtc]     = SYSUTCDATETIME(),
                        [ResolvedByUserId] = @StaffUserId,
                        [Notes]            = @Notes
                    WHERE [FineId] = @FineId;";

                using (SqlCommand updCmd = new(updateSql, conn, tx))
                {
                    updCmd.Parameters.Add("@StaffUserId", SqlDbType.Int).Value = staffUserId;
                    updCmd.Parameters.Add("@Notes", SqlDbType.NVarChar, 500).Value = paymentNote;
                    updCmd.Parameters.Add("@FineId", SqlDbType.Int).Value = fineId;
                    updCmd.ExecuteNonQuery();
                }

                InsertAuditLog(conn, tx, fineId, borrowRecordId, "Paid", currentAmount, currentAmount,
                    currentStatus, "Paid", auditReason, staffUserId, staffUsername);

                tx.Commit();
                FinesChanged?.Invoke(this, EventArgs.Empty);

                return ServiceOperationResult.Ok(LocalizationService.IsVietnamese
                    ? $"[Thu tiền phạt thành công] Đã xác nhận thanh toán khoản phạt {fineCode} ({currentAmount:N0} VNĐ • Hình thức: {normalizedMethod}) và chuyển trạng thái sang Đã thanh toán (Paid)."
                    : $"[Payment Recorded] Fine {fineCode} ({currentAmount:N0} VND • Method: {normalizedMethod}) has been marked as Paid.");
            }
            catch (SqlException ex)
            {
                AppDiagnosticLogger.LogDatabaseException(nameof(FineManagementService), nameof(MarkFineAsPaid), ex, $"FineId={fineId}");
                return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                    ? $"Lỗi SQL Server khi xác nhận thu tiền phạt: {ex.Message}"
                    : $"SQL Server error while marking fine as paid: {ex.Message}");
            }
        }

        // =====================================================================
        // 5. TRANSACTIONAL HOOK FOR BOOK RETURN (ProcessBookReturn Integration)
        // =====================================================================

        /// <summary>
        /// Upserts an overdue fine inside an existing transaction when a book is returned.
        /// If returned on time or early (daysOverdue == 0), no fine is created.
        /// If already exists and unpaid, updates the final daysOverdue and amount.
        /// If already waived/paid, leaves resolved status intact.
        /// Never creates a duplicate overdue fine for the same BorrowRecordId.
        /// </summary>
        public static void UpsertOverdueFineOnReturnInTransaction(
            SqlConnection conn,
            SqlTransaction tx,
            int borrowRecordId,
            int userId,
            DateTime dueAtUtc,
            DateTime returnedAtUtc,
            int staffUserId,
            string staffUsername)
        {
            int daysOverdue = CalculateOverdueDays(dueAtUtc, returnedAtUtc);
            if (daysOverdue <= 0)
            {
                return; // Returned on time or early: fine = 0, no record needed
            }

            decimal dailyRate = DefaultDailyOverdueRateVnd;
            const string rateSql = "SELECT TOP (1) [SettingValueDecimal] FROM [dbo].[FineSettings] WHERE [SettingKey] = @Key;";
            using (SqlCommand rateCmd = new(rateSql, conn, tx))
            {
                rateCmd.Parameters.Add("@Key", SqlDbType.NVarChar, 60).Value = SettingKeyOverdueRate;
                object? rObj = rateCmd.ExecuteScalar();
                if (rObj is not null && rObj != DBNull.Value && decimal.TryParse(rObj.ToString(), out decimal parsed))
                {
                    dailyRate = parsed;
                }
            }

            decimal finalAmount = daysOverdue * dailyRate;

            // Check if fine already exists
            const string checkSql = @"
                SELECT [FineId], [Amount], [FineStatus]
                FROM [dbo].[Fines] WITH (UPDLOCK, HOLDLOCK)
                WHERE [BorrowRecordId] = @BorrowRecordId AND [FineType] = N'Overdue';";

            int existingFineId = 0;
            decimal existingAmount = 0;
            string existingStatus = string.Empty;

            using (SqlCommand checkCmd = new(checkSql, conn, tx))
            {
                checkCmd.Parameters.Add("@BorrowRecordId", SqlDbType.Int).Value = borrowRecordId;
                using SqlDataReader rdr = checkCmd.ExecuteReader();
                if (rdr.Read())
                {
                    existingFineId = rdr.GetInt32(0);
                    existingAmount = rdr.GetDecimal(1);
                    existingStatus = rdr.GetString(2);
                }
            }

            if (existingFineId > 0)
            {
                // If existing fine is already Waived or Paid, do not alter its status
                if (string.Equals(existingStatus, "Unpaid", StringComparison.OrdinalIgnoreCase))
                {
                    const string updSql = @"
                        UPDATE [dbo].[Fines]
                        SET [DaysOverdue]  = @Days,
                            [DailyRateVnd] = @Rate,
                            [Amount]       = @Amount,
                            [UpdatedAtUtc] = SYSUTCDATETIME()
                        WHERE [FineId] = @FineId;";

                    using SqlCommand updCmd = new(updSql, conn, tx);
                    updCmd.Parameters.Add("@Days", SqlDbType.Int).Value = daysOverdue;
                    updCmd.Parameters.Add("@Rate", SqlDbType.Decimal).Value = dailyRate;
                    updCmd.Parameters.Add("@Amount", SqlDbType.Decimal).Value = finalAmount;
                    updCmd.Parameters.Add("@FineId", SqlDbType.Int).Value = existingFineId;
                    updCmd.ExecuteNonQuery();

                    InsertAuditLog(conn, tx, existingFineId, borrowRecordId, "UpdatedOnReturn", existingAmount, finalAmount,
                        "Unpaid", "Unpaid", $"Cập nhật khoản phạt khi hoàn tất trả sách ({daysOverdue} ngày quá hạn).",
                        staffUserId, staffUsername);
                }
            }
            else
            {
                string fineCode = GenerateNextFineCode(conn, tx);

                const string insSql = @"
                    INSERT INTO [dbo].[Fines]
                        ([FineCode], [BorrowRecordId], [UserId], [FineType], [DaysOverdue], [DailyRateVnd], [Amount], [FineStatus], [CreatedAtUtc], [Notes])
                    OUTPUT INSERTED.[FineId]
                    VALUES
                        (@FineCode, @BorrowRecordId, @UserId, N'Overdue', @Days, @Rate, @Amount, N'Unpaid', SYSUTCDATETIME(), @Notes);";

                int newId;
                using (SqlCommand insCmd = new(insSql, conn, tx))
                {
                    insCmd.Parameters.Add("@FineCode", SqlDbType.NVarChar, 30).Value = fineCode;
                    insCmd.Parameters.Add("@BorrowRecordId", SqlDbType.Int).Value = borrowRecordId;
                    insCmd.Parameters.Add("@UserId", SqlDbType.Int).Value = userId;
                    insCmd.Parameters.Add("@Days", SqlDbType.Int).Value = daysOverdue;
                    insCmd.Parameters.Add("@Rate", SqlDbType.Decimal).Value = dailyRate;
                    insCmd.Parameters.Add("@Amount", SqlDbType.Decimal).Value = finalAmount;
                    insCmd.Parameters.Add("@Notes", SqlDbType.NVarChar, 500).Value = $"Ghi nhận tự động khi trả sách muộn {daysOverdue} ngày.";

                    newId = Convert.ToInt32(insCmd.ExecuteScalar());
                }

                InsertAuditLog(conn, tx, newId, borrowRecordId, "RecordedOnReturn", null, finalAmount,
                    null, "Unpaid", $"Tự động ghi nhận phạt quá hạn {finalAmount:N0} VNĐ ({daysOverdue} ngày) khi độc giả trả sách.",
                    staffUserId, staffUsername);
            }
        }

        // =====================================================================
        // PRIVATE HELPERS
        // =====================================================================

        private static FineItemModel MapRecordedFine(SqlDataReader reader)
        {
            DateTime borrowedUtc = DateTime.SpecifyKind(reader.GetDateTime(11), DateTimeKind.Utc);
            DateTime dueUtc = DateTime.SpecifyKind(reader.GetDateTime(12), DateTimeKind.Utc);
            DateTime? returnedLocal = null;
            if (!reader.IsDBNull(13))
            {
                returnedLocal = DateTime.SpecifyKind(reader.GetDateTime(13), DateTimeKind.Utc).ToLocalTime();
            }

            DateTime createdUtc = DateTime.SpecifyKind(reader.GetDateTime(19), DateTimeKind.Utc);
            DateTime? updatedLocal = reader.IsDBNull(20) ? null : DateTime.SpecifyKind(reader.GetDateTime(20), DateTimeKind.Utc).ToLocalTime();
            DateTime? paidLocal = reader.IsDBNull(21) ? null : DateTime.SpecifyKind(reader.GetDateTime(21), DateTimeKind.Utc).ToLocalTime();
            DateTime? waivedLocal = reader.IsDBNull(22) ? null : DateTime.SpecifyKind(reader.GetDateTime(22), DateTimeKind.Utc).ToLocalTime();

            return new FineItemModel
            {
                FineId = reader.GetInt32(0),
                FineCode = reader.GetString(1),
                BorrowRecordId = reader.GetInt32(2),
                BorrowRecordCode = reader.GetString(3),
                UserId = reader.GetInt32(4),
                Username = reader.GetString(5),
                ReaderFullName = reader.GetString(6),
                ReaderCardId = reader.IsDBNull(7) ? string.Empty : reader.GetString(7),
                BookId = reader.GetInt32(8),
                BookCode = reader.GetString(9),
                BookTitle = reader.GetString(10),
                BorrowedAtLocal = borrowedUtc.ToLocalTime(),
                DueAtLocal = dueUtc.ToLocalTime(),
                ReturnedAtLocal = returnedLocal,
                FineType = reader.GetString(14),
                DaysOverdue = reader.GetInt32(15),
                DailyRateVnd = reader.GetDecimal(16),
                Amount = reader.GetDecimal(17),
                FineStatus = reader.GetString(18),
                IsEstimatedOnly = false,
                CreatedAtLocal = createdUtc.ToLocalTime(),
                UpdatedAtLocal = updatedLocal,
                PaidAtLocal = paidLocal,
                WaivedAtLocal = waivedLocal,
                ResolvedByUsername = reader.GetString(23),
                Notes = reader.GetString(24)
            };
        }

        private static FineSummaryStatsModel AggregateStats(IEnumerable<FineItemModel> items, decimal currentRate)
        {
            decimal unp = 0;
            decimal est = 0;
            decimal pd = 0;
            decimal wv = 0;
            int unpCnt = 0;
            int estCnt = 0;
            int pdCnt = 0;
            int wvCnt = 0;

            foreach (FineItemModel item in items)
            {
                if (item.IsEstimatedOnly)
                {
                    est += item.Amount;
                    estCnt++;
                }
                else if (string.Equals(item.FineStatus, "Unpaid", StringComparison.OrdinalIgnoreCase))
                {
                    unp += item.Amount;
                    unpCnt++;
                }
                else if (string.Equals(item.FineStatus, "Paid", StringComparison.OrdinalIgnoreCase))
                {
                    pd += item.Amount;
                    pdCnt++;
                }
                else if (string.Equals(item.FineStatus, "Waived", StringComparison.OrdinalIgnoreCase))
                {
                    wv += item.Amount;
                    wvCnt++;
                }
            }

            return new FineSummaryStatsModel
            {
                CurrentDailyOverdueRateVnd = currentRate,
                TotalRecordedUnpaidVnd = unp,
                TotalEstimatedOverdueVnd = est,
                TotalPaidVnd = pd,
                TotalWaivedVnd = wv,
                RecordedUnpaidCount = unpCnt,
                EstimatedOverdueCount = estCnt,
                PaidCount = pdCnt,
                WaivedCount = wvCnt
            };
        }

        private static string GenerateNextFineCode(SqlConnection conn, SqlTransaction tx)
        {
            const string sql = "SELECT ISNULL(MAX([FineId]), 0) + 1 FROM [dbo].[Fines] WITH (TABLOCKX);";
            using SqlCommand cmd = new(sql, conn, tx);
            int nextSeq = Convert.ToInt32(cmd.ExecuteScalar());
            return $"FN-{DateTime.UtcNow.Year}-{nextSeq:D4}";
        }

        private static void InsertAuditLog(
            SqlConnection conn,
            SqlTransaction tx,
            int fineId,
            int borrowRecordId,
            string actionType,
            decimal? previousAmount,
            decimal newAmount,
            string? previousStatus,
            string newStatus,
            string reason,
            int staffUserId,
            string staffUsername)
        {
            const string sql = @"
                INSERT INTO [dbo].[FineAuditLogs]
                    ([FineId], [BorrowRecordId], [ActionType], [PreviousAmount], [NewAmount], [PreviousStatus], [NewStatus], [Reason], [PerformedByUserId], [PerformedByUsername], [CreatedAtUtc])
                VALUES
                    (@FineId, @BorrowRecordId, @ActionType, @PreviousAmount, @NewAmount, @PreviousStatus, @NewStatus, @Reason, @StaffUserId, @StaffUsername, SYSUTCDATETIME());";

            using SqlCommand cmd = new(sql, conn, tx);
            cmd.Parameters.Add("@FineId", SqlDbType.Int).Value = fineId;
            cmd.Parameters.Add("@BorrowRecordId", SqlDbType.Int).Value = borrowRecordId;
            cmd.Parameters.Add("@ActionType", SqlDbType.NVarChar, 40).Value = actionType;
            cmd.Parameters.Add("@PreviousAmount", SqlDbType.Decimal).Value = previousAmount.HasValue ? previousAmount.Value : DBNull.Value;
            cmd.Parameters.Add("@NewAmount", SqlDbType.Decimal).Value = newAmount;
            cmd.Parameters.Add("@PreviousStatus", SqlDbType.NVarChar, 30).Value = string.IsNullOrWhiteSpace(previousStatus) ? DBNull.Value : previousStatus;
            cmd.Parameters.Add("@NewStatus", SqlDbType.NVarChar, 30).Value = newStatus;
            cmd.Parameters.Add("@Reason", SqlDbType.NVarChar, 500).Value = reason;
            cmd.Parameters.Add("@StaffUserId", SqlDbType.Int).Value = staffUserId;
            cmd.Parameters.Add("@StaffUsername", SqlDbType.NVarChar, 50).Value = staffUsername;
            cmd.ExecuteNonQuery();
        }
    }
}

