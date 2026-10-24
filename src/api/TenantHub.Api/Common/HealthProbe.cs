namespace TenantHub.Api.Common;

/// <summary>
/// <c>dotnet TenantHub.Api.dll --health-check</c>: asks the running app's readiness endpoint and exits 0 when it is
/// healthy. The chiseled runtime image has no shell or curl, so the container's health check runs the app's own
/// binary in this mode (M11).
/// </summary>
public static class HealthProbe
{
    public const string Argument = "--health-check";

    public static async Task<int> RunAsync()
    {
        // ASPNETCORE_HTTP_PORTS is how the .NET images set the listening port (8080 by default).
        var port = (Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS") ?? "8080").Split([';', ','])[0];
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        try
        {
            using var response = await http.GetAsync(new Uri($"http://localhost:{port}/health"));
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (HttpRequestException)
        {
            return 1;
        }
        catch (TaskCanceledException)
        {
            return 1;
        }
    }
}
