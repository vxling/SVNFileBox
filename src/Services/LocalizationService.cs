#nullable enable
using System;
using System.Collections.Generic;
using System.Windows;

namespace SVNFileBox.Services;

public class LocalizationService
{
    private static LocalizationService? _instance;
    public static LocalizationService Instance => _instance ??= new LocalizationService();

    private string _currentLanguage = "auto";
    private ResourceDictionary? _resourceDict;

    public event EventHandler? LanguageChanged;

    private readonly Dictionary<string, Dictionary<string, string>> _strings = new()
    {
        ["zh"] = new Dictionary<string, string>
        {
            // SettingsWindow
            ["SettingsTitle"] = "设置",
            ["AutoSync"] = "自动同步",
            ["AutoSyncTip"] = "启用后自动同步文件变更（不可关闭）",
            ["SyncInterval"] = "同步周期（分钟）",
            ["SyncIntervalTip"] = "服务器轮询检查的间隔（1-30分钟）",
            ["Theme"] = "主题",
            ["ThemeTip"] = "界面外观颜色模式",
            ["ThemeSystem"] = "跟随系统",
            ["ThemeLight"] = "浅色",
            ["ThemeDark"] = "深色",
            ["Language"] = "语言",
            ["LanguageTip"] = "界面显示语言",
            ["LangSystem"] = "跟随系统",
            ["LangZh"] = "中文",
            ["LangEn"] = "English",
            ["ProxyUrl"] = "代理地址",
            ["ProxyUrlTip"] = "HTTP 代理（留空则不使用代理）",
            ["ProxyPlaceholder"] = "http://proxy:8080",
            ["AutoStart"] = "开机启动",
            ["AutoStartTip"] = "Windows 启动时自动运行",
            ["MinimizeToTray"] = "最小化到托盘",
            ["MinimizeToTrayTip"] = "关闭窗口时最小化到系统托盘",
            ["OK"] = "确定",
            ["Cancel"] = "取消",
            ["Minutes"] = "分钟",

            // MainWindow
            ["Repositories"] = "仓库列表",
            ["AddLocalRepository"] = "添加本地仓库",
            ["CheckoutFromNetwork"] = "从网络添加仓库",
            ["ViewSyncRecords"] = "查看同步记录",
            ["Settings"] = "设置",
            ["About"] = "关于",
            ["Path"] = "路径",
            ["Refresh"] = "刷新",
            ["ColumnType"] = "类型",
            ["ColumnName"] = "名称",
            ["ColumnStatus"] = "状态",
            ["ColumnSize"] = "大小",
            ["ColumnModified"] = "修改时间",
            ["ParentDirectory"] = "返回上级目录",
            ["OpenInExplorer"] = "在资源管理器中打开",
            ["CopyPath"] = "复制路径",
            ["Paste"] = "粘贴",
            ["NewFolder"] = "新建文件夹",
            ["Rename"] = "重命名",
            ["Delete"] = "删除",
            ["ManualSync"] = "手工同步",
            ["Copied"] = "已复制: {0}",
            ["DeleteSuccess"] = "已删除: {0}",
            ["DeleteFailed"] = "删除失败: {0}",
            ["NewFolderTitle"] = "新建文件夹",
            ["NewFolderPrompt"] = "文件夹名称:",
            ["DeleteConfirmTitle"] = "确认删除",
            ["DeleteConfirmMessage"] = "确定要删除 {0} \"{1}\" 吗？",
            ["PasteFailed"] = "粘贴失败: {0}",
            ["RenameTitle"] = "重命名",
            ["RenamePrompt"] = "新名称:",
            ["RenameSuccess"] = "已重命名: {0}",
            ["RenameFailed"] = "重命名失败: {0}",

            // CheckoutWindow
            ["CheckoutTitle"] = "从网络添加仓库",
            ["RepoName"] = "仓库名称",
            ["RepoUrl"] = "仓库 URL",
            ["Username"] = "用户名",
            ["Password"] = "密码",
            ["CheckoutSuccess"] = "Checkout 成功",
            ["CheckoutFailed"] = "Checkout 失败",
            ["LocalPathExists"] = "本地路径已存在",
            ["RepoNameRequired"] = "请输入仓库名称",
            ["RepoUrlRequired"] = "请输入仓库 URL",

            // Sync Records
            ["SyncRecords"] = "同步记录",
            ["NoRecords"] = "暂无同步记录",

            // AboutWindow
            ["AboutTitle"] = "关于",
            ["AppName"] = "SVNFileBox",
            ["Version"] = "版本",
            ["VersionNumber"] = "2.1.0",
            ["Tagline"] = "SVN 版 Dropbox",
            ["Desc1"] = "本地文件夹自动同步到 SVN 仓库，多设备共享。",
            ["Desc2"] = "冲突时以最后修改时间为准自动解决。",
            ["Close"] = "关闭",

            // AddLocalRepoWindow
            ["AddLocalRepoTitle"] = "添加本地仓库",
            ["LocalPath"] = "本地路径:",
            ["Browse"] = "浏览...",
            ["Confirm"] = "确认",

            // Input Dialog
            ["InputTitle"] = "输入",

            // Conflict Window
            ["ConflictTitle"] = "发现冲突文件",
            ["Local"] = "本地:",
            ["Server"] = "服务器:",
            ["KeepLocal"] = "保留本地版本",
            ["AcceptServer"] = "接受服务器版本",
            ["KeepBoth"] = "保留两者（备份后接受服务器）",
            ["KeepAllLocal"] = "全选保留本地",
            ["AcceptAllServer"] = "全选接受服务器",
            ["ConflictConfirm"] = "确定",

            // FileCopyProgressWindow
            ["CopyProgressTitle"] = "文件同步中",
            ["Analyzing"] = "分析中: {0}",
            ["Copying"] = "同步中: {0}",
            ["Elapsed"] = "用时: {0}",
            ["CancelSync"] = "取消同步",

            // Status
            ["Ready"] = "就绪",
            ["Syncing"] = "同步中...",
            ["SyncComplete"] = "同步完成",
            ["SyncFailed"] = "同步失败: {0}",
            ["AnalyzingFiles"] = "正在分析文件变更...",
            ["RepoNotFound"] = "仓库不存在，请重新配置",
            ["Checkout"] = "Checkout",
        },

        ["en"] = new Dictionary<string, string>
        {
            // SettingsWindow
            ["SettingsTitle"] = "Settings",
            ["AutoSync"] = "Auto Sync",
            ["AutoSyncTip"] = "Automatically sync file changes (cannot be disabled)",
            ["SyncInterval"] = "Sync Interval (minutes)",
            ["SyncIntervalTip"] = "Server poll interval (1-30 minutes)",
            ["Theme"] = "Theme",
            ["ThemeTip"] = "Application color mode",
            ["ThemeSystem"] = "Follow System",
            ["ThemeLight"] = "Light",
            ["ThemeDark"] = "Dark",
            ["Language"] = "Language",
            ["LanguageTip"] = "Interface display language",
            ["LangSystem"] = "Follow System",
            ["LangZh"] = "中文",
            ["LangEn"] = "English",
            ["ProxyUrl"] = "Proxy URL",
            ["ProxyUrlTip"] = "HTTP proxy (leave empty to disable)",
            ["ProxyPlaceholder"] = "http://proxy:8080",
            ["AutoStart"] = "Auto Start",
            ["AutoStartTip"] = "Run automatically when Windows starts",
            ["MinimizeToTray"] = "Minimize to Tray",
            ["MinimizeToTrayTip"] = "Minimize to system tray when window is closed",
            ["OK"] = "OK",
            ["Cancel"] = "Cancel",
            ["Minutes"] = "minutes",

            // MainWindow
            ["Repositories"] = "Repositories",
            ["AddLocalRepository"] = "Add Local Repository",
            ["CheckoutFromNetwork"] = "Add Repository from Network",
            ["ViewSyncRecords"] = "View Sync Records",
            ["Settings"] = "Settings",
            ["About"] = "About",
            ["Path"] = "Path",
            ["Refresh"] = "Refresh",
            ["ColumnType"] = "Type",
            ["ColumnName"] = "Name",
            ["ColumnStatus"] = "Status",
            ["ColumnSize"] = "Size",
            ["ColumnModified"] = "Modified",
            ["ParentDirectory"] = "Parent Directory",
            ["OpenInExplorer"] = "Open in Explorer",
            ["CopyPath"] = "Copy Path",
            ["Paste"] = "Paste",
            ["NewFolder"] = "New Folder",
            ["Rename"] = "Rename",
            ["Delete"] = "Delete",
            ["ManualSync"] = "Manual Sync",
            ["Copied"] = "Copied: {0}",
            ["DeleteSuccess"] = "Deleted: {0}",
            ["DeleteFailed"] = "Delete failed: {0}",
            ["NewFolderTitle"] = "New Folder",
            ["NewFolderPrompt"] = "Folder name:",
            ["DeleteConfirmTitle"] = "Confirm Delete",
            ["DeleteConfirmMessage"] = "Are you sure you want to delete {0} \"{1}\"?",
            ["PasteFailed"] = "Paste failed: {0}",
            ["RenameTitle"] = "Rename",
            ["RenamePrompt"] = "New name:",
            ["RenameSuccess"] = "Renamed: {0}",
            ["RenameFailed"] = "Rename failed: {0}",

            // CheckoutWindow
            ["CheckoutTitle"] = "Add Repository from Network",
            ["RepoName"] = "Repository Name",
            ["RepoUrl"] = "Repository URL",
            ["Username"] = "Username",
            ["Password"] = "Password",
            ["CheckoutSuccess"] = "Checkout successful",
            ["CheckoutFailed"] = "Checkout failed",
            ["LocalPathExists"] = "Local path already exists",
            ["RepoNameRequired"] = "Repository name is required",
            ["RepoUrlRequired"] = "Repository URL is required",

            // Sync Records
            ["SyncRecords"] = "Sync Records",
            ["NoRecords"] = "No sync records",

            // AboutWindow
            ["AboutTitle"] = "About",
            ["AppName"] = "SVNFileBox",
            ["Version"] = "Version",
            ["VersionNumber"] = "2.1.0",
            ["Tagline"] = "SVN-style Dropbox",
            ["Desc1"] = "Automatically sync local folders to SVN repositories for multi-device sharing.",
            ["Desc2"] = "Conflicts are resolved automatically based on last-modified time.",
            ["Close"] = "Close",

            // AddLocalRepoWindow
            ["AddLocalRepoTitle"] = "Add Local Repository",
            ["LocalPath"] = "Local Path:",
            ["Browse"] = "Browse...",
            ["Confirm"] = "Confirm",

            // Input Dialog
            ["InputTitle"] = "Input",

            // Conflict Window
            ["ConflictTitle"] = "File Conflicts Detected",
            ["Local"] = "Local:",
            ["Server"] = "Server:",
            ["KeepLocal"] = "Keep Local Version",
            ["AcceptServer"] = "Accept Server Version",
            ["KeepBoth"] = "Keep Both (backup then accept server)",
            ["KeepAllLocal"] = "Keep All Local",
            ["AcceptAllServer"] = "Accept All Server",
            ["ConflictConfirm"] = "Confirm",

            // FileCopyProgressWindow
            ["CopyProgressTitle"] = "File Sync in Progress",
            ["Analyzing"] = "Analyzing: {0}",
            ["Copying"] = "Copying: {0}",
            ["Elapsed"] = "Elapsed: {0}",
            ["CancelSync"] = "Cancel Sync",

            // Status
            ["Ready"] = "Ready",
            ["Syncing"] = "Syncing...",
            ["SyncComplete"] = "Sync complete",
            ["SyncFailed"] = "Sync failed: {0}",
            ["AnalyzingFiles"] = "Analyzing file changes...",
            ["RepoNotFound"] = "Repository not found, please reconfigure",
            ["Checkout"] = "Checkout",
        }
    };

    public LocalizationService()
    {
        RebuildResourceDictionary();
    }

    private void RebuildResourceDictionary()
    {
        var lang = _currentLanguage == "auto"
            ? System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName
            : _currentLanguage;
        if (!_strings.ContainsKey(lang)) lang = "en";

        _resourceDict = new ResourceDictionary();
        foreach (var kvp in _strings[lang])
        {
            _resourceDict[kvp.Key] = kvp.Value;
        }
    }

    public void SetLanguage(string lang)
    {
        _currentLanguage = lang;
        RebuildResourceDictionary();
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    public ResourceDictionary ResourceDictionary => _resourceDict ?? new ResourceDictionary();

    public string GetString(string key)
    {
        var lang = _currentLanguage == "auto"
            ? System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName
            : _currentLanguage;
        if (!_strings.ContainsKey(lang)) lang = "en";
        return _strings[lang].TryGetValue(key, out var value) ? value : key;
    }

    public string GetString(string key, params object[] args)
    {
        var template = GetString(key);
        try { return string.Format(template, args); }
        catch { return template; }
    }
}
