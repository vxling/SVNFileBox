using System;
using System.Runtime.InteropServices;
using System.Windows;
using SVNFileBox.Services;

namespace SVNFileBox;

/// <summary>
/// 监听 Windows 系统主题（深/浅色）变化，自动切换应用主题
/// </summary>
public class ThemeWatcher
{
    private static ThemeWatcher? _instance;
    public static ThemeWatcher Instance => _instance ??= new ThemeWatcher();

    public event Action<bool>? SystemThemeChanged; // true=深色, false=浅色

    private ThemeWatcher() { }

    public bool IsSystemDarkMode()
    {
        try
        {
            // 读取注册表：0=浅色, 1=深色
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("AppsUseLightTheme");
            return value is int i && i == 0; // 0=深色主题, 1=浅色主题
        }
        catch
        {
            return false; // 默认浅色
        }
    }

    public void Start()
    {
        // 监听 Windows 主题变化（通过 SystemEvents）
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    public void Stop()
    {
        Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
    }

    private void OnUserPreferenceChanged(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        if (e.Category == Microsoft.Win32.UserPreferenceCategory.General)
        {
            var isDark = IsSystemDarkMode();
            Application.Current?.Dispatcher.Invoke(() =>
            {
                // 只有当配置为 "system" 时才跟随系统主题切换
                ThemeChanged?.Invoke(isDark);
            });
        }
    }

    /// <summary>
    /// 主题变化事件，传递当前系统主题（isDark）
    /// </summary>
    public event Action<bool>? ThemeChanged;
}