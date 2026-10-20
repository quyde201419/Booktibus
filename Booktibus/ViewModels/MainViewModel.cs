using System.Collections.ObjectModel;
using System.Windows.Input;
using Booktibus.Commands;
using Booktibus.Models;
using Booktibus.Services;

namespace Booktibus.ViewModels
{
    /// <summary>
    /// Main window ViewModel coordinating Authentication (Login/Logout), Session state,
    /// Service-layer Authorization, Sidebar navigation, and bilingual UI updates.
    /// </summary>
    public class MainViewModel : BaseViewModel
    {
        private readonly SessionService _sessionService;
        private readonly AuthorizationService _authorizationService;
        private readonly TestAuthenticationService _testAuthenticationService;
        private readonly SqlAuthenticationService _sqlAuthenticationService;
        private readonly ReaderWorkspaceService _readerWorkspaceService;
        private readonly LibraryManagementService _libraryManagementService;
        private readonly FineManagementService _fineManagementService;
        private readonly Dictionary<NavigationPageKey, PageViewModelBase> _pagesByKey = new();
        private PageViewModelBase _currentPage = null!;
        private string _statusMessage = string.Empty;

        public MainViewModel()
        {
            _sessionService = new SessionService();
            _authorizationService = new AuthorizationService(_sessionService);
            _testAuthenticationService = new TestAuthenticationService();
            _sqlAuthenticationService = new SqlAuthenticationService();
            _readerWorkspaceService = new ReaderWorkspaceService(_sessionService, _authorizationService);
            _libraryManagementService = new LibraryManagementService(_sessionService, _authorizationService);
            _fineManagementService = new FineManagementService(_sessionService, _authorizationService);

            LoginViewModel = new LoginViewModel(_testAuthenticationService, _sqlAuthenticationService, _sessionService);
            SidebarItems = new ObservableCollection<NavigationItemModel>();

            NavigateCommand = new RelayCommand(ExecuteNavigate);
            ToggleLanguageCommand = new RelayCommand(ExecuteToggleLanguage);
            AccountProfileCommand = new RelayCommand(ExecuteAccountProfileAction);
            LogoutCommand = new RelayCommand(ExecuteLogout, () => IsAuthenticated);

            InitializePagesAndSidebar();

            _sessionService.SessionChanged += OnSessionChanged;
            _readerWorkspaceService.ProfileUpdated += (_, _) => OnPropertyChanged(nameof(CurrentAccountDisplayName));
            LocalizationService.LanguageChanged += OnLanguageChanged;
        }

        public LoginViewModel LoginViewModel { get; }

        public ObservableCollection<NavigationItemModel> SidebarItems { get; }

        public bool IsAuthenticated => _sessionService.IsAuthenticated;

        public PageViewModelBase CurrentPage
        {
            get => _currentPage;
            private set => SetProperty(ref _currentPage, value);
        }

        public string CurrentAccountDisplayName =>
            _sessionService.CurrentSession?.FullName ?? string.Empty;

        public string CurrentAccountRoleBadge
        {
            get
            {
                if (_sessionService.CurrentSession is not { } session)
                {
                    return string.Empty;
                }

                return session.Role switch
                {
                    UserRole.Administrator => "ADMINISTRATOR",
                    UserRole.Librarian => "LIBRARIAN",
                    UserRole.Reader => string.IsNullOrWhiteSpace(session.ReaderCardId)
                        ? "READER"
                        : $"READER • {session.ReaderCardId}",
                    _ => "USER"
                };
            }
        }

        public string CurrentAccountInitials
        {
            get
            {
                if (_sessionService.CurrentSession is not { } session)
                {
                    return "BK";
                }

                return session.Role switch
                {
                    UserRole.Administrator => "AD",
                    UserRole.Librarian => "LB",
                    UserRole.Reader => "RD",
                    _ => "BK"
                };
            }
        }

        public string StatusMessage
        {
            get => _statusMessage;
            private set => SetProperty(ref _statusMessage, value);
        }

        public ICommand NavigateCommand { get; }

        public ICommand ToggleLanguageCommand { get; }

        public ICommand AccountProfileCommand { get; }

        public ICommand LogoutCommand { get; }

        /// <summary>
        /// Navigates to the requested page only after verifying role authorization at the Service layer.
        /// </summary>
        public void NavigateToPage(NavigationPageKey targetKey)
        {
            if (!_authorizationService.CanAccessPage(targetKey))
            {
                string roleName = _sessionService.CurrentSession?.Role.ToString() ?? "Unauthenticated";
                StatusMessage = LocalizationService.IsVietnamese
                    ? $"[Chặn ở tầng Service] Vai trò '{roleName}' không được phép mở trang '{targetKey}'."
                    : $"[Blocked by Service Layer] Role '{roleName}' is not authorized to open page '{targetKey}'.";
                return;
            }

            foreach (NavigationItemModel item in SidebarItems)
            {
                item.IsSelected = item.PageKey == targetKey;
            }

            if (_pagesByKey.TryGetValue(targetKey, out PageViewModelBase? pageViewModel))
            {
                CurrentPage = pageViewModel;
                StatusMessage = LocalizationService.IsVietnamese
                    ? $"[Điều hướng Hợp lệ] Đang hiển thị trang: {CurrentPage.PageTitle}"
                    : $"[Authorized Navigation] Viewing page: {CurrentPage.PageTitle}";
            }
        }

        private void OnSessionChanged(object? sender, UserSession? session)
        {
            OnPropertyChanged(nameof(IsAuthenticated));
            OnPropertyChanged(nameof(CurrentAccountDisplayName));
            OnPropertyChanged(nameof(CurrentAccountRoleBadge));
            OnPropertyChanged(nameof(CurrentAccountInitials));
            RelayCommand.RaiseCanExecuteChanged();

            if (session is not null)
            {
                ApplyRoleVisibility(session.Role);
                RefreshPagesForSession();
                NavigateToPage(NavigationPageKey.Dashboard);

                StatusMessage = LocalizationService.IsVietnamese
                    ? $"[Đăng nhập thành công] Xin chào {session.FullName} (Vai trò: {session.Role})."
                    : $"[Signed In] Welcome {session.FullName} (Role: {session.Role}).";
            }
        }

        private void ExecuteLogout()
        {
            string previousUser = _sessionService.CurrentSession?.FullName ?? string.Empty;
            _sessionService.EndSession();

            string logoutNotice = LocalizationService.IsVietnamese
                ? $"Đã đăng xuất phiên làm việc của '{previousUser}'."
                : $"Successfully signed out session for '{previousUser}'.";
            LoginViewModel.ResetForLogout(logoutNotice);
        }

        private void ExecuteNavigate(object? parameter)
        {
            if (parameter is NavigationItemModel navItem)
            {
                NavigateToPage(navItem.PageKey);
            }
            else if (parameter is NavigationPageKey pageKey)
            {
                NavigateToPage(pageKey);
            }
            else if (parameter is string keyString && Enum.TryParse(keyString, out NavigationPageKey parsedKey))
            {
                NavigateToPage(parsedKey);
            }
        }

        private void ExecuteToggleLanguage()
        {
            LocalizationService.ToggleLanguage();
        }

        private void ExecuteAccountProfileAction()
        {
            if (_sessionService.CurrentSession is not { } session)
            {
                return;
            }

            if (_authorizationService.CanAccessPage(NavigationPageKey.ReaderProfile))
            {
                NavigateToPage(NavigationPageKey.ReaderProfile);
            }

            string cardInfo = string.IsNullOrWhiteSpace(session.ReaderCardId)
                ? string.Empty
                : $" | Card: {session.ReaderCardId}";

            StatusMessage = LocalizationService.IsVietnamese
                ? $"[Phiên hiện tại] Họ tên: {session.FullName} | Tài khoản: {session.Username} (UserId={session.UserId}) | Vai trò: {session.Role}{cardInfo} | Đăng nhập lúc: {session.SignedInAtLocal:HH:mm:ss}."
                : $"[Active Session] Name: {session.FullName} | Username: {session.Username} (UserId={session.UserId}) | Role: {session.Role}{cardInfo} | Signed in at: {session.SignedInAtLocal:HH:mm:ss}.";
        }

        private void OnLanguageChanged(object? sender, EventArgs e)
        {
            LoginViewModel.OnLanguageUpdated();

            foreach (NavigationItemModel navItem in SidebarItems)
            {
                navItem.RefreshLocalization();
            }

            foreach (PageViewModelBase page in _pagesByKey.Values)
            {
                page.OnLanguageUpdated();
            }

            OnPropertyChanged(nameof(CurrentAccountDisplayName));
            OnPropertyChanged(nameof(CurrentAccountRoleBadge));

            if (IsAuthenticated && CurrentPage is not null)
            {
                StatusMessage = LocalizationService.IsVietnamese
                    ? $"[Ngôn ngữ] Đã chuyển giao diện sang Tiếng Việt. Trang hiện tại: {CurrentPage.PageTitle}"
                    : $"[Language] Switched interface to English. Current page: {CurrentPage.PageTitle}";
            }
        }

        private void InitializePagesAndSidebar()
        {
            RegisterPageAndSidebarItem(
                new DashboardViewModel(
                    _sessionService,
                    _authorizationService,
                    _readerWorkspaceService,
                    _libraryManagementService,
                    NavigateToPage,
                    msg => StatusMessage = msg),
                "◈",
                "Str.Nav.Dashboard",
                AuthorizationService.GetAllowedRolesForPage(NavigationPageKey.Dashboard));

            RegisterPageAndSidebarItem(
                new BookCatalogViewModel(
                    _readerWorkspaceService,
                    _libraryManagementService,
                    _authorizationService,
                    NavigateToPage,
                    msg => StatusMessage = msg),
                "▤",
                "Str.Nav.Books",
                AuthorizationService.GetAllowedRolesForPage(NavigationPageKey.Books));

            RegisterPageAndSidebarItem(
                new ReaderFavoritesViewModel(
                    _readerWorkspaceService,
                    NavigateToPage,
                    msg => StatusMessage = msg),
                "♥",
                "Str.Nav.Favorites",
                AuthorizationService.GetAllowedRolesForPage(NavigationPageKey.Favorites));

            RegisterPageAndSidebarItem(
                new ReaderBorrowingViewModel(
                    _sessionService,
                    _authorizationService,
                    _readerWorkspaceService,
                    _libraryManagementService,
                    NavigateToPage,
                    msg => StatusMessage = msg),
                "⇄",
                "Str.Nav.BorrowingReturns",
                AuthorizationService.GetAllowedRolesForPage(NavigationPageKey.BorrowingReturns));

            RegisterPageAndSidebarItem(
                new ReaderProfileViewModel(
                    _readerWorkspaceService,
                    msg => StatusMessage = msg),
                "◉",
                "Str.Nav.ReaderProfile",
                AuthorizationService.GetAllowedRolesForPage(NavigationPageKey.ReaderProfile));

            RegisterPageAndSidebarItem(
                new LibrarianReadersViewModel(
                    _libraryManagementService,
                    _readerWorkspaceService,
                    msg => StatusMessage = msg),
                "❖",
                "Str.Nav.Readers",
                AuthorizationService.GetAllowedRolesForPage(NavigationPageKey.Readers));

            RegisterPageAndSidebarItem(
                new FinesViewModel(
                    _fineManagementService,
                    _sessionService,
                    _authorizationService,
                    msg => StatusMessage = msg),
                "§",
                "Str.Nav.Fines",
                AuthorizationService.GetAllowedRolesForPage(NavigationPageKey.Fines));

            RegisterPageAndSidebarItem(
                new LibraryReportsViewModel(
                    _libraryManagementService,
                    msg => StatusMessage = msg),
                "▣",
                "Str.Nav.Reports",
                AuthorizationService.GetAllowedRolesForPage(NavigationPageKey.Reports));

            RegisterPageAndSidebarItem(
                new AdminAccountManagementViewModel(
                    _libraryManagementService,
                    msg => StatusMessage = msg),
                "⚙",
                "Str.Nav.Settings",
                AuthorizationService.GetAllowedRolesForPage(NavigationPageKey.Settings));

            CurrentPage = _pagesByKey[NavigationPageKey.Dashboard];
        }

        private void RegisterPageAndSidebarItem(
            PageViewModelBase pageViewModel,
            string iconGlyph,
            string sidebarTitleResourceKey,
            IReadOnlyCollection<UserRole> allowedRoles)
        {
            _pagesByKey[pageViewModel.PageKey] = pageViewModel;
            SidebarItems.Add(new NavigationItemModel(
                pageViewModel.PageKey,
                iconGlyph,
                sidebarTitleResourceKey,
                allowedRoles));
        }

        private void ApplyRoleVisibility(UserRole role)
        {
            foreach (NavigationItemModel item in SidebarItems)
            {
                item.UpdateForRole(role);
            }
        }

        private void RefreshPagesForSession()
        {
            foreach (PageViewModelBase page in _pagesByKey.Values)
            {
                switch (page)
                {
                    case DashboardViewModel dashVm:
                        dashVm.OnSessionUpdated();
                        break;
                    case BookCatalogViewModel catalogVm:
                        catalogVm.OnSessionUpdated();
                        break;
                    case ReaderFavoritesViewModel favVm:
                        favVm.OnSessionUpdated();
                        break;
                    case ReaderBorrowingViewModel borrowVm:
                        borrowVm.OnSessionUpdated();
                        break;
                    case ReaderProfileViewModel profileVm:
                        profileVm.OnSessionUpdated();
                        break;
                    case LibrarianReadersViewModel readersVm:
                        readersVm.OnSessionUpdated();
                        break;
                    case LibraryReportsViewModel reportsVm:
                        reportsVm.OnSessionUpdated();
                        break;
                    case AdminAccountManagementViewModel adminAccountsVm:
                        adminAccountsVm.OnSessionUpdated();
                        break;
                    case FinesViewModel finesVm:
                        finesVm.OnSessionUpdated();
                        break;
                }
            }
        }
    }
}

