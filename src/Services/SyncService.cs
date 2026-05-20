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
/// Dependency: IRepositoryContext (provides executor, FileWatcher events, repo state).
/// All SVN operations flow through _repoContext.Executor.
/// FileWatcher events arrive via _repoContext.FilesChangedForSync.
/// </summary>
public class SyncService : IDisposable
{
    private readonly IRepositoryContext _repoContext;
    private readonly SyncRecordService _recordService;

    private readonly System.Timers.Timer _pollTimer;
    private readonly System.Timers.Timer _fullSyncTimer;
    private readonly ConcurrentDictionary<string, int> _failedFileAttempts = new();

    private int _isPolling;
    private int _isSyncing;

    public event EventHandler<string>? SyncNotification;
    public event EventHandler? FilesChanged;
    public event EventHandler<List<ConflictedFileInfo>>? ConflictDetected;

    public SyncService(IRepositoryContext repoContext, SyncRecordService recordService)
    {
        _repoContext = repoContext;
        _recordService = recordService;

        // Poll timer (downward sync: SVN server → local)
        _pollTimer = new System.Timers.Timer(60_000);
        _pollTimer.Elapsed += OnPollTimerElapsed;
        _pollTimer.AutoReset = true;

        // Full sync timer (safety net every 15 min)
        _fullSyncTimer = new System.Timers.Timer(15 * 60 * 1000);
        _fullSyncTimer.Elapsed += OnFullSyncTimerElapsed;
        _fullSyncTimer.AutoReset = true;

        // Wire FileWatcher events from RepositoryContext → enqueue path changes
        _repoContext.FilesChangedForSync += OnFilesChanged;

        // Subscribe to per-file transfer activity from SvnService and record each file
        SvnService.FileTransferActivity += (path, action) =>
        {
            var repoName = _repoContext.CurrentRepository?.Name;
            if (string.IsNullOrEmpty(repoName) || string.IsNullOrEmpty(path)) return;
            _recordService.AddRecord(repoName, path, action, "Success");
        };

        Log.Information("SyncService created with RepositoryContext");
    }

    #region Start / Stop

    /// <summary>
    /// Starts the sync engine for a repository.
    /// Called after RepositoryContext.SwitchTo() has already started the FileWatcher and executor.
    /// </summary>
    public void StartSync(Repository repo)
    {
        _pollTimer.Start();
        _fullSyncTimer.Start();

        // Trigger immediate scan so uncommitted local changes from previous session
        // are picked up and queued immediately.
        _ = ScanAndCommitAsync();

        Log.Information("Sync timers started for {Name}", repo.Name);
    }

    public void StopSync()
    {
        _pollTimer.Stop();
        _fullSyncTimer.Stop();
        Log.Information("Sync timers stopped");
    }

    /// <summary>Triggers an immediate ScanAndCommit.</summary>
    public async Task SyncNowAsync() => await ScanAndCommitAsync();

    public void DisableFileWatcher() => _repoContext.DisableFileWatcher();
    public void ReEnableFileWatcher() => _repoContext.ReEnableFileWatcher();

    #endregion

    #region ---- Enqueue Operations ----

    public void EnqueueAddAsync(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        _ = _repoContext.Executor.ExecuteAsync(SvnCommand.Add, path);
        Log.Information("[SyncService] Enqueued Add: {Path}", path);
    }

    public void EnqueueDeleteAsync(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        _ = _repoContext.Executor.ExecuteAsync(SvnCommand.Delete, path);
        Log.Information("[SyncService] Enqueued Delete: {Path}", path);
    }

    public void EnqueueMove(string fromPath, string toPath)
    {
        if (string.IsNullOrEmpty(fromPath) || string.IsNullOrEmpty(toPath)) return;
        _ = _repoContext.Executor.ExecuteAsync(SvnCommand.Delete, fromPath);
        _ = _repoContext.Executor.ExecuteAsync(SvnCommand.Add, toPath);
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
            var verResult = _repoContext.Executor.ExecuteAsync(SvnCommand.IsVersioned, path).Result;
            if (!verResult.Success || verResult.Value != "true")
            {
                Log.Debug("[SyncService] Skipping untracked missing file: {File}", path);
                return;
            }
            _ = _repoContext.Executor.ExecuteAsync(SvnCommand.Delete, path);
            Log.Information("[SyncService] FileWatcher: Deleted, Path: {File}", path);
            return;
        }

        var result = _repoContext.Executor.ExecuteAsync(SvnCommand.IsVersioned, path).Result;
        bool isVersioned = result.Success && result.Value == "true";

        if (!isVersioned)
        {
            _ = _repoContext.Executor.ExecuteAsync(SvnCommand.Add, path);
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
        _ = _repoContext.Executor.ExecuteAsync(SvnCommand.Add, workingCopyPath);
        Log.Information("[SyncService] Enqueued Add (working copy): {Path}", workingCopyPath);
    }

    #endregion

    #region ---- ScanAndCommit: svn status → batch Commit via HeavyWrite ----

    private async Task ScanAndCommitAsync()
    {
        var repo = _repoContext.CurrentRepository;
        if (repo == null) return;
        if (Interlocked.CompareExchange(ref _isSyncing, 1, 0) != 0)
        {
            Log.Debug("[ScanAndCommit] Sync already in progress, skipping");
            return;
        }

        try
        {
            var repoPath = repo.Path;
            var statusResult = await _repoContext.Executor.ExecuteAsync(SvnCommand.Status, repoPath);
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
                .ToList();

            foreach (var (filePath, _) in unversionedFiles)
            {
                _ = _repoContext.Executor.ExecuteAsync(SvnCommand.Add, filePath);
                Log.Debug("[ScanAndCommit] Enqueued Add for unversioned: {Path}", filePath);
            }

            var dirGroups = versionedChanges
                .GroupBy(kv => Path.GetDirectoryName(kv.Key) ?? "")
                .ToList();

            Log.Information("[ScanAndCommit] Committing {DirCount} dirs, {FileCount} files (+ {AddCount} unversioned)",
                dirGroups.Count, versionedChanges.Count, unversionedFiles.Count);

            foreach (var group in dirGroups)
            {
                var dirPath = string.IsNullOrEmpty(group.Key) ? repoPath : group.Key;
                var fileCount = group.Count();
                var message = fileCount == 1
                    ? $"Auto-sync: {Path.GetFileName(group.First().Key)}"
                    : $"Auto-sync: {fileCount} files in {Path.GetFileName(dirPath)}";

                _ = _repoContext.Executor.ExecuteAsync(SvnCommand.Commit, dirPath, message: message);
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

    #endregion

    #region ---- FileWatcher event handler ----

    private void OnFilesChanged(object? sender, EventArgs e)
    {
        // FileWatcher debounces, so we process the batch here.
        // EnqueueFileChangeAsync decides Add/Delete/Modify per file.
        // This is called from RepositoryContext's FileWatcher event.
        Log.Debug("[SyncService] FileWatcher change batch received");
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
        var repo = _repoContext.CurrentRepository;
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
        var repo = _repoContext.CurrentRepository;
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
            var localRevResult = await _repoContext.Executor.ExecuteAsync(SvnCommand.GetRevision, repo.Path);
            var localRev = localRevResult.Success && int.TryParse(localRevResult.Value, out var lr) ? lr : -1;
            var serverRevResult = await _repoContext.Executor.ExecuteAsync(SvnCommand.GetHeadRevision,
                repo.Url ?? "", username: repo.Username, password: repo.Password);
            var serverRev = serverRevResult.Success && int.TryParse(serverRevResult.Value, out var sr) ? sr : -1;

            Log.Debug("PollCheck: local={Local}, server={Server}", localRev, serverRev);
            if (serverRev <= localRev) return;

            Log.Information("Server has updates: local={Local}, server={Server}", localRev, serverRev);

            var updateSuccess = await UpdateInChunksAsync();
            if (updateSuccess)
            {
                var conflictInfo = await BuildConflictInfoListAsync(repo.Path);
                if (conflictInfo.Count > 0)
                {
                    ConflictDetected?.Invoke(this, conflictInfo);
                }
                else
                {
                    _recordService.AddRecord(
                        repo.Name, repo.Path, "Update", "Success",
                        $"Updated {serverRev - localRev} revision(s)");
                    Notify($"已从服务器更新 {serverRev - localRev} 个版本");
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

    private async Task<bool> UpdateInChunksAsync()
    {
        var repo = _repoContext.CurrentRepository!;

        // 1. Get list of remote-changed file paths
        var gsupResult = await _repoContext.Executor.ExecuteAsync(SvnCommand.GetServerUpdatePaths, repo.Path);
        var filePaths = gsupResult.Success && !string.IsNullOrEmpty(gsupResult.Value)
            ? gsupResult.Value!.Split(';', StringSplitOptions.RemoveEmptyEntries).ToList()
            : new List<string>();

        Log.Debug("[UpdateInChunks] GetServerUpdatePaths returned {Count} remote-changed paths", filePaths.Count);
        if (filePaths.Count == 0)
        {
            Log.Debug("[UpdateInChunks] No remote changes");
            return true;
        }

        // 2. Merge file list into unique parent directories
        //    (same directory → one Update task, deduplication removes duplicates)
        var dirs = filePaths
            .Select(p => Path.GetDirectoryName(p)?.Replace('\\', '/') ?? ".")
            .Distinct()
            .ToList();

        Log.Information("[UpdateInChunks] {FileCount} files → {DirCount} unique dirs to update",
            filePaths.Count, dirs.Count);

        // 3. Enqueue one Update per directory and wait for all to complete via TCS
        // 3. Enqueue one Update per directory and wait for all to complete via TCS
        var tasks = dirs.Select(dir =>
            _repoContext.Executor.ExecuteUpdateAsync(repo.Path, new List<string> { dir }));

        var results = await Task.WhenAll(tasks);
        var allSuccess = results.All(r => r.Success);

        Log.Information("[UpdateInChunks] All Updates done: {DirCount} dirs, success={AllSuccess}",
            dirs.Count, allSuccess);
        return allSuccess;
    }


    private async Task<List<ConflictedFileInfo>> BuildConflictInfoListAsync(string repoPath)
    {
        var conflictInfo = new List<ConflictedFileInfo>();
        var cfResult = await _repoContext.Executor.ExecuteAsync(SvnCommand.GetConflictedFiles, repoPath);
        if (!cfResult.Success || string.IsNullOrEmpty(cfResult.Value))
            return conflictInfo;

        var conflictedFiles = cfResult.Value!.Split(';', StringSplitOptions.RemoveEmptyEntries);
        foreach (var filePath in conflictedFiles)
        {
            try
            {
                conflictInfo.Add(new ConflictedFileInfo
                {
                    FilePath = filePath,
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
        var repo = _repoContext.CurrentRepository;
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
                        var resolved = (await _repoContext.Executor.ExecuteAsync(SvnCommand.Resolve, info.FilePath)).Success;
                        if (!resolved) Log.Warning("Resolve(MineFull) returned false for {File}", info.FilePath);
                        var committed = (await _repoContext.Executor.ExecuteAsync(SvnCommand.Commit, parentDir,
                            message: $"Auto-sync: [Conflict Resolved — Kept Local] {fileName}")).Success;
                        Log.Information("Conflict KeepLocal: {File}, resolve={Resolved}, commit={Committed}",
                            info.FilePath, resolved, committed);
                        break;
                    }
                    case ConflictResolution.AcceptServer:
                    {
                        var resolved = (await _repoContext.Executor.ExecuteAsync(SvnCommand.Resolve, info.FilePath)).Success;
                        if (!resolved) Log.Warning("Resolve(TheirsFull) returned false for {File}", info.FilePath);
                        Log.Information("Conflict AcceptServer: {File}, resolved={Resolved}", info.FilePath, resolved);
                        break;
                    }
                    case ConflictResolution.KeepBoth:
                    {
                        var backupPath = info.FilePath + $".local-backup-{DateTime.UtcNow:yyyyMMddHHmmss}";
                        File.Copy(info.FilePath, backupPath, overwrite: true);
                        Log.Information("Conflict KeepBoth: copied {Original} → {Backup}", info.FilePath, backupPath);
                        var resolved = (await _repoContext.Executor.ExecuteAsync(SvnCommand.Resolve, info.FilePath)).Success;
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

    public void Dispose()
    {
        _repoContext.FilesChangedForSync -= OnFilesChanged;
        _pollTimer.Stop(); _pollTimer.Dispose();
        _fullSyncTimer.Stop(); _fullSyncTimer.Dispose();
        Log.Information("[SyncService] Disposed");
    }
}