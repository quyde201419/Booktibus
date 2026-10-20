using System.IO;
using System.Net;
using System.Net.Mail;
using System.Text.Json;
using Booktibus.Models;

namespace Booktibus.Services
{
    /// <summary>
    /// Configurable email delivery service for Booktibus.
    /// - Never hardcodes email passwords, app passwords, API keys, or secrets in source code.
    /// - Never sends live emails over the network unless SMTP settings AND explicit live-send consent
    ///   (BOOKTIBUS_SMTP_ENABLE_LIVE_SEND=true or SmtpEnableLiveSend=true in appsettings.local.json) are provided.
    /// - Defaults safely to Simulated Sandbox Mode so the entire Forgot Password workflow can be tested locally.
    /// </summary>
    public class EmailDeliveryService : IEmailDeliveryService
    {
        private readonly object _syncLock = new();
        private EmailMessageModel? _latestSimulatedEmail;
        private string? _lastSimulatedOtpForAutomatedTests;

        /// <summary>
        /// Optional test hook allowing automated integration tests to simulate an SMTP transport failure.
        /// </summary>
        public bool SimulateDeliveryFailureForTesting { get; set; }

        public bool IsSimulationMode => !TryLoadLiveSmtpConfiguration(out _);

        public string GetDeliveryModeSummary()
        {
            if (TryLoadLiveSmtpConfiguration(out SmtpRuntimeConfig? config) && config is not null)
            {
                return LocalizationService.IsVietnamese
                    ? $"[Chế độ SMTP Thật] Đang gửi email qua máy chủ SMTP '{config.Host}:{config.Port}'."
                    : $"[Live SMTP Mode] Sending emails via SMTP server '{config.Host}:{config.Port}'.";
            }

            return LocalizationService.IsVietnamese
                ? "[Chế độ Giả lập Email — Chưa cấu hình SMTP] Hệ thống chưa có cấu hình SMTP (Host/Port/Sender/Credentials) và sự đồng ý gửi email thật, nên email chứa mã OTP được chuyển vào Hộp thư Giả lập cục bộ để kiểm thử an toàn."
                : "[Simulated Email Mode — SMTP Not Configured] Live SMTP settings (Host/Port/Sender/Credentials) and live-send consent are not configured; OTP emails are routed to the local Simulated Inbox for safe testing.";
        }

        public EmailDeliveryResult SendPasswordResetOtpEmail(
            string recipientEmail,
            string recipientFullName,
            string targetUsername,
            string otpCode,
            TimeSpan validityDuration)
        {
            if (SimulateDeliveryFailureForTesting)
            {
                return EmailDeliveryResult.Failed(LocalizationService.IsVietnamese
                    ? "Không thể kết nối tới dịch vụ gửi email (Lỗi giả lập kiểm thử)."
                    : "Unable to connect to the email delivery service (Simulated test failure).");
            }

            if (string.IsNullOrWhiteSpace(recipientEmail) ||
                string.IsNullOrWhiteSpace(targetUsername) ||
                string.IsNullOrWhiteSpace(otpCode))
            {
                return EmailDeliveryResult.Failed(LocalizationService.IsVietnamese
                    ? "Thông tin người nhận email không hợp lệ."
                    : "Invalid email recipient parameters.");
            }

            int validMinutes = Math.Max(1, (int)Math.Round(validityDuration.TotalMinutes));

            string subject = LocalizationService.IsVietnamese
                ? $"[Booktibus] Mã xác thực OTP khôi phục mật khẩu cho tài khoản '{targetUsername}'"
                : $"[Booktibus] Password Reset OTP Verification Code for account '{targetUsername}'";

            string body = LocalizationService.IsVietnamese
                ? $"Xin chào {recipientFullName},\n\n" +
                  $"Hệ thống Quản lý Thư viện Booktibus nhận được yêu cầu đặt lại mật khẩu cho tài khoản:\n" +
                  $"• Tên đăng nhập (Username): {targetUsername}\n" +
                  $"• Email nhận mã: {recipientEmail}\n\n" +
                  $"Mã xác thực OTP (6 chữ số) của bạn là: {otpCode}\n\n" +
                  $"Lưu ý bảo mật:\n" +
                  $"- Mã OTP này chỉ có hiệu lực trong vòng {validMinutes} phút và chỉ sử dụng 1 lần duy nhất.\n" +
                  $"- Mã OTP chỉ áp dụng cho đúng tài khoản '{targetUsername}' (không ảnh hưởng đến tài khoản khác dùng chung email).\n" +
                  $"- Tuyệt đối không chia sẻ mã OTP này cho bất kỳ ai."
                : $"Hello {recipientFullName},\n\n" +
                  $"Booktibus Library Management System received a password reset request for:\n" +
                  $"• Username: {targetUsername}\n" +
                  $"• Registered Email: {recipientEmail}\n\n" +
                  $"Your 6-digit OTP verification code is: {otpCode}\n\n" +
                  $"Security Notice:\n" +
                  $"- This OTP expires in {validMinutes} minutes and can only be used once.\n" +
                  $"- This OTP applies strictly to username '{targetUsername}' (other accounts sharing this email are unaffected).\n" +
                  $"- Never share this OTP code with anyone.";

            // Check if live SMTP is explicitly configured and enabled
            if (TryLoadLiveSmtpConfiguration(out SmtpRuntimeConfig? smtpConfig) && smtpConfig is not null)
            {
                try
                {
                    using SmtpClient client = new(smtpConfig.Host, smtpConfig.Port)
                    {
                        EnableSsl = smtpConfig.UseSsl,
                        DeliveryMethod = SmtpDeliveryMethod.Network,
                        UseDefaultCredentials = false,
                        Credentials = new NetworkCredential(smtpConfig.Username, smtpConfig.Password),
                        Timeout = 10_000
                    };

                    using MailMessage mail = new()
                    {
                        From = new MailAddress(smtpConfig.SenderEmail, "Booktibus Library System"),
                        Subject = subject,
                        Body = body,
                        IsBodyHtml = false
                    };
                    mail.To.Add(new MailAddress(recipientEmail, recipientFullName));

                    client.Send(mail);
                    return EmailDeliveryResult.SentLive();
                }
                catch (Exception ex)
                {
                    return EmailDeliveryResult.Failed(ex.Message);
                }
            }

            // Default: Simulated Sandbox Mode (in-memory only, never written to log files or console)
            lock (_syncLock)
            {
                _latestSimulatedEmail = new EmailMessageModel
                {
                    RecipientEmail = recipientEmail,
                    RecipientDisplayName = recipientFullName,
                    TargetUsername = targetUsername,
                    Subject = subject,
                    BodyText = body,
                    CreatedAtLocal = DateTime.Now
                };
                _lastSimulatedOtpForAutomatedTests = otpCode;
            }

            return EmailDeliveryResult.SentSimulated();
        }

        public EmailMessageModel? GetLatestSimulatedEmail()
        {
            lock (_syncLock)
            {
                return _latestSimulatedEmail;
            }
        }

        /// <summary>
        /// Retrieves the latest OTP from the in-memory simulation sandbox for automated integration tests.
        /// Never writes to logs or disk.
        /// </summary>
        public string? GetLatestSimulatedOtpForTesting()
        {
            lock (_syncLock)
            {
                return _lastSimulatedOtpForAutomatedTests;
            }
        }

        public void ClearSimulatedInbox()
        {
            lock (_syncLock)
            {
                _latestSimulatedEmail = null;
                _lastSimulatedOtpForAutomatedTests = null;
            }
        }

        private static bool TryLoadLiveSmtpConfiguration(out SmtpRuntimeConfig? config)
        {
            config = null;

            // 1. Check environment variables first (never hardcoded in source)
            string? enableLiveStr = Environment.GetEnvironmentVariable("BOOKTIBUS_SMTP_ENABLE_LIVE_SEND");
            string? host = Environment.GetEnvironmentVariable("BOOKTIBUS_SMTP_HOST");
            string? portStr = Environment.GetEnvironmentVariable("BOOKTIBUS_SMTP_PORT");
            string? user = Environment.GetEnvironmentVariable("BOOKTIBUS_SMTP_USER");
            string? pass = Environment.GetEnvironmentVariable("BOOKTIBUS_SMTP_PASS");
            string? sender = Environment.GetEnvironmentVariable("BOOKTIBUS_SMTP_FROM");

            // 2. Fallback to external local file appsettings.local.json if present
            if (string.IsNullOrWhiteSpace(host))
            {
                try
                {
                    string localConfigPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.local.json");
                    if (File.Exists(localConfigPath))
                    {
                        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(localConfigPath));
                        JsonElement root = doc.RootElement;

                        if (root.TryGetProperty("SmtpEnableLiveSend", out JsonElement liveElem))
                        {
                            enableLiveStr = liveElem.ValueKind == JsonValueKind.True ? "true" : "false";
                        }
                        if (root.TryGetProperty("SmtpHost", out JsonElement hostElem))
                        {
                            host = hostElem.GetString();
                        }
                        if (root.TryGetProperty("SmtpPort", out JsonElement portElem))
                        {
                            portStr = portElem.ToString();
                        }
                        if (root.TryGetProperty("SmtpUsername", out JsonElement userElem))
                        {
                            user = userElem.GetString();
                        }
                        if (root.TryGetProperty("SmtpPassword", out JsonElement passElem))
                        {
                            pass = passElem.GetString();
                        }
                        if (root.TryGetProperty("SmtpFromEmail", out JsonElement fromElem))
                        {
                            sender = fromElem.GetString();
                        }
                    }
                }
                catch
                {
                    // Ignore malformed local configuration and stay in safe simulation mode
                }
            }

            if (!string.Equals(enableLiveStr?.Trim(), "true", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(host) ||
                string.IsNullOrWhiteSpace(user) ||
                string.IsNullOrWhiteSpace(pass) ||
                string.IsNullOrWhiteSpace(sender))
            {
                return false;
            }

            int port = int.TryParse(portStr, out int parsedPort) && parsedPort > 0 ? parsedPort : 587;

            config = new SmtpRuntimeConfig
            {
                Host = host.Trim(),
                Port = port,
                Username = user.Trim(),
                Password = pass,
                SenderEmail = sender.Trim(),
                UseSsl = true
            };
            return true;
        }

        private sealed class SmtpRuntimeConfig
        {
            public string Host { get; init; } = string.Empty;
            public int Port { get; init; } = 587;
            public string Username { get; init; } = string.Empty;
            public string Password { get; init; } = string.Empty;
            public string SenderEmail { get; init; } = string.Empty;
            public bool UseSsl { get; init; } = true;
        }
    }
}

