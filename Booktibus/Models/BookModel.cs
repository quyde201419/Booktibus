using System.IO;
using System.Text;
using Booktibus.Services;
using Booktibus.ViewModels;

namespace Booktibus.Models
{
    /// <summary>
    /// Comprehensive Book Model representing books in the library catalog (Module: Catalog & Media).
    /// Inherits from BookItemModel to maintain full compatibility with circulation and reservations,
    /// and extends it with media cover image support, file path resolution, and batch import utilities.
    /// </summary>
    public class BookModel : BookItemModel
    {
        private string _coverImagePath = string.Empty;

        public BookModel()
        {
        }

        public BookModel(BookItemModel source)
        {
            if (source == null) return;
            BookId = source.BookId;
            BookCode = source.BookCode;
            Title = source.Title;
            Author = source.Author;
            Category = source.Category;
            Publisher = source.Publisher;
            PublishYear = source.PublishYear;
            Isbn = source.Isbn;
            TotalCopies = source.TotalCopies;
            AvailableCopies = source.AvailableCopies;
            ShelfLocation = source.ShelfLocation;
            Description = source.Description;
            IsSampleData = source.IsSampleData;
            IsFavorite = source.IsFavorite;
            IsReservedByCurrentUser = source.IsReservedByCurrentUser;
        }

        public string CoverImagePath
        {
            get => _coverImagePath;
            set
            {
                if (SetProperty(ref _coverImagePath, value ?? string.Empty))
                {
                    OnPropertyChanged(nameof(DisplayCoverImagePath));
                    OnPropertyChanged(nameof(HasCustomCoverImage));
                }
            }
        }

        public bool HasCustomCoverImage => !string.IsNullOrWhiteSpace(_coverImagePath) && File.Exists(GetResolvedCoverPath());

        public string DisplayCoverImagePath
        {
            get
            {
                string resolved = GetResolvedCoverPath();
                if (!string.IsNullOrEmpty(resolved) && File.Exists(resolved))
                {
                    return resolved;
                }

                // Check default image in app base directory
                string defaultImg = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Images", "Covers", "default_book_cover.png");
                if (File.Exists(defaultImg))
                {
                    return defaultImg;
                }

                return "pack://application:,,,/Resources/default_book_cover.png";
            }
        }

        private string GetResolvedCoverPath()
        {
            if (string.IsNullOrWhiteSpace(_coverImagePath))
            {
                return string.Empty;
            }

            if (File.Exists(_coverImagePath))
            {
                return Path.GetFullPath(_coverImagePath);
            }

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string combined = Path.Combine(baseDir, _coverImagePath);
            if (File.Exists(combined))
            {
                return combined;
            }

            string currentDir = Path.Combine(Directory.GetCurrentDirectory(), _coverImagePath);
            if (File.Exists(currentDir))
            {
                return currentDir;
            }

            return string.Empty;
        }
    }

    /// <summary>
    /// Model representing a single parsed book entry from Excel/CSV for batch import.
    /// </summary>
    public class BatchBookImportItem
    {
        public int RowIndex { get; set; }
        public string BookCode { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Author { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string Publisher { get; set; } = string.Empty;
        public int? PublishYear { get; set; }
        public string Isbn { get; set; } = string.Empty;
        public int TotalCopies { get; set; } = 1;
        public string ShelfLocation { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string CoverImagePath { get; set; } = string.Empty;
        public bool IsValid { get; set; } = true;
        public string ErrorMessage { get; set; } = string.Empty;
    }

    /// <summary>
    /// Outcome summary of batch importing books from Excel/CSV.
    /// </summary>
    public class BatchImportResult
    {
        public int TotalRows { get; set; }
        public int SuccessCount { get; set; }
        public int FailureCount { get; set; }
        public List<string> ErrorMessages { get; } = new();

        public string SummaryMessage => LocalizationService.IsVietnamese
            ? $"Kết quả nhập danh mục: Thành công {SuccessCount}/{TotalRows} cuốn sách." +
              (FailureCount > 0 ? $" ({FailureCount} dòng bị bỏ qua hoặc lỗi)" : "")
            : $"Batch import result: Successfully imported {SuccessCount}/{TotalRows} books." +
              (FailureCount > 0 ? $" ({FailureCount} rows skipped or invalid)" : "");
    }

    /// <summary>
    /// High-performance CSV parser and generator for batch book imports.
    /// Handles UTF-8 encoding (with/without BOM), commas, semicolons, quotes, and Vietnamese accents.
    /// </summary>
    public static class CsvBookParser
    {
        public static List<BatchBookImportItem> ParseCsvFile(string filePath, out List<string> parsingWarnings)
        {
            parsingWarnings = new List<string>();
            List<BatchBookImportItem> items = new();

            if (!File.Exists(filePath))
            {
                parsingWarnings.Add("File không tồn tại trên hệ thống.");
                return items;
            }

            string[] lines = File.ReadAllLines(filePath, Encoding.UTF8);
            if (lines.Length == 0)
            {
                parsingWarnings.Add("File rỗng, không có dữ liệu.");
                return items;
            }

            int headerIndex = -1;
            char delimiter = ',';
            Dictionary<string, int> columnMap = new(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (string.IsNullOrWhiteSpace(line)) continue;

                int commaCount = CountOccurrences(line, ',');
                int semiCount = CountOccurrences(line, ';');
                char detectedDelim = semiCount > commaCount ? ';' : ',';

                var tokens = ParseCsvLine(line, detectedDelim);
                if (tokens.Count >= 3 && (tokens.Any(t => t.Equals("BookCode", StringComparison.OrdinalIgnoreCase) ||
                                                         t.Equals("Mã sách", StringComparison.OrdinalIgnoreCase) ||
                                                         t.Equals("Title", StringComparison.OrdinalIgnoreCase) ||
                                                         t.Equals("Tên sách", StringComparison.OrdinalIgnoreCase))))
                {
                    headerIndex = i;
                    delimiter = detectedDelim;
                    for (int c = 0; c < tokens.Count; c++)
                    {
                        string normalizedHeader = NormalizeHeader(tokens[c]);
                        if (!columnMap.ContainsKey(normalizedHeader))
                        {
                            columnMap[normalizedHeader] = c;
                        }
                    }
                    break;
                }
            }

            if (headerIndex == -1 || columnMap.Count == 0)
            {
                parsingWarnings.Add("Không tìm thấy dòng tiêu đề (Header) hợp lệ trong file. Vui lòng sử dụng file mẫu chuẩn.");
                return items;
            }

            for (int i = headerIndex + 1; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (string.IsNullOrWhiteSpace(line)) continue;

                var tokens = ParseCsvLine(line, delimiter);
                int rowNumber = i + 1;

                string bookCode = GetTokenValue(tokens, columnMap, "bookcode");
                string title = GetTokenValue(tokens, columnMap, "title");
                string author = GetTokenValue(tokens, columnMap, "author");
                string category = GetTokenValue(tokens, columnMap, "category");
                string publisher = GetTokenValue(tokens, columnMap, "publisher");
                string yearStr = GetTokenValue(tokens, columnMap, "publishyear");
                string isbn = GetTokenValue(tokens, columnMap, "isbn");
                string copiesStr = GetTokenValue(tokens, columnMap, "totalcopies");
                string shelf = GetTokenValue(tokens, columnMap, "shelflocation");
                string desc = GetTokenValue(tokens, columnMap, "description");
                string cover = GetTokenValue(tokens, columnMap, "coverimagepath");

                BatchBookImportItem item = new()
                {
                    RowIndex = rowNumber,
                    BookCode = bookCode,
                    Title = title,
                    Author = author,
                    Category = string.IsNullOrWhiteSpace(category) ? "Chung" : category,
                    Publisher = publisher,
                    Isbn = isbn,
                    ShelfLocation = string.IsNullOrWhiteSpace(shelf) ? "Kệ A1" : shelf,
                    Description = desc,
                    CoverImagePath = cover
                };

                if (string.IsNullOrWhiteSpace(bookCode))
                {
                    item.IsValid = false;
                    item.ErrorMessage = $"Dòng {rowNumber}: Thiếu mã sách (BookCode).";
                }
                else if (string.IsNullOrWhiteSpace(title))
                {
                    item.IsValid = false;
                    item.ErrorMessage = $"Dòng {rowNumber}: Thiếu tên sách (Title).";
                }
                else if (string.IsNullOrWhiteSpace(author))
                {
                    item.IsValid = false;
                    item.ErrorMessage = $"Dòng {rowNumber}: Thiếu tên tác giả (Author).";
                }
                else
                {
                    if (int.TryParse(copiesStr, out int copies) && copies > 0)
                    {
                        item.TotalCopies = copies;
                    }
                    else
                    {
                        item.TotalCopies = 1;
                    }

                    if (int.TryParse(yearStr, out int year) && year > 0 && year <= DateTime.Now.Year + 1)
                    {
                        item.PublishYear = year;
                    }
                }

                items.Add(item);
            }

            return items;
        }

        public static string GenerateSampleCsvTemplate()
        {
            StringBuilder sb = new();
            sb.AppendLine("Mã sách,Tên sách,Tác giả,Thể loại,Nhà xuất bản,Năm XB,ISBN,Số lượng,Vị trí kệ,Mô tả,Ảnh bìa");
            sb.AppendLine("BK-IT-001,C# 12 and .NET 8,Mark J. Price,Công nghệ thông tin,Packt Publishing,2023,978-1837635870,5,Kệ A1, Tầng 1, Ngăn 1,Hướng dẫn lập trình C# hiện đại từ căn bản đến nâng cao.,");
            sb.AppendLine("BK-IT-002,Clean Code: A Handbook of Agile Software Craftsmanship,Robert C. Martin,Công nghệ thông tin,Prentice Hall,2008,978-0132350884,4,Kệ A1, Tầng 1, Ngăn 2,Cẩm nang viết code sạch và bảo trì dễ dàng cho lập trình viên.,");
            sb.AppendLine("BK-DB-001,SQL Server Query Tuning,Grant Fritchey,Cơ sở dữ liệu,Apress,2022,978-1484279564,3,Kệ B2, Tầng 1, Ngăn 1,Tối ưu hóa câu truy vấn T-SQL và index trong SQL Server.,");
            sb.AppendLine("BK-LIT-001,Dế Mèn Phiêu Lưu Ký,Tô Hoài,Văn học Việt Nam,NXB Kim Đồng,2021,978-6042211902,8,Kệ C1, Tầng 2, Ngăn 3,Tác phẩm kinh điển về cuộc phiêu lưu của chú Dế Mèn và bài học tình bạn.,");
            sb.AppendLine("BK-ECO-001,Kinh Tế Học Cơ Bản,Thomas Sowell,Kinh tế & Quản trị,NXB Trẻ,2020,978-6041165744,6,Kệ D2, Tầng 1, Ngăn 4,Giải thích các quy luật kinh tế học dễ hiểu cho độc giả đại chúng.,");
            return sb.ToString();
        }

        private static string NormalizeHeader(string header)
        {
            string clean = header.Trim().ToLowerInvariant()
                .Replace(" ", "").Replace("_", "").Replace("-", "");

            return clean switch
            {
                "mãsách" or "masach" or "bookcode" or "code" or "mã" => "bookcode",
                "tênsách" or "tensach" or "title" or "tiêuđề" or "tieude" or "tên" => "title",
                "tácgiả" or "tacgia" or "author" => "author",
                "thểloại" or "theloai" or "category" or "chủđề" => "category",
                "nhàxuấtbản" or "nhaxuatban" or "nxb" or "publisher" => "publisher",
                "nămxb" or "namxb" or "nămxuấtbản" or "publishyear" or "year" or "năm" => "publishyear",
                "isbn" or "mãisbn" => "isbn",
                "sốlượng" or "soluong" or "totalcopies" or "copies" or "sl" => "totalcopies",
                "vịtríkệ" or "vitrike" or "kệ" or "shelflocation" or "shelf" or "vịtrí" => "shelflocation",
                "môtả" or "mota" or "description" or "tómtắt" => "description",
                "ảnhbìa" or "anhbia" or "coverimagepath" or "cover" or "hìnhảnh" => "coverimagepath",
                _ => clean
            };
        }

        private static string GetTokenValue(List<string> tokens, Dictionary<string, int> columnMap, string key)
        {
            if (columnMap.TryGetValue(key, out int idx) && idx >= 0 && idx < tokens.Count)
            {
                return tokens[idx].Trim();
            }
            return string.Empty;
        }

        private static int CountOccurrences(string text, char ch)
        {
            int count = 0;
            foreach (char c in text)
            {
                if (c == ch) count++;
            }
            return count;
        }

        private static List<string> ParseCsvLine(string line, char delimiter)
        {
            List<string> result = new();
            bool inQuotes = false;
            StringBuilder current = new();

            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '"')
                {
                    if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                    {
                        current.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = !inQuotes;
                    }
                }
                else if (c == delimiter && !inQuotes)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }
                else
                {
                    current.Append(c);
                }
            }

            result.Add(current.ToString());
            return result;
        }
    }
}
