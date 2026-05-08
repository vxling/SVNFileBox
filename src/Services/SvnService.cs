#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using SVNFileBox.Models;
using Serilog;

namespace SVNFileBox.Services;

public class SvnService
{
    // C# static initialization is thread-safe — equivalent to a static variable initialized once at program start
    private static readonly string _svnPath;
    private static readonly string? _svnError;
    private static bool _validated;

    static SvnService()
    {
        _svnPath = FindSvnPath(out var error);
        _svnError = error;
        Log.Information("SvnService static init — svnPath={SvnPath}, error={Error}", _svnPath, _svnError ?? "none");
    }

    public SvnService()
    {
        EnsureSvnAvailable();
    }

    /// <summary>
    /// Throws InvalidOperationException if SVN was not found at static init time.
    /// Safe to call from any constructor or method — only fires once.
    /// </summary>
    private static void EnsureSvnAvailable()
    {
        if (_validated) return;
        _validated = true;

        if (!string.IsNullOrEmpty(_svnError))
            throw new InvalidOperationException(
                $"SVN executable not found: {_svnError}. Please install TortoiseSVN or SlikSVN and ensure svn.exe is in PATH or under Program Files.");
    }

    private static string FindSvnPath(out string? error)
    {
        error = null;
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string[] possiblePaths = new[]
        {
            Path.Combine(programFiles, "TortoiseSVN", "bin", "svn.exe"),
            Path.Combine(programFiles, "VisualSVN Server", "bin", "svn.exe"),
            Path.Combine(programFiles, "SlikSvn", "bin", "svn.exe"),
            "svn"
        };


        foreach (var p in possiblePaths)
        {
            if (File.Exists(p)) return p;
        }

        // svn in PATH
        try
        {
            var result = RunCommandSync("svn", "--version --quiet");
            if (result.exitCode == 0 && !string.IsNullOrWhiteSpace(result.output))
                return "svn";
        }
        catch { }


        error = $"TortoiseSVN/VisualSVN/SlikSvn not found in Program Files ({programFiles}), and 'svn' not in PATH." +
                " Please install TortoiseSVN (https://tortoisesvn.net) and ensure 'bin' folder is in PATH.";
        return "svn";
    }

    private static (string output, int exitCode, string error) RunCommandSync(string fileName, string arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            }
        };
        process.Start();
        var output = process.StandardOutput.ReadToEnd().Trim();
        var error = process.StandardError.ReadToEnd().Trim();
        process.WaitForExit();
        return (output, process.ExitCode, error);
    }

    public async Task<bool> IsSvnAvailableAsync()
    {
        try
        {
            var result = await RunCommandAsync("--version --quiet");
            return result.exitCode == 0 && !string.IsNullOrWhiteSpace(result.output);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "SVN not available");
            return false;
        }
    }

    public async Task<Dictionary<string, SvnStatus>> GetStatusAsync(string workingCopyPath)
    {
        var statuses = new Dictionary<string, SvnStatus>();
        var hasDotUnversioned = false;

        try
        {
            Log.Debug("[GetStatusAsync] Running svn status --xml on: {Path}", workingCopyPath);
            var result = await RunCommandAsync($"status --non-interactive --xml \"{workingCopyPath}\"");
            Log.Debug("[GetStatusAsync] Raw output ({Len} chars)", result.output.Length);

            if (result.exitCode != 0)
            {
                // W155010 = unversioned directory — enumerate files directly and mark all as Unversioned
                if (result.error.Contains("W155010") && Directory.Exists(workingCopyPath))
                {
                    Log.Debug("[GetStatusAsync] Directory is unversioned, enumerating files directly");
                    foreach (var file in Directory.GetFiles(workingCopyPath, "*", SearchOption.TopDirectoryOnly))
                    {
                        var relPath = file;
                        if (!statuses.ContainsKey(relPath))
                            statuses[relPath] = SvnStatus.Unversioned;
                    }
                    foreach (var dir in Directory.GetDirectories(workingCopyPath, "*", SearchOption.TopDirectoryOnly))
                    {
                        var relPath = dir;
                        if (!statuses.ContainsKey(relPath))
                            statuses[relPath] = SvnStatus.Unversioned;
                    }
                    Log.Debug("[GetStatusAsync] Unversioned directory: added {Count} entries", statuses.Count);
                    return statuses;
                }
                Log.Warning("SVN status failed: {Error}", result.error);
                return statuses;
            }

            var doc = XDocument.Parse(result.output);
            var ns = doc.Root?.GetDefaultNamespace() ?? XNamespace.None;

            // Find <target> elements (files/directories)
            var targetElements = doc.Descendants(ns + "target");
            foreach (var target in targetElements)
            {
                var entryElements = target.Elements(ns + "entry");
                foreach (var entry in entryElements)
                {
                    var path = entry.Attribute("path")?.Value?.Trim();
                    if (string.IsNullOrEmpty(path)) continue;

                    var wcStatusEl = entry.Element(ns + "wc-status");
                    if (wcStatusEl == null) continue;

                    var itemAttr = wcStatusEl.Attribute("item");
                    var propAttr = wcStatusEl.Attribute("props");
                    var item = itemAttr?.Value ?? "";
                    var prop = propAttr?.Value ?? "";

                    // '? .' or '? <fullPath>' means the directory itself is unversioned
                    if (item == "unversioned" && (path == "." || path.Equals(workingCopyPath, StringComparison.OrdinalIgnoreCase)))
                    {
                        hasDotUnversioned = true;
                        Log.Debug("[GetStatusAsync] Detected unversioned current dir: path={Path}", path);
                        continue;
                    }

                    // Determine status: item attr takes precedence, prop attr indicates modifications
                    var svnStatus = item switch
                    {
                        "modified" => SvnStatus.Modified,
                        "added" => SvnStatus.Added,
                        "deleted" => SvnStatus.Deleted,
                        "conflicted" => SvnStatus.Conflicted,
                        "unversioned" => SvnStatus.Unversioned,
                        "missing" => SvnStatus.Missing,
                        "replaced" => SvnStatus.Replaced,
                        "obstructed" => SvnStatus.Obstructed,
                        "external" => SvnStatus.External,
                        "incomplete" => SvnStatus.Unknown,
                        _ => SvnStatus.Normal
                    };

                    if (svnStatus != SvnStatus.Normal || !statuses.ContainsKey(path))
                    {
                        statuses[path] = svnStatus;
                    }
                }
            }
            Log.Debug("[GetStatusAsync] Parsed {Count} status entries", statuses.Count);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error getting SVN status for {Path}", workingCopyPath);
        }

        // If the current directory itself is unversioned ("? ."), mark all children as unversioned too
        if (hasDotUnversioned && Directory.Exists(workingCopyPath))
        {
            Log.Debug("[GetStatusAsync] '? .' detected, enumerating unversioned children in {Path}", workingCopyPath);
            foreach (var file in Directory.GetFiles(workingCopyPath, "*", SearchOption.TopDirectoryOnly))
            {
                if (!statuses.ContainsKey(file))
                    statuses[file] = SvnStatus.Unversioned;
            }
            foreach (var dir in Directory.GetDirectories(workingCopyPath, "*", SearchOption.TopDirectoryOnly))
            {
                if (!statuses.ContainsKey(dir))
                    statuses[dir] = SvnStatus.Unversioned;
            }
            Log.Debug("[GetStatusAsync] '? .' enumeration added {Count} entries", statuses.Count);
        }

        return statuses;
    }

    public async Task<string> GetRepoUrlAsync(string workingCopyPath)
    {
        try
        {
            var result = await RunCommandAsync($"info --non-interactive --xml \"{workingCopyPath}\"");
            if (result.exitCode == 0)
            {
                var doc = XDocument.Parse(result.output);
                var ns = doc.Root?.GetDefaultNamespace() ?? XNamespace.None;
                var urlEl = doc.Descendants(ns + "url").FirstOrDefault();
                return urlEl?.Value?.Trim() ?? "";
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error getting repo URL for {Path}", workingCopyPath);
        }
        return "";
    }

    public async Task<int> GetWorkingCopyRevisionAsync(string workingCopyPath)
    {
        try
        {
            var result = await RunCommandAsync($"info --non-interactive --xml \"{workingCopyPath}\"");
            Log.Debug("GetWorkingCopyRevision: path={Path}, exitCode={ExitCode}", workingCopyPath, result.exitCode);
            if (result.exitCode == 0)
            {
                var doc = XDocument.Parse(result.output);
                var ns = doc.Root?.GetDefaultNamespace() ?? XNamespace.None;
                var revEl = doc.Descendants(ns + "entry").FirstOrDefault();
                var revAttr = revEl?.Attribute("revision");
                if (revAttr != null && int.TryParse(revAttr.Value, out var revision))
                    return revision;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error getting revision for {Path}", workingCopyPath);
        }
        return -1;
    }

    public async Task<int> GetHeadRevisionAsync(string repoUrl, string? username = null, string? password = null)
    {
        try
        {
            var args = $"info --non-interactive -r HEAD --xml \"{repoUrl}\"";
            if (!string.IsNullOrEmpty(username))
            {
                args = $"--username \"{username}\" " + args;
                if (!string.IsNullOrEmpty(password))
                    args = $"--password \"{password}\" " + args;
            }
            Log.Debug("GetHeadRevision: args={Args}", args);
            var result = await RunCommandAsync(args);
            Log.Debug("GetHeadRevision: url={Url}, exitCode={ExitCode}", repoUrl, result.exitCode);
            if (result.exitCode == 0)
            {
                var doc = XDocument.Parse(result.output);
                var ns = doc.Root?.GetDefaultNamespace() ?? XNamespace.None;
                var revEl = doc.Descendants(ns + "entry").FirstOrDefault();
                var revAttr = revEl?.Attribute("revision");
                if (revAttr != null && int.TryParse(revAttr.Value, out var revision))
                    return revision;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error getting HEAD revision for {Url}", repoUrl);
        }
        return -1;
    }

    public async Task<(string output, int exitCode, string error)> RunCommandAsync(string arguments, int timeoutMs = 60000)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = _svnPath,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                }
            };

            var outputBuilder = new StringBuilder();
            var errorBuilder = new StringBuilder();
            var lockObj = new object();

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                lock (lockObj) { outputBuilder.AppendLine(e.Data); }
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                lock (lockObj) { errorBuilder.AppendLine(e.Data); }
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var waitTask = process.WaitForExitAsync();
            var timeoutTask = Task.Delay(timeoutMs);

            // Wait for either process exit or timeout — whichever comes first
            var firstFinished = await Task.WhenAny(waitTask, timeoutTask);

            if (firstFinished == timeoutTask)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                // Block until process actually terminates after kill
                try { process.WaitForExit(500); } catch { }
                Log.Warning("SVN command timed out after {Timeout}ms: {Arguments}", timeoutMs, arguments);
                return ("", 1, $"Command timed out after {timeoutMs / 1000}s");
            }

            // Process exited before timeout — waitTask is already done, just grab output.
            // Do NOT await timeoutTask (that would waste time waiting for the full delay).

            string output, error;
            lock (lockObj)
            {
                output = outputBuilder.ToString().Trim();
                error = errorBuilder.ToString().Trim();
            }

            Log.Debug("[RunCommandAsync] exitCode={Code} outputLen={OutLen} errorLen={ErrLen} args={Args}", process.ExitCode, output.Length, error.Length, arguments);

            return (output, process.ExitCode, error);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to run svn {Arguments}", arguments);
            return ("", 1, ex.Message);
        }
    }

    public async Task<bool> CommitAsync(string workingCopyPath, string message, string? username = null, string? password = null)
    {
        Log.Information("Committing {Path} with message: {Message}", workingCopyPath, message);
        var args = $"commit --non-interactive -m \"{message}\" \"{workingCopyPath}\"";
        if (!string.IsNullOrEmpty(username))
            args = $"--username \"{username}\" " + args;
        if (!string.IsNullOrEmpty(password))
            args = $"--password \"{password}\" " + args;
        var result = await RunCommandAsync(args);
        return result.exitCode == 0;
    }

    public async Task<bool> UpdateAsync(string workingCopyPath, string? username = null, string? password = null)
    {
        Log.Information("Updating {Path}", workingCopyPath);
        var args = $"update --non-interactive \"{workingCopyPath}\"";
        if (!string.IsNullOrEmpty(username))
            args = $"--username \"{username}\" " + args;
        if (!string.IsNullOrEmpty(password))
            args = $"--password \"{password}\" " + args;
        var result = await RunCommandAsync(args);
        return result.exitCode == 0;
    }

    public async Task<bool> AddFileAsync(string filePath)
    {
        var result = await RunCommandAsync($"add --non-interactive \"{filePath}\"");
        return result.exitCode == 0;
    }

    public async Task<bool> DeleteAsync(string path)
    {
        Log.Information("Running svn delete for {Path}", path);
        var result = await RunCommandAsync($"delete --non-interactive \"{path}\"");
        return result.exitCode == 0;
    }

    public async Task<bool> RevertAsync(string path, bool recursive = true)
    {
        var recurseFlag = recursive ? "--recursive" : "";
        var result = await RunCommandAsync($"revert --non-interactive {recurseFlag} \"{path}\"");
        return result.exitCode == 0;
    }

    public async Task<bool> CleanupAsync(string workingCopyPath)
    {
        var result = await RunCommandAsync($"cleanup \"{workingCopyPath}\"");
        return result.exitCode == 0;
    }

    public async Task<(string output, int exitCode)> SvnAddRecursiveAsync(string directoryPath)
    {
        var result = await RunCommandAsync($"add --force --non-interactive \"{directoryPath}\"");
        return (result.output, result.exitCode);
    }

    public async Task<bool> UnlockAsync(string path)
    {
        Log.Information("Running svn unlock for {Path}", path);
        var result = await RunCommandAsync($"unlock --non-interactive \"{path}\"");
        return result.exitCode == 0;
    }

    public async Task<bool> BreakWriteLockAsync(string path)
    {
        Log.Information("Running svn unlock --break-write-lock for {Path}", path);
        var result = await RunCommandAsync($"unlock --non-interactive --break-lock \"{path}\"");
        return result.exitCode == 0;
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
        var args = $"checkout --non-interactive \"{url}\" \"{localPath}\"";
        if (!string.IsNullOrEmpty(username))
        {
            args = $"--username \"{username}\" " + args;
            if (!string.IsNullOrEmpty(password))
            {
                args = $"--password \"{password}\" " + args;
            }
        }

        Log.Information("Running svn checkout: {Args}", args);
        var result = await RunCommandAsync(args);
        return (result.output, result.exitCode, result.error);
    }

    /// <summary>
    /// Check if a directory is a valid SVN working copy.
    /// </summary>
    public bool IsValidWorkingCopy(string path)
    {
        return Directory.Exists(path) && File.Exists(Path.Combine(path, ".svn", "entries"));
    }
}
