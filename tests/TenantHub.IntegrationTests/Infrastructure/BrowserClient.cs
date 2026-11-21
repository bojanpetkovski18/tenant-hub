using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using TenantHub.Api.Features.Auth;

namespace TenantHub.IntegrationTests.Infrastructure;

/// <summary>Uses the API like the Angular app: load the session first (gets the XSRF cookie), then act.</summary>
public sealed class BrowserClient : IDisposable
{
    public static readonly Uri BaseAddress = new("https://localhost");

    /// <summary>A portal page, for reading the cookies a browser there would see.</summary>
    public static readonly Uri PortalPage = new("https://localhost/portal/");
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
        var serverClock = factory.Services.GetService<TimeProvider>() ?? TimeProvider.System;
        Http = factory.CreateDefaultClient(BaseAddress, new ServerClockCookieHandler(Cookies, serverClock), _xsrf);
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

    /// <summary>
    /// A photo upload as the browser sends it: <c>multipart/form-data</c> with one <c>file</c> part, under the name and
    /// declared type given (both of which the API ignores).
    /// </summary>
    public async Task<HttpResponseMessage> UploadAsync(string path, byte[] content, string fileName = "photo.jpg", string contentType = "image/jpeg")
    {
        await EnsureXsrfAsync();
        using var form = Upload(content, fileName, contentType);
        return await Http.PostAsync(path, form, Ct);
    }

    public static MultipartFormDataContent Upload(byte[] content, string fileName = "photo.jpg", string contentType = "image/jpeg")
    {
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        return new MultipartFormDataContent { { file, "file", fileName } };
    }

    /// <summary>The portal session as its pages load it (issues the portal's XSRF token).</summary>
    public async Task<JsonElement> PortalSessionAsync(string organizationSlug)
    {
        var response = await Http.GetAsync($"/api/portal/session?orgSlug={Uri.EscapeDataString(organizationSlug)}", Ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>(Json, Ct);
    }

    /// <summary>An unsafe portal request, after the portal page has loaded its session once.</summary>
    public async Task<HttpResponseMessage> PortalSendAsync(HttpMethod method, string path, object? body, string organizationSlug)
    {
        if (Cookies.GetCookies(PortalPage)["XSRF-TOKEN"] is null)
        {
            await PortalSessionAsync(organizationSlug);
        }

        using var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
        return await Http.SendAsync(request, Ct);
    }

    private async Task EnsureXsrfAsync()
    {
        // A client with an API key is a machine, not a browser: it has no session to fetch a token from (M21).
        if (Cookies.GetCookies(BaseAddress)["XSRF-TOKEN"] is null && Http.DefaultRequestHeaders.Authorization is null)
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
        // Like Angular on the page it runs on: portal requests come from portal pages, which read the portal's token
        // (path /portal) before the staff one (D040).
        var page = request.RequestUri?.AbsolutePath.StartsWith("/api/portal", StringComparison.Ordinal) == true
            ? BrowserClient.PortalPage
            : BrowserClient.BaseAddress;
        var token = cookies.GetCookies(page)["XSRF-TOKEN"]?.Value;
        if (Enabled && unsafeMethod && token is not null)
        {
            request.Headers.Add("X-XSRF-TOKEN", token);
        }

        return base.SendAsync(request, cancellationToken);
    }
}
