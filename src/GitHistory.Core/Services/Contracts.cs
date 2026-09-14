using GitHistory.Core.Models;

namespace GitHistory.Core.Services;

public interface IRepositoryService
{
    Task<IReadOnlyList<RepositoryInfo>> GetRepositoriesAsync(CancellationToken cancellationToken = default);
    Task<RepositoryInfo> ConnectAsync(string remoteUrl, IProgress<OperationProgress>? progress, CancellationToken cancellationToken);
    Task<BranchSnapshot?> GetCachedSnapshotAsync(RepositoryInfo repository, string branch, CancellationToken cancellationToken);
    Task<BranchSnapshot> RefreshAsync(RepositoryInfo repository, string branch, IProgress<OperationProgress>? progress, CancellationToken cancellationToken);
    Task<DiffResult> GetDiffAsync(RepositoryInfo repository, FileChange change, CancellationToken cancellationToken);
    Task RemoveAsync(RepositoryInfo repository, CancellationToken cancellationToken);
}
public interface IHistoryQueryService
{
    IReadOnlyList<FileRow> Query(BranchSnapshot snapshot, HistoryFilter filter, DateTimeOffset now);
    IReadOnlyList<FileChange> GetFileHistory(BranchSnapshot snapshot, FileRow row);
    IReadOnlyList<ActivityPoint> GetActivity(BranchSnapshot snapshot, HistoryFilter filter);
    int GetContributorCount(BranchSnapshot snapshot, HistoryFilter filter);
}
public interface IUserSettingsStore
{
    Task<UserSettings> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(UserSettings settings, CancellationToken cancellationToken = default);
}
public interface IClipboardService { void SetText(string text); }
public interface IUiDispatcher { Task InvokeAsync(Action action, CancellationToken cancellationToken = default); }
public interface IAppearanceService { void SetTheme(string theme); void ResetLayout(); }
public interface IDialogService { Task<bool> ConfirmAsync(string title, string message); }
