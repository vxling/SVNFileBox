#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using SVNFileBox.Models;
using Serilog;

namespace SVNFileBox.Services;

/// <summary>
/// Sync record service backed by SQLite (one table per repo).
/// Retention: MaxAgeDays = 10 days, MaxRecordsPerRepo = 10,000 records.
/// </summary>
public class SyncRecordService
{
    private static SyncRecordService? _instance;
    public static SyncRecordService Instance => _instance ??= new SyncRecordService();

    private readonly SqliteSyncRecordStore _store;

    public ObservableCollection<SyncRecord> Records { get; } = new();

    /// <summary>Set from MainViewModel to marshal record additions to the UI thread.</summary>
    public Dispatcher? UiDispatcher { get; set; }

    public SyncRecordService()
    {
        _store = new SqliteSyncRecordStore();

        // Run cleanup on startup (async, fire-and-forget)
        Task.Run(() => _store.CleanupAll());

        Log.Information("[SyncRecordService] Initialized with SQLite store");
    }

    /// <summary>Get records for a specific repo (loads from SQLite) or all repos if null.</summary>
    public IEnumerable<SyncRecord> GetRecords(string? repoName = null)
    {
        if (!string.IsNullOrEmpty(repoName))
        {
            _store.EnsureRepo(repoName);
            return _store.GetRecords(repoName);
        }
        return _store.GetAllRecords();
    }

    /// <summary>Get records for a specific repo and populate the in-memory collection.</summary>
    public void LoadRecordsForRepo(string repoName)
    {
        _store.EnsureRepo(repoName);
        Records.Clear();
        foreach (var r in _store.GetRecords(repoName))
            Records.Add(r);
    }

    public void AddRecord(string repoName, string filePath, string operation, string result, string message = "")
    {
        var timestamp = DateTime.Now;
        var record = new SyncRecord
        {
            Timestamp = timestamp,
            RepoName = repoName,
            FilePath = filePath,
            Operation = operation,
            Result = result,
            Message = message
        };

        Action addRecord = () =>
        {
            Records.Insert(0, record);
            if (Records.Count > 1000) Records.RemoveAt(Records.Count - 1);
        };

        if (UiDispatcher != null && UiDispatcher.CheckAccess())
            addRecord();
        else if (UiDispatcher != null)
            UiDispatcher.Invoke(addRecord);
        else
            addRecord();

        // Persist to SQLite
        _store.EnsureRepo(repoName);
        _store.AddRecord(repoName, timestamp, filePath, operation, result, message);

        Log.Debug("SyncRecord added: [{Op}] {Path} -> {Result}", operation, filePath, result);
    }

    public void AddRecord(string repoName, string filePath, string operation, string result)
        => AddRecord(repoName, filePath, operation, result, "");

    /// <summary>Call this when a repository is removed — drops its record table.</summary>
    public void DeleteRepoRecords(string repoName)
    {
        _store.DeleteRepo(repoName);
    }

    public void SetRetentionDays(int days) { /* now fixed at 10 */ }
}