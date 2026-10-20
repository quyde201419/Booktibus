using System.Data;
using System.Text.RegularExpressions;
using Booktibus.Data;
using Booktibus.Helpers;
using Booktibus.Models;
using Microsoft.Data.SqlClient;

namespace Booktibus.Services
{
    /// <summary>
    /// SQL Server implementation of IAuthenticationService backed by BooktibusDB.
    /// Persists PBKDF2-SHA256 password hashes, cryptographic salts, 5-attempt / 15-minute lockouts,
    /// Reader self-registration (strictly Role = 'Reader'), and one-time initial Administrator bootstrap.
    /// </summary>
    public class SqlAuthenticationService : IAuthenticationService
    {
        public bool IsTemporaryTestProvider => false;

        public int MaxFailedAttemptsBeforeLockout => 5;

        public TimeSpan LockoutDuration => TimeSpan.FromMinutes(15);

        public bool RequiresInitialAdminSetup()
        {
            try
            {
                using SqlConnection conn = new(DatabaseConfig.GetConnectionString());
                conn.Open();

                const string sql = @"
                    SELECT COUNT(1)
                    FROM [dbo].[Users]
                    WHERE [Role] = N'Administrator' AND [IsActive] = 1;";

                using SqlCommand cmd = new(sql, conn);
                int count = Convert.ToInt32(cmd.ExecuteScalar());
                return count == 0;
            }
            catch
            {
                return false;
            }
        }

        public AuthenticationResult Authenticate(string username, string password)
        {
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
            {
                return AuthenticationResult.Failure(AuthenticationFailureReason.EmptyCredentials);
            }

            string normalizedUsername = username.Trim();

            try
            {
                using SqlConnection conn = new(DatabaseConfig.GetConnectionString());
                conn.Open();

                const string selectSql = @"
                    SELECT
                        u.[UserId],
                        u.[Username],
                        u.[FullName],
                        u.[Role],
                        u.[PasswordHash],
                        u.[PasswordSalt],
                        u.[IsActive],
                        u.[FailedLoginAttempts],
                        u.[LockoutEndUtc],
                        r.[ReaderCardId]
                    FROM [dbo].[Users] u
                    LEFT JOIN [dbo].[Readers] r ON r.[UserId] = u.[UserId]
                    WHERE u.[Username] = @Username;";

                int userId;
                string dbUsername;
                string fullName;
                string roleStr;
                string passwordHash;
                string passwordSalt;
                bool isActive;
                int failedAttempts;
                DateTime? lockoutEndUtc;
                string? readerCardId;

                using (SqlCommand selectCmd = new(selectSql, conn))
                {
                    selectCmd.Parameters.Add("@Username", SqlDbType.NVarChar, 50).Value = normalizedUsername;
                    using SqlDataReader reader = selectCmd.ExecuteReader();

                    if (!reader.Read())
                    {
                        // Do not disclose whether username exists
                        return AuthenticationResult.Failure(
                            AuthenticationFailureReason.InvalidCredentials,
                            remainingAttempts: MaxFailedAttemptsBeforeLockout);
                    }

                    userId = reader.GetInt32(0);
                    dbUsername = reader.GetString(1);
                    fullName = reader.GetString(2);
                    roleStr = reader.GetString(3);
                    passwordHash = reader.GetString(4);
                    passwordSalt = reader.GetString(5);
                    isActive = reader.GetBoolean(6);
                    failedAttempts = reader.GetInt32(7);
                    lockoutEndUtc = reader.IsDBNull(8)
                        ? null
                        : DateTime.SpecifyKind(reader.GetDateTime(8), DateTimeKind.Utc);
                    readerCardId = reader.IsDBNull(9) ? null : reader.GetString(9);
                }

                if (!isActive)
                {
                    return AuthenticationResult.Failure(AuthenticationFailureReason.AccountDisabled);
                }

                DateTime nowUtc = DateTime.UtcNow;

                // 1. Check persistent database lockout state
                if (lockoutEndUtc.HasValue)
                {
                    if (lockoutEndUtc.Value > nowUtc)
                    {
                        TimeSpan remainingLockout = lockoutEndUtc.Value - nowUtc;
                        return AuthenticationResult.Failure(
                            AuthenticationFailureReason.AccountLockedOut,
                            remainingAttempts: 0,
                            remainingLockout: remainingLockout);
                    }

                    // Lockout window has expired; reset counters in SQL Server
                    ResetUserLockoutInDb(conn, userId);
                    failedAttempts = 0;
                }

                // 2. Verify password against stored PBKDF2-SHA256 hash & salt
                bool isValidPassword = PasswordHasher.VerifyPassword(password, passwordHash, passwordSalt);
                if (!isValidPassword)
                {
                    failedAttempts++;

                    if (failedAttempts >= MaxFailedAttemptsBeforeLockout)
                    {
                        DateTime newLockoutEndUtc = nowUtc.Add(LockoutDuration);
                        UpdateFailedLoginInDb(conn, userId, failedAttempts, newLockoutEndUtc);

                        return AuthenticationResult.Failure(
                            AuthenticationFailureReason.AccountLockedOut,
                            remainingAttempts: 0,
                            remainingLockout: LockoutDuration);
                    }

                    UpdateFailedLoginInDb(conn, userId, failedAttempts, null);
                    int remainingAttempts = MaxFailedAttemptsBeforeLockout - failedAttempts;

                    return AuthenticationResult.Failure(
                        AuthenticationFailureReason.InvalidCredentials,
                        remainingAttempts: remainingAttempts);
                }

                // 3. Successful login -> clear any previous failed attempts in SQL Server
                if (failedAttempts > 0 || lockoutEndUtc.HasValue)
                {
                    ResetUserLockoutInDb(conn, userId);
                }

                if (!Enum.TryParse(roleStr, ignoreCase: true, out UserRole parsedRole))
                {
                    parsedRole = UserRole.Reader;
                }

                UserSession session = new()
                {
                    UserId = userId,
                    Username = dbUsername,
                    FullName = fullName,
                    Role = parsedRole,
                    ReaderCardId = readerCardId,
                    SignedInAtLocal = DateTime.Now
                };

                return AuthenticationResult.Success(session);
            }
            catch (SqlException ex)
            {
                AppDiagnosticLogger.LogDatabaseException(nameof(SqlAuthenticationService), nameof(Authenticate), ex, $"Username={normalizedUsername}");
                return AuthenticationResult.Failure(
                    AuthenticationFailureReason.DatabaseError,
                    technicalDetails: ex.Message);
            }
        }

        /// <summary>
        /// Registers a new library member in BooktibusDB.
        /// Role is strictly enforced at the service and SQL level as 'Reader'.
        /// </summary>
        public RegistrationResult RegisterReader(ReaderRegistrationRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);

            string fullName = request.FullName?.Trim() ?? string.Empty;
            string username = request.Username?.Trim() ?? string.Empty;
            string email = request.Email?.Trim() ?? string.Empty;
            string phoneNumber = request.PhoneNumber?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(fullName) ||
                string.IsNullOrWhiteSpace(username) ||
                string.IsNullOrWhiteSpace(email) ||
                string.IsNullOrEmpty(request.Password))
            {
                return RegistrationResult.Failure(RegistrationFailureReason.MissingRequiredFields);
            }

            if (username.Length < 4 || username.Length > 50 || !Regex.IsMatch(username, @"^[a-zA-Z0-9._-]+$"))
            {
                return RegistrationResult.Failure(RegistrationFailureReason.InvalidUsernameFormat);
            }

            if (!Regex.IsMatch(email, @"^[^@\s]+@[^@\s]+\.[^@\s]+$"))
            {
                return RegistrationResult.Failure(RegistrationFailureReason.InvalidEmailFormat);
            }

            if (!TestAuthenticationService.IsStrongPassword(request.Password))
            {
                return RegistrationResult.Failure(RegistrationFailureReason.WeakPassword);
            }

            if (!string.Equals(request.Password, request.ConfirmPassword, StringComparison.Ordinal))
            {
                return RegistrationResult.Failure(RegistrationFailureReason.PasswordConfirmationMismatch);
            }

            try
            {
                using SqlConnection conn = new(DatabaseConfig.GetConnectionString());
                conn.Open();
                using SqlTransaction tx = conn.BeginTransaction(IsolationLevel.Serializable);

                // Check duplicate Username (Email is allowed to be shared across multiple accounts)
                using (SqlCommand checkUserCmd = new(
                    "SELECT COUNT(1) FROM [dbo].[Users] WHERE [Username] = @Username;",
                    conn,
                    tx))
                {
                    checkUserCmd.Parameters.Add("@Username", SqlDbType.NVarChar, 50).Value = username;
                    if (Convert.ToInt32(checkUserCmd.ExecuteScalar()) > 0)
                    {
                        tx.Rollback();
                        return RegistrationResult.Failure(RegistrationFailureReason.DuplicateUsername);
                    }
                }

                // Hash password with PBKDF2-SHA256 & 16-byte salt
                (string passwordHash, string passwordSalt) = PasswordHasher.HashPassword(request.Password);

                // Insert into dbo.Users with strictly enforced Role = 'Reader'
                const string insertUserSql = @"
                    INSERT INTO [dbo].[Users]
                        ([Username], [FullName], [Email], [Role], [PasswordHash], [PasswordSalt], [IsActive], [FailedLoginAttempts])
                    VALUES
                        (@Username, @FullName, @Email, N'Reader', @PasswordHash, @PasswordSalt, 1, 0);
                    SELECT CAST(SCOPE_IDENTITY() AS INT);";

                int newUserId;
                using (SqlCommand insertUserCmd = new(insertUserSql, conn, tx))
                {
                    insertUserCmd.Parameters.Add("@Username", SqlDbType.NVarChar, 50).Value = username;
                    insertUserCmd.Parameters.Add("@FullName", SqlDbType.NVarChar, 120).Value = fullName;
                    insertUserCmd.Parameters.Add("@Email", SqlDbType.NVarChar, 150).Value = email;
                    insertUserCmd.Parameters.Add("@PasswordHash", SqlDbType.NVarChar, 128).Value = passwordHash;
                    insertUserCmd.Parameters.Add("@PasswordSalt", SqlDbType.NVarChar, 128).Value = passwordSalt;

                    newUserId = Convert.ToInt32(insertUserCmd.ExecuteScalar());
                }

                string readerCardId = $"RD-{DateTime.UtcNow.Year}-{newUserId:D4}";

                const string insertReaderSql = @"
                    INSERT INTO [dbo].[Readers]
                        ([UserId], [ReaderCardId], [FullName], [Email], [PhoneNumber], [MembershipStatus])
                    VALUES
                        (@UserId, @ReaderCardId, @FullName, @Email, @PhoneNumber, N'Active');";

                using (SqlCommand insertReaderCmd = new(insertReaderSql, conn, tx))
                {
                    insertReaderCmd.Parameters.Add("@UserId", SqlDbType.Int).Value = newUserId;
                    insertReaderCmd.Parameters.Add("@ReaderCardId", SqlDbType.NVarChar, 30).Value = readerCardId;
                    insertReaderCmd.Parameters.Add("@FullName", SqlDbType.NVarChar, 120).Value = fullName;
                    insertReaderCmd.Parameters.Add("@Email", SqlDbType.NVarChar, 150).Value = email;
                    insertReaderCmd.Parameters.Add("@PhoneNumber", SqlDbType.NVarChar, 25).Value =
                        string.IsNullOrWhiteSpace(phoneNumber) ? DBNull.Value : phoneNumber;

                    insertReaderCmd.ExecuteNonQuery();
                }

                tx.Commit();
                return RegistrationResult.Success(username, readerCardId);
            }
            catch (SqlException ex)
            {
                AppDiagnosticLogger.LogDatabaseException(nameof(SqlAuthenticationService), nameof(RegisterReader), ex, $"Username={username}");
                return RegistrationResult.Failure(
                    RegistrationFailureReason.DatabaseUnavailable,
                    technicalDetails: ex.Message);
            }
        }

        /// <summary>
        /// One-time secure bootstrap for creating the initial Administrator account when BooktibusDB has 0 Administrators.
        /// Once an Administrator exists, this method permanently rejects further calls.
        /// </summary>
        public RegistrationResult SetupInitialAdministrator(InitialAdminSetupRequest request)
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

            if (username.Length < 4 || username.Length > 50 || !Regex.IsMatch(username, @"^[a-zA-Z0-9._-]+$"))
            {
                return RegistrationResult.Failure(RegistrationFailureReason.InvalidUsernameFormat);
            }

            if (!Regex.IsMatch(email, @"^[^@\s]+@[^@\s]+\.[^@\s]+$"))
            {
                return RegistrationResult.Failure(RegistrationFailureReason.InvalidEmailFormat);
            }

            if (!TestAuthenticationService.IsStrongPassword(request.Password))
            {
                return RegistrationResult.Failure(RegistrationFailureReason.WeakPassword);
            }

            if (!string.Equals(request.Password, request.ConfirmPassword, StringComparison.Ordinal))
            {
                return RegistrationResult.Failure(RegistrationFailureReason.PasswordConfirmationMismatch);
            }

            try
            {
                using SqlConnection conn = new(DatabaseConfig.GetConnectionString());
                conn.Open();
                using SqlTransaction tx = conn.BeginTransaction(IsolationLevel.Serializable);

                // Verify inside serializable transaction that NO Administrator exists yet
                const string checkAdminSql = @"
                    SELECT COUNT(1)
                    FROM [dbo].[Users] WITH (UPDLOCK, HOLDLOCK)
                    WHERE [Role] = N'Administrator';";

                using (SqlCommand checkAdminCmd = new(checkAdminSql, conn, tx))
                {
                    if (Convert.ToInt32(checkAdminCmd.ExecuteScalar()) > 0)
                    {
                        tx.Rollback();
                        return RegistrationResult.Failure(RegistrationFailureReason.AdminAlreadyInitialized);
                    }
                }

                // Check duplicate Username (Email may be shared across accounts)
                using (SqlCommand checkDupCmd = new(
                    "SELECT COUNT(1) FROM [dbo].[Users] WHERE [Username] = @Username;",
                    conn,
                    tx))
                {
                    checkDupCmd.Parameters.Add("@Username", SqlDbType.NVarChar, 50).Value = username;
                    if (Convert.ToInt32(checkDupCmd.ExecuteScalar()) > 0)
                    {
                        tx.Rollback();
                        return RegistrationResult.Failure(RegistrationFailureReason.DuplicateUsername);
                    }
                }

                (string passwordHash, string passwordSalt) = PasswordHasher.HashPassword(request.Password);

                const string insertAdminSql = @"
                    INSERT INTO [dbo].[Users]
                        ([Username], [FullName], [Email], [Role], [PasswordHash], [PasswordSalt], [IsActive], [FailedLoginAttempts])
                    VALUES
                        (@Username, @FullName, @Email, N'Administrator', @PasswordHash, @PasswordSalt, 1, 0);";

                using (SqlCommand insertAdminCmd = new(insertAdminSql, conn, tx))
                {
                    insertAdminCmd.Parameters.Add("@Username", SqlDbType.NVarChar, 50).Value = username;
                    insertAdminCmd.Parameters.Add("@FullName", SqlDbType.NVarChar, 120).Value = fullName;
                    insertAdminCmd.Parameters.Add("@Email", SqlDbType.NVarChar, 150).Value = email;
                    insertAdminCmd.Parameters.Add("@PasswordHash", SqlDbType.NVarChar, 128).Value = passwordHash;
                    insertAdminCmd.Parameters.Add("@PasswordSalt", SqlDbType.NVarChar, 128).Value = passwordSalt;

                    insertAdminCmd.ExecuteNonQuery();
                }

                tx.Commit();
                return RegistrationResult.Success(username);
            }
            catch (SqlException ex)
            {
                AppDiagnosticLogger.LogDatabaseException(nameof(SqlAuthenticationService), nameof(SetupInitialAdministrator), ex, $"Username={username}");
                return RegistrationResult.Failure(
                    RegistrationFailureReason.DatabaseUnavailable,
                    technicalDetails: ex.Message);
            }
        }

        public IReadOnlyList<TestCredentialGuideItem> GetStage4TestCredentialGuides()
        {
            return [];
        }

        public void ResetTestAccountLockouts()
        {
            // No-op in production SQL Server provider; lockouts are enforced persistently in dbo.Users.
        }

        private static void UpdateFailedLoginInDb(SqlConnection conn, int userId, int failedAttempts, DateTime? lockoutEndUtc)
        {
            const string sql = @"
                UPDATE [dbo].[Users]
                SET [FailedLoginAttempts] = @FailedAttempts,
                    [LockoutEndUtc] = @LockoutEndUtc
                WHERE [UserId] = @UserId;";

            using SqlCommand cmd = new(sql, conn);
            cmd.Parameters.Add("@FailedAttempts", SqlDbType.Int).Value = failedAttempts;
            cmd.Parameters.Add("@LockoutEndUtc", SqlDbType.DateTime2).Value =
                lockoutEndUtc.HasValue ? lockoutEndUtc.Value : DBNull.Value;
            cmd.Parameters.Add("@UserId", SqlDbType.Int).Value = userId;
            cmd.ExecuteNonQuery();
        }

        private static void ResetUserLockoutInDb(SqlConnection conn, int userId)
        {
            const string sql = @"
                UPDATE [dbo].[Users]
                SET [FailedLoginAttempts] = 0,
                    [LockoutEndUtc] = NULL
                WHERE [UserId] = @UserId;";

            using SqlCommand cmd = new(sql, conn);
            cmd.Parameters.Add("@UserId", SqlDbType.Int).Value = userId;
            cmd.ExecuteNonQuery();
        }
    }
}

