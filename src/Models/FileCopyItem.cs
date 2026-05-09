#nullable enable
using System;
using System.Collections.Generic;
using System.IO;

namespace SVNFileBox.Models;

public enum CopyItemType { File, Directory }

public class FileCopyItem
{
    /// <summary>Absolute source path.</summary>
    public required string SourcePath { get; init; }

    /// <summary>Absolute destination path.</summary>
    public required string DestPath { get; init; }

    /// <summary>Relative path from the copy root, used to build DestPath.</summary>
    public required string RelativePath { get; init; }

    public CopyItemType ItemType { get; init; }

    /// <summary>File size in bytes. 0 for directories.</summary>
    public long SizeBytes { get; init; }

    public string Name => Path.GetFileName(SourcePath);

    public bool IsDirectory => ItemType == CopyItemType.Directory;
    public bool IsFile => ItemType == CopyItemType.File;
}
