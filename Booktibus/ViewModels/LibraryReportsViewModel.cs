using System.Collections.ObjectModel;
using System.Windows.Input;
using Booktibus.Commands;
using Booktibus.Models;
using Booktibus.Services;

namespace Booktibus.ViewModels
{
    /// <summary>
    /// ViewModel for Library Reports &amp; Statistics (Stage 6.4.2).
    /// Queries live statistics from BooktibusDB (Total Titles, Total/Available Copies,
    /// Registered Readers, Active/Overdue/Returned Loans, Category Breakdown, and Monthly Circulation Breakdown).
    /// </summary>
    public class LibraryReportsViewModel : PageViewModelBase
    {
        private readonly LibraryManagementService _libraryManagementService;
        private readonly Action<string> _statusFeedbackCallback;

        private int _totalBookTitles;
        private int _totalBookCopies;
        private int _availableBookCopies;
        private int _totalRegisteredReaders;
        private int _activeBorrowedLoans;
        private int _overdueLoans;
        private int _returnedLoans;

        public LibraryReportsViewModel(
            LibraryManagementService libraryManagementService,
            Action<string> statusFeedbackCallback)
            : base(
                NavigationPageKey.Reports,
                "Str.Page.Reports.Title",
                "Str.Page.Reports.Subtitle")
        {
            _libraryManagementService = libraryManagementService;
            _statusFeedbackCallback = statusFeedbackCallback;

            CategoryStats = new ObservableCollection<CategoryStatRowModel>();
            MonthlyLoanStats = new ObservableCollection<PeriodLoanStatRowModel>();

            RefreshReportsCommand = new RelayCommand(ExecuteRefreshReports);

            _libraryManagementService.CatalogChanged += (_, _) => LoadLiveStatistics();
            _libraryManagementService.CirculationChanged += (_, _) => LoadLiveStatistics();
            _libraryManagementService.ReadersChanged += (_, _) => LoadLiveStatistics();
        }

        public ObservableCollection<CategoryStatRowModel> CategoryStats { get; }

        public ObservableCollection<PeriodLoanStatRowModel> MonthlyLoanStats { get; }

        public int TotalBookTitles
        {
            get => _totalBookTitles;
            private set => SetProperty(ref _totalBookTitles, value);
        }

        public int TotalBookCopies
        {
            get => _totalBookCopies;
            private set
            {
                if (SetProperty(ref _totalBookCopies, value))
                {
                    OnPropertyChanged(nameof(CopiesBreakdownDisplay));
                }
            }
        }

        public int AvailableBookCopies
        {
            get => _availableBookCopies;
            private set
            {
                if (SetProperty(ref _availableBookCopies, value))
                {
                    OnPropertyChanged(nameof(CopiesBreakdownDisplay));
                }
            }
        }

        public string CopiesBreakdownDisplay => $"{AvailableBookCopies} / {TotalBookCopies}";

        public int TotalRegisteredReaders
        {
            get => _totalRegisteredReaders;
            private set => SetProperty(ref _totalRegisteredReaders, value);
        }

        public int ActiveBorrowedLoans
        {
            get => _activeBorrowedLoans;
            private set => SetProperty(ref _activeBorrowedLoans, value);
        }

        public int OverdueLoans
        {
            get => _overdueLoans;
            private set => SetProperty(ref _overdueLoans, value);
        }

        public int ReturnedLoans
        {
            get => _returnedLoans;
            private set => SetProperty(ref _returnedLoans, value);
        }

        public bool IsMonthlyStatsEmpty => MonthlyLoanStats.Count == 0;

        public ICommand RefreshReportsCommand { get; }

        public override void OnLanguageUpdated()
        {
            base.OnLanguageUpdated();
            LoadLiveStatistics();
        }

        public void OnSessionUpdated()
        {
            LoadLiveStatistics();
        }

        private void ExecuteRefreshReports()
        {
            LoadLiveStatistics();
            _statusFeedbackCallback(LocalizationService.IsVietnamese
                ? $"[Báo cáo & Thống kê] Đã cập nhật số liệu trực tiếp từ SQL Server (BooktibusDB): {TotalBookTitles} đầu sách ({TotalBookCopies} bản sao), {TotalRegisteredReaders} độc giả, {ActiveBorrowedLoans} đang mượn."
                : $"[Reports & Analytics] Refreshed live SQL Server statistics: {TotalBookTitles} titles ({TotalBookCopies} copies), {TotalRegisteredReaders} readers, {ActiveBorrowedLoans} active loans.");
        }

        private void LoadLiveStatistics()
        {
            LibraryDatabaseStatisticsModel stats = _libraryManagementService.GetLiveDatabaseStatistics();

            TotalBookTitles = stats.TotalBookTitles;
            TotalBookCopies = stats.TotalBookCopies;
            AvailableBookCopies = stats.AvailableBookCopies;
            TotalRegisteredReaders = stats.TotalRegisteredReaders;
            ActiveBorrowedLoans = stats.ActiveBorrowedLoans;
            OverdueLoans = stats.OverdueLoans;
            ReturnedLoans = stats.ReturnedLoans;

            CategoryStats.Clear();
            foreach (CategoryStatRowModel cat in stats.CategoryBreakdown)
            {
                CategoryStats.Add(cat);
            }

            MonthlyLoanStats.Clear();
            foreach (PeriodLoanStatRowModel month in stats.MonthlyLoanBreakdown)
            {
                MonthlyLoanStats.Add(month);
            }

            OnPropertyChanged(nameof(IsMonthlyStatsEmpty));
            OnPropertyChanged(nameof(CopiesBreakdownDisplay));
        }
    }
}

