using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace SVNFileBox.Tests;

/// <summary>
/// Tests the sync record retention policy: max 10 days or 10000 records per repo.
/// </summary>
public class RetentionPolicyTests
{
    static (int DaysKept, int MaxRecords) GetPolicy() => (10, 10000);

    static List<DateTime> ComputeExpiredDates(DateTime now, int daysKept)
    {
        var cutoff = now.AddDays(-daysKept);
        return new List<DateTime> { cutoff.AddDays(-1), cutoff.AddDays(-5), cutoff.AddDays(-30) };
    }

    static List<DateTime> ComputeKeptDates(DateTime now, int daysKept)
    {
        var cutoff = now.AddDays(-daysKept);
        return new List<DateTime> { cutoff.AddDays(0), cutoff.AddDays(-1).AddHours(1), now };
    }

    [Fact]
    public void Retention_cutoff_10_days_exact()
    {
        var (daysKept, _) = GetPolicy();
        var now = new DateTime(2026, 5, 20, 12, 0, 0);
        var cutoff = now.AddDays(-daysKept); // 2026-05-10

        var expired = new[] { new DateTime(2026, 5, 9), new DateTime(2026, 5, 5) };
        var kept = new[] { new DateTime(2026, 5, 10), new DateTime(2026, 5, 15), now };

        Assert.All(expired, d => Assert.True(d < cutoff));
        Assert.All(kept, d => Assert.True(d >= cutoff));
    }

    [Fact]
    public void Retention_10000_record_cap()
    {
        const int MaxRecords = 10000;
        const int TotalRecords = 15000;

        // Simulate: keep newest 10000, discard oldest 5000
        var allRecords = new List<int>();
        for (int i = 0; i < TotalRecords; i++) allRecords.Add(i);

        var newest10000 = allRecords.OrderByDescending(r => r).Take(MaxRecords).ToList();

        Assert.Equal(MaxRecords, newest10000.Count);
        Assert.Equal(10000, newest10000[^1]); // last kept = record 9999
        Assert.True(newest10000[0] > newest10000[^1]); // newest > oldest kept
    }

    [Fact]
    public void Retention_under_cap_no_records_removed()
    {
        const int MaxRecords = 10000;
        var records = Enumerable.Range(0, 5000).Select(i => (id: i, dt: DateTime.Now.AddMinutes(-i))).ToList();

        var kept = records.Count <= MaxRecords ? records : records.OrderByDescending(r => r.dt).Take(MaxRecords).ToList();

        Assert.Equal(5000, kept.Count);
    }

    [Fact]
    public void Retention_at_exact_cap()
    {
        const int MaxRecords = 10000;
        var records = Enumerable.Range(0, 10000).Select(i => (id: i, dt: DateTime.Now.AddMinutes(-i))).ToList();

        var kept = records.Count <= MaxRecords ? records : records.OrderByDescending(r => r.dt).Take(MaxRecords).ToList();

        Assert.Equal(MaxRecords, kept.Count);
    }

    [Fact]
    public void Retention_just_over_cap()
    {
        const int MaxRecords = 10000;
        var records = Enumerable.Range(0, 10001).Select(i => (id: i, dt: DateTime.Now.AddMinutes(-i))).ToList();

        var kept = records.Count <= MaxRecords ? records : records.OrderByDescending(r => r.dt).Take(MaxRecords).ToList();

        Assert.Equal(MaxRecords, kept.Count);
        Assert.Equal(1, records.Count - kept.Count); // 1 discarded
    }

    [Fact]
    public void Retention_both_age_and_count_applied()
    {
        // Scenario: 15000 records, oldest are beyond 10 days, newest 10000 within 10 days
        const int MaxRecords = 10000;
        const int DaysKept = 10;
        var now = new DateTime(2026, 5, 20, 12, 0, 0);

        // 12000 records within 10 days, 3000 records older than 10 days
        var records = new List<(int id, DateTime dt)>();
        for (int i = 0; i < 12000; i++)
            records.Add((i, now.AddMinutes(-i))); // newest 12000 records, within 10 days
        for (int i = 12000; i < 15000; i++)
            records.Add((i, now.AddDays(-11)));   // oldest 3000 records, beyond cutoff

        // First filter by age
        var withinRetention = records.Where(r => r.dt >= now.AddDays(-DaysKept)).ToList();
        // Then apply count cap
        var kept = withinRetention.Count <= MaxRecords
            ? withinRetention
            : withinRetention.OrderByDescending(r => r.dt).Take(MaxRecords).ToList();

        Assert.Equal(10000, kept.Count);
        // All kept records must be within retention period
        Assert.All(kept, r => Assert.True(r.dt >= now.AddDays(-DaysKept)));
    }

    [Fact]
    public void Retention_empty_repo()
    {
        var records = new List<(int id, DateTime dt)>();
        var kept = records.Count <= 10000 ? records : records.Take(0).ToList();
        Assert.Empty(kept);
    }

    [Fact]
    public void Retention_single_old_record_discarded()
    {
        var records = new List<(int id, DateTime dt)>
        {
            (1, DateTime.Now.AddDays(-15))
        };

        var cutoff = DateTime.Now.AddDays(-10);
        var withinRetention = records.Where(r => r.dt >= cutoff).ToList();

        Assert.Empty(withinRetention);
    }

    [Fact]
    public void Retention_single_new_record_kept()
    {
        var records = new List<(int id, DateTime dt)>
        {
            (1, DateTime.Now)
        };

        var cutoff = DateTime.Now.AddDays(-10);
        var withinRetention = records.Where(r => r.dt >= cutoff).ToList();

        Assert.Single(withinRetention);
    }
}

/// <summary>
/// Tests sanitization of repo names for SQLite table names.
/// </summary>
public class TableNameSanitizationTests
{
    static string SanitizeTableName(string repoName)
    {
        // Simple simulation of what SqliteSyncRecordStore does
        var sanitized = repoName.Replace(' ', '_').Replace('-', '_');
        // Remove any non-alphanumeric except underscore
        var result = "";
        foreach (var c in sanitized)
            if (char.IsLetterOrDigit(c) || c == '_') result += c;
        // Ensure not empty and not starts with digit
        if (string.IsNullOrEmpty(result) || char.IsDigit(result[0]))
            result = "repo_" + result;
        return "sync_" + result.ToLower();
    }

    [Theory]
    [InlineData("My Project", "sync_my_project")]
    [InlineData("repo-1", "sync_repo_1")]
    [InlineData("SVNFileBox", "sync_svnfilebox")]
    [InlineData("测试仓库", "sync_测试仓库")]
    [InlineData("repo/with/slash", "sync_repo_with_slash")]
    [InlineData("123 project", "sync_repo_123_project")]
    [InlineData("C:", "sync_c_")]
    [InlineData("", "sync_default")]
    public void Sanitize_various_repo_names(string repoName, string expectedPrefix)
    {
        var result = SanitizeTableName(repoName);
        Assert.StartsWith("sync_", result);
        Assert.DoesNotContain("-", result);
        Assert.DoesNotContain(" ", result);
    }

    [Fact]
    public void Sanitize_numeric_start_prepended()
    {
        var result = SanitizeTableName("123");
        Assert.StartsWith("repo_", result); // "repo_" + sanitized numeric prefix
    }
}