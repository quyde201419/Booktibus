using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using Booktibus.Commands;
using Booktibus.Models;
using Booktibus.Services;

namespace Booktibus.ViewModels
{
    /// <summary>
    /// ViewModel for Borrowing &amp; Return Tracking (Stage 5.4 for Readers)
    /// and Circulation Checkout &amp; Return Management (Stage 6.3.2 for Librarians &amp; Administrators).
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

            RefreshCommand = new RelayCommand(ExecuteRefresh);
            StaffSearchCommand = new RelayCommand(ExecuteRefresh);
            BrowseCatalogCommand = new RelayCommand(() => _navigateCallback(NavigationPageKey.Books));
            CreateLoanCommand = new RelayCommand(ExecuteCreateLoan);
            ProcessReturnCommand = new RelayCommand(ExecuteProcessReturn);

            _libraryManagementService.CirculationChanged += (_, _) =>
            {
                LoadBorrowRecordsFromDatabase();
                LoadStaffLoanPickers();
            };
            _libraryManagementService.CatalogChanged += (_, _) => LoadStaffLoanPickers();
            _libraryManagementService.ReadersChanged += (_, _) => LoadStaffLoanPickers();

            InitializeStatusFilters();
        }

        public ObservableCollection<BorrowRecordItemModel> BorrowRecords { get; }

        public ObservableCollection<StaffBorrowRecordItemModel> StaffBorrowRecords { get; }

        public ObservableCollection<FilterOptionItem> StatusFilterOptions { get; }

        public ObservableCollection<ReaderDirectoryItemModel> AvailableReadersForLoan { get; }

        public ObservableCollection<BookItemModel> AvailableBooksForLoan { get; }

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
            set => SetProperty(ref _selectedBorrowRecord, value);
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
                }
            }
        }

        public bool HasSelectedStaffRecord => SelectedStaffBorrowRecord is not null;

        public bool CanReturnSelectedStaffRecord =>
            SelectedStaffBorrowRecord is not null && !SelectedStaffBorrowRecord.IsReturned;

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
                        ? $"Chế độ Nghiệp vụ Lưu thông ({session.Role}: {session.Username}) • Quản lý toàn bộ giao dịch lập phiếu mượn và ghi nhận trả sách trong BooktibusDB"
                        : $"Staff Circulation Mode ({session.Role}: {session.Username}) • Managing all book checkouts and returns in BooktibusDB";
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

        public override void OnLanguageUpdated()
        {
            base.OnLanguageUpdated();
            InitializeStatusFilters();
            LoadBorrowRecordsFromDatabase();
            OnPropertyChanged(nameof(ActiveReaderCardBanner));
        }

        public void OnSessionUpdated()
        {
            OnPropertyChanged(nameof(IsStaffCirculationManager));
            OnPropertyChanged(nameof(ActiveReaderCardBanner));
            StaffActionFeedback = string.Empty;
            InitializeStatusFilters();
            LoadBorrowRecordsFromDatabase();
            if (IsStaffCirculationManager)
            {
                LoadStaffLoanPickers();
            }
        }

        private void ExecuteRefresh()
        {
            LoadBorrowRecordsFromDatabase();
            if (IsStaffCirculationManager)
            {
                LoadStaffLoanPickers();
            }

            int count = IsStaffCirculationManager ? StaffBorrowRecords.Count : BorrowRecords.Count;
            _statusFeedbackCallback(LocalizationService.IsVietnamese
                ? $"[Mượn & Trả sách] Đã tải {count} giao dịch thực tế từ bảng dbo.BorrowRecords."
                : $"[Borrowing & Returns] Loaded {count} real transaction(s) from dbo.BorrowRecords.");
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
                ? $"Xác nhận ghi nhận trả sách cho phiếu '{target.RecordCode}'?\n\n• Độc giả: {target.ReaderFullName} ({target.ReaderCardId})\n• Sách: {target.BookTitle} ({target.BookCode})\n• Hệ thống sẽ chuyển trạng thái sang 'Returned' và cộng lại 1 bản sao vào kho."
                : $"Confirm book return for loan '{target.RecordCode}'?\n\n• Reader: {target.ReaderFullName} ({target.ReaderCardId})\n• Book: {target.BookTitle} ({target.BookCode})\n• This will mark the loan as 'Returned' and restore 1 copy to inventory.";

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

                SelectedStaffBorrowRecord = StaffBorrowRecords.FirstOrDefault();
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

                SelectedBorrowRecord = BorrowRecords.FirstOrDefault();
            }

            OnPropertyChanged(nameof(IsRecordsEmpty));
            OnPropertyChanged(nameof(ActiveBorrowedCount));
            OnPropertyChanged(nameof(ReturnedCount));
            OnPropertyChanged(nameof(OverdueCount));
        }
    }
}

