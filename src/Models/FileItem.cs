using System;
using System.Collections.Generic;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using SVNFileBox.Services;

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
    private bool _isSelected;

    [ObservableProperty]
    private FileSvnStatus _svnStatus = FileSvnStatus.Normal;

    public string FileSizeDisplay => IsDirectory ? "" : FormatFileSize(FileSize);

    public string LastModifiedDisplay => LastModified == DateTime.MinValue ? "" : LastModified.ToString("yyyy-MM-dd HH:mm");

    public string TypeDisplay
    {
        get
        {
            var L = LocalizationService.Instance;
            if (IsParentDirectory) return "";
            if (IsDirectory) return L.GetString("FileType_Folder");
            return GetFileIcon(Name);
        }
    }

    public bool IsParentDirectory { get; set; }

    public int SortGroup => IsParentDirectory ? 0 : 1;

    private static string GetFileIcon(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        var L = LocalizationService.Instance;
        return ext switch
        {
            ".cs" or ".fs" or ".vb" or ".java" or ".py" or ".go" or ".rs" or ".c" or ".cpp" or ".h" or ".hpp" => L.GetString("FileType_Code"),
            ".xlsx" or ".xls" or ".xlsm" or ".csv" => L.GetString("FileType_Excel"),
            ".docx" or ".doc" or ".odt" or ".rtf" => L.GetString("FileType_Word"),
            ".pptx" or ".ppt" or ".odp" => L.GetString("FileType_PPT"),
            ".pdf" => L.GetString("FileType_PDF"),
            ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".webp" or ".ico" or ".svg" or ".tiff" => L.GetString("FileType_Image"),
            ".mp4" or ".avi" or ".mkv" or ".mov" or ".wmv" or ".flv" or ".webm" => L.GetString("FileType_Video"),
            ".mp3" or ".wav" or ".flac" or ".aac" or ".ogg" or ".wma" => L.GetString("FileType_Audio"),
            ".zip" or ".rar" or ".7z" or ".tar" or ".gz" or ".bz2" => L.GetString("FileType_Archive"),
            ".txt" or ".md" or ".log" or ".ini" or ".cfg" or ".conf" => L.GetString("FileType_Text"),
            ".json" or ".xml" or ".yaml" or ".yml" or ".toml" or ".ini" => L.GetString("FileType_Config"),
            ".html" or ".htm" or ".css" or ".js" or ".ts" or ".jsx" or ".tsx" or ".vue" or ".sass" or ".scss" => L.GetString("FileType_Web"),
            ".exe" or ".msi" or ".dll" or ".sys" or ".bat" or ".cmd" or ".ps1" => L.GetString("FileType_Executable"),
            _ => L.GetString("FileType_Document")
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