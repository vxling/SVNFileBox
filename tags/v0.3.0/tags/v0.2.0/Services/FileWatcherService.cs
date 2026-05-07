#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Timers;
using Serilog;

namespace SVNFileBox.Services;

public class FileWatcherService : IDisposable
{
    private FileSystemWatcher? _watcher;
    private readonly System.Timers.Timer _debounceTimer;
    private readonly System.Timers.Timer _retryTimer;
    private readonly object _lock = new();
    private readonly HashSet<string> _changedFiles = new();
    private int _debounceMs = 5000;
    private string? _watchPath;
    private bool _isRetrying;

    public event EventHandler<string[]>? FilesChanged;

    public bool IsWatching => _watcher != null && _watcher.EnableRaisingEvents;

    public FileWatcherService()
    {
        _debounceTimer = new System.Timers.Timer(_debounceMs);
        _debounceTimer.AutoReset = false;
        _debounceTimer.Elapsed += OnDebounceTimerElapsed;

        _retryTimer = new System.Timers.Timer(5000);
        _retryTimer.AutoReset = false;
        _retryTimer.Elapsed += OnRetryTimerElapsed;

        Log.Debug("FileWatcherService created");
    }

    public void StartWatching(string path)
    {
        _watchPath = path;
        StartWatchingInternal();
    }

    private void StartWatchingInternal()
    {
        if (string.IsNullOrEmpty(_watchPath)) return;

        StopWatching();

        if (!Directory.Exists(_watchPath))
        {
            Log.Warning("Cannot watch non-existent path: {Path}", _watchPath);
            return;
        }

        Log.Information("Starting file watch on: {Path}", _watchPath);

        try
        {
            _watcher = new FileSystemWatcher(_watchPath)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName
                             | NotifyFilters.DirectoryName
                             | NotifyFilters.LastWrite
                             | NotifyFilters.Size
            };

            _watcher.Changed += OnFileChanged;
            _watcher.Created += OnFileChanged;
            _watcher.Deleted += OnFileChanged;
            _watcher.Renamed += OnFileRenamed;
            _watcher.Error += OnWatcherError;

            _watcher.EnableRaisingEvents = true;
            _isRetrying = false;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to create FileSystemWatcher for {Path}", _watchPath);
        }
    }

    public void StopWatching()
    {
        if (_watcher != null)
        {
            Log.Debug("Stopping file watch");
            _watcher.EnableRaisingEvents = false;
            try { _watcher.Dispose(); } catch { }
            _watcher = null;
        }
    }

    public void SetDebounceMs(int ms)
    {
        _debounceMs = ms;
        _debounceTimer.Interval = ms;
    }

    private bool IsSvnDirectory(string path)
    {
        // Normalize to handle both Unix '/' and Windows '\' separators
        var normalized = path.Replace('/', Path.DirectorySeparatorChar);
        return normalized.Contains(Path.DirectorySeparatorChar + ".svn" + Path.DirectorySeparatorChar)
            || normalized.EndsWith(Path.DirectorySeparatorChar + ".svn");
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        if (IsSvnDirectory(e.FullPath)) return;

        lock (_lock)
        {
            _changedFiles.Add(e.FullPath);
            _debounceTimer.Stop();
            _debounceTimer.Start();
        }
    }

    private void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        if (IsSvnDirectory(e.FullPath)) return;

        lock (_lock)
        {
            _changedFiles.Add(e.OldFullPath);
            _changedFiles.Add(e.FullPath);
            _debounceTimer.Stop();
            _debounceTimer.Start();
        }
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        var ex = e.GetException();
        Log.Error(ex, "FileWatcher error, will attempt reconnect");

        if (!_isRetrying && !string.IsNullOrEmpty(_watchPath))
        {
            _isRetrying = true;
            _retryTimer.Stop();
            _retryTimer.Start();
        }
    }

    private void OnRetryTimerElapsed(object? sender, ElapsedEventArgs e)
    {
        Log.Information("Attempting to reconnect FileSystemWatcher to {Path}", _watchPath);
        StartWatchingInternal();
        if (_watcher == null || !_watcher.EnableRaisingEvents)
        {
            Log.Warning("Reconnection failed, will retry in 10s");
            _retryTimer.Interval = 10000;
            _retryTimer.Start();
        }
        else
        {
            Log.Information("FileSystemWatcher reconnected successfully");
            _retryTimer.Interval = 5000;
            _isRetrying = false;
        }
    }

    private void OnDebounceTimerElapsed(object? sender, ElapsedEventArgs e)
    {
        string[] files;
        lock (_lock)
        {
            files = _changedFiles.ToArray();
            _changedFiles.Clear();
        }

        if (files.Length > 0)
        {
            Log.Debug("Files changed: {Count} files", files.Length);
            FilesChanged?.Invoke(this, files);
        }
    }

    public void Dispose()
    {
        StopWatching();
        _debounceTimer.Dispose();
        _retryTimer.Stop();
        _retryTimer.Dispose();
    }
}
