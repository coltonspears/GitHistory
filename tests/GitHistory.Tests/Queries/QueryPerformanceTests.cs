using System.Diagnostics;
using GitHistory.Core.Models;
using GitHistory.Core.Services;
using Xunit.Abstractions;

namespace GitHistory.Tests.Queries;

public sealed class QueryPerformanceTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Performance")]
    public void Report_filter_latency_for_ten_thousand_files_and_one_hundred_thousand_commits()
    {
        const int fileCount = 10_000;
        const int commitCount = 100_000;
        var now = new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);
        var files = Enumerable.Range(0, fileCount).Select(i => new TreeFile($"src/Module{i % 10}/File{i:D5}.cs", "object", "100644")).ToArray();
        var commits = new CommitInfo[commitCount];
        var changes = new FileChange[commitCount];
        for (int i = 0; i < commitCount; i++)
        {
            commits[i] = new CommitInfo(i.ToString("x40"), i == commitCount - 1 ? null : (i + 1).ToString("x40"),
                "Alex Rivera", "alex@example.com", now.AddMinutes(-i), now.AddMinutes(-i), "Improve query responsiveness");
            changes[i] = new FileChange(commits[i], files[i % fileCount].Path, null,
                i >= commitCount - fileCount ? ChangeKind.Added : ChangeKind.Modified, "old", "object", "100644", "100644");
        }
        var snapshot = new BranchSnapshot("benchmark", "main", commits[0].Oid, now, commits, changes, files);
        var service = new HistoryQueryService();
        var all = new HistoryFilter(FileViewMode.AllFiles, null, null);
        var watch = Stopwatch.StartNew();
        Assert.Equal(fileCount, service.Query(snapshot, all, now).Count);
        double coldMs = watch.Elapsed.TotalMilliseconds;

        double[] samples = new double[12];
        for (int i = 0; i < samples.Length; i++)
        {
            watch.Restart();
            var rows = service.Query(snapshot, all with { Search = i % 2 == 0 ? "File" : "Module4" }, now);
            samples[i] = watch.Elapsed.TotalMilliseconds;
            Assert.Equal(i % 2 == 0 ? fileCount : fileCount / 10, rows.Count);
        }
        Array.Sort(samples);
        watch.Restart();
        var visible = service.Query(snapshot, all, now);
        var activity = service.GetActivity(snapshot, all);
        double completeFilterMs = watch.Elapsed.TotalMilliseconds;
        Assert.Equal(fileCount, visible.Count);
        Assert.Equal(commitCount, activity.Sum(point => point.Commits));
        output.WriteLine($"Dataset: {commitCount:N0} commits, {fileCount:N0} current files.");
        output.WriteLine($"Cold snapshot query/index: {coldMs:F1} ms. Warm filtering median: {samples[samples.Length / 2]:F1} ms; max: {samples[^1]:F1} ms.");
        output.WriteLine($"Warm full file list plus activity aggregation: {completeFilterMs:F1} ms.");
        output.WriteLine("Target: warm filtering < 200 ms. Timings are reported rather than asserted because CI hardware varies.");
    }
}
