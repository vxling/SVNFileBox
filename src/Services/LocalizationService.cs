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
    public string CurrentLanguage => _currentLanguage;
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
            ["SyncIntervalTip"] = "服务器轮询检查的间隔（1-10分钟）",
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
            ["Yes"] = "是",
            ["No"] = "否",
            ["Exit"] = "退出",
            ["OpenSVNFileBox"] = "打开 SVNFileBox",
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
            ["Back"] = "返回",
            ["OpenInExplorer"] = "在资源管理器中打开",
            ["CopyPath"] = "复制路径",
            ["Copy"] = "复制",
            ["CopiedToClipboard"] = "已复制 {0} 到剪贴板",
            ["Paste"] = "粘贴",
            ["NewFolder"] = "新建文件夹",
            ["Rename"] = "重命名",
            ["Delete"] = "删除",
            ["ManualSync"] = "手工同步",
            ["Copied"] = "已复制: {0}",
            ["DeleteSuccess"] = "已删除: {0}",
            ["DeleteFailed"] = "删除失败: {0}",
            ["Folder"] = "文件夹",
            ["File"] = "文件",
            ["NewFolderTitle"] = "新建文件夹",
            ["NewFolderPrompt"] = "文件夹名称:",
            ["NewFolderSuccess"] = "已创建文件夹: {0}",
            ["NewFolderFailed"] = "创建文件夹失败: {0}",
            ["FolderAlreadyExists"] = "文件夹 \"{0}\" 已存在",
            ["New"] = "新建",
            ["NewTextFile"] = "文本文档",
            ["NewWordDoc"] = "Microsoft Word 文档",
            ["NewExcelSheet"] = "Microsoft Excel 工作表",
            ["NewPowerPoint"] = "Microsoft PowerPoint",
            ["NewPngImage"] = "PNG 图片",
            ["NewBmpImage"] = "BMP 图片",
            ["NewFileSuccess"] = "已创建: {0}",
            ["NewFileFailed"] = "创建文件失败: {0}",
            ["RenameTitle"] = "重命名",
            ["RenamePrompt"] = "新名称:",
            ["RenameSuccess"] = "已重命名: {0}",
            ["RenameFailed"] = "重命名失败: {0}",
            ["NameAlreadyTaken"] = "名称 \"{0}\" 已被占用",
            ["DeleteConfirmTitle"] = "确认删除",
            ["DeleteConfirmMessage"] = "确定要删除 {0} \"{1}\" 吗？",
            ["PasteFailed"] = "粘贴失败: {0}",
            ["OpenFailed"] = "打开失败: {0}",
            ["ManualSyncInProgress"] = "正在手工同步...",
            ["SyncComplete"] = "同步完成",
            ["SyncFailed"] = "同步失败: {0}",
            ["ResolvingConflicts"] = "正在处理 {0} 个冲突文件...",
            ["ConflictsResolved"] = "冲突处理完成：{0} 个",
            ["ConflictResolutionFailed"] = "冲突处理失败",
            ["AppUnhandledError"] = "错误",
            ["AppUnhandledErrorMessage"] = "发生未处理的错误:\n\n{0}\n\n程序将继续运行。",
            ["SplashStartupFailed"] = "启动失败",
            ["SplashStartupFailedMessage"] = "启动失败:\n\n{0}\n\n请修复后重新启动程序。",
            ["SplashStartupFailedStatus"] = "请查看日志或重新启动程序",
            ["SplashStartupFailedStatusText"] = "❌ 启动失败: {0}",
            ["CopyInProgress"] = "当前正在执行复制操作，请等待完成后再试。",
            ["Prompt"] = "提示",
            ["AnalysisCancelled"] = "已取消分析",
            ["NoFilesToCopy"] = "没有文件可复制",
            ["SameLocation"] = "源和目标位置相同，无法复制。",
            ["CopyCancelled"] = "已取消复制",
            ["CopyFailed"] = "复制失败: {0}",
            ["CopiedNItems"] = "已复制 {0} 个项目",
            ["CopiedNItemsSkippedM"] = "已复制 {0} 个，跳过 {1} 个",
            ["ConfirmRemove"] = "确认移除",
            ["RemoveRepoConfirm"] = "确定要移除仓库 \"{0}\"？\n本地文件不会删除。",
            ["RemoveNetworkRepoConfirm"] = "确定要移除网络仓库 \"{0}\"？\n移除后，本地文件也会被删除！",
            ["MinimizedToTray"] = "已最小化到托盘，双击恢复",

            // Sync Records columns
            ["ColTime"] = "时间",
            ["ColRepo"] = "仓库",
            ["ColFile"] = "文件",
            ["ColOperation"] = "操作",
            ["ColResult"] = "结果",
            ["ColDetail"] = "详情",

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
            ["RepoNameInvalid"] = "仓库名称包含非法字符",
            ["RepoUrlRequired"] = "请输入仓库 URL",
            ["DuplicateRepoUrl"] = "该网络仓库地址已存在，不能重复添加",
            ["CannotCreateDir"] = "无法创建目录: {0}",
            ["CheckoutInProgress"] = "正在 checkout，请稍候...",

            // Splash
            ["SplashTagline"] = "SVN 文件管理器",
            ["SplashInitializing"] = "正在初始化...",
            ["SplashStep"] = "步骤 {0} / {1}",
            ["SplashComplete"] = "启动完成",
            ["SplashTitle"] = "SVNFileBox",

            // Sync Records
            ["SyncRecords"] = "同步记录",
            ["NoRecords"] = "暂无同步记录",

            // AboutWindow
            ["AboutTitle"] = "关于",
            ["AppName"] = "SVNFileBox",
            ["Version"] = "版本",
            ["VersionNumber"] = "2.3.0",
            ["Tagline"] = "基于SVN实现的类似dropbox客户端",
            ["Desc1"] = "本地文件夹自动同步到 SVN 仓库，多设备共享。",
            ["Desc2"] = "冲突时以最后修改时间为准自动解决。",
            ["Close"] = "关闭",

            // AddLocalRepoWindow
            ["AddLocalRepoTitle"] = "添加本地仓库",
            ["LocalPath"] = "本地路径:",
            ["Browse"] = "浏览...",
            ["Confirm"] = "确认",
            ["SelectFolderTitle"] = "选择 SVN 工作副本目录",
            ["PleaseSelectDir"] = "请选择目录",
            ["LocalPathAlreadyAdded"] = "本地路径已存在，不能重复添加",
            ["NotValidWorkingCopy"] = "所选目录不是有效的 SVN 工作副本（没有 .svn 目录）",
            ["CheckingRepoUrl"] = "正在检查仓库 URL...",

            // Input Dialog
            ["InputTitle"] = "输入",

            // Conflict Window
            ["ConflictTitle"] = "发现冲突文件",
            ["ConflictDesc"] = "服务器更新与本地修改产生冲突，请为每个文件选择处理方式。建议参考文件修改时间判断，但最终由你决定。",
            ["SuggestAcceptServer"] = "建议: 接受服务器",
            ["SuggestKeepLocal"] = "建议: 保留本地",
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
            ["SyncIntervalTip"] = "Server poll interval (1-10 minutes)",
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
            ["Yes"] = "Yes",
            ["No"] = "No",
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
            ["Back"] = "Back",
            ["OpenInExplorer"] = "Open in Explorer",
            ["CopyPath"] = "Copy Path",
            ["Copy"] = "Copy",
            ["CopiedToClipboard"] = "{0} copied to clipboard",
            ["Paste"] = "Paste",
            ["NewFolder"] = "New Folder",
            ["Rename"] = "Rename",
            ["Delete"] = "Delete",
            ["ManualSync"] = "Manual Sync",
            ["Copied"] = "Copied: {0}",
            ["DeleteSuccess"] = "Deleted: {0}",
            ["DeleteFailed"] = "Delete failed: {0}",
            ["Folder"] = "Folder",
            ["File"] = "File",
            ["NewFolderTitle"] = "New Folder",
            ["NewFolderPrompt"] = "Folder name:",
            ["DeleteConfirmTitle"] = "Confirm Delete",
            ["DeleteConfirmMessage"] = "Are you sure you want to delete {0} \"{1}\"?",
            ["PasteFailed"] = "Paste failed: {0}",
            ["NewFolderSuccess"] = "Folder created: {0}",
            ["NewFolderFailed"] = "Failed to create folder: {0}",
            ["New"] = "New",
            ["NewTextFile"] = "Text Document",
            ["NewWordDoc"] = "Microsoft Word Document",
            ["NewExcelSheet"] = "Microsoft Excel Worksheet",
            ["NewPowerPoint"] = "Microsoft PowerPoint",
            ["NewPngImage"] = "PNG Image",
            ["NewBmpImage"] = "BMP Image",
            ["NewFileSuccess"] = "Created: {0}",
            ["NewFileFailed"] = "Failed to create file: {0}",
            ["FolderAlreadyExists"] = "Folder \"{0}\" already exists",
            ["NameAlreadyTaken"] = "Name \"{0}\" is already taken",
            ["OpenFailed"] = "Failed to open: {0}",
            ["ManualSyncInProgress"] = "Manual sync in progress...",
            ["SyncComplete"] = "Sync complete",
            ["SyncFailed"] = "Sync failed: {0}",
            ["ResolvingConflicts"] = "Resolving {0} conflict(s)...",
            ["ConflictsResolved"] = "Resolved {0} conflict(s)",
            ["ConflictResolutionFailed"] = "Conflict resolution failed",
            ["CopyInProgress"] = "A copy operation is already in progress. Please wait.",
            ["AppUnhandledError"] = "Error",
            ["AppUnhandledErrorMessage"] = "An unhandled error occurred:\n\n{0}\n\nThe application will continue running.",
            ["SplashStartupFailed"] = "Startup Failed",
            ["SplashStartupFailedMessage"] = "Startup failed:\n\n{0}\n\nPlease fix the issue and restart the program.",
            ["SplashStartupFailedStatus"] = "Please check the log or restart the program.",
            ["SplashStartupFailedStatusText"] = "❌ Startup failed: {0}",
            ["Prompt"] = "Prompt",
            ["AnalysisCancelled"] = "Analysis cancelled",
            ["NoFilesToCopy"] = "No files to copy",
            ["SameLocation"] = "Source and destination are the same. Cannot copy.",
            ["CopyCancelled"] = "Copy cancelled",
            ["CopyFailed"] = "Copy failed: {0}",
            ["CopiedNItems"] = "Copied {0} item(s)",
            ["CopiedNItemsSkippedM"] = "Copied {0} item(s), skipped {1}",
            ["ConfirmRemove"] = "Confirm Remove",
            ["RemoveRepoConfirm"] = "Remove repository \"{0}\"?\nLocal files will not be deleted.",
            ["RemoveNetworkRepoConfirm"] = "Remove network repository \"{0}\"?\nAfter removal, local files will also be deleted!",
            ["MinimizedToTray"] = "Minimized to tray. Double-click to restore.",
            ["RenamePrompt"] = "New name:",
            ["RenameSuccess"] = "Renamed: {0}",
            ["RenameFailed"] = "Rename failed: {0}",
            ["OpenSVNFileBox"] = "Open SVNFileBox",
            ["Exit"] = "Exit",
            // Sync Records columns
            ["ColTime"] = "Time",
            ["ColRepo"] = "Repo",
            ["ColFile"] = "File",
            ["ColOperation"] = "Operation",
            ["ColResult"] = "Result",
            ["ColDetail"] = "Detail",

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
            ["RepoNameInvalid"] = "Repository name contains invalid characters",
            ["RepoUrlRequired"] = "Repository URL is required",
            ["DuplicateRepoUrl"] = "This network repository URL already exists and cannot be added again.",
            ["CannotCreateDir"] = "Cannot create directory: {0}",
            ["CheckoutInProgress"] = "Checking out, please wait...",

            // Splash
            ["SplashTagline"] = "SVN File Manager",
            ["SplashInitializing"] = "Initializing...",
            ["SplashStep"] = "Step {0} / {1}",
            ["SplashComplete"] = "Startup complete",
            ["SplashTitle"] = "SVNFileBox",

            // Sync Records
            ["SyncRecords"] = "Sync Records",
            ["NoRecords"] = "No sync records",

            // AboutWindow
            ["AboutTitle"] = "About",
            ["AppName"] = "SVNFileBox",
            ["Version"] = "Version",
            ["VersionNumber"] = "2.3.0",
            ["Tagline"] = "A Dropbox-like client implemented based on SVN",
            ["Desc1"] = "Automatically sync local folders to SVN repositories for multi-device sharing.",
            ["Desc2"] = "Conflicts are resolved automatically based on last-modified time.",
            ["Close"] = "Close",

            // AddLocalRepoWindow
            ["AddLocalRepoTitle"] = "Add Local Repository",
            ["LocalPath"] = "Local Path:",
            ["Browse"] = "Browse...",
            ["Confirm"] = "Confirm",
            ["SelectFolderTitle"] = "Select SVN Working Copy Directory",
            ["PleaseSelectDir"] = "Please select a directory",
            ["LocalPathAlreadyAdded"] = "This local path already exists and cannot be added again",
            ["NotValidWorkingCopy"] = "The selected directory is not a valid SVN working copy (no .svn directory)",
            ["CheckingRepoUrl"] = "Checking repository URL...",

            // Input Dialog
            ["InputTitle"] = "Input",

            // Conflict Window
            ["ConflictTitle"] = "File Conflicts Detected",
            ["ConflictDesc"] = "Server updates conflict with local changes. Please choose how to handle each file. Check modified times for reference.",
            ["SuggestAcceptServer"] = "Suggestion: Accept Server",
            ["SuggestKeepLocal"] = "Suggestion: Keep Local",
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
