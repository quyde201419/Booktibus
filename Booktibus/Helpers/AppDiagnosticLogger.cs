using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace Booktibus.Helpers
{
    /// <summary>
    /// Centralized technical diagnostic logger for SQL Server and application runtime exceptions.
    /// Strictly sanitizes and redacts any sensitive authentication data (passwords, OTP codes,
    /// PBKDF2 hashes, salts, or connection secrets) before writing to Trace or disk log files.
    /// </summary>
    public static class AppDiagnosticLogger
    {
        private static readonly object LogLock = new();

        private static readonly Regex SensitiveKeyValuePattern = new(
            @"(?i)\b(password|pwd|passphrase|otp|otpcode|passwordhash|passwordsalt|otphash|otpsalt|secret|token)\s*([:=])\s*([^\s,;""']+)",
            RegexOptions.Compiled);

        public static string GetLogFilePath()
        {
            string logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");
            return Path.Combine(logDir, "booktibus-diagnostics.log");
        }

        /// <summary>
        /// Logs a SQL Server exception with technical metadata (ErrorNumber, State, Class, Server)
        /// while ensuring no passwords, OTPs, hashes, or salts are ever written.
        /// </summary>
        public static void LogDatabaseException(
            string component,
            string operation,
            SqlException ex,
            string? safeContext = null)
        {
            if (ex is null)
            {
                return;
            }

            string sanitizedMessage = SanitizeTechnicalText(ex.Message);
            string sanitizedContext = SanitizeTechnicalText(safeContext);

            StringBuilder sb = new();
            sb.Append('[')
              .Append(DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss 'UTC'"))
              .Append("] [SQL_ERROR] ")
              .Append('[')
              .Append(string.IsNullOrWhiteSpace(component) ? "Database" : component.Trim())
              .Append('.')
              .Append(string.IsNullOrWhiteSpace(operation) ? "Operation" : operation.Trim())
              .Append("] ")
              .Append($"SqlErrorNumber={ex.Number}; State={ex.State}; Class={ex.Class}; ")
              .Append($"Message=\"{sanitizedMessage}\"");

            if (!string.IsNullOrWhiteSpace(sanitizedContext))
            {
                sb.Append($"; Context=\"{sanitizedContext}\"");
            }

            WriteEntryInternal(sb.ToString());
        }

        /// <summary>
        /// Logs a general application or runtime exception safely without exposing credentials.
        /// </summary>
        public static void LogException(
            string component,
            string operation,
            Exception ex,
            string? safeContext = null)
        {
            if (ex is null)
            {
                return;
            }

            if (ex is SqlException sqlEx)
            {
                LogDatabaseException(component, operation, sqlEx, safeContext);
                return;
            }

            string sanitizedMessage = SanitizeTechnicalText(ex.Message);
            string sanitizedContext = SanitizeTechnicalText(safeContext);

            StringBuilder sb = new();
            sb.Append('[')
              .Append(DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss 'UTC'"))
              .Append("] [APP_ERROR] ")
              .Append('[')
              .Append(string.IsNullOrWhiteSpace(component) ? "Application" : component.Trim())
              .Append('.')
              .Append(string.IsNullOrWhiteSpace(operation) ? "Operation" : operation.Trim())
              .Append("] ")
              .Append($"ExceptionType={ex.GetType().Name}; ")
              .Append($"Message=\"{sanitizedMessage}\"");

            if (!string.IsNullOrWhiteSpace(sanitizedContext))
            {
                sb.Append($"; Context=\"{sanitizedContext}\"");
            }

            WriteEntryInternal(sb.ToString());
        }

        /// <summary>
        /// Redacts any key-value pairs that resemble passwords, OTPs, hashes, or salts.
        /// </summary>
        public static string SanitizeTechnicalText(string? rawText)
        {
            if (string.IsNullOrWhiteSpace(rawText))
            {
                return string.Empty;
            }

            string singleLine = rawText
                .Replace("\r", " ", StringComparison.Ordinal)
                .Replace("\n", " ", StringComparison.Ordinal)
                .Trim();

            return SensitiveKeyValuePattern.Replace(singleLine, "$1$2[REDACTED]");
        }

        public static IReadOnlyList<string> ReadRecentLogEntries(int maxEntries = 50)
        {
            try
            {
                string logPath = GetLogFilePath();
                if (!File.Exists(logPath))
                {
                    return [];
                }

                lock (LogLock)
                {
                    string[] allLines = File.ReadAllLines(logPath, Encoding.UTF8);
                    if (allLines.Length <= maxEntries)
                    {
                        return allLines;
                    }

                    return allLines[^maxEntries..];
                }
            }
            catch
            {
                return [];
            }
        }

        private static void WriteEntryInternal(string formattedEntry)
        {
            try
            {
                Trace.WriteLine(formattedEntry);

                string logPath = GetLogFilePath();
                string? logDir = Path.GetDirectoryName(logPath);

                lock (LogLock)
                {
                    if (!string.IsNullOrWhiteSpace(logDir) && !Directory.Exists(logDir))
                    {
                        Directory.CreateDirectory(logDir);
                    }

                    File.AppendAllText(logPath, formattedEntry + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch
            {
                // Never allow diagnostic logging failures to interrupt application flow
            }
        }
    }
}

