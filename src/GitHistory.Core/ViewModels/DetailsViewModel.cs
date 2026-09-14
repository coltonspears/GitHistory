using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using GitHistory.Core.Models;
using GitHistory.Core.Services;
using Microsoft.Extensions.Logging;

namespace GitHistory.Core.ViewModels;

public sealed partial class DetailsViewModel : ObservableObject, IDisposable
{
    private readonly IRepositoryService _repositories;
    private readonly IHistoryQueryService _queries;
    private readonly IMessenger _messenger;
    private readonly IClipboardService _clipboard;
    private readonly ILogger<DetailsViewModel> _logger;
    private RepositoryInfo? _repository;
    private CancellationTokenSource? _diffOperation;
    private int _version;
    private readonly object _taskGate = new();
    private readonly HashSet<Task> _pending = [];
    private bool _disposed;
    private Task? _shutdown;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasSelection)), NotifyCanExecuteChangedFor(nameof(CopyPathCommand))]
    public partial FileRow? SelectedFile { get; set; }
    [ObservableProperty] public partial IReadOnlyList<FileChange> History { get; set; } = [];
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(CopyShaCommand))] public partial FileChange? SelectedChange { get; set; }
    [ObservableProperty] public partial IReadOnlyList<DiffLine> DiffLines { get; set; } = [];
    [ObservableProperty] public partial string DiffSummary { get; set; } = "Select a file to explore its history";
    [ObservableProperty] public partial bool IsBusy { get; set; }
    public bool HasSelection => SelectedFile is not null;

    public DetailsViewModel(IRepositoryService repositories, IHistoryQueryService queries, IMessenger messenger, IClipboardService clipboard, ILogger<DetailsViewModel> logger)
    {
        _repositories = repositories; _queries = queries; _messenger = messenger; _clipboard = clipboard; _logger = logger;
        messenger.Register<FileSelected>(this, static (recipient, message) => ((DetailsViewModel)recipient).SelectFile(message));
    }
    private void SelectFile(FileSelected message)
    {
        if (_disposed) return;
        _repository = message.Repository;
        SelectedFile = message.File;
        History = message.Snapshot is not null && message.File is not null ? _queries.GetFileHistory(message.Snapshot, message.File) : [];
        SelectedChange = History.FirstOrDefault(change => change == message.File?.LatestChange) ?? History.FirstOrDefault();
        if (SelectedChange is null) { CancelCurrent(); ++_version; DiffLines = []; DiffSummary = "Select a file to explore its history"; IsBusy = false; }
    }
    partial void OnSelectedChangeChanged(FileChange? value)
    {
        _messenger.Send(new CommitSelected(_repository, value));
        _ = LoadDiffAsync();
    }
    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task LoadDiffAsync()
    {
        lock (_taskGate)
        {
            if (_disposed) return Task.CompletedTask;
            var task = LoadDiffCoreAsync();
            if (!task.IsCompleted)
            {
                _pending.Add(task);
                _ = ForgetCompletedAsync(task);
            }
            return task;
        }
    }

    private async Task LoadDiffCoreAsync()
    {
        CancelCurrent();
        var version = ++_version;
        var repository = _repository; var change = SelectedChange;
        if (repository is null || change is null)
        {
            DiffLines = [];
            DiffSummary = SelectedFile is null ? "Select a file to explore its history" : "Select a commit to inspect its diff";
            IsBusy = false;
            return;
        }
        using var operation = new CancellationTokenSource();
        _diffOperation = operation;
        IsBusy = true; DiffLines = []; DiffSummary = "Loading diff…";
        try
        {
            var diff = await _repositories.GetDiffAsync(repository, change, operation.Token);
            if (version != _version) return;
            DiffLines = diff.Lines;
            DiffSummary = diff.Summary;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to load diff");
            if (version == _version) DiffSummary = $"Diff unavailable: {ex.Message}";
        }
        finally
        {
            lock (_taskGate)
                if (ReferenceEquals(_diffOperation, operation)) _diffOperation = null;
            if (version == _version) IsBusy = false;
        }
    }
    private bool CanCopyPath() => !_disposed && SelectedFile is not null;
    private bool CanCopySha() => !_disposed && SelectedChange is not null;
    [RelayCommand(CanExecute = nameof(CanCopyPath))] private void CopyPath() => _clipboard.SetText(SelectedFile!.Path);
    [RelayCommand(CanExecute = nameof(CanCopySha))] private void CopySha() => _clipboard.SetText(SelectedChange!.Commit.Oid);
    private void CancelCurrent()
    {
        lock (_taskGate)
        {
            _diffOperation?.Cancel();
            _diffOperation = null;
        }
    }

    private async Task ForgetCompletedAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception) { /* The operation reports its own error to the view model. */ }
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

    private static async Task AwaitPendingAsync(Task[] pending)
    {
        try { await Task.WhenAll(pending); }
        catch (Exception) { /* Operation errors have already been observed and reported. */ }
    }

    public void Dispose()
    {
        lock (_taskGate)
        {
            if (_disposed) return;
            _disposed = true;
            CancelCurrent();
            ++_version;
        }
        _messenger.UnregisterAll(this);
        CopyPathCommand.NotifyCanExecuteChanged();
        CopyShaCommand.NotifyCanExecuteChanged();
    }
}
