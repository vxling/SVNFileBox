#nullable enable

using System;
using System.Windows;
using Microsoft.Win32;
using Serilog;

namespace SVNFileBox.Services;

/// <summary>
/// 管理 SVNFileBox 的主题偏好设置和 Win11 主题资源动态切换。
///
/// 主题资源（Win11LightTheme.xaml / Win11DarkTheme.xaml）动态替换 App.Current.Resources.MergedDictionaries。
/// </summary>
public class ThemeService
{
    private static ThemeService? _instance;
    public static ThemeService Instance => _instance ??= new ThemeService();

    /// <summary>当前 app 偏好的主题（light / dark / system）</summary>
    private string _currentTheme = "system";

    /// <summary>当前已加载的主题文件名</summary>
    private string _loadedThemeFile = "";

    public event EventHandler<string>? ThemeChanged;

    public ThemeService()
    {
        // 监听系统主题变化，只有在 "system" 模式时才响应
        ThemeWatcher.Instance.ThemeChanged += isDarkMode =>
        {
            if (_currentTheme == "system")
            {
                LoadWin11Theme("system");
            }
        };
    }

    /// <summary>
    /// 应用主题偏好，动态替换 Win11 主题资源字典
    /// </summary>
    public void ApplyTheme(string theme)
    {
        _currentTheme = theme;
        Log.Information("[Theme] Preference set to: {Theme}", theme);
        LoadWin11Theme(theme);
        ThemeChanged?.Invoke(this, theme);
    }

    /// <summary>
    /// 动态加载 Win11 主题 ResourceDictionary 到 App.Current.Resources
    /// </summary>
    private void LoadWin11Theme(string configTheme)
    {
        try
        {
            var resolved = ResolveTheme(configTheme);
            var fileName = resolved == "dark" ? "Win11DarkTheme.xaml" : "Win11LightTheme.xaml";

            if (_loadedThemeFile == fileName) return;

            var dict = new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/SVNFileBox;component/Themes/{fileName}")
            };

            // 移除旧的 Win11 主题字典
            ResourceDictionary? toRemove = null;
            foreach (var d in Application.Current.Resources.MergedDictionaries)
            {
                if (d.Source?.OriginalString.Contains("Win11") == true)
                {
                    toRemove = d;
                    break;
                }
            }
            if (toRemove != null)
                Application.Current.Resources.MergedDictionaries.Remove(toRemove);

            // 添加新主题字典（插入到最前面，让它优先于 Fluent 主题）
            Application.Current.Resources.MergedDictionaries.Insert(0, dict);
            _loadedThemeFile = fileName;

            Log.Information("[Theme] Win11 theme loaded: {FileName}", fileName);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Theme] Failed to load Win11 theme");
        }
    }

    /// <summary>
    /// 获取当前 Windows 系统实际主题
    /// </summary>
    public string GetSystemTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize", false);
            if (key != null)
            {
                var value = key.GetValue("AppsUseLightTheme");
                if (value is int intVal)
                    return intVal == 1 ? "light" : "dark";
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Theme] Cannot read system theme");
        }
        return "light";
    }

    /// <summary>
    /// 根据配置中的主题值，解析出实际应使用的主题
    /// </summary>
    public string ResolveTheme(string configTheme)
    {
        return configTheme == "system" ? GetSystemTheme() : configTheme;
    }
}