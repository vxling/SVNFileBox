#nullable enable
using System;
using System.Collections.Generic;

namespace SVNFileBox.Services;

public class LocalizationService
{
    private static LocalizationService? _instance;
    public static LocalizationService Instance => _instance ??= new LocalizationService();

    private string _currentLanguage = "auto";
    private readonly Dictionary<string, Dictionary<string, string>> _strings = new();

    private LocalizationService()
    {
        _strings["zh"] = new Dictionary<string, string>
        {
            ["AppTitle"] = "SVNFileBox",
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
            ["Copied"] = "已复制: {0}",
            ["DeleteSuccess"] = "已删除: {0}",
            ["DeleteFailed"] = "删除失败: {0}",
            ["NewFolderTitle"] = "新建文件夹",
            ["NewFolderPrompt"] = "文件夹名称:",
            ["DeleteConfirmTitle"] = "确认删除",
            ["DeleteConfirmMessage"] = "确定要删除 {0} \"{1}\" 吗？",
            ["CheckoutTitle"] = "从网络添加仓库",
            ["RepoName"] = "仓库名称",
            ["RepoUrl"] = "仓库 URL",
            ["Username"] = "用户名",
            ["Password"] = "密码",
            ["Confirm"] = "确认",
            ["Cancel"] = "取消",
            ["CheckoutSuccess"] = "Checkout 成功",
            ["CheckoutFailed"] = "Checkout 失败",
            ["LocalPathExists"] = "本地路径已存在",
            ["RepoNameRequired"] = "请输入仓库名称",
            ["RepoUrlRequired"] = "请输入仓库 URL",
            ["SyncRecords"] = "同步记录",
            ["NoRecords"] = "暂无同步记录",
            ["SettingsTitle"] = "设置",
            ["AutoSync"] = "自动同步",
            ["SyncInterval"] = "同步周期（分钟）",
            ["Language"] = "语言",
            ["ProxyUrl"] = "代理地址",
            ["AutoStart"] = "开机启动",
            ["MinimizeToTray"] = "最小化到托盘",
        };

        _strings["en"] = new Dictionary<string, string>
        {
            ["AppTitle"] = "SVNFileBox",
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
            ["Copied"] = "Copied: {0}",
            ["DeleteSuccess"] = "Deleted: {0}",
            ["DeleteFailed"] = "Delete failed: {0}",
            ["NewFolderTitle"] = "New Folder",
            ["NewFolderPrompt"] = "Folder name:",
            ["DeleteConfirmTitle"] = "Confirm Delete",
            ["DeleteConfirmMessage"] = "Are you sure you want to delete {0} \"{1}\"?",
            ["CheckoutTitle"] = "Add Repository from Network",
            ["RepoName"] = "Repository Name",
            ["RepoUrl"] = "Repository URL",
            ["Username"] = "Username",
            ["Password"] = "Password",
            ["Confirm"] = "Confirm",
            ["Cancel"] = "Cancel",
            ["CheckoutSuccess"] = "Checkout successful",
            ["CheckoutFailed"] = "Checkout failed",
            ["LocalPathExists"] = "Local path already exists",
            ["RepoNameRequired"] = "Repository name is required",
            ["RepoUrlRequired"] = "Repository URL is required",
            ["SyncRecords"] = "Sync Records",
            ["NoRecords"] = "No sync records",
            ["SettingsTitle"] = "Settings",
            ["AutoSync"] = "Auto Sync",
            ["SyncInterval"] = "Sync Interval (minutes)",
            ["Language"] = "Language",
            ["ProxyUrl"] = "Proxy URL",
            ["AutoStart"] = "Auto Start",
            ["MinimizeToTray"] = "Minimize to Tray",
        };
    }

    public void SetLanguage(string lang) => _currentLanguage = lang;

    public string GetString(string key)
    {
        var lang = _currentLanguage == "auto"
            ? System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName
            : _currentLanguage;

        if (!_strings.ContainsKey(lang))
            lang = "en";

        return _strings[lang].TryGetValue(key, out var value) ? value : key;
    }

    public string GetString(string key, params object[] args)
    {
        var template = GetString(key);
        try { return string.Format(template, args); }
        catch { return template; }
    }
}