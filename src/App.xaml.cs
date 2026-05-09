#nullable enable
using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Serilog;
using SVNFileBox.Services;
using SVNFileBox.Windows;

namespace SVNFileBox;

public partial class App : Application
{
    private SplashWindow? _splash;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SVNFileBox", "logs");
        Directory.CreateDirectory(logDir);
        var logPath = Path.Combine(logDir, "svnfilebox.log");
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(logPath, rollingInterval: RollingInterval.Day)
            .CreateLogger();
        Log.Information("SVNFileBox started");
    }

    private async void Application_Startup(object sender, StartupEventArgs e)
    {
        _splash = new SplashWindow();
        _splash.Show();

        try
        {
            // Step 1: SVN 环境检查（已跳过，SharpSvn 为纯托管库，无需外部依赖）
            _splash.SetStatus("正在检查系统环境...");

            // Step 2: Load config
            _splash.SetStatus("正在加载配置...");
            var configService = new ConfigService();
            await configService.LoadAsync();
            Log.Information("[Startup] Step 2 complete: Config loaded ({Count} repos)", configService.Config.Repositories.Count);

            // Step 3: Initialize sync service and pre-load repo data
            _splash.SetStatus("正在加载仓库信息...");
            var syncRecordService = SyncRecordService.Instance;
            Log.Information("[Startup] Step 3 complete: SyncRecordService ready");

            // Step 4: Show main window
            _splash.SetStatus("正在显示主窗口...");
            _splash.Complete();
            Log.Information("[Startup] Step 4 complete: Main window shown");
        }
        catch (InvalidOperationException ex)
        {
            // SVN not found — fatal, abort startup
            Log.Fatal(ex, "[Startup] SVN not found, aborting");
            _splash.ShowErrorAndClose(ex.Message);
            Shutdown(1);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "[Startup] Unexpected error during startup");
            _splash.ShowErrorAndClose(ex.Message);
            Shutdown(1);
        }
    }

    private void Application_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "[UnhandledException] {Message}", e.Exception.Message);
        MessageBox.Show($"发生未处理的错误:\n\n{e.Exception.Message}\n\n程序将继续运行。",
            "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
