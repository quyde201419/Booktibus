using Booktibus.Services;

namespace Booktibus.Models
{
    /// <summary>
    /// Represents a library fine item (either an officially recorded fine in dbo.Fines
    /// or a live-calculated provisional/estimated fine for an active unreturned overdue loan).
    /// </summary>
    public class FineItemModel
    {
        public int? FineId { get; init; }

        public string FineCode { get; init; } = string.Empty;

        public int BorrowRecordId { get; init; }

        public string BorrowRecordCode { get; init; } = string.Empty;

        public int UserId { get; init; }

        public string Username { get; init; } = string.Empty;

        public string ReaderFullName { get; init; } = string.Empty;

        public string ReaderCardId { get; init; } = string.Empty;

        public int BookId { get; init; }

        public string BookCode { get; init; } = string.Empty;

        public string BookTitle { get; init; } = string.Empty;

        public DateTime BorrowedAtLocal { get; init; }

        public DateTime DueAtLocal { get; init; }

        public DateTime? ReturnedAtLocal { get; init; }

        public string FineType { get; init; } = "Overdue";

        public int DaysOverdue { get; init; }

        public decimal DailyRateVnd { get; init; }

        public decimal Amount { get; init; }

        /// <summary>
        /// Values: "Estimated" (provisional unreturned overdue), "Unpaid" (officially recorded),
        /// "Waived" (waived with reason), or "Paid" (only when a verified payment exists).
        /// </summary>
        public string FineStatus { get; init; } = "Unpaid";

        public bool IsEstimatedOnly { get; init; }

        public DateTime CreatedAtLocal { get; init; }

        public DateTime? UpdatedAtLocal { get; init; }

        public DateTime? PaidAtLocal { get; init; }

        public DateTime? WaivedAtLocal { get; init; }

        public string ResolvedByUsername { get; init; } = string.Empty;

        public string Notes { get; init; } = string.Empty;

        public bool IsOfficiallyRecorded => !IsEstimatedOnly && FineId.HasValue;

        public bool CanBeConfirmed => IsEstimatedOnly && DaysOverdue > 0;

        public bool CanBeAdjustedOrWaived =>
            IsOfficiallyRecorded &&
            string.Equals(FineStatus, "Unpaid", StringComparison.OrdinalIgnoreCase);

        public string FormattedAmount => $"{Amount:N0} VNĐ";

        public string FormattedDailyRate => LocalizationService.IsVietnamese
            ? $"{DailyRateVnd:N0} VNĐ/ngày"
            : $"{DailyRateVnd:N0} VND/day";

        public string DueDateDisplay => DueAtLocal.ToString("dd/MM/yyyy");

        public string ReturnedDateDisplay => ReturnedAtLocal.HasValue
            ? ReturnedAtLocal.Value.ToString("dd/MM/yyyy HH:mm")
            : (LocalizationService.IsVietnamese ? "Chưa trả (Đang mượn)" : "Unreturned (Active Loan)");

        public string RecordNatureBadgeText => IsEstimatedOnly
            ? (LocalizationService.IsVietnamese ? "DỰ KIẾN (TẠM TÍNH)" : "ESTIMATED (PROVISIONAL)")
            : (LocalizationService.IsVietnamese ? "ĐÃ GHI NHẬN CHÍNH THỨC" : "OFFICIALLY RECORDED");

        public string StatusDisplayText
        {
            get
            {
                if (IsEstimatedOnly || string.Equals(FineStatus, "Estimated", StringComparison.OrdinalIgnoreCase))
                {
                    return LocalizationService.IsVietnamese
                        ? "Tạm tính (Đang quá hạn chưa trả)"
                        : "Estimated (Overdue Unreturned)";
                }

                return FineStatus switch
                {
                    "Unpaid" => LocalizationService.IsVietnamese
                        ? "Chưa thanh toán (Đã ghi nhận)"
                        : "Unpaid (Officially Recorded)",
                    "Waived" => LocalizationService.IsVietnamese
                        ? "Được miễn phạt"
                        : "Waived",
                    "Paid" => LocalizationService.IsVietnamese
                        ? "Đã thanh toán"
                        : "Paid",
                    _ => FineStatus
                };
            }
        }

        public string CauseExplanationText
        {
            get
            {
                if (IsEstimatedOnly)
                {
                    return LocalizationService.IsVietnamese
                        ? $"Sách chưa trả và đã quá hạn {DaysOverdue} ngày tính đến hôm nay (Hạn trả: {DueAtLocal:dd/MM/yyyy} • Mức tính: {DailyRateVnd:N0} VNĐ/ngày). Khoản phạt sẽ được chốt chính thức khi độc giả trả sách hoặc khi Thủ thư xác nhận."
                        : $"Book is unreturned and {DaysOverdue} day(s) past due date ({DueAtLocal:dd/MM/yyyy} • Rate: {DailyRateVnd:N0} VND/day). Fine will be finalized upon book return or librarian confirmation.";
                }

                if (ReturnedAtLocal.HasValue)
                {
                    return LocalizationService.IsVietnamese
                        ? $"Sách trả muộn {DaysOverdue} ngày (Hạn trả: {DueAtLocal:dd/MM/yyyy}, Ngày trả thực tế: {ReturnedAtLocal.Value:dd/MM/yyyy} • Mức áp dụng: {DailyRateVnd:N0} VNĐ/ngày)."
                        : $"Book returned {DaysOverdue} day(s) late (Due: {DueAtLocal:dd/MM/yyyy}, Returned: {ReturnedAtLocal.Value:dd/MM/yyyy} • Applied rate: {DailyRateVnd:N0} VND/day).";
                }

                return LocalizationService.IsVietnamese
                    ? $"Khoản phạt quá hạn đã được ghi nhận chính thức ({DaysOverdue} ngày quá hạn • Mức áp dụng: {DailyRateVnd:N0} VNĐ/ngày)."
                    : $"Officially recorded overdue fine ({DaysOverdue} day(s) overdue • Applied rate: {DailyRateVnd:N0} VND/day).";
            }
        }
    }

    public class FineSummaryStatsModel
    {
        public decimal CurrentDailyOverdueRateVnd { get; init; } = 5000m;

        public decimal TotalRecordedUnpaidVnd { get; init; }

        public decimal TotalEstimatedOverdueVnd { get; init; }

        public decimal TotalCombinedOutstandingVnd => TotalRecordedUnpaidVnd + TotalEstimatedOverdueVnd;

        public decimal TotalPaidVnd { get; init; }

        public decimal TotalWaivedVnd { get; init; }

        public int RecordedUnpaidCount { get; init; }

        public int EstimatedOverdueCount { get; init; }

        public int PaidCount { get; init; }

        public int WaivedCount { get; init; }
    }

    public class FineAuditLogItemModel
    {
        public int AuditLogId { get; init; }

        public int FineId { get; init; }

        public int BorrowRecordId { get; init; }

        public string ActionType { get; init; } = string.Empty;

        public decimal? PreviousAmount { get; init; }

        public decimal NewAmount { get; init; }

        public string PreviousStatus { get; init; } = string.Empty;

        public string NewStatus { get; init; } = string.Empty;

        public string Reason { get; init; } = string.Empty;

        public string PerformedByUsername { get; init; } = string.Empty;

        public DateTime CreatedAtLocal { get; init; }

        public string SummaryLine
        {
            get
            {
                string prevAmtText = PreviousAmount.HasValue ? $"{PreviousAmount.Value:N0} VNĐ -> " : string.Empty;
                string prevStText = !string.IsNullOrWhiteSpace(PreviousStatus) ? $"{PreviousStatus} -> " : string.Empty;
                return $"[{CreatedAtLocal:dd/MM/yyyy HH:mm}] {ActionType} ({PerformedByUsername}): {prevAmtText}{NewAmount:N0} VNĐ | {prevStText}{NewStatus} — {Reason}";
            }
        }
    }

    public class AdjustFineRequest
    {
        public int FineId { get; init; }

        public string NewAmountText { get; init; } = string.Empty;

        public string Reason { get; init; } = string.Empty;
    }

    public class WaiveFineRequest
    {
        public int FineId { get; init; }

        public string Reason { get; init; } = string.Empty;
    }
}

