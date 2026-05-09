#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SVNFileBox.Models;
using Serilog;

namespace SVNFileBox.Services;

public class CopyProgress
{
    public required string CurrentFile { get; init; }
    public int CurrentIndex { get; init; }
    public int TotalCount { get; init; }
    public long BytesCopied { get; init; }
    public long TotalBytes { get; init; }
    public double ProgressPercent => TotalBytes > 0 ? (double)BytesCopied / TotalBytes * 100 : 0;
    public string BytesDisplay => $"{FileCopyPlan.FormatBytes(BytesCopied)} / {FileCopyPlan.FormatBytes(TotalBytes)}";
}

public class CopyResult
{
    public int CopiedCount { get; init; }
    public int SkippedCount { get; init; }
    public int OverwrittenCount { get; init; }
    public IReadOnlyList<string> SvnAddedPaths { get; init; } = [];
    public bool WasCancelled { get; init; }
    public bool HasError => ErrorMessage != null;
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Phase 2: executes the FileCopyPlan, copying files one by one with progress reporting
/// and cancellation support. After copying, adds new files to SVN and commits.
/// </summary>
public class FileCopier
{
    private readonly SvnService _svnService = new();

    /// <summary>Cancels the in-progress copy operation.</summary>
    public CancellationTokenSource? Cts { get; private set; }

    public bool IsRunning => Cts != null && !Cts.IsCancellationRequested;

    public async Task<CopyResult> CopyAsync(
        FileCopyPlan plan,
        IProgress<CopyProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        Cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = Cts.Token;

        int copied = 0, skipped = 0, overwritten = 0;
        long bytesCopied = 0;
        var svnAddedPaths = new List<string>();

        // Separate files and directories
        var files = plan.Items.Where(i => i.IsFile).ToList();
        var dirs = plan.Items.Where(i => i.IsDirectory).Reverse().ToList(); // reverse so children before parents

        Log.Information("[FileCopier] Starting copy: {FileCount} files, {DirCount} dirs to {Dest}",
            files.Count, dirs.Count, plan.DestRoot);

        try
        {
            // Create all directories first (bottom-up order already in dirs)
            foreach (var dir in dirs)
            {
                token.ThrowIfCancellationRequested();
                if (!Directory.Exists(dir.DestPath))
                {
                    try
                    {
                        Directory.CreateDirectory(dir.DestPath);
                        // Only add to SVN if it's a new directory (not already tracked)
                        if (!_svnService.IsVersioned(dir.DestPath))
                        {
                            await _svnService.AddPathAsync(dir.DestPath);
                            svnAddedPaths.Add(dir.DestPath);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "Failed to create directory: {Path}", dir.DestPath);
                        skipped++;
                    }
                }
            }

            // Copy files
            for (int i = 0; i < files.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var item = files[i];

                try
                {
                    var destExists = File.Exists(item.DestPath);
                    if (destExists) overwritten++;

                    await CopyFileWithStreamAsync(item.SourcePath, item.DestPath, token);
                    copied++;
                    bytesCopied += item.SizeBytes;

                    // svn add immediately after each file — ensures tracked even if cancelled
                    if (!_svnService.IsVersioned(item.DestPath))
                    {
                        await _svnService.AddPathAsync(item.DestPath);
                    }

                    // DEBUG: 2s delay per file to observe progress window during copy
                    Thread.Sleep(2000);

                    progress?.Report(new CopyProgress
                    {
                        CurrentFile = item.Name,
                        CurrentIndex = i + 1,
                        TotalCount = files.Count,
                        BytesCopied = bytesCopied,
                        TotalBytes = plan.TotalBytes
                    });
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Failed to copy file: {Path}", item.SourcePath);
                    skipped++;
                }
            }

            Log.Information("[FileCopier] Copy complete: {Copied} copied, {Skipped} skipped, {Overwritten} overwritten",
                copied, skipped, overwritten);
        }
        catch (OperationCanceledException)
        {
            Log.Information("[FileCopier] Copy cancelled by user");
            return new CopyResult
            {
                CopiedCount = copied,
                SkippedCount = skipped,
                OverwrittenCount = overwritten,
                SvnAddedPaths = svnAddedPaths,
                WasCancelled = true
            };
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[FileCopier] Copy failed");
            return new CopyResult
            {
                CopiedCount = copied,
                SkippedCount = skipped,
                OverwrittenCount = overwritten,
                SvnAddedPaths = svnAddedPaths,
                ErrorMessage = ex.Message
            };
        }
        finally
        {
            Cts.Dispose();
            Cts = null;
        }

        // Commit once after all files are copied
        // (directories were added during the dirs loop, files were added per-file above)
        if (copied > 0 && !token.IsCancellationRequested)
        {
            try
            {
                await _svnService.CommitAsync(plan.DestRoot, $"Auto-sync: [Add] {copied} item(s)");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "SVN commit after copy failed — files were added but not committed");
            }
        }

        return new CopyResult
        {
            CopiedCount = copied,
            SkippedCount = skipped,
            OverwrittenCount = overwritten,
            SvnAddedPaths = svnAddedPaths
        };
    }

    /// <summary>Stream-based file copy for large file support and proper cancellation.</summary>
    private static async Task CopyFileWithStreamAsync(string source, string dest, CancellationToken token)
    {
        const int bufferSize = 81920; // 80 KB buffer

        await using var sourceStream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var destStream = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);

        await sourceStream.CopyToAsync(destStream, bufferSize, token);
    }

    public void Cancel()
    {
        if (IsRunning)
        {
            Log.Information("[FileCopier] Cancellation requested");
            Cts?.Cancel();
        }
    }
}
