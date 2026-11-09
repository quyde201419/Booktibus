using System.Data;
using System.Text.RegularExpressions;
using Booktibus.Data;
using Booktibus.Helpers;
using Booktibus.Models;
using Microsoft.Data.SqlClient;

namespace Booktibus.Services
{
    /// <summary>
    /// Business and data access service for Stage 5 Reader features:
    /// Book Catalog Search, Book Details, Personal Favorites, Personal Borrowing Tracking, and Reader Profile.
    /// Strictly derives UserId from SessionService.CurrentSession and enforces service-layer authorization.
    /// </summary>
    public class ReaderWorkspaceService
    {
        private readonly SessionService _sessionService;
        private readonly AuthorizationService _authorizationService;

        public event EventHandler? FavoritesChanged;

        public event EventHandler? ProfileUpdated;

        public ReaderWorkspaceService(
            SessionService sessionService,
            AuthorizationService authorizationService)
        {
            ArgumentNullException.ThrowIfNull(sessionService);
            ArgumentNullException.ThrowIfNull(authorizationService);

            _sessionService = sessionService;
            _authorizationService = authorizationService;
        }

        // =====================================================================
        // 1. BOOK CATALOG SEARCH & DETAILS
        // =====================================================================

        public IReadOnlyList<string> GetDistinctCategories()
        {
            if (!_authorizationService.HasPermission(AppPermission.BrowseCatalog))
            {
                return [];
            }

            List<string> categories = new();

            try
            {
                using SqlConnection conn = new(DatabaseConfig.GetConnectionString());
                conn.Open();

                const string sql = @"
                    SELECT DISTINCT [Category]
                    FROM [dbo].[Books]
                    WHERE [Category] IS NOT NULL AND LTRIM(RTRIM([Category])) <> N''
                    ORDER BY [Category] ASC;";

                using SqlCommand cmd = new(sql, conn);
                using SqlDataReader reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    categories.Add(reader.GetString(0));
                }
            }
            catch
            {
                // Return empty list if database is temporarily unreachable
            }

            return categories;
        }

        /// <summary>
        /// Searches books in dbo.Books by keyword (Title or Author), category, and availability filter,
        /// marking whether each book is in the currently authenticated user's Favorites.
        /// </summary>
        public IReadOnlyList<BookItemModel> SearchBooks(
            string? searchKeyword,
            string? categoryFilter,
            string? availabilityFilter,
            out string? errorMessage)
        {
            errorMessage = null;

            if (!_authorizationService.TryAuthorizeOperation(AppPermission.BrowseCatalog, "SearchBooks()", out string authMsg))
            {
                errorMessage = authMsg;
                return [];
            }

            int currentUserId = _sessionService.CurrentSession!.UserId;
            string keyword = searchKeyword?.Trim() ?? string.Empty;
            string category = categoryFilter?.Trim() ?? string.Empty;
            string availability = availabilityFilter?.Trim() ?? "ALL";

            List<BookItemModel> results = new();

            try
            {
                using SqlConnection conn = new(DatabaseConfig.GetConnectionString());
                conn.Open();
                LibraryManagementService.EnsureCirculationSchema(conn);

                const string sql = @"
                    SELECT
                        b.[BookId],
                        b.[BookCode],
                        b.[Title],
                        b.[Author],
                        b.[Category],
                        ISNULL(b.[Publisher], N''),
                        b.[PublishYear],
                        ISNULL(b.[Isbn], N''),
                        b.[TotalCopies],
                        b.[AvailableCopies],
                        ISNULL(b.[ShelfLocation], N''),
                        ISNULL(b.[Description], N''),
                        b.[IsSampleData],
                        CASE WHEN rf.[FavoriteId] IS NOT NULL THEN CAST(1 AS BIT) ELSE CAST(0 AS BIT) END AS [IsFavorite],
                        (SELECT COUNT(1) FROM [dbo].[BookReservations] rsv WHERE rsv.[BookId] = b.[BookId] AND rsv.[ReservationStatus] IN (N'Pending', N'Ready')) AS [ActiveReservationsCount],
                        ISNULL((SELECT TOP (1) rsv2.[ReservationStatus] FROM [dbo].[BookReservations] rsv2 WHERE rsv2.[BookId] = b.[BookId] AND rsv2.[UserId] = @CurrentUserId AND rsv2.[ReservationStatus] IN (N'Pending', N'Ready') ORDER BY rsv2.[ReservedAtUtc] DESC), N'') AS [CurrentUserReservationStatus]
                    FROM [dbo].[Books] b
                    LEFT JOIN [dbo].[ReaderFavorites] rf
                        ON rf.[BookId] = b.[BookId] AND rf.[UserId] = @CurrentUserId
                    WHERE
                        (@Keyword = N''
                         OR b.[Title] LIKE N'%' + @Keyword + N'%'
                         OR b.[Author] LIKE N'%' + @Keyword + N'%'
                         OR b.[BookCode] LIKE N'%' + @Keyword + N'%'
                         OR b.[Isbn] LIKE N'%' + @Keyword + N'%')
                        AND (@Category = N'' OR b.[Category] = @Category)
                        AND (
                            @Availability = N'ALL'
                            OR (@Availability = N'AVAILABLE' AND b.[AvailableCopies] > 0)
                            OR (@Availability = N'OUT_OF_STOCK' AND b.[AvailableCopies] = 0)
                        )
                    ORDER BY b.[Title] ASC;";

                using SqlCommand cmd = new(sql, conn);
                cmd.Parameters.Add("@CurrentUserId", SqlDbType.Int).Value = currentUserId;
                cmd.Parameters.Add("@Keyword", SqlDbType.NVarChar, 200).Value = keyword;
                cmd.Parameters.Add("@Category", SqlDbType.NVarChar, 80).Value = category;
                cmd.Parameters.Add("@Availability", SqlDbType.NVarChar, 30).Value = availability;

                using SqlDataReader reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    results.Add(MapBookFromReader(reader));
                }
            }
            catch (SqlException ex)
            {
                AppDiagnosticLogger.LogDatabaseException(nameof(ReaderWorkspaceService), nameof(SearchBooks), ex, $"UserId={currentUserId}");
                errorMessage = LocalizationService.IsVietnamese
                    ? $"Không thể tải danh mục sách từ SQL Server: {ex.Message}"
                    : $"Failed to load book catalog from SQL Server: {ex.Message}";
            }

            return results;
        }

        // =====================================================================
        // 2. READER FAVORITES (Strictly Scoped to Authenticated Session UserId)
        // =====================================================================

        public IReadOnlyList<FavoriteBookItemModel> GetOwnFavorites(out string? errorMessage)
        {
            errorMessage = null;
            if (_sessionService.CurrentSession is not { } session)
            {
                errorMessage = LocalizationService.IsVietnamese
                    ? "Vui lòng đăng nhập để xem danh sách yêu thích."
                    : "Please sign in to view your favorites.";
                return [];
            }

            return TryGetFavoritesForUser(session.UserId, out errorMessage);
        }

        /// <summary>
        /// Retrieves favorites for a target UserId only after verifying service-layer account isolation.
        /// Ensures a Reader cannot read another Reader's favorites even if they share the same Email.
        /// </summary>
        public IReadOnlyList<FavoriteBookItemModel> TryGetFavoritesForUser(int targetUserId, out string? errorMessage)
        {
            errorMessage = null;

            if (!_authorizationService.CanAccessUserAccountData(targetUserId, out string denialMessage))
            {
                errorMessage = denialMessage;
                return [];
            }

            List<FavoriteBookItemModel> favorites = new();

            try
            {
                using SqlConnection conn = new(DatabaseConfig.GetConnectionString());
                conn.Open();

                const string sql = @"
                    SELECT
                        rf.[FavoriteId],
                        rf.[UserId],
                        rf.[AddedAtUtc],
                        b.[BookId],
                        b.[BookCode],
                        b.[Title],
                        b.[Author],
                        b.[Category],
                        ISNULL(b.[Publisher], N''),
                        b.[PublishYear],
                        ISNULL(b.[Isbn], N''),
                        b.[TotalCopies],
                        b.[AvailableCopies],
                        ISNULL(b.[ShelfLocation], N''),
                        ISNULL(b.[Description], N''),
                        b.[IsSampleData]
                    FROM [dbo].[ReaderFavorites] rf
                    INNER JOIN [dbo].[Books] b ON b.[BookId] = rf.[BookId]
                    WHERE rf.[UserId] = @UserId
                    ORDER BY rf.[AddedAtUtc] DESC;";

                using SqlCommand cmd = new(sql, conn);
                cmd.Parameters.Add("@UserId", SqlDbType.Int).Value = targetUserId;

                using SqlDataReader reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    int favoriteId = reader.GetInt32(0);
                    int userId = reader.GetInt32(1);
                    DateTime addedAtUtc = DateTime.SpecifyKind(reader.GetDateTime(2), DateTimeKind.Utc);

                    BookItemModel book = new()
                    {
                        BookId = reader.GetInt32(3),
                        BookCode = reader.GetString(4),
                        Title = reader.GetString(5),
                        Author = reader.GetString(6),
                        Category = reader.GetString(7),
                        Publisher = reader.GetString(8),
                        PublishYear = reader.IsDBNull(9) ? null : reader.GetInt32(9),
                        Isbn = reader.GetString(10),
                        TotalCopies = reader.GetInt32(11),
                        AvailableCopies = reader.GetInt32(12),
                        ShelfLocation = reader.GetString(13),
                        Description = reader.GetString(14),
                        IsSampleData = reader.GetBoolean(15),
                        IsFavorite = true
                    };

                    favorites.Add(new FavoriteBookItemModel
                    {
                        FavoriteId = favoriteId,
                        UserId = userId,
                        Book = book,
                        AddedAtLocal = addedAtUtc.ToLocalTime()
                    });
                }
            }
            catch (SqlException ex)
            {
                AppDiagnosticLogger.LogDatabaseException(nameof(ReaderWorkspaceService), nameof(TryGetFavoritesForUser), ex, $"TargetUserId={targetUserId}");
                errorMessage = LocalizationService.IsVietnamese
                    ? $"Lỗi đọc danh sách yêu thích từ SQL Server: {ex.Message}"
                    : $"Error loading favorites from SQL Server: {ex.Message}";
            }

            return favorites;
        }

        public ServiceOperationResult AddToOwnFavorites(int bookId)
        {
            if (!_authorizationService.TryAuthorizeOperation(AppPermission.ManageOwnFavorites, "AddToOwnFavorites()", out string authMsg))
            {
                return ServiceOperationResult.Fail(authMsg);
            }

            int authenticatedUserId = _sessionService.CurrentSession!.UserId;

            try
            {
                using SqlConnection conn = new(DatabaseConfig.GetConnectionString());
                conn.Open();

                const string sql = @"
                    IF NOT EXISTS (
                        SELECT 1 FROM [dbo].[ReaderFavorites]
                        WHERE [UserId] = @UserId AND [BookId] = @BookId
                    )
                    BEGIN
                        INSERT INTO [dbo].[ReaderFavorites] ([UserId], [BookId])
                        VALUES (@UserId, @BookId);
                    END;";

                using SqlCommand cmd = new(sql, conn);
                cmd.Parameters.Add("@UserId", SqlDbType.Int).Value = authenticatedUserId;
                cmd.Parameters.Add("@BookId", SqlDbType.Int).Value = bookId;
                cmd.ExecuteNonQuery();

                FavoritesChanged?.Invoke(this, EventArgs.Empty);

                return ServiceOperationResult.Ok(LocalizationService.IsVietnamese
                    ? "[Sách yêu thích] Đã lưu sách vào danh sách yêu thích của tài khoản bạn trong SQL Server."
                    : "[Favorites] Saved book to your personal Favorites list in SQL Server.");
            }
            catch (SqlException ex)
            {
                AppDiagnosticLogger.LogDatabaseException(nameof(ReaderWorkspaceService), nameof(AddToOwnFavorites), ex, $"UserId={authenticatedUserId}; BookId={bookId}");
                return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                    ? $"Không thể thêm vào yêu thích: {ex.Message}"
                    : $"Failed to add book to favorites: {ex.Message}");
            }
        }

        public ServiceOperationResult RemoveFromOwnFavorites(int bookId)
        {
            if (!_authorizationService.TryAuthorizeOperation(AppPermission.ManageOwnFavorites, "RemoveFromOwnFavorites()", out string authMsg))
            {
                return ServiceOperationResult.Fail(authMsg);
            }

            int authenticatedUserId = _sessionService.CurrentSession!.UserId;

            try
            {
                using SqlConnection conn = new(DatabaseConfig.GetConnectionString());
                conn.Open();

                const string sql = @"
                    DELETE FROM [dbo].[ReaderFavorites]
                    WHERE [UserId] = @UserId AND [BookId] = @BookId;";

                using SqlCommand cmd = new(sql, conn);
                cmd.Parameters.Add("@UserId", SqlDbType.Int).Value = authenticatedUserId;
                cmd.Parameters.Add("@BookId", SqlDbType.Int).Value = bookId;
                cmd.ExecuteNonQuery();

                FavoritesChanged?.Invoke(this, EventArgs.Empty);

                return ServiceOperationResult.Ok(LocalizationService.IsVietnamese
                    ? "[Sách yêu thích] Đã xóa sách khỏi danh sách yêu thích của bạn."
                    : "[Favorites] Removed book from your personal Favorites list.");
            }
            catch (SqlException ex)
            {
                AppDiagnosticLogger.LogDatabaseException(nameof(ReaderWorkspaceService), nameof(RemoveFromOwnFavorites), ex, $"UserId={authenticatedUserId}; BookId={bookId}");
                return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                    ? $"Không thể xóa khỏi yêu thích: {ex.Message}"
                    : $"Failed to remove book from favorites: {ex.Message}");
            }
        }

        public ServiceOperationResult ToggleOwnFavorite(BookItemModel book)
        {
            ArgumentNullException.ThrowIfNull(book);

            if (book.IsFavorite)
            {
                ServiceOperationResult res = RemoveFromOwnFavorites(book.BookId);
                if (res.IsSuccess)
                {
                    book.IsFavorite = false;
                }
                return res;
            }
            else
            {
                ServiceOperationResult res = AddToOwnFavorites(book.BookId);
                if (res.IsSuccess)
                {
                    book.IsFavorite = true;
                }
                return res;
            }
        }

        // =====================================================================
        // 3. PERSONAL BORROWING & RETURN TRACKING
        // =====================================================================

        public IReadOnlyList<BorrowRecordItemModel> GetOwnBorrowRecords(string? statusFilter, out string? errorMessage)
        {
            errorMessage = null;
            if (_sessionService.CurrentSession is not { } session)
            {
                errorMessage = LocalizationService.IsVietnamese
                    ? "Vui lòng đăng nhập để theo dõi lịch sử mượn trả."
                    : "Please sign in to view your borrowing records.";
                return [];
            }

            return TryGetBorrowRecordsForUser(session.UserId, statusFilter, out errorMessage);
        }

        public IReadOnlyList<BorrowRecordItemModel> TryGetBorrowRecordsForUser(
            int targetUserId,
            string? statusFilter,
            out string? errorMessage)
        {
            errorMessage = null;

            if (!_authorizationService.CanAccessUserAccountData(targetUserId, out string denialMessage))
            {
                errorMessage = denialMessage;
                return [];
            }

            string normalizedStatus = statusFilter?.Trim() ?? "ALL";
            List<BorrowRecordItemModel> records = new();

            try
            {
                using SqlConnection conn = new(DatabaseConfig.GetConnectionString());
                conn.Open();
                LibraryManagementService.EnsureCirculationSchema(conn);

                const string syncOverdueSql = @"
                    UPDATE [dbo].[BorrowRecords]
                    SET [BorrowStatus] = N'Overdue'
                    WHERE [UserId] = @UserId
                      AND [ReturnedAtUtc] IS NULL
                      AND [BorrowStatus] = N'Borrowed'
                      AND [DueAtUtc] < SYSUTCDATETIME();";

                using (SqlCommand syncCmd = new(syncOverdueSql, conn))
                {
                    syncCmd.Parameters.Add("@UserId", SqlDbType.Int).Value = targetUserId;
                    syncCmd.ExecuteNonQuery();
                }

                const string sql = @"
                    SELECT
                        br.[BorrowRecordId],
                        br.[RecordCode],
                        br.[UserId],
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
                    WHERE br.[UserId] = @UserId
                      AND (@StatusFilter = N'ALL' OR br.[BorrowStatus] = @StatusFilter)
                    ORDER BY br.[BorrowedAtUtc] DESC;";

                using SqlCommand cmd = new(sql, conn);
                cmd.Parameters.Add("@UserId", SqlDbType.Int).Value = targetUserId;
                cmd.Parameters.Add("@StatusFilter", SqlDbType.NVarChar, 30).Value = normalizedStatus;

                using SqlDataReader reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    DateTime borrowedUtc = DateTime.SpecifyKind(reader.GetDateTime(8), DateTimeKind.Utc);
                    DateTime dueUtc = DateTime.SpecifyKind(reader.GetDateTime(9), DateTimeKind.Utc);
                    DateTime? returnedLocal = reader.IsDBNull(10)
                        ? null
                        : DateTime.SpecifyKind(reader.GetDateTime(10), DateTimeKind.Utc).ToLocalTime();

                    records.Add(new BorrowRecordItemModel
                    {
                        BorrowRecordId = reader.GetInt32(0),
                        RecordCode = reader.GetString(1),
                        UserId = reader.GetInt32(2),
                        ReaderCardId = reader.GetString(3),
                        BookId = reader.GetInt32(4),
                        BookCode = reader.GetString(5),
                        BookTitle = reader.GetString(6),
                        BookAuthor = reader.GetString(7),
                        BorrowedAtLocal = borrowedUtc.ToLocalTime(),
                        DueAtLocal = dueUtc.ToLocalTime(),
                        ReturnedAtLocal = returnedLocal,
                        BorrowStatus = reader.GetString(11),
                        Notes = reader.GetString(12),
                        RenewalCount = reader.GetInt32(13),
                        HasActiveReservationOnBook = reader.GetBoolean(14)
                    });
                }
            }
            catch (SqlException ex)
            {
                AppDiagnosticLogger.LogDatabaseException(nameof(ReaderWorkspaceService), nameof(TryGetBorrowRecordsForUser), ex, $"TargetUserId={targetUserId}");
                errorMessage = LocalizationService.IsVietnamese
                    ? $"Lỗi đọc dữ liệu mượn trả từ SQL Server: {ex.Message}"
                    : $"Error loading borrowing records from SQL Server: {ex.Message}";
            }

            return records;
        }

        // =====================================================================
        // 4. READER PROFILE (Read & Update Editable Fields Only)
        // =====================================================================

        public ReaderProfileModel? GetOwnProfile(out string? errorMessage)
        {
            errorMessage = null;
            if (_sessionService.CurrentSession is not { } session)
            {
                errorMessage = LocalizationService.IsVietnamese
                    ? "Chưa có phiên đăng nhập hợp lệ."
                    : "No active authenticated session.";
                return null;
            }

            return TryGetProfileForUser(session.UserId, out errorMessage);
        }

        public ReaderProfileModel? TryGetProfileForUser(int targetUserId, out string? errorMessage)
        {
            errorMessage = null;

            if (!_authorizationService.CanAccessUserAccountData(targetUserId, out string denialMessage))
            {
                errorMessage = denialMessage;
                return null;
            }

            try
            {
                using SqlConnection conn = new(DatabaseConfig.GetConnectionString());
                conn.Open();

                const string sql = @"
                    SELECT
                        u.[UserId],
                        u.[Username],
                        u.[Role],
                        u.[FullName],
                        u.[Email],
                        u.[CreatedAtUtc],
                        ISNULL(r.[ReaderCardId], N'N/A'),
                        ISNULL(r.[PhoneNumber], N''),
                        ISNULL(r.[MembershipStatus], N'Active'),
                        r.[RegisteredAtUtc]
                    FROM [dbo].[Users] u
                    LEFT JOIN [dbo].[Readers] r ON r.[UserId] = u.[UserId]
                    WHERE u.[UserId] = @UserId;";

                using SqlCommand cmd = new(sql, conn);
                cmd.Parameters.Add("@UserId", SqlDbType.Int).Value = targetUserId;

                using SqlDataReader reader = cmd.ExecuteReader();
                if (!reader.Read())
                {
                    errorMessage = LocalizationService.IsVietnamese
                        ? "Không tìm thấy hồ sơ tài khoản trong cơ sở dữ liệu."
                        : "Account profile was not found in the database.";
                    return null;
                }

                DateTime createdUtc = DateTime.SpecifyKind(reader.GetDateTime(5), DateTimeKind.Utc);
                DateTime registeredUtc = reader.IsDBNull(9)
                    ? createdUtc
                    : DateTime.SpecifyKind(reader.GetDateTime(9), DateTimeKind.Utc);

                return new ReaderProfileModel
                {
                    UserId = reader.GetInt32(0),
                    Username = reader.GetString(1),
                    Role = reader.GetString(2),
                    FullName = reader.GetString(3),
                    Email = reader.GetString(4),
                    ReaderCardId = reader.GetString(6),
                    PhoneNumber = reader.GetString(7),
                    MembershipStatus = reader.GetString(8),
                    RegisteredAtLocal = registeredUtc.ToLocalTime()
                };
            }
            catch (SqlException ex)
            {
                AppDiagnosticLogger.LogDatabaseException(nameof(ReaderWorkspaceService), nameof(TryGetProfileForUser), ex, $"TargetUserId={targetUserId}");
                errorMessage = LocalizationService.IsVietnamese
                    ? $"Lỗi tải hồ sơ từ SQL Server: {ex.Message}"
                    : $"Failed to load profile from SQL Server: {ex.Message}";
                return null;
            }
        }

        /// <summary>
        /// Updates only the allowed personal fields (FullName, Email, PhoneNumber) for the currently
        /// authenticated user. Never modifies UserId, Username, Role, or ReaderCardId.
        /// </summary>
        public ServiceOperationResult UpdateOwnProfile(UpdateReaderProfileRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);

            if (!_authorizationService.TryAuthorizeOperation(AppPermission.ManageOwnProfile, "UpdateOwnProfile()", out string authMsg))
            {
                return ServiceOperationResult.Fail(authMsg);
            }

            UserSession currentSession = _sessionService.CurrentSession!;
            int authenticatedUserId = currentSession.UserId;

            string fullName = request.FullName?.Trim() ?? string.Empty;
            string email = request.Email?.Trim() ?? string.Empty;
            string phone = request.PhoneNumber?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(fullName) || string.IsNullOrWhiteSpace(email))
            {
                return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                    ? "Họ và tên và Địa chỉ Email không được để trống."
                    : "Full Name and Email Address are required.");
            }

            if (!Regex.IsMatch(email, @"^[^@\s]+@[^@\s]+\.[^@\s]+$"))
            {
                return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                    ? "Địa chỉ Email không đúng định dạng."
                    : "Please enter a valid email address.");
            }

            if (!string.IsNullOrWhiteSpace(phone) && !Regex.IsMatch(phone, @"^[0-9+\-\s()]{7,20}$"))
            {
                return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                    ? "Số điện thoại chỉ được chứa chữ số và ký tự +, -, khoảng trắng (7–20 ký tự)."
                    : "Phone number must be 7–20 characters and contain only digits, +, -, or spaces.");
            }

            try
            {
                using SqlConnection conn = new(DatabaseConfig.GetConnectionString());
                conn.Open();
                using SqlTransaction tx = conn.BeginTransaction();

                // 1. Update dbo.Users (FullName and Email only; UserId, Username, Role are never touched)
                const string updateUsersSql = @"
                    UPDATE [dbo].[Users]
                    SET [FullName] = @FullName,
                        [Email] = @Email
                    WHERE [UserId] = @UserId;";

                using (SqlCommand userCmd = new(updateUsersSql, conn, tx))
                {
                    userCmd.Parameters.Add("@FullName", SqlDbType.NVarChar, 120).Value = fullName;
                    userCmd.Parameters.Add("@Email", SqlDbType.NVarChar, 150).Value = email;
                    userCmd.Parameters.Add("@UserId", SqlDbType.Int).Value = authenticatedUserId;
                    userCmd.ExecuteNonQuery();
                }

                // 2. Update dbo.Readers if a reader record exists for this UserId (ReaderCardId is never touched)
                const string updateReadersSql = @"
                    UPDATE [dbo].[Readers]
                    SET [FullName] = @FullName,
                        [Email] = @Email,
                        [PhoneNumber] = @PhoneNumber
                    WHERE [UserId] = @UserId;";

                using (SqlCommand readerCmd = new(updateReadersSql, conn, tx))
                {
                    readerCmd.Parameters.Add("@FullName", SqlDbType.NVarChar, 120).Value = fullName;
                    readerCmd.Parameters.Add("@Email", SqlDbType.NVarChar, 150).Value = email;
                    readerCmd.Parameters.Add("@PhoneNumber", SqlDbType.NVarChar, 25).Value =
                        string.IsNullOrWhiteSpace(phone) ? DBNull.Value : phone;
                    readerCmd.Parameters.Add("@UserId", SqlDbType.Int).Value = authenticatedUserId;
                    readerCmd.ExecuteNonQuery();
                }

                tx.Commit();

                _sessionService.UpdateCurrentSessionFullName(fullName);
                ProfileUpdated?.Invoke(this, EventArgs.Empty);

                return ServiceOperationResult.Ok(LocalizationService.IsVietnamese
                    ? "[Hồ sơ cá nhân] Đã lưu cập nhật thông tin cá nhân vào SQL Server thành công."
                    : "[Personal Profile] Successfully saved your profile changes to SQL Server.");
            }
            catch (SqlException ex)
            {
                AppDiagnosticLogger.LogDatabaseException(nameof(ReaderWorkspaceService), nameof(UpdateOwnProfile), ex, $"UserId={authenticatedUserId}");
                return ServiceOperationResult.Fail(LocalizationService.IsVietnamese
                    ? $"Không thể cập nhật hồ sơ: {ex.Message}"
                    : $"Failed to update profile: {ex.Message}");
            }
        }

        private static BookItemModel MapBookFromReader(SqlDataReader reader)
        {
            int activeReservations = reader.FieldCount > 14 && !reader.IsDBNull(14) ? reader.GetInt32(14) : 0;
            string currentUserRsvStatus = reader.FieldCount > 15 && !reader.IsDBNull(15) ? reader.GetString(15) : string.Empty;

            return new BookItemModel
            {
                BookId = reader.GetInt32(0),
                BookCode = reader.GetString(1),
                Title = reader.GetString(2),
                Author = reader.GetString(3),
                Category = reader.GetString(4),
                Publisher = reader.GetString(5),
                PublishYear = reader.IsDBNull(6) ? null : reader.GetInt32(6),
                Isbn = reader.GetString(7),
                TotalCopies = reader.GetInt32(8),
                AvailableCopies = reader.GetInt32(9),
                ShelfLocation = reader.GetString(10),
                Description = reader.GetString(11),
                IsSampleData = reader.GetBoolean(12),
                IsFavorite = reader.GetBoolean(13),
                ActiveReservationsCount = activeReservations,
                IsReservedByCurrentUser = !string.IsNullOrWhiteSpace(currentUserRsvStatus),
                CurrentUserReservationStatus = currentUserRsvStatus
            };
        }
    }
}
