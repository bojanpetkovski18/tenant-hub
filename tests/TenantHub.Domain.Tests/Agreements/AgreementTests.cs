using TenantHub.Domain.Agreements;
using TenantHub.Domain.Jobs;

namespace TenantHub.Domain.Tests.Agreements;

/// <summary>SPEC-V2 5.1: the agreement's own rules, generation, and what edits, skips and ends do to visits.</summary>
public sealed class AgreementTests
{
    private static readonly Guid Org = Guid.NewGuid();
    private static readonly Guid Client = Guid.NewGuid();
    private static readonly Guid Owner = Guid.NewGuid();
    private static readonly Guid Maya = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2027, 1, 4, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2027, 1, 4); // a Monday
    private static readonly Dictionary<Guid, string> Names = new() { [Client] = "Acme Offices", [Maya] = "Maya Torres", [Owner] = "Olivia Bennett" };
    private static readonly JobActor Manager = new(Owner, CanManage: true, CanWork: true);

    private static AgreementTemplate Template(Guid? assignee = null, string title = "Office cleaning") =>
        AgreementTemplate.Validate(title, null, assignee, JobPriority.Normal, 120m, 90);

    private static Schedule Weekly(DateOnly? start = null, params DayOfWeek[] days) =>
        new(Recurrence.Weekly(days.Length > 0 ? days : [DayOfWeek.Monday]), start ?? Today, null, null);

    private static Agreement Create(AgreementTemplate? template = null, Schedule? schedule = null) =>
        Agreement.Create(Org, Client, template ?? Template(), schedule ?? Weekly(), Today, Owner, Names, Now);

    private static List<Job> Generate(Agreement agreement, IReadOnlySet<DateOnly>? existing = null, int? room = null)
    {
        var plan = VisitGeneration.Plan([(agreement, existing ?? new HashSet<DateOnly>())], Today, room);
        var number = 1;
        return [.. plan.Visits.Select(v => Job.Generate(v.Agreement, number++, v.Date, Names, Now))];
    }

    [Fact]
    public void An_agreement_cannot_start_before_today()
    {
        var error = Assert.Throws<DomainValidationException>(() => Create(schedule: Weekly(Today.AddDays(-1))));

        Assert.Equal(["startsOn"], error.Errors.Keys);
    }

    [Fact]
    public void Generation_covers_60_days_from_today_and_names_the_system()
    {
        var agreement = Create(Template(assignee: Maya));

        var visits = Generate(agreement);

        // Mondays from 4 January to 5 March 2027 (today plus 60 days): 9 of them.
        Assert.Equal(9, visits.Count);
        Assert.Equal(Today, visits[0].OccurrenceOn);
        Assert.Equal(new DateOnly(2027, 3, 1), visits[^1].OccurrenceOn);
        Assert.All(visits, v =>
        {
            Assert.Equal((JobStatus.Scheduled, v.OccurrenceOn, v.OccurrenceOn, agreement.Id), (v.Status, v.ScheduledOn, v.DueOn, v.AgreementId));
            Assert.Equal((120m, 90, Maya, Client), (v.QuotedAmount!.Value, v.EstimatedMinutes!.Value, v.AssigneeUserId!.Value, v.ClientId));
            var created = Assert.IsType<JobEvent>(Assert.Single(v.DomainEvents));
            Assert.Equal(AuditActor.System("System (agreement Office cleaning)"), created.Actor);
        });
    }

    [Fact]
    public void Without_an_assignee_visits_are_new()
    {
        Assert.All(Generate(Create()), v => Assert.Equal(JobStatus.New, v.Status));
    }

    [Fact]
    public void Existing_dates_are_not_generated_again()
    {
        var agreement = Create();
        var first = Generate(agreement);

        var again = Generate(agreement, first.Select(v => v.OccurrenceOn!.Value).ToHashSet());

        Assert.Empty(again);
    }

    [Fact]
    public void The_plan_limit_cuts_the_far_end_across_agreements_and_names_them()
    {
        var mondays = Create(Template(title: "Mondays"));
        var fridays = Create(Template(title: "Fridays"), Weekly(days: DayOfWeek.Friday));

        var plan = VisitGeneration.Plan([(mondays, new HashSet<DateOnly>()), (fridays, new HashSet<DateOnly>())], Today, room: 3);

        Assert.Equal([new DateOnly(2027, 1, 4), new DateOnly(2027, 1, 8), new DateOnly(2027, 1, 11)], plan.Visits.Select(v => v.Date));
        // Each is cut at its first date left out: Friday 15 and Monday 18 January.
        Assert.Equal(new Dictionary<Guid, DateOnly> { [mondays.Id] = new(2027, 1, 18), [fridays.Id] = new(2027, 1, 15) }, plan.LeftOut);
        Assert.Empty(VisitGeneration.Plan([(mondays, new HashSet<DateOnly>())], Today, room: 0).Visits);
    }

    [Fact]
    public void Paused_and_ended_agreements_generate_nothing_and_resuming_never_back_fills()
    {
        var agreement = Create();
        agreement.Pause(Now);
        Assert.Empty(Generate(agreement));

        agreement.Resume(Now);
        var later = Today.AddDays(14);
        var resumed = VisitGeneration.Plan([(agreement, new HashSet<DateOnly>())], later, room: null);
        Assert.All(resumed.Visits, v => Assert.True(v.Date >= later));

        agreement.End(Now);
        Assert.Empty(Generate(agreement));
    }

    [Fact]
    public void An_edit_updates_untouched_future_visits_withdraws_dropped_dates_and_leaves_the_rest()
    {
        var agreement = Create();
        var visits = Generate(agreement);
        var byHand = visits[1];
        byHand.Update(byHand.Details with { Title = "Office cleaning, front door too" }, Manager, Names, Now);
        var started = visits[2];
        started.Update(started.Details with { AssigneeUserId = Maya }, Manager, Names, Now);
        started.TransitionTo(JobStatus.InProgress, Manager, null, Now);
        var past = visits[0]; // today's visit, which becomes past when the edit happens tomorrow
        var tomorrow = Today.AddDays(1);

        // Every other week from now on, a new title and Maya as assignee.
        agreement.Update(
            Template(assignee: Maya, title: "Deep office cleaning"),
            new Schedule(Recurrence.Weekly([DayOfWeek.Monday], everyWeeks: 2), Today, null, null),
            Names,
            Now);
        var plan = VisitGeneration.AfterEdit(agreement, visits, tomorrow);

        Assert.True(byHand.ChangedByHand);
        Assert.DoesNotContain(byHand, plan.Update.Concat(plan.Withdraw));
        Assert.DoesNotContain(started, plan.Update.Concat(plan.Withdraw));
        Assert.DoesNotContain(past, plan.Update.Concat(plan.Withdraw));
        // Fortnightly from 4 January: 1 Feb, 15 Feb and 1 Mar stay; 25 Jan, 8 Feb and 22 Feb go. 18 January is on the
        // schedule but in progress, and 11 January was changed by hand.
        Assert.Equal(
            [new DateOnly(2027, 2, 1), new DateOnly(2027, 2, 15), new DateOnly(2027, 3, 1)],
            plan.Update.Select(v => v.OccurrenceOn!.Value));
        Assert.Equal(
            [new DateOnly(2027, 1, 25), new DateOnly(2027, 2, 8), new DateOnly(2027, 2, 22)],
            plan.Withdraw.Select(v => v.OccurrenceOn!.Value));

        var updated = plan.Update[0];
        Assert.True(updated.FollowAgreement(agreement, Names, Now));
        Assert.Equal(("Deep office cleaning", Maya, JobStatus.Scheduled), (updated.Title, updated.AssigneeUserId!.Value, updated.Status));
        Assert.False(updated.ChangedByHand);
        Assert.Equal(AuditActor.System("System (agreement Deep office cleaning)"), ((JobEvent)updated.DomainEvents[^1]).Actor);
    }

    [Fact]
    public void Skipping_cancels_one_visit_with_the_reason_and_keeps_its_date()
    {
        var visit = Generate(Create())[0];

        visit.Skip(Manager, Now);

        Assert.Equal((JobStatus.Cancelled, Today), (visit.Status, visit.OccurrenceOn!.Value));
        Assert.Contains(((JobEvent)visit.DomainEvents[^1]).Changes, c => c is { Field: "reason", To: "Skipped" });
    }

    [Fact]
    public void A_visit_is_never_deleted_whatever_its_status()
    {
        var visits = Generate(Create());
        visits[1].Skip(Manager, Now);

        Assert.Equal("visit-not-deletable", Assert.Throws<DomainException>(() => visits[0].MarkDeleted(Manager)).Code);
        Assert.Equal("visit-not-deletable", Assert.Throws<DomainException>(() => visits[1].MarkDeleted(Manager)).Code);
    }

    [Fact]
    public void Only_visits_of_an_agreement_can_be_skipped()
    {
        var job = Job.Create(Org, 1, JobDetails.Validate("Leak", null, Client, null, null, null, null, null), Manager, Names, Now);

        Assert.Equal("not-a-visit", Assert.Throws<DomainException>(() => job.Skip(Manager, Now)).Code);
    }

    [Fact]
    public void Ending_cancels_new_and_scheduled_visits_from_today_and_keeps_the_rest()
    {
        var agreement = Create();
        var visits = Generate(agreement);
        visits[0].Update(visits[0].Details with { AssigneeUserId = Maya }, Manager, Names, Now);
        visits[0].TransitionTo(JobStatus.InProgress, Manager, null, Now);

        agreement.End(Now);
        var cancelled = VisitGeneration.CancelledByEnd(agreement, visits, Today).ToList();

        Assert.Equal(visits.Count - 1, cancelled.Count);
        Assert.DoesNotContain(visits[0], cancelled);
        Assert.Equal("agreement-ended", Assert.Throws<DomainException>(() => agreement.Update(Template(), Weekly(), Names, Now)).Code);
    }

    [Fact]
    public void Status_changes_follow_the_lifecycle()
    {
        var agreement = Create();

        Assert.Equal("agreement-status", Assert.Throws<DomainException>(() => agreement.Resume(Now)).Code);
        agreement.Pause(Now);
        Assert.Equal("agreement-status", Assert.Throws<DomainException>(() => agreement.Pause(Now)).Code);
        agreement.End(Now);
        Assert.Equal("agreement-ended", Assert.Throws<DomainException>(() => agreement.End(Now)).Code);
        Assert.Equal(
            ["agreement.created", "agreement.paused", "agreement.ended"],
            agreement.DomainEvents.OfType<AgreementEvent>().Select(e => e.Action));
    }

    [Fact]
    public void Archiving_the_client_pauses_with_a_reason_and_the_plan_hold_is_recorded_once_each_way()
    {
        var archived = Create();
        Assert.True(archived.PauseForArchivedClient("Acme Offices", Now));
        Assert.Equal((AgreementStatus.Paused, GenerationHold.ClientArchived), (archived.Status, archived.PausedReason!.Value));

        var held = Create();
        held.RecordGeneration(Today.AddDays(60), limitReached: true, Now);
        held.RecordGeneration(Today.AddDays(61), limitReached: true, Now);
        Assert.Equal(GenerationHold.PlanLimit, held.PausedReason);
        held.RecordGeneration(Today.AddDays(62), limitReached: false, Now);

        Assert.Null(held.PausedReason);
        Assert.Equal(AgreementStatus.Active, held.Status);
        Assert.Equal(Today.AddDays(62), held.GeneratedThrough);
        Assert.Equal(
            ["agreement.created", "agreement.generation_held", "agreement.generation_resumed"],
            held.DomainEvents.OfType<AgreementEvent>().Select(e => e.Action));
    }

    [Fact]
    public void An_edit_records_only_what_changed()
    {
        var agreement = Create();

        Assert.False(agreement.Update(Template(), Weekly(), Names, Now));
        Assert.True(agreement.Update(Template(), Weekly(days: [DayOfWeek.Monday, DayOfWeek.Thursday]), Names, Now));

        var change = Assert.Single(((AgreementEvent)agreement.DomainEvents[^1]).Changes);
        Assert.Equal(new FieldChange("recurrence", "Weekly on Mon", "Weekly on Mon, Thu"), change);
    }
}
