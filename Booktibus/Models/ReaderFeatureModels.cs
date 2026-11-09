using Booktibus.Services;
using Booktibus.ViewModels;

namespace Booktibus.Models
{
    /// <summary>
    /// Represents a book item in the catalog with live availability, favorite status, and reservation status.
    /// </summary>
    public class BookItemModel : BaseViewModel
    {
        private bool _isFavorite;
        private bool _isReservedByCurrentUser;
        private int _activeReservationsCount;
        private string _currentUserReservationStatus = string.Empty;

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

        public bool IsOutOfStock => AvailableCopies == 0;

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

        public bool IsReservedByCurrentUser
        {
            get => _isReservedByCurrentUser;
            set
            {
                if (SetProperty(ref _isReservedByCurrentUser, value))
                {
                    OnPropertyChanged(nameof(CanReserve));
                    OnPropertyChanged(nameof(ReserveButtonLabel));
                    OnPropertyChanged(nameof(ReservationBadgeText));
                }
            }
        }

        public int ActiveReservationsCount
        {
            get => _activeReservationsCount;
            set
            {
                if (SetProperty(ref _activeReservationsCount, value))
                {
                    OnPropertyChanged(nameof(AvailabilityDisplay));
                    OnPropertyChanged(nameof(ReservationBadgeText));
                }
            }
        }

        public string CurrentUserReservationStatus
        {
            get => _currentUserReservationStatus;
            set
            {
                if (SetProperty(ref _currentUserReservationStatus, value))
                {
                    OnPropertyChanged(nameof(ReserveButtonLabel));
                    OnPropertyChanged(nameof(ReservationBadgeText));
                }
            }
        }

        public bool CanReserve => AvailableCopies == 0 && !IsReservedByCurrentUser;

        public string AvailabilityDisplay
        {
            get
            {
                string holdSuffix = ActiveReservationsCount > 0
                    ? (LocalizationService.IsVietnamese ? $" • {ActiveReservationsCount} đặt trước" : $" • {ActiveReservationsCount} reserved")
                    : string.Empty;

                return LocalizationService.IsVietnamese
                    ? (IsAvailable
                        ? $"Còn sách ({AvailableCopies}/{TotalCopies}){holdSuffix}"
                        : $"Hết sách (0/{TotalCopies}){holdSuffix}")
                    : (IsAvailable
                        ? $"Available ({AvailableCopies}/{TotalCopies}){holdSuffix}"
                        : $"Out of Stock (0/{TotalCopies}){holdSuffix}");
            }
        }

        public string PickerDisplay => $"{BookCode} — {Title} ({AvailabilityDisplay})";

        public string SampleDataBadge => IsSampleData
            ? (LocalizationService.IsVietnamese ? "DỮ LIỆU MẪU" : "SAMPLE DATA")
            : (LocalizationService.IsVietnamese ? "CSDL THỰC TẾ" : "LIVE DB");

        public string FavoriteButtonLabel => IsFavorite
            ? (LocalizationService.IsVietnamese ? "♥ Bỏ yêu thích" : "♥ Remove Favorite")
            : (LocalizationService.IsVietnamese ? "♡ Thêm vào Yêu thích" : "♡ Add to Favorites");

        public string FavoriteBadgeText => IsFavorite
            ? (LocalizationService.IsVietnamese ? "★ Đã yêu thích" : "★ Favorited")
            : string.Empty;

        public string ReserveButtonLabel
        {
            get
            {
                if (IsReservedByCurrentUser)
                {
                    if (string.Equals(CurrentUserReservationStatus, "Ready", StringComparison.OrdinalIgnoreCase))
                    {
                        return LocalizationService.IsVietnamese
                            ? "✓ Sách đã về — Đang giữ cho bạn"
                            : "✓ Ready — Held for You";
                    }

                    return LocalizationService.IsVietnamese
                        ? "⏳ Đã đặt trước (Đang chờ sách)"
                        : "⏳ Reserved (Waiting in Queue)";
                }

                return LocalizationService.IsVietnamese
                    ? "📌 Đặt trước sách (Reserve)"
                    : "📌 Reserve Book (Hold)";
            }
        }

        public string ReservationBadgeText
        {
            get
            {
                if (IsReservedByCurrentUser)
                {
                    return string.Equals(CurrentUserReservationStatus, "Ready", StringComparison.OrdinalIgnoreCase)
                        ? (LocalizationService.IsVietnamese ? "ĐANG GIỮ CHO BẠN" : "HELD FOR YOU")
                        : (LocalizationService.IsVietnamese ? "ĐÃ ĐẶT TRƯỚC" : "RESERVED");
                }

                if (ActiveReservationsCount > 0)
                {
                    return LocalizationService.IsVietnamese
                        ? $"Có {ActiveReservationsCount} đặt trước"
                        : $"{ActiveReservationsCount} Hold(s)";
                }

                return string.Empty;
            }
        }

        public void RefreshLocalization()
        {
            OnPropertyChanged(nameof(AvailabilityDisplay));
            OnPropertyChanged(nameof(PickerDisplay));
            OnPropertyChanged(nameof(SampleDataBadge));
            OnPropertyChanged(nameof(FavoriteButtonLabel));
            OnPropertyChanged(nameof(FavoriteBadgeText));
            OnPropertyChanged(nameof(ReserveButtonLabel));
            OnPropertyChanged(nameof(ReservationBadgeText));
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

        public int RenewalCount { get; init; }

        public bool HasActiveReservationOnBook { get; init; }

        public bool IsReturned => ReturnedAtLocal.HasValue || string.Equals(BorrowStatus, "Returned", StringComparison.OrdinalIgnoreCase);

        public bool IsOverdue => !IsReturned && (string.Equals(BorrowStatus, "Overdue", StringComparison.OrdinalIgnoreCase) || DueAtLocal.Date < DateTime.Now.Date);

        public bool CanRenew => !IsReturned && !IsOverdue && RenewalCount < 2 && !HasActiveReservationOnBook;

        public string RenewalStatusDisplay => LocalizationService.IsVietnamese
            ? $"{RenewalCount}/2 lần"
            : $"{RenewalCount}/2";

        public string RenewalConstraintHint
        {
            get
            {
                if (IsReturned)
                {
                    return LocalizationService.IsVietnamese
                        ? "Phiếu mượn đã trả hoàn tất."
                        : "Loan has already been returned.";
                }

                if (IsOverdue)
                {
                    return LocalizationService.IsVietnamese
                        ? "Không thể gia hạn: Sách đã quá hạn trả."
                        : "Cannot renew: Loan is already overdue.";
                }

                if (RenewalCount >= 2)
                {
                    return LocalizationService.IsVietnamese
                        ? "Không thể gia hạn: Đã đạt tối đa 2/2 lần gia hạn."
                        : "Cannot renew: Maximum 2/2 renewals reached.";
                }

                if (HasActiveReservationOnBook)
                {
                    return LocalizationService.IsVietnamese
                        ? "Không thể gia hạn: Đầu sách đang có người đặt trước."
                        : "Cannot renew: Another reader has reserved this book.";
                }

                return LocalizationService.IsVietnamese
                    ? $"Đủ điều kiện gia hạn (Còn {2 - RenewalCount} lần gia hạn)."
                    : $"Eligible for renewal ({2 - RenewalCount} renewal(s) remaining).";
            }
        }

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
    /// Represents a Book Reservation / Hold record in dbo.BookReservations.
    /// </summary>
    public class BookReservationItemModel
    {
        public int ReservationId { get; init; }

        public string ReservationCode { get; init; } = string.Empty;

        public int BookId { get; init; }

        public string BookCode { get; init; } = string.Empty;

        public string BookTitle { get; init; } = string.Empty;

        public string BookAuthor { get; init; } = string.Empty;

        public int UserId { get; init; }

        public string Username { get; init; } = string.Empty;

        public string ReaderFullName { get; init; } = string.Empty;

        public string ReaderCardId { get; init; } = string.Empty;

        public DateTime ReservedAtLocal { get; init; }

        public DateTime? ReadyAtLocal { get; init; }

        public DateTime? ExpiresAtLocal { get; init; }

        public string ReservationStatus { get; init; } = "Pending";

        public string Notes { get; init; } = string.Empty;

        public bool IsPending => string.Equals(ReservationStatus, "Pending", StringComparison.OrdinalIgnoreCase);

        public bool IsReadyForPickup => string.Equals(ReservationStatus, "Ready", StringComparison.OrdinalIgnoreCase);

        public bool CanCancel => IsPending || IsReadyForPickup;

        public bool CanFulfill => IsReadyForPickup;

        public string ReservedDateDisplay => ReservedAtLocal.ToString("dd/MM/yyyy HH:mm");

        public string HoldUntilDisplay => ExpiresAtLocal.HasValue
            ? ExpiresAtLocal.Value.ToString("dd/MM/yyyy HH:mm")
            : (LocalizationService.IsVietnamese ? "Chờ sách trả về" : "Waiting for return");

        public string LocalizedStatusDisplay
        {
            get
            {
                return ReservationStatus switch
                {
                    "Pending" => LocalizationService.IsVietnamese ? "Đang chờ sách trả (Pending)" : "Waiting for Return (Pending)",
                    "Ready" => LocalizationService.IsVietnamese ? "Đang giữ sách cho người đặt (Held / Ready)" : "Held for Reader (Ready)",
                    "Fulfilled" => LocalizationService.IsVietnamese ? "Đã nhận sách (Fulfilled)" : "Fulfilled",
                    "Cancelled" => LocalizationService.IsVietnamese ? "Đã hủy (Cancelled)" : "Cancelled",
                    _ => ReservationStatus
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
