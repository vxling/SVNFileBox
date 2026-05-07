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

    private readonly string _recordsPath;
    private readonly JsonSerializerOptions _jsonOptions;
    private int _retentionDays = 30;

    public ObservableCollection<SyncRecord> Records { get; } = new();

    /// <summary>Set from MainViewModel to marshal record additions to the UI thread.</summary>
    public Dispatcher? UiDispatcher { get; set; }

    private SyncRecordService()
    {
        var configDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SVNFileBox");
        Directory.CreateDirectory(configDir);
        _recordsPath = Path.Combine(configDir, "sync_records.json");

        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        Load();
    }

    public void SetRetentionDays(int days) => _retentionDays = days;

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
            Save();
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
        while (Records.Count > 0 && Records[^1].Timestamp < cutoff)
        {
            Records.RemoveAt(Records.Count - 1);
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_recordsPath)) return;
            var json = File.ReadAllText(_recordsPath);
            var records = JsonSerializer.Deserialize<List<SyncRecord>>(json, _jsonOptions);
            if (records != null)
            {
                Records.Clear();
                foreach (var r in records.OrderByDescending(r => r.Timestamp))
                    Records.Add(r);
            }
            TrimOldRecords();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to load sync records");
        }
    }

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(Records.ToList(), _jsonOptions);
            File.WriteAllText(_recordsPath, json);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save sync records");
        }
    }
}
