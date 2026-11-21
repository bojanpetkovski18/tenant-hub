using System.Net;
using System.Net.Http.Headers;
using System.Text;
using TenantHub.Domain.Memberships;
using TenantHub.IntegrationTests.Infrastructure;
using TenantHub.IntegrationTests.Isolation;

namespace TenantHub.IntegrationTests.Exports;

/// <summary>
/// US-112 and SPEC-V2 5.8: the audit log for a range of days and the jobs list for its filters, as CSV that spreadsheets
/// open (UTF-8 with a byte-order mark); cells a spreadsheet would run as formulas are neutralized; each organization
/// exports only its own rows; the export streams, so memory stays flat however long it is. The memory measurement
/// reads the process's heap, so the class runs alone (other tests' allocations would count otherwise).
/// </summary>
[Collection(nameof(ExportTests))]
public sealed class ExportTests(PostgresFixture postgres, ITestOutputHelper output)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Today => DateTime.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public async Task The_audit_export_is_a_spreadsheet_ready_csv_of_the_range_and_only_this_organization_s()
    {
        var tenants = await SharedApiTenants.GetAsync<ExportTests>(postgres);
        var owner = await tenants.ClientForAsync(Role.Owner);

        var response = await owner.Http.GetAsync($"/api/orgs/{tenants.OrgA}/audit/export?from={Today}&to={Today}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("utf-8", response.Content.Headers.ContentType?.CharSet);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal($"audit-{Today}-to-{Today}.csv", response.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString() ?? "", StringComparison.Ordinal);
        var bytes = await response.Content.ReadAsByteArrayAsync(Ct);
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        var lines = Encoding.UTF8.GetString(bytes[3..]).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("Occurred at (", lines[0], StringComparison.Ordinal);
        Assert.EndsWith(",Actor,Actor kind,Action,Entity type,Entity id,Summary,Changes", lines[0], StringComparison.Ordinal);
        Assert.Contains(lines, l => l.Contains("job.created", StringComparison.Ordinal) && l.Contains("Alpha Standing Job", StringComparison.Ordinal));
        var csv = string.Join('\n', lines);
        Assert.DoesNotContain(tenants.JobBTitle, csv, StringComparison.Ordinal);
        Assert.DoesNotContain(tenants.ClientBName, csv, StringComparison.Ordinal);

        // Yesterday holds nothing of today's.
        var yesterday = DateTime.UtcNow.AddDays(-2).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var empty = await owner.Http.GetStringAsync($"/api/orgs/{tenants.OrgA}/audit/export?from={yesterday}&to={yesterday}", Ct);
        Assert.Single(empty.TrimStart('﻿').Split("\r\n", StringSplitOptions.RemoveEmptyEntries));
    }

    [Theory]
    [InlineData("", "from", "to")]
    [InlineData("?from=2026-01-01", "to")]
    [InlineData("?from=2026-03-01&to=2026-02-28", "to")]
    [InlineData("?from=2025-01-01&to=2026-01-02", "to")] // 367 days
    public async Task The_audit_export_needs_a_range_of_at_most_366_days(string query, params string[] fields)
    {
        var tenants = await SharedApiTenants.GetAsync<ExportTests>(postgres);
        var owner = await tenants.ClientForAsync(Role.Owner);

        await ProblemAssert.HasFieldErrorsAsync(await owner.Http.GetAsync($"/api/orgs/{tenants.OrgA}/audit/export{query}", Ct), fields);
        Assert.Equal(HttpStatusCode.OK, (await owner.Http.GetAsync($"/api/orgs/{tenants.OrgA}/audit/export?from=2025-01-01&to=2026-01-01", Ct)).StatusCode);
    }

    [Theory]
    [InlineData("=HYPERLINK(\"https://evil.example\")")]
    [InlineData("+1+2")]
    [InlineData("-2+3")]
    [InlineData("@SUM(1,2)")]
    public async Task A_cell_a_spreadsheet_would_run_as_a_formula_is_neutralized(string title)
    {
        var tenants = await SharedApiTenants.GetAsync<ExportTests>(postgres);
        var owner = await tenants.ClientForAsync(Role.Owner);
        var created = await owner.PostJsonAsync($"/api/orgs/{tenants.OrgA}/jobs", new { title, clientId = tenants.ClientA, priority = "normal" });
        created.EnsureSuccessStatusCode();

        var csv = await owner.Http.GetStringAsync($"/api/orgs/{tenants.OrgA}/jobs/export?q={Uri.EscapeDataString(title)}", Ct);

        var row = csv.Split("\r\n")[1];
        var cells = Cells(row);
        Assert.Equal("'" + title, cells[1]);
    }

    [Fact]
    public async Task The_jobs_export_applies_the_list_s_filters_and_holds_only_this_organization_s_jobs()
    {
        var tenants = await SharedApiTenants.GetAsync<ExportTests>(postgres);
        var owner = await tenants.ClientForAsync(Role.Owner);
        var title = $"Filtered export {Guid.NewGuid():N}";
        var created = await owner.PostJsonAsync($"/api/orgs/{tenants.OrgA}/jobs", new { title, clientId = tenants.ClientA, priority = "urgent", quotedAmount = 1234.5m });
        created.EnsureSuccessStatusCode();

        var all = await owner.Http.GetStringAsync($"/api/orgs/{tenants.OrgA}/jobs/export", Ct);
        var urgent = await owner.Http.GetStringAsync($"/api/orgs/{tenants.OrgA}/jobs/export?priority=urgent", Ct);
        var low = await owner.Http.GetStringAsync($"/api/orgs/{tenants.OrgA}/jobs/export?priority=low", Ct);

        Assert.Equal("Job,Title,Client,Status,Priority,Assignee,Scheduled,Due,Overdue,Quoted amount,Estimated minutes,Created,Completed", all.TrimStart('﻿').Split("\r\n")[0]);
        Assert.Contains("Alpha Standing Job", all, StringComparison.Ordinal);
        Assert.DoesNotContain(tenants.JobBTitle, all, StringComparison.Ordinal);
        var row = Cells(urgent.Split("\r\n").Single(l => l.Contains(title, StringComparison.Ordinal)));
        Assert.Equal((title, "Alpha Standing Client", "New", "Urgent", "1234.50"), (row[1], row[2], row[3], row[4], row[9]));
        Assert.DoesNotContain(title, low, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Exporting_50000_audit_entries_streams_with_flat_memory()
    {
        var tenants = await SharedApiTenants.GetAsync<ExportTests>(postgres);
        var owner = await tenants.ClientForAsync(Role.Owner);
        const int entries = 50_000;
        await using (var connection = new Npgsql.NpgsqlConnection(PostgresFixture.AdminConnectionStringFor(tenants.ConnectionString)))
        {
            await connection.OpenAsync(Ct);
            await using var command = new Npgsql.NpgsqlCommand(
                """
                INSERT INTO audit_entries (organization_id, occurred_at, actor_user_id, actor_name, actor_kind, actor_ref, action, entity_type, entity_id, summary, changes, trace_id)
                SELECT @org, timestamptz '2025-06-01 00:00:00+00' + g * interval '10 minutes', NULL, 'Load test', 'System', NULL, 'job.updated', 'job', gen_random_uuid(),
                       'Updated JOB-' || g || ': a summary of ordinary length for an ordinary change to an ordinary job',
                       '[{"field":"title","from":"Before","to":"After"}]'::jsonb, NULL
                FROM generate_series(1, @count) g
                """,
                connection);
            command.Parameters.AddWithValue("org", tenants.OrgA);
            command.Parameters.AddWithValue("count", entries);
            await command.ExecuteNonQueryAsync(Ct);
        }

        // From 2025-06-01 the rows run for about 347 days.
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/orgs/{tenants.OrgA}/audit/export?from=2025-06-01&to=2026-06-01");
        var baseline = GC.GetTotalMemory(forceFullCollection: true);
        using var response = await owner.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, Ct);
        await using var body = await response.Content.ReadAsStreamAsync(Ct);
        var buffer = new byte[64 * 1024];
        long total = 0, nextSample = 0, peak = 0;
        var lines = 0;
        int read;
        while ((read = await body.ReadAsync(buffer, Ct)) > 0)
        {
            total += read;
            lines += buffer.AsSpan(0, read).Count((byte)'\n');
            if (total >= nextSample)
            {
                // Live memory only (a full collection first): what the export holds, not the garbage it leaves.
                peak = Math.Max(peak, GC.GetTotalMemory(forceFullCollection: true) - baseline);
                nextSample = total + (1024 * 1024);
            }
        }

        output.WriteLine($"Exported {lines - 1} rows, {total / 1024.0 / 1024.0:0.0} MB; peak live memory above baseline {peak / 1024.0 / 1024.0:0.0} MB.");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(lines - 1 >= entries, $"{lines - 1} rows");
        // Holding the result would keep at least the CSV's size alive; streaming keeps a few buffers.
        Assert.True(peak < total / 4, $"Peak {peak} bytes for a {total}-byte export.");
    }

    /// <summary>The fields of one CSV record (quoted fields with doubled quotes; no line breaks inside).</summary>
    private static List<string> Cells(string row)
    {
        var cells = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < row.Length; i++)
        {
            var c = row[i];
            if (quoted && c == '"' && i + 1 < row.Length && row[i + 1] == '"')
            {
                cell.Append('"');
                i++;
            }
            else if (c == '"')
            {
                quoted = !quoted;
            }
            else if (c == ',' && !quoted)
            {
                cells.Add(cell.ToString());
                cell.Clear();
            }
            else
            {
                cell.Append(c);
            }
        }

        cells.Add(cell.ToString());
        return cells;
    }
}

/// <summary>Runs <see cref="ExportTests"/> with nothing else in parallel.</summary>
[CollectionDefinition(nameof(ExportTests), DisableParallelization = true)]
public sealed class ExportTestsRunAlone;
