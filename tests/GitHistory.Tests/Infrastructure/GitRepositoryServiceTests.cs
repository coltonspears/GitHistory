using System.Diagnostics;
using System.Text;
using GitHistory.Core.Models;
using GitHistory.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace GitHistory.Tests.Infrastructure;

public sealed class GitRepositoryServiceTests
{
    [Theory]
    [InlineData("https://github.com/example/project.git")]
    [InlineData("ssh://git@github.com/example/project.git")]
    [InlineData("git@github.com:example/project.git")]
    [InlineData("git.example.com:example/project.git")]
    public void AcceptsHttpsAndSshRemotes(string remote) => Assert.Equal(remote, GitRepositoryService.ValidateRemote(remote));

    [Theory]
    [InlineData("https://token@github.com/example/project.git")]
    [InlineData("https://user:password@github.com/example/project.git")]
    [InlineData("https://github.com/example/project.git?token=secret")]
    [InlineData("http://github.com/example/project.git")]
    [InlineData("file:///C:/repos/test")]
    [InlineData("ext::malicious-command")]
    [InlineData("--upload-pack=malicious-command")]
    [InlineData("https://github.com/")]
    public void RejectsSecretsAndUnsupportedProtocols(string remote) => Assert.Throws<ArgumentException>(() => GitRepositoryService.ValidateRemote(remote));

    [Fact]
    public async Task ConnectDiscoversBranchesAndPersistsRepository()
    {
        using var fixture = await GitFixture.CreateAsync();
        await fixture.WriteAsync("old.txt", "created years ago\n");
        await fixture.CommitAsync("Initial", "2020-01-01T12:00:00Z");
        await fixture.RunAsync("branch", "feature/ui");
        var repository = await fixture.ConnectAsync();
        Assert.Equal("main", repository.DefaultBranch);
        Assert.Equal(["feature/ui", "main"], repository.Branches);
        using var reopened = fixture.CreateService();
        var saved = Assert.Single(await reopened.GetRepositoriesAsync());
        Assert.Equal(repository.Id, saved.Id);
        Assert.Equal(repository.RemoteUrl, saved.RemoteUrl);
        var snapshot = await reopened.RefreshAsync(saved, "main", null, CancellationToken.None);
        Assert.Equal(2020, Assert.Single(snapshot.Commits).CommittedAt.Year);
        Assert.Equal("old.txt", Assert.Single(snapshot.Files).Path);
        await fixture.RunAsync("branch", "feature/added-later");
        await reopened.RefreshAsync(saved, "main", null, CancellationToken.None);
        Assert.Contains("feature/added-later", Assert.Single(await reopened.GetRepositoriesAsync()).Branches);
    }

    [Fact]
    public async Task SupportsSha256RepositoriesAndEmptyBlobDiffs()
    {
        using var fixture = await GitFixture.CreateAsync("sha256");
        await fixture.WriteAsync("sha256.txt", "sha256 object format\n");
        await fixture.CommitAsync("SHA-256 commit");
        var repository = await fixture.ConnectAsync();
        var snapshot = await fixture.RefreshAsync(repository);
        Assert.Equal(64, snapshot.TipOid.Length);
        var patch = await fixture.Service.GetDiffAsync(repository, Assert.Single(snapshot.Changes), CancellationToken.None);
        Assert.Contains(patch.Lines, line => line is { Kind: DiffLineKind.Added, Text: "sha256 object format" });
    }

    [Fact]
    public async Task DeletedRemoteBranchRemainsSelectableFromCompletedCacheAfterRestart()
    {
        using var fixture = await GitFixture.CreateAsync();
        await fixture.WriteAsync("main.txt", "main branch\n");
        await fixture.CommitAsync("Main branch");
        await fixture.RunAsync("checkout", "-b", "feature/cached");
        await fixture.WriteAsync("feature.txt", "preserve this history\n");
        await fixture.CommitAsync("Cached feature");
        await fixture.RunAsync("checkout", "main");
        var repository = await fixture.ConnectAsync();
        var previous = await fixture.Service.RefreshAsync(repository, "feature/cached", null, CancellationToken.None);
        await fixture.RunAsync("branch", "-D", "feature/cached");

        await Assert.ThrowsAsync<GitCommandException>(() => fixture.Service.RefreshAsync(repository, "feature/cached", null, CancellationToken.None));
        using var reopened = fixture.CreateService();
        var saved = Assert.Single(await reopened.GetRepositoriesAsync());
        Assert.Equal("main", saved.DefaultBranch);
        Assert.Equal(["feature/cached", "main"], saved.Branches);
        var cached = await reopened.GetCachedSnapshotAsync(saved, "feature/cached", CancellationToken.None);
        Assert.NotNull(cached);
        Assert.Equal(previous.TipOid, cached.TipOid);
        Assert.Equal(previous.RefreshedAt, cached.RefreshedAt);
        Assert.Contains(cached.Files, file => file.Path == "feature.txt");

        await Assert.ThrowsAsync<GitCommandException>(() => reopened.RefreshAsync(saved, "feature/cached", null, CancellationToken.None));
        Assert.Contains("feature/cached", Assert.Single(await reopened.GetRepositoriesAsync()).Branches);
        var unchanged = await reopened.GetCachedSnapshotAsync(saved, "feature/cached", CancellationToken.None);
        Assert.Equal(previous.TipOid, unchanged!.TipOid);
    }

    [Fact]
    public async Task IndexesRenamesDeletesBinaryAndNativeDiffs()
    {
        using var fixture = await GitFixture.CreateAsync();
        await fixture.WriteAsync("old name.txt", "one\ntwo\nthree\n");
        await fixture.WriteAsync("delete.txt", "bye\n");
        await File.WriteAllBytesAsync(Path.Combine(fixture.Remote, "image.bin"), [0, 1, 2, 3]);
        await fixture.CommitAsync("Initial files");
        await fixture.RunAsync("mv", "old name.txt", "renamed name.txt");
        await fixture.CommitAsync("Rename file");
        await fixture.WriteAsync("renamed name.txt", "one\nchanged\nthree\n");
        await fixture.RunAsync("rm", "delete.txt");
        await File.WriteAllBytesAsync(Path.Combine(fixture.Remote, "image.bin"), [0, 9, 8, 7]);
        await fixture.CommitAsync("Modify and delete");
        var repository = await fixture.ConnectAsync();
        var snapshot = await fixture.RefreshAsync(repository);
        Assert.Equal(3, snapshot.Commits.Count);
        var rename = Assert.Single(snapshot.Changes, change => change.Kind == ChangeKind.Renamed);
        Assert.Equal("old name.txt", rename.OldPath);
        Assert.Equal("renamed name.txt", rename.Path);
        Assert.DoesNotContain(snapshot.Files, file => file.Path == "delete.txt");
        var modified = snapshot.Changes.First(change => change.Path == "renamed name.txt");
        var patch = await fixture.Service.GetDiffAsync(repository, modified, CancellationToken.None);
        Assert.Contains(patch.Lines, line => line is { Kind: DiffLineKind.Deleted, OldLine: 2, Text: "two" });
        Assert.Contains(patch.Lines, line => line is { Kind: DiffLineKind.Added, NewLine: 2, Text: "changed" });
        Assert.False(patch.IsBinary);
        var deleted = snapshot.Changes.First(change => change.Kind == ChangeKind.Deleted);
        var deletedPatch = await fixture.Service.GetDiffAsync(repository, deleted, CancellationToken.None);
        Assert.Contains(deletedPatch.Lines, line => line is { Kind: DiffLineKind.Deleted, Text: "bye" });
        var binary = await fixture.Service.GetDiffAsync(repository, snapshot.Changes.First(change => change.Path == "image.bin"), CancellationToken.None);
        Assert.True(binary.IsBinary);
        var added = snapshot.Changes.First(change => change.Path == "delete.txt" && change.Kind == ChangeKind.Added);
        var addedPatch = await fixture.Service.GetDiffAsync(repository, added, CancellationToken.None);
        Assert.Contains(addedPatch.Lines, line => line is { Kind: DiffLineKind.Added, NewLine: 1, Text: "bye" });
    }

    [Fact]
    public async Task MergeIsComparedWithFirstParentAndClockSkewDoesNotReorderAncestry()
    {
        using var fixture = await GitFixture.CreateAsync();
        await fixture.WriteAsync("base.txt", "base\n");
        var first = await fixture.CommitAsync("Base", "2026-01-02T12:00:00Z");
        await fixture.RunAsync("checkout", "-b", "feature");
        await fixture.WriteAsync("feature.txt", "landed via merge\n");
        var side = await fixture.CommitAsync("Feature author commit", "2026-01-20T12:00:00Z");
        await fixture.RunAsync("checkout", "main");
        await fixture.WriteAsync("main.txt", "main\n");
        var middle = await fixture.CommitAsync("Main work", "2026-01-10T12:00:00Z");
        await fixture.RunAtAsync("2026-01-05T12:00:00Z", "merge", "--no-ff", "feature", "-m", "Merge feature");
        var merge = (await fixture.RunAsync("rev-parse", "HEAD")).Trim();
        var repository = await fixture.ConnectAsync();
        var snapshot = await fixture.RefreshAsync(repository);
        Assert.Equal([merge, middle, first], snapshot.Commits.Select(commit => commit.Oid));
        Assert.DoesNotContain(snapshot.Commits, commit => commit.Oid == side);
        var landed = snapshot.Changes.First(change => change.Path == "feature.txt");
        Assert.Equal(merge, landed.Commit.Oid);
        Assert.Equal(5, landed.Commit.CommittedAt.Day);
        Assert.Equal(middle, landed.Commit.ParentOid);
    }

    [Fact]
    public async Task PreservesCaseAndNulDelimitedUnusualPaths()
    {
        using var fixture = await GitFixture.CreateAsync();
        await fixture.WriteAsync("seed.txt", "content\n");
        var blob = (await fixture.RunAsync("hash-object", "-w", "seed.txt")).Trim();
        var paths = new[] { "Upper.txt", "upper.txt", "dir/space name.txt", "dir/你好.txt", "dir/tab\tname.txt", "dir/new\nline.txt", ":(glob)*.txt", "GHCOMMIT" };
        foreach (var path in paths) await fixture.RunAsync("update-index", "--add", "--cacheinfo", $"100644,{blob},{path}");
        await fixture.RunAsync("commit", "-m", "Index unusual paths");
        var repository = await fixture.ConnectAsync();
        var snapshot = await fixture.RefreshAsync(repository);
        Assert.Equal(paths.Order(StringComparer.Ordinal), snapshot.Files.Select(file => file.Path).Order(StringComparer.Ordinal));
        Assert.Equal(paths.Order(StringComparer.Ordinal), snapshot.Changes.Select(change => change.Path).Order(StringComparer.Ordinal));
        var cached = await fixture.Service.GetCachedSnapshotAsync(repository, "main", CancellationToken.None);
        Assert.NotNull(cached);
        Assert.Equal(paths.Order(StringComparer.Ordinal), cached.Files.Select(file => file.Path).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task RefreshReusesCommitsAndRebuildsAfterForcePush()
    {
        using var fixture = await GitFixture.CreateAsync();
        await fixture.WriteAsync("base.txt", "base\n");
        var first = await fixture.CommitAsync("Base");
        await fixture.WriteAsync("original.txt", "original\n");
        var original = await fixture.CommitAsync("Original tip");
        var repository = await fixture.ConnectAsync();
        var initial = await fixture.RefreshAsync(repository);
        var repeated = await fixture.RefreshAsync(repository);
        Assert.Equal(initial.TipOid, repeated.TipOid);
        Assert.Equal(2, fixture.CountIndexedCommits());
        await fixture.RunAsync("reset", "--hard", first);
        await fixture.WriteAsync("replacement.txt", "replacement\n");
        var replacement = await fixture.CommitAsync("Replacement tip");
        var refreshed = await fixture.RefreshAsync(repository);
        Assert.Equal([replacement, first], refreshed.Commits.Select(commit => commit.Oid));
        Assert.DoesNotContain(refreshed.Files, file => file.Path == "original.txt");
        Assert.Contains(refreshed.Files, file => file.Path == "replacement.txt");
        Assert.Equal(3, fixture.CountIndexedCommits());
        var oldDiff = await fixture.Service.GetDiffAsync(repository, initial.Changes.First(change => change.Commit.Oid == original), CancellationToken.None);
        Assert.Contains(oldDiff.Lines, line => line.Text == "original");
    }

    [Fact]
    public async Task FailedFetchAndCancelledPublishPreserveTheCompletedSnapshot()
    {
        using var fixture = await GitFixture.CreateAsync();
        await fixture.WriteAsync("base.txt", "base\n");
        await fixture.CommitAsync("Base");
        var repository = await fixture.ConnectAsync();
        var previous = await fixture.RefreshAsync(repository);
        var emptyRemote = Path.Combine(fixture.Root, "empty-remote");
        Directory.CreateDirectory(emptyRemote);
        await Assert.ThrowsAsync<GitCommandException>(() => fixture.RefreshAsync(repository with { RemoteUrl = emptyRemote }));
        var afterFailure = await fixture.Service.GetCachedSnapshotAsync(repository, "main", CancellationToken.None);
        Assert.Equal(previous.TipOid, afterFailure!.TipOid);
        Assert.Equal(previous.RefreshedAt, afterFailure.RefreshedAt);
        await fixture.WriteAsync("next.txt", "next\n");
        await fixture.CommitAsync("New work");
        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress<OperationProgress>(value => { if (value.Stage == "Saving") cancellation.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service.RefreshAsync(repository, "main", progress, cancellation.Token));
        using var reopened = fixture.CreateService();
        var offline = await reopened.GetCachedSnapshotAsync(repository, "main", CancellationToken.None);
        Assert.Equal(previous.TipOid, offline!.TipOid);
        Assert.Equal(previous.RefreshedAt, offline.RefreshedAt);
        Assert.Equal(1, fixture.CountIndexedCommits());
    }

    [Fact]
    public async Task SubmodulesAndLfsPointersRemainLocalRepositoryEntries()
    {
        using var fixture = await GitFixture.CreateAsync();
        await fixture.WriteAsync("base.txt", "base\n");
        var first = await fixture.CommitAsync("Base");
        await fixture.WriteAsync("large.lfs", "version https://git-lfs.github.com/spec/v1\noid sha256:" + new string('a', 64) + "\nsize 99999999\n");
        await fixture.RunAsync("add", "large.lfs");
        await fixture.RunAsync("update-index", "--add", "--cacheinfo", $"160000,{first},vendor/module");
        await fixture.RunAsync("commit", "-m", "Add external pointers");
        var repository = await fixture.ConnectAsync();
        var snapshot = await fixture.RefreshAsync(repository);
        Assert.Contains(snapshot.Files, file => file.Path == "vendor/module" && file.Mode == "160000");
        var moduleDiff = await fixture.Service.GetDiffAsync(repository, snapshot.Changes.First(change => change.Path == "vendor/module"), CancellationToken.None);
        Assert.Contains(moduleDiff.Lines, line => line.Text.StartsWith("Submodule pointer:", StringComparison.Ordinal));
        var lfsDiff = await fixture.Service.GetDiffAsync(repository, snapshot.Changes.First(change => change.Path == "large.lfs"), CancellationToken.None);
        Assert.Contains(lfsDiff.Lines, line => line.Text == "size 99999999");
    }

    [Fact]
    public async Task UserGitFormattingDoesNotCorruptMetadataOrDiffLineNumbers()
    {
        using var fixture = await GitFixture.CreateAsync();
        await fixture.WriteAsync("format.txt", "one\n\nthree\n");
        await fixture.CommitAsync("Base");
        var repository = await fixture.ConnectAsync();
        await fixture.RefreshAsync(repository);
        var bareDirectory = Path.Combine(fixture.Data, "repositories", repository.Id + ".git");
        await fixture.RunAsync("--git-dir", bareDirectory, "config", "i18n.logOutputEncoding", "iso-8859-1");
        await fixture.RunAsync("--git-dir", bareDirectory, "config", "diff.suppressBlankEmpty", "true");
        await fixture.RunAsync("--git-dir", bareDirectory, "config", "diff.outputIndicatorNew", "!");
        await fixture.WriteAsync("format.txt", "one\n\nchanged\n");
        await fixture.CommitAsync("Résumé with configured Git output");
        var snapshot = await fixture.RefreshAsync(repository);
        Assert.Equal("Résumé with configured Git output", snapshot.Commits[0].Subject);
        var patch = await fixture.Service.GetDiffAsync(repository, snapshot.Changes[0], CancellationToken.None);
        Assert.Contains(patch.Lines, line => line is { Kind: DiffLineKind.Context, OldLine: 2, NewLine: 2, Text: "" });
        Assert.Contains(patch.Lines, line => line is { Kind: DiffLineKind.Added, NewLine: 3, Text: "changed" });
    }

    [Fact]
    public async Task RemovingRepositoryClearsSavedSnapshotsAndAllowsReconnect()
    {
        using var fixture = await GitFixture.CreateAsync();
        await fixture.WriteAsync("base.txt", "base\n");
        await fixture.CommitAsync("Base");
        var repository = await fixture.ConnectAsync();
        await fixture.RefreshAsync(repository);
        await fixture.Service.RemoveAsync(repository, CancellationToken.None);
        Assert.Empty(await fixture.Service.GetRepositoriesAsync());
        Assert.Null(await fixture.Service.GetCachedSnapshotAsync(repository, "main", CancellationToken.None));
        var reconnected = await fixture.ConnectAsync();
        Assert.Single((await fixture.RefreshAsync(reconnected)).Commits);
    }

    [Fact]
    public async Task PatchLimitsBothBytesAndLines()
    {
        var patch = "@@ -0,0 +1,30000 @@\n" + string.Concat(Enumerable.Repeat("+new line\n", 30000));
        var linesResult = GitOutputParser.ParsePatch(patch, "large patch");
        Assert.True(linesResult.IsTruncated);
        Assert.Equal(20001, linesResult.Lines.Count);
        Assert.Equal(DiffLineKind.Notice, linesResult.Lines[^1].Kind);
        await using var bytes = new MemoryStream(Encoding.UTF8.GetBytes("@@ -0,0 +1 @@\n+" + new string('x', 3 * 1024 * 1024) + "\n"));
        var bytesResult = await GitOutputParser.ReadPatchAsync(bytes, "large line", CancellationToken.None);
        Assert.True(bytesResult.IsTruncated);
        Assert.Equal(bytes.Length, bytes.Position);
        Assert.All(bytesResult.Lines, line => Assert.True(line.Text.Length < GitOutputParser.MaximumPatchBytes));
    }

    [Fact]
    public async Task SettingsRoundTripAndRecoverFromMalformedJson()
    {
        using var fixture = await GitFixture.CreateAsync();
        using var store = new JsonUserSettingsStore(fixture.Data);
        Assert.Equal(new UserSettings(), await store.LoadAsync());
        var settings = new UserSettings("selected", "feature/ui", "Light", "AllFiles");
        await store.SaveAsync(settings);
        Assert.Equal(settings, await store.LoadAsync());
        await File.WriteAllTextAsync(Path.Combine(fixture.Data, "settings.json"), "{ broken }");
        Assert.Equal(new UserSettings(), await store.LoadAsync());
    }

    internal sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}

internal sealed class GitFixture : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "GitHistory-tests", Guid.NewGuid().ToString("N"));
    public string Remote => Path.Combine(Root, "remote");
    public string Data => Path.Combine(Root, "cache");
    public GitRepositoryService Service { get; private set; } = null!;

    public static async Task<GitFixture> CreateAsync(string objectFormat = "sha1")
    {
        var fixture = new GitFixture();
        Directory.CreateDirectory(fixture.Remote);
        await fixture.RunAsync("init", "--initial-branch=main", "--object-format=" + objectFormat);
        await fixture.RunAsync("config", "user.name", "Fixture Author");
        await fixture.RunAsync("config", "user.email", "fixture@example.test");
        await fixture.RunAsync("config", "commit.gpgsign", "false");
        await fixture.RunAsync("config", "core.autocrlf", "false");
        await fixture.RunAsync("config", "core.protectNTFS", "false");
        fixture.Service = fixture.CreateService();
        return fixture;
    }

    public GitRepositoryService CreateService() => new(Data, NullLogger<GitRepositoryService>.Instance, allowLocalRemotes: true);
    public Task<RepositoryInfo> ConnectAsync() => Service.ConnectAsync(Remote, null, CancellationToken.None);
    public Task<BranchSnapshot> RefreshAsync(RepositoryInfo repository) => Service.RefreshAsync(repository, "main", null, CancellationToken.None);

    public Task WriteAsync(string path, string content)
    {
        var fullPath = Path.Combine(Remote, path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        return File.WriteAllTextAsync(fullPath, content, new UTF8Encoding(false));
    }

    public async Task<string> CommitAsync(string subject, string? date = null)
    {
        await RunAsync("add", "--all");
        await RunAtAsync(date, "commit", "-m", subject);
        return (await RunAsync("rev-parse", "HEAD")).Trim();
    }

    public Task<string> RunAsync(params string[] arguments) => RunAtAsync(null, arguments);

    public async Task<string> RunAtAsync(string? date, params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = Remote,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        if (date is not null)
        {
            start.Environment["GIT_AUTHOR_DATE"] = date;
            start.Environment["GIT_COMMITTER_DATE"] = date;
        }
        using var process = Process.Start(start)!;
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await outputTask;
        var error = await errorTask;
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {error}");
        return output;
    }

    public int CountIndexedCommits()
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(Data, "history.db")};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM commits";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    public void Dispose()
    {
        Service?.Dispose();
        var allowedRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "GitHistory-tests")) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(Root);
        if (target.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase) && Directory.Exists(target))
        {
            foreach (var file in Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(target, recursive: true);
        }
    }
}
