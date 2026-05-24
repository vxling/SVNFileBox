#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Timers;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using SharpSvn;
using System.Text.Json;
using SVNFileBox.Models;
using SVNFileBox.Services;
using Serilog;

namespace SVNFileBox.Services;

/// <summary>
/// Sync engine for SVN upward (local→server) and downward (server→local) operations.
///
/// Takes explicit dependencies so it is fully isolated per-repository
/// (no IRepositoryContext, no shared state between RepoManager instances).
/// </summary>
public class SyncService : IDisposable
{
    private readonly ISvnCommandExecutor _syncExecutor;
    private readonly SyncRecordService _recordService;
    private readonly SvnService _svnService;
    private readonly FileWatcherService _fileWatcher;
    private Repository _repository;

    private readonly System.Timers.Timer _pollTimer;
    private readonly System.Timers.Timer _fullSyncTimer;
    private readonly ConcurrentDictionary<string, int> _failedFileAttempts = new();

    private int _isPolling;
    private int _isSyncing;
    private int _staleCounter;  // counts consecutive polls where serverRev == localRev
    private CancellationTokenSource? _cts;

    public event EventHandler<string>? SyncNotification;
    public event EventHandler? FilesChanged;
    public event EventHandler<List<ConflictedFileInfo>>? ConflictDetected;

    /// <summary>
    /// Creates a SyncService for the given executor and repository.
    /// </summary>
    public SyncService(
        ISvnCommandExecutor executor,
        SyncRecordService recordService,
        SvnService svnService,
        FileWatcherService fileWatcher,
        Repository repository)
    {
        _syncExecutor = executor;
        _recordService = recordService;
        _svnService = svnService;
        _fileWatcher = fileWatcher;
        _repository = repository;

        // Poll timer (downward sync: SVN server → local)
        _pollTimer = new System.Timers.Timer(60_000);
        _pollTimer.Elapsed += OnPollTimerElapsed;
        _pollTimer.AutoReset = true;

        // Full sync timer (safety net every 15 min)
        _fullSyncTimer = new System.Timers.Timer(15 * 60 * 1000);
        _fullSyncTimer.Elapsed += OnFullSyncTimerElapsed;
        _fullSyncTimer.AutoReset = true;

        // Subscribe to per-file transfer activity from SvnService and record each file
        _svnService.FileTransferActivity += (path, action) =>
        {
            var repoName = _repository?.Name;
            if (string.IsNullOrEmpty(repoName) || string.IsNullOrEmpty(path)) return;
            _recordService.AddRecord(repoName, path, action, "Success");
        };

        Log.Information("[SyncService] Created for {Name}", repository.Name);
    }

    /// <summary>Updates the repository reference (called after repo switch).</summary>
    public void SetRepository(Repository repository)
    {
        _repository = repository;
    }

    #region Start / Stop

    /// <summary>
    /// Starts the sync engine for a repository.
    /// Called by RepoManager.Focus() after starting the FileWatcher and executor.
    /// </summary>
    public void StartSync(Repository repo)
    {
        _cts = new CancellationTokenSource();
        _pollTimer.Start();
        _fullSyncTimer.Start();

        // 切换仓库后立即触发一次下行同步（拉取服务器最新版本），避免遗漏其他设备的修改
        _ = PollCoreAsync();

        Log.Information("[SyncService] Timers started for {Name}", repo.Name);
    }

    public void StopSync()
    {
        _pollTimer.Stop();
        _fullSyncTimer.Stop();
        Log.Information("[SyncService] Timers stopped");
    }

    /// <summary>
    /// Drains all in-flight operations (ScanAndCommit, HeavyWrite tasks).
    /// Returns when _isSyncing transitions to 0, or after 30s timeout.
    /// Called by RepoManager.DismissAsync() before switching away.
    /// </summary>
    public async Task DrainAsync()
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (_isSyncing != 0 || _isPolling != 0)
        {
            if (DateTime.UtcNow > deadline)
            {
                Log.Warning("[SyncService] DrainAsync timed out (isSyncing={Syncing}, isPolling={Polling})",
                    _isSyncing, _isPolling);
                return;
            }
            await Task.Delay(200);
        }
        Log.Debug("[SyncService] DrainAsync complete");
    }

    /// <summary>
    /// Initiates immediate cancellation of any in-flight SVN operations.
    /// Called by RepoManager.Shutdown() for hard shutdown.
    /// </summary>
    public void Cancel()
    {
        _cts?.Cancel();
        StopSync();
        Log.Information("[SyncService] Cancel signal sent");
    }

    /// <summary>Triggers an immediate ScanAndCommit.</summary>
    public async Task SyncNowAsync() => await ScanAndCommitAsync();

    /// <summary>
    /// Temporarily disables the FileWatcher to prevent change events during bulk operations
    /// such as a file copy.  Call ReEnableFileWatcher() when done.
    /// </summary>
    public void DisableFileWatcher() => _fileWatcher?.Disable();

    /// <summary>
    /// Re-enables the FileWatcher after a bulk operation.
    /// </summary>
    public void ReEnableFileWatcher() => _fileWatcher?.Enable();

    #endregion

    #region ---- Enqueue Operations ----

    public void EnqueueAddAsync(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        _ = _syncExecutor.ExecuteAsync(SvnCommand.Add, path);
        Log.Information("[SyncService] Enqueued Add: {Path}", path);
    }

    public void EnqueueDeleteAsync(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        _ = _syncExecutor.ExecuteAsync(SvnCommand.Delete, path);
        Log.Information("[SyncService] Enqueued Delete: {Path}", path);
    }

    public void EnqueueMove(string fromPath, string toPath)
    {
        if (string.IsNullOrEmpty(fromPath) || string.IsNullOrEmpty(toPath)) return;
        _ = _syncExecutor.ExecuteAsync(SvnCommand.Delete, fromPath);
        _ = _syncExecutor.ExecuteAsync(SvnCommand.Add, toPath);
        Log.Information("[SyncService] Enqueued Move: {From} → {To}", fromPath, toPath);
    }

    public void EnqueueModify(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        Log.Information("[SyncService] Modify detected (no action): {Path}", path);
    }

    /// <summary>
    /// Analyzes a file path detected by FileWatcher and enqueues the appropriate operation.
    /// </summary>
    public void EnqueueFileChangeAsync(string path)
    {
        if (string.IsNullOrEmpty(Path.GetDirectoryName(path))) return;

        bool fileExists = File.Exists(path) || Directory.Exists(path);

        if (!fileExists)
        {
            var verResult = _syncExecutor.ExecuteAsync(SvnCommand.IsVersioned, path).Result;
            if (!verResult.Success || verResult.Value != "true")
            {
                Log.Debug("[SyncService] Skipping untracked missing file: {File}", path);
                return;
            }
            _ = _syncExecutor.ExecuteAsync(SvnCommand.Delete, path);
            Log.Information("[SyncService] FileWatcher: Deleted, Path: {File}", path);
            return;
        }

        var result = _syncExecutor.ExecuteAsync(SvnCommand.IsVersioned, path).Result;
        bool isVersioned = result.Success && result.Value == "true";

        if (!isVersioned)
        {
            _ = _syncExecutor.ExecuteAsync(SvnCommand.Add, path);
            Log.Information("[SyncService] FileWatcher: Added, Path: {File}", path);
        }
        else
        {
            Log.Information("[SyncService] FileWatcher: Modified, Path: {File}", path);
        }
    }

    public void EnqueueCommitForWorkingCopy(string workingCopyPath)
    {
        if (string.IsNullOrEmpty(workingCopyPath)) return;
        _ = _syncExecutor.ExecuteAsync(SvnCommand.Add, workingCopyPath);
        Log.Information("[SyncService] Enqueued Add (working copy): {Path}", workingCopyPath);
    }

    #endregion

    #region ---- ScanAndCommit: svn status → batch Commit via HeavyWrite ----

    private async Task ScanAndCommitAsync()
    {
        var repo = _repository;
        if (repo == null) return;
        if (Interlocked.CompareExchange(ref _isSyncing, 1, 0) != 0)
        {
            Log.Debug("[ScanAndCommit] Sync already in progress, skipping");
            return;
        }

        try
        {
            var repoPath = repo.Path;
            var statusResult = await _syncExecutor.ExecuteAsync(SvnCommand.Status, repoPath, depth: true);
            if (!statusResult.Success)
            {
                Log.Warning("[ScanAndCommit] svn status failed: {Error}", statusResult.Error);
                return;
            }

            var statusesRaw = statusResult.Value ?? "{}";
            var statuses = JsonSerializer.Deserialize<Dictionary<string, FileSvnStatus>>(statusesRaw) ?? new();

            if (statuses.Count == 0)
            {
                Log.Debug("[ScanAndCommit] No changes to commit");
                return;
            }

            var unversionedFiles = statuses
                .Where(kv => kv.Value == FileSvnStatus.Unversioned)
                .ToList();
            var versionedChanges = statuses
                .Where(kv => kv.Value != FileSvnStatus.Conflicted
                          && kv.Value != FileSvnStatus.Unversioned)
                .Select(kv => (Key: kv.Key.Replace('\\', '/'), Value: kv.Value))
                .ToList();

            foreach (var (filePath, _) in unversionedFiles)
            {
                _ = _syncExecutor.ExecuteAsync(SvnCommand.Add, filePath);
                Log.Debug("[ScanAndCommit] Enqueued Add for unversioned: {Path}", filePath);
            }

            // Group by directory, filtering out paths outside the repo root
            var normalizedRepoPathForCommit = repoPath.Replace('\\', '/').TrimEnd('/');
            var inRepoVersionedChanges = versionedChanges
                .Where(kv => kv.Key.StartsWith(normalizedRepoPathForCommit + '/') || kv.Key == normalizedRepoPathForCommit)
                .ToList();

            // Map parent dir of root-level files to repo root itself
            var dirGroups = inRepoVersionedChanges
                .Select(kv =>
                {
                    // If the last segment has no '.', it's likely a directory — use kv.Key as dir directly
                    var lastSegment = Path.GetFileName(kv.Key);
                    var parent = lastSegment.Contains('.') ? Path.GetDirectoryName(kv.Key)?.Replace('\\', '/') : kv.Key;
                    return (key: string.IsNullOrEmpty(parent) ? normalizedRepoPathForCommit : parent, kv);
                })
                .GroupBy(x => x.key)
                .ToList();

            Log.Information("[ScanAndCommit] Committing {DirCount} dirs, {FileCount} files (+ {AddCount} unversioned)",
                dirGroups.Count, versionedChanges.Count, unversionedFiles.Count);

            // Process deepest dirs first (parent dirs after children to avoid "out of date" errors)
            var sortedGroups = dirGroups.OrderByDescending(g => g.Key.Split('\\', '/').Length);

            foreach (var group in sortedGroups)
            {
                var dirPath = string.IsNullOrEmpty(group.Key) ? repoPath : group.Key;
                var fileCount = group.Count();
                var message = fileCount == 1
                    ? $"Auto-sync: {Path.GetFileName(group.First().kv.Key)}"
                    : $"Auto-sync: {fileCount} files in {Path.GetFileName(dirPath)}";

                _ = _syncExecutor.ExecuteAsync(SvnCommand.Commit, dirPath, message: message);
                Log.Debug("[ScanAndCommit] Enqueued Commit for dir: {Dir}, {Count} files", dirPath, fileCount);
            }

            Notify("批量同步完成");
            FilesChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[ScanAndCommit] failed");
            Notify($"批量同步失败: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _isSyncing, 0);
        }
    }

    private void Notify(string message) => SyncNotification?.Invoke(this, message);

    #endregion

    #region ---- Timers ----

    private async void OnPollTimerElapsed(object? sender, ElapsedEventArgs e)
    {
        await PollCoreAsync();
    }

    private async void OnFullSyncTimerElapsed(object? sender, ElapsedEventArgs e)
    {
        var repo = _repository;
        if (repo == null) return;
        try
        {
            await WaitForPollAsync();
            if (Interlocked.CompareExchange(ref _isSyncing, 1, 0) != 0)
            {
                Log.Debug("[FullSync] Sync already in progress, skipping timer tick");
                return;
            }
            Log.Information("[FullSync] Starting full sync for {Name}", repo.Name);
            await ScanAndCommitAsync();
            Notify("定时全量同步完成");
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

    private async Task WaitForPollAsync()
    {
        while (Interlocked.CompareExchange(ref _isPolling, 0, 0) != 0)
            await Task.Delay(200);
    }

    #endregion

    #region ---- PollCore (downward sync) ----

    private async Task PollCoreAsync()
    {
        var repo = _repository;
        if (repo == null) return;
        if (Interlocked.CompareExchange(ref _isPolling, 1, 0) == 1) return;
        if (Interlocked.CompareExchange(ref _isSyncing, 0, 0) != 0)
        {
            Interlocked.Exchange(ref _isPolling, 0);
            Log.Debug("[PollCore] Skipping, full sync in progress");
            return;
        }

        try
        {
            var localRevResult = await _syncExecutor.ExecuteAsync(SvnCommand.GetRevision, repo.Path);
            var localRev = localRevResult.Success && int.TryParse(localRevResult.Value, out var lr) ? lr : -1;
            var serverRevResult = await _syncExecutor.ExecuteAsync(SvnCommand.GetHeadRevision,  repo.Url ?? "");
            var serverRev = serverRevResult.Success && int.TryParse(serverRevResult.Value, out var sr) ? sr : -1;

            Log.Debug("PollCheck: local={Local}, server={Server}", localRev, serverRev);

            // If server and local revisions match, check for incomplete working copy state
            // (e.g. previous update was interrupted by crash/power loss).
            // If incomplete items exist, force an update to repair the working copy.
            // Also track consecutive "no updates" polls: after 30 minutes (30 polls × 1 min), force a full update.
            bool hasIncomplete = false;
            if (serverRev == localRev)
            {
                _staleCounter++;
                if (_staleCounter >= 30)
                {
                    Log.Information("[PollCheck] Stale for {Count} consecutive polls, forcing full update", _staleCounter);
                    var result = await _syncExecutor.ExecuteAsync(SvnCommand.Update, repo.Path);
                    if (!result.Success)
                    {
                        _recordService.AddRecord(repo.Name, repo.Path, "Update", "Failed",
                            $"Stale refresh failed: {result.Error}");
                        Notify("Stale refresh: 强制更新失败");
                        _staleCounter = 0;
                        return;
                    }

                    var conflictInfo = await BuildConflictInfoListAsync(repo.Path);
                    if (conflictInfo.Count > 0)
                    {
                        ConflictDetected?.Invoke(this, conflictInfo);
                    }
                    else
                    {
                        _recordService.AddRecord(repo.Name, repo.Path, "Update", "Success",
                            "Stale refresh update");
                        Notify("Stale refresh: 已强制全量更新");
                        FilesChanged?.Invoke(this, EventArgs.Empty);
                    }
                    _staleCounter = 0;
                    return;
                }

                var statusResult = await _syncExecutor.ExecuteAsync(SvnCommand.Status, repo.Path, depth: true);
                if (statusResult.Success && !string.IsNullOrEmpty(statusResult.Value))
                {
                    try
                    {
                        var statuses = System.Text.Json.JsonSerializer
                            .Deserialize<Dictionary<string, SVNFileBox.Models.FileSvnStatus>>(statusResult.Value ?? "{}");
                        hasIncomplete = statuses?.Values.Any(s => s == SVNFileBox.Models.FileSvnStatus.Incomplete) ?? false;
                        if (hasIncomplete)
                            Log.Warning("[PollCheck] Incomplete items detected in working copy, forcing update to repair");
                    }
                    catch { /* ignore deserialization errors */ }
                }
            }
            else
            {
                // serverRev != localRev means there was an update, reset counter
                _staleCounter = 0;
            }

            var isRepairUpdate = hasIncomplete && serverRev == localRev;
            if (serverRev <= localRev && !isRepairUpdate) return;

            if (isRepairUpdate)
            {
                Log.Warning("[PollCheck] Incomplete items detected, forcing full update to repair working copy");
                var result = await _syncExecutor.ExecuteAsync(SvnCommand.Update, repo.Path);
                if (!result.Success)
                {
                    _recordService.AddRecord(repo.Name, repo.Path, "Update", "Failed",
                        $"Repair update failed: {result.Error}");
                    Notify("修复 update 失败");
                    return;
                }

                var conflictInfo = await BuildConflictInfoListAsync(repo.Path);
                if (conflictInfo.Count > 0)
                {
                    ConflictDetected?.Invoke(this, conflictInfo);
                }
                else
                {
                    _recordService.AddRecord(repo.Name, repo.Path, "Update", "Success",
                        "Update (repaired incomplete working copy)");
                    Notify("已修复 working copy 中的 incomplete 状态");
                    FilesChanged?.Invoke(this, EventArgs.Empty);
                }
                return;
            }

            Log.Information("Server has updates: local={Local}, server={Server}", localRev, serverRev);


            var updateResult = await UpdateInChunksAsync();
            var updateSuccess = updateResult.success;
            if (updateSuccess)
            {
                var conflictInfo = await BuildConflictInfoListAsync(repo.Path);
                if (conflictInfo.Count > 0)
                {
                    ConflictDetected?.Invoke(this, conflictInfo);
                }
                else
                {
                    var recordMsg = $"Updated from r{localRev} to r{serverRev}, {updateResult.fileCount} file(s) changed";
                    _recordService.AddRecord(
                        repo.Name, repo.Path, "Update", "Success", recordMsg);
                    Notify($"已从服务器更新 r{localRev} → r{serverRev}，共 {updateResult.fileCount} 个文件");
                    FilesChanged?.Invoke(this, EventArgs.Empty);
                }
            }
            else
            {
                _recordService.AddRecord(repo.Name, repo.Path, "Update", "Failed", "Update returned false");
                Notify("更新失败");
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

    private async Task<(bool success, int fileCount)> UpdateInChunksAsync()
    {
        var repo = _repository;

        // 1. Get list of remote-changed file paths
        var gsupResult = await _syncExecutor.ExecuteAsync(SvnCommand.GetServerUpdatePaths, repo.Path);
        var filePaths = gsupResult.Success && !string.IsNullOrEmpty(gsupResult.Value)
            ? gsupResult.Value!.Split(';', StringSplitOptions.RemoveEmptyEntries).ToList()
            : new List<string>();

        Log.Debug("[UpdateInChunks] GetServerUpdatePaths returned {Count} remote-changed paths", filePaths.Count);
        if (filePaths.Count == 0)
        {
            Log.Debug("[UpdateInChunks] No remote changes");
            return (true, 0);
        }

        // 2. Merge file list into unique parent directories
        //    Filter to only paths within the repo root (ignore externals/overflow)
        //    Normalize all paths to forward-slash for consistent comparison
        var normalizedRepoPath = repo.Path.Replace('\\', '/').TrimEnd('/');
        var inRepoPaths = filePaths
            .Select(p => p.Replace('\\', '/'))
            .Where(p => p.StartsWith(normalizedRepoPath + '/') || p == normalizedRepoPath)
            .ToList();

        // Compute parent dirs; root-file maps to repo root (normalizedRepoPath)
        var dirs = inRepoPaths
            .Select(p =>
            {
                // If the last segment has no '.', it's likely a directory (not a file),
                // so p is already the dir path and GetDirectoryName would incorrectly go above repo root.
                var lastSegment = Path.GetFileName(p);
                var dir = lastSegment.Contains('.') ? Path.GetDirectoryName(p)?.Replace('\\', '/') : p;
                return string.IsNullOrEmpty(dir) || dir == normalizedRepoPath ? normalizedRepoPath : dir;
            })
            .Distinct()
            .OrderByDescending(d => d.Split('\\', '/').Length)  // deepest dirs first
            .ToList();

        Log.Information("[UpdateInChunks] {FileCount} remote files → {DirCount} dirs (filtered from {RawCount})",
            inRepoPaths.Count, dirs.Count, filePaths.Count);

        // 3. Enqueue one Update per directory and wait for all to complete via TCS
        var tasks = dirs.Select(dir =>
            _syncExecutor.ExecuteUpdateAsync(repo.Path, new List<string> { dir }));

        var results = await Task.WhenAll(tasks);
        var allSuccess = results.All(r => r.Success);

        Log.Information("[UpdateInChunks] All Updates done: {DirCount} dirs, success={AllSuccess}",
            dirs.Count, allSuccess);
        return (allSuccess, inRepoPaths.Count);
    }


    private async Task<List<ConflictedFileInfo>> BuildConflictInfoListAsync(string repoPath)
    {
        var conflictInfo = new List<ConflictedFileInfo>();
        var cfResult = await _syncExecutor.ExecuteAsync(SvnCommand.GetConflictedFiles, repoPath);
        if (!cfResult.Success || string.IsNullOrEmpty(cfResult.Value))
            return conflictInfo;

        var conflictedFiles = cfResult.Value!.Split(';', StringSplitOptions.RemoveEmptyEntries);
        foreach (var filePath in conflictedFiles)
        {
            try
            {
                var localTime = System.IO.File.GetLastWriteTimeUtc(filePath).ToLocalTime();
                var serverTime = (await _svnService.GetLastChangedTimeAsync(filePath)).ToLocalTime();
                conflictInfo.Add(new ConflictedFileInfo
                {
                    FilePath = filePath,
                    LocalModifiedTime = localTime,
                    ServerModifiedTime = serverTime,
                    SelectedResolution = ConflictResolution.AcceptServer,
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to build conflict info for {File}", filePath);
            }
        }
        return conflictInfo;
    }

    public async Task<int> ApplyConflictResolutionsAsync(List<ConflictedFileInfo> conflictInfo)
    {
        var repo = _repository;
        if (repo == null) return 0;
        int handled = 0;

        foreach (var info in conflictInfo)
        {
            try
            {
                var parentDir = Path.GetDirectoryName(info.FilePath) ?? repo.Path;
                var fileName = Path.GetFileName(info.FilePath);

                switch (info.SelectedResolution)
                {
                    case ConflictResolution.KeepLocal:
                    {
                        var resolved = (await _syncExecutor.ExecuteAsync(SvnCommand.Resolve, info.FilePath, accept: SharpSvn.SvnAccept.MineFull)).Success;
                        if (!resolved) Log.Warning("Resolve(MineFull) returned false for {File}", info.FilePath);
                        var committed = (await _syncExecutor.ExecuteAsync(SvnCommand.Commit, parentDir,
                            message: $"Auto-sync: [Conflict Resolved — Kept Local] {fileName}")).Success;
                        Log.Information("Conflict KeepLocal: {File}, resolve={Resolved}, commit={Committed}",
                            info.FilePath, resolved, committed);
                        break;
                    }
                    case ConflictResolution.AcceptServer:
                    {
                        var resolved = (await _syncExecutor.ExecuteAsync(SvnCommand.Resolve, info.FilePath, accept: SharpSvn.SvnAccept.TheirsFull)).Success;
                        if (!resolved) Log.Warning("Resolve(TheirsFull) returned false for {File}", info.FilePath);
                        Log.Information("Conflict AcceptServer: {File}, resolved={Resolved}", info.FilePath, resolved);
                        break;
                    }
                    case ConflictResolution.KeepBoth:
                    {
                        var backupPath = info.FilePath + $".local-backup-{DateTime.UtcNow:yyyyMMddHHmmss}";
                        File.Copy(info.FilePath, backupPath, overwrite: true);
                        Log.Information("Conflict KeepBoth: copied {Original} → {Backup}", info.FilePath, backupPath);
                        var resolved = (await _syncExecutor.ExecuteAsync(SvnCommand.Resolve, info.FilePath, accept: SharpSvn.SvnAccept.TheirsFull)).Success;
                        Log.Information("Conflict KeepBoth: {File} accepted server, resolved={Resolved}",
                            info.FilePath, resolved);
                        break;
                    }
                }

                handled++;
                _recordService.AddRecord(
                    repo.Name, fileName, "ConflictResolved", "Success",
                    $"User chose: {info.SelectedResolution}");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to apply conflict resolution for {File}", info.FilePath);
                _recordService.AddRecord(
                    repo.Name, Path.GetFileName(info.FilePath), "ConflictResolved", "Failed", ex.Message);
            }
        }
        return handled;
    }

    #endregion

    /// <summary>
    /// Called by RepoManager to record a file transfer event from SvnService.
    /// </summary>
    public void RecordFileTransfer(string path, string action)
    {
        var repoName = _repository?.Name;
        if (string.IsNullOrEmpty(repoName) || string.IsNullOrEmpty(path)) return;
        _recordService.AddRecord(repoName, path, action, "Success");
    }

    public void Dispose()
    {
        Cancel();
        _pollTimer.Stop(); _pollTimer.Dispose();
        _fullSyncTimer.Stop(); _fullSyncTimer.Dispose();
        Log.Information("[SyncService] Disposed");
    }
}