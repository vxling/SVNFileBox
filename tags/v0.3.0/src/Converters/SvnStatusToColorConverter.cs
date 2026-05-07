using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using SVNFileBox.Models;

namespace SVNFileBox.Converters;

public class SvnStatusToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not SvnStatus status) return Brushes.Gray;

        return status switch
        {
            SvnStatus.Modified   => new SolidColorBrush(Color.FromRgb(0x1E, 0x88, 0xE5)), // Blue
            SvnStatus.Added      => new SolidColorBrush(Color.FromRgb(0x00, 0xA6, 0x50)), // Green
            SvnStatus.Deleted    => new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35)), // Red
            SvnStatus.Conflicted => new SolidColorBrush(Color.FromRgb(0xFB, 0x8C, 0x00)), // Orange
            SvnStatus.Unversioned => new SolidColorBrush(Color.FromRgb(0x9E, 0x9E, 0x9E)), // Gray
            SvnStatus.Missing    => new SolidColorBrush(Color.FromRgb(0x8E, 0x24, 0xAA)), // Purple
            SvnStatus.Normal      => new SolidColorBrush(Color.FromRgb(0x00, 0xC8, 0x53)), // Green check
            SvnStatus.Hidden        => Brushes.Transparent, // No badge for parent directory row
            _ => Brushes.Transparent
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class SvnStatusToTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not SvnStatus status) return "";

        return status switch
        {
            SvnStatus.Normal      => "✓",
            SvnStatus.Hidden        => "",
            SvnStatus.Modified    => "M",
            SvnStatus.Added       => "A",
            SvnStatus.Deleted     => "D",
            SvnStatus.Conflicted  => "C",
            SvnStatus.Unversioned => "?",
            SvnStatus.Missing     => "!",
            SvnStatus.Replaced    => "R",
            SvnStatus.Obstructed => "~",
            SvnStatus.External    => "X",
            SvnStatus.Unknown     => "I",
            _ => ""
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
