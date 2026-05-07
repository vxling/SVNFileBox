using System;
using System.Windows;
using System.Windows.Threading;
using Serilog;
using SVNFileBox.Views;

namespace SVNFileBox.Windows;

public partial class SplashWindow : Window
{
    private readonly DispatcherTimer _fakeProgressTimer;
    private int _step;

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
    /// Throws exceptions to abort startup.
    /// </summary>
    public void SetStatus(string status)
    {
        StatusText.Text = status;
        _step++;
        StepText.Text = $"步骤 {_step} / 4";
        Log.Debug("[Splash] {Status}", status);
        ProgressBar.Value = Math.Min(ProgressBar.Value + 20, 95);
        // Force UI update immediately so user sees the message
        Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
    }

    public void Complete()
    {
        _fakeProgressTimer.Stop();
        ProgressBar.Value = 100;
        StatusText.Text = "启动完成";
        StepText.Text = "步骤 4 / 4";
        var mainWindow = new MainWindow();
        mainWindow.Show();
        Dispatcher.Invoke(() => Close());
    }

    public void ShowErrorAndClose(string message)
    {
        _fakeProgressTimer.Stop();
        StatusText.Text = $"❌ 启动失败: {message}";
        StatusText.Foreground = System.Windows.Media.Brushes.Red;
        StepText.Text = "请查看日志或重新启动程序";
        ProgressBar.Value = 0;
        Log.Error("[Splash] Startup failed: {Message}", message);
        MessageBox.Show($"启动失败:\n\n{message}\n\n请修复后重新启动程序。",
            "SVNFileBox 启动错误", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
