using System.Net;
using System.Text;
using System.Text.Json;
using GitHistory.Core.Models;
using GitHistory.Infrastructure;

namespace GitHistory.Tests.Providers;

public sealed class RepositoryProviderServiceTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

    [Fact]
    public async Task GitHub_import_includes_private_collaborator_and_organization_repositories_and_uses_safe_pagination()
    {
        var requests = new List<string>();
        using var http = Client(async (request, _) =>
        {
            requests.Add(request.RequestUri!.AbsoluteUri);
            Assert.Equal("api.github.com", request.RequestUri.Host);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("session-secret", request.Headers.Authorization.Parameter);
            Assert.Contains("visibility=all", request.RequestUri.Query);
            Assert.Contains("owner,collaborator,organization_member", request.RequestUri.Query);
            var rows = requests.Count == 1 ? Enumerable.Range(0, 100).Select(i => GitHubRepo(i)).ToArray() : [GitHubRepo(100)];
            var response = Json(JsonSerializer.Serialize(rows));
            response.Headers.TryAddWithoutValidation("Link", "<https://evil.test/steal>; rel=\"next\"");
            await Task.CompletedTask;
            return response;
        });
        using var service = new RepositoryProviderService(http, new FakeCommands());
        var rows = await service.DiscoverAsync(new(RepositoryProvider.GitHub, AccessToken: "session-secret"), null, default);
        Assert.Equal(101, rows.Count);
        Assert.All(rows, row => Assert.True(row.IsPrivate));
        Assert.Equal(2, requests.Count);
        Assert.Contains("page=2", requests[1]);
    }

    [Fact]
    public async Task Existing_GitHub_cli_signin_is_used_without_secret_arguments()
    {
        var commands = new FakeCommands((executable, arguments) =>
        {
            Assert.Equal("gh", executable);
            Assert.Equal(["api", "--hostname", "github.com", "--method", "GET"], arguments.Take(5));
            return JsonSerializer.Serialize(new[] { GitHubRepo(1) });
        });
        using var service = new RepositoryProviderService(commandRunner: commands);
        var rows = await service.DiscoverAsync(new(RepositoryProvider.GitHub), null, default);
        Assert.Single(rows);
        Assert.Equal(1, commands.Calls);
    }

    [Fact]
    public async Task Imported_GitHub_token_is_reused_for_associated_pull_requests_only_in_memory()
    {
        using var http = Client((request, _) =>
        {
            Assert.Equal("secret", request.Headers.Authorization!.Parameter);
            return Task.FromResult(Json(request.RequestUri!.AbsolutePath == "/user/repos" ? "[]" :
                "[{\"number\":7,\"title\":\"Safer refresh\",\"state\":\"closed\",\"merged_at\":\"2026-09-14T01:00:00Z\",\"user\":{\"login\":\"author\"},\"html_url\":\"https://evil.test/\"}]"));
        });
        using var service = new RepositoryProviderService(http, new FakeCommands());
        await service.DiscoverAsync(new(RepositoryProvider.GitHub, AccessToken: "secret"), null, default);
        var pullRequest = Assert.Single(await service.GetPullRequestsAsync(Repo("git@github.com:owner/repo.git"), Sha, default));
        Assert.Equal("Merged", pullRequest.State);
        Assert.Equal("author", pullRequest.Author);
        Assert.Equal("https://github.com/owner/repo/pull/7", pullRequest.Url);
    }

    [Fact]
    public async Task Azure_PAT_discovery_removes_username_and_queries_both_normal_and_merge_commits()
    {
        var calls = 0;
        using var http = Client(async (request, token) =>
        {
            calls++;
            Assert.Equal("dev.azure.com", request.RequestUri!.Host);
            Assert.Equal("Basic", request.Headers.Authorization!.Scheme);
            Assert.Equal(":private-pat", Encoding.UTF8.GetString(Convert.FromBase64String(request.Headers.Authorization.Parameter!)));
            if (calls == 1)
            {
                Assert.Contains("/org/My%20Project/_apis/git/repositories", request.RequestUri.AbsoluteUri);
                return Json("{\"value\":[{\"name\":\"Repo\",\"remoteUrl\":\"https://username@dev.azure.com/org/My%20Project/_git/Repo\",\"project\":{\"visibility\":\"private\"}},{\"name\":\"Malicious\",\"remoteUrl\":\"https://dev.azure.com/other/project/_git/repo\"}]} ");
            }
            if (calls == 2) return Json("{\"id\":\"e9c04255-e4eb-42f9-81a2-7d1d616f7965\"}");
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Contains("/e9c04255-e4eb-42f9-81a2-7d1d616f7965/pullrequestquery", request.RequestUri.AbsolutePath);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Assert.Equal("commit", body.RootElement.GetProperty("queries")[0].GetProperty("type").GetString());
            Assert.Equal("lastMergeCommit", body.RootElement.GetProperty("queries")[1].GetProperty("type").GetString());
            var pr = "[{\"pullRequestId\":23,\"title\":\"UI polish\",\"status\":\"completed\",\"createdBy\":{\"displayName\":\"Ada\"}}]";
            return Json("{\"results\":[{\"" + Sha + "\":" + pr + "},{\"" + Sha + "\":" + pr + "}]}");
        });
        using var service = new RepositoryProviderService(http, new FakeCommands());
        var row = Assert.Single(await service.DiscoverAsync(new(RepositoryProvider.AzureDevOps, "https://dev.azure.com/org", "My Project", "private-pat"), null, default));
        Assert.True(row.IsPrivate);
        Assert.DoesNotContain("username", row.CloneUrl);
        var pr = Assert.Single(await service.GetPullRequestsAsync(Repo(row.CloneUrl), Sha, default));
        Assert.Equal("Ada", pr.Author);
        Assert.Equal("https://dev.azure.com/org/My%20Project/_git/Repo/pullrequest/23", pr.Url);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task Azure_CLI_uses_DevOps_Entra_resource_and_token_only_in_http_header()
    {
        var commands = new FakeCommands((executable, arguments) =>
        {
            Assert.Equal("az", executable);
            Assert.Contains("499b84ac-1321-427f-aa17-267ca6975798", arguments);
            Assert.DoesNotContain("entra-secret", arguments);
            return "entra-secret\r\n";
        });
        using var http = Client((request, _) =>
        {
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("entra-secret", request.Headers.Authorization.Parameter);
            Assert.DoesNotContain("entra-secret", request.RequestUri!.AbsoluteUri);
            return Task.FromResult(Json("{\"value\":[]}"));
        });
        using var service = new RepositoryProviderService(http, commands);
        Assert.Empty(await service.DiscoverAsync(new(RepositoryProvider.AzureDevOps, "org"), null, default));
    }

    [Theory]
    [InlineData("https://dev.azure.com.evil.test/org")]
    [InlineData("https://dev.azure.com/org?secret=yes")]
    [InlineData("https://user:pass@dev.azure.com/org")]
    [InlineData("https://dev.azure.com:444/org")]
    [InlineData("org/../../other")]
    public async Task Invalid_Azure_organization_never_receives_credentials(string organization)
    {
        var commands = new FakeCommands();
        using var http = Client((_, _) => throw new Xunit.Sdk.XunitException("Unexpected HTTP request"));
        using var service = new RepositoryProviderService(http, commands);
        await Assert.ThrowsAsync<ArgumentException>(() => service.DiscoverAsync(new(RepositoryProvider.AzureDevOps, organization, AccessToken: "secret"), null, default));
        Assert.Equal(0, commands.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Redirect)]
    public async Task Error_bodies_and_redirect_targets_never_leak_tokens_into_exceptions(HttpStatusCode code)
    {
        using var http = Client((_, _) => Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent("accessToken=secret") }));
        using var service = new RepositoryProviderService(http, new FakeCommands());
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.DiscoverAsync(new(RepositoryProvider.GitHub, AccessToken: "secret"), null, default));
        Assert.DoesNotContain("secret", error.ToString());
        Assert.DoesNotContain("secret", new RepositoryImportRequest(RepositoryProvider.GitHub, AccessToken: "secret").ToString());
    }

    [Fact]
    public async Task Cancellation_interrupts_pending_provider_requests()
    {
        using var started = new SemaphoreSlim(0);
        using var http = Client(async (_, token) =>
        {
            started.Release();
            await Task.Delay(Timeout.Infinite, token);
            return Json("[]");
        });
        using var service = new RepositoryProviderService(http, new FakeCommands());
        using var cancellation = new CancellationTokenSource();
        var operation = service.DiscoverAsync(new(RepositoryProvider.GitHub, AccessToken: "secret"), null, cancellation.Token);
        Assert.True(await started.WaitAsync(TimeSpan.FromSeconds(3)));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
    }

    [Fact]
    public async Task Azure_pagination_stays_in_organization_and_rejects_repeated_tokens()
    {
        var count = 0;
        using var http = Client((request, _) =>
        {
            count++;
            Assert.StartsWith("https://dev.azure.com/org/_apis/", request.RequestUri!.AbsoluteUri);
            if (count == 2) Assert.Contains("continuationToken=https%3A%2F%2Fevil.test", request.RequestUri.AbsoluteUri);
            var response = Json("{\"value\":[]}");
            response.Headers.TryAddWithoutValidation("x-ms-continuationtoken", "https://evil.test");
            return Task.FromResult(response);
        });
        using var service = new RepositoryProviderService(http, new FakeCommands());
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DiscoverAsync(new(RepositoryProvider.AzureDevOps, "org", AccessToken: "secret"), null, default));
        Assert.Equal(2, count);
    }

    private static object GitHubRepo(int index) => new { name = "repo" + index, full_name = "owner/repo" + index, clone_url = "https://github.com/owner/repo" + index + ".git", @private = true };
    private static RepositoryInfo Repo(string url) => new("id", "Repo", url, "main", ["main"]);
    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private static HttpClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) => new(new Handler(send));
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
    private sealed class FakeCommands(Func<string, IReadOnlyList<string>, string>? response = null) : IProviderCommandRunner
    {
        public int Calls { get; private set; }
        public Task<string> RunAsync(string executable, IReadOnlyList<string> arguments, string failureMessage, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(response?.Invoke(executable, arguments) ?? throw new Xunit.Sdk.XunitException("Unexpected CLI request"));
        }
    }
}
