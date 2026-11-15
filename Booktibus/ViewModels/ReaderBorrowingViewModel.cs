using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using Booktibus.Commands;
using Booktibus.Models;
using Booktibus.Services;

namespace Booktibus.ViewModels
{
    public class RenewalDaysOptionItem
    {
        public int Days { get; init; }

        public string DisplayText { get; init; } = string.Empty;
    }

    /// <summary>
    /// ViewModel for Borrowing &amp; Return Tracking (Stage 5.4 for Readers),
    /// Circulation Checkout &amp; Return Management (Stage 6.3.2 for Librarians &amp; Administrators),
    /// Loan Renewal (+7 / +14 days), and Book Reservation / Hold when AvailableCopies == 0.
    /// </summary>
    public class ReaderBorrowingViewModel : PageViewModelBase
    {
        private readonly SessionService _sessionService;
        private readonly AuthorizationService _authorizationService;
        private readonly ReaderWorkspaceService _readerService;
        private readonly LibraryManagementService _libraryManagementService;
        private readonly Action<NavigationPageKey> _navigateCallback;
        private readonly Action<string> _statusFeedbackCallback;

        private string _staffSearchKeyword = string.Empty;
        private FilterOptionItem? _selectedStatusFilter;
        private BorrowRecordItemModel? _selectedBorrowRecord;
        private StaffBorrowRecordItemModel? _selectedStaffBorrowRecord;
        private string _errorMessage = string.Empty;
        private bool _isSuppressingReload;

        // Stage 6.3.2: Staff Checkout & Return Form Fields
        private ReaderDirectoryItemModel? _selectedLoanReader;
        private BookItemModel? _selectedLoanBook;
        private string _loanDaysInput = "14";
        private string _loanNotesInput = string.Empty;
        private string _returnNotesInput = string.Empty;
        private string _staffActionFeedback = string.Empty;
        private bool _isStaffFeedbackSuccess;

        // Loan Renewal Fields
        private RenewalDaysOptionItem? _selectedRenewalDaysOption;

        // Book Reservation / Hold Fields
        private BookItemModel? _selectedBookToReserve;
        private string _reservationNotesInput = string.Empty;
        private BookReservationItemModel? _selectedReservation;

        public ReaderBorrowingViewModel(
            SessionService sessionService,
            AuthorizationService authorizationService,
            ReaderWorkspaceService readerService,
            LibraryManagementService libraryManagementService,
            Action<NavigationPageKey> navigateCallback,
            Action<string> statusFeedbackCallback)
            : base(
                NavigationPageKey.BorrowingReturns,
                "Str.Page.BorrowingReturns.Title",
                "Str.Page.BorrowingReturns.Subtitle")
        {
            _sessionService = sessionService;
            _authorizationService = authorizationService;
            _readerService = readerService;
            _libraryManagementService = libraryManagementService;
            _navigateCallback = navigateCallback;
            _statusFeedbackCallback = statusFeedbackCallback;

            BorrowRecords = new ObservableCollection<BorrowRecordItemModel>();
            StaffBorrowRecords = new ObservableCollection<StaffBorrowRecordItemModel>();
            StatusFilterOptions = new ObservableCollection<FilterOptionItem>();
            AvailableReadersForLoan = new ObservableCollection<ReaderDirectoryItemModel>();
            AvailableBooksForLoan = new ObservableCollection<BookItemModel>();
            RenewalDayOptions = new ObservableCollection<RenewalDaysOptionItem>();
            OutOfStockBooksForReservation = new ObservableCollection<BookItemModel>();
            ReservationsList = new ObservableCollection<BookReservationItemModel>();

            RefreshCommand = new RelayCommand(ExecuteRefresh);
            StaffSearchCommand = new RelayCommand(ExecuteRefresh);
            BrowseCatalogCommand = new RelayCommand(() => _navigateCallback(NavigationPageKey.Books));
            CreateLoanCommand = new RelayCommand(ExecuteCreateLoan);
            ProcessReturnCommand = new RelayCommand(ExecuteProcessReturn);
            RenewLoanCommand = new RelayCommand(ExecuteRenewLoan);
            ReserveBookCommand = new RelayCommand(ExecuteReserveBook);
            CancelReservationCommand = new RelayCommand(ExecuteCancelReservation);
            FulfillReservationCommand = new RelayCommand(ExecuteFulfillReservation);

            _libraryManagementService.CirculationChanged += (_, _) =>
            {
                LoadBorrowRecordsFromDatabase();
                LoadStaffLoanPickers();
                LoadReservationWorkspace();
            };
            _libraryManagementService.CatalogChanged += (_, _) =>
            {
                LoadStaffLoanPickers();
                LoadReservationWorkspace();
            };
            _libraryManagementService.ReadersChanged += (_, _) => LoadStaffLoanPickers();

            InitializeStatusFilters();
            InitializeRenewalOptions();
        }

        public ObservableCollection<BorrowRecordItemModel> BorrowRecords { get; }

        public ObservableCollection<StaffBorrowRecordItemModel> StaffBorrowRecords { get; }

        public ObservableCollection<FilterOptionItem> StatusFilterOptions { get; }

        public ObservableCollection<ReaderDirectoryItemModel> AvailableReadersForLoan { get; }

        public ObservableCollection<BookItemModel> AvailableBooksForLoan { get; }

        public ObservableCollection<RenewalDaysOptionItem> RenewalDayOptions { get; }

        public ObservableCollection<BookItemModel> OutOfStockBooksForReservation { get; }

        public ObservableCollection<BookReservationItemModel> ReservationsList { get; }

        public bool IsStaffCirculationManager => _authorizationService.HasPermission(AppPermission.ProcessCirculation);

        public string StaffSearchKeyword
        {
            get => _staffSearchKeyword;
            set => SetProperty(ref _staffSearchKeyword, value);
        }

        public FilterOptionItem? SelectedStatusFilter
        {
            get => _selectedStatusFilter;
            set
            {
                if (SetProperty(ref _selectedStatusFilter, value) && !_isSuppressingReload)
                {
                    LoadBorrowRecordsFromDatabase();
                }
            }
        }

        public BorrowRecordItemModel? SelectedBorrowRecord
        {
            get => _selectedBorrowRecord;
            set
            {
                if (SetProperty(ref _selectedBorrowRecord, value))
                {
                    NotifySelectedLoanRenewalState();
                }
            }
        }

        public StaffBorrowRecordItemModel? SelectedStaffBorrowRecord
        {
            get => _selectedStaffBorrowRecord;
            set
            {
                if (SetProperty(ref _selectedStaffBorrowRecord, value))
                {
                    OnPropertyChanged(nameof(HasSelectedStaffRecord));
                    OnPropertyChanged(nameof(CanReturnSelectedStaffRecord));
                    NotifySelectedLoanRenewalState();
                }
            }
        }

        public bool HasSelectedStaffRecord => SelectedStaffBorrowRecord is not null;

        public bool CanReturnSelectedStaffRecord =>
            SelectedStaffBorrowRecord is not null && !SelectedStaffBorrowRecord.IsReturned;

        public RenewalDaysOptionItem? SelectedRenewalDaysOption
        {
            get => _selectedRenewalDaysOption;
            set => SetProperty(ref _selectedRenewalDaysOption, value);
        }

        public bool CanRenewSelectedLoan => IsStaffCirculationManager
            ? SelectedStaffBorrowRecord?.CanRenew == true
            : SelectedBorrowRecord?.CanRenew == true;

        public string SelectedLoanRenewalSummary
        {
            get
            {
                if (IsStaffCirculationManager)
                {
                    if (SelectedStaffBorrowRecord is not { } sr)
                    {
                        return LocalizationService.IsVietnamese
                            ? "Chọn một phiếu đang mượn trong bảng để gia hạn."
                            : "Select an active loan in the table to renew.";
                    }

                    return $"{sr.RecordCode} — {sr.BookTitle} • Hạn trả: {sr.DueDateDisplay} • Gia hạn: {sr.RenewalStatusDisplay}";
                }

                if (SelectedBorrowRecord is not { } r)
                {
                    return LocalizationService.IsVietnamese
                        ? "Chọn một phiếu đang mượn trong bảng để gia hạn."
                        : "Select an active loan in the table to renew.";
                }

                return $"{r.RecordCode} — {r.BookTitle} • Hạn trả: {r.DueDateDisplay} • Gia hạn: {r.RenewalStatusDisplay}";
            }
        }

        public string SelectedLoanRenewalHint => IsStaffCirculationManager
            ? (SelectedStaffBorrowRecord?.RenewalConstraintHint ?? string.Empty)
            : (SelectedBorrowRecord?.RenewalConstraintHint ?? string.Empty);

        public BookItemModel? SelectedBookToReserve
        {
            get => _selectedBookToReserve;
            set
            {
                if (SetProperty(ref _selectedBookToReserve, value))
                {
                    OnPropertyChanged(nameof(CanSubmitReservation));
                }
            }
        }

        public string ReservationNotesInput
        {
            get => _reservationNotesInput;
            set => SetProperty(ref _reservationNotesInput, value);
        }

        public bool CanSubmitReservation => SelectedBookToReserve is not null && SelectedBookToReserve.AvailableCopies == 0;

        public bool HasOutOfStockBooks => OutOfStockBooksForReservation.Count > 0;

        public BookReservationItemModel? SelectedReservation
        {
            get => _selectedReservation;
            set
            {
                if (SetProperty(ref _selectedReservation, value))
                {
                    OnPropertyChanged(nameof(CanCancelSelectedReservation));
                    OnPropertyChanged(nameof(CanFulfillSelectedReservation));
                }
            }
        }

        public bool CanCancelSelectedReservation => SelectedReservation?.CanCancel == true;

        public bool CanFulfillSelectedReservation =>
            IsStaffCirculationManager && SelectedReservation?.CanFulfill == true;

        public bool HasReservations => ReservationsList.Count > 0;

        public ReaderDirectoryItemModel? SelectedLoanReader
        {
            get => _selectedLoanReader;
            set => SetProperty(ref _selectedLoanReader, value);
        }

        public BookItemModel? SelectedLoanBook
        {
            get => _selectedLoanBook;
            set => SetProperty(ref _selectedLoanBook, value);
        }

        public string LoanDaysInput
        {
            get => _loanDaysInput;
            set => SetProperty(ref _loanDaysInput, value);
        }

        public string LoanNotesInput
        {
            get => _loanNotesInput;
            set => SetProperty(ref _loanNotesInput, value);
        }

        public string ReturnNotesInput
        {
            get => _returnNotesInput;
            set => SetProperty(ref _returnNotesInput, value);
        }

        public string StaffActionFeedback
        {
            get => _staffActionFeedback;
            private set
            {
                SetProperty(ref _staffActionFeedback, value);
                OnPropertyChanged(nameof(HasStaffFeedback));
            }
        }

        public bool IsStaffFeedbackSuccess
        {
            get => _isStaffFeedbackSuccess;
            private set => SetProperty(ref _isStaffFeedbackSuccess, value);
        }

        public bool HasStaffFeedback => !string.IsNullOrWhiteSpace(StaffActionFeedback);

        public bool IsRecordsEmpty => IsStaffCirculationManager
            ? StaffBorrowRecords.Count == 0
            : BorrowRecords.Count == 0;

        public int ActiveBorrowedCount => IsStaffCirculationManager
            ? StaffBorrowRecords.Count(r => !r.IsReturned)
            : BorrowRecords.Count(r => r.BorrowStatus != "Returned");

        public int ReturnedCount => IsStaffCirculationManager
            ? StaffBorrowRecords.Count(r => r.IsReturned)
            : BorrowRecords.Count(r => r.BorrowStatus == "Returned");

        public int OverdueCount => IsStaffCirculationManager
            ? StaffBorrowRecords.Count(r => r.IsOverdue)
            : BorrowRecords.Count(r => r.BorrowStatus == "Overdue");

        public string ActiveReaderCardBanner
        {
            get
            {
                if (_sessionService.CurrentSession is not { } session)
                {
                    return string.Empty;
                }

                if (IsStaffCirculationManager)
                {
                    return LocalizationService.IsVietnamese
                        ? $"Chế độ Nghiệp vụ Lưu thông ({session.Role}: {session.Username}) • Quản lý lập phiếu mượn, trả sách, gia hạn và đặt trước trong BooktibusDB"
                        : $"Staff Circulation Mode ({session.Role}: {session.Username}) • Managing checkouts, returns, renewals, and reservations in BooktibusDB";
                }

                return LocalizationService.IsVietnamese
                    ? $"Tài khoản Độc giả: {session.Username} (UserId={session.UserId}) • Mã thẻ độc giả: {session.ReaderCardId ?? "N/A"}"
                    : $"Reader Account: {session.Username} (UserId={session.UserId}) • Reader Card ID: {session.ReaderCardId ?? "N/A"}";
            }
        }

        public string ErrorMessage
        {
            get => _errorMessage;
            private set
            {
                SetProperty(ref _errorMessage, value);
                OnPropertyChanged(nameof(HasError));
            }
        }

        public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

        public ICommand RefreshCommand { get; }

        public ICommand StaffSearchCommand { get; }

        public ICommand BrowseCatalogCommand { get; }

        public ICommand CreateLoanCommand { get; }

        public ICommand ProcessReturnCommand { get; }

        public ICommand RenewLoanCommand { get; }

        public ICommand ReserveBookCommand { get; }

        public ICommand CancelReservationCommand { get; }

        public ICommand FulfillReservationCommand { get; }

        public override void OnLanguageUpdated()
        {
            base.OnLanguageUpdated();
            InitializeStatusFilters();
            InitializeRenewalOptions();
            LoadBorrowRecordsFromDatabase();
            LoadReservationWorkspace();
            OnPropertyChanged(nameof(ActiveReaderCardBanner));
            NotifySelectedLoanRenewalState();
        }

        public void OnSessionUpdated()
        {
            OnPropertyChanged(nameof(IsStaffCirculationManager));
            OnPropertyChanged(nameof(ActiveReaderCardBanner));
            StaffActionFeedback = string.Empty;
            InitializeStatusFilters();
            InitializeRenewalOptions();
            LoadBorrowRecordsFromDatabase();
            LoadReservationWorkspace();
            if (IsStaffCirculationManager)
            {
                LoadStaffLoanPickers();
            }
        }

        private void NotifySelectedLoanRenewalState()
        {
            OnPropertyChanged(nameof(CanRenewSelectedLoan));
            OnPropertyChanged(nameof(SelectedLoanRenewalSummary));
            OnPropertyChanged(nameof(SelectedLoanRenewalHint));
        }

        private void ExecuteRefresh()
        {
            LoadBorrowRecordsFromDatabase();
            LoadReservationWorkspace();
            if (IsStaffCirculationManager)
            {
                LoadStaffLoanPickers();
            }

            int count = IsStaffCirculationManager ? StaffBorrowRecords.Count : BorrowRecords.Count;
            _statusFeedbackCallback(LocalizationService.IsVietnamese
                ? $"[Mượn & Trả sách] Đã tải {count} giao dịch mượn trả và {ReservationsList.Count} phiếu đặt trước từ CSDL."
                : $"[Borrowing & Returns] Loaded {count} loan transaction(s) and {ReservationsList.Count} reservation(s) from DB.");
        }

        private void ExecuteCreateLoan()
        {
            StaffActionFeedback = string.Empty;

            if (SelectedLoanReader is null)
            {
                IsStaffFeedbackSuccess = false;
                StaffActionFeedback = LocalizationService.IsVietnamese
                    ? "Vui lòng chọn một Độc giả để lập phiếu mượn."
                    : "Please select a Reader to create a loan.";
                return;
            }

            if (SelectedLoanBook is null)
            {
                IsStaffFeedbackSuccess = false;
                StaffActionFeedback = LocalizationService.IsVietnamese
                    ? "Vui lòng chọn một Đầu sách còn bản sao để cho mượn."
                    : "Please select an available Book to lend.";
                return;
            }

            if (!int.TryParse(LoanDaysInput?.Trim(), out int loanDays))
            {
                IsStaffFeedbackSuccess = false;
                StaffActionFeedback = LocalizationService.IsVietnamese
                    ? "Số ngày mượn phải là một số nguyên hợp lệ (1–60 ngày)."
                    : "Loan duration must be a valid integer (1–60 days).";
                return;
            }

            ServiceOperationResult res = _libraryManagementService.CreateBorrowTransaction(new CreateBorrowLoanRequest
            {
                ReaderUserId = SelectedLoanReader.UserId,
                BookId = SelectedLoanBook.BookId,
                LoanDays = loanDays,
                Notes = LoanNotesInput
            });

            IsStaffFeedbackSuccess = res.IsSuccess;
            StaffActionFeedback = res.Message;
            _statusFeedbackCallback(res.Message);

            if (res.IsSuccess)
            {
                LoanNotesInput = string.Empty;
            }
        }

        private void ExecuteProcessReturn(object? parameter)
        {
            StaffBorrowRecordItemModel? target = parameter as StaffBorrowRecordItemModel ?? SelectedStaffBorrowRecord;
            if (target is null)
            {
                return;
            }

            if (target.IsReturned)
            {
                IsStaffFeedbackSuccess = false;
                StaffActionFeedback = LocalizationService.IsVietnamese
                    ? $"Phiếu mượn '{target.RecordCode}' đã được ghi nhận trả sách trước đó."
                    : $"Loan '{target.RecordCode}' has already been returned.";
                _statusFeedbackCallback(StaffActionFeedback);
                return;
            }

            string confirmMsg = LocalizationService.IsVietnamese
                ? $"Xác nhận ghi nhận trả sách cho phiếu '{target.RecordCode}'?\n\n• Độc giả: {target.ReaderFullName} ({target.ReaderCardId})\n• Sách: {target.BookTitle} ({target.BookCode})\n• Hệ thống sẽ chuyển trạng thái sang 'Returned' và cộng lại 1 bản sao (hoặc giữ sách cho người đã đặt trước nếu có)."
                : $"Confirm book return for loan '{target.RecordCode}'?\n\n• Reader: {target.ReaderFullName} ({target.ReaderCardId})\n• Book: {target.BookTitle} ({target.BookCode})\n• This will mark the loan as 'Returned' and restore 1 copy (or hold it for the next reserver).";

            string confirmTitle = LocalizationService.IsVietnamese
                ? "Xác nhận Ghi nhận Trả sách"
                : "Confirm Book Return";

            if (MessageBox.Show(confirmMsg, confirmTitle, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                return;
            }

            ServiceOperationResult res = _libraryManagementService.ProcessBookReturn(target.BorrowRecordId, ReturnNotesInput);
            IsStaffFeedbackSuccess = res.IsSuccess;
            StaffActionFeedback = res.Message;
            _statusFeedbackCallback(res.Message);

            if (res.IsSuccess)
            {
                ReturnNotesInput = string.Empty;
            }
        }

        private void ExecuteRenewLoan(object? parameter)
        {
            StaffActionFeedback = string.Empty;

            int borrowRecordId = 0;
            if (parameter is StaffBorrowRecordItemModel staffParam)
            {
                borrowRecordId = staffParam.BorrowRecordId;
            }
            else if (parameter is BorrowRecordItemModel readerParam)
            {
                borrowRecordId = readerParam.BorrowRecordId;
            }
            else if (IsStaffCirculationManager && SelectedStaffBorrowRecord is not null)
            {
                borrowRecordId = SelectedStaffBorrowRecord.BorrowRecordId;
            }
            else if (!IsStaffCirculationManager && SelectedBorrowRecord is not null)
            {
                borrowRecordId = SelectedBorrowRecord.BorrowRecordId;
            }

            if (borrowRecordId <= 0)
            {
                IsStaffFeedbackSuccess = false;
                StaffActionFeedback = LocalizationService.IsVietnamese
                    ? "Vui lòng chọn một phiếu mượn trong bảng để gia hạn."
                    : "Please select a loan in the table to renew.";
                return;
            }

            int extensionDays = SelectedRenewalDaysOption?.Days ?? 7;
            ServiceOperationResult res = _libraryManagementService.RenewBorrowLoan(borrowRecordId, extensionDays);

            IsStaffFeedbackSuccess = res.IsSuccess;
            StaffActionFeedback = res.Message;
            _statusFeedbackCallback(res.Message);
        }

        private void ExecuteReserveBook()
        {
            StaffActionFeedback = string.Empty;

            if (SelectedBookToReserve is null)
            {
                IsStaffFeedbackSuccess = false;
                StaffActionFeedback = LocalizationService.IsVietnamese
                    ? "Vui lòng chọn một đầu sách đang hết bản sao (AvailableCopies = 0) để đặt trước."
                    : "Please select an out-of-stock book (AvailableCopies = 0) to reserve.";
                return;
            }

            int? targetReaderId = IsStaffCirculationManager ? SelectedLoanReader?.UserId : null;
            if (IsStaffCirculationManager && (!targetReaderId.HasValue || targetReaderId.Value <= 0))
            {
                IsStaffFeedbackSuccess = false;
                StaffActionFeedback = LocalizationService.IsVietnamese
                    ? "Trong chế độ Thủ thư, vui lòng chọn Độc giả (ở mục Độc giả mượn sách) cần đặt trước."
                    : "In Staff mode, please select a Reader to place the reservation for.";
                return;
            }

            ServiceOperationResult res = _libraryManagementService.ReserveBook(
                SelectedBookToReserve.BookId,
                targetReaderId,
                ReservationNotesInput);

            IsStaffFeedbackSuccess = res.IsSuccess;
            StaffActionFeedback = res.Message;
            _statusFeedbackCallback(res.Message);

            if (res.IsSuccess)
            {
                ReservationNotesInput = string.Empty;
            }
        }

        private void ExecuteCancelReservation(object? parameter)
        {
            BookReservationItemModel? target = parameter as BookReservationItemModel ?? SelectedReservation;
            if (target is null)
            {
                return;
            }

            ServiceOperationResult res = _libraryManagementService.CancelReservation(target.ReservationId);
            IsStaffFeedbackSuccess = res.IsSuccess;
            StaffActionFeedback = res.Message;
            _statusFeedbackCallback(res.Message);
        }

        private void ExecuteFulfillReservation(object? parameter)
        {
            BookReservationItemModel? target = parameter as BookReservationItemModel ?? SelectedReservation;
            if (target is null)
            {
                return;
            }

            if (!int.TryParse(LoanDaysInput?.Trim(), out int loanDays) || loanDays < 1 || loanDays > 60)
            {
                loanDays = 14;
            }

            ServiceOperationResult res = _libraryManagementService.FulfillReservation(target.ReservationId, loanDays);
            IsStaffFeedbackSuccess = res.IsSuccess;
            StaffActionFeedback = res.Message;
            _statusFeedbackCallback(res.Message);
        }

        private void LoadStaffLoanPickers()
        {
            if (!IsStaffCirculationManager)
            {
                return;
            }

            int? prevReaderUserId = SelectedLoanReader?.UserId;
            int? prevBookId = SelectedLoanBook?.BookId;

            AvailableReadersForLoan.Clear();
            foreach (ReaderDirectoryItemModel r in _libraryManagementService.GetReadersDirectory("", "Active", out _))
            {
                if (r.IsAccountActive)
                {
                    AvailableReadersForLoan.Add(r);
                }
            }

            AvailableBooksForLoan.Clear();
            foreach (BookItemModel b in _readerService.SearchBooks("", "", "AVAILABLE", out _))
            {
                AvailableBooksForLoan.Add(b);
            }

            SelectedLoanReader = AvailableReadersForLoan.FirstOrDefault(r => r.UserId == prevReaderUserId)
                                 ?? AvailableReadersForLoan.FirstOrDefault();
            SelectedLoanBook = AvailableBooksForLoan.FirstOrDefault(b => b.BookId == prevBookId)
                               ?? AvailableBooksForLoan.FirstOrDefault();
        }

        private void LoadReservationWorkspace()
        {
            int? prevOutBookId = SelectedBookToReserve?.BookId;
            int? prevRsvId = SelectedReservation?.ReservationId;

            OutOfStockBooksForReservation.Clear();
            foreach (BookItemModel b in _readerService.SearchBooks("", "", "OUT_OF_STOCK", out _))
            {
                OutOfStockBooksForReservation.Add(b);
            }

            SelectedBookToReserve = OutOfStockBooksForReservation.FirstOrDefault(b => b.BookId == prevOutBookId)
                                    ?? OutOfStockBooksForReservation.FirstOrDefault();

            ReservationsList.Clear();
            foreach (BookReservationItemModel rsv in _libraryManagementService.GetReservationsForCurrentSession(out _))
            {
                ReservationsList.Add(rsv);
            }

            SelectedReservation = ReservationsList.FirstOrDefault(r => r.ReservationId == prevRsvId)
                                  ?? ReservationsList.FirstOrDefault();

            OnPropertyChanged(nameof(HasOutOfStockBooks));
            OnPropertyChanged(nameof(CanSubmitReservation));
            OnPropertyChanged(nameof(HasReservations));
        }

        private void InitializeRenewalOptions()
        {
            int prevDays = _selectedRenewalDaysOption?.Days ?? 7;
            RenewalDayOptions.Clear();
            if (LocalizationService.IsVietnamese)
            {
                RenewalDayOptions.Add(new RenewalDaysOptionItem { Days = 7, DisplayText = "+ 7 ngày (Gia hạn 1 tuần)" });
                RenewalDayOptions.Add(new RenewalDaysOptionItem { Days = 14, DisplayText = "+ 14 ngày (Gia hạn 2 tuần)" });
            }
            else
            {
                RenewalDayOptions.Add(new RenewalDaysOptionItem { Days = 7, DisplayText = "+ 7 Days (1 Week)" });
                RenewalDayOptions.Add(new RenewalDaysOptionItem { Days = 14, DisplayText = "+ 14 Days (2 Weeks)" });
            }

            SelectedRenewalDaysOption = RenewalDayOptions.FirstOrDefault(o => o.Days == prevDays)
                                        ?? RenewalDayOptions.FirstOrDefault();
        }

        private void InitializeStatusFilters()
        {
            _isSuppressingReload = true;
            string prevKey = _selectedStatusFilter?.Key ?? "ALL";

            StatusFilterOptions.Clear();
            if (LocalizationService.IsVietnamese)
            {
                StatusFilterOptions.Add(new FilterOptionItem { Key = "ALL", DisplayText = "Tất cả trạng thái" });
                StatusFilterOptions.Add(new FilterOptionItem { Key = "Borrowed", DisplayText = "Đang mượn (Borrowed)" });
                StatusFilterOptions.Add(new FilterOptionItem { Key = "Returned", DisplayText = "Đã trả (Returned)" });
                StatusFilterOptions.Add(new FilterOptionItem { Key = "Overdue", DisplayText = "Quá hạn (Overdue)" });
            }
            else
            {
                StatusFilterOptions.Add(new FilterOptionItem { Key = "ALL", DisplayText = "All Statuses" });
                StatusFilterOptions.Add(new FilterOptionItem { Key = "Borrowed", DisplayText = "Currently Borrowed" });
                StatusFilterOptions.Add(new FilterOptionItem { Key = "Returned", DisplayText = "Returned" });
                StatusFilterOptions.Add(new FilterOptionItem { Key = "Overdue", DisplayText = "Overdue" });
            }

            _selectedStatusFilter = StatusFilterOptions.FirstOrDefault(s => s.Key == prevKey) ?? StatusFilterOptions.FirstOrDefault();
            OnPropertyChanged(nameof(SelectedStatusFilter));
            _isSuppressingReload = false;
        }

        private void LoadBorrowRecordsFromDatabase()
        {
            string statusKey = _selectedStatusFilter?.Key ?? "ALL";
            int? prevStaffId = SelectedStaffBorrowRecord?.BorrowRecordId;
            int? prevReaderId = SelectedBorrowRecord?.BorrowRecordId;

            if (IsStaffCirculationManager)
            {
                IReadOnlyList<StaffBorrowRecordItemModel> staffList = _libraryManagementService.GetAllBorrowRecordsForStaff(
                    StaffSearchKeyword,
                    statusKey,
                    out string? staffErr);

                ErrorMessage = staffErr ?? string.Empty;

                StaffBorrowRecords.Clear();
                foreach (StaffBorrowRecordItemModel item in staffList)
                {
                    StaffBorrowRecords.Add(item);
                }

                SelectedStaffBorrowRecord = StaffBorrowRecords.FirstOrDefault(r => r.BorrowRecordId == prevStaffId)
                                            ?? StaffBorrowRecords.FirstOrDefault();
            }
            else
            {
                IReadOnlyList<BorrowRecordItemModel> list = _readerService.GetOwnBorrowRecords(statusKey, out string? error);

                ErrorMessage = error ?? string.Empty;

                BorrowRecords.Clear();
                foreach (BorrowRecordItemModel item in list)
                {
                    BorrowRecords.Add(item);
                }

                SelectedBorrowRecord = BorrowRecords.FirstOrDefault(r => r.BorrowRecordId == prevReaderId)
                                       ?? BorrowRecords.FirstOrDefault();
            }

            OnPropertyChanged(nameof(IsRecordsEmpty));
            OnPropertyChanged(nameof(ActiveBorrowedCount));
            OnPropertyChanged(nameof(ReturnedCount));
            OnPropertyChanged(nameof(OverdueCount));
            NotifySelectedLoanRenewalState();
        }
    }
}
