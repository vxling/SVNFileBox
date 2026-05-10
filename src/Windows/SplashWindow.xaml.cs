using System;
using System.Windows;
using System.Windows.Threading;
using Serilog;
using SVNFileBox.Services;
using SVNFileBox.Views;
using SVNFileBox.Windows;

namespace SVNFileBox.Windows;

public partial class SplashWindow : Window
{
    private readonly DispatcherTimer _fakeProgressTimer;
    private int _step;
    private int _totalSteps = 4;

    public SplashWindow()
    {
        InitializeComponent();
        _fakeProgressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _fakeProgressTimer.Tick += (_, _) =>
        {
            if (ProgressBar.Value < 90)
                ProgressBar.Value += 15;
        };
    }

    /// <summary>
    /// Call sequentially to advance through startup steps.
    /// </summary>
    public void SetStatus(string status)
    {
        StatusText.Text = status;
        _step++;
        StepText.Text = LocalizationService.Instance.GetString("SplashStep", _step, _totalSteps);
        Log.Debug("[Splash] {Status}", status);
        ProgressBar.Value = Math.Min(ProgressBar.Value + 20, 95);
        // Force UI update immediately so user sees the message
        Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
    }

    public void ShowErrorAndClose(string message)
    {
        _fakeProgressTimer.Stop();
        StatusText.Text = LocalizationService.Instance.GetString("SplashStartupFailedStatusText", message);
        StatusText.Foreground = System.Windows.Media.Brushes.Red;
        StepText.Text = LocalizationService.Instance.GetString("SplashStartupFailedStatus");
        ProgressBar.Value = 0;
        Log.Error("[Splash] Startup failed: {Message}", message);
        MsgBox.Show(
            LocalizationService.Instance.GetString("SplashStartupFailedMessage", message),
            LocalizationService.Instance.GetString("SplashStartupFailed"),
            MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
