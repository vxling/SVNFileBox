#nullable enable
using System;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SVNFileBox.Services;
using Serilog;

namespace SVNFileBox.Windows;

public partial class SettingsWindow : Window
{
    private readonly ConfigService _configService;

    public SettingsWindow(ConfigService configService)
    {
        InitializeComponent();
        _configService = configService;
        LoadSettings();

        SyncIntervalSlider.ValueChanged += (s, e) =>
        {
            SyncIntervalText.Text = $"{(int)SyncIntervalSlider.Value} {LocalizationService.Instance.GetString("Minutes")}";
        };

        // 监听语言切换，动态刷新界面文本
        LocalizationService.Instance.LanguageChanged += OnLanguageChanged;
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        // 语言切换后刷新 SyncIntervalText（因为它是代码动态设置的）
        SyncIntervalText.Text = $"{(int)SyncIntervalSlider.Value} {LocalizationService.Instance.GetString("Minutes")}";
    }

    private void LoadSettings()
    {
        AutoSyncCheckBox.IsChecked = _configService.Config.AutoSyncEnabled;
        SyncIntervalSlider.Value = _configService.Config.SyncIntervalMinutes;
        SyncIntervalText.Text = $"{_configService.Config.SyncIntervalMinutes} {LocalizationService.Instance.GetString("Minutes")}";
        ProxyUrlBox.Text = _configService.Config.ProxyUrl;
        AutoStartCheckBox.IsChecked = _configService.Config.AutoStart;
        MinimizeToTrayCheckBox.IsChecked = _configService.Config.MinimizeToTray;

        // Theme combo — 用 SelectedIndex 直接对应配置值
        ThemeComboBox.SelectedIndex = _configService.Config.Theme switch
        {
            "light" => 1,
            "dark" => 2,
            _ => 0  // system
        };

        // Language combo — 用 SelectedIndex 直接对应配置值
        LanguageComboBox.SelectedIndex = _configService.Config.Language switch
        {
            "zh" => 1,
            "en" => 2,
            _ => 0  // auto
        };
    }

    private void OK_Click(object sender, RoutedEventArgs e)
    {
        _configService.Config.AutoSyncEnabled = AutoSyncCheckBox.IsChecked == true;
        _configService.Config.SyncIntervalMinutes = (int)SyncIntervalSlider.Value;
        _configService.Config.ProxyUrl = ProxyUrlBox.Text?.Trim() ?? "";
        _configService.Config.AutoStart = AutoStartCheckBox.IsChecked == true;
        _configService.Config.MinimizeToTray = MinimizeToTrayCheckBox.IsChecked == true;

        // Theme
        _configService.Config.Theme = ThemeComboBox.SelectedIndex switch
        {
            1 => "light",
            2 => "dark",
            _ => "system"
        };

        // Language
        _configService.Config.Language = LanguageComboBox.SelectedIndex switch
        {
            1 => "zh",
            2 => "en",
            _ => "auto"
        };

        // Apply theme
        ThemeService.Instance.ApplyTheme(_configService.Config.Theme);

        // Apply language
        LocalizationService.Instance.SetLanguage(_configService.Config.Language);

        // Auto start registration
        UpdateAutoStart(_configService.Config.AutoStart);

        _ = _configService.SaveAsync();
        Log.Information("Settings saved");
        DialogResult = true;
        Close();
    }

    private void UpdateAutoStart(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
            if (key == null) return;

            if (enable)
            {
                var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(exePath))
                    key.SetValue("SVNFileBox", $"\"{exePath}\"");
            }
            else
            {
                key.DeleteValue("SVNFileBox", false);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to update auto start registry");
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
