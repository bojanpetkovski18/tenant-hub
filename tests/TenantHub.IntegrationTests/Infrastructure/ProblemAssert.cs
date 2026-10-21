using System.Net;
using System.Text.Json;

namespace TenantHub.IntegrationTests.Infrastructure;

public static class ProblemAssert
{
    /// <summary>Asserts the status and problem-details content type, and returns the parsed body.</summary>
    public static async Task<JsonElement> IsProblemAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == status, $"Expected {(int)status}, got {(int)response.StatusCode}: {body}");
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    /// <summary>A 400 validation problem with an error for each named field.</summary>
    public static async Task<JsonElement> HasFieldErrorsAsync(HttpResponseMessage response, params string[] fields)
    {
        var problem = await IsProblemAsync(response, HttpStatusCode.BadRequest);
        var errors = problem.GetProperty("errors");
        foreach (var field in fields)
        {
            Assert.True(errors.TryGetProperty(field, out _), $"No error for '{field}' in {errors}");
        }

        return errors;
    }

    public static async Task HasCodeAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var problem = await IsProblemAsync(response, status);
        Assert.Equal(code, problem.GetProperty("code").GetString());
    }
}
