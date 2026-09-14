using CommunityToolkit.Mvvm.Messaging;
using GitHistory.Core.Models;
using GitHistory.Core.Services;
using GitHistory.Core.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace GitHistory.Tests.ViewModels;

public sealed class MainViewModelTests
{
    [Fact]
    public async Task Saving_before_initialization_does_not_overwrite_existing_settings()
    {
        await using var fixture = new Fixture();
        fixture.Main.Theme = "Dark";
        await fixture.Main.SaveAsync();

        Assert.False(fixture.Main.IsInitialized);
        Assert.Empty(fixture.Settings.Writes);
    }

    [Theory]
    [InlineData("settings")]
    [InlineData("repositories")]
    [InlineData("refresh")]
    public async Task Saving_during_or_after_canceled_initialization_does_not_write_settings(string stage)
    {
        await using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task PauseAsync(CancellationToken token)
        {
            entered.TrySetResult();
            await pending.Task.WaitAsync(token);
        }

        if (stage == "settings")
            fixture.Settings.Load = async token => { await PauseAsync(token); return fixture.Settings.Existing; };
        else if (stage == "repositories")
            fixture.Repositories.List = async token => { await PauseAsync(token); return [Fixture.Repository]; };
        else
            fixture.Repositories.Refresh = async (repository, branch, token) =>
            {
                await PauseAsync(token);
                return Fixture.Snapshot(repository, branch);
            };

        Task initializing = fixture.Main.InitializeAsync(cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await fixture.Main.SaveAsync();
        Assert.False(fixture.Main.IsInitialized);
        Assert.Empty(fixture.Settings.Writes);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => initializing);
        await fixture.Main.SaveAsync();
        Assert.False(fixture.Main.IsInitialized);
        Assert.Empty(fixture.Settings.Writes);
    }

    [Fact]
    public async Task Completed_initialization_saves_the_current_repository_branch_theme_and_view_mode()
    {
        await using var fixture = new Fixture();
        await fixture.Main.InitializeAsync();
        Assert.True(fixture.Main.IsInitialized);
        Assert.Equal("Light", fixture.Main.Theme);
        Assert.Equal("Light", fixture.Appearance.Theme);
        Assert.Equal(Fixture.Repository.Id, fixture.Workspace.SelectedRepository?.Id);
        Assert.Equal("develop", fixture.Workspace.SelectedBranch);
        Assert.Equal(FileViewMode.AllFiles, fixture.Browser.Mode);

        fixture.Main.ToggleThemeCommand.Execute(null);
        fixture.Browser.Mode = FileViewMode.RecentChanges;
        await fixture.Main.SaveAsync();

        Assert.Equal(new UserSettings(Fixture.Repository.Id, "develop", "Dark", "RecentChanges"),
            Assert.Single(fixture.Settings.Writes));
    }

    [Fact]
    public async Task Opening_the_palette_while_the_connection_form_is_open_keeps_only_one_modal_visible()
    {
        await using var fixture = new Fixture();
        fixture.Workspace.OpenConnectCommand.Execute(null);
        fixture.Main.OpenPaletteCommand.Execute(null);
        Assert.True(fixture.Workspace.IsConnectOpen);
        Assert.False(fixture.Main.IsPaletteOpen);

        fixture.Workspace.CloseConnectCommand.Execute(null);
        fixture.Main.OpenPaletteCommand.Execute(null);
        Assert.True(fixture.Main.IsPaletteOpen);
        Assert.False(fixture.Workspace.IsConnectOpen);

        await fixture.Main.ExecutePaletteCommand.ExecuteAsync(new PaletteItem("connect", "Connect repository", ""));
        Assert.False(fixture.Main.IsPaletteOpen);
        Assert.True(fixture.Workspace.IsConnectOpen);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public static RepositoryInfo Repository { get; } = new("saved", "Saved repository", "https://example.com/saved.git", "main", ["main", "develop"]);
        public SettingsStore Settings { get; } = new();
        public RepositoryService Repositories { get; } = new();
        public AppearanceService Appearance { get; } = new();
        public WorkspaceViewModel Workspace { get; }
        public BrowserViewModel Browser { get; }
        public DetailsViewModel Details { get; }
        public MainViewModel Main { get; }

        public Fixture()
        {
            var messenger = new StrongReferenceMessenger();
            var queries = new HistoryQueryService();
            var dispatcher = new Dispatcher();
            Workspace = new WorkspaceViewModel(Repositories, messenger, dispatcher, new Dialogs(), NullLogger<WorkspaceViewModel>.Instance);
            Browser = new BrowserViewModel(queries, messenger, dispatcher, TimeProvider.System, NullLogger<BrowserViewModel>.Instance);
            Details = new DetailsViewModel(Repositories, queries, messenger, new Clipboard(), NullLogger<DetailsViewModel>.Instance);
            Main = new MainViewModel(Workspace, Browser, Details, Settings, Appearance);
        }

        public static BranchSnapshot Snapshot(RepositoryInfo repository, string branch) =>
            new(repository.Id, branch, "tip", DateTimeOffset.Now, [], [], []);

        public async ValueTask DisposeAsync() => await Task.WhenAll(Workspace.ShutdownAsync(), Browser.ShutdownAsync(), Details.ShutdownAsync());
    }

    private sealed class SettingsStore : IUserSettingsStore
    {
        public UserSettings Existing { get; } = new(Fixture.Repository.Id, "develop", "Light", "AllFiles");
        public List<UserSettings> Writes { get; } = [];
        public Func<CancellationToken, Task<UserSettings>>? Load { get; set; }
        public Task<UserSettings> LoadAsync(CancellationToken cancellationToken = default) => Load?.Invoke(cancellationToken) ?? Task.FromResult(Existing);
        public Task SaveAsync(UserSettings settings, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Writes.Add(settings);
            return Task.CompletedTask;
        }
    }

    private sealed class RepositoryService : IRepositoryService
    {
        public Func<CancellationToken, Task<IReadOnlyList<RepositoryInfo>>>? List { get; set; }
        public Func<RepositoryInfo, string, CancellationToken, Task<BranchSnapshot>>? Refresh { get; set; }
        public Task<IReadOnlyList<RepositoryInfo>> GetRepositoriesAsync(CancellationToken cancellationToken = default) =>
            List?.Invoke(cancellationToken) ?? Task.FromResult<IReadOnlyList<RepositoryInfo>>([Fixture.Repository]);
        public Task<BranchSnapshot?> GetCachedSnapshotAsync(RepositoryInfo repository, string branch, CancellationToken cancellationToken) =>
            Task.FromResult<BranchSnapshot?>(Fixture.Snapshot(repository, branch));
        public Task<BranchSnapshot> RefreshAsync(RepositoryInfo repository, string branch, IProgress<OperationProgress>? progress, CancellationToken cancellationToken) =>
            Refresh?.Invoke(repository, branch, cancellationToken) ?? Task.FromResult(Fixture.Snapshot(repository, branch));
        public Task<RepositoryInfo> ConnectAsync(string remoteUrl, IProgress<OperationProgress>? progress, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<DiffResult> GetDiffAsync(RepositoryInfo repository, FileChange change, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RemoveAsync(RepositoryInfo repository, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class AppearanceService : IAppearanceService
    {
        public string? Theme { get; private set; }
        public void SetTheme(string theme) => Theme = theme;
        public void ResetLayout() { }
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

    private sealed class Clipboard : IClipboardService
    {
        public void SetText(string text) { }
    }
}
