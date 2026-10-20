using System.Text.RegularExpressions;
using Booktibus.Helpers;
using Booktibus.Models;

namespace Booktibus.Services
{
    /// <summary>
    /// Stage 4 temporary test authentication service used prior to SQL Server database connection.
    /// Preserved intact as fallback when SQL Server BooktibusDB is not yet configured.
    /// Stores all passwords exclusively as PBKDF2-SHA256 hashes + cryptographic salts and enforces
    /// a 15-minute account lockout after 5 consecutive failed login attempts.
    /// </summary>
    public class TestAuthenticationService : IAuthenticationService
    {
        private readonly List<UserAccount> _testAccounts = new();

        public TestAuthenticationService()
        {
            SeedHashedTestAccounts();
        }

        public bool IsTemporaryTestProvider => true;

        public int MaxFailedAttemptsBeforeLockout => 5;

        public TimeSpan LockoutDuration => TimeSpan.FromMinutes(15);

        public bool RequiresInitialAdminSetup()
        {
            return !_testAccounts.Any(a => a.Role == UserRole.Administrator && a.IsActive);
        }

        public AuthenticationResult Authenticate(string username, string password)
        {
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
            {
                return AuthenticationResult.Failure(AuthenticationFailureReason.EmptyCredentials);
            }

            string normalizedUsername = username.Trim();
            UserAccount? account = _testAccounts.FirstOrDefault(
                a => string.Equals(a.Username, normalizedUsername, StringComparison.OrdinalIgnoreCase));

            // Do not reveal whether the username exists or not
            if (account is null)
            {
                return AuthenticationResult.Failure(
                    AuthenticationFailureReason.InvalidCredentials,
                    remainingAttempts: MaxFailedAttemptsBeforeLockout);
            }

            if (!account.IsActive)
            {
                return AuthenticationResult.Failure(AuthenticationFailureReason.AccountDisabled);
            }

            DateTime nowUtc = DateTime.UtcNow;

            // Check if account is currently locked out
            if (account.LockoutEndUtc.HasValue)
            {
                if (account.LockoutEndUtc.Value > nowUtc)
                {
                    TimeSpan remainingLockout = account.LockoutEndUtc.Value - nowUtc;
                    return AuthenticationResult.Failure(
                        AuthenticationFailureReason.AccountLockedOut,
                        remainingAttempts: 0,
                        remainingLockout: remainingLockout);
                }

                // Lockout period expired; reset counters
                account.LockoutEndUtc = null;
                account.FailedLoginAttempts = 0;
            }

            // Verify password against PBKDF2-SHA256 hash & salt
            bool passwordValid = PasswordHasher.VerifyPassword(password, account.PasswordHash, account.PasswordSalt);
            if (!passwordValid)
            {
                account.FailedLoginAttempts++;

                if (account.FailedLoginAttempts >= MaxFailedAttemptsBeforeLockout)
                {
                    account.LockoutEndUtc = nowUtc.Add(LockoutDuration);
                    return AuthenticationResult.Failure(
                        AuthenticationFailureReason.AccountLockedOut,
                        remainingAttempts: 0,
                        remainingLockout: LockoutDuration);
                }

                int remainingAttempts = MaxFailedAttemptsBeforeLockout - account.FailedLoginAttempts;
                return AuthenticationResult.Failure(
                    AuthenticationFailureReason.InvalidCredentials,
                    remainingAttempts: remainingAttempts);
            }

            // Successful login resets failed attempts and lockout state
            account.FailedLoginAttempts = 0;
            account.LockoutEndUtc = null;

            UserSession session = new()
            {
                UserId = account.UserId,
                Username = account.Username,
                FullName = account.FullName,
                Role = account.Role,
                ReaderCardId = account.ReaderCardId,
                SignedInAtLocal = DateTime.Now
            };

            return AuthenticationResult.Success(session);
        }

        public RegistrationResult RegisterReader(ReaderRegistrationRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);

            string fullName = request.FullName?.Trim() ?? string.Empty;
            string username = request.Username?.Trim() ?? string.Empty;
            string email = request.Email?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(fullName) ||
                string.IsNullOrWhiteSpace(username) ||
                string.IsNullOrWhiteSpace(email) ||
                string.IsNullOrEmpty(request.Password))
            {
                return RegistrationResult.Failure(RegistrationFailureReason.MissingRequiredFields);
            }

            if (username.Length < 4 || !Regex.IsMatch(username, @"^[a-zA-Z0-9._-]+$"))
            {
                return RegistrationResult.Failure(RegistrationFailureReason.InvalidUsernameFormat);
            }

            if (!Regex.IsMatch(email, @"^[^@\s]+@[^@\s]+\.[^@\s]+$"))
            {
                return RegistrationResult.Failure(RegistrationFailureReason.InvalidEmailFormat);
            }

            if (!IsStrongPassword(request.Password))
            {
                return RegistrationResult.Failure(RegistrationFailureReason.WeakPassword);
            }

            if (!string.Equals(request.Password, request.ConfirmPassword, StringComparison.Ordinal))
            {
                return RegistrationResult.Failure(RegistrationFailureReason.PasswordConfirmationMismatch);
            }

            if (_testAccounts.Any(a => string.Equals(a.Username, username, StringComparison.OrdinalIgnoreCase)))
            {
                return RegistrationResult.Failure(RegistrationFailureReason.DuplicateUsername);
            }

            (string hash, string salt) = PasswordHasher.HashPassword(request.Password);
            int nextReaderNumber = _testAccounts.Count(a => a.Role == UserRole.Reader) + 1;
            string assignedCardId = $"RD-{DateTime.Now.Year}-{nextReaderNumber:D3}";

            _testAccounts.Add(new UserAccount
            {
                UserId = _testAccounts.Max(a => a.UserId) + 1,
                Username = username,
                FullName = fullName,
                Email = email,
                Role = UserRole.Reader, // Strictly enforced as Reader
                ReaderCardId = assignedCardId,
                PasswordHash = hash,
                PasswordSalt = salt,
                IsActive = true
            });

            return RegistrationResult.Success(username, assignedCardId);
        }

        public RegistrationResult SetupInitialAdministrator(InitialAdminSetupRequest request)
        {
            return RegistrationResult.Failure(RegistrationFailureReason.AdminAlreadyInitialized);
        }

        public IReadOnlyList<TestCredentialGuideItem> GetStage4TestCredentialGuides()
        {
            if (LocalizationService.IsVietnamese)
            {
                return
                [
                    new TestCredentialGuideItem
                    {
                        RoleName = "Reader (Độc giả — Thẻ RD-2026-001)",
                        Username = "reader.test",
                        TestPassword = "Reader@Booktibus#2026",
                        ScopeDescription = "Chỉ xem được thông tin cá nhân, lịch sử mượn trả và tiền phạt của chính thẻ RD-2026-001."
                    },
                    new TestCredentialGuideItem
                    {
                        RoleName = "Librarian (Thủ thư)",
                        Username = "librarian.test",
                        TestPassword = "Librarian@Booktibus#2026",
                        ScopeDescription = "Quản lý sách, độc giả, mượn trả, phí phạt và xem báo cáo (Không vào Cài đặt hệ thống)."
                    },
                    new TestCredentialGuideItem
                    {
                        RoleName = "Administrator (Quản trị viên)",
                        Username = "admin.test",
                        TestPassword = "Admin@Booktibus#2026",
                        ScopeDescription = "Toàn quyền truy cập tất cả phân hệ và cấu hình hệ thống."
                    }
                ];
            }

            return
            [
                new TestCredentialGuideItem
                {
                    RoleName = "Reader (Card RD-2026-001)",
                    Username = "reader.test",
                    TestPassword = "Reader@Booktibus#2026",
                    ScopeDescription = "Restricted to own profile, borrowing history, and fines for card RD-2026-001."
                },
                new TestCredentialGuideItem
                {
                    RoleName = "Librarian (Library Staff)",
                    Username = "librarian.test",
                    TestPassword = "Librarian@Booktibus#2026",
                    ScopeDescription = "Manages books, readers, circulation, fines, and reports (No System Settings access)."
                },
                new TestCredentialGuideItem
                {
                    RoleName = "Administrator (System Admin)",
                    Username = "admin.test",
                    TestPassword = "Admin@Booktibus#2026",
                    ScopeDescription = "Full access to all library modules and system settings."
                }
            ];
        }

        public void ResetTestAccountLockouts()
        {
            foreach (UserAccount account in _testAccounts)
            {
                account.FailedLoginAttempts = 0;
                account.LockoutEndUtc = null;
            }
        }

        public static bool IsStrongPassword(string password)
        {
            if (string.IsNullOrEmpty(password) || password.Length < 8)
            {
                return false;
            }

            bool hasLetter = password.Any(char.IsLetter);
            bool hasDigit = password.Any(char.IsDigit);
            return hasLetter && hasDigit;
        }

        private void SeedHashedTestAccounts()
        {
            _testAccounts.Clear();

            (string readerHash, string readerSalt) = PasswordHasher.HashPassword("Reader@Booktibus#2026");
            _testAccounts.Add(new UserAccount
            {
                UserId = 101,
                Username = "reader.test",
                FullName = "Nguyễn Minh Anh (Tài khoản Thử nghiệm)",
                Email = "reader.test@booktibus.local",
                Role = UserRole.Reader,
                ReaderCardId = "RD-2026-001",
                PasswordHash = readerHash,
                PasswordSalt = readerSalt,
                IsActive = true
            });

            (string librarianHash, string librarianSalt) = PasswordHasher.HashPassword("Librarian@Booktibus#2026");
            _testAccounts.Add(new UserAccount
            {
                UserId = 201,
                Username = "librarian.test",
                FullName = "Trần Hoàng Nam (Thủ thư Thử nghiệm)",
                Email = "librarian.test@booktibus.local",
                Role = UserRole.Librarian,
                ReaderCardId = null,
                PasswordHash = librarianHash,
                PasswordSalt = librarianSalt,
                IsActive = true
            });

            (string adminHash, string adminSalt) = PasswordHasher.HashPassword("Admin@Booktibus#2026");
            _testAccounts.Add(new UserAccount
            {
                UserId = 301,
                Username = "admin.test",
                FullName = "Lê Quản Trị (Admin Thử nghiệm)",
                Email = "admin.test@booktibus.local",
                Role = UserRole.Administrator,
                ReaderCardId = null,
                PasswordHash = adminHash,
                PasswordSalt = adminSalt,
                IsActive = true
            });
        }
    }
}

