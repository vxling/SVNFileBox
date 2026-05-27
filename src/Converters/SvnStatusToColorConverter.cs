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
        if (value is not FileSvnStatus status) return Brushes.Gray;

        return status switch
        {
            FileSvnStatus.Modified   => new SolidColorBrush(Color.FromRgb(0x1E, 0x88, 0xE5)), // Blue
            FileSvnStatus.Added      => new SolidColorBrush(Color.FromRgb(0x00, 0xA6, 0x50)), // Green
            FileSvnStatus.Deleted    => new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35)), // Red
            FileSvnStatus.Conflicted => new SolidColorBrush(Color.FromRgb(0xFB, 0x8C, 0x00)), // Orange
            FileSvnStatus.Unversioned => new SolidColorBrush(Color.FromRgb(0x9E, 0x9E, 0x9E)), // Gray
            FileSvnStatus.Missing    => new SolidColorBrush(Color.FromRgb(0xE6, 0x7E, 0x22)), // Brown-orange
            FileSvnStatus.Replaced    => new SolidColorBrush(Color.FromRgb(0x7B, 0x1F, 0xA2)), // Purple
            FileSvnStatus.Obstructed => new SolidColorBrush(Color.FromRgb(0xF5, 0x57, 0x00)), // Deep Orange
            FileSvnStatus.Incomplete   => new SolidColorBrush(Color.FromRgb(0xFF, 0xA0, 0x00)), // Amber - checkout in progress
            FileSvnStatus.External    => new SolidColorBrush(Color.FromRgb(0xAA, 0x00, 0xFF)), // Violet
            FileSvnStatus.Normal      => new SolidColorBrush(Color.FromRgb(0x00, 0xC8, 0x53)), // Green check
            FileSvnStatus.Hidden        => Brushes.Transparent, // No badge for parent directory row
            FileSvnStatus.TreeConflicted  => new SolidColorBrush(Color.FromRgb(0xF0, 0x00, 0x00)), // Red
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
        if (value is not FileSvnStatus status) return "";

        return status switch
        {
            FileSvnStatus.Normal      => "✓",
            FileSvnStatus.Hidden        => "",
            FileSvnStatus.Modified    => "M",
            FileSvnStatus.Added       => "A",
            FileSvnStatus.Deleted     => "D",
            FileSvnStatus.Conflicted  => "C",
            FileSvnStatus.Unversioned => "?",
            FileSvnStatus.Missing     => "!",
            FileSvnStatus.Replaced    => "R",
            FileSvnStatus.Obstructed => "~",
            FileSvnStatus.Incomplete  => "…",
            FileSvnStatus.External    => "X",
            FileSvnStatus.TreeConflicted  => "T",
            _ => ""
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
