using System.Diagnostics;
using System.Globalization;
using System.Text;
using GitHistory.Core.Models;
using GitHistory.Core.Services;
using Xunit.Abstractions;

namespace GitHistory.Tests.Infrastructure;

public sealed class GitPerformanceTests(ITestOutputHelper output)
{
    [BenchmarkFact]
    [Trait("Category", "Benchmark")]
    public async Task MeasuresOneHundredThousandCommitsAndTenThousandFiles()
    {
        using var fixture = await GitFixture.CreateAsync();
        var stopwatch = Stopwatch.StartNew();
        await ImportAsync(fixture, 100_000, 10_000);
        var import = stopwatch.Elapsed;
        var repository = await fixture.ConnectAsync();
        stopwatch.Restart();
        var cold = await fixture.RefreshAsync(repository);
        var coldRefresh = stopwatch.Elapsed;
        Assert.Equal(100_000, cold.Commits.Count);
        Assert.Equal(10_000, cold.Files.Count);

        stopwatch.Restart();
        var unchanged = await fixture.RefreshAsync(repository);
        var warmRefresh = stopwatch.Elapsed;
        Assert.Equal(cold.TipOid, unchanged.TipOid);

        using var reopened = fixture.CreateService();
        stopwatch.Restart();
        var cached = await reopened.GetCachedSnapshotAsync(repository, "main", CancellationToken.None);
        var cachedOpen = stopwatch.Elapsed;
        Assert.NotNull(cached);

        var queries = new HistoryQueryService();
        stopwatch.Restart();
        var firstRows = queries.Query(cached, new(FileViewMode.AllFiles, null, null), DateTimeOffset.UtcNow);
        var firstQuery = stopwatch.Elapsed;
        Assert.Equal(10_000, firstRows.Count);
        stopwatch.Restart();
        var filtered = queries.Query(cached, new(FileViewMode.AllFiles, null, null, Search: "file001"), DateTimeOffset.UtcNow);
        var filteredQuery = stopwatch.Elapsed;
        Assert.Equal(100, filtered.Count);
        var report = $"""
            Fixture: 100,000 first-parent commits; 10,000 files; {cached.Changes.Count:N0} path changes.
            Fast-import fixture creation: {import.TotalSeconds:F3} s
            Cold local fetch + complete index + snapshot publish: {coldRefresh.TotalSeconds:F3} s
            Unchanged local fetch + reused index + snapshot publish: {warmRefresh.TotalSeconds:F3} s
            Cached snapshot open (fresh service): {cachedOpen.TotalMilliseconds:F1} ms
            Initial All Files query including query index: {firstQuery.TotalMilliseconds:F1} ms
            Filter 10,000 file rows: {filteredQuery.TotalMilliseconds:F1} ms
            Fetch uses a local source; internet and authentication time are not represented.
            """;
        output.WriteLine(report);
        await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "git-performance-results.txt"), report);
    }

    private static async Task ImportAsync(GitFixture fixture, int commitCount, int fileCount)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = fixture.Remote,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            CreateNoWindow = true
        };
        start.ArgumentList.Add("fast-import");
        start.ArgumentList.Add("--quiet");
        using var process = Process.Start(start)!;
        var errors = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEndAsync();
        using var writer = process.StandardInput;
        writer.NewLine = "\n";
        var timestamp = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        for (var i = 0; i < commitCount; i++)
        {
            var subject = "Benchmark commit " + i.ToString(CultureInfo.InvariantCulture);
            var time = (timestamp + i).ToString(CultureInfo.InvariantCulture);
            await writer.WriteAsync($"commit refs/heads/main\nauthor Fixture Author <fixture@example.test> {time} +0000\ncommitter Fixture Author <fixture@example.test> {time} +0000\ndata {subject.Length}\n{subject}\n");
            var startFile = i == 0 ? 0 : i % fileCount;
            var endFile = i == 0 ? fileCount : startFile + 1;
            for (var file = startFile; file < endFile; file++)
            {
                var content = $"File {file}, change {i}\n";
                var path = $"dir{file / 100:D3}/file{file % 100:D3}.txt";
                await writer.WriteAsync($"M 100644 inline {path}\ndata {content.Length}\n{content}\n");
            }
            await writer.WriteLineAsync();
        }
        await writer.FlushAsync();
        writer.Close();
        await process.WaitForExitAsync();
        var error = await errors;
        await output;
        Assert.True(process.ExitCode == 0, error);
    }
}

public sealed class BenchmarkFactAttribute : FactAttribute
{
    public BenchmarkFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("GITHISTORY_BENCHMARK") != "1")
            Skip = "Set GITHISTORY_BENCHMARK=1 to run the real 100k-commit / 10k-file Git benchmark.";
    }
}
