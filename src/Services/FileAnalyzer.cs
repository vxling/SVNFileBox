#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SVNFileBox.Models;
using Serilog;

namespace SVNFileBox.Services;

/// <summary>
/// Phase 1: analyzes source paths and builds a flat copy plan.
/// This is a snapshot of all files/dirs to copy — executed as-is in Phase 2,
/// which avoids any issues from the destination changing during the copy itself.
/// </summary>
public class FileAnalyzer
{
    /// <summary>
    /// Analyzes source paths and produces a flat FileCopyPlan.
    /// Returns null if source equals destination.
    /// </summary>
    /// <param name="sourcePaths">Files and/or directories to copy.</param>
    /// <param name="destRoot">Destination root directory.</param>
    /// <param name="progress">Progress callback per scanned item.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public FileCopyPlan? Analyze(
        IEnumerable<string> sourcePaths,
        string destRoot,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var items = new List<FileCopyItem>();
        var sourceRoot = string.Empty;

        foreach (var sourcePath in sourcePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var fullSource = Path.GetFullPath(sourcePath);
            var isDir = Directory.Exists(fullSource);
            var isFile = File.Exists(fullSource);

            if (!isDir && !isFile) continue;

            if (string.IsNullOrEmpty(sourceRoot))
                sourceRoot = Path.GetDirectoryName(fullSource) ?? fullSource;

            if (isDir)
                CollectDirectory(fullSource, destRoot, sourceRoot, items, progress, cancellationToken);
            else
                CollectFile(fullSource, destRoot, sourceRoot, items, progress, cancellationToken);
        }

        if (items.Count == 0) return null;

        var plan = new FileCopyPlan
        {
            SourceRoot = sourceRoot,
            DestRoot = Path.GetFullPath(destRoot),
            Items = items
        };

        Log.Debug("[FileAnalyzer] Plan: {FileCount} files, {DirCount} dirs, {TotalBytes} bytes",
            plan.FileCount, plan.DirCount, plan.TotalBytes);

        return plan;
    }

    private void CollectDirectory(string dirPath, string destRoot, string sourceRoot, List<FileCopyItem> items, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var dir = new DirectoryInfo(dirPath);
        // Files first
        foreach (var file in dir.GetFiles())
        {
            cancellationToken.ThrowIfCancellationRequested();
            CollectFile(file.FullName, destRoot, sourceRoot, items, progress, cancellationToken);
        }
        // Then sub-directories recursively
        foreach (var subDir in dir.GetDirectories())
        {
            cancellationToken.ThrowIfCancellationRequested();
            CollectDirectory(subDir.FullName, destRoot, sourceRoot, items, progress, cancellationToken);
            items.Add(MakeDirItem(subDir.FullName, destRoot, sourceRoot));
        }
        // Finally add the directory itself
        items.Add(MakeDirItem(dirPath, destRoot, sourceRoot));
    }

    private void CollectFile(string filePath, string destRoot, string sourceRoot, List<FileCopyItem> items, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        items.Add(MakeFileItem(filePath, destRoot, sourceRoot));
        progress?.Report(filePath);
    }

    private static FileCopyItem MakeFileItem(string filePath, string destRoot, string sourceRoot)
    {
        var relPath = Path.GetRelativePath(sourceRoot, filePath);
        return new FileCopyItem
        {
            SourcePath = filePath,
            DestPath = Path.Combine(destRoot, relPath),
            RelativePath = relPath,
            ItemType = CopyItemType.File,
            SizeBytes = new FileInfo(filePath).Length
        };
    }

    private static FileCopyItem MakeDirItem(string dirPath, string destRoot, string sourceRoot)
    {
        var relPath = Path.GetRelativePath(sourceRoot, dirPath);
        return new FileCopyItem
        {
            SourcePath = dirPath,
            DestPath = Path.Combine(destRoot, relPath),
            RelativePath = relPath,
            ItemType = CopyItemType.Directory,
            SizeBytes = 0
        };
    }
}
