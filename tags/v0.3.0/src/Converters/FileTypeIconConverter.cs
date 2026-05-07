#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using SVNFileBox.Models;

namespace SVNFileBox.Converters;

/// <summary>
/// Returns a color icon image for files based on their extension.
/// Directories use a folder icon. Falls back to a generic file icon for unknown types.
/// Icons are loaded from Assets/Icons/*.png pack URIs.
/// </summary>
public class FileTypeIconConverter : IValueConverter
{
    private static readonly Dictionary<string, BitmapSource> _iconCache = new(StringComparer.OrdinalIgnoreCase);
    private static BitmapSource? _folderIcon;
    private static BitmapSource? _genericFileIcon;

    private static readonly Dictionary<string, string> ExtensionToIcon = new(StringComparer.OrdinalIgnoreCase)
    {
        // Office
        { ".docx", "word" },
        { ".doc",  "word" },
        { ".xlsx", "excel" },
        { ".xls",  "excel" },
        { ".csv",  "excel" },
        { ".pptx", "ppt" },
        { ".ppt",  "ppt" },
        // PDF
        { ".pdf",  "pdf" },
        // Text & Images
        { ".txt",  "txt" },
        { ".png",  "image" },
        { ".jpg",  "image" },
        { ".jpeg", "image" },
        { ".gif",  "image" },
        { ".bmp",  "image" },
        { ".webp", "image" },
        { ".ico",  "image" },
        { ".svg",  "image" },
        // Archive
        { ".zip",  "zip" },
        { ".rar",  "zip" },
        { ".7z",   "zip" },
        { ".tar",  "zip" },
        { ".gz",   "zip" },
        // Data
        { ".json", "json" },
        { ".xml",  "json" },
        { ".yaml", "json" },
        { ".yml",  "json" },
        // Web & Code
        { ".html", "html" },
        { ".htm",  "html" },
        { ".css",  "html" },
        { ".js",   "html" },
        { ".cs",   "html" },
    };

    private static BitmapSource? LoadIcon(string name)
    {
        var uri = $"pack://application:,,,/Assets/Icons/{name}.png";
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(uri);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not FileItem item)
            return null;

        // Parent directory navigation item (".." / "返回上级目录")
        if (item.Name == ".." || item.Name == "返回上级目录")
        {
            var key = "__parent__";
            if (!_iconCache.TryGetValue(key, out var backIcon))
            {
                backIcon = CreateBackArrowIcon();
                _iconCache[key] = backIcon;
            }
            return backIcon;
        }

        // Directory → folder icon
        if (item.IsDirectory || (!string.IsNullOrEmpty(item.FullPath) && Directory.Exists(item.FullPath)))
        {
            if (_folderIcon == null)
                _folderIcon = LoadIcon("folder") ?? CreateFallbackFolderIcon();
            return _folderIcon;
        }

        // Try to get file-type-specific icon
        var ext = Path.GetExtension(item.Name);
        if (!string.IsNullOrEmpty(ext) && ExtensionToIcon.TryGetValue(ext, out var iconName))
        {
            if (!_iconCache.TryGetValue(iconName, out var icon))
            {
                icon = LoadIcon(iconName) ?? CreateGenericFileIcon();
                _iconCache[iconName] = icon;
            }
            return icon;
        }

        // Fallback: generic file icon
        if (_genericFileIcon == null)
            _genericFileIcon = CreateGenericFileIcon();
        return _genericFileIcon;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();

    private static BitmapSource CreateBackArrowIcon()
    {
        var disp = System.Windows.Application.Current?.Dispatcher;
        if (disp != null && !disp.CheckAccess())
            return (BitmapSource)disp.Invoke(() => CreateBackArrowIconCore());
        return CreateBackArrowIconCore();
    }

    private static BitmapSource CreateBackArrowIconCore()
    {
        var bitmap = new BitmapImage();
        // Use pack URI like LoadIcon, works in VS, build output, and published single-file
        var uri = new Uri("pack://application:,,,/Assets/Icons/parent_dir.png");
        try
        {
            bitmap.BeginInit();
            bitmap.UriSource = uri;
            bitmap.DecodePixelWidth = 24;
            bitmap.DecodePixelHeight = 24;
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            // Fallback: draw with System.Drawing
        }
        using var bmp = new System.Drawing.Bitmap(20, 20);
        using var g = System.Drawing.Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(System.Drawing.Color.Transparent);
        using var pen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(255, 80, 80, 80), 1.5f);
        g.DrawLine(pen, 14, 10, 6, 10);
        g.DrawLine(pen, 8, 7, 6, 10);
        g.DrawLine(pen, 8, 13, 6, 10);
        g.DrawLine(pen, 10, 14, 10, 6);
        g.DrawLine(pen, 7, 8, 10, 6);
        g.DrawLine(pen, 13, 8, 10, 6);
        var ms = new System.IO.MemoryStream();
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        ms.Seek(0, System.IO.SeekOrigin.Begin);
        bitmap.BeginInit();
        bitmap.StreamSource = ms;
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    // Wraps System.Drawing calls in a Dispatcher invoke when called from a non-UI thread.
    // System.Drawing.Graphics requires the UI thread to function safely.
    private static BitmapSource CreateGenericFileIcon()
    {
        var disp = System.Windows.Application.Current?.Dispatcher;
        if (disp != null && !disp.CheckAccess())
            return (BitmapSource)disp.Invoke(() => CreateGenericFileIconCore());
        return CreateGenericFileIconCore();
    }

    private static BitmapSource CreateGenericFileIconCore()
    {
        using var bmp = new System.Drawing.Bitmap(20, 20);
        using var g = System.Drawing.Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var brush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(255, 100, 100, 100));
        using var pen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(255, 80, 80, 80), 1f);
        g.FillRectangle(brush, 3, 2, 14, 16);
        g.DrawRectangle(pen, 3, 2, 14, 16);
        g.FillPolygon(System.Drawing.Brushes.White, new[]
        {
            new System.Drawing.Point(11, 2),
            new System.Drawing.Point(17, 2),
            new System.Drawing.Point(17, 8)
        });
        g.DrawLine(System.Drawing.Pens.White, 6, 10, 14, 10);
        g.DrawLine(System.Drawing.Pens.White, 6, 13, 14, 13);

        using var ms = new MemoryStream();
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        ms.Position = 0;

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = ms;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource CreateFallbackFolderIcon()
    {
        var disp = System.Windows.Application.Current?.Dispatcher;
        if (disp != null && !disp.CheckAccess())
            return (BitmapSource)disp.Invoke(() => CreateFallbackFolderIconCore());
        return CreateFallbackFolderIconCore();
    }

    private static BitmapSource CreateFallbackFolderIconCore()
    {
        using var bmp = new System.Drawing.Bitmap(20, 20);
        using var g = System.Drawing.Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var brush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(255, 255, 193, 7));
        using var pen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(255, 255, 152, 0), 1.5f);
        g.FillRectangle(brush, 2, 6, 8, 3);
        g.FillRectangle(brush, 2, 8, 16, 10);
        g.DrawRectangle(pen, 2, 8, 16, 10);

        using var ms = new MemoryStream();
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        ms.Position = 0;

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = ms;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }
}
