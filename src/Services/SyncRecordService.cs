using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Threading;
using SVNFileBox.Models;
using Serilog;

namespace SVNFileBox.Services;

public class SyncRecordService
{
    private static SyncRecordService? _instance;
    public static SyncRecordService Instance => _instance ??= new SyncRecordService();

    private readonly string _recordsDir;
    private readonly JsonSerializerOptions _jsonOptions;
    private int _retentionDays = 30;

    public ObservableCollection<SyncRecord> Records { get; } = new();

    /// <summary>Set from MainViewModel to marshal record additions to the UI thread.</summary>
    public Dispatcher? UiDispatcher { get; set; }

    public SyncRecordService()
    {
        var configDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SVNFileBox");
        _recordsDir = Path.Combine(configDir, "sync_records");
        Directory.CreateDirectory(_recordsDir);

        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        LoadAll();
    }

    public void SetRetentionDays(int days) => _retentionDays = days;

    /// <summary>Get records for a specific repo, or all repos if repoName is null/empty.</summary>
    public IEnumerable<SyncRecord> GetRecords(string? repoName = null)
    {
        if (string.IsNullOrEmpty(repoName))
            return Records;
        return Records.Where(r => r.RepoName == repoName);
    }

    public void AddRecord(string repoName, string filePath, string operation, string result, string message = "")
    {
        var record = new SyncRecord
        {
            Timestamp = DateTime.Now,
            RepoName = repoName,
            FilePath = filePath,
            Operation = operation,
            Result = result,
            Message = message
        };

        Action addRecord = () =>
        {
            Records.Insert(0, record);
            TrimOldRecords();
            SaveRecordsForRepo(repoName);
        };

        if (UiDispatcher != null && UiDispatcher.CheckAccess())
            addRecord();
        else if (UiDispatcher != null)
            UiDispatcher.Invoke(addRecord);
        else
            addRecord();

        Log.Debug("SyncRecord added: [{Op}] {Path} -> {Result}", operation, filePath, result);
    }

    public void AddRecord(string repoName, string filePath, string operation, string result)
        => AddRecord(repoName, filePath, operation, result, "");

    private void TrimOldRecords()
    {
        var cutoff = DateTime.Now.AddDays(-_retentionDays);
        var toTrim = Records.Where(r => r.Timestamp < cutoff).ToList();
        foreach (var r in toTrim)
        {
            Records.Remove(r);
            DeleteRecordFile(r.RepoName);
        }
    }

    private string RepoFilePath(string repoName) =>
        Path.Combine(_recordsDir, SanitizeFileName(repoName) + ".json");

    private static string SanitizeFileName(string name) =>
        string.Join("_", name.Split(Path.GetInvalidFileNameChars()));

    private void DeleteRecordFile(string repoName)
    {
        try
        {
            var path = RepoFilePath(repoName);
            if (File.Exists(path))
            {
                var remaining = Records.Count(r => r.RepoName == repoName);
                if (remaining == 0)
                    File.Delete(path);
                else
                    SaveRecordsForRepo(repoName);
            }
        }
        catch { }
    }

    private void LoadAll()
    {
        try
        {
            Records.Clear();
            if (!Directory.Exists(_recordsDir)) return;
            foreach (var file in Directory.GetFiles(_recordsDir, "*.json"))
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var records = JsonSerializer.Deserialize<List<SyncRecord>>(json, _jsonOptions);
                    if (records != null)
                        foreach (var r in records)
                            Records.Add(r);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Failed to load sync record file: {File}", file);
                }
            }
            foreach (var r in Records.OrderByDescending(r => r.Timestamp).ToList())
            {
                Records.Remove(r);
                Records.Add(r);
            }
            TrimOldRecords();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to load sync records");
        }
    }

    private void SaveRecordsForRepo(string repoName)
    {
        try
        {
            var repoRecords = Records.Where(r => r.RepoName == repoName).ToList();
            var json = JsonSerializer.Serialize(repoRecords, _jsonOptions);
            File.WriteAllText(RepoFilePath(repoName), json);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save sync records for repo: {Repo}", repoName);
        }
    }
}
