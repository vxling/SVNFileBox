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
    public bool AutoStartMinimize { get; set; } = true;
    public string? ActiveRepositoryName { get; set; }
    public List<Repository> Repositories { get; set; } = new();
    /// <summary>
    /// 文件传输活动超时（秒）：文件传输过程中，如果超过此时间没有任何文件上下行，则判定为卡死。默认120秒，最大600秒。
    /// </summary>
    public int FileTransferTimeoutSeconds { get; set; } = 120;
    /// <summary>
    /// 是否显示多选工具栏（checkbox 列 + 批量操作按钮）。默认 false（隐藏）。
    /// </summary>
    public bool MultiSelectEnabled { get; set; } = false;
}