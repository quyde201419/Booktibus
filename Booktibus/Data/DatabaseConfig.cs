using System.IO;
using System.Text.Json;
using Booktibus.Helpers;
using Booktibus.Models;
using Booktibus.Services;
using Microsoft.Data.SqlClient;

namespace Booktibus.Data
{
    /// <summary>
    /// Manages SQL Server connection configuration and verifies database/schema readiness
    /// before switching the application from TestAuthenticationService to SqlAuthenticationService.
    /// Prioritizes Windows Authentication and blocks usage of the 'sa' account.
    /// </summary>
    public static class DatabaseConfig
    {
        public const string DefaultDatabaseName = "BooktibusDB";
        private static string _configuredServerName = ResolveInitialServerName();

        public static string ConfiguredServerName
        {
            get => _configuredServerName;
            set
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    _configuredServerName = value.Trim();
                }
            }
        }

        /// <summary>
        /// Builds a secure Windows Authentication connection string targeting the specified database.
        /// Never embeds plaintext passwords or uses the 'sa' account.
        /// </summary>
        public static string GetConnectionString(string databaseName = DefaultDatabaseName)
        {
            string? customConnStr = Environment.GetEnvironmentVariable("BOOKTIBUS_CONNECTION_STRING");
            if (!string.IsNullOrWhiteSpace(customConnStr))
            {
                ValidateNotUsingSaAccount(customConnStr);
                var envBuilder = new SqlConnectionStringBuilder(customConnStr)
                {
                    InitialCatalog = databaseName,
                    ConnectTimeout = 4
                };
                return envBuilder.ConnectionString;
            }

            SqlConnectionStringBuilder builder = new()
            {
                DataSource = _configuredServerName,
                InitialCatalog = databaseName,
                IntegratedSecurity = true,
                Encrypt = true,
                TrustServerCertificate = true,
                ConnectTimeout = 4
            };

            return builder.ConnectionString;
        }

        /// <summary>
        /// Probes the SQL Server instance to check whether the server is reachable,
        /// whether BooktibusDB exists, whether the Users/Readers tables are created,
        /// and whether an initial Administrator account has been set up.
        /// </summary>
        public static DatabaseProbeResult ProbeDatabase()
        {
            string serverName = _configuredServerName;

            try
            {
                // Step 1: Probe 'master' to verify SQL Server reachability and check if BooktibusDB exists
                using SqlConnection masterConn = new(GetConnectionString("master"));
                masterConn.Open();

                using SqlCommand checkDbCmd = new(
                    "SELECT CASE WHEN DB_ID(@DbName) IS NOT NULL THEN 1 ELSE 0 END;",
                    masterConn);
                checkDbCmd.Parameters.AddWithValue("@DbName", DefaultDatabaseName);

                bool dbExists = Convert.ToInt32(checkDbCmd.ExecuteScalar()) == 1;
                if (!dbExists)
                {
                    return new DatabaseProbeResult
                    {
                        IsServerReachable = true,
                        IsDatabaseCreated = false,
                        IsSchemaReady = false,
                        HasAdministratorAccount = false,
                        ServerName = serverName,
                        DatabaseName = DefaultDatabaseName,
                        StatusSummary = LocalizationService.IsVietnamese
                            ? $"Đã kết nối máy chủ SQL Server ({serverName}), nhưng chưa có database '{DefaultDatabaseName}'. Hãy chạy file Data/BooktibusAuthSchema.sql trong SSMS."
                            : $"Connected to SQL Server ({serverName}), but database '{DefaultDatabaseName}' was not found. Please run Data/BooktibusAuthSchema.sql in SSMS."
                    };
                }

                // Step 2: Connect to BooktibusDB and verify all 10 required tables
                using SqlConnection appConn = new(GetConnectionString(DefaultDatabaseName));
                appConn.Open();

                var requiredTables = new[]
                {
                    "Roles",
                    "Users",
                    "Readers",
                    "Books",
                    "ReaderFavorites",
                    "BorrowRecords",
                    "PasswordResetOtps",
                    "FineSettings",
                    "Fines",
                    "FineAuditLogs"
                };

                const string checkTablesSql = @"
                    SELECT TABLE_NAME
                    FROM INFORMATION_SCHEMA.TABLES
                    WHERE TABLE_SCHEMA = 'dbo' AND TABLE_TYPE = 'BASE TABLE';";

                var existingTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using (SqlCommand checkSchemaCmd = new(checkTablesSql, appConn))
                using (SqlDataReader reader = checkSchemaCmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        existingTables.Add(reader.GetString(0));
                    }
                }

                var missingTables = requiredTables.Where(t => !existingTables.Contains(t)).ToList();

                if (missingTables.Count > 0)
                {
                    string missingList = string.Join(", ", missingTables);
                    return new DatabaseProbeResult
                    {
                        IsServerReachable = true,
                        IsDatabaseCreated = true,
                        IsSchemaReady = false,
                        HasAdministratorAccount = false,
                        MissingTables = missingTables,
                        ServerName = serverName,
                        DatabaseName = DefaultDatabaseName,
                        StatusSummary = LocalizationService.IsVietnamese
                            ? $"Database '{DefaultDatabaseName}' đã tồn tại nhưng thiếu các bảng: {missingList}. Hãy chạy các script migration: Data/BooktibusAuthSchema.sql, Data/BooktibusStage5ReaderSchema.sql, Data/BooktibusStage8FinesSchema.sql."
                            : $"Database '{DefaultDatabaseName}' exists but is missing tables: {missingList}. Please execute migration scripts: Data/BooktibusAuthSchema.sql, Data/BooktibusStage5ReaderSchema.sql, Data/BooktibusStage8FinesSchema.sql."
                    };
                }

                // Step 3: Check if at least one Administrator account exists
                const string checkAdminSql = @"
                    SELECT COUNT(1)
                    FROM [dbo].[Users]
                    WHERE [Role] = N'Administrator' AND [IsActive] = 1;";

                using SqlCommand checkAdminCmd = new(checkAdminSql, appConn);
                int adminCount = Convert.ToInt32(checkAdminCmd.ExecuteScalar());
                bool hasAdmin = adminCount > 0;

                return new DatabaseProbeResult
                {
                    IsServerReachable = true,
                    IsDatabaseCreated = true,
                    IsSchemaReady = true,
                    HasAdministratorAccount = hasAdmin,
                    ServerName = serverName,
                    DatabaseName = DefaultDatabaseName,
                    StatusSummary = LocalizationService.IsVietnamese
                        ? (hasAdmin
                            ? $"[SQL Server Đã Kết Nối] Đang sử dụng database '{DefaultDatabaseName}' trên máy chủ '{serverName}' (Windows Authentication)."
                            : $"[SQL Server Đã Kết Nối] Database '{DefaultDatabaseName}' đã sẵn sàng. Chưa có tài khoản Quản trị viên — hãy khởi tạo Administrator đầu tiên.")
                        : (hasAdmin
                            ? $"[SQL Server Connected] Active database '{DefaultDatabaseName}' on '{serverName}' (Windows Authentication)."
                            : $"[SQL Server Connected] Database '{DefaultDatabaseName}' is ready. No Administrator exists yet — please complete Initial Admin Setup.")
                };
            }
            catch (SqlException ex)
            {
                AppDiagnosticLogger.LogDatabaseException(nameof(DatabaseConfig), nameof(ProbeDatabase), ex, $"Server={serverName}; Database={DefaultDatabaseName}");
                return new DatabaseProbeResult
                {
                    IsServerReachable = false,
                    IsDatabaseCreated = false,
                    IsSchemaReady = false,
                    HasAdministratorAccount = false,
                    ServerName = serverName,
                    DatabaseName = DefaultDatabaseName,
                    StatusSummary = LocalizationService.IsVietnamese
                        ? $"[Chưa kết nối được SQL Server '{serverName}'] ({ex.Message}). Ứng dụng đang tạm giữ chế độ Xác thực Thử nghiệm."
                        : $"[SQL Server '{serverName}' unreachable] ({ex.Message}). Application remains on Temporary Test Authentication."
                };
            }
            catch (Exception ex)
            {
                AppDiagnosticLogger.LogException(nameof(DatabaseConfig), nameof(ProbeDatabase), ex, $"Server={serverName}; Database={DefaultDatabaseName}");
                return new DatabaseProbeResult
                {
                    IsServerReachable = false,
                    IsDatabaseCreated = false,
                    IsSchemaReady = false,
                    HasAdministratorAccount = false,
                    ServerName = serverName,
                    DatabaseName = DefaultDatabaseName,
                    StatusSummary = ex.Message
                };
            }
        }

        private static string ResolveInitialServerName()
        {
            string? envServer = Environment.GetEnvironmentVariable("BOOKTIBUS_SQL_SERVER");
            if (!string.IsNullOrWhiteSpace(envServer))
            {
                return envServer.Trim();
            }

            try
            {
                string localConfigPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.local.json");
                if (File.Exists(localConfigPath))
                {
                    using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(localConfigPath));
                    if (doc.RootElement.TryGetProperty("SqlServerName", out JsonElement serverElement))
                    {
                        string? configured = serverElement.GetString();
                        if (!string.IsNullOrWhiteSpace(configured))
                        {
                            return configured.Trim();
                        }
                    }
                }
            }
            catch
            {
                // Ignore malformed local file and fall back to localhost
            }

            return "localhost";
        }

        private static void ValidateNotUsingSaAccount(string connectionString)
        {
            SqlConnectionStringBuilder builder = new(connectionString);
            if (!builder.IntegratedSecurity &&
                string.Equals(builder.UserID?.Trim(), "sa", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Security policy violation: Using the SQL Server 'sa' superuser account for application connections is prohibited.");
            }
        }
    }
}

