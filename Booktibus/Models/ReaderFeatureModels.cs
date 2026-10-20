using Booktibus.Services;
using Booktibus.ViewModels;

namespace Booktibus.Models
{
    /// <summary>
    /// Represents a book item in the catalog with live availability and favorite status for the current user.
    /// </summary>
    public class BookItemModel : BaseViewModel
    {
        private bool _isFavorite;

        public int BookId { get; init; }

        public string BookCode { get; init; } = string.Empty;

        public string Title { get; init; } = string.Empty;

        public string Author { get; init; } = string.Empty;

        public string Category { get; init; } = string.Empty;

        public string Publisher { get; init; } = string.Empty;

        public int? PublishYear { get; init; }

        public string Isbn { get; init; } = string.Empty;

        public int TotalCopies { get; init; }

        public int AvailableCopies { get; init; }

        public string ShelfLocation { get; init; } = string.Empty;

        public string Description { get; init; } = string.Empty;

        public bool IsSampleData { get; init; }

        public bool IsAvailable => AvailableCopies > 0;

        public bool IsFavorite
        {
            get => _isFavorite;
            set
            {
                if (SetProperty(ref _isFavorite, value))
                {
                    OnPropertyChanged(nameof(FavoriteButtonLabel));
                    OnPropertyChanged(nameof(FavoriteBadgeText));
                }
            }
        }

        public string AvailabilityDisplay => LocalizationService.IsVietnamese
            ? (IsAvailable
                ? $"Còn sách ({AvailableCopies}/{TotalCopies})"
                : $"Hết sách (0/{TotalCopies})")
            : (IsAvailable
                ? $"Available ({AvailableCopies}/{TotalCopies})"
                : $"Out of Stock (0/{TotalCopies})");

        public string SampleDataBadge => IsSampleData
            ? (LocalizationService.IsVietnamese ? "DỮ LIỆU MẪU" : "SAMPLE DATA")
            : (LocalizationService.IsVietnamese ? "CSDL THỰC TẾ" : "LIVE DB");

        public string FavoriteButtonLabel => IsFavorite
            ? (LocalizationService.IsVietnamese ? "♥ Bỏ yêu thích" : "♥ Remove Favorite")
            : (LocalizationService.IsVietnamese ? "♡ Thêm vào Yêu thích" : "♡ Add to Favorites");

        public string FavoriteBadgeText => IsFavorite
            ? (LocalizationService.IsVietnamese ? "★ Đã yêu thích" : "★ Favorited")
            : string.Empty;

        public void RefreshLocalization()
        {
            OnPropertyChanged(nameof(AvailabilityDisplay));
            OnPropertyChanged(nameof(SampleDataBadge));
            OnPropertyChanged(nameof(FavoriteButtonLabel));
            OnPropertyChanged(nameof(FavoriteBadgeText));
        }
    }

    /// <summary>
    /// Represents a book saved to a Reader's personal Favorites list in SQL Server.
    /// </summary>
    public class FavoriteBookItemModel
    {
        public int FavoriteId { get; init; }

        public int UserId { get; init; }

        public BookItemModel Book { get; init; } = null!;

        public DateTime AddedAtLocal { get; init; }

        public string AddedAtDisplay => AddedAtLocal.ToString("dd/MM/yyyy HH:mm");
    }

    /// <summary>
    /// Represents a borrowing/return transaction belonging to the authenticated Reader from dbo.BorrowRecords.
    /// </summary>
    public class BorrowRecordItemModel
    {
        public int BorrowRecordId { get; init; }

        public string RecordCode { get; init; } = string.Empty;

        public int UserId { get; init; }

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

        public string BorrowedDateDisplay => BorrowedAtLocal.ToString("dd/MM/yyyy");

        public string DueDateDisplay => DueAtLocal.ToString("dd/MM/yyyy");

        public string ReturnedDateDisplay => ReturnedAtLocal.HasValue
            ? ReturnedAtLocal.Value.ToString("dd/MM/yyyy")
            : (LocalizationService.IsVietnamese ? "Chưa trả" : "Not returned");

        public string LocalizedStatusDisplay
        {
            get
            {
                if (!LocalizationService.IsVietnamese)
                {
                    return BorrowStatus;
                }

                return BorrowStatus switch
                {
                    "Borrowed" => "Đang mượn",
                    "Returned" => "Đã trả",
                    "Overdue" => "Quá hạn",
                    _ => BorrowStatus
                };
            }
        }
    }

    /// <summary>
    /// Represents the personal profile of the currently authenticated user / reader.
    /// </summary>
    public class ReaderProfileModel
    {
        public int UserId { get; init; }

        public string Username { get; init; } = string.Empty;

        public string Role { get; init; } = string.Empty;

        public string ReaderCardId { get; init; } = string.Empty;

        public string FullName { get; init; } = string.Empty;

        public string Email { get; init; } = string.Empty;

        public string PhoneNumber { get; init; } = string.Empty;

        public string MembershipStatus { get; init; } = string.Empty;

        public DateTime RegisteredAtLocal { get; init; }
    }

    /// <summary>
    /// Request DTO for updating editable Reader profile fields.
    /// Security rule: Does NOT include UserId, Username, Role, or ReaderCardId.
    /// </summary>
    public class UpdateReaderProfileRequest
    {
        public string FullName { get; init; } = string.Empty;

        public string Email { get; init; } = string.Empty;

        public string PhoneNumber { get; init; } = string.Empty;
    }

    public class ServiceOperationResult
    {
        public bool IsSuccess { get; init; }

        public string Message { get; init; } = string.Empty;

        public static ServiceOperationResult Ok(string message) => new()
        {
            IsSuccess = true,
            Message = message
        };

        public static ServiceOperationResult Fail(string message) => new()
        {
            IsSuccess = false,
            Message = message
        };
    }
}

