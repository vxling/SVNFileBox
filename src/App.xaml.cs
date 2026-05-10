#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using Serilog;
using SVNFileBox.Services;
using SVNFileBox.Views;
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

        // 监听语言切换事件，动态更新 ResourceDictionary
        LocalizationService.Instance.LanguageChanged += OnLanguageChanged;
        UpdateLanguageResources();
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        UpdateLanguageResources();
    }

    private void UpdateLanguageResources()
    {
        try
        {
            var langDict = LocalizationService.Instance.ResourceDictionary;
            // 找到现有的语言字典并替换
            ResourceDictionary? toRemove = null;
            foreach (var d in Resources.MergedDictionaries)
            {
                if (d["SettingsTitle"] != null)
                {
                    toRemove = d;
                    break;
                }
            }
            if (toRemove != null)
                Resources.MergedDictionaries.Remove(toRemove);
            Resources.MergedDictionaries.Add(langDict);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[App] Failed to update language resources");
        }
    }

    private async void Application_Startup(object sender, StartupEventArgs e)
    {
        try
        {
            // Load config first, apply language before showing any UI
            var configService = new ConfigService();
            await configService.LoadAsync();
            LocalizationService.Instance.SetLanguage(configService.Config.Language);
            ThemeService.Instance.ApplyTheme(configService.Config.Theme);

            // Now show splash — language is already set
            _splash = new SplashWindow();
            _splash.Show();

            // Step 1: Initialize services
            _splash.SetStatus("Initializing services...");
            var syncRecordService = SyncRecordService.Instance;
            Log.Information("[Startup] Step 1 complete: Services initialized");

            // Step 2: Pre-create MainWindow (hidden)
            _splash.SetStatus("Loading main window...");
            var mainWindow = new MainWindow { Visibility = Visibility.Hidden };
            Log.Information("[Startup] Step 2 complete: MainWindow pre-created");

            // Step 3: Show main window
            mainWindow.Show();
            _splash.Close();
            Log.Information("[Startup] Step 3 complete: Main window shown");
        }
        catch (InvalidOperationException ex)
        {
            Log.Fatal(ex, "[Startup] SVN not found, aborting");
            _splash?.ShowErrorAndClose(ex.Message);
            Shutdown(1);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "[Startup] Unexpected error during startup");
            _splash?.ShowErrorAndClose(ex.Message);
            Shutdown(1);
        }
    }

    private void Application_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "[UnhandledException] {Message}", e.Exception.Message);
        MessageBox.Show(
            LocalizationService.Instance.GetString("AppUnhandledErrorMessage", e.Exception.Message),
            LocalizationService.Instance.GetString("AppUnhandledError"),
            MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
