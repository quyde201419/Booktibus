using System.Collections.ObjectModel;
using System.Windows.Input;
using Booktibus.Commands;
using Booktibus.Models;
using Booktibus.Services;

namespace Booktibus.ViewModels
{
    /// <summary>
    /// ViewModel for Librarian &amp; Administrator Reader Directory Management (Stage 6.3.3).
    /// Supports searching readers by name, username, email, or card ID, inspecting their
    /// borrowing history, and updating permitted reader profile/membership status fields.
    /// </summary>
    public class LibrarianReadersViewModel : PageViewModelBase
    {
        private readonly LibraryManagementService _libraryManagementService;
        private readonly ReaderWorkspaceService _readerWorkspaceService;
        private readonly Action<string> _statusFeedbackCallback;

        private string _searchKeyword = string.Empty;
        private FilterOptionItem? _selectedStatusFilter;
        private ReaderDirectoryItemModel? _selectedReader;
        private string _editFullName = string.Empty;
        private string _editEmail = string.Empty;
        private string _editPhone = string.Empty;
        private string _selectedEditMembershipStatus = "Active";
        private string _feedbackMessage = string.Empty;
        private bool _isFeedbackSuccess;
        private bool _isSuppressingReload;

        public LibrarianReadersViewModel(
            LibraryManagementService libraryManagementService,
            ReaderWorkspaceService readerWorkspaceService,
            Action<string> statusFeedbackCallback)
            : base(
                NavigationPageKey.Readers,
                "Str.Page.Readers.Title",
                "Str.Page.Readers.Subtitle")
        {
            _libraryManagementService = libraryManagementService;
            _readerWorkspaceService = readerWorkspaceService;
            _statusFeedbackCallback = statusFeedbackCallback;

            Readers = new ObservableCollection<ReaderDirectoryItemModel>();
            StatusFilterOptions = new ObservableCollection<FilterOptionItem>();
            SelectedReaderBorrowHistory = new ObservableCollection<BorrowRecordItemModel>();
            MembershipStatusChoices = ["Active", "Suspended"];

            SearchCommand = new RelayCommand(ExecuteSearch);
            ClearFiltersCommand = new RelayCommand(ExecuteClearFilters);
            SaveReaderChangesCommand = new RelayCommand(ExecuteSaveReaderChanges);

            _libraryManagementService.ReadersChanged += (_, _) => ReloadReadersPreservingSelection();
            _libraryManagementService.CirculationChanged += (_, _) => ReloadReadersPreservingSelection();

            InitializeStatusFilters();
        }

        public ObservableCollection<ReaderDirectoryItemModel> Readers { get; }

        public ObservableCollection<FilterOptionItem> StatusFilterOptions { get; }

        public ObservableCollection<BorrowRecordItemModel> SelectedReaderBorrowHistory { get; }

        public IReadOnlyList<string> MembershipStatusChoices { get; }

        public string SearchKeyword
        {
            get => _searchKeyword;
            set => SetProperty(ref _searchKeyword, value);
        }

        public FilterOptionItem? SelectedStatusFilter
        {
            get => _selectedStatusFilter;
            set
            {
                if (SetProperty(ref _selectedStatusFilter, value) && !_isSuppressingReload)
                {
                    LoadReadersFromDatabase();
                }
            }
        }

        public ReaderDirectoryItemModel? SelectedReader
        {
            get => _selectedReader;
            set
            {
                if (SetProperty(ref _selectedReader, value))
                {
                    OnPropertyChanged(nameof(HasSelectedReader));
                    PopulateSelectedReaderDetails(value);
                }
            }
        }

        public bool HasSelectedReader => SelectedReader is not null;

        public bool IsReadersListEmpty => Readers.Count == 0;

        public bool IsSelectedReaderHistoryEmpty => SelectedReaderBorrowHistory.Count == 0;

        public string ReadersCountSummary => LocalizationService.IsVietnamese
            ? $"Hiển thị {Readers.Count} hồ sơ thẻ độc giả từ BooktibusDB"
            : $"Showing {Readers.Count} reader card profile(s) from BooktibusDB";

        public string EditFullName
        {
            get => _editFullName;
            set => SetProperty(ref _editFullName, value);
        }

        public string EditEmail
        {
            get => _editEmail;
            set => SetProperty(ref _editEmail, value);
        }

        public string EditPhone
        {
            get => _editPhone;
            set => SetProperty(ref _editPhone, value);
        }

        public string SelectedEditMembershipStatus
        {
            get => _selectedEditMembershipStatus;
            set => SetProperty(ref _selectedEditMembershipStatus, value);
        }

        public string FeedbackMessage
        {
            get => _feedbackMessage;
            private set
            {
                SetProperty(ref _feedbackMessage, value);
                OnPropertyChanged(nameof(HasFeedback));
            }
        }

        public bool IsFeedbackSuccess
        {
            get => _isFeedbackSuccess;
            private set => SetProperty(ref _isFeedbackSuccess, value);
        }

        public bool HasFeedback => !string.IsNullOrWhiteSpace(FeedbackMessage);

        public ICommand SearchCommand { get; }

        public ICommand ClearFiltersCommand { get; }

        public ICommand SaveReaderChangesCommand { get; }

        public override void OnLanguageUpdated()
        {
            base.OnLanguageUpdated();
            InitializeStatusFilters();
            ReloadReadersPreservingSelection();
            OnPropertyChanged(nameof(ReadersCountSummary));
        }

        public void OnSessionUpdated()
        {
            FeedbackMessage = string.Empty;
            InitializeStatusFilters();
            LoadReadersFromDatabase();
        }

        private void ExecuteSearch()
        {
            LoadReadersFromDatabase();
            _statusFeedbackCallback(ReadersCountSummary);
        }

        private void ExecuteClearFilters()
        {
            _isSuppressingReload = true;
            SearchKeyword = string.Empty;
            SelectedStatusFilter = StatusFilterOptions.FirstOrDefault();
            _isSuppressingReload = false;

            LoadReadersFromDatabase();
            _statusFeedbackCallback(LocalizationService.IsVietnamese
                ? "[Quản lý Độc giả] Đã đặt lại bộ lọc tìm kiếm độc giả."
                : "[Reader Management] Reset reader search filters.");
        }

        private void ExecuteSaveReaderChanges()
        {
            if (SelectedReader is null)
            {
                return;
            }

            FeedbackMessage = string.Empty;

            ServiceOperationResult res = _libraryManagementService.UpdateReaderByStaff(new UpdateReaderByStaffRequest
            {
                TargetUserId = SelectedReader.UserId,
                FullName = EditFullName,
                Email = EditEmail,
                PhoneNumber = EditPhone,
                MembershipStatus = SelectedEditMembershipStatus
            });

            IsFeedbackSuccess = res.IsSuccess;
            FeedbackMessage = res.Message;
            _statusFeedbackCallback(res.Message);
        }

        private void PopulateSelectedReaderDetails(ReaderDirectoryItemModel? reader)
        {
            SelectedReaderBorrowHistory.Clear();

            if (reader is null)
            {
                EditFullName = string.Empty;
                EditEmail = string.Empty;
                EditPhone = string.Empty;
                SelectedEditMembershipStatus = "Active";
                OnPropertyChanged(nameof(IsSelectedReaderHistoryEmpty));
                return;
            }

            EditFullName = reader.FullName;
            EditEmail = reader.Email;
            EditPhone = reader.PhoneNumber;
            SelectedEditMembershipStatus = reader.MembershipStatus;

            IReadOnlyList<BorrowRecordItemModel> history = _readerWorkspaceService.TryGetBorrowRecordsForUser(
                reader.UserId,
                "ALL",
                out _);

            foreach (BorrowRecordItemModel row in history)
            {
                SelectedReaderBorrowHistory.Add(row);
            }

            OnPropertyChanged(nameof(IsSelectedReaderHistoryEmpty));
        }

        private void InitializeStatusFilters()
        {
            _isSuppressingReload = true;
            string prevKey = _selectedStatusFilter?.Key ?? "ALL";

            StatusFilterOptions.Clear();
            if (LocalizationService.IsVietnamese)
            {
                StatusFilterOptions.Add(new FilterOptionItem { Key = "ALL", DisplayText = "Tất cả trạng thái thẻ" });
                StatusFilterOptions.Add(new FilterOptionItem { Key = "Active", DisplayText = "Đang hoạt động (Active)" });
                StatusFilterOptions.Add(new FilterOptionItem { Key = "Suspended", DisplayText = "Tạm đình chỉ (Suspended)" });
            }
            else
            {
                StatusFilterOptions.Add(new FilterOptionItem { Key = "ALL", DisplayText = "All Card Statuses" });
                StatusFilterOptions.Add(new FilterOptionItem { Key = "Active", DisplayText = "Active" });
                StatusFilterOptions.Add(new FilterOptionItem { Key = "Suspended", DisplayText = "Suspended" });
            }

            _selectedStatusFilter = StatusFilterOptions.FirstOrDefault(s => s.Key == prevKey) ?? StatusFilterOptions.FirstOrDefault();
            OnPropertyChanged(nameof(SelectedStatusFilter));
            _isSuppressingReload = false;
        }

        private void ReloadReadersPreservingSelection()
        {
            int? prevUserId = SelectedReader?.UserId;
            LoadReadersFromDatabase(prevUserId);
        }

        private void LoadReadersFromDatabase(int? preserveUserId = null)
        {
            string statusKey = _selectedStatusFilter?.Key ?? "ALL";
            IReadOnlyList<ReaderDirectoryItemModel> list = _libraryManagementService.GetReadersDirectory(
                SearchKeyword,
                statusKey,
                out string? error);

            if (!string.IsNullOrWhiteSpace(error))
            {
                IsFeedbackSuccess = false;
                FeedbackMessage = error;
            }

            Readers.Clear();
            foreach (ReaderDirectoryItemModel item in list)
            {
                Readers.Add(item);
            }

            if (preserveUserId.HasValue)
            {
                SelectedReader = Readers.FirstOrDefault(r => r.UserId == preserveUserId.Value) ?? Readers.FirstOrDefault();
            }
            else
            {
                SelectedReader = Readers.FirstOrDefault();
            }

            OnPropertyChanged(nameof(IsReadersListEmpty));
            OnPropertyChanged(nameof(ReadersCountSummary));
        }
    }
}

