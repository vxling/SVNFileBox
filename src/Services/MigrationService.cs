#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Serilog;
using SVNFileBox.Models;

namespace SVNFileBox.Services;

/// <summary>
/// 将旧版数据（AppData）迁移到新版目录（~/.svnfilebox/）。
/// </summary>
public static class MigrationService
{
    /// <summary>旧版数据根目录。</summary>
    public static string OldBase => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SVNFileBox");

    /// <summary>
    /// 执行迁移（若无需迁移则直接返回）。
    /// progress?.Report(string) 每次汇报当前步骤描述。
    /// </summary>
    public static async Task<bool> MigrateIfNeededAsync(IProgress<string>? progress = null)
    {
        if (!Directory.Exists(OldBase))
        {
            Log.Information("[Migration] No old data found at {OldBase}, skipping", OldBase);
            return true;
        }

        Log.Information("[Migration] Old data found at {OldBase}, starting migration", OldBase);
        progress?.Report("正在迁移旧数据...");

        try
        {
            var oldWorkcopiesDir = Path.Combine(OldBase, "workcopies");
            var newWorkcopiesDir = AppPaths.WorkCopies;

            // ── 0. 先解析旧配置，提取需要迁移的网络仓库列表 ─────────────────
            var reposToMigrate = new List<(string oldPath, string newPath, string name)>();
            var oldConfigPath = Path.Combine(OldBase, "config.json");

            if (File.Exists(oldConfigPath))
            {
                var json = await File.ReadAllTextAsync(oldConfigPath);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("Repositories", out var repos) && repos.ValueKind == JsonValueKind.Array)
                {
                    foreach (var elem in repos.EnumerateArray())
                    {
                        var repo = JsonSerializer.Deserialize<Repository>(elem.GetRawText());
                        if (repo == null || string.IsNullOrEmpty(repo.Path)) continue;

                        // 只有网络仓库才在 workcopies 目录下，需要迁移
                        if (repo.RepositoryType == RepositoryType.Network
                            && repo.Path.StartsWith(oldWorkcopiesDir, StringComparison.OrdinalIgnoreCase))
                        {
                            var repoName = new DirectoryInfo(repo.Path).Name;
                            var oldWcPath = Path.Combine(oldWorkcopiesDir, repoName);
                            var newWcPath = Path.Combine(newWorkcopiesDir, repoName);

                            // 新路径已存在，说明已经迁移过或有手动配置的同名仓库，跳过
                            if (Directory.Exists(newWcPath))
                            {
                                Log.Information("[Migration] Skip {Name}: new working copy already exists at {NewPath}",
                                    repoName, newWcPath);
                                continue;
                            }

                            reposToMigrate.Add((oldWcPath, newWcPath, repoName));
                        }
                    }
                }
            }

            // ── 1. 迁移配置文件 ────────────────────────────────────────────
            progress?.Report("迁移配置文件...");
            await MigrateConfigAsync(oldConfigPath, oldWorkcopiesDir, newWorkcopiesDir);
            Log.Information("[Migration] Config migrated");

            // ── 2. 迁移同步记录数据库（仅在新路径不存在时） ─────────────────
            var newDbPath = Path.Combine(AppPaths.Config, "sync_records.db");
            if (!File.Exists(newDbPath))
            {
                progress?.Report("迁移同步记录...");
                var oldDbPath = Path.Combine(OldBase, "sync_records.db");
                if (File.Exists(oldDbPath))
                {
                    Directory.CreateDirectory(AppPaths.Config);
                    File.Copy(oldDbPath, newDbPath, overwrite: false);
                    Log.Information("[Migration] Sync records DB migrated");
                }
            }
            else
            {
                Log.Information("[Migration] Skip sync records DB: already exists at {NewPath}", newDbPath);
            }

            // ── 3. 迁移工作副本（仅网络仓库 + 新路径不存在时） ─────────────
            if (reposToMigrate.Count > 0)
            {
                progress?.Report("迁移工作副本...");
                Directory.CreateDirectory(newWorkcopiesDir);

                var i = 0;
                foreach (var (oldPath, newPath, name) in reposToMigrate)
                {
                    i++;
                    var pct = i * 100 / reposToMigrate.Count;
                    progress?.Report($"迁移工作副本: {name} ({i}/{reposToMigrate.Count})");

                    if (!Directory.Exists(oldPath))
                    {
                        Log.Information("[Migration] Skip {Name}: old working copy not found at {OldPath}", name, oldPath);
                        continue;
                    }

                    await CopyDirectoryAsync(oldPath, newPath);
                    Log.Information("[Migration] Migrated working copy {Name} ({Pct}%)", name, pct);
                }
            }
            else
            {
                Log.Information("[Migration] No network repos with old working copies to migrate");
            }

            // ── 4. 删除旧数据 ─────────────────────────────────────────────
            progress?.Report("清理旧数据...");
            try
            {
                Directory.Delete(OldBase, recursive: true);
                Log.Information("[Migration] Old data deleted at {OldBase}", OldBase);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[Migration] Failed to delete old data at {OldBase}", OldBase);
            }

            progress?.Report("迁移完成");
            Log.Information("[Migration] Migration completed successfully");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Migration] Migration failed");
            progress?.Report($"迁移失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 迁移配置文件，将 Repositories 中 Path 的 workcopies 前缀替换为新路径。
    /// </summary>
    private static async Task MigrateConfigAsync(string oldConfigPath, string oldWorkcopies, string newWorkcopies)
    {
        var json = await File.ReadAllTextAsync(oldConfigPath);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("Repositories", out var repos) || repos.ValueKind != JsonValueKind.Array)
        {
            // 无 Repositories 字段，直接复制
            Directory.CreateDirectory(AppPaths.Config);
            File.Copy(oldConfigPath, Path.Combine(AppPaths.Config, "config.json"), overwrite: true);
            return;
        }

        var updatedRepos = new List<Repository>();
        foreach (var elem in repos.EnumerateArray())
        {
            var repo = JsonSerializer.Deserialize<Repository>(elem.GetRawText());
            if (repo != null && !string.IsNullOrEmpty(repo.Path))
            {
                if (repo.Path.StartsWith(oldWorkcopies, StringComparison.OrdinalIgnoreCase))
                {
                    repo.Path = newWorkcopies + repo.Path[oldWorkcopies.Length..];
                }
                updatedRepos.Add(repo);
            }
        }

        var config = new AppConfig();
        foreach (var prop in root.EnumerateObject())
        {
            switch (prop.Name)
            {
                case nameof(AppConfig.Repositories):
                    foreach (var r in updatedRepos)
                        config.Repositories.Add(r);
                    break;
                case nameof(AppConfig.ActiveRepositoryName):
                    config.ActiveRepositoryName = prop.Value.GetString(); break;
                case nameof(AppConfig.AutoSyncEnabled):
                    config.AutoSyncEnabled = prop.Value.GetBoolean(); break;
                case nameof(AppConfig.SyncIntervalMinutes):
                    config.SyncIntervalMinutes = prop.Value.GetInt32(); break;
                case nameof(AppConfig.ConflictStrategy):
                    config.ConflictStrategy = prop.Value.GetString() ?? ""; break;
                case nameof(AppConfig.ProxyUrl):
                    config.ProxyUrl = prop.Value.GetString() ?? ""; break;
                case nameof(AppConfig.SyncRecordRetentionDays):
                    config.SyncRecordRetentionDays = prop.Value.GetInt32(); break;
                case nameof(AppConfig.AutoStart):
                    config.AutoStart = prop.Value.GetBoolean(); break;
                case nameof(AppConfig.MinimizeToTray):
                    config.MinimizeToTray = prop.Value.GetBoolean(); break;
                case nameof(AppConfig.Language):
                    config.Language = prop.Value.GetString() ?? "auto"; break;
                case nameof(AppConfig.Theme):
                    config.Theme = prop.Value.GetString() ?? "system"; break;
                case nameof(AppConfig.AutoStartMinimize):
                    config.AutoStartMinimize = prop.Value.GetBoolean(); break;
                case nameof(AppConfig.FileTransferTimeoutSeconds):
                    config.FileTransferTimeoutSeconds = prop.Value.GetInt32(); break;
            }
        }

        Directory.CreateDirectory(AppPaths.Config);
        var newJson = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(Path.Combine(AppPaths.Config, "config.json"), newJson);
    }

    /// <summary>
    /// 递归复制目录（源目录自身 → 目标目录，不在外层再包一层子目录）。
    /// </summary>
    private static Task CopyDirectoryAsync(string source, string dest)
    {
        return Task.Run(() =>
        {
            Directory.CreateDirectory(dest);

            // 复制所有文件
            foreach (var file in new DirectoryInfo(source).GetFiles())
                file.CopyTo(Path.Combine(dest, file.Name), overwrite: true);

            // 递归复制子目录
            foreach (var subDir in new DirectoryInfo(source).GetDirectories())
            {
                var subDest = Path.Combine(dest, subDir.Name);
                CopyDirectoryAsync(subDir.FullName, subDest).Wait();
            }
        });
    }
}
