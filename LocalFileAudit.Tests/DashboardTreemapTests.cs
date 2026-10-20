using System;
using System.Collections.Generic;
using System.Linq;
using LocalFileAudit.Core.Common;
using LocalFileAudit.Core.Models;
using Xunit;

namespace LocalFileAudit.Tests;

public class DashboardTreemapTests
{
    [Fact]
    public void FilterNonOverlappingDirectories_ExcludesAncestors_ReturnsOnlyLeaves()
    {
        // Hierarchy:
        // D:\Root (100 MB)
        //   ├── FolderA (60 MB)
        //   │     └── Sub (60 MB)
        //   └── FolderB (40 MB)
        var dirs = new List<DirectorySummary>
        {
            new() { DirectoryPath = @"D:\Root", RecursiveSizeBytes = 100_000_000, DirectSizeBytes = 0 },
            new() { DirectoryPath = @"D:\Root\FolderA", RecursiveSizeBytes = 60_000_000, DirectSizeBytes = 0 },
            new() { DirectoryPath = @"D:\Root\FolderA\Sub", RecursiveSizeBytes = 60_000_000, DirectSizeBytes = 60_000_000 },
            new() { DirectoryPath = @"D:\Root\FolderB", RecursiveSizeBytes = 40_000_000, DirectSizeBytes = 40_000_000 }
        };

        var filtered = TreemapPartitionHelper.FilterNonOverlappingDirectories(dirs);

        // Must only contain Sub and FolderB
        Assert.Equal(2, filtered.Count);
        Assert.Contains(filtered, d => d.DirectoryPath.Equals(@"D:\Root\FolderA\Sub", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(filtered, d => d.DirectoryPath.Equals(@"D:\Root\FolderB", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(filtered, d => d.DirectoryPath.Equals(@"D:\Root", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(filtered, d => d.DirectoryPath.Equals(@"D:\Root\FolderA", StringComparison.OrdinalIgnoreCase));

        // Invariant: No item in the filtered set can be an ancestor of another
        for (int i = 0; i < filtered.Count; i++)
        {
            for (int j = 0; j < filtered.Count; j++)
            {
                if (i == j) continue;
                var a = filtered[i].DirectoryPath.TrimEnd('\\', '/');
                var b = filtered[j].DirectoryPath.TrimEnd('\\', '/');
                Assert.False(b.StartsWith(a + "\\", StringComparison.OrdinalIgnoreCase), $"{a} is an ancestor of {b}");
            }
        }

        // Percentage check with 100 MB total: neither exceeds 100% and sum equals 100%
        long total = 100_000_000;
        double sumPct = 0;
        foreach (var item in filtered)
        {
            double pct = (double)item.RecursiveSizeBytes / total * 100.0;
            Assert.True(pct <= 100.0, $"Percentage {pct}% exceeds 100%");
            sumPct += pct;
        }
        Assert.Equal(100.0, sumPct, 2);
    }

    [Fact]
    public void TreemapPartitionHelper_WhenParentAndChildPresent_ProducesNonOverlappingPartition()
    {
        var dirs = new List<DirectorySummary>
        {
            new() { DirectoryPath = @"C:\Data", RecursiveSizeBytes = 500, DirectSizeBytes = 100 },
            new() { DirectoryPath = @"C:\Data\Photos", RecursiveSizeBytes = 400, DirectSizeBytes = 400 }
        };

        var nonOverlapping = TreemapPartitionHelper.FilterNonOverlappingDirectories(dirs);

        Assert.Single(nonOverlapping);
        Assert.Equal(@"C:\Data\Photos", nonOverlapping[0].DirectoryPath);
    }

    [Fact]
    public void UtcDateTimeHelper_ToLocal_ConvertsUtcToLocalMachineTime()
    {
        var utcTime = new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
        var expectedLocal = utcTime.ToLocalTime();

        var local = UtcDateTimeHelper.ToLocal(utcTime);

        Assert.Equal(expectedLocal, local);
        Assert.Equal(DateTimeKind.Local, local.Kind);
    }

    [Fact]
    public void UtcDateTimeHelper_ToLocalNullable_HandlesNullGracefully()
    {
        Assert.Null(UtcDateTimeHelper.ToLocalNullable(null));

        var utcTime = new DateTime(2026, 10, 20, 15, 30, 0, DateTimeKind.Utc);
        var local = UtcDateTimeHelper.ToLocalNullable(utcTime);
        Assert.NotNull(local);
        Assert.Equal(utcTime.ToLocalTime(), local.Value);
    }
}
