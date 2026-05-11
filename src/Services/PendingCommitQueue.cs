#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Serilog;

namespace SVNFileBox.Services;

/// <summary>
/// Represents a single pending SVN commit operation in the queue.
/// </summary>
public class PendingCommitItem
{
    /// <summary>Absolute path to the file or directory.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// For Move operations: the original path before the move.
    /// Null for non-Move operations.
    /// </summary>
    public string? FromPath { get; set; }

    public CommitOperation Operation { get; set; } = CommitOperation.Modify;

    /// <summary>Item status: Pending → InProgress → Committed | Failed</summary>
    public CommitStatus Status { get; set; } = CommitStatus.Pending;

    /// <summary>Wall-clock time when the item was enqueued.</summary>
    public DateTime QueuedAt { get; set; } = DateTime.Now;

    /// <summary>Number of times commit has been attempted and failed.</summary>
    public int RetryCount { get; set; }
}

/// <summary>SVN operations that affect the working copy state.</summary>
public enum CommitOperation
{
    /// <summary>svn add — new/unversioned file or directory added to version control.</summary>
    Add,
    /// <summary>svn delete — file or directory removed from version control.</summary>
    Delete,
    /// <summary>No explicit command — already versioned file modified, commit will auto-detect.</summary>
    Modify,
    /// <summary>svn move — file or directory renamed/moved (FromPath → Path).</summary>
    Move,
}

/// <summary>Lifecycle state of a queue item.</summary>
public enum CommitStatus
{
    Pending,
    InProgress,
    Committed,
    Failed,
}

/// <summary>
/// In-memory queue of pending SVN operations backed by JSON persistence.
/// Items are kept in insertion order; Resolve() collapses duplicate paths
/// by keeping only the last operation for each path (last-wins semantics).
///
/// Thread-safe for concurrent Enqueue from FileWatcher/Rename_Click/Delete_Click,
/// but CommitBatch should only be called from a single caller at a time (the
/// QueueCommitProcessor).
/// </summary>
public class PendingCommitQueue
{
    private static PendingCommitQueue? _instance;
    public static PendingCommitQueue Instance => _instance ??= new PendingCommitQueue();

    private readonly object _lock = new();
    private readonly List<PendingCommitItem> _items = new();
    private readonly string _queueFilePath;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>Exposed for unit tests and debugging.</summary>
    public IReadOnlyList<PendingCommitItem> Items
    {
        get { lock (_lock) return _items.ToList().AsReadOnly(); }
    }

    public int Count
    {
        get { lock (_lock) return _items.Count; }
    }

    public PendingCommitQueue()
    {
        var configDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SVNFileBox");
        var queueDir = Path.Combine(configDir, "commit_queue");
        Directory.CreateDirectory(queueDir);
        _queueFilePath = Path.Combine(queueDir, "pending_queue.json");

        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() }
        };

        Load();
        Log.Information("[PendingCommitQueue] Initialized, {Count} pending items in queue", Count);
    }

    /// <summary>
    /// Adds a new operation to the tail of the queue.
    /// Duplicate paths are allowed — Resolve() collapses them (last wins).
    /// </summary>
    public void Enqueue(string path, CommitOperation operation, string? fromPath = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        var item = new PendingCommitItem
        {
            Path = path,
            FromPath = fromPath,
            Operation = operation,
            Status = CommitStatus.Pending,
            QueuedAt = DateTime.Now,
            RetryCount = 0
        };

        lock (_lock)
        {
            _items.Add(item);
            Log.Debug("[PendingCommitQueue] Enqueued {Op} {Path}", operation, path);
        }

        // Persist asynchronously to avoid blocking the caller
        _ = SaveAsync();
    }

    /// <summary>
    /// Enqueues a Move operation (rename or move).
    /// </summary>
    public void EnqueueMove(string fromPath, string toPath)
    {
        Enqueue(toPath, CommitOperation.Move, fromPath);
    }

    /// <summary>
    /// Resolves the queue: for each unique path, keeps only the LAST occurrence.
    /// Resolution rules (last-wins):
    ///   Delete after anything → Delete (file no longer exists)
    ///   Modify after Add → Modify (Add is absorbed into Modify since commit auto-detects)
    ///   Move → Move (both FromPath and Path are marked)
    ///   Add after Delete on same path → Add (re-creation)
    ///
    /// Returns items in safe execution order: all Deletes first, then Moves, then Adds,
    /// then Modifies (which require no explicit command).
    /// </summary>
    public List<PendingCommitItem> Resolve()
    {
        lock (_lock)
        {
            // Last-wins: iterate in reverse, keep first-seen (which is the last in original order)
            var seen = new Dictionary<string, PendingCommitItem>(StringComparer.OrdinalIgnoreCase);
            for (int i = _items.Count - 1; i >= 0; i--)
            {
                var item = _items[i];
                var key = item.Operation == CommitOperation.Move
                    ? item.Path          // Move: track destination path
                    : item.Path;        // Others: track by path

                if (!seen.ContainsKey(key))
                    seen[key] = item;

                // For Move, also register the source path so Delete on source is tracked
                if (item.Operation == CommitOperation.Move && !seen.ContainsKey(item.FromPath!))
                    seen[item.FromPath!] = item;
            }

            var resolved = seen.Values.ToList();

            // Safe execution order:
            // 1. Deletes first  (parent dirs deleted after children would fail)
            // 2. Moves next
            // 3. Adds last
            // 4. Modifies require no explicit command, placed at the end
            var ordered = new List<PendingCommitItem>();
            ordered.AddRange(resolved.Where(x => x.Operation == CommitOperation.Delete));
            ordered.AddRange(resolved.Where(x => x.Operation == CommitOperation.Move));
            ordered.AddRange(resolved.Where(x => x.Operation == CommitOperation.Add));
            ordered.AddRange(resolved.Where(x => x.Operation == CommitOperation.Modify));

            return ordered;
        }
    }

    /// <summary>
    /// Marks the given items as InProgress before a commit attempt.
    /// </summary>
    public void MarkInProgress(IEnumerable<PendingCommitItem> items)
    {
        lock (_lock)
        {
            foreach (var item in items)
            {
                var existing = _items.FirstOrDefault(x =>
                    x.Path == item.Path && x.QueuedAt == item.QueuedAt);
                if (existing != null)
                    existing.Status = CommitStatus.InProgress;
            }
        }
    }

    /// <summary>
    /// Marks the given items as Committed and removes them from the queue.
    /// </summary>
    public void MarkCommitted(IEnumerable<PendingCommitItem> items)
    {
        lock (_lock)
        {
            var toRemove = items.ToHashSet();
            _items.RemoveAll(x =>
                toRemove.Any(t => t.Path == x.Path && t.QueuedAt == x.QueuedAt));
        }
        _ = SaveAsync();
    }

    /// <summary>
    /// Marks the given items as Failed and increments their retry count.
    /// </summary>
    public void MarkFailed(IEnumerable<PendingCommitItem> items)
    {
        lock (_lock)
        {
            foreach (var item in items)
            {
                var existing = _items.FirstOrDefault(x =>
                    x.Path == item.Path && x.QueuedAt == item.QueuedAt);
                if (existing != null)
                {
                    existing.Status = CommitStatus.Failed;
                    existing.RetryCount++;
                }
            }
        }
        _ = SaveAsync();
    }

    /// <summary>
    /// Returns items that have reached max retry count and should be surfaced to the user.
    /// </summary>
    public IEnumerable<PendingCommitItem> GetStaleItems(int maxRetries = 3)
    {
        lock (_lock)
        {
            return _items
                .Where(x => x.Status == CommitStatus.Failed && x.RetryCount >= maxRetries)
                .ToList();
        }
    }

    /// <summary>
    /// Clears all committed/failed items, keeping only Pending items.
    /// Called after a successful batch commit to prune stale history.
    /// </summary>
    public void Prune()
    {
        lock (_lock)
        {
            _items.RemoveAll(x => x.Status == CommitStatus.Committed);
            // Keep Failed items so they can be retried
        }
        _ = SaveAsync();
    }

    /// <summary>
    /// Synchronously saves the current queue to disk.
    /// </summary>
    public void Save()
    {
        lock (_lock)
        {
            SaveInternal();
        }
    }

    private async Task SaveAsync()
    {
        List<PendingCommitItem> snapshot;
        lock (_lock)
        {
            snapshot = _items.ToList();
        }
        try
        {
            var json = JsonSerializer.Serialize(snapshot, _jsonOptions);
            await File.WriteAllTextAsync(_queueFilePath, json);
            Log.Debug("[PendingCommitQueue] Saved {Count} items to disk", snapshot.Count);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[PendingCommitQueue] Failed to persist queue to disk");
        }
    }

    private void SaveInternal()
    {
        try
        {
            var json = JsonSerializer.Serialize(_items, _jsonOptions);
            File.WriteAllText(_queueFilePath, json);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[PendingCommitQueue] Failed to persist queue to disk");
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_queueFilePath)) return;
            var json = File.ReadAllText(_queueFilePath);
            var items = JsonSerializer.Deserialize<List<PendingCommitItem>>(json, _jsonOptions);
            if (items != null)
            {
                lock (_lock)
                {
                    _items.Clear();
                    _items.AddRange(items);
                }
                Log.Information("[PendingCommitQueue] Loaded {Count} items from disk", items.Count);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[PendingCommitQueue] Failed to load queue from disk, starting fresh");
        }
    }
}
