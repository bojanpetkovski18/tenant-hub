using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using TenantHub.Api.Features.Auth;

namespace TenantHub.IntegrationTests.Infrastructure;

/// <summary>Uses the API like the Angular app: load the session first (gets the XSRF cookie), then act.</summary>
public sealed class BrowserClient : IDisposable
{
    public static readonly Uri BaseAddress = new("https://localhost");
    public const string Password = "Correct-Horse-9";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly XsrfHeaderHandler _xsrf;

    public BrowserClient(ApiFactory factory)
    {
        Cookies = new CookieContainer();
        _xsrf = new XsrfHeaderHandler(Cookies);
        Http = factory.CreateDefaultClient(BaseAddress, new CookieContainerHandler(Cookies), _xsrf);
    }

    public HttpClient Http { get; }

    public CookieContainer Cookies { get; }

    /// <summary>Turn off to simulate a cross-site request that cannot read the XSRF cookie.</summary>
    public bool SendXsrfHeader { get => _xsrf.Enabled; set => _xsrf.Enabled = value; }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static string NewEmail() => $"user-{Guid.NewGuid():N}@test.example";

    public async Task<SessionResponse> SessionAsync()
    {
        var response = await Http.GetAsync("/api/auth/session", Ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<SessionResponse>(Json, Ct))!;
    }

    public async Task<HttpResponseMessage> SignUpAsync(
        string email,
        string organizationName = "Brightline Cleaning Co.",
        string password = Password,
        string displayName = "Olivia Bennett",
        string timeZone = "Europe/Amsterdam")
    {
        await EnsureXsrfAsync();
        return await Http.PostAsJsonAsync("/api/auth/sign-up", new { displayName, email, password, organizationName, timeZone }, Ct);
    }

    public async Task<HttpResponseMessage> SignInAsync(string email, string password = Password)
    {
        await EnsureXsrfAsync();
        return await Http.PostAsJsonAsync("/api/auth/sign-in", new { email, password }, Ct);
    }

    public async Task<HttpResponseMessage> SignOutAsync()
    {
        await EnsureXsrfAsync();
        return await Http.PostAsync("/api/auth/sign-out", content: null, Ct);
    }

    public async Task<HttpResponseMessage> CreateOrganizationAsync(string name, string timeZone = "UTC")
    {
        await EnsureXsrfAsync();
        return await Http.PostAsJsonAsync("/api/orgs", new { name, timeZone }, Ct);
    }

    public async Task<HttpResponseMessage> PostJsonAsync(string path, object body)
    {
        await EnsureXsrfAsync();
        return await Http.PostAsJsonAsync(path, body, Ct);
    }

    public async Task<HttpResponseMessage> PatchJsonAsync(string path, object body)
    {
        await EnsureXsrfAsync();
        return await Http.PatchAsJsonAsync(path, body, Ct);
    }

    public async Task<HttpResponseMessage> SendJsonAsync(HttpMethod method, string path, object? body)
    {
        await EnsureXsrfAsync();
        using var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
        return await Http.SendAsync(request, Ct);
    }

    private async Task EnsureXsrfAsync()
    {
        if (Cookies.GetCookies(BaseAddress)["XSRF-TOKEN"] is null)
        {
            await SessionAsync();
        }
    }

    public void Dispose() => Http.Dispose();
}

public sealed class XsrfHeaderHandler(CookieContainer cookies) : DelegatingHandler
{
    public bool Enabled { get; set; } = true;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var unsafeMethod = request.Method != HttpMethod.Get && request.Method != HttpMethod.Head;
        var token = cookies.GetCookies(BrowserClient.BaseAddress)["XSRF-TOKEN"]?.Value;
        if (Enabled && unsafeMethod && token is not null)
        {
            request.Headers.Add("X-XSRF-TOKEN", token);
        }

        return base.SendAsync(request, cancellationToken);
    }
}
