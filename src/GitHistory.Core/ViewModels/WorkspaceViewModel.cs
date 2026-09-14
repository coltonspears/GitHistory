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
    private int _connectionGeneration = -1;
    private bool _settingSelection;
    private bool _disposed;
    private Task? _shutdown;
    [ObservableProperty] public partial IReadOnlyList<RepositoryInfo> Repositories { get; set; } = [];
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
    public IRelayCommand RefreshCancelCommand => CancelCommand;

    public Task InitializeAsync(UserSettings settings, CancellationToken cancellationToken = default) =>
        Track(() => InitializeCoreAsync(settings, cancellationToken));

    private async Task InitializeCoreAsync(UserSettings settings, CancellationToken cancellationToken)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var saved = await repositories.GetRepositoriesAsync(operation.Token);
        operation.Token.ThrowIfCancellationRequested();
        Repositories = saved;
        var selected = saved.FirstOrDefault(r => r.Id == settings.SelectedRepositoryId) ?? saved.FirstOrDefault();
        SetSelection(selected, selected?.Branches.Contains(settings.SelectedBranch ?? "") == true
            ? settings.SelectedBranch : selected?.DefaultBranch);
        await LoadSelectionAsync(operation.Token);
    }

    partial void OnSelectedRepositoryChanged(RepositoryInfo? value)
    {
        OnPropertyChanged(nameof(IsDemo));
        RefreshCommand.NotifyCanExecuteChanged();
        RemoveRepositoryCommand.NotifyCanExecuteChanged();
        if (_settingSelection || _disposed) return;
        SetSelection(value, value?.DefaultBranch);
        _ = LoadSelectionAsync(CancellationToken.None);
    }

    partial void OnSelectedBranchChanged(string? value)
    {
        if (!_settingSelection && !_disposed) _ = LoadSelectionAsync(CancellationToken.None);
    }

    private void SetSelection(RepositoryInfo? repository, string? branch)
    {
        _settingSelection = true;
        try
        {
            SelectedRepository = repository;
            Branches = repository?.Branches ?? [];
            SelectedBranch = branch;
        }
        finally { _settingSelection = false; }
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
    private Task RefreshAsync(CancellationToken cancellationToken) => LoadSelectionAsync(cancellationToken);

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
            var saved = await repositories.GetRepositoriesAsync(operation.Token);
            if (!IsCurrent(generation, operation.Token)) return;
            connected = result;
            Repositories = saved;
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
            SetSelection(connected, connected.DefaultBranch);
            await LoadSelectionAsync(cancellationToken);
        }
    }

    private Task LoadSelectionAsync(CancellationToken cancellationToken) => Track(() => LoadSelectionCoreAsync(cancellationToken));

    private async Task LoadSelectionCoreAsync(CancellationToken cancellationToken)
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
            }
            Status = repository.IsDemo ? "Opening demo" : "Refreshing remote";
            var snapshot = await repositories.RefreshAsync(repository, branch, Progress(generation), operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            var saved = await repositories.GetRepositoriesAsync(operation.Token);
            if (!IsCurrent(generation, operation.Token)) return;
            var updated = saved.FirstOrDefault(item => item.Id == repository.Id) ?? repository;
            Repositories = saved;
            SetSelection(updated, branch);
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
        if (selected is null || !await dialogs.ConfirmAsync("Remove repository", $"Remove {selected.Name} from your saved repositories?")) return;
        if (_disposed || cancellationToken.IsCancellationRequested) return;
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
            var result = await repositories.GetRepositoriesAsync(operation.Token);
            if (IsCurrent(generation, operation.Token)) saved = result;
        }
        catch (OperationCanceledException) { if (IsCurrent(generation)) Status = "Removal canceled"; }
        catch (Exception ex) { if (IsCurrent(generation)) SetError(ex, "Could not remove repository"); }
        finally { FinishOperation(operation, generation); }
        if (saved is not null && IsCurrent(generation, cancellationToken))
        {
            Repositories = saved;
            var next = saved.FirstOrDefault();
            SetSelection(next, next?.DefaultBranch);
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
