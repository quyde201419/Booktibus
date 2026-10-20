using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Booktibus.Converters
{
    /// <summary>
    /// Converts a boolean value to Visibility.Visible or Visibility.Collapsed.
    /// </summary>
    public class BooleanToVisibilityConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            bool flag = value is bool b && b;
            if (parameter is string paramStr && string.Equals(paramStr, "Invert", StringComparison.OrdinalIgnoreCase))
            {
                flag = !flag;
            }

            return flag ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            bool isVisible = value is Visibility visibility && visibility == Visibility.Visible;
            if (parameter is string paramStr && string.Equals(paramStr, "Invert", StringComparison.OrdinalIgnoreCase))
            {
                isVisible = !isVisible;
            }

            return isVisible;
        }
    }
}

