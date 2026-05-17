#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Serilog;
using SVNFileBox.Services;
using SVNFileBox.Views;
using SVNFileBox.Windows;

namespace SVNFileBox;

public partial class App : Application
{
    private static readonly Mutex _instanceMutex = new(true, @"Global\SVNFileBox_SingleInstance");
    private SplashWindow? _splash;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Single-instance check: if another instance is already running, show error and exit
        if (!_instanceMutex.WaitOne(TimeSpan.Zero))
        {
            MessageBox.Show(
                "SVNFileBox is already running.\n\nIf the main window is hidden, check the system tray.",
                "SVNFileBox - Already Running",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Log.CloseAndFlush();
            Shutdown(1);
            return;
        }

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

            // Pre-create MainWindow before showing splash
            var mainWindow = new MainWindow { Visibility = Visibility.Hidden };

            // Now show splash — language/theme are already set
            _splash = new SplashWindow();
            _splash.Show();

            // Step 1: Initialize services
            _splash.SetStatus("Initializing services...");
            var syncRecordService = SyncRecordService.Instance;
            Log.Information("[Startup] Step 1 complete: Services initialized");

            // Step 2: Load system file type icons (fallback to emoji on failure)
            _splash.SetStatus("Loading resources...");
            IconExtractor.Initialize();
            Log.Information("[Startup] Step 2 complete: IconExtractor initialized");

            // Step 3: Show main window
            _splash.SetStatus("Loading main window...");
            mainWindow.Show();
            _splash.Close();

            // Auto-start: hide main window immediately, keep running in tray
            // Only minimize if launched with --autostart (i.e., from Windows startup),
            // NOT when the user manually launches the app from Explorer/Start Menu.
            bool isAutoStart = e.Args.Contains("--autostart", StringComparer.OrdinalIgnoreCase);
            if (isAutoStart && configService.Config.AutoStart && configService.Config.AutoStartMinimize)
            {
                mainWindow.WindowState = WindowState.Minimized;
                mainWindow.Hide();
                Log.Information("[Startup] Auto-start from Windows startup: main window hidden, running in tray");
            }

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
            MsgBox.Show(
            null!, // no owner during app crash
            LocalizationService.Instance.GetString("AppUnhandledErrorMessage", e.Exception.Message),
            LocalizationService.Instance.GetString("AppUnhandledError"),
            MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
