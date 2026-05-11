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

    public QueueCommitProcessor(SvnService svnService, int intervalSeconds = 30, int minBatchSize = 5)
    {
        _svnService = svnService;
        _intervalMs = intervalSeconds * 1000;
        _minBatchSize = minBatchSize;

        _timer = new System.Timers.Timer(_intervalMs);
        _timer.Elapsed += OnTimerElapsed;
        _timer.AutoReset = true;
        _timer.Start();

        Log.Information("[QueueCommitProcessor] Started, interval={Interval}s, minBatch={MinBatch}",
            intervalSeconds, minBatchSize);
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

    private async Task<BatchCommitResult> ExecuteBatchCommitAsync(List<PendingCommitItem> items)
    {
        var result = new BatchCommitResult();

        try
        {
            // Commit all items together from the repo root so cross-directory moves
            // (e.g. /src/a.txt → /dst/b.txt) are handled atomically in one commit.
            // Topological sort ensures parent dirs are deleted before children,
            // and added before their descendants.
            var sorted = TopologicalSort(items);

            // Execute pre-commit commands in topological order
            foreach (var item in sorted)
            {
                switch (item.Operation)
                {
                    case CommitOperation.Delete:
                        var delOk = await _svnService.DeleteAsync(item.Path);
                        if (!delOk)
                            Log.Warning("[QueueCommitProcessor] svn delete failed for {Path}", item.Path);
                        break;
                    case CommitOperation.Move:
                        var mvOk = await _svnService.MoveAsync(item.FromPath!, item.Path);
                        if (!mvOk)
                            Log.Warning("[QueueCommitProcessor] svn move failed: {From} → {To}", item.FromPath, item.Path);
                        break;
                    case CommitOperation.Add:
                        var addOk = await _svnService.AddPathAsync(item.Path);
                        if (!addOk)
                            Log.Warning("[QueueCommitProcessor] svn add failed for {Path}", item.Path);
                        break;
                    // Modify: no pre-command needed, commit auto-detects changes
                }
            }

            // Single commit for all items from the repo root
            var message = BuildCommitMessage(items);
            var repoRoot = FindRepoRoot(sorted);
            var committed = await _svnService.CommitAsync(repoRoot, message);
            if (!committed)
            {
                result.Success = false;
                result.ErrorMessage = "Commit failed";
                return result;
            }

            if (items.Count == 0)
            {
                result.Success = false;
                result.ErrorMessage = "Nothing to commit";
                return result;
            }

            result.Success = true;
            result.Revision = "ok";
            result.ItemsCount = items.Count;
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

    private static string GetCommonParent(PendingCommitItem item)
    {
        // For Move, both the source (FromPath) and destination (Path) must be included
        // in the common-parent calculation so they land in the same commit group.
        var path = item.Operation == CommitOperation.Move ? item.FromPath! : item.Path;
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
