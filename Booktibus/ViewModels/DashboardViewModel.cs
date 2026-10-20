using System.Collections.ObjectModel;
using System.Windows.Input;
using Booktibus.Commands;
using Booktibus.Models;
using Booktibus.Services;

namespace Booktibus.ViewModels
{
    /// <summary>
    /// ViewModel for the Dashboard page (Stage 6.4.2).
    /// Queries real-time statistics and circulation activity from SQL Server (BooktibusDB)
    /// for Librarian/Administrator (system-wide) and Reader (strictly personal UserId scope).
    /// </summary>
    public class DashboardViewModel : PageViewModelBase
    {
        private readonly SessionService _sessionService;
        private readonly AuthorizationService _authorizationService;
        private readonly ReaderWorkspaceService _readerWorkspaceService;
        private readonly LibraryManagementService _libraryManagementService;
        private readonly Action<NavigationPageKey> _navigateToPageCallback;
        private readonly Action<string> _statusFeedbackCallback;
        private SampleActivityRowModel? _selectedActivityRow;

        public DashboardViewModel(
            SessionService sessionService,
            AuthorizationService authorizationService,
            ReaderWorkspaceService readerWorkspaceService,
            LibraryManagementService libraryManagementService,
            Action<NavigationPageKey> navigateToPageCallback,
            Action<string> statusFeedbackCallback)
            : base(
                NavigationPageKey.Dashboard,
                "Str.Page.Dashboard.Title",
                "Str.Page.Dashboard.Subtitle")
        {
            _sessionService = sessionService;
            _authorizationService = authorizationService;
            _readerWorkspaceService = readerWorkspaceService;
            _libraryManagementService = libraryManagementService;
            _navigateToPageCallback = navigateToPageCallback;
            _statusFeedbackCallback = statusFeedbackCallback;

            MetricCards = new ObservableCollection<DashboardMetricCardModel>();
            RecentActivities = new ObservableCollection<SampleActivityRowModel>();
            SystemNotices = new ObservableCollection<SystemNoticeItemModel>();

            QuickNavigateCommand = new RelayCommand(ExecuteQuickNavigate);
            InspectActivityCommand = new RelayCommand(ExecuteInspectActivity);
            RefreshSampleDataCommand = new RelayCommand(ExecuteRefreshDashboardData);

            TestOwnReaderAccessCommand = new RelayCommand(ExecuteTestOwnReaderAccess);
            TestOtherReaderAccessCommand = new RelayCommand(ExecuteTestOtherReaderAccess);
            TestLibrarianPermissionCommand = new RelayCommand(ExecuteTestLibrarianPermission);
            TestAdminPermissionCommand = new RelayCommand(ExecuteTestAdminPermission);

            _libraryManagementService.CatalogChanged += (_, _) => PopulateDashboardContent();
            _libraryManagementService.CirculationChanged += (_, _) => PopulateDashboardContent();
            _libraryManagementService.ReadersChanged += (_, _) => PopulateDashboardContent();
            _readerWorkspaceService.FavoritesChanged += (_, _) => PopulateDashboardContent();

            PopulateDashboardContent();
        }

        public ObservableCollection<DashboardMetricCardModel> MetricCards { get; }

        public ObservableCollection<SampleActivityRowModel> RecentActivities { get; }

        public ObservableCollection<SystemNoticeItemModel> SystemNotices { get; }

        public SampleActivityRowModel? SelectedActivityRow
        {
            get => _selectedActivityRow;
            set
            {
                if (SetProperty(ref _selectedActivityRow, value) && value != null)
                {
                    string message = LocalizationService.IsVietnamese
                        ? $"[Dashboard] Đang chọn dòng: {value.ReferenceCode} — {value.SubjectSummary}"
                        : $"[Dashboard] Selected row: {value.ReferenceCode} — {value.SubjectSummary}";
                    _statusFeedbackCallback(message);
                }
            }
        }

        public ICommand QuickNavigateCommand { get; }

        public ICommand InspectActivityCommand { get; }

        public ICommand RefreshSampleDataCommand { get; }

        public ICommand TestOwnReaderAccessCommand { get; }

        public ICommand TestOtherReaderAccessCommand { get; }

        public ICommand TestLibrarianPermissionCommand { get; }

        public ICommand TestAdminPermissionCommand { get; }

        public override void OnLanguageUpdated()
        {
            base.OnLanguageUpdated();
            PopulateDashboardContent();
        }

        public void OnSessionUpdated()
        {
            PopulateDashboardContent();
        }

        private void ExecuteQuickNavigate(object? parameter)
        {
            if (parameter is NavigationPageKey pageKey)
            {
                _navigateToPageCallback(pageKey);
            }
            else if (parameter is string pageKeyStr && Enum.TryParse(pageKeyStr, out NavigationPageKey parsedKey))
            {
                _navigateToPageCallback(parsedKey);
            }
        }

        private void ExecuteInspectActivity()
        {
            if (SelectedActivityRow is null)
            {
                string prompt = LocalizationService.IsVietnamese
                    ? "[Dashboard] Vui lòng chọn một dòng trong bảng Hoạt động gần đây trước khi xem chi tiết."
                    : "[Dashboard] Please select a row in the Recent Activities table first.";
                _statusFeedbackCallback(prompt);
                return;
            }

            string detailMsg = LocalizationService.IsVietnamese
                ? $"[Chi tiết {SelectedActivityRow.ReferenceCode}] {SelectedActivityRow.ActivityType}: {SelectedActivityRow.SubjectSummary} ({SelectedActivityRow.DataStatusBadge})."
                : $"[Detail {SelectedActivityRow.ReferenceCode}] {SelectedActivityRow.ActivityType}: {SelectedActivityRow.SubjectSummary} ({SelectedActivityRow.DataStatusBadge}).";
            _statusFeedbackCallback(detailMsg);
        }

        private void ExecuteRefreshDashboardData()
        {
            PopulateDashboardContent();
            string refreshMsg = LocalizationService.IsVietnamese
                ? "[Dashboard] Đã cập nhật số liệu thống kê thực tế trực tiếp từ SQL Server (BooktibusDB)."
                : "[Dashboard] Refreshed live statistics directly from SQL Server (BooktibusDB).";
            _statusFeedbackCallback(refreshMsg);
        }

        private void ExecuteTestOwnReaderAccess()
        {
            string targetCard = _sessionService.CurrentSession?.ReaderCardId ?? "RD-2026-0002";
            if (_authorizationService.CanAccessReaderRecord(targetCard, out string denialMessage))
            {
                string okMsg = LocalizationService.IsVietnamese
                    ? $"[Phân quyền Hợp lệ — Tầng Service] Cho phép truy cập hồ sơ và lịch sử mượn trả của thẻ '{targetCard}'."
                    : $"[Service Layer Authorized] Permitted access to profile and borrowing history for card '{targetCard}'.";
                _statusFeedbackCallback(okMsg);
            }
            else
            {
                _statusFeedbackCallback(denialMessage);
            }
        }

        private void ExecuteTestOtherReaderAccess()
        {
            const string otherReaderCard = "RD-2026-9999";
            if (_authorizationService.CanAccessReaderRecord(otherReaderCard, out string denialMessage))
            {
                string okMsg = LocalizationService.IsVietnamese
                    ? $"[Phân quyền Hợp lệ — Tầng Service] Vai trò '{_sessionService.CurrentSession?.Role}' được phép tra cứu hồ sơ của độc giả '{otherReaderCard}'."
                    : $"[Service Layer Authorized] Role '{_sessionService.CurrentSession?.Role}' is permitted to inspect reader record '{otherReaderCard}'.";
                _statusFeedbackCallback(okMsg);
            }
            else
            {
                _statusFeedbackCallback(denialMessage);
            }
        }

        private void ExecuteTestLibrarianPermission()
        {
            _authorizationService.TryAuthorizeOperation(
                AppPermission.ManageCatalog,
                "ManageBookCatalog()",
                out string message);
            _statusFeedbackCallback(message);
        }

        private void ExecuteTestAdminPermission()
        {
            _authorizationService.TryAuthorizeOperation(
                AppPermission.ManageSystemSettings,
                "UpdateSystemSettings()",
                out string message);
            _statusFeedbackCallback(message);
        }

        private void PopulateDashboardContent()
        {
            MetricCards.Clear();
            RecentActivities.Clear();
            SystemNotices.Clear();

            UserSession? session = _sessionService.CurrentSession;
            if (session is null)
            {
                return;
            }

            bool isReader = session.Role == UserRole.Reader;
            string ownCardId = session.ReaderCardId ?? "RD-2026-0001";
            LibraryDatabaseStatisticsModel liveStats = _libraryManagementService.GetLiveDatabaseStatistics();

            if (isReader)
            {
                var ownBorrows = _readerWorkspaceService.GetOwnBorrowRecords("ALL", out _);
                var ownFavs = _readerWorkspaceService.GetOwnFavorites(out _);
                int ownActiveCount = ownBorrows.Count(r => r.BorrowStatus != "Returned");
                int ownReturnedCount = ownBorrows.Count(r => r.BorrowStatus == "Returned");

                if (LocalizationService.IsVietnamese)
                {
                    MetricCards.Add(new DashboardMetricCardModel
                    {
                        Title = "MÃ THẺ ĐỘC GIẢ CÁ NHÂN",
                        SampleValue = ownCardId,
                        SampleTag = "CSDL THỰC TẾ",
                        Subtitle = $"Tài khoản: {session.Username} (UserId={session.UserId})",
                        AccentHex = "#14284B"
                    });
                    MetricCards.Add(new DashboardMetricCardModel
                    {
                        Title = "SÁCH ĐANG MƯỢN CỦA TÔI",
                        SampleValue = ownActiveCount.ToString("D2"),
                        SampleTag = "CSDL THỰC TẾ",
                        Subtitle = $"Truy vấn từ dbo.BorrowRecords ({ownCardId})",
                        AccentHex = "#0F766E"
                    });
                    MetricCards.Add(new DashboardMetricCardModel
                    {
                        Title = "LỊCH SỬ ĐÃ TRẢ CỦA TÔI",
                        SampleValue = ownReturnedCount.ToString("D2"),
                        SampleTag = "CSDL THỰC TẾ",
                        Subtitle = $"Số phiếu đã trả hoàn tất ({ownCardId})",
                        AccentHex = "#C9A24D"
                    });
                    MetricCards.Add(new DashboardMetricCardModel
                    {
                        Title = "SÁCH YÊU THÍCH CỦA TÔI",
                        SampleValue = ownFavs.Count.ToString("D2"),
                        SampleTag = "CSDL THỰC TẾ",
                        Subtitle = $"Lưu độc lập theo UserId={session.UserId}",
                        AccentHex = "#15803D"
                    });

                    foreach (BorrowRecordItemModel br in ownBorrows.Take(8))
                    {
                        RecentActivities.Add(new SampleActivityRowModel
                        {
                            ReferenceCode = br.RecordCode,
                            ActivityType = br.LocalizedStatusDisplay,
                            SubjectSummary = $"{br.BookTitle} ({br.BookCode})",
                            ActorDisplay = br.ReaderCardId,
                            TimeDisplay = br.BorrowedDateDisplay,
                            DataStatusBadge = "CSDL THỰC TẾ"
                        });
                    }

                    if (RecentActivities.Count == 0)
                    {
                        RecentActivities.Add(new SampleActivityRowModel
                        {
                            ReferenceCode = "—",
                            ActivityType = "Trạng thái trống",
                            SubjectSummary = "Tài khoản của bạn hiện chưa có giao dịch mượn/trả nào trong bảng dbo.BorrowRecords.",
                            ActorDisplay = ownCardId,
                            TimeDisplay = DateTime.Now.ToString("dd/MM/yyyy"),
                            DataStatusBadge = "CHƯA CÓ GIAO DỊCH"
                        });
                    }
                }
                else
                {
                    MetricCards.Add(new DashboardMetricCardModel
                    {
                        Title = "MY READER CARD ID",
                        SampleValue = ownCardId,
                        SampleTag = "LIVE SQL DB",
                        Subtitle = $"Account: {session.Username} (UserId={session.UserId})",
                        AccentHex = "#14284B"
                    });
                    MetricCards.Add(new DashboardMetricCardModel
                    {
                        Title = "MY ACTIVE LOANS",
                        SampleValue = ownActiveCount.ToString("D2"),
                        SampleTag = "LIVE SQL DB",
                        Subtitle = $"Queried from dbo.BorrowRecords ({ownCardId})",
                        AccentHex = "#0F766E"
                    });
                    MetricCards.Add(new DashboardMetricCardModel
                    {
                        Title = "MY RETURNED LOANS",
                        SampleValue = ownReturnedCount.ToString("D2"),
                        SampleTag = "LIVE SQL DB",
                        Subtitle = $"Completed returns ({ownCardId})",
                        AccentHex = "#C9A24D"
                    });
                    MetricCards.Add(new DashboardMetricCardModel
                    {
                        Title = "MY FAVORITE BOOKS",
                        SampleValue = ownFavs.Count.ToString("D2"),
                        SampleTag = "LIVE SQL DB",
                        Subtitle = $"Isolated by UserId={session.UserId}",
                        AccentHex = "#15803D"
                    });

                    foreach (BorrowRecordItemModel br in ownBorrows.Take(8))
                    {
                        RecentActivities.Add(new SampleActivityRowModel
                        {
                            ReferenceCode = br.RecordCode,
                            ActivityType = br.LocalizedStatusDisplay,
                            SubjectSummary = $"{br.BookTitle} ({br.BookCode})",
                            ActorDisplay = br.ReaderCardId,
                            TimeDisplay = br.BorrowedDateDisplay,
                            DataStatusBadge = "LIVE SQL DB"
                        });
                    }

                    if (RecentActivities.Count == 0)
                    {
                        RecentActivities.Add(new SampleActivityRowModel
                        {
                            ReferenceCode = "—",
                            ActivityType = "Empty State",
                            SubjectSummary = "Your account currently has no borrowing records in dbo.BorrowRecords.",
                            ActorDisplay = ownCardId,
                            TimeDisplay = DateTime.Now.ToString("dd/MM/yyyy"),
                            DataStatusBadge = "NO RECORDS YET"
                        });
                    }
                }
            }
            else
            {
                // Librarian & Administrator: Live System-Wide Statistics from SQL Server (Stage 6.4.2)
                if (LocalizationService.IsVietnamese)
                {
                    MetricCards.Add(new DashboardMetricCardModel
                    {
                        Title = "ĐẦU SÁCH & BẢN SAO (CSDL)",
                        SampleValue = $"{liveStats.TotalBookTitles} ({liveStats.AvailableBookCopies}/{liveStats.TotalBookCopies})",
                        SampleTag = "CSDL THỰC TẾ",
                        Subtitle = $"{liveStats.TotalBookTitles} đầu sách • Còn {liveStats.AvailableBookCopies}/{liveStats.TotalBookCopies} bản sao",
                        AccentHex = "#14284B"
                    });
                    MetricCards.Add(new DashboardMetricCardModel
                    {
                        Title = "TỔNG SỐ ĐỘC GIẢ (CSDL)",
                        SampleValue = liveStats.TotalRegisteredReaders.ToString("D2"),
                        SampleTag = "CSDL THỰC TẾ",
                        Subtitle = $"Truy vấn từ dbo.Readers (Tổng TK: {liveStats.TotalUsersCount})",
                        AccentHex = "#0F766E"
                    });
                    MetricCards.Add(new DashboardMetricCardModel
                    {
                        Title = "GIAO DỊCH ĐANG MƯỢN",
                        SampleValue = liveStats.ActiveBorrowedLoans.ToString("D2"),
                        SampleTag = "CSDL THỰC TẾ",
                        Subtitle = $"Đã trả hoàn tất: {liveStats.ReturnedLoans} phiếu",
                        AccentHex = "#C9A24D"
                    });
                    MetricCards.Add(new DashboardMetricCardModel
                    {
                        Title = "GIAO DỊCH QUÁ HẠN",
                        SampleValue = liveStats.OverdueLoans.ToString("D2"),
                        SampleTag = "CSDL THỰC TẾ",
                        Subtitle = "Tự động phát hiện theo hạn trả thực tế",
                        AccentHex = "#B45309"
                    });

                    foreach (StaffBorrowRecordItemModel br in liveStats.RecentLoanTransactions)
                    {
                        RecentActivities.Add(new SampleActivityRowModel
                        {
                            ReferenceCode = br.RecordCode,
                            ActivityType = br.LocalizedStatusDisplay,
                            SubjectSummary = $"{br.BookTitle} ({br.BookCode})",
                            ActorDisplay = $"{br.ReaderCardId} ({br.Username})",
                            TimeDisplay = br.BorrowedDateDisplay,
                            DataStatusBadge = "CSDL THỰC TẾ"
                        });
                    }

                    if (RecentActivities.Count == 0)
                    {
                        RecentActivities.Add(new SampleActivityRowModel
                        {
                            ReferenceCode = "—",
                            ActivityType = "Trạng thái trống",
                            SubjectSummary = "Bảng dbo.BorrowRecords hiện chưa có giao dịch mượn trả nào. Hãy mở 'Mượn & Trả sách' để lập phiếu mượn đầu tiên.",
                            ActorDisplay = "BooktibusDB",
                            TimeDisplay = DateTime.Now.ToString("dd/MM/yyyy"),
                            DataStatusBadge = "CHƯA CÓ GIAO DỊCH"
                        });
                    }
                }
                else
                {
                    MetricCards.Add(new DashboardMetricCardModel
                    {
                        Title = "BOOK TITLES & COPIES (DB)",
                        SampleValue = $"{liveStats.TotalBookTitles} ({liveStats.AvailableBookCopies}/{liveStats.TotalBookCopies})",
                        SampleTag = "LIVE SQL DB",
                        Subtitle = $"{liveStats.TotalBookTitles} titles • {liveStats.AvailableBookCopies}/{liveStats.TotalBookCopies} copies available",
                        AccentHex = "#14284B"
                    });
                    MetricCards.Add(new DashboardMetricCardModel
                    {
                        Title = "REGISTERED READERS (DB)",
                        SampleValue = liveStats.TotalRegisteredReaders.ToString("D2"),
                        SampleTag = "LIVE SQL DB",
                        Subtitle = $"From dbo.Readers (Total accounts: {liveStats.TotalUsersCount})",
                        AccentHex = "#0F766E"
                    });
                    MetricCards.Add(new DashboardMetricCardModel
                    {
                        Title = "ACTIVE LOANS (DB)",
                        SampleValue = liveStats.ActiveBorrowedLoans.ToString("D2"),
                        SampleTag = "LIVE SQL DB",
                        Subtitle = $"Completed returns: {liveStats.ReturnedLoans}",
                        AccentHex = "#C9A24D"
                    });
                    MetricCards.Add(new DashboardMetricCardModel
                    {
                        Title = "OVERDUE LOANS (DB)",
                        SampleValue = liveStats.OverdueLoans.ToString("D2"),
                        SampleTag = "LIVE SQL DB",
                        Subtitle = "Auto-detected from actual DueAtUtc",
                        AccentHex = "#B45309"
                    });

                    foreach (StaffBorrowRecordItemModel br in liveStats.RecentLoanTransactions)
                    {
                        RecentActivities.Add(new SampleActivityRowModel
                        {
                            ReferenceCode = br.RecordCode,
                            ActivityType = br.LocalizedStatusDisplay,
                            SubjectSummary = $"{br.BookTitle} ({br.BookCode})",
                            ActorDisplay = $"{br.ReaderCardId} ({br.Username})",
                            TimeDisplay = br.BorrowedDateDisplay,
                            DataStatusBadge = "LIVE SQL DB"
                        });
                    }

                    if (RecentActivities.Count == 0)
                    {
                        RecentActivities.Add(new SampleActivityRowModel
                        {
                            ReferenceCode = "—",
                            ActivityType = "Empty State",
                            SubjectSummary = "No circulation transactions exist in dbo.BorrowRecords yet. Open 'Borrowing & Returns' to issue a loan.",
                            ActorDisplay = "BooktibusDB",
                            TimeDisplay = DateTime.Now.ToString("dd/MM/yyyy"),
                            DataStatusBadge = "NO RECORDS YET"
                        });
                    }
                }
            }

            if (LocalizationService.IsVietnamese)
            {
                SystemNotices.Add(new SystemNoticeItemModel
                {
                    CategoryBadge = "PHIÊN ĐĂNG NHẬP HIỆN TẠI",
                    Title = $"{session.FullName} ({session.Role})",
                    Description = isReader
                        ? $"Tầng Service đang cô lập dữ liệu chỉ hiển thị các bản ghi thuộc UserId={session.UserId} (Mã thẻ '{ownCardId}')."
                        : $"Tài khoản đang hoạt động với quyền '{session.Role}' — truy vấn số liệu trực tiếp từ SQL Server (BooktibusDB).",
                    BannerType = "Info"
                });
                SystemNotices.Add(new SystemNoticeItemModel
                {
                    CategoryBadge = "BẢO MẬT & TOÀN VẸN DỮ LIỆU",
                    Title = "PBKDF2-SHA256 & Kiểm soát Giao dịch SQL Server",
                    Description = "Mọi thao tác quản lý sách, mượn trả, độc giả và tài khoản đều được kiểm tra quyền tại tầng Service trước khi thực thi.",
                    BannerType = "Warning"
                });
            }
            else
            {
                SystemNotices.Add(new SystemNoticeItemModel
                {
                    CategoryBadge = "ACTIVE SESSION SCOPE",
                    Title = $"{session.FullName} ({session.Role})",
                    Description = isReader
                        ? $"Service layer strictly isolates records to UserId={session.UserId} (Reader Card '{ownCardId}')."
                        : $"Signed in with '{session.Role}' privileges — querying live metrics from SQL Server (BooktibusDB).",
                    BannerType = "Info"
                });
                SystemNotices.Add(new SystemNoticeItemModel
                {
                    CategoryBadge = "SECURITY & TRANSACTION INTEGRITY",
                    Title = "PBKDF2-SHA256 & Service-Layer Guards",
                    Description = "All catalog, circulation, reader, and account operations are authorized at the service layer before execution.",
                    BannerType = "Warning"
                });
            }

            if (RecentActivities.Count > 0)
            {
                _selectedActivityRow = RecentActivities[0];
                OnPropertyChanged(nameof(SelectedActivityRow));
            }
        }
    }
}

