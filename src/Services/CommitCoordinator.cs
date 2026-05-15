#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace SVNFileBox.Services;

/// <summary>
/// Central coordinator for all SVN write operations (add/delete/move/modify).
/// 
/// All write operations — triggered by FileWatcher, FileCopier, or UI actions —
/// flow through this class. It ensures writes are serialized through a semaphore,
/// pre-checks file states with svn add/delete before enqueueing, and hands off
/// actual SVN commits to QueueCommitProcessor for batch processing.
///
/// Design goals:
/// - Single entry point for all SVN write operations (no direct PendingCommitQueue access from other classes)
/// - Serializes writes via semaphore to prevent WC locks
/// - Pre-checks (svn add/delete) happen here, not in QueueCommitProcessor
/// - QueueCommitProcessor only executes the final svn commit
/// </summary>
public class CommitCoordinator : IDisposable
{
    private static readonly Lazy<CommitCoordinator> _lazy = new(() => new CommitCoordinator());
    public static CommitCoordinator Instance => _lazy.Value;

    private readonly SvnService _svnService = new();
    private readonly QueueCommitProcessor _queueProcessor;
    private int _isLocked; // >0: SVN write operation in progress (Update/Commit), skip all enqueue attempts

    public QueueCommitProcessor Processor => _queueProcessor;

    private CommitCoordinator()
    {
        _queueProcessor = new QueueCommitProcessor(_svnService);
        // Do NOT call Start() here — SyncService calls EnsureStarted() after
        // all event handlers are wired up, so the timer doesn't fire before
        // BatchCompleted/BatchFailed subscriptions are ready.
        Log.Information("[CommitCoordinator] Initialized (not yet started)");
    }

    /// <summary>
    /// Starts the background timer. Called by SyncService after event handlers are wired.
    /// </summary>
    public void EnsureStarted()
    {
        _queueProcessor.Start();
        Log.Information("[CommitCoordinator] Started");
    }

    /// <summary>
    /// Acquires the SVN write lock. While held, all Enqueue* calls are silently skipped.
    /// </summary>
    public void Lock() => Interlocked.Exchange(ref _isLocked, 1);

    /// <summary>
    /// Releases the SVN write lock.
    /// </summary>
    public void Unlock() => Interlocked.Exchange(ref _isLocked, 0);

    /// <summary>
    /// Enqueues a newly created file or directory for svn add.
    /// Called by UI actions (e.g. user creates a new folder via UI).
    /// </summary>
    public async Task EnqueueAddAsync(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        if (Interlocked.CompareExchange(ref _isLocked, 0, 0) != 0) return;

        // Pre-check: svn add to ensure the path is tracked before enqueueing.
        // This runs under the semaphore in SvnService.
        var added = await _svnService.AddPathAsync(path);
        if (!added)
        {
            Log.Warning("[CommitCoordinator] EnqueueAdd: svn add failed for {Path}", path);
            return;
        }

        PendingCommitQueue.Instance.Enqueue(path, CommitOperation.Add);
        Log.Information("[CommitCoordinator] Enqueued Add: {Path}", path);
    }

    /// <summary>
    /// Enqueues a deleted file or directory for svn delete.
    /// Called by UI delete actions.
    /// </summary>
    public async Task EnqueueDeleteAsync(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        if (Interlocked.CompareExchange(ref _isLocked, 0, 0) != 0) return;

        // Pre-check: mark as deleted in SVN before enqueueing.
        var deleted = await _svnService.DeleteAsync(path);
        if (!deleted)
        {
            Log.Warning("[CommitCoordinator] EnqueueDelete: svn delete failed for {Path}", path);
            return;
        }

        PendingCommitQueue.Instance.Enqueue(path, CommitOperation.Delete);
        Log.Information("[CommitCoordinator] Enqueued Delete: {Path}", path);
    }

    /// <summary>
    /// Enqueues a move (rename) operation.
    /// Called by UI rename/drag actions.
    /// </summary>
    public void EnqueueMove(string fromPath, string toPath)
    {
        if (string.IsNullOrEmpty(fromPath) || string.IsNullOrEmpty(toPath)) return;
        PendingCommitQueue.Instance.EnqueueMove(fromPath, toPath);
        Log.Information("[CommitCoordinator] Enqueued Move: {From} → {To}", fromPath, toPath);
    }

    /// <summary>
    /// Enqueues a modify (content change) for an already-versioned file.
    /// </summary>
    public void EnqueueModify(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        PendingCommitQueue.Instance.Enqueue(path, CommitOperation.Modify);
        Log.Information("[CommitCoordinator] Enqueued Modify: {Path}", path);
    }

    /// <summary>
    /// Analyzes a file path and determines the appropriate operation, then enqueues it.
    /// Called by FileWatcher when external changes are detected.
    /// </summary>
    public async Task EnqueueFileChangeAsync(string path)
    {
        if (string.IsNullOrEmpty(Path.GetDirectoryName(path))) return;
        if (Interlocked.CompareExchange(ref _isLocked, 0, 0) != 0) return;

        bool fileExists = File.Exists(path) || Directory.Exists(path);

        if (!fileExists)
        {
            // Physical file/folder is gone — either deleted externally or part of a rename.
            if (!_svnService.IsVersioned(path))
            {
                // Was never tracked — nothing to sync.
                Log.Debug("[CommitCoordinator] Skipping untracked missing file: {File}", path);
                return;
            }
            // svn delete marks it as deleted; QueueCommitProcessor will finalize via commit.
            await _svnService.DeleteAsync(path);
            PendingCommitQueue.Instance.Enqueue(path, CommitOperation.Delete);
            Log.Information("[CommitCoordinator] SvnStatus: Deleted, Path: {File}", path);
            return;
        }

        // File/folder exists — could be a new create, a modify, or the destination of a rename.
        if (!IsSvnManaged(path))
        {
            // Unversioned → svn add marks it for addition; QueueCommitProcessor will commit.
            await _svnService.AddPathAsync(path);
            PendingCommitQueue.Instance.Enqueue(path, CommitOperation.Add);
            Log.Information("[CommitCoordinator] SvnStatus: Added, Path: {File}", path);
        }
        else
        {
            // Already versioned → svn commit will auto-detect content changes.
            PendingCommitQueue.Instance.Enqueue(path, CommitOperation.Modify);
            Log.Information("[CommitCoordinator] SvnStatus: Modified, Path: {File}", path);
        }
    }

    /// <summary>
    /// Called by FileCopier after a copy completes.
    /// Scans the working copy for newly added files and enqueues them for async commit.
    /// </summary>
    public async Task EnqueueCommitAsync(string workingCopyPath)
    {
        if (string.IsNullOrEmpty(workingCopyPath)) return;

        // Scan for newly added (svn status) files in the working copy.
        // FileCopier already ran svn add on each file during copy, so here we just
        // need to enqueue the dest root so the queue processor commits them.
        PendingCommitQueue.Instance.Enqueue(workingCopyPath, CommitOperation.Add);
        Log.Information("[CommitCoordinator] Enqueued commit for working copy: {Path}", workingCopyPath);

        await Task.CompletedTask; // Placeholder for potential future svn status scan
    }

    /// <summary>
    /// Forces an immediate queue flush (user-triggered SyncNow).
    /// </summary>
    public async Task SyncNowAsync()
    {
        await _queueProcessor.SyncNowAsync();
    }

    /// <summary>
    /// Forces an immediate queue flush (fire-and-forget).
    /// </summary>
    public void SyncNow()
    {
        _queueProcessor.SyncNow();
    }

    private static bool IsSvnManaged(string path)
    {
        var dir = Path.GetDirectoryName(path);
        while (!string.IsNullOrEmpty(dir))
        {
            if (Directory.Exists(Path.Combine(dir, ".svn")))
                return true;
            if (dir == Path.GetPathRoot(dir)) break;
            dir = Path.GetDirectoryName(dir);
        }
        return false;
    }

    public void Dispose()
    {
        _queueProcessor.Stop();
        _queueProcessor.Dispose();
        Log.Information("[CommitCoordinator] Disposed");
    }
}
