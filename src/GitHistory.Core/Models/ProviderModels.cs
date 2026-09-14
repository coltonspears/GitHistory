namespace GitHistory.Core.Models;

public enum RepositoryProvider { GitHub, AzureDevOps }

/// <summary>Credentials are for the current process only and must never be persisted or logged.</summary>
public sealed record RepositoryImportRequest(RepositoryProvider Provider, string Organization = "", string Project = "", string AccessToken = "")
{
    public override string ToString() => $"{Provider} import (credentials redacted)";
}
public sealed record RemoteRepository(string Name, string FullName, string CloneUrl, string WebUrl, bool IsPrivate, string Provider);
public sealed record PullRequestInfo(int Number, string Title, string State, string Author, string Url);
public sealed record RepositoryLocation(RepositoryProvider Provider, string Host, string Owner, string Project, string Repository, string WebUrl, string ApiBaseUrl);
