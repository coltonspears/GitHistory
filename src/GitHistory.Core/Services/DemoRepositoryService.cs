using GitHistory.Core.Models;

namespace GitHistory.Core.Services;

/// <summary>Explicitly labeled local sample data shares the production query and presentation pipeline.</summary>
public sealed class DemoRepositoryService(IRepositoryService inner, TimeProvider clock) : IRepositoryService
{
    public async Task<IReadOnlyList<RepositoryInfo>> GetRepositoriesAsync(CancellationToken cancellationToken = default) =>
        new[] { DemoData.Repository }.Concat(await inner.GetRepositoriesAsync(cancellationToken)).ToArray();
    public Task<RepositoryInfo> ConnectAsync(string remoteUrl, IProgress<OperationProgress>? progress, CancellationToken cancellationToken) => inner.ConnectAsync(remoteUrl, progress, cancellationToken);
    public Task<BranchSnapshot?> GetCachedSnapshotAsync(RepositoryInfo repository, string branch, CancellationToken cancellationToken) =>
        repository.IsDemo ? Task.FromResult<BranchSnapshot?>(DemoData.CreateSnapshot(branch, clock.GetUtcNow())) : inner.GetCachedSnapshotAsync(repository, branch, cancellationToken);
    public Task<BranchSnapshot> RefreshAsync(RepositoryInfo repository, string branch, IProgress<OperationProgress>? progress, CancellationToken cancellationToken) =>
        repository.IsDemo ? Task.FromResult(DemoData.CreateSnapshot(branch, clock.GetUtcNow())) : inner.RefreshAsync(repository, branch, progress, cancellationToken);
    public Task<DiffResult> GetDiffAsync(RepositoryInfo repository, FileChange change, CancellationToken cancellationToken) =>
        repository.IsDemo ? Task.FromResult(DemoData.CreateDiff(change)) : inner.GetDiffAsync(repository, change, cancellationToken);
    public Task RemoveAsync(RepositoryInfo repository, CancellationToken cancellationToken) => repository.IsDemo ? Task.CompletedTask : inner.RemoveAsync(repository, cancellationToken);
}
