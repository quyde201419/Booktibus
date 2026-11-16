using System.IO;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Booktibus.Commands;
using Booktibus.Models;
using Booktibus.Services;
using QRCoder;

namespace Booktibus.ViewModels
{
    /// <summary>
    /// ViewModel for the Reader Personal Profile page (Stage 5.5).
    /// Displays read-only identity/security fields (UserId, Username, Role, ReaderCardId, MembershipStatus, MembershipTier)
    /// and allows editing permitted personal fields (FullName, Email, PhoneNumber),
    /// as well as upgrading to PRO and VIP membership packages with PayOS VietQR integration.
    /// </summary>
    public class ReaderProfileViewModel : PageViewModelBase
    {
        private readonly ReaderWorkspaceService _readerService;
        private readonly PayOSPaymentService _payOSService;
        private readonly Action<string> _statusFeedbackCallback;

        // Read-only identity fields (strictly non-editable by Reader)
        private int _userId;
        private string _username = string.Empty;
        private string _role = string.Empty;
        private string _readerCardId = string.Empty;
        private string _membershipStatus = string.Empty;
        private string _membershipTier = "Standard";
        private string _registeredAtDisplay = string.Empty;

        // Editable personal fields
        private string _fullNameInput = string.Empty;
        private string _emailInput = string.Empty;
        private string _phoneNumberInput = string.Empty;

        private string _feedbackMessage = string.Empty;
        private bool _isFeedbackSuccess;
        private bool _hasFeedback;

        // Membership Upgrade Dialog States
        private bool _isUpgradeModalOpen;
        private string _selectedUpgradeTier = string.Empty;
        private string _upgradeTierTitle = string.Empty;
        private string _upgradePriceDisplay = string.Empty;
        private decimal _upgradeAmount;
        private string _upgradeNotes = string.Empty;
        private ImageSource? _upgradeQrImageSource;

        public ReaderProfileViewModel(
            ReaderWorkspaceService readerService,
            Action<string> statusFeedbackCallback)
            : base(
                NavigationPageKey.ReaderProfile,
                "Str.Page.ReaderProfile.Title",
                "Str.Page.ReaderProfile.Subtitle")
        {
            _readerService = readerService;
            _payOSService = new PayOSPaymentService();
            _payOSService.MembershipPaymentSucceeded += OnMembershipPaymentSucceeded;
            _statusFeedbackCallback = statusFeedbackCallback;

            SaveProfileCommand = new RelayCommand(ExecuteSaveProfile);
            ReloadProfileCommand = new RelayCommand(ExecuteReloadProfile);

            UpgradeToProCommand = new RelayCommand(() => OpenUpgradeModal("PRO"));
            UpgradeToVipCommand = new RelayCommand(() => OpenUpgradeModal("VIP"));
            ConfirmUpgradeCommand = new RelayCommand(ExecuteConfirmUpgrade);
            CloseUpgradeModalCommand = new RelayCommand(ExecuteCloseUpgradeModal);
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
            private set
            {
                if (SetProperty(ref _role, value))
                {
                    OnPropertyChanged(nameof(IsReader));
                }
            }
        }

        public bool IsReader => string.Equals(Role, "Reader", StringComparison.OrdinalIgnoreCase);

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

        public string MembershipTier
        {
            get => _membershipTier;
            private set
            {
                if (SetProperty(ref _membershipTier, value))
                {
                    OnPropertyChanged(nameof(TierDisplayName));
                    OnPropertyChanged(nameof(IsStandard));
                    OnPropertyChanged(nameof(IsPro));
                    OnPropertyChanged(nameof(IsVip));
                    OnPropertyChanged(nameof(StandardButtonText));
                    OnPropertyChanged(nameof(ProButtonText));
                    OnPropertyChanged(nameof(CanUpgradePro));
                    OnPropertyChanged(nameof(VipButtonText));
                    OnPropertyChanged(nameof(CanUpgradeVip));
                }
            }
        }

        public string TierDisplayName => MembershipTier switch
        {
            "PRO" => "THÀNH VIÊN PRO ★",
            "VIP" => "THÀNH VIÊN VIP 👑",
            _ => "THÀNH VIÊN TIÊU CHUẨN (STANDARD)"
        };

        public bool IsStandard => string.Equals(MembershipTier, "Standard", StringComparison.OrdinalIgnoreCase);
        public bool IsPro => string.Equals(MembershipTier, "PRO", StringComparison.OrdinalIgnoreCase);
        public bool IsVip => string.Equals(MembershipTier, "VIP", StringComparison.OrdinalIgnoreCase);

        public string StandardButtonText => IsStandard ? "✓ Đang sử dụng (Mặc định)" : "Gói cơ bản";
        public bool CanUpgradeStandard => false;

        public string ProButtonText => IsPro
            ? "✓ ĐÃ KÍCH HOẠT"
            : (IsVip ? "Đã có gói cao hơn (VIP)" : "Nâng cấp gói PRO");
        public bool CanUpgradePro => !IsPro && !IsVip;

        public string VipButtonText => IsVip
            ? "👑 ĐÃ KÍCH HOẠT"
            : (IsPro ? "Nâng cấp lên VIP" : "Nâng cấp gói VIP");
        public bool CanUpgradeVip => !IsVip;

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
                if (SetProperty(ref _feedbackMessage, value))
                {
                    HasFeedback = !string.IsNullOrWhiteSpace(value);
                }
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

        // Upgrade Modal Properties
        public bool IsUpgradeModalOpen
        {
            get => _isUpgradeModalOpen;
            set => SetProperty(ref _isUpgradeModalOpen, value);
        }

        public string SelectedUpgradeTier
        {
            get => _selectedUpgradeTier;
            set => SetProperty(ref _selectedUpgradeTier, value);
        }

        public string UpgradeTierTitle
        {
            get => _upgradeTierTitle;
            set => SetProperty(ref _upgradeTierTitle, value);
        }

        public string UpgradePriceDisplay
        {
            get => _upgradePriceDisplay;
            set => SetProperty(ref _upgradePriceDisplay, value);
        }

        public string UpgradeNotes
        {
            get => _upgradeNotes;
            set => SetProperty(ref _upgradeNotes, value);
        }

        public ImageSource? UpgradeQrImageSource
        {
            get => _upgradeQrImageSource;
            set => SetProperty(ref _upgradeQrImageSource, value);
        }

        public ICommand SaveProfileCommand { get; }

        public ICommand ReloadProfileCommand { get; }

        public ICommand UpgradeToProCommand { get; }

        public ICommand UpgradeToVipCommand { get; }

        public ICommand ConfirmUpgradeCommand { get; }

        public ICommand CloseUpgradeModalCommand { get; }

        public override void OnLanguageUpdated()
        {
            base.OnLanguageUpdated();
            OnPropertyChanged(nameof(TierDisplayName));
            LoadProfileFromDatabase();
        }

        public void OnSessionUpdated()
        {
            ClearBanner();
            LoadProfileFromDatabase();
        }

        private async void OpenUpgradeModal(string tier)
        {
            SelectedUpgradeTier = tier;
            if (tier == "PRO")
            {
                UpgradeTierTitle = "NÂNG CẤP GÓI THÀNH VIÊN PRO ★";
                _upgradeAmount = 50000m;
                UpgradePriceDisplay = "50,000 VNĐ / tháng";
                UpgradeNotes = "Quyền lợi: Mượn tối đa 10 cuốn sách • Hạn mượn 30 ngày • Gia hạn 4 lần • Ưu tiên giữ sách đặt trước.";
            }
            else
            {
                UpgradeTierTitle = "NÂNG CẤP GÓI THÀNH VIÊN VIP 👑";
                _upgradeAmount = 100000m;
                UpgradePriceDisplay = "100,000 VNĐ / tháng";
                UpgradeNotes = "Quyền lợi: Mượn tối đa 25 cuốn sách • Hạn mượn 60 ngày • Gia hạn không giới hạn • Giảm 50% tiền phạt • VIP Lounge.";
            }

            // Display card image immediately
            UpgradeQrImageSource = LoadUserVietQr();
            IsUpgradeModalOpen = true;

            // Connect to PayOS API for automatic payment detection
            var payResult = await _payOSService.CreateMembershipPaymentLinkAsync(tier, (int)_upgradeAmount, UserId);
            if (payResult != null && !string.IsNullOrWhiteSpace(payResult.QrCode))
            {
                BitmapImage? dynamicBmp = GenerateQrImage(payResult.QrCode);
                if (dynamicBmp != null)
                {
                    UpgradeQrImageSource = dynamicBmp;
                }
            }
        }

        private void OnMembershipPaymentSucceeded(object? sender, string tier)
        {
            ServiceOperationResult result = _readerService.UpgradeMembership(tier, "PayOS VietQR", _upgradeAmount);
            IsUpgradeModalOpen = false;
            IsFeedbackSuccess = true;
            FeedbackMessage = $"[Kích hoạt tự động thành công] Chúc mừng bạn đã nâng cấp thành công lên gói {tier}!";
            _statusFeedbackCallback(FeedbackMessage);
            LoadProfileFromDatabase();
        }

        private void ExecuteCloseUpgradeModal()
        {
            _payOSService.StopPolling();
            IsUpgradeModalOpen = false;
        }

        private static BitmapImage? LoadUserVietQr()
        {
            try
            {
                BitmapImage bmp = new();
                bmp.BeginInit();
                bmp.UriSource = new Uri("pack://application:,,,/Assets/my_vietqr.jpg", UriKind.Absolute);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch
            {
                return null;
            }
        }

        private void ExecuteConfirmUpgrade()
        {
            if (string.IsNullOrWhiteSpace(SelectedUpgradeTier)) return;

            ServiceOperationResult result = _readerService.UpgradeMembership(
                SelectedUpgradeTier,
                "PayOS VietQR",
                _upgradeAmount);

            if (result.IsSuccess)
            {
                IsUpgradeModalOpen = false;
                IsFeedbackSuccess = true;
                FeedbackMessage = result.Message;
                _statusFeedbackCallback(result.Message);
                LoadProfileFromDatabase();
            }
            else
            {
                IsFeedbackSuccess = false;
                FeedbackMessage = result.Message;
            }
        }

        private static BitmapImage? GenerateQrImage(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            try
            {
                using QRCodeGenerator qrGenerator = new();
                using QRCodeData qrCodeData = qrGenerator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
                using PngByteQRCode qrCode = new(qrCodeData);
                byte[] qrCodeBytes = qrCode.GetGraphic(20);

                BitmapImage bmp = new();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = new MemoryStream(qrCodeBytes);
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch
            {
                return null;
            }
        }

        private void ExecuteSaveProfile()
        {
            UpdateReaderProfileRequest request = new()
            {
                FullName = FullNameInput,
                Email = EmailInput,
                PhoneNumber = PhoneNumberInput
            };

            ServiceOperationResult result = _readerService.UpdateOwnProfile(request);
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
            MembershipTier = profile.MembershipTier;
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
