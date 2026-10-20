using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using Booktibus.Commands;
using Booktibus.Models;
using Booktibus.Services;

namespace Booktibus.ViewModels
{
    /// <summary>
    /// ViewModel for Administrator Account &amp; Role Management (Stage 6.4.1).
    /// Strictly guarded at the service layer by AppPermission.ManageSystemSettings.
    /// Protects the last active Administrator account and uses PBKDF2-SHA256 for password resets.
    /// </summary>
    public class AdminAccountManagementViewModel : PageViewModelBase
    {
        private readonly LibraryManagementService _libraryManagementService;
        private readonly Action<string> _statusFeedbackCallback;

        private string _searchKeyword = string.Empty;
        private FilterOptionItem? _selectedRoleFilter;
        private ManagedUserAccountModel? _selectedUser;
        private string _selectedTargetRole = "Reader";

        // Reset password fields (never hardcoded default password)
        private string _resetPasswordInput = string.Empty;
        private string _resetConfirmPasswordInput = string.Empty;

        // Create new account fields
        private string _newUsername = string.Empty;
        private string _newFullName = string.Empty;
        private string _newEmail = string.Empty;
        private string _newPhone = string.Empty;
        private string _newRole = "Librarian";
        private string _newPassword = string.Empty;
        private string _newConfirmPassword = string.Empty;

        private string _feedbackMessage = string.Empty;
        private bool _isFeedbackSuccess;
        private bool _isSuppressingReload;

        public AdminAccountManagementViewModel(
            LibraryManagementService libraryManagementService,
            Action<string> statusFeedbackCallback)
            : base(
                NavigationPageKey.Settings,
                "Str.Page.Settings.Title",
                "Str.Page.Settings.Subtitle")
        {
            _libraryManagementService = libraryManagementService;
            _statusFeedbackCallback = statusFeedbackCallback;

            ManagedUsers = new ObservableCollection<ManagedUserAccountModel>();
            RoleFilterOptions = new ObservableCollection<FilterOptionItem>();
            RoleChoices = ["Reader", "Librarian", "Administrator"];

            SearchCommand = new RelayCommand(ExecuteSearch);
            ClearFiltersCommand = new RelayCommand(ExecuteClearFilters);
            ApplyRoleChangeCommand = new RelayCommand(ExecuteApplyRoleChange);
            ToggleActiveStatusCommand = new RelayCommand(ExecuteToggleActiveStatus);
            UnlockAccountCommand = new RelayCommand(ExecuteUnlockAccount);
            ResetPasswordCommand = new RelayCommand(ExecuteResetPassword);
            CreateUserAccountCommand = new RelayCommand(ExecuteCreateUserAccount);

            _libraryManagementService.AccountsChanged += (_, _) => ReloadAccountsPreservingSelection();

            InitializeRoleFilters();
        }

        public ObservableCollection<ManagedUserAccountModel> ManagedUsers { get; }

        public ObservableCollection<FilterOptionItem> RoleFilterOptions { get; }

        public IReadOnlyList<string> RoleChoices { get; }

        public string SearchKeyword
        {
            get => _searchKeyword;
            set => SetProperty(ref _searchKeyword, value);
        }

        public FilterOptionItem? SelectedRoleFilter
        {
            get => _selectedRoleFilter;
            set
            {
                if (SetProperty(ref _selectedRoleFilter, value) && !_isSuppressingReload)
                {
                    LoadUsersFromDatabase();
                }
            }
        }

        public ManagedUserAccountModel? SelectedUser
        {
            get => _selectedUser;
            set
            {
                if (SetProperty(ref _selectedUser, value))
                {
                    OnPropertyChanged(nameof(HasSelectedUser));
                    OnPropertyChanged(nameof(ToggleActiveButtonLabel));
                    if (value is not null)
                    {
                        SelectedTargetRole = value.Role;
                    }
                }
            }
        }

        public bool HasSelectedUser => SelectedUser is not null;

        public string ToggleActiveButtonLabel => SelectedUser?.IsActive == true
            ? (LocalizationService.IsVietnamese ? "Vô hiệu hóa Tài khoản" : "Deactivate Account")
            : (LocalizationService.IsVietnamese ? "Kích hoạt lại Tài khoản" : "Activate Account");

        public string AccountsSummaryText => LocalizationService.IsVietnamese
            ? $"Tổng số {ManagedUsers.Count} tài khoản trong bảng dbo.Users"
            : $"Total {ManagedUsers.Count} user account(s) in dbo.Users";

        public string SelectedTargetRole
        {
            get => _selectedTargetRole;
            set => SetProperty(ref _selectedTargetRole, value);
        }

        public string ResetPasswordInput
        {
            get => _resetPasswordInput;
            set => SetProperty(ref _resetPasswordInput, value);
        }

        public string ResetConfirmPasswordInput
        {
            get => _resetConfirmPasswordInput;
            set => SetProperty(ref _resetConfirmPasswordInput, value);
        }

        public string NewUsername
        {
            get => _newUsername;
            set => SetProperty(ref _newUsername, value);
        }

        public string NewFullName
        {
            get => _newFullName;
            set => SetProperty(ref _newFullName, value);
        }

        public string NewEmail
        {
            get => _newEmail;
            set => SetProperty(ref _newEmail, value);
        }

        public string NewPhone
        {
            get => _newPhone;
            set => SetProperty(ref _newPhone, value);
        }

        public string NewRole
        {
            get => _newRole;
            set => SetProperty(ref _newRole, value);
        }

        public string NewPassword
        {
            get => _newPassword;
            set => SetProperty(ref _newPassword, value);
        }

        public string NewConfirmPassword
        {
            get => _newConfirmPassword;
            set => SetProperty(ref _newConfirmPassword, value);
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

        public ICommand ApplyRoleChangeCommand { get; }

        public ICommand ToggleActiveStatusCommand { get; }

        public ICommand UnlockAccountCommand { get; }

        public ICommand ResetPasswordCommand { get; }

        public ICommand CreateUserAccountCommand { get; }

        public override void OnLanguageUpdated()
        {
            base.OnLanguageUpdated();
            InitializeRoleFilters();
            ReloadAccountsPreservingSelection();
            OnPropertyChanged(nameof(AccountsSummaryText));
            OnPropertyChanged(nameof(ToggleActiveButtonLabel));
        }

        public void OnSessionUpdated()
        {
            FeedbackMessage = string.Empty;
            InitializeRoleFilters();
            LoadUsersFromDatabase();
        }

        private void ExecuteSearch()
        {
            LoadUsersFromDatabase();
            _statusFeedbackCallback(AccountsSummaryText);
        }

        private void ExecuteClearFilters()
        {
            _isSuppressingReload = true;
            SearchKeyword = string.Empty;
            SelectedRoleFilter = RoleFilterOptions.FirstOrDefault();
            _isSuppressingReload = false;

            LoadUsersFromDatabase();
        }

        private void ExecuteApplyRoleChange()
        {
            if (SelectedUser is null)
            {
                return;
            }

            string confirmMsg = LocalizationService.IsVietnamese
                ? $"Bạn có chắc chắn muốn đổi vai trò của tài khoản '{SelectedUser.Username}' (UserId={SelectedUser.UserId}) từ '{SelectedUser.Role}' sang '{SelectedTargetRole}'?"
                : $"Are you sure you want to change the role of '{SelectedUser.Username}' (UserId={SelectedUser.UserId}) from '{SelectedUser.Role}' to '{SelectedTargetRole}'?";

            string confirmTitle = LocalizationService.IsVietnamese
                ? "Xác nhận Thay đổi Vai trò"
                : "Confirm Role Change";

            if (MessageBox.Show(confirmMsg, confirmTitle, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                return;
            }

            ServiceOperationResult res = _libraryManagementService.UpdateUserRole(SelectedUser.UserId, SelectedTargetRole);
            IsFeedbackSuccess = res.IsSuccess;
            FeedbackMessage = res.Message;
            _statusFeedbackCallback(res.Message);
        }

        private void ExecuteToggleActiveStatus()
        {
            if (SelectedUser is null)
            {
                return;
            }

            bool targetActive = !SelectedUser.IsActive;
            string actionNameVi = targetActive ? "kích hoạt lại" : "vô hiệu hóa";
            string actionNameEn = targetActive ? "activate" : "deactivate";

            string confirmMsg = LocalizationService.IsVietnamese
                ? $"Bạn có chắc chắn muốn {actionNameVi} tài khoản '{SelectedUser.Username}' (UserId={SelectedUser.UserId})?"
                : $"Are you sure you want to {actionNameEn} account '{SelectedUser.Username}' (UserId={SelectedUser.UserId})?";

            string confirmTitle = LocalizationService.IsVietnamese
                ? "Xác nhận Trạng thái Tài khoản"
                : "Confirm Account Status Change";

            if (MessageBox.Show(confirmMsg, confirmTitle, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                return;
            }

            ServiceOperationResult res = _libraryManagementService.SetUserActiveStatus(SelectedUser.UserId, targetActive);
            IsFeedbackSuccess = res.IsSuccess;
            FeedbackMessage = res.Message;
            _statusFeedbackCallback(res.Message);
        }

        private void ExecuteUnlockAccount()
        {
            if (SelectedUser is null)
            {
                return;
            }

            ServiceOperationResult res = _libraryManagementService.UnlockUserAccount(SelectedUser.UserId);
            IsFeedbackSuccess = res.IsSuccess;
            FeedbackMessage = res.Message;
            _statusFeedbackCallback(res.Message);
        }

        private void ExecuteResetPassword()
        {
            if (SelectedUser is null)
            {
                return;
            }

            ServiceOperationResult res = _libraryManagementService.ResetUserPasswordByAdmin(
                SelectedUser.UserId,
                ResetPasswordInput,
                ResetConfirmPasswordInput);

            IsFeedbackSuccess = res.IsSuccess;
            FeedbackMessage = res.Message;
            _statusFeedbackCallback(res.Message);

            if (res.IsSuccess)
            {
                ResetPasswordInput = string.Empty;
                ResetConfirmPasswordInput = string.Empty;
            }
        }

        private void ExecuteCreateUserAccount()
        {
            ServiceOperationResult res = _libraryManagementService.CreateManagedUserAccount(new CreateManagedUserRequest
            {
                Username = NewUsername,
                FullName = NewFullName,
                Email = NewEmail,
                PhoneNumber = NewPhone,
                Role = NewRole,
                Password = NewPassword,
                ConfirmPassword = NewConfirmPassword
            });

            IsFeedbackSuccess = res.IsSuccess;
            FeedbackMessage = res.Message;
            _statusFeedbackCallback(res.Message);

            if (res.IsSuccess)
            {
                NewUsername = string.Empty;
                NewFullName = string.Empty;
                NewEmail = string.Empty;
                NewPhone = string.Empty;
                NewPassword = string.Empty;
                NewConfirmPassword = string.Empty;
            }
        }

        private void InitializeRoleFilters()
        {
            _isSuppressingReload = true;
            string prevKey = _selectedRoleFilter?.Key ?? "ALL";

            RoleFilterOptions.Clear();
            if (LocalizationService.IsVietnamese)
            {
                RoleFilterOptions.Add(new FilterOptionItem { Key = "ALL", DisplayText = "Tất cả vai trò" });
                RoleFilterOptions.Add(new FilterOptionItem { Key = "Reader", DisplayText = "Độc giả (Reader)" });
                RoleFilterOptions.Add(new FilterOptionItem { Key = "Librarian", DisplayText = "Thủ thư (Librarian)" });
                RoleFilterOptions.Add(new FilterOptionItem { Key = "Administrator", DisplayText = "Quản trị viên (Administrator)" });
            }
            else
            {
                RoleFilterOptions.Add(new FilterOptionItem { Key = "ALL", DisplayText = "All Roles" });
                RoleFilterOptions.Add(new FilterOptionItem { Key = "Reader", DisplayText = "Reader" });
                RoleFilterOptions.Add(new FilterOptionItem { Key = "Librarian", DisplayText = "Librarian" });
                RoleFilterOptions.Add(new FilterOptionItem { Key = "Administrator", DisplayText = "Administrator" });
            }

            _selectedRoleFilter = RoleFilterOptions.FirstOrDefault(r => r.Key == prevKey) ?? RoleFilterOptions.FirstOrDefault();
            OnPropertyChanged(nameof(SelectedRoleFilter));
            _isSuppressingReload = false;
        }

        private void ReloadAccountsPreservingSelection()
        {
            int? prevUserId = SelectedUser?.UserId;
            LoadUsersFromDatabase(prevUserId);
        }

        private void LoadUsersFromDatabase(int? preserveUserId = null)
        {
            string roleKey = _selectedRoleFilter?.Key ?? "ALL";
            IReadOnlyList<ManagedUserAccountModel> list = _libraryManagementService.GetManagedUsers(
                SearchKeyword,
                roleKey,
                out string? error);

            if (!string.IsNullOrWhiteSpace(error))
            {
                IsFeedbackSuccess = false;
                FeedbackMessage = error;
            }

            ManagedUsers.Clear();
            foreach (ManagedUserAccountModel item in list)
            {
                ManagedUsers.Add(item);
            }

            if (preserveUserId.HasValue)
            {
                SelectedUser = ManagedUsers.FirstOrDefault(u => u.UserId == preserveUserId.Value) ?? ManagedUsers.FirstOrDefault();
            }
            else
            {
                SelectedUser = ManagedUsers.FirstOrDefault();
            }

            OnPropertyChanged(nameof(AccountsSummaryText));
            OnPropertyChanged(nameof(ToggleActiveButtonLabel));
        }
    }
}

