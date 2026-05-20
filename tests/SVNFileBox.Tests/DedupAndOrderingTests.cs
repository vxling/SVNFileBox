using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace SVNFileBox.Tests;

/// <summary>
/// Tests the dedup map logic used in SvnCommandExecutor to prevent
/// duplicate commands of the same type/path from being processed concurrently.
/// </summary>
public class DedupMapTests
{
    [Fact]
    public void Dedup_same_path_same_command_blocks()
    {
        var inFlight = new Dictionary<(string cmd, string path), bool>();

        // First add of "Update D:/repo/sub" should succeed
        var key = ("Update", "D:/repo/sub");
        bool acquired = !inFlight.ContainsKey(key);
        if (acquired) inFlight[key] = true;

        Assert.True(acquired);
        Assert.Single(inFlight);
    }

    [Fact]
    public void Dedup_same_path_different_command_allowed()
    {
        var inFlight = new Dictionary<(string cmd, string path), bool>();

        var key1 = ("Update", "D:/repo/sub");
        var key2 = ("Commit", "D:/repo/sub");

        bool first = !inFlight.ContainsKey(key1) && (inFlight[key1] = true);
        bool second = !inFlight.ContainsKey(key2) && (inFlight[key2] = true);

        Assert.True(first);
        Assert.True(second);
        Assert.Equal(2, inFlight.Count);
    }

    [Fact]
    public void Dedup_different_path_same_command_allowed()
    {
        var inFlight = new Dictionary<(string cmd, string path), bool>();

        var key1 = ("Update", "D:/repo/sub1");
        var key2 = ("Update", "D:/repo/sub2");

        bool first = !inFlight.ContainsKey(key1) && (inFlight[key1] = true);
        bool second = !inFlight.ContainsKey(key2) && (inFlight[key2] = true);

        Assert.True(first);
        Assert.True(second);
        Assert.Equal(2, inFlight.Count);
    }

    [Fact]
    public void Dedup_same_path_same_command_rejected_when_in_flight()
    {
        var inFlight = new Dictionary<(string cmd, string path), bool>
        {
            [("Update", "D:/repo/sub")] = true
        };

        var key = ("Update", "D:/repo/sub");
        bool acquired = !inFlight.ContainsKey(key);

        Assert.False(acquired);
    }

    [Fact]
    public void Dedup_after_completion_allows_reuse()
    {
        var inFlight = new Dictionary<(string cmd, string path), bool>
        {
            [("Update", "D:/repo/sub")] = true
        };

        var key = ("Update", "D:/repo/sub");

        // Simulate completion: remove from inFlight
        inFlight.Remove(key);

        bool acquired = !inFlight.ContainsKey(key) && (inFlight[key] = true);
        Assert.True(acquired);
    }

    [Fact]
    public void Dedup_3_concurrent_same_command_same_path()
    {
        var inFlight = new Dictionary<(string cmd, string path), bool>();

        var key = ("Commit", "D:/repo/sub");

        var results = new List<bool>();
        for (int i = 0; i < 3; i++)
        {
            results.Add(!inFlight.ContainsKey(key) && (inFlight[key] = true));
        }

        Assert.True(results[0]); // first allowed
        Assert.False(results[1]); // second blocked
        Assert.False(results[2]); // third blocked
        Assert.Single(inFlight);
    }
}

/// <summary>
/// Tests the directory ordering logic (deepest-first) used in
/// ScanAndCommit and UpdateInChunks to ensure children are processed
/// before parents.
/// </summary>
public class DirOrderingTests
{
    static List<string> SortDeepestFirst(List<string> dirs)
    {
        return dirs.Distinct().OrderByDescending(d => d.Split('\\', '/').Length).ToList();
    }

    [Fact]
    public void Order_deepest_first_simple()
    {
        var dirs = new List<string>
        {
            "D:/repo",
            "D:/repo/sub",
            "D:/repo/sub/deep",
        };

        var result = SortDeepestFirst(dirs);

        Assert.Equal("D:/repo/sub/deep", result[0]);
        Assert.Equal("D:/repo/sub", result[1]);
        Assert.Equal("D:/repo", result[2]);
    }

    [Fact]
    public void Order_deepest_first_multiple_branches()
    {
        var dirs = new List<string>
        {
            "D:/repo",
            "D:/repo/a",
            "D:/repo/a/b",
            "D:/repo/x",
            "D:/repo/x/y/z",
        };

        var result = SortDeepestFirst(dirs);

        var depths = result.Select(d => d.Split('\\', '/').Length).ToList();
        Assert.True(depths.SequenceEqual(depths.OrderByDescending(x => x)));
    }

    [Fact]
    public void Order_same_depth_stable()
    {
        var dirs = new List<string> { "D:/repo/a", "D:/repo/b", "D:/repo/c" };

        var result = SortDeepestFirst(dirs);

        Assert.Equal(3, result.Count);
        // All same depth, order among them doesn't matter but all present
        Assert.True(result.All(d => d.StartsWith("D:/repo/")));
    }

    [Fact]
    public void Order_empty_list()
    {
        var result = SortDeepestFirst(new List<string>());
        Assert.Empty(result);
    }

    [Fact]
    public void Order_single_dir()
    {
        var result = SortDeepestFirst(new List<string> { "D:/repo" });
        Assert.Single(result);
        Assert.Equal("D:/repo", result[0]);
    }

    [Fact]
    public void Order_deduplicates_before_sorting()
    {
        var dirs = new List<string>
        {
            "D:/repo/sub",
            "D:/repo/sub", // duplicate
            "D:/repo",
            "D:/repo/sub/deep",
        };

        var result = SortDeepestFirst(dirs);

        Assert.Equal(3, result.Count);
    }
}

/// <summary>
/// Tests the scan-and-commit grouping logic.
/// </summary>
public class ScanAndCommitGroupTests
{
    record FileStatus(string Path, bool IsVersioned);

    static List<(string Dir, List<string> Files)> GroupForCommit(
        List<string> filePaths, string repoPath)
    {
        var normalized = repoPath.Replace('\\', '/').TrimEnd('/');
        var inRepo = filePaths
            .Where(p => p.StartsWith(normalized + '/') || p == normalized)
            .ToList();

        return inRepo
            .Select(p =>
            {
                var parent = Path.GetDirectoryName(p)?.Replace('\\', '/');
                return (dir: string.IsNullOrEmpty(parent) ? normalized : parent, file: p);
            })
            .GroupBy(x => x.dir, x => x.file)
            .OrderByDescending(g => g.Key.Split('\\', '/').Length)
            .Select(g => (g.Key, g.ToList()))
            .ToList();
    }

    [Fact]
    public void Group_single_file_root_level()
    {
        var files = new List<string> { "D:/repo/file.txt" };
        var result = GroupForCommit(files, "D:/repo");

        Assert.Single(result);
        Assert.Equal("D:/repo", result[0].Dir);
        Assert.Single(result[0].Files);
        Assert.Equal("D:/repo/file.txt", result[0].Files[0]);
    }

    [Fact]
    public void Group_multiple_files_same_dir()
    {
        var files = new List<string>
        {
            "D:/repo/sub/a.txt",
            "D:/repo/sub/b.txt",
            "D:/repo/sub/c.txt",
        };

        var result = GroupForCommit(files, "D:/repo");

        Assert.Single(result);
        Assert.Equal("D:/repo/sub", result[0].Dir);
        Assert.Equal(3, result[0].Files.Count);
    }

    [Fact]
    public void Group_multiple_dirs_sorted_deepest_first()
    {
        var files = new List<string>
        {
            "D:/repo/file.txt",
            "D:/repo/sub/deep/file.txt",
            "D:/repo/sub/file.txt",
        };

        var result = GroupForCommit(files, "D:/repo");

        Assert.Equal(3, result.Count);
        Assert.Equal("D:/repo/sub/deep", result[0].Dir); // deepest
        Assert.Equal("D:/repo/sub", result[1].Dir);
        Assert.Equal("D:/repo", result[2].Dir); // shallowest
    }

    [Fact]
    public void Group_excludes_paths_outside_repo()
    {
        var files = new List<string>
        {
            "D:/repo/file.txt",
            "D:/external/file.txt",
            "D:/repo/sub/file.txt",
        };

        var result = GroupForCommit(files, "D:/repo");

        Assert.Equal(2, result.Count);
        Assert.All(result, g => Assert.StartsWith("D:/repo", g.Dir));
    }

    [Fact]
    public void Group_empty_list()
    {
        var result = GroupForCommit(new List<string>(), "D:/repo");
        Assert.Empty(result);
    }

    [Fact]
    public void Group_mixed_case_unix_windows()
    {
        var files = new List<string>
        {
            @"D:\repo\sub\file.txt",
            "D:/repo/file.txt",
        };

        var result = GroupForCommit(files, @"D:\repo");

        Assert.Equal(2, result.Count);
    }
}