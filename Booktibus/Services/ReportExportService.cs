using System.IO;
using System.Text;
using Booktibus.Data;
using Booktibus.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Win32;

namespace Booktibus.Services
{
    /// <summary>
    /// Service responsible for exporting library analytical reports to CSV/Excel files.
    /// Uses UTF-8 with BOM (Byte Order Mark) encoding so that Microsoft Excel and other
    /// spreadsheet editors display Vietnamese text and special characters seamlessly without mojibake.
    /// </summary>
    public class ReportExportService
    {
        /// <summary>
        /// Prompts the user to save and exports the complete book catalog and inventory from dbo.Books.
        /// </summary>
        public bool ExportBooksInventory(out string? savedFilePath, out string? errorMessage)
        {
            savedFilePath = null;
            errorMessage = null;

            try
            {
                StringBuilder sb = new();
                sb.AppendLine("Mã Sách,Tên Sách,Tác Giả,Thể Loại,Nhà Xuất Bản,Năm Xuất Bản,ISBN,Vị Trí Kệ,Tổng Số Bản,Bản Có Sẵn,Đang Cho Mượn");

                using SqlConnection conn = new(DatabaseConfig.GetConnectionString());
                conn.Open();

                const string sql = @"
                    SELECT 
                        [BookCode],
                        [Title],
                        [Author],
                        [Category],
                        ISNULL([Publisher], N'') AS [Publisher],
                        [PublishYear],
                        ISNULL([Isbn], N'') AS [Isbn],
                        ISNULL([ShelfLocation], N'') AS [ShelfLocation],
                        [TotalCopies],
                        [AvailableCopies],
                        ([TotalCopies] - [AvailableCopies]) AS [BorrowedCopies]
                    FROM [dbo].[Books]
                    ORDER BY [Category], [Title];";

                using SqlCommand cmd = new(sql, conn);
                using SqlDataReader reader = cmd.ExecuteReader();

                while (reader.Read())
                {
                    string bookCode = EscapeCsv(reader["BookCode"].ToString());
                    string title = EscapeCsv(reader["Title"].ToString());
                    string author = EscapeCsv(reader["Author"].ToString());
                    string category = EscapeCsv(reader["Category"].ToString());
                    string publisher = EscapeCsv(reader["Publisher"].ToString());
                    string year = reader["PublishYear"] == DBNull.Value ? "" : reader["PublishYear"].ToString()!;
                    string isbn = EscapeCsv(reader["Isbn"].ToString());
                    string shelf = EscapeCsv(reader["ShelfLocation"].ToString());
                    int total = Convert.ToInt32(reader["TotalCopies"]);
                    int avail = Convert.ToInt32(reader["AvailableCopies"]);
                    int borrowed = Convert.ToInt32(reader["BorrowedCopies"]);

                    sb.AppendLine($"{bookCode},{title},{author},{category},{publisher},{year},{isbn},{shelf},{total},{avail},{borrowed}");
                }

                string defaultName = $"Booktibus_TonKhoSach_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
                return PromptAndSaveCsv(defaultName, sb.ToString(), out savedFilePath, out errorMessage);
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// Prompts the user to save and exports outstanding unpaid fine records (both officially recorded and overdue provisional).
        /// </summary>
        public bool ExportUnpaidFines(FineManagementService fineService, out string? savedFilePath, out string? errorMessage)
        {
            savedFilePath = null;
            errorMessage = null;

            try
            {
                IReadOnlyList<FineItemModel> fines = fineService.GetStaffFinesWorkspace("", "Unpaid", out _, out string? queryError);
                if (queryError != null && fines.Count == 0)
                {
                    errorMessage = queryError;
                    return false;
                }

                // Also include provisional overdue records if any
                IReadOnlyList<FineItemModel> estimatedFines = fineService.GetStaffFinesWorkspace("", "ESTIMATED", out _, out _);

                StringBuilder sb = new();
                sb.AppendLine("Mã Khoản Phạt,Mã Phiếu Mượn,Độc Giả,Mã Thẻ,Tên Sách,Mã Sách,Hạn Trả,Ngày Trả Thực Tế,Số Ngày Quá Hạn,Mức Phạt Ngày (VNĐ),Số Tiền Phạt (VNĐ),Tính Chất / Phân Loại,Trạng Thái,Ghi Chú");

                List<FineItemModel> allUnpaid = new(fines);
                foreach (FineItemModel est in estimatedFines)
                {
                    if (allUnpaid.All(x => x.BorrowRecordId != est.BorrowRecordId))
                    {
                        allUnpaid.Add(est);
                    }
                }

                foreach (FineItemModel item in allUnpaid)
                {
                    string fineCode = EscapeCsv(item.FineCode);
                    string borrowCode = EscapeCsv(item.BorrowRecordCode);
                    string readerName = EscapeCsv(item.ReaderFullName);
                    string cardId = EscapeCsv(item.ReaderCardId);
                    string title = EscapeCsv(item.BookTitle);
                    string bookCode = EscapeCsv(item.BookCode);
                    string dueDate = EscapeCsv(item.DueDateDisplay);
                    string returnDate = EscapeCsv(item.ReturnedDateDisplay);
                    int days = item.DaysOverdue;
                    decimal rate = item.DailyRateVnd;
                    decimal amount = item.Amount;
                    string nature = EscapeCsv(item.RecordNatureBadgeText);
                    string status = EscapeCsv(item.StatusDisplayText);
                    string notes = EscapeCsv(item.Notes);

                    sb.AppendLine($"{fineCode},{borrowCode},{readerName},{cardId},{title},{bookCode},{dueDate},{returnDate},{days},{rate:F0},{amount:F0},{nature},{status},{notes}");
                }

                string defaultName = $"Booktibus_NoPhatChuaThu_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
                return PromptAndSaveCsv(defaultName, sb.ToString(), out savedFilePath, out errorMessage);
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// Prompts the user to save and exports the monthly circulation statistics breakdown.
        /// </summary>
        public bool ExportMonthlyCirculation(IEnumerable<PeriodLoanStatRowModel> monthlyStats, out string? savedFilePath, out string? errorMessage)
        {
            savedFilePath = null;
            errorMessage = null;

            try
            {
                StringBuilder sb = new();
                sb.AppendLine("Kỳ Thống Kê (Tháng/Năm),Tổng Lượt Mượn,Đang Mượn & Quá Hạn,Đã Trả Hoàn Tất");

                foreach (PeriodLoanStatRowModel row in monthlyStats)
                {
                    string period = EscapeCsv(row.PeriodLabel);
                    int total = row.TotalLoansInPeriod;
                    int active = row.ActiveOrOverdueInPeriod;
                    int returned = row.ReturnedInPeriod;

                    sb.AppendLine($"{period},{total},{active},{returned}");
                }

                string defaultName = $"Booktibus_ThongKeMuonTra_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
                return PromptAndSaveCsv(defaultName, sb.ToString(), out savedFilePath, out errorMessage);
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }
        }

        private static bool PromptAndSaveCsv(string defaultFileName, string content, out string? savedFilePath, out string? errorMessage)
        {
            savedFilePath = null;
            errorMessage = null;

            SaveFileDialog sfd = new()
            {
                FileName = defaultFileName,
                Filter = "CSV File (*.csv)|*.csv|All Files (*.*)|*.*",
                DefaultExt = ".csv",
                Title = LocalizationService.IsVietnamese ? "Lưu tệp báo cáo CSV / Excel" : "Save CSV / Excel Report File"
            };

            if (sfd.ShowDialog() == true)
            {
                try
                {
                    // Use UTF-8 with BOM so Microsoft Excel opens Vietnamese characters seamlessly
                    File.WriteAllText(sfd.FileName, content, new UTF8Encoding(true));
                    savedFilePath = sfd.FileName;
                    return true;
                }
                catch (Exception ex)
                {
                    errorMessage = ex.Message;
                    return false;
                }
            }

            return false;
        }

        private static string EscapeCsv(string? value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            bool mustQuote = value.Contains(',') || value.Contains('"') || value.Contains('\r') || value.Contains('\n');
            string escaped = value.Replace("\"", "\"\"");
            return mustQuote ? $"\"{escaped}\"" : escaped;
        }
    }
}
