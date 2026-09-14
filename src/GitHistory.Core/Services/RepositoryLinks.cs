using System.Globalization;
using System.Text.RegularExpressions;
using GitHistory.Core.Models;

namespace GitHistory.Core.Services;

/// <summary>Browser links for supported hosts. Unknown hosts are never guessed to be GitHub.</summary>
public static partial class RepositoryLinks
{
    public static bool TryParse(string? remote, out RepositoryLocation location, IEnumerable<string>? knownGitHubHosts = null)
    {
        location = null!;
        if (string.IsNullOrWhiteSpace(remote) || remote.Any(char.IsControl) || remote.Contains('\\')) return false;
        remote = remote.Trim();
        if (!remote.Contains("://", StringComparison.Ordinal))
        {
            var scp = ScpRemote().Match(remote);
            if (scp.Success) remote = $"ssh://{scp.Groups[1].Value}/{scp.Groups[2].Value}";
        }
        if (!Uri.TryCreate(remote, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "ssh") ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || uri.UserInfo.Contains(':')) return false;
        var parts = uri.AbsolutePath.Trim('/').Split('/').Select(Uri.UnescapeDataString).ToArray();
        if (parts.Any(p => string.IsNullOrWhiteSpace(p) || p is "." or ".." || p.Contains('/') || p.Contains('\\') || p.Any(char.IsControl))) return false;
        var host = uri.IdnHost.ToLowerInvariant();
        string owner, project, repository;
        if (host == "ssh.dev.azure.com" && parts.Length == 4 && parts[0] == "v3")
        {
            owner = parts[1]; project = parts[2]; repository = parts[3];
        }
        else if (host == "dev.azure.com" && parts.Length == 4 && parts[2] == "_git")
        {
            owner = parts[0]; project = parts[1]; repository = parts[3];
        }
        else if (host.EndsWith(".visualstudio.com", StringComparison.Ordinal) && host.Count(c => c == '.') == 2 &&
                 ((parts.Length == 3 && parts[1] == "_git") || (parts.Length == 4 && parts[0] == "DefaultCollection" && parts[2] == "_git")))
        {
            owner = host[..^".visualstudio.com".Length]; project = parts[^3]; repository = parts[^1];
        }
        else
        {
            if (parts.Length != 2 || !(host == "github.com" || knownGitHubHosts?.Contains(host, StringComparer.OrdinalIgnoreCase) == true)) return false;
            repository = parts[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? parts[1][..^4] : parts[1];
            if (string.IsNullOrEmpty(repository)) return false;
            var web = $"https://{host}/{Escape(parts[0])}/{Escape(repository)}";
            location = new(RepositoryProvider.GitHub, host, parts[0], "", repository, web,
                host == "github.com" ? "https://api.github.com" : $"https://{host}/api/v3");
            return true;
        }
        var baseUrl = $"https://dev.azure.com/{Escape(owner)}/{Escape(project)}";
        location = new(RepositoryProvider.AzureDevOps, "dev.azure.com", owner, project, repository,
            $"{baseUrl}/_git/{Escape(repository)}", $"{baseUrl}/_apis/git/repositories/{Escape(repository)}");
        return true;
    }

    public static string? Repository(string remote) => TryParse(remote, out var value) ? value.WebUrl : null;
    public static string? Branch(string remote, string branch) => !string.IsNullOrEmpty(branch) && TryParse(remote, out var value)
        ? value.Provider == RepositoryProvider.GitHub ? $"{value.WebUrl}/tree/{Escape(branch)}" : $"{value.WebUrl}?version={Escape("GB" + branch)}" : null;
    public static string? File(string remote, string path, string branchOrCommit, bool isCommit = false) =>
        !string.IsNullOrEmpty(path) && !string.IsNullOrEmpty(branchOrCommit) && TryParse(remote, out var value)
        ? value.Provider == RepositoryProvider.GitHub
            ? $"{value.WebUrl}/blob/{Escape(branchOrCommit)}/{string.Join('/', path.Split('/').Select(Escape))}"
            : $"{value.WebUrl}?path={Escape("/" + path.TrimStart('/'))}&version={Escape((isCommit ? "GC" : "GB") + branchOrCommit)}&_a=contents"
        : null;
    public static string? Commit(string remote, string sha) => ValidSha().IsMatch(sha) && TryParse(remote, out var value)
        ? $"{value.WebUrl}/commit/{sha}" : null;
    public static string? PullRequest(string remote, int number) => number > 0 && TryParse(remote, out var value)
        ? $"{value.WebUrl}/{(value.Provider == RepositoryProvider.GitHub ? "pull" : "pullrequest")}/{number.ToString(CultureInfo.InvariantCulture)}" : null;

    /// <summary>Offline hint only: the title/state deliberately identify that metadata has not been fetched.</summary>
    public static PullRequestInfo? InferPullRequest(string remote, string subject)
    {
        if (!TryParse(remote, out var value)) return null;
        var match = value.Provider == RepositoryProvider.GitHub ? GitHubMerge().Match(subject) : AzureMerge().Match(subject);
        if (!match.Success || !int.TryParse(match.Groups["number"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number <= 0) return null;
        return new(number, subject, "From commit message · unverified", "", PullRequest(remote, number)!);
    }

    private static string Escape(string text) => Uri.EscapeDataString(text);
    [GeneratedRegex(@"^(?:[^@/:]+@)?([^/:]+):(.+)$", RegexOptions.CultureInvariant)] private static partial Regex ScpRemote();
    [GeneratedRegex(@"\A[0-9a-fA-F]{7,64}\z", RegexOptions.CultureInvariant)] private static partial Regex ValidSha();
    [GeneratedRegex(@"(?:^Merge pull request #(?<number>\d+)\b.*$|\(#(?<number>\d+)\)$)", RegexOptions.CultureInvariant)] private static partial Regex GitHubMerge();
    [GeneratedRegex(@"^Merged PR (?<number>\d+):", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)] private static partial Regex AzureMerge();
}
