using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitHistory.Core.Models;
using GitHistory.Core.Services;

namespace GitHistory.Core.ViewModels;

public sealed partial class ImportRepositoryItem(RemoteRepository repository) : ObservableObject
{
    public RemoteRepository Repository { get; } = repository;
    [ObservableProperty] public partial bool IsSelected { get; set; }
}

public sealed partial class RepositoryImportViewModel(IRepositoryProviderService providerService, IRepositoryService repositories,
    WorkspaceViewModel workspace, IUiDispatcher dispatcher) : ObservableObject, IDisposable
{
    private readonly OperationLifetime lifetime = new();
    private CancellationTokenSource? operation;
    private int generation;
    [ObservableProperty] public partial bool IsOpen { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsAzureDevOps)), NotifyPropertyChangedFor(nameof(OrganizationHint))]
    public partial RepositoryProvider Provider { get; set; }
    public bool IsAzureDevOps => Provider == RepositoryProvider.AzureDevOps;
    public string OrganizationHint => IsAzureDevOps ? "Organization name or dev.azure.com URL" : "GitHub owner (optional)";
    [ObservableProperty] public partial string Organization { get; set; } = "";
    [ObservableProperty] public partial string Project { get; set; } = "";
    [ObservableProperty] public partial string AccessToken { get; set; } = "";
    [ObservableProperty] public partial string Search { get; set; } = "";
    [ObservableProperty] public partial IReadOnlyList<ImportRepositoryItem> Repositories { get; set; } = [];
    [ObservableProperty] public partial IReadOnlyList<ImportRepositoryItem> VisibleRepositories { get; set; } = [];
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(DiscoverCommand)), NotifyCanExecuteChangedFor(nameof(ImportSelectedCommand))]
    public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial string Status { get; set; } = "Find repositories available to your account, including private repositories.";
    [ObservableProperty] public partial string Error { get; set; } = "";
    public IReadOnlyList<RepositoryProvider> Providers { get; } = Enum.GetValues<RepositoryProvider>();
    partial void OnSearchChanged(string value) => Filter();
    partial void OnProviderChanged(RepositoryProvider value)
    {
        Cancel(); ++generation; IsBusy = false; Repositories = []; Filter(); AccessToken = ""; Error = "";
    }
    private void Filter() => VisibleRepositories = Repositories.Where(r => r.Repository.FullName.Contains((Search ?? "").Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
    private bool CanStart() => !lifetime.IsClosed && !IsBusy;
    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task DiscoverAsync() => lifetime.Run(async token =>
    {
        Cancel();
        var version = ++generation;
        using var active = CancellationTokenSource.CreateLinkedTokenSource(token);
        operation = active;
        IsBusy = true; Error = ""; Status = "Loading repositories…";
        var request = new RepositoryImportRequest(Provider, Organization?.Trim() ?? "", Project?.Trim() ?? "", AccessToken ?? "");
        try
        {
            var result = await providerService.DiscoverAsync(request, Progress(version), active.Token);
            if (!Current(version, active.Token)) return;
            var known = workspace.Repositories.Select(r => r.RemoteUrl.TrimEnd('/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Repositories = result.Select(r => new ImportRepositoryItem(r) { IsSelected = false }).ToArray();
            Filter();
            Status = $"{result.Count:N0} repositories found. Select the ones to import. {result.Count(r => known.Contains(r.CloneUrl.TrimEnd('/'))):N0} already saved.";
        }
        catch (OperationCanceledException) { if (Current(version)) Status = "Discovery canceled"; }
        catch (Exception ex) { if (Current(version)) { Error = ex.Message; Status = "Could not list repositories"; } }
        finally { if (ReferenceEquals(operation, active)) operation = null; if (Current(version)) IsBusy = false; }
    });
    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task ImportSelectedAsync() => lifetime.Run(async token =>
    {
        var selected = Repositories.Where(r => r.IsSelected).ToArray();
        if (selected.Length == 0) { Error = "Select at least one repository to import."; return; }
        Cancel(); var version = ++generation;
        using var active = CancellationTokenSource.CreateLinkedTokenSource(token);
        operation = active;
        IsBusy = true; Error = "";
        var failures = new List<string>();
        var imported = 0;
        try
        {
            foreach (var item in selected)
            {
                active.Token.ThrowIfCancellationRequested();
                Status = $"Importing {imported + failures.Count + 1} of {selected.Length}: {item.Repository.FullName}";
                try
                {
                    await repositories.ConnectAsync(item.Repository.CloneUrl, null, active.Token);
                    imported++;
                    if (Current(version)) item.IsSelected = false;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { failures.Add($"{item.Repository.FullName}: {ex.Message}"); }
            }
            if (Current(version)) Status = $"Imported {imported:N0} repositories. Open a workspace to fetch its history.";
        }
        catch (OperationCanceledException) { if (Current(version)) Status = $"Import canceled; {imported:N0} repositories saved."; }
        finally
        {
            // Publish successfully saved repositories even after cancellation without selecting a different workspace.
            if (!lifetime.IsClosed && imported > 0)
            {
                try { await workspace.ReloadRepositoriesAsync(cancellationToken: token); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { failures.Add($"Refresh saved workspaces: {ex.Message}"); }
            }
            if (ReferenceEquals(operation, active)) operation = null;
            if (Current(version)) { IsBusy = false; Error = string.Join(Environment.NewLine, failures); }
        }
    });
    private IProgress<OperationProgress> Progress(int version) => new InlineProgress(value =>
        _ = lifetime.Run(async token =>
        {
            try { await dispatcher.InvokeAsync(() => { if (Current(version)) Status = $"{value.Stage} · {value.Detail}"; }, token); }
            catch (OperationCanceledException) { }
        }));
    private bool Current(int version, CancellationToken token = default) => !lifetime.IsClosed && version == generation && !token.IsCancellationRequested;
    [RelayCommand] private void SelectAll() { foreach (var item in VisibleRepositories) item.IsSelected = true; }
    [RelayCommand] private void ClearSelection() { foreach (var item in Repositories) item.IsSelected = false; }
    [RelayCommand] private void Cancel()
    {
        bool wasBusy = IsBusy;
        ++generation;
        operation?.Cancel();
        IsBusy = false;
        if (wasBusy) Status = "Canceled. Repositories already imported remain saved.";
    }
    [RelayCommand] private void Close() { Cancel(); IsOpen = false; AccessToken = ""; }
    public Task ShutdownAsync() { Close(); return lifetime.ShutdownAsync(); }
    public void Dispose() { Close(); lifetime.Dispose(); }
    private sealed class InlineProgress(Action<OperationProgress> report) : IProgress<OperationProgress> { public void Report(OperationProgress value) => report(value); }
}
