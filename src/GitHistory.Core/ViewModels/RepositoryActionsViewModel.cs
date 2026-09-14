using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using GitHistory.Core.Models;
using GitHistory.Core.Services;

namespace GitHistory.Core.ViewModels;

public sealed partial class RepositoryActionsViewModel : ObservableObject, IDisposable
{
    private readonly IDesktopIntegration desktop;
    private readonly IClipboardService clipboard;
    private readonly IRepositoryProviderService providers;
    private readonly SettingsViewModel settings;
    private readonly IMessenger messenger;
    private readonly OperationLifetime lifetime = new();
    private readonly Dictionary<string, string> localFolders = new(StringComparer.Ordinal);
    private RepositoryInfo? repository;
    private string? branch;
    private FileRow? file;
    private FileChange? change;
    private CancellationTokenSource? prOperation;
    private int prGeneration;
    [ObservableProperty] public partial string Error { get; set; } = "";
    [ObservableProperty] public partial string Status { get; set; } = "";
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial string LocalFolder { get; set; } = "No local checkout linked";
    [ObservableProperty] public partial bool IsLoadingPullRequests { get; set; }
    [ObservableProperty] public partial IReadOnlyList<PullRequestInfo> PullRequests { get; set; } = [];
    [ObservableProperty] public partial string PullRequestStatus { get; set; } = "Select a commit to find associated pull requests.";
    public IReadOnlyDictionary<string, string> LocalFolders => new Dictionary<string, string>(localFolders, StringComparer.Ordinal);

    public RepositoryActionsViewModel(IDesktopIntegration desktop, IClipboardService clipboard, IRepositoryProviderService providers,
        SettingsViewModel settings, IMessenger messenger)
    {
        this.desktop = desktop; this.clipboard = clipboard; this.providers = providers; this.settings = settings; this.messenger = messenger;
        messenger.Register<SnapshotChanged>(this, static (recipient, value) => ((RepositoryActionsViewModel)recipient).OnSnapshot(value));
        messenger.Register<FileSelected>(this, static (recipient, value) => ((RepositoryActionsViewModel)recipient).OnFile(value));
        messenger.Register<CommitSelected>(this, static (recipient, value) => ((RepositoryActionsViewModel)recipient).OnCommit(value));
        settings.PropertyChanged += SettingsChanged;
    }
    public void Load(UserSettings saved)
    {
        localFolders.Clear();
        foreach (var item in saved.LocalFolders ?? new Dictionary<string, string>())
            if (!string.IsNullOrWhiteSpace(item.Value)) localFolders[item.Key] = item.Value;
        UpdateLocalFolder();
    }
    private void OnSnapshot(SnapshotChanged value)
    {
        repository = value.Repository; branch = value.Snapshot?.Branch ?? repository.DefaultBranch;
        if (value.Snapshot is null) { file = null; OnCommit(new(repository, null)); }
        UpdateLocalFolder(); NotifyCommands();
    }
    private void OnFile(FileSelected value)
    {
        repository = value.Repository; branch = value.Snapshot?.Branch; file = value.File;
        UpdateLocalFolder(); NotifyCommands();
    }
    private void OnCommit(CommitSelected value)
    {
        prOperation?.Cancel(); ++prGeneration;
        repository = value.Repository; change = value.Change;
        IsLoadingPullRequests = false;
        var inferred = repository is null || change is null ? null : RepositoryLinks.InferPullRequest(repository.RemoteUrl, change.Subject);
        PullRequests = inferred is null ? [] : [inferred];
        PullRequestStatus = inferred is not null ? "Reference found in the commit message; load details to verify." : "Load associated pull requests from the source control provider.";
        NotifyCommands();
        if (settings.AutoLoadPullRequests && CanLoadPullRequests()) _ = LoadPullRequestsAsync();
    }
    private void SettingsChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(SettingsViewModel.AutoLoadPullRequests)) return;
        if (settings.AutoLoadPullRequests && CanLoadPullRequests()) _ = LoadPullRequestsAsync();
        else if (!settings.AutoLoadPullRequests) { prOperation?.Cancel(); ++prGeneration; IsLoadingPullRequests = false; }
    }
    private void UpdateLocalFolder() => LocalFolder = repository is not null && localFolders.TryGetValue(repository.Id, out var folder) ? folder : "No local checkout linked";
    private bool CanUseRepository() => !lifetime.IsClosed && repository is { IsDemo: false };
    private bool CanOpenRepositoryBrowser() => CanUseRepository() && RepositoryLinks.Repository(repository!.RemoteUrl) is not null;
    private bool CanLoadPullRequests() => CanOpenRepositoryBrowser() && change is not null;
    private void NotifyCommands()
    {
        OpenRepositoryBrowserCommand.NotifyCanExecuteChanged(); CopyRemoteUrlCommand.NotifyCanExecuteChanged();
        OpenExplorerCommand.NotifyCanExecuteChanged(); OpenCursorCommand.NotifyCanExecuteChanged();
        LinkLocalFolderCommand.NotifyCanExecuteChanged(); UnlinkLocalFolderCommand.NotifyCanExecuteChanged();
        OpenFileBrowserCommand.NotifyCanExecuteChanged(); OpenCommitBrowserCommand.NotifyCanExecuteChanged(); LoadPullRequestsCommand.NotifyCanExecuteChanged();
    }
    private void OpenUrl(string? url)
    {
        try { if (url is null) throw new InvalidOperationException("This remote does not provide a supported browser link."); desktop.OpenBrowser(url); Error = ""; }
        catch (Exception ex) { Error = ex.Message; }
    }
    [RelayCommand(CanExecute = nameof(CanOpenRepositoryBrowser))] private void OpenRepositoryBrowser() => OpenUrl(RepositoryLinks.Branch(repository!.RemoteUrl, branch ?? repository.DefaultBranch));
    [RelayCommand(CanExecute = nameof(CanOpenRepositoryBrowser))] private void OpenFileBrowser(FileRow? row)
    {
        row ??= file;
        if (row is null || repository is null) return;
        var latest = row.LatestChange;
        var revision = latest?.Kind == ChangeKind.Deleted ? latest.Commit.ParentOid : latest?.Commit.Oid;
        OpenUrl(RepositoryLinks.File(repository.RemoteUrl, row.Path, revision ?? branch ?? repository.DefaultBranch, isCommit: revision is not null));
    }
    [RelayCommand(CanExecute = nameof(CanOpenRepositoryBrowser))] private void OpenCommitBrowser(FileChange? selected)
    {
        selected ??= change;
        if (selected is not null && repository is not null) OpenUrl(RepositoryLinks.Commit(repository.RemoteUrl, selected.Commit.Oid));
    }
    [RelayCommand] private void OpenPullRequest(PullRequestInfo? pullRequest) { if (pullRequest is not null) OpenUrl(pullRequest.Url); }
    [RelayCommand] private void CopyPath(FileRow? row)
    {
        try { row ??= file; if (row is not null) { clipboard.SetText(row.Path); Status = "Repository path copied"; Error = ""; } }
        catch (Exception ex) { Error = ex.Message; }
    }
    [RelayCommand(CanExecute = nameof(CanUseRepository))] private void CopyRemoteUrl()
    {
        try { clipboard.SetText(repository!.RemoteUrl); Status = "Remote URL copied"; Error = ""; }
        catch (Exception ex) { Error = ex.Message; }
    }
    [RelayCommand(CanExecute = nameof(CanUseRepository))] private Task LinkLocalFolderAsync() => LocalActionAsync(null, "link");
    [RelayCommand(CanExecute = nameof(CanUseRepository))] private Task OpenExplorerAsync(FileRow? row) => LocalActionAsync(row, "explorer");
    [RelayCommand(CanExecute = nameof(CanUseRepository))] private Task OpenCursorAsync(FileRow? row) => LocalActionAsync(row, "cursor");
    [RelayCommand(CanExecute = nameof(CanUseRepository))] private void UnlinkLocalFolder()
    {
        if (repository is not null) localFolders.Remove(repository.Id);
        UpdateLocalFolder();
    }
    private Task LocalActionAsync(FileRow? row, string action) => lifetime.Run(async token =>
    {
        var target = repository;
        if (target is null || target.IsDemo) return;
        IsBusy = true; Error = ""; Status = action == "link" ? "Choose an existing local checkout" : "Opening local checkout…";
        try
        {
            localFolders.TryGetValue(target.Id, out var folder);
            if (folder is null || action == "link")
            {
                folder = await desktop.PickFolderAsync(folder, token);
                token.ThrowIfCancellationRequested();
                if (folder is null) { Status = "Folder selection canceled"; return; }
                localFolders[target.Id] = folder;
                UpdateLocalFolder();
            }
            if (action == "explorer") await desktop.OpenExplorerAsync(folder, row?.Path, token);
            else if (action == "cursor") await desktop.OpenCursorAsync(folder, row?.Path, string.IsNullOrWhiteSpace(settings.CursorCommand) ? "cursor" : settings.CursorCommand, token);
            Status = action == "link" ? $"Linked {target.Name} to its local checkout" : "Opened local checkout";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!lifetime.IsClosed) Error = ex.Message; }
        finally { if (!lifetime.IsClosed) IsBusy = false; }
    });
    [RelayCommand(CanExecute = nameof(CanLoadPullRequests))]
    private Task LoadPullRequestsAsync() => lifetime.Run(async token =>
    {
        var target = repository; var selected = change;
        if (target is null || selected is null) return;
        prOperation?.Cancel(); var version = ++prGeneration;
        using var active = CancellationTokenSource.CreateLinkedTokenSource(token);
        prOperation = active; IsLoadingPullRequests = true; PullRequestStatus = "Loading pull request information…";
        try
        {
            var result = await providers.GetPullRequestsAsync(target, selected.Commit.Oid, active.Token);
            if (lifetime.IsClosed || version != prGeneration || active.IsCancellationRequested) return;
            PullRequests = result;
            PullRequestStatus = result.Count == 0 ? "No associated pull requests found." : $"{result.Count} associated pull request(s)";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!lifetime.IsClosed && version == prGeneration) PullRequestStatus = $"PR details unavailable: {ex.Message}"; }
        finally { if (ReferenceEquals(prOperation, active)) prOperation = null; if (!lifetime.IsClosed && version == prGeneration) IsLoadingPullRequests = false; }
    });
    [RelayCommand] private void CancelPullRequests() { prOperation?.Cancel(); ++prGeneration; IsLoadingPullRequests = false; PullRequestStatus = "PR lookup canceled"; }
    public Task ShutdownAsync() { Dispose(); return lifetime.ShutdownAsync(); }
    public void Dispose() { messenger.UnregisterAll(this); settings.PropertyChanged -= SettingsChanged; prOperation?.Cancel(); lifetime.Dispose(); NotifyCommands(); }
}
