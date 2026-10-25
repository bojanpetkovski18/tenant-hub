using System.Net;
using System.Net.Http.Json;
using TenantHub.IntegrationTests.Infrastructure;

namespace TenantHub.IntegrationTests;

/// <summary>
/// M11: the API serves the SPA from its web root (deep links get index.html, with the CSP), keeps 404 problems for
/// unknown API routes and missing files, and trusts forwarded headers only when configured to.
/// </summary>
public sealed class ProductionServingTests : IDisposable
{
    private const string IndexHtml = "<!doctype html><html><body><app-root></app-root></body></html>";
    private readonly string _webRoot = Directory.CreateTempSubdirectory("tenanthub-wwwroot-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_webRoot, recursive: true);

    [Theory]
    [InlineData("/")]
    [InlineData("/o/brightline/jobs")]
    [InlineData("/invite")]
    public async Task Pages_get_the_spa_with_the_csp_and_no_caching(string path)
    {
        File.WriteAllText(Path.Combine(_webRoot, "index.html"), IndexHtml);
        await using var factory = new OfflineApiFactory().WithWebHostBuilder(b => b.UseSetting(Microsoft.AspNetCore.Hosting.WebHostDefaults.WebRootKey, _webRoot));
        using var client = factory.CreateClient();

        var response = await client.GetAsync(path, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(IndexHtml, await response.Content.ReadAsStringAsync(Ct));
        Assert.Contains("script-src 'self'", response.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
        Assert.Equal("no-cache", response.Headers.CacheControl?.ToString());
    }

    [Theory]
    [InlineData("GET", "/api/no-such-thing")]
    [InlineData("GET", "/missing-file.js")]
    [InlineData("POST", "/o/brightline/jobs")]
    public async Task Unknown_api_routes_files_and_unsafe_methods_stay_404_problems(string method, string path)
    {
        File.WriteAllText(Path.Combine(_webRoot, "index.html"), IndexHtml);
        await using var factory = new OfflineApiFactory().WithWebHostBuilder(b => b.UseSetting(Microsoft.AspNetCore.Hosting.WebHostDefaults.WebRootKey, _webRoot));
        using var client = factory.CreateClient();

        var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path), Ct);

        // An unsafe request without the XSRF header is refused first (400); the point is that it is never the page.
        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Without_a_build_pages_are_404_problems_as_before()
    {
        await using var factory = new OfflineApiFactory();
        using var client = factory.CreateClient();

        await ProblemAssert.IsProblemAsync(await client.GetAsync("/o/brightline/jobs", Ct), HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Forwarded_headers_are_ignored_unless_configured()
    {
        await using var factory = new OfflineApiFactory();
        using var client = factory.CreateClient();

        var seen = await SendForwardedAsync(client);

        Assert.NotEqual("203.0.113.7", seen.Ip);
        Assert.Equal("http", seen.Scheme);
    }

    [Fact]
    public async Task Forwarded_headers_from_a_trusted_proxy_set_the_client_address_and_scheme()
    {
        await using var factory = new OfflineApiFactory().WithWebHostBuilder(b => b
            .UseSetting("ForwardedHeaders:Enabled", "true")
            .UseSetting("ForwardedHeaders:KnownNetworks:0", "0.0.0.0/0")
            .UseSetting("ForwardedHeaders:KnownNetworks:1", "::/0"));
        using var client = factory.CreateClient();

        var seen = await SendForwardedAsync(client);

        Assert.Equal(("203.0.113.7", "https"), (seen.Ip, seen.Scheme));
    }

    private static async Task<Connection> SendForwardedAsync(HttpClient client)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ApiFactory.ConnectionPath);
        request.Headers.Add("X-Forwarded-For", "203.0.113.7");
        request.Headers.Add("X-Forwarded-Proto", "https");
        var response = await client.SendAsync(request, Ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<Connection>(Ct))!;
    }

    private sealed record Connection(string? Ip, string Scheme);
}
