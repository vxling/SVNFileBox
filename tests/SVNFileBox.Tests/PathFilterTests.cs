using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace SVNFileBox.Tests;

public class PathDepthTests
{
    static int GetPathDepth(string path) => path.Split('\\', '/').Length;

    [Theory]
    [InlineData("D:/repo1", 1)]
    [InlineData("D:/repo1/sub", 2)]
    [InlineData("D:/repo1/sub/deep", 3)]
    [InlineData("D:/repo1/a/b/c/d", 5)]
    [InlineData(@"D:\repo1", 1)]
    [InlineData(@"D:\repo1\sub", 2)]
    [InlineData(@"D:\repo1\sub\deep\more", 4)]
    [InlineData("https://svn.server/repo/trunk/src/app", 6)]
    [InlineData("repo/file.txt", 2)]
    [InlineData("a/b/c/d/e/f", 6)]
    public void PathDepth_various_formats(string path, int expectedDepth)
    {
        Assert.Equal(expectedDepth, GetPathDepth(path));
    }
}

public class PathFilterTests
{
    static List<string> FilterToRepoRoot(List<string> paths, string repoPath)
    {
        var normalized = repoPath.Replace('\\', '/').TrimEnd('/');
        return paths.Where(p => p.StartsWith(normalized + '/') || p == normalized).ToList();
    }

    static List<string> ComputeParentDirs(List<string> inRepoPaths, string normalizedRepoPath)
    {
        return inRepoPaths
            .Select(p =>
            {
                var dir = Path.GetDirectoryName(p)?.Replace('\\', '/');
                return string.IsNullOrEmpty(dir) || dir == normalizedRepoPath ? normalizedRepoPath : dir;
            })
            .Distinct()
            .OrderByDescending(d => d.Split('\\', '/').Length)
            .ToList();
    }

    [Fact]
    public void Filter_excludes_sibling_repos()
    {
        var repoPath = "D:/repo1";
        var paths = new List<string>
        {
            "D:/repo1/file.txt",
            "D:/repo2/file.txt",
            "D:/repo1 sibling/file.txt",
            "D:/repo1邊測試/file.txt",
            "D:/repo10/file.txt",
        };

        var result = FilterToRepoRoot(paths, repoPath);

        Assert.Single(result);
        Assert.Equal("D:/repo1/file.txt", result[0]);
    }

    [Fact]
    public void Filter_excludes_subdirectory_named_similar_to_repo()
    {
        var repoPath = "D:/repo";
        var paths = new List<string>
        {
            "D:/repo/file.txt",
            "D:/repository/file.txt",
            "D:/repo-backup/file.txt",
        };

        var result = FilterToRepoRoot(paths, repoPath);

        Assert.Single(result);
    }

    [Fact]
    public void Filter_trims_trailing_slashes()
    {
        var repoPath = "D:/repo/";
        var paths = new List<string>
        {
            "D:/repo/file.txt",
            "D:/repo/sub/file.txt",
        };

        var result = FilterToRepoRoot(paths, repoPath);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Filter_handles_repo_root_exact_match()
    {
        var repoPath = "D:/repo";
        var paths = new List<string>
        {
            "D:/repo",
            "D:/repo/",
        };

        var result = FilterToRepoRoot(paths, repoPath);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void ComputeParentDirs_mixed_depths_sorted()
    {
        var paths = new List<string>
        {
            "D:/repo/sub/deep/file.txt",
            "D:/repo/sub/file.txt",
            "D:/repo/file.txt",
            "D:/repo/another/deep/file.txt",
        };

        var normalizedRepoPath = "D:/repo";
        var result = ComputeParentDirs(paths, normalizedRepoPath);

        Assert.Equal(4, result.Count);
        Assert.Equal("D:/repo/sub/deep", result[0]);  // depth 3
        Assert.Equal("D:/repo/another/deep", result[1]); // depth 3, order among same depth is not guaranteed but distinct
        Assert.Equal("D:/repo/sub", result[2]);        // depth 2
        Assert.Equal("D:/repo", result[3]);            // depth 1
    }

    [Fact]
    public void ComputeParentDirs_all_same_parent_deduplicates()
    {
        var paths = new List<string>
        {
            "D:/repo/sub/a.txt",
            "D:/repo/sub/b.txt",
            "D:/repo/sub/c.txt",
            "D:/repo/sub/deep/d.txt",
        };

        var result = ComputeParentDirs(paths, "D:/repo");

        Assert.Equal(2, result.Count);
        Assert.Contains("D:/repo/sub", result);
        Assert.Contains("D:/repo", result);
    }

    [Fact]
    public void ComputeParentDirs_handles_path_with_no_separator()
    {
        var paths = new List<string> { "D:/repo" };
        var result = ComputeParentDirs(paths, "D:/repo");

        Assert.Single(result);
        Assert.Equal("D:/repo", result[0]);
    }

    [Fact]
    public void ComputeParentDirs_empty_in_list_returns_empty()
    {
        var result = ComputeParentDirs(new List<string>(), "D:/repo");
        Assert.Empty(result);
    }

    [Fact]
    public void ComputeParentDirs_unix_forward_slash_only()
    {
        var paths = new List<string>
        {
            "/home/user/repo/file.txt",
            "/home/user/repo/sub/deep/file.txt",
        };

        var result = ComputeParentDirs(paths, "/home/user/repo");

        Assert.Equal(3, result.Count);
        Assert.Contains("/home/user/repo/sub/deep", result);
        Assert.Contains("/home/user/repo/sub", result);
        Assert.Contains("/home/user/repo", result);
    }
}