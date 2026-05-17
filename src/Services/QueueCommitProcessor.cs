#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace SVNFileBox.Services;

/// <summary>
/// Background processor that consumes the PendingCommitQueue and performs
/// batch SVN commits at regular intervals.
///
/// Trigger conditions (any one):
///   1. Timer fires (default: every 30 seconds)
///   2. Queue reaches minimum batch size (default: 5 items)
///   3. SyncNow() is called by the user
///
/// Execution flow:
///   Resolve() → Execute SVN commands → Commit → MarkCommitted
///   On failure → MarkFailed → retry via existing RetryPendingUpdatesAsync mechanism
/// </summary>
public class QueueCommitProcessor : IDisposable
{
    private readonly SvnService _svnService;
    private readonly System.Timers.Timer _timer;
    private readonly int _intervalMs;
    private readonly int _minBatchSize;
    private int _isRunning;

    /// <summary>Fired after a batch commit completes (success or failure).</summary>
    public event EventHandler<BatchCommitResult>? BatchCompleted;

    /// <summary>
    /// Fired when a batch commit fails. Failed items are still in the queue (via MarkFailed)
    /// and are also forwarded here so SyncService can add them to its _pendingUpdates retry pool.
    /// </summary>
    public event EventHandler<IReadOnlyList<PendingCommitItem>>? BatchFailed;

    public QueueCommitProcessor(SvnService svnService, int intervalSeconds = 60, int minBatchSize = 5)
    {
        _svnService = svnService;
        _intervalMs = intervalSeconds * 1000;
        _minBatchSize = minBatchSize;

        _timer = new System.Timers.Timer(_intervalMs);
        _timer.Elapsed += OnTimerElapsed;
        _timer.AutoReset = true;

        Log.Information("[QueueCommitProcessor] Created, interval={Interval}s, minBatch={MinBatch}",
            intervalSeconds, minBatchSize);
    }

    public void Start()
    {
        _timer.Start();
        Log.Debug("[QueueCommitProcessor] Timer started");
    }

    public void Stop()
    {
        _timer.Stop();
        Log.Debug("[QueueCommitProcessor] Timer stopped");
    }

    private void OnTimerElapsed(object? sender, System.Timers.ElapsedEventArgs e)
    {
        // Timer always processes the queue regardless of size —
        // otherwise the timer would silently skip commits when the queue is small.
        _ = ProcessQueueAsync();
    }

    /// <summary>
    /// Triggers an immediate queue flush. Safe to call from UI or other threads.
    /// Returns a Task that completes when the batch commit finishes.
    /// </summary>
    public async Task SyncNowAsync()
    {
        Log.Debug("[QueueCommitProcessor] SyncNow requested");
        // Always flush on explicit user request — ignore minBatchSize
        await ProcessQueueAsync(forceCommit: true);
    }

    /// <summary>
    /// Triggers an immediate queue flush (fire-and-forget, no await).
    /// </summary>
    public void SyncNow()
    {
        Log.Debug("[QueueCommitProcessor] SyncNow (fire-and-forget) requested");
        // Always flush on explicit user request — ignore minBatchSize
        _ = ProcessQueueAsync(forceCommit: true);
    }

    /// <summary>
    /// Main entry point: resolves the queue and executes a batch commit.
    /// </summary>
    /// <param name="forceCommit">If true, commits regardless of queue size (user-triggered SyncNow).</param>
    public async Task ProcessQueueAsync(bool forceCommit = false)
    {
        if (Interlocked.CompareExchange(ref _isRunning, 1, 0) == 1)
        {
            Log.Debug("[QueueCommitProcessor] Already running, skipping");
            return;
        }

        try
        {
            var queue = PendingCommitQueue.Instance;
            if (queue.Count == 0)
            {
                Log.Debug("[QueueCommitProcessor] Queue is empty, nothing to do");
                return;
            }

            // Timer-triggered: only skip if queue is completely empty.
            // minBatchSize is a batching hint — timer fires whenever there's any pending work.
            // SyncNow (forceCommit=true): always flush regardless of size.
            if (!forceCommit && queue.Count == 0)
            {
                Log.Debug("[QueueCommitProcessor] Queue is empty, nothing to do");
                return;
            }

            var resolved = queue.Resolve();
            if (resolved.Count == 0)
            {
                Log.Debug("[QueueCommitProcessor] Resolve() returned empty, skipping");
                return;
            }

            Log.Information("[QueueCommitProcessor] Processing batch: {Count} items", resolved.Count);

            var result = await ExecuteBatchCommitAsync(resolved);

            if (result.Success)
            {
                queue.MarkCommitted(resolved);
                queue.Prune();
                Log.Information("[QueueCommitProcessor] Batch committed successfully: {Count} items, revision {Revision}",
                    resolved.Count, result.Revision);
            }
            else
            {
                queue.MarkFailed(resolved);
                Log.Warning("[QueueCommitProcessor] Batch commit failed: {Error}", result.ErrorMessage);

                // Bridge failed items to SyncService's retry pool (RetryPendingUpdatesAsync)
                BatchFailed?.Invoke(this, resolved.ToList());
            }

            BatchCompleted?.Invoke(this, result);
        }
        finally
        {
            Interlocked.Exchange(ref _isRunning, 0);
        }
    }

    private async Task<BatchCommitResult> ExecuteBatchCommitAsync(List<PendingCommitItem> allItems)
    {
        var result = new BatchCommitResult();

        try
        {
            var sorted = TopologicalSort(allItems);

            // If only 1 item, commit it individually (per user request)
            if (sorted.Count == 1)
            {
                var itemResult = await CommitSingleItemAsync(sorted[0]);
                result.Success = itemResult;
                result.ErrorMessage = itemResult ? null : "Single item commit failed";
                result.Revision = itemResult ? "ok" : null;
                result.ItemsCount = 1;
                return result;
            }

            // Partition into chunks of 5; each chunk is committed independently
            const int chunkSize = 5;
            var chunks = sorted
                .Select((item, index) => new { item, index })
                .GroupBy(x => x.index / chunkSize)
                .Select(g => g.Select(x => x.item).ToList())
                .ToList();

            var failedChunks = new List<string>();
            var totalCommitted = 0;

            foreach (var chunk in chunks)
            {
                try
                {
                    var chunkResult = await ExecuteChunkAsync(chunk);
                    if (chunkResult.Success)
                        totalCommitted += chunk.Count;
                    else
                        failedChunks.Add(chunkResult.ErrorMessage ?? "unknown error");
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "[QueueCommitProcessor] Chunk commit threw exception, continuing with next chunk");
                    failedChunks.Add(ex.Message);
                }
            }

            result.Success = failedChunks.Count == 0;
            result.ErrorMessage = failedChunks.Count > 0
                ? $"Chunks failed: {string.Join("; ", failedChunks)}"
                : null;
            result.Revision = "ok";
            result.ItemsCount = totalCommitted;
        }
        catch (TimeoutException ex)
        {
            result.Success = false;
            result.ErrorMessage = $"Timeout: {ex.Message}";
            Log.Error(ex, "[QueueCommitProcessor] Batch commit timed out");
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorMessage = ex.Message;
            Log.Error(ex, "[QueueCommitProcessor] Batch commit failed with exception");
        }

        return result;
    }

    /// <summary>Executes pre-commit operations + svn commit for a single chunk of items.</summary>
    private async Task<BatchCommitResult> ExecuteChunkAsync(List<PendingCommitItem> chunk)
    {
        var result = new BatchCommitResult();

        // Execute pre-commit commands in topological order
        foreach (var item in chunk)
        {
            switch (item.Operation)
            {
                case CommitOperation.Delete:
                    if (!File.Exists(item.Path) && !Directory.Exists(item.Path))
                    {
                        var delOk = await _svnService.DeleteAsync(item.Path);
                        if (!delOk)
                            Log.Warning("[QueueCommitProcessor] svn delete failed for {Path}", item.Path);
                    }
                    else
                    {
                        Log.Debug("[QueueCommitProcessor] File restored before commit, skipping delete: {Path}", item.Path);
                    }
                    break;
                case CommitOperation.Move:
                    var mvOk = await _svnService.MoveAsync(item.FromPath!, item.Path);
                    if (!mvOk)
                        Log.Warning("[QueueCommitProcessor] svn move failed: {From} → {To}", item.FromPath, item.Path);
                    break;
                case CommitOperation.Add:
                    if (File.Exists(item.Path) || Directory.Exists(item.Path))
                    {
                        var addOk = await _svnService.AddPathAsync(item.Path);
                        if (!addOk)
                            Log.Warning("[QueueCommitProcessor] svn add failed for {Path}", item.Path);
                    }
                    else
                    {
                        Log.Debug("[QueueCommitProcessor] File no longer exists, skipping add: {Path}", item.Path);
                    }
                    break;
            }
        }

        // Find common ancestor within the repo root for this chunk
        var repoRoot = FindRepoRoot(chunk);
        var commitRoot = FindChunkCommitRoot(chunk, repoRoot);

        // Parent folder may have been deleted — skip if commit root no longer exists
        if (!Directory.Exists(commitRoot) && !File.Exists(commitRoot))
        {
            Log.Debug("[QueueCommitProcessor] Commit root does not exist, skipping chunk: {Root}", commitRoot);
            result.Success = true;
            result.Revision = "ok";
            result.ErrorMessage = null;
            result.ItemsCount = 0;
            return result;
        }

        var message = BuildCommitMessage(chunk);
        var committed = await _svnService.CommitAsync(commitRoot, message);

        result.Success = committed;
        result.Revision = committed ? "ok" : null;
        result.ErrorMessage = committed ? null : "Commit failed";
        result.ItemsCount = committed ? chunk.Count : 0;
        return result;
    }

    /// <summary>Commits a single item individually (used when count==1).</summary>
    private async Task<bool> CommitSingleItemAsync(PendingCommitItem item)
    {
        switch (item.Operation)
        {
            case CommitOperation.Delete:
                if (File.Exists(item.Path) || Directory.Exists(item.Path))
                {
                    Log.Debug("[QueueCommitProcessor] File restored before commit, skipping delete: {Path}", item.Path);
                    return true;
                }
                return await _svnService.DeleteAsync(item.Path);
            case CommitOperation.Move:
                return await _svnService.MoveAsync(item.FromPath!, item.Path);
            case CommitOperation.Add:
                if (!File.Exists(item.Path) && !Directory.Exists(item.Path))
                {
                    Log.Debug("[QueueCommitProcessor] File no longer exists, skipping add: {Path}", item.Path);
                    return true;
                }
                return await _svnService.AddPathAsync(item.Path);
            default:
                // Modify: just commit the file's parent dir
                var root = FindRepoRoot(new List<PendingCommitItem> { item });
                return await _svnService.CommitAsync(Path.GetDirectoryName(item.Path) ?? root, "Auto-sync: Modify single file");
        }
    }

    private static string FindChunkCommitRoot(List<PendingCommitItem> chunk, string repoRoot)
    {
        // Find the deepest common ancestor of all paths in the chunk
        var paths = chunk.Select(i => i.Operation == CommitOperation.Move ? i.FromPath! : i.Path).ToList();
        var common = paths[0];

        for (int i = 1; i < paths.Count; i++)
        {
            common = GetCommonAncestor(common, paths[i]);
            if (string.IsNullOrEmpty(common))
                break;
        }

        if (string.IsNullOrEmpty(common) || !Directory.Exists(common))
            return repoRoot;

        // Ensure we never commit above the repo root.
        // Use the drives/roots themselves for comparison, not GetFullPath (which resolves "C:" to cwd).
        var commonRoot = Path.GetPathRoot(common) ?? "";
        var repoRootPath = Path.GetPathRoot(repoRoot) ?? "";
        if (!string.IsNullOrEmpty(commonRoot) && !string.Equals(commonRoot, repoRootPath, StringComparison.OrdinalIgnoreCase))
        {
            // common is on a different drive or root — cannot safely clamp, fall back to repoRoot
            Log.Warning("[QueueCommitProcessor] Common ancestor {Common} is on a different drive from repo root {Root}, using repo root",
                common, repoRoot);
            return repoRoot;
        }

        var commonFull = Path.GetFullPath(common);
        var repoFull = Path.GetFullPath(repoRoot);

        if (!commonFull.StartsWith(repoFull, StringComparison.OrdinalIgnoreCase))
        {
            Log.Warning("[QueueCommitProcessor] Common ancestor {Common} is outside repo root {Root}, clamping to repo root",
                commonFull, repoFull);
            return repoRoot;
        }

        return common;
    }

    /// <summary>Returns the longest common ancestor of two paths.</summary>
    private static string GetCommonAncestor(string path1, string path2)
    {
        var parts1 = path1.Trim(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var parts2 = path2.Trim(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);

        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < Math.Min(parts1.Length, parts2.Length); i++)
        {
            if (string.Equals(parts1[i], parts2[i], StringComparison.OrdinalIgnoreCase))
            {
                if (sb.Length > 0)
                    sb.Append(Path.DirectorySeparatorChar);
                sb.Append(parts1[i]);
            }
            else
            {
                break;
            }
        }

        return sb.ToString();
    }

    private static List<PendingCommitItem> TopologicalSort(List<PendingCommitItem> items)
    {
        // Deletes: deepest paths first (delete child before parent)
        // Adds:    shallowest paths first (add parent before child)
        // Moves/Modifies: no ordering constraint, place at the end
        var deletes = items.Where(x => x.Operation == CommitOperation.Delete)
                           .OrderByDescending(x => GetPathDepth(x.Path))
                           .ThenBy(x => x.Path)
                           .ToList();
        var adds = items.Where(x => x.Operation == CommitOperation.Add)
                        .OrderBy(x => GetPathDepth(x.Path))
                        .ThenBy(x => x.Path)
                        .ToList();
        var rest = items.Where(x => x.Operation != CommitOperation.Delete
                                 && x.Operation != CommitOperation.Add)
                        .ToList();

        var sorted = new List<PendingCommitItem>(deletes.Count + adds.Count + rest.Count);
        sorted.AddRange(deletes);
        sorted.AddRange(adds);
        sorted.AddRange(rest);
        return sorted;
    }

    private static int GetPathDepth(string path)
    {
        // Counts path segments; deeper paths (more segments) sort after shallower ones.
        // /a/b/c.txt → 3,  /a/b → 2,  /a → 1
        return string.IsNullOrEmpty(path) ? 0
            : path.Trim(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                   .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar,
                          StringSplitOptions.RemoveEmptyEntries).Length;
    }

    private static string FindRepoRoot(List<PendingCommitItem> items)
    {
        if (items.Count == 0) return ".";
        // Use the shallowest path as the commit root (covers all children)
        var shallowest = items.OrderBy(x => GetPathDepth(
            x.Operation == CommitOperation.Move ? x.FromPath! : x.Path)).First();
        var path = shallowest.Operation == CommitOperation.Move ? shallowest.FromPath! : shallowest.Path;
        return Path.GetDirectoryName(path) ?? ".";
    }


    private static string BuildCommitMessage(List<PendingCommitItem> items)
    {
        var adds = items.Count(x => x.Operation == CommitOperation.Add);
        var deletes = items.Count(x => x.Operation == CommitOperation.Delete);
        var moves = items.Count(x => x.Operation == CommitOperation.Move);
        var modifies = items.Count(x => x.Operation == CommitOperation.Modify);

        var parts = new List<string>();
        if (adds > 0) parts.Add($"Add {adds}");
        if (deletes > 0) parts.Add($"Delete {deletes}");
        if (moves > 0) parts.Add($"Move {moves}");
        if (modifies > 0) parts.Add($"Modify {modifies}");

        var summary = items.Count == 1
            ? Path.GetFileName(items[0].Path)
            : $"{items.Count} items";

        return $"Auto-sync: [{string.Join(", ", parts)}] {summary}";
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
    }
}

/// <summary>Result of a single batch commit attempt.</summary>
public class BatchCommitResult
{
    public bool Success { get; set; }
    public string? Revision { get; set; }
    public string? ErrorMessage { get; set; }
    /// <summary>Number of items in the batch. 0 means there was nothing to commit.</summary>
    public int ItemsCount { get; set; }
}
