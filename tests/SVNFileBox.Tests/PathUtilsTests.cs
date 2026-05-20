using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace SVNFileBox.Tests;

public class PathUtilsTests
{
    static List<string> FilterToRepoRoot(List<string> paths, string repoPath)
    {
        var normalizedRepoPath = repoPath.Replace('\\', '/').TrimEnd('/');
        return paths
            .Where(p => p.StartsWith(normalizedRepoPath + '/') || p == normalizedRepoPath)
            .ToList();
    }

    static List<string> ComputeParentDirs(List<string> inRepoPaths, string normalizedRepoPath)
    {
        return inRepoPaths
            .Select(p =>
            {
                var dir = System.IO.Path.GetDirectoryName(p)?.Replace('\\', '/');
                return string.IsNullOrEmpty(dir) || dir == normalizedRepoPath ? normalizedRepoPath : dir;
            })
            .Distinct()
            .OrderByDescending(d => d.Split('\\', '/').Length)
            .ToList();
    }

    [Fact]
    public void FilterToRepoRoot_excludes_external_paths()
    {
        var repoPath = "D:/repo1";
        var paths = new List<string>
        {
            "D:/repo1/file.txt",
            "D:/repo1/sub/file.txt",
            "D:/repo2/external/file.txt",
            "D:/repo1",
        };

        var result = FilterToRepoRoot(paths, repoPath);

        Assert.Equal(3, result.Count);
        Assert.Contains("D:/repo1/file.txt", result);
        Assert.Contains("D:/repo1/sub/file.txt", result);
        Assert.Contains("D:/repo1", result);
        Assert.DoesNotContain("D:/repo2/external/file.txt", result);
    }

    [Fact]
    public void FilterToRepoRoot_handles_windows_backslash()
    {
        var repoPath = @"D:\repo1";
        var paths = new List<string>
        {
            @"D:\repo1\file.txt",
            @"D:\repo1\sub\file.txt",
            @"D:\other\file.txt",
        };

        var result = FilterToRepoRoot(paths, repoPath);

        Assert.Equal(2, result.Count);
        Assert.Contains(@"D:\repo1\file.txt", result);
    }

    [Fact]
    public void ComputeParentDirs_root_level_file_maps_to_repo_root()
    {
        var repoPath = "D:/repo1";
        var paths = new List<string>
        {
            "D:/repo1/file.txt",
            "D:/repo1/README.md",
        };

        var normalizedRepoPath = repoPath.Replace('\\', '/').TrimEnd('/');
        var result = ComputeParentDirs(paths, normalizedRepoPath);

        Assert.Single(result);
        Assert.Equal("D:/repo1", result[0]);
    }

    [Fact]
    public void ComputeParentDirs_deepest_dirs_first()
    {
        var repoPath = "D:/repo1";
        var paths = new List<string>
        {
            "D:/repo1/a.txt",
            "D:/repo1/sub/b.txt",
            "D:/repo1/sub/deep/c.txt",
        };

        var normalizedRepoPath = repoPath.Replace('\\', '/').TrimEnd('/');
        var result = ComputeParentDirs(paths, normalizedRepoPath);

        Assert.Equal(3, result.Count);
        // deepest first: sub/deep > sub > repo1
        Assert.Equal("D:/repo1/sub/deep", result[0]);
        Assert.Equal("D:/repo1/sub", result[1]);
        Assert.Equal("D:/repo1", result[2]);
    }

    [Fact]
    public void ComputeParentDirs_same_dir_deduplicated()
    {
        var repoPath = "D:/repo1";
        var paths = new List<string>
        {
            "D:/repo1/sub/a.txt",
            "D:/repo1/sub/b.txt",
            "D:/repo1/sub/c.txt",
        };

        var normalizedRepoPath = repoPath.Replace('\\', '/').TrimEnd('/');
        var result = ComputeParentDirs(paths, normalizedRepoPath);

        Assert.Single(result);
        Assert.Equal("D:/repo1/sub", result[0]);
    }

    [Fact]
    public void ComputeParentDirs_empty_parent_maps_to_repo_root()
    {
        var repoPath = "D:/repo1";
        var paths = new List<string>
        {
            "D:/repo1",
            "D:/repo1/file.txt",
        };

        var normalizedRepoPath = repoPath.Replace('\\', '/').TrimEnd('/');
        var result = ComputeParentDirs(paths, normalizedRepoPath);

        Assert.Single(result);
        Assert.Equal("D:/repo1", result[0]);
    }

    [Fact]
    public void PathDepth_split_count_windows_and_unix()
    {
        Assert.Equal(1, @"D:\repo1".Split('\\', '/').Length);
        Assert.Equal(2, @"D:\repo1\sub".Split('\\', '/').Length);
        Assert.Equal(3, @"D:\repo1\sub\deep".Split('\\', '/').Length);
        Assert.Equal(1, "D:/repo1".Split('\\', '/').Length);
        Assert.Equal(2, "D:/repo1/sub".Split('\\', '/').Length);
        Assert.Equal(3, "D:/repo1/sub/deep".Split('\\', '/').Length);
    }
}