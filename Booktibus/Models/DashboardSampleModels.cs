namespace Booktibus.Models
{
    /// <summary>
    /// Represents a statistic summary card on the Dashboard.
    /// Explicitly tagged as sample layout data in Stage 3 (before database connection).
    /// </summary>
    public class DashboardMetricCardModel
    {
        public string Title { get; init; } = string.Empty;

        public string SampleValue { get; init; } = string.Empty;

        public string SampleTag { get; init; } = string.Empty;

        public string Subtitle { get; init; } = string.Empty;

        public string AccentHex { get; init; } = "#14284B";
    }

    /// <summary>
    /// Represents an illustrative activity entry in the Recent Activities table on the Dashboard.
    /// Explicitly marked as sample data to avoid presenting mock records as real SQL Server data.
    /// </summary>
    public class SampleActivityRowModel
    {
        public string ReferenceCode { get; init; } = string.Empty;

        public string ActivityType { get; init; } = string.Empty;

        public string SubjectSummary { get; init; } = string.Empty;

        public string ActorDisplay { get; init; } = string.Empty;

        public string TimeDisplay { get; init; } = string.Empty;

        public string DataStatusBadge { get; init; } = string.Empty;
    }

    /// <summary>
    /// Represents a notice item in the Dashboard notifications panel.
    /// </summary>
    public class SystemNoticeItemModel
    {
        public string CategoryBadge { get; init; } = string.Empty;

        public string Title { get; init; } = string.Empty;

        public string Description { get; init; } = string.Empty;

        public string BannerType { get; init; } = "Info";
    }
}

