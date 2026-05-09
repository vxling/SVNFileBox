#nullable enable
using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using SVNFileBox.Models;
using SVNFileBox.Services;

namespace SVNFileBox.Windows;

public partial class FileCopyProgressWindow : Window
{
    private readonly FileCopier _copier;
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _stopwatch = new();

    /// <summary>
    /// Raised when the user clicks Cancel. Subscribers should cancel their operations.
    /// </summary>
    public event Action? CancelRequested;

    public FileCopyProgressWindow(FileCopier copier)
    {
        _copier = copier;
        InitializeComponent();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += Timer_Tick;
    }

    public void StartAnalysis(string currentItem)
    {
        Title = "正在分析文件...";
        CurrentFileText.Text = currentItem;
        ItemIndexText.Text = "正在扫描...";
        ProgressBar.IsIndeterminate = true;
        BytesText.Text = "";
        TimeText.Text = "用时: --";
        CancelButton.IsEnabled = true;
        CancelButton.Content = "取消";
        _stopwatch.Restart();
        _timer.Start();
    }

    public void StartCopy()
    {
        Title = "正在复制文件";
        ProgressBar.IsIndeterminate = false;
        _timer.Stop();
        _stopwatch.Restart();
        _timer.Start();
    }

    public void Stop()
    {
        _stopwatch.Stop();
        _timer.Stop();
    }

    public void UpdateProgress(CopyProgress p)
    {
        CurrentFileText.Text = p.CurrentFile;
        ItemIndexText.Text = $"第 {p.CurrentIndex} 个文件，共 {p.TotalCount} 个";
        ProgressBar.Value = p.ProgressPercent;
        BytesText.Text = p.BytesDisplay;
        TimeText.Text = $"用时: {_stopwatch.Elapsed:mm\\:ss}";
    }

    public void Cancel_Click(object sender, RoutedEventArgs e)
    {
        CancelRequested?.Invoke();
        CancelButton.IsEnabled = false;
        CancelButton.Content = "正在取消...";
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        // Prevent closing while running — must click Cancel
        if (_copier.IsRunning)
        {
            e.Cancel = true;
            _copier.Cancel();
        }
        else
        {
            _timer.Stop();
        }
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        if (_stopwatch.IsRunning)
            TimeText.Text = $"用时: {_stopwatch.Elapsed:mm\\:ss}";
    }
}
