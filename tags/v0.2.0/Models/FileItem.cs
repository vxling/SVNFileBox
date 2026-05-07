using System;
using System.Collections.Generic;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SVNFileBox.Models;

public partial class FileItem : ObservableObject
{
    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private string _fullPath = "";

    [ObservableProperty]
    private bool _isDirectory;

    [ObservableProperty]
    private long _fileSize;

    [ObservableProperty]
    private DateTime _lastModified;

    [ObservableProperty]
    private SvnStatus _svnStatus = SvnStatus.Normal;

    public string FileSizeDisplay => IsDirectory ? "" : FormatFileSize(FileSize);

    public string LastModifiedDisplay => LastModified.ToString("yyyy-MM-dd HH:mm");

    public string TypeDisplay => IsDirectory ? "📁" : GetFileIcon(Name);

    private static string GetFileIcon(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".cs" => "💻",
            ".xlsx" or ".xls" => "📊",
            ".docx" or ".doc" => "📝",
            ".pptx" or ".ppt" => "📽️",
            ".pdf" => "📕",
            ".txt" => "📄",
            ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" => "🖼️",
            ".zip" or ".rar" or ".7z" => "📦",
            ".json" or ".xml" or ".yaml" or ".yml" => "📋",
            ".html" or ".css" or ".js" => "🌐",
            _ => "📄"
        };
    }

    private static string FormatFileSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
    }
}