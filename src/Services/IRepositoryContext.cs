#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SVNFileBox.Models;

namespace SVNFileBox.Services;

/// <summary>
/// Manages the active repository session: credentials, file watcher lifecycle,
/// SVN executor, and sync timers. Single source of truth for the current repo.
/// </summary>
public interface IRepositoryContext
{
    /// <summary>Current working copy path.</summary>
    string CurrentPath { get; }

    /// <summary>Whether username + password are configured.</summary>
    bool HasCredentials { get; }

    /// <summary>Currently active repository, if any.</summary>
    Repository? CurrentRepository { get; }

    /// <summary>Direct access to the SVN command executor (for SyncService).</summary>
    ISvnCommandExecutor Executor { get; }

    /// <summary>Fired when the file watcher detects changes.</summary>
    event EventHandler? FilesChangedForSync;

    /// <summary>Fired on sync notifications (status messages).</summary>
    event EventHandler<string>? SyncNotification;

    /// <summary>Fired when conflicts are detected after update.</summary>
    event EventHandler<List<ConflictedFileInfo>>? ConflictDetected;

    /// <summary>
    /// Switches to a new repository.
    /// Stops old watcher/executor, starts new watcher, starts sync timers.
    /// </summary>
    void SwitchTo(Repository repo);

    /// <summary>Stops all watchers, timers, and the executor.</summary>
    void StopSync();

    /// <summary>Stops the file watcher temporarily (e.g. during conflict resolution).</summary>
    void DisableFileWatcher();

    /// <summary>Restarts the file watcher after DisableFileWatcher.</summary>
    void ReEnableFileWatcher();

    /// <summary>
    /// Opens a local SVN working copy by path. Credentials optional.
    /// </summary>
    Task<bool> OpenLocalRepositoryAsync(string path, string username = "", string password = "");

    /// <summary>Checks out a remote repository to local path.</summary>
    Task<bool> CheckoutRemoteRepositoryAsync(string url, string savePath, string username, string password);

    /// <summary>Reads the remote URL of a local working copy.</summary>
    Task<string> GetRemoteUrlAsync(string path);

    /// <summary>Checks if path is a valid SVN working copy.</summary>
    Task<bool> IsValidWorkingCopyAsync(string path);
}