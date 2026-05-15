#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Timers;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using SharpSvn;
using SVNFileBox.Models;
using Serilog;

namespace SVNFileBox.Services;

public class SyncService : IDisposable
{
    private readonly ConfigService _configService;
    private readonly SvnService _svnService = new();
    private readonly FileWatcherService _fileWatcher = new();
    private readonly SyncRecordService _recordService;
    private readonly QueueCommitProcessor _queueProcessor;
    private readonly System.Timers.Timer _pollTimer;
    private readonly System.Timers.Timer _fullSyncTimer;
    private readonly ConcurrentDictionary<string, int> _failedFileAttempts = new();
    private readonly object _pendingLock = new();
    private readonly List<string> _pendingUpdates = new();
    private Repository? _currentRepo;
    private int _isPolling;
    private int _isCommitting;
    private int _isSyncing;
    private int _disableCount; // >0 means FileWatcher is paused
    private bool _watcherEnabledBeforeDisable;
    private int _pollIntervalMs = 60000;
    private int _maxRetries = 3;

    /// <summary>
    /// Async-local storage for the current repo name during Update/Commit operations.
    /// Used to record individual file transfer activity to SyncRecordService from
    /// the SvnService.FileTransferActivity event.
    /// </summary>
    private static readonly AsyncLocal<string> _currentRepoName = new();

    public event EventHandler<string>? SyncNotification;
    public event EventHandler? FilesChanged;
    /// <summary>
    /// Raised when server update creates conflicts. The sync loop pauses until
    /// all ConflictFileInfo objects are resolved by the caller.
    /// </summary>
    public event EventHandler<List<ConflictedFileInfo>>? ConflictDetected;

    public SyncService(ConfigService configService, SyncRecordService recordService)
    {
        _configService = configService;
        _recordService = recordService;
        // Use the shared QueueCommitProcessor from CommitCoordinator instead of creating our own
        _queueProcessor = CommitCoordinator.Instance.Processor;
        _queueProcessor.BatchCompleted += (_, result) =>
        {
            // Only notify for actual changes — skip if queue was empty (ItemsCount == 0)
            if (result.Success && result.ItemsCount > 0)
                Notify(result.Revision == "ok"
                    ? "批量同步完成"
                    : $"批量同步完成 (r{result.Revision})");
            else if (!result.Success && !string.IsNullOrEmpty(result.ErrorMessage))
                Notify($"批量同步失败: {result.ErrorMessage}");
        };
        _queueProcessor.BatchFailed += (_, failedItems) =>
        {
            foreach (var item in failedItems)
                AddPendingUpdate(item.Path);
        };

        // Subscribe to per-file transfer activity from SvnService and record each file
        SvnService.FileTransferActivity += (path, action) =>
        {
            var repoName = _currentRepoName.Value;
            if (string.IsNullOrEmpty(repoName) || string.IsNullOrEmpty(path)) return;
            _recordService.AddRecord(repoName, path, action, "Success");
        };
        _pollTimer = new System.Timers.Timer(_pollIntervalMs);
        _pollTimer.Elapsed += OnPollTimerElapsed;
        _pollTimer.AutoReset = true;

        _fileWatcher.FilesChanged += OnFilesChanged;

        _pollIntervalMs = _configService.Config.SyncIntervalMinutes * 60 * 1000;
        _pollTimer.Interval = _pollIntervalMs;

        // Full sync every 15 minutes to catch anything FileWatcher missed
        _fullSyncTimer = new System.Timers.Timer(15 * 60 * 1000);
        _fullSyncTimer.Elapsed += OnFullSyncTimerElapsed;
        _fullSyncTimer.AutoReset = true;

        Log.Information("SyncService created with poll interval {IntervalMs}ms", _pollIntervalMs);
    }

    public void StartSync(Repository repo)
    {
        _currentRepo = repo;
        _fileWatcher.StartWatching(repo.Path);
        _pollTimer.Start();
        _fullSyncTimer.Start();
        _queueProcessor.Start();
        Log.Information("Sync started for {Name} at {Path}", repo.Name, repo.Path);
    }

    public void StopSync()
    {
        _fileWatcher.StopWatching();
        _pollTimer.Stop();
        _fullSyncTimer.Stop();
        _queueProcessor.Stop();
        Log.Information("Sync stopped");
    }

    /// <summary>
    /// Pauses FileWatcher notifications (nested-safe: call DisableFileWatcher once per ReEnableFileWatcher).
    /// </summary>
    public void DisableFileWatcher()
    {
        Interlocked.Increment(ref _disableCount);
        // Only disable once (not per nesting level)
        if (_disableCount == 1)
        {
            _watcherEnabledBeforeDisable = _fileWatcher.IsWatching;
            if (_watcherEnabledBeforeDisable)
                _fileWatcher.StopWatching();
            Log.Debug("[SyncService] FileWatcher paused");
        }
    }

    /// <summary>
    /// Resumes FileWatcher notifications (must be called once per DisableFileWatcher).
    /// </summary>
    public void ReEnableFileWatcher()
    {
        var c = Interlocked.Decrement(ref _disableCount);
        // Only re-enable when fully unwound
        if (c == 0 && _watcherEnabledBeforeDisable)
        {
            if (_currentRepo != null)
            {
                _fileWatcher.StartWatching(_currentRepo.Path);
                Log.Debug("[SyncService] FileWatcher resumed");
            }
        }
    }

    /// <summary>
    /// 手工同步：先提交本地变更（上行），再从服务器更新（下行）。
    /// </summary>
    public async Task SyncNowAsync()
    {
        if (_currentRepo == null) return;
        if (Interlocked.CompareExchange(ref _isSyncing, 1, 0) == 1)
        {
            Log.Information("Sync already in progress, skipping");
            return;
        }
        try
        {
            _currentRepoName.Value = _currentRepo.Name;
            await _queueProcessor.SyncNowAsync(); // flush pending queue (上行)
            await PollCoreAsync();                  // download server changes (下行)
            await FullSyncAsync();                  // safety net: scan & commit unversioned/missing
        }
        finally
        {
            _currentRepoName.Value = null;
            Interlocked.Exchange(ref _isSyncing, 0);
        }
    }

    private async void OnFilesChanged(object? sender, string[] files)
    {
        if (files.Length == 0) return;
        if (Interlocked.CompareExchange(ref _isCommitting, 1, 0) == 1) return;
        // Also skip if a full sync is in progress or FileWatcher is paused (e.g., during file copy)
        if (Interlocked.CompareExchange(ref _isSyncing, 0, 0) != 0) return;
        if (Interlocked.CompareExchange(ref _disableCount, 0, 0) != 0) return;

        try
        {
            Log.Information("File changes detected: {Count} files", files.Length);

            foreach (var file in files)
            {
                try
                {
                    await CommitCoordinator.Instance.EnqueueFileChangeAsync(file);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Failed to enqueue file change: {File}", file);
                }
            }

            FilesChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            Interlocked.Exchange(ref _isCommitting, 0);
        }
    }

    private async void OnFullSyncTimerElapsed(object? sender, ElapsedEventArgs e)
    {
        if (_currentRepo == null) return;
        if (Interlocked.CompareExchange(ref _isSyncing, 1, 0) == 1)
        {
            Log.Debug("[FullSync] Sync already in progress, skipping timer tick");
            return;
        }
        try
        {
            Log.Information("[FullSync] Starting full sync scan for {Name}", _currentRepo.Name);
            await FullSyncAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[FullSync] Full sync failed");
        }
        finally
        {
            Interlocked.Exchange(ref _isSyncing, 0);
        }
    }

    /// <summary>
    /// Full sync: scan all changes via svn status and commit everything in one shot.
    /// This acts as a safety net for changes that FileWatcher may have missed.
    /// </summary>
    private async Task FullSyncAsync()
    {
        if (_currentRepo == null) return;

        var statuses = await _svnService.GetStatusAsync(_currentRepo.Path, SvnDepth.Infinity);
        if (statuses.Count == 0)
        {
            Log.Debug("[FullSync] No changes detected");
            return;
        }

        bool anyChange = false;

        foreach (var (path, status) in statuses)
        {
            switch (status)
            {
                case FileSvnStatus.Unversioned:
                {
                    var addSuccess = await _svnService.AddFileAsync(path);
                    if (addSuccess)
                    {
                        Log.Information("[FullSync] SvnStatus: Added, Path: {Path}", path);
                        anyChange = true;
                    }
                    else
                    {
                        Log.Warning("[FullSync] SvnStatus: AddFailed, Path: {Path}", path);
                    }
                    break;
                }
                case FileSvnStatus.Missing:
                {
                    var delSuccess = await _svnService.DeleteAsync(path);
                    if (delSuccess)
                    {
                        Log.Information("[FullSync] SvnStatus: Deleted, Path: {Path}", path);
                        anyChange = true;
                    }
                    else
                    {
                        Log.Warning("[FullSync] SvnStatus: DeleteFailed, Path: {Path}", path);
                    }
                    break;
                }
                case FileSvnStatus.Modified:
                case FileSvnStatus.Added:
                case FileSvnStatus.Deleted:
                case FileSvnStatus.Replaced:
                {
                    // Already marked in SVN index (staged), just needs a commit
                    Log.Information("[FullSync] SvnStatus: {Status}, Path: {Path}", status, path);
                    anyChange = true;
                    break;
                }
                case FileSvnStatus.Conflicted:
                {
                    // Cannot auto-resolve — leave for user to handle
                    Log.Warning("[FullSync] SvnStatus: Conflicted, Path: {Path} — skipping, requires manual resolution", path);
                    break;
                }
                default:
                {
                    // Obstructed, External, Unknown, etc. — skip
                    Log.Debug("[FullSync] SvnStatus: {Status}, Path: {Path} — skipped", status, path);
                    break;
                }
            }
        }

        if (anyChange)
        {
            var msg = $"Auto-sync: [Full Scan] {statuses.Count} item(s) changed";
            var success = await _svnService.CommitAsync(_currentRepo.Path, msg, _currentRepo.Username, _currentRepo.Password);
            if (success)
            {
                Log.Information("[FullSync] Committed {Count} changes", statuses.Count);
                _recordService.AddRecord(_currentRepo.Name, _currentRepo.Path, "FullScan", "Success", $"Committed {statuses.Count} item(s)");
                Notify($"全量同步完成: {statuses.Count} 项已提交");
            }
            else
            {
                Log.Warning("[FullSync] Commit failed");
                _recordService.AddRecord(_currentRepo.Name, _currentRepo.Path, "FullScan", "Failed", "Commit returned non-zero");
            }
        }
        else
        {
            Log.Debug("[FullSync] No pending changes to commit");
        }
    }

    private void OnPollTimerElapsed(object? sender, ElapsedEventArgs e)
    {
        // Fire-and-forget: don't await, timer handler must not be async void
        _ = PollCoreAsync();
    }

    private async Task PollCoreAsync()
    {
        if (_currentRepo == null) return;
        if (Interlocked.CompareExchange(ref _isPolling, 1, 0) == 1) return;

        try
        {
            // First: retry pending files
            await RetryPendingUpdatesAsync();

            // Then: check for new server changes
            var localRev = await _svnService.GetWorkingCopyRevisionAsync(_currentRepo.Path);
            var serverRev = await _svnService.GetHeadRevisionAsync(_currentRepo.Url, _currentRepo.Username, _currentRepo.Password);

            Log.Debug("PollCheck: local={Local}, server={Server}", localRev, serverRev);
            if (serverRev <= localRev)
            {
                Log.Debug("No server updates, local={Local}, server={Server}", localRev, serverRev);
                return;
            }

            Log.Information("Server has updates: local={Local}, server={Server}", localRev, serverRev);
            _currentRepoName.Value = _currentRepo?.Name;
            var updateSuccess = await _svnService.UpdateAsync(_currentRepo.Path);
            if (updateSuccess)
            {
                var conflictInfo = await BuildConflictInfoListAsync(_currentRepo.Path);
                if (conflictInfo.Count > 0)
                {
                    // Raise event — MainWindow shows ConflictWindow as a modal dialog,
                    // waits for user to pick resolutions, then calls ApplyConflictResolutionsAsync.
                    ConflictDetected?.Invoke(this, conflictInfo);
                    // Do NOT await or call ApplyConflictResolutionsAsync here.
                    // The MainWindow.OnConflictDetected handler shows the dialog and triggers resolution.
                }
                else
                {
                    _recordService.AddRecord(_currentRepo.Name, _currentRepo.Path, "Update", "Success", $"Updated {serverRev - localRev} revision(s)");
                    Notify($"已从服务器更新 {serverRev - localRev} 个版本");
                    FilesChanged?.Invoke(this, EventArgs.Empty);
                }
            }
            else
            {
                Log.Warning("Update failed for {Path}", _currentRepo.Path);
                _recordService.AddRecord(_currentRepo.Name, _currentRepo.Path, "Update", "Failed", "Update returned false");
                Notify($"更新失败");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Poll timer error");
        }
        finally
        {
            Interlocked.Exchange(ref _isPolling, 0);
        }
    }

    private async Task RetryPendingUpdatesAsync()
    {
        List<string> toRetry;
        lock (_pendingLock)
        {
            toRetry = new List<string>(_pendingUpdates);
        }

        if (toRetry.Count == 0) return;

        Log.Information("Retrying {Count} pending files", toRetry.Count);

        foreach (var file in toRetry)
        {
            try
            {
                if (!File.Exists(file) && !Directory.Exists(file))
                {
                    lock (_pendingLock) { _pendingUpdates.Remove(file); }
                    continue;
                }

                var parentDir = Path.GetDirectoryName(file) ?? _currentRepo?.Path ?? "";
                var updateSuccess = await _svnService.UpdateAsync(file);

                if (updateSuccess)
                {
                    lock (_pendingLock) { _pendingUpdates.Remove(file); }
                    _failedFileAttempts.TryRemove(file, out _);
                    Notify($"已同步(重试): {Path.GetFileName(file)}");
                    Log.Information("Pending file updated: {File}", file);
                }
                else
                {
                    var attempts = _failedFileAttempts.GetOrAdd(file, 0) + 1;
                    _failedFileAttempts[file] = attempts;

                    if (attempts >= _maxRetries)
                    {
                        lock (_pendingLock) { _pendingUpdates.Remove(file); }
                        Notify($"同步失败(多次重试): {Path.GetFileName(file)} - 请关闭占用程序");
                        Log.Warning("File failed after {Attempts} attempts: {File}", attempts, file);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error retrying pending file: {File}", file);
            }
        }
    }

    // Marshal SyncNotification to the UI thread since timer callbacks run on ThreadPool.
    private void Notify(string message)
    {
        var disp = Application.Current?.Dispatcher;
        if (disp != null && !disp.CheckAccess())
            disp.Invoke(() => SyncNotification?.Invoke(this, message));
        else
            SyncNotification?.Invoke(this, message);
    }

    private void AddPendingUpdate(string filePath)
    {
        lock (_pendingLock)
        {
            if (!_pendingUpdates.Contains(filePath))
            {
                _pendingUpdates.Add(filePath);
                Log.Information("Added to pending updates: {File}", filePath);
            }
        }
    }

    /// <summary>
    /// Scans for conflicted files and builds a list with local/server timestamps
    /// and a Last-Write-Wins suggestion. Does NOT resolve anything.
    /// </summary>
    private async Task<List<ConflictedFileInfo>> BuildConflictInfoListAsync(string workingCopyPath)
    {
        var conflictInfo = new List<ConflictedFileInfo>();
        var conflictedPaths = await _svnService.GetConflictedFilesAsync(workingCopyPath);
        Log.Information("Found {Count} conflicted files", conflictedPaths.Count);

        foreach (var filePath in conflictedPaths)
        {
            try
            {
                if (!File.Exists(filePath)) continue;

                var localTime = File.GetLastWriteTimeUtc(filePath);
                var serverTime = await _svnService.GetLastChangedTimeAsync(filePath);

                conflictInfo.Add(new ConflictedFileInfo
                {
                    FilePath = filePath,
                    LocalModifiedTime = localTime,
                    ServerModifiedTime = serverTime,
                    SuggestedResolution = localTime > serverTime
                        ? ConflictResolution.KeepLocal
                        : ConflictResolution.AcceptServer,
                    SelectedResolution = localTime > serverTime
                        ? ConflictResolution.KeepLocal
                        : ConflictResolution.AcceptServer,
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to build conflict info for {File}", filePath);
            }
        }

        return conflictInfo;
    }

    /// <summary>
    /// Applies user-selected resolutions from the ConflictWindow.
    /// Runs after the user closes the ConflictWindow — called from SyncService's caller (MainWindow).
    /// </summary>
    internal async Task<int> ApplyConflictResolutionsAsync(List<ConflictedFileInfo> conflictInfo)
    {
        if (_currentRepo == null) return 0;
        int handled = 0;

        foreach (var info in conflictInfo)
        {
            try
            {
                var parentDir = Path.GetDirectoryName(info.FilePath) ?? _currentRepo.Path;
                var fileName = Path.GetFileName(info.FilePath);

                switch (info.SelectedResolution)
                {
                    case ConflictResolution.KeepLocal:
                    {
                        // Accept local version: resolve to MineFull then commit
                        var resolved = await _svnService.ResolveAsync(info.FilePath, SvnAccept.MineFull);
                        if (!resolved) Log.Warning("Resolve(MineFull) returned false for {File}", info.FilePath);
                        var committed = await _svnService.CommitAsync(parentDir, $"Auto-sync: [Conflict Resolved — Kept Local] {fileName}");
                        Log.Information("Conflict KeepLocal: {File}, resolve={Resolved}, commit={Committed}", info.FilePath, resolved, committed);
                        break;
                    }
                    case ConflictResolution.AcceptServer:
                    {
                        // Accept server version: resolve to TheirsFull (svn stores server version in working file)
                        var resolved = await _svnService.ResolveAsync(info.FilePath, SvnAccept.TheirsFull);
                        if (!resolved) Log.Warning("Resolve(TheirsFull) returned false for {File}", info.FilePath);
                        Log.Information("Conflict AcceptServer: {File}, resolved={Resolved}", info.FilePath, resolved);
                        break;
                    }
                    case ConflictResolution.KeepBoth:
                    {
                        // Keep local as backup, then accept server version
                        var backupPath = info.FilePath + $".local-backup-{DateTime.UtcNow:yyyyMMddHHmmss}";
                        File.Copy(info.FilePath, backupPath, overwrite: true);
                        Log.Information("Conflict KeepBoth: copied {Original} → {Backup}", info.FilePath, backupPath);
                        var resolved = await _svnService.ResolveAsync(info.FilePath, SvnAccept.TheirsFull);
                        Log.Information("Conflict KeepBoth: {File} accepted server, resolved={Resolved}", info.FilePath, resolved);
                        break;
                    }
                }

                handled++;
                _recordService.AddRecord(_currentRepo.Name, fileName, "ConflictResolved", "Success",
                    $"User chose: {info.SelectedResolution}");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to apply conflict resolution for {File}", info.FilePath);
                _recordService.AddRecord(_currentRepo.Name, Path.GetFileName(info.FilePath), "ConflictResolved", "Failed", ex.Message);
            }
        }

        return handled;
    }

    public void Dispose()
    {
        _fileWatcher.Dispose();
        _pollTimer.Stop();
        _pollTimer.Dispose();
        _fullSyncTimer.Stop();
        _fullSyncTimer.Dispose();
        _queueProcessor.Dispose();
    }
}
