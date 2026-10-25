using System.Net;
using System.Text.Json;
using TenantHub.IntegrationTests.Infrastructure;

namespace TenantHub.IntegrationTests;

public sealed class PipelineTests(OfflineApiFactory factory) : IClassFixture<OfflineApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Liveness_returns_200()
    {
        var response = await _client.GetAsync("/health/live", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/no-such-route")]
    public async Task Security_headers_are_on_every_response(string path)
    {
        var response = await _client.GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal("nosniff", Header(response, "X-Content-Type-Options"));
        Assert.Equal("strict-origin-when-cross-origin", Header(response, "Referrer-Policy"));
        Assert.Contains("camera=()", Header(response, "Permissions-Policy"), StringComparison.Ordinal);
        Assert.Equal("same-origin", Header(response, "Cross-Origin-Opener-Policy"));

        var csp = Header(response, "Content-Security-Policy");
        Assert.Contains("frame-ancestors 'none'", csp, StringComparison.Ordinal);
        Assert.Contains("script-src 'self'", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-inline", csp, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unknown_route_returns_problem_details_with_trace_id()
    {
        var response = await _client.GetAsync("/no-such-route", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var problem = await ReadProblemAsync(response);
        Assert.Equal(404, problem.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("traceId").GetString()));
    }

    [Fact]
    public async Task Unhandled_exception_returns_problem_details_without_the_exception_message()
    {
        var response = await _client.GetAsync(ApiFactory.ThrowingPath, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain("secret detail", body, StringComparison.Ordinal);
        var problem = await ReadProblemAsync(response, body);
        Assert.Equal(500, problem.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("traceId").GetString()));
    }

    [Theory]
    [InlineData("/openapi/v1.json")]
    [InlineData("/scalar")]
    public async Task Api_explorer_is_not_exposed_outside_development(string path)
    {
        var response = await _client.GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static string Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : "";

    private static async Task<JsonElement> ReadProblemAsync(HttpResponseMessage response, string? body = null)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        body ??= await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return JsonDocument.Parse(body).RootElement.Clone();
    }
}
