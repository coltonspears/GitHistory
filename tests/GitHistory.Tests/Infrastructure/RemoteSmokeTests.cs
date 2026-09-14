using GitHistory.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace GitHistory.Tests.Infrastructure;

public sealed class RemoteSmokeTests(ITestOutputHelper output)
{
    [RemoteSmokeFact]
    [Trait("Category", "RemoteSmoke")]
    public async Task ProductionEngineReadsHttpsRemoteAndReopensItsCache()
    {
        using var fixture = await GitFixture.CreateAsync();
        using var service = new GitRepositoryService(fixture.Data, NullLogger<GitRepositoryService>.Instance);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var remote = Environment.GetEnvironmentVariable("GITHISTORY_TEST_REMOTE")!;
        var repository = await service.ConnectAsync(remote, null, cancellation.Token);
        Assert.NotEmpty(repository.Branches);
        var snapshot = await service.RefreshAsync(repository, repository.DefaultBranch, null, cancellation.Token);
        Assert.NotEmpty(snapshot.Commits);
        Assert.NotEmpty(snapshot.Files);
        Assert.NotEmpty(snapshot.Changes);
        var change = snapshot.Changes.First(change => change.NewMode == "100644");
        var diff = await service.GetDiffAsync(repository, change, cancellation.Token);
        Assert.NotEmpty(diff.Lines);
        using var reopened = new GitRepositoryService(fixture.Data, NullLogger<GitRepositoryService>.Instance);
        var cached = await reopened.GetCachedSnapshotAsync(repository, repository.DefaultBranch, cancellation.Token);
        Assert.Equal(snapshot.TipOid, cached!.TipOid);
        Assert.Equal(snapshot.Commits.Count, cached.Commits.Count);
        var report = $"Production remote smoke passed: {repository.Name} / {repository.DefaultBranch}; tip {snapshot.TipOid}; {snapshot.Commits.Count} first-parent commits; {snapshot.Files.Count} files; native diff and offline reopen verified.";
        output.WriteLine(report);
        await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "git-remote-smoke-results.txt"), report, cancellation.Token);
    }
}

public sealed class RemoteSmokeFactAttribute : FactAttribute
{
    public RemoteSmokeFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GITHISTORY_TEST_REMOTE")))
            Skip = "Set GITHISTORY_TEST_REMOTE to an HTTPS Git remote to run this read-only network smoke test.";
    }
}
