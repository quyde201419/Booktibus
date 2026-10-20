using System.Data;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Booktibus.Data;
using Booktibus.Helpers;
using Booktibus.Models;
using Microsoft.Data.SqlClient;

namespace Booktibus.Services
{
    /// <summary>
    /// Implements the secure 8-step Forgot Password (Quên mật khẩu) workflow backed by SQL Server BooktibusDB.
    /// Security guarantees:
    /// - Account lookup is strictly by Username -> UserId (supports multiple accounts sharing the same email).
    /// - Users cannot supply an arbitrary email to reset another account's password.
    /// - 6-digit OTP is generated via CSPRNG (RandomNumberGenerator) and stored ONLY as a PBKDF2-SHA256 hash + salt.
    /// - OTP expires in 5 minutes, is single-use, and locks out after 5 failed verification attempts.
    /// - Rate limiting prevents OTP spam (cooldown between requests + max 3 requests per 15 minutes).
    /// - Anti-enumeration: Step 1 returns the exact same response whether a username exists or not.
    /// - Never writes OTPs, passwords, hashes, or salts to logs.
    /// </summary>
    public class PasswordResetService
    {
        private static readonly Regex UsernamePattern = new(@"^[a-zA-Z0-9._-]{4,50}$", RegexOptions.Compiled);
        private static readonly Regex OtpDigitsPattern = new(@"^[0-9]{6}$", RegexOptions.Compiled);

        private readonly SessionService _sessionService;
        private readonly IEmailDeliveryService _emailDeliveryService;
        private readonly object _rateLimitLock = new();
        private readonly Dictionary<string, List<DateTime>> _requestHistoryByUsername = new(StringComparer.OrdinalIgnoreCase);

        public PasswordResetService(
            SessionService sessionService,
            IEmailDeliveryService emailDeliveryService)
        {
            ArgumentNullException.ThrowIfNull(sessionService);
            ArgumentNullException.ThrowIfNull(emailDeliveryService);

            _sessionService = sessionService;
            _emailDeliveryService = emailDeliveryService;
        }

        public TimeSpan OtpValidityDuration { get; set; } = TimeSpan.FromMinutes(5);

        public int MaxOtpVerificationAttempts { get; set; } = 5;

        public TimeSpan RequestCooldownDuration { get; set; } = TimeSpan.FromSeconds(60);

        public TimeSpan RequestRateLimitWindow { get; set; } = TimeSpan.FromMinutes(15);

        public int MaxRequestsPerWindow { get; set; } = 3;

        /// <summary>
        /// Clears in-memory rate-limit counters (used by automated test suites between test cases).
        /// </summary>
        public void ResetRateLimitCountersForTesting()
        {
            lock (_rateLimitLock)
            {
                _requestHistoryByUsername.Clear();
            }
        }

        /// <summary>
        /// Steps 1–3: Accepts a Username, looks up the account's registered Email in dbo.Users,
        /// generates a CSPRNG 6-digit OTP, stores its PBKDF2-SHA256 hash in dbo.PasswordResetOtps,
        /// and sends the OTP to the registered email address.
        /// Enforces anti-enumeration so callers cannot distinguish existing vs non-existing usernames.
        /// </summary>
        public OtpRequestResult RequestPasswordResetOtp(string username)
        {
            if (string.IsNullOrWhiteSpace(username))
            {
                return OtpRequestResult.Rejected(PasswordResetFailureReason.EmptyUsername);
            }

            string normalizedUsername = username.Trim();
            if (!UsernamePattern.IsMatch(normalizedUsername))
            {
                return OtpRequestResult.Rejected(PasswordResetFailureReason.InvalidUsernameFormat);
            }

            DateTime nowUtc = DateTime.UtcNow;

            // 1. Check in-memory rate limit (applies identically to existing and non-existing usernames)
            if (!CheckRateLimitAllowed(normalizedUsername, nowUtc, out int retrySeconds))
            {
                return OtpRequestResult.Rejected(
                    PasswordResetFailureReason.OtpRateLimitExceeded,
                    retryAfterSeconds: retrySeconds);
            }

            try
            {
                using SqlConnection conn = new(DatabaseConfig.GetConnectionString());
                conn.Open();

                EnsurePasswordResetOtpTableExists(conn);

                // 2. Look up account strictly by Username (never by Email alone)
                const string findUserSql = @"
                    SELECT [UserId], [Username], [FullName], [Email], [IsActive]
                    FROM [dbo].[Users]
                    WHERE [Username] = @Username;";

                int userId;
                string dbUsername;
                string fullName;
                string email;
                bool isActive;

                using (SqlCommand findCmd = new(findUserSql, conn))
                {
                    findCmd.Parameters.Add("@Username", SqlDbType.NVarChar, 50).Value = normalizedUsername;
                    using SqlDataReader reader = findCmd.ExecuteReader();

                    if (!reader.Read())
                    {
                        // Anti-enumeration: record rate-limit timestamp and return Accepted()
                        RecordRateLimitTimestamp(normalizedUsername, nowUtc);
                        return OtpRequestResult.Accepted();
                    }

                    userId = reader.GetInt32(0);
                    dbUsername = reader.GetString(1);
                    fullName = reader.GetString(2);
                    email = reader.GetString(3);
                    isActive = reader.GetBoolean(4);
                }

                if (!isActive || string.IsNullOrWhiteSpace(email))
                {
                    // Anti-enumeration: do not disclose disabled state on the unauthenticated forgot-password screen
                    RecordRateLimitTimestamp(normalizedUsername, nowUtc);
                    return OtpRequestResult.Accepted();
                }

                // 3. Check persistent SQL Server rate limit for this UserId
                if (!CheckDatabaseOtpRateLimit(conn, userId, nowUtc, out int dbRetrySeconds))
                {
                    return OtpRequestResult.Rejected(
                        PasswordResetFailureReason.OtpRateLimitExceeded,
                        retryAfterSeconds: dbRetrySeconds);
                }

                // 4. Generate cryptographic 6-digit OTP and hash with PBKDF2-SHA256 + random salt
                string otpCode = GenerateCryptographicSixDigitOtp();
                (string otpHash, string otpSalt) = PasswordHasher.HashPassword(otpCode);
                DateTime expiresAtUtc = nowUtc.Add(OtpValidityDuration);

                using SqlTransaction tx = conn.BeginTransaction(IsolationLevel.ReadCommitted);

                // Invalidate any previous unused OTPs for this UserId
                const string invalidateOldSql = @"
                    UPDATE [dbo].[PasswordResetOtps]
                    SET [IsUsed] = 1,
                        [UsedAtUtc] = @NowUtc
                    WHERE [UserId] = @UserId AND [IsUsed] = 0;";

                using (SqlCommand invCmd = new(invalidateOldSql, conn, tx))
                {
                    invCmd.Parameters.Add("@NowUtc", SqlDbType.DateTime2).Value = nowUtc;
                    invCmd.Parameters.Add("@UserId", SqlDbType.Int).Value = userId;
                    invCmd.ExecuteNonQuery();
                }

                // Insert new hashed OTP record linked to UserId
                const string insertOtpSql = @"
                    INSERT INTO [dbo].[PasswordResetOtps]
                        ([UserId], [OtpHash], [OtpSalt], [CreatedAtUtc], [ExpiresAtUtc], [AttemptCount], [MaxAttempts], [IsUsed])
                    VALUES
                        (@UserId, @OtpHash, @OtpSalt, @CreatedAtUtc, @ExpiresAtUtc, 0, @MaxAttempts, 0);";

                using (SqlCommand insCmd = new(insertOtpSql, conn, tx))
                {
                    insCmd.Parameters.Add("@UserId", SqlDbType.Int).Value = userId;
                    insCmd.Parameters.Add("@OtpHash", SqlDbType.NVarChar, 128).Value = otpHash;
                    insCmd.Parameters.Add("@OtpSalt", SqlDbType.NVarChar, 128).Value = otpSalt;
                    insCmd.Parameters.Add("@CreatedAtUtc", SqlDbType.DateTime2).Value = nowUtc;
                    insCmd.Parameters.Add("@ExpiresAtUtc", SqlDbType.DateTime2).Value = expiresAtUtc;
                    insCmd.Parameters.Add("@MaxAttempts", SqlDbType.Int).Value = MaxOtpVerificationAttempts;
                    insCmd.ExecuteNonQuery();
                }

                // 5. Send OTP to the account's registered Email
                EmailDeliveryResult emailResult = _emailDeliveryService.SendPasswordResetOtpEmail(
                    email,
                    fullName,
                    dbUsername,
                    otpCode,
                    OtpValidityDuration);

                if (!emailResult.IsSuccess)
                {
                    tx.Rollback();
                    return OtpRequestResult.Rejected(
                        PasswordResetFailureReason.EmailDeliveryFailed,
                        technicalDetails: emailResult.ErrorDetails);
                }

                tx.Commit();
                RecordRateLimitTimestamp(normalizedUsername, nowUtc);
                return OtpRequestResult.Accepted();
            }
            catch (SqlException ex)
            {
                AppDiagnosticLogger.LogDatabaseException(nameof(PasswordResetService), nameof(RequestPasswordResetOtp), ex, $"Username={normalizedUsername}");
                return OtpRequestResult.Rejected(
                    PasswordResetFailureReason.DatabaseError,
                    technicalDetails: ex.Message);
            }
        }

        /// <summary>
        /// Step 4: Verifies whether the user-entered 6-digit OTP matches the active, unexpired,
        /// non-exhausted OTP record for the specified Username.
        /// Increments AttemptCount on failure and permanently invalidates the OTP after MaxAttempts (5).
        /// </summary>
        public OtpVerificationResult VerifyPasswordResetOtp(string username, string otpCode)
        {
            if (string.IsNullOrWhiteSpace(username))
            {
                return OtpVerificationResult.Invalid(PasswordResetFailureReason.EmptyUsername);
            }

            string normalizedOtp = otpCode?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(normalizedOtp) || !OtpDigitsPattern.IsMatch(normalizedOtp))
            {
                return OtpVerificationResult.Invalid(PasswordResetFailureReason.EmptyOtp);
            }

            string normalizedUsername = username.Trim();
            DateTime nowUtc = DateTime.UtcNow;

            try
            {
                using SqlConnection conn = new(DatabaseConfig.GetConnectionString());
                conn.Open();

                EnsurePasswordResetOtpTableExists(conn);

                using SqlTransaction tx = conn.BeginTransaction(IsolationLevel.Serializable);

                if (!TryGetActiveUserIdByUsername(conn, tx, normalizedUsername, out int userId))
                {
                    tx.Rollback();
                    return OtpVerificationResult.Invalid(PasswordResetFailureReason.InvalidOrExpiredOtp);
                }

                OtpVerificationResult verifyResult = VerifyOtpInternal(conn, tx, userId, normalizedOtp, nowUtc, consumeOnSuccess: false);
                tx.Commit();
                return verifyResult;
            }
            catch (SqlException ex)
            {
                AppDiagnosticLogger.LogDatabaseException(nameof(PasswordResetService), nameof(VerifyPasswordResetOtp), ex, $"Username={normalizedUsername}");
                return OtpVerificationResult.Invalid(
                    PasswordResetFailureReason.DatabaseError,
                    technicalDetails: ex.Message);
            }
        }

        /// <summary>
        /// Steps 5–8: Atomically verifies the OTP, validates the new password against project policy,
        /// updates PasswordHash and PasswordSalt in dbo.Users using PBKDF2-SHA256, clears login lockout,
        /// invalidates the OTP so it can never be reused, and ends any active session for that user.
        /// </summary>
        public PasswordResetResult CompletePasswordReset(
            string username,
            string otpCode,
            string newPassword,
            string confirmPassword)
        {
            if (string.IsNullOrWhiteSpace(username))
            {
                return PasswordResetResult.Failure(PasswordResetFailureReason.EmptyUsername);
            }

            string normalizedOtp = otpCode?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(normalizedOtp) || !OtpDigitsPattern.IsMatch(normalizedOtp))
            {
                return PasswordResetResult.Failure(PasswordResetFailureReason.EmptyOtp);
            }

            if (string.IsNullOrEmpty(newPassword) || string.IsNullOrEmpty(confirmPassword))
            {
                return PasswordResetResult.Failure(PasswordResetFailureReason.MissingPasswordFields);
            }

            if (!TestAuthenticationService.IsStrongPassword(newPassword))
            {
                return PasswordResetResult.Failure(PasswordResetFailureReason.WeakPassword);
            }

            if (!string.Equals(newPassword, confirmPassword, StringComparison.Ordinal))
            {
                return PasswordResetResult.Failure(PasswordResetFailureReason.PasswordConfirmationMismatch);
            }

            string normalizedUsername = username.Trim();
            DateTime nowUtc = DateTime.UtcNow;

            try
            {
                using SqlConnection conn = new(DatabaseConfig.GetConnectionString());
                conn.Open();

                EnsurePasswordResetOtpTableExists(conn);

                using SqlTransaction tx = conn.BeginTransaction(IsolationLevel.Serializable);

                if (!TryGetActiveUserIdByUsername(conn, tx, normalizedUsername, out int userId))
                {
                    tx.Rollback();
                    return PasswordResetResult.Failure(PasswordResetFailureReason.InvalidOrExpiredOtp);
                }

                // Atomically verify AND consume the OTP inside the serializable transaction
                OtpVerificationResult otpVerify = VerifyOtpInternal(conn, tx, userId, normalizedOtp, nowUtc, consumeOnSuccess: true);
                if (!otpVerify.IsValid)
                {
                    // Commit the incremented AttemptCount / invalidation so brute-force attempts are persisted
                    tx.Commit();
                    return PasswordResetResult.Failure(
                        otpVerify.FailureReason,
                        remainingOtpAttempts: otpVerify.RemainingAttempts,
                        technicalDetails: otpVerify.TechnicalDetails);
                }

                // Hash new password with PBKDF2-SHA256 + 16-byte cryptographic random salt
                (string newHash, string newSalt) = PasswordHasher.HashPassword(newPassword);

                // Update dbo.Users strictly by UserId (preserves shared email accounts completely independent)
                // Also clears FailedLoginAttempts and LockoutEndUtc
                const string updatePasswordSql = @"
                    UPDATE [dbo].[Users]
                    SET [PasswordHash] = @PasswordHash,
                        [PasswordSalt] = @PasswordSalt,
                        [FailedLoginAttempts] = 0,
                        [LockoutEndUtc] = NULL
                    WHERE [UserId] = @UserId;";

                using (SqlCommand updateCmd = new(updatePasswordSql, conn, tx))
                {
                    updateCmd.Parameters.Add("@PasswordHash", SqlDbType.NVarChar, 128).Value = newHash;
                    updateCmd.Parameters.Add("@PasswordSalt", SqlDbType.NVarChar, 128).Value = newSalt;
                    updateCmd.Parameters.Add("@UserId", SqlDbType.Int).Value = userId;
                    updateCmd.ExecuteNonQuery();
                }

                tx.Commit();

                // Invalidate active session if the same user was signed in
                if (_sessionService.CurrentSession?.UserId == userId)
                {
                    _sessionService.EndSession();
                }

                return PasswordResetResult.Success();
            }
            catch (SqlException ex)
            {
                AppDiagnosticLogger.LogDatabaseException(nameof(PasswordResetService), nameof(CompletePasswordReset), ex, $"Username={normalizedUsername}");
                return PasswordResetResult.Failure(
                    PasswordResetFailureReason.DatabaseError,
                    technicalDetails: ex.Message);
            }
        }

        private static bool TryGetActiveUserIdByUsername(
            SqlConnection conn,
            SqlTransaction tx,
            string username,
            out int userId)
        {
            userId = 0;

            const string sql = @"
                SELECT [UserId], [IsActive]
                FROM [dbo].[Users] WITH (UPDLOCK, HOLDLOCK)
                WHERE [Username] = @Username;";

            using SqlCommand cmd = new(sql, conn, tx);
            cmd.Parameters.Add("@Username", SqlDbType.NVarChar, 50).Value = username;

            using SqlDataReader reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                return false;
            }

            int foundId = reader.GetInt32(0);
            bool isActive = reader.GetBoolean(1);
            if (!isActive)
            {
                return false;
            }

            userId = foundId;
            return true;
        }

        private static OtpVerificationResult VerifyOtpInternal(
            SqlConnection conn,
            SqlTransaction tx,
            int userId,
            string candidateOtp,
            DateTime nowUtc,
            bool consumeOnSuccess)
        {
            const string selectLatestOtpSql = @"
                SELECT TOP (1)
                    [OtpRequestId],
                    [OtpHash],
                    [OtpSalt],
                    [ExpiresAtUtc],
                    [AttemptCount],
                    [MaxAttempts],
                    [IsUsed]
                FROM [dbo].[PasswordResetOtps] WITH (UPDLOCK, HOLDLOCK)
                WHERE [UserId] = @UserId
                ORDER BY [OtpRequestId] DESC;";

            int otpRequestId;
            string otpHash;
            string otpSalt;
            DateTime expiresAtUtc;
            int attemptCount;
            int maxAttempts;
            bool isUsed;

            using (SqlCommand cmd = new(selectLatestOtpSql, conn, tx))
            {
                cmd.Parameters.Add("@UserId", SqlDbType.Int).Value = userId;
                using SqlDataReader reader = cmd.ExecuteReader();
                if (!reader.Read())
                {
                    return OtpVerificationResult.Invalid(PasswordResetFailureReason.InvalidOrExpiredOtp);
                }

                otpRequestId = reader.GetInt32(0);
                otpHash = reader.GetString(1);
                otpSalt = reader.GetString(2);
                expiresAtUtc = DateTime.SpecifyKind(reader.GetDateTime(3), DateTimeKind.Utc);
                attemptCount = reader.GetInt32(4);
                maxAttempts = reader.GetInt32(5);
                isUsed = reader.GetBoolean(6);
            }

            // 1. Check if already used
            if (isUsed)
            {
                return OtpVerificationResult.Invalid(PasswordResetFailureReason.InvalidOrExpiredOtp);
            }

            // 2. Check if already exceeded max attempts
            if (attemptCount >= maxAttempts)
            {
                MarkOtpUsed(conn, tx, otpRequestId, nowUtc);
                return OtpVerificationResult.Invalid(PasswordResetFailureReason.OtpAttemptsExceeded, remainingAttempts: 0);
            }

            // 3. Check if expired
            if (expiresAtUtc <= nowUtc)
            {
                MarkOtpUsed(conn, tx, otpRequestId, nowUtc);
                return OtpVerificationResult.Invalid(PasswordResetFailureReason.InvalidOrExpiredOtp);
            }

            // 4. Constant-time PBKDF2-SHA256 verification of OTP
            bool isMatch = PasswordHasher.VerifyPassword(candidateOtp, otpHash, otpSalt);
            if (!isMatch)
            {
                int newAttemptCount = attemptCount + 1;
                bool exhausted = newAttemptCount >= maxAttempts;

                const string incAttemptSql = @"
                    UPDATE [dbo].[PasswordResetOtps]
                    SET [AttemptCount] = @AttemptCount,
                        [IsUsed] = @IsUsed,
                        [UsedAtUtc] = @UsedAtUtc
                    WHERE [OtpRequestId] = @OtpRequestId;";

                using (SqlCommand incCmd = new(incAttemptSql, conn, tx))
                {
                    incCmd.Parameters.Add("@AttemptCount", SqlDbType.Int).Value = newAttemptCount;
                    incCmd.Parameters.Add("@IsUsed", SqlDbType.Bit).Value = exhausted;
                    incCmd.Parameters.Add("@UsedAtUtc", SqlDbType.DateTime2).Value = exhausted ? nowUtc : DBNull.Value;
                    incCmd.Parameters.Add("@OtpRequestId", SqlDbType.Int).Value = otpRequestId;
                    incCmd.ExecuteNonQuery();
                }

                if (exhausted)
                {
                    return OtpVerificationResult.Invalid(PasswordResetFailureReason.OtpAttemptsExceeded, remainingAttempts: 0);
                }

                int remaining = Math.Max(0, maxAttempts - newAttemptCount);
                return OtpVerificationResult.Invalid(
                    PasswordResetFailureReason.InvalidOrExpiredOtp,
                    remainingAttempts: remaining);
            }

            // 5. Valid OTP! If consumeOnSuccess is true (Step 8), mark it used immediately
            if (consumeOnSuccess)
            {
                const string consumeAllForUserSql = @"
                    UPDATE [dbo].[PasswordResetOtps]
                    SET [IsUsed] = 1,
                        [UsedAtUtc] = @NowUtc
                    WHERE [UserId] = @UserId AND [IsUsed] = 0;";

                using SqlCommand consumeCmd = new(consumeAllForUserSql, conn, tx);
                consumeCmd.Parameters.Add("@NowUtc", SqlDbType.DateTime2).Value = nowUtc;
                consumeCmd.Parameters.Add("@UserId", SqlDbType.Int).Value = userId;
                consumeCmd.ExecuteNonQuery();
            }

            return OtpVerificationResult.Valid();
        }

        private static void MarkOtpUsed(SqlConnection conn, SqlTransaction tx, int otpRequestId, DateTime nowUtc)
        {
            const string sql = @"
                UPDATE [dbo].[PasswordResetOtps]
                SET [IsUsed] = 1,
                    [UsedAtUtc] = @NowUtc
                WHERE [OtpRequestId] = @OtpRequestId;";

            using SqlCommand cmd = new(sql, conn, tx);
            cmd.Parameters.Add("@NowUtc", SqlDbType.DateTime2).Value = nowUtc;
            cmd.Parameters.Add("@OtpRequestId", SqlDbType.Int).Value = otpRequestId;
            cmd.ExecuteNonQuery();
        }

        private bool CheckDatabaseOtpRateLimit(SqlConnection conn, int userId, DateTime nowUtc, out int retrySeconds)
        {
            retrySeconds = 0;

            // Check most recent OTP creation time for cooldown
            const string latestSql = @"
                SELECT TOP (1) [CreatedAtUtc]
                FROM [dbo].[PasswordResetOtps]
                WHERE [UserId] = @UserId
                ORDER BY [OtpRequestId] DESC;";

            using (SqlCommand latestCmd = new(latestSql, conn))
            {
                latestCmd.Parameters.Add("@UserId", SqlDbType.Int).Value = userId;
                object? scalar = latestCmd.ExecuteScalar();
                if (scalar is DateTime lastCreatedRaw)
                {
                    DateTime lastCreatedUtc = DateTime.SpecifyKind(lastCreatedRaw, DateTimeKind.Utc);
                    TimeSpan elapsed = nowUtc - lastCreatedUtc;
                    if (elapsed < RequestCooldownDuration)
                    {
                        retrySeconds = Math.Max(1, (int)Math.Ceiling((RequestCooldownDuration - elapsed).TotalSeconds));
                        return false;
                    }
                }
            }

            // Check count within rolling window (e.g. max 3 per 15 minutes)
            DateTime windowStartUtc = nowUtc - RequestRateLimitWindow;
            const string windowCountSql = @"
                SELECT COUNT(1)
                FROM [dbo].[PasswordResetOtps]
                WHERE [UserId] = @UserId AND [CreatedAtUtc] >= @WindowStartUtc;";

            using (SqlCommand countCmd = new(windowCountSql, conn))
            {
                countCmd.Parameters.Add("@UserId", SqlDbType.Int).Value = userId;
                countCmd.Parameters.Add("@WindowStartUtc", SqlDbType.DateTime2).Value = windowStartUtc;
                int recentCount = Convert.ToInt32(countCmd.ExecuteScalar());
                if (recentCount >= MaxRequestsPerWindow)
                {
                    retrySeconds = Math.Max(1, (int)Math.Ceiling(RequestRateLimitWindow.TotalSeconds));
                    return false;
                }
            }

            return true;
        }

        private bool CheckRateLimitAllowed(string username, DateTime nowUtc, out int retrySeconds)
        {
            retrySeconds = 0;
            lock (_rateLimitLock)
            {
                if (!_requestHistoryByUsername.TryGetValue(username, out List<DateTime>? timestamps))
                {
                    return true;
                }

                DateTime windowStart = nowUtc - RequestRateLimitWindow;
                timestamps.RemoveAll(t => t < windowStart);

                if (timestamps.Count > 0)
                {
                    DateTime lastRequest = timestamps[^1];
                    TimeSpan sinceLast = nowUtc - lastRequest;
                    if (sinceLast < RequestCooldownDuration)
                    {
                        retrySeconds = Math.Max(1, (int)Math.Ceiling((RequestCooldownDuration - sinceLast).TotalSeconds));
                        return false;
                    }
                }

                if (timestamps.Count >= MaxRequestsPerWindow)
                {
                    DateTime oldestInWindow = timestamps[0];
                    TimeSpan untilWindowFrees = (oldestInWindow + RequestRateLimitWindow) - nowUtc;
                    retrySeconds = Math.Max(1, (int)Math.Ceiling(untilWindowFrees.TotalSeconds));
                    return false;
                }

                return true;
            }
        }

        private void RecordRateLimitTimestamp(string username, DateTime nowUtc)
        {
            lock (_rateLimitLock)
            {
                if (!_requestHistoryByUsername.TryGetValue(username, out List<DateTime>? timestamps))
                {
                    timestamps = new List<DateTime>();
                    _requestHistoryByUsername[username] = timestamps;
                }

                timestamps.Add(nowUtc);
            }
        }

        private static string GenerateCryptographicSixDigitOtp()
        {
            int value = RandomNumberGenerator.GetInt32(0, 1_000_000);
            return value.ToString("D6");
        }

        private static void EnsurePasswordResetOtpTableExists(SqlConnection conn)
        {
            const string sql = @"
                IF OBJECT_ID(N'dbo.PasswordResetOtps', N'U') IS NULL
                BEGIN
                    CREATE TABLE [dbo].[PasswordResetOtps]
                    (
                        [OtpRequestId] INT           IDENTITY(1,1) NOT NULL,
                        [UserId]       INT           NOT NULL,
                        [OtpHash]      NVARCHAR(128) NOT NULL,
                        [OtpSalt]      NVARCHAR(128) NOT NULL,
                        [CreatedAtUtc] DATETIME2(0)  NOT NULL CONSTRAINT [DF_PasswordResetOtps_CreatedAtUtc] DEFAULT (SYSUTCDATETIME()),
                        [ExpiresAtUtc] DATETIME2(0)  NOT NULL,
                        [AttemptCount] INT           NOT NULL CONSTRAINT [DF_PasswordResetOtps_AttemptCount] DEFAULT (0),
                        [MaxAttempts]  INT           NOT NULL CONSTRAINT [DF_PasswordResetOtps_MaxAttempts] DEFAULT (5),
                        [IsUsed]       BIT           NOT NULL CONSTRAINT [DF_PasswordResetOtps_IsUsed] DEFAULT (0),
                        [UsedAtUtc]    DATETIME2(0)  NULL,

                        CONSTRAINT [PK_PasswordResetOtps] PRIMARY KEY CLUSTERED ([OtpRequestId] ASC),
                        CONSTRAINT [FK_PasswordResetOtps_Users] FOREIGN KEY ([UserId]) REFERENCES [dbo].[Users] ([UserId]),
                        CONSTRAINT [CK_PasswordResetOtps_AttemptsNonNegative] CHECK ([AttemptCount] >= 0 AND [MaxAttempts] >= 1)
                    );

                    CREATE NONCLUSTERED INDEX [IX_PasswordResetOtps_UserId_CreatedAtUtc]
                        ON [dbo].[PasswordResetOtps] ([UserId] ASC, [CreatedAtUtc] DESC);
                END;";

            using SqlCommand cmd = new(sql, conn);
            cmd.ExecuteNonQuery();
        }
    }
}

