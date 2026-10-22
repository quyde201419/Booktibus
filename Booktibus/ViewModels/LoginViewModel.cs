using System.Collections.ObjectModel;
using System.Windows.Input;
using Booktibus.Commands;
using Booktibus.Data;
using Booktibus.Models;
using Booktibus.Services;

namespace Booktibus.ViewModels
{
    public enum AuthScreenMode
    {
        SignIn = 0,
        RegisterReader = 1,
        InitialAdminSetup = 2,
        ForgotPassword = 3
    }

    /// <summary>
    /// ViewModel for the Authentication screen (Sign In, Reader Self-Registration,
    /// One-Time Initial Administrator Bootstrap, Forgot Password OTP Recovery,
    /// and SQL Server BooktibusDB Verification).
    /// Automatically switches from TestAuthenticationService to SqlAuthenticationService
    /// only after BooktibusDB connectivity and schema are verified.
    /// </summary>
    public class LoginViewModel : BaseViewModel
    {
        private readonly TestAuthenticationService _fallbackTestService;
        private readonly SqlAuthenticationService _sqlAuthService;
        private readonly SessionService _sessionService;
        private readonly IEmailDeliveryService _emailDeliveryService;
        private readonly PasswordResetService _passwordResetService;
        private IAuthenticationService _activeAuthService;

        private AuthScreenMode _currentMode = AuthScreenMode.SignIn;
        private string _serverNameInput = DatabaseConfig.ConfiguredServerName;
        private string _databaseStatusMessage = string.Empty;
        private bool _isSqlDatabaseActive;
        private bool _canShowInitialAdminTab;

        // Sign-In fields
        private string _username = string.Empty;
        private string _password = string.Empty;

        // Registration / Initial Admin fields
        private string _regFullName = string.Empty;
        private string _regUsername = string.Empty;
        private string _regEmail = string.Empty;
        private string _regPhone = string.Empty;
        private string _regPassword = string.Empty;
        private string _regConfirmPassword = string.Empty;

        // Forgot Password fields
        private ForgotPasswordStep _forgotStep = ForgotPasswordStep.EnterUsername;
        private string _forgotUsername = string.Empty;
        private string _forgotOtpCode = string.Empty;
        private string _forgotNewPassword = string.Empty;
        private string _forgotConfirmPassword = string.Empty;
        private bool _isSimulatedInboxExpanded;

        private string _errorMessage = string.Empty;
        private string _infoMessage = string.Empty;
        private bool _hasError;
        private bool _hasInfo;

        public LoginViewModel(
            TestAuthenticationService fallbackTestService,
            SqlAuthenticationService sqlAuthService,
            SessionService sessionService,
            IEmailDeliveryService? emailDeliveryService = null,
            PasswordResetService? passwordResetService = null)
        {
            ArgumentNullException.ThrowIfNull(fallbackTestService);
            ArgumentNullException.ThrowIfNull(sqlAuthService);
            ArgumentNullException.ThrowIfNull(sessionService);

            _fallbackTestService = fallbackTestService;
            _sqlAuthService = sqlAuthService;
            _sessionService = sessionService;
            _emailDeliveryService = emailDeliveryService ?? new EmailDeliveryService();
            _passwordResetService = passwordResetService ?? new PasswordResetService(_sessionService, _emailDeliveryService);
            _activeAuthService = _fallbackTestService;

            TestCredentialGuides = new ObservableCollection<TestCredentialGuideItem>();

            SwitchModeCommand = new RelayCommand(ExecuteSwitchMode);
            ProbeDatabaseCommand = new RelayCommand(ExecuteProbeDatabase);
            LoginCommand = new RelayCommand(ExecuteLogin);
            RegisterReaderCommand = new RelayCommand(ExecuteRegisterReader);
            SetupInitialAdminCommand = new RelayCommand(ExecuteSetupInitialAdmin);
            ClearFormCommand = new RelayCommand(ExecuteClearForm);
            ToggleLanguageCommand = new RelayCommand(() => LocalizationService.ToggleLanguage());
            FillTestCredentialCommand = new RelayCommand(ExecuteFillTestCredential);
            ResetTestLockoutCommand = new RelayCommand(ExecuteResetTestLockout);

            // Forgot Password Commands
            OpenForgotPasswordCommand = new RelayCommand(ExecuteOpenForgotPassword);
            RequestPasswordResetOtpCommand = new RelayCommand(ExecuteRequestPasswordResetOtp);
            VerifyResetOtpCommand = new RelayCommand(ExecuteVerifyResetOtp);
            ConfirmPasswordResetCommand = new RelayCommand(ExecuteConfirmPasswordReset);
            ChangeForgotUsernameCommand = new RelayCommand(ExecuteChangeForgotUsername);
            ToggleSimulatedInboxCommand = new RelayCommand(ExecuteToggleSimulatedInbox);

            ProbeAndSelectAuthenticationProvider();
        }

        public ObservableCollection<TestCredentialGuideItem> TestCredentialGuides { get; }

        public bool IsTemporaryTestProvider => _activeAuthService.IsTemporaryTestProvider;

        public bool IsSqlDatabaseActive
        {
            get => _isSqlDatabaseActive;
            private set
            {
                if (SetProperty(ref _isSqlDatabaseActive, value))
                {
                    OnPropertyChanged(nameof(IsTemporaryTestProvider));
                }
            }
        }

        public bool CanShowInitialAdminTab
        {
            get => _canShowInitialAdminTab;
            private set => SetProperty(ref _canShowInitialAdminTab, value);
        }

        public string ServerNameInput
        {
            get => _serverNameInput;
            set => SetProperty(ref _serverNameInput, value);
        }

        public string DatabaseStatusMessage
        {
            get => _databaseStatusMessage;
            private set => SetProperty(ref _databaseStatusMessage, value);
        }

        public AuthScreenMode CurrentMode
        {
            get => _currentMode;
            private set
            {
                if (SetProperty(ref _currentMode, value))
                {
                    OnPropertyChanged(nameof(IsSignInMode));
                    OnPropertyChanged(nameof(IsRegisterReaderMode));
                    OnPropertyChanged(nameof(IsInitialAdminMode));
                    OnPropertyChanged(nameof(IsForgotPasswordMode));
                }
            }
        }

        public bool IsSignInMode => CurrentMode == AuthScreenMode.SignIn;

        public bool IsRegisterReaderMode => CurrentMode == AuthScreenMode.RegisterReader;

        public bool IsInitialAdminMode => CurrentMode == AuthScreenMode.InitialAdminSetup;

        public bool IsForgotPasswordMode => CurrentMode == AuthScreenMode.ForgotPassword;

        public ForgotPasswordStep ForgotStep
        {
            get => _forgotStep;
            private set
            {
                if (SetProperty(ref _forgotStep, value))
                {
                    OnPropertyChanged(nameof(IsForgotStep1EnterUsername));
                    OnPropertyChanged(nameof(IsForgotStep2VerifyOtp));
                    OnPropertyChanged(nameof(IsForgotStep3SetPassword));
                }
            }
        }

        public bool IsForgotStep1EnterUsername => ForgotStep == ForgotPasswordStep.EnterUsername;

        public bool IsForgotStep2VerifyOtp => ForgotStep == ForgotPasswordStep.VerifyOtp;

        public bool IsForgotStep3SetPassword => ForgotStep == ForgotPasswordStep.SetNewPassword;

        public bool IsEmailSimulationMode => _emailDeliveryService.IsSimulationMode;

        public string EmailDeliveryModeNotice => _emailDeliveryService.GetDeliveryModeSummary();

        public bool IsSimulatedInboxExpanded
        {
            get => _isSimulatedInboxExpanded;
            set => SetProperty(ref _isSimulatedInboxExpanded, value);
        }

        public string SimulatedEmailPreviewText
        {
            get
            {
                EmailMessageModel? msg = _emailDeliveryService.GetLatestSimulatedEmail();
                if (msg is null)
                {
                    return LocalizationService.IsVietnamese
                        ? "Chưa có email giả lập nào được phát sinh trong phiên kiểm thử hiện tại."
                        : "No simulated email has been generated in the current test session.";
                }

                return $"[{msg.CreatedAtLocal:HH:mm:ss}] To: {msg.RecipientEmail} | Account: {msg.TargetUsername}\n" +
                       $"Subject: {msg.Subject}\n" +
                       $"------------------------------------------------------------\n" +
                       $"{msg.BodyText}";
            }
        }

        // Sign-In Properties
        public string Username
        {
            get => _username;
            set
            {
                if (SetProperty(ref _username, value))
                {
                    ClearFeedback();
                }
            }
        }

        public string Password
        {
            get => _password;
            set
            {
                if (SetProperty(ref _password, value))
                {
                    ClearFeedback();
                }
            }
        }

        // Registration & Initial Admin Properties
        public string RegFullName
        {
            get => _regFullName;
            set
            {
                if (SetProperty(ref _regFullName, value))
                {
                    ClearFeedback();
                }
            }
        }

        public string RegUsername
        {
            get => _regUsername;
            set
            {
                if (SetProperty(ref _regUsername, value))
                {
                    ClearFeedback();
                }
            }
        }

        public string RegEmail
        {
            get => _regEmail;
            set
            {
                if (SetProperty(ref _regEmail, value))
                {
                    ClearFeedback();
                }
            }
        }

        public string RegPhone
        {
            get => _regPhone;
            set
            {
                if (SetProperty(ref _regPhone, value))
                {
                    ClearFeedback();
                }
            }
        }

        public string RegPassword
        {
            get => _regPassword;
            set
            {
                if (SetProperty(ref _regPassword, value))
                {
                    ClearFeedback();
                }
            }
        }

        public string RegConfirmPassword
        {
            get => _regConfirmPassword;
            set
            {
                if (SetProperty(ref _regConfirmPassword, value))
                {
                    ClearFeedback();
                }
            }
        }

        // Forgot Password Properties
        public string ForgotUsername
        {
            get => _forgotUsername;
            set
            {
                if (SetProperty(ref _forgotUsername, value))
                {
                    ClearFeedback();
                }
            }
        }

        public string ForgotOtpCode
        {
            get => _forgotOtpCode;
            set
            {
                if (SetProperty(ref _forgotOtpCode, value))
                {
                    ClearFeedback();
                }
            }
        }

        public string ForgotNewPassword
        {
            get => _forgotNewPassword;
            set
            {
                if (SetProperty(ref _forgotNewPassword, value))
                {
                    ClearFeedback();
                }
            }
        }

        public string ForgotConfirmPassword
        {
            get => _forgotConfirmPassword;
            set
            {
                if (SetProperty(ref _forgotConfirmPassword, value))
                {
                    ClearFeedback();
                }
            }
        }

        public string ErrorMessage
        {
            get => _errorMessage;
            private set
            {
                SetProperty(ref _errorMessage, value);
                HasError = !string.IsNullOrWhiteSpace(value);
            }
        }

        public bool HasError
        {
            get => _hasError;
            private set => SetProperty(ref _hasError, value);
        }

        public string InfoMessage
        {
            get => _infoMessage;
            private set
            {
                SetProperty(ref _infoMessage, value);
                HasInfo = !string.IsNullOrWhiteSpace(value);
            }
        }

        public bool HasInfo
        {
            get => _hasInfo;
            private set => SetProperty(ref _hasInfo, value);
        }

        public ICommand SwitchModeCommand { get; }

        public ICommand ProbeDatabaseCommand { get; }

        public ICommand LoginCommand { get; }

        public ICommand RegisterReaderCommand { get; }

        public ICommand SetupInitialAdminCommand { get; }

        public ICommand ClearFormCommand { get; }

        public ICommand ToggleLanguageCommand { get; }

        public ICommand FillTestCredentialCommand { get; }

        public ICommand ResetTestLockoutCommand { get; }

        public ICommand OpenForgotPasswordCommand { get; }

        public ICommand RequestPasswordResetOtpCommand { get; }

        public ICommand VerifyResetOtpCommand { get; }

        public ICommand ConfirmPasswordResetCommand { get; }

        public ICommand ChangeForgotUsernameCommand { get; }

        public ICommand ToggleSimulatedInboxCommand { get; }

        public void OnLanguageUpdated()
        {
            ProbeAndSelectAuthenticationProvider();
            OnPropertyChanged(nameof(EmailDeliveryModeNotice));
            OnPropertyChanged(nameof(SimulatedEmailPreviewText));
            ClearFeedback();
        }

        public void ResetForLogout(string logoutNotice)
        {
            Password = string.Empty;
            ClearRegistrationFields();
            ClearForgotPasswordFields();
            CurrentMode = AuthScreenMode.SignIn;
            ErrorMessage = string.Empty;
            InfoMessage = logoutNotice;
        }

        public void ProbeAndSelectAuthenticationProvider()
        {
            DatabaseConfig.ConfiguredServerName = ServerNameInput;
            DatabaseProbeResult probe = DatabaseConfig.ProbeDatabase();

            DatabaseStatusMessage = probe.StatusSummary;

            if (probe.CanUseSqlAuthentication)
            {
                _activeAuthService = _sqlAuthService;
                IsSqlDatabaseActive = true;
                CanShowInitialAdminTab = !probe.HasAdministratorAccount;

                if (CurrentMode == AuthScreenMode.InitialAdminSetup && !CanShowInitialAdminTab)
                {
                    CurrentMode = AuthScreenMode.SignIn;
                }
            }
            else
            {
                _activeAuthService = _fallbackTestService;
                IsSqlDatabaseActive = false;
                CanShowInitialAdminTab = false;

                if (CurrentMode == AuthScreenMode.InitialAdminSetup)
                {
                    CurrentMode = AuthScreenMode.SignIn;
                }
            }

            RefreshTestCredentialGuides();
        }

        private void ExecuteProbeDatabase()
        {
            ClearFeedback();
            ProbeAndSelectAuthenticationProvider();

            if (IsSqlDatabaseActive)
            {
                InfoMessage = LocalizationService.IsVietnamese
                    ? $"Đã kết nối và chuyển sang SqlAuthenticationService (BooktibusDB trên '{DatabaseConfig.ConfiguredServerName}')."
                    : $"Verified and switched to SqlAuthenticationService (BooktibusDB on '{DatabaseConfig.ConfiguredServerName}').";
            }
            else
            {
                ErrorMessage = DatabaseStatusMessage;
            }
        }

        private void ExecuteSwitchMode(object? parameter)
        {
            ClearFeedback();
            if (parameter is string modeStr && Enum.TryParse(modeStr, out AuthScreenMode targetMode))
            {
                if (targetMode == AuthScreenMode.InitialAdminSetup && !CanShowInitialAdminTab)
                {
                    return;
                }

                if (targetMode == AuthScreenMode.ForgotPassword)
                {
                    ExecuteOpenForgotPassword();
                    return;
                }

                CurrentMode = targetMode;
            }
        }

        private void ExecuteOpenForgotPassword()
        {
            ClearFeedback();
            string candidateUser = Username?.Trim() ?? string.Empty;
            ClearForgotPasswordFields();
            if (!string.IsNullOrWhiteSpace(candidateUser))
            {
                ForgotUsername = candidateUser;
            }
            CurrentMode = AuthScreenMode.ForgotPassword;
            OnPropertyChanged(nameof(IsEmailSimulationMode));
            OnPropertyChanged(nameof(EmailDeliveryModeNotice));
            OnPropertyChanged(nameof(SimulatedEmailPreviewText));
        }

        private void ExecuteRequestPasswordResetOtp()
        {
            ClearFeedback();
            _emailDeliveryService.ClearSimulatedInbox();
            OnPropertyChanged(nameof(SimulatedEmailPreviewText));

            OtpRequestResult result = _passwordResetService.RequestPasswordResetOtp(ForgotUsername);
            OnPropertyChanged(nameof(SimulatedEmailPreviewText));

            if (result.IsAcceptedForProcessing)
            {
                ForgotOtpCode = string.Empty;
                ForgotNewPassword = string.Empty;
                ForgotConfirmPassword = string.Empty;
                ForgotStep = ForgotPasswordStep.VerifyOtp;

                if (IsEmailSimulationMode)
                {
                    IsSimulatedInboxExpanded = true;
                    OnPropertyChanged(nameof(IsSimulatedInboxExpanded));
                    InfoMessage = LocalizationService.IsVietnamese
                        ? "Hệ thống đang ở Chế độ Giả lập Email (môi trường Dev, chưa cấu hình SMTP thật). Mã OTP 6 chữ số đã được tạo và hiển thị ngay tại khung 'Hộp thư Email Giả lập' màu vàng bên dưới! Hãy sao chép mã OTP để xác thực."
                        : "System is in Simulated Email Mode (Dev environment, live SMTP not configured). Your 6-digit OTP code has been generated and displayed in the 'Simulated Email Sandbox' box below! Please copy the OTP code to verify.";
                }
                else
                {
                    // Anti-enumeration message: identical whether the username exists or not
                    InfoMessage = LocalizationService.IsVietnamese
                        ? "Nếu tài khoản tồn tại và có địa chỉ email hợp lệ, hệ thống đã gửi mã xác thực OTP (6 chữ số, hiệu lực 5 phút) tới email đăng ký của tài khoản đó. Vui lòng nhập mã OTP bên dưới."
                        : "If the account exists and has a valid registered email address, a 6-digit OTP code (valid for 5 minutes) has been sent to its registered email. Please enter the OTP code below.";
                }
                return;
            }

            SetPasswordResetErrorMessage(result.FailureReason, result.RetryAfterSeconds, 0, result.TechnicalDetails);
        }

        private void ExecuteVerifyResetOtp()
        {
            ClearFeedback();

            OtpVerificationResult result = _passwordResetService.VerifyPasswordResetOtp(ForgotUsername, ForgotOtpCode);
            if (result.IsValid)
            {
                ForgotNewPassword = string.Empty;
                ForgotConfirmPassword = string.Empty;
                ForgotStep = ForgotPasswordStep.SetNewPassword;

                InfoMessage = LocalizationService.IsVietnamese
                    ? "Mã xác thực OTP hợp lệ! Bây giờ hãy nhập mật khẩu mới và xác nhận mật khẩu mới bên dưới."
                    : "OTP code verified successfully! Please enter and confirm your new password below.";
                return;
            }

            SetPasswordResetErrorMessage(result.FailureReason, 0, result.RemainingAttempts, result.TechnicalDetails);
        }

        private void ExecuteConfirmPasswordReset()
        {
            ClearFeedback();

            string targetUser = ForgotUsername.Trim();
            PasswordResetResult result = _passwordResetService.CompletePasswordReset(
                targetUser,
                ForgotOtpCode,
                ForgotNewPassword,
                ForgotConfirmPassword);

            if (result.IsSuccess)
            {
                _emailDeliveryService.ClearSimulatedInbox();
                ClearForgotPasswordFields();
                CurrentMode = AuthScreenMode.SignIn;
                Username = targetUser;
                Password = string.Empty;

                InfoMessage = LocalizationService.IsVietnamese
                    ? $"[Đặt lại mật khẩu thành công] Mật khẩu mới của tài khoản '{targetUser}' đã được băm PBKDF2-SHA256 và cập nhật vào SQL Server. Mã OTP đã được vô hiệu hóa. Hãy đăng nhập bằng mật khẩu mới."
                    : $"[Password Reset Successful] The new password for '{targetUser}' has been hashed with PBKDF2-SHA256 and saved to SQL Server. The OTP has been invalidated. Please sign in with your new password.";
                return;
            }

            SetPasswordResetErrorMessage(result.FailureReason, 0, result.RemainingOtpAttempts, result.TechnicalDetails);
        }

        private void ExecuteChangeForgotUsername()
        {
            ClearFeedback();
            ForgotOtpCode = string.Empty;
            ForgotNewPassword = string.Empty;
            ForgotConfirmPassword = string.Empty;
            ForgotStep = ForgotPasswordStep.EnterUsername;
        }

        private void ExecuteToggleSimulatedInbox()
        {
            IsSimulatedInboxExpanded = !IsSimulatedInboxExpanded;
            OnPropertyChanged(nameof(SimulatedEmailPreviewText));
        }

        private void SetPasswordResetErrorMessage(
            PasswordResetFailureReason reason,
            int retryAfterSeconds,
            int remainingAttempts,
            string? technicalDetails)
        {
            switch (reason)
            {
                case PasswordResetFailureReason.EmptyUsername:
                    ErrorMessage = LocalizationService.IsVietnamese
                        ? "Vui lòng nhập Tên đăng nhập cần khôi phục mật khẩu."
                        : "Please enter the Username for password recovery.";
                    break;

                case PasswordResetFailureReason.InvalidUsernameFormat:
                    ErrorMessage = LocalizationService.IsVietnamese
                        ? "Tên đăng nhập phải từ 4–50 ký tự và chỉ chứa chữ cái, chữ số, dấu chấm (.), gạch dưới (_) hoặc gạch ngang (-)."
                        : "Username must be 4–50 characters and contain only letters, digits, '.', '_', or '-'.";
                    break;

                case PasswordResetFailureReason.EmptyOtp:
                    ErrorMessage = LocalizationService.IsVietnamese
                        ? "Vui lòng nhập mã xác thực OTP gồm đúng 6 chữ số."
                        : "Please enter a valid 6-digit numeric OTP code.";
                    break;

                case PasswordResetFailureReason.InvalidOrExpiredOtp:
                    string attemptHintVi = remainingAttempts > 0
                        ? $" (Còn {remainingAttempts} lần thử cho mã OTP này)."
                        : string.Empty;
                    string attemptHintEn = remainingAttempts > 0
                        ? $" ({remainingAttempts} attempt(s) remaining for this OTP)."
                        : string.Empty;

                    ErrorMessage = LocalizationService.IsVietnamese
                        ? $"Mã xác thực OTP không chính xác, đã hết hạn (5 phút) hoặc đã được sử dụng.{attemptHintVi}"
                        : $"The OTP code is invalid, expired (5-minute limit), or has already been used.{attemptHintEn}";
                    break;

                case PasswordResetFailureReason.OtpAttemptsExceeded:
                    ErrorMessage = LocalizationService.IsVietnamese
                        ? "Bạn đã nhập sai mã OTP quá 5 lần cho phép. Mã OTP hiện tại đã bị vô hiệu hóa để chống dò mã. Vui lòng yêu cầu gửi mã OTP mới."
                        : "Maximum 5 failed OTP verification attempts exceeded. This OTP has been permanently invalidated. Please request a new OTP code.";
                    break;

                case PasswordResetFailureReason.OtpRateLimitExceeded:
                    int waitSec = Math.Max(1, retryAfterSeconds);
                    ErrorMessage = LocalizationService.IsVietnamese
                        ? $"Bạn đã yêu cầu gửi mã OTP quá nhanh hoặc vượt quá giới hạn cho phép (tối đa 3 lần / 15 phút). Vui lòng đợi {waitSec} giây trước khi thử lại."
                        : $"Too many OTP requests (cooldown active or max 3 requests per 15 minutes reached). Please wait {waitSec} second(s) before trying again.";
                    break;

                case PasswordResetFailureReason.MissingPasswordFields:
                    ErrorMessage = LocalizationService.IsVietnamese
                        ? "Vui lòng nhập đầy đủ Mật khẩu mới và Xác nhận mật khẩu mới."
                        : "Please enter both New Password and Confirm New Password.";
                    break;

                case PasswordResetFailureReason.WeakPassword:
                    ErrorMessage = LocalizationService.IsVietnamese
                        ? "Mật khẩu mới chưa đủ mạnh: Cần tối thiểu 8 ký tự, bao gồm chữ hoa, chữ thường, chữ số và ký tự đặc biệt."
                        : "New password is too weak: Must be at least 8 characters and include uppercase, lowercase, digits, and special characters.";
                    break;

                case PasswordResetFailureReason.PasswordConfirmationMismatch:
                    ErrorMessage = LocalizationService.IsVietnamese
                        ? "Xác nhận mật khẩu mới không khớp với mật khẩu mới đã nhập."
                        : "Password confirmation does not match the new password.";
                    break;

                case PasswordResetFailureReason.EmailDeliveryFailed:
                    ErrorMessage = LocalizationService.IsVietnamese
                        ? $"Không thể gửi email chứa mã OTP: {technicalDetails}"
                        : $"Failed to send OTP email: {technicalDetails}";
                    break;

                case PasswordResetFailureReason.DatabaseError:
                default:
                    ErrorMessage = LocalizationService.IsVietnamese
                        ? $"Lỗi kết nối hoặc truy vấn cơ sở dữ liệu SQL Server: {technicalDetails}"
                        : $"SQL Server database connection or query error: {technicalDetails}";
                    break;
            }
        }

        private void ExecuteLogin()
        {
            ClearFeedback();

            AuthenticationResult result = _activeAuthService.Authenticate(Username, Password);

            if (result.IsSuccess && result.Session is not null)
            {
                Password = string.Empty;
                _sessionService.StartSession(result.Session);
                return;
            }

            switch (result.FailureReason)
            {
                case AuthenticationFailureReason.EmptyCredentials:
                    ErrorMessage = LocalizationService.IsVietnamese
                        ? "Vui lòng nhập đầy đủ Tên đăng nhập và Mật khẩu."
                        : "Please enter both Username and Password.";
                    break;

                case AuthenticationFailureReason.AccountLockedOut:
                    int minutesLeft = Math.Max(1, (int)Math.Ceiling(result.RemainingLockoutDuration?.TotalMinutes ?? 15));
                    ErrorMessage = LocalizationService.IsVietnamese
                        ? $"Tài khoản đã bị tạm khóa bảo mật do đăng nhập sai 5 lần liên tiếp. Vui lòng thử lại sau {minutesLeft} phút."
                        : $"Account is temporarily locked due to 5 consecutive failed login attempts. Please try again in {minutesLeft} minute(s).";
                    break;

                case AuthenticationFailureReason.AccountDisabled:
                    ErrorMessage = LocalizationService.IsVietnamese
                        ? "Tài khoản này hiện đang bị vô hiệu hóa. Vui lòng liên hệ Quản trị viên."
                        : "This account is currently disabled. Please contact the Administrator.";
                    break;

                case AuthenticationFailureReason.DatabaseError:
                    ErrorMessage = LocalizationService.IsVietnamese
                        ? $"Lỗi truy vấn SQL Server: {result.TechnicalDetails}"
                        : $"SQL Server query error: {result.TechnicalDetails}";
                    break;

                case AuthenticationFailureReason.InvalidCredentials:
                default:
                    ErrorMessage = LocalizationService.IsVietnamese
                        ? $"Tên đăng nhập hoặc mật khẩu không chính xác. (Còn {result.RemainingAttemptsBeforeLockout} lần thử trước khi khóa 15 phút)."
                        : $"Invalid username or password. ({result.RemainingAttemptsBeforeLockout} attempt(s) remaining before 15-minute lockout).";
                    break;
            }
        }

        private void ExecuteRegisterReader()
        {
            ClearFeedback();

            ReaderRegistrationRequest request = new()
            {
                FullName = RegFullName,
                Username = RegUsername,
                Email = RegEmail,
                PhoneNumber = RegPhone,
                Password = RegPassword,
                ConfirmPassword = RegConfirmPassword
            };

            RegistrationResult result = _activeAuthService.RegisterReader(request);
            if (result.IsSuccess)
            {
                string registeredUser = result.RegisteredUsername ?? RegUsername.Trim();
                string cardId = result.AssignedReaderCardId ?? "RD-2026";
                ClearRegistrationFields();
                CurrentMode = AuthScreenMode.SignIn;
                Username = registeredUser;

                InfoMessage = LocalizationService.IsVietnamese
                    ? $"[Đăng ký Độc giả thành công] Tài khoản '{registeredUser}' (Vai trò: Reader, Mã thẻ: {cardId}) đã được tạo và băm mật khẩu PBKDF2-SHA256. Hãy nhập mật khẩu để đăng nhập."
                    : $"[Reader Registration Successful] Account '{registeredUser}' (Role: Reader, Card ID: {cardId}) has been created with PBKDF2-SHA256 hashing. Please sign in.";
                return;
            }

            SetRegistrationErrorMessage(result);
        }

        private void ExecuteSetupInitialAdmin()
        {
            ClearFeedback();

            InitialAdminSetupRequest request = new()
            {
                FullName = RegFullName,
                Username = RegUsername,
                Email = RegEmail,
                Password = RegPassword,
                ConfirmPassword = RegConfirmPassword
            };

            RegistrationResult result = _activeAuthService.SetupInitialAdministrator(request);
            if (result.IsSuccess)
            {
                string adminUser = result.RegisteredUsername ?? RegUsername.Trim();
                ClearRegistrationFields();
                ProbeAndSelectAuthenticationProvider();
                CurrentMode = AuthScreenMode.SignIn;
                Username = adminUser;

                InfoMessage = LocalizationService.IsVietnamese
                    ? $"[Khởi tạo Quản trị viên Thành công] Tài khoản Administrator '{adminUser}' đã được lưu vào BooktibusDB. Chức năng khởi tạo Admin ban đầu đã được khóa lại."
                    : $"[Initial Administrator Created] Administrator account '{adminUser}' has been saved to BooktibusDB. Initial Admin Setup is now permanently locked.";
                return;
            }

            SetRegistrationErrorMessage(result);
        }

        private void SetRegistrationErrorMessage(RegistrationResult result)
        {
            switch (result.FailureReason)
            {
                case RegistrationFailureReason.MissingRequiredFields:
                    ErrorMessage = LocalizationService.IsVietnamese
                        ? "Vui lòng điền đầy đủ Họ tên, Tên đăng nhập, Email và Mật khẩu."
                        : "Please fill in Full Name, Username, Email, and Password.";
                    break;

                case RegistrationFailureReason.InvalidUsernameFormat:
                    ErrorMessage = LocalizationService.IsVietnamese
                        ? "Tên đăng nhập phải từ 4–50 ký tự và chỉ chứa chữ cái, chữ số, dấu chấm (.), gạch dưới (_) hoặc gạch ngang (-)."
                        : "Username must be 4–50 characters and contain only letters, digits, '.', '_', or '-'.";
                    break;

                case RegistrationFailureReason.InvalidEmailFormat:
                    ErrorMessage = LocalizationService.IsVietnamese
                        ? "Địa chỉ Email không đúng định dạng."
                        : "Please enter a valid email address.";
                    break;

                case RegistrationFailureReason.WeakPassword:
                    ErrorMessage = LocalizationService.IsVietnamese
                        ? "Mật khẩu chưa đủ mạnh: Cần tối thiểu 8 ký tự, bao gồm chữ hoa, chữ thường, chữ số và ký tự đặc biệt."
                        : "Password is too weak: Must be at least 8 characters and include uppercase, lowercase, digits, and special characters.";
                    break;

                case RegistrationFailureReason.PasswordConfirmationMismatch:
                    ErrorMessage = LocalizationService.IsVietnamese
                        ? "Mật khẩu xác nhận không khớp với mật khẩu đã nhập."
                        : "Password confirmation does not match the entered password.";
                    break;

                case RegistrationFailureReason.DuplicateUsername:
                    ErrorMessage = LocalizationService.IsVietnamese
                        ? "Tên đăng nhập này đã tồn tại trong hệ thống. Vui lòng chọn tên đăng nhập khác."
                        : "This username is already taken. Please choose a different username.";
                    break;

                case RegistrationFailureReason.AdminAlreadyInitialized:
                    ErrorMessage = LocalizationService.IsVietnamese
                        ? "Hệ thống đã có tài khoản Administrator. Không thể khởi tạo thêm qua bước thiết lập ban đầu."
                        : "An Administrator account already exists. Initial bootstrap is locked.";
                    break;

                case RegistrationFailureReason.DatabaseUnavailable:
                default:
                    ErrorMessage = LocalizationService.IsVietnamese
                        ? $"Không thể ghi dữ liệu vào SQL Server: {result.TechnicalDetails}"
                        : $"Failed to save record to SQL Server: {result.TechnicalDetails}";
                    break;
            }
        }

        private void ExecuteClearForm()
        {
            Username = string.Empty;
            Password = string.Empty;
            ClearRegistrationFields();
            ClearForgotPasswordFields();
            ClearFeedback();
        }

        private void ClearRegistrationFields()
        {
            RegFullName = string.Empty;
            RegUsername = string.Empty;
            RegEmail = string.Empty;
            RegPhone = string.Empty;
            RegPassword = string.Empty;
            RegConfirmPassword = string.Empty;
        }

        private void ClearForgotPasswordFields()
        {
            ForgotStep = ForgotPasswordStep.EnterUsername;
            ForgotUsername = string.Empty;
            ForgotOtpCode = string.Empty;
            ForgotNewPassword = string.Empty;
            ForgotConfirmPassword = string.Empty;
            IsSimulatedInboxExpanded = false;
            OnPropertyChanged(nameof(SimulatedEmailPreviewText));
        }

        private void ExecuteFillTestCredential(object? parameter)
        {
            if (parameter is TestCredentialGuideItem item)
            {
                CurrentMode = AuthScreenMode.SignIn;
                Username = item.Username;
                Password = item.TestPassword;
                ErrorMessage = string.Empty;
                InfoMessage = LocalizationService.IsVietnamese
                    ? $"Đã điền tài khoản thử nghiệm '{item.Username}'. Nhấn 'Đăng nhập vào Booktibus' để tiếp tục."
                    : $"Filled test credentials for '{item.Username}'. Click 'Sign In to Booktibus' to proceed.";
            }
        }

        private void ExecuteResetTestLockout()
        {
            _activeAuthService.ResetTestAccountLockouts();
            ErrorMessage = string.Empty;
            InfoMessage = LocalizationService.IsVietnamese
                ? "Đã đặt lại bộ đếm đăng nhập sai và mở khóa các tài khoản thử nghiệm."
                : "Reset failed login counters and unlocked all Stage 4 test accounts.";
        }

        private void ClearFeedback()
        {
            ErrorMessage = string.Empty;
            InfoMessage = string.Empty;
        }

        private void RefreshTestCredentialGuides()
        {
            TestCredentialGuides.Clear();
            foreach (TestCredentialGuideItem item in _activeAuthService.GetStage4TestCredentialGuides())
            {
                TestCredentialGuides.Add(item);
            }
            OnPropertyChanged(nameof(IsTemporaryTestProvider));
        }
    }
}

