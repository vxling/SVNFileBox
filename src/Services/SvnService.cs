#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using SharpSvn;
using SVNFileBox.Models;
using Serilog;

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

    public async Task<Dictionary<string, SvnStatus>> GetStatusAsync(string workingCopyPath)
    {
        var statuses = new Dictionary<string, SvnStatus>();

        await Task.Run(() =>
        {
            try
            {
                var collection = new SvnStatusEventArgs();
                bool success = _client.GetStatus(workingCopyPath, new SvnStatusArgs
                {
                    Depth = SvnDepth.Infinity,
                    RetrieveAllEntries = true,
                }, out collection);

                if (!success || collection == null)
                {
                    // W155010 = unversioned directory
                    if (Directory.Exists(workingCopyPath))
                    {
                        foreach (var file in Directory.GetFiles(workingCopyPath, "*", SearchOption.TopDirectoryOnly))
                            statuses[file] = SvnStatus.Unversioned;
                        foreach (var dir in Directory.GetDirectories(workingCopyPath, "*", SearchOption.TopDirectoryOnly))
                            statuses[dir] = SvnStatus.Unversioned;
                    }
                    return;
                }

                foreach (SvnStatusEventArgs item in collection)
                {
                    var path = item.FullPath;
                    if (string.IsNullOrEmpty(path)) continue;

                    // Skip the root if unversioned ("? .")
                    if (item.LocalNodeStatus == SvnStatus.NotVersioned &&
                        (path == workingCopyPath || path.EndsWith(".")))
                        continue;

                    var svnStatus = item.LocalNodeStatus switch
                    {
                        SvnStatus.Modified => SvnStatus.Modified,
                        SvnStatus.Added => SvnStatus.Added,
                        SvnStatus.Deleted => SvnStatus.Deleted,
                        SvnStatus.Conflicted => SvnStatus.Conflicted,
                        SvnStatus.NotVersioned => SvnStatus.Unversioned,
                        SvnStatus.Missing => SvnStatus.Missing,
                        SvnStatus.Replaced => SvnStatus.Replaced,
                        SvnStatus.Obstructed => SvnStatus.Obstructed,
                        SvnStatus.External => SvnStatus.External,
                        SvnStatus.Incomplete => SvnStatus.Unknown,
                        _ => SvnStatus.Normal
                    };

                    if (svnStatus != SvnStatus.Normal || !statuses.ContainsKey(path))
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
                if (_client.GetRepositoryRoot(workingCopyPath, out var root))
                    return root.ToString();
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
                if (_client.Info(workingCopyPath, out var info))
                    return (int)info.Revision;
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
                var args = new SvnInfoEventArgs();
                if (_client.GetInfo(uri, new SvnUriTarget(uri, SvnRevision.Head), out args))
                    return (int)args.Revision;
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
                var targets = new SvnPathTargetCollection { new SvnPathTarget(workingCopyPath) };
                var args = new SvnCommitArgs { LogMessage = message };
                return _client.Commit(targets, args);
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
                var result = _client.Update(workingCopyPath);
                return result != null;
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

    public async Task<bool> CleanupAsync(string workingCopyPath)
    {
        return await Task.Run(() =>
        {
            try
            {
                return _client.Cleanup(workingCopyPath);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Cleanup failed for {Path}", workingCopyPath);
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
                var added = _client.Add(directoryPath, new SvnAddArgs { Depth = SvnDepth.Infinity });
                return (added.Count.ToString(), 0);
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
                return _client.Unlock(path);
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
                return _client.Lock(path, "", false);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Break lock failed for {Path}", path);
                return false;
            }
        });
    }

    /// <summary>
    /// Checkout a remote SVN repository to a local path.
    /// </summary>
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
                var result = _client.CheckOut(new Uri(url), localPath);
                return (result.Revision.ToString(), 0, "");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Checkout failed for {Url} to {Path}", url, localPath);
                return ("", 1, ex.Message);
            }
        });
    }

    /// <summary>
    /// Check if a directory is a valid SVN working copy.
    /// </summary>
    public bool IsValidWorkingCopy(string path)
    {
        try
        {
            return _client.GetRepositoryRoot(path, out _);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Resolve a conflicted file by accepting a specific resolution.
    /// </summary>
    public async Task<bool> ResolveAsync(string path, SvnAccept accept)
    {
        return await Task.Run(() =>
        {
            try
            {
                return _client.Resolve(path, new SvnResolveArgs { Accept = accept });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Resolve failed for {Path}", path);
                return false;
            }
        });
    }

    /// <summary>
    /// Get conflicted files in a working copy.
    /// </summary>
    public async Task<List<string>> GetConflictedFilesAsync(string workingCopyPath)
    {
        return await Task.Run(() =>
        {
            var files = new List<string>();
            try
            {
                var args = new SvnConflictEventArgs();
                if (_client.GetConflicts(workingCopyPath, out var conflicts))
                {
                    foreach (SvnConflictEventArgs conflict in conflicts)
                    {
                        if (!string.IsNullOrEmpty(conflict.Path))
                            files.Add(Path.Combine(workingCopyPath, conflict.Path));
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error getting conflicts for {Path}", workingCopyPath);
            }
            return files;
        });
    }

    /// <summary>
    /// Get the last changed time of a file from SVN (UTC).
    /// </summary>
    public async Task<DateTime> GetLastChangedTimeAsync(string filePath)
    {
        return await Task.Run(() =>
        {
            try
            {
                if (_client.Info(filePath, out var info))
                    return info.LastChangeTime.ToUniversalTime();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error getting last changed time for {Path}", filePath);
            }
            return DateTime.MinValue;
        });
    }

    /// <summary>
    /// Check if a path is under SVN version control.
    /// </summary>
    public bool IsVersioned(string path)
    {
        try
        {
            return _client.GetRepositoryRoot(path, out _);
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        _client.Dispose();
    }
}
