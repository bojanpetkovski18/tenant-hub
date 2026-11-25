using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TenantHub.Application.Tenancy;
using TenantHub.Infrastructure.Seed;
using TenantHub.IntegrationTests.Infrastructure;

namespace TenantHub.IntegrationTests.Data;

/// <summary>
/// Everything a demo visitor can see, without ids or random keys, one line per row: equal fingerprints mean equal
/// demos. It covers the v1 data, the v2 data (agreements, contacts, quotes, checklists, photos, sign-offs, requests,
/// notifications, integrations) and what belongs to the demo accounts themselves (names, notification preferences),
/// which outlives the organizations. Only the seeded organizations and accounts are read, so a visitor's own
/// organization does not count.
/// </summary>
internal static class DemoFingerprint
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<IReadOnlyList<string>> OfAsync(string connectionString)
    {
        await using var db = PostgresFixture.CreateDbContext(connectionString);
        var orgs = await db.Organizations.AsNoTracking().Where(o => o.IsDemoSeed).ToDictionaryAsync(o => o.Id, o => o.Slug, Ct);
        var orgIds = orgs.Keys.ToList();
        var demoEmails = DemoSeeder.Emails.ToList();
        var users = await db.Users.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.Email!, Ct);
        var clients = await db.Clients.AcrossTenants().AsNoTracking().Where(c => orgIds.Contains(c.OrganizationId)).ToDictionaryAsync(c => c.Id, c => c.Name, Ct);
        var contacts = await db.ClientContacts.AcrossTenants().AsNoTracking().Where(c => orgIds.Contains(c.OrganizationId)).ToListAsync(Ct);
        var contactEmails = contacts.ToDictionary(c => c.Id, c => c.Email);
        var jobs = await db.Jobs.AcrossTenants().AsNoTracking().Where(j => orgIds.Contains(j.OrganizationId)).ToListAsync(Ct);
        var jobNumbers = jobs.ToDictionary(j => j.Id, j => $"{orgs[j.OrganizationId]}#{j.Number}");
        var agreements = await db.Agreements.AcrossTenants().AsNoTracking().Where(a => orgIds.Contains(a.OrganizationId)).ToListAsync(Ct);
        var agreementTitles = agreements.ToDictionary(a => a.Id, a => a.Title);

        string User(Guid? id) => id is { } value ? users.GetValueOrDefault(value, "?") : "-";
        string Contact(Guid? id) => id is { } value ? contactEmails.GetValueOrDefault(value, "?") : "-";

        var lines = new List<string>();
        lines.AddRange((await db.Organizations.AsNoTracking().Where(o => o.IsDemoSeed).ToListAsync(Ct))
            .Select(o => $"org|{o.Slug}|{o.Name}|{o.Plan}|{o.TimeZone}|{o.DailyCapacityMinutes}|{o.NextJobNumber}|{o.CreatedAt:O}"));
        lines.AddRange((await db.Users.AsNoTracking().Where(u => demoEmails.Contains(u.Email!)).ToListAsync(Ct))
            .Select(u => $"user|{u.Email}|{u.DisplayName}"));
        lines.AddRange((await db.Memberships.AcrossTenants().AsNoTracking().Where(m => orgIds.Contains(m.OrganizationId)).ToListAsync(Ct))
            .Select(m => $"member|{orgs[m.OrganizationId]}|{User(m.UserId)}|{m.Role}"));
        lines.AddRange((await db.Clients.AcrossTenants().AsNoTracking().Where(c => orgIds.Contains(c.OrganizationId)).ToListAsync(Ct))
            .Select(c => $"client|{orgs[c.OrganizationId]}|{c.Name}|{c.Status}|{c.Email}|{c.ContactName}|{c.CreatedAt:O}"));
        lines.AddRange(contacts
            .Select(c => $"contact|{orgs[c.OrganizationId]}|{clients[c.ClientId]}|{c.Name}|{c.Email}|{c.CreatedAt:O}|{c.RemovedAt:O}|{c.LastSignInAt:O}"));
        lines.AddRange(agreements
            .Select(a => $"agreement|{orgs[a.OrganizationId]}|{a.Title}|{clients[a.ClientId]}|{User(a.AssigneeUserId)}|{a.Status}|{a.PausedReason}|{JsonSerializer.Serialize(a.Recurrence)}|{a.StartsOn}|{a.EndsOn}|{a.MaxVisits}|{a.GeneratedThrough}|{a.ChecklistTemplate.Count}|{a.CreatedAt:O}"));
        lines.AddRange(jobs
            .Select(j => $"job|{jobNumbers[j.Id]}|{j.Title}|{clients[j.ClientId]}|{User(j.AssigneeUserId)}|{j.Status}|{j.Priority}|{j.ScheduledOn}|{j.DueOn}|{j.QuotedAmount}|{j.EstimatedMinutes}|{j.CreatedAt:O}|{j.CompletedAt:O}"
                + $"|{(j.AgreementId is { } a ? agreementTitles[a] : "-")}|{j.OccurrenceOn}"
                + $"|{j.ApprovalStatus}|{j.ApprovalSentAt:O}|{User(j.ApprovalSentByUserId)}|{j.ApprovedAmount}|{j.ApprovalDecidedAt:O}|{Contact(j.ApprovalDecidedByContactId)}|{j.ApprovalComment}"
                + $"|{j.ClientFeedback}|{j.ClientFeedbackText}|{j.ClientFeedbackAt:O}|{Contact(j.ClientFeedbackByContactId)}"));
        lines.AddRange((await db.ChecklistItems.AcrossTenants().AsNoTracking().Where(i => orgIds.Contains(i.OrganizationId)).ToListAsync(Ct))
            .Select(i => $"checklist|{jobNumbers[i.JobId]}|{i.Text}|{i.Required}|{i.DoneAt:O}|{User(i.DoneByUserId)}"));
        lines.AddRange((await db.Attachments.AcrossTenants().AsNoTracking().Where(p => orgIds.Contains(p.OrganizationId)).ToListAsync(Ct))
            .Select(p => $"photo|{jobNumbers[p.JobId]}|{p.ContentType}|{p.Bytes}|{p.Width}x{p.Height}|{p.CreatedAt:O}|{User(p.CreatedByUserId)}"));
        lines.AddRange((await db.WorkRequests.AcrossTenants().AsNoTracking().Where(r => orgIds.Contains(r.OrganizationId)).ToListAsync(Ct))
            .Select(r => $"request|{orgs[r.OrganizationId]}|{clients[r.ClientId]}|{Contact(r.ContactId)}|{r.Title}|{r.Description}|{r.PreferredOn}|{r.Status}|{(r.JobId is { } j ? jobNumbers[j] : "-")}|{r.DeclineReason}|{r.CreatedAt:O}|{r.DecidedAt:O}"));
        lines.AddRange((await db.Notifications.AcrossTenants().AsNoTracking().Where(n => orgIds.Contains(n.OrganizationId)).ToListAsync(Ct))
            .Select(n => $"notification|{orgs[n.OrganizationId]}|{User(n.UserId)}|{n.Type}|{n.Content.Text}|{n.CreatedAt:O}|{n.ReadAt:O}"));
        var demoUserIds = users.Where(u => demoEmails.Contains(u.Value)).Select(u => u.Key).ToList();
        lines.AddRange((await db.NotificationPreferences.AsNoTracking().Where(p => demoUserIds.Contains(p.UserId)).ToListAsync(Ct))
            .Select(p => $"preference|{User(p.UserId)}|{p.Type}|{p.Email}"));
        lines.AddRange((await db.ApiKeys.AcrossTenants().AsNoTracking().Where(k => orgIds.Contains(k.OrganizationId)).ToListAsync(Ct))
            .Select(k => $"api-key|{orgs[k.OrganizationId]}|{k.Name}"));
        lines.AddRange((await db.WebhookEndpoints.AcrossTenants().AsNoTracking().Where(w => orgIds.Contains(w.OrganizationId)).ToListAsync(Ct))
            .Select(w => $"webhook|{orgs[w.OrganizationId]}|{w.Url}"));
        lines.AddRange((await db.AuditEntries.AcrossTenants().AsNoTracking().Where(a => orgIds.Contains(a.OrganizationId)).ToListAsync(Ct))
            .Select(a => $"audit|{orgs[a.OrganizationId]}|{a.OccurredAt:O}|{a.ActorName}|{a.ActorKind}|{a.Action}|{a.Summary}"));
        return [.. lines.Order(StringComparer.Ordinal)];
    }
}
