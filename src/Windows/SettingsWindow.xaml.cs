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
            SyncIntervalText.Text = $"{(int)SyncIntervalSlider.Value} 分钟";
        };
    }

    private void LoadSettings()
    {
        AutoSyncCheckBox.IsChecked = _configService.Config.AutoSyncEnabled;
        SyncIntervalSlider.Value = _configService.Config.SyncIntervalMinutes;
        SyncIntervalText.Text = $"{_configService.Config.SyncIntervalMinutes} 分钟";
        ProxyUrlBox.Text = _configService.Config.ProxyUrl;
        AutoStartCheckBox.IsChecked = _configService.Config.AutoStart;
        MinimizeToTrayCheckBox.IsChecked = _configService.Config.MinimizeToTray;

        // Theme combo
        var theme = _configService.Config.Theme;
        foreach (ComboBoxItem item in ThemeComboBox.Items)
        {
            var content = item.Content?.ToString() ?? "";
            if ((theme == "system" && content == "跟随系统") ||
                (theme == "light" && content == "浅色") ||
                (theme == "dark" && content == "深色"))
            {
                ThemeComboBox.SelectedItem = item;
                break;
            }
        }

        // Language combo
        var lang = _configService.Config.Language;
        foreach (ComboBoxItem item in LanguageComboBox.Items)
        {
            var content = item.Content?.ToString() ?? "";
            if ((lang == "auto" && content == "跟随系统") ||
                (lang == "zh" && content == "中文") ||
                (lang == "en" && content == "English"))
            {
                LanguageComboBox.SelectedItem = item;
                break;
            }
        }
    }

    private void OK_Click(object sender, RoutedEventArgs e)
    {
        _configService.Config.AutoSyncEnabled = AutoSyncCheckBox.IsChecked == true;
        _configService.Config.SyncIntervalMinutes = (int)SyncIntervalSlider.Value;
        _configService.Config.ProxyUrl = ProxyUrlBox.Text?.Trim() ?? "";
        _configService.Config.AutoStart = AutoStartCheckBox.IsChecked == true;
        _configService.Config.MinimizeToTray = MinimizeToTrayCheckBox.IsChecked == true;

        // Language
        var selectedLang = (LanguageComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "跟随系统";
        _configService.Config.Language = selectedLang switch
        {
            "中文" => "zh",
            "English" => "en",
            _ => "auto"
        };

        // Theme
        var selectedTheme = (ThemeComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "跟随系统";
        _configService.Config.Theme = selectedTheme switch
        {
            "浅色" => "light",
            "深色" => "dark",
            _ => "system"
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