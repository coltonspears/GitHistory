using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using GitHistory.Core.Models;
using GitHistory.Core.Services;
using Microsoft.Extensions.Logging;

namespace GitHistory.Core.ViewModels;

public sealed partial class WorkspaceViewModel(
    IRepositoryService repositories, IMessenger messenger, IUiDispatcher dispatcher,
    IDialogService dialogs, ILogger<WorkspaceViewModel> logger) : ObservableObject, IDisposable
{
    private readonly object _taskGate = new();
    private readonly HashSet<Task> _pending = [];
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _operation;
    private RepositoryInfo? _displayedRepository;
    private int _generation;
    private int _catalogGeneration;
    private int _selectionVersion;
    private int _connectionGeneration = -1;
    private bool _settingSelection;
    private RepositoryInfo? _acceptedRepository;
    private bool _disposed;
    private Task? _shutdown;
    [ObservableProperty] public partial IReadOnlyList<RepositoryInfo> Repositories { get; set; } = [];
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasVisibleRepositories))]
    public partial IReadOnlyList<RepositoryInfo> VisibleRepositories { get; set; } = [];
    [ObservableProperty] public partial string RepositorySearch { get; set; } = "";
    [ObservableProperty] public partial bool ShowOnlyPinned { get; set; }
    [ObservableProperty] public partial IReadOnlyList<string> PinnedRepositoryIds { get; set; } = [];
    [ObservableProperty] public partial bool RefreshOnOpen { get; set; } = true;
    [ObservableProperty] public partial RepositoryInfo? SelectedRepository { get; set; }
    [ObservableProperty] public partial IReadOnlyList<string> Branches { get; set; } = [];
    [ObservableProperty] public partial string? SelectedBranch { get; set; }
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(ConnectCommand))] public partial string RemoteUrl { get; set; } = "";
    [ObservableProperty] public partial bool IsConnectOpen { get; set; }
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(ConnectCommand)), NotifyCanExecuteChangedFor(nameof(RefreshCommand)), NotifyCanExecuteChangedFor(nameof(RemoveRepositoryCommand))]
    public partial bool IsBusy { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasError))] public partial string Error { get; set; } = "";
    [ObservableProperty] public partial string Status { get; set; } = "Ready";
    [ObservableProperty] public partial string ProgressText { get; set; } = "";
    [ObservableProperty] public partial string LastUpdated { get; set; } = "Choose a repository to get started";
    public bool HasError => Error.Length > 0;
    public bool IsDemo => SelectedRepository?.IsDemo == true;
    public bool HasVisibleRepositories => VisibleRepositories.Count > 0;
    public bool SelectedIsPinned => SelectedRepository is not null && IsPinned(SelectedRepository);
    public IRelayCommand RefreshCancelCommand => CancelCommand;

    public Task InitializeAsync(UserSettings settings, CancellationToken cancellationToken = default) =>
        Track(() => InitializeCoreAsync(settings, cancellationToken));

    private async Task InitializeCoreAsync(UserSettings settings, CancellationToken cancellationToken)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        PinnedRepositoryIds = settings.PinnedRepositoryIds?.Distinct(StringComparer.Ordinal).ToArray() ?? [];
        ShowOnlyPinned = settings.ShowOnlyPinned;
        RefreshOnOpen = settings.RefreshOnOpen;
        int selectionVersion = _selectionVersion;
        int generation = _generation;
        int catalogGeneration = ++_catalogGeneration;
        IsBusy = true;
        Status = "Loading workspaces";
        ProgressText = "Reading saved repositories…";
        try
        {
            var saved = await repositories.GetRepositoriesAsync(operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            if (catalogGeneration != _catalogGeneration) return;
            bool selectionUnchanged = selectionVersion == _selectionVersion;
            string? selectedId = selectionUnchanged ? settings.SelectedRepositoryId : SelectedRepository?.Id;
            string? branch = selectionUnchanged ? settings.SelectedBranch : SelectedBranch;
            PublishRepositories(saved, selectedId, branch);
            if (selectionUnchanged) await LoadSelectionAsync(operation.Token);
        }
        finally
        {
            if (IsCurrent(generation)) IsBusy = false;
        }
    }

    partial void OnSelectedRepositoryChanged(RepositoryInfo? value)
    {
        OnPropertyChanged(nameof(IsDemo));
        OnPropertyChanged(nameof(SelectedIsPinned));
        RefreshCommand.NotifyCanExecuteChanged();
        RemoveRepositoryCommand.NotifyCanExecuteChanged();
        if (_settingSelection || _disposed) return;
        // A filtered or refreshed navigation control can temporarily clear its selection.
        // The active workspace has its own lifetime and is not cleared by navigation visibility.
        if (value is null && _acceptedRepository is not null && Repositories.Any(r => r.Id == _acceptedRepository.Id))
        {
            SetSelection(_acceptedRepository, SelectedBranch);
            return;
        }
        ++_selectionVersion;
        SetSelection(value, value?.DefaultBranch);
        _ = LoadSelectionAsync(CancellationToken.None);
    }

    partial void OnSelectedBranchChanged(string? value)
    {
        if (!_settingSelection && !_disposed)
        {
            ++_selectionVersion;
            _ = LoadSelectionAsync(CancellationToken.None);
        }
    }

    private void SetSelection(RepositoryInfo? repository, string? branch)
    {
        bool wasSettingSelection = _settingSelection;
        _settingSelection = true;
        try
        {
            _acceptedRepository = repository;
            SelectedRepository = repository;
            Branches = repository?.Branches ?? [];
            SelectedBranch = branch;
        }
        finally { _settingSelection = wasSettingSelection; }
    }

    private void PublishRepositories(IReadOnlyList<RepositoryInfo> saved, string? selectedId, string? branch)
    {
        // Preserve reference identities when only the list instance changed. In particular,
        // protect the entire ItemsSource publication from selection callbacks, not just its end.
        var existing = Repositories.ToDictionary(r => r.Id, StringComparer.Ordinal);
        var stable = saved.Select(repository => existing.TryGetValue(repository.Id, out var previous) && Equivalent(previous, repository)
            ? previous : repository).ToArray();
        var selected = stable.FirstOrDefault(r => r.Id == selectedId) ?? stable.FirstOrDefault();
        bool wasSettingSelection = _settingSelection;
        _settingSelection = true;
        try
        {
            if (Repositories.Count != stable.Length || !Repositories.Zip(stable).All(pair => ReferenceEquals(pair.First, pair.Second)))
                Repositories = stable;
            UpdateVisibleRepositories();
            SetSelection(selected, selected?.Branches.Contains(branch ?? "", StringComparer.Ordinal) == true ? branch : selected?.DefaultBranch);
        }
        finally { _settingSelection = wasSettingSelection; }
    }

    private static bool Equivalent(RepositoryInfo left, RepositoryInfo right) =>
        left.Id == right.Id && left.Name == right.Name && left.RemoteUrl == right.RemoteUrl &&
        left.DefaultBranch == right.DefaultBranch && left.IsDemo == right.IsDemo && left.Branches.SequenceEqual(right.Branches, StringComparer.Ordinal);

    partial void OnRepositorySearchChanged(string value) => UpdateVisibleRepositories();
    partial void OnShowOnlyPinnedChanged(bool value) => UpdateVisibleRepositories();
    partial void OnPinnedRepositoryIdsChanged(IReadOnlyList<string> value)
    {
        OnPropertyChanged(nameof(SelectedIsPinned));
        UpdateVisibleRepositories();
    }

    public bool IsPinned(RepositoryInfo? repository) => repository is not null && PinnedRepositoryIds.Contains(repository.Id, StringComparer.Ordinal);

    [RelayCommand]
    private void SelectRepository(RepositoryInfo? repository)
    {
        if (_disposed || repository is null || repository.Id == _acceptedRepository?.Id) return;
        var saved = Repositories.FirstOrDefault(item => item.Id == repository.Id);
        if (saved is not null) SelectedRepository = saved;
    }

    [RelayCommand]
    private void TogglePin(RepositoryInfo? repository)
    {
        repository ??= _acceptedRepository;
        if (_disposed || repository is null) return;
        PinnedRepositoryIds = IsPinned(repository)
            ? PinnedRepositoryIds.Where(id => id != repository.Id).ToArray()
            : [.. PinnedRepositoryIds, repository.Id];
    }

    private void UpdateVisibleRepositories()
    {
        string search = (RepositorySearch ?? "").Trim();
        var visible = Repositories.Where(repository => (!ShowOnlyPinned || IsPinned(repository)) &&
                (repository.Name.Contains(search, StringComparison.OrdinalIgnoreCase) || repository.RemoteUrl.Contains(search, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(IsPinned).ThenBy(repository => repository.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        if (VisibleRepositories.Count == visible.Length && VisibleRepositories.Zip(visible).All(pair => ReferenceEquals(pair.First, pair.Second))) return;
        var selected = _acceptedRepository;
        var branch = SelectedBranch;
        bool wasSettingSelection = _settingSelection;
        _settingSelection = true;
        try
        {
            VisibleRepositories = visible;
            SetSelection(selected, branch);
        }
        finally { _settingSelection = wasSettingSelection; }
    }

    public Task ReloadRepositoriesAsync(string? preferredRepositoryId = null, CancellationToken cancellationToken = default) =>
        Track(() => ReloadRepositoriesCoreAsync(preferredRepositoryId, cancellationToken));

    private async Task ReloadRepositoriesCoreAsync(string? preferredRepositoryId, CancellationToken cancellationToken)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        int catalogGeneration = ++_catalogGeneration;
        int selectionVersion = _selectionVersion;
        var saved = await repositories.GetRepositoriesAsync(operation.Token);
        operation.Token.ThrowIfCancellationRequested();
        if (_disposed || catalogGeneration != _catalogGeneration) return;
        string? previousId = _acceptedRepository?.Id;
        string? previousBranch = SelectedBranch;
        string? selectedId = selectionVersion == _selectionVersion ? preferredRepositoryId ?? previousId : previousId;
        PublishRepositories(saved, selectedId, selectedId == previousId ? previousBranch : null);
        if (SelectedRepository?.Id != previousId || SelectedBranch != previousBranch)
        {
            ++_selectionVersion;
            await LoadSelectionAsync(operation.Token);
        }
    }

    private bool CanRefresh() => !_disposed && SelectedRepository is not null && !IsBusy;
    private bool CanConnect() => !_disposed && !IsBusy && !string.IsNullOrWhiteSpace(RemoteUrl);
    private bool CanRemoveRepository() => !_disposed && SelectedRepository is { IsDemo: false } && !IsBusy;
    [RelayCommand] private void OpenConnect() { if (!_disposed) { Error = ""; IsConnectOpen = true; } }
    [RelayCommand]
    private void CloseConnect()
    {
        if (IsConnectOpen && IsBusy && _connectionGeneration == _generation) Cancel();
        IsConnectOpen = false;
    }
    [RelayCommand]
    private void Cancel()
    {
        lock (_taskGate)
        {
            _operation?.Cancel();
            _operation = null;
        }
    }
    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private Task RefreshAsync(CancellationToken cancellationToken) => LoadSelectionAsync(cancellationToken, forceRefresh: true);

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private Task ConnectAsync(CancellationToken cancellationToken) => Track(() => ConnectCoreAsync(cancellationToken));

    private async Task ConnectCoreAsync(CancellationToken cancellationToken)
    {
        Cancel();
        var generation = ++_generation;
        _connectionGeneration = generation;
        using var operation = BeginOperation(cancellationToken);
        IsBusy = true;
        Error = "";
        RepositoryInfo? connected = null;
        try
        {
            var result = await repositories.ConnectAsync(RemoteUrl.Trim(), Progress(generation), operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            int catalogGeneration = ++_catalogGeneration;
            var saved = await repositories.GetRepositoriesAsync(operation.Token);
            if (!IsCurrent(generation, operation.Token)) return;
            connected = result;
            if (catalogGeneration == _catalogGeneration) PublishRepositories(saved, _acceptedRepository?.Id, SelectedBranch);
            if (!IsCurrent(generation, operation.Token)) return;
            IsConnectOpen = false;
            RemoteUrl = "";
        }
        catch (OperationCanceledException) { if (IsCurrent(generation)) Status = "Connection canceled"; }
        catch (Exception ex) { if (IsCurrent(generation)) SetError(ex, "Could not connect"); }
        finally
        {
            if (_connectionGeneration == generation) _connectionGeneration = -1;
            FinishOperation(operation, generation);
        }

        if (connected is not null && IsCurrent(generation, cancellationToken))
        {
            connected = Repositories.FirstOrDefault(repository => repository.Id == connected.Id) ?? connected;
            ++_selectionVersion;
            SetSelection(connected, connected.DefaultBranch);
            await LoadSelectionAsync(cancellationToken);
        }
    }

    private Task LoadSelectionAsync(CancellationToken cancellationToken, bool forceRefresh = false) =>
        Track(() => LoadSelectionCoreAsync(cancellationToken, forceRefresh));

    private async Task LoadSelectionCoreAsync(CancellationToken cancellationToken, bool forceRefresh)
    {
        Cancel();
        var generation = ++_generation;
        var repository = SelectedRepository;
        var branch = SelectedBranch;
        if (repository is null || string.IsNullOrEmpty(branch))
        {
            if (_displayedRepository is not null) messenger.Send(new SnapshotChanged(_displayedRepository, null));
            _displayedRepository = null;
            IsBusy = false;
            Error = "";
            Status = "Ready";
            ProgressText = "";
            LastUpdated = "Choose a repository to get started";
            return;
        }

        using var operation = BeginOperation(cancellationToken);
        IsBusy = true;
        Error = "";
        Status = "Opening repository";
        ProgressText = "Reading cached history…";
        LastUpdated = "Loading…";
        _displayedRepository = repository;
        messenger.Send(new SnapshotChanged(repository, null));
        BranchSnapshot? cached = null;
        try
        {
            cached = await repositories.GetCachedSnapshotAsync(repository, branch, operation.Token);
            if (!IsCurrent(generation, operation.Token)) return;
            if (cached is not null)
            {
                messenger.Send(new SnapshotChanged(repository, cached));
                LastUpdated = $"Cached · {cached.RefreshedAt.LocalDateTime:g}";
                if (!forceRefresh && !RefreshOnOpen)
                {
                    Status = repository.IsDemo ? "Demo workspace · sample data" : "Cached history · refresh on open is off";
                    ProgressText = $"{cached.Commits.Count:N0} commits indexed · {cached.Files.Count:N0} current files";
                    return;
                }
            }
            Status = repository.IsDemo ? "Opening demo" : "Refreshing remote";
            var snapshot = await repositories.RefreshAsync(repository, branch, Progress(generation), operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            int catalogGeneration = ++_catalogGeneration;
            var saved = await repositories.GetRepositoriesAsync(operation.Token);
            if (!IsCurrent(generation, operation.Token)) return;
            if (catalogGeneration == _catalogGeneration) PublishRepositories(saved, repository.Id, branch);
            if (!IsCurrent(generation, operation.Token)) return;
            var updated = SelectedRepository ?? repository;
            _displayedRepository = updated;
            messenger.Send(new SnapshotChanged(updated, snapshot));
            Status = updated.IsDemo ? "Demo workspace · sample data" : "Up to date";
            LastUpdated = updated.IsDemo ? "Sample repository · no network connection" : $"Updated {snapshot.RefreshedAt.LocalDateTime:g}";
            ProgressText = $"{snapshot.Commits.Count:N0} commits indexed · {snapshot.Files.Count:N0} current files";
        }
        catch (OperationCanceledException)
        {
            if (IsCurrent(generation)) Status = cached is null ? "Canceled" : "Canceled · showing cached history";
        }
        catch (Exception ex)
        {
            if (IsCurrent(generation))
                SetError(ex, cached is null ? "Unable to load repository" : "Refresh failed · showing cached history");
        }
        finally { FinishOperation(operation, generation); }
    }

    private CancellationTokenSource BeginOperation(CancellationToken cancellationToken)
    {
        var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        lock (_taskGate) _operation = operation;
        return operation;
    }

    private bool IsCurrent(int generation, CancellationToken cancellationToken = default) =>
        !_disposed && generation == _generation && !cancellationToken.IsCancellationRequested;

    private void FinishOperation(CancellationTokenSource operation, int generation)
    {
        lock (_taskGate)
            if (ReferenceEquals(_operation, operation)) _operation = null;
        if (IsCurrent(generation)) IsBusy = false;
    }

    private IProgress<OperationProgress> Progress(int generation) => new DispatchProgress(value =>
        _ = Track(async () =>
        {
            try
            {
                await dispatcher.InvokeAsync(() =>
                {
                    if (!IsCurrent(generation)) return;
                    Status = value.Stage;
                    ProgressText = value.Detail;
                }, _lifetime.Token);
            }
            catch (OperationCanceledException) { }
        }));

    private void SetError(Exception ex, string status)
    {
        logger.LogWarning(ex, "Repository operation failed: {Status}", status);
        Status = status;
        Error = ex.Message;
    }

    [RelayCommand(CanExecute = nameof(CanRemoveRepository))]
    private Task RemoveRepositoryAsync(CancellationToken cancellationToken) => Track(() => RemoveRepositoryCoreAsync(cancellationToken));

    private async Task RemoveRepositoryCoreAsync(CancellationToken cancellationToken)
    {
        var selected = SelectedRepository;
        int selectionVersion = _selectionVersion;
        if (selected is null || !await dialogs.ConfirmAsync("Remove repository", $"Remove {selected.Name} from your saved repositories?")) return;
        if (_disposed || cancellationToken.IsCancellationRequested || selectionVersion != _selectionVersion) return;
        Cancel();
        var generation = ++_generation;
        using var operation = BeginOperation(cancellationToken);
        IsBusy = true;
        Error = "";
        IReadOnlyList<RepositoryInfo>? saved = null;
        try
        {
            await repositories.RemoveAsync(selected, operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            int catalogGeneration = ++_catalogGeneration;
            var result = await repositories.GetRepositoriesAsync(operation.Token);
            if (IsCurrent(generation, operation.Token)) saved = catalogGeneration == _catalogGeneration ? result : Repositories;
        }
        catch (OperationCanceledException) { if (IsCurrent(generation)) Status = "Removal canceled"; }
        catch (Exception ex) { if (IsCurrent(generation)) SetError(ex, "Could not remove repository"); }
        finally { FinishOperation(operation, generation); }
        if (saved is not null && IsCurrent(generation, cancellationToken))
        {
            PinnedRepositoryIds = PinnedRepositoryIds.Where(id => id != selected.Id).ToArray();
            var next = saved.FirstOrDefault();
            PublishRepositories(saved, next?.Id, next?.DefaultBranch);
            ++_selectionVersion;
            await LoadSelectionAsync(cancellationToken);
        }
    }

    private Task Track(Func<Task> action)
    {
        lock (_taskGate)
        {
            if (_disposed) return Task.CompletedTask;
            var task = action();
            if (!task.IsCompleted)
            {
                _pending.Add(task);
                _ = ForgetCompletedAsync(task);
            }
            return task;
        }
    }

    private async Task ForgetCompletedAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception) { /* Commands report errors; startup errors belong to their awaiting caller. */ }
        finally { lock (_taskGate) _pending.Remove(task); }
    }

    public Task ShutdownAsync()
    {
        lock (_taskGate)
        {
            if (_shutdown is not null) return _shutdown;
            Dispose();
            return _shutdown = AwaitPendingAsync(_pending.ToArray());
        }
    }

    private async Task AwaitPendingAsync(Task[] pending)
    {
        try { await Task.WhenAll(pending); }
        catch (Exception) { /* Already observed by the operation and its original caller. */ }
        finally { _lifetime.Dispose(); }
    }

    public void Dispose()
    {
        lock (_taskGate)
        {
            if (_disposed) return;
            _disposed = true;
            ++_generation;
            Cancel();
            _lifetime.Cancel();
        }
        messenger.UnregisterAll(this);
        ConnectCommand.NotifyCanExecuteChanged();
        RefreshCommand.NotifyCanExecuteChanged();
        RemoveRepositoryCommand.NotifyCanExecuteChanged();
    }

    private sealed class DispatchProgress(Action<OperationProgress> report) : IProgress<OperationProgress>
    {
        public void Report(OperationProgress value) => report(value);
    }
}
