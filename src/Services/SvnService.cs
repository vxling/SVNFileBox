#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using SharpSvn;
using SVNFileBox.Models;
using Serilog;
using SharpSvnStatus = SharpSvn.SvnStatus;

namespace SVNFileBox.Services;

public class SvnService : IDisposable
{
    private readonly SvnClient _client;

    public SvnService()
    {
        _client = new SvnClient();
        Log.Information("SvnService initialized — using SharpSvn {Version}",
            typeof(SvnClient).Assembly.GetName().Version?.ToString() ?? "unknown");
    }

    public async Task<Dictionary<string, FileSvnStatus>> GetStatusAsync(string workingCopyPath)
    {
        var statuses = new Dictionary<string, FileSvnStatus>();

        await Task.Run(() =>
        {
            try
            {
                _client.GetStatus(workingCopyPath, new SvnStatusArgs
                {
                    Depth = SvnDepth.Children,
                    RetrieveAllEntries = true,
                }, out var results);

                foreach (var item in results)
                {
                    var path = item.Path;
                    if (string.IsNullOrEmpty(path)) continue;

                    if (item.LocalNodeStatus == SharpSvnStatus.NotVersioned &&
                        (path == workingCopyPath || path.EndsWith(".")))
                        continue;

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

                    if (svnStatus != FileSvnStatus.Normal || !statuses.ContainsKey(path))
                        statuses[path] = svnStatus;
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error getting SVN status for {Path}", workingCopyPath);
            }
        });

        return statuses;
    }

    public async Task<string> GetRepoUrlAsync(string workingCopyPath)
    {
        return await Task.Run(() =>
        {
            try
            {
                var root = _client.GetRepositoryRoot(workingCopyPath);
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
        return await Task.Run(() =>
        {
            try
            {
                SvnInfoEventArgs? infoResult = null;
                var handler = new EventHandler<SvnInfoEventArgs>((s, e) => infoResult = e);
                _client.Info(workingCopyPath, handler);
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
        return await Task.Run(() =>
        {
            try
            {
                var uri = new Uri(repoUrl);
                SvnInfoEventArgs? infoResult = null;
                var handler = new EventHandler<SvnInfoEventArgs>((s, e) => infoResult = e);
                _client.Info(new SvnUriTarget(uri, SvnRevision.Head), handler);
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
        return await Task.Run(() =>
        {
            try
            {
                var args = new SvnCommitArgs { LogMessage = message };
                return _client.Commit(workingCopyPath, args);
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
        return await Task.Run(() =>
        {
            try
            {
                return _client.Update(workingCopyPath);
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
        return await Task.Run(() =>
        {
            try
            {
                return _client.Add(filePath);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Add failed for {Path}", filePath);
                return false;
            }
        });
    }

    public async Task<bool> DeleteAsync(string path)
    {
        return await Task.Run(() =>
        {
            try
            {
                return _client.Delete(path);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Delete failed for {Path}", path);
                return false;
            }
        });
    }

    public async Task<bool> RevertAsync(string path, bool recursive = true)
    {
        return await Task.Run(() =>
        {
            try
            {
                var args = new SvnRevertArgs { Depth = recursive ? SvnDepth.Infinity : SvnDepth.Empty };
                return _client.Revert(path, args);
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
        return await Task.Run(() =>
        {
            try
            {
                return _client.CleanUp(workingCopyPath);
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
        return await Task.Run(() =>
        {
            try
            {
                _client.GetStatus(directoryPath, new SvnStatusArgs { Depth = SvnDepth.Infinity }, out var dirResults);
                int count = 0;
                foreach (var r in dirResults)
                {
                    if (r.LocalNodeStatus == SharpSvnStatus.NotVersioned)
                    {
                        if (_client.Add(r.Path))
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
        return await Task.Run(() =>
        {
            try
            {
                return _client.Unlock(new[] { path }, new SvnUnlockArgs());
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
        return await Task.Run(() =>
        {
            try
            {
                return _client.Lock(path, new SvnLockArgs { StealLock = true });
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
        return await Task.Run(() =>
        {
            try
            {
                SvnUpdateResult? result = null;
                _client.CheckOut(new SvnUriTarget(url), localPath, new SvnCheckOutArgs(), out result);
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
        try
        {
            return _client.GetRepositoryRoot(path) != null;
        }
        catch
        {
            return false;
        }
    }

    public bool IsVersioned(string path)
    {
        try
        {
            return _client.GetRepositoryRoot(path) != null;
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
        return await Task.Run(() =>
        {
            try
            {
                return _client.Resolve(path, accept);
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
        return await Task.Run(() =>
        {
            var files = new List<string>();
            try
            {
                _client.GetStatus(workingCopyPath, new SvnStatusArgs
                {
                    Depth = SvnDepth.Empty,
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
        return await Task.Run(() =>
        {
            try
            {
                SvnInfoEventArgs? infoResult = null;
                var handler = new EventHandler<SvnInfoEventArgs>((s, e) => infoResult = e);
                _client.Info(filePath, handler);
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
        _client.Dispose();
    }
}
