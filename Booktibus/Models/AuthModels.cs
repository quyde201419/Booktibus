namespace Booktibus.Models
{
    /// <summary>
    /// Granular business permissions enforced at the Service/Business layer.
    /// </summary>
    public enum AppPermission
    {
        ViewDashboard = 0,
        BrowseCatalog = 1,
        ManageCatalog = 2,
        ManageReaders = 3,
        ViewOwnBorrowingAndFines = 4,
        ProcessCirculation = 5,
        ManageAllFines = 6,
        ViewReports = 7,
        ManageSystemSettings = 8,
        ManageOwnFavorites = 9,
        ManageOwnProfile = 10
    }

    /// <summary>
    /// Represents a stored user account entity with hashed credentials and lockout tracking.
    /// </summary>
    public class UserAccount
    {
        public int UserId { get; init; }

        public string Username { get; init; } = string.Empty;

        public string FullName { get; init; } = string.Empty;

        public string Email { get; init; } = string.Empty;

        public UserRole Role { get; init; }

        /// <summary>
        /// Associated Reader Card ID when Role is Reader (used to isolate personal records).
        /// </summary>
        public string? ReaderCardId { get; init; }

        public string PasswordHash { get; init; } = string.Empty;

        public string PasswordSalt { get; init; } = string.Empty;

        public bool IsActive { get; set; } = true;

        public int FailedLoginAttempts { get; set; }

        public DateTime? LockoutEndUtc { get; set; }
    }

    /// <summary>
    /// Represents an authenticated user session (never exposes password hash or salt).
    /// </summary>
    public class UserSession
    {
        public int UserId { get; init; }

        public string Username { get; init; } = string.Empty;

        public string FullName { get; init; } = string.Empty;

        public UserRole Role { get; init; }

        public string? ReaderCardId { get; init; }

        public DateTime SignedInAtLocal { get; init; } = DateTime.Now;
    }

    /// <summary>
    /// Categories of authentication outcomes for clear, localized error feedback.
    /// </summary>
    public enum AuthenticationFailureReason
    {
        None = 0,
        EmptyCredentials = 1,
        InvalidCredentials = 2,
        AccountLockedOut = 3,
        AccountDisabled = 4,
        DatabaseError = 5
    }

    /// <summary>
    /// Encapsulates the result of an authentication attempt.
    /// </summary>
    public class AuthenticationResult
    {
        public bool IsSuccess { get; init; }

        public AuthenticationFailureReason FailureReason { get; init; }

        public UserSession? Session { get; init; }

        public int RemainingAttemptsBeforeLockout { get; init; }

        public TimeSpan? RemainingLockoutDuration { get; init; }

        public string? TechnicalDetails { get; init; }

        public static AuthenticationResult Success(UserSession session) => new()
        {
            IsSuccess = true,
            FailureReason = AuthenticationFailureReason.None,
            Session = session
        };

        public static AuthenticationResult Failure(
            AuthenticationFailureReason reason,
            int remainingAttempts = 0,
            TimeSpan? remainingLockout = null,
            string? technicalDetails = null) => new()
        {
            IsSuccess = false,
            FailureReason = reason,
            RemainingAttemptsBeforeLockout = remainingAttempts,
            RemainingLockoutDuration = remainingLockout,
            TechnicalDetails = technicalDetails
        };
    }

    /// <summary>
    /// Input model for self-service Reader account registration.
    /// Note: Does NOT contain any Role property; Role is strictly enforced as UserRole.Reader in the service layer.
    /// </summary>
    public class ReaderRegistrationRequest
    {
        public string FullName { get; init; } = string.Empty;

        public string Username { get; init; } = string.Empty;

        public string Email { get; init; } = string.Empty;

        public string PhoneNumber { get; init; } = string.Empty;

        public string Password { get; init; } = string.Empty;

        public string ConfirmPassword { get; init; } = string.Empty;
    }

    /// <summary>
    /// Input model for one-time initial Administrator bootstrap when BooktibusDB has 0 Administrators.
    /// </summary>
    public class InitialAdminSetupRequest
    {
        public string FullName { get; init; } = string.Empty;

        public string Username { get; init; } = string.Empty;

        public string Email { get; init; } = string.Empty;

        public string Password { get; init; } = string.Empty;

        public string ConfirmPassword { get; init; } = string.Empty;
    }

    public enum RegistrationFailureReason
    {
        None = 0,
        MissingRequiredFields = 1,
        InvalidUsernameFormat = 2,
        InvalidEmailFormat = 3,
        WeakPassword = 4,
        PasswordConfirmationMismatch = 5,
        DuplicateUsername = 6,
        DuplicateEmail = 7,
        AdminAlreadyInitialized = 8,
        DatabaseUnavailable = 9
    }

    public class RegistrationResult
    {
        public bool IsSuccess { get; init; }

        public RegistrationFailureReason FailureReason { get; init; }

        public string? AssignedReaderCardId { get; init; }

        public string? RegisteredUsername { get; init; }

        public string? TechnicalDetails { get; init; }

        public static RegistrationResult Success(string username, string? readerCardId = null) => new()
        {
            IsSuccess = true,
            FailureReason = RegistrationFailureReason.None,
            RegisteredUsername = username,
            AssignedReaderCardId = readerCardId
        };

        public static RegistrationResult Failure(
            RegistrationFailureReason reason,
            string? technicalDetails = null) => new()
        {
            IsSuccess = false,
            FailureReason = reason,
            TechnicalDetails = technicalDetails
        };
    }

    /// <summary>
    /// Represents the live verification state of the SQL Server BooktibusDB connection and schema.
    /// </summary>
    public class DatabaseProbeResult
    {
        public bool IsServerReachable { get; init; }

        public bool IsDatabaseCreated { get; init; }

        public bool IsSchemaReady { get; init; }

        public bool HasAdministratorAccount { get; init; }

        public string ServerName { get; init; } = "localhost";

        public string DatabaseName { get; init; } = "BooktibusDB";

        public string StatusSummary { get; init; } = string.Empty;

        public IReadOnlyList<string> MissingTables { get; init; } = Array.Empty<string>();

        public bool CanUseSqlAuthentication => IsServerReachable && IsDatabaseCreated && IsSchemaReady;
    }

    /// <summary>
    /// Describes a clearly labeled Stage 4 test credential entry for UI testing before SQL Server is connected.
    /// </summary>
    public class TestCredentialGuideItem
    {
        public string RoleName { get; init; } = string.Empty;

        public string Username { get; init; } = string.Empty;

        public string TestPassword { get; init; } = string.Empty;

        public string ScopeDescription { get; init; } = string.Empty;
    }
}

