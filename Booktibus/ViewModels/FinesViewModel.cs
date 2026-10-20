using System.Collections.ObjectModel;
using System.Windows.Input;
using Booktibus.Commands;
using Booktibus.Models;
using Booktibus.Services;

namespace Booktibus.ViewModels
{
    /// <summary>
    /// ViewModel for the complete Stage 8 Fines Management screen.
    /// Provides role-tailored views:
    /// - Reader: views own outstanding fines, estimated overdue amounts, and waived/paid records.
    /// - Librarian / Administrator: views all library fines, search & filter, confirms overdue fines,
    ///   adjusts fine amounts with mandatory reason, waives fines with mandatory reason, and views audit logs.
    /// - Administrator: additionally manages the system-wide daily overdue fine rate.
    /// </summary>
    public class FinesViewModel : PageViewModelBase
    {
        private readonly FineManagementService _fineService;
        private readonly SessionService _sessionService;
        private readonly AuthorizationService _authorizationService;
        private readonly Action<string> _statusFeedbackCallback;

        private FineItemModel? _selectedFine;
        private string _searchKeyword = string.Empty;
        private string _selectedStatusFilter = "ALL";

        // Summary Stats
        private string _totalRecordedUnpaidDisplay = "0 VNĐ";
        private string _totalEstimatedOverdueDisplay = "0 VNĐ";
        private string _totalCombinedOutstandingDisplay = "0 VNĐ";
        private string _totalPaidDisplay = "0 VNĐ";
        private string _totalWaivedDisplay = "0 VNĐ";
        private string _currentDailyRateDisplay = "5,000 VNĐ/ngày";

        // Dialog States
        private bool _isAdjustDialogOpen;
        private string _adjustAmountInput = string.Empty;
        private string _adjustReasonInput = string.Empty;

        private bool _isWaiveDialogOpen;
        private string _waiveReasonInput = string.Empty;

        private bool _isRateDialogOpen;
        private string _newRateInput = "5000";

        private bool _isAuditLogsOpen;

        // Feedback
        private string _feedbackMessage = string.Empty;
        private bool _hasError;
        private bool _hasSuccess;

        public FinesViewModel(
            FineManagementService fineService,
            SessionService sessionService,
            AuthorizationService authorizationService,
            Action<string> statusFeedbackCallback)
            : base(NavigationPageKey.Fines, "Str.Page.Fines.Title", "Str.Page.Fines.Subtitle")
        {
            ArgumentNullException.ThrowIfNull(fineService);
            ArgumentNullException.ThrowIfNull(sessionService);
            ArgumentNullException.ThrowIfNull(authorizationService);
            ArgumentNullException.ThrowIfNull(statusFeedbackCallback);

            _fineService = fineService;
            _sessionService = sessionService;
            _authorizationService = authorizationService;
            _statusFeedbackCallback = statusFeedbackCallback;

            FinesList = new ObservableCollection<FineItemModel>();
            AuditLogsList = new ObservableCollection<FineAuditLogItemModel>();

            RefreshCommand = new RelayCommand(LoadFines);
            ClearFiltersCommand = new RelayCommand(ExecuteClearFilters);
            ConfirmOverdueFineCommand = new RelayCommand(ExecuteConfirmOverdueFine, () => SelectedFine?.CanBeConfirmed == true && IsStaffMode);
            OpenAdjustDialogCommand = new RelayCommand(ExecuteOpenAdjustDialog, () => SelectedFine?.CanBeAdjustedOrWaived == true && IsStaffMode);
            SubmitAdjustCommand = new RelayCommand(ExecuteSubmitAdjust);
            CloseAdjustDialogCommand = new RelayCommand(() => IsAdjustDialogOpen = false);
            OpenWaiveDialogCommand = new RelayCommand(ExecuteOpenWaiveDialog, () => SelectedFine?.CanBeAdjustedOrWaived == true && IsStaffMode);
            SubmitWaiveCommand = new RelayCommand(ExecuteSubmitWaive);
            CloseWaiveDialogCommand = new RelayCommand(() => IsWaiveDialogOpen = false);
            OpenAuditLogsCommand = new RelayCommand(ExecuteOpenAuditLogs, () => SelectedFine?.IsOfficiallyRecorded == true && IsStaffMode);
            CloseAuditLogsCommand = new RelayCommand(() => IsAuditLogsOpen = false);
            OpenRateDialogCommand = new RelayCommand(ExecuteOpenRateDialog, () => IsAdmin);
            SubmitRateUpdateCommand = new RelayCommand(ExecuteSubmitRateUpdate);
            CloseRateDialogCommand = new RelayCommand(() => IsRateDialogOpen = false);

            _fineService.FinesChanged += (_, _) => LoadFines();

            LoadFines();
        }

        public ObservableCollection<FineItemModel> FinesList { get; }

        public ObservableCollection<FineAuditLogItemModel> AuditLogsList { get; }

        public bool IsReaderMode => _sessionService.CurrentSession?.Role == UserRole.Reader;

        public bool IsStaffMode => _sessionService.CurrentSession?.Role is UserRole.Librarian or UserRole.Administrator;

        public bool IsAdmin => _sessionService.CurrentSession?.Role == UserRole.Administrator;

        public string ActiveRoleSummaryBadge => _sessionService.CurrentSession?.Role switch
        {
            UserRole.Administrator => "ADMINISTRATOR • TOÀN QUYỀN QUẢN LÝ TIỀN PHẠT & CẤU HÌNH",
            UserRole.Librarian => "LIBRARIAN • QUẢN LÝ, ĐIỀU CHỈNH & MIỄN TIỀN PHẠT",
            UserRole.Reader => $"READER • THEO DÕI TIỀN PHẠT CỦA THẺ {_sessionService.CurrentSession?.ReaderCardId}",
            _ => "USER"
        };

        public FineItemModel? SelectedFine
        {
            get => _selectedFine;
            set
            {
                if (SetProperty(ref _selectedFine, value))
                {
                    OnPropertyChanged(nameof(HasSelectedFine));
                    RelayCommand.RaiseCanExecuteChanged();
                }
            }
        }

        public bool HasSelectedFine => SelectedFine is not null;

        public string SearchKeyword
        {
            get => _searchKeyword;
            set
            {
                if (SetProperty(ref _searchKeyword, value))
                {
                    LoadFines();
                }
            }
        }

        public string SelectedStatusFilter
        {
            get => _selectedStatusFilter;
            set
            {
                if (SetProperty(ref _selectedStatusFilter, value))
                {
                    LoadFines();
                }
            }
        }

        public string TotalRecordedUnpaidDisplay
        {
            get => _totalRecordedUnpaidDisplay;
            private set => SetProperty(ref _totalRecordedUnpaidDisplay, value);
        }

        public string TotalEstimatedOverdueDisplay
        {
            get => _totalEstimatedOverdueDisplay;
            private set => SetProperty(ref _totalEstimatedOverdueDisplay, value);
        }

        public string TotalCombinedOutstandingDisplay
        {
            get => _totalCombinedOutstandingDisplay;
            private set => SetProperty(ref _totalCombinedOutstandingDisplay, value);
        }

        public string TotalPaidDisplay
        {
            get => _totalPaidDisplay;
            private set => SetProperty(ref _totalPaidDisplay, value);
        }

        public string TotalWaivedDisplay
        {
            get => _totalWaivedDisplay;
            private set => SetProperty(ref _totalWaivedDisplay, value);
        }

        public string CurrentDailyRateDisplay
        {
            get => _currentDailyRateDisplay;
            private set => SetProperty(ref _currentDailyRateDisplay, value);
        }

        public bool IsAdjustDialogOpen
        {
            get => _isAdjustDialogOpen;
            set => SetProperty(ref _isAdjustDialogOpen, value);
        }

        public string AdjustAmountInput
        {
            get => _adjustAmountInput;
            set => SetProperty(ref _adjustAmountInput, value);
        }

        public string AdjustReasonInput
        {
            get => _adjustReasonInput;
            set => SetProperty(ref _adjustReasonInput, value);
        }

        public bool IsWaiveDialogOpen
        {
            get => _isWaiveDialogOpen;
            set => SetProperty(ref _isWaiveDialogOpen, value);
        }

        public string WaiveReasonInput
        {
            get => _waiveReasonInput;
            set => SetProperty(ref _waiveReasonInput, value);
        }

        public bool IsRateDialogOpen
        {
            get => _isRateDialogOpen;
            set => SetProperty(ref _isRateDialogOpen, value);
        }

        public string NewRateInput
        {
            get => _newRateInput;
            set => SetProperty(ref _newRateInput, value);
        }

        public bool IsAuditLogsOpen
        {
            get => _isAuditLogsOpen;
            set => SetProperty(ref _isAuditLogsOpen, value);
        }

        public string FeedbackMessage
        {
            get => _feedbackMessage;
            private set => SetProperty(ref _feedbackMessage, value);
        }

        public bool HasError
        {
            get => _hasError;
            private set => SetProperty(ref _hasError, value);
        }

        public bool HasSuccess
        {
            get => _hasSuccess;
            private set => SetProperty(ref _hasSuccess, value);
        }

        public ICommand RefreshCommand { get; }
        public ICommand ClearFiltersCommand { get; }
        public ICommand ConfirmOverdueFineCommand { get; }
        public ICommand OpenAdjustDialogCommand { get; }
        public ICommand SubmitAdjustCommand { get; }
        public ICommand CloseAdjustDialogCommand { get; }
        public ICommand OpenWaiveDialogCommand { get; }
        public ICommand SubmitWaiveCommand { get; }
        public ICommand CloseWaiveDialogCommand { get; }
        public ICommand OpenAuditLogsCommand { get; }
        public ICommand CloseAuditLogsCommand { get; }
        public ICommand OpenRateDialogCommand { get; }
        public ICommand SubmitRateUpdateCommand { get; }
        public ICommand CloseRateDialogCommand { get; }

        public void OnSessionUpdated()
        {
            OnPropertyChanged(nameof(IsReaderMode));
            OnPropertyChanged(nameof(IsStaffMode));
            OnPropertyChanged(nameof(IsAdmin));
            OnPropertyChanged(nameof(ActiveRoleSummaryBadge));
            LoadFines();
        }

        public override void OnLanguageUpdated()
        {
            base.OnLanguageUpdated();
            OnPropertyChanged(nameof(ActiveRoleSummaryBadge));
            LoadFines();
        }

        public void LoadFines()
        {
            ClearFeedback();
            FinesList.Clear();
            SelectedFine = null;

            IReadOnlyList<FineItemModel> items;
            FineSummaryStatsModel summary;

            if (IsReaderMode)
            {
                items = _fineService.GetOwnFinesWorkspace(out summary, out string? err);
                if (!string.IsNullOrWhiteSpace(err))
                {
                    SetFeedback(err, isError: true);
                }
            }
            else
            {
                items = _fineService.GetStaffFinesWorkspace(SearchKeyword, SelectedStatusFilter, out summary, out string? err);
                if (!string.IsNullOrWhiteSpace(err))
                {
                    SetFeedback(err, isError: true);
                }
            }

            foreach (FineItemModel fine in items)
            {
                FinesList.Add(fine);
            }

            TotalRecordedUnpaidDisplay = $"{summary.TotalRecordedUnpaidVnd:N0} VNĐ";
            TotalEstimatedOverdueDisplay = $"{summary.TotalEstimatedOverdueVnd:N0} VNĐ";
            TotalCombinedOutstandingDisplay = $"{summary.TotalCombinedOutstandingVnd:N0} VNĐ";
            TotalPaidDisplay = $"{summary.TotalPaidVnd:N0} VNĐ";
            TotalWaivedDisplay = $"{summary.TotalWaivedVnd:N0} VNĐ";
            CurrentDailyRateDisplay = LocalizationService.IsVietnamese
                ? $"{summary.CurrentDailyOverdueRateVnd:N0} VNĐ/ngày"
                : $"{summary.CurrentDailyOverdueRateVnd:N0} VND/day";

            RelayCommand.RaiseCanExecuteChanged();
        }

        private void ExecuteClearFilters()
        {
            _searchKeyword = string.Empty;
            _selectedStatusFilter = "ALL";
            OnPropertyChanged(nameof(SearchKeyword));
            OnPropertyChanged(nameof(SelectedStatusFilter));
            LoadFines();
        }

        private void ExecuteConfirmOverdueFine()
        {
            if (SelectedFine is null || !SelectedFine.CanBeConfirmed)
            {
                return;
            }

            ServiceOperationResult result = _fineService.ConfirmOrRecordOverdueFine(SelectedFine.BorrowRecordId);
            if (result.IsSuccess)
            {
                SetFeedback(result.Message, isError: false);
                _statusFeedbackCallback(result.Message);
                LoadFines();
            }
            else
            {
                SetFeedback(result.Message, isError: true);
            }
        }

        private void ExecuteOpenAdjustDialog()
        {
            if (SelectedFine is null || !SelectedFine.CanBeAdjustedOrWaived)
            {
                return;
            }

            AdjustAmountInput = SelectedFine.Amount.ToString("0");
            AdjustReasonInput = string.Empty;
            IsAdjustDialogOpen = true;
        }

        private void ExecuteSubmitAdjust()
        {
            if (SelectedFine is null || !SelectedFine.FineId.HasValue)
            {
                return;
            }

            if (!decimal.TryParse(AdjustAmountInput.Trim(), out decimal newAmount) || newAmount < 0)
            {
                SetFeedback(LocalizationService.IsVietnamese
                    ? "Số tiền điều chỉnh không hợp lệ (Phải là số không âm)."
                    : "Adjusted fine amount is invalid (Must be non-negative).", isError: true);
                return;
            }

            ServiceOperationResult result = _fineService.AdjustFineAmount(
                SelectedFine.FineId.Value,
                newAmount,
                AdjustReasonInput);

            if (result.IsSuccess)
            {
                IsAdjustDialogOpen = false;
                SetFeedback(result.Message, isError: false);
                _statusFeedbackCallback(result.Message);
                LoadFines();
            }
            else
            {
                SetFeedback(result.Message, isError: true);
            }
        }

        private void ExecuteOpenWaiveDialog()
        {
            if (SelectedFine is null || !SelectedFine.CanBeAdjustedOrWaived)
            {
                return;
            }

            WaiveReasonInput = string.Empty;
            IsWaiveDialogOpen = true;
        }

        private void ExecuteSubmitWaive()
        {
            if (SelectedFine is null || !SelectedFine.FineId.HasValue)
            {
                return;
            }

            ServiceOperationResult result = _fineService.WaiveFine(
                SelectedFine.FineId.Value,
                WaiveReasonInput);

            if (result.IsSuccess)
            {
                IsWaiveDialogOpen = false;
                SetFeedback(result.Message, isError: false);
                _statusFeedbackCallback(result.Message);
                LoadFines();
            }
            else
            {
                SetFeedback(result.Message, isError: true);
            }
        }

        private void ExecuteOpenAuditLogs()
        {
            if (SelectedFine is null || !SelectedFine.FineId.HasValue)
            {
                return;
            }

            AuditLogsList.Clear();
            var logs = _fineService.GetFineAuditLogs(SelectedFine.FineId.Value);
            foreach (var log in logs)
            {
                AuditLogsList.Add(log);
            }

            IsAuditLogsOpen = true;
        }

        private void ExecuteOpenRateDialog()
        {
            NewRateInput = _fineService.GetCurrentDailyOverdueRateVnd().ToString("0");
            IsRateDialogOpen = true;
        }

        private void ExecuteSubmitRateUpdate()
        {
            if (!decimal.TryParse(NewRateInput.Trim(), out decimal newRate) || newRate < 0)
            {
                SetFeedback(LocalizationService.IsVietnamese
                    ? "Mức phạt theo ngày không hợp lệ."
                    : "Daily fine rate is invalid.", isError: true);
                return;
            }

            ServiceOperationResult result = _fineService.UpdateOverdueDailyRate(newRate);
            if (result.IsSuccess)
            {
                IsRateDialogOpen = false;
                SetFeedback(result.Message, isError: false);
                _statusFeedbackCallback(result.Message);
                LoadFines();
            }
            else
            {
                SetFeedback(result.Message, isError: true);
            }
        }

        private void SetFeedback(string message, bool isError)
        {
            FeedbackMessage = message;
            HasError = isError;
            HasSuccess = !isError;
        }

        private void ClearFeedback()
        {
            FeedbackMessage = string.Empty;
            HasError = false;
            HasSuccess = false;
        }
    }
}

