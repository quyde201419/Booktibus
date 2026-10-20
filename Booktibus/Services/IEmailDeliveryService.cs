using Booktibus.Models;

namespace Booktibus.Services
{
    /// <summary>
    /// Contract for sending transactional emails (such as Forgot Password OTP verification emails).
    /// Supports both Live SMTP mode (when configured via environment variables / local config)
    /// and Simulated Sandbox mode (default when SMTP is not configured).
    /// Never embeds hardcoded credentials or logs OTP secrets.
    /// </summary>
    public interface IEmailDeliveryService
    {
        /// <summary>
        /// True when live SMTP is not configured or not enabled, meaning emails are captured in the local simulation sandbox.
        /// </summary>
        bool IsSimulationMode { get; }

        /// <summary>
        /// Human-readable description of the current email delivery mode and missing configuration (if any).
        /// </summary>
        string GetDeliveryModeSummary();

        /// <summary>
        /// Dispatches a password reset OTP email to the account's registered email address.
        /// </summary>
        EmailDeliveryResult SendPasswordResetOtpEmail(
            string recipientEmail,
            string recipientFullName,
            string targetUsername,
            string otpCode,
            TimeSpan validityDuration);

        /// <summary>
        /// Returns the most recently simulated email message (only populated when IsSimulationMode is true)
        /// for desktop testing when no SMTP server is configured.
        /// </summary>
        EmailMessageModel? GetLatestSimulatedEmail();

        /// <summary>
        /// Clears any stored simulated email messages from memory.
        /// </summary>
        void ClearSimulatedInbox();
    }
}

