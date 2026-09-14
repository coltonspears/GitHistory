using GitHistory.Core.Models;
using GitHistory.Core.Services;
using GitHistory.Infrastructure;
using Xunit.Abstractions;

namespace GitHistory.Tests.Providers;

public sealed class ProviderSmokeTests(ITestOutputHelper output)
{
    [ProviderSmokeFact]
    [Trait("Category", "ProviderSmoke")]
    public async Task Production_GitHub_import_reads_existing_CLI_signin_and_private_repository_metadata()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using var service = new RepositoryProviderService();
        var rows = await service.DiscoverAsync(new(RepositoryProvider.GitHub), null, cancellation.Token);
        Assert.NotEmpty(rows);
        Assert.All(rows, row => Assert.True(RepositoryLinks.TryParse(row.CloneUrl, out _)));
        Assert.Equal(rows.Count, rows.DistinctBy(row => row.CloneUrl, StringComparer.OrdinalIgnoreCase).Count());
        output.WriteLine($"GitHub provider import passed: {rows.Count:N0} accessible repositories, {rows.Count(row => row.IsPrivate):N0} private. Repository names and credentials omitted.");
    }
}

public sealed class ProviderSmokeFactAttribute : FactAttribute
{
    public ProviderSmokeFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("GITHISTORY_PROVIDER_SMOKE") != "1")
            Skip = "Set GITHISTORY_PROVIDER_SMOKE=1 to read accessible GitHub repository metadata using your existing gh sign-in.";
    }
}
