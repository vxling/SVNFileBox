#nullable enable
using System;
using Microsoft.Win32;
using Serilog;

namespace SVNFileBox.Services;

/// <summary>
/// 管理 Windows 主题设置，读写注册表，支持浅色/深色/跟随系统三种模式。
/// 切换主题时会广播 WM_SETTINGCHANGE，部分应用会自动响应。
/// </summary>
public class ThemeService
{
    private static ThemeService? _instance;
    public static ThemeService Instance => _instance ??= new ThemeService();

    private const string ThemeRegPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string AppsUseLightThemeKey = "AppsUseLightTheme";

    private string CurrentTheme { get; set; } = "system";

    public event EventHandler<string>? ThemeChanged;

    /// <summary>
    /// 应用主题设置（从配置读取后调用）
    /// </summary>
    public void ApplyTheme(string theme)
    {
        CurrentTheme = theme;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(ThemeRegPath, true);
            if (key == null)
            {
                Log.Warning("[Theme] Cannot open registry key for theme");
                return;
            }

            switch (theme)
            {
                case "light":
                    key.SetValue(AppsUseLightThemeKey, 1, RegistryValueKind.DWord);
                    Log.Information("[Theme] Applied: Light");
                    break;
                case "dark":
                    key.SetValue(AppsUseLightThemeKey, 0, RegistryValueKind.DWord);
                    Log.Information("[Theme] Applied: Dark");
                    break;
                case "system":
                    // 跟随系统：删除键值让系统自己决定
                    try { key.DeleteValue(AppsUseLightThemeKey, false); } catch { }
                    Log.Information("[Theme] Applied: System");
                    break;
            }

            // 广播 WM_SETTINGCHANGE，让其他窗口感知变化
            BroadcastSettingChange();
            ThemeChanged?.Invoke(this, theme);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Theme] Failed to apply theme: {Theme}", theme);
        }
    }

    /// <summary>
    /// 获取当前 Windows 系统实际主题
    /// </summary>
    public string GetSystemTheme()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(ThemeRegPath, false);
            if (key != null)
            {
                var value = key.GetValue(AppsUseLightThemeKey);
                if (value is int intVal)
                    return intVal == 1 ? "light" : "dark";
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Theme] Cannot read system theme");
        }
        return "light"; // 默认浅色
    }

    /// <summary>
    /// 根据配置中的主题值，解析出实际应使用的主题
    /// </summary>
    public string ResolveTheme(string configTheme)
    {
        return configTheme == "system" ? GetSystemTheme() : configTheme;
    }

    private void BroadcastSettingChange()
    {
        try
        {
            // 广播 WM_SETTINGCHANGE
            // HwndSource.FromHwnd(IntPtr.Zero)?.Invoke(...)  // 需要窗口句柄
            // 改用 P/Invoke 直接发送广播
            NativeMethods.SendMessageTimeout(
                NativeMethods.HWND_BROADCAST,
                NativeMethods.WM_SETTINGCHANGE,
                IntPtr.Zero,
                "ImmersiveColorSet",
                NativeMethods.SMTO_ABORTIFHUNG,
                5000,
                out _);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Theme] Failed to broadcast WM_SETTINGCHANGE");
        }
    }
}

// Native methods for WM_SETTINGCHANGE broadcast
internal static class NativeMethods
{
    public static readonly IntPtr HWND_BROADCAST = new IntPtr(0xffff);
    public const int WM_SETTINGCHANGE = 0x001A;
    public const int SMTO_ABORTIFHUNG = 0x0002;

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Auto)]
    public static extern IntPtr SendMessageTimeout(
        IntPtr hWnd,
        int Msg,
        IntPtr wParam,
        string lParam,
        int fuFlags,
        int uTimeout,
        out IntPtr lpdwResult);
}
