#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using SVNFileBox.Models;
using Serilog;

namespace SVNFileBox.Services;

/// <summary>
/// Manages the active repository session:
///   - Single FileWatcher lifecycle (start/stop on repo switch)
///   - SVN executor lifecycle (Start/Stop)
///   - Credential state
///   - Repository open/checkout
/// 
/// One instance per application session. All SVN write operations flow
/// through the embedded executor.
/// </summary>
public class RepositoryContext : IRepositoryContext, IDisposable
{
    private SvnCommandExecutor _executor = new();
    private readonly FileWatcherService _fileWatcher = new();
    private readonly SvnService _svnService = new();

    private string _currentPath = "";
    private string _username = "";
    private string _password = "";
    private Repository? _currentRepo;
    private bool _syncRunning;

    public string CurrentPath => _currentPath;
    public bool HasCredentials => !string.IsNullOrWhiteSpace(_username) && !string.IsNullOrWhiteSpace(_password);
    public Repository? CurrentRepository => _currentRepo;
    public ISvnCommandExecutor Executor => _executor;

    public event EventHandler<string[]>? FilesChanged;

    public event EventHandler<string>? SyncNotification;
    public event EventHandler<List<ConflictedFileInfo>>? ConflictDetected;

    public RepositoryContext()
    {
        _fileWatcher.FilesChanged += (_, files) => FilesChanged?.Invoke(this, files);
    }

    /// <summary>
    /// Switches to a new repository. Stops the old FileWatcher and SVN executor,
    /// then starts fresh for the new repository.
    /// </summary>
    public void SwitchTo(Repository repo)
    {
        if (_syncRunning)
            StopSync();

        // Dispose the old executor and create a fresh one — avoids lingering state
        // (CTS, Channels, WorkerLoop) from the previous repo's session.
        _executor.Dispose();
        _executor = new SvnCommandExecutor();

        _currentRepo = repo;
        _currentPath = repo.Path;
        _username = repo.Username ?? "";
        _password = repo.Password ?? "";

        _executor.Start();
        _fileWatcher.StartWatching(repo.Path);
        _syncRunning = true;

        Log.Information("[RepositoryContext] Switched to repo: {Name} at {Path}", repo.Name, repo.Path);

        // Validate credentials by checking the remote URL.
        // If auth fails on first attempt, clear cache and retry.
        // This prevents stale cached credentials from blocking subsequent operations.
        if (!string.IsNullOrEmpty(repo.Url))
        {
            _ = Task.Run(async () =>
            {
                var (success, rev) = await _svnService.ValidateCredentialsAsync(repo.Url, _username, _password);
                if (success)
                    Log.Information("[RepositoryContext] Credential validation OK for {Url} (rev {Revision})", repo.Url, rev);
                else
                    Log.Warning("[RepositoryContext] Credential validation FAILED for {Url}", repo.Url);
            });
        }
    }

    public void StopSync()
    {
        if (!_syncRunning) return;
        _fileWatcher.StopWatching();
        _executor.Stop();
        _syncRunning = false;
        Log.Information("[RepositoryContext] Sync stopped");
    }

    public void DisableFileWatcher()
    {
        _fileWatcher.StopWatching();
        Log.Debug("[RepositoryContext] FileWatcher disabled");
    }

    public void ReEnableFileWatcher()
    {
        if (_currentRepo != null)
            _fileWatcher.StartWatching(_currentRepo.Path);
        Log.Debug("[RepositoryContext] FileWatcher re-enabled");
    }

    // ── IRepositoryContext ──────────────────────────────────────────────

    public async Task<bool> IsValidWorkingCopyAsync(string path)
    {
        var r = await _executor.ExecuteAsync(SvnCommand.IsValidWorkingCopy, path);
        return r.Success && r.Value == "true";
    }

    public async Task<bool> OpenLocalRepositoryAsync(string path, string username = "", string password = "")
    {
        var vwcr = await _executor.ExecuteAsync(SvnCommand.IsValidWorkingCopy, path);
        if (!(vwcr.Success && vwcr.Value == "true"))
        {
            Log.Warning("[RepositoryContext] Not a valid SVN working copy: {Path}", path);
            return false;
        }

        _currentPath = path;
        _username = username ?? "";
        _password = password ?? "";
        _fileWatcher.StartWatching(path);

        var remoteUrl = await GetRemoteUrlAsync(path);
        Log.Information("[RepositoryContext] Opened local repository: {Path}, remote: {Url}", path, remoteUrl);

        return true;
    }

    public async Task<bool> CheckoutRemoteRepositoryAsync(string url, string savePath, string username, string password)
    {
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(savePath))
        {
            Log.Warning("[RepositoryContext] Checkout called with empty URL or path");
            return false;
        }

        if (!string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(password))
        {
            _username = username;
            _password = password;
        }

        if (!Directory.Exists(savePath))
            Directory.CreateDirectory(savePath);

        Log.Information("[RepositoryContext] Starting checkout: {Url} → {Path}", url, savePath);

        var coResult = await _executor.ExecuteAsync(SvnCommand.Checkout, savePath,
            repoUrl: url, username: _username, password: _password);

        if (!coResult.Success)
        {
            Log.Error("[RepositoryContext] Checkout failed: {Error}", coResult.Error ?? "unknown");
            return false;
        }

        _currentPath = savePath;
        _fileWatcher.StartWatching(savePath);

        Log.Information("[RepositoryContext] Checkout complete: {Path}", savePath);
        return true;
    }

    public async Task<string> GetRemoteUrlAsync(string path)
    {
        var r = await _executor.ExecuteAsync(SvnCommand.Info, path);
        return r.Success ? (r.Value ?? "") : "";
    }

    public void Dispose()
    {
        _fileWatcher.Dispose();
        _executor.Dispose();
    }
}