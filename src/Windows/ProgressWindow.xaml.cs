#nullable enable
using System;
using System.Windows;
using System.Windows.Controls;

namespace SVNFileBox.Windows;

public partial class ProgressWindow : Window
{
    /// <summary>
    /// Raised when the user clicks Cancel. Subscribers should cancel their operations.
    /// The window is NOT closed automatically — the caller decides when to close it.
    /// </summary>
    public event EventHandler? CancelRequested;

    public ProgressWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Update progress bar and optional status/title text.
    /// </summary>
    /// <param name="progress">0–100</param>
    /// <param name="statusText">Optional. Shown above the progress bar. Pass null to leave unchanged.</param>
    /// <param name="title">Optional. Updates the window title. Pass null to leave unchanged.</param>
    public void UpdateProgress(double progress, string? statusText = null, string? title = null)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => UpdateProgress(progress, statusText, title));
            return;
        }

        ProgressBar.Value = progress;

        if (statusText != null)
        {
            StatusText.Text = statusText;
            StatusText.Visibility = Visibility.Visible;
        }

        if (title != null)
            Title = title;
    }

    /// <summary>
    /// Enable or disable the Cancel button.
    /// </summary>
    public bool CanCancel
    {
        get => CancelButton.Visibility == Visibility.Visible;
        set => CancelButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Set the window title (initial value, before any UpdateProgress call).
    /// </summary>
    public void SetTitle(string title) => Title = title;

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        CancelButton.IsEnabled = false;
        CancelButton.Content = "正在取消...";
        CancelRequested?.Invoke(this, EventArgs.Empty);
    }
}
