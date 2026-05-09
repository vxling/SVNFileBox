#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SVNFileBox.Models;

public class FileCopyPlan
{
    /// <summary>All items to copy (files first, then directories in reverse for correct deletion order).</summary>
    public required IReadOnlyList<FileCopyItem> Items { get; init; }

    /// <summary>Source root path used to build the plan.</summary>
    public required string SourceRoot { get; init; }

    /// <summary>Destination root path.</summary>
    public required string DestRoot { get; init; }

    public int FileCount => Items.Count(i => i.IsFile);
    public int DirCount => Items.Count(i => i.IsDirectory);
    public long TotalBytes => Items.Sum(i => i.SizeBytes);

    /// <summary>Human-readable total size.</summary>
    public string TotalSizeDisplay => FormatBytes(TotalBytes);

    /// <summary>True when source and destination resolve to the same location.</summary>
    public bool IsSameLocation => Path.GetFullPath(SourceRoot).TrimEnd(Path.DirectorySeparatorChar).Equals(
        Path.GetFullPath(DestRoot).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

    public static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / 1024.0 / 1024.0:F1} MB";
        return $"{bytes / 1024.0 / 1024.0 / 1024.0:F2} GB";
    }
}
