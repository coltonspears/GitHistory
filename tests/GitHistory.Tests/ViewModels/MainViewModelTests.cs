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

        Assert.Equal(new UserSettings(Fixture.Repository.Id, "develop", "Classic", "RecentChanges"),
            Assert.Single(fixture.Settings.Writes));
    }

    [Theory]
    [InlineData("Dark", "Dark")]
    [InlineData("Light", "Light")]
    [InlineData("Classic", "Classic")]
    [InlineData("Dusk", "Dusk")]
    [InlineData("dUsK", "Dusk")]
    [InlineData("unknown-palette", "Dark")]
    [InlineData(null, "Dark")]
    public async Task Saved_theme_is_restored_canonically_and_survives_saving(string? persistedTheme, string expected)
    {
        await using var fixture = new Fixture();
        fixture.Settings.Load = _ => Task.FromResult(fixture.Settings.Existing with { Theme = persistedTheme! });

        await fixture.Main.InitializeAsync();
        await fixture.Main.SaveAsync();

        Assert.Equal(expected, fixture.Main.Theme);
        Assert.Equal(expected, fixture.Main.Settings.Theme);
        Assert.Equal(expected, fixture.Appearance.Theme);
        Assert.Equal(expected, Assert.Single(fixture.Settings.Writes).Theme);
    }

    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    [InlineData("Classic")]
    [InlineData("Dusk")]
    public async Task Choosing_a_theme_in_settings_updates_the_shell_and_saved_preferences(string theme)
    {
        await using var fixture = new Fixture();
        await fixture.Main.InitializeAsync();

        fixture.Main.Settings.Theme = theme;
        await fixture.Main.SaveAsync();

        Assert.Equal(theme, fixture.Main.Theme);
        Assert.Equal(theme, fixture.Appearance.Theme);
        Assert.Equal(theme, Assert.Single(fixture.Settings.Writes).Theme);
    }

    [Fact]
    public async Task Theme_shortcut_cycles_all_choices_in_settings_order_and_wraps()
    {
        await using var fixture = new Fixture();
        await fixture.Main.InitializeAsync();
        fixture.Main.Settings.Theme = "Dark";
        Assert.Equal(new[] { "Dark", "Light", "Classic", "Dusk" }, fixture.Main.Settings.Themes);

        foreach (string expected in new[] { "Light", "Classic", "Dusk", "Dark" })
        {
            fixture.Main.ToggleThemeCommand.Execute(null);
            await fixture.Main.SaveAsync();
            Assert.Equal(expected, fixture.Main.Theme);
            Assert.Equal(expected, fixture.Main.Settings.Theme);
            Assert.Equal(expected, fixture.Appearance.Theme);
            Assert.Equal(expected, fixture.Settings.Writes.Last().Theme);
        }

        fixture.Main.Settings.Theme = "Dusk";
        await fixture.Main.ExecutePaletteCommand.ExecuteAsync(new PaletteItem("theme", "Cycle theme", ""));
        Assert.Equal("Dark", fixture.Main.Theme);
    }

    [Fact]
    public async Task An_unknown_interactive_theme_falls_back_to_dark_without_desynchronizing_settings()
    {
        await using var fixture = new Fixture();
        await fixture.Main.InitializeAsync();

        fixture.Main.Theme = "missing-theme";
        await fixture.Main.SaveAsync();

        Assert.Equal("Dark", fixture.Main.Theme);
        Assert.Equal("Dark", fixture.Main.Settings.Theme);
        Assert.Equal("Dark", fixture.Appearance.Theme);
        Assert.Equal("Dark", Assert.Single(fixture.Settings.Writes).Theme);
        fixture.Main.ToggleThemeCommand.Execute(null);
        Assert.Equal("Light", fixture.Main.Theme);
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

    [Fact]
    public async Task Settings_and_pins_survive_save_without_serializing_import_credentials()
    {
        await using var fixture = new Fixture();
        await fixture.Main.InitializeAsync();
        fixture.Main.Settings.DefaultDatePreset = "30 days";
        fixture.Main.Settings.RefreshOnOpen = false;
        fixture.Main.Settings.ShowActivityChart = false;
        fixture.Main.Settings.AutoLoadPullRequests = true;
        fixture.Main.Settings.CursorCommand = "C:\\Tools\\Cursor.exe";
        fixture.Main.Import.AccessToken = "session-secret-not-for-settings";
        fixture.Workspace.TogglePinCommand.Execute(Fixture.Repository);
        fixture.Workspace.ShowOnlyPinned = true;
        await fixture.Main.SaveAsync();
        var saved = Assert.Single(fixture.Settings.Writes);
        Assert.Equal("30 days", saved.DefaultDatePreset);
        Assert.Equal("30 days", fixture.Browser.DatePreset);
        Assert.False(saved.RefreshOnOpen);
        Assert.False(fixture.Workspace.RefreshOnOpen);
        Assert.False(saved.ShowActivityChart);
        Assert.True(saved.AutoLoadPullRequests);
        Assert.Equal("C:\\Tools\\Cursor.exe", saved.CursorCommand);
        Assert.NotNull(saved.PinnedRepositoryIds);
        Assert.Equal(Fixture.Repository.Id, Assert.Single(saved.PinnedRepositoryIds));
        Assert.True(saved.ShowOnlyPinned);
        Assert.DoesNotContain("session-secret", System.Text.Json.JsonSerializer.Serialize(saved), StringComparison.Ordinal);
        fixture.Main.OpenSettingsCommand.Execute(null);
        Assert.True(fixture.Main.Settings.IsOpen);
        fixture.Main.OpenImportCommand.Execute(null);
        Assert.False(fixture.Main.Settings.IsOpen);
        Assert.True(fixture.Main.Import.IsOpen);
        fixture.Main.ClosePaletteCommand.Execute(null);
        Assert.False(fixture.Main.Import.IsOpen);
        Assert.Empty(fixture.Main.Import.AccessToken);
    }

    [Fact]
    public async Task Clearing_editor_values_does_not_break_navigation_search_or_import()
    {
        await using var fixture = new Fixture();
        await fixture.Main.InitializeAsync();
        fixture.Workspace.RepositorySearch = null!;
        fixture.Browser.FolderSearch = null!;
        fixture.Main.PaletteSearch = null!;
        fixture.Main.Import.Search = null!;
        fixture.Main.Import.Organization = null!;
        fixture.Main.Import.Project = null!;
        fixture.Main.Import.AccessToken = null!;
        await fixture.Main.Import.DiscoverCommand.ExecuteAsync(null);
        Assert.Single(fixture.Workspace.VisibleRepositories);
        Assert.NotEmpty(fixture.Main.PaletteItems);
        Assert.Empty(fixture.Main.Import.Error);
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
            var desktop = new Desktop();
            var preferences = new SettingsViewModel(Appearance, desktop);
            var providers = new Providers();
            var import = new RepositoryImportViewModel(providers, Repositories, Workspace, dispatcher);
            var actions = new RepositoryActionsViewModel(desktop, new Clipboard(), providers, preferences, messenger);
            Main = new MainViewModel(Workspace, Browser, Details, Settings, Appearance, preferences, import, actions);
        }

        public static BranchSnapshot Snapshot(RepositoryInfo repository, string branch) =>
            new(repository.Id, branch, "tip", DateTimeOffset.Now, [], [], []);

        public async ValueTask DisposeAsync() => await Task.WhenAll(Workspace.ShutdownAsync(), Browser.ShutdownAsync(), Details.ShutdownAsync(), Main.Import.ShutdownAsync(), Main.Actions.ShutdownAsync());
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
    private sealed class Providers : IRepositoryProviderService
    {
        public Task<IReadOnlyList<RemoteRepository>> DiscoverAsync(RepositoryImportRequest request, IProgress<OperationProgress>? progress, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<RemoteRepository>>([]);
        public Task<IReadOnlyList<PullRequestInfo>> GetPullRequestsAsync(RepositoryInfo repository, string commitSha, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<PullRequestInfo>>([]);
    }
    private sealed class Desktop : IDesktopIntegration
    {
        public Task<string?> PickFolderAsync(string? initialFolder, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
        public void OpenBrowser(string url) { }
        public Task OpenExplorerAsync(string localFolder, string? repositoryPath, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task OpenCursorAsync(string localFolder, string? repositoryPath, string executable, CancellationToken cancellationToken) => Task.CompletedTask;
        public void OpenDataFolder() { }
    }
}
