using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;
using Serilog;
using SVNFileBox.Models;

namespace SVNFileBox.Services;

/// <summary>
/// SQLite-backed sync record store — single shared table.
/// Schema: one table sync_records with repo_name as a normal column.
/// Retention: MaxAgeDays = 10, MaxRecordsPerRepo = 10,000.
/// </summary>
public class SqliteSyncRecordStore : IDisposable
{
    private static SqliteSyncRecordStore? _instance;
    public static SqliteSyncRecordStore Instance => _instance ??= new SqliteSyncRecordStore();

    private readonly string _dbPath;
    private readonly SqliteConnection _conn;
    private bool _disposed;

    private const int MaxAgeDays = 10;
    private const int MaxRecordsPerRepo = 10_000;

    public SqliteSyncRecordStore()
    {
        var configDir = AppPaths.Base;
        _dbPath = Path.Combine(configDir, "sync_records.db");

        _conn = new SqliteConnection($"Data Source={_dbPath}");
        _conn.Open();

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS sync_records (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                repo_name TEXT NOT NULL,
                timestamp TEXT NOT NULL,
                file_path TEXT NOT NULL,
                operation TEXT NOT NULL,
                result TEXT NOT NULL,
                message TEXT NOT NULL DEFAULT ''
            );
            CREATE INDEX IF NOT EXISTS idx_repo ON sync_records(repo_name);
            CREATE INDEX IF NOT EXISTS idx_ts ON sync_records(timestamp);";
        cmd.ExecuteNonQuery();

        Log.Information("[SqliteSyncRecordStore] Opened {Path}", _dbPath);
    }

    /// <summary>Adds a record. Auto-trims oldest if over MaxRecordsPerRepo.</summary>
    public void AddRecord(string repoName, DateTime timestamp, string filePath,
        string operation, string result, string message)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO sync_records (repo_name, timestamp, file_path, operation, result, message)
            VALUES (@repo, @ts, @path, @op, @result, @msg)";
        cmd.Parameters.AddWithValue("@repo", repoName);
        cmd.Parameters.AddWithValue("@ts", timestamp.ToString("O"));
        cmd.Parameters.AddWithValue("@path", filePath);
        cmd.Parameters.AddWithValue("@op", operation);
        cmd.Parameters.AddWithValue("@result", result);
        cmd.Parameters.AddWithValue("@msg", message);
        cmd.ExecuteNonQuery();

        TrimByCount(repoName);
    }

    /// <summary>Returns records for a repo, newest first.</summary>
    public IEnumerable<SyncRecord> GetRecords(string repoName, int limit = 1000)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            SELECT id, repo_name, timestamp, file_path, operation, result, message
            FROM sync_records
            WHERE repo_name = @repo
            ORDER BY id DESC
            LIMIT @limit";
        cmd.Parameters.AddWithValue("@repo", repoName);
        cmd.Parameters.AddWithValue("@limit", limit);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            yield return new SyncRecord
            {
                Id = reader.GetInt64(0),
                RepoName = reader.GetString(1),
                Timestamp = DateTime.Parse(reader.GetString(2)),
                FilePath = reader.GetString(3),
                Operation = reader.GetString(4),
                Result = reader.GetString(5),
                Message = reader.GetString(6)
            };
        }
    }

    /// <summary>Returns all records across all repos, newest first.</summary>
    public IEnumerable<SyncRecord> GetAllRecords(int limit = 1000)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            SELECT id, repo_name, timestamp, file_path, operation, result, message
            FROM sync_records
            ORDER BY id DESC
            LIMIT @limit";
        cmd.Parameters.AddWithValue("@limit", limit);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            yield return new SyncRecord
            {
                Id = reader.GetInt64(0),
                RepoName = reader.GetString(1),
                Timestamp = DateTime.Parse(reader.GetString(2)),
                FilePath = reader.GetString(3),
                Operation = reader.GetString(4),
                Result = reader.GetString(5),
                Message = reader.GetString(6)
            };
        }
    }

    /// <summary>
    /// Deletes all records for a repository.
    /// </summary>
    public void DeleteRepo(string repoName)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM sync_records WHERE repo_name = @repo";
        cmd.Parameters.AddWithValue("@repo", repoName);
        int deleted = cmd.ExecuteNonQuery();
        Log.Information("[SqliteSyncRecordStore] Deleted {Count} records for repo: {Repo}", deleted, repoName);
    }

    /// <summary>
    /// Runs cleanup: removes records older than MaxAgeDays and trims each repo to MaxRecordsPerRepo.
    /// </summary>
    public void CleanupAll()
    {
        // Trim by age
        var cutoff = DateTime.Now.AddDays(-MaxAgeDays).ToString("O");
        using var delCmd = _conn.CreateCommand();
        delCmd.CommandText = "DELETE FROM sync_records WHERE timestamp < @cutoff";
        delCmd.Parameters.AddWithValue("@cutoff", cutoff);
        int deleted = delCmd.ExecuteNonQuery();
        if (deleted > 0)
            Log.Debug("[SqliteSyncRecordStore] Trimmed {Count} old records", deleted);

        // Trim by count per repo (belt-and-suspenders)
        using var repoCmd = _conn.CreateCommand();
        repoCmd.CommandText = "SELECT DISTINCT repo_name FROM sync_records";
        using var reader = repoCmd.ExecuteReader();
        var repoNames = new List<string>();
        while (reader.Read()) repoNames.Add(reader.GetString(0));
        reader.Close();

        foreach (var name in repoNames)
            TrimByCount(name);
    }

    private void TrimByCount(string repoName)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            DELETE FROM sync_records
            WHERE repo_name = @repo
              AND id NOT IN (
                  SELECT id FROM sync_records WHERE repo_name = @repo
                  ORDER BY id DESC LIMIT @limit
              )";
        cmd.Parameters.AddWithValue("@repo", repoName);
        cmd.Parameters.AddWithValue("@limit", MaxRecordsPerRepo);
        int removed = cmd.ExecuteNonQuery();
        if (removed > 0)
            Log.Debug("[SqliteSyncRecordStore] Trimmed {Count} excess records for {Repo}", removed, repoName);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _conn.Dispose();
        _disposed = true;
    }
}