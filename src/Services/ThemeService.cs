#nullable enable

using System;
using Microsoft.Win32;
using Serilog;

namespace SVNFileBox.Services;

/// <summary>
/// 管理 SVNFileBox 的主题偏好设置。
///
/// 注意：本类只负责读写 app 自己的配置，不操作 OS 注册表。
/// 主题的实际呈现由 PresentationFramework.Fluent 决定（跟随 OS 主题）。
/// 若需要 app 自己的浅色/深色主题切换而不影响 OS，需自定义资源字典替换。
/// </summary>
public class ThemeService
{
    private static ThemeService? _instance;
    public static ThemeService Instance => _instance ??= new ThemeService();

    /// <summary>当前 app 偏好的主题（light / dark / system）</summary>
    private string _currentTheme = "system";

    public event EventHandler<string>? ThemeChanged;

    /// <summary>
    /// 应用主题偏好（仅记录到内存，不写 OS 注册表）
    /// </summary>
    public void ApplyTheme(string theme)
    {
        _currentTheme = theme;
        Log.Information("[Theme] Preference set to: {Theme}", theme);
        ThemeChanged?.Invoke(this, theme);
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
