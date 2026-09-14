using System.Threading.Channels;
using CommunityToolkit.Mvvm.Messaging;
using GitHistory.Core.Models;
using GitHistory.Core.Services;
using GitHistory.Core.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace GitHistory.Tests.ViewModels;

public sealed class BrowserAndDetailsTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Browser_discards_a_slow_old_query_after_a_new_filter_has_been_applied()
    {
        var messenger = new StrongReferenceMessenger();
        var dispatcher = new QueuedDispatcher();
        using var browser = new BrowserViewModel(new HistoryQueryService(), messenger, dispatcher, new FixedClock(), NullLogger<BrowserViewModel>.Instance);
        var snapshot = DemoData.CreateSnapshot(now: Now);

        messenger.Send(new SnapshotChanged(DemoData.Repository, snapshot));
        var oldResult = await dispatcher.NextAsync();
        browser.Search = "HistoryQueryService.cs";
        var newerResult = await dispatcher.NextAsync();
        newerResult.Execute();
        Assert.Single(browser.Files);
        Assert.Equal("src/GitHistory.Core/Services/HistoryQueryService.cs", browser.Files[0].Path);

        oldResult.Execute();
        Assert.Single(browser.Files);
        Assert.Equal("src/GitHistory.Core/Services/HistoryQueryService.cs", browser.SelectedFile?.Path);
    }

    [Fact]
    public async Task Browser_discards_pending_results_after_switching_repository()
    {
        var messenger = new StrongReferenceMessenger();
        var dispatcher = new QueuedDispatcher();
        using var browser = new BrowserViewModel(new HistoryQueryService(), messenger, dispatcher, new FixedClock(), NullLogger<BrowserViewModel>.Instance);
        var first = DemoData.CreateSnapshot(now: Now);
        var second = DemoData.CreateSnapshot("develop", Now);

        messenger.Send(new SnapshotChanged(DemoData.Repository, first));
        var oldResult = await dispatcher.NextAsync();
        messenger.Send(new SnapshotChanged(DemoData.Repository, second));
        var newerResult = await dispatcher.NextAsync();
        newerResult.Execute();
        Assert.Contains(browser.Files, row => row.Path.EndsWith("BranchInsights.xaml", StringComparison.Ordinal));

        oldResult.Execute();
        Assert.Contains(browser.Files, row => row.Path.EndsWith("BranchInsights.xaml", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Browser_shutdown_waits_for_all_queued_results_and_prevents_late_state_changes()
    {
        var messenger = new StrongReferenceMessenger();
        var dispatcher = new QueuedDispatcher();
        using var browser = new BrowserViewModel(new HistoryQueryService(), messenger, dispatcher, new FixedClock(), NullLogger<BrowserViewModel>.Instance);
        messenger.Send(new SnapshotChanged(DemoData.Repository, DemoData.CreateSnapshot(now: Now)));
        var first = await dispatcher.NextAsync();
        browser.Search = "HistoryQueryService.cs";
        var second = await dispatcher.NextAsync();
        Task shutdown = browser.ShutdownAsync();
        Assert.False(shutdown.IsCompleted);
        second.Execute();
        Assert.False(shutdown.IsCompleted);
        first.Execute();
        await shutdown;

        Assert.Empty(browser.Files);
        Assert.Empty(browser.Activity);
        Assert.Equal(0, browser.CommitCount);
        await browser.ApplyFilterCommand.ExecuteAsync(null);
        await browser.ShutdownAsync();
        browser.Dispose();
    }

    [Fact]
    public async Task Browser_shutdown_awaits_running_queries_without_logging_their_late_failures()
    {
        var messenger = new StrongReferenceMessenger();
        using var queries = new FailingQuery();
        var logger = new CountingLogger();
        using var browser = new BrowserViewModel(queries, messenger, new ImmediateDispatcher(), new FixedClock(), logger);
        messenger.Send(new SnapshotChanged(DemoData.Repository, DemoData.CreateSnapshot(now: Now)));
        await queries.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task shutdown = browser.ShutdownAsync();
        Assert.False(shutdown.IsCompleted);
        queries.Release.Set();
        await shutdown;

        Assert.Equal(0, logger.Count);
        Assert.Empty(browser.Files);
    }

    [Fact]
    public async Task Clearing_a_snapshot_invalidates_queued_rows_and_aggregate_counts()
    {
        var messenger = new StrongReferenceMessenger();
        var dispatcher = new QueuedDispatcher();
        using var browser = new BrowserViewModel(new HistoryQueryService(), messenger, dispatcher, new FixedClock(), NullLogger<BrowserViewModel>.Instance);
        messenger.Send(new SnapshotChanged(DemoData.Repository, DemoData.CreateSnapshot(now: Now)));
        var pending = await dispatcher.NextAsync();
        messenger.Send(new SnapshotChanged(DemoData.Repository, null));
        pending.Execute();
        await browser.ShutdownAsync();

        Assert.Empty(browser.Files);
        Assert.Empty(browser.Activity);
        Assert.Null(browser.SelectedFile);
        Assert.Equal(0, browser.CommitCount);
        Assert.Equal(0, browser.ContributorCount);
    }

    [Fact]
    public async Task Activity_keeps_full_date_context_while_counts_and_day_selection_use_the_selected_window()
    {
        var messenger = new StrongReferenceMessenger();
        var queries = new HistoryQueryService();
        using var browser = new BrowserViewModel(queries, messenger, new ImmediateDispatcher(), new FixedClock(), NullLogger<BrowserViewModel>.Instance);
        var snapshot = DemoData.CreateSnapshot(now: Now);
        messenger.Send(new SnapshotChanged(DemoData.Repository, snapshot));
        await browser.ApplyFilterCommand.ExecuteAsync(null);
        int overviewCount = browser.Activity.Count;
        Assert.True(browser.CommitCount < browser.Activity.Sum(point => point.Commits));
        Assert.Equal(queries.GetActivity(snapshot, new HistoryFilter(FileViewMode.RecentChanges, Now.AddDays(-7), Now.AddTicks(1))).Sum(point => point.Commits), browser.CommitCount);

        browser.Mode = FileViewMode.AllFiles;
        await browser.ApplyFilterCommand.ExecuteAsync(null);
        var oldest = browser.Activity[0];
        browser.SelectedActivity = oldest;
        await browser.ApplyFilterCommand.ExecuteAsync(null);
        Assert.Equal(FileViewMode.RecentChanges, browser.Mode);
        Assert.Equal("Custom", browser.DatePreset);
        Assert.NotEmpty(browser.Files);
        Assert.All(browser.Files, row => Assert.Equal(oldest.Date, row.LatestChange!.Commit.LocalDate.Date));
        Assert.Equal(overviewCount, browser.Activity.Count);
        await browser.ShutdownAsync();
    }

    [Fact]
    public async Task Browser_counts_all_matching_contributors_to_a_file_and_folder_ids_do_not_collide()
    {
        var messenger = new StrongReferenceMessenger();
        using var browser = new BrowserViewModel(new HistoryQueryService(), messenger, new ImmediateDispatcher(), new FixedClock(), NullLogger<BrowserViewModel>.Instance);
        var template = DemoData.CreateSnapshot(now: Now);
        var latest = template.Changes[0] with
        {
            Path = "$root/shared.cs",
            Commit = template.Commits[0] with { AuthorName = "Bob", AuthorEmail = "bob@example.com" }
        };
        var older = latest with
        {
            Kind = ChangeKind.Added,
            Commit = latest.Commit with { Oid = "older", AuthorName = "Alice", AuthorEmail = "alice@example.com", CommittedAt = Now.AddDays(-1) }
        };
        var snapshot = template with
        {
            Commits = [latest.Commit, older.Commit], Changes = [latest, older],
            Files = [new("$root/shared.cs", "one", "100644"), new("$root/nested/other.cs", "two", "100644"), new("root/other.cs", "three", "100644")]
        };
        messenger.Send(new SnapshotChanged(DemoData.Repository, snapshot));
        await browser.ApplyFilterCommand.ExecuteAsync(null);

        Assert.Single(browser.Files);
        Assert.Equal(2, browser.ContributorCount);
        Assert.Equal(browser.TreeNodes.Count, browser.TreeNodes.Select(node => node.Id).Distinct(StringComparer.Ordinal).Count());
        var root = browser.TreeNodes.Single(node => node.Path == "");
        var folder = browser.TreeNodes.Single(node => node.Path == "$root");
        var nested = browser.TreeNodes.Single(node => node.Path == "$root/nested");
        Assert.NotEqual(root.Id, folder.Id);
        Assert.Equal(root.Id, folder.ParentId);
        Assert.Equal(folder.Id, nested.ParentId);
        await browser.ShutdownAsync();
    }

    [Fact]
    public async Task Invalid_custom_dates_clear_selection_and_counts_as_well_as_rows()
    {
        var messenger = new StrongReferenceMessenger();
        using var browser = new BrowserViewModel(new HistoryQueryService(), messenger, new ImmediateDispatcher(), new FixedClock(), NullLogger<BrowserViewModel>.Instance);
        messenger.Send(new SnapshotChanged(DemoData.Repository, DemoData.CreateSnapshot(now: Now)));
        await browser.ApplyFilterCommand.ExecuteAsync(null);
        Assert.NotNull(browser.SelectedFile);
        Assert.True(browser.CommitCount > 0);

        browser.FromDate = Now.LocalDateTime.Date.AddDays(2);
        await browser.ApplyFilterCommand.ExecuteAsync(null);

        Assert.NotEmpty(browser.QueryError);
        Assert.Empty(browser.Files);
        Assert.Empty(browser.Activity);
        Assert.Null(browser.SelectedFile);
        Assert.Equal(0, browser.CommitCount);
        Assert.Equal(0, browser.ContributorCount);
    }

    [Fact]
    public async Task All_files_includes_old_files_while_recent_changes_uses_the_default_week()
    {
        var messenger = new StrongReferenceMessenger();
        using var browser = new BrowserViewModel(new HistoryQueryService(), messenger, new ImmediateDispatcher(), new FixedClock(), NullLogger<BrowserViewModel>.Instance);
        var snapshot = DemoData.CreateSnapshot(now: Now);
        messenger.Send(new SnapshotChanged(DemoData.Repository, snapshot));
        await browser.ApplyFilterCommand.ExecuteAsync(null);
        Assert.All(browser.Files, row => Assert.True(row.LatestChange!.Commit.CommittedAt >= Now.AddDays(-7)));

        browser.Mode = FileViewMode.AllFiles;
        await browser.ApplyFilterCommand.ExecuteAsync(null);
        Assert.Equal(snapshot.Files.Count, browser.Files.Count);
        Assert.Contains(browser.Files, row => row.LatestChange!.Commit.CommittedAt < Now.AddDays(-7));
    }

    [Fact]
    public async Task Details_can_load_again_after_an_inflight_diff_is_canceled_by_clearing_selection()
    {
        var messenger = new StrongReferenceMessenger();
        var repositories = new DiffRepository();
        using var details = CreateDetails(repositories, messenger);
        var (snapshot, row) = Selection();
        messenger.Send(new FileSelected(DemoData.Repository, snapshot, row));

        var pending = new TaskCompletionSource<DiffResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken requestToken = default;
        repositories.Handler = (_, _, token) => { requestToken = token; return pending.Task; };
        Task oldLoad = details.LoadDiffCommand.ExecuteAsync(null);
        Assert.True(details.IsBusy);
        messenger.Send(new FileSelected(null, null, null));
        Assert.True(requestToken.IsCancellationRequested);
        pending.SetCanceled(requestToken);
        await oldLoad;

        repositories.Handler = (_, _, _) => Task.FromResult(Diff("recovered"));
        messenger.Send(new FileSelected(DemoData.Repository, snapshot, row));
        await details.LoadDiffCommand.ExecuteAsync(null);
        Assert.Equal("recovered", details.DiffSummary);
        Assert.False(details.IsBusy);
    }

    [Fact]
    public async Task Details_rejects_a_late_diff_even_if_the_repository_ignores_cancellation()
    {
        var messenger = new StrongReferenceMessenger();
        var repositories = new DiffRepository();
        using var details = CreateDetails(repositories, messenger);
        var (snapshot, row) = Selection();
        messenger.Send(new FileSelected(DemoData.Repository, snapshot, row));

        var pending = new TaskCompletionSource<DiffResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        repositories.Handler = (_, _, _) => pending.Task;
        Task oldLoad = details.LoadDiffCommand.ExecuteAsync(null);
        var other = new HistoryQueryService().Query(snapshot, new HistoryFilter(FileViewMode.RecentChanges, Now.AddDays(-7), null), Now)
            .First(candidate => candidate.Path != row.Path);
        repositories.Handler = (_, _, _) => Task.FromResult(Diff("new selection"));
        messenger.Send(new FileSelected(DemoData.Repository, snapshot, other));
        Assert.Equal("new selection", details.DiffSummary);

        pending.SetResult(Diff("stale result"));
        await oldLoad;
        Assert.Equal("new selection", details.DiffSummary);
        Assert.False(details.IsBusy);
    }

    [Fact]
    public async Task Details_shows_full_lineage_but_initially_selects_the_change_from_the_clicked_row()
    {
        var messenger = new StrongReferenceMessenger();
        var repositories = new DiffRepository { Handler = (_, change, _) => Task.FromResult(Diff(change.Commit.Oid)) };
        using var details = CreateDetails(repositories, messenger);
        var queries = new HistoryQueryService();
        var snapshot = DemoData.CreateSnapshot(now: Now);
        var latestRow = queries.Query(snapshot, new HistoryFilter(FileViewMode.AllFiles, null, null), Now)[0];
        var fullHistory = queries.GetFileHistory(snapshot, latestRow);
        Assert.True(fullHistory.Count > 1);
        var anchor = fullHistory[1];
        var historicalRow = latestRow with { Path = anchor.Path, LatestChange = anchor };

        messenger.Send(new FileSelected(DemoData.Repository, snapshot, historicalRow));

        Assert.Equal(fullHistory, details.History);
        Assert.Same(anchor, details.SelectedChange);
        Assert.Equal(anchor.Commit.Oid, details.DiffSummary);
        await details.ShutdownAsync();
    }

    [Fact]
    public async Task Clearing_only_the_selected_commit_cancels_and_clears_the_diff_immediately()
    {
        var messenger = new StrongReferenceMessenger();
        var repositories = new DiffRepository();
        using var details = CreateDetails(repositories, messenger);
        var (snapshot, row) = Selection();
        messenger.Send(new FileSelected(DemoData.Repository, snapshot, row));
        var pending = new TaskCompletionSource<DiffResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        repositories.Handler = (_, _, _) => pending.Task;
        Task loading = details.LoadDiffCommand.ExecuteAsync(null);
        Assert.True(details.IsBusy);
        details.SelectedChange = null;
        Assert.Empty(details.DiffLines);
        Assert.False(details.IsBusy);
        Assert.Contains("Select a commit", details.DiffSummary, StringComparison.Ordinal);
        pending.SetResult(Diff("obsolete"));
        await loading;
        Assert.Empty(details.DiffLines);
        Assert.Contains("Select a commit", details.DiffSummary, StringComparison.Ordinal);
        await details.ShutdownAsync();
    }

    [Fact]
    public async Task Details_preserves_raw_diff_lines_for_the_presentation_layer()
    {
        var messenger = new StrongReferenceMessenger();
        var raw = new DiffResult([
            new(null, null, "diff --git a/example.cs b/example.cs", DiffLineKind.Header),
            new(null, null, "index 123..456 100644", DiffLineKind.Header),
            new(null, null, "--- a/example.cs", DiffLineKind.Header),
            new(null, null, "+++ b/example.cs", DiffLineKind.Header),
            new(null, null, "@@ -1 +1 @@", DiffLineKind.Header),
            new(1, null, "old code", DiffLineKind.Deleted),
            new(null, 1, "+++ literal content", DiffLineKind.Added),
            new(null, null, "Truncated after 20,000 lines", DiffLineKind.Notice)
        ], "2 changes", IsTruncated: true);
        var repositories = new DiffRepository { Handler = (_, _, _) => Task.FromResult(raw) };
        using var details = CreateDetails(repositories, messenger);
        var (snapshot, row) = Selection();
        messenger.Send(new FileSelected(DemoData.Repository, snapshot, row));
        await details.LoadDiffCommand.ExecuteAsync(null);

        Assert.Same(raw.Lines, details.DiffLines);
        Assert.Equal(8, details.DiffLines.Count);
        Assert.Equal("@@ -1 +1 @@", details.DiffLines[4].Text);
        Assert.Contains(details.DiffLines, line => line.Text == "+++ literal content");
        Assert.Contains(details.DiffLines, line => line.Kind == DiffLineKind.Notice);
        Assert.Equal(8, raw.Lines.Count);
        await details.ShutdownAsync();
    }

    [Fact]
    public async Task Details_shutdown_waits_for_all_superseded_diffs_and_is_idempotent()
    {
        var messenger = new StrongReferenceMessenger();
        var repositories = new DiffRepository();
        using var details = CreateDetails(repositories, messenger);
        var (snapshot, row) = Selection();
        messenger.Send(new FileSelected(DemoData.Repository, snapshot, row));
        var first = new TaskCompletionSource<DiffResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<DiffResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tokens = new Dictionary<string, CancellationToken>();
        repositories.Handler = (_, change, token) =>
        {
            tokens[change.Path] = token;
            return change.Path == row.Path ? first.Task : second.Task;
        };
        Task old = details.LoadDiffCommand.ExecuteAsync(null);
        var other = new HistoryQueryService().Query(snapshot, new HistoryFilter(FileViewMode.RecentChanges, Now.AddDays(-7), null), Now)
            .First(candidate => candidate.Path != row.Path);
        messenger.Send(new FileSelected(DemoData.Repository, snapshot, other));
        Task shutdown = details.ShutdownAsync();
        Assert.True(tokens[row.Path].IsCancellationRequested);
        Assert.True(tokens[other.Path].IsCancellationRequested);
        Assert.False(shutdown.IsCompleted);
        second.SetResult(Diff("second"));
        Assert.False(shutdown.IsCompleted);
        first.SetResult(Diff("first"));
        await shutdown;
        await old;
        Assert.False(details.CopyPathCommand.CanExecute(null));
        await details.ShutdownAsync();
        details.Dispose();
    }

    [Fact]
    public async Task Diff_errors_are_recoverable_and_copy_commands_follow_selection()
    {
        var messenger = new StrongReferenceMessenger();
        var repositories = new DiffRepository();
        var clipboard = new Clipboard();
        using var details = new DetailsViewModel(repositories, new HistoryQueryService(), messenger, clipboard, NullLogger<DetailsViewModel>.Instance);
        Assert.False(details.CopyPathCommand.CanExecute(null));
        Assert.False(details.CopyShaCommand.CanExecute(null));
        var (snapshot, row) = Selection();
        repositories.Handler = (_, _, _) => Task.FromException<DiffResult>(new IOException("object is missing"));
        messenger.Send(new FileSelected(DemoData.Repository, snapshot, row));
        await details.LoadDiffCommand.ExecuteAsync(null);
        Assert.Contains("object is missing", details.DiffSummary, StringComparison.Ordinal);
        Assert.False(details.IsBusy);
        Assert.True(details.CopyPathCommand.CanExecute(null));
        details.CopyPathCommand.Execute(null);
        Assert.Equal(row.Path, clipboard.Text);
        details.CopyShaCommand.Execute(null);
        Assert.Equal(row.LatestChange!.Commit.Oid, clipboard.Text);

        repositories.Handler = (_, _, _) => Task.FromResult(Diff("retried successfully"));
        await details.LoadDiffCommand.ExecuteAsync(null);
        Assert.Equal("retried successfully", details.DiffSummary);
        messenger.Send(new FileSelected(null, null, null));
        Assert.Empty(details.DiffLines);
        Assert.False(details.CopyPathCommand.CanExecute(null));
        Assert.False(details.CopyShaCommand.CanExecute(null));
    }

    private static DetailsViewModel CreateDetails(DiffRepository repositories, IMessenger messenger) =>
        new(repositories, new HistoryQueryService(), messenger, new Clipboard(), NullLogger<DetailsViewModel>.Instance);

    private static (BranchSnapshot Snapshot, FileRow Row) Selection()
    {
        var snapshot = DemoData.CreateSnapshot(now: Now);
        var row = new HistoryQueryService().Query(snapshot, new HistoryFilter(FileViewMode.RecentChanges, Now.AddDays(-7), null), Now)[0];
        return (snapshot, row);
    }

    private static DiffResult Diff(string summary) => new([new(1, 1, summary, DiffLineKind.Context)], summary);

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class ImmediateDispatcher : IUiDispatcher
    {
        public Task InvokeAsync(Action action, CancellationToken cancellationToken = default) { action(); return Task.CompletedTask; }
    }

    private sealed class QueuedDispatcher : IUiDispatcher
    {
        private readonly Channel<DispatchRequest> requests = Channel.CreateUnbounded<DispatchRequest>();
        public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
        {
            var request = new DispatchRequest(action);
            requests.Writer.TryWrite(request);
            return request.Completion.Task;
        }
        public async Task<DispatchRequest> NextAsync() => await requests.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
    }

    private sealed class DispatchRequest(Action action)
    {
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Execute() { action(); Completion.SetResult(); }
    }

    private sealed class Clipboard : IClipboardService
    {
        public string? Text { get; private set; }
        public void SetText(string text) => Text = text;
    }

    private sealed class FailingQuery : IHistoryQueryService, IDisposable
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new();
        public IReadOnlyList<FileRow> Query(BranchSnapshot snapshot, HistoryFilter filter, DateTimeOffset now)
        {
            Started.TrySetResult();
            if (!Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("The test did not release its query.");
            throw new IOException("A query failed after shutdown began.");
        }
        public IReadOnlyList<FileChange> GetFileHistory(BranchSnapshot snapshot, FileRow row) => [];
        public IReadOnlyList<ActivityPoint> GetActivity(BranchSnapshot snapshot, HistoryFilter filter) => [];
        public int GetContributorCount(BranchSnapshot snapshot, HistoryFilter filter) => 0;
        public void Dispose() => Release.Dispose();
    }

    private sealed class CountingLogger : Microsoft.Extensions.Logging.ILogger<BrowserViewModel>
    {
        public int Count { get; private set; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Count++;
    }

    private sealed class DiffRepository : IRepositoryService
    {
        public Func<RepositoryInfo, FileChange, CancellationToken, Task<DiffResult>> Handler { get; set; } = (_, _, _) => Task.FromResult(Diff("initial"));
        public Task<DiffResult> GetDiffAsync(RepositoryInfo repository, FileChange change, CancellationToken cancellationToken) => Handler(repository, change, cancellationToken);
        public Task<IReadOnlyList<RepositoryInfo>> GetRepositoriesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<RepositoryInfo> ConnectAsync(string remoteUrl, IProgress<OperationProgress>? progress, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BranchSnapshot?> GetCachedSnapshotAsync(RepositoryInfo repository, string branch, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BranchSnapshot> RefreshAsync(RepositoryInfo repository, string branch, IProgress<OperationProgress>? progress, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RemoveAsync(RepositoryInfo repository, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
