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
    private readonly System.Timers.Timer _pollTimer;
    private readonly System.Timers.Timer _fullSyncTimer;
    private readonly ConcurrentDictionary<string, int> _failedFileAttempts = new();
    private readonly object _pendingLock = new();
    private readonly List<string> _pendingUpdates = new();
    private Repository? _currentRepo;
    private int _isPolling;
    private int _isCommitting;
    private int _pollIntervalMs = 60000;
    private int _maxRetries = 3;

    public event EventHandler<string>? SyncNotification;
    public event EventHandler? FilesChanged;

    public SyncService(ConfigService configService, SyncRecordService recordService)
    {
        _configService = configService;
        _recordService = recordService;
        _pollTimer = new System.Timers.Timer(_pollIntervalMs);
        _pollTimer.Elapsed += OnPollTimerElapsed;
        _pollTimer.AutoReset = true;

        _fileWatcher.FilesChanged += OnFilesChanged;

        _pollIntervalMs = _configService.Config.SyncIntervalMinutes * 60 * 1000;
        _pollTimer.Interval = _pollIntervalMs;

        // Full sync every 30 minutes to catch anything FileWatcher missed
        _fullSyncTimer = new System.Timers.Timer(30 * 60 * 1000);
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
        Log.Information("Sync started for {Name} at {Path}", repo.Name, repo.Path);
    }

    public void StopSync()
    {
        _fileWatcher.StopWatching();
        _pollTimer.Stop();
        _fullSyncTimer.Stop();
        Log.Information("Sync stopped");
    }

    /// <summary>
    /// 手工同步：先提交本地变更（上行），再从服务器更新（下行）。
    /// </summary>
    public async Task SyncNowAsync()
    {
        if (_currentRepo == null) return;
        await RetryPendingUpdatesAsync(); // upload pending local commits
        await PollCoreAsync(); // download server changes
    }

    private async void OnFilesChanged(object? sender, string[] files)
    {
        if (files.Length == 0) return;
        if (Interlocked.CompareExchange(ref _isCommitting, 1, 0) == 1) return;

        try
        {
            Log.Information("File changes detected: {Count} files", files.Length);

            // 5s debounce before commit
            await Task.Delay(5000);

            foreach (var file in files)
            {
                try
                {
                    await CommitFileAsync(file);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Failed to commit file: {File}", file);
                }
            }

            FilesChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            Interlocked.Exchange(ref _isCommitting, 0);
        }
    }

    private async Task CommitFileAsync(string filePath)
    {
        if (_currentRepo == null) return;
        if (string.IsNullOrEmpty(Path.GetDirectoryName(filePath))) return;

        var fileName = Path.GetFileName(filePath);
        var parentDir = Path.GetDirectoryName(filePath) ?? _currentRepo.Path;
        bool fileExists = File.Exists(filePath) || Directory.Exists(filePath);

        // Handle deleted files: svn delete + commit
        if (!fileExists)
        {
            // Check if file was actually tracked by SVN before attempting delete
            if (!_svnService.IsVersioned(filePath))
            {
                // File was never tracked by SVN, nothing to sync
                Log.Information("Deleted file was never tracked by SVN, skipping: {File}", filePath);
                return;
            }
            var delSuccess = await _svnService.DeleteAsync(filePath);
            if (!delSuccess)
            {
                Log.Warning("svn delete failed for {File}", filePath);
                _recordService.AddRecord(_currentRepo.Name, fileName, "Delete", "Failed", "Delete returned false");
                Notify($"删除同步失败: {fileName}");
                return;
            }
            var delCommit = await _svnService.CommitAsync(parentDir, $"Auto-sync: [Delete] {fileName}", _currentRepo.Username, _currentRepo.Password);
            if (delCommit)
            {
                Log.Information("Deleted and committed: {File}", filePath);
                _recordService.AddRecord(_currentRepo.Name, fileName, "Delete", "Success");
                Notify($"已同步删除: {fileName}");
            }
            else
            {
                _recordService.AddRecord(_currentRepo.Name, fileName, "Delete", "Failed", "Commit returned non-zero");
                Notify($"删除同步失败: {fileName}");
            }
            return;
        }

        var operation = Directory.Exists(filePath) ? "Add" : "Update";

        // svn add for unversioned files
        if (!IsSvnManaged(filePath))
        {
            await _svnService.AddFileAsync(filePath);
            operation = "Add";
        }

        var message = $"Auto-sync: [{operation}] {fileName}";
        var success = await _svnService.CommitAsync(
            Path.GetDirectoryName(filePath) ?? _currentRepo.Path,
            message,
            _currentRepo.Username,
            _currentRepo.Password);

        if (success)
        {
            Log.Information("Committed: {File}", filePath);
            _recordService.AddRecord(_currentRepo.Name, fileName, operation, "Success");
            Notify($"已同步: {fileName}");
        }
        else
        {
            // Retry up to _maxRetries times with delay
            var attempts = _failedFileAttempts.GetOrAdd(filePath, 0) + 1;
            _failedFileAttempts[filePath] = attempts;
            if (attempts < _maxRetries)
            {
                Log.Warning("Commit failed for {File}, will retry ({Attempts}/{Max})", filePath, attempts, _maxRetries);
                AddPendingUpdate(filePath);
            }
            else
            {
                _recordService.AddRecord(_currentRepo.Name, fileName, operation, "Failed", "Commit returned non-zero after retries");
                Notify($"同步失败: {fileName}");
                _failedFileAttempts.TryRemove(filePath, out _);
            }
        }
    }

    private bool IsSvnManaged(string path)
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


    private async void OnFullSyncTimerElapsed(object? sender, ElapsedEventArgs e)
    {
        if (_currentRepo == null) return;
        Log.Information("[FullSync] Starting full sync scan for {Name}", _currentRepo.Name);
        try
        {
            await FullSyncAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[FullSync] Full sync failed");
        }
    }

    /// <summary>
    /// Full sync: scan all changes via svn status and commit everything in one shot.
    /// This acts as a safety net for changes that FileWatcher may have missed.
    /// </summary>
    private async Task FullSyncAsync()
    {
        if (_currentRepo == null) return;

        var statuses = await _svnService.GetStatusAsync(_currentRepo.Path);
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
                        Log.Information("[FullSync] Added: {Path}", path);
                        anyChange = true;
                    }
                    else
                    {
                        Log.Warning("[FullSync] Failed to add {Path}", path);
                    }
                    break;
                }
                case FileSvnStatus.Missing:
                {
                    var delSuccess = await _svnService.DeleteAsync(path);
                    if (delSuccess)
                    {
                        Log.Information("[FullSync] Marked deleted: {Path}", path);
                        anyChange = true;
                    }
                    else
                    {
                        Log.Warning("[FullSync] Failed to delete {Path}", path);
                    }
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
            var updateSuccess = await _svnService.UpdateAsync(_currentRepo.Path);
            if (updateSuccess)
            {
                // Check for conflicts
                var conflictedFiles = await _svnService.GetConflictedFilesAsync(_currentRepo.Path);
                if (conflictedFiles.Count > 0)
                {
                    var handled = await HandleConflictsAsync();
                    if (handled > 0)
                    {
                        _recordService.AddRecord(_currentRepo.Name, _currentRepo.Path, "ConflictResolved", "Success", $"Resolved {handled} conflict(s) by Last-Write-Wins");
                        FilesChanged?.Invoke(this, EventArgs.Empty);
                    }
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
    /// Detects and resolves conflicted files using Last-Write-Wins strategy.
    /// Compares local file's last write time vs SVN revision timestamp.
    /// If local is newer → keep local, mark resolved. If server is newer → accept theirs.
    /// </summary>
    private async Task<int> HandleConflictsAsync()
    {
        if (_currentRepo == null) return 0;

        int handled = 0;

        try
        {
            var conflictedFiles = await _svnService.GetConflictedFilesAsync(_currentRepo.Path);
            Log.Information("Found {Count} conflicted files", conflictedFiles.Count);

            foreach (var filePath in conflictedFiles)
            {
                try
                {
                    if (!File.Exists(filePath)) continue;

                    var localTime = File.GetLastWriteTimeUtc(filePath);
                    var serverTime = await _svnService.GetLastChangedTimeAsync(filePath);

                    bool localNewer = localTime > serverTime;
                    Log.Information("Conflict on {File}: local={Local}, server={Server} → {Winner} wins",
                        Path.GetFileName(filePath), localTime, serverTime, localNewer ? "local" : "server");

                    var parentDir = Path.GetDirectoryName(filePath) ?? _currentRepo.Path;

                    if (localNewer)
                    {
                        // Keep local: accept working, then commit
                        await _svnService.ResolveAsync(filePath, SvnAccept.Working);
                        await _svnService.CommitAsync(parentDir, $"Auto-sync: [Conflict Resolved - Keep Local] {Path.GetFileName(filePath)}");
                    }
                    else
                    {
                        // Keep server: update + resolve theirs-full
                        await _svnService.UpdateAsync(filePath);
                        await _svnService.ResolveAsync(filePath, SvnAccept.TheirsFull);
                    }

                    handled++;
                    _recordService.AddRecord(_currentRepo.Name, Path.GetFileName(filePath), "ConflictResolved", "Success",
                        localNewer ? "Local kept (Last-Write-Wins)" : "Server kept (Last-Write-Wins)");
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Failed to resolve conflict: {File}", filePath);
                    _recordService.AddRecord(_currentRepo.Name, Path.GetFileName(filePath), "ConflictResolved", "Failed", ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error handling conflicts");
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
    }
}
