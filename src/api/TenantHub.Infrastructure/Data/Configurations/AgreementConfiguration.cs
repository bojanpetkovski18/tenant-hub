using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using TenantHub.Domain.Agreements;
using TenantHub.Domain.Clients;
using TenantHub.Domain.Jobs;
using TenantHub.Domain.Memberships;
using TenantHub.Domain.Organizations;
using TenantHub.Infrastructure.Identity;

namespace TenantHub.Infrastructure.Data.Configurations;

internal sealed class AgreementConfiguration : IEntityTypeConfiguration<Agreement>
{
    public void Configure(EntityTypeBuilder<Agreement> agreement)
    {
        agreement.ToTable("agreements");
        agreement.HasKey(a => a.Id);
        agreement.Property(a => a.Id).ValueGeneratedNever();
        agreement.Ignore(a => a.DomainEvents);
        agreement.Ignore(a => a.Template);
        agreement.Ignore(a => a.Schedule);
        agreement.Ignore(a => a.SystemActor);

        // The target of the jobs → agreements composite foreign key: a visit belongs to an agreement of its organization.
        agreement.HasAlternateKey(a => new { a.OrganizationId, a.Id }).HasName("ak_agreements_organization_id_id");

        agreement.Property(a => a.Title).HasMaxLength(JobDetails.TitleMaxLength);
        agreement.Property(a => a.Description).HasMaxLength(JobDetails.DescriptionMaxLength);
        agreement.Property(a => a.Priority).HasConversion<string>().HasMaxLength(20);
        agreement.Property(a => a.QuotedAmount).HasColumnType("numeric(12,2)");
        agreement.Property(a => a.Status).HasConversion<string>().HasMaxLength(20);
        agreement.Property(a => a.PausedReason).HasConversion<string>().HasMaxLength(30);
        agreement.Property(a => a.StartsOn).HasColumnType("date");
        agreement.Property(a => a.EndsOn).HasColumnType("date");
        agreement.Property(a => a.GeneratedThrough).HasColumnType("date");

        // The rule as one jsonb document, read and written whole; the domain validates it again on the way in.
        agreement.Property(a => a.Recurrence)
            .HasColumnType("jsonb")
            .HasConversion(
                new ValueConverter<Recurrence, string>(r => RecurrenceJson.Write(r), json => RecurrenceJson.Read(json)),
                new ValueComparer<Recurrence>((a, b) => a!.Equals(b), r => r.GetHashCode(), r => r));

        // The checklist each visit starts with (SPEC-V2 8 checklist_template), as one jsonb array of { text, required }.
        agreement.Property(a => a.ChecklistTemplate)
            .HasColumnName("checklist_template")
            .HasColumnType("jsonb")
            .HasDefaultValueSql("'[]'::jsonb")
            .HasConversion(
                new ValueConverter<IReadOnlyList<ChecklistEntry>, string>(c => ChecklistJson.Write(c), json => ChecklistJson.Read(json)),
                new ValueComparer<IReadOnlyList<ChecklistEntry>>(
                    (a, b) => a!.SequenceEqual(b!),
                    c => c.Aggregate(0, (hash, e) => HashCode.Combine(hash, e)),
                    c => c.ToList()));

        // PostgreSQL's xmin system column as the concurrency token (D004).
        agreement.Property(a => a.Version).IsRowVersion();

        agreement.HasOne<Organization>().WithMany().HasForeignKey(a => a.OrganizationId).OnDelete(DeleteBehavior.Cascade);

        // Composite foreign keys, as for jobs (SPEC 3.3, point 4): the client and the template's assignee belong to the
        // agreement's organization. NO ACTION so that deleting the organization cascades through all of them.
        agreement.HasOne<Client>().WithMany()
            .HasForeignKey(a => new { a.OrganizationId, a.ClientId })
            .HasPrincipalKey(c => new { c.OrganizationId, c.Id })
            .OnDelete(DeleteBehavior.NoAction);
        agreement.HasOne<Membership>().WithMany()
            .HasForeignKey(a => new { a.OrganizationId, a.AssigneeUserId })
            .OnDelete(DeleteBehavior.NoAction);
        agreement.HasOne<AppUser>().WithMany().HasForeignKey(a => a.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>The stored form of a checklist template; the domain's rules apply again when it is read.</summary>
internal static class ChecklistJson
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string Write(IReadOnlyList<ChecklistEntry> entries) => JsonSerializer.Serialize(entries, Json);

    public static IReadOnlyList<ChecklistEntry> Read(string json) =>
        ChecklistEntry.ValidateList([.. (JsonSerializer.Deserialize<List<ChecklistEntry>>(json, Json) ?? []).Select(e => ((string?)e.Text, (bool?)e.Required))]);
}

/// <summary>The stored form of a <see cref="Recurrence"/>: only the fields of its kind, enums as names.</summary>
internal static class RecurrenceJson
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Write(Recurrence r) => JsonSerializer.Serialize(
        new Stored(r.Kind, r.Interval, r.Weekdays.Count > 0 ? [.. r.Weekdays] : null, r.Day, r.LastDay ? true : null, r.Week, r.Weekday),
        Json);

    public static Recurrence Read(string json)
    {
        var s = JsonSerializer.Deserialize<Stored>(json, Json) ?? throw new InvalidOperationException("Empty recurrence.");
        return Recurrence.Validate(s.Kind, s.Interval, s.Weekdays, s.Day, s.LastDay ?? false, s.Week, s.Weekday);
    }

    private sealed record Stored(
        RecurrenceKind Kind,
        int Interval,
        DayOfWeek[]? Weekdays,
        int? Day,
        bool? LastDay,
        WeekOfMonth? Week,
        DayOfWeek? Weekday);
}
