using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GitHistory.Core.Models;
using GitHistory.Core.Services;

namespace GitHistory.Infrastructure;

/// <summary>Read-only provider discovery. API credentials exist only in this service's process memory.</summary>
public sealed class RepositoryProviderService : IRepositoryProviderService, IDisposable
{
    private const int PageSize = 100;
    private const int MaximumPages = 100;
    private const int MaximumResponseBytes = 16 * 1024 * 1024;
    private readonly HttpClient http;
    private readonly bool ownsHttp;
    private readonly IProviderCommandRunner commands;
    private readonly ConcurrentDictionary<string, string> sessionTokens = new(StringComparer.OrdinalIgnoreCase);

    public RepositoryProviderService(HttpClient? httpClient = null, IProviderCommandRunner? commandRunner = null)
    {
        ownsHttp = httpClient is null;
        http = httpClient ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(45) };
        commands = commandRunner ?? new ProviderCommandRunner();
    }

    public async Task<IReadOnlyList<RemoteRepository>> DiscoverAsync(RepositoryImportRequest request, IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.AccessToken.Any(char.IsControl)) throw new ArgumentException("The access token contains an invalid character.");
        return request.Provider switch
        {
            RepositoryProvider.GitHub => await DiscoverGitHubAsync(request, progress, cancellationToken).ConfigureAwait(false),
            RepositoryProvider.AzureDevOps => await DiscoverAzureAsync(request, progress, cancellationToken).ConfigureAwait(false),
            _ => throw new ArgumentException("Choose GitHub or Azure DevOps.")
        };
    }

    private async Task<IReadOnlyList<RemoteRepository>> DiscoverGitHubAsync(RepositoryImportRequest request, IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
    {
        var repositories = new List<RemoteRepository>();
        for (var page = 1; page <= MaximumPages; page++)
        {
            progress?.Report(new("Loading repositories", $"GitHub · page {page} · {repositories.Count:N0} available repositories"));
            var endpoint = $"user/repos?visibility=all&affiliation=owner,collaborator,organization_member&sort=full_name&per_page={PageSize}&page={page}";
            using var document = await GitHubJsonAsync(endpoint, request.AccessToken, cancellationToken).ConfigureAwait(false);
            RequireArray(document.RootElement);
            foreach (var item in document.RootElement.EnumerateArray())
            {
                var fullName = String(item, "full_name");
                if (!string.IsNullOrWhiteSpace(request.Organization) && !fullName.StartsWith(request.Organization.Trim() + "/", StringComparison.OrdinalIgnoreCase)) continue;
                var cloneUrl = String(item, "clone_url");
                if (!RepositoryLinks.TryParse(cloneUrl, out var location) || location.Provider != RepositoryProvider.GitHub) continue;
                repositories.Add(new(String(item, "name"), fullName, location.WebUrl + ".git", location.WebUrl, Boolean(item, "private"), "GitHub"));
            }
            if (document.RootElement.GetArrayLength() < PageSize)
            {
                if (!string.IsNullOrEmpty(request.AccessToken)) sessionTokens["github.com"] = request.AccessToken;
                return repositories.DistinctBy(r => r.CloneUrl, StringComparer.OrdinalIgnoreCase).OrderBy(r => r.FullName, StringComparer.OrdinalIgnoreCase).ToArray();
            }
        }
        throw new InvalidOperationException("GitHub returned more than 10,000 accessible repositories. Narrow access with a fine-grained token.");
    }

    private async Task<IReadOnlyList<RemoteRepository>> DiscoverAzureAsync(RepositoryImportRequest request, IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
    {
        var organization = AzureOrganization(request.Organization);
        var project = request.Project.Trim();
        ValidateSegment(project, allowEmpty: true);
        var credential = await AzureCredentialAsync(organization, request.AccessToken, cancellationToken).ConfigureAwait(false);
        var baseUrl = $"https://dev.azure.com/{Escape(organization)}/{(project.Length == 0 ? "" : Escape(project) + "/")}_apis/git/repositories?api-version=7.1&includeAllUrls=true";
        var repositories = new List<RemoteRepository>();
        string? continuation = null;
        var seenContinuations = new HashSet<string>(StringComparer.Ordinal);
        for (var page = 1; page <= MaximumPages; page++)
        {
            progress?.Report(new("Loading repositories", $"Azure DevOps · {organization} · {repositories.Count:N0} available repositories"));
            var url = continuation is null ? baseUrl : baseUrl + "&continuationToken=" + Escape(continuation);
            using var response = await SendAsync(HttpMethod.Get, url, credential, null, cancellationToken).ConfigureAwait(false);
            using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            if (!document.RootElement.TryGetProperty("value", out var items)) throw InvalidResponse();
            RequireArray(items);
            foreach (var item in items.EnumerateArray())
            {
                if (Boolean(item, "isDisabled")) continue;
                if (!RepositoryLinks.TryParse(String(item, "remoteUrl"), out var location) || location.Provider != RepositoryProvider.AzureDevOps ||
                    !location.Owner.Equals(organization, StringComparison.OrdinalIgnoreCase)) continue;
                var isPrivate = !item.TryGetProperty("project", out var projectData) || String(projectData, "visibility") != "public";
                repositories.Add(new(String(item, "name"), $"{location.Owner}/{location.Project}/{location.Repository}", location.WebUrl, location.WebUrl, isPrivate, "Azure DevOps"));
            }
            continuation = response.Headers.TryGetValues("x-ms-continuationtoken", out var values) ? values.FirstOrDefault() : null;
            if (string.IsNullOrEmpty(continuation))
            {
                if (!string.IsNullOrEmpty(request.AccessToken)) sessionTokens["azure:" + organization] = request.AccessToken;
                return repositories.DistinctBy(r => r.CloneUrl, StringComparer.Ordinal).OrderBy(r => r.FullName, StringComparer.OrdinalIgnoreCase).ToArray();
            }
            if (!seenContinuations.Add(continuation)) throw new InvalidOperationException("Azure DevOps repeated a pagination token. Try importing again.");
        }
        throw new InvalidOperationException("Azure DevOps returned too many repository pages. Choose a project to narrow the import.");
    }

    public async Task<IReadOnlyList<PullRequestInfo>> GetPullRequestsAsync(RepositoryInfo repository, string commitSha, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (repository.IsDemo || !RepositoryLinks.TryParse(repository.RemoteUrl, out var location)) return [];
        if (commitSha.Length is not (40 or 64) || !commitSha.All(Uri.IsHexDigit)) throw new ArgumentException("A complete Git commit ID is required.");
        if (location.Provider == RepositoryProvider.GitHub)
        {
            var results = new List<PullRequestInfo>();
            sessionTokens.TryGetValue(location.Host, out var token);
            for (var page = 1; page <= MaximumPages; page++)
            {
                using var document = await GitHubJsonAsync($"repos/{Escape(location.Owner)}/{Escape(location.Repository)}/commits/{commitSha}/pulls?per_page={PageSize}&page={page}", token ?? "", cancellationToken, allowPublicFallback: true).ConfigureAwait(false);
                RequireArray(document.RootElement);
                foreach (var item in document.RootElement.EnumerateArray())
                {
                    if (!item.TryGetProperty("number", out var numberElement) || !numberElement.TryGetInt32(out var number) || number <= 0) continue;
                    var merged = item.TryGetProperty("merged_at", out var mergedAt) && mergedAt.ValueKind == JsonValueKind.String;
                    results.Add(new(number, String(item, "title"), merged ? "Merged" : String(item, "state"), NestedString(item, "user", "login"), RepositoryLinks.PullRequest(repository.RemoteUrl, number)!));
                }
                if (document.RootElement.GetArrayLength() < PageSize) return results.DistinctBy(r => r.Number).ToArray();
            }
            throw new InvalidOperationException("Too many pull requests are associated with this commit.");
        }

        var credential = await AzureCredentialAsync(location.Owner, "", cancellationToken).ConfigureAwait(false);
        using var repositoryResponse = await SendAsync(HttpMethod.Get, location.ApiBaseUrl + "?api-version=7.1", credential, null, cancellationToken).ConfigureAwait(false);
        using var repositoryDocument = await ReadJsonAsync(repositoryResponse, cancellationToken).ConfigureAwait(false);
        var repositoryId = String(repositoryDocument.RootElement, "id");
        if (!Guid.TryParse(repositoryId, out _)) throw InvalidResponse();
        var queryUrl = $"https://dev.azure.com/{Escape(location.Owner)}/{Escape(location.Project)}/_apis/git/repositories/{repositoryId}/pullrequestquery?api-version=7.1";
        var query = new AzurePullRequestQuery([new("commit", [commitSha]), new("lastMergeCommit", [commitSha])]);
        var body = JsonSerializer.Serialize(query, ProviderJsonContext.Default.AzurePullRequestQuery);
        using var response = await SendAsync(HttpMethod.Post, queryUrl, credential, body, cancellationToken).ConfigureAwait(false);
        using var azureDocument = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        if (!azureDocument.RootElement.TryGetProperty("results", out var queryResults)) throw InvalidResponse();
        RequireArray(queryResults);
        var azureResults = new List<PullRequestInfo>();
        foreach (var queryResult in queryResults.EnumerateArray())
        {
            if (!queryResult.TryGetProperty(commitSha, out var pullRequests)) continue;
            RequireArray(pullRequests);
            foreach (var item in pullRequests.EnumerateArray())
            {
                if (!item.TryGetProperty("pullRequestId", out var numberElement) || !numberElement.TryGetInt32(out var number) || number <= 0) continue;
                azureResults.Add(new(number, String(item, "title"), String(item, "status"), NestedString(item, "createdBy", "displayName"), RepositoryLinks.PullRequest(repository.RemoteUrl, number)!));
            }
        }
        return azureResults.DistinctBy(r => r.Number).ToArray();
    }

    private async Task<JsonDocument> GitHubJsonAsync(string endpoint, string token, CancellationToken cancellationToken, bool allowPublicFallback = false)
    {
        if (string.IsNullOrEmpty(token))
        {
            try
            {
                var json = await commands.RunAsync("gh", ["api", "--hostname", "github.com", "--method", "GET", endpoint],
                    "GitHub sign-in is unavailable. Run 'gh auth login' or supply a token with repository access in Import.", cancellationToken).ConfigureAwait(false);
                return JsonDocument.Parse(json);
            }
            catch (InvalidOperationException) when (allowPublicFallback) { }
        }
        using var response = await SendAsync(HttpMethod.Get, "https://api.github.com/" + endpoint,
            string.IsNullOrEmpty(token) ? null : new("Bearer", token), null, cancellationToken).ConfigureAwait(false);
        return await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<AuthenticationHeaderValue> AzureCredentialAsync(string organization, string suppliedToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(suppliedToken) && sessionTokens.TryGetValue("azure:" + organization, out var savedToken)) suppliedToken = savedToken;
        if (!string.IsNullOrEmpty(suppliedToken)) return new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(":" + suppliedToken)));
        var token = await commands.RunAsync("az", ["account", "get-access-token", "--resource", "499b84ac-1321-427f-aa17-267ca6975798", "--query", "accessToken", "--output", "tsv"],
            "Azure DevOps sign-in is unavailable. Run 'az login' for the organization's tenant or supply a PAT with Code (Read) access in Import.", cancellationToken).ConfigureAwait(false);
        token = token.Trim();
        if (string.IsNullOrEmpty(token) || token.Any(char.IsControl)) throw new InvalidOperationException("Azure CLI did not return a valid access token. Run 'az login' again.");
        return new("Bearer", token);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, AuthenticationHeaderValue? credential, string? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = credential;
        request.Headers.UserAgent.ParseAdd("GitHistory/1.1");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        HttpResponseMessage response;
        try { response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new InvalidOperationException("The repository provider timed out. Check your connection and try again."); }
        catch (HttpRequestException) { throw new InvalidOperationException("The repository provider could not be reached. Check your connection and try again."); }
        if (response.IsSuccessStatusCode) return response;
        var status = response.StatusCode;
        response.Dispose();
        throw new InvalidOperationException(status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "The provider denied access. Check your CLI sign-in or token permissions, including organization SSO authorization.",
            HttpStatusCode.NotFound => "The repository or organization is unavailable to this account. Check its name and your private repository permissions.",
            (HttpStatusCode)429 => "The provider's request limit was reached. Try again later.",
            _ => $"The repository provider returned HTTP {(int)status}. Try again later."
        });
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > MaximumResponseBytes) throw new InvalidOperationException("The provider response is too large. Narrow the import to a project.");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (output.Length + read > MaximumResponseBytes) throw new InvalidOperationException("The provider response is too large. Narrow the import to a project.");
            output.Write(buffer, 0, read);
        }
        try { return JsonDocument.Parse(output.ToArray()); }
        catch (JsonException) { throw InvalidResponse(); }
    }

    private static string AzureOrganization(string input)
    {
        input = input.Trim();
        if (Uri.TryCreate(input, UriKind.Absolute, out var uri))
        {
            if (uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo) || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
                throw new ArgumentException("Use an organization name or https://dev.azure.com/organization.");
            if (uri.Host.Equals("dev.azure.com", StringComparison.OrdinalIgnoreCase)) input = uri.AbsolutePath.Trim('/');
            else if (uri.Host.EndsWith(".visualstudio.com", StringComparison.OrdinalIgnoreCase) && uri.Host.Count(c => c == '.') == 2 && uri.AbsolutePath == "/") input = uri.Host[..^".visualstudio.com".Length];
            else throw new ArgumentException("Use an organization name or https://dev.azure.com/organization.");
        }
        if (string.IsNullOrEmpty(input) || !input.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
            throw new ArgumentException("Enter your Azure DevOps organization name, for example 'contoso'.");
        return input;
    }

    private static void ValidateSegment(string value, bool allowEmpty)
    {
        if ((!allowEmpty && string.IsNullOrWhiteSpace(value)) || value is "." or ".." || value.Any(char.IsControl) || value.IndexOfAny(['/', '\\', '?', '#']) >= 0)
            throw new ArgumentException("Enter a project name without URL separators.");
    }
    private static string Escape(string text) => Uri.EscapeDataString(text);
    private static string String(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static string NestedString(JsonElement item, string parent, string child) => item.TryGetProperty(parent, out var value) && value.ValueKind == JsonValueKind.Object ? String(value, child) : "";
    private static bool Boolean(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
    private static void RequireArray(JsonElement value) { if (value.ValueKind != JsonValueKind.Array) throw InvalidResponse(); }
    private static InvalidOperationException InvalidResponse() => new("The repository provider returned an unexpected response. Try again later.");
    public void Dispose() { sessionTokens.Clear(); if (ownsHttp) http.Dispose(); }
}

internal sealed record AzurePullRequestQuery(AzurePullRequestQueryInput[] Queries);
internal sealed record AzurePullRequestQueryInput(string Type, string[] Items);
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AzurePullRequestQuery))]
internal partial class ProviderJsonContext : JsonSerializerContext;
