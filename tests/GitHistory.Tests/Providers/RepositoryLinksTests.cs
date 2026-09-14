using GitHistory.Core.Models;
using GitHistory.Core.Services;

namespace GitHistory.Tests.Providers;

public sealed class RepositoryLinksTests
{
    [Theory]
    [InlineData("https://github.com/owner/repo.git", "https://github.com/owner/repo")]
    [InlineData("git@github.com:owner/repo.git", "https://github.com/owner/repo")]
    [InlineData("ssh://git@github.com/owner/repo.git", "https://github.com/owner/repo")]
    [InlineData("https://name@dev.azure.com/org/My%20Project/_git/My%20Repo", "https://dev.azure.com/org/My%20Project/_git/My%20Repo")]
    [InlineData("git@ssh.dev.azure.com:v3/org/My%20Project/My%20Repo", "https://dev.azure.com/org/My%20Project/_git/My%20Repo")]
    [InlineData("ssh://git@ssh.dev.azure.com/v3/org/Project/Repo", "https://dev.azure.com/org/Project/_git/Repo")]
    [InlineData("https://org.visualstudio.com/Project/_git/Repo", "https://dev.azure.com/org/Project/_git/Repo")]
    [InlineData("https://org.visualstudio.com/DefaultCollection/Project/_git/Repo", "https://dev.azure.com/org/Project/_git/Repo")]
    public void Supported_remotes_have_canonical_credential_free_browser_links(string remote, string expected)
        => Assert.Equal(expected, RepositoryLinks.Repository(remote));

    [Theory]
    [InlineData("https://github.com.evil.test/org/repo")]
    [InlineData("https://gitlab.com/org/repo")]
    [InlineData("http://github.com/org/repo")]
    [InlineData("https://user:secret@github.com/org/repo")]
    [InlineData("https://github.com/org/repo?token=secret")]
    [InlineData("https://github.com/org/repo#anchor")]
    [InlineData("https://dev.azure.com.evil.test/org/project/_git/repo")]
    [InlineData("https://org.visualstudio.com.evil.test/project/_git/repo")]
    [InlineData("https://github.com/org%2Frepo/other")]
    [InlineData("file:///C:/repo")]
    public void Unsupported_or_credential_bearing_remotes_are_not_guessed(string remote)
        => Assert.Null(RepositoryLinks.Repository(remote));

    [Fact]
    public void Enterprise_host_requires_explicit_known_host_configuration()
    {
        Assert.False(RepositoryLinks.TryParse("git@git.contoso.test:team/repo.git", out _));
        Assert.True(RepositoryLinks.TryParse("git@git.contoso.test:team/repo.git", out var location, ["git.contoso.test"]));
        Assert.Equal("https://git.contoso.test/api/v3", location.ApiBaseUrl);
        Assert.Equal(RepositoryProvider.GitHub, location.Provider);
    }

    [Fact]
    public void File_and_branch_segments_are_escaped_and_azure_revisions_are_typed()
    {
        const string gitHub = "git@github.com:owner/repo.git";
        const string azure = "git@ssh.dev.azure.com:v3/org/project/repo";
        Assert.Equal("https://github.com/owner/repo/blob/feature%2Fnew/src/a%20%23%3F.cs", RepositoryLinks.File(gitHub, "src/a #?.cs", "feature/new"));
        Assert.Equal("https://github.com/owner/repo/tree/feature%2Fnew", RepositoryLinks.Branch(gitHub, "feature/new"));
        Assert.Equal("https://dev.azure.com/org/project/_git/repo?path=%2Fsrc%2Fa%20%23%3F.cs&version=GCabc1234&_a=contents", RepositoryLinks.File(azure, "src/a #?.cs", "abc1234", isCommit: true));
        Assert.Equal("https://dev.azure.com/org/project/_git/repo?version=GBfeature%2Fnew", RepositoryLinks.Branch(azure, "feature/new"));
        Assert.Null(RepositoryLinks.Commit(gitHub, "abc/def"));
    }

    [Theory]
    [InlineData("https://github.com/o/r", "Merge pull request #42 from owner/feature", 42, "/pull/42")]
    [InlineData("https://github.com/o/r", "Fix a bug (#13)", 13, "/pull/13")]
    [InlineData("https://dev.azure.com/o/p/_git/r", "Merged PR 123: Fix a bug", 123, "/pullrequest/123")]
    public void Offline_pull_request_hints_are_explicitly_unverified(string remote, string subject, int number, string suffix)
    {
        var hint = Assert.IsType<PullRequestInfo>(RepositoryLinks.InferPullRequest(remote, subject));
        Assert.Equal(number, hint.Number);
        Assert.EndsWith(suffix, hint.Url);
        Assert.Contains("unverified", hint.State);
        Assert.Empty(hint.Author);
    }

    [Fact]
    public void Arbitrary_issue_references_are_not_presented_as_pull_requests()
    {
        Assert.Null(RepositoryLinks.InferPullRequest("https://github.com/o/r", "Fixes #42"));
        Assert.Null(RepositoryLinks.InferPullRequest("https://github.com/o/r", "(#42) might be an issue"));
        Assert.Null(RepositoryLinks.InferPullRequest("https://github.com/o/r", "Merge pull request #999999999999"));
    }
}
