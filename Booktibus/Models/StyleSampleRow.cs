namespace Booktibus.Models
{
    /// <summary>
    /// Represents a technical UI style specification row used to test the PremiumDataGridStyle in Stage 2.
    /// Not fake library business data.
    /// </summary>
    public class StyleSampleRow
    {
        public string SpecCode { get; init; } = string.Empty;

        public string ComponentName { get; init; } = string.Empty;

        public string ResourceKey { get; init; } = string.Empty;

        public string SupportedStates { get; init; } = string.Empty;
    }
}

