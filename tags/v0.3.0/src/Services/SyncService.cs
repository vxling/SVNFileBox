#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Timers;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using SVNFileBox.Models;
using Serilog;

namespace SVNFileBox.Services;

public class SyncService : IDisposable
{
    private static readonly Regex _lastChangedDateRegex = new(@"Last Changed Date: (.+)", RegexOptions.Compiled);
    private static readonly Regex _lockFileRegex = new(@"([A-Za-z]:[^ ""']+|\S+\.xlsx|\S+\.xls|\S+\.docx|\S+\.doc)", RegexOptions.Compiled);
    private static readonly Regex _repoUrlRegex = new(@"^URL:\s*(.+)$", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex _revisionRegex = new(@"^Revision:\s*(\d+)$", RegexOptions.Compiled | RegexOptions.Multiline);
    private readonly ConfigService _configService;
    private readonly SvnService _svnService = new();
    private readonly FileWatcherService _fileWatcher = new();
    private readonly SyncRecordService _recordService;
    private readonly System.Timers.Timer _pollTimer;
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

        Log.Information("SyncService created with poll interval {IntervalMs}ms", _pollIntervalMs);
    }

    public void StartSync(Repository repo)
    {
        _currentRepo = repo;
        _fileWatcher.StartWatching(repo.Path);
        _pollTimer.Start();
        Log.Information("Sync started for {Name} at {Path}", repo.Name, repo.Path);
    }

    public void StopSync()
    {
        _fileWatcher.StopWatching();
        _pollTimer.Stop();
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
        if (!File.Exists(filePath) && !Directory.Exists(filePath)) return;

        var fileName = Path.GetFileName(filePath);
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
            var result = await _svnService.RunCommandAsync($"update --non-interactive \"{_currentRepo.Path}\"");
            if (result.exitCode == 0)
            {
                _recordService.AddRecord(_currentRepo.Name, _currentRepo.Path, "Update", "Success", $"Updated {serverRev - localRev} revision(s)");
                Notify($"已从服务器更新 {serverRev - localRev} 个版本");
                FilesChanged?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                var error = result.error;
                if (error.Contains("locked") || error.Contains("locked by"))
                {
                    var lockedFiles = ExtractLockedFiles(error);
                    foreach (var file in lockedFiles)
                        AddPendingUpdate(file);
                    _recordService.AddRecord(_currentRepo.Name, _currentRepo.Path, "Update", "Skipped", "Some files are locked");
                    Notify("部分文件被占用，已跳过，将在下次重试");
                }
                else if (error.Contains("conflict") || result.output.Contains("C ") || result.output.Contains("conflicted"))
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
                    _recordService.AddRecord(_currentRepo.Name, _currentRepo.Path, "Update", "Failed", error);
                    Notify($"更新失败: {error}");
                }
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
                var result = await _svnService.RunCommandAsync($"update --non-interactive \"{file}\"");

                if (result.exitCode == 0)
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

    // Note: check-then-act inside lock is safe — prevents duplicate entries
    // even if multiple threads call AddPendingUpdate simultaneously.
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

    private List<string> ExtractLockedFiles(string errorOutput)
    {
        var files = new List<string>();
        var lines = errorOutput.Split('\n');
        foreach (var line in lines)
        {
            if (line.Contains("locked") || line.Contains("lock"))
            {
                var match = _lockFileRegex.Match(line);
                if (match.Success)
                {
                    var file = match.Value.Trim('\'', '"', ' ');
                    if (File.Exists(file))
                        files.Add(file);
                }
            }
        }
        return files;
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
            // Get list of conflicted files (status starts with 'C')
            var statusResult = await _svnService.RunCommandAsync($"status --non-interactive \"{_currentRepo.Path}\"");
            if (statusResult.exitCode != 0) return 0;

            var conflictedFiles = new List<string>();
            var lines = statusResult.output.Split('\n');
            foreach (var line in lines)
            {
                if (line.Length > 8 && line[0] == 'C')
                {
                    var relPath = line.Substring(8).Trim();
                    if (!string.IsNullOrEmpty(relPath))
                        conflictedFiles.Add(Path.Combine(_currentRepo.Path, relPath));
                }
            }

            Log.Information("Found {Count} conflicted files", conflictedFiles.Count);

            foreach (var filePath in conflictedFiles)
            {
                try
                {
                    if (!File.Exists(filePath)) continue;

                    var localTime = File.GetLastWriteTimeUtc(filePath);

                    // Get server file's last changed date via svn info
                    var infoResult = await _svnService.RunCommandAsync($"info --non-interactive \"{filePath}\"");
                    DateTime serverTime = DateTime.MinValue;
                    if (infoResult.exitCode == 0)
                    {
                        var dateMatch = _lastChangedDateRegex.Match(infoResult.output);
                        if (dateMatch.Success)
                        {
                            var dateStr = dateMatch.Groups[1].Value.Trim();
                            // SVN date format: "2026-04-28 14:30:00 +0800"
                            if (DateTime.TryParseExact(dateStr, "yyyy-MM-dd HH:mm:ss zzz",
                                System.Globalization.CultureInfo.InvariantCulture,
                                System.Globalization.DateTimeStyles.AssumeUniversal,
                                out var parsed))
                                serverTime = parsed.ToUniversalTime();
                            else if (DateTime.TryParse(dateStr, out parsed))
                                serverTime = parsed.ToUniversalTime();
                        }
                    }

                    bool localNewer = localTime > serverTime;
                    Log.Information("Conflict on {File}: local={Local}, server={Server} → {Winner} wins",
                        Path.GetFileName(filePath), localTime, serverTime, localNewer ? "local" : "server");

                    var parentDir = Path.GetDirectoryName(filePath) ?? _currentRepo.Path;

                    if (localNewer)
                    {
                        // Keep local: accept working to clear conflict markers, then commit
                        await _svnService.RunCommandAsync($"resolve --non-interactive --accept working \"{filePath}\"");
                        await _svnService.CommitAsync(parentDir, $"Auto-sync: [Conflict Resolved - Keep Local] {Path.GetFileName(filePath)}");
                    }
                    else
                    {
                        // Keep server: accept theirs-full
                        await _svnService.RunCommandAsync($"update --non-interactive --accept theirs-full \"{filePath}\"");
                        await _svnService.RunCommandAsync($"resolve --non-interactive --accept theirs-full \"{filePath}\"");
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
    }
}
