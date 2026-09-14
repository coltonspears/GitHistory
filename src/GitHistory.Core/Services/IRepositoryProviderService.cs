using GitHistory.Core.Models;

namespace GitHistory.Core.Services;

public interface IRepositoryProviderService
{
    Task<IReadOnlyList<RemoteRepository>> DiscoverAsync(RepositoryImportRequest request, IProgress<OperationProgress>? progress, CancellationToken cancellationToken);
    Task<IReadOnlyList<PullRequestInfo>> GetPullRequestsAsync(RepositoryInfo repository, string commitSha, CancellationToken cancellationToken);
}
