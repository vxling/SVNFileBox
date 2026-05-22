#nullable enable
using System;
using System.IO;

namespace SVNFileBox;

/// <summary>
/// 全局数据路径，所有配置、日志、工作副本都存在用户主目录下的 .svnfilebox 目录。
/// </summary>
public static class AppPaths
{
    private static readonly string BaseDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".svnfilebox");

    /// <summary>~/.svnfilebox/</summary>
    public static string Base => BaseDir;

    /// <summary>~/.svnfilebox/config/</summary>
    public static string Config => Path.Combine(BaseDir, "config");

    /// <summary>~/.svnfilebox/logs/</summary>
    public static string Logs => Path.Combine(BaseDir, "logs");

    /// <summary>~/.svnfilebox/workcopies/</summary>
    public static string WorkCopies => Path.Combine(BaseDir, "workcopies");

    /// <summary>
    /// 确保所有目录存在（程序启动时调用一次）。
    /// </summary>
    public static void EnsureDirectoriesExist()
    {
        Directory.CreateDirectory(BaseDir);
        Directory.CreateDirectory(Config);
        Directory.CreateDirectory(Logs);
        Directory.CreateDirectory(WorkCopies);
    }
}
