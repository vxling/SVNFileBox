#nullable enable
using System.Windows;
using System.Windows.Controls;

namespace SVNFileBox.Converters;

/// <summary>
/// Attached property to store the sort binding path on GridViewColumn,
/// since GridViewColumn itself has no Tag property.
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
}
