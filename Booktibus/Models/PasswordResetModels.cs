namespace Booktibus.Models
{
    /// <summary>
    /// Represents the current sub-step in the Forgot Password (Quên mật khẩu) recovery workflow.
    /// </summary>
    public enum ForgotPasswordStep
    {
        EnterUsername = 0,
        VerifyOtp = 1,
        SetNewPassword = 2
    }

    public enum PasswordResetFailureReason
    {
        None = 0,
        EmptyUsername = 1,
        InvalidUsernameFormat = 2,
        EmptyOtp = 3,
        InvalidOrExpiredOtp = 4,
        OtpAttemptsExceeded = 5,
        OtpRateLimitExceeded = 6,
        MissingPasswordFields = 7,
        WeakPassword = 8,
        PasswordConfirmationMismatch = 9,
        EmailDeliveryFailed = 10,
        DatabaseError = 11
    }

    /// <summary>
    /// Result of Step 1–3 (Requesting an OTP for a given username).
    /// Note: To prevent account enumeration, IsAcceptedForProcessing is true both when the
    /// account exists and when the username does not exist, returning the exact same UI message.
    /// </summary>
    public class OtpRequestResult
    {
        public bool IsAcceptedForProcessing { get; init; }

        public PasswordResetFailureReason FailureReason { get; init; }

        public int RetryAfterSeconds { get; init; }

        public string? TechnicalDetails { get; init; }

        public static OtpRequestResult Accepted() => new()
        {
            IsAcceptedForProcessing = true,
            FailureReason = PasswordResetFailureReason.None
        };

        public static OtpRequestResult Rejected(
            PasswordResetFailureReason reason,
            int retryAfterSeconds = 0,
            string? technicalDetails = null) => new()
        {
            IsAcceptedForProcessing = false,
            FailureReason = reason,
            RetryAfterSeconds = retryAfterSeconds,
            TechnicalDetails = technicalDetails
        };
    }

    /// <summary>
    /// Result of Step 4 (Verifying whether the entered 6-digit OTP is valid and unexpired for the username).
    /// </summary>
    public class OtpVerificationResult
    {
        public bool IsValid { get; init; }

        public PasswordResetFailureReason FailureReason { get; init; }

        public int RemainingAttempts { get; init; }

        public string? TechnicalDetails { get; init; }

        public static OtpVerificationResult Valid() => new()
        {
            IsValid = true,
            FailureReason = PasswordResetFailureReason.None
        };

        public static OtpVerificationResult Invalid(
            PasswordResetFailureReason reason,
            int remainingAttempts = 0,
            string? technicalDetails = null) => new()
        {
            IsValid = false,
            FailureReason = reason,
            RemainingAttempts = remainingAttempts,
            TechnicalDetails = technicalDetails
        };
    }

    /// <summary>
    /// Result of Step 5–8 (Validating new password, updating PBKDF2-SHA256 hash/salt in SQL Server, and invalidating OTP).
    /// </summary>
    public class PasswordResetResult
    {
        public bool IsSuccess { get; init; }

        public PasswordResetFailureReason FailureReason { get; init; }

        public int RemainingOtpAttempts { get; init; }

        public string? TechnicalDetails { get; init; }

        public static PasswordResetResult Success() => new()
        {
            IsSuccess = true,
            FailureReason = PasswordResetFailureReason.None
        };

        public static PasswordResetResult Failure(
            PasswordResetFailureReason reason,
            int remainingOtpAttempts = 0,
            string? technicalDetails = null) => new()
        {
            IsSuccess = false,
            FailureReason = reason,
            RemainingOtpAttempts = remainingOtpAttempts,
            TechnicalDetails = technicalDetails
        };
    }

    /// <summary>
    /// Represents an outbound email message dispatched by IEmailDeliveryService.
    /// </summary>
    public class EmailMessageModel
    {
        public string RecipientEmail { get; init; } = string.Empty;

        public string RecipientDisplayName { get; init; } = string.Empty;

        public string TargetUsername { get; init; } = string.Empty;

        public string Subject { get; init; } = string.Empty;

        public string BodyText { get; init; } = string.Empty;

        public DateTime CreatedAtLocal { get; init; } = DateTime.Now;
    }

    /// <summary>
    /// Result of sending an email via IEmailDeliveryService (either Live SMTP or Simulated Sandbox Mode).
    /// </summary>
    public class EmailDeliveryResult
    {
        public bool IsSuccess { get; init; }

        public bool WasSimulated { get; init; }

        public string? ErrorDetails { get; init; }

        public static EmailDeliveryResult SentLive() => new()
        {
            IsSuccess = true,
            WasSimulated = false
        };

        public static EmailDeliveryResult SentSimulated() => new()
        {
            IsSuccess = true,
            WasSimulated = true
        };

        public static EmailDeliveryResult Failed(string errorDetails) => new()
        {
            IsSuccess = false,
            WasSimulated = false,
            ErrorDetails = errorDetails
        };
    }
}

