using Booktibus.Services;

namespace Booktibus.Models
{
    /// <summary>
    /// Request DTO for Librarian/Admin adding or updating a book in dbo.Books (Stage 6.1).
    /// </summary>
    public class BookUpsertRequest
    {
        public int? BookId { get; init; }

        public string BookCode { get; init; } = string.Empty;

        public string Title { get; init; } = string.Empty;

        public string Author { get; init; } = string.Empty;

        public string Category { get; init; } = string.Empty;

        public string Publisher { get; init; } = string.Empty;

        public string PublishYearText { get; init; } = string.Empty;

        public string Isbn { get; init; } = string.Empty;

        public string TotalCopiesText { get; init; } = "1";

        public string ShelfLocation { get; init; } = string.Empty;

        public string Description { get; init; } = string.Empty;
    }

    /// <summary>
    /// Request DTO for Librarian/Admin recording a new book checkout transaction (Stage 6.2).
    /// </summary>
    public class CreateBorrowLoanRequest
    {
        public int ReaderUserId { get; init; }

        public int BookId { get; init; }

        public int LoanDays { get; init; } = 14;

        public string Notes { get; init; } = string.Empty;
    }

    /// <summary>
    /// Represents a circulation transaction row for Librarian/Admin management (includes Reader identity).
    /// </summary>
    public class StaffBorrowRecordItemModel
    {
        public int BorrowRecordId { get; init; }

        public string RecordCode { get; init; } = string.Empty;

        public int UserId { get; init; }

        public string Username { get; init; } = string.Empty;

        public string ReaderFullName { get; init; } = string.Empty;

        public string ReaderCardId { get; init; } = string.Empty;

        public int BookId { get; init; }

        public string BookCode { get; init; } = string.Empty;

        public string BookTitle { get; init; } = string.Empty;

        public string BookAuthor { get; init; } = string.Empty;

        public DateTime BorrowedAtLocal { get; init; }

        public DateTime DueAtLocal { get; init; }

        public DateTime? ReturnedAtLocal { get; init; }

        public string BorrowStatus { get; init; } = string.Empty;

        public string Notes { get; init; } = string.Empty;

        public bool IsReturned => ReturnedAtLocal.HasValue || string.Equals(BorrowStatus, "Returned", StringComparison.OrdinalIgnoreCase);

        public bool IsOverdue => !IsReturned && (string.Equals(BorrowStatus, "Overdue", StringComparison.OrdinalIgnoreCase) || DueAtLocal < DateTime.Now);

        public string BorrowedDateDisplay => BorrowedAtLocal.ToString("dd/MM/yyyy HH:mm");

        public string DueDateDisplay => DueAtLocal.ToString("dd/MM/yyyy");

        public string ReturnedDateDisplay => ReturnedAtLocal.HasValue
            ? ReturnedAtLocal.Value.ToString("dd/MM/yyyy HH:mm")
            : (LocalizationService.IsVietnamese ? "Chưa trả" : "Not returned");

        public string LocalizedStatusDisplay
        {
            get
            {
                if (IsReturned)
                {
                    return LocalizationService.IsVietnamese ? "Đã trả (Returned)" : "Returned";
                }

                if (IsOverdue)
                {
                    return LocalizationService.IsVietnamese ? "Quá hạn (Overdue)" : "Overdue";
                }

                return LocalizationService.IsVietnamese ? "Đang mượn (Borrowed)" : "Borrowed";
            }
        }
    }

    /// <summary>
    /// Represents a Reader directory item for Librarian/Admin Reader Management (Stage 6.3).
    /// Strictly excludes password hashes, salts, or sensitive authentication secrets.
    /// </summary>
    public class ReaderDirectoryItemModel
    {
        public int ReaderId { get; init; }

        public int UserId { get; init; }

        public string ReaderCardId { get; init; } = string.Empty;

        public string Username { get; init; } = string.Empty;

        public string FullName { get; init; } = string.Empty;

        public string Email { get; init; } = string.Empty;

        public string PhoneNumber { get; init; } = string.Empty;

        public string MembershipStatus { get; init; } = "Active";

        public bool IsAccountActive { get; init; } = true;

        public DateTime RegisteredAtLocal { get; init; }

        public int ActiveLoansCount { get; init; }

        public int OverdueLoansCount { get; init; }

        public int TotalLoansCount { get; init; }

        public string RegisteredDateDisplay => RegisteredAtLocal.ToString("dd/MM/yyyy");

        public string AccountStatusDisplay => IsAccountActive
            ? (LocalizationService.IsVietnamese ? $"Hoạt động ({MembershipStatus})" : $"Active ({MembershipStatus})")
            : (LocalizationService.IsVietnamese ? "Đã khóa tài khoản" : "Account Disabled");

        public string PickerDisplay => $"{ReaderCardId} — {FullName} ({Username})";
    }

    public class UpdateReaderByStaffRequest
    {
        public int TargetUserId { get; init; }

        public string FullName { get; init; } = string.Empty;

        public string Email { get; init; } = string.Empty;

        public string PhoneNumber { get; init; } = string.Empty;

        public string MembershipStatus { get; init; } = "Active";
    }

    /// <summary>
    /// Represents a user account row for Administrator Account &amp; Role Management (Stage 6.4.1).
    /// Never exposes PasswordHash or PasswordSalt.
    /// </summary>
    public class ManagedUserAccountModel
    {
        public int UserId { get; init; }

        public string Username { get; init; } = string.Empty;

        public string FullName { get; init; } = string.Empty;

        public string Email { get; init; } = string.Empty;

        public string Role { get; init; } = string.Empty;

        public string ReaderCardId { get; init; } = string.Empty;

        public bool IsActive { get; init; }

        public int FailedLoginAttempts { get; init; }

        public DateTime? LockoutEndLocal { get; init; }

        public DateTime CreatedAtLocal { get; init; }

        public bool IsCurrentlyLockedOut => LockoutEndLocal.HasValue && LockoutEndLocal.Value > DateTime.Now;

        public string CreatedDateDisplay => CreatedAtLocal.ToString("dd/MM/yyyy HH:mm");

        public string ActiveBadgeDisplay => IsActive
            ? (LocalizationService.IsVietnamese ? "Đang hoạt động" : "Active")
            : (LocalizationService.IsVietnamese ? "Vô hiệu hóa" : "Disabled");

        public string LockoutStatusDisplay => IsCurrentlyLockedOut
            ? (LocalizationService.IsVietnamese
                ? $"Đang khóa đến {LockoutEndLocal:HH:mm:ss}"
                : $"Locked until {LockoutEndLocal:HH:mm:ss}")
            : (FailedLoginAttempts > 0
                ? (LocalizationService.IsVietnamese ? $"Sai {FailedLoginAttempts}/5 lần" : $"{FailedLoginAttempts}/5 failed")
                : (LocalizationService.IsVietnamese ? "Bình thường" : "Normal"));
    }

    public class CreateManagedUserRequest
    {
        public string Username { get; init; } = string.Empty;

        public string FullName { get; init; } = string.Empty;

        public string Email { get; init; } = string.Empty;

        public string PhoneNumber { get; init; } = string.Empty;

        public string Role { get; init; } = "Librarian";

        public string Password { get; init; } = string.Empty;

        public string ConfirmPassword { get; init; } = string.Empty;
    }

    /// <summary>
    /// Real-time library statistics queried from SQL Server BooktibusDB (Stage 6.4.2).
    /// </summary>
    public class LibraryDatabaseStatisticsModel
    {
        public bool IsConnectedToSql { get; init; }

        public int TotalBookTitles { get; init; }

        public int TotalBookCopies { get; init; }

        public int AvailableBookCopies { get; init; }

        public int TotalRegisteredReaders { get; init; }

        public int TotalUsersCount { get; init; }

        public int ActiveBorrowedLoans { get; init; }

        public int OverdueLoans { get; init; }

        public int ReturnedLoans { get; init; }

        public List<CategoryStatRowModel> CategoryBreakdown { get; init; } = new();

        public List<PeriodLoanStatRowModel> MonthlyLoanBreakdown { get; init; } = new();

        public List<StaffBorrowRecordItemModel> RecentLoanTransactions { get; init; } = new();
    }

    public class CategoryStatRowModel
    {
        public string CategoryName { get; init; } = string.Empty;

        public int TitleCount { get; init; }

        public int TotalCopiesSum { get; init; }

        public int AvailableCopiesSum { get; init; }
    }

    public class PeriodLoanStatRowModel
    {
        public string PeriodLabel { get; init; } = string.Empty;

        public int TotalLoansInPeriod { get; init; }

        public int ActiveOrOverdueInPeriod { get; init; }

        public int ReturnedInPeriod { get; init; }
    }
}

