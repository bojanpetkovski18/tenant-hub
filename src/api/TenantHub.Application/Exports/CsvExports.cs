using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using TenantHub.Application.Abstractions;
using TenantHub.Application.Jobs;
using TenantHub.Domain;
using TenantHub.Domain.Jobs;
using TenantHub.Domain.Organizations;

namespace TenantHub.Application.Exports;

/// <summary>
/// CSV as spreadsheets read it (D051): UTF-8 with a byte-order mark (without one, Excel reads UTF-8 as the local code
/// page), comma-separated, CRLF between records, fields quoted when they hold a comma, a quote or a line break. A cell
/// that starts with <c>=</c>, <c>+</c>, <c>-</c>, <c>@</c>, a tab or a carriage return gets a leading apostrophe, so a
/// spreadsheet shows it as text instead of running it as a formula (SPEC-V2 5.8).
/// </summary>
public sealed class CsvWriter(TextWriter writer)
{
    public static readonly Encoding Utf8WithBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

    public const string ContentType = "text/csv; charset=utf-8";

    public static string Neutralize(string value) =>
        value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r' ? "'" + value : value;

    public static string Field(string? value)
    {
        var safe = Neutralize(value ?? "");
        return safe.AsSpan().IndexOfAny(",\"\r\n") >= 0 ? "\"" + safe.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : safe;
    }

    public async Task WriteRowAsync(IReadOnlyList<string?> fields)
    {
        for (var i = 0; i < fields.Count; i++)
        {
            if (i > 0)
            {
                await writer.WriteAsync(',');
            }

            await writer.WriteAsync(Field(fields[i]));
        }

        await writer.WriteAsync("\r\n");
    }
}

/// <summary>An export ready to write: checked and named before the first byte, so a refusal is still a problem response.</summary>
public sealed class CsvExport(string fileName, Func<TextWriter, CancellationToken, Task> write)
{
    public string FileName { get; } = fileName;

    public Task WriteAsync(TextWriter writer, CancellationToken cancellationToken) => write(writer, cancellationToken);
}

/// <summary>
/// The audit log as CSV (US-112, SPEC-V2 7 <c>GET /audit/export</c>): days <paramref name="From"/> to
/// <paramref name="To"/> in the organization's time zone, at most 366 days, oldest first. Rows stream from the
/// database one at a time; the whole log is never in memory.
/// </summary>
public sealed record AuditExportRequest(DateOnly? From, DateOnly? To)
{
    public const int MaxDays = 366;
}

public sealed class ExportAuditQuery(IAppDbContext db, ITenantContext tenant)
{
    public async Task<CsvExport> PrepareAsync(AuditExportRequest request, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        if (request.From is null)
        {
            errors["from"] = ["Choose the first day."];
        }

        if (request.To is null)
        {
            errors["to"] = ["Choose the last day."];
        }

        if (request is { From: { } first, To: { } last })
        {
            if (last < first)
            {
                errors["to"] = ["Choose a last day on or after the first."];
            }
            else if (last.DayNumber - first.DayNumber + 1 > AuditExportRequest.MaxDays)
            {
                errors["to"] = [$"Export at most {AuditExportRequest.MaxDays} days at a time."];
            }
        }

        if (errors.Count > 0)
        {
            throw new DomainValidationException(errors);
        }

        var (from, to) = (request.From!.Value, request.To!.Value);
        var zoneId = await db.Organizations.Where(o => o.Id == tenant.OrganizationId).Select(o => o.TimeZone).SingleAsync(cancellationToken);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId);
        var start = Organization.StartOfDayIn(zoneId, from);
        var end = Organization.StartOfDayIn(zoneId, to.AddDays(1));
        var entries = db.AuditEntries.AsNoTracking()
            .Where(a => a.OccurredAt >= start && a.OccurredAt < end)
            .OrderBy(a => a.OccurredAt)
            .ThenBy(a => a.Id)
            .Select(a => new { a.OccurredAt, a.ActorName, a.ActorKind, a.Action, a.EntityType, a.EntityId, a.Summary, a.Changes });

        return new CsvExport($"audit-{from:yyyy-MM-dd}-to-{to:yyyy-MM-dd}.csv", async (writer, ct) =>
        {
            var csv = new CsvWriter(writer);
            await csv.WriteRowAsync([$"Occurred at ({zoneId})", "Actor", "Actor kind", "Action", "Entity type", "Entity id", "Summary", "Changes"]);
            await foreach (var a in entries.AsAsyncEnumerable().WithCancellation(ct))
            {
                await csv.WriteRowAsync([
                    TimeZoneInfo.ConvertTime(a.OccurredAt, zone).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                    a.ActorName,
                    a.ActorKind.ToString(),
                    a.Action,
                    a.EntityType,
                    a.EntityId.ToString(),
                    a.Summary,
                    string.Join("; ", a.Changes.Select(c => $"{c.Field}: {c.From ?? "–"} → {c.To ?? "–"}")),
                ]);
            }
        });
    }
}

/// <summary>
/// The jobs list as CSV (US-112, SPEC-V2 7 <c>GET /jobs/export</c>): the list's own filters and sort, every page.
/// Key-enabled. Names of people come from the organization's members (a bounded set), fetched once before the rows.
/// </summary>
public sealed class ExportJobsQuery(IAppDbContext db, ListJobsQuery list, JobViews views, IIdentityService identity)
{
    public async Task<CsvExport> PrepareAsync(ListJobsRequest request, CancellationToken cancellationToken)
    {
        var today = await views.TodayAsync(cancellationToken);
        var jobs = ListJobsQuery.Sorted(await list.FilteredAsync(request, today, cancellationToken), request.Sort)
            .Select(j => new
            {
                j.Number,
                j.Title,
                ClientName = db.Clients.Where(c => c.Id == j.ClientId).Select(c => c.Name).FirstOrDefault(),
                j.AssigneeUserId,
                j.Status,
                j.Priority,
                j.ScheduledOn,
                j.DueOn,
                j.QuotedAmount,
                j.EstimatedMinutes,
                j.CreatedAt,
                j.CompletedAt,
            });

        var members = await db.Memberships.AsNoTracking().Select(m => m.UserId).Distinct().ToListAsync(cancellationToken);
        var people = await identity.FindProfilesAsync(members, cancellationToken);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(await views.TimeZoneAsync(cancellationToken));

        string Day(DateOnly? day) => day?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "";
        string Time(DateTimeOffset? at) => at is { } t ? TimeZoneInfo.ConvertTime(t, zone).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "";

        return new CsvExport($"jobs-{today:yyyy-MM-dd}.csv", async (writer, ct) =>
        {
            var csv = new CsvWriter(writer);
            await csv.WriteRowAsync(["Job", "Title", "Client", "Status", "Priority", "Assignee", "Scheduled", "Due", "Overdue", "Quoted amount", "Estimated minutes", "Created", "Completed"]);
            await foreach (var j in jobs.AsAsyncEnumerable().WithCancellation(ct))
            {
                await csv.WriteRowAsync([
                    Job.FormatNumber(j.Number),
                    j.Title,
                    j.ClientName ?? "Unknown client",
                    JobLabels.Of(j.Status),
                    JobLabels.Of(j.Priority),
                    j.AssigneeUserId is { } id ? people.GetValueOrDefault(id)?.DisplayName ?? "A former member" : "",
                    Day(j.ScheduledOn),
                    Day(j.DueOn),
                    Job.IsOverdue(j.DueOn, j.Status, today) ? "yes" : "no",
                    j.QuotedAmount?.ToString("0.00", CultureInfo.InvariantCulture) ?? "",
                    j.EstimatedMinutes?.ToString(CultureInfo.InvariantCulture) ?? "",
                    Time(j.CreatedAt),
                    Time(j.CompletedAt),
                ]);
            }
        });
    }
}
