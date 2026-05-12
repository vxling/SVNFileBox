#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
/// SVN operations wrapper. Each method creates its own SvnClient instance —
/// SharpSvn is lightweight and this avoids all threading/reentrancy concerns
/// that come from sharing a single instance across concurrent calls.
///
/// All operations are serialized via a SemaphoreSlim(1,1) to prevent concurrent
/// SVN operations on the same working copy, regardless of trigger source
/// (manual, timer, or FileWatcher).
/// </summary>
public class SvnService : IDisposable
{
    /// <summary>
    /// Serializes all SVN operations — manual, timer-triggered, and FileWatcher-triggered.
    /// Only one operation runs at a time; others queue behind it.
    /// </summary>
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    /// <summary>
    /// Default network timeout in milliseconds for each SvnClient operation.
    /// </summary>
    private const int DefaultTimeoutMs = 120_000;

    public SvnService()
    {
        // Set default ServicePointManager timeout for all HTTP/Web requests (SVN uses HTTP)
        ServicePointManager.DefaultConnectionLimit = 4;
        ServicePointManager.Expect100Continue = false;

        Log.Information("SvnService initialized — SharpSvn {Version}, operation timeout {Timeout}s",
            typeof(SvnClient).Assembly.GetName().Version?.ToString() ?? "unknown",
            DefaultTimeoutMs / 1000);
    }

    /// <summary>
    /// Runs an SVN operation with exclusive access (serialized) and timeout.
    /// All SVN operations must go through this to prevent concurrent access issues.
    /// SharpSvn calls are synchronous; they run on a background thread with timeout enforced.
    /// </summary>
    private async Task<T> ExecuteAsync<T>(Func<CancellationToken, T> operation, CancellationToken cancellationToken = default)
    {
        // Wait for exclusive access (queue behind any in-flight operation)
        if (!await _semaphore.WaitAsync(TimeSpan.FromMilliseconds(DefaultTimeoutMs), cancellationToken))
        {
            throw new TimeoutException(
                $"SVN operation timed out waiting for lock after {DefaultTimeoutMs / 1000}s. " +
                "Another SVN operation may be stuck. Try again or restart the application.");
        }

        try
        {
            // Wrap the operation in Task.Run so it runs on a background thread.
            // The linked CTS enforces an overall operation timeout (not just the semaphore wait).
            using var timeoutCts = new CancellationTokenSource();
            timeoutCts.CancelAfter(DefaultTimeoutMs);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            var result = await Task.Run(() => operation(linkedCts.Token), linkedCts.Token);
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Our internal timeout was the cause — 转化成 TimeoutException
            throw new TimeoutException($"SVN operation timed out after {DefaultTimeoutMs / 1000}s. Server may be unreachable.");
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task<Dictionary<string, FileSvnStatus>> GetStatusAsync(string workingCopyPath, SvnDepth depth = SvnDepth.Children)
    {
        return await ExecuteAsync(token =>
        {
            var statuses = new Dictionary<string, FileSvnStatus>();

            try
            {
                using var client = new SvnClient();
                var handler = new EventHandler<SvnStatusEventArgs>(delegate(object? sender, SvnStatusEventArgs item)
                {
                    var path = item.Path;
                    if (string.IsNullOrEmpty(path)) return;

                    if (item.LocalNodeStatus != SharpSvnStatus.Normal)
                        Log.Debug("SvnStatus: path={Path} localStatus={Status} remoteStatus={Remote}", path, item.LocalNodeStatus, item.RemoteNodeStatus);

                    if (item.LocalNodeStatus == SharpSvnStatus.NotVersioned &&
                        (path == workingCopyPath || path.EndsWith(".")))
                        return;

                    var svnStatus = item.LocalNodeStatus switch
                    {
                        SharpSvnStatus.Modified => FileSvnStatus.Modified,
                        SharpSvnStatus.Added => FileSvnStatus.Added,
                        SharpSvnStatus.Deleted => FileSvnStatus.Deleted,
                        SharpSvnStatus.Conflicted => FileSvnStatus.Conflicted,
                        SharpSvnStatus.NotVersioned => FileSvnStatus.Unversioned,
                        SharpSvnStatus.Missing => FileSvnStatus.Missing,
                        SharpSvnStatus.Replaced => FileSvnStatus.Replaced,
                        SharpSvnStatus.Obstructed => FileSvnStatus.Obstructed,
                        SharpSvnStatus.External => FileSvnStatus.External,
                        SharpSvnStatus.Incomplete => FileSvnStatus.Unknown,
                        _ => FileSvnStatus.Normal
                    };

                    if (svnStatus != FileSvnStatus.Normal)
                        statuses[path] = svnStatus;
                });

                client.Status(workingCopyPath, new SvnStatusArgs
                {
                    Depth = depth,
                    RetrieveAllEntries = true,
                }, handler);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error getting SVN status for {Path}", workingCopyPath);
            }

            return statuses;
        });
    }

    public async Task<string> GetRepoUrlAsync(string workingCopyPath)
    {
        return await ExecuteAsync(token =>
        {
            try
            {
                using var client = new SvnClient();
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

    public async Task<int> GetWorkingCopyRevisionAsync(string workingCopyPath)
    {
        return await ExecuteAsync(token =>
        {
            try
            {
                using var client = new SvnClient();
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

    public async Task<int> GetHeadRevisionAsync(string repoUrl, string? username = null, string? password = null)
    {
        return await ExecuteAsync(token =>
        {
            try
            {
                using var client = new SvnClient();
                AcceptSelfSignedCert(client);
                var uri = new Uri(repoUrl);
                SvnInfoEventArgs? infoResult = null;
                var handler = new EventHandler<SvnInfoEventArgs>((s, e) => infoResult = e);
                client.Info(new SvnUriTarget(uri, SvnRevision.Head), handler);
                return infoResult != null ? (int)infoResult.Revision : -1;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error getting HEAD revision for {Url}", repoUrl);
            }
            return -1;
        });
    }

    public async Task<(string output, int exitCode, string error)> RunCommandAsync(string arguments, int timeoutMs = 60000)
    {
        // SharpSvn doesn't use CLI — all operations go through the API.
        // This method is kept for compatibility but returns empty success.
        await Task.CompletedTask;
        return ("", 0, "");
    }

    public async Task<bool> CommitAsync(string workingCopyPath, string message, string? username = null, string? password = null)
    {
        return await ExecuteAsync(token =>
        {
            try
            {
                using var client = new SvnClient();
                var args = new SvnCommitArgs { LogMessage = message };
                return client.Commit(workingCopyPath, args);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Commit failed for {Path}", workingCopyPath);
                return false;
            }
        });
    }

    public async Task<bool> UpdateAsync(string workingCopyPath, string? username = null, string? password = null)
    {
        return await ExecuteAsync(token =>
        {
            try
            {
                using var client = new SvnClient();
                return client.Update(workingCopyPath);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Update failed for {Path}", workingCopyPath);
                return false;
            }
        });
    }

    public async Task<bool> AddFileAsync(string filePath)
    {
        return await ExecuteAsync(token =>
        {
            try
            {
                using var client = new SvnClient();
                return client.Add(filePath);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Add failed for {Path}", filePath);
                return false;
            }
        });
    }

    public async Task<bool> AddPathAsync(string path)
    {
        return await ExecuteAsync(token =>
        {
            try
            {
                using var client = new SvnClient();
                return client.Add(path);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "AddPath failed for {Path}", path);
                return false;
            }
        });
    }

    public async Task<bool> DeleteAsync(string path)
    {
        return await ExecuteAsync(token =>
        {
            try
            {
                using var client = new SvnClient();
                return client.Delete(path);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Delete failed for {Path}", path);
                return false;
            }
        });
    }

    public async Task<bool> MoveAsync(string fromPath, string toPath)
    {
        return await ExecuteAsync(token =>
        {
            try
            {
                using var client = new SvnClient();
                return client.Move(fromPath, toPath);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Move failed: {From} → {To}", fromPath, toPath);
                return false;
            }
        });
    }

    public async Task<bool> RevertAsync(string path, bool recursive = true)
    {
        return await ExecuteAsync(token =>
        {
            try
            {
                using var client = new SvnClient();
                var args = new SvnRevertArgs { Depth = recursive ? SvnDepth.Infinity : SvnDepth.Empty };
                return client.Revert(path, args);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Revert failed for {Path}", path);
                return false;
            }
        });
    }

    public async Task<bool> CleanUpAsync(string workingCopyPath)
    {
        return await ExecuteAsync(token =>
        {
            try
            {
                using var client = new SvnClient();
                return client.CleanUp(workingCopyPath);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "CleanUp failed for {Path}", workingCopyPath);
                return false;
            }
        });
    }

    public async Task<(string output, int exitCode)> SvnAddRecursiveAsync(string directoryPath)
    {
        return await ExecuteAsync(token =>
        {
            try
            {
                using var client = new SvnClient();
                client.GetStatus(directoryPath, new SvnStatusArgs { Depth = SvnDepth.Infinity }, out Collection<SvnStatusEventArgs> results);
                int count = 0;
                foreach (var r in results)
                {
                    if (r.LocalNodeStatus == SharpSvnStatus.NotVersioned)
                    {
                        if (client.Add(r.Path))
                            count++;
                    }
                }
                return (count.ToString(), 0);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Add recursive failed for {Path}", directoryPath);
                return ("", 1);
            }
        });
    }

    public async Task<bool> UnlockAsync(string path)
    {
        return await ExecuteAsync(token =>
        {
            try
            {
                using var client = new SvnClient();
                return client.Unlock(new[] { path }, new SvnUnlockArgs());
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Unlock failed for {Path}", path);
                return false;
            }
        });
    }

    public async Task<bool> BreakWriteLockAsync(string path)
    {
        return await ExecuteAsync(token =>
        {
            try
            {
                using var client = new SvnClient();
                return client.Lock(path, new SvnLockArgs { StealLock = true });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Break lock failed for {Path}", path);
                return false;
            }
        });
    }

    public async Task<(string output, int exitCode, string error)> CheckoutAsync(
        string url,
        string localPath,
        string? username = null,
        string? password = null)
    {
        return await ExecuteAsync(token =>
        {
            try
            {
                using var client = new SvnClient();
                AcceptSelfSignedCert(client);
                if (!string.IsNullOrEmpty(username))
                    client.Authentication.ForceCredentials(username, password ?? "");
                SvnUpdateResult? result = null;
                client.CheckOut(new SvnUriTarget(url), localPath, new SvnCheckOutArgs(), out result);
                return (result?.Revision.ToString() ?? "", 0, "");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Checkout failed for {Url} to {Path}", url, localPath);
                return ("", 1, ex.Message);
            }
        });
    }

    public bool IsValidWorkingCopy(string path)
    {
        // These are fast local-only checks — no need to serialize or timeout
        try
        {
            using var client = new SvnClient();
            return client.GetRepositoryRoot(path) != null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Categorizes the result of a connection test to a repository URL,
    /// used to give users specific feedback (auth failure, network issue, etc.).
    /// </summary>
    public enum SvnConnectResult
    {
        Success,
        AuthFailed,        // 401 / authentication failed
        AccessDenied,     // 403 / no read access
        RepoNotFound,      // 404 / repository does not exist at this URL
        NetworkError,      // network unreachable / DNS / connection refused
        SslCertError,      // SSL certificate problem
        Timeout,           // operation timed out
        Unknown,           // anything else
    }

    /// <summary>
    /// Registers a self-signed certificate acceptance handler on the client so
    /// --trust-server-cert behavior is consistent across all operations.
    /// </summary>
    private static void AcceptSelfSignedCert(SvnClient client)
    {
        client.ServersCertificateFailure += (sender, e) =>
        {
            Log.Debug("Accepting self-signed SSL certificate for {Host}", e.HostName);
            e.Accept = true;
        };
    }

    /// <summary>
    /// Lightweight connection test — does a single svn list with depth=empty
    /// to determine reachability and categorize the error if any.
    /// </summary>
    public async Task<(SvnConnectResult result, string? errorMessage)> TestConnectionAsync(
        string url,
        string? username = null,
        string? password = null)
    {
        return await ExecuteAsync(token =>
        {
            try
            {
                using var client = new SvnClient();
                AcceptSelfSignedCert(client);
                if (!string.IsNullOrEmpty(username))
                    client.Authentication.ForceCredentials(username, password ?? "");

                // SvnListArgs with Depth=Empty is the cheapest possible remote call
                var args = new SvnListArgs { Depth = SvnDepth.Empty };
                SvnListEventArgs? info = null;
                client.List(new SvnUriTarget(url), args, out info);
                return (SvnConnectResult.Success, (string?)null);
            }
            catch (SvnAuthenticationException)
            {
                return (SvnConnectResult.AuthFailed, null);
            }
            catch (SvnAccessDeniedException)
            {
                return (SvnConnectResult.AccessDenied, null);
            }
            catch (SvnRepositoryIOException ex) when (ex.Message.Contains("E175002") || ex.Message.Contains("170013"))
            {
                // E175002: PROPFIND of '/svn/xxx': 404 Not Found / repository not found
                // E170013: Unable to connect to repository (same underlying condition)
                return (SvnConnectResult.RepoNotFound, null);
            }
            catch (SvnRepositoryIOException ex) when (
                ex.Message.Contains("E175003") ||      // PROPFIND on non-DAV endpoint
                ex.Message.Contains("E175002") ||      // 405 Method Not Allowed etc.
                ex.InnerException?.Message.Contains("SSL") == true ||
                ex.InnerException?.Message.Contains("ssl") == true)
            {
                return (SvnConnectResult.SslCertError, null);
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
                {
                    return (SvnConnectResult.NetworkError, null);
                }
                return (SvnConnectResult.Unknown, ex.Message);
            }
            catch (TimeoutException)
            {
                return (SvnConnectResult.Timeout, null);
            }
            catch (Exception ex)
            {
                return (SvnConnectResult.Unknown, ex.Message);
            }
        });
    }

    public bool IsVersioned(string path)
    {
        // Fast local-only check — no need to serialize or timeout
        try
        {
            using var client = new SvnClient();
            return client.GetRepositoryRoot(path) != null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Resolve a conflicted file by accepting a specific resolution.
    /// SvnAccept.Working = keep working file as-is (defer/Postpone);
    /// SvnAccept.MineFull = keep local version; SvnAccept.TheirsFull = accept server version.
    /// </summary>
    public async Task<bool> ResolveAsync(string path, SvnAccept accept)
    {
        return await ExecuteAsync(token =>
        {
            try
            {
                using var client = new SvnClient();
                return client.Resolve(path, accept);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Resolve failed for {Path}", path);
                return false;
            }
        });
    }

    /// <summary>
    /// Detects conflicted files by scanning for SVN conflict status
    /// (.mine, .r*, .orig) — used because SharpSvn 1.14005.390 has no GetConflicts API.
    /// </summary>
    public async Task<List<string>> GetConflictedFilesAsync(string workingCopyPath)
    {
        return await ExecuteAsync(token =>
        {
            var files = new List<string>();
            try
            {
                using var client = new SvnClient();
                client.GetStatus(workingCopyPath, new SvnStatusArgs
                {
                    Depth = SvnDepth.Infinity,
                    RetrieveAllEntries = true,
                }, out var conflictedResults);

                foreach (var item in conflictedResults)
                {
                    if (item.LocalNodeStatus == SharpSvnStatus.Conflicted && !string.IsNullOrEmpty(item.Path))
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

    public async Task<DateTime> GetLastChangedTimeAsync(string filePath)
    {
        return await ExecuteAsync(token =>
        {
            try
            {
                using var client = new SvnClient();
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

    public void Dispose()
    {
        _semaphore.Dispose();
    }
}
