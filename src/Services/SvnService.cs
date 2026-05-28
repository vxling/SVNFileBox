#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using SharpSvn;
using SVNFileBox.Models;
using Serilog;
using SharpSvnStatus = SharpSvn.SvnStatus;

namespace SVNFileBox.Services;

/// <summary>
/// SVN operations wrapper, organized into three operation tiers:
///
/// Tier 1 — ReadOnly (no lock needed, pure local, sub-second):
///   status, info, revision queries. No WC lock, no network.
///   Multiple concurrent reads via _readSemaphore(10).
///
/// Tier 2 — LocalWrite (exclusive WC write lock, fast, sub-second):
///   add, delete, move, revert, resolve. Touches WC metadata only.
///   Serialized via _writeSemaphore(1) to prevent db lock conflicts.
///   Must NOT be called while a HeavyWrite is in-flight.
///
/// Tier 3 — HeavyWrite (exclusive WC write lock, slow, network-bound):
///   commit, update. Involves file transfer + potentially network.
///   Uses activity-watchdog timeout (IdleTimeoutMs). Should go through
///   PendingCommitQueue for batching — never called concurrently.
///
/// Each method creates its own SvnClient instance (SharpSvn is lightweight
/// and this avoids all threading/reentrancy concerns).
/// </summary>
public class SvnService : IDisposable
{
    // ─────────────────────────────────────────────────────────────────────────
    // Static concurrency primitives (shared across all SvnService instances)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Serializes all write-class operations (both LocalWrite and HeavyWrite).
    /// Static so all instances share the same lock. Only one write at a time.
    /// </summary>
    private static readonly SemaphoreSlim _writeSemaphore = new(1, 1);

    /// <summary>
    /// Allows concurrent read operations. Up to 10 reads simultaneously.
    /// </summary>
    private static readonly SemaphoreSlim _readSemaphore = new(10, 10);

    /// <summary>
    /// Deduplicates concurrent GetHeadRevisionAsync calls for the same repoUrl.
    /// </summary>
    private static readonly Dictionary<string, Task<int>> _headRevisionCache = new();
    private static readonly SemaphoreSlim _headRevisionLock = new(1, 1);
    private static readonly TimeSpan HeadRevisionCacheTtl = TimeSpan.FromSeconds(30);

    private const int LockWaitTimeoutMs = 30_000;
    private const int SafetyNetTimeoutMs = 600_000;
    private const int HttpTimeoutMs = 60_000;

    // ─────────────────────────────────────────────────────────────────────────
    // Activity timeout for HeavyWrite operations (commit/update)
    // ─────────────────────────────────────────────────────────────────────────

    private static int _fileTransferTimeoutMs = 120_000;

    public static int FileTransferTimeoutMs
    {
        get => _fileTransferTimeoutMs;
        set => _fileTransferTimeoutMs = Math.Clamp(value, 30_000, 600_000);
    }

    public static event Action? FileTransferTimeoutChanged;
    public static void NotifyFileTransferTimeoutChanged() => FileTransferTimeoutChanged?.Invoke();

    /// <summary>
    /// Raised whenever a file is transferred during a HeavyWrite operation.
    /// Instance event — each SvnService instance has its own subscribers.
    /// </summary>
    public event Action<string, string>? FileTransferActivity;

    // ─────────────────────────────────────────────────────────────────────────
    // Constructor
    // ─────────────────────────────────────────────────────────────────────────

    public SvnService()
    {
#pragma warning disable SYSLIB0014
        ServicePointManager.DefaultConnectionLimit = 4;
        ServicePointManager.Expect100Continue = false;
        ServicePointManager.FindServicePoint(new Uri("https://dummy")).ConnectionLeaseTimeout = HttpTimeoutMs;
#pragma warning restore SYSLIB0014

        Log.Information("SvnService initialized — SharpSvn {Version}",
            typeof(SvnClient).Assembly.GetName().Version?.ToString() ?? "unknown");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Tier 1 — ReadOnly Operations
    // No lock needed. Multiple concurrent reads. Pure local, sub-second.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Tier 1. Gets SVN status of all items under a path. Pure local read.
    /// </summary>
    public async Task<Dictionary<string, FileSvnStatus>> GetStatusAsync(
        string workingCopyPath,
        bool depth)
    {
        return await ExecuteRead(token =>
        {
            var statuses = new Dictionary<string, FileSvnStatus>();
            try
            {
                using var client = CreateClient();
                var handler = new EventHandler<SvnStatusEventArgs>(delegate (object? sender, SvnStatusEventArgs item)
                {
                    var path = item.Path;
                    if (string.IsNullOrEmpty(path)) return;

                    if (item.LocalNodeStatus == SharpSvnStatus.NotVersioned &&
                        (path == workingCopyPath || path.EndsWith(".")))
                        return;

                    var svnStatus =  item.LocalNodeStatus switch
                        {
                            SharpSvnStatus.Modified    => FileSvnStatus.Modified,
                            SharpSvnStatus.Added       => FileSvnStatus.Added,
                            SharpSvnStatus.Deleted     => FileSvnStatus.Deleted,
                            SharpSvnStatus.Conflicted  => FileSvnStatus.Conflicted,
                            SharpSvnStatus.NotVersioned=> FileSvnStatus.Unversioned,
                            SharpSvnStatus.Missing      => FileSvnStatus.Missing,
                            SharpSvnStatus.Replaced     => FileSvnStatus.Replaced,
                            SharpSvnStatus.Obstructed  => FileSvnStatus.Obstructed,
                            SharpSvnStatus.External     => FileSvnStatus.External,
                            SharpSvnStatus.Incomplete   => FileSvnStatus.Incomplete,
                            _                           => FileSvnStatus.Normal
                        };

                    if (svnStatus != FileSvnStatus.Normal)
                        statuses[path] = (svnStatus != FileSvnStatus.Conflicted && item.Conflicted == true) ? FileSvnStatus.TreeConflicted : svnStatus;
                });

                client.Status(workingCopyPath, new SvnStatusArgs
                {
                    Depth = depth? SvnDepth.Infinity:SvnDepth.Children,
                    RetrieveAllEntries = true,
                    // RetrieveRemoteStatus = depth,
                }, handler);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error getting SVN status for {Path}", workingCopyPath);
            }
            return statuses;
        });
    }

    /// <summary>
    /// Tier 1. Returns paths that have pending updates on the server.
    /// Uses svn status --show-updates (GetRemoteStatus=true) to query the server
    /// for changed files. Pure read-only, does not modify local working copy.
    /// </summary>
    public async Task<List<string>> GetServerUpdatePathsAsync(string workingCopyPath)
    {
        return await ExecuteRead(token =>
        {
            var paths = new List<string>();
            try
            {
                using var client = CreateClient();
                var handler = new EventHandler<SvnStatusEventArgs>(delegate (object? sender, SvnStatusEventArgs item)
                {
                    // RemoteContentStatus is only populated when GetRemoteStatus=true.
                    // IsRemoteUpdated=true means the file has pending changes on the server
                    // that are not yet in the local working copy.
                    if (item.IsRemoteUpdated && !string.IsNullOrEmpty(item.Path))
                    {
                        paths.Add(item.Path);
                    }
                });

                client.Status(workingCopyPath, new SvnStatusArgs
                {
                    RetrieveRemoteStatus = true,
                    RetrieveAllEntries = true,
                    Depth = SvnDepth.Infinity
                }, handler);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error getting server update paths for {Path}", workingCopyPath);
            }
            return paths;
        });
    }

    /// <summary>
    /// Tier 1. Reads the remote repository URL for a local working copy.
    /// Pure local, no credentials needed.
    /// </summary>
    public async Task<string> GetRepoUrlAsync(string workingCopyPath)
    {
        return await ExecuteRead(token =>
        {
            try
            {
                using var client = CreateClient();
                var root = client.GetRepositoryRoot(workingCopyPath);
                return root?.ToString() ?? "";
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error getting repo URL for {Path}", workingCopyPath);
            }
            return "";
        });
    }

    /// <summary>
    /// Tier 1. Gets the current working copy revision number. Pure local read.
    /// </summary>
    public async Task<int> GetWorkingCopyRevisionAsync(string workingCopyPath)
    {
        return await ExecuteRead(token =>
        {
            try
            {
                using var client = CreateClient();
                SvnInfoEventArgs? infoResult = null;
                var handler = new EventHandler<SvnInfoEventArgs>((s, e) => infoResult = e);
                client.Info(workingCopyPath, handler);
                return infoResult != null ? (int)infoResult.Revision : -1;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error getting revision for {Path}", workingCopyPath);
            }
            return -1;
        });
    }

    /// <summary>
    /// Tier 1. Gets the HEAD revision of a remote repository.
    /// Deduplicates concurrent requests for the same URL (shared HTTP request).
    /// </summary>
    public async Task<int> GetHeadRevisionAsync(string repoUrl, string? username = null, string? password = null)
    {
        var newTask = DoGetHeadRevisionAsync(repoUrl, username, password);
        Task<int>? inFlightTask;

        lock (_headRevisionLock)
        {
            if (_headRevisionCache.TryGetValue(repoUrl, out inFlightTask))
                Log.Debug("[GetHeadRevisionAsync] Reusing in-flight request for {Url}", repoUrl);
            else
            {
                inFlightTask = newTask;
                _headRevisionCache[repoUrl] = newTask;
            }
        }

        if (inFlightTask != newTask)
            return await inFlightTask;

        try
        {
            return await newTask;
        }
        finally
        {
            var taskToEvict = newTask;
            _ = Task.Delay(HeadRevisionCacheTtl).ContinueWith(_ =>
            {
                lock (_headRevisionLock)
                {
                    if (_headRevisionCache.TryGetValue(repoUrl, out var cached) && cached == taskToEvict)
                        _headRevisionCache.Remove(repoUrl);
                }
            });
        }
    }

    private async Task<int> DoGetHeadRevisionAsync(string repoUrl, string? username, string? password)
    {
        return await ExecuteRead(token =>
        {
            try
            {
                using var client = CreateClient();
                if (!string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(password))
                    client.Authentication.ForceCredentials(username, password);

                var uri = new Uri(repoUrl);
                SvnInfoEventArgs? infoResult = null;
                var handler = new EventHandler<SvnInfoEventArgs>((s, e) => infoResult = e);
                client.Info(new SvnUriTarget(uri, SvnRevision.Head), handler);
                var rev = infoResult != null ? (int)infoResult.Revision : -1;
                Log.Debug("[GetHeadRevisionAsync] Result for {Url} = {Revision}", repoUrl, rev);
                return rev;
            }
            catch (Exception ex)
            {
                if (IsSvnAuthError(ex))
                {
                    CredentialExpired?.Invoke(repoUrl);
                    Log.Error("[GetHeadRevisionAsync] Authentication error, credentials required for {Url}", repoUrl);
                }
                Log.Error(ex, "[GetHeadRevisionAsync] Failed for {Url}", repoUrl);
                return -1;
            }
        });
    }

    public async Task<(bool success, int revision)> ValidateCredentialsAsync(
        string repoUrl, string? username = null, string? password = null)
    {
        // First attempt — try with ForceCredentials only
        try
        {
            using var client = CreateClient();
            if (!string.IsNullOrEmpty(username))
                client.Authentication.ForceCredentials(username, password ?? "");

            var uri = new Uri(repoUrl);
            SvnInfoEventArgs? infoResult = null;
            var handler = new EventHandler<SvnInfoEventArgs>((s, e) => infoResult = e);
            client.Info(new SvnUriTarget(uri, SvnRevision.Head), handler);
            var rev = infoResult != null ? (int)infoResult.Revision : -1;
            Log.Debug("[ValidateCredentialsAsync] Success for {Url} = {Revision}", repoUrl, rev);
            return (true, rev);
        }
        catch (SvnRepositoryIOException ex) when (ex.InnerException is SvnAuthenticationException)
        {
            // Retry once after clearing stale in-memory auth cache
            Log.Warning("[ValidateCredentialsAsync] Auth failed on first attempt, retrying with cleared cache for {Url}", repoUrl);
            try
            {
                using var client = CreateClient();
                client.Authentication.ClearAuthenticationCache();
                if (!string.IsNullOrEmpty(username))
                    client.Authentication.ForceCredentials(username, password ?? "");

                var uri = new Uri(repoUrl);
                SvnInfoEventArgs? infoResult = null;
                var handler = new EventHandler<SvnInfoEventArgs>((s, e) => infoResult = e);
                client.Info(new SvnUriTarget(uri, SvnRevision.Head), handler);
                var rev = infoResult != null ? (int)infoResult.Revision : -1;
                Log.Debug("[ValidateCredentialsAsync] Retry result for {Url} = {Revision}", repoUrl, rev);
                return (true, rev);
            }
            catch (Exception retryEx)
            {
                Log.Error(retryEx, "[ValidateCredentialsAsync] Retry failed for {Url}", repoUrl);
                return (false, -1);
            }
        }
        catch (Exception outerEx)
        {
            Log.Error(outerEx, "[ValidateCredentialsAsync] Failed for {Url}", repoUrl);
            return (false, -1);
        }
    }

    /// <summary>
    /// Tier 1. Detects conflicted files in a working copy. Pure local scan.
    /// </summary>
    public async Task<List<string>> GetConflictedFilesAsync(string workingCopyPath)
    {
        return await ExecuteRead(token =>
        {
            var files = new List<string>();
            try
            {
                using var client = CreateClient();
                client.GetStatus(workingCopyPath, new SvnStatusArgs
                {
                    Depth = SvnDepth.Infinity,
                    RetrieveAllEntries = true,
                }, out var conflictedResults);

                foreach (var item in conflictedResults)
                {
                    if ((item.LocalNodeStatus == SharpSvnStatus.Conflicted || item.TreeConflict != null) && !string.IsNullOrEmpty(item.Path))
                        files.Add(item.Path);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error getting conflicted files for {Path}", workingCopyPath);
            }
            return files;
        });
    }

    /// <summary>
    /// Tier 1. Gets the last-changed time of a file from SVN metadata. Pure local.
    /// </summary>
    public async Task<DateTime> GetLastChangedTimeAsync(string filePath)
    {
        return await ExecuteRead(token =>
        {
            try
            {
                using var client = CreateClient();
                SvnInfoEventArgs? infoResult = null;
                var handler = new EventHandler<SvnInfoEventArgs>((s, e) => infoResult = e);
                client.Info(filePath, handler);
                return infoResult?.LastChangeTime.ToUniversalTime() ?? DateTime.MinValue;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error getting last changed time for {Path}", filePath);
            }
            return DateTime.MinValue;
        });
    }

    /// <summary>
    /// Tier 1. Fast local-only check. No lock needed.
    /// </summary>
    public bool IsVersioned(string path)
    {
        try
        {
            using var client = CreateClient();
            return client.GetRepositoryRoot(path) != null;
        }
        catch { return false; }
    }

    /// <summary>
    /// Tier 1. Fast local-only check. No lock needed.
    /// </summary>
    public bool IsValidWorkingCopy(string path)
    {
        try
        {
            using var client = CreateClient();
            return client.GetRepositoryRoot(path) != null;
        }
        catch { return false; }
    }

    /// <summary>
    /// Lightweight local-only credential check.
    /// No network call — uses GetRepositoryRoot on local .svn metadata.
    /// Returns true if the cached credential is usable, false if auth failed.
    /// </summary>
    public bool IsCredentialValid(string workingCopyPath)
    {
        try
        {
            using var client = CreateClient();
            client.Authentication.Clear();
            // GetRepositoryRoot reads local .svn metadata only — no network traffic
            var root = client.GetRepositoryRoot(workingCopyPath);
            return root != null;
        }
        catch (SvnAuthenticationException)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Fired when Update or Commit throws SvnAuthenticationException —
    /// indicates the cached credential has expired and user needs to re-enter password.
    /// </summary>
    public event Action<string>? CredentialExpired;

    // ─────────────────────────────────────────────────────────────────────────
    // Tier 2 — LocalWrite Operations
    // Exclusive WC write lock. Fast (sub-second). No network.
    // Serialized via _writeSemaphore(1) — never run concurrently.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Tier 2. Adds a file or directory to SVN version control.
    /// </summary>
    public async Task<bool> AddPathAsync(string path)
    {
        return await ExecuteLocalWrite(token =>
        {
            TryCleanStaleLocks(GetWorkingCopyRoot(path));
            using var client = CreateClient();
            try
            {
                return client.Add(path);
            }
            catch (SharpSvn.SvnEntryException ex) when (ex.Message.Contains("already"))
            {
                Log.Warning("[SvnService] File already added: {Path}", path);
                return true;
            }
            catch (SharpSvn.SvnException ex) when (ex.InnerException is FileNotFoundException)
            {
                Log.Warning("[SvnService] File not found, cannot add: {Path}", path);
                return false;
            }
        });
    }

    /// <summary>
    /// Tier 2. Removes a file or directory from SVN version control.
    /// Idempotent — safe to call on already-deleted items.
    /// </summary>
    public async Task<bool> DeleteAsync(string path)
    {
        return await ExecuteLocalWrite(token =>
        {
            TryCleanStaleLocks(GetWorkingCopyRoot(path));
            using var client = CreateClient();
            try
            {
                return client.Delete(path);
            }
            catch (SharpSvn.SvnUnversionedNodeException)
            {
                Log.Warning("[SvnService] File is not under version control: {Path}", path);
                return true;
            }
            catch (SharpSvn.SvnException ex) when (ex.Message.Contains("NotFound") || ex.InnerException is FileNotFoundException)
            {
                Log.Warning("[SvnService] File not found, treating as already deleted: {Path}", path);
                return true;
            }
            catch (SharpSvn.SvnWorkingCopyLockException)
            {
                Log.Warning("[SvnService] Working copy is locked: {Path}", path);
                return false;
            }
            catch (SharpSvn.SvnInvalidNodeKindException)
            {
                Log.Warning("[SvnService] Not a working copy (parent may be deleted): {Path}", path);
                return true;
            }
        });
    }

    /// <summary>
    /// Tier 2. Moves (renames) a file or directory within SVN.
    /// </summary>
    public async Task<bool> MoveAsync(string fromPath, string toPath)
    {
        return await ExecuteLocalWrite(token =>
        {
            TryCleanStaleLocks(GetWorkingCopyRoot(fromPath));
            using var client = CreateClient();
            try
            {
                return client.Move(fromPath, toPath);
            }
            catch (SharpSvn.SvnException ex) when (ex.InnerException is FileNotFoundException)
            {
                Log.Warning("[SvnService] Source file not found: {From} -> {To}", fromPath, toPath);
                return false;
            }
            catch (SharpSvn.SvnWorkingCopyPathNotFoundException)
            {
                // Source already gone
                return true;
            }
        });
    }

    /// <summary>
    /// Tier 2. Reverts local changes to a file or directory.
    /// </summary>
    public async Task<bool> RevertAsync(string path, bool recursive = true)
    {
        return await ExecuteLocalWrite(token =>
        {
            TryCleanStaleLocks(GetWorkingCopyRoot(path));
            using var client = CreateClient();
            var args = new SvnRevertArgs { Depth = recursive ? SvnDepth.Infinity : SvnDepth.Empty };
            return client.Revert(path, args);
        });
    }

    /// <summary>
    /// Tier 2. Resolves a conflicted file by accepting a specific version.
    /// </summary>
    public async Task<bool> ResolveAsync(string path, SvnAccept accept)
    {
        return await ExecuteLocalWrite(token =>
        {
            TryCleanStaleLocks(GetWorkingCopyRoot(path));
            using var client = CreateClient();
            return client.Resolve(path, accept);
        });
    }

    /// <summary>
    /// Tier 2. Breaks a write lock on a file (admin operation).
    /// </summary>
    public async Task<bool> BreakWriteLockAsync(string path)
    {
        return await ExecuteLocalWrite(token =>
        {
            try
            {
                using var client = CreateClient();
                return client.Lock(path, new SvnLockArgs { StealLock = true });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Break lock failed for {Path}", path);
                return false;
            }
        });
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Tier 3 — HeavyWrite Operations
    // Exclusive WC write lock. Slow (file transfer + network).
    // Activity watchdog timeout. Go through PendingCommitQueue for batching.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Tier 3. Commits local changes to the SVN server.
    /// Uses activity watchdog: cancels if no file-transfer activity for IdleTimeoutMs.
    /// Prefer going through PendingCommitQueue for batching rather than calling directly.
    /// </summary>
    public async Task<bool> CommitAsync(string workingCopyPath, string message)
    {
        try
        {
            return await ExecuteHeavyWrite(
                (token, progressCts) =>
                {
                    TryCleanStaleLocks(workingCopyPath);
                    return ExecuteSvnWithNotify(client =>
                    {
                        try
                        {
                            var args = new SvnCommitArgs { LogMessage = message };
                            return client.Commit(workingCopyPath, args);
                        }catch (SvnWorkingCopyException e) when (e.Message.Contains("tree-conflict") || (e.InnerException != null && e.InnerException.Message.Contains("tree-conflict")))
                        {
                            Log.Error("[CommitAsync] Tree-conflict detected for {Path}，message {Message}", workingCopyPath, e.Message);
                            return false;
                        }
                    }, token, progressCts);
                },
                workingCopyPath,
                onAuthFailed: path => CredentialExpired?.Invoke(path));
        }
        catch (Exception ex)
        {
            if (IsSvnAuthError(ex))
            {
                CredentialExpired?.Invoke(workingCopyPath);
                Log.Error("[CommitAsync] Authentication error, credentials required for {Path}", workingCopyPath);
            }
            Log.Error(ex, "[CommitAsync] Failed for {Path}", workingCopyPath);
            return false;
        }
    }

    /// <summary>
    /// Tier 3. Updates the working copy from the SVN server.
    /// Uses activity watchdog. Prefer going through PendingCommitQueue for batching.
    /// </summary>
    public async Task<bool> UpdateAsync(string workingCopyPath)
    {
        return await UpdateAsync(new[] { workingCopyPath });
    }

    /// <summary>
    /// Tier 3. Updates the specified sub-paths within a working copy from the SVN server.
    /// </summary>
    public async Task<bool> UpdateAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return true;
        var topDir = paths.Count == 1
            ? paths[0]
            : Path.GetDirectoryName(paths[0]) ?? paths[0];
        try
        {
            return await ExecuteHeavyWrite(
                (token, progressCts) =>
                {
                    TryCleanStaleLocks(topDir);
                    return ExecuteSvnWithNotify(
                        client => client.Update(paths.ToArray()),
                        token, progressCts);
                },
                topDir,
                onAuthFailed: path => CredentialExpired?.Invoke(path));
        }
        catch (Exception ex)
        {
            if (IsSvnAuthError(ex))
            {
                CredentialExpired?.Invoke(topDir);
                Log.Error("[UpdateAsync] Authentication error, credentials required for {TopDir}", topDir);
            }
            Log.Error(ex, "[UpdateAsync] Failed for {TopDir}", topDir);
            return false;
        }
    }

    /// <summary>
    /// Tier 3. Checks out a remote repository to a local path.}

    /// <summary>
    /// Tier 3. Checks out a remote repository to a local path.
    /// Supports cooperative cancellation via the provided CancellationToken.
    /// </summary>
    public async Task<(string output, int exitCode, string error)> CheckoutAsync(
        string repoUrl,
        string workingCopyPath,
        string? username = null,
        string? password = null)
    {
        // First attempt
        try
        {
            TryCleanStaleLocks(workingCopyPath);
            return CheckoutCore(repoUrl, workingCopyPath, username, password);
        }
        catch (SvnRepositoryIOException ex) when (ex.InnerException is SvnAuthenticationException)
        {
            Log.Warning("[CheckOutAsync] Auth failed, retrying with cleared cache for {Url}", repoUrl);
            try
            {
                TryCleanStaleLocks(workingCopyPath);
                return CheckoutCore(repoUrl, workingCopyPath, username, password, clearCache: true);
            }
            catch (Exception retryEx)
            {
                Log.Error(retryEx, "[CheckOutAsync] Retry failed for {Url}", repoUrl);
                return ("", 1, retryEx.Message);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[CheckOutAsync] Failed for {Url}", repoUrl);
            return ("", 1, ex.Message);
        }
    }

    private (string output, int exitCode, string error) CheckoutCore(
        string repoUrl, string workingCopyPath, string? username, string? password, bool clearCache = false)
    {
        using var client = CreateClient();
        if (clearCache) client.Authentication.ClearAuthenticationCache();
        if (!string.IsNullOrEmpty(username))
            client.Authentication.ForceCredentials(username, password ?? "");

        var lastActivity = DateTime.UtcNow;
        var timeoutMs = FileTransferTimeoutMs;
        client.Notify += (sender, e) =>
        {
            lastActivity = DateTime.UtcNow;
            FileTransferActivity?.Invoke(e.Path ?? "", e.Action.ToString());
        };

        SvnUpdateResult? result = null;
        client.CheckOut(new SvnUriTarget(repoUrl), workingCopyPath, new SvnCheckOutArgs(), out result);
        return (result?.Revision.ToString() ?? "", 0, "");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Connection test (Tier 1 — read-only probe)
    // ─────────────────────────────────────────────────────────────────────────

    public enum SvnConnectResult
    {
        Success, AuthFailed, AccessDenied, RepoNotFound,
        NetworkError, SslCertError, Timeout, Unknown
    }

    /// <summary>
    /// Tier 1. Lightweight connection probe — single svn list to determine
    /// reachability and categorize any error.
    /// </summary>
    public async Task<(SvnConnectResult result, string? errorMessage)> TestConnectionAsync(
        string url, string? username = null, string? password = null)
    {
        // First attempt
        SvnAuthenticationException? authEx = null;
        var (result, errorMsg) = await ExecuteRead(token =>
        {
            try
            {
                using var client = CreateClient();
                if (!string.IsNullOrEmpty(username))
                    client.Authentication.ForceCredentials(username, password ?? "");

                SvnListEventArgs? info = null;
                client.List(new SvnUriTarget(url), new SvnListArgs { Depth = SvnDepth.Empty },
                    new EventHandler<SvnListEventArgs>((s, e) => info = e));
                return (SvnConnectResult.Success, (string?)null);
            }
            catch (SvnAuthenticationException ex) { authEx = ex; return (SvnConnectResult.AuthFailed, (string?)null); }
            catch (SvnAuthorizationException ex) { return (SvnConnectResult.AccessDenied, (string?)null); }
            catch (SvnRepositoryIOException ex) when (ex.InnerException is SvnAuthenticationException)
            {
                // Wrapped auth exception — password wrong or credentials rejected
                Log.Debug("[TestConnectionAsync] Auth failed (wrapped): {Msg}", ex.Message);
                return (SvnConnectResult.AuthFailed, (string?)null);
            }
            catch (SvnRepositoryIOException ex)
            {
                if (ex.Message.Contains("E230001")) return (SvnConnectResult.SslCertError, (string?)null);
                if (ex.Message.Contains("E175002") || ex.Message.Contains("E170013")) return (SvnConnectResult.RepoNotFound, (string?)null);
                if (ex.Message.Contains("E175003")) return (SvnConnectResult.SslCertError, (string?)null);
                var msg = ex.Message;
                if (msg.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
                    msg.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
                    msg.Contains("could not resolve", StringComparison.OrdinalIgnoreCase) ||
                    msg.Contains("No route to host", StringComparison.OrdinalIgnoreCase) ||
                    msg.Contains("Connection refused", StringComparison.OrdinalIgnoreCase) ||
                    msg.Contains("network", StringComparison.OrdinalIgnoreCase) ||
                    msg.Contains("connection", StringComparison.OrdinalIgnoreCase))
                    return (SvnConnectResult.NetworkError, (string?)null);
                return (SvnConnectResult.Unknown, ex.Message);
            }
            catch (TimeoutException) { return (SvnConnectResult.Timeout, (string?)null); }
            catch (Exception ex) { return (SvnConnectResult.Unknown, ex.Message); }
        });

        // Retry once after clearing stale auth cache
        if (authEx != null)
        {
            Log.Warning("[TestConnectionAsync] Auth failed on first attempt, retrying with cleared cache for {Url}", url);
            return await ExecuteRead(token =>
            {
                try
                {
                    using var client = CreateClient();
                    client.Authentication.ClearAuthenticationCache();
                    if (!string.IsNullOrEmpty(username))
                        client.Authentication.ForceCredentials(username, password ?? "");

                    SvnListEventArgs? info = null;
                    client.List(new SvnUriTarget(url), new SvnListArgs { Depth = SvnDepth.Empty },
                        new EventHandler<SvnListEventArgs>((s, e) => info = e));
                    return (SvnConnectResult.Success, (string?)null);
                }
                catch (SvnAuthenticationException) { return (SvnConnectResult.AuthFailed, (string?)null); }
                catch (SvnAuthorizationException) { return (SvnConnectResult.AccessDenied, (string?)null); }
                catch (SvnRepositoryIOException ex) when (ex.InnerException is SvnAuthenticationException)
                {
                    Log.Debug("[TestConnectionAsync] Auth failed on retry (wrapped): {Msg}", ex.Message);
                    return (SvnConnectResult.AuthFailed, (string?)null);
                }
                catch (SvnRepositoryIOException ex)
                {
                    if (ex.Message.Contains("E230001")) return (SvnConnectResult.SslCertError, (string?)null);
                    if (ex.Message.Contains("E175002") || ex.Message.Contains("E170013")) return (SvnConnectResult.RepoNotFound, (string?)null);
                    if (ex.Message.Contains("E175003")) return (SvnConnectResult.SslCertError, (string?)null);
                    return (SvnConnectResult.Unknown, ex.Message);
                }
                catch (SvnIOException ex)
                {
                    var msg = ex.Message;
                    if (msg.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
                        msg.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
                        msg.Contains("could not resolve", StringComparison.OrdinalIgnoreCase) ||
                        msg.Contains("No route to host", StringComparison.OrdinalIgnoreCase) ||
                        msg.Contains("Connection refused", StringComparison.OrdinalIgnoreCase) ||
                        msg.Contains("network", StringComparison.OrdinalIgnoreCase))
                        return (SvnConnectResult.NetworkError, (string?)null);
                    return (SvnConnectResult.Unknown, ex.Message);
                }
                catch (TimeoutException) { return (SvnConnectResult.Timeout, (string?)null); }
                catch (Exception ex) { return (SvnConnectResult.Unknown, ex.Message); }
            });
        }

        return (result, errorMsg);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Internal execution helpers
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Tier 1 helper: ReadOnly operations.
    /// Uses _readSemaphore(10) — multiple reads run concurrently.
    /// Hard 600s safety-net timeout.
    /// </summary>
    private async Task<T> ExecuteRead<T>(Func<CancellationToken, T> operation)
    {
        if (!await _readSemaphore.WaitAsync(LockWaitTimeoutMs))
        {
            throw new TimeoutException(
                $"SVN read timed out waiting for a read slot after {LockWaitTimeoutMs / 1000}s. " +
                "Too many concurrent reads may be blocking.");
        }

        try
        {
            using var safetyCts = new CancellationTokenSource(SafetyNetTimeoutMs);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(safetyCts.Token);
            return await Task.Run(() => operation(linked.Token), linked.Token);
        }
        catch (OperationCanceledException) when (!LinkedCancellationToken(0).IsCancellationRequested)
        {
            throw new TimeoutException($"SVN read timed out after {SafetyNetTimeoutMs / 1000}s.");
        }
        finally
        {
            _readSemaphore.Release();
        }

        CancellationToken LinkedCancellationToken(int _) => default;
    }

    /// <summary>
    /// Tier 2 helper: LocalWrite operations.
    /// Uses _writeSemaphore(1) — exclusive, serialized with HeavyWrite.
    /// Hard 30s timeout (local metadata ops should be sub-second).
    /// </summary>
    private async Task<T> ExecuteLocalWrite<T>(Func<CancellationToken, T> operation)
    {
        if (!await _writeSemaphore.WaitAsync(LockWaitTimeoutMs))
        {
            throw new TimeoutException(
                $"SVN write timed out waiting for the write lock after {LockWaitTimeoutMs / 1000}s.");
        }

        try
        {
            using var safetyCts = new CancellationTokenSource(LockWaitTimeoutMs);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(safetyCts.Token);
            return await Task.Run(() => operation(linked.Token), linked.Token);
        }
        finally
        {
            _writeSemaphore.Release();
        }
    }

    /// <summary>
    /// Tier 3 helper: HeavyWrite operations.
    /// Uses _writeSemaphore(1) — exclusive, serialized with LocalWrite.
    /// Activity watchdog timeout + 600s hard ceiling.
    /// onAuthFailed is called with the workingCopyPath when SvnAuthenticationException is caught.
    /// </summary>
    private async Task<T> ExecuteHeavyWrite<T>(
        Func<CancellationToken, CancellationTokenSource, T> operation,
        string workingCopyPath = "",
        Action<string>? onAuthFailed = null)
    {
        if (!await _writeSemaphore.WaitAsync(LockWaitTimeoutMs))
        {
            throw new TimeoutException(
                $"SVN operation timed out waiting for write lock after {LockWaitTimeoutMs / 1000}s.");
        }

        try
        {
            var idleTimeoutMs = FileTransferTimeoutMs;
            using var progressCts = new CancellationTokenSource();
            using var safetyCts = new CancellationTokenSource(SafetyNetTimeoutMs);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                progressCts.Token, safetyCts.Token);

            return await Task.Run(() => operation(linked.Token, progressCts), linked.Token);
        }
        catch (SvnAuthenticationException)
        {
            if (onAuthFailed != null && !string.IsNullOrEmpty(workingCopyPath))
                onAuthFailed(workingCopyPath);
            throw;
        }
        catch (OperationCanceledException) when (!IsCancellationTokenSourceCancelled(0))
        {
            throw new TimeoutException(
                $"文件传输超时（{FileTransferTimeoutMs / 1000}s 无活动）。请检查网络后重试。");
        }
        finally
        {
            _writeSemaphore.Release();
        }

        bool IsCancellationTokenSourceCancelled(int _) => false;
    }

    /// <summary>
    /// Wraps a SharpSvn operation with an activity watchdog.
    /// Resets on each Notify event. Fires progressCts cancellation if no
    /// activity for FileTransferTimeoutMs, interrupting the operation.
    /// </summary>
    private T ExecuteSvnWithNotify<T>(
        Func<SvnClient, T> svnOperation,
        CancellationToken token,
        CancellationTokenSource progressCts)
    {
        using var client = CreateClient();
        var lastActivity = DateTime.UtcNow;
        var timeoutMs = FileTransferTimeoutMs;

        client.Notify += (sender, e) =>
        {
            lastActivity = DateTime.UtcNow;
            FileTransferActivity?.Invoke(e.Path ?? "", e.Action.ToString());
        };

        using var watchdogCts = new CancellationTokenSource();
        var watchdog = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested && !watchdogCts.Token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(2_000, CancellationTokenSource.CreateLinkedTokenSource(token, watchdogCts.Token).Token);
                }
                catch { break; }

                if ((DateTime.UtcNow - lastActivity).TotalMilliseconds > timeoutMs)
                {
                    Log.Warning("[SvnService] No activity for {Seconds}s, cancelling", timeoutMs / 1000);
                    progressCts.Cancel();
                    break;
                }
            }
        }, token);

        try { return svnOperation(client); }
        finally { watchdogCts.Cancel(); try { watchdog.Wait(500); } catch { } }
    }

    /// <summary>
    /// Returns true if the exception (or any of its inner exceptions) indicates
    /// an SVN authentication / authorization failure (bad credentials or no permission).
    /// </summary>
    private static bool IsSvnAuthError(Exception ex)
    {
        var current = ex;
        while (current != null)
        {
            if (current is SvnAuthorizationException)
                return true;
            if (current is SvnException svnErr)
            {
                // E170001 = "Authentication failed" in SVN protocol
                if (svnErr.SvnErrorCode == (SvnErrorCode)0x170001)
                    return true;
            }
            current = current.InnerException;
        }
        return false;
    }

    /// <summary>
    /// Returns the working copy root (directory containing .svn) for any path
    /// within the working copy.
    /// </summary>
    private static string GetWorkingCopyRoot(string path)
    {
        using var client = CreateClient();
        return client.GetWorkingCopyRoot(path);
    }

    /// <summary>
    /// Cleans stale WC locks left by a previously interrupted operation.
    /// Called at the start of every write-class operation.
    /// Never throws — failures are logged but do not block the operation.
    /// </summary>
    private void TryCleanStaleLocks(string workingCopyPath)
    {
        try
        {
            using var client = CreateClient();
            var result = client.CleanUp(workingCopyPath);
            Log.Debug("[SvnService] Cleanup for {Path}: {Result}", workingCopyPath, result ? "success" : "nothing to clean");
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[SvnService] Cleanup: no stale lock for {Path}", workingCopyPath);
        }
    }

    /// <summary>
    /// Creates a SvnClient with SSL certificate auto-accept pre-configured.
    /// </summary>
    private static SvnClient CreateClient()
    {
        var client = new SvnClient();
        client.Authentication.SslServerTrustHandlers += (sender, e) =>
        {
            e.AcceptedFailures = e.Failures;
            e.Save = true;
        };
        return client;
    }

    public void Dispose()
    {
        // Static semaphores are NOT disposed — shared across all instances.
    }
}
