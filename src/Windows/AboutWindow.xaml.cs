#nullable enable
using System;
using System.Windows;
using SVNFileBox.Services;

namespace SVNFileBox.Windows;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        LocalizationService.Instance.LanguageChanged += OnLanguageChanged;
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        // DynamicResource handles window title automatically via XAML binding.
        // Content controls that are not dynamically bound would need manual update here.
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
