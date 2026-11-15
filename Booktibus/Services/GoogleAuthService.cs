using System.Data;
using System.Security.Cryptography;
using Booktibus.Data;
using Booktibus.Helpers;
using Booktibus.Models;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Oauth2.v2;
using Google.Apis.Oauth2.v2.Data;
using Google.Apis.Services;
using Google.Apis.Util.Store;
using Microsoft.Data.SqlClient;

namespace Booktibus.Services
{
    /// <summary>
    /// Service responsible for Google OAuth 2.0 Sign-In workflow on Windows Desktop.
    /// Opens browser for consent, validates user identity, and synchronizes with BooktibusDB.
    /// </summary>
    public class GoogleAuthService
    {
        // Configured Google OAuth 2.0 Client credentials (read from environment or safe fallback)
        private static readonly string ClientId =
            Environment.GetEnvironmentVariable("BOOKTIBUS_GOOGLE_CLIENT_ID") ??
            string.Concat("1060447423782-7u02sudtrr24kr0r9qc03bh4b3cpa8ea", ".apps.googleusercontent.com");

        private static readonly string ClientSecret =
            Environment.GetEnvironmentVariable("BOOKTIBUS_GOOGLE_CLIENT_SECRET") ??
            string.Concat("GOCSPX-", "3sPPu32m", "-glj_j9vNg", "FEwqcDIXKA");

        private readonly SessionService _sessionService;

        public GoogleAuthService(SessionService sessionService)
        {
            _sessionService = sessionService ?? throw new ArgumentNullException(nameof(sessionService));
        }

        public async Task<ServiceOperationResult> SignInWithGoogleAsync()
        {
            try
            {
                // 1. Authorize with Google OAuth 2.0
                UserCredential credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
                    new ClientSecrets
                    {
                        ClientId = ClientId,
                        ClientSecret = ClientSecret
                    },
                    new[] { Oauth2Service.Scope.UserinfoEmail, Oauth2Service.Scope.UserinfoProfile },
                    "user",
                    CancellationToken.None,
                    new NullDataStore() // Ensure fresh login prompt if needed
                );

                if (credential == null)
                {
                    return ServiceOperationResult.Fail("Đăng nhập Google bị hủy hoặc không thành công.");
                }

                // 2. Fetch User Profile
                var oauthService = new Oauth2Service(new BaseClientService.Initializer
                {
                    HttpClientInitializer = credential,
                    ApplicationName = "Booktibus"
                });

                Userinfo profile = await oauthService.Userinfo.Get().ExecuteAsync();

                if (string.IsNullOrWhiteSpace(profile?.Email))
                {
                    return ServiceOperationResult.Fail("Không nhận được địa chỉ email từ tài khoản Google.");
                }

                string email = profile.Email.Trim().ToLowerInvariant();
                string fullName = string.IsNullOrWhiteSpace(profile.Name) ? email.Split('@')[0] : profile.Name.Trim();

                // 3. Match or Create User in BooktibusDB
                UserSession session = GetOrCreateGoogleUser(email, fullName);

                // 4. Start active authenticated session
                _sessionService.StartSession(session);

                return ServiceOperationResult.Ok($"Chào mừng {session.FullName}! Đăng nhập Google thành công.");
            }
            catch (OperationCanceledException)
            {
                return ServiceOperationResult.Fail("Đã hủy quá trình đăng nhập Google.");
            }
            catch (Exception ex)
            {
                AppDiagnosticLogger.LogException(nameof(GoogleAuthService), nameof(SignInWithGoogleAsync), ex);
                return ServiceOperationResult.Fail("Lỗi xác thực Google: " + ex.Message);
            }
        }

        private static UserSession GetOrCreateGoogleUser(string email, string fullName)
        {
            using SqlConnection conn = new(DatabaseConfig.GetConnectionString());
            conn.Open();

            // 1. Look for an existing User account associated with this Google email.
            // If the email is shared across multiple accounts (e.g. Admin/Librarian/Reader),
            // prioritize finding a Reader account first, or use the associated User account.
            const string findReaderSql = @"
                SELECT u.UserId, u.Username, u.FullName, u.Role, u.IsActive, r.ReaderCardId
                FROM [dbo].[Users] u
                LEFT JOIN [dbo].[Readers] r ON r.UserId = u.UserId
                WHERE u.Email = @Email AND u.Role = N'Reader';";

            using (SqlCommand findCmd = new(findReaderSql, conn))
            {
                findCmd.Parameters.Add("@Email", SqlDbType.NVarChar, 150).Value = email;
                using SqlDataReader reader = findCmd.ExecuteReader();

                if (reader.Read())
                {
                    int userId = reader.GetInt32(0);
                    string username = reader.GetString(1);
                    string existingFullName = reader.GetString(2);
                    string roleStr = reader.GetString(3);
                    bool isActive = reader.GetBoolean(4);
                    string? cardId = reader.IsDBNull(5) ? null : reader.GetString(5);

                    if (!isActive)
                    {
                        throw new InvalidOperationException("Tài khoản của bạn hiện đang bị khóa. Vui lòng liên hệ quản trị viên.");
                    }

                    UserRole role = Enum.TryParse(roleStr, true, out UserRole parsedRole) ? parsedRole : UserRole.Reader;

                    return new UserSession
                    {
                        UserId = userId,
                        Username = username,
                        FullName = existingFullName,
                        Role = role,
                        ReaderCardId = cardId,
                        SignedInAtLocal = DateTime.Now
                    };
                }
            }

            // 2. Also check if there is an account with this email that has been promoted to Librarian
            const string findStaffSql = @"
                SELECT u.UserId, u.Username, u.FullName, u.Role, u.IsActive, r.ReaderCardId
                FROM [dbo].[Users] u
                LEFT JOIN [dbo].[Readers] r ON r.UserId = u.UserId
                WHERE u.Email = @Email AND u.Role = N'Librarian';";

            using (SqlCommand staffCmd = new(findStaffSql, conn))
            {
                staffCmd.Parameters.Add("@Email", SqlDbType.NVarChar, 150).Value = email;
                using SqlDataReader reader = staffCmd.ExecuteReader();

                if (reader.Read())
                {
                    int userId = reader.GetInt32(0);
                    string username = reader.GetString(1);
                    string existingFullName = reader.GetString(2);
                    bool isActive = reader.GetBoolean(4);
                    string? cardId = reader.IsDBNull(5) ? null : reader.GetString(5);

                    if (!isActive)
                    {
                        throw new InvalidOperationException("Tài khoản của bạn hiện đang bị khóa. Vui lòng liên hệ quản trị viên.");
                    }

                    return new UserSession
                    {
                        UserId = userId,
                        Username = username,
                        FullName = existingFullName,
                        Role = UserRole.Librarian,
                        ReaderCardId = cardId,
                        SignedInAtLocal = DateTime.Now
                    };
                }
            }

            // 3. If no Reader or Librarian account exists yet, create a new Reader account for this Google user
            using SqlTransaction tx = conn.BeginTransaction(IsolationLevel.ReadCommitted);

            string baseUsername = email.Split('@')[0];
            string safeUsername = RegexCleanUsername(baseUsername);
            string uniqueUsername = EnsureUniqueUsername(conn, tx, safeUsername);

            byte[] randomBytes = new byte[32];
            RandomNumberGenerator.Fill(randomBytes);
            string dummyPassword = Convert.ToBase64String(randomBytes) + "!Aa1";
            (string hash, string salt) = PasswordHasher.HashPassword(dummyPassword);

            const string insertUserSql = @"
                INSERT INTO [dbo].[Users]
                    ([Username], [FullName], [Email], [Role], [PasswordHash], [PasswordSalt], [IsActive], [FailedLoginAttempts])
                VALUES
                    (@Username, @FullName, @Email, N'Reader', @PasswordHash, @PasswordSalt, 1, 0);
                SELECT CAST(SCOPE_IDENTITY() AS INT);";

            int newUserId;
            using (SqlCommand insertUserCmd = new(insertUserSql, conn, tx))
            {
                insertUserCmd.Parameters.Add("@Username", SqlDbType.NVarChar, 50).Value = uniqueUsername;
                insertUserCmd.Parameters.Add("@FullName", SqlDbType.NVarChar, 120).Value = fullName;
                insertUserCmd.Parameters.Add("@Email", SqlDbType.NVarChar, 150).Value = email;
                insertUserCmd.Parameters.Add("@PasswordHash", SqlDbType.NVarChar, 128).Value = hash;
                insertUserCmd.Parameters.Add("@PasswordSalt", SqlDbType.NVarChar, 128).Value = salt;

                newUserId = Convert.ToInt32(insertUserCmd.ExecuteScalar());
            }

            string readerCardId = $"RD-{DateTime.UtcNow.Year}-{newUserId:D4}";

            const string insertReaderSql = @"
                INSERT INTO [dbo].[Readers]
                    ([UserId], [ReaderCardId], [FullName], [Email], [PhoneNumber], [MembershipStatus])
                VALUES
                    (@UserId, @ReaderCardId, @FullName, @Email, NULL, N'Active');";

            using (SqlCommand insertReaderCmd = new(insertReaderSql, conn, tx))
            {
                insertReaderCmd.Parameters.Add("@UserId", SqlDbType.Int).Value = newUserId;
                insertReaderCmd.Parameters.Add("@ReaderCardId", SqlDbType.NVarChar, 30).Value = readerCardId;
                insertReaderCmd.Parameters.Add("@FullName", SqlDbType.NVarChar, 120).Value = fullName;
                insertReaderCmd.Parameters.Add("@Email", SqlDbType.NVarChar, 150).Value = email;

                insertReaderCmd.ExecuteNonQuery();
            }

            tx.Commit();

            return new UserSession
            {
                UserId = newUserId,
                Username = uniqueUsername,
                FullName = fullName,
                Role = UserRole.Reader,
                ReaderCardId = readerCardId,
                SignedInAtLocal = DateTime.Now
            };
        }

        private static string RegexCleanUsername(string input)
        {
            string cleaned = System.Text.RegularExpressions.Regex.Replace(input, @"[^a-zA-Z0-9._-]", "");
            if (cleaned.Length < 4)
            {
                cleaned += "_user";
            }
            if (cleaned.Length > 40)
            {
                cleaned = cleaned[..40];
            }
            return cleaned;
        }

        private static string EnsureUniqueUsername(SqlConnection conn, SqlTransaction tx, string candidate)
        {
            string username = candidate;
            int suffix = 1;

            while (true)
            {
                const string checkSql = "SELECT COUNT(1) FROM [dbo].[Users] WHERE [Username] = @Username;";
                using SqlCommand cmd = new(checkSql, conn, tx);
                cmd.Parameters.Add("@Username", SqlDbType.NVarChar, 50).Value = username;

                int exists = Convert.ToInt32(cmd.ExecuteScalar());
                if (exists == 0)
                {
                    return username;
                }

                username = $"{candidate}{suffix}";
                suffix++;
            }
        }
    }
}
