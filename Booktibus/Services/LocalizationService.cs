using System.Windows;

namespace Booktibus.Services
{
    /// <summary>
    /// Manages runtime switching between Vietnamese (vi) and English (en) ResourceDictionaries.
    /// </summary>
    public static class LocalizationService
    {
        private const string VietnameseDictionaryPath = "Resources/Strings.vi.xaml";
        private const string EnglishDictionaryPath = "Resources/Strings.en.xaml";

        public static string CurrentLanguageCode { get; private set; } = "vi";

        public static bool IsVietnamese => string.Equals(CurrentLanguageCode, "vi", StringComparison.OrdinalIgnoreCase);

        public static event EventHandler? LanguageChanged;

        /// <summary>
        /// Switches the active localization ResourceDictionary in Application.Current.Resources.
        /// </summary>
        /// <param name="languageCode">"vi" for Vietnamese, "en" for English.</param>
        public static void SetLanguage(string languageCode)
        {
            string normalizedCode = string.Equals(languageCode, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "vi";
            string targetPath = normalizedCode == "en" ? EnglishDictionaryPath : VietnameseDictionaryPath;

            if (Application.Current?.Resources?.MergedDictionaries is not { } mergedDictionaries)
            {
                CurrentLanguageCode = normalizedCode;
                return;
            }

            ResourceDictionary newLanguageDictionary = new()
            {
                Source = new Uri(targetPath, UriKind.Relative)
            };

            ResourceDictionary? existingLanguageDictionary = mergedDictionaries.FirstOrDefault(
                d => d.Source != null &&
                     (d.Source.OriginalString.EndsWith("Strings.vi.xaml", StringComparison.OrdinalIgnoreCase) ||
                      d.Source.OriginalString.EndsWith("Strings.en.xaml", StringComparison.OrdinalIgnoreCase)));

            if (existingLanguageDictionary != null)
            {
                int index = mergedDictionaries.IndexOf(existingLanguageDictionary);
                mergedDictionaries[index] = newLanguageDictionary;
            }
            else
            {
                mergedDictionaries.Add(newLanguageDictionary);
            }

            CurrentLanguageCode = normalizedCode;
            LanguageChanged?.Invoke(null, EventArgs.Empty);
        }

        /// <summary>
        /// Toggles between Vietnamese and English.
        /// </summary>
        public static void ToggleLanguage()
        {
            SetLanguage(IsVietnamese ? "en" : "vi");
        }

        /// <summary>
        /// Retrieves a localized string resource by key, with a fallback value.
        /// </summary>
        public static string GetString(string resourceKey, string fallback = "")
        {
            if (Application.Current?.TryFindResource(resourceKey) is string value)
            {
                return value;
            }

            return fallback;
        }
    }
}

