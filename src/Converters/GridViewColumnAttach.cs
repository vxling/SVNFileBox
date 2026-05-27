#nullable enable
using System.Windows;
using System.Windows.Controls;

namespace SVNFileBox.Converters;

/// <summary>
/// Attached properties for GridViewColumn behavior.
/// IsFillColumn: marks a column to auto-stretch and fill remaining width
/// </summary>
public static class GridViewColumnAttach
{
    public static readonly DependencyProperty SortPropertyPathProperty =
        DependencyProperty.RegisterAttached(
            "SortPropertyPath",
            typeof(string),
            typeof(GridViewColumnAttach),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.Inherits));

    public static void SetSortPropertyPath(DependencyObject element, string? value)
        => element.SetValue(SortPropertyPathProperty, value);

    public static string? GetSortPropertyPath(DependencyObject element)
        => element.GetValue(SortPropertyPathProperty) as string;

    public static readonly DependencyProperty IsFillColumnProperty =
        DependencyProperty.RegisterAttached(
            "IsFillColumn",
            typeof(bool),
            typeof(GridViewColumnAttach),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    public static void SetIsFillColumn(DependencyObject element, bool value)
        => element.SetValue(IsFillColumnProperty, value);

    public static bool GetIsFillColumn(DependencyObject element)
        => (bool)element.GetValue(IsFillColumnProperty);
}
