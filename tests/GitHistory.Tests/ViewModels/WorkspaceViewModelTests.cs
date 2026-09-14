using CommunityToolkit.Mvvm.Messaging;
using GitHistory.Core.Models;
using GitHistory.Core.Services;
using GitHistory.Core.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace GitHistory.Tests.ViewModels;

public sealed class WorkspaceViewModelTests
{
    private static readonly RepositoryInfo First = new("first", "First", "https://example.com/first.git", "main", ["main"]);
    private static readonly RepositoryInfo Second = new("second", "Second", "https://example.com/second.git", "main", ["main"]);

    [Fact]
    public async Task Canceled_refresh_keeps_the_completed_cached_snapshot_visible()
    {
        var repositories = new FakeRepositories();
        var messenger = new StrongReferenceMessenger();
        var seen = new SnapshotObserver(messenger);
        using var workspace = Create(repositories, messenger);
        await workspace.InitializeAsync(new UserSettings());

        var pending = new TaskCompletionSource<BranchSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken token = default;
        repositories.Refresh = (_, _, _, cancellationToken) => { token = cancellationToken; return pending.Task; };
        Task refresh = workspace.RefreshCommand.ExecuteAsync(null);
        Assert.Same(repositories.Cached(First, "main"), seen.Last?.Snapshot);
        workspace.CancelCommand.Execute(null);
        Assert.True(token.IsCancellationRequested);
        pending.SetCanceled(token);
        await refresh;

        Assert.Contains("showing cached history", workspace.Status, StringComparison.Ordinal);
        Assert.StartsWith("Cached", workspace.LastUpdated, StringComparison.Ordinal);
        Assert.NotNull(seen.Last?.Snapshot);
        Assert.False(workspace.IsBusy);
        Assert.True(workspace.RefreshCommand.CanExecute(null));
        await workspace.ShutdownAsync();
    }

    [Fact]
    public async Task Offline_refresh_failure_preserves_cached_history_and_reports_recoverable_error()
    {
        var repositories = new FakeRepositories
        {
            Refresh = (_, _, _, _) => Task.FromException<BranchSnapshot>(new IOException("network is unavailable"))
        };
        var messenger = new StrongReferenceMessenger();
        var seen = new SnapshotObserver(messenger);
        using var workspace = Create(repositories, messenger);
        await workspace.InitializeAsync(new UserSettings());

        Assert.NotNull(seen.Last?.Snapshot);
        Assert.Equal("network is unavailable", workspace.Error);
        Assert.Contains("showing cached history", workspace.Status, StringComparison.Ordinal);
        Assert.False(workspace.IsBusy);

        repositories.Refresh = (repository, branch, _, _) => Task.FromResult(FakeRepositories.Fresh(repository, branch));
        await workspace.RefreshCommand.ExecuteAsync(null);
        Assert.Empty(workspace.Error);
        Assert.Equal("Up to date", workspace.Status);
        await workspace.ShutdownAsync();
    }

    [Fact]
    public async Task Superseded_repository_refresh_cannot_replace_the_new_selection()
    {
        var repositories = new FakeRepositories();
        var messenger = new StrongReferenceMessenger();
        var seen = new SnapshotObserver(messenger);
        using var workspace = Create(repositories, messenger);
        await workspace.InitializeAsync(new UserSettings());

        var pending = new TaskCompletionSource<BranchSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        repositories.Refresh = (repository, branch, _, _) => repository.Id == First.Id
            ? pending.Task : Task.FromResult(FakeRepositories.Fresh(repository, branch));
        Task old = workspace.RefreshCommand.ExecuteAsync(null);
        workspace.SelectedRepository = Second;
        Assert.Equal(Second.Id, seen.Last?.Repository.Id);
        pending.SetResult(FakeRepositories.Fresh(First, "main"));
        await old;

        Assert.Equal(Second.Id, workspace.SelectedRepository?.Id);
        Assert.Equal(Second.Id, seen.Last?.Snapshot?.RepositoryId);
        Assert.Equal("Up to date", workspace.Status);
        await workspace.ShutdownAsync();
    }

    [Fact]
    public async Task Shutdown_waits_for_every_superseded_operation_even_when_cancellation_is_ignored()
    {
        var repositories = new FakeRepositories();
        var messenger = new StrongReferenceMessenger();
        using var workspace = Create(repositories, messenger);
        await workspace.InitializeAsync(new UserSettings());

        var first = new TaskCompletionSource<BranchSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<BranchSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tokens = new Dictionary<string, CancellationToken>();
        repositories.Refresh = (repository, _, _, token) =>
        {
            tokens[repository.Id] = token;
            return repository.Id == First.Id ? first.Task : second.Task;
        };
        Task old = workspace.RefreshCommand.ExecuteAsync(null);
        workspace.SelectedRepository = Second;
        Task shutdown = workspace.ShutdownAsync();
        Assert.True(tokens[First.Id].IsCancellationRequested);
        Assert.True(tokens[Second.Id].IsCancellationRequested);
        Assert.False(shutdown.IsCompleted);
        second.SetResult(FakeRepositories.Fresh(Second, "main"));
        Assert.False(shutdown.IsCompleted);
        first.SetResult(FakeRepositories.Fresh(First, "main"));
        await shutdown;
        await old;

        Assert.False(workspace.RefreshCommand.CanExecute(null));
        int calls = repositories.RefreshCalls;
        await workspace.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(calls, repositories.RefreshCalls);
        workspace.Dispose();
        await workspace.ShutdownAsync();
    }

    [Fact]
    public async Task Shutdown_also_waits_for_initial_repository_loading()
    {
        var saved = new TaskCompletionSource<IReadOnlyList<RepositoryInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var repositories = new FakeRepositories { List = _ => saved.Task };
        using var workspace = Create(repositories, new StrongReferenceMessenger());
        Task initialize = workspace.InitializeAsync(new UserSettings());
        Task shutdown = workspace.ShutdownAsync();
        Assert.False(shutdown.IsCompleted);
        saved.SetResult([First]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => initialize);
        await shutdown;
        Assert.Equal(0, repositories.RefreshCalls);
    }

    [Fact]
    public async Task A_superseded_connection_does_not_change_selection_or_clear_the_connect_form()
    {
        var repositories = new FakeRepositories();
        using var workspace = Create(repositories, new StrongReferenceMessenger());
        await workspace.InitializeAsync(new UserSettings());
        var pending = new TaskCompletionSource<RepositoryInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        repositories.Connect = (_, _, _) => pending.Task;
        workspace.RemoteUrl = "https://example.com/third.git";
        workspace.IsConnectOpen = true;
        Task connect = workspace.ConnectCommand.ExecuteAsync(null);
        workspace.SelectedRepository = Second;
        pending.SetResult(new RepositoryInfo("third", "Third", workspace.RemoteUrl, "main", ["main"]));
        await connect;

        Assert.Equal(Second.Id, workspace.SelectedRepository?.Id);
        Assert.Equal("https://example.com/third.git", workspace.RemoteUrl);
        Assert.True(workspace.IsConnectOpen);
        Assert.Equal("Up to date", workspace.Status);
        Assert.Empty(workspace.Error);
        await workspace.ShutdownAsync();
    }

    [Fact]
    public async Task Closing_the_connect_form_cancels_its_connection_but_keeps_regular_refresh_running()
    {
        var repositories = new FakeRepositories();
        using var workspace = Create(repositories, new StrongReferenceMessenger());
        await workspace.InitializeAsync(new UserSettings());
        var refreshPending = new TaskCompletionSource<BranchSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken refreshToken = default;
        repositories.Refresh = (_, _, _, token) => { refreshToken = token; return refreshPending.Task; };
        Task refresh = workspace.RefreshCommand.ExecuteAsync(null);
        workspace.CloseConnectCommand.Execute(null);
        workspace.OpenConnectCommand.Execute(null);
        workspace.CloseConnectCommand.Execute(null);
        Assert.False(refreshToken.IsCancellationRequested);
        workspace.CancelCommand.Execute(null);
        refreshPending.SetCanceled(refreshToken);
        await refresh;

        var connectPending = new TaskCompletionSource<RepositoryInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken connectToken = default;
        repositories.Connect = (_, _, token) => { connectToken = token; return connectPending.Task; };
        workspace.RemoteUrl = "https://example.com/new.git";
        workspace.OpenConnectCommand.Execute(null);
        Task connect = workspace.ConnectCommand.ExecuteAsync(null);
        workspace.CloseConnectCommand.Execute(null);
        Assert.True(connectToken.IsCancellationRequested);
        Assert.False(workspace.IsConnectOpen);
        connectPending.SetCanceled(connectToken);
        await connect;
        await workspace.ShutdownAsync();
    }

    [Fact]
    public async Task Successful_refresh_rediscovers_branches_without_reloading_or_changing_active_branch()
    {
        var repositories = new FakeRepositories();
        repositories.Refresh = (repository, branch, _, _) =>
        {
            repositories.Saved = [First with { Branches = ["main", "develop", "feature/new"] }, Second];
            return Task.FromResult(FakeRepositories.Fresh(repository, branch));
        };
        using var workspace = Create(repositories, new StrongReferenceMessenger());
        await workspace.InitializeAsync(new UserSettings());

        Assert.Equal("main", workspace.SelectedBranch);
        Assert.Equal(["main", "develop", "feature/new"], workspace.Branches);
        Assert.Equal(1, repositories.RefreshCalls);
        await workspace.ShutdownAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Navigation_selection_feedback_during_catalog_refresh_cannot_reopen_previous_workspace(bool clearsSelection)
    {
        var repositories = new FakeRepositories();
        var messenger = new StrongReferenceMessenger();
        var seen = new SnapshotObserver(messenger);
        using var workspace = Create(repositories, messenger);
        await workspace.InitializeAsync(new UserSettings());
        int callsBeforeSwitch = repositories.RefreshCalls;
        repositories.Saved = [First with { Name = "First updated" }, Second];
        bool navigationReset = false;
        workspace.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(workspace.Repositories)) return;
            navigationReset = true;
            // Emulate a navigation control coercing SelectedItem when ItemsSource changes.
            workspace.SelectedRepository = clearsSelection ? null : First;
        };

        workspace.SelectRepositoryCommand.Execute(Second);

        Assert.True(navigationReset);
        Assert.Equal(Second.Id, workspace.SelectedRepository?.Id);
        Assert.Equal(Second.Id, seen.Last?.Repository.Id);
        Assert.Equal(Second.Id, seen.Last?.Snapshot?.RepositoryId);
        Assert.Equal(callsBeforeSwitch + 1, repositories.RefreshCalls);
        Assert.False(workspace.IsBusy);
        await workspace.ShutdownAsync();
    }

    [Fact]
    public async Task Equivalent_catalog_records_reuse_navigation_and_selected_object_identity()
    {
        var repositories = new FakeRepositories
        {
            List = _ => Task.FromResult<IReadOnlyList<RepositoryInfo>>([
                First with { Branches = ["main"] }, Second with { Branches = ["main"] }])
        };
        using var workspace = Create(repositories, new StrongReferenceMessenger());
        await workspace.InitializeAsync(new UserSettings());
        var original = workspace.SelectedRepository;
        var catalog = workspace.Repositories;
        var visible = workspace.VisibleRepositories;

        await workspace.RefreshCommand.ExecuteAsync(null);

        Assert.Same(original, workspace.SelectedRepository);
        Assert.Same(catalog, workspace.Repositories);
        Assert.Same(visible, workspace.VisibleRepositories);
        await workspace.ShutdownAsync();
    }

    [Fact]
    public async Task Search_and_pin_filters_keep_the_active_workspace_and_do_not_fetch()
    {
        var repositories = new FakeRepositories();
        using var workspace = Create(repositories, new StrongReferenceMessenger());
        await workspace.InitializeAsync(new UserSettings(SelectedRepositoryId: Second.Id));
        int calls = repositories.RefreshCalls;
        workspace.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(workspace.VisibleRepositories)) workspace.SelectedRepository = First;
        };

        workspace.RepositorySearch = "first";
        Assert.Equal([First.Id], workspace.VisibleRepositories.Select(repository => repository.Id));
        Assert.Equal(Second.Id, workspace.SelectedRepository?.Id);
        workspace.RepositorySearch = "no match";
        Assert.False(workspace.HasVisibleRepositories);
        workspace.SelectedRepository = null;
        Assert.Equal(Second.Id, workspace.SelectedRepository?.Id);
        workspace.RepositorySearch = "EXAMPLE.com";
        Assert.Equal(2, workspace.VisibleRepositories.Count);

        workspace.TogglePinCommand.Execute(null);
        Assert.True(workspace.SelectedIsPinned);
        Assert.Equal([Second.Id], workspace.PinnedRepositoryIds);
        Assert.Equal(Second.Id, workspace.VisibleRepositories[0].Id);
        workspace.ShowOnlyPinned = true;
        Assert.Equal([Second.Id], workspace.VisibleRepositories.Select(repository => repository.Id));
        workspace.TogglePinCommand.Execute(Second);
        Assert.Empty(workspace.VisibleRepositories);
        Assert.False(workspace.SelectedIsPinned);
        Assert.Equal(Second.Id, workspace.SelectedRepository?.Id);
        Assert.Equal(calls, repositories.RefreshCalls);
        await workspace.ShutdownAsync();
    }

    [Fact]
    public async Task Saved_pin_preferences_are_restored_without_forcing_a_pinned_workspace()
    {
        using var workspace = Create(new FakeRepositories(), new StrongReferenceMessenger());
        await workspace.InitializeAsync(new UserSettings(SelectedRepositoryId: First.Id,
            PinnedRepositoryIds: [Second.Id, Second.Id], ShowOnlyPinned: true));

        Assert.Equal([Second.Id], workspace.PinnedRepositoryIds);
        Assert.Equal([Second.Id], workspace.VisibleRepositories.Select(repository => repository.Id));
        Assert.Equal(First.Id, workspace.SelectedRepository?.Id);
        Assert.False(workspace.SelectedIsPinned);
        await workspace.ShutdownAsync();
    }

    [Fact]
    public async Task Disabling_refresh_on_open_uses_cached_history_but_manual_refresh_still_fetches()
    {
        var repositories = new FakeRepositories();
        var messenger = new StrongReferenceMessenger();
        var seen = new SnapshotObserver(messenger);
        using var workspace = Create(repositories, messenger);
        await workspace.InitializeAsync(new UserSettings(RefreshOnOpen: false));

        Assert.Equal(0, repositories.RefreshCalls);
        Assert.Equal("cached", seen.Last?.Snapshot?.TipOid);
        Assert.Contains("refresh on open is off", workspace.Status, StringComparison.Ordinal);
        workspace.SelectedRepository = Second;
        Assert.Equal(0, repositories.RefreshCalls);
        Assert.Equal(Second.Id, seen.Last?.Snapshot?.RepositoryId);
        await workspace.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(1, repositories.RefreshCalls);
        Assert.Equal("Up to date", workspace.Status);
        await workspace.ShutdownAsync();
    }

    [Fact]
    public async Task Disabling_refresh_on_open_still_fetches_a_repository_without_a_completed_cache()
    {
        var repositories = new FakeRepositories { Cache = (_, _, _) => Task.FromResult<BranchSnapshot?>(null) };
        using var workspace = Create(repositories, new StrongReferenceMessenger());

        await workspace.InitializeAsync(new UserSettings(RefreshOnOpen: false));

        Assert.Equal(1, repositories.RefreshCalls);
        Assert.Equal("Up to date", workspace.Status);
        await workspace.ShutdownAsync();
    }

    [Fact]
    public async Task Reloading_imported_workspaces_preserves_active_branch_and_does_not_refetch()
    {
        var repositories = new FakeRepositories();
        using var workspace = Create(repositories, new StrongReferenceMessenger());
        await workspace.InitializeAsync(new UserSettings(SelectedRepositoryId: Second.Id));
        int calls = repositories.RefreshCalls;
        var selected = workspace.SelectedRepository;
        repositories.Saved = [First, Second, new RepositoryInfo("third", "Imported", "https://example.com/imported.git", "main", ["main"])];

        await workspace.ReloadRepositoriesAsync();

        Assert.Equal(3, workspace.Repositories.Count);
        Assert.Same(selected, workspace.SelectedRepository);
        Assert.Equal("main", workspace.SelectedBranch);
        Assert.Equal(calls, repositories.RefreshCalls);
        await workspace.ShutdownAsync();
    }

    [Fact]
    public async Task A_slow_reload_cannot_apply_its_preferred_workspace_after_the_user_switches()
    {
        var repositories = new FakeRepositories();
        using var workspace = Create(repositories, new StrongReferenceMessenger());
        await workspace.InitializeAsync(new UserSettings(RefreshOnOpen: false));
        var pending = new TaskCompletionSource<IReadOnlyList<RepositoryInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        repositories.List = _ => pending.Task;
        Task reload = workspace.ReloadRepositoriesAsync(First.Id);
        workspace.SelectRepositoryCommand.Execute(Second);

        pending.SetResult([First, Second]);
        await reload;

        Assert.Equal(Second.Id, workspace.SelectedRepository?.Id);
        Assert.Equal(0, repositories.RefreshCalls);
        await workspace.ShutdownAsync();
    }

    [Fact]
    public async Task Shutdown_waits_for_in_progress_catalog_reload_and_does_not_publish_it()
    {
        var repositories = new FakeRepositories();
        using var workspace = Create(repositories, new StrongReferenceMessenger());
        await workspace.InitializeAsync(new UserSettings());
        var pending = new TaskCompletionSource<IReadOnlyList<RepositoryInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        repositories.List = _ => pending.Task;
        Task reload = workspace.ReloadRepositoriesAsync(Second.Id);
        Task shutdown = workspace.ShutdownAsync();
        Assert.False(shutdown.IsCompleted);
        pending.SetResult([Second]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reload);
        await shutdown;

        Assert.Equal(First.Id, workspace.SelectedRepository?.Id);
    }

    private static WorkspaceViewModel Create(FakeRepositories repositories, IMessenger messenger) =>
        new(repositories, messenger, new Dispatcher(), new Dialogs(), NullLogger<WorkspaceViewModel>.Instance);

    private sealed class SnapshotObserver
    {
        public SnapshotChanged? Last { get; private set; }
        public SnapshotObserver(IMessenger messenger) => messenger.Register<SnapshotChanged>(this,
            static (recipient, message) => ((SnapshotObserver)recipient).Last = message);
    }

    private sealed class Dispatcher : IUiDispatcher
    {
        public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            action();
            return Task.CompletedTask;
        }
    }

    private sealed class Dialogs : IDialogService
    {
        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(true);
    }

    private sealed class FakeRepositories : IRepositoryService
    {
        private readonly Dictionary<string, BranchSnapshot> snapshots = new(StringComparer.Ordinal);
        public IReadOnlyList<RepositoryInfo> Saved { get; set; } = [First, Second];
        public Func<CancellationToken, Task<IReadOnlyList<RepositoryInfo>>>? List { get; set; }
        public Func<string, IProgress<OperationProgress>?, CancellationToken, Task<RepositoryInfo>> Connect { get; set; } = (_, _, _) => Task.FromResult(First);
        public Func<RepositoryInfo, string, CancellationToken, Task<BranchSnapshot?>>? Cache { get; set; }
        public Func<RepositoryInfo, string, IProgress<OperationProgress>?, CancellationToken, Task<BranchSnapshot>> Refresh { get; set; } = (repository, branch, _, _) => Task.FromResult(Fresh(repository, branch));
        public int RefreshCalls { get; private set; }
        public static BranchSnapshot Fresh(RepositoryInfo repository, string branch) =>
            new(repository.Id, branch, repository.Id + "-new", DateTimeOffset.Now, [], [], []);
        public BranchSnapshot Cached(RepositoryInfo repository, string branch)
        {
            string key = repository.Id + "/" + branch;
            if (!snapshots.TryGetValue(key, out var snapshot))
                snapshots.Add(key, snapshot = Fresh(repository, branch) with { TipOid = "cached", RefreshedAt = DateTimeOffset.Now.AddDays(-1) });
            return snapshot;
        }
        public Task<IReadOnlyList<RepositoryInfo>> GetRepositoriesAsync(CancellationToken cancellationToken = default) => List?.Invoke(cancellationToken) ?? Task.FromResult(Saved);
        public Task<RepositoryInfo> ConnectAsync(string remoteUrl, IProgress<OperationProgress>? progress, CancellationToken cancellationToken) => Connect(remoteUrl, progress, cancellationToken);
        public Task<BranchSnapshot?> GetCachedSnapshotAsync(RepositoryInfo repository, string branch, CancellationToken cancellationToken) =>
            Cache?.Invoke(repository, branch, cancellationToken) ?? Task.FromResult<BranchSnapshot?>(Cached(repository, branch));
        public Task<BranchSnapshot> RefreshAsync(RepositoryInfo repository, string branch, IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
        {
            RefreshCalls++;
            return Refresh(repository, branch, progress, cancellationToken);
        }
        public Task<DiffResult> GetDiffAsync(RepositoryInfo repository, FileChange change, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RemoveAsync(RepositoryInfo repository, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
