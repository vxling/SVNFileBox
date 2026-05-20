using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;
using Serilog;
using SVNFileBox.Models;

namespace SVNFileBox.Services;

/// <summary>
/// SQLite-backed sync record store.
/// Schema: one table per repo (name sanitized), auto-cleanup by age + count.
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
        var configDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SVNFileBox");
        _dbPath = Path.Combine(configDir, "sync_records.db");

        _conn = new SqliteConnection($"Data Source={_dbPath}");
        _conn.Open();

        // Shared journal table — cleanup runs against this table
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS sync_meta (
                repo_name TEXT PRIMARY KEY,
                created_at TEXT NOT NULL
            );";
        cmd.ExecuteNonQuery();

        Log.Information("[SqliteSyncRecordStore] Opened {Path}", _dbPath);
    }

    // ── Per-repo table helpers ────────────────────────────────────────

    private static string TableName(string repoName) =>
        $"sync_{Sanitize(repoName)}";

    private static string Sanitize(string name) =>
        string.Join("_", name.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Ensures the per-repo table exists and meta is registered.</summary>
    public void EnsureRepo(string repoName)
    {
        var table = TableName(repoName);
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $@"
            CREATE TABLE IF NOT EXISTS {table} (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp TEXT NOT NULL,
                file_path TEXT NOT NULL,
                operation TEXT NOT NULL,
                result TEXT NOT NULL,
                message TEXT NOT NULL DEFAULT ''
            );
            CREATE INDEX IF NOT EXISTS idx_{table}_timestamp ON {table}(timestamp);";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "INSERT OR IGNORE INTO sync_meta (repo_name, created_at) VALUES (@name, @now)";
        cmd.Parameters.Clear();
        cmd.Parameters.AddWithValue("@name", repoName);
        cmd.Parameters.AddWithValue("@now", DateTime.Now.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>Adds a record for the given repo. Auto-trims oldest if over MaxRecordsPerRepo.</summary>
    public void AddRecord(string repoName, DateTime timestamp, string filePath,
        string operation, string result, string message)
    {
        var table = TableName(repoName);

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $@"
            INSERT INTO {table} (timestamp, file_path, operation, result, message)
            VALUES (@ts, @path, @op, @result, @msg)";
        cmd.Parameters.AddWithValue("@ts", timestamp.ToString("O"));
        cmd.Parameters.AddWithValue("@path", filePath);
        cmd.Parameters.AddWithValue("@op", operation);
        cmd.Parameters.AddWithValue("@result", result);
        cmd.Parameters.AddWithValue("@msg", message);
        cmd.ExecuteNonQuery();

        // Trim by count
        TrimByCount(table);
    }

    /// <summary>Returns all records for a repo, newest first.</summary>
    public IEnumerable<SyncRecord> GetRecords(string repoName, int limit = 1000)
    {
        var table = TableName(repoName);
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT timestamp, file_path, operation, result, message
            FROM {table}
            ORDER BY id DESC
            LIMIT @limit";
        cmd.Parameters.AddWithValue("@limit", limit);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            yield return new SyncRecord
            {
                Timestamp = DateTime.Parse(reader.GetString(0)),
                FilePath = reader.GetString(1),
                Operation = reader.GetString(2),
                Result = reader.GetString(3),
                Message = reader.GetString(4)
            };
        }
    }

    /// <summary>Returns recent records across all repos, newest first.</summary>
    public IEnumerable<SyncRecord> GetAllRecords(int limit = 1000)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            SELECT repo_name, timestamp, file_path, operation, result, message
            FROM sync_meta m
            JOIN pragma_table_list(m.repo_name) t ON t.name = 'sync_' || m.repo_name
            ORDER BY m.created_at DESC
            LIMIT @limit";
        cmd.Parameters.AddWithValue("@limit", limit);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            yield return new SyncRecord
            {
                RepoName = reader.GetString(0),
                Timestamp = DateTime.Parse(reader.GetString(1)),
                FilePath = reader.GetString(2),
                Operation = reader.GetString(3),
                Result = reader.GetString(4),
                Message = reader.GetString(5)
            };
        }
    }

    // ── Cleanup ──────────────────────────────────────────────────────

    /// <summary>
    /// Drops the per-repo table when a repository is removed.
    /// Call this from wherever the repo is deleted.
    /// </summary>
    public void DeleteRepo(string repoName)
    {
        var table = TableName(repoName);
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"DROP TABLE IF EXISTS {table}";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "DELETE FROM sync_meta WHERE repo_name = @name";
        cmd.Parameters.Clear();
        cmd.Parameters.AddWithValue("@name", repoName);
        cmd.ExecuteNonQuery();

        Log.Information("[SqliteSyncRecordStore] Deleted records for repo: {Repo}", repoName);
    }

    /// <summary>Runs cleanup on all repos: removes records older than MaxAgeDays and trims to MaxRecordsPerRepo.</summary>
    public void CleanupAll()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT repo_name FROM sync_meta";
        var repoNames = new List<string>();
        using (var reader = cmd.ExecuteReader())
            while (reader.Read()) repoNames.Add(reader.GetString(0));

        foreach (var name in repoNames)
        {
            var table = TableName(name);

            // Trim by age
            var cutoff = DateTime.Now.AddDays(-MaxAgeDays).ToString("O");
            using var delCmd = _conn.CreateCommand();
            delCmd.CommandText = $"DELETE FROM {table} WHERE timestamp < @cutoff";
            delCmd.Parameters.AddWithValue("@cutoff", cutoff);
            int deleted = delCmd.ExecuteNonQuery();
            if (deleted > 0)
                Log.Debug("[SqliteSyncRecordStore] Trimmed {Count} old records for {Repo}", deleted, name);

            // Trim by count (already done on insert, but belt-and-suspenders here)
            TrimByCount(table);
        }
    }

    private void TrimByCount(string table)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $@"
            DELETE FROM {table}
            WHERE id NOT IN (
                SELECT id FROM {table} ORDER BY id DESC LIMIT {MaxRecordsPerRepo}
            )";
        int removed = cmd.ExecuteNonQuery();
        if (removed > 0)
            Log.Debug("[SqliteSyncRecordStore] Trimmed {Count} excess records from {Table}", removed, table);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _conn.Dispose();
        _disposed = true;
    }
}