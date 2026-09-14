namespace GitHistory.Core.Services;

public interface IDesktopIntegration
{
    Task<string?> PickFolderAsync(string? initialFolder, CancellationToken cancellationToken);
    void OpenBrowser(string url);
    Task OpenExplorerAsync(string localFolder, string? repositoryPath, CancellationToken cancellationToken);
    Task OpenCursorAsync(string localFolder, string? repositoryPath, string executable, CancellationToken cancellationToken);
    void OpenDataFolder();
}
