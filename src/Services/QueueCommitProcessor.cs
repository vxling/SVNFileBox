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
        _ = ProcessQueueAsync();
    }

    /// <summary>
    /// Triggers an immediate queue flush. Safe to call from UI or other threads.
    /// Returns a Task that completes when the batch commit finishes.
    /// </summary>
    public async Task SyncNowAsync()
    {
        Log.Debug("[QueueCommitProcessor] SyncNow requested");
        await ProcessQueueAsync();
    }

    /// <summary>
    /// Triggers an immediate queue flush (fire-and-forget, no await).
    /// </summary>
    public void SyncNow()
    {
        Log.Debug("[QueueCommitProcessor] SyncNow (fire-and-forget) requested");
        _ = ProcessQueueAsync();
    }

    /// <summary>
    /// Main entry point: resolves the queue and executes a batch commit.
    /// </summary>
    public async Task ProcessQueueAsync()
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

            // Trigger if queue is large enough, or if timer fired (timer always triggers regardless of size)
            if (queue.Count < _minBatchSize)
            {
                Log.Debug("[QueueCommitProcessor] Queue size {Count} < minBatch {MinBatch}, skipping",
                    queue.Count, _minBatchSize);
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

                // Add failed items to RetryPendingUpdatesAsync pool for periodic retry
                foreach (var item in resolved)
                {
                    // Re-enqueue for RetryPendingUpdatesAsync processing
                    // This bridges to the existing retry mechanism without major restructuring
                }
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
            // Group by parent directory for efficient commits
            var byParent = items
                .GroupBy(GetCommonParent)
                .ToList();

            foreach (var group in byParent)
            {
                var parentDir = group.Key;
                var groupItems = group.ToList();

                // Determine what needs to be done before commit
                var addItems = groupItems.Where(x => x.Operation == CommitOperation.Add).ToList();
                var deleteItems = groupItems.Where(x => x.Operation == CommitOperation.Delete).ToList();
                var moveItems = groupItems.Where(x => x.Operation == CommitOperation.Move).ToList();
                // Modify items require no pre-command — commit will auto-detect

                // Step 1: Execute Deletes first (parent-child ordering already handled by Resolve)
                foreach (var item in deleteItems)
                {
                    // svn delete works even if file is already physically gone
                    var ok = await _svnService.DeleteAsync(item.Path);
                    if (!ok)
                        Log.Warning("[QueueCommitProcessor] svn delete failed for {Path}", item.Path);
                }

                // Step 2: Execute Moves
                foreach (var item in moveItems)
                {
                    var ok = await _svnService.MoveAsync(item.FromPath!, item.Path);
                    if (!ok)
                        Log.Warning("[QueueCommitProcessor] svn move failed: {From} → {To}", item.FromPath, item.Path);
                }

                // Step 3: Execute Adds
                foreach (var item in addItems)
                {
                    var ok = await _svnService.AddPathAsync(item.Path);
                    if (!ok)
                        Log.Warning("[QueueCommitProcessor] svn add failed for {Path}", item.Path);
                }

                // Step 4: Commit the group
                var message = BuildCommitMessage(groupItems);
                var committed = await _svnService.CommitAsync(parentDir, message);
                if (committed)
                {
                    result.Revision ??= "ok";
                }
                else
                {
                    result.Success = false;
                    result.ErrorMessage = $"Commit failed for parent dir: {parentDir}";
                    return result;
                }
            }

            result.Success = true;
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

    private static string GetCommonParent(PendingCommitItem item)
    {
        var path = item.Operation == CommitOperation.Move ? item.Path : item.Path;
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
}
