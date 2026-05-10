#nullable enable
using System;
using System.Globalization;
using System.Windows.Data;
using SVNFileBox.Services;

namespace SVNFileBox.Converters;

/// <summary>
/// Converts LocalIsNewer bool to localized suggestion text for conflict resolution.
/// </summary>
public class LocalIsNewerToSuggestionConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool localIsNewer)
        {
            var key = localIsNewer ? "SuggestKeepLocal" : "SuggestAcceptServer";
            return LocalizationService.Instance.GetString(key);
        }
        return "";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
