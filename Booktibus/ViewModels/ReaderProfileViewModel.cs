using System.Windows.Input;
using Booktibus.Commands;
using Booktibus.Models;
using Booktibus.Services;

namespace Booktibus.ViewModels
{
    /// <summary>
    /// ViewModel for the Reader Personal Profile page (Stage 5.5).
    /// Displays read-only identity/security fields (UserId, Username, Role, ReaderCardId, MembershipStatus)
    /// and allows editing permitted personal fields (FullName, Email, PhoneNumber).
    /// </summary>
    public class ReaderProfileViewModel : PageViewModelBase
    {
        private readonly ReaderWorkspaceService _readerService;
        private readonly Action<string> _statusFeedbackCallback;

        // Read-only identity fields (strictly non-editable by Reader)
        private int _userId;
        private string _username = string.Empty;
        private string _role = string.Empty;
        private string _readerCardId = string.Empty;
        private string _membershipStatus = string.Empty;
        private string _registeredAtDisplay = string.Empty;

        // Editable personal fields
        private string _fullNameInput = string.Empty;
        private string _emailInput = string.Empty;
        private string _phoneNumberInput = string.Empty;

        private string _feedbackMessage = string.Empty;
        private bool _isFeedbackSuccess;
        private bool _hasFeedback;

        public ReaderProfileViewModel(
            ReaderWorkspaceService readerService,
            Action<string> statusFeedbackCallback)
            : base(
                NavigationPageKey.ReaderProfile,
                "Str.Page.ReaderProfile.Title",
                "Str.Page.ReaderProfile.Subtitle")
        {
            _readerService = readerService;
            _statusFeedbackCallback = statusFeedbackCallback;

            SaveProfileCommand = new RelayCommand(ExecuteSaveProfile);
            ReloadProfileCommand = new RelayCommand(ExecuteReloadProfile);
        }

        public int UserId
        {
            get => _userId;
            private set => SetProperty(ref _userId, value);
        }

        public string Username
        {
            get => _username;
            private set => SetProperty(ref _username, value);
        }

        public string Role
        {
            get => _role;
            private set => SetProperty(ref _role, value);
        }

        public string ReaderCardId
        {
            get => _readerCardId;
            private set => SetProperty(ref _readerCardId, value);
        }

        public string MembershipStatus
        {
            get => _membershipStatus;
            private set => SetProperty(ref _membershipStatus, value);
        }

        public string RegisteredAtDisplay
        {
            get => _registeredAtDisplay;
            private set => SetProperty(ref _registeredAtDisplay, value);
        }

        public string FullNameInput
        {
            get => _fullNameInput;
            set
            {
                if (SetProperty(ref _fullNameInput, value))
                {
                    ClearBanner();
                }
            }
        }

        public string EmailInput
        {
            get => _emailInput;
            set
            {
                if (SetProperty(ref _emailInput, value))
                {
                    ClearBanner();
                }
            }
        }

        public string PhoneNumberInput
        {
            get => _phoneNumberInput;
            set
            {
                if (SetProperty(ref _phoneNumberInput, value))
                {
                    ClearBanner();
                }
            }
        }

        public string FeedbackMessage
        {
            get => _feedbackMessage;
            private set
            {
                SetProperty(ref _feedbackMessage, value);
                HasFeedback = !string.IsNullOrWhiteSpace(value);
            }
        }

        public bool IsFeedbackSuccess
        {
            get => _isFeedbackSuccess;
            private set => SetProperty(ref _isFeedbackSuccess, value);
        }

        public bool HasFeedback
        {
            get => _hasFeedback;
            private set => SetProperty(ref _hasFeedback, value);
        }

        public ICommand SaveProfileCommand { get; }

        public ICommand ReloadProfileCommand { get; }

        public override void OnLanguageUpdated()
        {
            base.OnLanguageUpdated();
            ClearBanner();
        }

        public void OnSessionUpdated()
        {
            LoadProfileFromDatabase();
            ClearBanner();
        }

        private void ExecuteSaveProfile()
        {
            ClearBanner();

            ServiceOperationResult result = _readerService.UpdateOwnProfile(new UpdateReaderProfileRequest
            {
                FullName = FullNameInput,
                Email = EmailInput,
                PhoneNumber = PhoneNumberInput
            });

            IsFeedbackSuccess = result.IsSuccess;
            FeedbackMessage = result.Message;
            _statusFeedbackCallback(result.Message);

            if (result.IsSuccess)
            {
                LoadProfileFromDatabase();
            }
        }

        private void ExecuteReloadProfile()
        {
            LoadProfileFromDatabase();
            IsFeedbackSuccess = true;
            FeedbackMessage = LocalizationService.IsVietnamese
                ? "Đã tải lại thông tin gốc từ cơ sở dữ liệu BooktibusDB."
                : "Reloaded original profile information from BooktibusDB.";
            _statusFeedbackCallback(FeedbackMessage);
        }

        private void LoadProfileFromDatabase()
        {
            ReaderProfileModel? profile = _readerService.GetOwnProfile(out string? error);
            if (profile is null)
            {
                IsFeedbackSuccess = false;
                FeedbackMessage = error ?? string.Empty;
                return;
            }

            UserId = profile.UserId;
            Username = profile.Username;
            Role = profile.Role;
            ReaderCardId = profile.ReaderCardId;
            MembershipStatus = profile.MembershipStatus;
            RegisteredAtDisplay = profile.RegisteredAtLocal.ToString("dd/MM/yyyy HH:mm");

            _fullNameInput = profile.FullName;
            _emailInput = profile.Email;
            _phoneNumberInput = profile.PhoneNumber;

            OnPropertyChanged(nameof(FullNameInput));
            OnPropertyChanged(nameof(EmailInput));
            OnPropertyChanged(nameof(PhoneNumberInput));
        }

        private void ClearBanner()
        {
            FeedbackMessage = string.Empty;
        }
    }
}

