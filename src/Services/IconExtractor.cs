using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows;
using System.Windows.Interop;
using Serilog;

namespace SVNFileBox.Services;

/// <summary>
/// 从 Windows Shell 提取文件类型的系统图标。
/// 支持回退方案：图标提取失败时自动降级为 emoji 占位符。
/// </summary>
public static class IconExtractor
{
    // 扩展名 → Icon（可能是 ImageSource 系统图标，也可能是 string emoji）
    private static readonly Dictionary<string, object> _iconCache = new();

    // 扩展名 → 回退用的 emoji（提取失败时使用）
    private static readonly Dictionary<string, string> _fallbackEmoji = new()
    {
        { ".txt",  "📄" },
        { ".docx", "📘" },
        { ".xlsx", "📗" },
        { ".pptx", "📙" },
        { ".png",  "🖼️" },
        { ".bmp",  "🎨" },
        { ".jpg",  "🖼️" },
        { ".pdf",  "📕" },
        { ".zip",  "📦" },
        { ".rar",  "📦" },
        { ".cs",   "💻" },
        { ".xaml", "📐" },
    };

    // 已知扩展名集合（预加载目标）
    private static readonly string[] _knownExtensions = { ".txt", ".docx", ".xlsx", ".pptx", ".png", ".bmp" };

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    private const uint SHGFI_ICON = 0x100;
    private const uint SHGFI_SMALLICON = 0x1;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x10;

    /// <summary>
    /// 程序启动时调用 — 预加载所有已知文件类型的系统图标。
    /// 若提取失败则静默降级，不抛异常。
    /// </summary>
    public static void Initialize()
    {
        foreach (var ext in _knownExtensions)
            GetIcon(ext);
    }

    /// <summary>
    /// 获取指定扩展名的图标。
    /// 优先返回系统图标；若提取失败则返回 emoji 回退。
    /// 永远不会返回 null。
    /// </summary>
    public static object GetIcon(string extension)
    {
        string ext = extension.StartsWith(".") ? extension : "." + extension;

        // 1. 缓存命中
        if (_iconCache.TryGetValue(ext, out var cached))
            return cached;

        // 2. 尝试从系统提取
        var systemIcon = ExtractIcon(ext);
        if (systemIcon != null)
        {
            _iconCache[ext] = systemIcon;
            return systemIcon;
        }

        // 3. 回退到 emoji（缓存 emoji，下次直接返回，不再做系统调用）
        var emoji = _fallbackEmoji.GetValueOrDefault(ext, "📄");
        _iconCache[ext] = emoji;
        return emoji;
    }

    private static ImageSource? ExtractIcon(string extension)
    {
        IntPtr hIcon = IntPtr.Zero;
        try
        {
            // 构造临时文件路径，SHGetFileInfo 会根据扩展名查注册表图标
            string tempPath = Path.Combine(Path.GetTempPath(), $"icon_probe_{Guid.NewGuid()}{extension}");
            try
            {
                // 写入空字节，SHGFI_USEFILEATTRIBUTES 让 Shell 根据扩展名返回图标
                File.WriteAllBytes(tempPath, Array.Empty<byte>());

                var shinfo = new SHFILEINFO();
                SHGetFileInfo(
                    tempPath,
                    0,
                    ref shinfo,
                    (uint)Marshal.SizeOf(shinfo),
                    SHGFI_ICON | SHGFI_SMALLICON | SHGFI_USEFILEATTRIBUTES);

                hIcon = shinfo.hIcon;
            }
            finally
            {
                try { File.Delete(tempPath); } catch { }
            }

            if (hIcon == IntPtr.Zero) return null;

            var icon = Icon.FromHandle(hIcon);
            var imageSource = Imaging.CreateBitmapSourceFromHIcon(
                icon.Handle,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            imageSource.Freeze(); // 跨线程使用
            return imageSource;
        }
        catch (Exception ex)
        {
            Log.Warning("[IconExtractor] Failed to extract system icon for {Extension}: {Error}", extension, ex.Message);
            return null;
        }
        finally
        {
            if (hIcon != IntPtr.Zero)
                DestroyIcon(hIcon);
        }
    }
}