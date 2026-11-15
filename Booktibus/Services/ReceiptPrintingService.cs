using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Booktibus.Models;

namespace Booktibus.Services
{
    /// <summary>
    /// Service responsible for rendering and printing official library receipts and slips:
    /// - Phiếu Mượn Sách (Book Loan Slip)
    /// - Phiếu Trả Sách (Book Return Slip)
    /// - Biên Lai Thu Tiền Phạt (Fine Payment Receipt)
    /// Uses WPF System.Windows.Controls.PrintDialog to print to physical printers or export to PDF via 'Microsoft Print to PDF'.
    /// </summary>
    public static class ReceiptPrintingService
    {
        private static readonly FontFamily DefaultFont = new("Segoe UI");
        private static readonly SolidColorBrush NavyBrush = new(Color.FromRgb(30, 58, 138));
        private static readonly SolidColorBrush DarkTextBrush = new(Color.FromRgb(15, 23, 42));
        private static readonly SolidColorBrush MutedTextBrush = new(Color.FromRgb(100, 116, 139));
        private static readonly SolidColorBrush BorderBrush = new(Color.FromRgb(203, 213, 225));
        private static readonly SolidColorBrush BoxBgBrush = new(Color.FromRgb(248, 250, 252));
        private static readonly SolidColorBrush SuccessBrush = new(Color.FromRgb(13, 148, 136));

        /// <summary>
        /// Prints an official Book Loan Slip (Phiếu Mượn Sách).
        /// </summary>
        public static bool PrintBorrowSlip(
            string recordCode,
            string readerCardId,
            string readerFullName,
            string bookTitle,
            string bookCode,
            string bookAuthor,
            DateTime borrowedDate,
            DateTime dueDate,
            int renewalCount,
            string? notes,
            string? staffName,
            out string? errorMessage)
        {
            List<(string Label, string Value)> details = new()
            {
                ("Mã Phiếu Mượn:", recordCode),
                ("Ngày Mượn:", borrowedDate.ToString("dd/MM/yyyy HH:mm")),
                ("Hạn Trả Sách:", dueDate.ToString("dd/MM/yyyy")),
                ("Mã Thẻ Độc Giả:", readerCardId),
                ("Họ Tên Độc Giả:", string.IsNullOrWhiteSpace(readerFullName) ? readerCardId : readerFullName),
                ("Tên Đầu Sách:", bookTitle),
                ("Mã Sách (Book Code):", bookCode),
                ("Tác Giả:", string.IsNullOrWhiteSpace(bookAuthor) ? "(Chưa cập nhật)" : bookAuthor),
                ("Số Lần Đã Gia Hạn:", $"{renewalCount}/2 lần"),
            };

            if (!string.IsNullOrWhiteSpace(notes))
            {
                details.Add(("Ghi Chú Mượn:", notes));
            }

            string reminder = "LƯU Ý: Quý độc giả vui lòng bảo quản sách cẩn thận và hoàn trả sách đúng hạn trước ngày " +
                             dueDate.ToString("dd/MM/yyyy") +
                             ". Trả sách trễ hạn sẽ phát sinh phí phạt theo quy chế thư viện.";

            Border visual = BuildReceiptVisual(
                title: "PHIẾU MƯỢN SÁCH",
                subTitle: "BOOK LOAN SLIP",
                codeBadge: recordCode,
                details: details,
                highlightBoxTitle: "QUY ĐỊNH BẢO QUẢN & HOÀN TRẢ",
                highlightBoxContent: reminder,
                leftSignerTitle: "NGƯỜI MƯỢN SÁCH",
                leftSignerName: string.IsNullOrWhiteSpace(readerFullName) ? readerCardId : readerFullName,
                rightSignerTitle: "THỦ THƯ LẬP PHIẾU",
                rightSignerName: string.IsNullOrWhiteSpace(staffName) ? "Thư viện Booktibus" : staffName);

            return ExecutePrint(visual, $"Phieu_Muon_{recordCode}", out errorMessage);
        }

        /// <summary>
        /// Convenience overload for reader BorrowRecordItemModel.
        /// </summary>
        public static bool PrintBorrowSlip(BorrowRecordItemModel record, string? staffName, out string? errorMessage)
        {
            return PrintBorrowSlip(
                record.RecordCode,
                record.ReaderCardId,
                readerFullName: string.Empty,
                record.BookTitle,
                record.BookCode,
                record.BookAuthor,
                record.BorrowedAtLocal,
                record.DueAtLocal,
                record.RenewalCount,
                record.Notes,
                staffName,
                out errorMessage);
        }

        /// <summary>
        /// Convenience overload for staff StaffBorrowRecordItemModel.
        /// </summary>
        public static bool PrintBorrowSlip(StaffBorrowRecordItemModel record, string? staffName, out string? errorMessage)
        {
            return PrintBorrowSlip(
                record.RecordCode,
                record.ReaderCardId,
                record.ReaderFullName,
                record.BookTitle,
                record.BookCode,
                record.BookAuthor,
                record.BorrowedAtLocal,
                record.DueAtLocal,
                record.RenewalCount,
                record.Notes,
                staffName,
                out errorMessage);
        }

        /// <summary>
        /// Convenience overload for reader BorrowRecordItemModel return slip.
        /// </summary>
        public static bool PrintReturnSlip(BorrowRecordItemModel record, string? staffName, out string? errorMessage)
        {
            DateTime returnedDate = record.ReturnedAtLocal ?? DateTime.Now;
            return PrintReturnSlip(
                record.RecordCode,
                record.ReaderCardId,
                readerFullName: string.Empty,
                record.BookTitle,
                record.BookCode,
                record.BorrowedAtLocal,
                returnedDate,
                record.DueAtLocal,
                record.Notes,
                staffName,
                out errorMessage);
        }

        /// <summary>
        /// Convenience overload for staff StaffBorrowRecordItemModel return slip.
        /// </summary>
        public static bool PrintReturnSlip(StaffBorrowRecordItemModel record, string? staffName, out string? errorMessage)
        {
            DateTime returnedDate = record.ReturnedAtLocal ?? DateTime.Now;
            return PrintReturnSlip(
                record.RecordCode,
                record.ReaderCardId,
                record.ReaderFullName,
                record.BookTitle,
                record.BookCode,
                record.BorrowedAtLocal,
                returnedDate,
                record.DueAtLocal,
                record.Notes,
                staffName,
                out errorMessage);
        }

        /// <summary>
        /// Prints an official Book Return Slip (Phiếu Trả Sách).
        /// </summary>
        public static bool PrintReturnSlip(
            string recordCode,
            string readerCardId,
            string readerFullName,
            string bookTitle,
            string bookCode,
            DateTime borrowedDate,
            DateTime returnedDate,
            DateTime dueDate,
            string? returnNotes,
            string? staffName,
            out string? errorMessage)
        {
            bool isOverdue = returnedDate.Date > dueDate.Date;
            int overdueDays = isOverdue ? (returnedDate.Date - dueDate.Date).Days : 0;

            List<(string Label, string Value)> details = new()
            {
                ("Mã Phiếu Trả:", recordCode),
                ("Mã Thẻ Độc Giả:", readerCardId),
                ("Họ Tên Độc Giả:", string.IsNullOrWhiteSpace(readerFullName) ? readerCardId : readerFullName),
                ("Tên Đầu Sách:", bookTitle),
                ("Mã Sách:", bookCode),
                ("Ngày Mượn:", borrowedDate.ToString("dd/MM/yyyy HH:mm")),
                ("Hạn Trả Quy Định:", dueDate.ToString("dd/MM/yyyy")),
                ("Ngày Trả Thực Tế:", returnedDate.ToString("dd/MM/yyyy HH:mm")),
                ("Tình Trạng Trả:", isOverdue ? $"TRẢ QUÁ HẠN ({overdueDays} ngày)" : "TRẢ ĐÚNG HẠN (Hoàn tất)"),
            };

            if (!string.IsNullOrWhiteSpace(returnNotes))
            {
                details.Add(("Ghi Chú Khi Trả:", returnNotes));
            }

            string notice = isOverdue
                ? $"Xác nhận độc giả đã hoàn trả sách vào kho. Do trễ hạn {overdueDays} ngày, khoản phí phạt đã được ghi nhận vào hệ thống."
                : "Xác nhận độc giả đã hoàn trả đầy đủ sách vào kho thư viện trong tình trạng tốt. Xin chân thành cảm ơn quý độc giả!";

            Border visual = BuildReceiptVisual(
                title: "PHIẾU TRẢ SÁCH",
                subTitle: "BOOK RETURN RECEIPT",
                codeBadge: recordCode,
                details: details,
                highlightBoxTitle: "XÁC NHẬN NHẬP KHO",
                highlightBoxContent: notice,
                leftSignerTitle: "NGƯỜI TRẢ SÁCH",
                leftSignerName: string.IsNullOrWhiteSpace(readerFullName) ? readerCardId : readerFullName,
                rightSignerTitle: "THỦ THƯ TIẾP NHẬN",
                rightSignerName: string.IsNullOrWhiteSpace(staffName) ? "Thư viện Booktibus" : staffName);

            return ExecutePrint(visual, $"Phieu_Tra_{recordCode}", out errorMessage);
        }

        /// <summary>
        /// Prints an official Fine Payment Receipt (Biên Lai Thu Tiền Phạt).
        /// </summary>
        public static bool PrintFineReceipt(
            FineItemModel fine,
            string? staffName,
            out string? errorMessage)
        {
            List<(string Label, string Value)> details = new()
            {
                ("Mã Biên Lai Thu Phạt:", fine.FineCode),
                ("Mã Phiếu Mượn Gốc:", fine.BorrowRecordCode),
                ("Độc Giả:", fine.ReaderFullName),
                ("Mã Thẻ:", fine.ReaderCardId),
                ("Tên Sách Liên Quan:", fine.BookTitle),
                ("Hạn Trả / Ngày Trả:", $"{fine.DueDateDisplay} — {fine.ReturnedDateDisplay}"),
                ("Số Ngày Quá Hạn:", $"{fine.DaysOverdue} ngày"),
                ("Mức Phạt Quy Định:", $"{fine.DailyRateVnd:N0} VNĐ / ngày"),
                ("Ngày Thu Tiền:", fine.PaidAtLocal.HasValue ? fine.PaidAtLocal.Value.ToString("dd/MM/yyyy HH:mm") : DateTime.Now.ToString("dd/MM/yyyy HH:mm")),
                ("Trạng Thái Thanh Toán:", "ĐÃ THU ĐỦ (PAID)"),
            };

            if (!string.IsNullOrWhiteSpace(fine.Notes))
            {
                details.Add(("Ghi Chú Nghiệp Vụ:", fine.Notes));
            }

            string amountString = $"{fine.Amount:N0} VNĐ";

            Border visual = BuildReceiptVisual(
                title: "BIÊN LAI THU TIỀN PHẠT",
                subTitle: "FINE PAYMENT RECEIPT",
                codeBadge: fine.FineCode,
                details: details,
                highlightBoxTitle: "TỔNG SỐ TIỀN PHẠT ĐÃ THU",
                highlightBoxContent: amountString,
                isAmountHighlight: true,
                leftSignerTitle: "NGƯỜI NỘP TIỀN",
                leftSignerName: fine.ReaderFullName,
                rightSignerTitle: "NGƯỜI THU TIỀN",
                rightSignerName: string.IsNullOrWhiteSpace(staffName) ? (string.IsNullOrWhiteSpace(fine.ResolvedByUsername) ? "Thư viện Booktibus" : fine.ResolvedByUsername) : staffName);

            return ExecutePrint(visual, $"Bien_Lai_Phat_{fine.FineCode}", out errorMessage);
        }

        private static Border BuildReceiptVisual(
            string title,
            string subTitle,
            string codeBadge,
            List<(string Label, string Value)> details,
            string highlightBoxTitle,
            string highlightBoxContent,
            bool isAmountHighlight = false,
            string leftSignerTitle = "BẠN ĐỌC",
            string leftSignerName = "",
            string rightSignerTitle = "THỦ THƯ",
            string rightSignerName = "")
        {
            Border rootBorder = new()
            {
                Background = Brushes.White,
                BorderBrush = BorderBrush,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(30, 24, 30, 24),
                Width = 520
            };
            System.Windows.Documents.TextElement.SetFontFamily(rootBorder, DefaultFont);

            StackPanel sp = new();

            // 1. BRAND HEADER & LOGO
            try
            {
                Image logo = new()
                {
                    Source = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/logo.png")),
                    Height = 44,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 0, 0, 8)
                };
                sp.Children.Add(logo);
            }
            catch
            {
                // Fallback gracefully if pack URI is unavailable
            }

            TextBlock brandTitle = new()
            {
                Text = "THƯ VIỆN BOOKTIBUS",
                FontWeight = FontWeights.Bold,
                FontSize = 16,
                Foreground = NavyBrush,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            sp.Children.Add(brandTitle);

            TextBlock brandSub = new()
            {
                Text = "Hệ thống Quản lý Thư viện Thông minh & Hiện đại",
                FontSize = 10.5,
                Foreground = MutedTextBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 2, 0, 8)
            };
            sp.Children.Add(brandSub);

            Border headerDivider = new()
            {
                Height = 1.5,
                Background = BorderBrush,
                Margin = new Thickness(0, 0, 0, 12)
            };
            sp.Children.Add(headerDivider);

            // 2. RECEIPT TITLE & CODE
            TextBlock docTitle = new()
            {
                Text = title,
                FontWeight = FontWeights.ExtraBold,
                FontSize = 15,
                Foreground = NavyBrush,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            sp.Children.Add(docTitle);

            TextBlock docSubTitle = new()
            {
                Text = $"{subTitle} • {codeBadge}",
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = MutedTextBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 2, 0, 14)
            };
            sp.Children.Add(docSubTitle);

            // 3. DETAILS GRID
            Border tableBorder = new()
            {
                Background = BoxBgBrush,
                BorderBrush = BorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(0, 0, 0, 14)
            };

            Grid grid = new();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            for (int i = 0; i < details.Count; i++)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                TextBlock lbl = new()
                {
                    Text = details[i].Label,
                    FontSize = 11.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = DarkTextBrush,
                    Margin = new Thickness(0, 3, 8, 3)
                };
                Grid.SetRow(lbl, i);
                Grid.SetColumn(lbl, 0);
                grid.Children.Add(lbl);

                TextBlock val = new()
                {
                    Text = details[i].Value,
                    FontSize = 11.5,
                    FontWeight = FontWeights.Normal,
                    Foreground = DarkTextBrush,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 3, 0, 3)
                };
                Grid.SetRow(val, i);
                Grid.SetColumn(val, 1);
                grid.Children.Add(val);
            }
            tableBorder.Child = grid;
            sp.Children.Add(tableBorder);

            // 4. HIGHLIGHT BOX (E.G. AMOUNT OR NOTICE)
            Border highlightBorder = new()
            {
                Background = isAmountHighlight ? new SolidColorBrush(Color.FromRgb(240, 253, 250)) : BoxBgBrush,
                BorderBrush = isAmountHighlight ? SuccessBrush : BorderBrush,
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(14, 10, 14, 10),
                Margin = new Thickness(0, 0, 0, 18)
            };

            StackPanel hlPanel = new();
            TextBlock hlTitle = new()
            {
                Text = highlightBoxTitle,
                FontSize = 10.5,
                FontWeight = FontWeights.Bold,
                Foreground = isAmountHighlight ? SuccessBrush : NavyBrush,
                HorizontalAlignment = isAmountHighlight ? HorizontalAlignment.Center : HorizontalAlignment.Left
            };
            hlPanel.Children.Add(hlTitle);

            TextBlock hlContent = new()
            {
                Text = highlightBoxContent,
                FontSize = isAmountHighlight ? 20 : 11,
                FontWeight = isAmountHighlight ? FontWeights.ExtraBold : FontWeights.Normal,
                Foreground = isAmountHighlight ? SuccessBrush : DarkTextBrush,
                HorizontalAlignment = isAmountHighlight ? HorizontalAlignment.Center : HorizontalAlignment.Left,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0)
            };
            hlPanel.Children.Add(hlContent);
            highlightBorder.Child = hlPanel;
            sp.Children.Add(highlightBorder);

            // 5. SIGNATURES
            Grid signGrid = new() { Margin = new Thickness(0, 10, 0, 20) };
            signGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            signGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // Left: Reader
            StackPanel leftSignPanel = new() { HorizontalAlignment = HorizontalAlignment.Center };
            leftSignPanel.Children.Add(new TextBlock
            {
                Text = leftSignerTitle,
                FontWeight = FontWeights.Bold,
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = DarkTextBrush
            });
            leftSignPanel.Children.Add(new TextBlock
            {
                Text = "(Ký và ghi rõ họ tên)",
                FontSize = 9.5,
                FontStyle = FontStyles.Italic,
                Foreground = MutedTextBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 2, 0, 45)
            });
            if (!string.IsNullOrWhiteSpace(leftSignerName))
            {
                leftSignPanel.Children.Add(new TextBlock
                {
                    Text = leftSignerName,
                    FontWeight = FontWeights.SemiBold,
                    FontSize = 11,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Foreground = DarkTextBrush
                });
            }
            Grid.SetColumn(leftSignPanel, 0);
            signGrid.Children.Add(leftSignPanel);

            // Right: Staff
            StackPanel rightSignPanel = new() { HorizontalAlignment = HorizontalAlignment.Center };
            rightSignPanel.Children.Add(new TextBlock
            {
                Text = rightSignerTitle,
                FontWeight = FontWeights.Bold,
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = DarkTextBrush
            });
            rightSignPanel.Children.Add(new TextBlock
            {
                Text = "(Ký và ghi rõ họ tên)",
                FontSize = 9.5,
                FontStyle = FontStyles.Italic,
                Foreground = MutedTextBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 2, 0, 45)
            });
            if (!string.IsNullOrWhiteSpace(rightSignerName))
            {
                rightSignPanel.Children.Add(new TextBlock
                {
                    Text = rightSignerName,
                    FontWeight = FontWeights.SemiBold,
                    FontSize = 11,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Foreground = DarkTextBrush
                });
            }
            Grid.SetColumn(rightSignPanel, 1);
            signGrid.Children.Add(rightSignPanel);

            sp.Children.Add(signGrid);

            // 6. FOOTER
            TextBlock footer = new()
            {
                Text = $"Thời gian in: {DateTime.Now:dd/MM/yyyy HH:mm:ss} • Booktibus Library Management System",
                FontSize = 9,
                Foreground = MutedTextBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 8, 0, 0)
            };
            sp.Children.Add(footer);

            rootBorder.Child = sp;
            return rootBorder;
        }

        private static bool ExecutePrint(FrameworkElement visual, string jobTitle, out string? errorMessage)
        {
            errorMessage = null;

            try
            {
                PrintDialog dialog = new();
                if (dialog.ShowDialog() == true)
                {
                    double printableWidth = dialog.PrintableAreaWidth > 100 ? dialog.PrintableAreaWidth : 793;
                    visual.Width = Math.Min(520, printableWidth);

                    visual.Measure(new Size(printableWidth, double.PositiveInfinity));
                    visual.Arrange(new Rect(new Point(0, 0), visual.DesiredSize));
                    visual.UpdateLayout();

                    dialog.PrintVisual(visual, jobTitle);
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }
        }
    }
}
