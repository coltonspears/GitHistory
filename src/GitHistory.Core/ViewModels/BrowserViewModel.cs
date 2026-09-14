using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using GitHistory.Core.Models;
using GitHistory.Core.Services;
using Microsoft.Extensions.Logging;

namespace GitHistory.Core.ViewModels;

public sealed partial class BrowserViewModel : ObservableObject, IDisposable
{
    private readonly IHistoryQueryService _queries;
    private readonly IMessenger _messenger;
    private readonly IUiDispatcher _dispatcher;
    private readonly TimeProvider _clock;
    private readonly ILogger<BrowserViewModel> _logger;
    private BranchSnapshot? _snapshot;
    private RepositoryInfo? _repository;
    private int _queryVersion;
    private bool _changingDates;
    private bool _updatingTree;
    private readonly object _taskGate = new();
    private readonly HashSet<Task> _pending = [];
    private readonly CancellationTokenSource _lifetime = new();
    private bool _disposed;
    private Task? _shutdown;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(FilesCount))] public partial IReadOnlyList<FileRow> Files { get; set; } = [];
    [ObservableProperty] public partial IReadOnlyList<DirectoryNode> TreeNodes { get; set; } = [];
    [ObservableProperty] public partial IReadOnlyList<DirectoryNode> VisibleTreeNodes { get; set; } = [];
    [ObservableProperty] public partial string FolderSearch { get; set; } = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(FolderLabel))] public partial DirectoryNode? SelectedNode { get; set; }
    public string FolderLabel => string.IsNullOrEmpty(SelectedNode?.Path) ? "All folders" : SelectedNode.Path;
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial IReadOnlyList<string> Authors { get; set; } = ["All authors"];
    [ObservableProperty] public partial string Search { get; set; } = "";
    [ObservableProperty] public partial string SelectedAuthor { get; set; } = "All authors";
    [ObservableProperty] public partial FileRow? SelectedFile { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Heading)), NotifyPropertyChangedFor(nameof(Subtitle))]
    public partial FileViewMode Mode { get; set; }
    public IReadOnlyList<FileViewMode> ViewModes { get; } = Enum.GetValues<FileViewMode>();
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsCustomRange))] public partial string DatePreset { get; set; } = "7 days";
    public IReadOnlyList<string> DatePresets { get; } = ["24 hours", "7 days", "30 days", "Custom"];
    [ObservableProperty] public partial DateTime FromDate { get; set; }
    [ObservableProperty] public partial DateTime UntilDate { get; set; }
    [ObservableProperty] public partial DateTime RangeStart { get; set; }
    [ObservableProperty] public partial DateTime RangeEnd { get; set; }
    [ObservableProperty] public partial IReadOnlyList<ActivityPoint> Activity { get; set; } = [];
    [ObservableProperty] public partial ActivityPoint? SelectedActivity { get; set; }
    [ObservableProperty] public partial int CommitCount { get; set; }
    [ObservableProperty] public partial int ContributorCount { get; set; }
    [ObservableProperty] public partial string QueryError { get; set; } = "";
    public int FilesCount => Files.Count;
    public bool IsCustomRange => DatePreset == "Custom";
    public string Heading => Mode == FileViewMode.RecentChanges ? "Recent changes" : "All files";
    public string Subtitle => Mode == FileViewMode.RecentChanges ? "What changed, when it landed, and who was behind it." : "Every current file, ordered by its last change on this branch.";

    public BrowserViewModel(IHistoryQueryService queries, IMessenger messenger, IUiDispatcher dispatcher, TimeProvider clock, ILogger<BrowserViewModel> logger)
    {
        _queries = queries; _messenger = messenger; _dispatcher = dispatcher; _clock = clock; _logger = logger;
        _changingDates = true;
        FromDate = clock.GetLocalNow().Date.AddDays(-7); UntilDate = clock.GetLocalNow().Date;
        RangeStart = FromDate; RangeEnd = UntilDate.AddDays(1);
        _changingDates = false;
        messenger.Register<SnapshotChanged>(this, static (recipient, message) => ((BrowserViewModel)recipient).SetSnapshot(message));
    }
    private void SetSnapshot(SnapshotChanged message)
    {
        if (_disposed) return;
        var folder = _repository?.Id == message.Repository?.Id ? SelectedNode?.Path : null;
        _snapshot = message.Snapshot; _repository = message.Repository;
        Authors = new[] { "All authors" }.Concat(_snapshot?.Commits.Select(c => c.AuthorName).Distinct().Order().ToArray() ?? []).ToArray();
        if (!Authors.Contains(SelectedAuthor)) SelectedAuthor = "All authors";
        _updatingTree = true;
        TreeNodes = BuildTree(_snapshot?.Files ?? []);
        SelectedNode = folder is null ? null : TreeNodes.FirstOrDefault(n => n.Path == folder);
        UpdateVisibleTree();
        _updatingTree = false;
        SelectedFile = null;
        _ = ApplyFilterAsync();
    }
    partial void OnSearchChanged(string value) => QueueFilter();
    partial void OnSelectedAuthorChanged(string value) => QueueFilter();
    partial void OnSelectedNodeChanged(DirectoryNode? value) { if (!_updatingTree) QueueFilter(); }
    partial void OnFolderSearchChanged(string value) => UpdateVisibleTree();
    [RelayCommand] private void SelectFolder(DirectoryNode? node) => SelectedNode = node;
    private void UpdateVisibleTree()
    {
        var search = (FolderSearch ?? "").Trim();
        if (search.Length == 0) { VisibleTreeNodes = TreeNodes; return; }
        var ids = new HashSet<string>(StringComparer.Ordinal) { "root" };
        foreach (var node in TreeNodes.Where(n => n.Path.Contains(search, StringComparison.OrdinalIgnoreCase)))
        {
            ids.Add(node.Id);
            var path = node.Path;
            while (path.Contains('/')) { path = path[..path.LastIndexOf('/')]; ids.Add("folder:" + path); }
        }
        VisibleTreeNodes = TreeNodes.Where(n => ids.Contains(n.Id)).ToArray();
    }
    [RelayCommand] private void ClearFilters()
    {
        _changingDates = true;
        Search = ""; FolderSearch = ""; SelectedAuthor = "All authors"; SelectedNode = null;
        _changingDates = false;
        QueueFilter();
    }
    [RelayCommand] private void CancelQuery()
    {
        Interlocked.Increment(ref _queryVersion);
        IsBusy = false;
    }
    partial void OnModeChanged(FileViewMode value) => QueueFilter();
    partial void OnSelectedFileChanged(FileRow? value) => _messenger.Send(new FileSelected(_repository, _snapshot, value));
    partial void OnDatePresetChanged(string value)
    {
        if (_changingDates) return;
        _changingDates = true;
        if (value != "Custom")
        {
            UntilDate = _clock.GetLocalNow().Date;
            FromDate = UntilDate.AddDays(value == "24 hours" ? -1 : value == "30 days" ? -30 : -7);
            RangeStart = FromDate; RangeEnd = UntilDate.AddDays(1);
        }
        _changingDates = false;
        QueueFilter();
    }
    partial void OnFromDateChanged(DateTime value) { if (!_changingDates) { SyncRange(); QueueFilter(); } }
    partial void OnUntilDateChanged(DateTime value) { if (!_changingDates) { SyncRange(); QueueFilter(); } }
    private void SyncRange()
    {
        _changingDates = true; DatePreset = "Custom"; RangeStart = FromDate.Date; RangeEnd = UntilDate.Date.AddDays(1); _changingDates = false;
    }
    partial void OnRangeStartChanged(DateTime value) => ApplyRange();
    partial void OnRangeEndChanged(DateTime value) => ApplyRange();
    private void ApplyRange()
    {
        if (_changingDates || RangeEnd <= RangeStart) return;
        _changingDates = true;
        Mode = FileViewMode.RecentChanges; DatePreset = "Custom"; FromDate = RangeStart.Date; UntilDate = RangeEnd.AddTicks(-1).Date;
        _changingDates = false; QueueFilter();
    }
    partial void OnSelectedActivityChanged(ActivityPoint? value)
    {
        if (value is null) return;
        _changingDates = true; Mode = FileViewMode.RecentChanges; DatePreset = "Custom"; FromDate = value.Date.Date; UntilDate = value.Date.Date;
        RangeStart = FromDate; RangeEnd = UntilDate.AddDays(1); _changingDates = false; QueueFilter();
    }
    private void QueueFilter() { if (!_changingDates && !_disposed) _ = ApplyFilterAsync(); }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task ApplyFilterAsync()
    {
        lock (_taskGate)
        {
            if (_disposed) return Task.CompletedTask;
            var task = ApplyFilterCoreAsync();
            if (!task.IsCompleted)
            {
                _pending.Add(task);
                _ = ForgetCompletedAsync(task);
            }
            return task;
        }
    }

    private async Task ApplyFilterCoreAsync()
    {
        var version = Interlocked.Increment(ref _queryVersion);
        IsBusy = false;
        var snapshot = _snapshot;
        if (snapshot is null) { Files = []; Activity = []; CommitCount = 0; ContributorCount = 0; return; }
        var now = _clock.GetUtcNow();
        DateTimeOffset? from = null, until = null;
        if (Mode == FileViewMode.RecentChanges)
        {
            from = DatePreset == "Custom" ? new DateTimeOffset(FromDate.Date) : now.AddDays(DatePreset == "24 hours" ? -1 : DatePreset == "30 days" ? -30 : -7);
            until = DatePreset == "Custom" ? new DateTimeOffset(UntilDate.Date.AddDays(1)) : now.AddTicks(1);
        }
        if (from >= until) { QueryError = "Choose an end date on or after the start date."; Files = []; Activity = []; SelectedFile = null; CommitCount = 0; ContributorCount = 0; return; }
        QueryError = "";
        IsBusy = true;
        var filter = new HistoryFilter(Mode, from, until, Search ?? "", SelectedAuthor == "All authors" ? "" : SelectedAuthor ?? "", SelectedNode?.Path ?? "");
        try
        {
            var result = await Task.Run(() =>
            {
                var rows = _queries.Query(snapshot, filter, now);
                var selectedActivity = _queries.GetActivity(snapshot, filter);
                var overview = filter.From is null && filter.Until is null ? selectedActivity : _queries.GetActivity(snapshot, filter with { From = null, Until = null });
                return (rows, selectedActivity, overview, contributors: _queries.GetContributorCount(snapshot, filter));
            }, _lifetime.Token);
            if (_disposed || version != _queryVersion) return;
            await _dispatcher.InvokeAsync(() =>
            {
                if (_disposed || version != _queryVersion) return;
                var path = SelectedFile?.Path;
                Files = result.Item1;
                Activity = result.Item3;
                CommitCount = result.Item2.Sum(p => p.Commits);
                ContributorCount = result.contributors;
                SelectedFile = Files.FirstOrDefault(f => f.Path == path) ?? Files.FirstOrDefault();
            }, _lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (_disposed || version != _queryVersion) return;
            _logger.LogError(ex, "History query failed");
            try
            {
                await _dispatcher.InvokeAsync(() => { if (!_disposed && version == _queryVersion) QueryError = ex.Message; }, _lifetime.Token);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        }
        finally
        {
            if (!_disposed && version == _queryVersion) IsBusy = false;
        }
    }
    private static IReadOnlyList<DirectoryNode> BuildTree(IReadOnlyList<TreeFile> files)
    {
        var folders = new Dictionary<string, int>(StringComparer.Ordinal) { [""] = files.Count };
        foreach (var file in files)
        {
            var offset = file.Path.IndexOf('/');
            while (offset >= 0)
            {
                var path = file.Path[..offset]; folders[path] = folders.GetValueOrDefault(path) + 1;
                offset = file.Path.IndexOf('/', offset + 1);
            }
        }
        return folders.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new DirectoryNode(p.Key.Length == 0 ? "root" : "folder:" + p.Key,
            p.Key.Length == 0 ? null : p.Key.Contains('/') ? "folder:" + p.Key[..p.Key.LastIndexOf('/')] : "root",
            p.Key.Length == 0 ? "All folders" : p.Key[(p.Key.LastIndexOf('/') + 1)..], p.Key, p.Value)).ToArray();
    }
    private async Task ForgetCompletedAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception) { /* Query failures are reported by the operation itself. */ }
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
        catch (Exception) { /* Already observed and reported by the query operation. */ }
        finally { _lifetime.Dispose(); }
    }

    public void Dispose()
    {
        lock (_taskGate)
        {
            if (_disposed) return;
            _disposed = true;
            Interlocked.Increment(ref _queryVersion);
            _lifetime.Cancel();
        }
        _messenger.UnregisterAll(this);
    }
}
