using System.Net;
using System.Net.Http.Json;
using TenantHub.Application.Clients;
using TenantHub.Application.Jobs;
using TenantHub.IntegrationTests.Infrastructure;

namespace TenantHub.IntegrationTests.Jobs;

/// <summary>
/// Found by the M10 load script: creating jobs (which takes the organization lock for numbering and limits) while
/// other jobs move through the workflow (whose audit entries check their foreign key to the organization) deadlocked
/// and answered 500. Both must simply succeed, however they interleave.
/// </summary>
public sealed class JobConcurrencyTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Concurrent_job_creation_and_transitions_in_one_organization_all_succeed()
    {
        await using var factory = new ApiFactory(await postgres.CreateDatabaseAsync());
        using var owner = new BrowserClient(factory);
        (await owner.SignUpAsync(BrowserClient.NewEmail(), "Busy Cleaners")).EnsureSuccessStatusCode();
        var session = await owner.SessionAsync();
        var org = $"/api/orgs/{session.User!.Memberships.Single().OrganizationId}";
        (await owner.SendJsonAsync(HttpMethod.Put, $"{org}/plan", new { plan = "business" })).EnsureSuccessStatusCode();
        var client = (await (await owner.PostJsonAsync($"{org}/clients", new { name = "Harbour View" })).Content.ReadFromJsonAsync<ClientView>(BrowserClient.Json, Ct))!;
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        object NewJob(int i) => new { title = $"Job {i}", clientId = client.Id, assigneeUserId = session.User.Id, scheduledOn = today, dueOn = today.AddDays(3) };

        // Each existing job takes one of the workflow's paths, as the load script does; some take two steps.
        string[][] paths = [["scheduled"], ["inProgress"], ["inProgress", "completed"], ["cancelled"]];
        async Task<HttpResponseMessage> MoveAsync(Guid id, string[] steps)
        {
            HttpResponseMessage response = null!;
            foreach (var to in steps)
            {
                response = await owner.PostJsonAsync($"{org}/jobs/{id}/transitions", new { to, reason = to == "cancelled" ? "Client cancelled" : null });
                if (!response.IsSuccessStatusCode)
                {
                    break;
                }
            }

            return response;
        }

        var failures = new List<string>();
        for (var round = 0; round < 6; round++)
        {
            var existing = new List<Guid>();
            for (var i = 0; i < 25; i++)
            {
                var job = await (await owner.PostJsonAsync($"{org}/jobs", NewJob(i))).Content.ReadFromJsonAsync<JobView>(BrowserClient.Json, Ct);
                existing.Add(job!.Id);
            }

            var creations = Enumerable.Range(0, 25).Select(i => owner.PostJsonAsync($"{org}/jobs", NewJob(100 + i)));
            var moves = existing.Select((id, i) => MoveAsync(id, paths[i % paths.Length]));
            foreach (var response in await Task.WhenAll(creations.Concat(moves)))
            {
                if (response.StatusCode != HttpStatusCode.OK && response.StatusCode != HttpStatusCode.Created)
                {
                    failures.Add($"{(int)response.StatusCode} {response.RequestMessage!.RequestUri!.AbsolutePath}");
                }
            }
        }

        Assert.True(failures.Count == 0, $"{failures.Count} requests failed: {string.Join(", ", failures.Take(5))}");
    }
}
