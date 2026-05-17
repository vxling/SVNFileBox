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
/// Concurrency model (read-write separation):
///   - Read operations (status/info/revision queries) take _readSemaphore.
///     Multiple reads can run concurrently; they never block writes.
///   - Write operations (update/commit/add/delete/move/revert) take _writeSemaphore.
///     Only one write runs at a time; they are fully serialized.
///   - This separation means a long-running update does NOT block svn status
///     from completing, and vice versa.
///
/// Static semaphores are shared across all SvnService instances.
public class SvnService : IDisposable
{
    /// <summary>
    /// Serializes all write SVN operations (update/commit/add/delete/move/revert).
    /// Static so all SvnService instances share the same lock.
    /// Only one write operation runs at a time.
    /// </summary>
    private static readonly SemaphoreSlim _writeSemaphore = new(1, 1);

    /// <summary>
    /// Allows concurrent read SVN operations (status/info/revision queries).
    /// Static so all SvnService instances share the same pool.
    /// Up to 10 reads can run simultaneously without blocking each other.
    /// </summary>
    private static readonly SemaphoreSlim _readSemaphore = new(10, 10);

    /// <summary>
    /// Deduplicates concurrent GetHeadRevisionAsync calls for the same repoUrl.
    /// Key = repoUrl, Value = in-flight Task{revision}.
    /// If two callers request the same repoUrl simultaneously, the second reuses
    /// the first caller's in-flight HTTP request instead of firing a duplicate.
    /// </summary>
    private static readonly Dictionary<string, Task<int>> _headRevisionCache = new();

    /// <summary>
    /// Lock protecting _headRevisionCache (not the data inside — the dict keys/values).
    /// Very short hold: only while reading/writing the dict entry itself.
    /// </summary>
    private static readonly SemaphoreSlim _headRevisionLock = new(1, 1);

    /// <summary>
    /// How long a GetHeadRevision result is considered fresh before a new server call is made.
    /// </summary>
    private static readonly TimeSpan HeadRevisionCacheTtl = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Max time to wait for the semaphore lock (30s). If another operation holds the lock
    /// longer than this, we give up — that operation is likely stuck.
    /// </summary>
    private const int LockWaitTimeoutMs = 30_000;

    /// <summary>
    /// Safety-net hard timeout: even if progress events keep firing, the operation will be
    /// cancelled after this long (600s). Prevents infinite hangs in extreme cases.
    /// </summary>
    private const int SafetyNetTimeoutMs = 600_000;

    /// <summary>
    /// Network-level HTTP timeout. Applied via ServicePointManager for all HTTP requests.
    /// </summary>
    private const int HttpTimeoutMs = 60_000;

    /// <summary>
    /// Default idle activity timeout for file transfer operations (Update/Commit), in milliseconds.
    /// Read from ConfigService at startup and updated via FileTransferTimeoutChanged event.
    /// </summary>
    private static int _fileTransferTimeoutMs = 120_000;

    public static int FileTransferTimeoutMs
    {
        get => _fileTransferTimeoutMs;
        set => _fileTransferTimeoutMs = Math.Clamp(value, 30_000, 600_000);
    }

    /// <summary>
    /// Raised when SettingsWindow saves a new FileTransferTimeoutSeconds value.
    /// Subscribing SvnService instances update their cached timeout from this event.
    /// </summary>
    public static event Action? FileTransferTimeoutChanged;

    /// <summary>
    /// Raised whenever a file is transferred during an Update or Commit operation.
    /// The event carries the file path and the SharpSvn Notify action (e.g., update, commit).
    /// </summary>
    public static event Action<string, string>? FileTransferActivity;

    /// <summary>
    /// Call this after updating FileTransferTimeoutMs to notify all SvnService instances.
    /// </summary>
    public static void NotifyFileTransferTimeoutChanged() => FileTransferTimeoutChanged?.Invoke();

    /// <summary>
    /// Creates a SvnClient with SSL certificate auto-accept pre-configured.
    /// Must be called BEFORE any SVN operation on the client instance.
    /// </summary>
    private static SvnClient CreateClient()
    {
        var client = new SvnClient(); // raw, not via CreateClient() to avoid recursion
        client.Authentication.SslServerTrustHandlers += (sender, e) =>
        {
            e.AcceptedFailures = e.Failures;
            e.Save = true;
        };
        return client;
    }

    public SvnService()
    {
#pragma warning disable SYSLIB0014
        // Set HTTP-level timeouts for all WebRequest/WebResponse operations
        // SharpSvn uses HttpWebRequest internally — these settings still affect its HTTP behavior
        ServicePointManager.DefaultConnectionLimit = 4;
        ServicePointManager.Expect100Continue = false;
        ServicePointManager.FindServicePoint(new Uri("https://dummy")).ConnectionLeaseTimeout = HttpTimeoutMs;
#pragma warning restore SYSLIB0014

        Log.Information("SvnService initialized — SharpSvn {Version}, lock timeout {LockTimeout}s, HTTP timeout {HttpTimeout}s",
            typeof(SvnClient).Assembly.GetName().Version?.ToString() ?? "unknown",
            LockWaitTimeoutMs / 1000,
            HttpTimeoutMs / 1000);
    }

    /// <summary>
    /// Runs a read SVN operation (status/info/revision) with shared read lock + timeout.
    /// Multiple reads can run concurrently; they never block writes.
    /// Does NOT use activity-based timeout — wall clock timeout is appropriate for reads.
    /// </summary>
    private async Task<T> ExecuteAsync<T>(Func<CancellationToken, T> operation, CancellationToken cancellationToken = default)
    {
        // Wait max 30s for a read slot — reads are concurrent so wait is usually brief
        if (!await _readSemaphore.WaitAsync(LockWaitTimeoutMs, cancellationToken))
        {
            throw new TimeoutException(
                $"SVN read operation timed out waiting for a read slot after {LockWaitTimeoutMs / 1000}s. " +
                "Too many concurrent reads may be blocking. Try again or restart the application.");
        }

        try
        {
            // Hard safety-net timeout (600s) — covers even the slowest read operations
            using var safetyNetCts = new CancellationTokenSource();
            safetyNetCts.CancelAfter(SafetyNetTimeoutMs);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, safetyNetCts.Token);

            var result = await Task.Run(() => operation(linkedCts.Token), linkedCts.Token);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"SVN read operation timed out after {SafetyNetTimeoutMs / 1000}s. Server may be unreachable.");
        }
        finally
        {
            _readSemaphore.Release();
        }
    }

    /// <summary>
    /// Runs an SVN file-transfer write operation (Update/Commit) with exclusive write lock
    /// + activity-based idle timeout. As long as SharpSvn.Notify events keep firing
    /// (files being transferred), the operation is considered alive and will not time out.
    /// Only when the idle period exceeds FileTransferTimeoutMs will it be cancelled.
    /// SharpSvn calls are synchronous so they run on a background thread with cancellation support.
    /// </summary>
    private async Task<T> ExecuteWithProgressTimeoutAsync<T>(
        Func<CancellationToken, CancellationTokenSource, T> operation,
        CancellationToken cancellationToken = default)
    {
        if (!await _writeSemaphore.WaitAsync(LockWaitTimeoutMs, cancellationToken))
        {
            throw new TimeoutException(
                $"SVN operation timed out waiting for write lock after {LockWaitTimeoutMs / 1000}s. " +
                "Another SVN operation may be stuck. Try again or restart the application.");
        }

        try
        {
            var idleTimeoutMs = FileTransferTimeoutMs;
            using var progressCts = new CancellationTokenSource();
            using var safetyNetCts = new CancellationTokenSource();

            // Safety net: absolute ceiling, even if progress events keep firing
            safetyNetCts.CancelAfter(SafetyNetTimeoutMs);

            // Progress idle timeout: will fire if no file-transfer activity is detected
            progressCts.CancelAfter(idleTimeoutMs);

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, progressCts.Token, safetyNetCts.Token);

            // Run on background thread with cancellation support
            // progressCts is captured by the closure so ExecuteSvnWithNotify can cancel it
            // when the activity watchdog fires, triggering linkedCTS → interrupting SharpSvn
            var result = await Task.Run(() => operation(linkedCts.Token, progressCts), linkedCts.Token);
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"文件传输超时（{FileTransferTimeoutMs / 1000}s 无活动）。服务器可能已断开，请检查网络后重试。");
        }
        finally
        {
            _writeSemaphore.Release();
        }
    }

    /// <summary>
    /// Wraps a synchronous SharpSvn operation (Update/Commit) with a Notify-based activity watchdog.
    /// The watchdog resets on each file-transfer Notify event. If no activity is seen for
    /// FileTransferTimeoutMs, it fires the provided progressCts to trigger cancellation through
    /// the linked CTS chain, interrupting the SharpSvn operation mid-flight.
    /// Runs the operation on a background thread so it can be cancelled mid-execution.
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
            var action = e.Action.ToString();
            var path = e.Path ?? "";
            Log.Debug("[SvnService] Transfer: {Action} {Path}", action, path);
            FileTransferActivity?.Invoke(path, action);
        };

        // Watchdog: if no Notify fires within FileTransferTimeoutMs, cancel progressCTS
        // which propagates through linkedCTS → Task.Run → SharpSvn operation interrupted
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
                    Log.Warning("[SvnService] No file transfer activity for {Seconds}s, cancelling",
                        timeoutMs / 1000);
                    progressCts.Cancel();  // fires linkedCTS → interrupts SharpSvn
                    break;
                }
            }
        }, token);

        try
        {
            return svnOperation(client);
        }
        finally
        {
            watchdogCts.Cancel();
            try { watchdog.Wait(500); } catch { }
        }
    }

    public async Task<Dictionary<string, FileSvnStatus>> GetStatusAsync(string workingCopyPath, SvnDepth depth = SvnDepth.Children)
    {
        return await ExecuteAsync(token =>
        {
            var statuses = new Dictionary<string, FileSvnStatus>();

            try
            {
                using var client = CreateClient();
                var handler = new EventHandler<SvnStatusEventArgs>(delegate (object? sender, SvnStatusEventArgs item)
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
                        SharpSvnStatus.Incomplete => FileSvnStatus.Missing, // Incomplete = missing/inaccessible locally → 显示 "!"
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

    public async Task<int> GetWorkingCopyRevisionAsync(string workingCopyPath)
    {
        return await ExecuteAsync(token =>
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

    public async Task<int> GetHeadRevisionAsync(string repoUrl, string? username = null, string? password = null)
    {
        // Deduplicate in-flight requests for the same repoUrl.
        // The cache holds the Task directly so concurrent callers all await the same HTTP call.
        var newTask = DoGetHeadRevisionAsync(repoUrl);
        Task<int>? inFlightTask;

        lock (_headRevisionLock)
        {
            if (_headRevisionCache.TryGetValue(repoUrl, out inFlightTask))
            {
                // Another call for the same URL is already in flight — reuse it
                Log.Debug("[GetHeadRevisionAsync] Reusing in-flight request for {Url}", repoUrl);
            }
            else
            {
                // First call for this URL — store our task; concurrent callers will find it
                inFlightTask = newTask;
                _headRevisionCache[repoUrl] = newTask;
            }
        }

        // If there was a racing in-flight task, await that instead of ours
        if (inFlightTask != newTask)
            return await inFlightTask;

        try
        {
            var result = await newTask;
            return result;
        }
        finally
        {
            // Evict after TTL so stale entries don't accumulate
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

    private async Task<int> DoGetHeadRevisionAsync(string repoUrl)
    {
        return await ExecuteAsync(token =>
        {
            try
            {
                using var client = CreateClient();
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
                Log.Error(ex, "[GetHeadRevisionAsync] Failed for {Url}", repoUrl);
                return -1;
            }
        });
    }

    public async Task<bool> CommitAsync(string workingCopyPath, string message, string? username = null, string? password = null)
    {
        return await ExecuteWithProgressTimeoutAsync((token, progressCts) =>
        {
            TryCleanStaleLocks(workingCopyPath); // Ensure no stale lock from a previously interrupted operation
            return ExecuteSvnWithNotify(client =>
            {
                var args = new SvnCommitArgs { LogMessage = message };
                return client.Commit(workingCopyPath, args);
            }, token, progressCts);
        });
    }

    public async Task<bool> UpdateAsync(string workingCopyPath, string? username = null, string? password = null)
    {
        return await ExecuteWithProgressTimeoutAsync((token, progressCts) =>
        {
            TryCleanStaleLocks(workingCopyPath);
            return ExecuteSvnWithNotify(client =>
            {
                return client.Update(workingCopyPath);
            }, token, progressCts);
        });
    }

    public async Task<bool> AddFileAsync(string filePath)
    {
        return await ExecuteAsync(token =>
        {
            TryCleanStaleLocks(GetWorkingCopyRoot(filePath));
            using var client = CreateClient();
            return client.Add(filePath);
        });
    }

    public async Task<bool> AddPathAsync(string path)
    {

        return await ExecuteAsync(token =>
        {
            TryCleanStaleLocks(GetWorkingCopyRoot(path));
            using var client = CreateClient();
            try
            {
                return client.Add(path);
            }
            catch (SharpSvn.SvnEntryException ex) when(ex.Message.Contains("already"))
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

    public async Task<bool> DeleteAsync(string path)
    {

        return await ExecuteAsync(token =>
        {
            TryCleanStaleLocks(GetWorkingCopyRoot(path));
            using var client = CreateClient();
            try
            {
                return client.Delete(path);
            }
            catch (SharpSvn.SvnUnversionedNodeException)
            {
                Log.Warning("[SvnService] File is not under version control, cannot delete and ignore: {Path}", path);
                return true;
            }
            catch (SharpSvn.SvnException ex) when (ex.Message.Contains("NotFound") || ex.InnerException is FileNotFoundException)
            {
                Log.Warning("[SvnService] File not found, treating as already deleted: {Path}", path);
                return true;
            }
            catch (SharpSvn.SvnWorkingCopyLockException)
            {
                Log.Warning("[SvnService] Working copy is locked, treating as already deleted: {Path}", path);
                return true;
            }
        }
            );

    }

    /// <summary>
    /// Returns the working copy root directory (the directory containing .svn)
    /// for any path within the working copy.
    /// </summary>
    private string GetWorkingCopyRoot(string path)
    {
        using var client = CreateClient();
        return client.GetWorkingCopyRoot(path);
    }

    public async Task<bool> MoveAsync(string fromPath, string toPath)
    {
        return await ExecuteAsync(token =>
        {
            TryCleanStaleLocks(GetWorkingCopyRoot(fromPath));
            using var client = CreateClient();
            try
            {
                return client.Move(fromPath, toPath);
            }
            catch (SharpSvn.SvnException ex) when (ex.InnerException is FileNotFoundException)
            {
                Log.Warning("[SvnService] Source file not found, cannot move: {FromPath} -> {ToPath}", fromPath, toPath);
                return false;
            }
            catch (SharpSvn.SvnWorkingCopyPathNotFoundException)
            {
                // Source already gone — treat as success
                return true;
            }
        }
            );

    }

    public async Task<bool> RevertAsync(string path, bool recursive = true)
    {
        return await ExecuteAsync(token =>
        {
            TryCleanStaleLocks(GetWorkingCopyRoot(path));
            using var client = CreateClient();
            var args = new SvnRevertArgs { Depth = recursive ? SvnDepth.Infinity : SvnDepth.Empty };
            return client.Revert(path, args);
        });
    }

    /// <summary>
    /// Attempts to clean any stale working-copy locks left by a previously interrupted operation.
    /// This is always called at the start of a write operation to ensure no residual lock blocks
    /// the new operation, even if the previous one was cancelled mid-flight.
    /// Never throws — cleanup failures are logged but do not prevent the operation from proceeding.
    /// </summary>
    private void TryCleanStaleLocks(string workingCopyPath)
    {
        try
        {
            using var client = CreateClient();
            bool result = client.CleanUp(workingCopyPath);
            Log.Debug("[SvnService] Stale lock cleaned for {Path}, result: {Result}", workingCopyPath, result ? "success" : "failed");
        }
        catch (Exception ex)
        {
            // If cleanup fails (e.g. no lock present, or already cleaned by another process),
            // just log and continue — the subsequent SVN operation will handle its own errors.
            Log.Debug(ex, "[SvnService] Cleanup attempt had no stale lock to remove for {Path}", workingCopyPath);
        }
    }

    public async Task<(string output, int exitCode, string error)> CheckoutAsync(
        string repoUrl,
        string workingCopyPath,
        string? username = null,
        string? password = null)
    {
        return await ExecuteAsync(token =>
        {
            TryCleanStaleLocks(GetWorkingCopyRoot(workingCopyPath));
            using var client = CreateClient();
            if (!string.IsNullOrEmpty(username))
                client.Authentication.ForceCredentials(username, password ?? "");

            SvnUpdateResult? result = null;
            client.CheckOut(new SvnUriTarget(repoUrl), workingCopyPath, new SvnCheckOutArgs(), out result);
            return (result?.Revision.ToString() ?? "", 0, "");
        });
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
        Timeout,          // operation timed out
        Unknown,           // anything else
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
                using var client = CreateClient();
                if (!string.IsNullOrEmpty(username))
                    client.Authentication.ForceCredentials(username, password ?? "");

                SvnListEventArgs? info = null;
                client.List(new SvnUriTarget(url), new SvnListArgs { Depth = SvnDepth.Empty },
                    new EventHandler<SvnListEventArgs>((s, e) => info = e));
                return (SvnConnectResult.Success, (string?)null);
            }
            catch (SvnAuthenticationException)
            {
                return (SvnConnectResult.AuthFailed, null);
            }
            catch (SvnAuthorizationException)
            {
                return (SvnConnectResult.AccessDenied, null);
            }
            catch (SvnRepositoryIOException ex)
            {
                if (ex.Message.Contains("E230001") || ex.InnerException?.Message.Contains("E230001") == true)
                    return (SvnConnectResult.SslCertError, null);
                if (ex.Message.Contains("E175002") || ex.Message.Contains("E170013") || ex.Message.Contains("170013"))
                    return (SvnConnectResult.RepoNotFound, null);
                if (ex.Message.Contains("E175003"))
                    return (SvnConnectResult.SslCertError, null);
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
            using var client = CreateClient();
            return client.GetRepositoryRoot(path) != null;
        }
        catch
        {
            return false;
        }
    }

    public bool IsValidWorkingCopy(string path)
    {
        // Fast local-only check — no need to serialize or timeout
        try
        {
            using var client = CreateClient();
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
            TryCleanStaleLocks(GetWorkingCopyRoot(path));
            using var client = CreateClient();
            return client.Resolve(path, accept);
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
                using var client = CreateClient();
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

    public async Task<bool> BreakWriteLockAsync(string path)
    {
        return await ExecuteAsync(token =>
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

    public void Dispose()
    {
        // Note: _semaphore is static and is NOT disposed here, since other
        // SvnService instances may still be using it. Static resources
        // are intentionally left to the process to clean up.
    }
}
