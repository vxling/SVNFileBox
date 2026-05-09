#nullable enable
using System.Collections.Generic;

namespace SVNFileBox.Models;

public class AppConfig
{
    public bool AutoSyncEnabled { get; set; } = true;
    public int SyncIntervalMinutes { get; set; } = 1;
    public string ConflictStrategy { get; set; } = "LastWriteWins";
    public string ProxyUrl { get; set; } = "";
    public int SyncRecordRetentionDays { get; set; } = 30;
    public bool AutoStart { get; set; } = true;
    public bool MinimizeToTray { get; set; } = true;
    public string Language { get; set; } = "auto";
    public string Theme { get; set; } = "system";
    public string? ActiveRepositoryName { get; set; }
    public List<Repository> Repositories { get; set; } = new();
}