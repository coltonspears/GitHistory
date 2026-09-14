using CommunityToolkit.Mvvm.Messaging;
using GitHistory.Core.Models;
using GitHistory.Core.Services;
using GitHistory.Core.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace GitHistory.Tests.ViewModels;

public sealed class RepositoryFeaturesTests
{
    private static readonly RepositoryInfo First = new("first", "First", "https://github.com/team/first.git", "main", ["main"]);
    private static readonly RepositoryInfo Second = new("second", "Second", "https://dev.azure.com/company/project/_git/second", "main", ["main"]);
    private static readonly RemoteRepository Alpha = new("Alpha", "team/Alpha", "https://github.com/team/Alpha.git", "https://github.com/team/Alpha", true, "GitHub");
    private static readonly RemoteRepository Beta = new("Beta", "team/Beta", "https://github.com/team/Beta.git", "https://github.com/team/Beta", false, "GitHub");
    private static readonly RemoteRepository Gamma = new("Gamma", "other/Gamma", "https://github.com/other/Gamma.git", "https://github.com/other/Gamma", true, "GitHub");

    [Theory]
    [InlineData(RepositoryProvider.GitHub)]
    [InlineData(RepositoryProvider.AzureDevOps)]
    public async Task Discovery_keeps_private_repositories_and_passes_provider_scope(RepositoryProvider provider)
    {
        await using var fixture = new Fixture();
        fixture.Import.Provider = provider;
        fixture.Import.Organization = " company ";
        fixture.Import.Project = " project ";
        fixture.Import.AccessToken = "session-only-test-token";

        await fixture.Import.DiscoverCommand.ExecuteAsync(null);

        Assert.Equal(3, fixture.Import.Repositories.Count);
        Assert.Equal(2, fixture.Import.Repositories.Count(item => item.Repository.IsPrivate));
        var request = Assert.Single(fixture.Providers.DiscoveryRequests);
        Assert.Equal(provider, request.Provider);
        Assert.Equal("company", request.Organization);
        Assert.Equal("project", request.Project);
        Assert.Equal("session-only-test-token", request.AccessToken);
        Assert.DoesNotContain(request.AccessToken, request.ToString(), StringComparison.Ordinal);
        Assert.False(fixture.Import.IsBusy);
        Assert.True(fixture.Import.DiscoverCommand.CanExecute(null));
    }

    [Fact]
    public async Task Search_select_all_and_clear_selection_preserve_choices_across_filters()
    {
        await using var fixture = new Fixture();
        await fixture.Import.DiscoverCommand.ExecuteAsync(null);
        fixture.Import.Search = "alpha";
        fixture.Import.SelectAllCommand.Execute(null);
        fixture.Import.Search = "OTHER/";
        fixture.Import.SelectAllCommand.Execute(null);
        fixture.Import.Search = "";

        Assert.Equal([Alpha.FullName, Gamma.FullName], fixture.Import.Repositories.Where(item => item.IsSelected).Select(item => item.Repository.FullName));
        Assert.Equal(3, fixture.Import.VisibleRepositories.Count);
        fixture.Import.Search = "Beta";
        fixture.Import.ClearSelectionCommand.Execute(null);
        Assert.DoesNotContain(fixture.Import.Repositories, item => item.IsSelected);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("close")]
    [InlineData("provider")]
    public async Task Superseded_discovery_rejects_late_progress_and_repository_results(string action)
    {
        await using var fixture = new Fixture();
        var pending = fixture.Pending<IReadOnlyList<RemoteRepository>>();
        IProgress<OperationProgress>? progress = null;
        CancellationToken operationToken = default;
        fixture.Providers.Discover = (_, reporter, token) => { progress = reporter; operationToken = token; return pending.Task; };
        fixture.Import.IsOpen = true;
        fixture.Import.AccessToken = "session-only-test-token";
        Task discovery = fixture.Import.DiscoverCommand.ExecuteAsync(null);
        Assert.True(fixture.Import.IsBusy);
        Assert.False(fixture.Import.DiscoverCommand.CanExecute(null));

        Supersede(fixture.Import, action);
        Assert.True(operationToken.IsCancellationRequested);
        progress!.Report(new OperationProgress("stale progress", "from the canceled provider"));
        pending.SetResult([Alpha]);
        await discovery;

        Assert.Empty(fixture.Import.Repositories);
        Assert.Empty(fixture.Import.VisibleRepositories);
        Assert.DoesNotContain("stale progress", fixture.Import.Status, StringComparison.Ordinal);
        Assert.Empty(fixture.Import.Error);
        Assert.False(fixture.Import.IsBusy);
        if (action is "close" or "provider") Assert.Empty(fixture.Import.AccessToken);
        if (action == "close") Assert.False(fixture.Import.IsOpen);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("close")]
    public async Task Canceled_discovery_does_not_surface_a_late_provider_error(string action)
    {
        await using var fixture = new Fixture();
        var pending = fixture.Pending<IReadOnlyList<RemoteRepository>>();
        fixture.Providers.Discover = (_, _, _) => pending.Task;
        Task discovery = fixture.Import.DiscoverCommand.ExecuteAsync(null);
        Supersede(fixture.Import, action);

        pending.SetException(new IOException("late provider error"));
        await discovery;

        Assert.Empty(fixture.Import.Error);
        Assert.DoesNotContain("late provider error", fixture.Import.Status, StringComparison.Ordinal);
        Assert.False(fixture.Import.IsBusy);
    }

    [Fact]
    public async Task Partial_import_keeps_successful_repositories_and_the_active_workspace()
    {
        await using var fixture = new Fixture();
        await fixture.Workspace.InitializeAsync(new UserSettings(SelectedRepositoryId: Second.Id, RefreshOnOpen: false));
        fixture.Providers.Discover = (_, _, _) => Task.FromResult<IReadOnlyList<RemoteRepository>>([Alpha, Beta]);
        fixture.Repositories.Connect = (url, _) => url == Beta.CloneUrl
            ? Task.FromException<RepositoryInfo>(new IOException("Access denied"))
            : Task.FromResult(fixture.Repositories.Save(Alpha));
        await fixture.Import.DiscoverCommand.ExecuteAsync(null);
        fixture.Import.SelectAllCommand.Execute(null);

        await fixture.Import.ImportSelectedCommand.ExecuteAsync(null);

        Assert.Equal(Second.Id, fixture.Workspace.SelectedRepository?.Id);
        Assert.Contains(fixture.Workspace.Repositories, repository => repository.RemoteUrl == Alpha.CloneUrl);
        Assert.DoesNotContain(fixture.Workspace.Repositories, repository => repository.RemoteUrl == Beta.CloneUrl);
        Assert.Contains("Imported 1", fixture.Import.Status, StringComparison.Ordinal);
        Assert.Contains("team/Beta: Access denied", fixture.Import.Error, StringComparison.Ordinal);
        Assert.False(fixture.Import.Repositories[0].IsSelected);
        Assert.True(fixture.Import.Repositories[1].IsSelected);
        Assert.Equal(0, fixture.Repositories.RefreshCalls);
        Assert.False(fixture.Import.IsBusy);
    }

    [Fact]
    public async Task Canceling_a_partial_import_still_publishes_already_saved_repositories()
    {
        await using var fixture = new Fixture();
        await fixture.Workspace.InitializeAsync(new UserSettings(SelectedRepositoryId: Second.Id, RefreshOnOpen: false));
        fixture.Providers.Discover = (_, _, _) => Task.FromResult<IReadOnlyList<RemoteRepository>>([Alpha, Beta]);
        var pending = fixture.Pending<RepositoryInfo>();
        CancellationToken secondToken = default;
        fixture.Repositories.Connect = (url, token) =>
        {
            if (url == Alpha.CloneUrl) return Task.FromResult(fixture.Repositories.Save(Alpha));
            secondToken = token;
            return pending.Task;
        };
        await fixture.Import.DiscoverCommand.ExecuteAsync(null);
        fixture.Import.SelectAllCommand.Execute(null);
        Task importing = fixture.Import.ImportSelectedCommand.ExecuteAsync(null);

        fixture.Import.CancelCommand.Execute(null);
        Assert.True(secondToken.IsCancellationRequested);
        pending.SetCanceled(secondToken);
        await importing;

        Assert.Contains(fixture.Workspace.Repositories, repository => repository.RemoteUrl == Alpha.CloneUrl);
        Assert.Equal(Second.Id, fixture.Workspace.SelectedRepository?.Id);
        Assert.False(fixture.Import.Repositories[0].IsSelected);
        Assert.True(fixture.Import.Repositories[1].IsSelected);
        Assert.False(fixture.Import.IsBusy);
    }

    [Fact]
    public async Task Context_actions_use_the_passed_file_and_commit_instead_of_the_current_selection()
    {
        await using var fixture = new Fixture();
        fixture.Actions.Load(new UserSettings(LocalFolders: new Dictionary<string, string> { [First.Id] = "C:/linked/first" }));
        FileRow selected = Row("src/selected.cs", 'a');
        FileRow clicked = Row("other/clicked file.cs", 'b');
        fixture.Messenger.Send(new SnapshotChanged(First, Snapshot(First)));
        fixture.Messenger.Send(new FileSelected(First, Snapshot(First), selected));
        fixture.Messenger.Send(new CommitSelected(First, selected.LatestChange));

        fixture.Actions.CopyPathCommand.Execute(clicked);
        Assert.Equal(clicked.Path, fixture.Clipboard.Text);
        fixture.Actions.OpenFileBrowserCommand.Execute(clicked);
        Assert.EndsWith("/blob/" + new string('b', 40) + "/other/clicked%20file.cs", fixture.Desktop.OpenedUrls[^1], StringComparison.Ordinal);
        fixture.Actions.OpenCommitBrowserCommand.Execute(clicked.LatestChange);
        Assert.EndsWith("/commit/" + new string('b', 40), fixture.Desktop.OpenedUrls[^1], StringComparison.Ordinal);
        await fixture.Actions.OpenExplorerCommand.ExecuteAsync(clicked);
        await fixture.Actions.OpenCursorCommand.ExecuteAsync(clicked);
        Assert.Equal(("C:/linked/first", clicked.Path), Assert.Single(fixture.Desktop.ExplorerCalls));
        Assert.Equal(("C:/linked/first", clicked.Path, "cursor"), Assert.Single(fixture.Desktop.CursorCalls));
        Assert.Empty(fixture.Actions.Error);
    }

    [Fact]
    public async Task A_deleted_files_browser_action_opens_the_parent_commit()
    {
        await using var fixture = new Fixture();
        FileRow row = Row("deleted/old.cs", 'd', ChangeKind.Deleted);
        fixture.Messenger.Send(new SnapshotChanged(First, Snapshot(First)));

        fixture.Actions.OpenFileBrowserCommand.Execute(row);

        Assert.Contains("/blob/" + new string('0', 40) + "/deleted/old.cs", Assert.Single(fixture.Desktop.OpenedUrls), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_late_pull_request_response_cannot_replace_the_newer_selected_commits_details()
    {
        await using var fixture = new Fixture();
        var pending = fixture.Pending<IReadOnlyList<PullRequestInfo>>();
        CancellationToken oldToken = default;
        var newest = new PullRequestInfo(22, "Current commit PR", "merged", "author", "https://github.com/team/first/pull/22");
        fixture.Providers.PullRequests = (_, sha, token) =>
        {
            if (sha == new string('a', 40)) { oldToken = token; return pending.Task; }
            return Task.FromResult<IReadOnlyList<PullRequestInfo>>([newest]);
        };
        fixture.Messenger.Send(new CommitSelected(First, Row("file.cs", 'a').LatestChange));
        Task oldRequest = fixture.Actions.LoadPullRequestsCommand.ExecuteAsync(null);
        Assert.True(fixture.Actions.IsLoadingPullRequests);

        fixture.Messenger.Send(new CommitSelected(First, Row("file.cs", 'b').LatestChange));
        Assert.True(oldToken.IsCancellationRequested);
        await fixture.Actions.LoadPullRequestsCommand.ExecuteAsync(null);
        Assert.Same(newest, Assert.Single(fixture.Actions.PullRequests));
        pending.SetResult([new PullRequestInfo(11, "Old commit PR", "open", "author", "https://github.com/team/first/pull/11")]);
        await oldRequest;

        Assert.Same(newest, Assert.Single(fixture.Actions.PullRequests));
        Assert.False(fixture.Actions.IsLoadingPullRequests);
    }

    [Fact]
    public async Task Linking_a_checkout_during_a_workspace_switch_saves_it_for_the_original_repository()
    {
        await using var fixture = new Fixture();
        fixture.Actions.Load(new UserSettings(LocalFolders: new Dictionary<string, string> { [Second.Id] = "C:/linked/second" }));
        var pending = fixture.Pending<string?>();
        fixture.Desktop.PickFolder = (_, _) => pending.Task;
        fixture.Messenger.Send(new SnapshotChanged(First, Snapshot(First)));
        Task linking = fixture.Actions.LinkLocalFolderCommand.ExecuteAsync(null);
        Assert.True(fixture.Actions.IsBusy);

        fixture.Messenger.Send(new SnapshotChanged(Second, Snapshot(Second)));
        pending.SetResult("C:/linked/first");
        await linking;

        Assert.Equal("C:/linked/first", fixture.Actions.LocalFolders[First.Id]);
        Assert.Equal("C:/linked/second", fixture.Actions.LocalFolders[Second.Id]);
        Assert.Equal("C:/linked/second", fixture.Actions.LocalFolder);
        Assert.False(fixture.Actions.IsBusy);
    }

    [Fact]
    public async Task Automatic_pull_request_setting_starts_and_cancels_lookup_for_the_current_commit()
    {
        await using var fixture = new Fixture();
        var pending = fixture.Pending<IReadOnlyList<PullRequestInfo>>();
        CancellationToken operationToken = default;
        fixture.Providers.PullRequests = (_, _, token) => { operationToken = token; return pending.Task; };
        fixture.Messenger.Send(new CommitSelected(First, Row("file.cs", 'a').LatestChange));
        Assert.Empty(fixture.Providers.PullRequestCalls);

        fixture.Settings.AutoLoadPullRequests = true;
        Assert.Single(fixture.Providers.PullRequestCalls);
        Assert.True(fixture.Actions.IsLoadingPullRequests);
        fixture.Settings.AutoLoadPullRequests = false;
        Assert.True(operationToken.IsCancellationRequested);
        Assert.False(fixture.Actions.IsLoadingPullRequests);
        pending.SetCanceled(operationToken);
        await fixture.Actions.ShutdownAsync();
        Assert.Empty(fixture.Actions.PullRequests);
    }

    [Fact]
    public async Task Source_control_commands_are_disabled_for_demo_and_unsupported_remotes()
    {
        await using var fixture = new Fixture();
        fixture.Messenger.Send(new SnapshotChanged(First with { IsDemo = true }, null));
        Assert.False(fixture.Actions.OpenExplorerCommand.CanExecute(null));
        Assert.False(fixture.Actions.OpenRepositoryBrowserCommand.CanExecute(null));
        fixture.Messenger.Send(new SnapshotChanged(First with { RemoteUrl = "https://git.internal/team/first.git" }, null));
        Assert.True(fixture.Actions.OpenExplorerCommand.CanExecute(null));
        Assert.False(fixture.Actions.OpenRepositoryBrowserCommand.CanExecute(null));
    }

    private static void Supersede(RepositoryImportViewModel import, string action)
    {
        if (action == "cancel") import.CancelCommand.Execute(null);
        else if (action == "close") import.CloseCommand.Execute(null);
        else import.Provider = RepositoryProvider.AzureDevOps;
    }

    private static FileRow Row(string path, char sha, ChangeKind kind = ChangeKind.Modified)
    {
        var timestamp = DateTimeOffset.UtcNow;
        var commit = new CommitInfo(new string(sha, 40), new string('0', 40), "Author", "author@example.com", timestamp, timestamp, "Update file");
        var change = new FileChange(commit, path, null, kind, new string('1', 40), new string('2', 40), "100644", "100644");
        return new FileRow(path, change, 1, "100644", timestamp);
    }

    private static BranchSnapshot Snapshot(RepositoryInfo repository) => new(repository.Id, "main", new string('f', 40), DateTimeOffset.UtcNow, [], [], []);

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly List<Action> releasePending = [];
        public StrongReferenceMessenger Messenger { get; } = new();
        public ProviderService Providers { get; } = new();
        public RepositoryService Repositories { get; } = new();
        public DesktopService Desktop { get; } = new();
        public Clipboard Clipboard { get; } = new();
        public WorkspaceViewModel Workspace { get; }
        public SettingsViewModel Settings { get; }
        public RepositoryImportViewModel Import { get; }
        public RepositoryActionsViewModel Actions { get; }
        public Fixture()
        {
            var dispatcher = new Dispatcher();
            Workspace = new(Repositories, Messenger, dispatcher, new Dialogs(), NullLogger<WorkspaceViewModel>.Instance);
            Settings = new(new Appearance(), Desktop);
            Import = new(Providers, Repositories, Workspace, dispatcher);
            Actions = new(Desktop, Clipboard, Providers, Settings, Messenger);
        }
        public TaskCompletionSource<T> Pending<T>()
        {
            var pending = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            releasePending.Add(() => pending.TrySetCanceled());
            return pending;
        }
        public async ValueTask DisposeAsync()
        {
            foreach (var release in releasePending) release();
            await Import.ShutdownAsync();
            await Actions.ShutdownAsync();
            await Workspace.ShutdownAsync();
        }
    }

    private sealed class ProviderService : IRepositoryProviderService
    {
        public Func<RepositoryImportRequest, IProgress<OperationProgress>?, CancellationToken, Task<IReadOnlyList<RemoteRepository>>> Discover { get; set; } =
            (_, _, _) => Task.FromResult<IReadOnlyList<RemoteRepository>>([Alpha, Beta, Gamma]);
        public Func<RepositoryInfo, string, CancellationToken, Task<IReadOnlyList<PullRequestInfo>>> PullRequests { get; set; } =
            (_, _, _) => Task.FromResult<IReadOnlyList<PullRequestInfo>>([]);
        public List<RepositoryImportRequest> DiscoveryRequests { get; } = [];
        public List<(string RepositoryId, string Sha)> PullRequestCalls { get; } = [];
        public Task<IReadOnlyList<RemoteRepository>> DiscoverAsync(RepositoryImportRequest request, IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
        {
            DiscoveryRequests.Add(request);
            return Discover(request, progress, cancellationToken);
        }
        public Task<IReadOnlyList<PullRequestInfo>> GetPullRequestsAsync(RepositoryInfo repository, string commitSha, CancellationToken cancellationToken)
        {
            PullRequestCalls.Add((repository.Id, commitSha));
            return PullRequests(repository, commitSha, cancellationToken);
        }
    }

    private sealed class RepositoryService : IRepositoryService
    {
        public List<RepositoryInfo> Saved { get; } = [First, Second];
        public Func<string, CancellationToken, Task<RepositoryInfo>> Connect { get; set; } = (_, _) => Task.FromResult(First);
        public int RefreshCalls { get; private set; }
        public RepositoryInfo Save(RemoteRepository remote)
        {
            var repository = new RepositoryInfo(remote.Name, remote.Name, remote.CloneUrl, "main", ["main"]);
            Saved.Add(repository);
            return repository;
        }
        public Task<IReadOnlyList<RepositoryInfo>> GetRepositoriesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<RepositoryInfo>>(Saved.ToArray());
        public Task<RepositoryInfo> ConnectAsync(string remoteUrl, IProgress<OperationProgress>? progress, CancellationToken cancellationToken) => Connect(remoteUrl, cancellationToken);
        public Task<BranchSnapshot?> GetCachedSnapshotAsync(RepositoryInfo repository, string branch, CancellationToken cancellationToken) => Task.FromResult<BranchSnapshot?>(Snapshot(repository));
        public Task<BranchSnapshot> RefreshAsync(RepositoryInfo repository, string branch, IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
        {
            RefreshCalls++;
            return Task.FromResult(Snapshot(repository));
        }
        public Task<DiffResult> GetDiffAsync(RepositoryInfo repository, FileChange change, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RemoveAsync(RepositoryInfo repository, CancellationToken cancellationToken) { Saved.Remove(repository); return Task.CompletedTask; }
    }

    private sealed class DesktopService : IDesktopIntegration
    {
        public Func<string?, CancellationToken, Task<string?>> PickFolder { get; set; } = (_, _) => Task.FromResult<string?>(null);
        public List<string> OpenedUrls { get; } = [];
        public List<(string Folder, string? Path)> ExplorerCalls { get; } = [];
        public List<(string Folder, string? Path, string Executable)> CursorCalls { get; } = [];
        public Task<string?> PickFolderAsync(string? initialFolder, CancellationToken cancellationToken) => PickFolder(initialFolder, cancellationToken);
        public void OpenBrowser(string url) => OpenedUrls.Add(url);
        public Task OpenExplorerAsync(string localFolder, string? repositoryPath, CancellationToken cancellationToken)
        {
            ExplorerCalls.Add((localFolder, repositoryPath)); return Task.CompletedTask;
        }
        public Task OpenCursorAsync(string localFolder, string? repositoryPath, string executable, CancellationToken cancellationToken)
        {
            CursorCalls.Add((localFolder, repositoryPath, executable)); return Task.CompletedTask;
        }
        public void OpenDataFolder() { }
    }
    private sealed class Clipboard : IClipboardService { public string Text { get; private set; } = ""; public void SetText(string text) => Text = text; }
    private sealed class Appearance : IAppearanceService { public void SetTheme(string theme) { } public void ResetLayout() { } }
    private sealed class Dialogs : IDialogService { public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(true); }
    private sealed class Dispatcher : IUiDispatcher
    {
        public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); action(); return Task.CompletedTask;
        }
    }
}
